using Agw.Settings.Contracts;
using Agw.Shared;
using Agw.Shared.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Settings.Api;

[ApiController]
[Route("api/quick-prompts")]
public sealed class QuickPromptsController : ControllerBase
{
    private readonly ISettingsStore _store;
    private readonly ICurrentUser _currentUser;

    public QuickPromptsController(ISettingsStore store, ICurrentUser currentUser)
    {
        _store = store;
        _currentUser = currentUser;
    }

    [HttpGet]
    [ProducesApiResult(typeof(IReadOnlyList<QuickPromptResponse>))]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var system = await ReadAsync(null, cancellationToken);
        var user = await ReadAsync(_currentUser.RequiredUserId, cancellationToken);
        return ApiResult.Ok(system.Items.Concat(user.Items).ToArray());
    }

    [HttpGet("manage")]
    [ProducesApiResult(typeof(QuickPromptManageResponse))]
    public async Task<IActionResult> Manage(CancellationToken cancellationToken)
    {
        var system = await ReadAsync(null, cancellationToken);
        var user = await ReadAsync(_currentUser.RequiredUserId, cancellationToken);
        return ApiResult.Ok(
            new QuickPromptManageResponse(
                new QuickPromptList(system.Items),
                system.Version,
                new QuickPromptList(user.Items),
                user.Version,
                IsAdministrator()
            )
        );
    }

    [HttpPut]
    [ProducesApiResult]
    public async Task<IActionResult> Update(
        [FromQuery] string kind,
        QuickPromptUpdateRequest request,
        CancellationToken cancellationToken
    )
    {
        var isSystem = string.Equals(kind, "system", StringComparison.OrdinalIgnoreCase);
        if (!isSystem && !string.Equals(kind, "user", StringComparison.OrdinalIgnoreCase))
            throw new AgwException(ErrorCodes.InvalidParam, "Quick prompt kind must be system or user.");
        if (isSystem && !IsAdministrator())
            throw new AgwException(ErrorCodes.AdministratorRequired);
        Validate(request.Items);
        var value = new QuickPromptDocument { Items = request.Items };
        bool saved;
        if (request.Version is null)
        {
            saved = isSystem
                ? await _store.TryCreateGlobalAsync(QuickPromptSettings.Key, value, cancellationToken)
                : await _store.TryCreateCurrentUserAsync(QuickPromptSettings.Key, value, cancellationToken);
        }
        else
        {
            saved = isSystem
                ? await _store.TryUpdateGlobalAsync(
                    QuickPromptSettings.Key,
                    value,
                    request.Version.Value,
                    cancellationToken
                )
                : await _store.TryUpdateCurrentUserAsync(
                    QuickPromptSettings.Key,
                    value,
                    request.Version.Value,
                    cancellationToken
                );
        }
        if (!saved)
            throw new AgwException(ErrorCodes.QuickPromptConflict);
        return ApiResult.Ok();
    }

    private async Task<(IReadOnlyList<QuickPromptResponse> Items, long? Version)> ReadAsync(
        string? userId,
        CancellationToken cancellationToken
    )
    {
        var snapshot = userId is null
            ? await _store.GetGlobalAsync<QuickPromptDocument>(QuickPromptSettings.Key, cancellationToken)
            : await _store.GetCurrentUserAsync<QuickPromptDocument>(QuickPromptSettings.Key, cancellationToken);
        var kind = userId is null ? "system" : "user";
        var items =
            snapshot
                ?.Value.Items.Select(item => new QuickPromptResponse(
                    item.Id,
                    item.Label,
                    item.Text,
                    item.Description,
                    kind
                ))
                .ToArray()
            ?? [];
        return (items, snapshot?.Version);
    }

    private bool IsAdministrator() => _currentUser.UserId == Constants.AdminUserId;

    private static void Validate(IReadOnlyList<QuickPromptItem> items)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (
                string.IsNullOrWhiteSpace(item.Id)
                || string.IsNullOrWhiteSpace(item.Label)
                || string.IsNullOrWhiteSpace(item.Text)
                || !ids.Add(item.Id)
            )
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "Quick prompt id, label and text must be unique and non-empty."
                );
        }
    }
}
