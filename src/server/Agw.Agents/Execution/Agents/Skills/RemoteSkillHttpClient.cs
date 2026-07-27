using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

using Agw.Shared.Exceptions;

using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.Skills;

public sealed class RemoteSkillHttpClient : IRemoteSkillClient
{
    public const string HttpClientName = "Agw.RemoteSkills";

    internal const long MaxResponseBytes = 100L * 1024 * 1024;
    private const int MaxSkillMarkdownBytes = 1024 * 1024;
    private const int MaxUrlLength = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Regex FrontmatterRegex = new(
        "\\A\\uFEFF?^---\\s*$(.+?)^---\\s*$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));
    private static readonly Regex YamlKeyValueRegex = new(
        "^([\\w-]+)\\s*:\\s*(?:[\"'](.+?)[\"']|(.+?))\\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));
    private readonly IHttpClientFactory _httpClientFactory;

    public RemoteSkillHttpClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string NormalizeUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            throw new AgwException(ErrorCodes.RemoteSkillUrlRequired);
        }

        if (!Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AgwException(ErrorCodes.RemoteSkillUrlInvalid);
        }

        var normalizedUrl = uri.AbsoluteUri;
        if (normalizedUrl.Length > MaxUrlLength)
        {
            throw new AgwException(ErrorCodes.RemoteSkillUrlInvalid);
        }

        return normalizedUrl;
    }

    public async Task<RemoteSkillDefinition> FetchAsync(
        string remoteUrl,
        CancellationToken cancellationToken = default)
    {
        var normalizedUrl = NormalizeUrl(remoteUrl);
        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, normalizedUrl);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new AgwException(
                    ErrorCodes.RemoteSkillFetchFailed,
                    $"Remote skill request returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new AgwException(
                    ErrorCodes.RemoteSkillResponseInvalid,
                    $"Remote skill response exceeds {MaxResponseBytes} bytes.");
            }

            var payload = await ReadResponseAsync(response.Content, cancellationToken);
            return ParseArchive(payload, cancellationToken);
        }
        catch (AgwException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgwException(ErrorCodes.RemoteSkillFetchFailed, "Remote skill request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new AgwException(
                ErrorCodes.RemoteSkillFetchFailed,
                $"Remote skill request failed: {exception.Message}");
        }
        catch (IOException exception)
        {
            throw new AgwException(
                ErrorCodes.RemoteSkillFetchFailed,
                $"Remote skill response could not be read: {exception.Message}");
        }
    }

    private static async Task<byte[]> ReadResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + count > MaxResponseBytes)
            {
                throw new AgwException(
                    ErrorCodes.RemoteSkillResponseInvalid,
                    $"Remote skill response exceeds {MaxResponseBytes} bytes.");
            }

            buffer.Write(chunk, 0, count);
        }
    }

    private static RemoteSkillDefinition ParseArchive(
        byte[] payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var skillEntries = archive.Entries
                .Where(entry => TryNormalizeArchivePath(entry.FullName, out var path) &&
                    !path.EndsWith('/') &&
                    string.Equals(
                        path[(path.LastIndexOf('/') + 1)..],
                        "SKILL.md",
                        StringComparison.Ordinal))
                .ToList();
            if (skillEntries.Count != 1)
            {
                throw InvalidResponse(
                    "Remote skill archive must contain exactly one SKILL.md file.");
            }

            var skillEntry = skillEntries[0];
            if (skillEntry.Length > MaxSkillMarkdownBytes)
            {
                throw InvalidResponse(
                    $"Remote SKILL.md exceeds {MaxSkillMarkdownBytes} bytes.");
            }

            using var entryStream = skillEntry.Open();
            using var contentStream = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = entryStream.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }

                if (contentStream.Length + count > MaxSkillMarkdownBytes)
                {
                    throw InvalidResponse(
                        $"Remote SKILL.md exceeds {MaxSkillMarkdownBytes} bytes.");
                }

                contentStream.Write(buffer, 0, count);
            }

            return ParseSkillMarkdown(StrictUtf8.GetString(contentStream.ToArray()));
        }
        catch (AgwException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException
            or DecoderFallbackException
            or ArgumentException)
        {
            throw InvalidResponse($"Remote skill archive is invalid: {exception.Message}");
        }
    }

    private static RemoteSkillDefinition ParseSkillMarkdown(string content)
    {
        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var frontmatterMatch = FrontmatterRegex.Match(normalized);
        if (!frontmatterMatch.Success)
        {
            throw InvalidResponse(
                "Remote SKILL.md must start with YAML frontmatter delimited by '---'.");
        }

        string? name = null;
        string? description = null;
        var frontmatter = frontmatterMatch.Groups[1].Value.Trim();
        foreach (Match fieldMatch in YamlKeyValueRegex.Matches(frontmatter))
        {
            var key = fieldMatch.Groups[1].Value;
            var value = fieldMatch.Groups[2].Success
                ? fieldMatch.Groups[2].Value
                : ParseYamlScalarValue(frontmatter, fieldMatch);
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        var instructions = normalized[(frontmatterMatch.Index + frontmatterMatch.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(description) ||
            string.IsNullOrWhiteSpace(instructions))
        {
            throw InvalidResponse(
                "Remote SKILL.md must contain name, description, and instruction content.");
        }

        try
        {
            _ = new AgentSkillFrontmatter(name.Trim(), description.Trim());
        }
        catch (ArgumentException exception)
        {
            throw InvalidResponse(
                $"Remote skill metadata is invalid: {exception.Message}");
        }

        return new RemoteSkillDefinition(
            name.Trim(),
            description.Trim(),
            instructions,
            []);
    }

    private static string ParseYamlScalarValue(string yamlContent, Match fieldMatch)
    {
        var value = fieldMatch.Groups[3].Value;
        if (value.Length == 0 || value[0] is not ('>' or '|'))
        {
            return value;
        }

        var preserveTrailingNewline = value.Length > 1 && value[1] == '+';
        var blockStart = yamlContent.IndexOf('\n', fieldMatch.Index + fieldMatch.Length);
        if (blockStart < 0)
        {
            return value;
        }

        var lines = new List<string>();
        using var reader = new StringReader(yamlContent[(blockStart + 1)..]);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                lines.Add(string.Empty);
                continue;
            }

            if (line[0] is not (' ' or '\t'))
            {
                break;
            }

            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var commonIndent = lines
            .Where(line => line.Length > 0)
            .Min(line => line.TakeWhile(character => character is ' ' or '\t').Count());
        var unindented = lines
            .Select(line => line.Length == 0
                ? string.Empty
                : line[Math.Min(commonIndent, line.Length)..])
            .ToArray();
        var parsed = value[0] == '|'
            ? string.Join("\n", unindented)
            : string.Join(" ", unindented.Where(line => line.Length > 0));
        return preserveTrailingNewline ? $"{parsed}\n" : parsed;
    }

    private static bool TryNormalizeArchivePath(string entryName, out string path)
    {
        path = entryName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.StartsWith('/') ||
            segments.Any(segment => segment is "." or ".."))
        {
            throw InvalidResponse("Remote skill archive contains an invalid path.");
        }

        return true;
    }

    private static AgwException InvalidResponse(string message)
    {
        return new AgwException(ErrorCodes.RemoteSkillResponseInvalid, message);
    }
}
