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
        // Trusted infrastructure scopes must not inherit an active anonymous
        // HTTP context. Preserve an explicit execution user when one exists;
        // otherwise use the legacy administrator audit actor for seeding.
        if (UserInfoUtil.IsSystemScopeActive)
        {
            return UserInfoUtil.UserId ?? Constants.AdminUserId;
        }

        if (UserInfoUtil.IsContextActive)
        {
            return UserInfoUtil.RequiredUserId;
        }

        return _userInfoService.Current?.GetUserId() ?? Constants.AdminUserId;
    }
}
