namespace Agw.Host.Runtime;

public sealed record ServerRuntimeDescriptor(
    int SchemaVersion,
    int Pid,
    string BaseUrl,
    int Port,
    string ServerVersion,
    int ApiMajorVersion,
    DateTimeOffset StartedAt);
