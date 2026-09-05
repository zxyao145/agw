using System.IO.Compression;
using Agw.Agents.Application.Persistence;
using Agw.Agents.ExternalAgents;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts;
using Agw.Shared;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Tooling;
using Agw.Skills.Contracts.Registration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Data;

/// <summary>
/// Database seeder for initializing default data on application startup.
/// </summary>
public class DbSeeder
{
    private const string DefaultSkillName = "xhs-explore";
    private const string DefaultSkillContentPath = "skills/xhs-explore";
    private const string DefaultSkillResourceName = "Agw.Infrastructure.SeedData.xhs-explore.zip";

    private static readonly Guid DeepSeekOpenAiProviderId = Guid.Parse("11111111-1111-1111-3333-000000000001");
    private static readonly Guid DeepSeekAnthropicProviderId = Guid.Parse("11111111-1111-1111-3333-000000000002");
    private static readonly Guid DeepSeekModelId = Guid.Parse("11111111-1111-1111-4444-000000000001");
    private static readonly Guid DeepSeekOpenAiModelProviderId = Guid.Parse("11111111-1111-1111-5555-000000000001");
    private static readonly Guid DeepSeekAnthropicModelProviderId = Guid.Parse("11111111-1111-1111-5555-000000000002");

    private static readonly Guid GeneralAgentId = Guid.Parse("11111111-1111-1111-6666-000000000001");
    private static readonly Guid LocationExtractorAgentId = Guid.Parse("11111111-1111-1111-6666-000000000002");
    private static readonly Guid AmapPoiSearchAgentId = Guid.Parse("11111111-1111-1111-6666-000000000003");

    private static readonly Guid XiaohongshuAgentflowId = Guid.Parse("11111111-1111-1111-7777-000000000001");

    private static readonly Guid XhsExploreSkillId = Guid.Parse("11111111-1111-1111-8888-000000000001");

    public static IReadOnlyList<Project> BuiltInProjects { get; } =
    [
        new Project
        {
            Id = ProjectDefaults.DefaultBuiltInId,
            Name = ProjectDefaults.DefaultBuiltInName,
            Description = "Default built-in project for general task execution.",
            Type = ProjectType.DefaultBuiltIn,
        },
        new Project
        {
            Id = ProjectDefaults.A2AId,
            Name = ProjectDefaults.A2AName,
            Description = "Built-in project for A2A task execution.",
            Type = ProjectType.DefaultBuiltIn,
        },
    ];

    private readonly AgwDbContext _context;
    private readonly ILogger<DbSeeder> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly AgwDataPaths _dataPaths;
    private readonly IReadOnlyList<IAgentSkillRegistration> _skillRegistrations;

    public DbSeeder(
        AgwDbContext context,
        ILogger<DbSeeder> logger,
        TimeProvider timeProvider,
        AgwDataPaths dataPaths,
        IEnumerable<IAgentSkillRegistration>? skillRegistrations = null
    )
    {
        _context = context;
        _logger = logger;
        _timeProvider = timeProvider;
        _dataPaths = dataPaths;
        _skillRegistrations = (skillRegistrations ?? []).ToArray();
    }

    /// <summary>
    /// Seeds the database with default data.
    /// </summary>
    public async Task SeedAsync()
    {
        using var systemScope = UserInfoUtil.PushSystemScope();
        try
        {
            _logger.LogInformation("Starting database seeding");

            await SeedBuiltInProjectsAsync();
            await SeedExternalAgentsAsync();
            var providers = await SeedProvidersAsync();
            var defaultModelProvider = await SeedDefaultModelAsync(providers);
            var agents = await SeedDefaultAgentsAsync(defaultModelProvider.Id);
            await SeedBuiltInClassSkillsAsync();
            var skill = await SeedDefaultSkillAsync();
            if (skill != null)
            {
                await SeedAgentSkillRelationAsync(agents["amap-poi-search"].Id, skill.Id);
            }
            await SeedDefaultAgentflowAsync(agents);

            await _context.SaveChangesAsync();
            _logger.LogInformation("Database seeding completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during database seeding");
            throw;
        }
    }

