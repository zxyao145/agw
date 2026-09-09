namespace Agw.Tools.Contracts.WebSearch;

public class WebSearchResult
{
    public string Query { get; set; } = "";
    public List<object> Results { get; set; } = new();
    public string Provider { get; set; } = "";
    public int TotalResults { get; set; }
    public double DurationSeconds { get; set; }
}
