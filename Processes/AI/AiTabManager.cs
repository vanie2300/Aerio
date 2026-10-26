namespace TabloidBrowser;

public sealed class AiTabManager
{
    private readonly IList<BrowserTab> _allTabs;
    private readonly Func<BrowserTab, string, Task> _navigateToUrl;
    private readonly Func<Task<BrowserTab>> _openBackgroundTab;
    private readonly Func<BrowserTab, Task> _activateTabAsync;
    private readonly SemaphoreSlim _tabLock;

    public AiTabManager(
        IList<BrowserTab> allTabs,
        Func<BrowserTab, string, Task> navigateToUrl,
        Func<Task<BrowserTab>> openBackgroundTab,
        Func<BrowserTab, Task> activateTabAsync,
        SemaphoreSlim tabLock)
    {
        _allTabs = allTabs;
        _navigateToUrl = navigateToUrl;
        _openBackgroundTab = openBackgroundTab;
        _activateTabAsync = activateTabAsync;
        _tabLock = tabLock;
    }

    public async Task<BrowserTab?> FindTabAsync(string tabId)
    {
        await _tabLock.WaitAsync();
        try
        {
            return _allTabs.FirstOrDefault(t => t.Id == tabId);
        }
        finally { _tabLock.Release(); }
    }

    public async Task<BrowserTab> CreateAiTabAsync(string taskId, string? url = null, bool activate = false)
    {
        var tab = await _openBackgroundTab();
        await _tabLock.WaitAsync();
        try
        {
            tab.Owner = TabOwner.AI;
            tab.AiTaskId = taskId;
        }
        finally { _tabLock.Release(); }

        if (url != null)
        {
            await _navigateToUrl(tab, url);
        }

        if (activate)
        {
            await _activateTabAsync(tab);
        }

        return tab;
    }

    public async Task<bool> CloseAiTabAsync(string tabId, string taskId)
    {
        await _tabLock.WaitAsync();
        try
        {
            var tab = _allTabs.FirstOrDefault(t => t.Id == tabId);
            if (tab == null || tab.AiTaskId != taskId)
                return false;

            _allTabs.Remove(tab);
            return true;
        }
        finally { _tabLock.Release(); }
    }

    public async Task<List<BrowserTab>> GetAiTabsAsync(string? taskId = null)
    {
        await _tabLock.WaitAsync();
        try
        {
            return _allTabs
                .Where(t => t.Owner == TabOwner.AI && (taskId == null || t.AiTaskId == taskId))
                .ToList();
        }
        finally { _tabLock.Release(); }
    }

    public async Task<bool> NavigateToAsync(string tabId, string taskId, string url)
    {
        BrowserTab? tab;
        await _tabLock.WaitAsync();
        try
        {
            tab = _allTabs.FirstOrDefault(t => t.Id == tabId);
            if (tab == null || tab.AiTaskId != taskId)
                return false;
        }
        finally { _tabLock.Release(); }

        await _navigateToUrl(tab, url);
        return true;
    }

    public async Task<bool> ActivateTabAsync(string tabId, string taskId)
    {
        BrowserTab? tab;
        await _tabLock.WaitAsync();
        try
        {
            tab = _allTabs.FirstOrDefault(t => t.Id == tabId);
            if (tab == null || tab.AiTaskId != taskId)
                return false;
        }
        finally { _tabLock.Release(); }

        await _activateTabAsync(tab);
        return true;
    }

    public async Task<bool> KeepTabAsync(string tabId, string taskId)
    {
        await _tabLock.WaitAsync();
        try
        {
            var tab = _allTabs.FirstOrDefault(t => t.Id == tabId);
            if (tab == null || tab.AiTaskId != taskId || tab.Owner != TabOwner.AI)
                return false;

            tab.Owner = TabOwner.User;
            tab.AiTaskId = null;
            return true;
        }
        finally { _tabLock.Release(); }
    }

    public async Task<bool> KeepTabAsync(string tabId)
    {
        await _tabLock.WaitAsync();
        try
        {
            var tab = _allTabs.FirstOrDefault(t => t.Id == tabId);
            if (tab == null || tab.Owner != TabOwner.AI)
                return false;

            tab.Owner = TabOwner.User;
            tab.AiTaskId = null;
            return true;
        }
        finally { _tabLock.Release(); }
    }

    public async Task<List<string>> KeepAllTabsAsync(string taskId)
    {
        await _tabLock.WaitAsync();
        try
        {
            var kept = new List<string>();
            foreach (var tab in _allTabs.Where(t => t.Owner == TabOwner.AI && t.AiTaskId == taskId).ToList())
            {
                tab.Owner = TabOwner.User;
                tab.AiTaskId = null;
                kept.Add(tab.Id);
            }
            return kept;
        }
        finally { _tabLock.Release(); }
    }
}
