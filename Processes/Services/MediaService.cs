using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace TabloidBrowser.Services;

public class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly Dispatcher _dispatcher;
    private bool _initialized;

    public string TrackTitle { get; private set; } = "";
    public string TrackArtist { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public bool HasMedia => _session != null;

    public event Action? MediaChanged;
    public event Action? PlaybackChanged;

    public MediaService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            OnCurrentSessionChanged(_manager, null);
            _initialized = true;
        }
        catch
        {
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, object? args)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        _session = sender.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            RefreshPlaybackInfo();
            _ = RefreshAndNotifyAsync();
        }
        else
        {
            _ = _dispatcher.BeginInvoke(() => MediaChanged?.Invoke());
        }
    }

    private async Task RefreshAndNotifyAsync()
    {
        await RefreshMediaPropertiesAsync();
        _ = _dispatcher.BeginInvoke(() => MediaChanged?.Invoke());
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshAndNotifyAsync();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        RefreshPlaybackInfo();
    }

    private async Task RefreshMediaPropertiesAsync()
    {
        if (_session == null) return;
        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            _ = _dispatcher.BeginInvoke(() =>
            {
                TrackTitle = props?.Title ?? "";
                TrackArtist = props?.Artist ?? "";
            });
        }
        catch
        {
        }
    }

    private void RefreshPlaybackInfo()
    {
        if (_session == null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            var status = info.PlaybackStatus;
            _ = _dispatcher.BeginInvoke(() =>
            {
                IsPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlaybackChanged?.Invoke();
            });
        }
        catch
        {
        }
    }

    public async Task<BitmapImage?> GetThumbnailAsync()
    {
        if (_session == null) return null;
        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            if (props?.Thumbnail == null) return null;
            using var stream = await props.Thumbnail.OpenReadAsync();
            using var memStream = new MemoryStream();
            await stream.AsStream().CopyToAsync(memStream);
            memStream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = memStream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public async Task PlayPauseAsync()
    {
        if (_session == null) return;
        try
        {
            if (IsPlaying)
                await _session.TryPauseAsync();
            else
                await _session.TryPlayAsync();
        }
        catch { }
    }

    public async Task NextAsync()
    {
        if (_session == null) return;
        try { await _session.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        if (_session == null) return;
        try { await _session.TrySkipPreviousAsync(); } catch { }
    }
}
