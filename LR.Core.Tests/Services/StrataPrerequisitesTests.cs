using LR.Core.Services.EngineBuilds;

namespace LR.Core.Tests.Services;

/// <summary>Which missing tools stop a Strata install, and what the refusal says.</summary>
public sealed class StrataPrerequisitesTests
{
    private static StrataPrerequisite Check(string name, bool found, bool required) =>
        new(name, found, required, "", $"https://example.com/{name}");

    [Fact]
    public void NothingMissing_DoesNotBlock()
    {
        Assert.Null(StrataPrerequisites.MissingMessage([Check("Git", true, true), Check("Python", true, true)]));
    }

    [Fact]
    public void MissingOptionalTool_DoesNotBlock()
    {
        Assert.Null(StrataPrerequisites.MissingMessage([Check("Git", true, true), Check("Build Tools", false, false)]));
    }

    [Fact]
    public void MissingRequiredTools_AreAllNamedWithWhereToGetThem()
    {
        var message = StrataPrerequisites.MissingMessage(
            [Check("Git", false, true), Check("Python", true, true), Check("Build Tools", false, true)]);

        Assert.NotNull(message);
        Assert.Contains("Git (https://example.com/Git)", message);
        Assert.Contains("Build Tools (https://example.com/Build Tools)", message);
        Assert.DoesNotContain("Python", message);
    }
}
