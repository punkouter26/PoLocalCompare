using Moq;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;

using PoLocalCompare.Shared.Enums;

using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

public class RecordVerdictTests
{
    private static Model MakeLocalModel(ModelId id) =>
        new(id, $"Model {id}", ModelType.Local, tdpWatts: 115.0, webLlmModelId: "llm-1");

    private static Duel MakePendingDuel(DuelId duelId, ModelId leftId, ModelId rightId) =>
        new(duelId, "Test prompt", "Test prompt (full)", leftId, rightId);

    // ── Happy path: Left wins ──────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_LeftWins_UpdatesBothModelsElo()
    {
        var leftModel = MakeLocalModel(ModelId.From("left-1"));
        var rightModel = MakeLocalModel(ModelId.From("right-1"));
        var duel = MakePendingDuel(DuelId.From("duel-1"), leftModel.ModelId, rightModel.ModelId);

        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(DuelId.From("duel-1"))).ReturnsAsync(duel);

        var modelRepo = new Mock<IModelRepository>();
        modelRepo.Setup(r => r.GetByIdAsync(ModelId.From("left-1"))).ReturnsAsync(leftModel);
        modelRepo.Setup(r => r.GetByIdAsync(ModelId.From("right-1"))).ReturnsAsync(rightModel);

        var eloRepo = new Mock<IEloHistoryRepository>();

        var handler = new RecordVerdictHandler(duelRepo.Object, modelRepo.Object, eloRepo.Object, kFactor: 32);
        var cmd = new RecordVerdictCommand(DuelId.From("duel-1"), DuelVerdict.Left);

        var result = await handler.HandleAsync(cmd);

