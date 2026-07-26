using Agw.Auth.Application;
using Agw.Shared;
using Agw.Shared.Data.Abstractions;

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
        var userId = _userInfoService.UserId;
        return string.IsNullOrWhiteSpace(userId)
            ? Constants.AdminUserId
            : userId;
    }
}
