using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed record RecordVerdictCommand(string DuelId, DuelVerdict Verdict);