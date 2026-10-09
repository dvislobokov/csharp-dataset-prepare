using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlcDataset.Core;

/// <summary>Shared JSON settings. Property order follows declaration order, so output is deterministic.</summary>
public static class FlcJson
{
    public static readonly JsonSerializerOptions Compact = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            // Keep non-ASCII source text readable and byte-stable; JSON escaping of control chars still applies.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return o;
    }

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Indented : Compact);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Compact) ?? throw new InvalidDataException($"Cannot deserialize {typeof(T).Name}");
}
