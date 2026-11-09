using System.Windows.Controls;

namespace TypographyFontFileViewer.Views;

public partial class FontListView : UserControl
{
    public FontListView() => InitializeComponent();

    /// <summary>
    /// Keeps the selected row on screen.
    /// </summary>
    /// <remarks>
    /// Ctrl+Down moves the selection from anywhere in the window, including from inside
    /// the preview text box, so the list has to follow along by itself.
    /// </remarks>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontList.SelectedItem is not null)
            FontList.ScrollIntoView(FontList.SelectedItem);
    }
}
