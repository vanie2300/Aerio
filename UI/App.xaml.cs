using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace TabloidBrowser;

public partial class App : Application
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabloidBrowser",
        "Logs");

    private static readonly string CrashLogPath = Path.Combine(LogDirectory, "crash.log");

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Win32Exception { NativeErrorCode: 1400 } ex && ex.StackTrace?.Contains("HwndWrapper") == true)
            {
                return;
            }

            WriteCrashLog(args.ExceptionObject as Exception);

            try
            {
                Environment.FailFast(
                    $"Fatal unhandled exception. See crash log at: {CrashLogPath}");
            }
            catch
            {
                Environment.Exit(1);
            }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Exception is Win32Exception { NativeErrorCode: 1400 } && args.Exception.StackTrace?.Contains("HwndWrapper") == true)
            {
                args.Handled = true;
                return;
            }

            WriteCrashLog(args.Exception);
            Debug.WriteLine($"[DispatcherUnhandledException] {args.Exception}");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            Debug.WriteLine($"[UnobservedTaskException] {args.Exception}");
            args.SetObserved();
        };
    }

    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }

            using var stream = new FileStream(CrashLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);

            writer.WriteLine($"--- {DateTimeOffset.Now:O} ---");
            writer.WriteLine($"Exception: {exception?.GetType().FullName}");
            writer.WriteLine($"Message: {exception?.Message}");
            writer.WriteLine($"Stack Trace:");
            writer.WriteLine(exception?.StackTrace);

            if (exception?.InnerException is { } inner)
            {
                writer.WriteLine($"--- Inner Exception ---");
                writer.WriteLine($"Type: {inner.GetType().FullName}");
                writer.WriteLine($"Message: {inner.Message}");
                writer.WriteLine(inner.StackTrace);
            }

            writer.WriteLine();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write crash log: {ex}");
        }
    }
}
