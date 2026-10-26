namespace TabloidBrowser;

public sealed class AiAuthorizer
{
    private readonly AiBrowserPolicy _policy;
    private readonly Func<BrowserTab?> _getActiveTab;

    public AiAuthorizer(AiBrowserPolicy policy, Func<BrowserTab?> getActiveTab)
    {
        _policy = policy;
        _getActiveTab = getActiveTab;
    }

    public AuthorizationResult Authorize(string toolName, BrowserTab? targetTab, bool requiresWrite)
    {
        if (!requiresWrite)
            return AuthorizationResult.Allowed();

        if (targetTab == null)
            return AuthorizationResult.Denied("No target tab available for this operation.");

        var activeTab = _getActiveTab();
        bool isActiveTab = targetTab == activeTab;

        if (isActiveTab && targetTab.Owner == TabOwner.User && !_policy.AllowNavigateActiveTab)
        {
            return AuthorizationResult.Denied(
                "Cannot modify the user's active tab. Use open_tab to create a new AI-owned tab instead.");
        }

        if (!isActiveTab && targetTab.Owner == TabOwner.User)
        {
            return AuthorizationResult.Denied(
                "Cannot modify a tab owned by the user. Only AI-owned tabs can be modified.");
        }

        return AuthorizationResult.Allowed();
    }
}

public sealed record AuthorizationResult(bool IsAllowed, string? DenialReason = null)
{
    public static AuthorizationResult Allowed() => new(true);
    public static AuthorizationResult Denied(string reason) => new(false, reason);
}
