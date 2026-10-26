using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TabloidBrowser;

public sealed record HymnSearchResult(
    string Title,
    string FirstLine,
    string Slug,
    string Meter,
    string Authors,
    string Languages,
    string Scripture,
    int InstanceCount,
    string HymnaryUrl);

public sealed record HymnLyrics(
    string Title,
    string Slug,
    IReadOnlyList<string> Stanzas,
    string HymnaryUrl);

public static class HymnService
{
    private const string UserAgent = "Mozilla/5.0";
    private const string SearchCsvUrl = "https://hymnary.org/search?qu={0}+in:texts&export=csv";
    private const string ScriptureApiUrl = "https://hymnary.org/api/scripture?reference={0}";
    private const string TextPageUrl = "https://hymnary.org/text/{0}";
    private const string MusicXmlUrl = "https://my.hymnary.org/song/{0}/unfolded.musicxml";

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly Regex MusicXmlLyricRegex = new(
        @"(?s)<lyric number=""(?<num>\d+)""[^>]*>(?<body>.*?)</lyric>",
        RegexOptions.Compiled);

    private static readonly Regex StanzaClassRegex = new(
        @"class=""ccel-stanza-(?<stanza>\d+)""",
        RegexOptions.Compiled);

    private static readonly Regex RelativeYRegex = new(
        @"relative-y=""(?<y>-?[\d.]+)""",
        RegexOptions.Compiled);

    private static readonly Regex SyllabicRegex = new(
        @"(?s)<syllabic>(?<syl>\w+)</syllabic>",
        RegexOptions.Compiled);

    private static readonly Regex LyricTextRegex = new(
        @"(?s)<text>(?<text>.*?)</text>",
        RegexOptions.Compiled);

    private static readonly Regex SongIdRegex = new(
        @"my\.hymnary\.org/song(?:/dynamic)?/(?<id>\d+)/",
        RegexOptions.Compiled);

    private static readonly Regex ScriptureQueryRegex = new(
        @"^(?:\d+\s+)?[A-Za-z]+(?:\s+[A-Za-z]+)*\s+\d+(?:[-:]\d+)*$",
        RegexOptions.Compiled);

    private sealed record LyricItem(string Stanza, int Number, double Y, string Syl, string Text);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public static async Task<IReadOnlyList<HymnSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
        {
            return [];
        }

