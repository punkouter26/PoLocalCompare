using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Application.Duels.RecordVerdict;

public sealed record RecordVerdictCommand(string DuelId, DuelVerdict Verdict);
