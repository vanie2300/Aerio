using Xunit;

namespace TabloidBrowser.Tests;

public class AiTaskTests
{
    [Fact]
    public void AiTask_HasUniqueId()
    {
        var a = new AiTask();
        var b = new AiTask();
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void AiTask_DefaultsToRunning()
    {
        var task = new AiTask();
        Assert.Equal(AiTaskStatus.Running, task.Status);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void AiTask_Complete_SetsStatusAndTimestamp()
    {
        var task = new AiTask();
        task.Complete();
        Assert.Equal(AiTaskStatus.Completed, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void AiTask_Fail_SetsStatusAndTimestamp()
    {
        var task = new AiTask();
        task.Fail();
        Assert.Equal(AiTaskStatus.Failed, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void AiTask_Cancel_SetsStatusAndTimestamp()
    {
        var task = new AiTask();
        task.Cancel();
        Assert.Equal(AiTaskStatus.Cancelled, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void AiTask_Goal_DefaultsToEmpty()
    {
        var task = new AiTask();
        Assert.Equal(string.Empty, task.Goal);
    }

    [Fact]
    public void AiTask_Goal_CanBeSet()
    {
        var task = new AiTask { Goal = "Research topic" };
        Assert.Equal("Research topic", task.Goal);
    }
}
