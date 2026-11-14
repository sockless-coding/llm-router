using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Reads/writes the single <see cref="SyclKeepAliveSettings"/> row. Safe to inject into
/// singletons — implementations reach the (scoped) DbContext internally.
/// </summary>
public interface ISyclKeepAliveSettingsService
{
    Task<SyclKeepAliveSettings> GetAsync(CancellationToken ct = default);

    Task SaveAsync(SyclKeepAliveMode mode, string environmentSetupCommand, string? executablePath, CancellationToken ct = default);
}