        return ScriptureQueryRegex.IsMatch(query.Trim())
            ? await SearchByScriptureAsync(query.Trim(), ct).ConfigureAwait(false)
            : await SearchByTitleAsync(query.Trim(), ct).ConfigureAwait(false);
    }

    public static async Task<HymnLyrics?> FetchLyricsAsync(string slug, string? title = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        slug = slug.Trim();

        long? songId;
        try
        {
            songId = await GetSongIdAsync(slug, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hymnary song id lookup failed for {Slug}", slug);
            return null;
        }

        if (songId is null)
        {
            return null;
        }

        string xml;
        try
        {
            xml = await HttpGetStringAsync(string.Format(MusicXmlUrl, songId), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hymnary musicxml fetch failed for {Slug} (song {SongId})", slug, songId);
            return null;
        }

        var stanzas = ParseMusicXmlLyrics(xml);
        if (stanzas is null || stanzas.Count == 0)
        {
            return null;
        }

        return new HymnLyrics(
            string.IsNullOrWhiteSpace(title) ? FormatTitleFromSlug(slug) : title,
            slug,
            stanzas,
            $"https://hymnary.org/text/{slug}");
    }

    public static bool LooksLikeScriptureQuery(string query) =>
        ScriptureQueryRegex.IsMatch(query?.Trim() ?? string.Empty);

    private static async Task<IReadOnlyList<HymnSearchResult>> SearchByTitleAsync(string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var csv = await HttpGetStringAsync(string.Format(SearchCsvUrl, encoded), ct).ConfigureAwait(false);

        var results = new List<HymnSearchResult>();
        var lines = csv.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            var row = ParseCsvRow(lines[i]);
            if (row.Count < 10)
            {
                continue;
            }

            var slug = row[4].Trim();
            if (slug.Length == 0)
            {
                continue;
            }

            results.Add(new HymnSearchResult(
                FirstNonEmpty(row[0], row[2]),
                row[1].Trim(),
                slug,
                row[7].Trim(),
                row[6].Trim(),
                row[5].Trim(),
                "",
                ParseCount(row[9]),
                $"https://hymnary.org/text/{slug}"));
        }

        if (results.Count > 100)
        {
            results = results
                .OrderByDescending(r => r.InstanceCount)
                .Take(100)
                .ToList();
        }

        return results;
    }

    private static async Task<IReadOnlyList<HymnSearchResult>> SearchByScriptureAsync(string reference, CancellationToken ct)
    {
        var json = await HttpGetStringAsync(string.Format(ScriptureApiUrl, Uri.EscapeDataString(reference)), ct).ConfigureAwait(false);
        var results = new List<HymnSearchResult>();

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return results;
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            var title = GetString(value, "title") ?? property.Name;
            var textLink = GetString(value, "text link");
            if (textLink is null)
            {
                continue;
            }

            var slug = SlugFromTextLink(textLink);
            if (slug is null)
            {
                continue;
            }

            var authorParts = new[] { "author", "translator", "composer", "arranger", "versifier" }
                .Select(field => GetString(value, field))
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            results.Add(new HymnSearchResult(
                title,
                "",
                slug,
                GetString(value, "meter") ?? "",
                string.Join(", ", authorParts),
                GetString(value, "original language") ?? "",
                GetString(value, "scripture references") ?? reference,
                ParseCount(GetString(value, "number of hymnals") ?? "0"),
                textLink));
        }

        return results
            .OrderByDescending(r => r.InstanceCount)
            .Take(100)
            .ToList();
    }

    private static async Task<long?> GetSongIdAsync(string slug, CancellationToken ct)
    {
        var html = await HttpGetStringAsync(string.Format(TextPageUrl, Uri.EscapeDataString(slug)), ct).ConfigureAwait(false);
        var match = SongIdRegex.Match(html);
        return match.Success && long.TryParse(match.Groups["id"].Value, out var id)
            ? id
            : null;
    }

    internal static IReadOnlyList<string>? ParseMusicXmlLyrics(string xml)
    {
        var items = new List<LyricItem>();

        foreach (Match block in MusicXmlLyricRegex.Matches(xml))
        {
            if (!int.TryParse(block.Groups["num"].Value, out var number))
            {
                continue;
            }

            var body = block.Groups["body"].Value;
            var stanza = StanzaClassRegex.Match(body);
            var y = RelativeYRegex.Match(body);
            var syl = SyllabicRegex.Match(body);
            var text = LyricTextRegex.Match(body);

            if (!stanza.Success || !y.Success || !syl.Success || !text.Success)
            {
                continue;
            }

            var decoded = WebUtility.HtmlDecode(text.Groups["text"].Value).Trim();
            if (decoded.Length == 0 || !double.TryParse(y.Groups["y"].Value, out var lineY))
            {
                continue;
            }

            items.Add(new LyricItem(stanza.Groups["stanza"].Value, number, lineY, syl.Groups["syl"].Value, decoded));
        }

        if (items.Count == 0)
        {
            return null;
        }

        var stanzas = new List<string>();
        var stanzaIds = new List<string>();
        var byStanza = new Dictionary<string, List<LyricItem>>();

        foreach (var item in items)
        {
            if (!byStanza.ContainsKey(item.Stanza))
            {
                byStanza[item.Stanza] = [];
                stanzaIds.Add(item.Stanza);
            }

            byStanza[item.Stanza].Add(item);
        }

        foreach (var stanzaId in stanzaIds)
        {
            var verses = GroupVerses(byStanza[stanzaId]);
            if (verses.Count == 0)
            {
                continue;
            }

            stanzas.Add(string.Join("\n\n", verses));
        }

        return stanzas.Count > 0 ? stanzas : null;
    }

    private static List<string> GroupVerses(List<LyricItem> items)
    {
        var verses = new List<string>();
        var currentNumber = -1;
        var currentNumberItems = new List<LyricItem>();

        void CloseNumber()
        {
            var line = BuildVerse(currentNumberItems);
            if (line.Length > 0)
            {
                verses.Add(line);
            }

            currentNumberItems.Clear();
        }

        foreach (var item in items)
        {
            if (item.Number != currentNumber)
            {
                if (currentNumber >= 0)
                {
                    CloseNumber();
                }

                currentNumber = item.Number;
            }

            currentNumberItems.Add(item);
        }

        if (currentNumberItems.Count > 0)
        {
            CloseNumber();
        }

        return verses;
    }

    private static string BuildVerse(List<LyricItem> items)
    {
        var lines = new List<string>();
        var lineWords = new List<string>();
        var currentY = double.NaN;
        var partial = string.Empty;

        void CloseLine()
        {
            if (partial.Length > 0)
            {
                lineWords.Add(partial);
                partial = string.Empty;
            }

            if (lineWords.Count > 0)
            {
                lines.Add(string.Join(" ", lineWords));
                lineWords.Clear();
            }
        }

        foreach (var item in items)
        {
            if (!double.IsNaN(currentY) && Math.Abs(item.Y - currentY) > 0.001)
            {
                CloseLine();
                currentY = item.Y;
                partial = string.Empty;
            }
            else if (double.IsNaN(currentY))
            {
                currentY = item.Y;
            }

            switch (item.Syl)
            {
                case "single":
                    if (partial.Length > 0)
                    {
                        lineWords.Add(partial + item.Text);
                        partial = string.Empty;
                    }
                    else
                    {
                        lineWords.Add(item.Text);
                    }

                    break;
                case "begin":
                    partial = item.Text;
                    break;
                case "middle":
                    partial += item.Text;
                    break;
                case "end":
                    partial += item.Text;
                    lineWords.Add(partial);
                    partial = string.Empty;
                    break;
                default:
                    if (partial.Length > 0)
                    {
                        lineWords.Add(partial + item.Text);
                        partial = string.Empty;
                    }
                    else
                    {
                        lineWords.Add(item.Text);
                    }

                    break;
            }
        }

        CloseLine();

        for (int i = 0; i < lines.Count; i++)
        {
            var tokens = lines[i].Split(' ');
            var merged = new List<string>();
            foreach (var token in tokens)
            {
                if (token.Length > 0 && !token.Any(char.IsLetterOrDigit) && merged.Count > 0)
                {
                    merged[^1] += token;
                }
                else
                {
                    merged.Add(token);
                }
            }

            lines[i] = string.Join(" ", merged);
        }

        return string.Join("\n", lines);
    }

    internal static List<List<string>> ParseCsvRows(string text)
    {
        var results = new List<List<string>>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var row = ParseCsvRow(line);
            if (row is not null && row.Count > 0 && row.Any(cell => cell.Length > 0))
            {
                results.Add(row);
            }
        }

        return results;
    }

    private static List<string>? ParseCsvRow(string line)
    {
        var row = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                row.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        row.Add(current.ToString());
        return row.Count > 0 ? row : null;
    }

    private static async Task<string> HttpGetStringAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string? GetString(System.Text.Json.JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? SlugFromTextLink(string textLink)
    {
        var prefix = "hymnary.org/text/";
        var index = textLink.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var slug = textLink[(index + prefix.Length)..];
        var end = slug.IndexOfAny(['/', '?', '#']);
        return end >= 0 ? slug[..end] : slug;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static int ParseCount(string value)
    {
        return int.TryParse(value.Trim(), out var count) ? count : 0;
    }

    private static string FormatTitleFromSlug(string slug)
    {
        var words = slug.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return slug;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length == 0)
            {
                continue;
            }

            words[i] = char.ToUpper(words[i][0], culture) + words[i][1..];
        }

        return string.Join(" ", words);
    }
}