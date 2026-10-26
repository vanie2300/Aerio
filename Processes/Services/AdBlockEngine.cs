using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace TabloidBrowser;

/// <summary>
/// Native content blocker. Compiles EasyList-style network filter rules and
/// answers ShouldBlock queries from the WebResourceRequested pipeline.
/// </summary>
public sealed class AdBlockEngine
{
    public const string EasyListUrl = "https://easylist.to/easylist/easylist.txt";
    private const int RefreshHours = 24;

    private static readonly string[] BundledAdDomains =
    [
        "ad.doubleclick.net",
        "adservice.google.com",
        "adservice.google.de",
        "googleadservices.com",
        "googlesyndication.com",
        "google-analytics.com",
        "googletagmanager.com",
        "googletagservices.com",
        "doubleclick.net",
        "2mdn.net",
        "adnxs.com",
        "adsafeprotected.com",
        "adroll.com",
        "adservice.doubleclick.net",
        "advertising.com",
        "adzerk.net",
        "amazon-adsystem.com",
        "a9.com",
        "aniview.com",
        "appnexus.com",
        "auditude.com",
        "betrad.com",
        "bidswitch.net",
        "braze.com",
        "brightcove.com",
        "bydemob.com",
        "casalemedia.com",
        "criteo.com",
        "criteo.net",
        "crowdtangle.com",
        "distilnetworks.com",
        "exoclick.com",
        "connect.facebook.net",
        "fbcdn.net",
        "flashtalking.com",
        "flurry.com",
        "gmads.net",
        "graphiq.com",
        "gumgum.com",
        "heliosservers.com",
        "hotjar.com",
        "imasdk.googleapis.com",
        "indexww.com",
        "inmobi.com",
        "intellitxt.com",
        "istrack.com",
        "jwpltx.com",
        "kanoodle.com",
        "liadm.com",
        "linkedin.com/analytics",
        "liveintent.com",
        "lotame.com",
        "mathtag.com",
        "media.net",
        "mediaplex.com",
        "mfadsrvr.com",
        "moatads.com",
        "mopub.com",
        "msads.net",
        "mxptint.net",
        "nativeads.com",
        "nexac.com",
        "nuggad.net",
        "omnisci.com",
        "openx.net",
        "openx.com",
        "outbrain.com",
        "optimizely.com",
        "omappapi.com",
        "pixel.quantserve.com",
        "pubmatic.com",
        "quantserve.com",
        "revcontent.com",
        "rubiconproject.com",
        "scorecardresearch.com",
        "securepubads.g.doubleclick.net",
        "semasio.net",
        "serving-sys.com",
        "sharethrough.com",
        "simpli.fi",
        "smartadserver.com",
        "sovrn.com",
        "spotx.tv",
        "steadyhq.com",
        "switchadhub.com",
        "taboola.com",
        "tapad.com",
        "teads.tv",
        "taboolanews.com",
        "theadserver.com",
        "thetracker.com",
        "tidaltv.com",
        "tremorhub.com",
        "tribalfusion.com",
        "trkn.us",
        "tubemogul.com",
        "unrulymedia.com",
        "verizonmedia.com",
        "weborama.fr",
        "www.googleadservices.com",
        "yieldmo.com",
        "yieldtraffic.com",
        "zedo.com",
        "adskeeper.com",
        "adloop.co",
        "admedo.com",
        "adpushup.com",
        "aduptech.com",
        "adversal.com",
        "affiliates.thridwatch.com",
        "affinity.com",
        "beacons.gcp.gvt2.com",
        "cnzz.com",
        "cookielaw.org",
        "ecosia.org/ads",
        "ezoic.net",
        "fastclick.net",
        "fotka.com",
        "gfkd.com",
        "glance.be",
        "go-mpulse.net",
        "imrworldwide.com",
        "infolinks.com",
        "intermarkets.net",
        "magnetic.com",
        "maxymiser.net",
        "mediamath.com",
        "metrics.apple.com",
        "moat.com",
        "narrative.io",
        "nativo.com",
        "neustar.biz",
        "omtrdc.net",
        "onesignal.com",
        "oymkt.com",
        "po.st",
        "popads.net",
        "propellerads.com",
        "skimresources.com",
        "smaato.net",
        "sonobi.com",
        "spot.im",
        "stackadapt.com",
        "statically.io",
        "tealium.com",
        "trackinglibrary.com",
        "undertone.com",
        "vk.com/ads",
        "w55c.net",
        "yandex.com/ads",
        "zeta.global"
    ];

