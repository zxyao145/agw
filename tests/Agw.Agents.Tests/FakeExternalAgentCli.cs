using System.Text.Json;

namespace Agw.Agents.Tests;

/// <summary>
/// 在测试目录中写入可执行的 Pi 命令行替身，SDK 通过真实进程、参数与 JSONL 协议与它通信。
/// Writes an executable Pi command-line stand-in into the test directory; the SDK talks to it through a real process, arguments and the JSONL protocol.
/// </summary>
internal sealed class FakeExternalAgentCli
{
    private const string PiScript = """
        #!/usr/bin/env node
        'use strict';
        const fs = require('fs');
        const path = require('path');
        const crypto = require('crypto');
        const readline = require('readline');
        const args = process.argv.slice(2);
        const sessionIndex = args.indexOf('--session');
        const resumed = sessionIndex >= 0 ? args[sessionIndex + 1] : null;
        const sessionId = resumed ?? crypto.randomUUID();
        fs.appendFileSync(path.join(__dirname, 'pi-invocations.jsonl'), JSON.stringify({ args, sessionId, resumed }) + '\n');
        const emit = event => process.stdout.write(JSON.stringify(event) + '\n');
        readline.createInterface({ input: process.stdin }).on('line', line => {
          const command = JSON.parse(line);
          const respond = data => emit(Object.assign({ type: 'response', id: command.id, command: command.type, success: true }, data === undefined ? {} : { data }));
          switch (command.type) {
            case 'get_state':
              respond({ sessionId, sessionFile: path.join(__dirname, 'pi-' + sessionId + '.jsonl') });
              break;
            case 'prompt':
              respond();
              emit({ type: 'agent_start' });
              emit({ type: 'turn_end', message: { role: 'assistant', content: [{ type: 'text', text: 'reply from ' + sessionId }], usage: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0, totalTokens: 2 }, stopReason: 'stop', timestamp: 1 }, toolResults: [] });
              emit({ type: 'agent_end', messages: [], willRetry: false });
              emit({ type: 'agent_settled' });
              break;
            default:
              respond();
              break;
          }
        });
        """;

    private readonly string _directory;

    public FakeExternalAgentCli(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        PiPath = WriteExecutable("pi", PiScript);
    }

    public string PiPath { get; }

    public IReadOnlyList<FakeInvocation> PiInvocations => ReadInvocations("pi-invocations.jsonl");

    private IReadOnlyList<FakeInvocation> ReadInvocations(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                return new FakeInvocation(
                    root.GetProperty("sessionId").GetString()!,
                    root.GetProperty("resumed").GetString(),
                    root.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()!).ToArray(),
                    root.TryGetProperty("input", out var input) ? input.GetString() : null
                );
            })
            .ToList();
    }

    private string WriteExecutable(string name, string script)
    {
        var path = Path.Combine(_directory, name);
        // 原始字符串的换行跟随源文件；shebang 行末的 CR 会被 env 当作解释器名称的一部分。
        // Raw string line endings follow the source file; a CR after the shebang becomes part of the interpreter name for env.
        File.WriteAllText(path, script.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}

/// <summary>
/// 一次 Pi RPC 命令行调用。A Pi RPC command-line invocation.
/// </summary>
internal sealed record FakeInvocation(
    string SessionId,
    string? Resumed,
    IReadOnlyList<string> Arguments,
    string? Input
);
