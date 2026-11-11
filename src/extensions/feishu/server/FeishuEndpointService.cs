using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.Feishu;

public sealed class FeishuEndpointService
{
    private readonly FeishuDbContext _db;
    private readonly FeishuNodeBridge _nodeBridge;
    private readonly FeishuRuntimeState _runtimeState;
    private readonly FeishuNotificationChannel _notificationChannel;
    private readonly TimeProvider _timeProvider;

    public FeishuEndpointService(
        FeishuDbContext db,
        FeishuNodeBridge nodeBridge,
        FeishuRuntimeState runtimeState,
        FeishuNotificationChannel notificationChannel,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _nodeBridge = nodeBridge;
        _runtimeState = runtimeState;
        _notificationChannel = notificationChannel;
        _timeProvider = timeProvider;
    }

    public async ValueTask<ExtensionEndpointResponse> HandleAsync(
        string endpointId,
        IExtensionContext context,
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        return endpointId switch
        {
            "apps-list" => await ListAppsAsync(context, services, cancellationToken),
            "apps-get" => await GetAppAsync(context, request, services, cancellationToken),
            "apps-create" => await CreateAppAsync(context, request, services, cancellationToken),
            "apps-update" => await UpdateAppAsync(context, request, services, cancellationToken),
            "apps-delete" => await DeleteAppAsync(request, services, cancellationToken),
            "apps-verify" => await VerifyAppAsync(context, request, services, cancellationToken),
            "targets-list" => await ListTargetsAsync(request, services, cancellationToken),
            "targets-discover" => await DiscoverTargetsAsync(context, request, services, cancellationToken),
            "targets-save" => await SaveTargetAsync(request, services, cancellationToken),
            "targets-delete" => await DeleteTargetAsync(request, services, cancellationToken),
            "bind-start" => await StartBindingAsync(context, request, services, cancellationToken),
            "bind-confirm-page" => await GetBindingPageAsync(request, cancellationToken),
            "bind-confirm" => await ConfirmBindingAsync(context, request, services, cancellationToken),
            "bind-list" => await ListBindingsAsync(request, services, cancellationToken),
            "bind-delete" => await DeleteBindingAsync(request, services, cancellationToken),
            "test-send" => await TestSendAsync(context, request, services, cancellationToken),
            _ => throw new AgwException(ErrorCodes.ExtensionPackageInvalid),
        };
    }

    private async ValueTask<ExtensionEndpointResponse> ListAppsAsync(
        IExtensionContext context,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuApp[] apps = await _db.Apps.AsNoTracking().OrderBy(row => row.Name).ToArrayAsync(cancellationToken);
        return ApiOk(apps.Select(app => ToResponse(app, _runtimeState.GetStatus(app))).ToArray());
    }

