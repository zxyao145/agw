using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Web;

public class WebSearchToolParams
{
    [Description(
        """
            The search query to use.
            """
    )]
    public string Query { get; set; } = "";

    [Description(
        """
            Only include search results from these domains.
            """
    )]
    public List<string>? AllowedDomains { get; set; }

    [Description(
        """
            Never include search results from these domains.
            """
    )]
    public List<string>? BlockedDomains { get; set; }

    [Description(
        """
            Maximum number of search results to return. Defaults to 5.
            """
    )]
    public int? MaxResults { get; set; }
}

public class SearchHit
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Content { get; set; } = "";
}

public class WebSearchResult
{
    public string Query { get; set; } = "";
    public List<object> Results { get; set; } = new();
    public string Provider { get; set; } = "";
    public int TotalResults { get; set; }
    public double DurationSeconds { get; set; }
}

internal sealed class WebSearchTool
{
    private const int DefaultMaxResults = 5;
    private const int MaximumMaxResults = 10;
    private const int RequestTimeoutMs = 30000;
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private static readonly Regex GoogleHeadingRegex = new(
        @"<a\b[^>]*href=[""']([^""']*(?:/url\?(?:[^""']*?[?&])?(?:q|url)=[^""']+|https?://[^""']+))[""'][^>]*>[\s\S]*?<h3\b[^>]*>([\s\S]*?)</h3>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
    );

    private static readonly Regex BingBlockRegex = new(
        @"<li\b[^>]*class=[""'][^""']*b_algo[^""']*[""'][^>]*>([\s\S]*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
    );

    private static readonly Regex BaiduHeadingRegex = new(
        @"<h3\b[^>]*>[\s\S]*?<a\b[^>]*href=[""']([^""']+)[""'][^>]*>([\s\S]*?)</a>[\s\S]*?</h3>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
    );

    private static readonly Regex StripScriptStyleRegex = new(
        @"<script\b[^>]*>[\s\S]*?</script>|<style\b[^>]*>[\s\S]*?</style>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
    );

    private static readonly Regex StripTagsRegex = new(
        @"<[^>]+>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
    );

    private static readonly Regex CollapseWhitespaceRegex = new(@"\s+", RegexOptions.CultureInvariant);

    public string Name => "web_search";

    public string Category => "Web";

