using System.Text;
using Xunit;

namespace Agw.Extensions.Feishu.Tests;

public sealed class FeishuBindingPageTests
{
    [Fact]
    public async Task Confirmation_UrlsPreservePublicPathPrefix()
    {
        var response = FeishuBindingPage.Confirmation();
        var html = new StringBuilder();
        await foreach (ReadOnlyMemory<byte> chunk in response.BodyStream!)
            html.Append(Encoding.UTF8.GetString(chunk.Span));

        Assert.Contains("new URL(\"../../../auth/antiforgery\", location.href)", html.ToString());
        Assert.Contains("new URL(\"confirm\", location.href)", html.ToString());

        var pageUri = new Uri("https://agw.example/gateway/api/ext/feishu/bind/confirm?code=token");
        Assert.Equal("/gateway/api/auth/antiforgery", new Uri(pageUri, "../../../auth/antiforgery").AbsolutePath);
        Assert.Equal("/gateway/api/ext/feishu/bind/confirm", new Uri(pageUri, "confirm").AbsolutePath);
    }
}
