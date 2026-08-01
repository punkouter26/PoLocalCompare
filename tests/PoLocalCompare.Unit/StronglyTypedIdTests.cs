using System.Text.Json;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

public class StronglyTypedIdTests
{
    // ── Construction and validation ────────────────────────────────────────

    [Fact]
    public void From_KeepsTheUnderlyingText()
    {
        Assert.Equal("abc", DuelId.From("abc").Value);
        Assert.Equal("abc", ModelId.From("abc").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_RejectsBlank(string? bad)
    {
        Assert.Throws<ArgumentException>(() => DuelId.From(bad!));
        Assert.Throws<ArgumentException>(() => ModelId.From(bad!));
    }

    [Fact]
    public void From_AcceptsNonUlidText()
    {
        // Seeded ids ("01SEED…") and rows written before the type existed are not ULIDs;
        // rejecting them would make existing storage unreadable.
        Assert.Equal("01SEED0000000000000000001", ModelId.From("01SEED0000000000000000001").Value);
    }

    [Fact]
    public void New_MintsDistinctIds()
    {
        Assert.NotEqual(DuelId.New(), DuelId.New());
        Assert.NotEqual(ModelId.New(), ModelId.New());
    }

    [Fact]
    public async Task New_IsLexicographicallyTimeOrdered()
    {
        // The Duels table gets its newest-first ordering from sorting on this value, so the
        // ULID timestamp prefix has to be lexicographically ordered.
        //
        // The delay is load-bearing: only the leading 48 bits encode the millisecond, and the
        // remaining 80 are random. Two ids minted inside the same millisecond therefore have no
        // guaranteed relative order, so comparing back-to-back calls would be flaky.
        var first = DuelId.New();
        await Task.Delay(2);
        var second = DuelId.New();

        Assert.True(
            string.CompareOrdinal(first.Value, second.Value) < 0,
            $"Expected '{first}' to sort before '{second}'.");
    }

    // ── default / empty ────────────────────────────────────────────────────

    [Fact]
    public void Default_IsEmptyAndReadsAsEmptyString()
    {
        Assert.True(default(DuelId).IsEmpty);
        Assert.Equal(string.Empty, default(DuelId).Value);
        Assert.True(default(ModelId).IsEmpty);
        Assert.Equal(string.Empty, default(ModelId).Value);
    }

    [Fact]
    public void PopulatedId_IsNotEmpty()
    {
        Assert.False(DuelId.From("d").IsEmpty);
    }

    // ── FromOrNull / FromOrDefault ─────────────────────────────────────────

    [Fact]
    public void FromOrNull_BlankBecomesNull()
    {
        Assert.Null(ModelId.FromOrNull(null));
        Assert.Null(ModelId.FromOrNull(""));
        Assert.Null(ModelId.FromOrNull("  "));
    }

    [Fact]
    public void FromOrNull_ValueIsWrapped()
    {
        Assert.Equal(ModelId.From("m"), ModelId.FromOrNull("m"));
    }

    [Fact]
    public void FromOrDefault_BlankBecomesDefaultRatherThanThrowing()
    {
        // Used at the storage boundary: a malformed legacy row should read back empty
        // rather than fail the whole query.
        Assert.True(DuelId.FromOrDefault(null).IsEmpty);
        Assert.True(DuelId.FromOrDefault("").IsEmpty);
    }

    // ── Equality ───────────────────────────────────────────────────────────

    [Fact]
    public void Equality_IsOrdinalAndCaseSensitive()
    {
        Assert.Equal(DuelId.From("abc"), DuelId.From("abc"));
        Assert.NotEqual(DuelId.From("abc"), DuelId.From("ABC"));
    }

    [Fact]
    public void Equality_WorksAsADictionaryKey()
    {
        var map = new Dictionary<ModelId, int> { [ModelId.From("m1")] = 7 };

        Assert.True(map.TryGetValue(ModelId.From("m1"), out var value));
        Assert.Equal(7, value);
    }

    [Fact]
    public void GetHashCode_MatchesForEqualIds()
    {
        Assert.Equal(ModelId.From("m1").GetHashCode(), ModelId.From("m1").GetHashCode());
    }

    // ── Ordering ───────────────────────────────────────────────────────────

    [Fact]
    public void OrderBy_Sorts_WithoutThrowing()
    {
        // Regression guard. DuelRepository.ListAsync does OrderByDescending(d => d.DuelId).
        // A struct with no IComparable makes LINQ fall back to the default object comparer,
        // which throws "At least one object must implement IComparable" — at run time, on the
        // archive listing, with nothing failing at compile time to warn you.
        var ids = new[] { DuelId.From("c"), DuelId.From("a"), DuelId.From("b") };

        var ascending = ids.OrderBy(x => x).Select(x => x.Value).ToArray();

        Assert.Equal(["a", "b", "c"], ascending);
    }

    [Fact]
    public void OrderByDescending_PutsNewestUlidFirst()
    {
        var older = DuelId.From("01AAAAAAAAAAAAAAAAAAAAAAAA");
        var newer = DuelId.From("01ZZZZZZZZZZZZZZZZZZZZZZZZ");

        var sorted = new[] { older, newer }.OrderByDescending(x => x).ToArray();

        Assert.Equal(newer, sorted[0]);
    }

    [Fact]
    public void ModelId_IsAlsoSortable()
    {
        var sorted = new[] { ModelId.From("m2"), ModelId.From("m1") }.OrderBy(x => x).ToArray();

        Assert.Equal("m1", sorted[0].Value);
    }

    [Fact]
    public void CompareTo_IsOrdinal()
    {
        Assert.True(DuelId.From("a").CompareTo(DuelId.From("b")) < 0);
        Assert.True(DuelId.From("b").CompareTo(DuelId.From("a")) > 0);
        Assert.Equal(0, DuelId.From("a").CompareTo(DuelId.From("a")));
    }

    [Fact]
    public void ComparisonOperators_Agree_WithCompareTo()
    {
        var a = ModelId.From("a");
        var b = ModelId.From("b");

        Assert.True(a < b);
        Assert.True(a <= b);
        Assert.True(b > a);
        Assert.True(b >= a);
    }

    // ── Formatting ─────────────────────────────────────────────────────────

    [Fact]
    public void ToString_ReturnsTheBareValue()
    {
        Assert.Equal("d1", DuelId.From("d1").ToString());
    }

    [Fact]
    public void Interpolation_ProducesTheBareValue()
    {
        var id = DuelId.From("d1");

        Assert.Equal("/arena/d1", $"/arena/{id}");
    }

    [Fact]
    public void ImplicitConversionToString_IsLossless()
    {
        string s = ModelId.From("m1");

        Assert.Equal("m1", s);
    }

    // ── Parsing (route binding relies on IParsable) ────────────────────────

    [Fact]
    public void TryParse_AcceptsText()
    {
        Assert.True(DuelId.TryParse("d1", null, out var id));
        Assert.Equal(DuelId.From("d1"), id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryParse_RejectsBlankWithoutThrowing(string? bad)
    {
        Assert.False(DuelId.TryParse(bad, null, out var id));
        Assert.True(id.IsEmpty);
    }

    [Fact]
    public void Parse_RoundTripsThroughToString()
    {
        var original = ModelId.New();

        Assert.Equal(original, ModelId.Parse(original.ToString(), null));
    }

    // ── JSON: the wire format must be indistinguishable from a raw string ──

    [Fact]
    public void Serializes_AsABareJsonString()
    {
        Assert.Equal("\"d1\"", JsonSerializer.Serialize(DuelId.From("d1")));
    }

    [Fact]
    public void Deserializes_FromABareJsonString()
    {
        Assert.Equal(ModelId.From("m1"), JsonSerializer.Deserialize<ModelId>("\"m1\""));
    }

    [Fact]
    public void EmptyId_SerializesAsNull()
    {
        Assert.Equal("null", JsonSerializer.Serialize(default(DuelId)));
    }

    [Fact]
    public void NullJson_DeserializesToEmpty()
    {
        Assert.True(JsonSerializer.Deserialize<DuelId>("null").IsEmpty);
    }

    [Fact]
    public void DtoRoundTrip_KeepsTheSameWireShapeAsRawStrings()
    {
        // The client and any saved API consumer parse this payload; typing the ids must not
        // have changed a single character of it.
        var dto = new DuelResultDto
        {
            DuelId = DuelId.From("d1"),
            ModelId = ModelId.From("m1"),
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"DuelId\":\"d1\"", json);
        Assert.Contains("\"ModelId\":\"m1\"", json);

        var back = JsonSerializer.Deserialize<DuelResultDto>(json)!;
        Assert.Equal(dto.DuelId, back.DuelId);
        Assert.Equal(dto.ModelId, back.ModelId);
    }

    [Fact]
    public void NullableIdProperty_OmitsRatherThanEmitsWhenUnset()
    {
        var dto = new VerdictResponseDto { DuelId = DuelId.From("d1") };

        var json = JsonSerializer.Serialize(dto);
        var back = JsonSerializer.Deserialize<VerdictResponseDto>(json)!;

        Assert.Null(back.WinnerModelId);
    }

    // ── Boxing hazard ──────────────────────────────────────────────────────

    [Fact]
    public void BoxedToObject_DoesNotBecomeAString()
    {
        // Regression guard. The implicit string conversion does NOT apply when the target is
        // object — Azure's TableEntity indexer takes object, so assigning an id straight into
        // it boxes the struct and Table Storage rejects it with "Not supported type". Every
        // TableEntity column assignment therefore has to say .Value explicitly. If this ever
        // starts returning a string, that constraint has changed and the repositories can be
        // simplified.
        object boxed = ModelId.From("m1");

        Assert.IsNotType<string>(boxed);
        Assert.IsType<ModelId>(boxed);
    }

    [Fact]
    public void DotValue_IsAStringSuitableForTableStorage()
    {
        object columnValue = ModelId.From("m1").Value;

        Assert.IsType<string>(columnValue);
    }
}
