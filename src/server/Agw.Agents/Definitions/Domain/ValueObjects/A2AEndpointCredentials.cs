using System.Collections.ObjectModel;

namespace Agw.Agents.Definitions.Domain.ValueObjects;

public sealed class A2AEndpointCredentials
{
    public A2AEndpointCredentials(IEnumerable<KeyValuePair<string, Dictionary<string, string>>> headersByOrigin)
    {
        HeadersByOrigin = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(
            headersByOrigin.ToDictionary(
                group => group.Key,
                group =>
                    (IReadOnlyDictionary<string, string>)
                        new ReadOnlyDictionary<string, string>(
                            new Dictionary<string, string>(group.Value, StringComparer.OrdinalIgnoreCase)
                        ),
                StringComparer.Ordinal
            )
        );
    }

    public static A2AEndpointCredentials Empty { get; } = new([]);

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> HeadersByOrigin { get; }
}
