using Agw.Shared.Exceptions;

namespace Agw.Shared.Pagination;

public static class PageValidation
{
    private static readonly int[] DefaultPageSizes = [10, 20, 50];
    private static readonly int[] ExtendedPageSizes = [.. DefaultPageSizes, 100];

    public static void Validate(int pageIndex, int pageSize, bool allowPageSize100 = false)
    {
        if (pageIndex < 1)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "pageIndex must be at least 1.");
        }

        var supportedPageSizes = allowPageSize100 ? ExtendedPageSizes : DefaultPageSizes;
        if (!supportedPageSizes.Contains(pageSize))
        {
            var sizes = string.Join(", ", supportedPageSizes.SkipLast(1));
            throw new AgwException(
                ErrorCodes.InvalidPageSize,
                $"pageSize must be one of {sizes}, or {supportedPageSizes[^1]}."
            );
        }
    }
}
