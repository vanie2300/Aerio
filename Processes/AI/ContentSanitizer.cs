using System.Text.RegularExpressions;

namespace TabloidBrowser;

public static class ContentSanitizer
{
    private static readonly string[] InjectionPatterns =
    [
        @"(?i)ignore\s+(all\s+)?(previous|prior|above|earlier|preceding)\s+(instructions|prompts|rules|guidelines|directives)",
        @"(?i)you\s+are\s+now\s+(a|an)\s+",
        @"(?i)^\s*system\s*:\s*",
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
        // French
        @"(?i)ignore\s+(toutes?\s+)?(les\s+)?(instructions|consignes|règles|directives)\s+(précédentes?|anterieures?|ci-dessus)",
        @"(?i)tu\s+es\s+maintenant\s+(un|une)\s+",
        @"(?i)dé\s?si\s?glarder\s+(les\s+)?(instructions|précédent)",
        @"(?i)oublie\s+(tout|tout\s+ce|toutes\s+tes)\s+",
        @"(?i)de\s+nouvelles?\s+(instructions|règles)\s*:",
        @"(?i)ignore\s+le\s+prompt\s+système",
        // Spanish
        @"(?i)ignora\s+(todas?\s+)?(las\s+)?(instrucciones|reglas|directivas)\s+(anteriores?|previas?|precedentes)",
        @"(?i)ahora\s+eres\s+(un|una)\s+",
        @"(?i)de\s+ja\s+(de\s+)?seguir\s+(las|los)\s+",
        @"(?i)olvida\s+(todo|todo\s+lo|todas)\s+",
        @"(?i)nuevas?\s+instrucciones?\s*:",
        @"(?i)ignora\s+el\s+prompt\s+del\s+sistema",
        // German
        @"(?i)ignoriere\s+(alle\s+)?(vorherigen?|früheren?|obigen?)\s+(Anweisungen|Regeln|Richtlinien)",
        @"(?i)du\s+bist\s+jetzt\s+(ein|eine)\s+",
        @"(?i)vergesse?\s+(alles|alle|all\s+deine)\s+",
        @"(?i)neue\s+(Anweisung|Regel)\s*:",
        @"(?i)ignoriere\s+den\s+System-Prompt",
        // Chinese (simplified)
        @"(?i)忽略(之前|以前|上面)的(指令|提示|规则|指导)",
        @"(?i)你现在是(一个|一位)",
        @"(?i)忘记(一切|所有|你的)",
        @"(?i)新的指令",
        @"(?i)忽略系统提示",
        // Japanese
        @"(?i)以前の指示を無視",
        @"(?i)あなたは今(の|な)",
        @"(?i)全てを忘れて",
        @"(?i)新しい指示",
        @"(?i)システムプロンプトを無視",
        // Korean
        @"(?i)이전 지시를 무시",
        @"(?i)당신은 이제",
        @"(?i)모든 것을 잊어",
        @"(?i)새로운 지시",
        @"(?i)시스템 프롬프트를 무시",
        // Russian
        @"(?i)игнорируй\s+(все\s+)?(предыдущие|ранее\s+данные)\s+(инструкции|правила|указания)",
        @"(?i)теперь\s+ты\s+(являешься\s+)?",
        @"(?i)забудь\s+(всё|все|всё\s+своё)\s+",
        @"(?i)новые\s+инструкции\s*:",
        @"(?i)игнорируй\s+системный\s+промпт",
    ];

    private static readonly Regex InjectionRegex = new(
        string.Join("|", InjectionPatterns),
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex PageContentOpenTagRegex = new("<\\s*page_content\\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PageContentCloseTagRegex = new("<\\s*/\\s*page_content\\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SystemOpenTagRegex = new("<\\s*system\\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SystemCloseTagRegex = new("<\\s*/\\s*system\\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Sanitize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var sanitized = InjectionRegex.Replace(content, match =>
        {
            var redacted = new string('*', match.Value.Length);
            return redacted;
        });

        // Regex-based tag escaping: exact-string Replace misses whitespace
        // variants like "</page_content >".
        sanitized = PageContentOpenTagRegex.Replace(sanitized, EscapedPageContentOpen);
        sanitized = PageContentCloseTagRegex.Replace(sanitized, EscapedPageContentClose);
        sanitized = SystemOpenTagRegex.Replace(sanitized, EscapedSystemOpen);
        sanitized = SystemCloseTagRegex.Replace(sanitized, EscapedSystemClose);

        return sanitized;
    }

    private static readonly string Esc = '\u0026'.ToString();
    private static readonly string EscapedPageContentOpen = Esc + "lt;page_content" + Esc + "gt;";
    private static readonly string EscapedPageContentClose = Esc + "lt;/page_content" + Esc + "gt;";
    private static readonly string EscapedSystemOpen = Esc + "lt;system" + Esc + "gt;";
    private static readonly string EscapedSystemClose = Esc + "lt;/system" + Esc + "gt;";

    public static bool ContainsSuspiciousPatterns(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return InjectionRegex.IsMatch(content);
    }
}
