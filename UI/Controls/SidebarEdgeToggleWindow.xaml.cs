using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TabloidBrowser;

public partial class SidebarEdgeToggleWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmMouseLeave = 0x02A3;
    private const uint TrackMouseLeave = 0x00000002;
    private static readonly IntPtr HtClient = new(1);
    private static readonly IntPtr MaNoActivate = new(3);
    private bool _isTrackingMouseLeave;
    private bool _isHovered;

    public SidebarEdgeToggleWindow() => InitializeComponent();

    public event RoutedEventHandler? ToggleRequested;

    private void SetHovered(bool isHovered)
    {
        if (_isHovered == isHovered)
        {
            return;
        }

        _isHovered = isHovered;
        if (isHovered)
        {
            RailEdgeIndicator.Width = 5;
            RailEdgeIndicator.Opacity = 1;
            RailEdgeIndicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Color.FromRgb(0xA9, 0x8A, 0xFF),
                BlurRadius = 16,
                ShadowDepth = 0,
                Opacity = 0.9
            };
            return;
        }

        RailEdgeIndicator.Width = 2;
        RailEdgeIndicator.Opacity = 0.9;
        RailEdgeIndicator.Effect = null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
        }
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) =>
        ToggleRequested?.Invoke(sender, e);

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmMouseMove)
        {
            if (!_isTrackingMouseLeave)
            {
                var tracking = new TrackMouseEventData
                {
                    Size = (uint)Marshal.SizeOf<TrackMouseEventData>(),
                    Flags = TrackMouseLeave,
                    WindowHandle = hwnd
                };
                _isTrackingMouseLeave = TrackMouseEvent(ref tracking);
            }

            SetHovered(true);
            return IntPtr.Zero;
        }

        if (message == WmMouseLeave)
        {
            _isTrackingMouseLeave = false;
            SetHovered(false);
            return IntPtr.Zero;
        }

        if (message == WmLeftButtonUp)
        {
            ToggleRequested?.Invoke(this, new RoutedEventArgs());
            handled = true;
            return IntPtr.Zero;
        }

        if (message == WmNcHitTest)
        {
            handled = true;
            return HtClient;
        }

        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public IntPtr WindowHandle;
        public uint HoverTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData eventTrack);
}
