using System.Text;

using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Exceptions;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Agw.Tools.ToolBlocks.Blocks.ProjectMemory;

/// <summary>
/// Provides project-scoped file memory without persisting provider state in an Agent session.
/// </summary>
public sealed class ProjectMemoryProvider : AIContextProvider
{
    public const string WriteToolName = "project_memory_write";
    public const string ReadFileToolName = "project_memory_read";
    public const string DeleteFileToolName = "project_memory_delete";
    public const string LsToolName = "project_memory_ls";
    public const string GrepToolName = "project_memory_grep";
    public const string ReplaceToolName = "project_memory_replace";
    public const string ReplaceLinesToolName = "project_memory_replace_lines";

    private const string DescriptionSuffix = "_description.md";
    private const string MemoryIndexFileName = "memories.md";
    private const int MaxIndexEntries = 50;

    private const string Instructions =
        """
        ## Project Memory
        You have access to project-scoped, file-based memory through the `project_memory_*` tools.
        These memories are shared by all agents and conversations in the current project.
        Use them for durable project knowledge such as architecture decisions, conventions, plans, and reusable results.
        Do not use project memory for private user information that should follow a user across projects.

        - Use descriptive file names (for example, "projectarchitecture.md" or "codingconventions.md").
        - Include a description when writing a file to improve future discovery.
        - Before starting related work, use project_memory_ls and project_memory_grep to find existing memories.
        - Keep memories current by overwriting files or using project_memory_replace and project_memory_replace_lines.
        """;

    private readonly AgentFileStore _fileStore;
    private readonly IApplicationLock _applicationLock;
    private readonly string _mutationResourceName;
    private AITool[]? _tools;

