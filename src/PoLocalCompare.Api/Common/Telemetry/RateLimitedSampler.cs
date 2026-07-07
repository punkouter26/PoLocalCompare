using OpenTelemetry.Trace;

namespace PoLocalCompare.Api.Common.Telemetry;

/// <summary>
/// Token-bucket head sampler capping sampled-in root traces per second (standards §6.3).
/// Exceptions bypass this entirely: they flow to Application Insights at 100% via the
/// Serilog error sink, independent of trace sampling.
/// </summary>
public sealed class RateLimitedSampler(double maxTracesPerSecond) : Sampler
{
    private readonly double _capacity = Math.Max(1, maxTracesPerSecond);
    private readonly Lock _gate = new();
    private double _tokens = Math.Max(1, maxTracesPerSecond);
    private long _lastRefillTicks = TimeProvider.System.GetTimestamp();

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        lock (_gate)
        {
            var now = TimeProvider.System.GetTimestamp();
            var elapsedSeconds = (now - _lastRefillTicks) / (double)TimeProvider.System.TimestampFrequency;
            _lastRefillTicks = now;
            _tokens = Math.Min(_capacity, _tokens + (elapsedSeconds * _capacity));

            if (_tokens < 1)
                return new SamplingResult(SamplingDecision.Drop);

            _tokens -= 1;
            return new SamplingResult(SamplingDecision.RecordAndSample);
        }
    }
}
