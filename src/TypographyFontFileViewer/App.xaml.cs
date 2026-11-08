using System.Windows;
using System.Windows.Threading;

namespace TypographyFontFileViewer;

public partial class App : Application
{
    /// <summary>
    /// Paths handed to the app on the command line: a font file or a folder.
    /// Phase 3 acts on this; Phase 0 only records it.
    /// </summary>
    public static string? StartupPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
            StartupPath = e.Args[0];

        // A font viewer meets malformed files by design, so an unhandled exception
        // should explain itself rather than vanish. Phase 7 adds the log file next
        // to the exe; for now the user at least gets a readable message.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Something went wrong and the app has to stop.\n\n{e.Exception.GetType().Name}: {e.Exception.Message}",
            "Typography Font File Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
