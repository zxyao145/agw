namespace Agw.Shared.Contracts.Storage;

public sealed record SearchHit(
    string Path,
    int LineNumber,
    string Line);
