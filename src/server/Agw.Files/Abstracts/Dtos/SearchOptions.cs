namespace Agw.Files.Abstracts.Dtos;

public sealed record SearchOptions(
    string Pattern,
    bool IsRegex = true,
    bool CaseInsensitive = false,
    bool Multiline = false,
    IReadOnlyList<string>? IncludeExtensions = null,
    string? FilenameGlob = null,
    int? MaxHits = null);
