using System.Text.Json.Nodes;

using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class LargeValueRedactorTests
{
    private static readonly string Image = new('A', 40_000);

    [Fact]
    public void Redact_ReplacesLongStringsAnywhere_KeepsShortOnes()
    {
        var json = $$"""{"prompt":"a cat","init_image":"{{Image}}","result":{"images":[{"index":0,"b64_json":"{{Image}}"}]},"seed":7}""";

        var redacted = JsonNode.Parse(LargeValueRedactor.Redact(json))!.AsObject();

        Assert.Equal("a cat", redacted["prompt"]!.GetValue<string>());
        Assert.Equal(7, redacted["seed"]!.GetValue<int>());
        Assert.Equal("<29 KB omitted>", redacted["init_image"]!.GetValue<string>());
        Assert.Equal("<29 KB omitted>", redacted["result"]!["images"]![0]!["b64_json"]!.GetValue<string>());
        Assert.Equal(0, redacted["result"]!["images"]![0]!["index"]!.GetValue<int>());
    }

    [Fact]
    public void Redact_ReplacesLongStringsInArrays()
    {
        var redacted = JsonNode.Parse(LargeValueRedactor.Redact($$"""{"ref_images":["{{Image}}","short"]}"""))!;

        Assert.Equal("<29 KB omitted>", redacted["ref_images"]![0]!.GetValue<string>());
        Assert.Equal("short", redacted["ref_images"]![1]!.GetValue<string>());
    }

    [Fact]
    public void Redact_NonJson_ReturnedUnchanged()
    {
        Assert.Equal("not json", LargeValueRedactor.Redact("not json"));
    }
}
