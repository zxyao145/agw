using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Skills.Application.Persistence;
using Agw.Skills.Contracts.Remote;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Skills.Application.Remote;

public sealed class RemoteSkillContentResolver : IRemoteSkillContentResolver
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRemoteSkillClient _remoteSkillClient;
    private readonly IRemoteSkillRefreshLock _refreshLock;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RemoteSkillContentResolver> _logger;

    public RemoteSkillContentResolver(
        IServiceScopeFactory scopeFactory,
        IRemoteSkillClient remoteSkillClient,
        IRemoteSkillRefreshLock refreshLock,
        TimeProvider timeProvider,
        ILogger<RemoteSkillContentResolver> logger
    )
    {
        _scopeFactory = scopeFactory;
        _remoteSkillClient = remoteSkillClient;
        _refreshLock = refreshLock;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<RemoteSkillDefinition> ResolveAsync(Guid skillId, CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(skillId, cancellationToken);
        if (TryUseCache(state, out var cached))
        {
            return cached;
        }

        await using var lease = await _refreshLock.AcquireAsync(skillId, cancellationToken);
        state = await ReadStateAsync(skillId, cancellationToken);
        if (TryUseCache(state, out cached))
        {
            return cached;
        }

        var definition = await _remoteSkillClient.FetchAsync(state.Skill.RemoteUrl!, cancellationToken);
        if (!string.Equals(definition.Name, state.Skill.Name, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.RemoteSkillIdentityChanged);
        }

        await SaveCacheAsync(state.Skill, definition, cancellationToken);
        _logger.LogInformation(
            "Refreshed remote skill cache for {SkillId} from {RemoteUrl}",
            state.Skill.Id,
            state.Skill.RemoteUrl
        );
        return definition;
    }

    private bool TryUseCache(RemoteSkillState state, out RemoteSkillDefinition definition)
    {
        definition = default!;
        if (
            state.Cache == null
            || !string.Equals(state.Cache.SourceUrl, state.Skill.RemoteUrl, StringComparison.Ordinal)
            || state.Cache.FetchedAt + CacheDuration <= _timeProvider.GetUtcNow()
        )
        {
            return false;
        }

        var cached = RemoteSkillDefinitionSerializer.Deserialize(state.Cache.ContentJson);
        if (cached == null)
        {
            return false;
        }

        if (!string.Equals(cached.Name, state.Skill.Name, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.RemoteSkillIdentityChanged);
        }

        definition = cached;
        return true;
    }

    private async Task<RemoteSkillState> ReadStateAsync(Guid skillId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ISkillsDbContext>();
        var skill = await dbContext
            .Skills.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == skillId, cancellationToken);
        if (skill == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        EnsureRemoteSkill(skill);
        var cache = await dbContext
            .RemoteSkillCaches.AsNoTracking()
            .SingleOrDefaultAsync(item => item.SkillId == skillId, cancellationToken);
        return new RemoteSkillState(skill, cache);
    }

    private async Task SaveCacheAsync(
        Skill expectedSkill,
        RemoteSkillDefinition definition,
        CancellationToken cancellationToken
    )
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ISkillsDbContext>();
        var skill = await dbContext
            .Skills.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == expectedSkill.Id, cancellationToken);
        if (skill == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        EnsureRemoteSkill(skill);
        if (
            !string.Equals(skill.RemoteUrl, expectedSkill.RemoteUrl, StringComparison.Ordinal)
            || !string.Equals(skill.Name, expectedSkill.Name, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.RemoteSkillConfigurationInvalid);
        }

        var cache = await dbContext.RemoteSkillCaches.SingleOrDefaultAsync(
            item => item.SkillId == skill.Id,
            cancellationToken
        );
        if (cache == null)
        {
            cache = new RemoteSkillCache { SkillId = skill.Id };
            cache.SourceUrl = skill.RemoteUrl!;
            cache.ContentJson = RemoteSkillDefinitionSerializer.Serialize(definition);
            cache.FetchedAt = _timeProvider.GetUtcNow();
            await dbContext.RemoteSkillCaches.AddAsync(cache, cancellationToken);
        }
        else
        {
            cache.SourceUrl = skill.RemoteUrl!;
            cache.ContentJson = RemoteSkillDefinitionSerializer.Serialize(definition);
            cache.FetchedAt = _timeProvider.GetUtcNow();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureRemoteSkill(Skill skill)
    {
        if (skill.Kind != SkillKind.Remote || string.IsNullOrWhiteSpace(skill.RemoteUrl))
        {
            throw new AgwException(ErrorCodes.RemoteSkillConfigurationInvalid);
        }
    }

    private sealed record RemoteSkillState(Skill Skill, RemoteSkillCache? Cache);
}
