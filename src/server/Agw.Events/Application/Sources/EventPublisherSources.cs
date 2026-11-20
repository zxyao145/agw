using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Domain.Services;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application.Sources;

public sealed class EventPublisherSources : IEventPublisherSources
{
    public const string ExtensionManagedKey = "publisher";

    private readonly IEventsDbContext _db;
    private readonly IEventIngestTransaction _transaction;
    private readonly IExtensionGenerationCatalog _extensions;
    private readonly EventSourceNameDomainService _nameService;

    public EventPublisherSources(
        IEventsDbContext db,
        IEventIngestTransaction transaction,
        IExtensionGenerationCatalog extensions,
        EventSourceNameDomainService nameService
    )
    {
        _db = db;
        _transaction = transaction;
        _extensions = extensions;
        _nameService = nameService;
    }

    public async Task<EventSource> EnsureBuiltInAsync(string name, CancellationToken cancellationToken)
    {
        var declaration = EventBuiltInSources.Get(name);
        var owner = UserInfoUtil.RequiredUserId;
        await _transaction.AcquireLockAsync(EventSourceNames.LockKey(owner), cancellationToken);
        var source = await FindAsync(owner, EventManagedBy.Agw, name, cancellationToken);
        if (source is not null)
            return source;

        await _nameService.EnsureAvailableAsync(name, null, cancellationToken);
        source = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = owner,
            Name = declaration.Name,
            Kind = EventSourceKind.Publisher,
            ManagedBy = EventManagedBy.Agw,
            ManagedKey = name,
            CeSource = declaration.CeSource,
            ConfigJson = "{}",
            Enabled = true,
        };
        _db.EventSources.Add(source);
        return source;
    }

    public async Task<EventSource> EnsureExtensionAsync(Guid extensionId, CancellationToken cancellationToken)
    {
        var owner = UserInfoUtil.RequiredUserId;
        var managedBy = EventManagedBy.ForExtension(extensionId);
        await _transaction.AcquireLockAsync(EventSourceNames.LockKey(owner), cancellationToken);
        var source = await FindAsync(owner, managedBy, ExtensionManagedKey, cancellationToken);
        if (source is not null)
            return source;

        var snapshot =
            _extensions
                .GetSnapshots()
                .Where(snapshot =>
                    snapshot.GenerationId.ExtensionId == extensionId
                    && snapshot.State is ExtensionGenerationState.Active or ExtensionGenerationState.Draining
                )
                .OrderByDescending(snapshot => snapshot.Epoch)
                .FirstOrDefault()
            ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
        var baseName = EventSourceNames.ExtensionNamePrefix + snapshot.Name;
        EventSourceNames.Validate(baseName, EventSourceKind.Publisher, managedBy);
        var name = await _nameService.NextAvailableAsync(baseName, cancellationToken);
        source = new EventSource
        {
            Id = Guid.CreateVersion7(),
            CreateBy = owner,
            Name = name,
            Kind = EventSourceKind.Publisher,
            ManagedBy = managedBy,
            ManagedKey = ExtensionManagedKey,
            CeSource = $"/agw/extensions/{extensionId:D}",
            ConfigJson = "{}",
            Enabled = true,
        };
        _db.EventSources.Add(source);
        return source;
    }

    public async Task EnsureAllBuiltInAsync(CancellationToken cancellationToken)
    {
        foreach (var declaration in EventBuiltInSources.All)
            await EnsureBuiltInAsync(declaration.Name, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<EventSource?> FindAsync(
        string owner,
        string managedBy,
        string managedKey,
        CancellationToken cancellationToken
    ) =>
        _db.EventSources.Local.FirstOrDefault(source =>
            source.CreateBy == owner && source.ManagedBy == managedBy && source.ManagedKey == managedKey
        )
        ?? await _db.EventSources.SingleOrDefaultAsync(
            source => source.CreateBy == owner && source.ManagedBy == managedBy && source.ManagedKey == managedKey,
            cancellationToken
        );
}
