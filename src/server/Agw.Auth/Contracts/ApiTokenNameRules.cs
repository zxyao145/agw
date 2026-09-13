namespace Agw.Auth.Contracts;

public static class ApiTokenNameRules
{
    public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();
}
