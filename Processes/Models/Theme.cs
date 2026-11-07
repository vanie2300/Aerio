using System.Windows;
using System.Windows.Media;

namespace TabloidBrowser;

public sealed record Theme
{
    public required string Name { get; init; }

    public required Color AppBg { get; init; }
    public required Color ChromeBg { get; init; }
    public required Color ChromeAlt { get; init; }
    public required Color InputBg { get; init; }
    public required Color TitleBg { get; init; }
    public required Color ButtonBg { get; init; }
    public required Color ButtonHover { get; init; }
    public required Color ButtonPressed { get; init; }
    public required Color Stroke { get; init; }
    public required Color SubtleStroke { get; init; }
    public required Color Text { get; init; }
    public required Color Muted { get; init; }
    public required Color Lime { get; init; }
    public required Color Pink { get; init; }

    public required Color Accent { get; init; }

    // Settings reference palette
    public required Color SidebarBg { get; init; }
    public required Color SurfaceBg { get; init; }
    public required Color SurfaceRaised { get; init; }
    public required Color FieldBg { get; init; }
    public required Color InkSecondary { get; init; }
    public required Color StrokeStrong { get; init; }
    public required Color Danger { get; init; }
    public required Color Positive { get; init; }

    public required Color TabActive { get; init; }
    public required Color TabInactive { get; init; }
    public required Color TabAudio { get; init; }
    public required Color TabActiveAccent { get; init; }
    public required Color TabInactiveAccent { get; init; }
    public required Color TabSleepingAccent { get; init; }
    public required Color TabAudioAccent { get; init; }

    /// <summary>
    /// Whether playing tabs tint their background with the site's accent
    /// (appearance setting). Carried on the theme so tabs receive it with the
    /// rest of their colours.
    /// </summary>
    public bool TintPlayingTabs { get; init; } = true;

    public void ApplyTo(ResourceDictionary resources)
    {
        resources["AppBg"] = new SolidColorBrush(AppBg);
        resources["ChromeBg"] = new SolidColorBrush(ChromeBg);
        resources["ChromeAlt"] = new SolidColorBrush(ChromeAlt);
        resources["InputBg"] = new SolidColorBrush(InputBg);
        resources["TitleBg"] = new SolidColorBrush(TitleBg);
        resources["ButtonBg"] = new SolidColorBrush(ButtonBg);
        resources["ButtonHover"] = new SolidColorBrush(ButtonHover);
        resources["ButtonPressed"] = new SolidColorBrush(ButtonPressed);
        resources["Stroke"] = new SolidColorBrush(Stroke);
        resources["SubtleStroke"] = new SolidColorBrush(SubtleStroke);
        resources["Text"] = new SolidColorBrush(Text);
        resources["Muted"] = new SolidColorBrush(Muted);
        resources["Lime"] = new SolidColorBrush(Lime);
        resources["Pink"] = new SolidColorBrush(Pink);
        resources["Accent"] = new SolidColorBrush(Accent);
        resources["SidebarBg"] = new SolidColorBrush(SidebarBg);
        resources["SurfaceBg"] = new SolidColorBrush(SurfaceBg);
        resources["SurfaceRaised"] = new SolidColorBrush(SurfaceRaised);
        resources["FieldBg"] = new SolidColorBrush(FieldBg);
        resources["InkSecondary"] = new SolidColorBrush(InkSecondary);
        resources["StrokeStrong"] = new SolidColorBrush(StrokeStrong);
        resources["Danger"] = new SolidColorBrush(Danger);
        resources["Positive"] = new SolidColorBrush(Positive);
        var isDark = Name.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        resources["GlassSurface"] = new SolidColorBrush(
            Color.FromArgb(isDark ? (byte)226 : (byte)246, isDark ? (byte)27 : (byte)255, isDark ? (byte)28 : (byte)255, isDark ? (byte)32 : (byte)255));
        resources["GlassInset"] = new SolidColorBrush(
            Color.FromArgb(isDark ? (byte)217 : (byte)240, isDark ? (byte)24 : (byte)249, isDark ? (byte)25 : (byte)249, isDark ? (byte)29 : (byte)249));
        resources["GlassEdge"] = new SolidColorBrush(
            Color.FromArgb(isDark ? (byte)58 : (byte)180, isDark ? (byte)58 : (byte)190, isDark ? (byte)59 : (byte)190, isDark ? (byte)66 : (byte)185));
    }

