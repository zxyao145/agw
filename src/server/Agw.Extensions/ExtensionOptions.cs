namespace Agw.Extensions;

public sealed class ExtensionOptions
{
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan UnloadTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan StatePollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaximumPackageBytes { get; set; } = 256 * 1024 * 1024;
    public int MaximumExtractedBytes { get; set; } = 1024 * 1024 * 1024;
    public string Directory { get; set; } = "./Extensions";
    public string HostRole { get; set; } = "Standalone";
}
