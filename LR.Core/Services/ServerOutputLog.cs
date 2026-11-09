using System.Globalization;

namespace LR.Core.Services;

/// <summary>
/// Everything a server writes, kept in a file on the router so it outlives the server's process (and
/// a container that removes itself). Each start begins a new file and keeps the one before it, so a
/// crash and the start after it can both be read.
/// </summary>
public sealed class ServerOutputLog : IDisposable
{
    /// <summary>A long-running server rolls over at this size rather than filling the disk.</summary>
    private const long MaxBytes = 20 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly string _path;
    private StreamWriter? _writer;
    private bool _failed;

    public ServerOutputLog(Guid instanceId)
    {
        _path = GetPath(instanceId);
    }

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "data", "server-output");

    public static string GetPath(Guid instanceId, bool previous = false) =>
        Path.Combine(Directory, $"{instanceId:N}{(previous ? ".previous" : "")}.log");

    /// <summary>Arguments are joined for display, with the value of any that carries a secret hidden.</summary>
    public static string MaskSecrets(IEnumerable<string> arguments)
    {
        var shown = new List<string>();
        var hideNext = false;
        foreach (var argument in arguments)
        {
            if (hideNext)
            {
                shown.Add("***");
                hideNext = false;
                continue;
            }
            var equals = argument.IndexOf('=');
            var name = equals > 0 ? argument[..equals] : argument;
            if (SecretArguments.Contains(name))
            {
                if (equals > 0)
                {
                    shown.Add(name + "=***");
                }
                else
                {
                    shown.Add(argument);
                    hideNext = true;
                }
                continue;
            }
            shown.Add(argument);
        }
        return string.Join(" ", shown);
    }

    private static readonly HashSet<string> SecretArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "--api-key", "--hf-token", "--token",
    };

    /// <summary>Starts a new file, keeping the last one as the previous output.</summary>
    public void BeginStart(string description)
    {
        lock (_lock)
        {
            CloseWriter();
            _failed = false;
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (File.Exists(_path))
                    File.Move(_path, PreviousPath, overwrite: true);
            }
            catch (IOException) { /* keep appending to the current file */ }
            catch (UnauthorizedAccessException) { }
            WriteLocked($"==== {Timestamp()} {description}");
        }
    }

    /// <summary>Adds a line of the server's output.</summary>
    public void Append(string line)
    {
        lock (_lock)
            WriteLocked(line);
    }

    /// <summary>Adds a note from the router, set apart from the server's own output.</summary>
    public void Note(string message)
    {
        lock (_lock)
            WriteLocked($"==== {Timestamp()} {message}");
    }

    public void Dispose()
    {
        lock (_lock)
            CloseWriter();
    }

    private string PreviousPath => Path.ChangeExtension(_path, null) + ".previous.log";

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private void WriteLocked(string line)
    {
        if (_failed) return;
        try
        {
            if (_writer is null)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream) { AutoFlush = true };
            }
            else if (_writer.BaseStream.Length > MaxBytes)
            {
                CloseWriter();
                File.Move(_path, PreviousPath, overwrite: true);
                WriteLocked($"==== {Timestamp()} Continued; earlier output is in the previous log.");
                if (_failed) return;
            }
            _writer!.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break a server; give up on the file until the next start.
            _failed = true;
            CloseWriter();
        }
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); }
        catch (IOException) { }
        _writer = null;
    }
}
