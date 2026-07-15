using Agw.Files.Exceptions;

using Renci.SshNet;

namespace Agw.Files.Application.Storage.Sftp;

public sealed class SftpFileSystemFactory
{
    public SftpFileSystem Create(SftpFileStorageOptions options)
    {
        if (options == null)
        {
            throw new AgwFilesException(FilesErrorCode.InvalidParameter, "SFTP options are required.");
        }

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new AgwFilesException(FilesErrorCode.InvalidStorageConfiguration, "SFTP host is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            throw new AgwFilesException(FilesErrorCode.InvalidStorageConfiguration, "SFTP username is required.");
        }

        ConnectionInfo connectionInfo;
        if (string.Equals(options.AuthType, "password", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Password))
            {
                throw new AgwFilesException(
                    FilesErrorCode.InvalidStorageConfiguration,
                    "SFTP password is required for password auth.");
            }

            connectionInfo = new ConnectionInfo(options.Host, options.Port, options.Username,
                new PasswordAuthenticationMethod(options.Username, options.Password));
        }
        else if (string.Equals(options.AuthType, "privateKey", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.PrivateKeyPath))
            {
                throw new AgwFilesException(
                    FilesErrorCode.InvalidStorageConfiguration,
                    "SFTP private key path is required for key auth.");
            }

            PrivateKeyFile privateKey;
            if (!string.IsNullOrWhiteSpace(options.Passphrase))
            {
                privateKey = new PrivateKeyFile(options.PrivateKeyPath, options.Passphrase);
            }
            else
            {
                privateKey = new PrivateKeyFile(options.PrivateKeyPath);
            }

            connectionInfo = new ConnectionInfo(options.Host, options.Port, options.Username,
                new PrivateKeyAuthenticationMethod(options.Username, privateKey));
        }
        else
        {
            throw new AgwFilesException(FilesErrorCode.InvalidStorageConfiguration,
                $"Unsupported SFTP auth type: {options.AuthType}. Supported values: 'password', 'privateKey'.");
        }

        var client = new SftpClient(connectionInfo);
        var rootPath = string.IsNullOrWhiteSpace(options.RootPath) ? "/" : options.RootPath;

        return new SftpFileSystem(client, rootPath);
    }
}
