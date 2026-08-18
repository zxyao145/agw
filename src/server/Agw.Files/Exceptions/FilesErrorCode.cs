namespace Agw.Files.Exceptions;

public enum FilesErrorCode
{
    InvalidParameter = 400_0001,
    PathOutsideRoot = 403_0001,
    ResourceNotFound = 404_0007,
    FileOperationFailed = 500_0025,
}
