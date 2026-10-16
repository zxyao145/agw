using System.Net.Http.Headers;
using Agw.Providers.Contracts.References;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace Agw.Agents.Execution.Agents.Runtime;

/// <summary>
/// <para>为 Agent 与摘要创建访问原生 Ollama API 的 ChatClient。</para>
/// <para>Creates chat clients for the native Ollama API, shared by agents and summaries.</para>
/// </summary>
public sealed class OllamaChatClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OllamaChatClientFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// <para>创建 ChatClient；释放返回的 ChatClient 时一并释放传入 SDK 的 HttpClient。</para>
    /// <para>Creates a chat client whose disposal also disposes the HttpClient passed to the SDK.</para>
    /// </summary>
    public IChatClient CreateClient(ModelProviderModelSnapshot model, ModelProviderProviderSnapshot provider)
    {
        // SDK 使用 api/chat 等相对路径，BaseAddress 以 / 结尾才能保留 Endpoint 的路径前缀。
        // The SDK sends relative paths such as api/chat; a trailing slash keeps the endpoint path prefix.
        if (
            !Uri.TryCreate($"{provider.Endpoint.Trim().TrimEnd('/')}/", UriKind.Absolute, out var baseAddress)
            || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new AgwException(ErrorCodes.InvalidUrl, "Provider endpoint must be an absolute HTTP(S) URL.");
        }

        // 只有启用且非空的 API Key 参与随机选择；没有时使用无认证连接。
        // Only enabled, non-empty API keys take part in random selection; otherwise connect without auth.
        var apiKeys = provider
            .AuthConfigs.Where(config => config.Enable && !string.IsNullOrWhiteSpace(config.ApiKey))
            .Select(config => config.ApiKey!.Trim())
            .ToList();
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = baseAddress;
        if (apiKeys.Count > 0)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                apiKeys[Random.Shared.Next(apiKeys.Count)]
            );
        }

        return new ChatClientBuilder(new CancellableOllamaApiClient(httpClient, model.Name))
            .Use(innerClient => new HttpClientOwningChatClient(innerClient, httpClient))
            .ConfigureOptions(options =>
            {
                // SDK 将 MaxOutputTokens 映射为 num_predict，其中包含 thinking 和回答。
                // The SDK maps MaxOutputTokens to num_predict, which covers both thinking and the answer.
                options.MaxOutputTokens ??= model.MaxOutputTokens;
                options.AddOllamaOption(OllamaOption.NumCtx, model.MaxContextWindowTokens);
            })
            .Build();
    }

    /// <summary>
    /// <para>让流式响应的正文读取遵循请求的取消令牌。</para>
    /// <para>Makes streaming response body reads observe the request cancellation token.</para>
    /// </summary>
    /// <remarks>
    /// <para>OllamaSharp 逐行读取时不传入令牌；Ollama 生成工具调用等期间暂时没有输出，取消后读取会一直等到下一行到达。</para>
    /// <para>OllamaSharp reads each line without a token; while Ollama sends no output, such as during tool-call
    /// generation, a canceled read waits until the next line arrives.</para>
    /// </remarks>
    private sealed class CancellableOllamaApiClient : OllamaApiClient
    {
        public CancellableOllamaApiClient(HttpClient client, string defaultModel)
            : base(client, defaultModel) { }

        protected override async Task<HttpResponseMessage> SendToOllamaAsync(
            HttpRequestMessage requestMessage,
            OllamaRequest? ollamaRequest,
            HttpCompletionOption completionOption,
            CancellationToken cancellationToken
        )
        {
            var response = await base.SendToOllamaAsync(
                    requestMessage,
                    ollamaRequest,
                    completionOption,
                    cancellationToken
                )
                .ConfigureAwait(false);
            // ResponseHeadersRead 时 SendAsync 的令牌只作用到响应头结束，正文读取需要另外绑定同一令牌。
            // With ResponseHeadersRead the SendAsync token ends at the headers, so body reads bind the same token.
            if (completionOption != HttpCompletionOption.ResponseHeadersRead)
            {
                return response;
            }

            try
            {
                var content = response.Content;
                var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var boundContent = new StreamContent(new CancellationBoundStream(stream, cancellationToken));
                foreach (var header in content.Headers)
                {
                    boundContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                response.Content = boundContent;
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// <para>释放 SDK ChatClient 后释放其使用的 HttpClient。</para>
    /// <para>Disposes the HttpClient after the SDK chat client that uses it.</para>
    /// </summary>
    private sealed class HttpClientOwningChatClient : DelegatingChatClient
    {
        private readonly HttpClient _httpClient;

        public HttpClientOwningChatClient(IChatClient innerClient, HttpClient httpClient)
            : base(innerClient)
        {
            _httpClient = httpClient;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _httpClient.Dispose();
            }
        }
    }
}
