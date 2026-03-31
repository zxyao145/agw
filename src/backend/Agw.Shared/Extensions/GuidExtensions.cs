namespace Agw.Shared;

public static class GuidExtensions
{
    public static string Normalize(this Guid id)
    {
        return Normalize(id, "D");
    }

    public static string Normalize(this Guid id, string? format)
    {
        return id.ToString(format);
    }
}
