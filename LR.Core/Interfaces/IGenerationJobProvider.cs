namespace LR.Core.Interfaces;

/// <summary>What a <see cref="IGenerationJobProvider"/> job generates.</summary>
public enum GenerationJobKind
{
    Image,
    Video,
}

/// <summary>
/// Optional capability implemented by backend providers whose engine runs generation as async jobs
/// (stable-diffusion.cpp's native <c>/sdcpp/v1</c> API): submit, poll for progress and preview frames,
/// cancel. Not part of <see cref="IBackendProvider"/> itself since chat engines have no such API. Used
/// by the Playground, which needs live progress, image inputs and video — none of which the OpenAI
/// images endpoint offers. Payloads and responses are the engine's own JSON, passed through as is.
/// </summary>
public interface IGenerationJobProvider
{
    /// <summary>
    /// The running server's capability document (supported modes, defaults, samplers, LoRAs, limits),
    /// or null if it can't be read.
    /// </summary>
    Task<string?> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>Submits a job and returns its id. Throws <see cref="HttpRequestException"/> if rejected.</summary>
    Task<string> SubmitJobAsync(GenerationJobKind kind, string payload, CancellationToken cancellationToken = default);

    /// <summary>The job's current state (status, queue position, latest preview, result or error).</summary>
    Task<string> GetJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Asks the server to cancel the job; a generating job stops at its next checkpoint.</summary>
    Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default);
}
