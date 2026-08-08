using System.Text;
using System.Text.Json;

namespace LR.Core.Wrapper;

/// <summary>
/// Thin NDJSON framing helper shared by both ends of the router&lt;-&gt;wrapper named pipe,
/// so the wire format is defined in exactly one place.
/// </summary>
public sealed class WrapperPipeConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WrapperPipeConnection(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    }

    /// <summary>
    /// Serializes and writes one message, terminated by a newline. Safe to call concurrently —
    /// writes are serialized internally since output pumping and command replies share one connection.
    /// </summary>
    public async Task SendAsync(WrapperMessage message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message, typeof(WrapperMessage), JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads and deserializes the next message. Returns null when the connection is closed.
    /// </summary>
    public async Task<WrapperMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var line = await _reader.ReadLineAsync(ct);
        if (line is null) return null;
        if (line.Length == 0) return null;

        return (WrapperMessage?)JsonSerializer.Deserialize(line, typeof(WrapperMessage), JsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        _reader.Dispose();
        await _stream.DisposeAsync();
    }
}