    public ProjectMemoryProvider(
        AgentFileStore fileStore,
        IApplicationLock applicationLock,
        string mutationResourceName)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        ArgumentNullException.ThrowIfNull(applicationLock);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationResourceName);

        _fileStore = fileStore;
        _applicationLock = applicationLock;
        _mutationResourceName = mutationResourceName;
    }

    public override IReadOnlyList<string> StateKeys => [];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var result = new AIContext
        {
            Instructions = Instructions,
            Tools = _tools ??= CreateTools()
        };
        var index = await _fileStore.ReadAsync(MemoryIndexFileName, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(index))
        {
            result.Messages =
            [
                new ChatMessage(
                    ChatRole.User,
                    "The following is the shared project memory index. " +
                    "Read relevant files with project_memory_read before continuing.\n\n" +
                    index)
            ];
        }

        return result;
    }

    [Description("Write a project memory file. Overwrites the file if it already exists. Include a description to improve future discovery.")]
    private async Task<string> WriteAsync(
        string fileName,
        string content,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeMemoryFileName(fileName);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken)
            .ConfigureAwait(false);

        await _fileStore.WriteAsync(normalized, content, cancellationToken).ConfigureAwait(false);
        var descriptionPath = GetDescriptionFileName(normalized);
        if (string.IsNullOrWhiteSpace(description))
        {
            await _fileStore.DeleteAsync(descriptionPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _fileStore.WriteAsync(descriptionPath, description, cancellationToken)
                .ConfigureAwait(false);
        }

        await RebuildMemoryIndexAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(description)
            ? $"File '{fileName}' written."
            : $"File '{fileName}' written with description.";
    }

    [Description("Read a project memory file by name. Returns a not-found message when the file does not exist.")]
    private async Task<string> ReadAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeMemoryFileName(fileName);
        return await _fileStore.ReadAsync(normalized, cancellationToken).ConfigureAwait(false)
            ?? $"File '{fileName}' not found.";
    }

    [Description("Delete a project memory file and its companion description, if present.")]
    private async Task<string> DeleteAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeMemoryFileName(fileName);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken)
            .ConfigureAwait(false);

        var deleted = await _fileStore.DeleteAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        await _fileStore.DeleteAsync(GetDescriptionFileName(normalized), cancellationToken)
            .ConfigureAwait(false);
        await RebuildMemoryIndexAsync(cancellationToken).ConfigureAwait(false);
        return deleted
            ? $"File '{fileName}' deleted."
            : $"File '{fileName}' not found.";
    }

    [Description("List project memory files with their descriptions. Optionally filter names with a glob_pattern such as '*.md'.")]
    private async Task<List<FileListEntry>> LsAsync(
        string? globPattern = null,
        CancellationToken cancellationToken = default)
    {
        var files = (await _fileStore.ListChildrenAsync(string.Empty, cancellationToken)
                .ConfigureAwait(false))
            .Where(static entry => string.Equals(entry.Type, FileStoreEntry.File, StringComparison.Ordinal))
            .Select(static entry => entry.Name)
            .ToList();
        var availableFiles = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var matcher = CreateGlobMatcher(globPattern);
        var result = new List<FileListEntry>();

        foreach (var file in files)
        {
            if (IsInternalFile(file) || matcher?.Match(file).HasMatches == false)
            {
                continue;
            }

            string? description = null;
            var descriptionPath = GetDescriptionFileName(file);
            if (availableFiles.Contains(descriptionPath))
            {
                description = await _fileStore.ReadAsync(descriptionPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            result.Add(new FileListEntry
            {
                Name = file,
                Type = FileStoreEntry.File,
                Description = description
            });
        }

        return result;
    }

    [Description("Search project memory contents with a case-insensitive regular expression and an optional glob_pattern.")]
    private async Task<List<FileSearchResult>> GrepAsync(
        string regexPattern,
        string? globPattern = null,
        CancellationToken cancellationToken = default)
    {
        var matches = await _fileStore.SearchAsync(
                string.Empty,
                regexPattern,
                string.IsNullOrWhiteSpace(globPattern) ? null : globPattern,
                recursive: false,
                cancellationToken)
            .ConfigureAwait(false);
        return matches
            .Where(static match => !IsInternalFile(match.FileName))
            .ToList();
    }

    [Description("Replace old_string with new_string in a project memory file. Unless replace_all is true, exactly one occurrence is required.")]
    private async Task<string> ReplaceAsync(
        string fileName,
        string oldString,
        string newString,
        bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeMemoryFileName(fileName);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken)
            .ConfigureAwait(false);

        var content = await _fileStore.ReadAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        if (content == null)
        {
            return $"File '{fileName}' not found.";
        }

        var replacement = ApplyReplace(content, oldString, newString, replaceAll);
        await _fileStore.WriteAsync(normalized, replacement.Content, cancellationToken)
            .ConfigureAwait(false);
        return $"Replaced {replacement.Count} occurrence(s) in '{fileName}'.";
    }

    [Description("Replace 1-based lines in a project memory file. Each edit supplies line_number and literal new_line, including any trailing newline to keep; an empty new_line deletes the line.")]
    private async Task<string> ReplaceLinesAsync(
        string fileName,
        List<FileLineEdit> edits,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeMemoryFileName(fileName);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken)
            .ConfigureAwait(false);

        var content = await _fileStore.ReadAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        if (content == null)
        {
            return $"File '{fileName}' not found.";
        }

        var updated = ApplyReplaceLines(content, edits);
        await _fileStore.WriteAsync(normalized, updated, cancellationToken).ConfigureAwait(false);
        return $"Replaced {edits.Count} line(s) in '{fileName}'.";
    }

    private AITool[] CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(
                (Func<string, string, string?, CancellationToken, Task<string>>)WriteAsync,
                new AIFunctionFactoryOptions { Name = WriteToolName }),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<string>>)ReadAsync,
                new AIFunctionFactoryOptions { Name = ReadFileToolName }),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<string>>)DeleteAsync,
                new AIFunctionFactoryOptions { Name = DeleteFileToolName }),
            AIFunctionFactory.Create(
                (Func<string?, CancellationToken, Task<List<FileListEntry>>>)LsAsync,
                new AIFunctionFactoryOptions { Name = LsToolName }),
            AIFunctionFactory.Create(
                (Func<string, string?, CancellationToken, Task<List<FileSearchResult>>>)GrepAsync,
                new AIFunctionFactoryOptions { Name = GrepToolName }),
            AIFunctionFactory.Create(
                (Func<string, string, string, bool, CancellationToken, Task<string>>)ReplaceAsync,
                new AIFunctionFactoryOptions { Name = ReplaceToolName }),
            AIFunctionFactory.Create(
                (Func<string, List<FileLineEdit>, CancellationToken, Task<string>>)ReplaceLinesAsync,
                new AIFunctionFactoryOptions { Name = ReplaceLinesToolName })
        ];
    }

    private async Task RebuildMemoryIndexAsync(CancellationToken cancellationToken)
    {
        var files = (await _fileStore.ListChildrenAsync(string.Empty, cancellationToken)
                .ConfigureAwait(false))
            .Where(static entry => string.Equals(entry.Type, FileStoreEntry.File, StringComparison.Ordinal))
            .Select(static entry => entry.Name)
            .Where(static file => !IsInternalFile(file))
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .Take(MaxIndexEntries)
            .ToList();
        var index = new StringBuilder()
            .AppendLine("# Project Memory Index")
            .AppendLine();

        foreach (var file in files)
        {
            var description = await _fileStore.ReadAsync(
                    GetDescriptionFileName(file),
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(description))
            {
                index.Append("- **").Append(file).AppendLine("**");
            }
            else
            {
                index.Append("- **").Append(file).Append("**: ").AppendLine(description);
            }
        }

        await _fileStore.WriteAsync(MemoryIndexFileName, index.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<IAsyncDisposable> AcquireMutationLockAsync(CancellationToken cancellationToken) =>
        _applicationLock.AcquireAsync(_mutationResourceName, cancellationToken);

    private static string NormalizeMemoryFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw InvalidParameter("A project memory file name must not be empty.");
        }

        var normalized = fileName.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(fileName) ||
            fileName.StartsWith("/", StringComparison.Ordinal) ||
            fileName.StartsWith('\\') ||
            (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw InvalidParameter("Project memory file names must be relative.");
        }

        var parts = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(static part => part is "." or ".."))
        {
            throw InvalidParameter("Project memory file names must not contain '.' or '..' segments.");
        }

        normalized = string.Join('/', parts);
        if (normalized.Length == 0)
        {
            throw InvalidParameter("A project memory file name must not be empty.");
        }

        if (normalized.Contains('/'))
        {
            throw InvalidParameter(
                "Project memory files must use flat names without directory separators.");
        }

        if (IsInternalFile(normalized))
        {
            throw InvalidParameter("The project memory file name is reserved by the system.");
        }

        return normalized;
    }

    private static Matcher? CreateGlobMatcher(string? globPattern)
    {
        if (string.IsNullOrWhiteSpace(globPattern))
        {
            return null;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(globPattern);
        return matcher;
    }

    private static (string Content, int Count) ApplyReplace(
        string content,
        string oldString,
        string newString,
        bool replaceAll)
    {
        if (string.IsNullOrEmpty(oldString))
        {
            throw InvalidParameter("old_string must not be empty.");
        }

        var count = 0;
        var startIndex = 0;
        while ((startIndex = content.IndexOf(oldString, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += oldString.Length;
        }

        if (count == 0)
        {
            throw InvalidParameter($"old_string not found: '{oldString}'.");
        }

        if (count > 1 && !replaceAll)
        {
            throw InvalidParameter(
                $"old_string occurs {count} times; pass replace_all=true or provide a more specific value.");
        }

        return (content.Replace(oldString, newString, StringComparison.Ordinal), count);
    }

    private static string ApplyReplaceLines(string content, IReadOnlyList<FileLineEdit> edits)
    {
        if (edits.Count == 0)
        {
            throw InvalidParameter("At least one line edit must be provided.");
        }

        var lines = SplitLinesKeepEnds(content);
        var lineNumbers = new HashSet<int>();
        foreach (var edit in edits)
        {
            if (!lineNumbers.Add(edit.LineNumber))
            {
                throw InvalidParameter($"Duplicate line_number {edit.LineNumber} in edits.");
            }

            if (edit.LineNumber < 1 || edit.LineNumber > lines.Count)
            {
                throw InvalidParameter(
                    $"line_number {edit.LineNumber} is out of range (file has {lines.Count} lines).");
            }
        }

        foreach (var edit in edits)
        {
            lines[edit.LineNumber - 1] = edit.NewLine;
        }

        return string.Concat(lines);
    }

    private static List<string> SplitLinesKeepEnds(string content)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            switch (content[index])
            {
                case '\n':
                    lines.Add(content[start..(index + 1)]);
                    start = index + 1;
                    break;
                case '\r':
                    var end = index + 1 < content.Length && content[index + 1] == '\n'
                        ? index + 2
                        : index + 1;
                    lines.Add(content[start..end]);
                    index = end - 1;
                    start = end;
                    break;
            }
        }

        if (start < content.Length)
        {
            lines.Add(content[start..]);
        }

        return lines;
    }

    private static string GetDescriptionFileName(string fileName)
    {
        var extensionIndex = fileName.LastIndexOf('.');
        return extensionIndex > 0
            ? fileName[..extensionIndex] + DescriptionSuffix
            : fileName + DescriptionSuffix;
    }

    private static bool IsInternalFile(string fileName) =>
        fileName.EndsWith(DescriptionSuffix, StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals(MemoryIndexFileName, StringComparison.OrdinalIgnoreCase);

    private static AgwException InvalidParameter(string message) =>
        new(ErrorCodes.InvalidParam, message);
}
