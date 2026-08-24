using Agw.Auth.Contracts;
using Agw.Shared;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Extensions;

namespace Agw.Host.Data;

public class EntityAuditUserIdProvider : IEntityAuditUserIdProvider
{
    private readonly IUserInfoService _userInfoService;

    public EntityAuditUserIdProvider(IUserInfoService userInfoService)
    {
        _userInfoService = userInfoService;
    }

    public string GetUserId()
    {
        return _userInfoService.Current?.GetUserId() ?? Constants.AdminUserId;
    }
}
