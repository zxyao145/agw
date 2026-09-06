namespace Agw.Auth.Contracts;

public sealed record LegacyApiTokenImport(
    Guid Id,
    string Name,
    string Prefix,
    string SecretHash,
    DateTimeOffset CreatedAt
);

public interface ILegacyApiTokenImporter
{
    Task ImportAsync(IReadOnlyList<LegacyApiTokenImport> tokens, CancellationToken cancellationToken = default);
}