    private async ValueTask<ExtensionEndpointResponse> GetAppAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetGuid(request.Query, "id", out Guid id))
            return ApiError(ErrorCodes.InvalidParam);
        FeishuApp? app = await _db.Apps.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        return app is null
            ? ApiError(ErrorCodes.ExtensionNotFound)
            : ApiOk(ToResponse(app, _runtimeState.GetStatus(app)));
    }

    private async ValueTask<ExtensionEndpointResponse> CreateAppAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuAppWriteRequest? input = ReadBody<FeishuAppWriteRequest>(request.Body);
        if (input is null || !IsValidConfiguration(input) || !TryGetAppMode(input.Mode, out FeishuAppMode mode))
            return ApiError(ErrorCodes.InvalidParam);

        Guid appId = Guid.CreateVersion7();
        var app = new FeishuApp
        {
            Id = appId,
            Name = input.Name.Trim(),
            Mode = mode,
            OpenDomain = input.OpenDomain ?? FeishuDomains.DefaultOpenDomain,
            WebhookUrl = ProtectSecret(context, appId, mode == FeishuAppMode.Webhook ? input.WebhookUrl : null),
            AppId = mode == FeishuAppMode.App ? input.AppId?.Trim() : null,
            WebhookSecret = ProtectSecret(context, appId, input.WebhookSecret),
            AppSecret = ProtectSecret(context, appId, input.AppSecret),
        };
        _db.Apps.Add(app);
        await _db.SaveChangesAsync(cancellationToken);
        _runtimeState.RequestScan();
        return ApiOk(ToResponse(app, _runtimeState.GetStatus(app)));
    }

    private async ValueTask<ExtensionEndpointResponse> UpdateAppAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuAppWriteRequest? input = ReadBody<FeishuAppWriteRequest>(request.Body);
        if (
            input?.Id is not { } id
            || input.Version is not { } version
            || !TryGetAppMode(input.Mode, out FeishuAppMode mode)
            || !IsValidConfiguration(input, allowBlankAppSecret: true, allowBlankWebhookUrl: true)
        )
            return ApiError(ErrorCodes.InvalidParam);
        FeishuApp? app = await _db.Apps.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (app is null)
            return ApiError(ErrorCodes.ExtensionNotFound);
        if (app.Version != version)
            return ApiError(ErrorCodes.ExtensionOperationConflict);

        app.Name = input.Name.Trim();
        app.Mode = mode;
        app.OpenDomain = input.OpenDomain ?? FeishuDomains.DefaultOpenDomain;
        if (mode == FeishuAppMode.Webhook)
            app.WebhookUrl = ReplaceSecret(context, id, input.WebhookUrl, app.WebhookUrl);
        else
            app.WebhookUrl = null;
        app.AppId = mode == FeishuAppMode.App ? input.AppId?.Trim() : null;
        app.WebhookSecret = ReplaceSecret(context, id, input.WebhookSecret, app.WebhookSecret);
        app.AppSecret = ReplaceSecret(context, id, input.AppSecret, app.AppSecret);
        if (app.Mode == FeishuAppMode.App && app.AppSecret is null)
            return ApiError(ErrorCodes.InvalidParam);
        if (app.Mode == FeishuAppMode.Webhook && app.WebhookUrl is null)
            return ApiError(ErrorCodes.InvalidParam);
        app.Version++;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiError(ErrorCodes.ExtensionOperationConflict);
        }
        _runtimeState.RequestScan(app.Id);
        return ApiOk(ToResponse(app, _runtimeState.GetStatus(app)));
    }

    private async ValueTask<ExtensionEndpointResponse> DeleteAppAsync(
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuAppVersionRequest? input = ReadBody<FeishuAppVersionRequest>(request.Body);
        if (input is null)
            return ApiError(ErrorCodes.InvalidParam);
        FeishuApp? app = await _db.Apps.SingleOrDefaultAsync(row => row.Id == input.Id, cancellationToken);
        if (app is null)
            return ApiError(ErrorCodes.ExtensionNotFound);
        if (app.Version != input.Version)
            return ApiError(ErrorCodes.ExtensionOperationConflict);

        _db.Targets.RemoveRange(await _db.Targets.Where(row => row.RootId == app.Id).ToArrayAsync(cancellationToken));
        _db.IdentityBindings.RemoveRange(
            await _db.IdentityBindings.Where(row => row.RootId == app.Id).ToArrayAsync(cancellationToken)
        );
        _db.BindRequests.RemoveRange(
            await _db.BindRequests.Where(row => row.RootId == app.Id).ToArrayAsync(cancellationToken)
        );
        _db.Messages.RemoveRange(await _db.Messages.Where(row => row.RootId == app.Id).ToArrayAsync(cancellationToken));
        _db.Callbacks.RemoveRange(
            await _db.Callbacks.Where(row => row.RootId == app.Id).ToArrayAsync(cancellationToken)
        );
        _db.Apps.Remove(app);
        await _db.SaveChangesAsync(cancellationToken);
        _runtimeState.RequestScan(app.Id);
        return ApiOk();
    }

    private async ValueTask<ExtensionEndpointResponse> VerifyAppAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuAppIdRequest? input = ReadBody<FeishuAppIdRequest>(request.Body);
        if (input is null)
            return ApiError(ErrorCodes.InvalidParam);
        FeishuApp? app = await _db
            .Apps.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == input.AppId, cancellationToken);
        if (app is null)
            return ApiError(ErrorCodes.ExtensionNotFound);
        if (app.Mode != FeishuAppMode.App)
            return ApiError(ErrorCodes.InvalidParam);

        FeishuNodeVerification result = await _nodeBridge.VerifyAsync(
            ToNodeApplication(context, app),
            cancellationToken
        );
        return ApiOk(new FeishuVerificationResponse { Valid = result.Valid, Error = result.Error });
    }

    private async ValueTask<ExtensionEndpointResponse> ListTargetsAsync(
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetGuid(request.Query, "appId", out Guid appId))
            return ApiError(ErrorCodes.InvalidParam);
        FeishuApp? app = await _db.Apps.AsNoTracking().SingleOrDefaultAsync(row => row.Id == appId, cancellationToken);
        if (app is null)
            return ApiError(ErrorCodes.ExtensionNotFound);
        if (app.Mode == FeishuAppMode.Webhook)
        {
            return ApiOk(
                new FeishuTargetResponse[]
                {
                    new FeishuTargetResponse
                    {
                        Id = app.Id,
                        AppId = app.Id,
                        Kind = FeishuTargetKind.Chat.ToString(),
                        ReceiveId = app.Id.ToString("D"),
                        Name = app.Name,
                    },
                }
            );
        }

        FeishuTargetResponse[] targets = await _db
            .Targets.AsNoTracking()
            .Where(row => row.RootId == appId)
            .OrderBy(row => row.Name)
            .Select(row => new FeishuTargetResponse
            {
                Id = row.Id,
                AppId = row.RootId,
                Kind = row.Kind.ToString(),
                ReceiveId = row.ReceiveId,
                Name = row.Name,
            })
            .ToArrayAsync(cancellationToken);
        return ApiOk(targets);
    }

    private async ValueTask<ExtensionEndpointResponse> DiscoverTargetsAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetGuid(request.Query, "appId", out Guid appId))
            return ApiError(ErrorCodes.InvalidParam);
        FeishuApp? app = await _db.Apps.AsNoTracking().SingleOrDefaultAsync(row => row.Id == appId, cancellationToken);
        if (app is null)
            return ApiError(ErrorCodes.ExtensionNotFound);
        if (app.Mode != FeishuAppMode.App)
            return ApiError(ErrorCodes.InvalidParam);
        IReadOnlyList<FeishuNodeChat> chats = await _nodeBridge.DiscoverChatsAsync(
            ToNodeApplication(context, app),
            cancellationToken
        );
        return ApiOk(
            chats.Select(chat => new FeishuChatResponse { ReceiveId = chat.ReceiveId, Name = chat.Name }).ToArray()
        );
    }

    private async ValueTask<ExtensionEndpointResponse> SaveTargetAsync(
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuTargetSaveRequest? input = ReadBody<FeishuTargetSaveRequest>(request.Body);
        if (
            input is null
            || !TryGetTargetKind(input.Kind, out FeishuTargetKind targetKind)
            || string.IsNullOrWhiteSpace(input.Name)
            || string.IsNullOrWhiteSpace(input.ReceiveId)
            || input.Name.Length > 200
            || input.ReceiveId.Length > 200
        )
            return ApiError(ErrorCodes.InvalidParam);
        FeishuApp? app = await _db
            .Apps.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == input.AppId, cancellationToken);
        if (app is null || app.Mode != FeishuAppMode.App)
            return ApiError(ErrorCodes.ExtensionNotFound);

        FeishuTarget? target = input.Id is { } id
            ? await _db.Targets.SingleOrDefaultAsync(
                row => row.Id == id && row.RootId == input.AppId,
                cancellationToken
            )
            : null;
        if (input.Id.HasValue && target is null)
            return ApiError(ErrorCodes.ExtensionNotFound);
        bool duplicate = await _db.Targets.AnyAsync(
            row =>
                row.RootId == input.AppId
                && row.Kind == targetKind
                && row.ReceiveId == input.ReceiveId
                && row.Id != input.Id,
            cancellationToken
        );
        if (duplicate)
            return ApiError(ErrorCodes.ExtensionOperationConflict);

        target ??= new FeishuTarget
        {
            RootId = input.AppId,
            ReceiveId = input.ReceiveId.Trim(),
            Name = input.Name.Trim(),
        };
        target.Kind = targetKind;
        target.ReceiveId = input.ReceiveId.Trim();
        target.Name = input.Name.Trim();
        if (_db.Entry(target).State == EntityState.Detached)
            _db.Targets.Add(target);
        await _db.SaveChangesAsync(cancellationToken);
        return ApiOk(ToResponse(target));
    }

    private async ValueTask<ExtensionEndpointResponse> DeleteTargetAsync(
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuTargetDeleteRequest? input = ReadBody<FeishuTargetDeleteRequest>(request.Body);
        if (input is null)
            return ApiError(ErrorCodes.InvalidParam);
        FeishuTarget? target = await _db.Targets.SingleOrDefaultAsync(
            row => row.Id == input.Id && row.RootId == input.AppId,
            cancellationToken
        );
        if (target is null)
            return ApiError(ErrorCodes.ExtensionNotFound);
        _db.Targets.Remove(target);
        await _db.SaveChangesAsync(cancellationToken);
        return ApiOk();
    }

    private async ValueTask<ExtensionEndpointResponse> StartBindingAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuBindStartRequest? input = ReadBody<FeishuBindStartRequest>(request.Body);
        if (input is null)
            return ApiError(ErrorCodes.InvalidParam);
        FeishuApp? app = await _db.Apps.SingleOrDefaultAsync(row => row.Id == input.AppId, cancellationToken);
        if (app is null || app.Mode != FeishuAppMode.App)
            return ApiError(ErrorCodes.ExtensionNotFound);

        string code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        DateTimeOffset expiresAt = _timeProvider.GetUtcNow().AddMinutes(10);
        _db.BindRequests.Add(
            new FeishuBindRequest
            {
                RootId = app.Id,
                OpenId = string.IsNullOrWhiteSpace(input.OpenId) ? null : input.OpenId.Trim(),
                CodeHash = HashCode(code),
                ExpiresAt = expiresAt,
            }
        );
        await _db.SaveChangesAsync(cancellationToken);
        string baseUrl = context.HostCapabilities.GetRequired<IExtensionServerAddress>().GetPublicBaseUrl();
        string confirmationUrl = new Uri(
            new Uri(baseUrl, UriKind.Absolute),
            $"api/ext/feishu/bind/confirm?code={Uri.EscapeDataString(code)}"
        ).ToString();
        return ApiOk(
            new FeishuBindStartResponse
            {
                Code = code,
                ExpiresAt = expiresAt,
                ConfirmationUrl = confirmationUrl,
            }
        );
    }

    private async ValueTask<ExtensionEndpointResponse> ConfirmBindingAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuBindConfirmRequest? input = ReadBody<FeishuBindConfirmRequest>(request.Body);
        if (input is null || string.IsNullOrWhiteSpace(input.Code) || input.Code.Length > 100)
            return ApiError(ErrorCodes.InvalidParam);
        string hash = HashCode(input.Code);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        FeishuBindRequest? pending = await _db.BindRequests.SingleOrDefaultAsync(
            row => row.CodeHash == hash && row.ExpiresAt > now,
            cancellationToken
        );
        if (pending is null || string.IsNullOrWhiteSpace(pending.OpenId))
            return ApiError(ErrorCodes.ExtensionNotFound);
        bool alreadyBound = await _db.IdentityBindings.AnyAsync(
            row => row.RootId == pending.RootId && row.OpenId == pending.OpenId,
            cancellationToken
        );
        if (alreadyBound)
            return ApiError(ErrorCodes.ExtensionOperationConflict);

        _db.IdentityBindings.Add(
            new FeishuIdentityBinding
            {
                RootId = pending.RootId,
                OpenId = pending.OpenId,
                UserId = context.CurrentUser.UserId!,
            }
        );
        _db.BindRequests.Remove(pending);
        await _db.SaveChangesAsync(cancellationToken);
        return ApiOk(
            new FeishuIdentityBindingResponse
            {
                AppId = pending.RootId,
                OpenId = pending.OpenId,
                UserId = context.CurrentUser.UserId!,
            }
        );
    }

    private async ValueTask<ExtensionEndpointResponse> GetBindingPageAsync(
        ExtensionEndpointRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetString(request.Query, "code", out string code) || code.Length > 100)
            return FeishuBindingPage.NotFound();
        string hash = HashCode(code);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool exists = await _db
            .BindRequests.AsNoTracking()
            .AnyAsync(row => row.CodeHash == hash && row.OpenId != null && row.ExpiresAt > now, cancellationToken);
        return exists ? FeishuBindingPage.Confirmation() : FeishuBindingPage.NotFound();
    }

    private async ValueTask<ExtensionEndpointResponse> DeleteBindingAsync(
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuBindDeleteRequest? input = ReadBody<FeishuBindDeleteRequest>(request.Body);
        if (input is null || string.IsNullOrWhiteSpace(input.OpenId))
            return ApiError(ErrorCodes.InvalidParam);
        FeishuIdentityBinding? binding = await _db.IdentityBindings.SingleOrDefaultAsync(
            row => row.RootId == input.AppId && row.OpenId == input.OpenId,
            cancellationToken
        );
        if (binding is null)
            return ApiError(ErrorCodes.ExtensionNotFound);
        _db.IdentityBindings.Remove(binding);
        await _db.SaveChangesAsync(cancellationToken);
        return ApiOk();
    }

    private async ValueTask<ExtensionEndpointResponse> ListBindingsAsync(
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetGuid(request.Query, "appId", out Guid appId))
            return ApiError(ErrorCodes.InvalidParam);
        FeishuIdentityBindingResponse[] bindings = await _db
            .IdentityBindings.AsNoTracking()
            .Where(row => row.RootId == appId)
            .OrderBy(row => row.OpenId)
            .Select(row => new FeishuIdentityBindingResponse
            {
                AppId = row.RootId,
                OpenId = row.OpenId,
                UserId = row.UserId,
            })
            .ToArrayAsync(cancellationToken);
        return ApiOk(bindings);
    }

    private async ValueTask<ExtensionEndpointResponse> TestSendAsync(
        IExtensionContext context,
        ExtensionEndpointRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken
    )
    {
        FeishuTestSendRequest? input = ReadBody<FeishuTestSendRequest>(request.Body);
        if (input is null)
            return ApiError(ErrorCodes.InvalidParam);
        var message = new NotificationMessage
        {
            NotificationId = Guid.CreateVersion7(),
            DispatchKey = $"test:{Guid.CreateVersion7():D}",
            ChannelKey = FeishuChannel.ChannelKey(context.ExtensionId, input.TargetId),
            IdempotencyKey = $"test:{Guid.CreateVersion7():D}",
            ContentVersion = 1,
            Kind = ExtensionNotificationKind.Notice,
            State = ExtensionNotificationState.Open,
            Title = "Agw test message",
            Body = "This message confirms the Feishu destination is configured.",
        };
        FeishuApp? app = await _db
            .Apps.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == input.AppId, cancellationToken);
        if (app is null)
            return ApiError(ErrorCodes.ExtensionNotFound);
        FeishuNodeMessage result = await _notificationChannel.SendForAppAsync(
            app,
            input.TargetId,
            message,
            cancellationToken
        );
        return ApiOk(result);
    }

    private FeishuAppResponse ToResponse(FeishuApp app, FeishuConnectionStatus status) =>
        new()
        {
            Id = app.Id,
            Name = app.Name,
            Mode = app.Mode.ToString(),
            OpenDomain = app.OpenDomain,
            HasWebhookUrl = app.WebhookUrl is not null,
            HasWebhookSecret = app.WebhookSecret is not null,
            AppId = app.AppId,
            HasAppSecret = app.AppSecret is not null,
            Enabled = app.Enabled,
            Version = app.Version,
            ConnectionStatus = status.State,
            LastError = status.Error,
        };

    private static FeishuTargetResponse ToResponse(FeishuTarget target) =>
        new()
        {
            Id = target.Id,
            AppId = target.RootId,
            Kind = target.Kind.ToString(),
            ReceiveId = target.ReceiveId,
            Name = target.Name,
        };

    private static T? ReadBody<T>(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } element)
            return default;
        JsonTypeInfo<T> typeInfo =
            FeishuApiJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        return element.Deserialize(typeInfo);
    }

    private static bool IsValidConfiguration(
        FeishuAppWriteRequest input,
        bool allowBlankAppSecret = false,
        bool allowBlankWebhookUrl = false
    )
    {
        if (
            !TryGetAppMode(input.Mode, out FeishuAppMode mode)
            || string.IsNullOrWhiteSpace(input.Name)
            || input.Name.Length > 200
        )
            return false;
        string domain = input.OpenDomain ?? FeishuDomains.DefaultOpenDomain;
        if (!FeishuDomains.IsSupportedOpenDomain(domain))
            return false;
        if (mode == FeishuAppMode.Webhook)
        {
            bool isValidWebhookUrl =
                allowBlankWebhookUrl && string.IsNullOrWhiteSpace(input.WebhookUrl)
                || IsAllowedWebhookUrl(input.WebhookUrl);
            return isValidWebhookUrl
                && (string.IsNullOrEmpty(input.WebhookSecret) || input.WebhookSecret.Length <= 1000);
        }
        bool hasAppSecret = !string.IsNullOrWhiteSpace(input.AppSecret);
        return !string.IsNullOrWhiteSpace(input.AppId)
            && input.AppId.Length <= 100
            && (allowBlankAppSecret || hasAppSecret)
            && (!hasAppSecret || input.AppSecret!.Length <= 1000);
    }

    private static bool IsAllowedWebhookUrl(string? value)
    {
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0
            || !uri.IsDefaultPort
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.Host is not ("open.feishu.cn" or "open.larksuite.com")
        )
            return false;
        return uri.AbsolutePath.StartsWith("/open-apis/bot/v2/hook/", StringComparison.Ordinal)
            && uri.AbsolutePath.Length > "/open-apis/bot/v2/hook/".Length;
    }

    private static string? ProtectSecret(IExtensionContext context, Guid appId, string? plaintext) =>
        string.IsNullOrWhiteSpace(plaintext) ? null : context.Secrets.Protect("app", appId, plaintext);

    private static string? ReplaceSecret(IExtensionContext context, Guid appId, string? plaintext, string? current) =>
        string.IsNullOrWhiteSpace(plaintext) ? current : context.Secrets.Protect("app", appId, plaintext);

    private FeishuNodeApplication ToNodeApplication(IExtensionContext context, FeishuApp app) =>
        new()
        {
            OpenDomain = app.OpenDomain,
            AppId = app.AppId ?? string.Empty,
            AppSecret = Unprotect(context, app.Id, app.AppSecret),
        };

    private static string Unprotect(IExtensionContext context, Guid appId, string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : context.Secrets.Unprotect("app", appId, value);

    private static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static bool TryGetAppMode(string value, out FeishuAppMode mode)
    {
        mode = default;
        return Enum.TryParse(value, ignoreCase: true, out mode)
            && Enum.IsDefined(mode)
            && string.Equals(mode.ToString(), value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetTargetKind(string value, out FeishuTargetKind kind)
    {
        kind = default;
        return Enum.TryParse(value, ignoreCase: true, out kind)
            && Enum.IsDefined(kind)
            && string.Equals(kind.ToString(), value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetGuid(
        IReadOnlyDictionary<string, IReadOnlyList<string>> query,
        string name,
        out Guid value
    )
    {
        value = default;
        return query.TryGetValue(name, out IReadOnlyList<string>? items)
            && items.Count > 0
            && Guid.TryParse(items[0], out value);
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, IReadOnlyList<string>> query,
        string name,
        out string value
    )
    {
        value = string.Empty;
        if (!query.TryGetValue(name, out IReadOnlyList<string>? items) || items.Count == 0)
            return false;
        value = items[0];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static ExtensionEndpointResponse ApiError(ErrorCode errorCode) => throw new AgwException(errorCode);

    private static ExtensionEndpointResponse ApiOk()
    {
        using JsonDocument document = JsonDocument.Parse("null");
        return ExtensionEndpointResponse.FromJson(document.RootElement);
    }

    private static ExtensionEndpointResponse ApiOk<T>(T value)
    {
        JsonTypeInfo<T> typeInfo =
            FeishuApiJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        return ExtensionEndpointResponse.FromJson(JsonSerializer.SerializeToElement(value, typeInfo));
    }
}
