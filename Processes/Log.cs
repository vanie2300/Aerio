using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TabloidBrowser;

public static class Log
{
    private static Logger? _logger;

    public static void Init(string logDirectory)
    {
        if (!Directory.Exists(logDirectory))
            Directory.CreateDirectory(logDirectory);

        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDirectory, "tabloid-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                shared: true)
            .CreateLogger();
    }

    public static void Info(string message) => _logger?.Information(message);
    public static void Info(string message, params object[] args) => _logger?.Information(message, args);
    public static void Debug(string message) => _logger?.Debug(message);
    public static void Debug(string message, params object[] args) => _logger?.Debug(message, args);
    public static void Debug(Exception ex, string message, params object[] args) => _logger?.Debug(ex, message, args);
    public static void Warn(string message) => _logger?.Warning(message);
    public static void Warn(string message, params object[] args) => _logger?.Warning(message, args);
    public static void Error(string message, Exception? ex = null)
    {
        if (ex != null)
            _logger?.Error(ex, message);
        else
            _logger?.Error(message);
    }
    public static void Error(Exception ex, string message, params object[] args) => _logger?.Error(ex, message, args);
    public static void Fatal(string message, Exception? ex = null)
    {
        if (ex != null)
            _logger?.Fatal(ex, message);
        else
            _logger?.Fatal(message);
    }

    public static void CloseAndFlush() => _logger?.Dispose();
}
