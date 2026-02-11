namespace CodexSdk;

public sealed record Turn(IReadOnlyList<ThreadItem> Items, string FinalResponse, Usage? Usage);
public sealed record RunResult(IReadOnlyList<ThreadItem> Items, string FinalResponse, Usage? Usage);
public sealed record RunStreamedResult(IAsyncEnumerable<ThreadEvent> Events);

public abstract record UserInput
{
    public sealed record Text(string Value) : UserInput;
    public sealed record LocalImage(string Path) : UserInput;
}

public sealed class Thread
{
    private readonly CodexExec _exec;
    private readonly CodexOptions _options;
    private readonly ThreadOptions _threadOptions;
    private string? _id;

    internal Thread(CodexExec exec, CodexOptions options, ThreadOptions threadOptions, string? id = null)
    {
        _exec = exec;
        _options = options;
        _threadOptions = threadOptions;
        _id = id;
    }

    public string? Id => _id;

    public Task<RunStreamedResult> RunStreamedAsync(string input, TurnOptions? turnOptions = null)
        => Task.FromResult(new RunStreamedResult(RunStreamedInternalAsync(input, Array.Empty<string>(), turnOptions ?? new TurnOptions())));

    public Task<RunStreamedResult> RunStreamedAsync(IReadOnlyList<UserInput> input, TurnOptions? turnOptions = null)
    {
        var (prompt, images) = NormalizeInput(input);
        return Task.FromResult(new RunStreamedResult(RunStreamedInternalAsync(prompt, images, turnOptions ?? new TurnOptions())));
    }

    public async Task<RunResult> RunAsync(string input, TurnOptions? turnOptions = null)
        => await RunCoreAsync(RunStreamedInternalAsync(input, Array.Empty<string>(), turnOptions ?? new TurnOptions()));

    public async Task<RunResult> RunAsync(IReadOnlyList<UserInput> input, TurnOptions? turnOptions = null)
    {
        var (prompt, images) = NormalizeInput(input);
        return await RunCoreAsync(RunStreamedInternalAsync(prompt, images, turnOptions ?? new TurnOptions()));
    }

    private async Task<RunResult> RunCoreAsync(IAsyncEnumerable<ThreadEvent> events)
    {
        var items = new List<ThreadItem>();
        var finalResponse = string.Empty;
        Usage? usage = null;
        ThreadError? turnFailure = null;

        await foreach (var @event in events)
        {
            switch (@event)
            {
                case ItemCompletedEvent { Item: AgentMessageItem message } itemCompleted:
                    finalResponse = message.Text;
                    items.Add(itemCompleted.Item);
                    break;
                case ItemCompletedEvent itemCompleted:
                    items.Add(itemCompleted.Item);
                    break;
                case TurnCompletedEvent turnCompleted:
                    usage = turnCompleted.Usage;
                    break;
                case TurnFailedEvent turnFailed:
                    turnFailure = turnFailed.Error;
                    break;
            }

            if (turnFailure is not null)
            {
                break;
            }
        }

        if (turnFailure is not null)
        {
            throw new InvalidOperationException(turnFailure.Message);
        }

        return new RunResult(items, finalResponse, usage);
    }

    private async IAsyncEnumerable<ThreadEvent> RunStreamedInternalAsync(
        string prompt,
        IReadOnlyList<string> images,
        TurnOptions turnOptions)
    {
        await using var schema = await OutputSchemaFile.CreateAsync(turnOptions.OutputSchema, turnOptions.CancellationToken);

        await foreach (var line in _exec.RunAsync(new CodexExecArgs
                       {
                           Input = prompt,
                           BaseUrl = _options.BaseUrl,
                           ApiKey = _options.ApiKey,
                           ThreadId = _id,
                           Images = images,
                           Model = _threadOptions.Model,
                           SandboxMode = _threadOptions.SandboxMode,
                           WorkingDirectory = _threadOptions.WorkingDirectory,
                           SkipGitRepoCheck = _threadOptions.SkipGitRepoCheck,
                           OutputSchemaFile = schema.SchemaPath,
                           ModelReasoningEffort = _threadOptions.ModelReasoningEffort,
                           CancellationToken = turnOptions.CancellationToken,
                           NetworkAccessEnabled = _threadOptions.NetworkAccessEnabled,
                           WebSearchMode = _threadOptions.WebSearchMode,
                           WebSearchEnabled = _threadOptions.WebSearchEnabled,
                           ApprovalPolicy = _threadOptions.ApprovalPolicy,
                           AdditionalDirectories = _threadOptions.AdditionalDirectories,
                       }))
        {
            ThreadEvent parsed;
            try
            {
                parsed = ThreadEventParser.Parse(line);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Failed to parse item: {line}", error);
            }

            if (parsed is ThreadStartedEvent started)
            {
                _id = started.ThreadId;
            }

            yield return parsed;
        }
    }

    private static (string Prompt, IReadOnlyList<string> Images) NormalizeInput(IReadOnlyList<UserInput> input)
    {
        var promptParts = new List<string>();
        foreach (var item in input)
        {
            if (item is UserInput.Text text)
            {
                promptParts.Add(text.Value);
            }
        }

        return (string.Join("\n\n", promptParts), input.OfType<UserInput.LocalImage>().Select(x => x.Path).ToArray());
    }
}
