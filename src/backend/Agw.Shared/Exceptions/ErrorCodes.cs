using System.Net;

namespace Agw.Shared.Exceptions;

public static class ErrorCodes
{
    public static readonly ErrorCode InvalidParam = new(400_0001, "Invalid params.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode TaskIdMismatch = new(400_0002, "Task id mismatch.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode A2ATaskIdMustBeGuid = new(400_0003, "A2A task id must be a GUID string.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode RootPathRequired = new(400_0004, "Root path is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode GitAddressRequired = new(400_0005, "gitAddress is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode UsernameRequired = new(400_0006, "username is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode TokenRequired = new(400_0007, "token is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode HttpsGitAddressRequired = new(400_0008, "Only HTTPS git addresses are supported.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode FilePathRequired = new(400_0009, "File path is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode DirectoryRequired = new(400_0010, "Directory is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode CannotDivideByZero = new(400_0011, "Cannot divide by zero.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillNameRequired = new(400_0012, "Skill name is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillNameTooLong = new(400_0013, "Skill name is too long.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillNameInvalidFormat = new(400_0014, "Skill name format is invalid.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillDescriptionRequired = new(400_0015, "Skill description is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillDescriptionTooLong = new(400_0016, "Skill description is too long.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillArchiveCannotBeEmpty = new(400_0017, "Skill archive cannot be empty.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillArchiveMustBeZip = new(400_0018, "Skill archive must be a .zip file.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillArchiveMissingSkillMarkdown = new(400_0019, "Skill archive must contain SKILL.md.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillArchiveContainsInvalidPaths = new(400_0020, "Skill archive contains invalid paths.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillMarkdownMissingFrontmatter = new(400_0021, "SKILL.md must start with YAML frontmatter.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SkillMarkdownIncompleteFrontmatter = new(400_0022, "SKILL.md frontmatter is incomplete.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode InvalidSkillDirectoryPath = new(400_0023, "Invalid skill directory path.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode MethodMustBeStatic = new(400_0024, "Method must be static.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode InvalidPageSize = new(400_0025, "Invalid page size.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode InvalidHistoryLength = new(400_0026, "Invalid history length.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode MessagePartsCannotBeEmpty = new(400_0027, "Message parts cannot be empty.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode InvalidDataUriFormat = new(400_0028, "Invalid data URI format.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode MediaTypeRequired = new(400_0029, "Media type is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode InvalidMediaType = new(400_0030, "Media type is invalid.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode UriMustBeDataUri = new(400_0031, "URI must be a data URI.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode InvalidOnceTriggerValue = new(400_0032, "Invalid once trigger value.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode InvalidIntervalTriggerValue = new(400_0033, "Invalid interval trigger value.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode MagenticRequiresAtLeastTwoAgents = new(400_0034, "Magentic pattern requires at least two agents.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode SystemAgentRequiresModelProvider = new(400_0035, "System agents must have a model provider.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode JobAgentTargetRequired = new(400_0036, "Job agent target is required.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode McpStdioCommandRequired = new(400_0037, "MCP stdio transport requires a command.", HttpStatusCode.BadRequest);
    public static readonly ErrorCode McpHttpUrlRequired = new(400_0038, "MCP HTTP/SSE transport requires a URL.", HttpStatusCode.BadRequest);

    public static readonly ErrorCode GitHubOAuthTokenNotFound = new(401_0001, "GitHub OAuth token was not found.", HttpStatusCode.Unauthorized);

    public static readonly ErrorCode FileNotFound = new(404_0001, "File was not found.", HttpStatusCode.NotFound);
    public static readonly ErrorCode DirectoryNotFound = new(404_0002, "Directory was not found.", HttpStatusCode.NotFound);
    public static readonly ErrorCode JobNotFound = new(404_0003, "Job was not found.", HttpStatusCode.NotFound);
    public static readonly ErrorCode A2ATaskNotFound = new(404_0004, "A2A task was not found.", HttpStatusCode.NotFound);
    public static readonly ErrorCode A2AExtendedAgentCardNotConfigured = new(404_0005, "Extended agent card is not configured.", HttpStatusCode.NotFound);
    public static readonly ErrorCode AgentNotFound = new(404_0006, "Agent was not found.", HttpStatusCode.NotFound);

    public static readonly ErrorCode SkillAlreadyExists = new(409_0001, "Skill already exists.", HttpStatusCode.Conflict);
    public static readonly ErrorCode A2ATaskIdAlreadyUsed = new(409_0002, "Task id is already used by a non-A2A task.", HttpStatusCode.Conflict);
    public static readonly ErrorCode SkillNameUpdateRequiresArchive = new(409_0003, "Updating skill name requires uploading a new archive.", HttpStatusCode.Conflict);
    public static readonly ErrorCode A2ATaskNotCancelable = new(409_0004, "Task is not cancelable.", HttpStatusCode.Conflict);
    public static readonly ErrorCode A2ATerminalTaskCannotAcceptMessages = new(409_0005, "Task is in a terminal state and cannot accept messages.", HttpStatusCode.Conflict);
    public static readonly ErrorCode A2ATerminalTaskCannotBeSubscribed = new(409_0006, "Task is in a terminal state and cannot be subscribed to.", HttpStatusCode.Conflict);

    public static readonly ErrorCode CannotCreateInstance = new(500_0001, "Cannot create instance.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode A2ATaskSnapshotCloneFailed = new(500_0002, "Failed to clone A2A task snapshot.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode LoggerFactoryNotSet = new(500_0003, "LoggerFactory is not set.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode ServiceProviderNotSet = new(500_0004, "ServiceProvider is not set.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode AiAgentCreationFailed = new(500_0005, "AI agent could not be created for execution.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode AgentReturnedNoResult = new(500_0006, "Agent returned no result.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode UnableToCreateAgentSession = new(500_0007, "Unable to create agent session.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode A2ANoSubscriberSet = new(500_0008, "A2A subscriber set was not found.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode A2AInvalidAgentResponse = new(500_0009, "Invalid agent response.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode ProjectTaskCreationFailed = new(500_0010, "Failed to create project task.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode AgentExecutionFailed = new(500_0011, "Agent execution failed.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode ProjectTaskMarkSucceededFailed = new(500_0012, "Failed to mark project task as succeeded.", HttpStatusCode.InternalServerError);
    public static readonly ErrorCode EnvironmentVariableNotSet = new(500_0013, "Required environment variable is not set.", HttpStatusCode.InternalServerError);

    public static readonly ErrorCode UnsupportedTransportType = new(501_0001, "Transport type is not supported.", HttpStatusCode.NotImplemented);
    public static readonly ErrorCode UnsupportedAgentType = new(501_0002, "Agent type is not supported.", HttpStatusCode.NotImplemented);
    public static readonly ErrorCode UnsupportedTriggerType = new(501_0003, "Trigger type is not supported.", HttpStatusCode.NotImplemented);
    public static readonly ErrorCode UnsupportedProviderType = new(501_0004, "Provider type is not supported.", HttpStatusCode.NotImplemented);
    public static readonly ErrorCode MagenticNotSupported = new(501_0005, "Magentic is not supported.", HttpStatusCode.NotImplemented);
    public static readonly ErrorCode A2APushNotificationNotSupported = new(501_0006, "Push notifications are not supported.", HttpStatusCode.NotImplemented);
    public static readonly ErrorCode A2AUnsupportedOperation = new(501_0007, "A2A operation is not supported.", HttpStatusCode.NotImplemented);

    public static readonly ErrorCode GitHubBadResponseStatusCode = new(502_0001, "GitHub returned a bad response status code.", HttpStatusCode.BadGateway);
}
