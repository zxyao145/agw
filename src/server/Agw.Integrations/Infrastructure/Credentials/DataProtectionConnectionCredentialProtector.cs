using Agw.Integrations.Application.Credentials;

using Microsoft.AspNetCore.DataProtection;

namespace Agw.Integrations.Infrastructure.Credentials;

public sealed class DataProtectionConnectionCredentialProtector : IConnectionCredentialProtector
{
    private const string Purpose = "Agw.Integrations.ConnectionCredential.v1";

    private readonly IDataProtector _protector;

    public DataProtectionConnectionCredentialProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
