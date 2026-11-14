using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using TypographyFontFileViewer.ViewModels;

namespace TypographyFontFileViewer.Views;

public partial class InfoView : UserControl
{
    public InfoView() => InitializeComponent();

    /// <summary>
    /// Opens a font's vendor, designer or licence URL in the user's browser.
    /// </summary>
    /// <remarks>
    /// Only http and https are followed. A font file is data from elsewhere, and its
    /// name table can hold any string at all; handing an arbitrary scheme to the shell
    /// would let a font decide what runs.
    /// </remarks>
    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        if (!Uri.TryCreate(e.Uri?.ToString(), UriKind.Absolute, out Uri? uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            if (DataContext is InfoViewModel model)
                model.Status = $"Could not open the link: {exception.Message}";
        }
    }
}
