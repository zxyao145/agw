using Agw.Integrations.Domain.Entities;
using Agw.Shared.Contracts.Integrations;

namespace Agw.Integrations;

public static class IntegrationConstants
{
    public const string CallbackPath = "/integrations/callback";
    public const string SystemActor = "integrations/oauth-callback";

    public const string RedirectPath = "/api/integrations/oauth/callback";
    public const string UiCallbackPath = CallbackPath;

    public static IReadOnlyList<AppDefinition> AppList { get; } =
        [
            new AppDefinition
            {
                Name = "github",
                DisplayName = "GitHub",
                Category = CategoryType.GitServer,
                Provider = "GitHub OAuth App",
                Description = "Connect your GitHub account to access repositories, issues, and pull requests directly from AGW.",
                AuthUrl = "https://github.com/login/oauth/authorize",
                TokenEndpoint = "https://github.com/login/oauth/access_token",
                Scopes = ["repo", "read:user", "read:org"],
                UsePkce = true,

                Tags = ["Git", "Coding"],
                ToolNames = ["git_clone"],
            },
            new AppDefinition
            {
                Name = "google-workspace",
                DisplayName = "Google Workspace",
                Category = CategoryType.Other,
                Provider = "Google OAuth 2.0",
                Description = "Connect calendars, files, and docs so workflows can act on shared organizational context.",
                AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth",
                TokenEndpoint = "https://oauth2.googleapis.com/token",
                SubjectField = "sub",
                Scopes = ["repo", "read:user", "read:org"],
                UsePkce = true,

                Tags = ["Git", "Coding"],
                ToolNames = [],
            },
        ];
}

