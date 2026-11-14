namespace VocalB1.Core.Diagnostics;

/// <summary>
/// Structured failure carrying the class and the pipeline stage that failed. Errors are never swallowed:
/// every boundary (serialization, parameter evaluation, render planning, bridge, UI command) wraps and
/// rethrows with this type so the log and the dialog can show "Class @ Stage".
/// </summary>
public sealed class StageException : Exception
{
    public string ClassName { get; }

    public string Stage { get; }

    public StageException(string className, string stage, string message, Exception? inner = null)
        : base($"[{className} @ {stage}] {message}", inner)
    {
        ClassName = className;
        Stage = stage;
    }
}

/// <summary>Helper that converts incidental exceptions into <see cref="StageException"/> with full context.</summary>
public static class StageGuard
{
    public static T Run<T>(string className, string stage, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (StageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StageException(className, stage, ex.Message, ex);
        }
    }

    public static void Run(string className, string stage, Action action)
    {
        try
        {
            action();
        }
        catch (StageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StageException(className, stage, ex.Message, ex);
        }
    }
}
