using System.Globalization;
using Agw.Shared.Exceptions;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage.Confined;

internal static class FileReadRevision
{
    public static string Get(Stream stream)
    {
        if (stream is not FileStream fileStream)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The opened file stream must be a FileStream.");
        }

        var handle = fileStream.SafeFileHandle;
        return OperatingSystem.IsWindows() ? WindowsNative.GetReadRevision(handle) : GetUnixRevision(handle);
    }

    private static string GetUnixRevision(SafeFileHandle handle)
    {
        var status = UnixNative.Stat(handle);
        return string.Join(
            ":",
            status.Dev.ToString(CultureInfo.InvariantCulture),
            status.Ino.ToString(CultureInfo.InvariantCulture),
            status.Size.ToString(CultureInfo.InvariantCulture),
            status.MTime.ToString(CultureInfo.InvariantCulture),
            status.MTimeNsec.ToString(CultureInfo.InvariantCulture),
            status.CTime.ToString(CultureInfo.InvariantCulture),
            status.CTimeNsec.ToString(CultureInfo.InvariantCulture)
        );
    }
}
