using LR.Core.Services;

namespace LR.Core.Tests.Services;

// All template snippets below are hand-written and structurally representative of real
// llama.cpp/GGUF chat templates — not copied verbatim from any specific model's template.
public class ChatTemplateVariableExtractorTests
{
    private readonly ChatTemplateVariableExtractor _extractor = new();

    [Fact]
    public void BooleanStyleCustomVariable_IsDetected()
    {
        var result = _extractor.Extract("{% if enable_thinking %}<think>{% endif %}");

        var v = Assert.Single(result);
        Assert.Equal("enable_thinking", v.Name);
        Assert.Empty(v.LiteralValues);
    }

    [Fact]
    public void MultiBranchComparison_CollectsLiteralValues()
    {
        var template = "{% if reasoning_effort == \"high\" %}A{% elif reasoning_effort == \"low\" %}B{% endif %}";

        var result = _extractor.Extract(template);

        var v = Assert.Single(result);
        Assert.Equal("reasoning_effort", v.Name);
        Assert.Equal(new[] { "high", "low" }, v.LiteralValues.OrderBy(x => x));
    }

    [Fact]
    public void StandardOnlyTemplate_ReportsNoFreeVariables()
    {
        var template = "{% for message in messages %}" +
                        "{{ message.role }}{{ message.content }}" +
                        "{% endfor %}" +
                        "{% if tools %}{{ tools }}{% endif %}" +
                        "{{ bos_token }}{{ eos_token }}" +
                        "{% if add_generation_prompt %}{{ loop.index }}{{ loop.last }}{% endif %}";

        var result = _extractor.Extract(template);

        Assert.Empty(result);
    }

    [Fact]
    public void LoopVariableAndAttribute_AreNotFreeVariables()
    {
        var template = "{% for message in messages %}{{ message.role }}{% endfor %}";

        var result = _extractor.Extract(template);

        Assert.Empty(result);
        Assert.DoesNotContain(result, v => v.Name is "message" or "role");
    }

    [Fact]
    public void MacroParameter_ShadowsFreeVariable()
    {
        var template = "{% macro fmt(x) %}{{ x }}{% endmacro %}{{ fmt(unrelated_free_var) }}";

        var result = _extractor.Extract(template);

        var v = Assert.Single(result);
        Assert.Equal("unrelated_free_var", v.Name);
    }

    [Fact]
    public void WhitespaceControlMarkers_DoNotBreakTagDetection()
    {
        var template = "{%- if custom_flag -%}x{%- endif -%}";

        var result = _extractor.Extract(template);

        var v = Assert.Single(result);
        Assert.Equal("custom_flag", v.Name);
    }

    [Fact]
    public void SetVariable_ShadowsSubsequentReference()
    {
        var template = "{% set custom_default = \"x\" %}{% if custom_default == extra_var %}y{% endif %}";

        var result = _extractor.Extract(template);

        var v = Assert.Single(result);
        Assert.Equal("extra_var", v.Name);
    }

    [Fact]
    public void DefaultFilter_CollectsLiteralValue()
    {
        var template = "{{ level | default(\"medium\") }}";

        var result = _extractor.Extract(template);

        var v = Assert.Single(result);
        Assert.Equal("level", v.Name);
        Assert.Contains("medium", v.LiteralValues);
    }

    [Fact]
    public void InMembership_CollectsLiteralValues()
    {
        var template = "{% if mode in [\"fast\", \"quality\"] %}x{% endif %}";

        var result = _extractor.Extract(template);

        var v = Assert.Single(result);
        Assert.Equal("mode", v.Name);
        Assert.Equal(new[] { "fast", "quality" }, v.LiteralValues.OrderBy(x => x));
    }

    [Fact]
    public void StringContainingTagLikeCharacters_DoesNotCloseTagEarly()
    {
        var template = "{{ x | default(\"}}\") }}";

        var result = _extractor.Extract(template);

        var v = Assert.Single(result);
        Assert.Equal("x", v.Name);
    }

    [Fact]
    public void RawBlock_IsSkippedEntirely()
    {
        var template = "{% raw %}{{ not_a_var }}{% endraw %}";

        var result = _extractor.Extract(template);

        Assert.Empty(result);
    }

    [Fact]
    public void TruncatedTemplate_DoesNotThrow()
    {
        var template = "{% if enable_thinking %}{{ reasoning_effort == \"hi";

        var result = _extractor.Extract(template);

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmptyInput_ReturnsEmptyList(string? template)
    {
        var result = _extractor.Extract(template);

        Assert.Empty(result);
    }
}
