using System.Security.Cryptography;
using System.Text;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.WebUtilities;

namespace Agw.Auth.Security;

public static class DesktopLoginProof
{
    public static string CreateCode() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    public static bool IsChallenge(string? value) => value is { Length: 43 } && value.All(IsBase64Url);

    public static bool IsVerifier(string? value) =>
        value is { Length: >= 43 and <= 128 }
        && value.All(character => IsBase64Url(character) || character is '.' or '~');

    public static string Challenge(string verifier) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static void Validate(string? code, string? verifier)
    {
        if (!IsChallenge(code) || !IsVerifier(verifier))
            throw new AgwException(ErrorCodes.DesktopLoginInvalid);
    }

    public static bool Matches(string challenge, string verifier) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(challenge),
            Encoding.ASCII.GetBytes(Challenge(verifier))
        );

    private static bool IsBase64Url(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';
}
