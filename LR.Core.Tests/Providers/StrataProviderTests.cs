using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;
using LR.Providers;
using LR.Providers.LlamaCpp;
using LR.Providers.Strata;

namespace LR.Core.Tests.Providers;

public sealed class StrataProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("strata-test-").FullName;
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddBackendEngines().BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Lays out the files a Strata checkout has after its setup has run.</summary>
    private void CreateStrataCheckout(string? runConfigJson = "{}")
    {
        Touch(StrataLayout.ServerScriptRelativePath);
        Touch(StrataLayout.PythonRelativePath);
        if (runConfigJson is not null)
            File.WriteAllText(Path.Combine(_root, "strata-coder.json"), runConfigJson);
    }

    private void Touch(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    private TestableStrataProvider CreateProvider()
    {
        var provider = ActivatorUtilities.CreateInstance<TestableStrataProvider>(_services);
        provider.Configure(new BackendConfigData { InstallFolderPath = _root });
        return provider;
    }

    [Fact]
    public void GetStartCommand_LaunchesServerScriptWithVenvPython_BoundToLoopback()
    {
        CreateStrataCheckout();
        using var provider = CreateProvider();

        var command = provider.GetStartCommand(new ModelPreset { ModelPath = "strata-coder.json", MainGpu = 1 });

        Assert.NotNull(command);
        Assert.Contains(Path.Combine(_root, StrataLayout.PythonRelativePath), command);
        Assert.Contains(Path.Combine(_root, StrataLayout.ServerScriptRelativePath), command);
        Assert.Contains("--engine strata", command);
        // A relative run-config path resolves against the Strata folder.
        Assert.Contains(Path.Combine(_root, "strata-coder.json"), command);
        Assert.Contains("--host 127.0.0.1", command);
        Assert.Contains("--gpu 1", command);
    }

    [Fact]
    public void GetStartCommand_Null_WhenRunConfigMissing()
    {
        CreateStrataCheckout(runConfigJson: null);
        using var provider = CreateProvider();

        Assert.Null(provider.GetStartCommand(new ModelPreset { ModelPath = "strata-coder.json" }));
    }

    [Theory]
    [InlineData("ready: http://127.0.0.1:8123/v1  (OpenAI: /v1/chat/completions, Anthropic: /v1/messages, context 65536 tokens)", 8123)]
    [InlineData("ready: http://192.168.1.5:9000/v1  (OpenAI: /v1/chat/completions)", 9000)]
    public void ReadyLine_SignalsLoadedAndPort(string line, int port)
    {
        using var provider = CreateProvider();

        Assert.True(provider.IsModelLoaded(line));
        Assert.Equal(port, provider.ListeningPort(line));
    }

    [Theory]
    [InlineData("[strata] experts loaded: 24576")]
    [InlineData("       open http://127.0.0.1:8123/ in a browser to chat; close this window to stop the model")]
    public void OtherLines_AreNotReadiness(string line)
    {
        using var provider = CreateProvider();

        Assert.False(provider.IsModelLoaded(line));
        Assert.Null(provider.ListeningPort(line));
    }

    [Theory]
    [InlineData("""{ "api_key": "k1, k2" }""", "k1")]
    [InlineData("""{ "api_key": ["", "k3"] }""", "k3")]
    [InlineData("""{ "api_key": "" }""", null)]
    [InlineData("""{ "port": 8080 }""", null)]
    public void RunConfigApiKey_IsSentAsBearerToken(string runConfigJson, string? expectedKey)
    {
        CreateStrataCheckout(runConfigJson);
        using var provider = CreateProvider();
        Assert.NotNull(provider.GetStartCommand(new ModelPreset { ModelPath = "strata-coder.json" }));

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/props");
        provider.ApplyHeaders(request);

        Assert.Equal(expectedKey, request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void Descriptor_RejectsFolderWithoutSetup()
    {
        Touch(StrataLayout.ServerScriptRelativePath);
        var descriptor = new StrataEngineDescriptor();

        Assert.Contains(".venv", descriptor.ValidateInstallFolder(_root));

        Touch(StrataLayout.PythonRelativePath);
        Assert.Null(descriptor.ValidateInstallFolder(_root));
    }

    [Fact]
    public void Factory_CreatesRegisteredEngines_AndNothingForUnregistered()
    {
        var factory = _services.GetRequiredService<IBackendProviderFactory>();
        var catalog = _services.GetRequiredService<IEngineCatalog>();

        Assert.IsType<StrataProvider>(factory.Create(ServerEngine.Strata));
        Assert.IsType<LlamaCppProvider>(factory.Create(ServerEngine.LlamaCpp));
        Assert.Null(factory.Create(ServerEngine.Ollama));
        Assert.Null(catalog.Get(ServerEngine.Ollama));
    }

    /// <summary>Exposes the protected engine hooks for testing.</summary>
    public class TestableStrataProvider(ILogger<StrataProvider> logger, IServiceScopeFactory scopeFactory)
        : StrataProvider(logger, scopeFactory)
    {
        public bool IsModelLoaded(string line) => IsModelLoadedLine(line);
        public int? ListeningPort(string line) => TryParseListeningPort(line);
        public void ApplyHeaders(HttpRequestMessage request) => ApplyRequestHeaders(request);
    }
}
