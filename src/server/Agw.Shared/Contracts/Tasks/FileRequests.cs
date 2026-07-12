namespace Agw.Shared.Contracts.Tasks;

public class FileListResponse
{
    public List<FileItem> Items { get; set; } = new();
}

public class FileItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "file" or "directory"
    public long? Size { get; set; }
    public DateTime? ModifiedTime { get; set; }
    public string? GitStatus { get; set; } // "added", "modified", "deleted", "untracked", or null
}

public class FileSearchResponse
{
    public List<FileSearchResult> Results { get; set; } = new();
}

public class FileSearchResult
{
    public string FullPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "file" or "directory"
}
