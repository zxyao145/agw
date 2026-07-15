using System.Security.Cryptography;

using Agw.Integrations.Infrastructure.Credentials;

using Microsoft.AspNetCore.DataProtection;

namespace Agw.Integrations.Tests;

public class DataProtectionConnectionCredentialProtectorTests
{
    [Fact]
    public void Protect_Plaintext_ReturnsProtectedValueThatRoundTrips()
    {
        var protector = new DataProtectionConnectionCredentialProtector(
            new EphemeralDataProtectionProvider());
        const string plaintext = "github-access-token";

        var protectedValue = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, protectedValue);
        Assert.DoesNotContain(plaintext, protectedValue, StringComparison.Ordinal);
        Assert.Equal(plaintext, protector.Unprotect(protectedValue));
    }

    [Fact]
    public void Unprotect_TamperedProtectedValue_Throws()
    {
        var protector = new DataProtectionConnectionCredentialProtector(
            new EphemeralDataProtectionProvider());
        var protectedValue = protector.Protect("github-access-token");
        var tamperIndex = protectedValue.Length / 2;
        var tamperedCharacters = protectedValue.ToCharArray();
        tamperedCharacters[tamperIndex] = tamperedCharacters[tamperIndex] == 'A' ? 'B' : 'A';
        var tamperedValue = new string(tamperedCharacters);

        Assert.Throws<CryptographicException>(() => protector.Unprotect(tamperedValue));
    }

    [Fact]
    public void Protect_ConnectionCredentialPurpose_CannotBeReadByAnotherPurpose()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new DataProtectionConnectionCredentialProtector(provider);
        var protectedValue = protector.Protect("github-access-token");

        Assert.Throws<CryptographicException>(() =>
            provider.CreateProtector("Agw.Integrations.OtherPurpose.v1").Unprotect(protectedValue));
    }
}
