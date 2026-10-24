using VocalB1.Core.Models;

namespace VocalB1.Core.Abstractions;

public enum EngineEventKind
{
    Log,
    Progress,
    Warning,
}

/// <summary>An event streamed back from the engine during rendering (log / progress).</summary>
public sealed record EngineEvent(EngineEventKind Kind, string Message, double? Progress = null)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

public sealed record RenderRequest(ProjectDocument Project, TrackDocument Track, string OutputPath);

public sealed record RenderResult(string OutputPath, TimeSpan Elapsed)
{
    /// <summary>Output duration in seconds, parsed from the WAV header.</summary>
    public double DurationSeconds { get; init; }
}

/// <summary>Thrown when the engine is unavailable or rendering fails; the message is user-readable.</summary>
public sealed class EngineException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// C# facade over the Python synthesis engine. Implementations must:
/// never modify existing .py files, never install/modify venv dependencies, and communicate only through the fixed bridge script.
/// </summary>
public interface IRenderEngine : IAsyncDisposable
{
    bool IsReady { get; }

    /// <summary>Starts the interpreter and completes the handshake; throws <see cref="EngineException"/> on failure.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a project. Implementations must forward synthesis logs to <paramref name="progress"/> in real time;
    /// cancellation kills the underlying process (the synthesis function itself cannot be interrupted) and then restarts it for the next call.
    /// </summary>
    Task<RenderResult> RenderAsync(
        RenderRequest request,
        IProgress<EngineEvent>? progress = null,
        CancellationToken cancellationToken = default);
}
