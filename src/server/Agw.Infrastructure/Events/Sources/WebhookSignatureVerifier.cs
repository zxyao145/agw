using System.Security.Cryptography;
using System.Text;

namespace Agw.Infrastructure.Events.Sources;

public static class WebhookSignatureVerifier
{
    private const string HmacPrefix = "sha256=";

    public static bool VerifyHmac(ReadOnlySpan<byte> body, string signature, string secret)
    {
        if (!signature.StartsWith(HmacPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] providedSignature;
        try
        {
            providedSignature = Convert.FromHexString(signature.AsSpan(HmacPrefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        if (providedSignature.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }

        var expectedSignature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature);
    }

    public static bool VerifyToken(string providedToken, string expectedToken)
    {
        var providedBytes = Encoding.UTF8.GetBytes(providedToken);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
