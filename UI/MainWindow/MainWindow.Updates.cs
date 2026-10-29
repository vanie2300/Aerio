using System.IO;
using System.Net.Http;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace TabloidBrowser;

public partial class MainWindow
{
    private static readonly AerioUpdateService UpdateService = new();

    private async Task<AerioRelease?> CheckForAerioUpdatesAsync(
        Button checkButton,
        Button downloadButton,
        TextBlock status)
    {
        checkButton.IsEnabled = false;
        downloadButton.Visibility = Visibility.Collapsed;
        status.Text = "Checking GitHub for updates…";

        try
        {
            var latestRelease = await UpdateService.GetLatestReleaseAsync();
            if (latestRelease is null)
            {
                status.Text = "No Aerio releases have been published yet.";
                StatusBar.StatusText.Text = "No Aerio releases found";
                return null;
            }

            var installedVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (installedVersion is null)
            {
                throw new InvalidOperationException("The installed Aerio version could not be determined.");
            }

            if (latestRelease.Version.CompareTo(installedVersion) <= 0)
            {
                status.Text = $"Aerio is up to date (v{installedVersion.ToString(3)}).";
                StatusBar.StatusText.Text = "Aerio is up to date";
                return null;
            }

            status.Text = $"Aerio v{latestRelease.Version} is available.";
            downloadButton.Visibility = Visibility.Visible;
            StatusBar.StatusText.Text = $"Aerio v{latestRelease.Version} update available";
            return latestRelease;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   InvalidDataException or JsonException or InvalidOperationException)
        {
            Log.Error(ex, "Failed to check for Aerio updates");
            status.Text = "Couldn't check for updates. Check your connection and try again.";
            StatusBar.StatusText.Text = "Couldn't check for Aerio updates";
            return null;
        }
        finally
        {
            checkButton.IsEnabled = true;
        }
    }

    private async Task DownloadAerioUpdateAsync(
        AerioRelease release,
        Button checkButton,
        Button downloadButton,
        TextBlock status)
    {
        checkButton.IsEnabled = false;
        downloadButton.IsEnabled = false;
        status.Text = "Downloading the installer…";

        var installerPath = Path.Combine(
            Path.GetTempPath(),
            "Aerio",
            "Updates",
            $"Aerio-Setup-win-x64-v{release.Version}.exe");
        var progress = new Progress<AerioDownloadProgress>(downloadProgress =>
        {
            if (downloadProgress.TotalBytes is > 0)
            {
                var percentage = (int)(downloadProgress.BytesReceived * 100 / downloadProgress.TotalBytes.Value);
                status.Text = $"Downloading the installer… {percentage}%";
            }
        });

        try
        {
            await UpdateService.DownloadInstallerAsync(release, installerPath, progress);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error(ex, "Failed to download Aerio update");
            status.Text = "Couldn't download the installer. Check your connection and try again.";
            StatusBar.StatusText.Text = "Couldn't download Aerio update";
            checkButton.IsEnabled = true;
            downloadButton.IsEnabled = true;
            return;
        }

        status.Text = "Installer downloaded.";
        StatusBar.StatusText.Text = $"Aerio v{release.Version} installer downloaded";
        var result = MessageBox.Show(
            this,
            "The update installer has been downloaded. Run it now? Aerio will close while the update is installed.",
            "Install Aerio update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var installerProcess = Process.Start(new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true
                });
                if (installerProcess is null)
                {
                    throw new InvalidOperationException("The update installer could not be started.");
                }

                Application.Current.Shutdown();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                Log.Error(ex, "Failed to start Aerio update installer");
                status.Text = "Installer downloaded, but Aerio couldn't start it.";
                StatusBar.StatusText.Text = "Couldn't start Aerio update installer";
            }
        }
        else
        {
            status.Text = $"Installer saved to {installerPath}";
        }

        checkButton.IsEnabled = true;
        downloadButton.IsEnabled = true;
    }
}
