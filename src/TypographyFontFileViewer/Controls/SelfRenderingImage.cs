using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Controls;

/// <summary>
/// An image that fetches its own picture and gives up when it is no longer on screen.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the gallery and the glyph map affordable. Both lists virtualize and
/// recycle their containers, so only what is actually visible exists; each control asks
/// for its bitmap when it is handed something to draw and cancels that request the moment
/// it is handed something else. Scrolling quickly through two thousand fonts therefore
/// leaves a couple of dozen live requests behind it, not two thousand.
/// </para>
/// <para>
/// A recycled container can receive a result meant for what it used to show, so every
/// result is checked against what the control is displaying now before it is painted.
/// </para>
/// </remarks>
public abstract class SelfRenderingImage : Image
{
    private CancellationTokenSource? _pending;

    protected SelfRenderingImage(HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        Stretch = Stretch.None;
        HorizontalAlignment = horizontal;
        VerticalAlignment = vertical;

        Loaded += (_, _) => QueueRender();
        Unloaded += (_, _) => CancellationHandoff.Cancel(ref _pending);
    }

    /// <summary>Dependency-property callback: any input changing means a redraw.</summary>
    protected static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SelfRenderingImage)d).QueueRender();

    /// <summary>
    /// Works out what to draw now, and calls <see cref="Start"/> or <see cref="Clear"/>.
    /// </summary>
    protected abstract void QueueRender();

    /// <summary>There is nothing to draw: stop whatever was in flight and go blank.</summary>
    protected void Clear()
    {
        CancellationHandoff.Cancel(ref _pending);
        Source = null;
    }

    /// <summary>
    /// Starts a render, superseding any still in flight.
    /// </summary>
    /// <param name="render">Produces the bitmap, or null when there is nothing to show.</param>
    /// <param name="stillWanted">
    /// Whether the control is still showing the thing this render was started for.
    /// </param>
    protected void Start(
        Func<CancellationToken, Task<BitmapSource?>> render, Func<bool> stillWanted)
    {
        CancellationTokenSource request = CancellationHandoff.Replace(ref _pending);
        _ = RunAsync(render, stillWanted, request.Token);
    }

    private async Task RunAsync(
        Func<CancellationToken, Task<BitmapSource?>> render,
        Func<bool> stillWanted,
        CancellationToken cancellationToken)
    {
        try
        {
            BitmapSource? bitmap = await render(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            if (stillWanted()) Source = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Recycled or superseded; the newer request owns this control now.
        }
        catch (Exception exception) when (exception is FontLoadException or ObjectDisposedException)
        {
            // Something that will not render shows as blank. The list beside it already
            // marks the file with a warning and the reason.
            if (stillWanted()) Source = null;
        }
    }
}
