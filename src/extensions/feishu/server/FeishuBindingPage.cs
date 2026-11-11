using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agw.Extensions.Sdk;
using Microsoft.AspNetCore.Http;

namespace Agw.Extensions.Feishu;

public static class FeishuBindingPage
{
    private static readonly string ExtensionIdJson = JsonSerializer.Serialize(
        FeishuExtension.ExtensionId.ToString("D"),
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default }
    );

    public static ExtensionEndpointResponse Confirmation()
    {
        string html = $$"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Confirm Feishu account</title>
                <style>
                  body { font: 16px system-ui, sans-serif; margin: 0; padding: 48px 20px; color: #17202a; background: #f5f7fa; }
                  main { max-width: 520px; margin: auto; padding: 28px; border: 1px solid #d9e0e8; border-radius: 12px; background: white; }
                  button { padding: 10px 18px; border: 0; border-radius: 8px; color: white; background: #1769aa; font: inherit; cursor: pointer; }
                  p { line-height: 1.5; }
                </style>
              </head>
              <body>
                <main>
                  <h1>Confirm Feishu account</h1>
                  <p>Confirm that this Feishu account may answer notifications in Agw.</p>
                  <button id="confirm" type="button">Confirm account link</button>
                  <p id="status" role="status"></p>
                </main>
                <script>
                  const extensionId = {{ExtensionIdJson}};
                  const statusElement = document.getElementById("status");
                  document.getElementById("confirm").addEventListener("click", async () => {
                    const code = new URLSearchParams(location.search).get("code");
                    if (!code) {
                      statusElement.textContent = "This binding link is invalid.";
                      return;
                    }
                    try {
                      const antiforgeryUrl = new URL("../../../auth/antiforgery", location.href);
                      const tokenResponse = await fetch(antiforgeryUrl, { credentials: "same-origin" });
                      const tokenEnvelope = await tokenResponse.json();
                      const requestToken = tokenEnvelope.data?.requestToken;
                      if (!tokenResponse.ok || !requestToken) {
                        statusElement.textContent = "Sign in to Agw before confirming this link.";
                        return;
                      }
                      const confirmUrl = new URL("confirm", location.href);
                      const response = await fetch(confirmUrl, {
                        method: "POST",
                        credentials: "same-origin",
                        headers: {
                          "Content-Type": "application/json",
                          "X-Agw-Extension-Id": extensionId,
                          "X-CSRF-TOKEN": requestToken
                        },
                        body: JSON.stringify({ code })
                      });
                      const envelope = await response.json();
                      statusElement.textContent = response.ok && envelope.code === 0
                        ? "The Feishu account is linked to Agw."
                        : envelope.title ?? "Agw could not confirm this link.";
                    } catch {
                      statusElement.textContent = "Agw could not confirm this link.";
                    }
                  });
                </script>
              </body>
            </html>
            """;
        return new ExtensionEndpointResponse
        {
            StatusCode = StatusCodes.Status200OK,
            ContentType = "text/html; charset=utf-8",
            BodyStream = StreamAsync(html),
        };
    }

    public static ExtensionEndpointResponse NotFound() =>
        new()
        {
            StatusCode = StatusCodes.Status404NotFound,
            ContentType = "text/html; charset=utf-8",
            BodyStream = StreamAsync(
                "<!doctype html><html lang=\"en\"><body><p>This binding link is invalid or expired.</p></body></html>"
            ),
        };

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAsync(
        string content,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return Encoding.UTF8.GetBytes(content);
    }
}
