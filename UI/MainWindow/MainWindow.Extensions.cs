using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TabloidBrowser;

public partial class MainWindow
{
    private const string UblockFolderName = "cjpalhdlnbpafiamejdnhcphjbkeiagm";

    private static bool IsUblockExtension(ExtensionInfo ext) =>
        string.Equals(Path.GetFileName(ext.FolderPath.TrimEnd('\\', '/')), UblockFolderName, StringComparison.OrdinalIgnoreCase);

    private string UblockFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabloidBrowser",
        "Extensions",
        UblockFolderName);

    private CoreWebView2BrowserExtension? _ublockExtension;

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is null)
        {
            return;
        }

        NavToolbar.ForceDarkMenuItem.IsChecked = _dataStore.Settings.ForceDarkPage;
        NavToolbar.UblockMenuItem.IsChecked = _dataStore.Settings.UblockEnabled;
        element.ContextMenu.PlacementTarget = element;
        element.ContextMenu.Placement = PlacementMode.Bottom;
        element.ContextMenu.IsOpen = true;
    }

    private void ForceDarkMenu_Checked(object sender, RoutedEventArgs e)
    {
        var settings = _dataStore.Settings with { ForceDarkPage = true };
        _dataStore.SaveSettings(settings);
        ApplyForceDarkToExistingTabs();
    }

    private void ForceDarkMenu_Unchecked(object sender, RoutedEventArgs e)
    {
        var settings = _dataStore.Settings with { ForceDarkPage = false };
        _dataStore.SaveSettings(settings);
        ApplyForceDarkToExistingTabs();
    }

    private async void ShowSettings_Click(object sender, RoutedEventArgs e)
    {
        try { await AddNativePageTabAsync(BrowserTabKind.Settings); }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private async void UblockToggle_Click(object sender, RoutedEventArgs e)
    {
        var newState = !_dataStore.Settings.UblockEnabled;
        _dataStore.SaveSettings(_dataStore.Settings with { UblockEnabled = newState });

        if (newState)
        {
            await EnsureUblockReadyAsync();
        }
        else if (_ublockExtension is not null)
        {
            try { await _ublockExtension.EnableAsync(false); }
            catch (Exception ex) { Log.Error(ex, "uBlock disable failed"); }
        }

        if (sender is MenuItem menuItem)
            menuItem.IsChecked = newState;

        StatusBar.StatusText.Text = newState ? "uBlock Origin enabled" : "uBlock Origin disabled";
    }

    private async Task EnsureUblockReadyAsync()
    {
        if (!_dataStore.Settings.UblockEnabled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_privateUserDataFolder))
        {
            return;
        }

        var folder = UblockFolderPath;
        if (!File.Exists(Path.Combine(folder, "manifest.json")))
        {
            SetStatusTextSafe("Installing uBlock Origin...");
            if (!await DownloadUblockAsync(folder))
            {
                return;
            }

            var installedVersion = ReadManifestVersion(folder);
            if (installedVersion is not null && !string.Equals(_dataStore.Settings.UblockVersion, installedVersion, StringComparison.Ordinal))
            {
                _dataStore.SaveSettings(_dataStore.Settings with { UblockVersion = installedVersion });
            }
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (_profile is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        if (_profile is null)
        {
            return;
        }

        if (_ublockExtension is null)
        {
            _ublockExtension = await Dispatcher.InvokeAsync(async () =>
            {
                try { return await _profile!.AddBrowserExtensionAsync(folder); }
                catch (Exception ex) { Log.Error(ex, "uBlock load failed"); return null; }
            }).Task.Unwrap();
        }

        if (_ublockExtension is not null)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                try { await _ublockExtension.EnableAsync(_dataStore.Settings.UblockEnabled); }
                catch (Exception ex) { Log.Error(ex, "uBlock enable failed"); }
            }).Task.Unwrap();
        }

        await CheckUblockUpdateAsync();
    }

    private async Task CheckUblockUpdateAsync()
    {
        try
        {
            if (!_dataStore.Settings.UblockEnabled)
            {
                return;
            }

            var lastCheck = _dataStore.Settings.UblockUpdateCheckUtc;
            if (lastCheck is not null && DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromDays(30))
            {
                return;
            }

            var installed = _dataStore.Settings.UblockVersion;
            var latest = await QueryLatestUblockVersionAsync();

            if (IsNewerVersion(latest, installed))
            {
                var folder = UblockFolderPath;
                if (await DownloadUblockAsync(folder))
                {
                    var version = ReadManifestVersion(folder);
                    if (version is not null)
                    {
                        if (_profile is not null)
                        {
                            _ublockExtension = await Dispatcher.InvokeAsync(async () =>
                            {
                                try
                                {
                                    var webExt = await _profile!.AddBrowserExtensionAsync(folder);
                                    await webExt.EnableAsync(true);
                                    return webExt;
                                }
                                catch (Exception ex) { Log.Error(ex, "uBlock reinstall failed"); return null; }
                            }).Task.Unwrap();
                        }

                        _dataStore.SaveSettings(_dataStore.Settings with
                        {
                            UblockVersion = version,
                            UblockUpdateCheckUtc = DateTimeOffset.UtcNow
                        });
                        SetStatusTextSafe($"uBlock Origin updated to v{version}");
                        return;
                    }
                }
            }

            _dataStore.SaveSettings(_dataStore.Settings with { UblockUpdateCheckUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "uBlock update check failed");
        }
    }

    private async Task<string?> QueryLatestUblockVersionAsync()
    {
        try
        {
            var url = $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=106.0.0.0&acceptformat=crx3&x=id%3D{UblockFolderName}%26installsource%3Dondemand%26uc";
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _crxHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri?.ToString();
            return finalUri is null ? null : ParseVersionFromCrxUri(finalUri);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "uBlock version query failed");
            return null;
        }
    }

    private static string? ParseVersionFromCrxUri(string uri)
    {
        var match = Regex.Match(uri, @"_(\d+(?:_\d+)+)\.crx$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace('_', '.') : null;
    }

    private static bool IsNewerVersion(string? latest, string? installed)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (string.IsNullOrWhiteSpace(installed)) return true;
        return Version.TryParse(latest, out var latestVersion)
            && Version.TryParse(installed, out var installedVersion)
            && latestVersion > installedVersion;
    }

    private async Task<bool> DownloadUblockAsync(string targetDir)
    {
        try
        {
            var url = $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=106.0.0.0&acceptformat=crx3&x=id%3D{UblockFolderName}%26installsource%3Dondemand%26uc";
            var tempDir = Path.Combine(Path.GetTempPath(), "TabloidBrowser", "CrxDownloads");
            Directory.CreateDirectory(tempDir);
            var crxPath = Path.Combine(tempDir, $"{UblockFolderName}.crx");

            using var response = await _crxHttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            await using (var fileStream = new FileStream(crxPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fileStream);
            }

            var bytes = await File.ReadAllBytesAsync(crxPath);
            try { File.Delete(crxPath); } catch (Exception ex) { Log.Debug(ex, "Failed to delete CRX temp file"); }

            // uBlock comes from the store: its CRX3 signature and identity are
            // mandatory before anything is extracted.
            if (!Crx3Verifier.TryVerify(bytes, UblockFolderName, out var verification, out var verifyError))
            {
                Log.Debug("uBlock CRX verification failed: {Error}", verifyError);
                return false;
            }

            var zipStart = verification.ZipStart;

            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }
            Directory.CreateDirectory(targetDir);

            using var zipStream = new MemoryStream(bytes, zipStart, bytes.Length - zipStart);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var normalizedTarget = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                if (!destPath.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
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

            return File.Exists(Path.Combine(targetDir, "manifest.json"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "uBlock download failed");
            return false;
        }
    }

    private static string? ReadManifestVersion(string folder)
    {
        try
        {
            var manifestPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read uBlock manifest version");
            return null;
        }
    }

    private async void ShowExtensions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tab = new BrowserTab
            {
                Kind = BrowserTabKind.Settings,
                Title = GetNativePageTitle(BrowserTabKind.Settings),
                Address = GetNativePageAddress(BrowserTabKind.Settings) + "#Extensions"
            };
            await _tabOperationLock.WaitAsync();
            try { _tabs.Add(tab); }
            finally { _tabOperationLock.Release(); }
            await ActivateTabAsync(tab);
        }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private void CleanupOldBuiltinExtensions()
    {
        var builtinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cjpalhdlnbpafiamejdnhcphjbkeiagm",
            "eppiocemhmnlbhjplcgkofciiegomcon"
        };

        var extensions = _dataStore.Settings.Extensions;
        var ublockExt = extensions.FirstOrDefault(IsUblockExtension);
        var filtered = extensions
            .Where(e => !builtinFolders.Contains(Path.GetFileName(e.FolderPath.TrimEnd('\\', '/'))))
            .ToList();

        if (filtered.Count != extensions.Count || ublockExt is not null)
        {
            _dataStore.SaveSettings(_dataStore.Settings with
            {
                Extensions = filtered,
                UblockEnabled = ublockExt is null ? _dataStore.Settings.UblockEnabled : ublockExt.Enabled
            });
        }
    }

    private async Task LoadExtensionsAsync()
    {
        if (_extensionsLoaded || _profile is null)
        {
            return;
        }

        _extensionsLoaded = true;

        CleanupOldBuiltinExtensions();

        foreach (var ext in _dataStore.Settings.Extensions)
        {
            if (ext.Enabled)
            {
                await TryLoadExtensionAsync(ext);
            }
        }

        LogStartupPhase("extensions loaded");
    }

    private async Task<CoreWebView2BrowserExtension?> TryLoadExtensionAsync(ExtensionInfo ext)
    {
        try
        {
            if (!Directory.Exists(ext.FolderPath))
            {
                return null;
            }

            if (_profile is null) return null;
            var webExt = await _profile.AddBrowserExtensionAsync(ext.FolderPath);
            _loadedExtensions[webExt.Id] = webExt;

            if (!ext.Enabled)
            {
                await webExt.EnableAsync(false);
            }

            return webExt;
        }
        catch (Exception ex)
        {
            StatusBar.StatusText.Text = $"Failed to load extension '{ext.Name}': {ex.Message}";
            return null;
        }
    }
}
