using System.Windows;
using System.Windows.Controls;

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
    private void OnGalleryMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
        if (DataContext is not ViewModels.MainViewModel model) return;

        model.Gallery.AdjustSize(e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GalleryList.SelectedItem is not null)
            GalleryList.ScrollIntoView(GalleryList.SelectedItem);
    }

    /// <summary>
    /// Double-clicking a font takes you to the Preview tab, where it is large.
    /// </summary>
    /// <remarks>
    /// Only when a row was actually hit: double-clicking the empty space below the last
    /// row, or the scrollbar, should do nothing.
    /// </remarks>
    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindRow(source) is null) return;

        RowActivated?.Invoke(this, EventArgs.Empty);
    }

    private static ListBoxItem? FindRow(DependencyObject source)
    {
        while (source is not null and not ListBoxItem)
        {
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);

            if (source is null) return null;
        }

        return source as ListBoxItem;
    }
}
