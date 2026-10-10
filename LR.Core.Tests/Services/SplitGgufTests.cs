using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class SplitGgufTests
{
    [Theory]
    [InlineData("IQ3_XXS/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_XXS-00001-of-00002.gguf", 1, 2)]
    [InlineData("model-00003-of-00004.GGUF", 3, 4)]
    public void ParsesShards(string name, int index, int count) =>
        Assert.Equal((index, count), SplitGguf.Parse(name));

    [Theory]
    [InlineData("model.gguf")]
    [InlineData("model-00001-of-00002.bin")]
    [InlineData("mmproj-Qwen3.8-Flash-Next-BF16.gguf")]
    public void SingleFilesAreNotShards(string name) => Assert.Null(SplitGguf.Parse(name));

    [Fact]
    public void ListsEveryShardOfASplitModel()
    {
        var first = Path.Combine("lib", "IQ3_S", "Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00001-of-00002.gguf");

        Assert.True(SplitGguf.IsFirstShard(first));
        Assert.Equal(
            [first, Path.Combine("lib", "IQ3_S", "Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00002-of-00002.gguf")],
            SplitGguf.AllShards(first));
    }

    [Fact]
    public void SingleFileIsItsOwnOnlyShard() =>
        Assert.Equal(["model.gguf"], SplitGguf.AllShards("model.gguf"));
}
