namespace DSystem.Api.Contracts;

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
}
