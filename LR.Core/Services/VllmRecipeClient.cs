using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;

namespace LR.Core.Services;

/// <inheritdoc cref="IVllmRecipeClient"/>
public sealed partial class VllmRecipeClient : IVllmRecipeClient
{
    public const string HttpClientName = "vllm-recipes";
    private const string BaseUrl = "https://recipes.vllm.ai";
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan HardwareIndexLifetime = TimeSpan.FromHours(12);
    private const int HardwareIndexConcurrency = 12;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<VllmRecipeClient> _logger;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private VllmRecipeCatalog? _catalog;
    private DateTime _catalogFetchedAt;
    private readonly SemaphoreSlim _hardwareGate = new(1, 1);
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _hardwareIndex;
    private DateTime _hardwareIndexFetchedAt;

    public VllmRecipeClient(IHttpClientFactory httpFactory, ILogger<VllmRecipeClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<VllmRecipeCatalog> GetCatalogAsync(CancellationToken ct = default)
    {
        await _catalogGate.WaitAsync(ct);
        try
        {
            if (_catalog is not null && DateTime.UtcNow - _catalogFetchedAt < CatalogLifetime)
                return _catalog;

            try
            {
                using var models = await FetchAsync("/models.json", ct)
                    ?? throw new InvalidOperationException("recipes.vllm.ai has no model index.");
                using var taxonomy = await FetchAsync("/taxonomy.json", ct)
                    ?? throw new InvalidOperationException("recipes.vllm.ai has no hardware list.");
                _catalog = ParseCatalog(models.RootElement, taxonomy.RootElement);
                _catalogFetchedAt = DateTime.UtcNow;
            }
            catch (Exception ex) when (_catalog is not null && ex is not OperationCanceledException)
            {
                // A recipe list that's a little old beats none while the site is unreachable.
                _logger.LogWarning(ex, "Could not refresh the vLLM recipe list; keeping the previous one.");
            }
            return _catalog!;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    public async Task<VllmRecipeDetails?> GetRecipeAsync(string hfId, CancellationToken ct = default)
    {
        using var doc = await FetchAsync($"/{CheckId(hfId)}.json", ct);
        return doc is null ? null : ParseRecipe(doc.RootElement);
    }

    public async Task<VllmRecipeRendering?> RenderAsync(string hfId, VllmRecipeSelection selection, CancellationToken ct = default)
    {
        using var doc = await FetchAsync($"/{CheckId(hfId)}.json{BuildQuery(selection)}", ct);
        return doc is null ? null : ParseRendering(doc.RootElement);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetHardwareIndexAsync(CancellationToken ct = default)
    {
        await _hardwareGate.WaitAsync(ct);
        try
        {
            if (_hardwareIndex is not null && DateTime.UtcNow - _hardwareIndexFetchedAt < HardwareIndexLifetime)
                return _hardwareIndex;

            var catalog = await GetCatalogAsync(ct);
            var index = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var failures = 0;
            await Parallel.ForEachAsync(catalog.Models, new ParallelOptions { MaxDegreeOfParallelism = HardwareIndexConcurrency, CancellationToken = ct }, async (model, token) =>
            {
                try
                {
                    if (await GetRecipeAsync(model.HfId, token) is { } recipe)
                        index[model.HfId] = recipe.Hardware;
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref failures);
                }
            });
            if (failures > 0)
                _logger.LogWarning("Left {Failures} of {Total} vLLM recipes out of the hardware index: they couldn't be read.", failures, catalog.Models.Count);

            // An index the site mostly failed to answer for isn't worth keeping for 12 hours.
            if (index.Count == 0 && _hardwareIndex is not null)
                return _hardwareIndex;
            _hardwareIndex = new Dictionary<string, IReadOnlyList<string>>(index, StringComparer.OrdinalIgnoreCase);
            _hardwareIndexFetchedAt = failures > catalog.Models.Count / 4 ? DateTime.UtcNow - HardwareIndexLifetime + TimeSpan.FromMinutes(10) : DateTime.UtcNow;
            return _hardwareIndex;
        }
        finally
        {
            _hardwareGate.Release();
        }
    }

    /// <summary>The query string for a selection: <c>hardware</c>, <c>variant</c>, <c>strategy</c>, <c>features</c>.</summary>
    public static string BuildQuery(VllmRecipeSelection selection)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{key}={Uri.EscapeDataString(value.Trim())}");
        }
        Add("hardware", selection.Hardware);
        Add("variant", selection.Variant);
        Add("strategy", selection.Strategy);
        // An empty list means every feature off; the site spells that "none". Null keeps its defaults.
        if (selection.Features is { } features)
            Add("features", features.Count == 0 ? "none" : string.Join(',', features));
        // Any query gets the rendered command rather than the recipe document, and every recipe has a default variant.
        if (parts.Count == 0)
            parts.Add("variant=default");
        return "?" + string.Join('&', parts);
    }

    private static string CheckId(string hfId)
    {
        var id = hfId?.Trim() ?? "";
        if (!HfIdRegex().IsMatch(id) || id.Contains(".."))
            throw new InvalidOperationException($"'{hfId}' isn't a Hugging Face model id (org/name).");
        return id;
    }

    private async Task<JsonDocument?> FetchAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(BaseUrl + path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"recipes.vllm.ai answered {(int)response.StatusCode} for {path}.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"recipes.vllm.ai sent something other than JSON for {path}.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach recipes.vllm.ai: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("recipes.vllm.ai didn't answer within 30 seconds.");
        }
    }

