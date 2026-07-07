// GoF: Value Object
namespace PoLocalCompare.Api.Common.Domain;

/// <summary>
/// Represents the green efficiency score: tokens generated per watt-hour of energy consumed.
/// Higher = more efficient. Only applicable to local models.
/// </summary>
public sealed class GreenScore
{
    public double Value { get; }

    public GreenScore(int tokenCount, double energyWh)
    {
        if (energyWh <= 0)
            throw new ArgumentException("Energy consumption must be greater than zero.", nameof(energyWh));

        Value = tokenCount / energyWh;
    }

    public override string ToString() => Value.ToString("F2");
}
