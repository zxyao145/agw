namespace Agw.Jobs.Tests;

public class JobSchedulerStructureTests
{
    [Fact]
    public void JobScheduler_StateAndDispatchLoop_RemainSimple()
    {
        var source = File.ReadAllText(FindJobSchedulerPath());
        var dispatchLoop = ExtractMethod(source, "RunDispatchLoopAsync");

        Assert.DoesNotContain("ConcurrentDictionary", source, StringComparison.Ordinal);
        Assert.True(
            CountCodeLines(dispatchLoop) <= 35,
            "RunDispatchLoopAsync should stay small enough to show the dispatch loop shape directly.");
    }

    private static string FindJobSchedulerPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(
                directory.FullName,
                "src",
                "backend",
                "Agw.Jobs",
                "Executors",
                "Common",
                "JobScheduler.cs");

            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate JobScheduler.cs from the test output directory.");
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var methodStart = source.IndexOf($"private async Task {methodName}", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Could not find method {methodName}.");

        var bodyStart = source.IndexOf('{', methodStart);
        Assert.True(bodyStart >= 0, $"Could not find method body for {methodName}.");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(bodyStart, index - bodyStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not extract method body for {methodName}.");
    }

    private static int CountCodeLines(string source)
    {
        return source
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Count(line =>
            {
                var trimmed = line.Trim();
                return trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal);
            });
    }
}
