using Agw.Agents.Application.Agents;
using Agw.Domain.Services;
using Agw.Shared.Contracts.Storage;
using Agw.Shared.Contracts.Tasks;

using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService : RuntimeServiceBase, IAgentRuntimeService
{
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly AgentAppService _agentAppService;
    private readonly IProjectAppService _projectAppService;
    private readonly ToolRegistryService _toolRegistry;
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly IProviderSessionState _providerSessionState;
    private readonly ITaskSessionBindingService _taskSessionBindingService;
    private readonly IWebHostEnvironment _webHostEnvironment;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly AgentSessionStateStore _sessionStateStore;
    private readonly LoggingMiddleware _loggingMiddleware;

    public AgentRuntimeService(
        AgentAppService agentAppService,
        IProjectAppService projectAppService,
        ToolRegistryService toolRegistry,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        ITaskSessionBindingService taskSessionBindingService,
        IWebHostEnvironment webHostEnvironment,
        IAgwFileSystemResolver fileSystemResolver,
        AgentSessionStateStore sessionStateStore,
        ILogger<AgentRuntimeService> logger,
        LoggingMiddleware loggingMiddleware)
    {
        _agentAppService = agentAppService;
        _projectAppService = projectAppService;
        _toolRegistry = toolRegistry;
        _chatHistoryProvider = chatHistoryProvider;
        _providerSessionState = providerSessionState;
        _taskSessionBindingService = taskSessionBindingService;
        _webHostEnvironment = webHostEnvironment;
        _fileSystemResolver = fileSystemResolver;
        _sessionStateStore = sessionStateStore;
        _logger = logger;
        _loggingMiddleware = loggingMiddleware;
    }
}
