using System.Diagnostics;
using System.Net;

using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Tools.Impl.Web;

public class WebFetchToolParams
{
    [Description(
        """
        The URL to fetch content from.
        """
    )]
    public string Url { get; set; } = "";

    [Description(
        """
        The prompt to run on the fetched content.
        """
    )]
    public string Prompt { get; set; } = "";
}

public class WebFetchToolResult
{
    public long Bytes { get; set; }
    public int Code { get; set; }
    public string CodeText { get; set; } = "";
    public string Result { get; set; } = "";
    public long DurationMs { get; set; }
    public string Url { get; set; } = "";
}

internal class WebFetchTool : IAgwTool
{
    public string Name => "web_fetch";

    public string Category => "Web";

    public bool AllowInPlanMode => true;

    [Description(
        """
        Fetches content from a specified URL and processes it using the provided prompt.
        IMPORTANT: WebFetch WILL FAIL for authenticated or private URLs. Before using this tool,
        check if the URL points to an authenticated service (e.g. Google Docs, Confluence, Jira, GitHub).
        If so, look for a specialized MCP tool that provides authenticated access.
        """
    )]
    public WebFetchToolResult Execute(WebFetchToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Url))
        {
            throw new AgwException(ErrorCodes.UrlRequired, "URL is required.");
        }

        if (!Uri.TryCreate(toolParams.Url, UriKind.Absolute, out var uri))
        {
            throw new AgwException(ErrorCodes.InvalidUrl, $"Invalid URL '{toolParams.Url}'.");
        }

        var stopwatch = Stopwatch.StartNew();

        var httpClientFactory = IocUtil.GetSingletonRequiredService<IHttpClientFactory>();
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; AgwBot/1.0)");

        HttpResponseMessage response;
        try
        {
            response = client.GetAsync(uri).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            var logger = IocUtil.CreateLogger<WebFetchTool>();
            logger.LogError(ex, "HTTP request failed for URL: {Url}", toolParams.Url);
            throw new AgwException(ErrorCodes.FetchFailed, $"Failed to fetch URL: {ex.Message}");
        }

        var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        var codeText = response.StatusCode.ToString();

        // Handle redirects
        if (response.StatusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect)
        {
            var redirectUrl = response.Headers.Location?.ToString() ?? "Unknown";
            var message = $"REDIRECT DETECTED: The URL redirects to a different host.\n\nOriginal URL: {toolParams.Url}\nRedirect URL: {redirectUrl}\nStatus: {(int)response.StatusCode} {codeText}\n\nTo complete your request, use WebFetch again with url: \"{redirectUrl}\"";

            stopwatch.Stop();
            return new WebFetchToolResult
            {
                Bytes = System.Text.Encoding.UTF8.GetByteCount(message),
                Code = (int)response.StatusCode,
                CodeText = codeText,
                Result = message,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Url = toolParams.Url
            };
        }

        // Apply prompt to content (simplified: truncate if too long)
        var result = ApplyPromptToContent(toolParams.Prompt, content);

        stopwatch.Stop();

        return new WebFetchToolResult
        {
            Bytes = bytes,
            Code = (int)response.StatusCode,
            CodeText = codeText,
            Result = result,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Url = toolParams.Url
        };
    }

    public AITool ToAITool()
    {
        Func<WebFetchToolParams, WebFetchToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }

    private static string ApplyPromptToContent(string prompt, string content)
    {
        // Simplified implementation: return truncated content with prompt context
        const int maxLength = 100_000;

        if (content.Length > maxLength)
        {
            content = content[..maxLength] + "\n\n[Content truncated due to length]";
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            return $"Prompt: {prompt}\n\n---\n\n{content}";
        }

        return content;
    }
}
