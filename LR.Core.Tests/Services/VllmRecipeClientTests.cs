using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using LR.Core.Interfaces;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class VllmRecipeClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (VllmRecipeClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new VllmRecipeClient(new StubFactory(handler), NullLogger<VllmRecipeClient>.Instance), handler);
    }

    private const string Models = """[{"hf_id":"Qwen/Qwen3-8B","title":"Qwen3-8B","provider":"Qwen","url":"/Qwen/Qwen3-8B","json":"/Qwen/Qwen3-8B.json"}]""";

    private const string Taxonomy = """
        {
          "hardware_profiles": { "rtx_5090": { "brand": "NVIDIA", "display_name": "RTX 5090", "gpu_count": 1, "vram_gb": 32, "restricted": true } },
          "strategies": {
            "single_node_tp": { "deploy_type": "single_node", "display_name": "Tensor Parallel" },
            "multi_node_tp": { "deploy_type": "multi_node", "display_name": "Multi-node" }
          }
        }
        """;

    /// <summary>Trimmed from recipes.vllm.ai/Google/gemma-4-E4B-it.json.</summary>
    private const string RecipeDocument = """
        {
          "hf_id": "Google/gemma-4-E4B-it",
          "meta": { "title": "Gemma 4 E4B", "description": "Small.", "tasks": ["text", "multimodal"] },
          "recommended_command": { "hardware": "h100", "argv": ["vllm", "serve", "google/gemma-4-E4B-it"] },
          "model": { "model_id": "google/gemma-4-E4B-it", "min_vllm_version": "0.20.0", "context_length": 131072 },
          "features": { "tool_calling": { "description": "Tools", "args": [] }, "spec_decoding": { "description": "Spec", "args": [] } },
          "opt_in_features": ["spec_decoding"],
          "variants": {
            "fp8": { "model_id": "RedHatAI/x", "precision": "fp8", "vram_minimum_gb": 10, "supported_hardware": ["rtx_5090"] },
            "default": { "precision": "bf16", "vram_minimum_gb": 20, "supported_hardware": ["h100"] }
          }
        }
        """;

    [Fact]
    public async Task Catalog_ParsesModelsAndHardware_OnlySingleNodeStrategies_AndIsCached()
    {
        var (client, handler) = Create(r => Json(r.RequestUri!.AbsolutePath == "/models.json" ? Models : Taxonomy));

        var catalog = await client.GetCatalogAsync();
        await client.GetCatalogAsync();

        Assert.Equal("Qwen/Qwen3-8B", Assert.Single(catalog.Models).HfId);
        var gpu = Assert.Single(catalog.Hardware);
        Assert.Equal(("rtx_5090", "RTX 5090", 1, 32), (gpu.Id, gpu.DisplayName, gpu.GpuCount, gpu.VramGb));
        Assert.Equal("single_node_tp", Assert.Single(catalog.Strategies).Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Recipe_ParsesChoices_DefaultVariantFirst_OptInFeaturesMarked()
    {
        var (client, _) = Create(_ => Json(RecipeDocument));

        var recipe = await client.GetRecipeAsync("Google/gemma-4-E4B-it");

        Assert.NotNull(recipe);
        Assert.Equal("Gemma 4 E4B", recipe.Title);
        Assert.Equal("0.20.0", recipe.MinVllmVersion);
        Assert.Equal(131072, recipe.ContextLength);
        Assert.Equal("h100", recipe.DefaultHardware);
        Assert.Equal(["default", "fp8"], recipe.Variants.Select(v => v.Key));
        Assert.Equal(["rtx_5090"], recipe.Variants[1].SupportedHardware);
        Assert.False(recipe.Features.Single(f => f.Key == "tool_calling").OptIn);
        Assert.True(recipe.Features.Single(f => f.Key == "spec_decoding").OptIn);
    }

    [Fact]
    public async Task UnknownRecipe_IsNull()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await client.GetRecipeAsync("no/such"));
        Assert.Null(await client.RenderAsync("no/such", new VllmRecipeSelection()));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b/c")]
    [InlineData("noslash")]
    [InlineData("a/b?x=1")]
    [InlineData("a/..")]
    [InlineData("")]
    public async Task BadIds_NeverReachTheNetwork(string id)
    {
        var (client, handler) = Create(_ => Json("{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRecipeAsync(id));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SiteFailures_BecomeMessagesForTheUser()
    {
        var (down, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Contains("500", (await Assert.ThrowsAsync<InvalidOperationException>(() => down.GetRecipeAsync("a/b"))).Message);

        var (html, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html>") });
        Assert.Contains("JSON", (await Assert.ThrowsAsync<InvalidOperationException>(() => html.GetRecipeAsync("a/b"))).Message);
    }

    [Fact]
    public void Query_EscapesValues_AndAlwaysAsksForARendering()
    {
        Assert.Equal("?variant=default", VllmRecipeClient.BuildQuery(new VllmRecipeSelection()));
        Assert.Equal("?hardware=a%26b&features=x%2Cy", VllmRecipeClient.BuildQuery(new VllmRecipeSelection("a&b", Features: ["x", "y"])));
        Assert.Equal("?features=none", VllmRecipeClient.BuildQuery(new VllmRecipeSelection(Features: [])));
    }
}
