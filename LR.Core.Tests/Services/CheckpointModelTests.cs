using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

/// <summary>
/// Checkpoint folders in the model library (TensorFold, vLLM): reading config.json, which engines run
/// a model, what a checkpoint download fetches, and importing/scanning/deleting checkpoint folders.
/// </summary>
public sealed class CheckpointModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("checkpoint-").FullName;
    private readonly SqliteConnection _connection;
    private readonly LRDbContext _context;
    private readonly ModelLibraryManager _library;

    public CheckpointModelTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LRDbContext(new DbContextOptionsBuilder<LRDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _library = new ModelLibraryManager(_context, new NoGguf());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class NoGguf : IGgufMetadataReader
    {
        public Task<GgufMetadata?> ReadAsync(string filePath, CancellationToken ct = default) => Task.FromResult<GgufMetadata?>(null);
    }

    // Shaped like SergiioB/Qwen3.8-27B-GPTQ-Int4-sym-G128-MTP-BF16: a multimodal config with the text model under text_config.
    private const string QwenConfig = """
        {"architectures": ["Qwen3_5ForConditionalGeneration"], "model_type": "qwen3_5", "torch_dtype": "bfloat16",
         "text_config": {"model_type": "qwen3_5_text", "max_position_embeddings": 262144, "num_hidden_layers": 64,
                         "num_attention_heads": 24, "num_key_value_heads": 4, "hidden_size": 5120, "intermediate_size": 17408,
                         "rope_parameters": {"rope_theta": 10000000}}}
        """;

    private string Checkpoint(string name, string config = QwenConfig, string? quantizeConfig = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "config.json"), config);
        File.WriteAllBytes(Path.Combine(folder, "model-00001-of-00002.safetensors"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(folder, "model-00002-of-00002.safetensors"), new byte[500]);
        File.WriteAllText(Path.Combine(folder, "chat_template.jinja"), "{{ messages }}");
        if (quantizeConfig is not null)
            File.WriteAllText(Path.Combine(folder, "quantize_config.json"), quantizeConfig);
        return folder;
    }

    // --- config.json ---

    [Fact]
    public void Config_IsRead_FromTheTextModelOfAMultimodalCheckpoint()
    {
        var model = new LocalModel { Format = ModelFormat.Checkpoint };

        CheckpointMetadataReader.ApplyConfig(model, (JsonObject)JsonNode.Parse(QwenConfig)!,
            (JsonObject)JsonNode.Parse("""{"bits": 4, "group_size": 128, "quant_method": "gptq"}""")!, "Qwen3.8-27B-GPTQ-Int4");

        Assert.Equal("qwen3_5", model.Architecture);
        Assert.Equal(262144, model.ContextLength);
        Assert.Equal(64, model.BlockCount);
        Assert.Equal(4, model.KvHeadCount);
        Assert.Equal(10000000, model.RopeFreqBase);
        Assert.Equal("GPTQ 4-bit g128", model.QuantizationLevel);
        Assert.Equal("27B", model.ParameterSize);
        Assert.Null(model.GgufModelName); // the library groups by it; classes are shared across unrelated models
    }

    [Theory]
    [InlineData("""{"quantization": {"group_size": 64, "bits": 4}}""", "MLX 4-bit g64")]
    [InlineData("""{"quantization_config": {"quant_method": "modelopt", "quant_algo": "NVFP4"}}""", "NVFP4")]
    [InlineData("""{"quantization_config": {"quant_method": "fp8"}}""", "FP8")]
    [InlineData("""{"quantization_config": {"quant_method": "awq", "bits": 4, "group_size": 128}}""", "AWQ 4-bit g128")]
    [InlineData("""{"torch_dtype": "bfloat16"}""", "BF16")]
    [InlineData("""{}""", null)]
    public void Quantization_IsLabelled(string config, string? expected)
    {
        Assert.Equal(expected, CheckpointMetadataReader.Quantization((JsonObject)JsonNode.Parse(config)!, null));
    }

    [Theory]
    [InlineData("Qwen3.8-27B-MLX-4bit", "27B")]
    [InlineData("NVIDIA-Nemotron-3.5-Lightning-30B-A3B-NVFP4", "30B-A3B")]
    [InlineData("Qwen3.5-0.6B", "0.6B")]
    [InlineData("gemma-4-31B-it-4bit", "31B")]
    [InlineData("SmolLM2-135M-Instruct", "135M")]
    [InlineData("Qwen3.8-27B-MLX-4bit-MTP", "27B")]
    [InlineData("Kolibri-1", null)]
    public void ParameterSize_IsTakenFromTheName(string name, string? expected)
    {
        Assert.Equal(expected, CheckpointMetadataReader.ParameterSizeFrom(name));
    }

    // --- engines ---

    [Fact]
    public void Engines_FollowTheFormat()
    {
        Assert.Equal([ServerEngine.TensorFold, ServerEngine.Vllm],
            ModelEngineSupport.For(new LocalModel { Format = ModelFormat.Checkpoint, QuantizationLevel = "GPTQ 4-bit g128" }));
        // MLX's quantization is read only by MLX runtimes.
        Assert.Equal([ServerEngine.TensorFold],
            ModelEngineSupport.For(new LocalModel { Format = ModelFormat.Checkpoint, QuantizationLevel = "MLX 4-bit g64" }));
        Assert.Equal([ServerEngine.LlamaCpp],
            ModelEngineSupport.For(new LocalModel { FilePath = @"C:\m\Qwen3-8B-Q4_K_M.gguf", Architecture = "qwen3" }));
        Assert.Equal([ServerEngine.LlamaCpp, ServerEngine.Strata],
            ModelEngineSupport.For(new LocalModel { FilePath = @"C:\m\Qwen3.8-Flash-Next-GSQ-RCO-Q4.gguf", Architecture = "qwen3next" }));
        Assert.Equal([ServerEngine.StableDiffusionCpp],
            ModelEngineSupport.For(new LocalModel { FilePath = @"C:\m\sd_xl_base_1.0.safetensors", Format = ModelFormat.Safetensors }));
    }

    // --- what a download fetches ---

    [Theory]
    [InlineData("config.json", true)]
    [InlineData("model-00001-of-00004.safetensors", true)]
    [InlineData("tokenizer.json", true)]
    [InlineData("chat_template.jinja", true)]
    [InlineData("pytorch_model.bin", false)]
    [InlineData("model.Q4_K_M.gguf", false)]
    [InlineData("original/consolidated.safetensors", false)]
    [InlineData(".gitattributes", false)]
    [InlineData("assets/benchmark.png", false)]
    [InlineData("runs/events.out.tfevents", false)]
    public void Download_TakesTheCheckpointFilesOnly(string path, bool included)
    {
        Assert.Equal(included, CheckpointDownload.Include(path));
    }

    [Fact]
    public void Download_RefusesARepoThatIsntACheckpoint()
    {
        HfRepoFile F(string name) => new() { Filename = name, SizeBytes = 1 };

        Assert.NotNull(CheckpointDownload.Select([F("config.json"), F("model.safetensors"), F("README.md")]));
        Assert.Null(CheckpointDownload.Select([F("model.Q4_K_M.gguf"), F("README.md")]));
        Assert.Null(CheckpointDownload.Select([F("config.json"), F("pytorch_model.bin")]));
    }

    [Fact]
    public void Download_SkipsFilesTheManifestHasUnchanged()
    {
        var folder = Checkpoint("resume");
        var file = new HfRepoFile { Filename = "model-00001-of-00002.safetensors", SizeBytes = 1000, BlobId = "abc" };

        Assert.False(CheckpointDownload.IsCurrent(folder, file, new Dictionary<string, string>()));
        CheckpointDownload.WriteManifest(folder, new() { [file.Filename] = "abc" });
        Assert.True(CheckpointDownload.IsCurrent(folder, file, CheckpointDownload.ReadManifest(folder)));
        // A changed blob, or a partial file, is fetched again.
        Assert.False(CheckpointDownload.IsCurrent(folder, new HfRepoFile { Filename = file.Filename, SizeBytes = 1000, BlobId = "def" }, CheckpointDownload.ReadManifest(folder)));
        Assert.False(CheckpointDownload.IsCurrent(folder, new HfRepoFile { Filename = file.Filename, SizeBytes = 2000, BlobId = "abc" }, CheckpointDownload.ReadManifest(folder)));
    }

    // --- the library ---

    [Fact]
    public async Task Import_ReadsACheckpointFolder()
    {
        var folder = Checkpoint("Qwen3.8-27B-GPTQ-Int4", quantizeConfig: """{"bits": 4, "group_size": 128, "quant_method": "gptq"}""");

        var model = await _library.ImportFromPathAsync(folder + Path.DirectorySeparatorChar);

        Assert.Equal(ModelFormat.Checkpoint, model.Format);
        Assert.Equal(folder, model.FilePath);
        Assert.Equal("Qwen3.8-27B-GPTQ-Int4", model.Name);
        Assert.Equal("GPTQ 4-bit g128", model.QuantizationLevel);
        Assert.Equal("{{ messages }}", model.ChatTemplate);
        Assert.True(model.FileSizeBytes >= 1500);
        Assert.Equal(ModelType.Chat, ModelKindClassifier.Classify(model));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _library.ImportFromPathAsync(folder));
    }

    [Fact]
    public async Task Import_RefusesAFolderWithoutACheckpoint()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _library.ImportFromPathAsync(folder));
    }

    [Fact]
    public async Task Scan_ListsACheckpointOnce_NotItsShards()
    {
        var checkpoint = Checkpoint("ckpt");
        var gguf = Path.Combine(_root, "other", "model-Q4_K_M.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(gguf)!);
        File.WriteAllBytes(gguf, new byte[10]);

        var found = await _library.ScanFolderAsync(_root);

        Assert.Equal(new[] { checkpoint, gguf }.Order(), found.Order());
    }

    [Fact]
    public async Task Refresh_MarksAMissingCheckpoint_AndDeleteRemovesTheFolder()
    {
        var folder = Checkpoint("gone");
        var model = await _library.ImportFromPathAsync(folder);

        File.Delete(Path.Combine(folder, "config.json"));
        await _library.RefreshMetadataAsync(model.Id);
        Assert.Equal(ModelStatus.Missing, (await _library.GetByIdAsync(model.Id))!.Status);

        await _library.DeleteAsync(model.Id, deleteFile: true);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task ImportedFiles_GetTheirFormat()
    {
        var sd = Path.Combine(_root, "sd_xl_base_1.0.safetensors");
        File.WriteAllBytes(sd, new byte[10]);

        Assert.Equal(ModelFormat.Safetensors, (await _library.ImportFromPathAsync(sd)).Format);
    }
}
