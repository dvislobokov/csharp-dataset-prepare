using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FlcDataset.Core;

/// <summary>Abstract record sink so other formats (e.g. Parquet) can be added later.</summary>
public interface IRecordWriter<in T> : IDisposable
{
    void Write(T record);
    OutputFileInfo Complete();
}

public sealed record OutputFileInfo(string Path, long Records, long Bytes, string Sha256);

/// <summary>
/// Writes JSONL (optionally gzip) to "&lt;path&gt;.partial" and renames on <see cref="Complete"/>, so an interrupted run never
/// leaves a truncated file under the final name. LF line endings, UTF-8 without BOM.
/// </summary>
public sealed class JsonlWriter<T> : IRecordWriter<T>
{
    readonly string _finalPath;
    readonly string _partialPath;
    readonly FileStream _file;
    readonly Stream _stream;
    readonly byte[] _newline = "\n"u8.ToArray();
    long _records;
    bool _completed;

    public JsonlWriter(string path, bool gzip = false)
    {
        _finalPath = gzip && !path.EndsWith(".gz") ? path + ".gz" : path;
        _partialPath = _finalPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_finalPath))!);
        _file = new FileStream(_partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        _stream = gzip ? new GZipStream(_file, CompressionLevel.SmallestSize, leaveOpen: true) : _file;
    }

    public long Records => _records;

    public void Write(T record)
    {
        var bytes = Encoding.UTF8.GetBytes(FlcJson.Serialize(record));
        _stream.Write(bytes);
        _stream.Write(_newline);
        _records++;
    }

    public OutputFileInfo Complete()
    {
        if (_completed) throw new InvalidOperationException("Already completed");
        _completed = true;
        if (_stream != _file) _stream.Dispose();
        _file.Flush(flushToDisk: true);
        _file.Dispose();
        File.Move(_partialPath, _finalPath, overwrite: true);
        return new OutputFileInfo(_finalPath, _records, new FileInfo(_finalPath).Length, Hashing.Sha256File(_finalPath));
    }

    public void Dispose()
    {
        if (_completed) return;
        if (_stream != _file) _stream.Dispose();
        _file.Dispose();
        // Incomplete output is left as *.partial for diagnosis; it is never mistaken for a finished shard.
    }
}

public static class Jsonl
{
    public static IEnumerable<string> ReadLines(string path)
    {
        using var fs = File.OpenRead(path);
        using Stream s = path.EndsWith(".gz") ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        using var reader = new StreamReader(s, new UTF8Encoding(false, true));
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) yield return line;
    }

    public static IEnumerable<T> Read<T>(string path) => ReadLines(path).Select(FlcJson.Deserialize<T>);

    /// <summary>Resolve "name.jsonl" or "name.jsonl.gz" inside a dataset directory.</summary>
    public static string? Find(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        if (File.Exists(p)) return p;
        return File.Exists(p + ".gz") ? p + ".gz" : null;
    }
}
