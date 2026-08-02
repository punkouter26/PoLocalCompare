using Microsoft.Extensions.DependencyInjection;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Integration;

/// <summary>
/// Drives the repositories directly against Azurite. These behaviours — ETag-conditional
/// updates, idempotent creates, the ULID-derived partition key, descending Elo history — are
/// invisible through the HTTP surface but are exactly what the standards §5.5 write discipline
/// promises, and only a real Table Storage endpoint can prove them.
/// </summary>
[Collection("Integration")]
public sealed class PersistenceTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;
    private IServiceScope _scope = null!;

    private IDuelRepository Duels => _scope.ServiceProvider.GetRequiredService<IDuelRepository>();
    private IDuelResultRepository Results => _scope.ServiceProvider.GetRequiredService<IDuelResultRepository>();
    private IModelRepository Models => _scope.ServiceProvider.GetRequiredService<IModelRepository>();
    private IEloHistoryRepository EloHistory => _scope.ServiceProvider.GetRequiredService<IEloHistoryRepository>();

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        _scope = _host.Services.CreateScope();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    private static Duel NewDuel(out ModelId left, out ModelId right)
    {
        left = ModelId.New();
        right = ModelId.New();
        return new Duel(DuelId.New(), "Prompt text", "Prompt text (full)", left, right);
    }

    // ── Duels ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Duel_RoundTripsThroughTableStorage()
    {
        var duel = NewDuel(out var left, out var right);

        await Duels.SaveAsync(duel);
        var loaded = await Duels.GetByIdAsync(duel.DuelId);

        Assert.NotNull(loaded);
        Assert.Equal(duel.DuelId, loaded!.DuelId);
        Assert.Equal(left, loaded.LeftModelId);
        Assert.Equal(right, loaded.RightModelId);
        Assert.Equal("Prompt text", loaded.PromptText);
    }

    [Fact]
    public async Task Duel_TypedIdsSurviveTheRoundTrip()
    {
        // The ids are stored as strings and rehydrated through ModelId.FromOrDefault; a
        // regression there would silently produce empty ids rather than fail loudly.
        var duel = NewDuel(out var left, out _);

        await Duels.SaveAsync(duel);
        var loaded = await Duels.GetByIdAsync(duel.DuelId);

        Assert.False(loaded!.LeftModelId.IsEmpty);
        Assert.Equal(left.Value, loaded.LeftModelId.Value);
    }

    [Fact]
    public async Task Duel_UnknownId_ReadsBackNull()
    {
        Assert.Null(await Duels.GetByIdAsync(DuelId.New()));
    }

    [Fact]
    public async Task Duel_NewlySaved_IsPendingWithNoWinner()
    {
        var duel = NewDuel(out _, out _);

        await Duels.SaveAsync(duel);
        var loaded = await Duels.GetByIdAsync(duel.DuelId);

        Assert.Equal(DuelVerdict.Pending, loaded!.Verdict);
        Assert.Null(loaded.WinnerModelId);
        Assert.Null(loaded.LoserModelId);
    }

    [Fact]
    public async Task Duel_SaveIsIdempotent()
    {
        // Standards §5.5: a repeated create must swallow the 409 rather than surface it —
        // the background queue can legitimately replay a work item.
        var duel = NewDuel(out _, out _);

        await Duels.SaveAsync(duel);
        var exception = await Record.ExceptionAsync(() => Duels.SaveAsync(duel));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Duel_CarriesAnETagWhenLoaded()
    {
        var duel = NewDuel(out _, out _);
        await Duels.SaveAsync(duel);

        var loaded = await Duels.GetByIdAsync(duel.DuelId);

        Assert.False(string.IsNullOrWhiteSpace(loaded!.ETag));
    }

    [Fact]
    public async Task Duel_UpdatePersistsTheVerdictAndItsSource()
    {
        var duel = NewDuel(out var left, out var right);
        await Duels.SaveAsync(duel);

        var loaded = await Duels.GetByIdAsync(duel.DuelId);
        loaded!.Verdict = DuelVerdict.Left;
        loaded.WinnerModelId = left;
        loaded.LoserModelId = right;
        loaded.VerdictSource = VerdictSource.Ai;
        loaded.JudgeRationale = "Left implemented the prompt more completely.";
        loaded.JudgeModel = "gpt-5.4-nano";
        loaded.CompletedAt = DateTimeOffset.UtcNow;
        await Duels.UpdateAsync(loaded);

        var reloaded = await Duels.GetByIdAsync(duel.DuelId);
        Assert.Equal(DuelVerdict.Left, reloaded!.Verdict);
        Assert.Equal(left, reloaded.WinnerModelId);
        Assert.Equal(right, reloaded.LoserModelId);
        Assert.Equal(VerdictSource.Ai, reloaded.VerdictSource);
        Assert.Equal("gpt-5.4-nano", reloaded.JudgeModel);
    }

    [Fact]
    public async Task Duel_WrittenBeforeVerdictSourceExisted_ReadsBackAsHuman()
    {
        // The column defaults to Human on read precisely so historical rows keep meaning
        // what they meant; blending them into the AI-ranked pool would be unrecoverable.
        var duel = NewDuel(out _, out _);
        await Duels.SaveAsync(duel);

        var loaded = await Duels.GetByIdAsync(duel.DuelId);

        Assert.Equal(VerdictSource.Human, loaded!.VerdictSource);
    }

    [Fact]
    public async Task Duel_ListReturnsSavedDuelsNewestFirst()
    {
        var first = NewDuel(out _, out _);
        await Duels.SaveAsync(first);
        await Task.Delay(2);
        var second = NewDuel(out _, out _);
        await Duels.SaveAsync(second);

        var listed = (await Duels.ListAsync(50, null)).ToList();

        var firstIndex = listed.FindIndex(d => d.DuelId == first.DuelId);
        var secondIndex = listed.FindIndex(d => d.DuelId == second.DuelId);
        Assert.True(secondIndex >= 0 && firstIndex >= 0);
        Assert.True(secondIndex < firstIndex, "The newer duel should sort ahead of the older one.");
    }

    [Fact]
    public async Task Duel_ListRespectsTheLimit()
    {
        for (var i = 0; i < 3; i++)
            await Duels.SaveAsync(NewDuel(out _, out _));

        var listed = await Duels.ListAsync(2, null);

        Assert.Equal(2, listed.Count());
    }

    [Fact]
    public void Duel_RefusesToPitAModelAgainstItself()
    {
        var same = ModelId.New();

        Assert.Throws<ArgumentException>(() =>
            new Duel(DuelId.New(), "Prompt", "Prompt", same, same));
    }

    [Fact]
    public void Duel_RefusesAnEmptyPrompt()
    {
        Assert.Throws<ArgumentException>(() =>
            new Duel(DuelId.New(), "   ", "   ", ModelId.New(), ModelId.New()));
    }

    // ── Duel results ───────────────────────────────────────────────────────

    [Fact]
    public async Task DuelResult_RoundTripsWithItsTelemetry()
    {
        var duelId = DuelId.New();
        var modelId = ModelId.New();
        await Results.SaveAsync(new DuelResult(duelId, modelId)
        {
            HtmlOutputRaw = "<html><body>x</body></html>",
            TokenCount = 123,
            TotalDurationMs = 4_567,
            OutputQualityScore = 90,
        });

        var loaded = await Results.GetAsync(duelId, modelId);

        Assert.NotNull(loaded);
        Assert.Equal(123, loaded!.TokenCount);
        Assert.Equal(4_567, loaded.TotalDurationMs);
        Assert.Equal(90, loaded.OutputQualityScore);
    }

    [Fact]
    public async Task DuelResult_IsKeyedByBothIds()
    {
        // DuelId is the partition and ModelId the row: the two results of one duel must not
        // overwrite each other.
        var duelId = DuelId.New();
        var left = ModelId.New();
        var right = ModelId.New();

        await Results.SaveAsync(new DuelResult(duelId, left) { TokenCount = 1 });
        await Results.SaveAsync(new DuelResult(duelId, right) { TokenCount = 2 });

        var all = (await Results.GetByDuelIdAsync(duelId)).ToList();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.ModelId == left && r.TokenCount == 1);
        Assert.Contains(all, r => r.ModelId == right && r.TokenCount == 2);
    }

    [Theory]
    [InlineData("' or PartitionKey ne '")]
    [InlineData("x' or RowKey ne 'x")]
    public async Task DuelResult_QuoteInId_CannotWidenTheFilterToOtherPartitions(string payload)
    {
        // Ids are not shape-validated (DuelId.From takes any non-blank string so legacy rows keep
        // round-tripping), so a route-supplied id reaches the OData filter verbatim. Pasted in
        // raw, these payloads close the quoted literal and turn the scoped read into a full-table
        // read of every duel's results. Bound as an escaped literal, they simply match nothing.
        var otherDuel = DuelId.New();
        await Results.SaveAsync(new DuelResult(otherDuel, ModelId.New()) { TokenCount = 7 });

        var leaked = await Results.GetByDuelIdAsync(DuelId.From(payload));

        Assert.Empty(leaked);
    }

    [Fact]
    public async Task DuelResult_FailureDetailsPersist()
    {
        var duelId = DuelId.New();
        var modelId = ModelId.New();
        await Results.SaveAsync(new DuelResult(duelId, modelId)
        {
            IsFailure = true,
            FailureReason = "WebGPU device lost",
        });

        var loaded = await Results.GetAsync(duelId, modelId);

        Assert.True(loaded!.IsFailure);
        Assert.Equal("WebGPU device lost", loaded.FailureReason);
    }

    [Fact]
    public async Task DuelResult_UnknownPair_ReadsBackNull()
    {
        Assert.Null(await Results.GetAsync(DuelId.New(), ModelId.New()));
    }

    [Fact]
    public async Task DuelResult_GetByDuelId_IsEmptyForAnUnknownDuel()
    {
        Assert.Empty(await Results.GetByDuelIdAsync(DuelId.New()));
    }

    // ── Models ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Model_RoundTripsThroughTableStorage()
    {
        var id = ModelId.New();
        await Models.SaveAsync(new Model(id, $"Persisted {Guid.NewGuid():N}", ModelType.Remote,
            apiEndpointRef: "dep-1", inputTokenPricePerMillion: 0.25m, outputTokenPricePerMillion: 1.5m));

        var loaded = await Models.GetByIdAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(ModelType.Remote, loaded!.ModelType);
        Assert.Equal(0.25m, loaded.InputTokenPricePerMillion);
        Assert.Equal(1.5m, loaded.OutputTokenPricePerMillion);
    }

    [Fact]
    public async Task Model_UnknownId_ReadsBackNull()
    {
        Assert.Null(await Models.GetByIdAsync(ModelId.New()));
    }

    [Fact]
    public async Task Model_UpdatePersistsEloAndCounters()
    {
        var id = ModelId.New();
        await Models.SaveAsync(new Model(id, $"Elo {Guid.NewGuid():N}", ModelType.Remote, apiEndpointRef: "dep-1"));

        var loaded = await Models.GetByIdAsync(id);
        loaded!.CurrentElo = 1234.5;
        loaded.DuelCount = 3;
        loaded.WinCount = 2;
        await Models.UpdateAsync(loaded);

        var reloaded = await Models.GetByIdAsync(id);
        Assert.Equal(1234.5, reloaded!.CurrentElo);
        Assert.Equal(3, reloaded.DuelCount);
        Assert.Equal(2, reloaded.WinCount);
    }

    [Fact]
    public async Task Model_DeleteRemovesIt()
    {
        var id = ModelId.New();
        await Models.SaveAsync(new Model(id, $"Doomed {Guid.NewGuid():N}", ModelType.Remote, apiEndpointRef: "dep-1"));

        await Models.DeleteAsync(id);

        Assert.Null(await Models.GetByIdAsync(id));
    }

    // ── Elo history ────────────────────────────────────────────────────────

    [Fact]
    public async Task EloHistory_RoundTripsARecord()
    {
        var modelId = ModelId.New();
        var opponent = ModelId.New();
        await EloHistory.SaveAsync(new EloRecord(modelId, DuelId.New(), 1216, 1200, "Win", opponent, 1200));

        var records = (await EloHistory.GetAllByModelAsync(modelId)).ToList();

        var record = Assert.Single(records);
        Assert.Equal(1216, record.EloAfter);
        Assert.Equal(1200, record.EloBefore);
        Assert.Equal(16, record.EloShift);
        Assert.Equal("Win", record.Outcome);
        Assert.Equal(opponent, record.OpponentModelId);
    }

    [Fact]
    public async Task EloHistory_ShiftIsDerivedNotStoredIndependently()
    {
        var modelId = ModelId.New();
        await EloHistory.SaveAsync(new EloRecord(modelId, DuelId.New(), 1184, 1200, "Loss", ModelId.New(), 1200));

        var record = Assert.Single(await EloHistory.GetAllByModelAsync(modelId));

        Assert.Equal(-16, record.EloShift);
    }

    [Fact]
    public async Task EloHistory_ReturnsNewestFirst()
    {
        // The RowKey is invertedTicks_DuelId precisely so Table Storage's ascending scan
        // yields descending time; the sparkline depends on that ordering.
        var modelId = ModelId.New();
        await EloHistory.SaveAsync(new EloRecord(modelId, DuelId.New(), 1210, 1200, "Win", ModelId.New(), 1200));
        await Task.Delay(2);
        await EloHistory.SaveAsync(new EloRecord(modelId, DuelId.New(), 1220, 1210, "Win", ModelId.New(), 1200));

        var records = (await EloHistory.GetAllByModelAsync(modelId)).ToList();

        Assert.Equal(2, records.Count);
        Assert.True(records[0].RecordedAt >= records[1].RecordedAt);
    }

    [Fact]
    public async Task EloHistory_GetLast20_CapsTheResultSet()
    {
        var modelId = ModelId.New();
        for (var i = 0; i < 22; i++)
            await EloHistory.SaveAsync(new EloRecord(modelId, DuelId.New(), 1200 + i, 1199 + i, "Win", ModelId.New(), 1200));

        var records = await EloHistory.GetLast20Async(modelId);

        Assert.True(records.Count() <= 20);
    }

    [Fact]
    public async Task EloHistory_IsPartitionedPerModel()
    {
        var mine = ModelId.New();
        var theirs = ModelId.New();
        await EloHistory.SaveAsync(new EloRecord(mine, DuelId.New(), 1210, 1200, "Win", theirs, 1200));
        await EloHistory.SaveAsync(new EloRecord(theirs, DuelId.New(), 1190, 1200, "Loss", mine, 1200));

        var mineRecords = await EloHistory.GetAllByModelAsync(mine);

        Assert.Single(mineRecords);
        Assert.Equal("Win", mineRecords.Single().Outcome);
    }

    [Fact]
    public async Task EloHistory_UnknownModel_IsEmpty()
    {
        Assert.Empty(await EloHistory.GetAllByModelAsync(ModelId.New()));
    }
}
