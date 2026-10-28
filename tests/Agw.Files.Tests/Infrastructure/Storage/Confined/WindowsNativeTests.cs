using System.Runtime.InteropServices;
using Agw.Files.Abstracts;

namespace Agw.Files.Tests.Infrastructure.Storage.Confined;

public sealed class WindowsNativeTests
{
    private const string WindowsNativeTypeName = "Agw.Files.Infrastructure.Storage.Confined.WindowsNative";

    [Fact]
    public void IoStatusBlock_Marshalled_UsesNativePointerLayout()
    {
        var type = GetNativeType("IoStatusBlock");

        Assert.Equal(IntPtr.Size * 2, Marshal.SizeOf(type));
        Assert.Equal(0, Marshal.OffsetOf(type, "StatusOrPointer").ToInt32());
        Assert.Equal(IntPtr.Size, Marshal.OffsetOf(type, "Information").ToInt32());
    }

    [Fact]
    public void FileRenameInformationHeader_Marshalled_UsesNativeFieldOffsets()
    {
        var type = GetNativeType("FileRenameInfoHeader");

        Assert.Equal(IntPtr.Size * 2 + 8, Marshal.SizeOf(type));
        Assert.Equal(IntPtr.Size, Marshal.OffsetOf(type, "RootDirectory").ToInt32());
        Assert.Equal(IntPtr.Size * 2, Marshal.OffsetOf(type, "FileNameLength").ToInt32());
        Assert.Equal(IntPtr.Size * 2 + sizeof(uint), Marshal.OffsetOf(type, "FileName").ToInt32());
    }

    private static Type GetNativeType(string nestedTypeName)
    {
        var type = typeof(IAgwFileSystem).Assembly.GetType($"{WindowsNativeTypeName}+{nestedTypeName}");
        Assert.NotNull(type);
        return type;
    }
}
