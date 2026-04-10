using Agw.Integrations.Contracts.Manager;
using Agw.Integrations.Domain.Entities;
using Agw.Shared.Data.Repositories;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agw.Integrations.Controllers;


[ApiController]
[Route("api/integrations")]
public class IntegrationsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IRepository<AppDefinition> _appDefinitionRepository;
    private readonly IRepository<AppInstance> _appInstanceRepository;
    private readonly IUnitOfWork _unitOfWork;

    public IntegrationsController(
        IRepository<AppDefinition> appDefinitionRepository,
        IRepository<AppInstance> appInstanceRepository,
        IUnitOfWork unitOfWork)
    {
        _appDefinitionRepository = appDefinitionRepository;
        _appInstanceRepository = appInstanceRepository;
        _unitOfWork = unitOfWork;
    }

    [HttpGet("app-definitions")]
    public async Task<IActionResult> ListAppDefinitionsAsync()
    {
        var appDefinitions = await _appDefinitionRepository.ListAsync(
            orderBy: query => query.OrderBy(app => app.DisplayName).ThenBy(app => app.Name));

        return Ok(appDefinitions.Select(Map));
    }

    [HttpGet("app-instances")]
    public async Task<IActionResult> ListAppInstancesAsync()
    {
        var appDefinitions = await _appDefinitionRepository.ListAsync();
        var definitionsByName = appDefinitions.ToDictionary(
            app => app.Name,
            StringComparer.OrdinalIgnoreCase);

        var appInstances = await _appInstanceRepository.ListAsync(
            orderBy: query => query.OrderByDescending(instance => instance.CreateTime).ThenBy(instance => instance.AppName),
            includes: instance => instance.AuthorizationToken!);

        var now = DateTimeOffset.UtcNow;
        var response = appInstances.Select(instance =>
        {
            definitionsByName.TryGetValue(instance.AppName, out var definition);
            return Map(instance, definition, now);
        });

        return Ok(response);
    }

    [HttpPost("app-instances")]
    public async Task<IActionResult> CreateAppInstanceAsync([FromBody] AppInstanceCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AppName)
            || string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            return BadRequest("Invalid app instance request.");
        }

        var definition = await _appDefinitionRepository.GetByIdAsync(request.AppName);
        if (definition == null)
        {
            return BadRequest("Invalid app instance request.");
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new AppInstance
        {
            Id = Guid.NewGuid(),
            AppName = definition.Name,
            ClientId = request.ClientId.Trim(),
            ClientSecret = request.ClientSecret.Trim(),
            UsePkce = request.UsePkce,
            CreateBy = User?.Identity?.Name ?? "system",
            CreateTime = now.UtcDateTime
        };

        await _appInstanceRepository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();

        return Ok(Map(entity, definition, now));
    }

    [HttpPost("app-instances/{id:guid}/authorize-start")]
    public async Task<IActionResult> AuthorizeStartAsync(Guid id, CancellationToken cancellationToken)
    {
        var appInstance = await _appInstanceRepository.GetByIdAsync(id);
        if (appInstance == null)
        {
            return NotFound();
        }

        var appDefinition = await _appDefinitionRepository.GetByIdAsync(appInstance.AppName);
        if (appDefinition == null)
        {
            return BadRequest("App definition not found.");
        }

        var state = Guid.NewGuid().ToString("N");
        string? verifier = null;
        string? codeChallenge = null;

        if (appInstance.UsePkce)
        {
            verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            codeChallenge = WebEncoders.Base64UrlEncode(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(verifier)));
        }

        AppendCallbackStateCookie(state, appInstance.Id, appDefinition.Name, verifier);

        var authorizeUrl = BuildAuthorizeUrl(appDefinition, appInstance, state, codeChallenge);
        return Ok(new AuthorizeStartResponse(authorizeUrl));
    }

    [HttpDelete("app-instances/{id:guid}")]
    public async Task<IActionResult> DeleteAppInstanceAsync(Guid id)
    {
        var appInstance = await _appInstanceRepository.Queryable
            .Include(instance => instance.AuthorizationToken)
            .FirstOrDefaultAsync(instance => instance.Id == id);

        if (appInstance == null)
        {
            return NotFound();
        }

        _appInstanceRepository.Remove(appInstance);
        await _unitOfWork.SaveChangesAsync();

        return NoContent();
    }

    private static AppDefinitionListItemResponse Map(AppDefinition definition)
    {
        return new AppDefinitionListItemResponse(
            definition.Name,
            definition.DisplayName,
            definition.Category,
            definition.Provider,
            definition.Description,
            definition.AuthUrl,
            [.. definition.Scopes],
            definition.UsePkce,
            [.. definition.Tags],
            [.. definition.ToolNames]);
    }

    private static AppInstanceListItemResponse Map(AppInstance instance, AppDefinition? definition, DateTimeOffset now)
    {
        var expiresAtUtc = instance.AuthorizationToken?.ExpiresAtUtc;
        var isAuthorizationExpired = expiresAtUtc.HasValue && expiresAtUtc.Value <= now;
        var isAuthorized = instance.AuthorizationToken is { AccessToken.Length: > 0 } && !isAuthorizationExpired;

        return new AppInstanceListItemResponse(
            instance.Id,
            instance.AppName,
            definition?.DisplayName ?? instance.AppName,
            definition?.Provider ?? string.Empty,
            definition?.Category,
            instance.UsePkce,
            instance.ClientId,
            !string.IsNullOrWhiteSpace(instance.ClientSecret),
            isAuthorized,
            isAuthorizationExpired,
            expiresAtUtc,
            instance.AuthorizationToken?.Subject,
            instance.CreateTime,
            instance.CreateBy,
            instance.UpdateTime,
            instance.UpdateBy);
    }

    private string BuildAuthorizeUrl(
        AppDefinition appDefinition,
        AppInstance appInstance,
        string state,
        string? codeChallenge)
    {
        var url = new UriBuilder(appDefinition.AuthUrl);
        var queryParameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = appInstance.ClientId,
            ["redirect_uri"] = BuildRedirectUri(),
            ["scope"] = string.Join(' ', appDefinition.Scopes),
            ["state"] = state
        };

        if (!string.IsNullOrWhiteSpace(codeChallenge))
        {
            queryParameters["code_challenge"] = codeChallenge;
            queryParameters["code_challenge_method"] = "S256";
        }

        url.Query = QueryHelpers.AddQueryString(string.Empty, queryParameters)
            .TrimStart('?');

        return url.ToString();
    }

    private string BuildRedirectUri()
    {
        if (TryResolveUiOriginFromHeader(Request.Headers.Origin, out var origin)
            || TryResolveUiOriginFromHeader(Request.Headers.Referer, out origin))
        {
            return $"{origin}{Request.PathBase}{IntegrationConstants.RedirectPath}";
        }

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}{IntegrationConstants.RedirectPath}";
    }

    private void AppendCallbackStateCookie(
        string state,
        Guid appInstanceId,
        string integrationId,
        string? verifier)
    {
        var payload = JsonSerializer.Serialize(
            new OAuthCallbackState
            {
                State = state,
                AppInstanceId = appInstanceId,
                IntegrationId = integrationId,
                UiCallbackUrl = BuildUiCallbackUrl(),
                Verifier = verifier,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            },
            JsonSerializerOptions);

        Response.Cookies.Append(
            BuildCallbackStateCookieName(state),
            Uri.EscapeDataString(payload),
            new CookieOptions
            {
                HttpOnly = true,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/",
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps
            });
    }

    private static string BuildCallbackStateCookieName(string state)
    {
        return $"agw_oauth2_{WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(state))}";
    }

    private string BuildUiCallbackUrl()
    {
        if (TryResolveUiOriginFromHeader(Request.Headers.Origin, out var origin)
            || TryResolveUiOriginFromHeader(Request.Headers.Referer, out origin))
        {
            return $"{origin}{Request.PathBase}{IntegrationConstants.UiCallbackPath}";
        }

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}{IntegrationConstants.UiCallbackPath}";
    }

    private static bool TryResolveUiOriginFromHeader(string? headerValue, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrWhiteSpace(headerValue)
            || !Uri.TryCreate(headerValue, UriKind.Absolute, out var uri))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private sealed record OAuthCallbackState
    {
        public string? State { get; init; }
        public Guid AppInstanceId { get; init; }
        public string? IntegrationId { get; init; }
        public string? UiCallbackUrl { get; init; }
        public string? Verifier { get; init; }
        public string? CreatedAt { get; init; }
    }
}
