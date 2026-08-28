using System.Security.Claims;
using Agw.Shared.Contracts;

namespace Agw.Auth.Contracts;

public interface IUserInfoService : ICurrentUser
{
    ClaimsPrincipal? Current { get; set; }
}
