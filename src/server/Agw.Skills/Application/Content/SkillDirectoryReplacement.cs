namespace Agw.Skills.Application.Content;

internal static class SkillDirectoryReplacement
{
    private const string BackupSuffix = ".backup-";

    public static string Backup(string directory, Guid revision)
    {
        var backupPath = $"{directory}{BackupSuffix}{revision:N}";
        if (Directory.Exists(directory))
        {
            Directory.Move(directory, backupPath);
        }
        else
        {
            // 原目录缺失时，用空文件记录这次更新。
            // An empty file records the update when the original directory is absent.
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            using var marker = File.OpenHandle(backupPath, FileMode.CreateNew, FileAccess.Write);
        }

        return backupPath;
    }

    public static void Recover(string directory, Guid? committedRevision)
    {
        var parent = Directory.GetParent(directory)!;
        if (!parent.Exists)
        {
            return;
        }

        var prefix = Path.GetFileName(directory) + BackupSuffix;
        var backupPath = Directory
            .EnumerateFileSystemEntries(
                parent.FullName,
                prefix + "*",
                new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }
            )
            .SingleOrDefault(path => Guid.TryParseExact(Path.GetFileName(path).AsSpan(prefix.Length), "N", out _));
        if (backupPath == null)
        {
            return;
        }

        if (Guid.ParseExact(Path.GetFileName(backupPath).AsSpan(prefix.Length), "N") == committedRevision)
        {
            Complete(backupPath);
            return;
        }

        RollBack(directory, backupPath);
    }

    public static void Complete(string? backupPath)
    {
        if (backupPath == null)
        {
            return;
        }

        if (Directory.Exists(backupPath))
        {
            Directory.Delete(backupPath, recursive: true);
        }
        else
        {
            File.Delete(backupPath);
        }
    }

    public static void RollBack(string directory, string backupPath)
    {
        var hasBackupDirectory = Directory.Exists(backupPath);
        if (!hasBackupDirectory && !File.Exists(backupPath))
        {
            return;
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        if (hasBackupDirectory)
        {
            Directory.Move(backupPath, directory);
        }
        else
        {
            File.Delete(backupPath);
        }
    }
}
