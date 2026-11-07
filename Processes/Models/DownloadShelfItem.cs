using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TabloidBrowser;

/// <summary>Lifecycle state of a download shown in the downloads shelf.</summary>
public enum DownloadShelfState
{
    Running,
    Completed,
    Interrupted
}

/// <summary>
/// Live, UI-free state for one download in the downloads shelf. Only depends on
/// <see cref="INotifyPropertyChanged"/> so it can be unit tested away from WPF.
/// </summary>
public sealed class DownloadShelfItem : INotifyPropertyChanged
{
    private long _bytesReceived;
    private long? _totalBytes;
    private DownloadShelfState _state = DownloadShelfState.Running;
    private string? _filePath;

    public DownloadShelfItem(string fileName, string sourceUrl, long? totalBytes, string? filePath = null)
    {
        FileName = string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
        SourceUrl = sourceUrl ?? string.Empty;
        _totalBytes = totalBytes is > 0 ? totalBytes : null;
        _filePath = filePath;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string FileName { get; }

    public string SourceUrl { get; }

    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (string.Equals(_filePath, value, StringComparison.Ordinal)) return;
            _filePath = value;
            Raise();
        }
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        private set
        {
            if (_bytesReceived == value) return;
            _bytesReceived = value;
            Raise();
            Raise(nameof(ProgressPercent));
            Raise(nameof(SizeText));
        }
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        private set
        {
            if (_totalBytes == value) return;
            _totalBytes = value;
            Raise();
            Raise(nameof(ProgressPercent));
            Raise(nameof(SizeText));
            Raise(nameof(IsIndeterminate));
        }
    }

    public DownloadShelfState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Raise();
            Raise(nameof(IsRunning));
            Raise(nameof(StateText));
        }
    }

    public bool IsRunning => _state == DownloadShelfState.Running;

    /// <summary>No known total size, so the progress bar animates.</summary>
    public bool IsIndeterminate => _totalBytes is null or <= 0;

    public double ProgressPercent
    {
        get
        {
            if (_totalBytes is not > 0)
            {
                return 0;
            }

            return Math.Clamp(_bytesReceived * 100.0 / _totalBytes.Value, 0, 100);
        }
    }

    public string StateText => _state switch
    {
        DownloadShelfState.Completed => "Completed",
        DownloadShelfState.Interrupted => "Interrupted",
        _ => "Downloading\u2026"
    };

    /// <summary>e.g. "1.4 MB of 6.0 MB" or "1.4 MB" when the total is unknown.</summary>
    public string SizeText
    {
        get
        {
            var received = FormatBytes(_bytesReceived);
            return _totalBytes is > 0
                ? $"{received} of {FormatBytes(_totalBytes.Value)}"
                : received;
        }
    }

    public void UpdateProgress(long bytesReceived, long? totalBytes)
    {
        BytesReceived = Math.Max(0, bytesReceived);
        TotalBytes = totalBytes is > 0 ? totalBytes : null;
    }

    public void Complete(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            FilePath = filePath;
        }

        if (_totalBytes is > 0)
        {
            BytesReceived = _totalBytes.Value;
        }

        State = DownloadShelfState.Completed;
    }

    public void Interrupt() => State = DownloadShelfState.Interrupted;

    /// <summary>Formats a byte count with one decimal for the shelf UI.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        const double Kb = 1024;
        const double Mb = Kb * 1024;
        const double Gb = Mb * 1024;

        return bytes switch
        {
            < (long)Kb => $"{bytes} B",
            < (long)Mb => $"{bytes / Kb:0.0} KB",
            < (long)Gb => $"{bytes / Mb:0.0} MB",
            _ => $"{bytes / Gb:0.0} GB"
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
