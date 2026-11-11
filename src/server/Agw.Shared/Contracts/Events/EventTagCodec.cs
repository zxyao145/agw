using Agw.Shared.Exceptions;

namespace Agw.Shared.Contracts.Events;

public static class EventTagCodec
{
    public static string Encode(IReadOnlyList<string> tags)
    {
        if (tags is null)
            throw InvalidParam();
        ValidateTags(tags);
        return string.Join(",", tags);
    }

    public static IReadOnlyList<string> Decode(string value)
    {
        if (value is null)
            throw InvalidParam();
        if (value.Length == 0)
            return [];

        var tags = value.Split(',');
        ValidateTags(tags);
        return tags;
    }

    private static void ValidateTags(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            if (string.IsNullOrEmpty(tag) || tag.Contains(',') || tag.Any(char.IsControl))
                throw InvalidParam();
        }
    }

    private static AgwException InvalidParam() => new(ErrorCodes.InvalidParam);
}
