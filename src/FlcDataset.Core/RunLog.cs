using System.Text;

namespace FlcDataset.Core;

/// <summary>Structured JSONL run log (one event per line) plus optional human-readable console echo.</summary>
public sealed class RunLog : IDisposable
{
    readonly StreamWriter? _writer;
    readonly bool _console;
    readonly Lock _lock = new();

    public RunLog(string? path, bool console = true)
    {
        _console = console;
        if (path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { NewLine = "\n" };
        }
    }

    public static RunLog Null { get; } = new(null, console: false);

    public void Info(string stage, string evt, object? data = null) => Write("info", stage, evt, data);
    public void Warn(string stage, string evt, object? data = null) => Write("warn", stage, evt, data);
    public void Error(string stage, string evt, object? data = null) => Write("error", stage, evt, data);

    void Write(string level, string stage, string evt, object? data)
    {
        var entry = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["level"] = level,
            ["stage"] = stage,
            ["event"] = evt,
            ["data"] = data,
        };
        var line = FlcJson.Serialize(entry);
        lock (_lock)
        {
            _writer?.WriteLine(line);
            if (_console && level != "debug")
                Console.Error.WriteLine($"[{level}] {stage}/{evt}{(data is null ? "" : " " + FlcJson.Serialize(data))}");
        }
    }

    public void Dispose()
    {
        lock (_lock) _writer?.Dispose();
    }
}
