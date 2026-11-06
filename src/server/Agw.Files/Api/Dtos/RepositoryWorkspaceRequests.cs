namespace Agw.Files.Api.Dtos;

public sealed record CloneRepositoryRequest
{
    public Guid ProjectId { get; init; }
    public string Url { get; init; } = "";

    /// <summary>目录名；省略时从 URL 推导。Directory name; derived from the URL when omitted.</summary>
    public string? Name { get; init; }
    public string? ProxyUrl { get; init; }
}

public sealed record CreateFeatureRequest
{
    public Guid ProjectId { get; init; }

    /// <summary>新 feature 的名称，前面会加上 <c>yyMMdd-</c>。The new feature name, prefixed with <c>yyMMdd-</c>.</summary>
    public string? Name { get; init; }

    /// <summary>已有 feature 的完整名称；设置时向其添加仓库。An existing feature's full name; adds repositories to it when set.</summary>
    public string? Feature { get; init; }

    /// <summary>客户端当地日期；省略时使用 UTC 日期。The client's local date; defaults to the UTC date.</summary>
    public DateOnly? Date { get; init; }
    public string[] Repositories { get; init; } = [];
}

public sealed record DeleteFeatureRequest
{
    public Guid ProjectId { get; init; }
    public string Feature { get; init; } = "";
}

public sealed record SwitchWorktreeBranchRequest
{
    public Guid ProjectId { get; init; }
    public string Feature { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Branch { get; init; } = "";
    public bool CreateBranch { get; init; }
}

public sealed record WorktreeRequest
{
    public Guid ProjectId { get; init; }
    public string Feature { get; init; } = "";
    public string Repository { get; init; } = "";
}

public sealed record RootRepositoryRequest
{
    public Guid ProjectId { get; init; }
    public string Repository { get; init; } = "";
}

public sealed record SyncRepositoryRequest
{
    public Guid ProjectId { get; init; }
    public string Repository { get; init; } = "";
    public string? ProxyUrl { get; init; }
}
