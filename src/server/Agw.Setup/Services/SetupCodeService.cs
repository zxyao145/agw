using System.Security.Cryptography;

namespace Agw.Setup.Services;

public sealed class SetupCodeService
{
    private string? _code;

    public SetupCodeService()
        : this(GenerateCode())
    {
    }

    public SetupCodeService(string code)
    {
        _code = code;
    }

    public string? CurrentCode => _code;

    public bool Matches(string? candidate)
    {
        var current = _code;
        return current != null && string.Equals(current, candidate?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public bool Consume(string? candidate)
    {
        var current = _code;
        if (current == null || !string.Equals(current, candidate?.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return Interlocked.CompareExchange(ref _code, null, current) == current;
    }

    private static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(12);
        var chars = bytes.Select(value => alphabet[value % alphabet.Length]).ToArray();
        return $"{new string(chars[..4])}-{new string(chars[4..8])}-{new string(chars[8..12])}";
    }
}
