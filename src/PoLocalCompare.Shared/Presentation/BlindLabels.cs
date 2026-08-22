namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// The stand-in names the Arena shows while a duel is blind.
/// </summary>
/// <remarks>
/// Centralised because the mask has to be total to be worth anything: the viewport caption, the
/// race lanes, the scorecard columns, the cost breakdown, the failure cards, the View Source tab
/// title and the vote buttons all name a side, and a single one of them leaking the real name
/// defeats the whole feature. Every one of those reads its name from the Arena's
/// <c>LeftDisplayName</c> / <c>RightDisplayName</c>, which return these while blind.
/// </remarks>
public static class BlindLabels
{
    public const string Left = "Model A";
    public const string Right = "Model B";

    /// <summary>The masked name for a side, or <paramref name="real"/> when not blind.</summary>
    public static string For(bool blind, bool isLeft, string real) =>
        blind ? (isLeft ? Left : Right) : real;
}
