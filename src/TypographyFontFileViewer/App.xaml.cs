using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace TypographyFontFileViewer;

public partial class App : Application
{
    /// <summary>
    /// Paths handed to the app on the command line: a font file or a folder.
    /// </summary>
    public static string? StartupPath { get; private set; }

    /// <summary>Where unhandled errors are written, beside the exe like everything else.</summary>
    public static string LogPath { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "TypographyFontFileViewer.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
            StartupPath = e.Args[0];

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    /// <summary>
    /// Catches what reaches the UI thread: writes it down, says so, and carries on.
    /// </summary>
    /// <remarks>
    /// A font viewer meets malformed files by design. The engine turns those into error
    /// cards, so anything arriving here is a real bug — worth recording properly rather
    /// than losing to a vanished window, and worth surviving if it possibly can be.
    /// </remarks>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string? written = Write("UI thread", e.Exception);

        MessageBoxResult answer = MessageBox.Show(
            BuildMessage(e.Exception, written, canContinue: true),
            "Typography Font File Viewer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error,
            MessageBoxResult.Yes);

        // Staying open is usually right: one failed render should not cost the user the
        // folder they had open.
        e.Handled = answer == MessageBoxResult.Yes;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) Write("background thread", exception);
    }

    /// <summary>
    /// A faulted task nobody awaited. Recorded, never shown.
    /// </summary>
    /// <remarks>
    /// Renders abandoned by fast scrolling land here routinely; a dialog for each would
    /// be unusable. Marking them observed stops the runtime treating them as fatal.
    /// </remarks>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Write("unobserved task", e.Exception);
        e.SetObserved();
    }

    private static string BuildMessage(Exception exception, string? logPath, bool canContinue)
    {
        var text = new StringBuilder();
        text.AppendLine("Something went wrong.");
        text.AppendLine();
        text.AppendLine($"{exception.GetType().Name}: {exception.Message}");

        if (logPath is not null)
        {
            text.AppendLine();
            text.AppendLine($"The details were written to:\n{logPath}");
        }

        if (canContinue)
        {
            text.AppendLine();
            text.AppendLine("Try to keep going?");
        }

        return text.ToString();
    }

    /// <summary>
    /// Appends an error to the log beside the exe.
    /// </summary>
    /// <returns>The log's path, or null if it could not be written.</returns>
    private static string? Write(string origin, Exception exception)
    {
        try
        {
            var entry = new StringBuilder();
            entry.AppendLine(new string('-', 72));
            entry.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  ({origin})");
            entry.AppendLine($"Version 1.0, {Environment.OSVersion}, .NET {Environment.Version}");
            entry.AppendLine();
            entry.AppendLine(exception.ToString());
            entry.AppendLine();

            File.AppendAllText(LogPath, entry.ToString());
            return LogPath;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            // The app may be somewhere unwritable, which is exactly the situation the
            // favourites folder already warns about. Nothing more to do here.
            return null;
        }
    }
}
