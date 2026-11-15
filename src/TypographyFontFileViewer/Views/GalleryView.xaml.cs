using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TypographyFontFileViewer.ViewModels;

namespace TypographyFontFileViewer.Views;

public partial class GalleryView : UserControl
{
    /// <summary>Raised when a row is double-clicked, so the window can show the Preview tab.</summary>
    public event EventHandler? RowActivated;

    public GalleryView()
    {
        InitializeComponent();

        // Switching to this tab should leave the keyboard pointed at the list, so Page
        // Down and the arrow keys scroll it without a click first.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) _ = Dispatcher.BeginInvoke(() => GalleryList.Focus());
        };
    }

    /// <summary>Ctrl and the wheel resizes the rows, as it does in the Preview tab.</summary>
    private void OnGalleryMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (DataContext is not MainViewModel model) return;

        model.Gallery.AdjustSize(e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GalleryList.SelectedItem is not null)
            GalleryList.ScrollIntoView(GalleryList.SelectedItem);
    }

    /// <summary>Tells the view model how wide a row can show, so it renders no more.</summary>
    private void OnGallerySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || DataContext is not MainViewModel model) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        if (dpi <= 0) dpi = 96;

        double usable = GalleryList.ActualWidth
                      - SystemParameters.VerticalScrollBarWidth
                      - 28;   // the row's own margins

        model.Gallery.RowWidthPx = (int)Math.Max(200, usable * dpi / 96.0);
    }

    /// <summary>
    /// Double-clicking a font takes you to the Preview tab, where it is large.
    /// </summary>
    /// <remarks>
    /// Only when a row was actually hit: double-clicking the empty space below the last
    /// row, or the scrollbar, should do nothing.
    /// </remarks>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (GalleryList.ContainerFromElement(source) is not ListBoxItem) return;

        RowActivated?.Invoke(this, EventArgs.Empty);
    }
}
