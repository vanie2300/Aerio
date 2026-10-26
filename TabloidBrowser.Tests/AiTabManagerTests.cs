using Xunit;

namespace TabloidBrowser.Tests;

public class AiTabManagerTests
{
    private (AiTabManager manager, List<BrowserTab> tabs) CreateManager()
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

        var manager = new AiTabManager(tabs, DummyNavigate, DummyOpen, DummyActivate, lockSlim);
        return (manager, tabs);
    }

    [Fact]
    public async Task FindTab_ReturnsNull_WhenNotFound()
    {
        var (manager, _) = CreateManager();
        Assert.Null(await manager.FindTabAsync("nonexistent"));
    }

    [Fact]
    public async Task FindTab_ReturnsNull_WhenDifferentTab()
    {
        var (manager, _) = CreateManager();
        var tab = new BrowserTab();
        Assert.Null(await manager.FindTabAsync(tab.Id));
    }

    [Fact]
    public async Task CreateAiTabAsync_SetsOwnerAndTaskId()
    {
        var (manager, tabs) = CreateManager();
        var tab = await manager.CreateAiTabAsync("task-1");
        Assert.Equal(TabOwner.AI, tab.Owner);
        Assert.Equal("task-1", tab.AiTaskId);
        Assert.Single(tabs);
    }

    [Fact]
    public async Task CreateAiTabAsync_FindsCreatedTab()
    {
        var (manager, _) = CreateManager();
        var tab = await manager.CreateAiTabAsync("task-1");
        Assert.Same(tab, await manager.FindTabAsync(tab.Id));
    }

    [Fact]
    public async Task GetAiTabs_ReturnsOnlyAiOwnedTabs()
    {
        var (manager, _) = CreateManager();
        await manager.CreateAiTabAsync("task-1");
        await manager.CreateAiTabAsync("task-1");
        var aiTabs = await manager.GetAiTabsAsync();
        Assert.Equal(2, aiTabs.Count);
        Assert.All(aiTabs, t => Assert.Equal(TabOwner.AI, t.Owner));
    }

    [Fact]
    public async Task GetAiTabs_FiltersByTaskId()
    {
        var (manager, _) = CreateManager();
        await manager.CreateAiTabAsync("task-1");
        await manager.CreateAiTabAsync("task-2");
        Assert.Single(await manager.GetAiTabsAsync("task-1"));
        Assert.Single(await manager.GetAiTabsAsync("task-2"));
    }

    [Fact]
    public async Task CloseAiTab_RemovesTab_WhenOwnershipMatches()
    {
        var (manager, tabs) = CreateManager();
        var tab = await manager.CreateAiTabAsync("task-1");
        var result = await manager.CloseAiTabAsync(tab.Id, "task-1");
        Assert.True(result);
        Assert.Empty(tabs);
    }

    [Fact]
    public async Task CloseAiTab_Rejects_WhenTaskIdMismatch()
    {
        var (manager, tabs) = CreateManager();
        var tab = await manager.CreateAiTabAsync("task-1");
        var result = await manager.CloseAiTabAsync(tab.Id, "task-wrong");
        Assert.False(result);
        Assert.Single(tabs);
    }

    [Fact]
    public async Task CloseAiTab_Rejects_WhenTabNotFound()
    {
        var (manager, _) = CreateManager();
        var result = await manager.CloseAiTabAsync("nonexistent", "task-1");
        Assert.False(result);
    }

    private (AiTabManager manager, List<BrowserTab> tabs, List<(BrowserTab tab, string url)> navigated) CreateManagerWithTracking()
    {
        var tabs = new List<BrowserTab>();
        var navigated = new List<(BrowserTab, string)>();
        var lockSlim = new SemaphoreSlim(1, 1);
        BrowserTab? activated = null;

        Task Navigate(BrowserTab tab, string url)
        {
            navigated.Add((tab, url));
            return Task.CompletedTask;
        }
        Task<BrowserTab> Open()
        {
            var tab = new BrowserTab();
            tabs.Add(tab);
            return Task.FromResult(tab);
        }
        Task Activate(BrowserTab tab)
        {
            activated = tab;
            return Task.CompletedTask;
        }

        var manager = new AiTabManager(tabs, Navigate, Open, Activate, lockSlim);
        return (manager, tabs, navigated);
    }

    [Fact]
    public async Task NavigateToAsync_NavigatesToUrl_WhenOwnershipMatches()
    {
        var (manager, tabs, navigated) = CreateManagerWithTracking();
        var tab = await manager.CreateAiTabAsync("task-1");
        var result = await manager.NavigateToAsync(tab.Id, "task-1", "https://example.com");
        Assert.True(result);
        Assert.Single(navigated);
        Assert.Equal("https://example.com", navigated[0].url);
    }

    [Fact]
    public async Task NavigateToAsync_Rejects_WhenTaskIdMismatch()
    {
        var (manager, tabs, navigated) = CreateManagerWithTracking();
        var tab = await manager.CreateAiTabAsync("task-1");
        var result = await manager.NavigateToAsync(tab.Id, "task-wrong", "https://example.com");
        Assert.False(result);
        Assert.Empty(navigated);
    }

    [Fact]
    public async Task NavigateToAsync_Rejects_WhenTabNotFound()
    {
        var (manager, _, navigated) = CreateManagerWithTracking();
        var result = await manager.NavigateToAsync("nonexistent", "task-1", "https://example.com");
        Assert.False(result);
        Assert.Empty(navigated);
    }

    [Fact]
    public async Task ActivateTabAsync_Activates_WhenOwnershipMatches()
    {
        var (manager, tabs, _) = CreateManagerWithTracking();
        var tab = await manager.CreateAiTabAsync("task-1");
        var result = await manager.ActivateTabAsync(tab.Id, "task-1");
        Assert.True(result);
    }

    [Fact]
    public async Task ActivateTabAsync_Rejects_WhenTaskIdMismatch()
    {
        var (manager, tabs, _) = CreateManagerWithTracking();
        var tab = await manager.CreateAiTabAsync("task-1");
        var result = await manager.ActivateTabAsync(tab.Id, "task-wrong");
        Assert.False(result);
    }

    [Fact]
    public async Task ActivateTabAsync_Rejects_WhenTabNotFound()
    {
        var (manager, _, _) = CreateManagerWithTracking();
        var result = await manager.ActivateTabAsync("nonexistent", "task-1");
        Assert.False(result);
    }

    [Fact]
    public async Task KeepTab_TransfersOwnershipToUser()
    {
        var (manager, tabs) = CreateManager();
        var tab = await manager.CreateAiTabAsync("task-1");
        Assert.Equal(TabOwner.AI, tab.Owner);

        var result = await manager.KeepTabAsync(tab.Id, "task-1");
        Assert.True(result);
        Assert.Equal(TabOwner.User, tab.Owner);
        Assert.Null(tab.AiTaskId);
    }

    [Fact]
    public async Task KeepTab_Rejects_WhenTabNotFound()
    {
        var (manager, _) = CreateManager();
        var result = await manager.KeepTabAsync("nonexistent", "task-1");
        Assert.False(result);
    }

    [Fact]
    public async Task KeepTab_Rejects_WhenTaskIdMismatch()
    {
        var (manager, tabs) = CreateManager();
        var tab = await manager.CreateAiTabAsync("task-1");
        var result = await manager.KeepTabAsync(tab.Id, "task-wrong");
        Assert.False(result);
        Assert.Equal(TabOwner.AI, tab.Owner);
    }

    [Fact]
    public async Task KeepTab_Rejects_WhenAlreadyUserOwned()
    {
        var (manager, tabs) = CreateManager();
        var tab = new BrowserTab { Owner = TabOwner.User, AiTaskId = "task-1" };
        tabs.Add(tab);
        var result = await manager.KeepTabAsync(tab.Id, "task-1");
        Assert.False(result);
    }

    [Fact]
    public async Task KeepTab_RemovesFromAiTabsList()
    {
        var (manager, tabs) = CreateManager();
        var tab = await manager.CreateAiTabAsync("task-1");
        Assert.Single(await manager.GetAiTabsAsync());

        await manager.KeepTabAsync(tab.Id, "task-1");
        Assert.Empty(await manager.GetAiTabsAsync());
    }

    [Fact]
    public async Task KeepTab_WithoutTaskId_KeepsAiTabRegardlessOfOwningTask()
    {
        var (manager, _) = CreateManager();
        var tab = await manager.CreateAiTabAsync("task-1");

        var result = await manager.KeepTabAsync(tab.Id);

        Assert.True(result);
        Assert.Equal(TabOwner.User, tab.Owner);
        Assert.Null(tab.AiTaskId);
    }

    [Fact]
    public async Task KeepTab_WithoutTaskId_RejectsUserOwnedTab()
    {
        var (manager, tabs) = CreateManager();
        var tab = new BrowserTab { Owner = TabOwner.User };
        tabs.Add(tab);

        var result = await manager.KeepTabAsync(tab.Id);

        Assert.False(result);
    }

    [Fact]
    public async Task KeepTab_WithoutTaskId_RejectsMissingTab()
    {
        var (manager, _) = CreateManager();
        var result = await manager.KeepTabAsync("nonexistent");
        Assert.False(result);
    }

    [Fact]
    public async Task KeepAllTabs_TransfersAllOwnership()
    {
        var (manager, tabs) = CreateManager();
        await manager.CreateAiTabAsync("task-1");
        await manager.CreateAiTabAsync("task-1");
        Assert.Equal(2, (await manager.GetAiTabsAsync()).Count);

        var kept = await manager.KeepAllTabsAsync("task-1");
        Assert.Equal(2, kept.Count);
        Assert.Empty(await manager.GetAiTabsAsync());
        Assert.All(tabs, t => Assert.Equal(TabOwner.User, t.Owner));
    }

    [Fact]
    public async Task KeepAllTabs_ReturnsEmpty_WhenNoAiTabs()
    {
        var (manager, _) = CreateManager();
        var kept = await manager.KeepAllTabsAsync("task-1");
        Assert.Empty(kept);
    }
}
