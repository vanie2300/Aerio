using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace TabloidBrowser;

public partial class MainWindow
{
    private UIElement BuildAiManagementPage(BrowserTab tab)
    {
        var settings = _dataStore.Settings;
        var toolsEnabled = settings.AiToolsEnabled ?? new Dictionary<string, bool>();

        var toolDefs = new (string Id, string Name, string Description)[]
        {
            ("search", "Search", "Search the web for information"),
            ("search_and_read", "Search & Read", "Search, navigate to a result, and read it"),
            ("read_page", "Read Page", "Read current page content, metadata, or links"),
            ("navigate", "Navigate", "Navigate the current tab to a URL"),
            ("open_tab", "Open Tab", "Open a URL in a new background tab"),
            ("close_tab", "Close Tab", "Close an AI-owned background tab"),
            ("click", "Click Element", "Click an interactive element by selector"),
            ("fill", "Fill Field", "Fill an input field with text"),
            ("list_elements", "List Elements", "List all interactive elements on the page"),
            ("scroll", "Scroll Page", "Scroll the page up or down"),
        };

        var toolToggles = new Dictionary<string, ToggleButton>();
        var toolRows = new List<object>();
        foreach (var (id, name, description) in toolDefs)
        {
            var isEnabled = !toolsEnabled.TryGetValue(id, out var enabled) || enabled;
            var (row, toggle) = SettingsToggleRow(name, description, isEnabled);
            toolToggles[id] = toggle;
            toolRows.Add(row);
        }

        var maxIter = SettingsSliderRow("Max iterations",
            "Maximum number of tool-calling iterations per request.",
            settings.AiMaxIterations, 1, 20, v => ((int)v).ToString());

        var maxOpenTabs = SettingsSliderRow("Max open tabs",
            "Maximum AI-owned tabs the agent can keep open per task (oldest is closed when exceeded).",
            settings.AiPolicy?.MaxOpenTabs ?? AiBrowserPolicy.Default.MaxOpenTabs, 1, 50, v => ((int)v).ToString());

        var (confirmRow, confirmSensitive) = SettingsToggleRow("Confirm sensitive actions",
            "Ask for confirmation before navigating or opening tabs when page context is active.",
            settings.AiPageContextConfirmation);

        var resetBtn = MatteButton("Reset to defaults", (_, _) =>
        {
            foreach (var (id, _, _) in toolDefs)
                toolToggles[id].IsChecked = true;
            maxIter.Input.Value = 10;
            maxOpenTabs.Input.Value = AiBrowserPolicy.Default.MaxOpenTabs;
            confirmSensitive.IsChecked = true;
            SaveAiSettings();
        });

        var searchEngineRow = SettingsRow2("Search engine",
            "The AI's web search tool uses your browser's selected search engine.",
            new TextBlock
            {
                Text = settings.SearchEngine,
                Foreground = GetBrush("Text"),
                VerticalAlignment = VerticalAlignment.Center
            });

        var sections = new FrameworkElement[]
        {
            PrefSection("Agent tools", "Enable or disable individual tools that the AI agent can use.", toolRows.ToArray()),
            PrefSection("Agent settings", null,
                maxIter.Control,
                maxOpenTabs.Control,
                searchEngineRow,
                confirmRow,
                SettingsRow2("Reset to defaults",
                    "Restore all agent tools and limits to their defaults.",
                    resetBtn))
        };

        var content = new StackPanel();
        for (int i = 0; i < sections.Length; i++)
        {
            // Reference: the first section on a page has no top border and
            // tighter top padding.
            if (sections[i] is Border sectionBorder)
            {
                sectionBorder.BorderThickness = i == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0);
                sectionBorder.Padding = i == 0 ? new Thickness(0, 5, 0, 30) : new Thickness(0, 26, 0, 30);
            }
            content.Children.Add(sections[i]);
        }

        void SaveAiSettings()
        {
            var newToolsEnabled = new Dictionary<string, bool>();
            foreach (var (id, _, _) in toolDefs)
                newToolsEnabled[id] = toolToggles[id].IsChecked == true;

            // Re-read the live settings so fields this page doesn't own (changed
            // elsewhere since the page was built) are not clobbered by a stale
            // snapshot.
            var saved = _dataStore.Settings with
            {
                AiToolsEnabled = newToolsEnabled,
                AiMaxIterations = ClampInt(((int)maxIter.Input.Value).ToString(), 1, 20, 10),
                AiPageContextConfirmation = confirmSensitive.IsChecked == true,
                AiPolicy = new AiBrowserPolicy(
                    MaxOpenTabs: ClampInt(((int)maxOpenTabs.Input.Value).ToString(), 1, 50, AiBrowserPolicy.Default.MaxOpenTabs),
                    AllowNavigateActiveTab: _dataStore.Settings.AiPolicy?.AllowNavigateActiveTab ?? false,
                    CleanupOnTaskComplete: _dataStore.Settings.AiPolicy?.CleanupOnTaskComplete ?? false)
            };
            _dataStore.SaveSettings(saved);
            ApplySettings();
            NotifySettingsChanged();
            StatusBar.StatusText.Text = "AI settings saved";
        }

        foreach (var toggle in toolToggles.Values)
        {
            toggle.Checked += (_, _) => SaveAiSettings();
            toggle.Unchecked += (_, _) => SaveAiSettings();
        }
        maxIter.Input.LostFocus += (_, _) => SaveAiSettings();
        maxOpenTabs.Input.LostFocus += (_, _) => SaveAiSettings();
        confirmSensitive.Checked += (_, _) => SaveAiSettings();
        confirmSensitive.Unchecked += (_, _) => SaveAiSettings();

        return BuildPageShell(tab, "AI Management", content);
    }
}
