namespace Agw.Integrations.Tools.GitHub.Dtos;

public class GitHubRepoInfo
{
    /// <summary>
    /// example: name
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// example: zxyao145/agw
    /// </summary>
    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = "";

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("owner")]
    public GitHubOwner Owner { get; set; } = default!;

    [JsonPropertyName("fork")]
    public bool Fork { get; set; }

    [Description("URL for obtaining detailed information about the repository.")]
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>
    /// example: git://github.com/zxyao145/agw.git
    /// </summary>
    [JsonPropertyName("git_url")]
    public string GitUrl { get; set; } = "";

    /// <summary>
    /// example: git@github.com:zxyao145/agw.git
    /// </summary>
    [JsonPropertyName("ssh_url")]
    public string SshUrl { get; set; } = "";

    /// <summary>
    /// example: https://github.com/zxyao145/agw.git
    /// </summary>
    [JsonPropertyName("clone_url")]
    public string CloneUrl { get; set; } = "";
}


public class GitHubOwner
{
    /// <summary>
    /// use name
    /// </summary>
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    [JsonPropertyName("avatar_url")]
    public string AvatarUrl { get; set; } = "";


    [Description("URL for obtaining detailed information about the user.")]
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [Description("User's address on GitHub homepage")]
    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    [Description("list user's repositories")]
    [JsonPropertyName("repos_url")]
    public string ReposUrl { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("user_view_type")]
    public string UserViewType { get; set; } = "";
}
