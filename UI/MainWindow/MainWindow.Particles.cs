using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TabloidBrowser;

public partial class MainWindow
{
    private static readonly Color AccentBase = Color.FromArgb(180, 114, 77, 223);
    private static readonly Color AccentLight = Color.FromArgb(100, 150, 120, 255);
    private static readonly Color AccentGlow = Color.FromArgb(40, 114, 77, 223);
    private static readonly Color WhiteSoft = Color.FromArgb(120, 255, 255, 255);
    private static readonly Color WhiteDim = Color.FromArgb(30, 255, 255, 255);

    private sealed class BgParticle
    {
        public double X, Y, SpeedX, SpeedY;
        public readonly double Size;
        public readonly Ellipse Visual;
        public readonly Ellipse GlowVisual;
        public double BaseOpacity;
        public double OpacityPhase;

        public BgParticle(double x, double y, double sx, double sy, double size, Color color, Color glowColor, Random rng)
        {
            X = x; Y = y; SpeedX = sx; SpeedY = sy; Size = size;
            BaseOpacity = 0.4 + rng.NextDouble() * 0.6;
            OpacityPhase = rng.NextDouble() * Math.PI * 2;

            Visual = new Ellipse
            {
                Width = size * 2,
                Height = size * 2,
                Fill = new SolidColorBrush(color),
                IsHitTestVisible = false,
                Opacity = BaseOpacity
            };

            GlowVisual = new Ellipse
            {
                Width = size * 6,
                Height = size * 6,
                Fill = new SolidColorBrush(glowColor),
                IsHitTestVisible = false,
                Opacity = 0
            };
        }
    }

    private BgParticle[] _bgParticles = [];
    private List<(int A, int B, Line Line, double BaseOpacity)> _bgConnections = [];
    private List<(int A, int B, Line Line)> _bgGlowConnections = [];
    private DispatcherTimer? _startBgTimer;
    private bool _bgInitialized;
    private double _bgTime;
    private int _bgFrame;

    private void InitBgParticles()
    {
        var w = BrowserPane.StartBgCanvas.ActualWidth;
        var h = BrowserPane.StartBgCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        BrowserPane.StartBgCanvas.Children.Clear();

        var rng = new Random();
        var count = Math.Min(70, Math.Max(30, (int)(w / 6)));
        var particleColor = GetAccentColor();
        var glowColor = Color.FromArgb(25, particleColor.R, particleColor.G, particleColor.B);
        var whiteGlow = Color.FromArgb(15, 255, 255, 255);

        var particles = new BgParticle[count];

        for (int i = 0; i < count; i++)
        {
            var useAccent = rng.NextDouble() > 0.5;
            var color = useAccent
                ? Color.FromArgb((byte)(120 + rng.Next(60)), particleColor.R, particleColor.G, particleColor.B)
                : Color.FromArgb((byte)(80 + rng.Next(80)), 255, 255, 255);
            var glow = useAccent ? glowColor : whiteGlow;

            particles[i] = new BgParticle(
                rng.NextDouble() * w,
                rng.NextDouble() * h,
                (rng.NextDouble() - 0.5) * 0.4,
                (rng.NextDouble() - 0.5) * 0.4,
                rng.NextDouble() * 2.5 + 0.8,
                color,
                glow,
                rng);
        }

        var connections = new List<(int, int, Line, double)>();
        var glowConnections = new List<(int, int, Line)>();
        var connectDist = Math.Min(w, h) * 0.15;

        for (int a = 0; a < count; a++)
        {
            for (int b = a + 1; b < count; b++)
            {
                var baseOpacity = rng.NextDouble() * 0.3 + 0.1;
                var line = new Line
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(20, particleColor.R, particleColor.G, particleColor.B)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                connections.Add((a, b, line, baseOpacity));

                var glowLine = new Line
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(8, particleColor.R, particleColor.G, particleColor.B)),
                    StrokeThickness = 3,
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                glowConnections.Add((a, b, glowLine));
            }
        }

