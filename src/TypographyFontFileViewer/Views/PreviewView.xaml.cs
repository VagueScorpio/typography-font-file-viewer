using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TypographyFontFileViewer.ViewModels;

namespace TypographyFontFileViewer.Views;

public partial class PreviewView : UserControl
{
    public PreviewView()
    {
        InitializeComponent();

        // The first size change usually arrives before the view model is bound, so the
        // width has to be published again once there is something to publish it to.
        Loaded += (_, _) => PublishAvailableWidth();
        DataContextChanged += (_, _) => PublishAvailableWidth();
    }

    private PreviewViewModel? Model => DataContext as PreviewViewModel;

    private void OnPreviewAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) PublishAvailableWidth();
    }

    /// <summary>Tells the view model how much room it has, so text wraps to the panel.</summary>
    private void PublishAvailableWidth()
    {
        if (Model is null || PreviewScroller.ActualWidth <= 0) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        if (dpi <= 0) dpi = 96;

        double usable = PreviewScroller.ActualWidth
                      - PreviewScroller.Padding.Left - PreviewScroller.Padding.Right
                      - SystemParameters.VerticalScrollBarWidth;

        Model.AvailableWidthPx = (int)Math.Max(80, usable * dpi / 96.0);
    }

    /// <summary>Ctrl and the wheel resizes the preview, as in every other viewer.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Model is null || Keyboard.Modifiers != ModifierKeys.Control) return;

        Model.AdjustSize(e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is null || PresetBox.SelectedItem is not PreviewPreset preset) return;

        Model.ApplyPreset(preset);

        // "Custom" means the user's own text, so put the caret where they can type it.
        if (preset.Text is null) PreviewTextBox.Focus();
    }

    /// <summary>Commits a typed size on Enter rather than waiting for focus to leave.</summary>
    private void OnSizeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;

        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
