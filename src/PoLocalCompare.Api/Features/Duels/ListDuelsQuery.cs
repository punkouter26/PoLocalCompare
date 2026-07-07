namespace PoLocalCompare.Api.Features.Duels;

public sealed record ListDuelsQuery(int Limit = 20, string? BeforeMonth = null);
