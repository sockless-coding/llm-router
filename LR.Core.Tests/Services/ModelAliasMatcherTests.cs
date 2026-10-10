using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class ModelAliasMatcherTests
{
    private static ModelPreset Preset(string name, string? aliases = null) => new() { Name = name, Aliases = aliases };

    [Fact]
    public void Resolve_ExactName()
    {
        var qwen = Preset("qwen3-coder");
        Assert.Same(qwen, ModelAliasMatcher.Resolve([Preset("llama"), qwen], "qwen3-coder"));
    }

    [Fact]
    public void Resolve_NameIsCaseSensitive_AliasIsNot()
    {
        var qwen = Preset("qwen3-coder", "GPT-4o");
        Assert.Null(ModelAliasMatcher.Resolve([qwen], "Qwen3-Coder"));
        Assert.Same(qwen, ModelAliasMatcher.Resolve([qwen], "gpt-4o"));
    }

    [Fact]
    public void Resolve_AliasListSeparators()
    {
        var p = Preset("p", "a, b\nc;d");
        foreach (var name in new[] { "a", "b", "c", "d" })
            Assert.Same(p, ModelAliasMatcher.Resolve([p], name));
    }

    [Fact]
    public void Resolve_Wildcard()
    {
        var p = Preset("p", "claude-*");
        Assert.Same(p, ModelAliasMatcher.Resolve([p], "claude-sonnet-5-5"));
        Assert.Null(ModelAliasMatcher.Resolve([p], "not-claude-x"));
    }

    [Fact]
    public void Resolve_WildcardIsLiteralOtherwise()
    {
        var p = Preset("p", "gpt-4.1*");
        Assert.Same(p, ModelAliasMatcher.Resolve([p], "gpt-4.1-mini"));
        Assert.Null(ModelAliasMatcher.Resolve([p], "gpt-401"));
    }

    [Fact]
    public void Resolve_NameBeatsAlias_ExactAliasBeatsWildcard()
    {
        var wildcard = Preset("broad", "gpt-*");
        var exact = Preset("specific", "gpt-4o");
        var named = Preset("gpt-4o-mini");

        Assert.Same(exact, ModelAliasMatcher.Resolve([wildcard, exact, named], "gpt-4o"));
        Assert.Same(named, ModelAliasMatcher.Resolve([wildcard, exact, named], "gpt-4o-mini"));
        Assert.Same(wildcard, ModelAliasMatcher.Resolve([wildcard, exact, named], "gpt-5"));
    }

    [Fact]
    public void Resolve_BlankOrUnknown_ReturnsNull()
    {
        Assert.Null(ModelAliasMatcher.Resolve([Preset("p", "x")], null));
        Assert.Null(ModelAliasMatcher.Resolve([Preset("p", "x")], " "));
        Assert.Null(ModelAliasMatcher.Resolve([Preset("p", "x")], "y"));
    }
}
