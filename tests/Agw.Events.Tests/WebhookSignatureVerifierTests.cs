using System.Security.Cryptography;
using System.Text;
using Agw.Infrastructure.Events.Sources;
using Xunit;

namespace Agw.Events.Tests;

public sealed class WebhookSignatureVerifierTests
{
    [Fact]
    public void Verify_HmacSignature_RejectsTamperedBody()
    {
        var secret = "webhook-secret";
        var originalBody = Encoding.UTF8.GetBytes("original payload");
        var tamperedBody = Encoding.UTF8.GetBytes("changed payload");
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), originalBody));

        var valid = WebhookSignatureVerifier.VerifyHmac(originalBody, $"sha256={signature}", secret);
        var tampered = WebhookSignatureVerifier.VerifyHmac(tamperedBody, $"sha256={signature}", secret);

        Assert.True(valid);
        Assert.False(tampered);
    }
}
