using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Settings;
using Microsoft.EntityFrameworkCore;

namespace Agw.Settings.Application.Persistence;

public interface ISettingsDbContext : IModuleDbContext
{
    DbSet<Setting> Settings { get; }
}
