using Xunit;

namespace TabloidBrowser.Tests;

public class AiWorkspaceTests
{
    [Fact]
    public void AiWorkspace_Ctor_SetsTaskId()
    {
        var ws = new AiWorkspace("task-1");
        Assert.Equal("task-1", ws.TaskId);
    }

    [Fact]
    public void AiWorkspace_ActiveTabId_DefaultsToNull()
    {
        var ws = new AiWorkspace("task-1");
        Assert.Null(ws.ActiveTabId);
    }

    [Fact]
    public void AiWorkspace_TabIds_StartsEmpty()
    {
        var ws = new AiWorkspace("task-1");
        Assert.Empty(ws.TabIds);
    }

    [Fact]
    public void AiWorkspace_AddTab_AddsToCollection()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        Assert.Single(ws.TabIds);
        Assert.Equal("tab-1", ws.TabIds[0]);
    }

    [Fact]
    public void AiWorkspace_AddTab_NoDuplicates()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        ws.AddTab("tab-1");
        Assert.Single(ws.TabIds);
    }

    [Fact]
    public void AiWorkspace_RemoveTab_RemovesFromCollection()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        ws.RemoveTab("tab-1");
        Assert.Empty(ws.TabIds);
    }

    [Fact]
    public void AiWorkspace_RemoveTab_ClearsActiveTabId()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        ws.ActiveTabId = "tab-1";
        ws.RemoveTab("tab-1");
        Assert.Null(ws.ActiveTabId);
    }

    [Fact]
    public void AiWorkspace_RemoveTab_DoesNotClearOtherActiveTabId()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        ws.AddTab("tab-2");
        ws.ActiveTabId = "tab-2";
        ws.RemoveTab("tab-1");
        Assert.Equal("tab-2", ws.ActiveTabId);
    }

    [Fact]
    public void AiWorkspace_KeepTab_RemovesTabFromCollection()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        var kept = ws.KeepTab("tab-1");
        Assert.Empty(ws.TabIds);
        Assert.Single(kept);
        Assert.Equal("tab-1", kept[0]);
    }

    [Fact]
    public void AiWorkspace_KeepTab_ClearsActiveTabId()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        ws.ActiveTabId = "tab-1";
        ws.KeepTab("tab-1");
        Assert.Null(ws.ActiveTabId);
    }

    [Fact]
    public void AiWorkspace_KeepTab_ReturnsEmpty_WhenTabNotInCollection()
    {
        var ws = new AiWorkspace("task-1");
        var kept = ws.KeepTab("nonexistent");
        Assert.Empty(kept);
        Assert.Empty(ws.TabIds);
    }

    [Fact]
    public void AiWorkspace_KeepTab_DoesNotClearOtherActiveTabId()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        ws.AddTab("tab-2");
        ws.ActiveTabId = "tab-2";
        ws.KeepTab("tab-1");
        Assert.Equal("tab-2", ws.ActiveTabId);
    }

    [Fact]
    public void AiWorkspace_KeepAllTabs_ReturnsAllTabIds()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        ws.AddTab("tab-2");
        ws.AddTab("tab-3");
        var kept = ws.KeepAllTabs();
        Assert.Equal(3, kept.Length);
        Assert.Contains("tab-1", kept);
        Assert.Contains("tab-2", kept);
        Assert.Contains("tab-3", kept);
    }

    [Fact]
    public void AiWorkspace_KeepAllTabs_ClearsCollectionAndActiveTab()
    {
        var ws = new AiWorkspace("task-1");
        ws.AddTab("tab-1");
        ws.AddTab("tab-2");
        ws.ActiveTabId = "tab-2";
        ws.KeepAllTabs();
        Assert.Empty(ws.TabIds);
        Assert.Null(ws.ActiveTabId);
    }

    [Fact]
    public void AiWorkspace_KeepAllTabs_ReturnsEmpty_WhenNoTabs()
    {
        var ws = new AiWorkspace("task-1");
        var kept = ws.KeepAllTabs();
        Assert.Empty(kept);
    }
}

public class AiTaskContextTests
{
    [Fact]
    public void AiTaskContext_SetsTaskAndWorkspace()
    {
        var task = new AiTask { Goal = "test" };
        var ws = new AiWorkspace(task.Id);
        var ctx = new AiTaskContext(task, ws);

        Assert.Same(task, ctx.Task);
        Assert.Same(ws, ctx.Workspace);
    }
}