    [Description(
        """
            Allows searching the web for current information. Provides search results and/or text commentary.
            """
    )]
    public WebSearchResult Execute(WebSearchToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Query))
        {
            throw new AgwException(ErrorCodes.QueryRequired, "Query is required.");
        }

        if (toolParams.AllowedDomains?.Count > 0 && toolParams.BlockedDomains?.Count > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParameters,
                "Cannot specify both allowed_domains and blocked_domains in the same request."
            );
        }

        var stopwatch = Stopwatch.StartNew();

        // Try providers in priority order and use the first parseable result set.
        var maxResults = NormalizeMaxResults(toolParams.MaxResults);
        var searchResponse = PerformSearch(
            toolParams.Query,
            maxResults,
            toolParams.AllowedDomains,
            toolParams.BlockedDomains
        );

        stopwatch.Stop();
        var durationSeconds = stopwatch.ElapsedMilliseconds / 1000.0;

        var results = new List<object>();
        results.AddRange(searchResponse.Results);

        return new WebSearchResult
        {
            Query = toolParams.Query,
            Results = results,
            Provider = searchResponse.Provider,
            TotalResults = searchResponse.Results.Count,
            DurationSeconds = durationSeconds,
        };
    }

    public AITool ToAITool()
    {
        Func<WebSearchToolParams, WebSearchResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }

    private static SearchProviderResponse PerformSearch(
        string query,
        int maxResults,
        List<string>? allowedDomains,
        List<string>? blockedDomains
    )
    {
        var httpClientFactory = IocUtil.GetSingletonRequiredService<IHttpClientFactory>();
        using var client = httpClientFactory.CreateClient();
        var failures = new List<string>();

        foreach (var provider in new[] { SearchProvider.Google, SearchProvider.Bing, SearchProvider.Baidu })
        {
            try
            {
                var results = provider switch
                {
                    SearchProvider.Google => SearchGoogle(client, query, maxResults, allowedDomains, blockedDomains),
                    SearchProvider.Bing => SearchBing(client, query, maxResults, allowedDomains, blockedDomains),
                    SearchProvider.Baidu => SearchBaidu(client, query, maxResults, allowedDomains, blockedDomains),
                    _ => [],
                };

                return new SearchProviderResponse(provider.ToString().ToLowerInvariant(), results);
            }
            catch (Exception ex)
            {
                failures.Add($"{provider}: {ex.Message}");
            }
        }

        throw new AgwException(
            ErrorCodes.FetchFailed,
            $"All web search providers failed for query '{query}'. {string.Join(" | ", failures)}"
        );
    }

    private static List<SearchHit> SearchGoogle(
        HttpClient client,
        string query,
        int maxResults,
        List<string>? allowedDomains,
        List<string>? blockedDomains
    )
    {
        var searchUrl = $"https://www.google.com/search?hl=en&num={maxResults}&gbv=1&q={WebUtility.UrlEncode(query)}";
        var html = SendSearchRequest(client, "Google", searchUrl, "en-US,en;q=0.9");

        if (
            Regex.IsMatch(
                html,
                @"unusual traffic|detected unusual traffic|sorry/index|To continue, please type",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            throw new AgwException(ErrorCodes.FetchFailed, "Google blocked background crawling for this request.");
        }

        return EnsureResults(
            "Google",
            ExtractGoogleResults(html, maxResults),
            maxResults,
            allowedDomains,
            blockedDomains
        );
    }

    private static List<SearchHit> SearchBing(
        HttpClient client,
        string query,
        int maxResults,
        List<string>? allowedDomains,
        List<string>? blockedDomains
    )
    {
        var searchUrl = $"https://www.bing.com/search?q={WebUtility.UrlEncode(query)}&count={maxResults}";
        var html = SendSearchRequest(client, "Bing", searchUrl, "en-US,en;q=0.9");

        return EnsureResults("Bing", ExtractBingResults(html, maxResults), maxResults, allowedDomains, blockedDomains);
    }

    private static List<SearchHit> SearchBaidu(
        HttpClient client,
        string query,
        int maxResults,
        List<string>? allowedDomains,
        List<string>? blockedDomains
    )
    {
        var searchUrl = $"https://www.baidu.com/s?wd={WebUtility.UrlEncode(query)}&rn={maxResults}";
        var html = SendSearchRequest(client, "Baidu", searchUrl, "zh-CN,zh;q=0.9,en;q=0.8");

        if (
            Regex.IsMatch(
                html,
                @"百度安全验证|网络不给力|请输入验证码|verify",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            throw new AgwException(ErrorCodes.FetchFailed, "Baidu blocked background crawling for this request.");
        }

        return EnsureResults(
            "Baidu",
            ExtractBaiduResults(html, maxResults),
            maxResults,
            allowedDomains,
            blockedDomains
        );
    }

    private static string SendSearchRequest(HttpClient client, string provider, string searchUrl, string acceptLanguage)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
        );
        request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(RequestTimeoutMs));
        using var response = client.SendAsync(request, timeout.Token).GetAwaiter().GetResult();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new AgwException(ErrorCodes.FetchFailed, $"{provider} search error: {(int)response.StatusCode}");
        }

        return response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult();
    }

    private static List<SearchHit> ExtractGoogleResults(string html, int maxResults)
    {
        var headings = GoogleHeadingRegex.Matches(html);
        var results = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < headings.Count && results.Count < maxResults; index++)
        {
            var match = headings[index];
            var title = StripHtml(match.Groups[2].Value);
            var url = ResolveSearchResultUrl(SearchProvider.Google, match.Groups[1].Value);

            if (
                string.IsNullOrWhiteSpace(title)
                || string.IsNullOrWhiteSpace(url)
                || url.Contains("/search?", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var dedupeKey = $"{title}::{url}";
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            var start = match.Index;
            var nextStart =
                index + 1 < headings.Count ? headings[index + 1].Index : Math.Min(start + 6000, html.Length);
            var section = html[start..Math.Min(nextStart, Math.Min(start + 6000, html.Length))];
            var snippet = ExtractSnippet(
                section,
                [
                    @"<div\b[^>]*class=[""'][^""']*(?:VwiC3b|yXK7lf|MUxGbd|kvH3mc)[^""']*[""'][^>]*>([\s\S]*?)</div>",
                    @"<span\b[^>]*class=[""'][^""']*(?:aCOpRe|hgKElc)[^""']*[""'][^>]*>([\s\S]*?)</span>",
                    @"<div\b[^>]*data-sncf=[""'][^""']*[""'][^>]*>([\s\S]*?)</div>",
                ],
                title
            );

            results.Add(
                new SearchHit
                {
                    Title = title,
                    Url = url,
                    Content = snippet,
                }
            );
        }

        return results;
    }

    private static List<SearchHit> ExtractBingResults(string html, int maxResults)
    {
        var blocks = BingBlockRegex.Matches(html);
        var results = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match block in blocks)
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            var section = block.Groups[1].Value;
            var headingMatch = Regex.Match(
                section,
                @"<h2\b[^>]*>\s*<a\b[^>]*href=[""']([^""']+)[""'][^>]*>([\s\S]*?)</a>\s*</h2>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
            );
            if (!headingMatch.Success)
            {
                continue;
            }

            var title = StripHtml(headingMatch.Groups[2].Value);
            var url = ResolveSearchResultUrl(SearchProvider.Bing, headingMatch.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var dedupeKey = $"{title}::{url}";
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            var snippet = ExtractSnippet(
                section,
                [
                    @"<div\b[^>]*class=[""'][^""']*b_caption[^""']*[""'][^>]*>[\s\S]*?<p\b[^>]*>([\s\S]*?)</p>",
                    @"<p\b[^>]*>([\s\S]*?)</p>",
                ],
                title
            );

            results.Add(
                new SearchHit
                {
                    Title = title,
                    Url = url,
                    Content = snippet,
                }
            );
        }

        return results;
    }

    private static List<SearchHit> ExtractBaiduResults(string html, int maxResults)
    {
        var headings = BaiduHeadingRegex.Matches(html);
        var results = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < headings.Count && results.Count < maxResults; index++)
        {
            var match = headings[index];
            var title = StripHtml(match.Groups[2].Value);
            var url = ResolveSearchResultUrl(SearchProvider.Baidu, match.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var dedupeKey = $"{title}::{url}";
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            var start = match.Index;
            var nextStart =
                index + 1 < headings.Count ? headings[index + 1].Index : Math.Min(start + 4000, html.Length);
            var section = html[start..Math.Min(nextStart, Math.Min(start + 4000, html.Length))];
            var snippetMatches = Regex.Matches(
                section,
                @"<(div|span|p)\b[^>]*class=[""'][^""']*(?:c-abstract|content-right_[^""']*|content-right|c-span-last|c-color-text|result-op[^""']*)[^""']*[""'][^>]*>([\s\S]*?)</\1>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
            );
            var snippet =
                snippetMatches
                    .Cast<Match>()
                    .Select(candidate => StripHtml(candidate.Groups[2].Value))
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text) && text != title)
                ?? StripHtml(section).Replace(title, "", StringComparison.Ordinal).Trim();

            results.Add(
                new SearchHit
                {
                    Title = title,
                    Url = url,
                    Content = snippet,
                }
            );
        }

        return results;
    }

    private static List<SearchHit> EnsureResults(
        string provider,
        List<SearchHit> results,
        int maxResults,
        List<string>? allowedDomains,
        List<string>? blockedDomains
    )
    {
        var filteredResults = results
            .Where(result => IsAllowedDomain(result.Url, allowedDomains, blockedDomains))
            .Take(maxResults)
            .ToList();

        if (filteredResults.Count == 0)
        {
            throw new AgwException(ErrorCodes.FetchFailed, $"{provider} returned no parseable search results.");
        }

        return filteredResults;
    }

    private static bool IsAllowedDomain(string url, List<string>? allowedDomains, List<string>? blockedDomains)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var domain = uri.Host;
        if (
            allowedDomains?.Count > 0
            && !allowedDomains.Any(allowed => domain.Contains(allowed, StringComparison.OrdinalIgnoreCase))
        )
        {
            return false;
        }

        if (
            blockedDomains?.Count > 0
            && blockedDomains.Any(blocked => domain.Contains(blocked, StringComparison.OrdinalIgnoreCase))
        )
        {
            return false;
        }

        return true;
    }

    private static string ExtractSnippet(string section, string[] patterns, string title)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(
                section,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
            );
            if (!match.Success)
            {
                continue;
            }

            var text = StripHtml(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(text) && text != title)
            {
                return text;
            }
        }

        return StripHtml(section).Replace(title, "", StringComparison.Ordinal).Trim();
    }

    private static string ResolveSearchResultUrl(SearchProvider provider, string rawUrl)
    {
        var normalized = NormalizeUrl(rawUrl);

        if (provider == SearchProvider.Google)
        {
            try
            {
                var absolute = normalized.StartsWith("/url?", StringComparison.OrdinalIgnoreCase)
                    ? $"https://www.google.com{normalized}"
                    : normalized;
                var url = new Uri(absolute);
                var target =
                    ParseQueryParameter(url.Query.TrimStart('?'), "q")
                    ?? ParseQueryParameter(url.Query.TrimStart('?'), "url");

                return target is null ? normalized : NormalizeUrl(target);
            }
            catch
            {
                return normalized;
            }
        }

        if (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            var baseUrl = provider == SearchProvider.Bing ? "https://www.bing.com" : "https://www.baidu.com";
            return $"{baseUrl}{normalized}";
        }

        return normalized;
    }

    private static string StripHtml(string input)
    {
        var decoded = WebUtility.HtmlDecode(input);
        var withoutScripts = StripScriptStyleRegex.Replace(decoded, " ");
        var withoutTags = StripTagsRegex.Replace(withoutScripts, " ");
        return CollapseWhitespaceRegex.Replace(withoutTags, " ").Trim();
    }

    private static string NormalizeUrl(string url)
    {
        return WebUtility
            .HtmlDecode(url)
            .Replace(@"\u002F", "/", StringComparison.Ordinal)
            .Replace(@"\u003A", ":", StringComparison.Ordinal)
            .Trim();
    }

    private static int NormalizeMaxResults(int? maxResults)
    {
        return Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaximumMaxResults);
    }

    private static string? ParseQueryParameter(string query, string key)
    {
        var pairs = query.Split('&');
        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx > 0)
            {
                var k = WebUtility.UrlDecode(pair[..idx]);
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return WebUtility.UrlDecode(pair[(idx + 1)..]);
                }
            }
        }
        return null;
    }

    private sealed record SearchProviderResponse(string Provider, List<SearchHit> Results);

    private enum SearchProvider
    {
        Google,
        Bing,
        Baidu,
    }
}