    public static readonly AdBlockEngine Active = new();
    private static int _refreshStarted;

    private volatile Snapshot _current;

    public AdBlockEngine()
    {
        _current = Compile(BundledAdDomains);
    }

    public Snapshot Current => _current;

    public int RuleCount => _current.RuleCount;

    public static async Task RefreshOnceAsync()
    {
        if (Interlocked.Exchange(ref _refreshStarted, 1) != 0) return;
        try
        {
            await Active.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AdBlock EasyList refresh failed");
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TabloidBrowser",
            "AdBlock");
        Directory.CreateDirectory(cacheDir);
        var cacheFile = Path.Combine(cacheDir, "easylist.txt");
        var metaFile = Path.Combine(cacheDir, "last-updated.txt");

        if (File.Exists(cacheFile))
        {
            try
            {
                var compiled = Compile(SplitLines(File.ReadAllText(cacheFile)));
                if (compiled.RuleCount > 0) _current = compiled;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to compile adblock rules from cache");
            }
        }

        var stale = true;
        if (File.Exists(metaFile))
        {
            try
            {
                var last = DateTime.Parse(File.ReadAllText(metaFile), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                stale = DateTime.UtcNow - last > TimeSpan.FromHours(RefreshHours);
            }
            catch (Exception ex) { Log.Error(ex, "Failed to parse adblock last-updated timestamp"); }
        }

        if (!stale) return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync(EasyListUrl, ct);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(ct);
            var compiled = Compile(SplitLines(text));
            if (compiled.RuleCount > 0)
            {
                _current = compiled;
                File.WriteAllText(cacheFile, text);
                File.WriteAllText(metaFile, DateTime.UtcNow.ToString("o"));
                Log.Debug("AdBlock loaded {RuleCount} rules from EasyList", compiled.RuleCount);
            }
        }
        catch (OperationCanceledException) { Log.Debug("EasyList download cancelled"); }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download EasyList");
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');

    public static Snapshot Compile(IEnumerable<string> lines)
    {
        var block = new RuleSet();
        var allow = new RuleSet();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var first = line[0];
            if (first is '!' or '#' or '[') continue;
            if (line.Contains("##") || line.Contains("#@#") || line.Contains("#$#") ||
                line.Contains("#?#") || line.Contains("#%#") || line.Contains("#=#")) continue;

            // @@ marks an exception (whitelist) rule. Compile it into the allow
            // set so EasyList's deliberate exemptions are honored.
            var isException = line.StartsWith("@@", StringComparison.Ordinal);
            if (isException) line = line[2..].Trim();

            var dollar = line.IndexOf('$');
            string rule;
            if (dollar >= 0)
            {
                var options = line[(dollar + 1)..];
                if (options.Contains("domain=", StringComparison.OrdinalIgnoreCase))
                    continue; // site-scoped restrictions can't be honored — drop rather than over-block
                rule = line[..dollar].Trim();
            }
            else
            {
                rule = line.Trim();
            }

            if (rule.Length == 0) continue;
            ParseRule(rule, isException ? allow : block);
        }

        return new Snapshot(block, allow);
    }

