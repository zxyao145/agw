namespace Agw.Shared.Contracts.Storage;

public sealed class FileStorageOptions
{
    public FileStorageType Type { get; set; } = FileStorageType.Local;
    public LocalFileStorageOptions? Local { get; set; }
    public SftpFileStorageOptions? Sftp { get; set; }
}

public sealed class LocalFileStorageOptions
{
    public string RootPath { get; set; } = "";
}

public sealed class SftpFileStorageOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string AuthType { get; set; } = "password";
    public string? Password { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? Passphrase { get; set; }
    public string RootPath { get; set; } = "";
}
