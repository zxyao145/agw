namespace Agw.Setup.Contracts;

public class SystemInitializationSettings
{
    public const string SectionName = "SystemInitialization";

    public bool IsInitialized { get; set; }

    public string? ApiKey { get; set; }
}
