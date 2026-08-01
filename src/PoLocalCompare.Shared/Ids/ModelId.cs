using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoLocalCompare.Shared.Ids;

/// <summary>
/// Identity of a model in the registry. See <see cref="DuelId"/> for the conversion rationale —
/// the pair exists so <c>(duelId, modelId)</c> argument lists cannot be passed in the wrong order.
/// </summary>
[JsonConverter(typeof(ModelIdJsonConverter))]
public readonly record struct ModelId : IParsable<ModelId>, ISpanFormattable, IComparable<ModelId>, IComparable
{
    private readonly string? _value;

    private ModelId(string value) => _value = value;

    /// <summary>The underlying ULID text. Never null; empty for <c>default(ModelId)</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True when this is <c>default(ModelId)</c> — i.e. no model.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Mints a fresh, lexicographically sortable id.</summary>
    public static ModelId New() => new(NUlid.Ulid.NewUlid().ToString());

    /// <summary>
    /// Wraps an existing id. Rejects null/whitespace but does not require ULID shape — seeded
    /// models and ids already in Table Storage must keep round-tripping.
    /// </summary>
    public static ModelId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("ModelId cannot be null or whitespace.", nameof(value))
            : new ModelId(value);

    /// <summary>
    /// Reads an optional id — a Table Storage column that may be absent or an unset foreign key
    /// (a duel with no winner yet). Blank becomes <c>null</c> rather than throwing.
    /// </summary>
    public static ModelId? FromOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new ModelId(value);

    /// <summary>
    /// Reads a required id defensively, yielding <c>default</c> for a blank. Use at the storage
    /// boundary, where a malformed legacy row should read back empty rather than fail the query.
    /// </summary>
    public static ModelId FromOrDefault(string? value) =>
        string.IsNullOrWhiteSpace(value) ? default : new ModelId(value);

    public static ModelId Parse(string s, IFormatProvider? provider = null) => From(s);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ModelId result)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            result = default;
            return false;
        }

        result = new ModelId(s);
        return true;
    }

    public static bool TryParse([NotNullWhen(true)] string? s, out ModelId result) => TryParse(s, null, out result);

    public bool Equals(ModelId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>
    /// Ordinal comparison, which for ULIDs is also chronological order — repositories sort on
    /// this to get newest-first listings. Without <see cref="IComparable{T}"/>, LINQ falls back
    /// to the default object comparer and throws "At least one object must implement IComparable"
    /// at run time rather than failing to compile.
    /// </summary>
    public int CompareTo(ModelId other) => string.CompareOrdinal(Value, other.Value);

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        ModelId other => CompareTo(other),
        _ => throw new ArgumentException($"Cannot compare a ModelId with {obj.GetType()}.", nameof(obj)),
    };

    public static bool operator <(ModelId left, ModelId right) => left.CompareTo(right) < 0;
    public static bool operator <=(ModelId left, ModelId right) => left.CompareTo(right) <= 0;
    public static bool operator >(ModelId left, ModelId right) => left.CompareTo(right) > 0;
    public static bool operator >=(ModelId left, ModelId right) => left.CompareTo(right) >= 0;

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public string ToString(string? format, IFormatProvider? formatProvider) => Value;

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (Value.TryCopyTo(destination))
        {
            charsWritten = Value.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }

    /// <summary>Widening to <see cref="string"/> is lossless, so it is implicit.</summary>
    public static implicit operator string(ModelId id) => id.Value;
}

/// <summary>Serializes as a bare JSON string, so the wire format is identical to the raw-string era.</summary>
public sealed class ModelIdJsonConverter : JsonConverter<ModelId>
{
    public override ModelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return string.IsNullOrWhiteSpace(raw) ? default : ModelId.From(raw);
    }

    public override void Write(Utf8JsonWriter writer, ModelId value, JsonSerializerOptions options)
    {
        if (value.IsEmpty)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value);
    }

    public override ModelId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ModelId.From(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, ModelId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);
}
