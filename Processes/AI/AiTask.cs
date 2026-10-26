namespace TabloidBrowser;

public enum AiTaskStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed class AiTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Goal { get; init; } = string.Empty;

    public AiTaskStatus Status { get; internal set; } = AiTaskStatus.Running;

    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; internal set; }

    public void Complete()
    {
        if (Status != AiTaskStatus.Running) return;
        Status = AiTaskStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    public void Fail()
    {
        if (Status != AiTaskStatus.Running) return;
        Status = AiTaskStatus.Failed;
        CompletedAt = DateTime.UtcNow;
    }

    public void Cancel()
    {
        if (Status != AiTaskStatus.Running) return;
        Status = AiTaskStatus.Cancelled;
        CompletedAt = DateTime.UtcNow;
    }
}
