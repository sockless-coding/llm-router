using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class ModelKindClassifierTests
{
    private static LocalModel Model(string fileName, string? architecture = null, string? repoId = null,
        string? decisionType = null, int? poolingType = null) => new()
    {
        Name = fileName,
        FilePath = Path.Combine("models", fileName),
        HfRepoId = repoId,
        HfFilename = repoId is null ? null : fileName,
        Source = repoId is null ? ModelSource.Local : ModelSource.HuggingFace,
        Architecture = architecture,
        DecisionType = decisionType,
        PoolingType = poolingType,
    };

    [Fact]
    public void PlainLlmIsChat() =>
        Assert.Equal(ModelType.Chat, ModelKindClassifier.Classify(Model("Qwen3-8B-Q4_K_M.gguf", "qwen3", "unsloth/Qwen3-8B-GGUF")));

    [Fact]
    public void ClipArchitectureOrMmprojNameIsProjector()
    {
        Assert.Equal(ModelType.Projector, ModelKindClassifier.Classify(Model("vision.gguf", "clip")));
        Assert.Equal(ModelType.Projector, ModelKindClassifier.Classify(Model("mmproj-F16.gguf", "qwen2vl")));
    }

    [Theory]
    [InlineData("openjev")]
    [InlineData("laya")]
    [InlineData("kev")]
    public void DecisionTypeMakesADecisionModel(string decisionType) =>
        Assert.Equal(ModelType.Decision, ModelKindClassifier.Classify(Model("model-Q8_0.gguf", "qwen3", decisionType: decisionType)));

    [Fact]
    public void ProjectorOfADecisionModelStaysAProjector() =>
        Assert.Equal(ModelType.Projector, ModelKindClassifier.Classify(Model("mmproj-openjev-F16.gguf", "clip", decisionType: null)));

    [Theory]
    [InlineData(1, ModelType.Embedding)]
    [InlineData(2, ModelType.Embedding)]
    [InlineData(3, ModelType.Embedding)]
    [InlineData(4, ModelType.Reranker)]
    [InlineData(0, ModelType.Chat)]
    public void PoolingTypeMarksEmbeddingAndReranking(int pooling, ModelType expected) =>
        Assert.Equal(expected, ModelKindClassifier.Classify(Model("model-F16.gguf", "bert", poolingType: pooling)));

    [Theory]
    [InlineData("black-forest-labs/FLUX.1-schnell", "flux1-schnell.safetensors", null, ModelType.Image)]
    [InlineData("black-forest-labs/FLUX.1-schnell", "ae.safetensors", null, ModelType.ImageComponent)]
    [InlineData("comfyanonymous/flux_text_encoders", "clip_l.safetensors", null, ModelType.ImageComponent)]
    [InlineData("city96/FLUX.1-dev-gguf", "flux1-dev-Q4_0.gguf", "flux", ModelType.Image)]
    [InlineData("city96/t5-v1_1-xxl-encoder-gguf", "t5-v1_1-xxl-encoder-Q8_0.gguf", "t5encoder", ModelType.ImageComponent)]
    public void DiffusionFilesAreImageOrComponent(string repoId, string file, string? arch, ModelType expected) =>
        Assert.Equal(expected, ModelKindClassifier.Classify(Model(file, arch, repoId)));

    [Fact]
    public void LlmLoraAdapterIsNotMistakenForADiffusionLora() =>
        Assert.Equal(ModelType.Chat, ModelKindClassifier.Classify(Model("my-lora-F16.gguf", "llama")));

    [Theory]
    [InlineData("openjev", true)]
    [InlineData("clef", true)]
    [InlineData("granite-switch", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyGraniteSwitchDecisionModelsAlsoChat(string? decisionType, bool decisionOnly) =>
        Assert.Equal(decisionOnly, ModelKindClassifier.IsDecisionOnly(decisionType));

    [Fact]
    public void DetailedLabelNamesTheDiffusionPartAndDualModels()
    {
        Assert.Equal("VAE", ModelKindClassifier.DetailedLabel(Model("ae.safetensors", repoId: "black-forest-labs/FLUX.1-schnell")));
        Assert.Equal("Decision + Chat", ModelKindClassifier.DetailedLabel(Model("granite.gguf", "graniteswitch", decisionType: "granite-switch")));
        Assert.Equal("Decision", ModelKindClassifier.DetailedLabel(Model("laya.gguf", "laya", decisionType: "laya")));
    }

    [Theory]
    [InlineData(ModelType.Chat, ModelTypeCategory.Chat)]
    [InlineData(ModelType.Projector, ModelTypeCategory.Chat)]
    [InlineData(ModelType.Reranker, ModelTypeCategory.Embedding)]
    [InlineData(ModelType.Decision, ModelTypeCategory.Decision)]
    [InlineData(ModelType.ImageComponent, ModelTypeCategory.Image)]
    public void TypesFileUnderTabs(ModelType type, ModelTypeCategory expected) =>
        Assert.Equal(expected, ModelKindClassifier.CategoryOf(type));
}
