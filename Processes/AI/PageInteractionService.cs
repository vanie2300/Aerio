using System.Text.Json;

namespace TabloidBrowser;

public sealed class PageInteractionService
{
    private readonly Func<string, Task<string?>> _executeJs;
    private readonly Func<Task> _waitForNavigation;

    public PageInteractionService(
        Func<string, Task<string?>> executeJs,
        Func<Task> waitForNavigation)
    {
        _executeJs = executeJs;
        _waitForNavigation = waitForNavigation;
    }

    public async Task<string?> GetCurrentUrlAsync()
    {
        var result = await _executeJs("window.location.href");
        return DecodeJsonString(result);
    }

    public async Task<bool> ClickElementAsync(string selector)
    {
        var escaped = EscapeJs(selector);
        var js = "(function(){" +
            "var el=document.querySelector('" + escaped + "');" +
            "if(!el)return false;" +
            "el.scrollIntoView({behavior:'smooth',block:'center'});" +
            "el.click();" +
            "return true;" +
            "})();";
        var result = await _executeJs(js);
        return result?.Trim('"') == "true";
    }

    public async Task<bool> FillFieldAsync(string selector, string value)
    {
        var escapedSel = EscapeJs(selector);
        var escapedVal = EscapeJs(value);
        var js = "(function(){" +
            "var el=document.querySelector('" + escapedSel + "');" +
            "if(!el)return false;" +
            "el.scrollIntoView({behavior:'smooth',block:'center'});" +
            "el.focus();" +
            "el.value='" + escapedVal + "';" +
            "el.dispatchEvent(new Event('input',{bubbles:true}));" +
            "el.dispatchEvent(new Event('change',{bubbles:true}));" +
            "return true;" +
            "})();";
        var result = await _executeJs(js);
        return result?.Trim('"') == "true";
    }

    public async Task<List<InteractiveElement>> GetInteractiveElementsAsync()
    {
        var js = "JSON.stringify(Array.from(document.querySelectorAll('input,textarea,select,button,a[href]')).slice(0,50).map(function(el){" +
            "var sel = el.id ? '#'+CSS.escape(el.id) : (el.name ? '[name=\"'+CSS.escape(el.name)+'\"]' : '');" +
            "if(!sel){var t=el.tagName.toLowerCase();" +
            "if(el.placeholder){sel=t+'[placeholder=\"'+CSS.escape(el.placeholder)+'\"]';}" +
            "else if(el.type&&el.type!=='text'){sel=t+'[type=\"'+CSS.escape(el.type)+'\"]';}" +
            "else{sel=t;}" +
            "}" +
            "return{tag:el.tagName.toLowerCase(),type:el.type||'',text:(el.textContent||'').trim().substring(0,80)," +
            "placeholder:el.placeholder||'',name:el.name||'',id:el.id||'',href:el.href||'',selector:sel};" +
            "}))";
        var json = await _executeJs(js);
        if (string.IsNullOrEmpty(json) || json == "null")
            return [];

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var document = JsonDocument.Parse(json);
            var elementsJson = document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString()
                : json;
            return string.IsNullOrWhiteSpace(elementsJson)
                ? []
                : JsonSerializer.Deserialize<List<InteractiveElement>>(elementsJson, options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<string> GetSelectedTextAsync()
    {
        var result = await _executeJs("window.getSelection().toString()");
        if (string.IsNullOrEmpty(result) || result == "null")
            return "";
        return DecodeJsonString(result) ?? "";
    }

    /// <summary>
    /// ExecuteScriptAsync returns a JSON-encoded string. Trim('"') leaves \n,
    /// \" and unicode escapes un-decoded (and trims legitimate quotes from the
    /// text itself) — decode properly instead.
    /// </summary>
    private static string? DecodeJsonString(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null")
            return null;
        try
        {
            return JsonSerializer.Deserialize<string>(json);
        }
        catch (JsonException)
        {
            return json.Trim('"');
        }
    }

    public async Task<bool> ScrollPageAsync(string direction, int? amount = null)
    {
        var px = amount ?? 500;
        var js = direction switch
        {
            "down" => $"window.scrollBy(0, {px});",
            "up" => $"window.scrollBy(0, -{px});",
            "right" => $"window.scrollBy({px}, 0);",
            "left" => $"window.scrollBy(-{px}, 0);",
            "top" => "window.scrollTo(0, 0);",
            "bottom" => "window.scrollTo(0, document.body.scrollHeight);",
            _ => ""
        };

        if (string.IsNullOrEmpty(js)) return false;
        await _executeJs(js);
        return true;
    }

    public async Task<bool> PressKeyAsync(string key)
    {
        var code = MapKeyToCode(key);
        if (string.IsNullOrEmpty(code)) return false;

        var js = "(function(){" +
            "var el=document.activeElement||document.body;" +
            $"el.dispatchEvent(new KeyboardEvent('keydown',{{key:'{EscapeJs(key)}',code:'{code}',bubbles:true}}));" +
            $"el.dispatchEvent(new KeyboardEvent('keyup',{{key:'{EscapeJs(key)}',code:'{code}',bubbles:true}}));" +
            "return true;" +
            "})();";
        var result = await _executeJs(js);
        return result?.Trim('"') == "true";
    }

    public async Task<bool> GoBackAsync()
    {
        await _executeJs("window.history.back()");
        return true;
    }

    private static string MapKeyToCode(string key) => key.ToLowerInvariant() switch
    {
        "enter" or "return" => "Enter",
        "tab" => "Tab",
        "escape" or "esc" => "Escape",
        "backspace" => "Backspace",
        "delete" or "del" => "Delete",
        "arrowup" or "up" => "ArrowUp",
        "arrowdown" or "down" => "ArrowDown",
        "arrowleft" or "left" => "ArrowLeft",
        "arrowright" or "right" => "ArrowRight",
        "home" => "Home",
        "end" => "End",
        "pageup" => "PageUp",
        "pagedown" => "PageDown",
        "space" or " " => "Space",
        _ => ""
    };

    private static string EscapeJs(string input)
    {
        return input
            .Replace("\\", "\\\\")
            .Replace("\u2028", "\\u2028")
            .Replace("\u2029", "\\u2029")
            .Replace("`", "\\`")
            .Replace("'", "\\'")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\0", "\\0");
    }
}

public sealed record InteractiveElement(
    string Tag,
    string Type,
    string Text,
    string Placeholder,
    string Name,
    string Id,
    string Href,
    string Selector
);
