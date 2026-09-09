namespace Agw.Tools.Contracts.WebSearch;

public class WebSearchToolParams
{
    [Description("The search query to use.")]
    public string Query { get; set; } = "";

    [Description("Only include search results from these domains.")]
    public List<string>? AllowedDomains { get; set; }

    [Description("Never include search results from these domains.")]
    public List<string>? BlockedDomains { get; set; }

    [Description("Maximum number of search results to return. Defaults to 5.")]
    public int? MaxResults { get; set; }
}
