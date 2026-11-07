using Xunit;

namespace TabloidBrowser.Tests;

public class DownloadShelfItemTests
{
    [Fact]
    public void Progress_WithKnownTotal_ComputesPercentage()
    {
        var item = new DownloadShelfItem("file.zip", "https://example.com/file.zip", 1000);

        item.UpdateProgress(250, 1000);

        Assert.Equal(25, item.ProgressPercent);
        Assert.False(item.IsIndeterminate);
        Assert.True(item.IsRunning);
    }

    [Fact]
    public void Progress_WithoutTotal_IsIndeterminate()
    {
        var item = new DownloadShelfItem("file.zip", "https://example.com/file.zip", null);

        item.UpdateProgress(2048, null);

        Assert.True(item.IsIndeterminate);
        Assert.Equal(0, item.ProgressPercent);
        Assert.Equal("2.0 KB", item.SizeText);
    }

    [Fact]
    public void Progress_ReceivedBeyondTotal_ClampsToFull()
    {
        var item = new DownloadShelfItem("file.zip", "https://example.com/file.zip", 100);

        item.UpdateProgress(500, 100);

        Assert.Equal(100, item.ProgressPercent);
    }

    [Fact]
    public void Progress_NegativeCounts_AreSane()
    {
        var item = new DownloadShelfItem("file.zip", "https://example.com/file.zip", 0);

        item.UpdateProgress(-5, -1);

        Assert.Equal(0, item.BytesReceived);
        Assert.Equal(0, item.ProgressPercent);
        Assert.True(item.IsIndeterminate);
    }

    [Fact]
    public void Complete_SetsCompletedStateAndFilePath()
    {
        var item = new DownloadShelfItem("file.zip", "https://example.com/file.zip", 500);

        item.UpdateProgress(400, 500);
        item.Complete(@"C:\downloads\file.zip");

        Assert.Equal(DownloadShelfState.Completed, item.State);
        Assert.False(item.IsRunning);
        Assert.Equal("Completed", item.StateText);
        Assert.Equal(100, item.ProgressPercent);
        Assert.Equal(@"C:\downloads\file.zip", item.FilePath);
    }

    [Fact]
    public void Interrupt_SetsInterruptedState()
    {
        var item = new DownloadShelfItem("file.zip", "https://example.com/file.zip", 500);

        item.Interrupt();

        Assert.Equal(DownloadShelfState.Interrupted, item.State);
        Assert.False(item.IsRunning);
        Assert.Equal("Interrupted", item.StateText);
    }

    [Fact]
    public void UpdateProgress_RaisesPropertyChanged()
    {
        var item = new DownloadShelfItem("file.zip", "https://example.com/file.zip", 100);
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.UpdateProgress(50, 100);

        Assert.Contains(nameof(DownloadShelfItem.BytesReceived), raised);
        Assert.Contains(nameof(DownloadShelfItem.ProgressPercent), raised);
        Assert.Contains(nameof(DownloadShelfItem.SizeText), raised);
    }

    [Fact]
    public void BlankFileName_UsesFallback()
    {
        var item = new DownloadShelfItem("   ", "https://example.com", null);

        Assert.Equal("download", item.FileName);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(2621440, "2.5 MB")]
    [InlineData(1073741824, "1.0 GB")]
    public void FormatBytes_UsesReadableUnits(long bytes, string expected)
    {
        Assert.Equal(expected, DownloadShelfItem.FormatBytes(bytes));
    }

    [Fact]
    public void FormatBytes_Negative_IsTreatedAsZero()
    {
        Assert.Equal("0 B", DownloadShelfItem.FormatBytes(-100));
    }

    [Fact]
    public void SizeText_WithTotal_ShowsBoth()
    {
        var item = new DownloadShelfItem("file.zip", "https://example.com/file.zip", 1048576);

        item.UpdateProgress(524288, 1048576);

        Assert.Equal("512.0 KB of 1.0 MB", item.SizeText);
    }
}
