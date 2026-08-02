namespace PoLocalCompare.Client.Components;

/// <summary>
/// What a model's output actually did once it ran, as reported by the probe injected into the
/// sandboxed preview.
/// </summary>
/// <remarks>
/// Mutable and mutated in place: probe messages trickle in over the life of the frame, and
/// allocating a fresh report per message would make the Arena's re-render churn for no reason.
/// Absence of errors is only meaningful once <see cref="Loaded"/> is true — before that, the
/// page may simply not have got there yet.
/// </remarks>
public sealed class RuntimeProbeReport
{
    private const int MaxRetainedMessages = 5;

    private readonly List<string> _errors = [];
    private readonly List<string> _resourceFailures = [];
    private readonly List<string> _consoleErrors = [];

    /// <summary>True once the frame raised its load event.</summary>
    public bool Loaded { get; set; }

    /// <summary>Milliseconds from document start to the load event, as measured inside the frame.</summary>
    public long? LoadMs { get; set; }

    public int ErrorCount { get; private set; }
    public int ResourceFailureCount { get; private set; }
    public int ConsoleErrorCount { get; private set; }

    /// <summary>Uncaught exceptions and rejected promises.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>External assets that failed to load — usually a CDN the network blocked.</summary>
    public IReadOnlyList<string> ResourceFailures => _resourceFailures;

    public IReadOnlyList<string> ConsoleErrors => _consoleErrors;

    public bool HasAnyProblem => ErrorCount > 0 || ResourceFailureCount > 0 || ConsoleErrorCount > 0;

    /// <summary>True only when the page finished loading and reported nothing wrong.</summary>
    public bool IsClean => Loaded && !HasAnyProblem;

    // A page stuck in a failing animation frame can emit the same error thousands of times.
    // Counts stay exact; only the retained sample is bounded.
    public void AddError(string message)
    {
        ErrorCount++;
        Retain(_errors, message);
    }

    public void AddResourceFailure(string message)
    {
        ResourceFailureCount++;
        Retain(_resourceFailures, message);
    }

    public void AddConsoleError(string message)
    {
        ConsoleErrorCount++;
        Retain(_consoleErrors, message);
    }

    private static void Retain(List<string> sink, string message)
    {
        if (sink.Count >= MaxRetainedMessages) return;
        if (sink.Contains(message, StringComparer.Ordinal)) return;
        sink.Add(message);
    }
}
