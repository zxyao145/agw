namespace CodexSdk;

public sealed class Codex
{
    private readonly CodexExec _exec;
    private readonly CodexOptions _options;

    public Codex(CodexOptions? options = null)
    {
        _options = options ?? new CodexOptions();
        _exec = new CodexExec(_options.CodexPathOverride, _options.Environment, _options.Config);
    }

    public Thread StartThread(ThreadOptions? options = null)
        => new(_exec, _options, options ?? new ThreadOptions());

    public Thread ResumeThread(string id, ThreadOptions? options = null)
        => new(_exec, _options, options ?? new ThreadOptions(), id);
}
