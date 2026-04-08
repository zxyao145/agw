using Agw.Shared;
using Agw.Shared.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Providers.Domain.Entities;

/// <summary>
/// Persists provider-issued OAuth tokens for a specific authenticated subject.
/// </summary>
[Table("oauth_authorization")]
public class OAuthAuthorizationToken : BaseEntity, IAggregateRoot
{
    /// <summary>
    /// Gets or sets the unique identifier for the stored token record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the provider name that issued the OAuth token.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider-specific subject or account identifier the token belongs to.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the access token used for authenticated provider requests.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional refresh token used to obtain a new access token.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Gets or sets the token type returned by the provider, such as Bearer.
    /// </summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>
    /// Gets or sets the optional space-delimited scopes granted to the token.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the access token expires, if known.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
