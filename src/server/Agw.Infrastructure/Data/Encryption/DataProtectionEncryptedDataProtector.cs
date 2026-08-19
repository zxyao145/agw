using System.Security.Cryptography;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.DataProtection;

namespace Agw.Infrastructure.Data.Encryption;

public sealed class DataProtectionEncryptedDataProtector : IEncryptedDataProtector
{
    public const string EnvelopePrefix = "agwenc:v1:";

    private readonly IDataProtectionProvider _dataProtectionProvider;

    public DataProtectionEncryptedDataProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _dataProtectionProvider = dataProtectionProvider;
    }

    public string Protect(string tableName, Guid entityId, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return plaintext;
        }
        var protector = CreateProtector(tableName, entityId);
        return EnvelopePrefix + protector.Protect(plaintext);
    }

    public string Unprotect(string tableName, Guid entityId, string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return protectedValue;
        }
        if (!protectedValue.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.EncryptedDataInvalid);
        }

        var payload = protectedValue[EnvelopePrefix.Length..];
        if (payload.Length == 0)
        {
            throw new AgwException(ErrorCodes.EncryptedDataInvalid);
        }

        try
        {
            return CreateProtector(tableName, entityId).Unprotect(payload);
        }
        catch (CryptographicException exception)
        {
            throw new AgwException(ErrorCodes.EncryptedDataInvalid, ErrorCodes.EncryptedDataInvalid.Message, exception);
        }
    }

    private IDataProtector CreateProtector(string tableName, Guid entityId)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new AgwException(ErrorCodes.EncryptedModelInvalid, "Encrypted entity table name is required.");
        }

        if (entityId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.EncryptedModelInvalid, "Encrypted entity ID must be a non-empty Guid.");
        }

        return _dataProtectionProvider
            .CreateProtector("Agw.DatabaseFieldEncryption")
            .CreateProtector("v1")
            .CreateProtector($"entity/{tableName}/{entityId:N}");
    }
}
