using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LightweightBrowser;

internal sealed class KlyroSearchIndex
{
    private const int MaxPagesPerSeed = 50;
    private const int MaxPageCharacters = 512 * 1024;
    private static readonly TimeSpan MinimumRequestDelay = TimeSpan.FromSeconds(1);
    private static readonly Regex TitlePattern = new("<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new("<a\\b[^>]*?href\\s*=\\s*['\"]([^'\"]+)['\"]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HiddenContentPattern = new(
        "<(script|style|noscript|svg)\\b[^>]*>.*?</\\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex WordPattern = new("[\\p{L}\\p{N}]{2,}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _seedFilePath;
    private readonly string _indexFilePath;
    private readonly HttpClient _httpClient;
    private readonly List<string> _seeds;
    private readonly List<IndexedPage> _pages;

    public KlyroSearchIndex(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _seedFilePath = Path.Combine(dataFolder, "search-seeds.json");
        _indexFilePath = Path.Combine(dataFolder, "search-index.json");
        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KlyroSearch/0.1 (local, user-seeded crawler)");
        _seeds = LoadFile<List<string>>(_seedFilePath) ?? [];
        _pages = LoadFile<List<IndexedPage>>(_indexFilePath) ?? [];
    }

    public IReadOnlyList<string> Seeds => _seeds.ToArray();

    public int PageCount => _pages.Count;

    public void AddSeed(string input)
    {
        if (!TryNormalizeSeed(input, out var seed))
            throw new ArgumentException("Enter a public HTTP or HTTPS website address.", nameof(input));

        if (_seeds.Contains(seed, StringComparer.OrdinalIgnoreCase))
            return;

        var updatedSeeds = _seeds.Append(seed).ToList();
        SaveFile(_seedFilePath, updatedSeeds);
        _seeds.Add(seed);
    }

    public void RemoveSeed(string seed)
    {
        if (!Uri.TryCreate(seed, UriKind.Absolute, out var seedUri))
            return;

        var updatedSeeds = _seeds
            .Where(item => !string.Equals(item, seed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var updatedPages = _pages
            .Where(page => !Uri.TryCreate(page.Url, UriKind.Absolute, out var pageUri) ||
                           !pageUri.Host.Equals(seedUri.Host, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SaveFile(_seedFilePath, updatedSeeds);
        SaveFile(_indexFilePath, updatedPages);
        _seeds.Clear();
        _seeds.AddRange(updatedSeeds);
        _pages.Clear();
        _pages.AddRange(updatedPages);
    }

    public IReadOnlyList<SearchResult> Search(string query, int maxResults = 40)
    {
        var terms = WordPattern.Matches(query)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
            return [];

        return _pages
            .Select(page => new SearchResult(
                page.Title,
                page.Url,
                page.Snippet,
                Score(page, terms)))
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
    }

    public async Task<int> CrawlAllAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        var indexedCount = 0;
        foreach (var seed in _seeds.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(seed, UriKind.Absolute, out var seedUri))
                continue;

            var robots = await LoadRobotsRulesAsync(seedUri, progress, cancellationToken);
            if (robots is null)
                continue;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<Uri>();
            pending.Enqueue(seedUri);
            var attemptedCount = 0;

            while (pending.Count > 0 && attemptedCount < MaxPagesPerSeed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageUri = pending.Dequeue();
                var normalizedUrl = NormalizePageUrl(pageUri);
                if (!visited.Add(normalizedUrl) || IsDisallowed(pageUri, robots))
                    continue;

                if (attemptedCount > 0)
                    await Task.Delay(MinimumRequestDelay, cancellationToken);

                attemptedCount++;
                progress.Report($"Crawling {pageUri.Host} ({attemptedCount}/{MaxPagesPerSeed})");
                try
                {
                    using var response = await _httpClient.GetAsync(
                        pageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.RequestMessage?.RequestUri is not { } finalUri ||
                        !IsSameOrigin(finalUri, seedUri) ||
                        !response.IsSuccessStatusCode ||
                        response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
                        !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var html = await ReadLimitedTextAsync(response, cancellationToken);
                    var page = ParsePage(finalUri, html);
                    if (page is null)
                        continue;

                    _pages.RemoveAll(existing =>
                        existing.Url.Equals(page.Url, StringComparison.OrdinalIgnoreCase));
                    _pages.Add(page);
                    SaveFile(_indexFilePath, _pages);
                    indexedCount++;

                    foreach (var link in ExtractLinks(finalUri, html, seedUri))
                    {
                        if (!visited.Contains(link.AbsoluteUri) && visited.Count + pending.Count < MaxPagesPerSeed * 4)
                            pending.Enqueue(link);
                    }
                }
                catch (HttpRequestException exception)
                {
                    progress.Report($"Skipped {pageUri}: {exception.Message}");
                }
            }
        }

        SaveFile(_indexFilePath, _pages);
        return indexedCount;
    }

    private async Task<IReadOnlyList<RobotsRule>?> LoadRobotsRulesAsync(
        Uri seedUri,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var robotsUri = new UriBuilder(seedUri.Scheme, seedUri.Host, seedUri.IsDefaultPort ? -1 : seedUri.Port)
        {
            Path = "/robots.txt"
        }.Uri;

        try
        {
            using var response = await _httpClient.GetAsync(
                robotsUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];
            if (!response.IsSuccessStatusCode ||
                response.RequestMessage?.RequestUri is not { } finalUri ||
                !IsSameOrigin(finalUri, seedUri))
            {
                progress.Report($"Could not read robots.txt for {seedUri.Host}; skipping this site.");
                return null;
            }

            var content = await ReadLimitedTextAsync(response, cancellationToken, 128 * 1024);
            return ParseRobotsRules(content);
        }
        catch (HttpRequestException exception)
        {
            progress.Report($"Could not read robots.txt for {seedUri.Host}: {exception.Message}");
            return null;
        }
    }

    private static IReadOnlyList<RobotsRule> ParseRobotsRules(string content)
    {
        var rules = new List<RobotsRule>();
        var appliesToKlyro = false;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var directive = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (directive.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                appliesToKlyro = value == "*" ||
                    value.Contains("KlyroSearch", StringComparison.OrdinalIgnoreCase);
            }
            else if (appliesToKlyro &&
                     (directive.Equals("disallow", StringComparison.OrdinalIgnoreCase) ||
                      directive.Equals("allow", StringComparison.OrdinalIgnoreCase)) &&
                     value.Length > 0)
            {
                rules.Add(new RobotsRule(value, directive.Equals("allow", StringComparison.OrdinalIgnoreCase)));
            }
        }
        return rules;
    }

    private static bool IsDisallowed(Uri uri, IReadOnlyList<RobotsRule> rules)
    {
        var path = uri.PathAndQuery;
        var matchingRule = rules
            .Where(rule => path.StartsWith(rule.Path, StringComparison.Ordinal))
            .OrderByDescending(rule => rule.Path.Length)
            .ThenByDescending(rule => rule.Allow)
            .FirstOrDefault();
        return matchingRule is { Allow: false };
    }

    private static async Task<string> ReadLimitedTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        int maxCharacters = MaxPageCharacters)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var buffer = new char[8192];
        var builder = new StringBuilder(Math.Min(maxCharacters, 64 * 1024));
        while (builder.Length < maxCharacters)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(
                0, Math.Min(buffer.Length, maxCharacters - builder.Length)), cancellationToken);
            if (count == 0)
                break;
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }

    private static IndexedPage? ParsePage(Uri uri, string html)
    {
        var titleMatch = TitlePattern.Match(html);
        var title = titleMatch.Success
            ? NormalizeText(titleMatch.Groups[1].Value)
            : "";
        var text = NormalizeText(TagPattern.Replace(HiddenContentPattern.Replace(html, " "), " "));
        if (text.Length < 40)
            return null;

        if (string.IsNullOrWhiteSpace(title))
            title = uri.Host + uri.AbsolutePath;

        return new IndexedPage(NormalizePageUrl(uri), title, text[..Math.Min(text.Length, 12000)],
            text[..Math.Min(text.Length, 600)]);
    }

    private static IEnumerable<Uri> ExtractLinks(Uri baseUri, string html, Uri seedUri)
    {
        foreach (Match match in LinkPattern.Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (!Uri.TryCreate(baseUri, href, out var link) ||
                (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps) ||
            !IsSameOrigin(link, seedUri) ||
                link.UserInfo.Length > 0 ||
                link.AbsoluteUri.Length > 2048 ||
                IsLikelyNonPage(link))
                continue;
            yield return link;
        }
    }

    private static bool IsLikelyNonPage(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Length > 0 && !new[]
        {
            ".html", ".htm", ".xhtml", ".php", ".asp", ".aspx"
        }.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePageUrl(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsSameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static string NormalizeText(string text) =>
        WebUtility.HtmlDecode(WhitespacePattern.Replace(text, " ")).Trim();

    private static double Score(IndexedPage page, IReadOnlyList<string> terms)
    {
        var score = 0d;
        foreach (var term in terms)
        {
            score += CountOccurrences(page.Title, term) * 5;
            score += CountOccurrences(page.Content, term);
        }
        return score;
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += term.Length;
        }
        return count;
    }

    private static bool TryNormalizeSeed(string input, out string seed)
    {
        seed = "";
        var text = input.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length > 0 ||
            IsLocalOrPrivateHost(uri))
            return false;

        seed = NormalizePageUrl(uri);
        return true;
    }

    private static bool IsLocalOrPrivateHost(Uri uri)
    {
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!IPAddress.TryParse(uri.Host, out var address))
            return false;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private static T? LoadFile<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
    }

    private static void SaveFile<T>(string path, T value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, true);
    }

    private sealed record IndexedPage(string Url, string Title, string Content, string Snippet);
    private sealed record RobotsRule(string Path, bool Allow);

    internal sealed record SearchResult(string Title, string Url, string Snippet, double Score);
}
