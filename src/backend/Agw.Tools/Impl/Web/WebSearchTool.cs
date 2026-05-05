using System.Diagnostics;
using System.Net;

using Agw.Shared.Contracts.Tools.Abstractions;
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
}

public class SearchHit
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

public class WebSearchResult
{
    public string Query { get; set; } = "";
    public List<object> Results { get; set; } = new();
    public double DurationSeconds { get; set; }
}

internal class WebSearchTool : IAgwTool
{
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
            throw new AgwException(ErrorCodes.InvalidParameters, "Cannot specify both allowed_domains and blocked_domains in the same request.");
        }

        var stopwatch = Stopwatch.StartNew();

        // Use a search engine API (DuckDuckGo HTML or similar)
        var searchResults = PerformSearch(toolParams.Query, toolParams.AllowedDomains, toolParams.BlockedDomains);

        stopwatch.Stop();
        var durationSeconds = stopwatch.ElapsedMilliseconds / 1000.0;

        var results = new List<object>();
        results.AddRange(searchResults);

        return new WebSearchResult
        {
            Query = toolParams.Query,
            Results = results,
            DurationSeconds = durationSeconds
        };
    }

    public AITool ToAITool()
    {
        Func<WebSearchToolParams, WebSearchResult> func = Execute;
        var aiTool = AIFunctionFactory.Create(func, Name);
        return aiTool;
    }

    private static List<SearchHit> PerformSearch(string query, List<string>? allowedDomains, List<string>? blockedDomains)
    {
        var hits = new List<SearchHit>();

        // Use DuckDuckGo lite HTML search
        var encodedQuery = WebUtility.UrlEncode(query);
        var url = $"https://html.duckduckgo.com/html/?q={encodedQuery}";

        var httpClientFactory = IocUtil.GetSingletonRequiredService<IHttpClientFactory>();
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; AgwBot/1.0)");

        try
        {
            var response = client.GetAsync(url).GetAwaiter().GetResult();
            var html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            // Simple HTML parsing to extract results
            hits = ParseDuckDuckGoResults(html, allowedDomains, blockedDomains);
        }
        catch
        {
            // Return empty results on failure
        }

        return hits;
    }

    private static List<SearchHit> ParseDuckDuckGoResults(string html, List<string>? allowedDomains, List<string>? blockedDomains)
    {
        var hits = new List<SearchHit>();

        // Simple regex-based parsing for DuckDuckGo HTML results
        var resultPattern = @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>";
        var matches = System.Text.RegularExpressions.Regex.Matches(html, resultPattern, System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Groups.Count >= 3)
            {
                var href = System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, "<[^>]+>", "").Trim();
                var title = System.Text.RegularExpressions.Regex.Replace(match.Groups[2].Value, "<[^>]+>", "").Trim();

                // DuckDuckGo uses redirect URLs
                if (href.StartsWith("//duckduckgo.com/l/?"))
                {
                    var uri = new Uri("https:" + href);
                    var query = uri.Query.TrimStart('?');
                    var uddg = ParseQueryParameter(query, "uddg");
                    href = uddg ?? href;
                }

                if (!string.IsNullOrEmpty(href) && !href.StartsWith("/"))
                {
                    var domain = new Uri(href).Host;

                    // Apply domain filters
                    if (allowedDomains != null && allowedDomains.Count > 0)
                    {
                        if (!allowedDomains.Any(d => domain.Contains(d, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }

                    if (blockedDomains != null && blockedDomains.Count > 0)
                    {
                        if (blockedDomains.Any(d => domain.Contains(d, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }

                    hits.Add(new SearchHit
                    {
                        Title = WebUtility.HtmlDecode(title),
                        Url = href
                    });
                }
            }
        }

        return hits.Take(10).ToList();
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
}
