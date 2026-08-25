using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tools.Application.Persistence;

public interface IToolsDbContext : IModuleDbContext
{
    DbSet<UserMemory> UserMemories { get; }

    DbSet<ProjectMemoryEntry> ProjectMemories { get; }
}
