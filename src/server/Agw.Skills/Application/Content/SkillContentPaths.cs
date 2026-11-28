using System.Security.Cryptography;
using System.Text;

namespace Agw.Skills.Application.Content;

internal static class SkillContentPaths
{
    public const string RetiredMarkerSuffix = ".retired";
    private const string OwnerDirectoryPrefix = "_user-";

    public static string GetOwnerDirectoryName(string ownerUserId) =>
        OwnerDirectoryPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ownerUserId)));

    public static bool IsOwnerDirectoryName(string directoryName) =>
        directoryName.Length == OwnerDirectoryPrefix.Length + 64
        && directoryName.StartsWith(OwnerDirectoryPrefix, StringComparison.Ordinal)
        && directoryName[OwnerDirectoryPrefix.Length..].All(char.IsAsciiHexDigitLower);
}
