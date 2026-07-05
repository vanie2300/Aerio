using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace TabloidBrowser;

public partial class App : Application
{
    private readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabloidBrowser",
        "Logs",
        "startup.log");

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
            try { MessageBox.Show($"Tabloid Browser encountered a fatal error.\n\nLog file:\n{_logPath}", "Tabloid Browser", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            Environment.Exit(1);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Exception is Win32Exception { NativeErrorCode: 1400 } && args.Exception.StackTrace?.Contains("HwndWrapper") == true)
            {
                args.Handled = true;
                return;
            }
            WriteCrashLog(args.Exception);
            Debug.WriteLine(args.Exception);
            args.Handled = true;
        };
    }

    private void WriteCrashLog(Exception? exception)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(
                _logPath,
                $"Timestamp: {DateTimeOffset.Now:O}{Environment.NewLine}{exception}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write crash log: {ex}");
        }
    }
}