    /// <summary>
    /// Runs a bounded data-recovery pass from the Host startup scope, separately from first-run seed insertion.
    /// The trusted database bootstrap owns the system scope; maintenance and locks remain required DI services.
    /// </summary>
    public static async Task<DurableExecutionScopeBackfillResult> RecoverDurableExecutionScopesAsync(
        IServiceProvider scopedServices,
        CancellationToken cancellationToken = default,
        DurableExecutionScopeCursor? after = null
    )
    {
        using var systemScope = UserInfoUtil.PushSystemScope();
        return await scopedServices
            .GetRequiredService<IDurableExecutionScopeMaintenance>()
            .BackfillAsync(cancellationToken, after)
            .ConfigureAwait(false);
    }

    private async Task SeedBuiltInProjectsAsync()
    {
        foreach (var definition in BuiltInProjects)
        {
            var existingProject = await _context.Projects.FirstOrDefaultAsync(project =>
                project.Id == definition.Id && project.CreateBy == Constants.AdminUserId
                || project.CreateBy == Constants.AdminUserId && project.Name == definition.Name
            );

            if (existingProject == null)
            {
                _logger.LogInformation("Seeding built-in project {ProjectName}", definition.Name);
                _context.Projects.Add(CreateBuiltInProject(definition));
                continue;
            }

            //existingProject.Name = definition.Name;
            //existingProject.Type = definition.Type;
            //existingProject.Description = definition.Description;
            //existingProject.UpdateTime = _timeProvider.GetUtcNow();
        }
    }

    private Project CreateBuiltInProject(Project definition)
    {
        var now = _timeProvider.GetUtcNow();
        var workspace = string.IsNullOrWhiteSpace(definition.Workspace)
            ? "~/.agw/" + definition.Name
            : definition.Workspace;
        return new Project
        {
            Id = definition.Id,
            Name = definition.Name,
            Type = definition.Type,
            Description = definition.Description,
            Workspace = workspace,
            ExtraSetting = definition.ExtraSetting,
            CreateBy = Constants.AdminUserId,
            CreateTime = now,
            UpdateBy = Constants.AdminUserId,
            UpdateTime = now,
        };
    }

    private async Task SeedExternalAgentsAsync()
    {
        foreach (var agent in AgentNames.ExternalAgentNames)
        {
            var agentName = agent.Name;
            var existingAgent = await _context.Agents.FirstOrDefaultAsync(a =>
                a.Name == agentName && a.Type == AgentType.External && a.CreateBy == Constants.AdminUserId
            );

            if (existingAgent != null)
            {
                _logger.LogInformation("External Agent {AgentName} already exists, skipping seed", agentName);
                continue;
            }

            _logger.LogInformation("Seeding External Agent: {agentName}", agentName);
            var agentDefinition = CreateBuiltInAgent(agent);
            _context.Agents.Add(agentDefinition);
        }
    }

