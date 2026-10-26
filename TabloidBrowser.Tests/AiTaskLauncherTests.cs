using Xunit;

namespace TabloidBrowser.Tests;

public class AiTaskLauncherTests
{
    private (AiTaskLauncher launcher, AiTabManager tabManager, List<BrowserTab> tabs) CreateLauncher(int maxTabs = 3)
    {
        var tabs = new List<BrowserTab>();
        var lockSlim = new SemaphoreSlim(1, 1);
        Task DummyNavigate(BrowserTab tab, string url) => Task.CompletedTask;
        Task<BrowserTab> DummyOpen()
        {
            var tab = new BrowserTab();
            tabs.Add(tab);
            return Task.FromResult(tab);
        }
        Task DummyActivate(BrowserTab tab) => Task.CompletedTask;
        Task DummyClose(BrowserTab tab) { tabs.Remove(tab); return Task.CompletedTask; }

        var tabManager = new AiTabManager(tabs, DummyNavigate, DummyOpen, DummyActivate, lockSlim);
        var policy = new AiBrowserPolicy(MaxOpenTabs: maxTabs);
        var launcher = new AiTaskLauncher(tabManager, policy, DummyClose);
        return (launcher, tabManager, tabs);
    }

    [Fact]
    public async Task LaunchAsync_CreatesTaskAndWorkspace()
    {
        var (launcher, _, _) = CreateLauncher();
        var context = await launcher.LaunchAsync("test goal");
        Assert.Equal("test goal", context.Task.Goal);
        Assert.Equal(context.Task.Id, context.Workspace.TaskId);
    }

    [Fact]
    public async Task LaunchAsync_TaskStartsRunning()
    {
        var (launcher, _, _) = CreateLauncher();
        var context = await launcher.LaunchAsync("goal");
        Assert.Equal(AiTaskStatus.Running, context.Task.Status);
    }

    [Fact]
    public async Task CreateTabAsync_CreatesAiOwnedTab()
    {
        var (launcher, _, tabs) = CreateLauncher();
        var context = await launcher.LaunchAsync("goal");
        var tab = await launcher.CreateTabAsync(context);
        Assert.Equal(TabOwner.AI, tab.Owner);
        Assert.Equal(context.Task.Id, tab.AiTaskId);
        Assert.Single(tabs);
        Assert.Contains(tab.Id, context.Workspace.TabIds);
    }

    [Fact]
    public async Task CreateTabAsync_SetsAiStateToLoading()
    {
        var (launcher, _, _) = CreateLauncher();
        var context = await launcher.LaunchAsync("goal");
        var tab = await launcher.CreateTabAsync(context);
        Assert.Equal(AiTaskState.Loading, tab.AiState);
    }

    [Fact]
    public async Task CreateTabAsync_EnforcesMaxTabLimit()
    {
        var (launcher, _, tabs) = CreateLauncher(maxTabs: 2);
        var context = await launcher.LaunchAsync("goal");
        await launcher.CreateTabAsync(context);
        await launcher.CreateTabAsync(context);
        await launcher.CreateTabAsync(context);
        Assert.Equal(2, tabs.Count);
    }

    [Fact]
    public async Task CreateTabAsync_ClosesOldestWhenLimitExceeded()
    {
        var (launcher, tabManager, tabs) = CreateLauncher(maxTabs: 2);
        var context = await launcher.LaunchAsync("goal");
        var tab1 = await launcher.CreateTabAsync(context);
        var tab2 = await launcher.CreateTabAsync(context);
        var tab3 = await launcher.CreateTabAsync(context);
        Assert.DoesNotContain(tab1, tabs);
        Assert.Contains(tab2, tabs);
        Assert.Contains(tab3, tabs);
    }

    [Fact]
    public async Task CreateTabAsync_EvictionUsesCloseDelegate()
    {
        var closed = new List<BrowserTab>();
        var tabs = new List<BrowserTab>();
        var lockSlim = new SemaphoreSlim(1, 1);
        Task Navigate(BrowserTab tab, string url) => Task.CompletedTask;
        Task<BrowserTab> Open()
        {
            var tab = new BrowserTab();
            tabs.Add(tab);
            return Task.FromResult(tab);
        }
        Task Activate(BrowserTab tab) => Task.CompletedTask;
        Task Close(BrowserTab tab) { tabs.Remove(tab); closed.Add(tab); return Task.CompletedTask; }

        var tabManager = new AiTabManager(tabs, Navigate, Open, Activate, lockSlim);
        var policy = new AiBrowserPolicy(MaxOpenTabs: 2);
        var launcher = new AiTaskLauncher(tabManager, policy, Close);

        var context = await launcher.LaunchAsync("goal");
        var tab1 = await launcher.CreateTabAsync(context);
        var tab2 = await launcher.CreateTabAsync(context);
        var tab3 = await launcher.CreateTabAsync(context);

        Assert.Single(closed);
        Assert.Equal(tab1.Id, closed[0].Id);
        Assert.Contains(tab2.Id, context.Workspace.TabIds);
        Assert.Contains(tab3.Id, context.Workspace.TabIds);
        Assert.DoesNotContain(tab1.Id, context.Workspace.TabIds);
    }

    [Fact]
    public async Task CreateTabAsync_WithUrl_Navigates()
    {
        BrowserTab? navigatedTab = null;
        string? navigatedUrl = null;

        var tabs = new List<BrowserTab>();
        var lockSlim = new SemaphoreSlim(1, 1);
        Task Navigate(BrowserTab tab, string url)
        {
            navigatedTab = tab;
            navigatedUrl = url;
            return Task.CompletedTask;
        }
        Task<BrowserTab> Open()
        {
            var tab = new BrowserTab();
            tabs.Add(tab);
            return Task.FromResult(tab);
        }
        Task Activate(BrowserTab tab) => Task.CompletedTask;
        Task Close(BrowserTab tab) { tabs.Remove(tab); return Task.CompletedTask; }

        var tabManager = new AiTabManager(tabs, Navigate, Open, Activate, lockSlim);
        var policy = new AiBrowserPolicy();
        var launcher = new AiTaskLauncher(tabManager, policy, Close);
        var context = await launcher.LaunchAsync("goal");
        var tab = await launcher.CreateTabAsync(context, "https://example.com");

        Assert.Equal("https://example.com", navigatedUrl);
        Assert.Same(tab, navigatedTab);
    }
}
