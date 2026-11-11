using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class StartupFailureSummaryTests
{
    /// <summary>The shape of a vLLM engine-core crash: the root cause first, then the API server's own traceback.</summary>
    private static readonly string[] VllmCrash =
    [
        "(APIServer pid=1) INFO 11-09 20:31:02 [api_server.py:1820] vLLM API server version 0.33.0",
        "(EngineCore pid=212) INFO 11-09 20:31:20 [gpu_model_runner.py:2602] Loading model from scratch...",
        "(EngineCore pid=212) ERROR 11-09 20:31:22 [core.py:708] EngineCore failed to start.",
        "(EngineCore pid=212) ERROR 11-09 20:31:22 [core.py:708] Traceback (most recent call last):",
        "(EngineCore pid=212) ERROR 11-09 20:31:22 [core.py:708]   File \"/opt/venv/lib/python3.12/site-packages/vllm/v1/engine/core.py\", line 699, in run_engine_core",
        "(EngineCore pid=212) ERROR 11-09 20:31:22 [core.py:708]     engine_core = EngineCoreProc(*args, **kwargs)",
        "(EngineCore pid=212) ERROR 11-09 20:31:22 [core.py:708]                   ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^",
        "(EngineCore pid=212) ERROR 11-09 20:31:22 [core.py:708] torch.OutOfMemoryError: XPU out of memory. Tried to allocate 2.00 GiB.",
        "Traceback (most recent call last):",
        "  File \"/opt/venv/bin/vllm\", line 10, in <module>",
        "    sys.exit(main())",
        "  File \"/opt/venv/lib/python3.12/site-packages/vllm/v1/engine/utils.py\", line 1473, in wait_for_engine_startup",
        "    raise RuntimeError(",
        "RuntimeError: Engine core initialization failed. See root cause above. Failed core proc(s): {}",
    ];

    [Fact]
    public void LikelyCause_IsTheFirstException()
    {
        var cause = StartupFailureSummary.LikelyCause(VllmCrash);
        Assert.NotNull(cause);
        Assert.Contains("torch.OutOfMemoryError: XPU out of memory", cause);
    }

    [Fact]
    public void LikelyCause_PrefersANativeCrashOverTheExceptionsItCauses()
    {
        string[] lines =
        [
            "(EngineCore pid=212) INFO 11-09 20:49:56 [qwen_triton_warmup.py:370] Warming up Qwen GDN Triton kernels.",
            "Capturing CUDA graphs (PIECEWISE):   0%|          | 0/5 [00:00<?, ?it/s]malloc(): unaligned tcache chunk detected",
            "Traceback (most recent call last):",
            "    raise RuntimeError(",
            "RuntimeError: Engine core initialization failed. See root cause above. Failed core proc(s): {}",
        ];
        Assert.Equal(lines[1], StartupFailureSummary.LikelyCause(lines));
    }

    [Fact]
    public void LikelyCause_SkipsAnExceptionThatOnlyPointsFurtherUp()
    {
        string[] lines =
        [
            "(EngineCore pid=212) ERROR 11-09 20:41:12 [core.py:1584] EngineCore failed to start.",
            "RuntimeError: Engine core initialization failed. See root cause above. Failed core proc(s): {}",
        ];
        Assert.Equal(lines[0], StartupFailureSummary.LikelyCause(lines));
    }

    [Fact]
    public void LikelyCause_FallsBackToTheFirstErrorLine()
    {
        string[] lines = ["starting", "docker: Error response from daemon: could not select device driver", "done"];
        Assert.Equal(lines[1], StartupFailureSummary.LikelyCause(lines));
    }

    [Fact]
    public void LikelyCause_IsNullWithoutErrors()
    {
        Assert.Null(StartupFailureSummary.LikelyCause(["Loading model", "Still loading"]));
    }

    [Fact]
    public void Describe_LeavesOutTracebackFramesButKeepsErrorsAndTheEnd()
    {
        var text = StartupFailureSummary.Describe("Server process exited prematurely with code 1.", VllmCrash, @"C:\data\x.log");

        Assert.StartsWith("Server process exited prematurely with code 1. Likely cause: (EngineCore pid=212) ERROR", text);
        Assert.Contains("EngineCore failed to start.", text);
        Assert.Contains("RuntimeError: Engine core initialization failed", text);
        Assert.DoesNotContain("Traceback (most recent call last)", text.Split("Last output:")[0]);
        Assert.DoesNotContain("engine_core = EngineCoreProc", text);
        Assert.EndsWith(@"Full output: C:\data\x.log", text);
        Assert.Single(text.Split('\n'), l => l.Contains("torch.OutOfMemoryError"));
    }

    [Fact]
    public void Describe_StaysWithinTheLimit()
    {
        var lines = Enumerable.Range(0, 400).Select(i => $"ERROR line {i}: " + new string('x', 600)).ToList();
        var text = StartupFailureSummary.Describe("Failed.", lines, "/path.log");
        Assert.True(text.Length <= StartupFailureSummary.MaxLength, $"{text.Length} characters");
        Assert.EndsWith("Full output: /path.log", text);
    }

    [Theory]
    [InlineData(new[] { "-m", "x", "--api-key", "secret", "--port", "8080" }, "-m x --api-key *** --port 8080")]
    [InlineData(new[] { "--api-key=secret", "--host", "0.0.0.0" }, "--api-key=*** --host 0.0.0.0")]
    [InlineData(new[] { "run", "--env", "HF_TOKEN", "image" }, "run --env HF_TOKEN image")]
    public void MaskSecrets_HidesSecretValues(string[] args, string expected)
    {
        Assert.Equal(expected, ServerOutputLog.MaskSecrets(args));
    }
}
