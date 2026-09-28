using Agw.Tools.Impl.ContextualTools.Shell;

namespace Agw.Tools.Tests;

public sealed class ShellFileAccessPolicyTests
{
    [Theory]
    [InlineData("cat README.md", "cat")]
    [InlineData("head -n 20 src/Program.cs", "head")]
    [InlineData("tail -f logs/app.log", "tail")]
    [InlineData("ls -la", "ls")]
    [InlineData("find . -name '*.cs'", "find")]
    [InlineData("grep -rn needle src", "grep")]
    [InlineData("rg needle", "rg")]
    [InlineData("touch notes.md", "touch")]
    [InlineData("mkdir -p build/out", "mkdir")]
    [InlineData("cp a.txt b.txt", "cp")]
    [InlineData("mv a.txt b.txt", "mv")]
    [InlineData("rm -rf build", "rm")]
    [InlineData("dotnet build 2>&1 | tee build.log", "tee")]
    [InlineData("sed -i 's/a/b/' file.txt", "sed")]
    [InlineData("sed 's/a/b/' file.txt", "sed")]
    [InlineData("awk '{print $1}' data.csv", "awk")]
    [InlineData("sort data.csv", "sort")]
    [InlineData("diff a.txt b.txt", "diff")]
    [InlineData("/bin/cat README.md", "cat")]
    [InlineData("\\cat README.md", "cat")]
    [InlineData("CAT.EXE README.md", "CAT")]
    [InlineData("Get-Content README.md", "Get-Content")]
    [InlineData("Remove-Item -Recurse build", "Remove-Item")]
    [InlineData("sudo rm -rf build", "rm")]
    [InlineData("env FOO=bar cat README.md", "cat")]
    [InlineData("timeout 10 cat README.md", "cat")]
    [InlineData("git ls-files | xargs rm", "rm")]
    [InlineData("git status && cat README.md", "cat")]
    [InlineData("git status; ls", "ls")]
    [InlineData("git status || ls", "ls")]
    [InlineData("(cd src && ls)", "ls")]
    [InlineData("echo $(cat secrets.txt)", "cat")]
    [InlineData("echo \"$(cat secrets.txt)\"", "cat")]
    [InlineData("echo `cat secrets.txt`", "cat")]
    [InlineData("FOO=1 cat README.md", "cat")]
    public void FindViolation_FileAccessProgram_NamesTheProgram(string command, string program)
    {
        var violation = ShellFileAccessPolicy.FindViolation(command);

        Assert.NotNull(violation);
        Assert.Contains($"'{program}'", violation, StringComparison.Ordinal);
        Assert.Contains("file_access_", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("echo hello > out.txt")]
    [InlineData("echo hello >> out.txt")]
    [InlineData("dotnet build > build.log 2>&1")]
    [InlineData("dotnet build &> build.log")]
    [InlineData("sort < data.csv")]
    [InlineData("git status 2> errors.txt")]
    [InlineData("echo hi >| out.txt")]
    [InlineData("echo hi > \"quoted name.txt\"")]
    public void FindViolation_FileRedirection_IsRejected(string command)
    {
        var violation = ShellFileAccessPolicy.FindViolation(command);

        Assert.NotNull(violation);
        Assert.Contains("Redirecting", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("python3 - <<EOF\nprint(open('x').read())\nEOF")]
    [InlineData("tr a b <<< hello")]
    [InlineData("diff <(git show HEAD:a) b")]
    public void FindViolation_HereDocumentsAndProcessSubstitution_AreRejected(string command)
    {
        var violation = ShellFileAccessPolicy.FindViolation(command);

        Assert.NotNull(violation);
        Assert.Contains("Here-documents", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bash -c 'cat README.md'", "bash")]
    [InlineData("sh -c \"rm -rf build\"", "sh")]
    [InlineData("python3 -c \"print(open('x').read())\"", "python3")]
    [InlineData("perl -e 'unlink \"x\"'", "perl")]
    [InlineData("perl -i -pe 's/a/b/' file", "perl")]
    [InlineData("node -e \"require('fs').readFileSync('x')\"", "node")]
    [InlineData("pwsh -Command Get-Content x", "pwsh")]
    [InlineData("echo 'print(1)' | python3 -", "python3")]
    public void FindViolation_InlineInterpreterCode_IsRejected(string command, string program)
    {
        var violation = ShellFileAccessPolicy.FindViolation(command);

        Assert.NotNull(violation);
        Assert.Contains($"'{program}'", violation, StringComparison.Ordinal);
        Assert.Contains("Inline code", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet test Agw.slnx")]
    [InlineData("dotnet build 2>&1")]
    [InlineData("git status")]
    [InlineData("git diff --stat")]
    [InlineData("git log --oneline -5 | sort")]
    [InlineData("git log | cut -d ' ' -f 1")]
    [InlineData("npm run build > /dev/null 2>&1")]
    [InlineData("pnpm install")]
    [InlineData("cd src && dotnet build")]
    [InlineData("pwd")]
    [InlineData("echo 'cat is not run here'")]
    [InlineData("echo \"ls inside quotes\"")]
    [InlineData("# cat README.md")]
    [InlineData("python3 scripts/generate.py")]
    [InlineData("bash scripts/build.sh")]
    [InlineData("node build.js")]
    [InlineData("sed 's/a/b/'")]
    [InlineData("awk '{print $1}'")]
    [InlineData("sort -k 2")]
    [InlineData("xargs echo")]
    [InlineData("wc -l < /dev/null")]
    [InlineData("echo $((1 + 2))")]
    [InlineData("which dotnet")]
    [InlineData("FOO=1 dotnet run")]
    public void FindViolation_ProgramExecution_IsAllowed(string command)
    {
        Assert.Null(ShellFileAccessPolicy.FindViolation(command));
    }

    [Fact]
    public void CreateShellPolicy_DeniesFileAccessAndAllowsPrograms()
    {
        var policy = ShellFileAccessPolicy.CreateShellPolicy();

        var denied = policy.Evaluate(new Microsoft.Agents.AI.Tools.Shell.ShellRequest("cat README.md", null));
        var allowed = policy.Evaluate(new Microsoft.Agents.AI.Tools.Shell.ShellRequest("dotnet build", null));

        Assert.False(denied.Allowed);
        Assert.Contains("'cat'", denied.Reason, StringComparison.Ordinal);
        Assert.True(allowed.Allowed);
    }
}