        Assert.NotNull(result);
        Assert.Equal(ModelId.From("left-1"), result.WinnerModelId);
        Assert.Equal(ModelId.From("right-1"), result.LoserModelId);
        Assert.True(result.EloShiftWinner > 0, "Winner ELO shift should be positive.");
        Assert.True(result.EloShiftLoser < 0, "Loser ELO shift should be negative.");
    }

    // ── Happy path: Right wins ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_RightWins_RightIsWinnerAndLeftIsLoser()
    {
        var leftModel = MakeLocalModel(ModelId.From("left-2"));
        var rightModel = MakeLocalModel(ModelId.From("right-2"));
        var duel = MakePendingDuel(DuelId.From("duel-2"), leftModel.ModelId, rightModel.ModelId);

        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(DuelId.From("duel-2"))).ReturnsAsync(duel);

        var modelRepo = new Mock<IModelRepository>();
        modelRepo.Setup(r => r.GetByIdAsync(ModelId.From("left-2"))).ReturnsAsync(leftModel);
        modelRepo.Setup(r => r.GetByIdAsync(ModelId.From("right-2"))).ReturnsAsync(rightModel);

        var eloRepo = new Mock<IEloHistoryRepository>();

        var handler = new RecordVerdictHandler(duelRepo.Object, modelRepo.Object, eloRepo.Object, kFactor: 32);
        var result = await handler.HandleAsync(new RecordVerdictCommand(DuelId.From("duel-2"), DuelVerdict.Right));

        Assert.NotNull(result);
        Assert.Equal(ModelId.From("right-2"), result.WinnerModelId);
        Assert.Equal(ModelId.From("left-2"), result.LoserModelId);
    }

    // ── Duplicate verdict → 409 (InvalidOperationException) ──────────────

    [Fact]
    public async Task HandleAsync_VerdictAlreadyRecorded_ThrowsInvalidOperation()
    {
        var leftModel = MakeLocalModel(ModelId.From("left-3"));
        var rightModel = MakeLocalModel(ModelId.From("right-3"));

        var duel = MakePendingDuel(DuelId.From("duel-3"), leftModel.ModelId, rightModel.ModelId);
        // Simulate already-recorded verdict
        duel.Verdict = DuelVerdict.Left;

        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(DuelId.From("duel-3"))).ReturnsAsync(duel);

        var modelRepo = new Mock<IModelRepository>();
        var eloRepo = new Mock<IEloHistoryRepository>();

        var handler = new RecordVerdictHandler(duelRepo.Object, modelRepo.Object, eloRepo.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new RecordVerdictCommand(DuelId.From("duel-3"), DuelVerdict.Left)));
    }

    // ── Pending verdict in command → ArgumentException (caller maps 422) ─

    [Fact]
    public async Task HandleAsync_PendingVerdictInCommand_ThrowsArgumentException()
    {
        var duelRepo = new Mock<IDuelRepository>();
        var modelRepo = new Mock<IModelRepository>();
        var eloRepo = new Mock<IEloHistoryRepository>();

        var handler = new RecordVerdictHandler(duelRepo.Object, modelRepo.Object, eloRepo.Object);

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(new RecordVerdictCommand(DuelId.From("duel-4"), DuelVerdict.Pending)));
    }

    // ── Duel not found → returns null ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_DuelNotFound_ReturnsNull()
    {
        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(It.IsAny<DuelId>())).ReturnsAsync((Duel?)null);

        var modelRepo = new Mock<IModelRepository>();
        var eloRepo = new Mock<IEloHistoryRepository>();

        var handler = new RecordVerdictHandler(duelRepo.Object, modelRepo.Object, eloRepo.Object);
        var result = await handler.HandleAsync(new RecordVerdictCommand(DuelId.From("missing"), DuelVerdict.Left));

        Assert.Null(result);
    }

    // ── EloRecord created for both models ─────────────────────────────────

    [Fact]
    public async Task HandleAsync_LeftWins_SavesEloRecordForBothModels()
    {
        var leftModel = MakeLocalModel(ModelId.From("left-5"));
        var rightModel = MakeLocalModel(ModelId.From("right-5"));
        var duel = MakePendingDuel(DuelId.From("duel-5"), leftModel.ModelId, rightModel.ModelId);

        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(DuelId.From("duel-5"))).ReturnsAsync(duel);

        var modelRepo = new Mock<IModelRepository>();
        modelRepo.Setup(r => r.GetByIdAsync(ModelId.From("left-5"))).ReturnsAsync(leftModel);
        modelRepo.Setup(r => r.GetByIdAsync(ModelId.From("right-5"))).ReturnsAsync(rightModel);

        var eloRepo = new Mock<IEloHistoryRepository>();

        var handler = new RecordVerdictHandler(duelRepo.Object, modelRepo.Object, eloRepo.Object, kFactor: 32);
        await handler.HandleAsync(new RecordVerdictCommand(DuelId.From("duel-5"), DuelVerdict.Left));

        // Verify SaveAsync called twice — once for winner, once for loser
        eloRepo.Verify(r => r.SaveAsync(It.IsAny<EloRecord>()), Times.Exactly(2));
    }

    // ── Winner DuelCount + WinCount updated; loser only DuelCount ─────────

    [Fact]
    public async Task HandleAsync_LeftWins_UpdatesDuelCountAndWinCountCorrectly()
    {
        var leftModel = MakeLocalModel(ModelId.From("left-6"));
        var rightModel = MakeLocalModel(ModelId.From("right-6"));
        var duel = MakePendingDuel(DuelId.From("duel-6"), leftModel.ModelId, rightModel.ModelId);

        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(DuelId.From("duel-6"))).ReturnsAsync(duel);

        var modelRepo = new Mock<IModelRepository>();
        modelRepo.Setup(r => r.GetByIdAsync(ModelId.From("left-6"))).ReturnsAsync(leftModel);
        modelRepo.Setup(r => r.GetByIdAsync(ModelId.From("right-6"))).ReturnsAsync(rightModel);

        var eloRepo = new Mock<IEloHistoryRepository>();

        var handler = new RecordVerdictHandler(duelRepo.Object, modelRepo.Object, eloRepo.Object, kFactor: 32);
        await handler.HandleAsync(new RecordVerdictCommand(DuelId.From("duel-6"), DuelVerdict.Left));

        // Winner (left): DuelCount++ and WinCount++
        Assert.Equal(1, leftModel.DuelCount);
        Assert.Equal(1, leftModel.WinCount);

        // Loser (right): DuelCount++ only, WinCount stays 0
        Assert.Equal(1, rightModel.DuelCount);
        Assert.Equal(0, rightModel.WinCount);
    }
}
