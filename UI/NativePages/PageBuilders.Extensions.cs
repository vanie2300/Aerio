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
    private UIElement BuildExtensionsContent(BrowserSettings settings)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        var (ublockRow, ublockToggle) = SettingsToggleRow("uBlock Origin",
            "Built-in ad blocker. Installed and updated automatically from the Chrome Web Store.",
            _dataStore.Settings.UblockEnabled);
        ublockToggle.Checked += async (_, _) =>
        {
            _dataStore.SaveSettings(_dataStore.Settings with { UblockEnabled = true });
            await EnsureUblockReadyAsync();
            StatusBar.StatusText.Text = "uBlock Origin enabled";
        };
        ublockToggle.Unchecked += async (_, _) =>
        {
            _dataStore.SaveSettings(_dataStore.Settings with { UblockEnabled = false });
            if (_ublockExtension is not null)
            {
                try { await _ublockExtension.EnableAsync(false); }
                catch (Exception ex) { Log.Error(ex, "uBlock disable failed"); }
            }
            StatusBar.StatusText.Text = "uBlock Origin disabled";
        };
        stack.Children.Add(ublockRow);

        var loadBtn = SettingsButton("Load unpacked");
        loadBtn.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select extension folder"
            };

            if (dialog.ShowDialog() == true)
            {
                if (_profile is null)
                {
                    StatusBar.StatusText.Text = "WebView is not initialized yet.";
                    return;
                }

                try
                {
                    var existing = _dataStore.Settings.Extensions;

                    if (existing.Any(e => string.Equals(e.FolderPath, dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
                    {
                        StatusBar.StatusText.Text = "Extension is already loaded.";
                        return;
                    }

                    var webExt = await _profile.AddBrowserExtensionAsync(dialog.FolderName);
                    var newExt = new ExtensionInfo(webExt.Id, webExt.Name, dialog.FolderName, true, false);
                    var updated = existing.Append(newExt).ToList();
                    _loadedExtensions[webExt.Id] = webExt;
                    _dataStore.SaveSettings(_dataStore.Settings with { Extensions = updated });
                    StatusBar.StatusText.Text = $"Loaded extension: {webExt.Name}";
                    if (_activeTab is not null) RenderNativeSurface(_activeTab, forceRebuild: true);
                }
                catch (Exception ex)
                {
                    StatusBar.StatusText.Text = $"Failed to load extension: {ex.Message}";
                }
            }
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        buttonRow.Children.Add(loadBtn);

        var crxBtn = SettingsButton("Install from CRX...");
        crxBtn.Margin = new Thickness(8, 0, 0, 0);
        crxBtn.Click += async (_, _) =>
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select extension CRX file",
                    Filter = "CRX files (*.crx)|*.crx|All files (*.*)|*.*"
                };

                if (dialog.ShowDialog() == true)
                {
                    await InstallCrxAsync(dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred");
            }
        };

        buttonRow.Children.Add(crxBtn);
        stack.Children.Add(SectionDivider());
        stack.Children.Add(SettingsRow2("Add extension",
            "Load an unpacked extension folder or install a .crx file.",
            buttonRow));

        var storeLink = new TextBlock
        {
            Text = "Get more extensions from Chrome Web Store",
            Foreground = GetBrush("Accent"),
            FontSize = 13.5,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 12, 0, 16),
            TextDecorations = TextDecorations.Underline
        };
        storeLink.MouseDown += async (_, _) =>
        {
            try
            {
                var tab = _activeTab;
                if (tab is not null && tab.WebView?.CoreWebView2 is not null)
                {
                    await NavigateWebAsync(tab, "https://chromewebstore.google.com/category/extensions");
                }
                else
                {
                    await AddWebTabAsync("https://chromewebstore.google.com/category/extensions");
                }
            }
            catch (Exception ex) { Log.Error(ex, "Error occurred"); }
        };
        stack.Children.Add(SectionDivider());
        stack.Children.Add(storeLink);

        var dropZone = new Border
        {
            Height = 100,
            Margin = new Thickness(0, 0, 0, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = GetBrush("SubtleStroke"),
            Background = GetBrush("ButtonBg"),
            AllowDrop = true,
            Child = new TextBlock
            {
                Text = "Drop .crx file here to install",
                Foreground = GetBrush("Muted"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        dropZone.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                dropZone.BorderBrush = GetBrush("Accent");
                dropZone.Background = GetBrush("ButtonPressed");
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        };

        dropZone.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        };

        dropZone.DragLeave += (_, _) =>
        {
            dropZone.BorderBrush = GetBrush("SubtleStroke");
            dropZone.Background = GetBrush("ButtonBg");
        };

        dropZone.Drop += async (_, e) =>
        {
            try
            {
                dropZone.BorderBrush = GetBrush("SubtleStroke");
                dropZone.Background = GetBrush("ButtonBg");

                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                var crxFile = files?.FirstOrDefault(f => f.EndsWith(".crx", StringComparison.OrdinalIgnoreCase));
                if (crxFile is null)
                {
                    StatusBar.StatusText.Text = "Please drop a .crx file.";
                    return;
                }

                await InstallCrxAsync(crxFile);
                e.Handled = true;
            }
            catch (Exception ex) { Log.Error(ex, "Error occurred"); }
        };

        stack.Children.Add(dropZone);

        if (settings.Extensions.Count == 0)
        {
            stack.Children.Add(SectionDivider());
            stack.Children.Add(EmptyText("No additional extensions installed."));
            return stack;
        }

        foreach (var ext in settings.Extensions)
        {
            stack.Children.Add(SectionDivider());
            var capturedExt = ext;

            var toggle = SettingsSwitch(ext.Enabled);
            toggle.IsEnabled = !ext.IsBuiltIn;
            toggle.VerticalAlignment = VerticalAlignment.Center;
            toggle.Checked += async (_, _) =>
            {
                try
                {
                    await SetExtensionEnabledAsync(capturedExt, true);
                    UpdateExtensionEnabled(capturedExt.Id, true);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error occurred");
                }
            };
            toggle.Unchecked += async (_, _) =>
            {
                try
                {
                    await SetExtensionEnabledAsync(capturedExt, false);
                    UpdateExtensionEnabled(capturedExt.Id, false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error occurred");
                }
            };

            var manageBtn = SettingsButton("Manage", ghost: true);
            manageBtn.Margin = new Thickness(10, 0, 0, 0);
            manageBtn.Click += async (_, _) =>
            {
                try
                {
                    var tab = _activeTab;
                    if (tab is not null && tab.WebView?.CoreWebView2 is not null)
                    {
                        await NavigateWebAsync(tab, $"chrome-extension://{capturedExt.Id}/popup.html");
                    }
                    else
                    {
                        await AddWebTabAsync($"chrome-extension://{capturedExt.Id}/popup.html");
                    }
                }
                catch (Exception ex) { Log.Error(ex, "Error occurred"); }
            };

            var removeBtn = SettingsButton("Remove", ghost: true);
            removeBtn.Margin = new Thickness(8, 0, 0, 0);
            removeBtn.IsEnabled = !ext.IsBuiltIn;
            removeBtn.Click += async (_, _) =>
            {
                try
                {
                    await RemoveExtensionAsync(capturedExt);
                    var filtered = _dataStore.Settings.Extensions.Where(e => e.Id != capturedExt.Id).ToList();
                    _dataStore.SaveSettings(_dataStore.Settings with { Extensions = filtered });
                    if (_activeTab is not null) RenderNativeSurface(_activeTab, forceRebuild: true);
                }
                catch (Exception ex)
                {
                    StatusBar.StatusText.Text = $"Failed to remove extension: {ex.Message}";
                }
            };

            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            controls.Children.Add(toggle);
            controls.Children.Add(manageBtn);
            controls.Children.Add(removeBtn);

            stack.Children.Add(SettingsRow2(ext.Name,
                ext.IsBuiltIn ? "Built-in extension - updated automatically." : "Loaded from a local folder.",
                controls));
        }

        return stack;
    }

    private async Task SetExtensionEnabledAsync(ExtensionInfo ext, bool enabled)
    {
        if (_loadedExtensions.TryGetValue(ext.Id, out var webExt))
        {
            try
            {
                await webExt.EnableAsync(enabled);
            }
            catch (Exception ex)
            {
                StatusBar.StatusText.Text = $"Failed to {(enabled ? "enable" : "disable")} extension: {ex.Message}";
            }
        }
    }

    private async Task RemoveExtensionAsync(ExtensionInfo ext)
    {
        if (_loadedExtensions.TryGetValue(ext.Id, out var webExt))
        {
            try
            {
                await webExt.RemoveAsync();
            }
            catch (Exception ex)
            {
                StatusBar.StatusText.Text = $"Failed to remove extension: {ex.Message}";
            }
            _loadedExtensions.Remove(ext.Id);
        }
    }

    private void UpdateExtensionEnabled(string id, bool enabled)
    {
        var updated = _dataStore.Settings.Extensions
            .Select(e => e.Id == id ? e with { Enabled = enabled } : e)
            .ToList();
        _dataStore.SaveSettings(_dataStore.Settings with { Extensions = updated });
    }

    private async Task InstallCrxAsync(string crxPath)
    {
        string extractDir;
        string? manifestError = null;
        string? webExtId = null;
        string? webExtName = null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(crxPath);

            int zipStart = FindZipStart(bytes);
            if (zipStart < 0)
            {
                await Dispatcher.InvokeAsync(() => StatusBar.StatusText.Text = "Invalid CRX file: ZIP data not found.");
                return;
            }

            var crxDirName = Path.GetFileNameWithoutExtension(crxPath);
            extractDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TabloidBrowser",
                "Extensions",
                crxDirName);

            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            Directory.CreateDirectory(extractDir);

            using var zipStream = new MemoryStream(bytes, zipStart, bytes.Length - zipStart);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var normalizedExtractDir = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var destPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
                if (!destPath.StartsWith(normalizedExtractDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    var parentDir = Path.GetDirectoryName(destPath);
                    if (parentDir is not null) Directory.CreateDirectory(parentDir);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            var manifestPath = Path.Combine(extractDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                manifestError = "Invalid CRX file: manifest.json not found.";
                Directory.Delete(extractDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => StatusBar.StatusText.Text = $"Failed to install CRX: {ex.Message}");
            return;
        }

        if (manifestError is not null)
        {
            await Dispatcher.InvokeAsync(() => StatusBar.StatusText.Text = manifestError);
            return;
        }

        await Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (_profile is null)
                {
                    StatusBar.StatusText.Text = "WebView is not initialized yet.";
                    return;
                }

                var webExt = await _profile.AddBrowserExtensionAsync(extractDir);
                webExtName = webExt.Name;
                webExtId = webExt.Id;

                var existing = _dataStore.Settings.Extensions;
                if (existing.Any(e => e.Id == webExt.Id))
                {
                    StatusBar.StatusText.Text = $"Extension '{webExtName}' is already installed.";
                    return;
                }

                var newExt = new ExtensionInfo(webExt.Id, webExtName, extractDir, true, false);
                _loadedExtensions[webExt.Id] = webExt;
                _dataStore.SaveSettings(_dataStore.Settings with { Extensions = existing.Append(newExt).ToList() });
                StatusBar.StatusText.Text = $"Installed extension: {webExtName}";
                if (_activeTab is not null) RenderNativeSurface(_activeTab, forceRebuild: true);
            }
            catch (Exception ex)
            {
                StatusBar.StatusText.Text = $"Failed to install CRX: {ex.Message}";
            }
        }).Task.Unwrap();
    }

    private static int FindZipStart(byte[] data)
    {
        // CRX3 files: parse the header length from the Cr24 container instead of
        // blind-scanning â€” headers larger than 2 KB were rejected, and PK bytes
        // inside the header blob produced bogus offsets.
        if (data.Length >= 12 &&
            data[0] == 'C' && data[1] == 'r' && data[2] == '2' && data[3] == '4')
        {
            var headerLength = BitConverter.ToUInt32(data, 8);
            var zipStart = 12 + (int)Math.Min(headerLength, int.MaxValue - 12);
            if (zipStart + 4 <= data.Length &&
                data[zipStart] == 0x50 && data[zipStart + 1] == 0x4B && data[zipStart + 2] == 0x03 && data[zipStart + 3] == 0x04)
            {
                return zipStart;
            }
        }

        int maxSearch = Math.Min(data.Length, 2048);
        for (int i = 0; i <= maxSearch - 4; i++)
        {
            if (data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x03 && data[i + 3] == 0x04)
            {
                return i;
            }
        }
        return -1;
    }
}
