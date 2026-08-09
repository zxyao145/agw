namespace Agw.Files.Api.Dtos;

public class FileItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long? Size { get; set; }
    public DateTimeOffset? ModifiedTime { get; set; }
    public string? GitStatus { get; set; }
    public string? GitStagedStatus { get; set; }
    public string? GitUnstagedStatus { get; set; }
}
