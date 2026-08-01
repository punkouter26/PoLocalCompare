namespace PoLocalCompare.Client.Services;

/// <summary>
/// Stamps every outgoing request with X-Session-ID (stable for the WASM app lifetime)
/// and a fresh X-Correlation-ID, so server logs stitch into client sessions (standards §6.9).
/// </summary>
public sealed class CorrelationHandler : DelegatingHandler
{
    public static readonly string SessionId = Guid.NewGuid().ToString("N");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-Session-ID", SessionId);
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", Guid.NewGuid().ToString("N"));
        return base.SendAsync(request, cancellationToken);
    }
}
