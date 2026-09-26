using Agw.Shared.Data.Entities.Auth;
using Agw.Shared.Exceptions;

namespace Agw.Auth.Domain.Behaviors;

public sealed class ApiTokenBehavior
{
    private const int MaxNameLength = 64;

    private readonly ApiToken _token;

    public ApiTokenBehavior(ApiToken token)
    {
        _token = token;
    }

    /// <summary>
    /// <para>Token 名称去除两端空白后为 1 到 64 个字符；按大写形式比较，因此同一所有者的名称不区分大小写。</para>
    /// <para>A token name has 1 to 64 characters after trimming and is compared in upper case, so one owner's names ignore case.</para>
    /// </summary>
    public void AssignName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Token name must be between 1 and 64 characters.");
        }

        _token.Name = name.Trim();
        _token.NormalizedName = _token.Name.ToUpperInvariant();
    }
}
