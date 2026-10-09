using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FlcDataset.Core;

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>Content-derived identifier: first 32 hex chars of SHA-256 over '\u001f'-joined parts.</summary>
    public static string StableId(params string[] parts) => Sha256Hex(string.Join('\u001f', parts))[..32];

    /// <summary>
    /// Allocation-free equivalent of <c>Uniform(seed, "caret", sourceSha, offset.ToString())</c>, used for every caret candidate.
    /// </summary>
    public static double CaretUniform(string seed, string sourceSha, int offset)
    {
        Span<char> chars = stackalloc char[seed.Length + sourceSha.Length + 24];
        int n = 0;
        seed.AsSpan().CopyTo(chars); n += seed.Length;
        chars[n++] = '\u001f';
        "caret".AsSpan().CopyTo(chars[n..]); n += 5;
        chars[n++] = '\u001f';
        sourceSha.AsSpan().CopyTo(chars[n..]); n += sourceSha.Length;
        chars[n++] = '\u001f';
        offset.TryFormat(chars[n..], out var w, provider: System.Globalization.CultureInfo.InvariantCulture); n += w;
        Span<byte> bytes = stackalloc byte[Encoding.UTF8.GetMaxByteCount(n)];
        int len = Encoding.UTF8.GetBytes(chars[..n], bytes);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes[..len], digest);
        return (BinaryPrimitives.ReadUInt64BigEndian(digest) >> 11) / (double)(1UL << 53);
    }

    /// <summary>Deterministic uniform value in [0,1) derived from the given parts (seeded sampling).</summary>
    public static double Uniform(params string[] parts)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(digest) >> 11; // 53 bits
        return v / (double)(1UL << 53);
    }
}
