using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoLocalCompare.Shared.Ids;

/// <summary>
/// Identity of a duel. A ULID string underneath, but distinct from <see cref="ModelId"/> at the
/// type level so the two can never be transposed — <see cref="DuelResult"/>-shaped types carry
/// both side by side, which is exactly where a raw-string swap goes unnoticed.
/// </summary>
/// <remarks>
/// Conversion is deliberately asymmetric: widening to <see cref="string"/> is implicit (lossless,
/// and keeps interpolation, logging and Table Storage keys terse), while narrowing from
/// <see cref="string"/> is explicit via <see cref="From"/> or <see cref="Parse(string)"/>. That way
/// a bare string can never drift into an id-typed position by accident.
/// </remarks>
[JsonConverter(typeof(DuelIdJsonConverter))]
public readonly record struct DuelId : IParsable<DuelId>, ISpanFormattable, IComparable<DuelId>, IComparable
{
    private readonly string? _value;

    private DuelId(string value) => _value = value;

    /// <summary>The underlying ULID text. Never null; empty for <c>default(DuelId)</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True when this is <c>default(DuelId)</c> — i.e. no duel.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Mints a fresh, lexicographically sortable id.</summary>
    public static DuelId New() => new(NUlid.Ulid.NewUlid().ToString());

    /// <summary>
    /// Wraps an existing id. Rejects null/whitespace but does not require ULID shape — ids
    /// already persisted in Table Storage predate this type and must keep round-tripping.
    /// </summary>
    public static DuelId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("DuelId cannot be null or whitespace.", nameof(value))
            : new DuelId(value);

    /// <summary>
    /// Reads an optional id — a Table Storage column that may be absent or an unset foreign key.
    /// Blank becomes <c>null</c> rather than throwing.
    /// </summary>
    public static DuelId? FromOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new DuelId(value);

    /// <summary>
    /// Reads a required id defensively, yielding <c>default</c> for a blank. Use at the storage
    /// boundary, where a malformed legacy row should read back empty rather than fail the query.
    /// </summary>
    public static DuelId FromOrDefault(string? value) =>
        string.IsNullOrWhiteSpace(value) ? default : new DuelId(value);

    public static DuelId Parse(string s, IFormatProvider? provider = null) => From(s);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out DuelId result)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            result = default;
            return false;
        }

        result = new DuelId(s);
        return true;
    }

    public static bool TryParse([NotNullWhen(true)] string? s, out DuelId result) => TryParse(s, null, out result);

    public bool Equals(DuelId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>
    /// Ordinal comparison, which for ULIDs is also chronological order — repositories sort on
    /// this to get newest-first listings. Without <see cref="IComparable{T}"/>, LINQ falls back
    /// to the default object comparer and throws "At least one object must implement IComparable"
    /// at run time rather than failing to compile.
    /// </summary>
    public int CompareTo(DuelId other) => string.CompareOrdinal(Value, other.Value);

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        DuelId other => CompareTo(other),
        _ => throw new ArgumentException($"Cannot compare a DuelId with {obj.GetType()}.", nameof(obj)),
    };

    public static bool operator <(DuelId left, DuelId right) => left.CompareTo(right) < 0;
    public static bool operator <=(DuelId left, DuelId right) => left.CompareTo(right) <= 0;
    public static bool operator >(DuelId left, DuelId right) => left.CompareTo(right) > 0;
    public static bool operator >=(DuelId left, DuelId right) => left.CompareTo(right) >= 0;

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
    public static implicit operator string(DuelId id) => id.Value;
}

/// <summary>Serializes as a bare JSON string, so the wire format is identical to the raw-string era.</summary>
public sealed class DuelIdJsonConverter : JsonConverter<DuelId>
{
    public override DuelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return string.IsNullOrWhiteSpace(raw) ? default : DuelId.From(raw);
    }

    public override void Write(Utf8JsonWriter writer, DuelId value, JsonSerializerOptions options)
    {
        if (value.IsEmpty)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value);
    }

    public override DuelId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DuelId.From(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, DuelId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);
}
