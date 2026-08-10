using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// API key manager with SQLite persistence via EF Core.
/// </summary>
public class ApiKeyManager : IApiKeyManager
{
    private const string KeyPrefixLiteral = "lr-";
    private const int KeyPrefixDisplayLength = 12;

    private readonly LRDbContext _context;

    public ApiKeyManager(LRDbContext context)
    {
        _context = context;
    }

    public async Task<(ApiKey Key, string RawKey)> CreateAsync(string name, bool allowAllModels, IEnumerable<Guid> allowedPresetIds)
    {
        var rawKey = GenerateRawKey();
        var key = new ApiKey
        {
            Name = name,
            KeyHash = Hash(rawKey),
            KeyPrefix = rawKey[..Math.Min(KeyPrefixDisplayLength, rawKey.Length)],
            AllowAllModels = allowAllModels
        };

        if (!allowAllModels)
        {
            foreach (var presetId in allowedPresetIds.Distinct())
                key.AllowedPresets.Add(new ApiKeyModelPreset { ApiKeyId = key.Id, ModelPresetId = presetId });
        }

        _context.ApiKeys.Add(key);
        await _context.SaveChangesAsync();

        return (key, rawKey);
    }

    public async Task<(ApiKey Key, string RawKey)?> RegenerateAsync(Guid id)
    {
        var key = await _context.ApiKeys.FindAsync(id);
        if (key is null) return null;

        var rawKey = GenerateRawKey();
        key.KeyHash = Hash(rawKey);
        key.KeyPrefix = rawKey[..Math.Min(KeyPrefixDisplayLength, rawKey.Length)];

        await _context.SaveChangesAsync();
        return (key, rawKey);
    }

    public async Task<bool> UpdateAsync(Guid id, string name, bool isEnabled, bool allowAllModels, IEnumerable<Guid> allowedPresetIds)
    {
        var key = await _context.ApiKeys
            .Include(k => k.AllowedPresets)
            .FirstOrDefaultAsync(k => k.Id == id);
        if (key is null) return false;

        key.Name = name;
        key.IsEnabled = isEnabled;
        key.AllowAllModels = allowAllModels;

        // Replace the scoping set wholesale — simplest correct approach for a small admin-edited list.
        _context.ApiKeyModelPresets.RemoveRange(key.AllowedPresets);
        key.AllowedPresets.Clear();

        if (!allowAllModels)
        {
            foreach (var presetId in allowedPresetIds.Distinct())
                key.AllowedPresets.Add(new ApiKeyModelPreset { ApiKeyId = key.Id, ModelPresetId = presetId });
        }

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var key = await _context.ApiKeys.FindAsync(id);
        if (key is null) return false;

        _context.ApiKeys.Remove(key);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<ApiKey>> GetAllAsync()
    {
        // Ordered client-side — SQLite can't translate ORDER BY on a DateTimeOffset column.
        var keys = await _context.ApiKeys
            .Include(k => k.AllowedPresets)
            .ToListAsync();
        return keys.OrderByDescending(k => k.CreatedAt).ToList().AsReadOnly();
    }

    public async Task<ApiKey?> GetByIdAsync(Guid id)
    {
        return await _context.ApiKeys
            .Include(k => k.AllowedPresets)
            .FirstOrDefaultAsync(k => k.Id == id);
    }

    public async Task<ApiKey?> ValidateAsync(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return null;

        var hash = Hash(rawKey);
        var key = await _context.ApiKeys
            .Include(k => k.AllowedPresets)
            .FirstOrDefaultAsync(k => k.KeyHash == hash);

        if (key is null || !key.IsEnabled)
            return null;

        key.LastUsedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();
        return key;
    }

    private static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return KeyPrefixLiteral + token;
    }

    private static string Hash(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
