namespace Agw.Files.Exceptions;

public enum FilesErrorCode
{
    InvalidParameter = 400_0001,
    PathOutsideRoot = 403_0001,
    InvalidStorageConfiguration = 500_0014,
    UnsupportedStorageBackend = 501_0008
}