    // --- Parsing. The site's JSON has no version, so everything optional stays optional. ---

    public static VllmRecipeCatalog ParseCatalog(JsonElement models, JsonElement taxonomy)
    {
        var summaries = models.ValueKind == JsonValueKind.Array
            ? models.EnumerateArray()
                .Select(m => new VllmRecipeSummary(Str(m, "hf_id") ?? "", Str(m, "title") ?? Str(m, "hf_id") ?? "", Str(m, "provider") ?? ""))
                .Where(m => m.HfId.Length > 0)
                .OrderBy(m => m.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        var hardware = new List<VllmHardwareProfile>();
        if (Obj(taxonomy, "hardware_profiles") is { } profiles)
            foreach (var p in profiles.EnumerateObject())
                hardware.Add(new VllmHardwareProfile(p.Name, Str(p.Value, "display_name") ?? p.Name, Str(p.Value, "brand") ?? "", Int(p.Value, "gpu_count") ?? 0, Int(p.Value, "vram_gb") ?? 0));

        var strategies = new List<VllmStrategy>();
        if (Obj(taxonomy, "strategies") is { } all)
            foreach (var s in all.EnumerateObject())
                if (s.Value.ValueKind == JsonValueKind.Object && Str(s.Value, "deploy_type") is "single_node")
                    strategies.Add(new VllmStrategy(s.Name, Str(s.Value, "display_name") ?? s.Name, Str(s.Value, "description")?.Trim()));

        return new VllmRecipeCatalog(summaries, hardware, strategies);
    }

    public static VllmRecipeDetails ParseRecipe(JsonElement root)
    {
        var hfId = Str(root, "hf_id") ?? "";
        var meta = Obj(root, "meta");
        var model = Obj(root, "model");

        var variants = new List<VllmRecipeVariant>();
        if (Obj(root, "variants") is { } v)
            foreach (var p in v.EnumerateObject())
                variants.Add(new VllmRecipeVariant(p.Name, Str(p.Value, "label"), Str(p.Value, "precision"), Num(p.Value, "vram_minimum_gb"),
                    Str(p.Value, "description"), Str(p.Value, "model_id"), Strings(p.Value, "supported_hardware")));
        // The default first, as the site lists it first.
        variants = variants.OrderByDescending(x => x.Key == "default").ToList();

        var optIn = Strings(root, "opt_in_features");
        var features = new List<VllmRecipeFeature>();
        if (Obj(root, "features") is { } f)
            foreach (var p in f.EnumerateObject())
                features.Add(new VllmRecipeFeature(p.Name, Str(p.Value, "description"), optIn.Contains(p.Name)));

        string? dependencies = root.TryGetProperty("dependencies", out var deps) && deps.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? deps.ToString()
            : null;

        // The profiles the site has a command for: by_hardware is the full list, a variant's
        // supported_hardware is often missing, and the default is listed even when nothing else is.
        var hardware = new List<string>();
        void AddHardware(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && !hardware.Contains(id, StringComparer.OrdinalIgnoreCase))
                hardware.Add(id);
        }
        var recommended = Obj(root, "recommended_command");
        AddHardware(recommended is { } r0 ? Str(r0, "hardware") : null);
        if (recommended is { } r1 && Obj(r1, "by_hardware") is { } byHardware)
            foreach (var p in byHardware.EnumerateObject())
                AddHardware(p.Name);
        foreach (var variant in variants)
            foreach (var id in variant.SupportedHardware)
                AddHardware(id);

        return new VllmRecipeDetails(
            hfId,
            meta is { } m ? Str(m, "title") ?? hfId : hfId,
            meta is { } m2 ? Str(m2, "description") : null,
            model is { } md ? Str(md, "min_vllm_version") : null,
            model is { } mc ? Int(mc, "context_length") : null,
            meta is { } m3 ? Strings(m3, "tasks") : [],
            Obj(root, "recommended_command") is { } rc ? Str(rc, "hardware") : null,
            variants,
            features,
            dependencies,
            hardware);
    }

    public static VllmRecipeRendering ParseRendering(JsonElement root)
    {
        var adjustments = new List<VllmRecipeAdjustment>();
        if (Obj(root, "selection") is { } selection && selection.TryGetProperty("adjusted", out var adjusted) && adjusted.ValueKind == JsonValueKind.Array)
            foreach (var a in adjusted.EnumerateArray())
                adjustments.Add(new VllmRecipeAdjustment(Str(a, "param") ?? "", ScalarText(a, "requested"), ScalarText(a, "used"), Str(a, "reason")));

        var environment = new Dictionary<string, string>();
        if (Obj(root, "env") is { } env)
            foreach (var p in env.EnumerateObject())
                environment[p.Name] = p.Value.ToString();

        return new VllmRecipeRendering(
            Str(root, "hf_id") ?? "",
            Str(root, "hardware") ?? "",
            Str(root, "strategy") ?? "",
            Str(root, "variant") ?? "",
            Str(root, "deploy_type") ?? "",
            Strings(root, "argv"),
            environment,
            adjustments,
            Str(root, "page_url"));
    }

    private static string? ScalarText(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v.ToString() : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex HfIdRegex();
}
