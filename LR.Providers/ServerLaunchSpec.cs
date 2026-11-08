namespace LR.Providers;

/// <summary>
/// The process a provider wants the wrapper to launch for a preset: what to run, with which
/// arguments, from which directory. Built by <see cref="ManagedServerProviderBase.BuildLaunchSpec"/>.
/// </summary>
/// <param name="ExecutablePath">Absolute path of the program to start.</param>
/// <param name="Arguments">Arguments, unquoted — the wrapper quotes as needed.</param>
/// <param name="WorkingDirectory">Directory the process starts in, or null for the wrapper's default.</param>
/// <param name="Environment">Extra environment variables for the process, or null. Never logged, unlike the arguments.</param>
public sealed record ServerLaunchSpec(string ExecutablePath, List<string> Arguments, string? WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null);
