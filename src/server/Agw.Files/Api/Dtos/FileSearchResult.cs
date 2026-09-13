namespace Agw.Files.Api.Dtos;

public class FileSearchResult
{
    public Guid? DirectoryId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}
