// GoF: Value Object
namespace PoLocalCompare.Api.Common.Domain;

/// <summary>
/// Represents the ratio of functional (non-whitespace, non-comment) characters to total character count
/// in an HTML output. Indicates how much of the output is meaningful code vs padding.
/// </summary>
public sealed class CharacterDensity
{
    public double Value { get; }

    public CharacterDensity(int functionalCharCount, long totalCharCount)
    {
        if (totalCharCount <= 0)
            throw new ArgumentException("Total character count must be greater than zero.", nameof(totalCharCount));

        Value = (double)functionalCharCount / totalCharCount;
    }

    public override string ToString() => Value.ToString("F4");
}
