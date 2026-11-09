using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Engine;

/// <summary>
/// One unit of work for the engine thread. Every request is cancellable, because fast
/// scrolling must be able to throw away work it no longer needs.
/// </summary>
internal abstract class EngineJob
{
    public required JobPriority Priority { get; init; }

    public required CancellationToken CancellationToken { get; init; }

    /// <summary>Runs on the engine thread. May touch native objects; nothing else may.</summary>
    public abstract void Execute(FontEngineContext context);

    /// <summary>Completes the waiting task as cancelled.</summary>
    public abstract void Cancel();

    /// <summary>Completes the waiting task with a failure.</summary>
    public abstract void Fail(Exception exception);
}

/// <summary>A job that produces a value.</summary>
internal sealed class EngineJob<TResult> : EngineJob
{
    private readonly Func<FontEngineContext, TResult> _work;

    private readonly TaskCompletionSource<TResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EngineJob(Func<FontEngineContext, TResult> work) => _work = work;

    /// <summary>Completes when the engine has run this job, failed it, or dropped it.</summary>
    public Task<TResult> Task => _completion.Task;

    public override void Execute(FontEngineContext context)
    {
        try
        {
            CancellationToken.ThrowIfCancellationRequested();
            TResult result = _work(context);
            _completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            _completion.TrySetCanceled(CancellationToken);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    public override void Cancel() => _completion.TrySetCanceled(CancellationToken);

    public override void Fail(Exception exception) => _completion.TrySetException(exception);
}
