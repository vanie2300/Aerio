using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TabloidBrowser;

public partial class MainWindow
{
    private static readonly (string Key, string Label)[] SitePermissionDefinitions =
    [
        ("Camera", "Camera"),
        ("Microphone", "Microphone"),
        ("Geolocation", "Location"),
        ("Notifications", "Notifications"),
        ("ClipboardRead", "Clipboard"),
        ("Autoplay", "Autoplay"),
        ("MultipleAutomaticDownloads", "Automatic downloads"),
        ("FileReadWrite", "File access"),
        ("LocalFonts", "Local fonts"),
        ("OtherSensors", "Sensors"),
        ("MidiSystemExclusiveMessages", "MIDI devices"),
        ("WindowManagement", "Window management"),
        ("PersistentStorage", "Persistent storage")
    ];

    private static readonly (string Group, string Key, string Label)[] PermissionGroupDefinitions =
    [
        ("Media && Sensors", "Camera", "Camera"),
        ("Media && Sensors", "Microphone", "Microphone"),
        ("Media && Sensors", "OtherSensors", "Sensors"),
        ("Location && Notifications", "Geolocation", "Location"),
        ("Location && Notifications", "Notifications", "Notifications"),
        ("Clipboard && Files", "ClipboardRead", "Clipboard"),
        ("Clipboard && Files", "FileReadWrite", "File access"),
        ("Clipboard && Files", "Autoplay", "Autoplay"),
        ("Clipboard && Files", "MultipleAutomaticDownloads", "Auto downloads"),
        ("Advanced", "LocalFonts", "Local fonts"),
        ("Advanced", "MidiSystemExclusiveMessages", "MIDI"),
        ("Advanced", "WindowManagement", "Window mgmt"),
        ("Advanced", "PersistentStorage", "Persistent storage")
    ];

    private static readonly string[] SitePermissionStateOptions = ["Ask", "Allow", "Block"];

    private string? _lastSitePermissionsUrl;
    private Popup? _sitePermissionsPopup;
    private Popup? _changelogPopup;
    private Grid? _popupOverlay;
    private bool _isPermissionsPopupOpen;
    private bool _isChangelogOpen;
    private bool _isSyncingProfilePermissions;