    public static Theme Dark { get; } = new()
    {
        Name = "Dark",
        AppBg = Color.FromRgb(16, 17, 20),
        ChromeBg = Color.FromRgb(23, 24, 28),
        ChromeAlt = Color.FromRgb(32, 33, 38),
        InputBg = Color.FromRgb(24, 25, 29),
        TitleBg = Color.FromRgb(17, 18, 22),
        ButtonBg = Color.FromRgb(27, 28, 32),
        ButtonHover = Color.FromRgb(41, 42, 48),
        ButtonPressed = Color.FromRgb(50, 51, 58),
        Stroke = Color.FromRgb(56, 57, 63),
        SubtleStroke = Color.FromRgb(42, 43, 48),
        Text = Color.FromRgb(241, 240, 243),
        Muted = Color.FromRgb(162, 160, 168),
        Lime = Color.FromRgb(80, 200, 80),
        Pink = Color.FromRgb(200, 180, 220),
        Accent = Color.FromRgb(149, 134, 200),
        SidebarBg = Color.FromRgb(23, 24, 28),
        SurfaceBg = Color.FromRgb(27, 28, 32),
        SurfaceRaised = Color.FromRgb(37, 38, 43),
        FieldBg = Color.FromRgb(24, 25, 29),
        InkSecondary = Color.FromRgb(192, 190, 197),
        StrokeStrong = Color.FromRgb(69, 70, 77),
        Danger = Color.FromRgb(237, 147, 153),
        Positive = Color.FromRgb(98, 196, 162),
        TabActive = Color.FromRgb(34, 35, 40),
        TabInactive = Color.FromRgb(23, 24, 28),
        TabAudio = Color.FromRgb(39, 34, 43),
        TabActiveAccent = Color.FromRgb(149, 134, 200),
        TabInactiveAccent = Color.FromRgb(75, 76, 83),
        TabSleepingAccent = Color.FromRgb(112, 111, 119),
        TabAudioAccent = Color.FromRgb(0, 200, 83)
    };

    public static Theme Light { get; } = new()
    {
        Name = "Light",
        AppBg = Color.FromRgb(245, 245, 243),
        ChromeBg = Color.FromRgb(233, 233, 229),
        ChromeAlt = Color.FromRgb(223, 223, 218),
        InputBg = Color.FromRgb(252, 252, 249),
        TitleBg = Color.FromRgb(238, 238, 233),
        ButtonBg = Color.FromRgb(239, 239, 234),
        ButtonHover = Color.FromRgb(227, 227, 221),
        ButtonPressed = Color.FromRgb(214, 214, 207),
        Stroke = Color.FromRgb(187, 187, 180),
        SubtleStroke = Color.FromRgb(215, 215, 209),
        Text = Color.FromRgb(22, 22, 20),
        Muted = Color.FromRgb(100, 100, 94),
        Lime = Color.FromRgb(70, 180, 70),
        Pink = Color.FromRgb(140, 100, 160),
        Accent = Color.FromRgb(152, 119, 232),
        SidebarBg = Color.FromRgb(245, 245, 247),
        SurfaceBg = Color.FromRgb(255, 255, 255),
        SurfaceRaised = Color.FromRgb(243, 243, 246),
        FieldBg = Color.FromRgb(255, 255, 255),
        InkSecondary = Color.FromRgb(81, 82, 92),
        StrokeStrong = Color.FromRgb(203, 208, 217),
        Danger = Color.FromRgb(196, 62, 73),
        Positive = Color.FromRgb(20, 132, 92),
        TabActive = Color.FromRgb(239, 239, 234),
        TabInactive = Color.FromRgb(224, 224, 218),
        TabAudio = Color.FromRgb(242, 227, 242),
        TabActiveAccent = Color.FromRgb(114, 77, 223),
        TabInactiveAccent = Color.FromRgb(152, 152, 146),
        TabSleepingAccent = Color.FromRgb(114, 114, 108),
        TabAudioAccent = Color.FromRgb(0, 200, 83)
    };
}
