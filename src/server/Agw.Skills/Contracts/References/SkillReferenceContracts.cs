using Agw.Shared.Data.Entities.Skills;

namespace Agw.Skills.Contracts.References;

public sealed record SkillReferenceSnapshot(
    Guid Id,
    string Name,
    string Description,
    SkillKind Kind,
    string ContentPath
);

public sealed record SkillDescriptor(Guid Id, string Name, string Description);

public interface ISkillReferenceFacade
{
    Task<IReadOnlySet<Guid>> FilterVisibleSkillIdsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<SkillReferenceSnapshot>> ResolveVisibleSkillsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<SkillDescriptor>> DescribeVisibleSkillsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    );
}
