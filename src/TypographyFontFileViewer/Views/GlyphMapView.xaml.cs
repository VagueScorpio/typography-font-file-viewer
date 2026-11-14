using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.ViewModels;

namespace TypographyFontFileViewer.Views;

public partial class GlyphMapView : UserControl
{
    public GlyphMapView()
    {
        InitializeComponent();

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) _ = Dispatcher.BeginInvoke(() => RowList.Focus());
        };
    }

    private GlyphMapViewModel? Model => DataContext as GlyphMapViewModel;

    /// <summary>Tells the view model how many cells fit across, so it can re-slice the rows.</summary>
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Model is null || !e.WidthChanged) return;

        double usable = RowList.ActualWidth - SystemParameters.VerticalScrollBarWidth - 8;
        Model.Columns = Math.Max(1, (int)(usable / Model.CellPitch));
    }

    /// <summary>Ctrl and the wheel resizes the cells, as it does everywhere else.</summary>
    private void OnGridMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Model is null || Keyboard.Modifiers != ModifierKeys.Control) return;

        Model.AdjustCellSize(e.Delta > 0 ? 8 : -8);
        e.Handled = true;
    }

    /// <summary>
    /// A click copies the character; a double-click opens the detail overlay.
    /// </summary>
    /// <remarks>
    /// WPF counts the clicks against the user's own double-click setting, so there is no
    /// timing to track here. The first click of a double-click has already copied by the
    /// time the second arrives, which is harmless — copying is what the tooltip promises
    /// a click will do.
    /// </remarks>
    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is null || sender is not FrameworkElement { DataContext: GlyphEntry glyph }) return;

        if (e.ClickCount >= 2)
        {
            _ = Model.OpenDetailCommand.ExecuteAsync(glyph);
            return;
        }

        Model.CopyCharacterCommand.Execute(glyph);
    }

    /// <summary>Right-click goes straight to the details, without copying.</summary>
    private void OnCellRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is null || sender is not FrameworkElement { DataContext: GlyphEntry glyph }) return;

        _ = Model.OpenDetailCommand.ExecuteAsync(glyph);
    }

    private void OnOverlayBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        // Only a click on the dimmed background closes it, not one on the panel itself.
        if (!ReferenceEquals(e.OriginalSource, sender)) return;

        Model?.CloseDetailCommand.Execute(null);
    }

    private void OnJumpKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        Jump();
        e.Handled = true;
    }

    private void OnJumpClick(object sender, RoutedEventArgs e) => Jump();

    private void Jump()
    {
        if (Model is null) return;

        int row = Model.ResolveJumpRow();
        if (row < 0 || row >= Model.Rows.Count) return;

        RowList.ScrollIntoView(Model.Rows[row]);
    }
}
