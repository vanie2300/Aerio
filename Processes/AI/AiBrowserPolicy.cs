namespace TabloidBrowser;

public sealed record AiBrowserPolicy(
    int MaxOpenTabs = 3,
    bool AllowNavigateActiveTab = false,
    bool CleanupOnTaskComplete = false
)
{
    public static readonly AiBrowserPolicy Default = new();
}
