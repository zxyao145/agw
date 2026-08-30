using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Auth;
using Microsoft.EntityFrameworkCore;

namespace Agw.Auth.Application.Persistence;

public interface IAuthDbContext : IModuleDbContext
{
    DbSet<ApiToken> ApiTokens { get; }
}
