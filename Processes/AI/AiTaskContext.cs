namespace TabloidBrowser;

public sealed class AiTaskContext
{
    public AiTask Task { get; }

    public AiWorkspace Workspace { get; }

    public AiTaskContext(AiTask task, AiWorkspace workspace)
    {
        Task = task;
        Workspace = workspace;
    }
}
