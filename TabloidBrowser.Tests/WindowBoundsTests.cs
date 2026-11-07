using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using Xunit;

namespace TabloidBrowser.Tests;

public class WindowBoundsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorderlessMaximize_LeavesTaskbarTriggerExposed_AndRestoresNormalBounds(bool useSystemCommand)
    {
        RunOnSta(() =>
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.CanResize,
                Width = 1000,
                Height = 600,
                MinWidth = 940,
                MinHeight = 540,
                ShowActivated = false,
                ShowInTaskbar = false,
                Opacity = 0
            };
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(10),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            HwndSource.FromHwnd(hwnd)!.AddHook(MaximizedBoundsHook);
            try
            {
                Assert.True(GetWindowRect(hwnd, out var normalBounds));
                window.Show();

                // Include repeated maximize/restore and a normal window at
                // monitor size, as used during the F11 fullscreen transition.
                for (var pass = 0; pass < 2; pass++)
                {
                    if (useSystemCommand)
                    {
                        SendMessage(hwnd, 0x112, new IntPtr(0xF030), IntPtr.Zero);
                    }
                    else
                    {
                        window.WindowState = WindowState.Maximized;
                    }
                    Assert.True(IsZoomed(hwnd));
                    Assert.True(GetWindowRect(hwnd, out var bounds));
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    Assert.True(GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info));
                    Assert.InRange(bounds.Left, info.Work.Left, info.Work.Right - 1);
                    Assert.InRange(bounds.Top, info.Work.Top, info.Work.Bottom - 1);
                    Assert.InRange(bounds.Right, bounds.Left + 1, info.Work.Right);
                    Assert.InRange(bounds.Bottom, bounds.Top + 1, info.Work.Bottom);
                    for (var edge = 0; edge < 4; edge++)
                    {
                        var data = new AppBarData
                        {
                            Size = Marshal.SizeOf<AppBarData>(),
                            Edge = edge,
                            Rect = info.Monitor
                        };
                        if (SHAppBarMessage(11, ref data) == IntPtr.Zero) continue;
                        Assert.True(edge switch
                        {
                            0 => bounds.Left >= info.Monitor.Left + 2,
                            1 => bounds.Top >= info.Monitor.Top + 2,
                            2 => bounds.Right <= info.Monitor.Right - 2,
                            _ => bounds.Bottom <= info.Monitor.Bottom - 2
                        }, $"The maximized HWND covers the auto-hide trigger on edge {edge}.");
                    }

                    window.WindowState = WindowState.Normal;
                    Assert.True(GetWindowRect(hwnd, out var restored));
                    Assert.Equal(normalBounds, restored);

                    Assert.True(SetWindowPos(hwnd, IntPtr.Zero,
                        info.Monitor.Left, info.Monitor.Top,
                        info.Monitor.Right - info.Monitor.Left,
                        info.Monitor.Bottom - info.Monitor.Top, 0x14));
                    Assert.True(GetWindowRect(hwnd, out var fullScreen));
                    Assert.Equal(info.Monitor, fullScreen);
                    Assert.True(SetWindowPos(hwnd, IntPtr.Zero,
                        normalBounds.Left, normalBounds.Top,
                        normalBounds.Right - normalBounds.Left,
                        normalBounds.Bottom - normalBounds.Top, 0x14));
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MaximizedBounds_PreservesMinimumAndMaximumTrackingSizes()
    {
        var data = Enumerable.Range(0, 10).Select(i => 100 + i).ToArray();
        var buffer = Marshal.AllocHGlobal(data.Length * sizeof(int));
        try
        {
            Marshal.Copy(data, 0, buffer, data.Length);
            MainWindow.UpdateMaximizedBounds(IntPtr.Zero, buffer);
            var actual = new int[data.Length];
            Marshal.Copy(buffer, actual, 0, actual.Length);
            Assert.Equal(data[..2], actual[..2]);
            Assert.Equal(data[6..], actual[6..]);
            Assert.True(actual[2] > 0 && actual[3] > 0);
            Assert.NotEqual(data[2..6], actual[2..6]);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr MaximizedBoundsHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x24) MainWindow.UpdateMaximizedBounds(hwnd, lParam);
        return IntPtr.Zero;
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Window bounds test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public PixelRect Monitor, Work; public int Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public IntPtr Window;
        public int Callback, Edge;
        public PixelRect Rect;
        public IntPtr Param;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out PixelRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(int message, ref AppBarData data);
}
