namespace TabloidBrowser;

public sealed record ExtensionInfo(
    string Id,
    string Name,
    string FolderPath,
    bool Enabled,
    bool IsBuiltIn);
