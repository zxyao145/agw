using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agw.Agents.Tests;

/// <summary>
/// 在测试目录中写入可执行的 Codex 与 Pi 命令行替身，SDK 通过真实进程、参数与 JSONL 协议与它们通信。替身按控制文件决定行为，并把每次调用写入调用记录。
/// Writes executable Codex and Pi command-line stand-ins into the test directory; the SDKs talk to them through real processes, arguments and the JSONL protocols. The stand-ins follow a control file and append every invocation to an invocation log.
/// </summary>
internal sealed class FakeExternalAgentCli
{
    private const string CodexScript = """
        #!/usr/bin/env node
        'use strict';
        const fs = require('fs');
        const path = require('path');
        const crypto = require('crypto');
        const control = JSON.parse(fs.readFileSync(path.join(__dirname, 'control.json'), 'utf8'));
        const args = process.argv.slice(2);
        const resumeIndex = args.indexOf('resume');
        const resumed = resumeIndex >= 0 ? args[resumeIndex + 1] : null;
        const emit = event => process.stdout.write(JSON.stringify(event) + '\n');
        let input = '';
        process.stdin.setEncoding('utf8');
        process.stdin.on('data', chunk => { input += chunk; });
        process.stdin.on('end', async () => {
          const threadId = resumed ?? crypto.randomUUID();
          if (!resumed || control.threadStartedOnResume) emit({ type: 'thread.started', thread_id: threadId });
          emit({ type: 'turn.started' });
          fs.appendFileSync(path.join(__dirname, 'codex-invocations.jsonl'), JSON.stringify({ args, threadId, resumed, input }) + '\n');
          if (control.holdFile) {
            while (!fs.existsSync(control.holdFile)) await new Promise(resolve => setTimeout(resolve, 20));
          }
          emit({ type: 'item.completed', item: { id: 'item-1', type: 'agent_message', text: 'reply from ' + threadId } });
          emit({ type: 'turn.completed', usage: { input_tokens: 1, cached_input_tokens: 0, output_tokens: 1, reasoning_output_tokens: 0 } });
        });
        """;

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
        CodexPath = WriteExecutable("codex", CodexScript);
        PiPath = WriteExecutable("pi", PiScript);
        Configure(threadStartedOnResume: true);
    }

    public string CodexPath { get; }

    public string PiPath { get; }

    /// <summary>
    /// 存在这个文件之前，Codex 替身在 turn.started 之后等待。
    /// Until this file exists, the Codex stand-in waits after turn.started.
    /// </summary>
    public string HoldFile => Path.Combine(_directory, "release");

    public void Configure(bool threadStartedOnResume, bool hold = false)
    {
        File.Delete(HoldFile);
        File.WriteAllText(
            Path.Combine(_directory, "control.json"),
            new JsonObject
            {
                ["threadStartedOnResume"] = threadStartedOnResume,
                ["holdFile"] = hold ? HoldFile : null,
            }.ToJsonString()
        );
    }

    public void Release() => File.WriteAllText(HoldFile, string.Empty);

    public IReadOnlyList<FakeInvocation> CodexInvocations => ReadInvocations("codex-invocations.jsonl", "threadId");

    public IReadOnlyList<FakeInvocation> PiInvocations => ReadInvocations("pi-invocations.jsonl", "sessionId");

    /// <summary>
    /// 等待 Codex 替身开始第 count 次运行并进入 turn.started 之后的阶段。
    /// Waits until the Codex stand-in has started its count-th run and passed turn.started.
    /// </summary>
    public async Task WaitForCodexRunsAsync(int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (CodexInvocations.Count < count)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private IReadOnlyList<FakeInvocation> ReadInvocations(string fileName, string idProperty)
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
                    root.GetProperty(idProperty).GetString()!,
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
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}

/// <summary>
/// 一次命令行调用：Codex 的 Input 为标准输入收到的提示词，Pi 的提示词经 RPC 发送，Input 为空。
/// One command-line invocation: Codex's Input is the prompt read from standard input; Pi receives prompts over RPC, so its Input is null.
/// </summary>
internal sealed record FakeInvocation(
    string SessionId,
    string? Resumed,
    IReadOnlyList<string> Arguments,
    string? Input
);
