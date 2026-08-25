using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Skills;
using Microsoft.EntityFrameworkCore;

namespace Agw.Skills.Application.Persistence;

public interface ISkillsDbContext : IModuleDbContext
{
    DbSet<Skill> Skills { get; }

    DbSet<RemoteSkillCache> RemoteSkillCaches { get; }
}
