namespace LR.Providers;

/// <summary>
/// Engine-specific interpretation of a server process's console output, used by
/// <see cref="WrapperProcessManager"/> to decide when a start has finished and to let the
/// provider mine the output for anything else it needs (e.g. llama.cpp's per-request timings).
/// </summary>
public interface IServerOutputHandler
{
    /// <summary>Called for every stdout/stderr line the server writes, during startup and after.</summary>
    void OnOutputLine(string line);

    /// <summary>True if <paramref name="line"/> signals the model has finished loading.</summary>
    bool IsModelLoadedLine(string line);

    /// <summary>
    /// The port the server reports it's listening on, if <paramref name="line"/> says so; else null.
    /// A start is complete once a model-loaded line and a listening line (for the expected port)
    /// have both been seen — they may be the same line.
    /// </summary>
    int? TryParseListeningPort(string line);
}
