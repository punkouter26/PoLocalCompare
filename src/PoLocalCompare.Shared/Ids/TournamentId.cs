using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoLocalCompare.Shared.Ids;

/// <summary>
/// Identity of a tournament — one bracket run. A ULID string underneath, but distinct from
/// <see cref="DuelId"/> at the type level: a bracket holds a duel id per played match, so the
/// two sit side by side in exactly the places a raw-string swap would go unnoticed.
/// </summary>
/// <remarks>
/// Conversion is asymmetric for the same reason <see cref="DuelId"/>'s is: widening to
/// <see cref="string"/> is implicit, narrowing is explicit via <see cref="From"/>.
/// </remarks>
[JsonConverter(typeof(TournamentIdJsonConverter))]
public readonly record struct TournamentId : IParsable<TournamentId>, ISpanFormattable, IComparable<TournamentId>, IComparable
{
    private readonly string? _value;

    private TournamentId(string value) => _value = value;

    /// <summary>The underlying ULID text. Never null; empty for <c>default(TournamentId)</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True when this is <c>default(TournamentId)</c> — i.e. no tournament.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Mints a fresh, lexicographically sortable id.</summary>
    public static TournamentId New() => new(NUlid.Ulid.NewUlid().ToString());

    /// <summary>
    /// Wraps an existing id. Rejects null/whitespace but does not require ULID shape — ids
    /// already persisted in Table Storage predate this type and must keep round-tripping.
    /// </summary>
    public static TournamentId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("TournamentId cannot be null or whitespace.", nameof(value))
            : new TournamentId(value);

    /// <summary>
    /// Reads an optional id — a Table Storage column that may be absent or an unset foreign key.
    /// Blank becomes <c>null</c> rather than throwing.
    /// </summary>
    public static TournamentId? FromOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new TournamentId(value);

    /// <summary>
    /// Reads a required id defensively, yielding <c>default</c> for a blank. Use at the storage
    /// boundary, where a malformed legacy row should read back empty rather than fail the query.
    /// </summary>
    public static TournamentId FromOrDefault(string? value) =>
        string.IsNullOrWhiteSpace(value) ? default : new TournamentId(value);

    public static TournamentId Parse(string s, IFormatProvider? provider = null) => From(s);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TournamentId result)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            result = default;
            return false;
        }

        result = new TournamentId(s);
        return true;
    }

    public static bool TryParse([NotNullWhen(true)] string? s, out TournamentId result) => TryParse(s, null, out result);

    public bool Equals(TournamentId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>
    /// Ordinal comparison, which for ULIDs is also chronological order — repositories sort on
    /// this to get newest-first listings. Without <see cref="IComparable{T}"/>, LINQ falls back
    /// to the default object comparer and throws "At least one object must implement IComparable"
    /// at run time rather than failing to compile.
    /// </summary>
    public int CompareTo(TournamentId other) => string.CompareOrdinal(Value, other.Value);

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        TournamentId other => CompareTo(other),
        _ => throw new ArgumentException($"Cannot compare a TournamentId with {obj.GetType()}.", nameof(obj)),
    };

    public static bool operator <(TournamentId left, TournamentId right) => left.CompareTo(right) < 0;
    public static bool operator <=(TournamentId left, TournamentId right) => left.CompareTo(right) <= 0;
    public static bool operator >(TournamentId left, TournamentId right) => left.CompareTo(right) > 0;
    public static bool operator >=(TournamentId left, TournamentId right) => left.CompareTo(right) >= 0;

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
    public static implicit operator string(TournamentId id) => id.Value;
}

/// <summary>Serializes as a bare JSON string, so the wire format is identical to the raw-string era.</summary>
public sealed class TournamentIdJsonConverter : JsonConverter<TournamentId>
{
    public override TournamentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return string.IsNullOrWhiteSpace(raw) ? default : TournamentId.From(raw);
    }

    public override void Write(Utf8JsonWriter writer, TournamentId value, JsonSerializerOptions options)
    {
        if (value.IsEmpty)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value);
    }

    public override TournamentId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TournamentId.From(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TournamentId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);
}
