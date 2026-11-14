using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class SyclKeepAlivePlannerTests
{
    private static ComputeDevice Gpu(string name, bool? display, bool sycl = true, bool enabled = true) => new()
    {
        InstanceId = "PCI\\VEN_8086&DEV_0000\\" + name,
        Name = name,
        Kind = ComputeDeviceKind.Gpu,
        IsEnabled = enabled,
        HasDisplayAttached = display,
        SupportedBackends = sycl ? new[] { BackendType.Sycl, BackendType.Vulkan } : new[] { BackendType.Vulkan },
    };

    [Fact]
    public void Off_never_runs()
    {
        var plan = SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Off, new[] { Gpu("Arc", display: false) });
        Assert.False(plan.ShouldRun);
    }

    [Fact]
    public void Headless_runs_when_any_sycl_gpu_has_no_monitor()
    {
        var plan = SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Headless,
            new[] { Gpu("iGPU", display: true), Gpu("Arc B70", display: false) });
        Assert.True(plan.ShouldRun);
        Assert.Contains("Arc B70", plan.Reason);
        Assert.DoesNotContain("iGPU", plan.Reason);
    }

    [Fact]
    public void Headless_does_not_run_when_every_sycl_gpu_has_a_monitor()
    {
        var plan = SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Headless,
            new[] { Gpu("Arc", display: true), Gpu("RTX", display: false, sycl: false) });
        Assert.False(plan.ShouldRun);
    }

    [Fact]
    public void Headless_treats_unknown_display_state_as_headless()
    {
        var plan = SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Headless, new[] { Gpu("Arc", display: null) });
        Assert.True(plan.ShouldRun);
    }

    [Fact]
    public void Disabled_and_non_sycl_gpus_are_ignored()
    {
        var devices = new[] { Gpu("Arc", display: false, enabled: false), Gpu("RTX", display: false, sycl: false) };
        Assert.False(SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Always, devices).ShouldRun);
        Assert.False(SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Headless, devices).ShouldRun);
    }

    [Fact]
    public void Always_runs_even_with_a_monitor()
    {
        var plan = SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Always, new[] { Gpu("Arc", display: true) });
        Assert.True(plan.ShouldRun);
    }

    [Fact]
    public void Command_line_loads_environment_then_runs_keepalive_into_log()
    {
        var args = SyclKeepAlivePlanner.BuildCommandLine(
            @"C:\LLM Router\Tools\sycl-keepalive\sycl-keepalive.exe",
            Array.Empty<string>(),
            SyclKeepAliveSettings.DefaultEnvironmentSetupCommand,
            @"C:\LLM Router\data\sycl-keepalive.log");

        Assert.Equal(
            "/d /s /c \"call \"C:\\Program Files (x86)\\Intel\\oneAPI\\setvars.bat\" intel64 --force > \"C:\\LLM Router\\data\\sycl-keepalive.log\" 2>&1 & " +
            "\"C:\\LLM Router\\Tools\\sycl-keepalive\\sycl-keepalive.exe\" >> \"C:\\LLM Router\\data\\sycl-keepalive.log\" 2>&1\"",
            args);
    }

    [Fact]
    public void Command_line_without_environment_runs_keepalive_directly()
    {
        var args = SyclKeepAlivePlanner.BuildCommandLine(@"C:\k.exe", Array.Empty<string>(), "  ", @"C:\k.log");
        Assert.Equal("/d /s /c \"\"C:\\k.exe\" >> \"C:\\k.log\" 2>&1\"", args);
    }

    [Fact]
    public void Command_line_passes_device_arguments()
    {
        var args = SyclKeepAlivePlanner.BuildCommandLine(@"C:\k.exe", new[] { "--device", "03:00.0", "--device", "07:00.0" }, null, @"C:\k.log");
        Assert.Equal("/d /s /c \"\"C:\\k.exe\" --device 03:00.0 --device 07:00.0 >> \"C:\\k.log\" 2>&1\"", args);
    }

    [Fact]
    public void Headless_targets_only_the_headless_cards_by_pci_address()
    {
        var withMonitor = Gpu("Arc A", display: true);
        withMonitor.PciAddress = "03:00.0";
        var headless = Gpu("Arc B", display: false);
        headless.PciAddress = "07:00.0";

        var plan = SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Headless, new[] { withMonitor, headless });

        Assert.Equal(new[] { "--device", "07:00.0" }, plan.Arguments);
    }

    [Fact]
    public void Headless_keeps_every_gpu_alive_when_a_headless_card_has_no_pci_address()
    {
        var known = Gpu("Arc A", display: false);
        known.PciAddress = "03:00.0";
        var unknown = Gpu("Arc B", display: false);

        var plan = SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Headless, new[] { known, unknown });

        Assert.True(plan.ShouldRun);
        Assert.Empty(plan.Arguments);
    }

    [Fact]
    public void Always_keeps_every_gpu_alive()
    {
        var gpu = Gpu("Arc", display: true);
        gpu.PciAddress = "03:00.0";
        Assert.Empty(SyclKeepAlivePlanner.Evaluate(SyclKeepAliveMode.Always, new[] { gpu }).Arguments);
    }

    [Fact]
    public void Missing_dll_exit_code_is_explained()
    {
        Assert.Contains("sycl9.dll", SyclKeepAlivePlanner.DescribeExit(unchecked((int)0xC0000135)));
        Assert.Equal("exit code 1", SyclKeepAlivePlanner.DescribeExit(1));
    }
}
