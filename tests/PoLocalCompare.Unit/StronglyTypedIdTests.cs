using System.Text.Json;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

/// <summary>
/// The strongly-typed ids are the partition/row key of every table, so a regression here
/// breaks storage at run time — keeping the most behaviour-covering tests rather than the
/// most numerous ones, since the underlying invariants are concentrated in a few checks.
/// </summary>
public class StronglyTypedIdTests
{
    [Fact]
    public void From_RejectsBlank()
    {
        Assert.Throws<ArgumentException>(() => DuelId.From(null!));
        Assert.Throws<ArgumentException>(() => ModelId.From("   "));
    }

    [Fact]
    public void New_IsLexicographicallyTimeOrdered()
    {
        // The Duels table gets its newest-first ordering from sorting on this value, so the
        // ULID timestamp prefix has to be lexicographically ordered. The delay is load-bearing:
        // only the leading 48 bits encode the millisecond, and the rest are random.
        var first = DuelId.New();
        Thread.Sleep(2);
        var second = DuelId.New();

        Assert.True(
            string.CompareOrdinal(first.Value, second.Value) < 0,
            $"Expected '{first}' to sort before '{second}'.");
    }

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
    public void TryParse_RejectsBlankWithoutThrowing()
    {
        Assert.False(DuelId.TryParse("  ", null, out var id));
        Assert.True(id.IsEmpty);
    }
}