        _bgParticles = particles;
        _bgConnections = connections;
        _bgGlowConnections = glowConnections;
        _bgTime = 0;
        _bgInitialized = true;

        foreach (var (_, _, line) in _bgGlowConnections)
            BrowserPane.StartBgCanvas.Children.Add(line);
        foreach (var (_, _, line, _) in _bgConnections)
            BrowserPane.StartBgCanvas.Children.Add(line);
        foreach (var p in _bgParticles)
        {
            BrowserPane.StartBgCanvas.Children.Add(p.GlowVisual);
            BrowserPane.StartBgCanvas.Children.Add(p.Visual);
        }
    }

    private void AnimateBgParticles()
    {
        try
        {
            if (!_bgInitialized)
            {
                InitBgParticles();
                if (!_bgInitialized) return;
            }

            var w = BrowserPane.StartBgCanvas.ActualWidth;
            var h = BrowserPane.StartBgCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var particles = _bgParticles;
            if (particles.Length == 0) return;

            _bgTime += 0.033;
            var connectDist = Math.Min(w, h) * 0.15;
            var connectDistSq = connectDist * connectDist;

            for (int i = 0; i < particles.Length; i++)
            {
                var p = particles[i];

                double nx = p.X + p.SpeedX;
                double ny = p.Y + p.SpeedY;

                if (nx < -20) { nx = w + 20; }
                else if (nx > w + 20) { nx = -20; }
                if (ny < -20) { ny = h + 20; }
                else if (ny > h + 20) { ny = -20; }

                p.X = nx;
                p.Y = ny;

                var breath = Math.Sin(_bgTime * 1.5 + p.OpacityPhase) * 0.15 + 0.85;
                var opacity = p.BaseOpacity * breath;
                Canvas.SetLeft(p.Visual, nx - p.Size);
                Canvas.SetTop(p.Visual, ny - p.Size);
                p.Visual.Opacity = Math.Clamp(opacity, 0, 1);

                Canvas.SetLeft(p.GlowVisual, nx - p.Size * 3);
                Canvas.SetTop(p.GlowVisual, ny - p.Size * 3);
                p.GlowVisual.Opacity = Math.Clamp(opacity * 0.3, 0, 1);
            }

            var connectionIdx = 0;
            var glowIdx = 0;
            var particleColor = GetAccentColor();
            var updateConnections = (_bgFrame++ & 1) == 0;

            for (int a = 0; a < particles.Length; a++)
            {
                for (int b = a + 1; b < particles.Length; b++)
                {
                    if (!updateConnections)
                    {
                        connectionIdx++;
                        glowIdx++;
                        continue;
                    }

                    double dx = particles[a].X - particles[b].X;
                    double dy = particles[a].Y - particles[b].Y;
                    double distSq = dx * dx + dy * dy;

                    var (_, _, line, baseOp) = _bgConnections[connectionIdx];
                    var (_, _, glowLine) = _bgGlowConnections[glowIdx];

                    if (distSq < connectDistSq)
                    {
                        var dist = Math.Sqrt(distSq);
                        var alpha = (1 - dist / connectDist);
                        var lineAlpha = alpha * alpha * 0.5;

                        line.X1 = particles[a].X;
                        line.Y1 = particles[a].Y;
                        line.X2 = particles[b].X;
                        line.Y2 = particles[b].Y;
                        line.Opacity = lineAlpha * baseOp * 2;
                        line.Visibility = Visibility.Visible;

                        glowLine.X1 = particles[a].X;
                        glowLine.Y1 = particles[a].Y;
                        glowLine.X2 = particles[b].X;
                        glowLine.Y2 = particles[b].Y;
                        glowLine.Opacity = lineAlpha * 0.3;
                        glowLine.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        line.Visibility = Visibility.Collapsed;
                        glowLine.Visibility = Visibility.Collapsed;
                    }

                    connectionIdx++;
                    glowIdx++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bg animation error");
        }
    }

    private void StartBg_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _bgInitialized = false;
    }
}
