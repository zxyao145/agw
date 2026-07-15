namespace Agw.Infrastructure.Data.Encryption;

public interface IEncryptedDataProtector
{
    string Protect(string tableName, Guid entityId, string plaintext);

    string Unprotect(string tableName, Guid entityId, string protectedValue);
}
