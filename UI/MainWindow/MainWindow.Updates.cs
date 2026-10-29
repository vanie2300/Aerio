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

    private async Task CheckForAerioUpdatesAsync(Button checkButton, TextBlock status)
    {
        checkButton.IsEnabled = false;
        status.Text = "Checking GitHub for updates…";

        try
        {
            var latestRelease = await UpdateService.GetLatestReleaseAsync();
            if (latestRelease is null)
            {
                status.Text = "No Aerio releases have been published yet.";
                StatusBar.StatusText.Text = "No Aerio releases found";
                return;
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
                return;
            }

            status.Text = $"Aerio v{latestRelease.Version} is available. Opening the release page…";
            Process.Start(new ProcessStartInfo(latestRelease.ReleasePageUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            StatusBar.StatusText.Text = $"Aerio v{latestRelease.Version} update available";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   InvalidDataException or JsonException or Win32Exception or InvalidOperationException)
        {
            Log.Error(ex, "Failed to check for Aerio updates");
            status.Text = "Couldn't check for updates. Check your connection and try again.";
            StatusBar.StatusText.Text = "Couldn't check for Aerio updates";
        }
        finally
        {
            checkButton.IsEnabled = true;
        }
    }
}
