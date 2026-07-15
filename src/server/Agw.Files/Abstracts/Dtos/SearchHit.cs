namespace Agw.Files.Abstracts.Dtos;

public sealed record SearchHit(
    string Path,
    int LineNumber,
    string Line);
