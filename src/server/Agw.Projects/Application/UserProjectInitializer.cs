using Agw.Auth.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public sealed class UserProjectInitializer : IUserProjectInitializer
{
    private readonly IProjectsDbContext _context;

    public UserProjectInitializer(IProjectsDbContext context)
    {
        _context = context;
    }

    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var owner = UserInfoUtil.RequiredUserId;
        foreach (var name in new[] { ProjectDefaults.DefaultBuiltInName, ProjectDefaults.A2AName })
        {
            if (
                await _context.Projects.AnyAsync(
                    project => project.CreateBy == owner && project.Name == name,
                    cancellationToken
                )
            )
                continue;
            _context.Projects.Add(
                new Project
                {
                    Id = Guid.CreateVersion7(),
                    Name = name,
                    Type = ProjectType.DefaultBuiltIn,
                    CreateBy = owner,
                }
            );
        }
        await _context.SaveChangesAsync(cancellationToken);
    }
}
