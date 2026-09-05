using Agw.Shared.Data.Abstractions;

namespace Agw.Projects.Tests;

internal sealed class TestAuditUserIdProvider : IEntityAuditUserIdProvider
{
    public string GetUserId() => UserInfoUtil.UserId ?? "tester";
}
