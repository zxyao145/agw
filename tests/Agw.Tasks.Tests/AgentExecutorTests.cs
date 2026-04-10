using System.Reflection;

using Agw.Jobs.Application.Services;
using Agw.Jobs.Domain.Entities;
using Agw.Shared.Contracts.Agents;

namespace Agw.Tasks.Tests;

public class AgentExecutorTests
{
    [Theory]
    [InlineData("  Nightly Sync  ", "  explicit prompt  ", "explicit prompt", "Nightly Sync")]
    [InlineData("   ", "  synchronize data  ", "synchronize data", "synchronize data")]
    [InlineData("   ", "   ", "Scheduled Job", "Scheduled Job")]
    public void BuildPromptAndTitle_UsesExpectedFallbacks(
        string name,
        string prompt,
        string expectedPrompt,
        string expectedTitle)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            AgentType = AgentRuntimeType.Agent,
            AgentId = Guid.NewGuid(),
            Name = name,
            Prompt = prompt
        };

        var result = InvokeBuildPromptAndTitle(job);

        Assert.Equal(expectedPrompt, result.Prompt);
        Assert.Equal(expectedTitle, result.Title);
    }

    private static (string Prompt, string Title) InvokeBuildPromptAndTitle(Job job)
    {
        var method = typeof(AgentExecutor).GetMethod(
            "BuildPromptAndTitle",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [job]);
        Assert.NotNull(result);

        var promptField = result!.GetType().GetField("Item1");
        var titleField = result.GetType().GetField("Item2");

        return (
            (string)promptField!.GetValue(result)!,
            (string)titleField!.GetValue(result)!);
    }
}
