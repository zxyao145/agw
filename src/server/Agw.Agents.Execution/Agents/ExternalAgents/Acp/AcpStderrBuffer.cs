using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal sealed class AcpStderrBuffer(int maxLines)
{
    private const int MaxChars = 4000;
    private readonly Queue<string> _lines = new();
    private readonly Lock _lock = new();
    private int _length;

    public string Tail
    {
        get
        {
            lock (_lock)
            {
                var tail = string.Join('\n', _lines);
                return tail.Length > MaxChars ? tail[^MaxChars..] : tail;
            }
        }
    }

    public async Task ReadAsync(StreamReader reader, ILogger logger, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            logger.LogDebug("ACP stderr: {StderrLine}", line);
            lock (_lock)
            {
                line = line.Length > MaxChars ? line[^MaxChars..] : line;
                _lines.Enqueue(line);
                _length += line.Length + 1;
                while (_lines.Count > maxLines || _length - _lines.Peek().Length - 1 > MaxChars)
                    _length -= _lines.Dequeue().Length + 1;
            }
        }
    }
}