    private async void PermissionRequested(BrowserTab tab, CoreWebView2PermissionRequestedEventArgs args)
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    try { PermissionRequested(tab, args); }
                    catch (Exception ex) { Log.Error(ex, "PermissionRequested dispatch failed"); }
                });
                return;
            }

            var host = NormalizePermissionHost(args.Uri) ?? NormalizePermissionHost(tab.Address);
            var permission = PermissionKey(args.PermissionKind);
            if (string.IsNullOrWhiteSpace(host) || permission is null)
            {
                return;
            }

            var rule = _dataStore.Settings.SitePermissions.FirstOrDefault(item =>
                string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Permission, permission, StringComparison.OrdinalIgnoreCase));

            if (rule is null)
            {
                try
                {
                    var deferral = args.GetDeferral();
                    try
                    {
                        var requestedState = await PromptForPermissionAsync(host, permission);
                        if (requestedState == "Default")
                        {
                            args.State = CoreWebView2PermissionState.Deny;
                            args.Handled = true;
                            return;
                        }

                        args.SavesInProfile = false;
                        args.State = ToWebViewPermissionState(requestedState);
                        args.Handled = true;
                        StatusBar.StatusText.Text = $"{PermissionLabel(permission)} {PermissionStateLabel(requestedState).ToLowerInvariant()} for {host}";
                    }
                    finally { deferral.Dispose(); }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "PermissionRequested failed");
                    args.State = CoreWebView2PermissionState.Deny;
                    args.Handled = true;
                }

                return;
            }

            args.SavesInProfile = false;
            args.State = ToWebViewPermissionState(rule.State);
            args.Handled = true;
            StatusBar.StatusText.Text = $"{PermissionLabel(permission)} {PermissionStateLabel(rule.State).ToLowerInvariant()} for {host}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PermissionRequested fatal");
            args.State = CoreWebView2PermissionState.Deny;
            args.Handled = true;
        }
    }

    private async Task<string> PromptForPermissionAsync(string host, string permission)
    {
        var label = PermissionLabel(permission);
        var tcs = new TaskCompletionSource<string>();
        var rememberChoice = false;

        RemovePopupBlur();

        if (Content is not Grid rootGrid)
        {
            return "Deny";
        }

        rootGrid.Effect = new BlurEffect
        {
            Radius = 14,
            KernelType = KernelType.Gaussian
        };

        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            IsHitTestVisible = false,
            Opacity = 0
        };
        Grid.SetRowSpan(overlay, rootGrid.RowDefinitions.Count);
        rootGrid.Children.Add(overlay);
        overlay.BeginAnimation(Grid.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.15)));

        var popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Center,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Scroll
        };

        popup.Closed += (_, _) =>
        {
            tcs.TrySetResult("Deny");
            RemovePopupBlur();
            if (overlay.Parent == rootGrid)
                rootGrid.Children.Remove(overlay);
        };

        var iconMap = new Dictionary<string, string>
        {
            ["Camera"] = "\xE722",
            ["Microphone"] = "\xE720",
            ["Geolocation"] = "\xE707",
            ["Notifications"] = "\xEA7F",
            ["ClipboardRead"] = "\xE7C1",
            ["Autoplay"] = "\xE768",
            ["MultipleAutomaticDownloads"] = "\xECE4",
            ["FileReadWrite"] = "\xEC25",
            ["LocalFonts"] = "\xE8D2",
            ["OtherSensors"] = "\xE94A",
            ["MidiSystemExclusiveMessages"] = "\xE8D6",
            ["WindowManagement"] = "\xE8B2",
            ["PersistentStorage"] = "\xE755"
        };
        var icon = iconMap.GetValueOrDefault(permission, "\xE76E");

        var iconCircle = new Border
        {
            Width = 56,
            Height = 56,
            Background = new SolidColorBrush(Color.FromArgb(30, 114, 77, 223)),
            CornerRadius = new CornerRadius(28),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 26,
                Foreground = GetBrush("Cyan"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var body = new Border
        {
            Width = 400,
            Background = GetBrush("AppBg"),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = GetBrush("Stroke"),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.5,
                BlurRadius = 24,
                ShadowDepth = 0
            },
            Child = new StackPanel { Margin = new Thickness(28, 24, 28, 20) }
        };

        var stack = (StackPanel)body.Child;

        stack.Children.Add(iconCircle);

        stack.Children.Add(new TextBlock
        {
            Text = "Permission request",
            Foreground = GetBrush("Text"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });

        stack.Children.Add(new TextBlock
        {
            Text = host,
            Foreground = GetBrush("Text"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 2)
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"wants to use your {label.ToLowerInvariant()}",
            Foreground = GetBrush("Muted"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 18)
        });

        var rememberCheck = new CheckBox
        {
            Content = "Remember my choice for this site",
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 16),
            IsChecked = false
        };
        rememberCheck.Checked += (_, _) => rememberChoice = true;
        rememberCheck.Unchecked += (_, _) => rememberChoice = false;
        stack.Children.Add(rememberCheck);

        var separator = new Border
        {
            Height = 1,
            Background = GetBrush("SubtleStroke"),
            Margin = new Thickness(0, 0, 0, 14)
        };
        stack.Children.Add(separator);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var denyBtn = new Button
        {
            Content = "Deny",
            MinWidth = 80,
            Height = 34,
            Padding = new Thickness(16, 2, 16, 2),
            Cursor = Cursors.Hand
        };

        var allowBtn = new Button
        {
            Content = "Allow",
            MinWidth = 80,
            Height = 34,
            Padding = new Thickness(16, 2, 16, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Background = GetBrush("Cyan"),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0)
        };

        void SetResult(string state)
        {
            if (rememberChoice)
            {
                SaveRuntimeSitePermission(host, permission, state);
            }

            tcs.TrySetResult(state);
            popup.IsOpen = false;
        }

        denyBtn.Click += (_, _) => SetResult("Deny");
        allowBtn.Click += (_, _) => SetResult("Allow");

        body.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                SetResult("Deny");
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                SetResult("Allow");
                e.Handled = true;
            }
        };

        btnRow.Children.Add(denyBtn);
        btnRow.Children.Add(allowBtn);
        stack.Children.Add(btnRow);

        popup.Child = body;
        popup.Opened += (_, _) => allowBtn.Focus();
        popup.IsOpen = true;

        return await tcs.Task;
    }

    private void SaveRuntimeSitePermission(string host, string permission, string state)
    {
        if (_isPrivate)
        {
            return;
        }

        var settings = _dataStore.Settings;
        var updatedPermissions = UpdateSitePermissionRules(
            settings.SitePermissions,
            host,
            new Dictionary<string, string> { [permission] = state });

        _dataStore.SaveSettings(settings with { SitePermissions = updatedPermissions });
    }

    private void RememberSitePermissionsUrl(BrowserTab tab)
    {
        if (tab.Kind == BrowserTabKind.Web && BrowserDataStore.IsHttpUrl(tab.Address))
        {
            _lastSitePermissionsUrl = tab.Address;
        }
    }

    private string GetSuggestedPermissionHost()
    {
        return NormalizePermissionHost(_lastSitePermissionsUrl) ??
            NormalizePermissionHost(_activeTab?.Address) ??
            string.Empty;
    }

    private static List<string> GetSitePermissionHosts(IEnumerable<SitePermissionRule> rules, string suggestedHost)
    {
        var hosts = rules
            .Select(rule => rule.Host)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .ToList();

        if (!string.IsNullOrWhiteSpace(suggestedHost))
        {
            hosts.Add(suggestedHost);
        }

        return hosts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? NormalizePermissionHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var input = value.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.Host.ToLowerInvariant();
        }

        if (Uri.TryCreate($"https://{input}", UriKind.Absolute, out var withScheme) &&
            !string.IsNullOrWhiteSpace(withScheme.Host))
        {
            return withScheme.Host.ToLowerInvariant();
        }

        return null;
    }

    private static List<SitePermissionRule> UpdateSitePermissionRules(
        IEnumerable<SitePermissionRule> existing,
        string? host,
        IReadOnlyDictionary<string, string> states)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return [.. existing];
        }

        var updated = existing
            .Where(rule => !string.Equals(rule.Host, host, StringComparison.OrdinalIgnoreCase) ||
                !states.ContainsKey(rule.Permission))
            .ToList();

        foreach (var item in states)
        {
            if (item.Value is not ("Allow" or "Deny"))
            {
                continue;
            }

            updated.Add(new SitePermissionRule(host, item.Key, item.Value));
        }

        return updated;
    }

    private static List<SitePermissionRule> MergeSitePermissionRules(
        IEnumerable<SitePermissionRule> existing,
        IEnumerable<SitePermissionRule> imported)
    {
        var merged = existing.ToDictionary(
            rule => $"{rule.Host}|{rule.Permission}",
            rule => rule,
            StringComparer.OrdinalIgnoreCase);

        foreach (var rule in imported)
        {
            merged[$"{rule.Host}|{rule.Permission}"] = rule;
        }

        return merged.Values
            .OrderBy(rule => rule.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.Permission, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SitePermissionRulesEqual(
        IEnumerable<SitePermissionRule> first,
        IEnumerable<SitePermissionRule> second)
    {
        static string key(SitePermissionRule rule) => $"{rule.Host}|{rule.Permission}|{rule.State}";

        return first.Select(key).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).SequenceEqual(
            second.Select(key).OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string GetSitePermissionState(IEnumerable<SitePermissionRule> rules, string? hostOrUrl, string permission)
    {
        var host = NormalizePermissionHost(hostOrUrl);
        if (string.IsNullOrWhiteSpace(host))
        {
            return "Default";
        }

        return rules.FirstOrDefault(rule =>
            string.Equals(rule.Host, host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.Permission, permission, StringComparison.OrdinalIgnoreCase))?.State ?? "Default";
    }

    private static string? PermissionKey(CoreWebView2PermissionKind kind)
    {
        return kind switch
        {
            CoreWebView2PermissionKind.Camera => "Camera",
            CoreWebView2PermissionKind.Microphone => "Microphone",
            CoreWebView2PermissionKind.Geolocation => "Geolocation",
            CoreWebView2PermissionKind.Notifications => "Notifications",
            CoreWebView2PermissionKind.ClipboardRead => "ClipboardRead",
            CoreWebView2PermissionKind.Autoplay => "Autoplay",
            CoreWebView2PermissionKind.MultipleAutomaticDownloads => "MultipleAutomaticDownloads",
            CoreWebView2PermissionKind.FileReadWrite => "FileReadWrite",
            CoreWebView2PermissionKind.LocalFonts => "LocalFonts",
            CoreWebView2PermissionKind.OtherSensors => "OtherSensors",
            CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "MidiSystemExclusiveMessages",
            CoreWebView2PermissionKind.WindowManagement => "WindowManagement",
            CoreWebView2PermissionKind.PersistentStorage => "PersistentStorage",
            _ => null
        };
    }

    private static CoreWebView2PermissionKind PermissionKindFromKey(string key)
    {
        return key switch
        {
            "Camera" => CoreWebView2PermissionKind.Camera,
            "Microphone" => CoreWebView2PermissionKind.Microphone,
            "Geolocation" => CoreWebView2PermissionKind.Geolocation,
            "Notifications" => CoreWebView2PermissionKind.Notifications,
            "ClipboardRead" => CoreWebView2PermissionKind.ClipboardRead,
            "Autoplay" => CoreWebView2PermissionKind.Autoplay,
            "MultipleAutomaticDownloads" => CoreWebView2PermissionKind.MultipleAutomaticDownloads,
            "FileReadWrite" => CoreWebView2PermissionKind.FileReadWrite,
            "LocalFonts" => CoreWebView2PermissionKind.LocalFonts,
            "OtherSensors" => CoreWebView2PermissionKind.OtherSensors,
            "MidiSystemExclusiveMessages" => CoreWebView2PermissionKind.MidiSystemExclusiveMessages,
            "WindowManagement" => CoreWebView2PermissionKind.WindowManagement,
            "PersistentStorage" => CoreWebView2PermissionKind.PersistentStorage,
            _ => CoreWebView2PermissionKind.UnknownPermission
        };
    }

    private static CoreWebView2PermissionState ToWebViewPermissionState(string state)
    {
        return state switch
        {
            "Allow" => CoreWebView2PermissionState.Allow,
            "Deny" or "Block" => CoreWebView2PermissionState.Deny,
            _ => CoreWebView2PermissionState.Default
        };
    }

    private static string PermissionStateFromWebView(CoreWebView2PermissionState state)
    {
        return state switch
        {
            CoreWebView2PermissionState.Allow => "Allow",
            CoreWebView2PermissionState.Deny => "Deny",
            _ => "Default"
        };
    }

    private static string PermissionStateFromUi(string? state)
    {
        return state switch
        {
            "Allow" => "Allow",
            "Block" => "Deny",
            _ => "Default"
        };
    }

    private static string PermissionStateToUi(string? state)
    {
        return state switch
        {
            "Allow" => "Allow",
            "Deny" => "Block",
            _ => "Ask"
        };
    }

    private static string PermissionStateLabel(string state)
    {
        return state switch
        {
            "Allow" => "allowed",
            "Deny" or "Block" => "blocked",
            _ => "set to ask"
        };
    }

    private static string PermissionLabel(string key)
    {
        foreach (var p in SitePermissionDefinitions)
            if (p.Key == key) return p.Label;
        return key;
    }

    private async Task SyncProfilePermissionSettingsAsync()
    {
        try
        {
            if (_isPrivate || _isSyncingProfilePermissions)
            {
                return;
            }

            _isSyncingProfilePermissions = true;
            var profile = _tabs
                .Select(tab => tab.WebView?.CoreWebView2?.Profile)
                .FirstOrDefault(profile => profile is not null);
            if (profile is null)
            {
                return;
            }

            var permissionSettings = await profile.GetNonDefaultPermissionSettingsAsync();
            var importedRules = new List<SitePermissionRule>();

            foreach (var setting in permissionSettings)
            {
                var host = NormalizePermissionHost(setting.PermissionOrigin);
                var permission = PermissionKey(setting.PermissionKind);
                var state = PermissionStateFromWebView(setting.PermissionState);

                if (string.IsNullOrWhiteSpace(host) || permission is null || state == "Default")
                {
                    continue;
                }

                importedRules.Add(new SitePermissionRule(host, permission, state));
            }

            if (importedRules.Count == 0)
            {
                return;
            }

            var merged = MergeSitePermissionRules(_dataStore.Settings.SitePermissions, importedRules);
            if (SitePermissionRulesEqual(_dataStore.Settings.SitePermissions, merged))
            {
                return;
            }

            _dataStore.SaveSettings(_dataStore.Settings with { SitePermissions = merged });
            var settingsTab = _activeTab;
            if (settingsTab?.Kind == BrowserTabKind.Settings)
            {
                RenderNativeSurface(settingsTab, forceRebuild: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred");
        }
        finally
        {
            _isSyncingProfilePermissions = false;
        }
    }

    private async Task ApplySitePermissionProfileStateAsync(string host, IReadOnlyDictionary<string, string> states)
    {
        var profile = _tabs
            .Select(tab => tab.WebView?.CoreWebView2?.Profile)
            .FirstOrDefault(profile => profile is not null);

        if (profile is null)
        {
            return;
        }

        foreach (var (permission, state) in states)
        {
            var kind = PermissionKindFromKey(permission);
            if (kind == CoreWebView2PermissionKind.UnknownPermission)
            {
                continue;
            }

            foreach (var origin in new[] { $"https://{host}", $"http://{host}" })
            {
                try
                {
                    await profile.SetPermissionStateAsync(kind, origin, ToWebViewPermissionState(state));
                }
                catch (InvalidOperationException ex)
                {
                    Log.Error(ex, "Error occurred");
                }
                catch (COMException ex)
                {
                    Log.Error(ex, "Error occurred");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error occurred");
                }
            }
        }
    }
}
