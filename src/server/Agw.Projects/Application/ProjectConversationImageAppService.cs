using System.Text.Json;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Application;

public sealed class ProjectConversationImageAppService
{
    private readonly IProjectsDbContext _dbContext;
    private readonly ProjectResolver _projectResolver;
    private readonly ICodexImagePathReader _codexImagePathReader;

    public ProjectConversationImageAppService(
        IProjectsDbContext dbContext,
        ProjectResolver projectResolver,
        ICodexImagePathReader codexImagePathReader
    )
    {
        _dbContext = dbContext;
        _projectResolver = projectResolver;
        _codexImagePathReader = codexImagePathReader;
    }

    public async Task<string?> GetDataUriAsync(
        Guid projectId,
        Guid conversationId,
        string messageId,
        string path,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(messageId, out var recordId) || string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new AgwException(ErrorCodes.InvalidParam);

        var project = await _projectResolver.ResolveRequiredAsync(projectId, cancellationToken);
        if (
            project == null
            || !await _dbContext.ProjectConversations.AnyAsync(
                conversation =>
                    conversation.Id == conversationId
                    && conversation.ProjectId == project.Id
                    && conversation.CreateBy == project.CreateBy,
                cancellationToken
            )
        )
            throw new AgwException(ErrorCodes.ResourceNotFound);

        var target = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.ConversationId == conversationId && record.Id == recordId,
                cancellationToken
            );
        if (target?.TurnId == null || target.ConversationSequence == null || target.ToChatMessage() is not { } message)
            return null;

        var threadId = GetString(message.AdditionalProperties, "threadId");
        var records = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record =>
                record.ConversationId == conversationId
                && record.TurnId == target.TurnId
                && record.TaskId == target.TaskId
                && record.ConversationSequence <= target.ConversationSequence
                && record.ConversationPayload != null
            )
            .OrderByDescending(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var images = records
            .Select(record => record.ToChatMessage())
            .Where(candidate => candidate != null && GetString(candidate.AdditionalProperties, "threadId") == threadId)
            .SelectMany(candidate => candidate!.Contents.OfType<DataContent>().Reverse())
            .Where(content => content.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var providerPaths = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            var imagePath = GetString(image.AdditionalProperties, "imagePath");
            if (imagePath == null && GetString(image.AdditionalProperties, "imageSource") == "imageView")
            {
                var imageThreadId = GetString(image.AdditionalProperties, "threadId");
                var turnId = GetString(image.AdditionalProperties, "turnId");
                var itemId = GetString(image.AdditionalProperties, "itemId");
                if (imageThreadId != threadId || threadId == null || turnId == null || itemId == null)
                    continue;

                if (!providerPaths.TryGetValue(turnId, out var paths))
                {
                    var itemIds = images
                        .Where(candidate =>
                            GetString(candidate.AdditionalProperties, "imagePath") == null
                            && GetString(candidate.AdditionalProperties, "imageSource") == "imageView"
                            && GetString(candidate.AdditionalProperties, "threadId") == threadId
                            && GetString(candidate.AdditionalProperties, "turnId") == turnId
                        )
                        .Select(candidate => GetString(candidate.AdditionalProperties, "itemId"))
                        .OfType<string>()
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    paths = await _codexImagePathReader.ReadAsync(
                        target.AgentId,
                        threadId,
                        turnId,
                        itemIds,
                        cancellationToken
                    );
                    providerPaths.Add(turnId, paths);
                }
                paths.TryGetValue(itemId, out imagePath);
            }

            if (imagePath != null && PathsEqual(imagePath, path))
                return image.Uri;
        }

        return null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        );

    private static string NormalizePath(string path) =>
        (Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path).Replace('\\', '/');

    private static string? GetString(AdditionalPropertiesDictionary? properties, string name) =>
        properties?.TryGetValue(name, out var value) == true
            ? value switch
            {
                string text when !string.IsNullOrWhiteSpace(text) => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            }
            : null;
}
