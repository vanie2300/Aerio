namespace TabloidBrowser;

public sealed class AiTaskLauncher
{
    private readonly AiTabManager _tabManager;
    private readonly AiBrowserPolicy _policy;
    private readonly Func<BrowserTab, Task> _closeTabAsync;

    public AiTaskLauncher(AiTabManager tabManager, AiBrowserPolicy policy, Func<BrowserTab, Task> closeTabAsync)
    {
        _tabManager = tabManager;
        _policy = policy;
        _closeTabAsync = closeTabAsync;
    }

    public Task<AiTaskContext> LaunchAsync(string goal)
    {
        var task = new AiTask { Goal = goal };
        var workspace = new AiWorkspace(task.Id);
        var context = new AiTaskContext(task, workspace);
        return Task.FromResult(context);
    }

    public async Task<BrowserTab> CreateTabAsync(AiTaskContext context, string? url = null, bool activate = false)
    {
        var existingAiTabs = await _tabManager.GetAiTabsAsync(context.Task.Id);
        if (existingAiTabs.Count >= _policy.MaxOpenTabs)
        {
            var oldest = existingAiTabs[0];
            context.Workspace.RemoveTab(oldest.Id);
            await _closeTabAsync(oldest);
        }

        var tab = await _tabManager.CreateAiTabAsync(context.Task.Id, url, activate);
        context.Workspace.AddTab(tab.Id);
        tab.AiState = AiTaskState.Loading;
        return tab;
    }
}
