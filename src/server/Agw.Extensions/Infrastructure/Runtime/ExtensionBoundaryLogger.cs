using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionBoundaryLogger : ILogger
{
    private readonly ILogger _logger;

    public ExtensionBoundaryLogger(ILogger logger)
    {
        _logger = logger;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        var stableState = new StableLogState(state.ToString() ?? string.Empty, SanitizeProperties(state));
        return _logger.BeginScope(stableState);
    }

    public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        string renderedMessage = formatter(state, exception);
        List<KeyValuePair<string, object?>> properties = SanitizeProperties(state);
        if (exception is not null)
        {
            properties.Add(new KeyValuePair<string, object?>("ExtensionException", exception.ToString()));
        }

        var stableState = new StableLogState(renderedMessage, properties);
        _logger.Log(logLevel, eventId, stableState, exception: null, static (entry, _) => entry.RenderedMessage);
    }

    private static List<KeyValuePair<string, object?>> SanitizeProperties<TState>(TState state)
    {
        var properties = new List<KeyValuePair<string, object?>>();
        if (state is not IEnumerable<KeyValuePair<string, object?>> values)
        {
            return properties;
        }

        foreach (KeyValuePair<string, object?> pair in values)
        {
            properties.Add(new KeyValuePair<string, object?>(pair.Key, SanitizeValue(pair.Value)));
        }

        return properties;
    }

    private static object? SanitizeValue(object? value) =>
        value switch
        {
            null => null,
            string
            or bool
            or byte
            or sbyte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong
            or float
            or double
            or decimal
            or Guid
            or DateTimeOffset => value,
            _ => value.ToString(),
        };

    private sealed class StableLogState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly IReadOnlyList<KeyValuePair<string, object?>> _properties;

        public StableLogState(string renderedMessage, IReadOnlyList<KeyValuePair<string, object?>> properties)
        {
            RenderedMessage = renderedMessage;
            _properties = properties;
        }

        public string RenderedMessage { get; }

        public int Count => _properties.Count;

        public KeyValuePair<string, object?> this[int index] => _properties[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _properties.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => RenderedMessage;
    }
}
