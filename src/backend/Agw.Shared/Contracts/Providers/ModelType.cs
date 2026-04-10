namespace Agw.Shared.Contracts.Providers;

[Flags]
public enum ModelType
{
    None = 0,
    Chat = 1,
    Image = 2,
    Audio = 4,
    Embedding = 8
}
