namespace TabloidBrowser;

public sealed class AiWorkspace
{
    public string TaskId { get; }

    public string? ActiveTabId { get; set; }

    private readonly List<string> _tabIds = [];
    private readonly object _lock = new();
    public IReadOnlyList<string> TabIds
    {
        get { lock (_lock) { return _tabIds.ToArray(); } }
    }

    public AiWorkspace(string taskId)
    {
        TaskId = taskId;
    }

    public void AddTab(string tabId)
    {
        lock (_lock)
        {
            if (!_tabIds.Contains(tabId))
                _tabIds.Add(tabId);
        }
    }

    public void RemoveTab(string tabId)
    {
        lock (_lock)
        {
            _tabIds.Remove(tabId);
            if (ActiveTabId == tabId)
                ActiveTabId = null;
        }
    }

    public string[] KeepTab(string tabId)
    {
        lock (_lock)
        {
            if (!_tabIds.Contains(tabId))
                return [];

            var kept = new[] { tabId };
            _tabIds.Remove(tabId);
            if (ActiveTabId == tabId)
                ActiveTabId = null;
            return kept;
        }
    }

    public string[] KeepAllTabs()
    {
        lock (_lock)
        {
            if (_tabIds.Count == 0)
                return [];

            var kept = _tabIds.ToArray();
            _tabIds.Clear();
            ActiveTabId = null;
            return kept;
        }
    }
}
