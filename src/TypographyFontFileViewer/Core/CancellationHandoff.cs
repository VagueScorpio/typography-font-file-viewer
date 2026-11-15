namespace TypographyFontFileViewer.Core;

/// <summary>
/// Hands a cancellation token source over from a superseded request to a newer one.
/// </summary>
/// <remarks>
/// The old source is cancelled but deliberately not disposed. Another thread may still be
/// holding it, and disposing one out from under an in-flight request is how a superseded
/// render turns into an <see cref="ObjectDisposedException"/>. A token source has nothing
/// unmanaged to release, so letting the collector have it is enough.
/// </remarks>
public static class CancellationHandoff
{
    /// <summary>Cancels whatever was pending and puts a fresh source in its place.</summary>
    public static CancellationTokenSource Replace(ref CancellationTokenSource? pending)
    {
        var request = new CancellationTokenSource();
        Interlocked.Exchange(ref pending, request)?.Cancel();
        return request;
    }

    /// <summary>Cancels whatever was pending without starting anything new.</summary>
    public static void Cancel(ref CancellationTokenSource? pending)
        => Interlocked.Exchange(ref pending, null)?.Cancel();
}
