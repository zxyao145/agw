using System.Text.Json;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.ContextualTools.Shell;

/// <summary>
/// 在 shell 函数执行前用 <see cref="ShellFileAccessPolicy"/> 检查命令，违反约束时抛出带错误码的
/// <see cref="AgwException"/>，由工具异常处理交给模型。
/// Checks the command with <see cref="ShellFileAccessPolicy"/> before the shell function runs and throws an
/// <see cref="AgwException"/> with an error code on violation, which the tool exception handling reports to the model.
/// </summary>
internal sealed class ShellFileAccessGuardAIFunction : DelegatingAIFunction
{
    private const string CommandParameter = "command";

    public ShellFileAccessGuardAIFunction(AIFunction innerFunction)
        : base(innerFunction) { }

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        if (
            arguments.TryGetValue(CommandParameter, out var value)
            && ReadString(value) is { } command
            && ShellFileAccessPolicy.FindViolation(command) is { } violation
        )
        {
            throw new AgwException(ErrorCodes.ShellFileAccessNotAllowed, violation);
        }

        return base.InvokeCoreAsync(arguments, cancellationToken);
    }

    private static string? ReadString(object? value) =>
        value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
}

/// <summary>
/// 向模型说明 shell 的使用约束，避免模型反复尝试被拒绝的命令。
/// Tells the model about the shell restriction so it does not keep trying rejected commands.
/// </summary>
internal sealed class ShellFileAccessInstructionsProvider : AIContextProvider
{
    internal const string Instructions = """
        ## Shell Usage
        The shell has the lowest priority of all your tools. Before running a shell command, check whether any other
        available tool can do the job and use that tool instead; use the shell only when nothing else can perform the
        task, such as running builds, tests, package managers and git.
        The shell must not read, list, search, create, write, edit or delete files: commands like cat, head, tail,
        less, ls, find, grep, rg, touch, tee, cp, mv, rm, mkdir, sed -i, inline interpreter code (sh -c, python -c)
        and any redirection to or from a file are rejected by the server before they run. Use the file_access_* tools
        for every file operation.
        """;

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(new AIContext { Instructions = Instructions });
}
