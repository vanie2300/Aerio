using System.Text.RegularExpressions;

namespace TabloidBrowser;

public static class ContentSanitizer
{
    private static readonly string[] InjectionPatterns =
    [
        @"(?i)ignore\s+(all\s+)?(previous|prior|above|earlier|preceding)\s+(instructions|prompts|rules|guidelines|directives)",
        @"(?i)you\s+are\s+now\s+(a|an)\s+",
        @"(?i)system\s*:\s*",
        @"(?i)new\s+instructions?\s*:",
        @"(?i)disregard\s+(all\s+)?(previous|prior|above|earlier)",
        @"(?i)forget\s+(everything|all|your)\s+",
        @"(?i)override\s+(your|the|all)\s+(instructions|rules|guidelines)",
        @"(?i)act\s+as\s+if\s+you\s+(have\s+)?(no|don't\s+have|do\s+not\s+have)\s+",
        @"(?i)your\s+new\s+(role|persona|instruction|task)\s+is",
        @"(?i)from\s+now\s+on\s+you\s+(will|must|should|are)",
        @"(?i)do\s+not\s+(follow|obey|listen\s+to)\s+",
        @"(?i)ignore\s+the\s+(system\s+)?prompt",
        @"(?i)\[system\]",
        @"(?i)<\s*system\s*>",
        @"(?i)IMPORTANT\s*:\s*you\s+(must|should|will|are)",
        @"(?i)CRITICAL\s*:\s*disregard",
        @"(?i)SECURITY\s*:\s*ignore",
        @"(?i)ADMIN\s*:\s*override",
        @"(?i)DEBUG\s*:\s*new\s+mode",
        @"(?i)you\s+are\s+being\s+(tested|evaluated)\s+by",
        @"(?i)the\s+previous\s+(message|instruction|content)\s+was\s+(a\s+)?(test|fake|decoy)",
    ];

    private static readonly Regex InjectionRegex = new(
        string.Join("|", InjectionPatterns),
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Sanitize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var sanitized = InjectionRegex.Replace(content, match =>
        {
            var redacted = new string('*', match.Value.Length);
            return redacted;
        });

        sanitized = sanitized
            .Replace("<page_content>", "&lt;page_content&gt;")
            .Replace("</page_content>", "&lt;/page_content&gt;")
            .Replace("<system>", "&lt;system&gt;")
            .Replace("</system>", "&lt;/system&gt;");

        return sanitized;
    }

    public static bool ContainsSuspiciousPatterns(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return InjectionRegex.IsMatch(content);
    }
}
