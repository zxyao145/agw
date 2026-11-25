using System.Formats.Tar;
using System.Text;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Shared.Exceptions;
using SharpCompress.Archives.Zip;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Readers.GZip;

namespace Agw.Agents.Infrastructure.Acp;

internal sealed class AcpArchiveExtractor
{
    private const int UnixTypeMask = 0xf000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixSymbolicLink = 0xa000;
    private const int UnixDirectory = 0x4000;
    private const int MaxLinkBytes = 4096;
    private readonly string _root;
    private readonly List<(string Path, string Target, bool Hard)> _links = [];

    private AcpArchiveExtractor(string root) => _root = Path.GetFullPath(root);

    public static async Task ExtractAsync(string archive, string urlPath, string root, CancellationToken ct)
    {
        var extractor = new AcpArchiveExtractor(root);
        await using var file = File.OpenRead(archive);
        if (urlPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            await extractor.ExtractZipAsync(file, ct);
        else
            await extractor.ExtractTarAsync(file, urlPath, ct);
        extractor.CreateLinks(ct);
    }

    private async Task ExtractZipAsync(Stream input, CancellationToken ct)
    {
        await using var zip = await ZipArchive.OpenAsyncArchive(input, cancellationToken: ct);
        await foreach (var entry in zip.EntriesAsync.WithCancellation(ct))
        {
            var name = entry.Key ?? throw new AgwException(ErrorCodes.InvalidParam, "Missing ZIP entry name.");
            if (name is "./" or ".")
                continue;
            var path = Destination(name);
            var mode = (entry.Attrib ?? 0) >> 16;
            var type = mode & UnixTypeMask;
            if (type == UnixSymbolicLink)
            {
                if (entry.Size > MaxLinkBytes)
                    throw new AgwException(ErrorCodes.InvalidParam, "Invalid ZIP symbolic link target.");
                using var target = new StreamReader(await entry.OpenEntryStreamAsync(ct), Encoding.UTF8);
                AddLink(path, await target.ReadToEndAsync(ct), false);
            }
            else if (entry.IsDirectory || type == UnixDirectory)
                Directory.CreateDirectory(path);
            else if (type is 0 or UnixRegularFile)
            {
                await using var content = await entry.OpenEntryStreamAsync(ct);
                await WriteFileAsync(path, content, (UnixFileMode)mode, ct);
            }
            else
                throw new AgwException(ErrorCodes.InvalidParam, $"Unsupported ZIP entry type: {type}.");
        }
    }

    private async Task ExtractTarAsync(Stream input, string urlPath, CancellationToken ct)
    {
        var bzip =
            urlPath.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase)
            || urlPath.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase);
        if (bzip)
        {
            await using var unpacked = BZip2Stream.Create(input, CompressionMode.Decompress, true, true);
            await ReadTarEntriesAsync(unpacked, ct);
            return;
        }
        await using var gzip = await GZipReader.OpenAsyncReader(input, cancellationToken: ct);
        if (!await gzip.MoveToNextEntryAsync(ct))
            throw new AgwException(ErrorCodes.InvalidParam, "Missing tar data in gzip archive.");
        await using var content = await gzip.OpenEntryStreamAsync(ct);
        await ReadTarEntriesAsync(content, ct);
        await content.CopyToAsync(Stream.Null, ct);
    }

    private async Task ReadTarEntriesAsync(Stream unpacked, CancellationToken ct)
    {
        using var tar = new TarReader(unpacked, leaveOpen: true);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (
                entry.EntryType == TarEntryType.GlobalExtendedAttributes
                || (entry.EntryType == TarEntryType.Directory && entry.Name is "." or "./")
            )
                continue;
            var path = Destination(entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    await WriteFileAsync(path, entry.DataStream ?? Stream.Null, entry.Mode, ct);
                    break;
                case TarEntryType.SymbolicLink:
                case TarEntryType.HardLink:
                    AddLink(path, entry.LinkName, entry.EntryType == TarEntryType.HardLink);
                    break;
                default:
                    throw new AgwException(ErrorCodes.InvalidParam, $"Unsupported tar entry type: {entry.EntryType}.");
            }
        }
    }

    private string Destination(string entry) => Path.Combine(_root, AcpRegistryRules.NormalizeCmd(entry));

    private static async Task WriteFileAsync(string path, Stream input, UnixFileMode archiveMode, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(file, ct);
        var execute = archiveMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        if (!OperatingSystem.IsWindows() && execute != 0)
            File.SetUnixFileMode(file.SafeFileHandle, File.GetUnixFileMode(file.SafeFileHandle) | execute);
    }

    private void AddLink(string path, string target, bool hard)
    {
        target = target.Replace('\\', '/');
        if (string.IsNullOrEmpty(target) || target.StartsWith('/') || target.Contains(':') || target.Contains('\0'))
            throw new AgwException(ErrorCodes.InvalidParam, "Archive link target must be relative.");
        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment is not ("." or ".."))
                AcpRegistryRules.NormalizeCmd(segment);
        var parent = Path.GetDirectoryName(path)!;
        var destination = Path.GetFullPath(target, hard ? _root : parent);
        if (
            destination != _root
            && !destination.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        )
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Archive link target is outside the installation directory."
            );
        Directory.CreateDirectory(parent);
        _links.Add((path, destination, hard));
    }

    private void CreateLinks(CancellationToken ct)
    {
        // 先写入普通文件，再创建链接。 Write regular files before exposing any archive links.
        foreach (var (path, target, hard) in _links.OrderByDescending(link => link.Hard))
        {
            ct.ThrowIfCancellationRequested();
            if (hard)
                File.Copy(target, path);
            else
            {
                var relative = Path.GetRelativePath(Path.GetDirectoryName(path)!, target);
                if (Directory.Exists(target))
                    Directory.CreateSymbolicLink(path, relative);
                else
                    File.CreateSymbolicLink(path, relative);
            }
        }
    }
}
