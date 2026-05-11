namespace PoLocalCompare.Application.Duels.ListDuels;

public sealed record ListDuelsQuery(int Limit = 20, string? BeforeMonth = null);