    private static void ParseRule(string rule, RuleSet target)
    {
        if (rule.StartsWith("||", StringComparison.Ordinal))
        {
            rule = rule.Substring(2);
            var slash = rule.IndexOf('/');
            var host = slash >= 0 ? rule.Substring(0, slash) : rule;
            var path = slash >= 0 ? rule.Substring(slash) : "";
            host = host.TrimEnd('^').TrimEnd('.');
            path = path.TrimEnd('^');
            target.AddHostRule(host, path);
        }
        else if (rule.StartsWith('|'))
        {
            var anchored = rule.TrimStart('|').TrimEnd('|');
            if (anchored.Length > 0)
                target.UrlRegexes.Add(CompileRegex("^" + ConvertSeparators(anchored) + "$"));
        }
        else if (rule.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 rule.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                 rule.StartsWith("*/", StringComparison.Ordinal))
        {
            if (!ContainsWildcard(rule) && Uri.TryCreate(rule, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
            {
                var path = u.PathAndQuery;
                target.AddHostRule(u.Host.TrimEnd('.'), path);
            }
            else if (rule.Length > 0)
            {
                target.UrlRegexes.Add(CompileRegex(ConvertSeparators(rule)));
            }
        }
        else
        {
            var slash = rule.IndexOf('/');
            if (slash > 0)
            {
                var host = rule.Substring(0, slash);
                var path = rule.Substring(slash).TrimEnd('^');
                target.AddHostRule(host.TrimEnd('^'), path);
            }
            else if (rule.Length > 0)
            {
                if (rule.Contains('*') || rule.Contains('^'))
                    target.UrlRegexes.Add(CompileRegex(ConvertSeparators(rule)));
                else
                    target.AddHostRule(rule.TrimEnd('^'), "");
            }
        }
    }

    internal sealed class RuleSet
    {
        public Dictionary<string, List<string>> HostPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<Regex>> HostPathRegexes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(Regex Host, List<string> Prefixes, List<Regex> Paths)> HostRegexes { get; } = [];
        public List<Regex> UrlRegexes { get; } = [];

        public void AddHostRule(string host, string path)
        {
            host = host.TrimStart('.').TrimEnd('.');
            if (host.Length == 0) return;

            if (host.Contains('*') || host.Contains('^'))
            {
                var hostRx = CompileRegex("(?:^|\\.)" + ConvertSeparators(host));
                var prefixes = new List<string>();
                var pathRxs = new List<Regex>();
                if (!string.IsNullOrEmpty(path))
                {
                    if (ContainsWildcard(path))
                        pathRxs.Add(CompileRegex("^" + ConvertSeparators(path) + "$"));
                    else
                        prefixes.Add(path);
                }
                HostRegexes.Add((hostRx, prefixes, pathRxs));
                return;
            }

            if (!string.IsNullOrEmpty(path) && ContainsWildcard(path))
            {
                if (!HostPathRegexes.TryGetValue(host.ToLowerInvariant(), out var pathRxs))
                {
                    pathRxs = new List<Regex>();
                    HostPathRegexes[host.ToLowerInvariant()] = pathRxs;
                }
                pathRxs.Add(CompileRegex("^" + ConvertSeparators(path) + "$"));
            }
            else
            {
                if (!HostPaths.TryGetValue(host.ToLowerInvariant(), out var paths))
                {
                    paths = new List<string>();
                    HostPaths[host.ToLowerInvariant()] = paths;
                }
                if (!string.IsNullOrEmpty(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    paths.Add(path);
            }
        }

        public bool Matches(Uri uri, string host, string hostLower, string pathAndQuery, string url)
        {
            while (true)
            {
                if (HostPaths.TryGetValue(hostLower, out var paths))
                {
                    if (paths.Count == 0) return true;
                    foreach (var p in paths)
                        if (pathAndQuery.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
                }

                if (HostPathRegexes.TryGetValue(hostLower, out var pathRxs))
                {
                    foreach (var rx in pathRxs)
                        if (rx.IsMatch(pathAndQuery)) return true;
                }

                var idx = hostLower.IndexOf('.');
                if (idx < 0) break;
                hostLower = hostLower.Substring(idx + 1);
            }

            foreach (var (hostRx, prefixes, pathRxs) in HostRegexes)
            {
                if (!hostRx.IsMatch(host)) continue;
                if (prefixes.Count == 0 && pathRxs.Count == 0) return true;
                foreach (var p in prefixes)
                    if (pathAndQuery.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var rx in pathRxs)
                    if (rx.IsMatch(pathAndQuery)) return true;
            }

            foreach (var rx in UrlRegexes)
                if (rx.IsMatch(url)) return true;

            return false;
        }
    }

    private static bool ContainsWildcard(string value) =>
        value.Contains('*') || value.Contains('^');

    private static string ConvertSeparators(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '^': sb.Append("(?:[/:?#=&]|$)"); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.ToString();
    }

    private static Regex CompileRegex(string pattern) =>
        new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Immutable compiled rule set. Safe to query from any thread.
    /// </summary>
    public sealed class Snapshot
    {
        private readonly RuleSet _block;
        private readonly RuleSet _allow;

        public int RuleCount { get; }

        internal Snapshot(AdBlockEngine.RuleSet block, AdBlockEngine.RuleSet allow)
        {
            _block = block;
            _allow = allow;
            RuleCount = block.HostPaths.Count + block.HostPathRegexes.Count +
                        block.HostRegexes.Count + block.UrlRegexes.Count;
        }

        public bool ShouldBlock(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host;
            if (string.IsNullOrEmpty(host)) return false;

            var hostLower = host.ToLowerInvariant();
            var pathAndQuery = uri.PathAndQuery;

            // Whitelist (@@) rules win over blocking rules.
            if (_allow.Matches(uri, host, hostLower, pathAndQuery, url)) return false;

            return _block.Matches(uri, host, hostLower, pathAndQuery, url);
        }
    }
}
