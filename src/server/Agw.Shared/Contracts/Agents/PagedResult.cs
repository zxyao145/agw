namespace Agw.Shared.Contracts.Agents;

public sealed class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required long Total { get; init; }

    public required int PageIndex { get; init; }

    public required int PageSize { get; init; }
}
