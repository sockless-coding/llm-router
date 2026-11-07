using LR.Providers.LlamaCpp;

namespace LR.Core.Tests.Providers;

public class LlamaCppReadinessTests
{
    [Theory]
    [InlineData("main: model loaded")]
    [InlineData("llama_server: model loaded")]
    [InlineData("0.00.077.682 I srv  server_run_i: model loaded")]
    public void RecognisesModelLoadedAcrossBuilds(string line) =>
        Assert.True(LlamaCppProvider.IsModelLoadedMarker(line));

    [Theory]
    [InlineData("print_info: model type = 15M")]
    [InlineData("srv  load_model: loading model 'x.gguf'")]
    public void IgnoresOtherLines(string line) =>
        Assert.False(LlamaCppProvider.IsModelLoadedMarker(line));

    [Theory]
    [InlineData("llama_server: listening on http://127.0.0.1:8080", 8080)]
    [InlineData("main: server is listening on http://0.0.0.0:8123 - starting the main loop", null)]
    [InlineData("0.00.077.687 I srv  server_on_li: listening on http://127.0.0.1:8091", 8091)]
    [InlineData("srv  server_on_li: listening on http://[::]:9000", 9000)]
    public void ParsesTheListeningPort(string line, int? expected) =>
        Assert.Equal(expected, LlamaCppProvider.ParseListeningPort(line));
}