    private Agent CreateBuiltInAgent(Agent definition)
    {
        var now = _timeProvider.GetUtcNow();
        return new Agent
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            Name = definition.Name,
            Type = definition.Type,
            Description = definition.Description,
            Extra = definition.Extra,

            CreateBy = Constants.AdminUserId,
            CreateTime = now,
            UpdateBy = Constants.AdminUserId,
            UpdateTime = now,
        };
    }

    private async Task<IReadOnlyDictionary<ProviderType, Provider>> SeedProvidersAsync()
    {
        var now = _timeProvider.GetUtcNow();
        var definitions = new[]
        {
            new Provider
            {
                Id = DeepSeekOpenAiProviderId,
                Name = "DeepSeek",
                ProviderType = ProviderType.OpenAIChatCompletions,
                Endpoint = "https://api.deepseek.com",
                Description = "DeepSeek OpenAI Compatible",
                CreateBy = Constants.AdminUserId,
                CreateTime = now,
                UpdateBy = Constants.AdminUserId,
                UpdateTime = now,
            },
            new Provider
            {
                Id = DeepSeekAnthropicProviderId,
                Name = "DeepSeek",
                ProviderType = ProviderType.Anthropic,
                Endpoint = "https://api.deepseek.com/anthropic",
                Description = "DeepSeek Anthropic Compatible",
                CreateBy = Constants.AdminUserId,
                CreateTime = now,
                UpdateBy = Constants.AdminUserId,
                UpdateTime = now,
            },
        };
        var providers = new Dictionary<ProviderType, Provider>();

        foreach (var definition in definitions)
        {
            var provider = await _context.Providers.FirstOrDefaultAsync(p =>
                p.Name == definition.Name
                && p.ProviderType == definition.ProviderType
                && p.CreateBy == Constants.AdminUserId
            );
            if (provider == null)
            {
                _logger.LogInformation(
                    "Seeding Provider: {ProviderName} of type {ProviderType}",
                    definition.Name,
                    definition.ProviderType
                );
                provider = definition;
                _context.Providers.Add(provider);
            }
            else
            {
                _logger.LogInformation(
                    "Provider {ProviderName} of type {ProviderType} already exists, skipping seed",
                    definition.Name,
                    definition.ProviderType
                );
            }

            providers[definition.ProviderType] = provider;
        }

        return providers;
    }

    private async Task<ModelProviderRelation> SeedDefaultModelAsync(
        IReadOnlyDictionary<ProviderType, Provider> providers
    )
    {
        var now = _timeProvider.GetUtcNow();
        var model = await _context.Models.FirstOrDefaultAsync(x =>
            x.Id == DeepSeekModelId && x.CreateBy == Constants.AdminUserId
            || x.Name == "deepseek-v4-pro" && x.CreateBy == Constants.AdminUserId
        );
        if (model == null)
        {
            _logger.LogInformation("Seeding LLM Model: deepseek-v4-pro");
            model = new AgwAiModel
            {
                Id = DeepSeekModelId,
                Name = "deepseek-v4-pro",
                MaxContextWindowTokens = AgwAiModel.DefaultMaxContextWindowTokens,
                MaxOutputTokens = AgwAiModel.DefaultMaxOutputTokens,
                CreateBy = Constants.AdminUserId,
                CreateTime = now,
                UpdateBy = Constants.AdminUserId,
                UpdateTime = now,
            };
            _context.Models.Add(model);
        }

        var openAiRelation = await SeedModelProviderRelationAsync(
            DeepSeekOpenAiModelProviderId,
            model.Id,
            providers[ProviderType.OpenAIChatCompletions].Id,
            now
        );
        await SeedModelProviderRelationAsync(
            DeepSeekAnthropicModelProviderId,
            model.Id,
            providers[ProviderType.Anthropic].Id,
            now
        );

        return openAiRelation;
    }

    private async Task<ModelProviderRelation> SeedModelProviderRelationAsync(
        Guid relationId,
        Guid modelId,
        Guid providerId,
        DateTimeOffset now
    )
    {
        var relation = await _context.ModelProviders.FirstOrDefaultAsync(x =>
            x.Id == relationId && x.CreateBy == Constants.AdminUserId
            || x.ModelId == modelId && x.ProviderId == providerId && x.CreateBy == Constants.AdminUserId
        );
        if (relation != null)
        {
            return relation;
        }

        relation = new ModelProviderRelation
        {
            Id = relationId,
            ModelId = modelId,
            ProviderId = providerId,
            RpsLimit = 60,
            CreateBy = Constants.AdminUserId,
            CreateTime = now,
            UpdateBy = Constants.AdminUserId,
            UpdateTime = now,
        };
        _context.ModelProviders.Add(relation);
        return relation;
    }

    private async Task<IReadOnlyDictionary<string, Agent>> SeedDefaultAgentsAsync(Guid modelProviderId)
    {
        var now = _timeProvider.GetUtcNow();
        var definitions = CreateDefaultAgentDefinitions(modelProviderId, now);
        var agents = new Dictionary<string, Agent>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            var agent = await _context.Agents.FirstOrDefaultAsync(x =>
                x.Id == definition.Id && x.CreateBy == Constants.AdminUserId
                || x.Name == definition.Name && x.CreateBy == Constants.AdminUserId
            );
            if (agent == null)
            {
                _logger.LogInformation("Seeding Agent: {AgentName}", definition.Name);
                agent = definition;
                _context.Agents.Add(agent);
            }
            else
            {
                BackfillDefaultAgentTools(agent, definition, now);
            }

            agents[definition.Name] = agent;
        }

        return agents;
    }

    private static IReadOnlyList<Agent> CreateDefaultAgentDefinitions(Guid modelProviderId, DateTimeOffset now)
    {
        return
        [
            new Agent
            {
                Id = GeneralAgentId,
                DisplayName = "General Agent",
                Name = "general-agent",
                Description = "General Agent",
                SystemPrompt = "You are a helpful assistant.",
                ModelProviderId = modelProviderId,
                EnableSummary = true,
                Type = AgentType.System,
                Tools =
                [
                    new ToolValue { Definition = new DiffToolDefinition() },
                    new ToolValue { Definition = new GitCloneToolDefinition() },
                    new ToolValue { Definition = new RunShellToolDefinition() },
                    new ToolBlockValue { Definition = new FileAccessToolBlockDefinition() },
                ],
                CreateBy = Constants.AdminUserId,
                CreateTime = now,
                UpdateBy = Constants.AdminUserId,
                UpdateTime = now,
            },
            new Agent
            {
                Id = LocationExtractorAgentId,
                DisplayName = "地址提取器",
                Name = "location-extractor",
                Description = "中国地理地址 POI 解析助手",
                SystemPrompt = """
                    你是一个中国地理地址 POI 解析助手。

                    任务：
                    从用户输入的任意中文文本中，识别用户真正想去、想描述、想推荐、想搜索的目的地，并提取对应地址信息。

                    规则：

                    1. 识别目的地
                    - 提取用户重点表达的地点。
                    - 忽略仅用于路线描述、位置说明、参考定位的地点。
                    - 忽略起点、途经点、附近地点。
                    - 如果文本中包含多个并列目的地，则全部返回。

                    2. 城市识别
                    - 你认识中国所有省、自治区、直辖市、地级市、县级市名称。
                    - city 字段统一返回所属城市名称。
                    - city 必须以“市”结尾。
                    - 示例：
                        - 北京 → 北京市
                        - 上海 → 上海市
                        - 武汉 → 武汉市
                        - 大冶 → 黄石市
                        - 阳朔 → 桂林市

                    3. 地址提取
                    - address 字段仅保留用户真正关注的地点名称。
                    - 删除省、市、区、县等行政区划名称。
                    - 删除路线描述、方向描述。
                    - 删除“附近”、“周边”、“旁边”等修饰词。

                    示例：
                    输入：
                    湖北省黄石市大冶市大泉沟

                    输出：
                      {
                          "locations": [
                              {
                                  "address": "大泉沟",
                                  "city": "黄石市"
                              }
                          ]
                      }

                    4. POI过滤
                    以下类型名称不要作为 address：
                    - 餐厅
                    - 饭店
                    - 酒店
                    - 民宿
                    - 客栈
                    - 咖啡馆
                    - 奶茶店
                    - 商场
                    - 超市
                    - KTV
                    - 酒吧
                    - 医院
                    - 学校

                    如果文本仅包含上述 POI，则返回空数组。

                    5. 景区保留
                    以下地点应保留：
                      - 景区
                      - 公园
                      - 山峰
                      - 湖泊
                      - 河流
                      - 古镇
                      - 村落
                      - 露营地
                      - 徒步路线
                      - 自然地貌
                      - 地标建筑
                      - 网红打卡地
                      - 小众景点

                    6.优先保留自然景观、人文景观、古村落、徒步路线、观景点等旅游目的地。

                    对于：
                      - 酒店
                      - 餐厅
                      - 咖啡馆
                      - 民宿
                      - 购物场所

                    即使名称明确出现，也不要作为最终目的地返回。

                    7. 输出格式
                    - 必须返回合法 JSON。
                    - 禁止输出 markdown。
                    - 禁止输出解释文字。
                    - 禁止输出额外字段。

                    输出格式：

                      {
                          "locations": [
                              {
                                  "address": "地址名称",
                                  "city": "城市名称"
                              }
                          ]
                      }
                    """,
                ModelProviderId = modelProviderId,
                Type = AgentType.System,
                Tools =
                [
                    new ToolValue { Definition = new WebFetchToolDefinition() },
                    new ToolValue { Definition = new WebSearchToolDefinition() },
                    new ToolBlockValue { Definition = new TodoToolBlockDefinition() },
                ],
                CreateBy = Constants.AdminUserId,
                CreateTime = now,
                UpdateBy = Constants.AdminUserId,
                UpdateTime = now,
            },
            new Agent
            {
                Id = AmapPoiSearchAgentId,
                DisplayName = "高德地图关键词搜索",
                Name = "amap-poi-search",
                SystemPrompt = """
                    根据用户输入的城市名 city 和关键字 address，使用高德地图关键词搜索能力，搜索相关的 poi。

                    如果搜索成功，则返回其中的第一个 poi 数据的信息包括：
                    - Longitude：经度
                    - Latitude：纬度
                    - Name：POI 名称
                    - FormattedAddress：POI 的 Address，如果不存在则使用 POI 的 Name

                    输出格式为 JSON：
                        {
                            "Latitude":  39.92,
                            "Longitude": 116.40,
                            "Name": "故宫",
                            "FormattedAddress": "北京故宫博物馆"
                        }

                    如果存在多个地址，返回多个地址对应的经纬度和地址信息，不需要做任何多余的处理，也不需要做行程规划。
                    """,
                ModelProviderId = modelProviderId,
                Type = AgentType.System,
                CreateBy = Constants.AdminUserId,
                CreateTime = now,
                UpdateBy = Constants.AdminUserId,
                UpdateTime = now,
            },
        ];
    }

    private static void BackfillDefaultAgentTools(Agent agent, Agent definition, DateTimeOffset now)
    {
        if (agent.Id != definition.Id)
        {
            return;
        }

        IReadOnlyList<IReadOnlyList<ToolValueObject>> obsoleteToolSignatures = agent.Id switch
        {
            var id when id == GeneralAgentId =>
            [
                [
                    new ToolValue { Definition = new DiffToolDefinition() },
                    new ToolValue { Definition = new GitCloneToolDefinition() },
                    new ToolValue { Definition = new BashToolDefinition() },
                ],
                [
                    new ToolValue { Definition = new DiffToolDefinition() },
                    new ToolValue { Definition = new GitCloneToolDefinition() },
                    new ToolValue { Definition = new BashToolDefinition() },
                    new ToolBlockValue { Definition = new FileAccessToolBlockDefinition() },
                ],
            ],
            var id when id == LocationExtractorAgentId =>
            [
                [new ToolValue { Definition = new WebFetchToolDefinition() }],
            ],
            _ => [],
        };
        if (!obsoleteToolSignatures.Any(agent.Tools.SequenceEqual))
        {
            return;
        }

        agent.Tools = definition.Tools;
        agent.UpdateBy = Constants.AdminUserId;
        agent.UpdateTime = now;
    }

    private async Task<Skill?> SeedDefaultSkillAsync()
    {
        var skill = await _context.Skills.FirstOrDefaultAsync(x =>
            x.CreateBy == Constants.AdminUserId && (x.Id == XhsExploreSkillId || x.Name == DefaultSkillName)
        );
        if (skill == null)
        {
            if (await _context.Skills.AnyAsync(x => x.Id == XhsExploreSkillId))
            {
                _logger.LogWarning(
                    "Default skill id {SkillId} is already owned by another user; skipping the administrator seed",
                    XhsExploreSkillId
                );
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            skill = new Skill
            {
                Id = XhsExploreSkillId,
                Name = DefaultSkillName,
                Description = "小红书技能集合",
                Kind = SkillKind.Local,
                ContentPath = DefaultSkillContentPath,
                RemoteUrl = null,
                CreateBy = Constants.AdminUserId,
                CreateTime = now,
                UpdateBy = Constants.AdminUserId,
                UpdateTime = now,
            };
            _context.Skills.Add(skill);
        }
        else if (
            skill.Id == XhsExploreSkillId
            && (
                skill.Name != DefaultSkillName
                || skill.Kind != SkillKind.Local
                || skill.ContentPath != DefaultSkillContentPath
                || skill.RemoteUrl != null
            )
        )
        {
            skill.Name = DefaultSkillName;
            skill.Kind = SkillKind.Local;
            skill.ContentPath = DefaultSkillContentPath;
            skill.RemoteUrl = null;
            skill.UpdateBy = Constants.AdminUserId;
            skill.UpdateTime = _timeProvider.GetUtcNow();
        }

        EnsureDefaultSkillContent();
        return skill;
    }

    private async Task SeedBuiltInClassSkillsAsync()
    {
        foreach (var registration in _skillRegistrations)
        {
            var existingById = await _context.Skills.FirstOrDefaultAsync(skill =>
                skill.Id == registration.Id && skill.CreateBy == Constants.AdminUserId
            );
            if (existingById == null)
            {
                var nameConflict = await _context.Skills.FirstOrDefaultAsync(skill =>
                    skill.Name == registration.Name && skill.CreateBy == Constants.AdminUserId
                );
                if (nameConflict != null)
                {
                    _logger.LogWarning(
                        "Built-in class skill {SkillName} was not seeded because the name is already used by skill {SkillId}",
                        registration.Name,
                        nameConflict.Id
                    );
                    continue;
                }

                var now = _timeProvider.GetUtcNow();
                _context.Skills.Add(
                    new Skill
                    {
                        Id = registration.Id,
                        Name = registration.Name,
                        Description = registration.Description,
                        Kind = SkillKind.BuiltIn,
                        ContentPath = string.Empty,
                        RemoteUrl = null,
                        CreateBy = Constants.AdminUserId,
                        CreateTime = now,
                        UpdateBy = Constants.AdminUserId,
                        UpdateTime = now,
                    }
                );
                continue;
            }

            if (
                existingById.Name == registration.Name
                && existingById.Description == registration.Description
                && existingById.Kind == SkillKind.BuiltIn
                && existingById.RemoteUrl == null
                && string.IsNullOrEmpty(existingById.ContentPath)
            )
            {
                continue;
            }

            var conflictingName = await _context.Skills.AnyAsync(skill =>
                skill.Id != registration.Id
                && skill.Name == registration.Name
                && skill.CreateBy == Constants.AdminUserId
            );
            if (conflictingName)
            {
                _logger.LogWarning(
                    "Built-in class skill {SkillId} metadata was not updated because name {SkillName} is already in use",
                    registration.Id,
                    registration.Name
                );
                continue;
            }

            existingById.Name = registration.Name;
            existingById.Description = registration.Description;
            existingById.Kind = SkillKind.BuiltIn;
            existingById.ContentPath = string.Empty;
            existingById.RemoteUrl = null;
            existingById.UpdateBy = Constants.AdminUserId;
            existingById.UpdateTime = _timeProvider.GetUtcNow();
        }
    }

    private async Task SeedAgentSkillRelationAsync(Guid agentId, Guid skillId)
    {
        if (await _context.AgentSkillRelations.AnyAsync(x => x.AgentId == agentId && x.SkillId == skillId))
        {
            return;
        }

        _context.AgentSkillRelations.Add(new AgentSkillRelation { AgentId = agentId, SkillId = skillId });
    }

    private async Task SeedDefaultAgentflowAsync(IReadOnlyDictionary<string, Agent> agents)
    {
        var existingAgentflow = await _context.Agentflows.FirstOrDefaultAsync(x =>
            x.Id == XiaohongshuAgentflowId && x.CreateBy == Constants.AdminUserId
            || x.Name == "Xiaohongshu Address Extraction" && x.CreateBy == Constants.AdminUserId
        );
        if (existingAgentflow != null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var agentflow = new Agentflow
        {
            Id = XiaohongshuAgentflowId,
            Name = "Xiaohongshu Address Extraction",
            Description = """
                Follow the steps provided by the user for the Xiaohongshu Post link:

                1. Call Xiaohongshu parsing skill to obtain the article title, body, and related content.
                2. Identify all the scenic spot names involved from the content of the article.
                3. For each attraction, call the Gaode Map MCP to search for nearby POIs.
                4. Take the first POI in the search results as the final result.
                5. Output the name, address, latitude and longitude (if any) of the POI, as well as its association with the attraction.

                根据用户提供的小红书 Post 链接执行以下步骤：

                1. 调用小红书解析 Skill，获取文章标题、正文及相关内容。
                2. 从文章内容中识别所有涉及的景点名称。
                3. 对每个景点，调用高德地图 MCP 搜索附近的 POI。
                4. 取搜索结果中的第一个 POI 作为最终结果。
                5. 输出 POI 的名称、地址、经纬度（如有）以及与景点的关联。
                """,
            CreateBy = Constants.AdminUserId,
            CreateTime = now,
            UpdateBy = Constants.AdminUserId,
            UpdateTime = now,
            Nodes =
            [
                new AgentflowNode
                {
                    NodeId = "input",
                    Kind = AgentflowNodeKind.Input,
                    Name = "Input",
                    PositionJson = "{\"x\":12,\"y\":10.9481361426256}",
                    CreateBy = Constants.AdminUserId,
                    CreateTime = now,
                    UpdateBy = Constants.AdminUserId,
                    UpdateTime = now,
                },
                new AgentflowNode
                {
                    NodeId = "0-1784023077088-4wxgi0",
                    Kind = AgentflowNodeKind.Agent,
                    RelateId = agents["general-agent"].Id,
                    Name = "general-agent",
                    PositionJson = "{\"x\":332,\"y\":12}",
                    Instructions = """
                        从文字中识别出相关的 http 或 https 连接地址。

                        然后从通过识别出来的 http 或 https 的地址，获取小红书的内容详情。

                        只需要返回正文，不要返回其他的任何信息。
                        """,
                    CreateBy = Constants.AdminUserId,
                    CreateTime = now,
                    UpdateBy = Constants.AdminUserId,
                    UpdateTime = now,
                },
                new AgentflowNode
                {
                    NodeId = "0-1784023208474-g5q65g",
                    Kind = AgentflowNodeKind.Agent,
                    RelateId = agents["location-extractor"].Id,
                    Name = "location-extractor",
                    PositionJson = "{\"x\":652,\"y\":12}",
                    CreateBy = Constants.AdminUserId,
                    CreateTime = now,
                    UpdateBy = Constants.AdminUserId,
                    UpdateTime = now,
                },
                new AgentflowNode
                {
                    NodeId = "0-1784030849721-zid7yj",
                    Kind = AgentflowNodeKind.Agent,
                    RelateId = agents["amap-poi-search"].Id,
                    Name = "amap-poi-search",
                    PositionJson = "{\"x\":972,\"y\":12}",
                    CreateBy = Constants.AdminUserId,
                    CreateTime = now,
                    UpdateBy = Constants.AdminUserId,
                    UpdateTime = now,
                },
            ],
            Edges =
            [
                new AgentflowEdge
                {
                    EdgeId = "edge-input-0-1784023077088-4wxgi0-1784030770450",
                    SourceNodeId = "input",
                    TargetNodeId = "0-1784023077088-4wxgi0",
                    Kind = AgentflowEdgeKind.FanOut,
                    CreateBy = Constants.AdminUserId,
                    CreateTime = now,
                    UpdateBy = Constants.AdminUserId,
                    UpdateTime = now,
                },
                new AgentflowEdge
                {
                    EdgeId = "edge-0-1784023077088-4wxgi0-0-1784023208474-g5q65g-1784030821565",
                    SourceNodeId = "0-1784023077088-4wxgi0",
                    TargetNodeId = "0-1784023208474-g5q65g",
                    Kind = AgentflowEdgeKind.Direct,
                    CreateBy = Constants.AdminUserId,
                    CreateTime = now,
                    UpdateBy = Constants.AdminUserId,
                    UpdateTime = now,
                },
                new AgentflowEdge
                {
                    EdgeId = "edge-0-1784023208474-g5q65g-0-1784030849721-zid7yj-1784030866867",
                    SourceNodeId = "0-1784023208474-g5q65g",
                    TargetNodeId = "0-1784030849721-zid7yj",
                    Kind = AgentflowEdgeKind.Direct,
                    CreateBy = Constants.AdminUserId,
                    CreateTime = now,
                    UpdateBy = Constants.AdminUserId,
                    UpdateTime = now,
                },
            ],
        };

        _context.Agentflows.Add(agentflow);
    }

    private void EnsureDefaultSkillContent()
    {
        var targetDirectory = Path.Combine(_dataPaths.SkillsDirectory, DefaultSkillName);
        if (!Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(_dataPaths.TempDirectory);
            var extractionRoot = Path.Combine(_dataPaths.TempDirectory, $"agw-default-skill-{Guid.CreateVersion7():N}");
            try
            {
                using var archiveStream =
                    typeof(DbSeeder).Assembly.GetManifestResourceStream(DefaultSkillResourceName)
                    ?? throw new AgwException(
                        ErrorCodes.CannotCreateInstance,
                        $"Embedded skill resource '{DefaultSkillResourceName}' was not found."
                    );
                ZipFile.ExtractToDirectory(archiveStream, extractionRoot);

                var extractedDirectory = Path.Combine(extractionRoot, DefaultSkillName);
                if (!Directory.Exists(extractedDirectory))
                {
                    throw new AgwException(
                        ErrorCodes.CannotCreateInstance,
                        $"Embedded skill resource does not contain '{DefaultSkillName}'."
                    );
                }

                Directory.CreateDirectory(_dataPaths.SkillsDirectory);
                Directory.Move(extractedDirectory, targetDirectory);
            }
            finally
            {
                if (Directory.Exists(extractionRoot))
                {
                    Directory.Delete(extractionRoot, recursive: true);
                }
            }
        }

        RewriteDefaultSkillName(Path.Combine(targetDirectory, "SKILL.md"));
    }

    private static void RewriteDefaultSkillName(string skillMarkdownPath)
    {
        var lines = File.ReadAllLines(skillMarkdownPath);
        var nameLineIndex = Array.FindIndex(lines, line => line.StartsWith("name:", StringComparison.Ordinal));
        if (nameLineIndex < 0)
        {
            throw new AgwException(
                ErrorCodes.CannotCreateInstance,
                "Embedded skill SKILL.md does not contain a name field."
            );
        }

        if (lines[nameLineIndex] == $"name: {DefaultSkillName}")
        {
            return;
        }

        lines[nameLineIndex] = $"name: {DefaultSkillName}";
        File.WriteAllLines(skillMarkdownPath, lines);
    }
}
