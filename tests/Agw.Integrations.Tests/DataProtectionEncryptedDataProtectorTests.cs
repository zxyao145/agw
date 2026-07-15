using Agw.Infrastructure.Data.Encryption;
using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.DataProtection;

namespace Agw.Integrations.Tests;

public class DataProtectionEncryptedDataProtectorTests
{
    [Fact]
    public void Protect_Plaintext_ReturnsV1EnvelopeThatRoundTrips()
    {
        var protector = CreateProtector();
        var entityId = Guid.NewGuid();
        const string plaintext = "github-access-token";

        var protectedValue = protector.Protect("integration_connection_credential", entityId, plaintext);

        Assert.StartsWith(DataProtectionEncryptedDataProtector.EnvelopePrefix, protectedValue, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, protectedValue, StringComparison.Ordinal);
        Assert.Equal(
            plaintext,
            protector.Unprotect("integration_connection_credential", entityId, protectedValue));
    }

    [Fact]
    public void Protect_SamePlaintext_ReturnsDifferentCiphertext()
    {
        var protector = CreateProtector();
        var entityId = Guid.NewGuid();

        var first = protector.Protect("provider_auth_config", entityId, "secret");
        var second = protector.Protect("provider_auth_config", entityId, "secret");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Unprotect_DifferentEntityOrTamperedPayload_Throws()
    {
        var protector = CreateProtector();
        var entityId = Guid.NewGuid();
        var protectedValue = protector.Protect("provider_auth_config", entityId, "secret");

        AssertEncryptedDataInvalid(() =>
            protector.Unprotect("provider_auth_config", Guid.NewGuid(), protectedValue));

        var characters = protectedValue.ToCharArray();
        characters[^1] = characters[^1] == 'A' ? 'B' : 'A';
        AssertEncryptedDataInvalid(() =>
            protector.Unprotect("provider_auth_config", entityId, new string(characters)));
    }

    [Fact]
    public void Protect_UsesExactV1EntityPurpose()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new DataProtectionEncryptedDataProtector(provider);
        var entityId = Guid.Parse("70a96ddc-1169-41e5-b739-d003f9998f71");

        var protectedValue = protector.Protect("provider_auth_config", entityId, "secret");
        var payload = protectedValue[DataProtectionEncryptedDataProtector.EnvelopePrefix.Length..];
        var plaintext = provider
            .CreateProtector("Agw.DatabaseFieldEncryption")
            .CreateProtector("v1")
            .CreateProtector("entity/provider_auth_config/70a96ddc116941e5b739d003f9998f71")
            .Unprotect(payload);

        Assert.Equal("secret", plaintext);
        AssertEncryptedDataInvalid(() =>
            protector.Unprotect("another_table", entityId, protectedValue));
    }

    [Theory]
    [InlineData("plaintext")]
    [InlineData("agwenc:v2:payload")]
    [InlineData("agwenc:v1:")]
    public void Unprotect_UnsupportedEnvelope_Throws(string value)
    {
        var protector = CreateProtector();

        AssertEncryptedDataInvalid(() =>
            protector.Unprotect("provider_auth_config", Guid.NewGuid(), value));
    }

    private static DataProtectionEncryptedDataProtector CreateProtector() =>
        new(new EphemeralDataProtectionProvider());

    private static void AssertEncryptedDataInvalid(Action action)
    {
        var exception = Assert.Throws<AgwException>(action);
        Assert.Equal(ErrorCodes.EncryptedDataInvalid.Code, exception.Code);
    }
}
