namespace Agw.Tools.Domain.Repositories;

public interface IUserMemoryRepository
{
    /// <summary>
    /// <para>判断同一用户的其他记忆是否已经使用该规范化名称。</para>
    /// <para>Checks whether another memory of the same user already uses the normalized name.</para>
    /// </summary>
    Task<bool> ExistsWithNormalizedNameAsync(
        string userId,
        string normalizedName,
        Guid excludedMemoryId,
        CancellationToken cancellationToken
    );
}
