using System.Net;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Infrastructure.Acp;

public sealed class AcpDownloadClient
{
    public const string ClientName = "acp-registry";
    private const int MaxRedirects = 5;
    private const int CopyBufferSize = 81920;
    private readonly IHttpClientFactory _factory;

    public AcpDownloadClient(IHttpClientFactory factory) => _factory = factory;

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var client = _factory.CreateClient(ClientName);
        var uri = request.RequestUri!;
        for (var redirects = 0; redirects <= MaxRedirects; redirects++)
        {
            if (!AcpRegistryRules.IsAllowedDownloadUri(uri))
                throw new AgwException(ErrorCodes.AcpDownloadFailed, "ACP download URL is not allowed.");
            using var next = new HttpRequestMessage(request.Method, uri);
            foreach (var header in request.Headers)
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await client.SendAsync(next, HttpCompletionOption.ResponseHeadersRead, ct);
            if (
                response.StatusCode
                is not (
                    HttpStatusCode.MovedPermanently
                    or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect
                )
            )
                return response;
            using (response)
            {
                var location =
                    response.Headers.Location
                    ?? throw new AgwException(ErrorCodes.AcpDownloadFailed, "ACP redirect has no Location.");
                uri = new Uri(uri, location);
            }
        }
        throw new AgwException(ErrorCodes.AcpDownloadFailed, $"ACP download exceeded {MaxRedirects} redirects.");
    }

    internal static async Task<long> CopyLimitedAsync(
        HttpContent content,
        Stream output,
        long limit,
        System.Security.Cryptography.IncrementalHash? hash,
        CancellationToken ct
    )
    {
        if (content.Headers.ContentLength > limit)
            throw new AgwException(ErrorCodes.AcpDownloadFailed, $"ACP download exceeds {limit} bytes.");
        await using var input = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > limit)
                throw new AgwException(ErrorCodes.AcpDownloadFailed, $"ACP download exceeds {limit} bytes.");
            hash?.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return total;
    }
}
