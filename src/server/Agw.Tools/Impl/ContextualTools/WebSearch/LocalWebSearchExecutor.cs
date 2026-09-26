using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Agw.Shared.Exceptions;
using Agw.Tools.Contracts.WebSearch;

namespace Agw.Tools.Impl.ContextualTools.WebSearch;

/// <summary>
/// Executes HTTP-based web searches for the contextual Tool's local fallback.
/// Tool registration, permissions, and AI function creation belong to WebSearchContextualTool.
/// </summary>
internal sealed partial class LocalWebSearchExecutor
{
    private const int DefaultMaxResults = 5;
    private const int MaximumMaxResults = 10;
    private const int RequestTimeoutMs = 30000;
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private readonly IHttpClientFactory _httpClientFactory;

    public LocalWebSearchExecutor(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private const RegexOptions HtmlRegexOptions =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    private static readonly Regex[] GoogleSnippetRegexes =
    [
        GoogleSnippetDivRegex(),
        GoogleSnippetSpanRegex(),
        GoogleSnippetDataRegex(),
    ];

    private static readonly Regex[] BingSnippetRegexes = [BingCaptionSnippetRegex(), BingParagraphSnippetRegex()];

    [GeneratedRegex(
        @"<a\b[^>]*href=[""']([^""']*(?:/url\?(?:[^""']*?[?&])?(?:q|url)=[^""']+|https?://[^""']+))[""'][^>]*>[\s\S]*?<h3\b[^>]*>([\s\S]*?)</h3>",
        HtmlRegexOptions
    )]
    private static partial Regex GoogleHeadingRegex();

    [GeneratedRegex(@"<li\b[^>]*class=[""'][^""']*b_algo[^""']*[""'][^>]*>([\s\S]*?)</li>", HtmlRegexOptions)]
    private static partial Regex BingBlockRegex();

    [GeneratedRegex(
        @"<h3\b[^>]*>[\s\S]*?<a\b[^>]*href=[""']([^""']+)[""'][^>]*>([\s\S]*?)</a>[\s\S]*?</h3>",
        HtmlRegexOptions
    )]
    private static partial Regex BaiduHeadingRegex();

    [GeneratedRegex(@"<script\b[^>]*>[\s\S]*?</script>|<style\b[^>]*>[\s\S]*?</style>", HtmlRegexOptions)]
    private static partial Regex StripScriptStyleRegex();

    [GeneratedRegex(@"<[^>]+>", HtmlRegexOptions)]
    private static partial Regex StripTagsRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(
        @"unusual traffic|detected unusual traffic|sorry/index|To continue, please type",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex GoogleBlockedRegex();

    [GeneratedRegex(
        @"百度安全验证|网络不给力|请输入验证码|verify",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BaiduBlockedRegex();

    [GeneratedRegex(@"<h2\b[^>]*>\s*<a\b[^>]*href=[""']([^""']+)[""'][^>]*>([\s\S]*?)</a>\s*</h2>", HtmlRegexOptions)]
    private static partial Regex BingHeadingRegex();

    [GeneratedRegex(
        @"<(div|span|p)\b[^>]*class=[""'][^""']*(?:c-abstract|content-right_[^""']*|content-right|c-span-last|c-color-text|result-op[^""']*)[^""']*[""'][^>]*>([\s\S]*?)</\1>",
        HtmlRegexOptions
    )]
    private static partial Regex BaiduSnippetRegex();

    [GeneratedRegex(
        @"<div\b[^>]*class=[""'][^""']*(?:VwiC3b|yXK7lf|MUxGbd|kvH3mc)[^""']*[""'][^>]*>([\s\S]*?)</div>",
        HtmlRegexOptions
    )]
    private static partial Regex GoogleSnippetDivRegex();

    [GeneratedRegex(
        @"<span\b[^>]*class=[""'][^""']*(?:aCOpRe|hgKElc)[^""']*[""'][^>]*>([\s\S]*?)</span>",
        HtmlRegexOptions
    )]
    private static partial Regex GoogleSnippetSpanRegex();

    [GeneratedRegex(@"<div\b[^>]*data-sncf=[""'][^""']*[""'][^>]*>([\s\S]*?)</div>", HtmlRegexOptions)]
    private static partial Regex GoogleSnippetDataRegex();

    [GeneratedRegex(
        @"<div\b[^>]*class=[""'][^""']*b_caption[^""']*[""'][^>]*>[\s\S]*?<p\b[^>]*>([\s\S]*?)</p>",
        HtmlRegexOptions
    )]
    private static partial Regex BingCaptionSnippetRegex();

    [GeneratedRegex(@"<p\b[^>]*>([\s\S]*?)</p>", HtmlRegexOptions)]
    private static partial Regex BingParagraphSnippetRegex();

    [Description(
        """
            Allows searching the web for current information. Provides search results and/or text commentary.
            """
    )]
    public async Task<WebSearchResult> ExecuteAsync(WebSearchToolParams toolParams, CancellationToken cancellationToken)
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
        var searchResponse = await PerformSearchAsync(
                toolParams.Query,
                maxResults,
                toolParams.AllowedDomains,
                toolParams.BlockedDomains,
                cancellationToken
            )
            .ConfigureAwait(false);

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

    private async Task<SearchProviderResponse> PerformSearchAsync(
        string query,
        int maxResults,
        List<string>? allowedDomains,
        List<string>? blockedDomains,
        CancellationToken cancellationToken
    )
    {
        using var client = _httpClientFactory.CreateClient();
        var failures = new List<string>();

        foreach (var provider in new[] { SearchProvider.Bing, SearchProvider.Google, SearchProvider.Baidu })
        {
            try
            {
                var results = provider switch
                {
                    SearchProvider.Google => await SearchGoogleAsync(
                            client,
                            query,
                            maxResults,
                            allowedDomains,
                            blockedDomains,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
                    SearchProvider.Bing => await SearchBingAsync(
                            client,
                            query,
                            maxResults,
                            allowedDomains,
                            blockedDomains,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
                    SearchProvider.Baidu => await SearchBaiduAsync(
                            client,
                            query,
                            maxResults,
                            allowedDomains,
                            blockedDomains,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
                    _ => [],
                };

                return new SearchProviderResponse(provider.ToString().ToLowerInvariant(), results);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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

    private static async Task<List<SearchHit>> SearchGoogleAsync(
        HttpClient client,
        string query,
        int maxResults,
        List<string>? allowedDomains,
        List<string>? blockedDomains,
        CancellationToken cancellationToken
    )
    {
        var searchUrl = $"https://www.google.com/search?hl=en&num={maxResults}&gbv=1&q={WebUtility.UrlEncode(query)}";
        var html = await SendSearchRequestAsync(client, "Google", searchUrl, "en-US,en;q=0.9", cancellationToken)
            .ConfigureAwait(false);

        if (GoogleBlockedRegex().IsMatch(html))
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

    private static async Task<List<SearchHit>> SearchBingAsync(
        HttpClient client,
        string query,
        int maxResults,
        List<string>? allowedDomains,
        List<string>? blockedDomains,
        CancellationToken cancellationToken
    )
    {
        var searchUrl = $"https://www.bing.com/search?q={WebUtility.UrlEncode(query)}&count={maxResults}";
        var html = await SendSearchRequestAsync(client, "Bing", searchUrl, "en-US,en;q=0.9", cancellationToken)
            .ConfigureAwait(false);

        return EnsureResults("Bing", ExtractBingResults(html, maxResults), maxResults, allowedDomains, blockedDomains);
    }

    private static async Task<List<SearchHit>> SearchBaiduAsync(
        HttpClient client,
        string query,
        int maxResults,
        List<string>? allowedDomains,
        List<string>? blockedDomains,
        CancellationToken cancellationToken
    )
    {
        var searchUrl = $"https://www.baidu.com/s?wd={WebUtility.UrlEncode(query)}&rn={maxResults}";
        var html = await SendSearchRequestAsync(
                client,
                "Baidu",
                searchUrl,
                "zh-CN,zh;q=0.9,en;q=0.8",
                cancellationToken
            )
            .ConfigureAwait(false);

        if (BaiduBlockedRegex().IsMatch(html))
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

    private static async Task<string> SendSearchRequestAsync(
        HttpClient client,
        string provider,
        string searchUrl,
        string acceptLanguage,
        CancellationToken cancellationToken
    )
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(RequestTimeoutMs));
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new AgwException(ErrorCodes.FetchFailed, $"{provider} search error: {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    private static List<SearchHit> ExtractGoogleResults(string html, int maxResults)
    {
        var headings = GoogleHeadingRegex().Matches(html);
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
            var snippet = ExtractSnippet(section, GoogleSnippetRegexes, title);

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
        var blocks = BingBlockRegex().Matches(html);
        var results = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match block in blocks)
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            var section = block.Groups[1].Value;
            var headingMatch = BingHeadingRegex().Match(section);
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

            var snippet = ExtractSnippet(section, BingSnippetRegexes, title);

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
        var headings = BaiduHeadingRegex().Matches(html);
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
            var snippetMatches = BaiduSnippetRegex().Matches(section);
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

    private static string ExtractSnippet(string section, Regex[] patterns, string title)
    {
        foreach (var pattern in patterns)
        {
            var match = pattern.Match(section);
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
        var withoutScripts = StripScriptStyleRegex().Replace(decoded, " ");
        var withoutTags = StripTagsRegex().Replace(withoutScripts, " ");
        return CollapseWhitespaceRegex().Replace(withoutTags, " ").Trim();
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
