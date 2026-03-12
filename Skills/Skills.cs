// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Threading.Tasks;
// using Microsoft.Extensions.DependencyInjection;
// using Microsoft.Extensions.Logging;

// // 1. Define the core interfaces for a pluggable skill system.
// // This mirrors the OpenClaw approach where each skill provides a specific capability.
// // (Inspired by /openclaw/openclaw/extensions and /openclaw/openclaw/skills)

// /// <summary>
// /// Represents a generic skill that an AI agent can possess.
// /// </summary>
// public interface ISkill
// {
//     string Id { get; }
//     string Name { get; }
//     string Description { get; }
//     Task<SkillExecutionResult> Execute(SkillExecutionContext context, Dictionary<string, object> parameters);
//     bool CanExecute(string actionName);
// }

// /// <summary>
// /// Represents the result of a skill execution.
// /// </summary>
// public class SkillExecutionResult
// {
//     public bool Success { get; set; }
//     public string Message { get; set; }
//     public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
// }

// /// <summary>
// /// Context provided to a skill during execution, similar to `OpenClawPluginApi` or `toolContext`.
// /// </summary>
// public class SkillExecutionContext
// {
//     public ILogger Logger { get; }
//     public IAgentService AgentService { get; }
//     // Add other common services or configurations needed by skills
//     // For example, a configuration reader or a way to interact with other channels.

//     public SkillExecutionContext(ILogger logger, IAgentService agentService)
//     {
//         Logger = logger;
//         AgentService = agentService;
//     }
// }

// /// <summary>
// /// Manages agents and their available skills.
// /// </summary>
// public interface IAgentService
// {
//     Task SendMessageToUser(string userId, string message);
//     // Potentially other agent-related operations like getting agent config, etc.
// }

// // 2. Implement concrete skills.
// // Each skill would be a separate class implementing ISkill.
// // (Analogous to individual skill directories like /openclaw/openclaw/skills/1password)

// public class DiffSkill : ISkill
// {
//     public string Id => "diffs";
//     public string Name => "Diff Viewer";
//     public string Description => "Generates and renders text differences.";

//     public bool CanExecute(string actionName) => actionName == "renderDiff";

//     public async Task<SkillExecutionResult> Execute(SkillExecutionContext context, Dictionary<string, object> parameters)
//     {
//         context.Logger.LogInformation($"Executing DiffSkill.renderDiff with parameters: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}");

//         if (!parameters.TryGetValue("before", out var beforeObj) || !parameters.TryGetValue("after", out var afterObj))
//         {
//             return new SkillExecutionResult { Success = false, Message = "Missing 'before' or 'after' parameters." };
//         }

//         string before = beforeObj.ToString();
//         string after = afterObj.ToString();

//         // Simulate diff generation logic
//         string diffOutput = $"--- Before ---\n{before}\n--- After ---\n{after}\n";
//         string viewerUrl = $"http://localhost:8080/diff-viewer/{Guid.NewGuid()}"; // Simulated URL

//         await context.AgentService.SendMessageToUser("current_user_id", $"Diff generated! View it at: {viewerUrl}");

//         return new SkillExecutionResult
//         {
//             Success = true,
//             Message = "Diff rendered successfully.",
//             Details = new Dictionary<string, object>
//             {
//                 { "diffOutput", diffOutput },
//                 { "viewerUrl", viewerUrl }
//             }
//         };
//     }
// }

// public class FeishuDocSkill : ISkill
// {
//     public string Id => "feishu_doc";
//     public string Name => "Feishu Document Operations";
//     public string Description => "Allows agents to interact with Feishu documents.";

//     public bool CanExecute(string actionName) =>
//         actionName == "read" || actionName == "write" || actionName == "create";

//     public async Task<SkillExecutionResult> Execute(SkillExecutionContext context, Dictionary<string, object> parameters)
//     {
//         if (!parameters.TryGetValue("action", out var actionObj) || !(actionObj is string action))
//         {
//             return new SkillExecutionResult { Success = false, Message = "Missing 'action' parameter for FeishuDocSkill." };
//         }

//         context.Logger.LogInformation($"Executing FeishuDocSkill action: {action}");

//         switch (action)
//         {
//             case "read":
//                 if (!parameters.TryGetValue("doc_token", out var docTokenObj) || !(docTokenObj is string docToken))
//                 {
//                     return new SkillExecutionResult { Success = false, Message = "Missing 'doc_token' for read action." };
//                 }
//                 // Simulate reading from Feishu
//                 string content = $"Content of document {docToken}";
//                 return new SkillExecutionResult { Success = true, Message = "Document read.", Details = new() { { "content", content } } };
//             case "write":
//                 if (!parameters.TryGetValue("doc_token", out docTokenObj) || !(docTokenObj is string writeDocToken) ||
//                     !parameters.TryGetValue("content", out var contentObj) || !(contentObj is string writeContent))
//                 {
//                     return new SkillExecutionResult { Success = false, Message = "Missing 'doc_token' or 'content' for write action." };
//                 }
//                 // Simulate writing to Feishu
//                 return new SkillExecutionResult { Success = true, Message = $"Content written to document {writeDocToken}." };
//             case "create":
//                 if (!parameters.TryGetValue("title", out var titleObj) || !(titleObj is string title))
//                 {
//                     return new SkillExecutionResult { Success = false, Message = "Missing 'title' for create action." };
//                 }
//                 // Simulate creating a document
//                 string newDocToken = Guid.NewGuid().ToString();
//                 return new SkillExecutionResult { Success = true, Message = $"Document '{title}' created with token {newDocToken}.", Details = new() { { "doc_token", newDocToken } } };
//             default:
//                 return new SkillExecutionResult { Success = false, Message = $"Unsupported action for FeishuDocSkill: {action}." };
//         }
//     }
// }

// // 3. Create a SkillManager responsible for registering and retrieving skills.
// // This acts as the central hub for discovering and dispatching to skills.
// public class SkillManager
// {
//     private readonly IServiceProvider _serviceProvider;
//     private readonly ILogger<SkillManager> _logger;
//     private readonly Dictionary<string, ISkill> _skills = new Dictionary<string, ISkill>();

//     public SkillManager(IServiceProvider serviceProvider, ILogger<SkillManager> logger)
//     {
//         _serviceProvider = serviceProvider;
//         _logger = logger;
//     }

//     public void RegisterSkill(ISkill skill)
//     {
//         if (_skills.ContainsKey(skill.Id))
//         {
//             _logger.LogWarning($"Skill with ID '{skill.Id}' already registered. Overwriting.");
//         }
//         _skills[skill.Id] = skill;
//         _logger.LogInformation($"Registered skill: {skill.Name} ({skill.Id})");
//     }

//     public ISkill GetSkill(string skillId)
//     {
//         if (_skills.TryGetValue(skillId, out var skill))
//         {
//             return skill;
//         }
//         throw new ArgumentException($"Skill with ID '{skillId}' not found.");
//     }

//     public IEnumerable<ISkill> GetAvailableSkills(IEnumerable<string> allowedSkillIds = null)
//     {
//         if (allowedSkillIds == null)
//         {
//             return _skills.Values;
//         }
//         return _skills.Values.Where(s => allowedSkillIds.Contains(s.Id));
//     }
// }

// // 4. Set up Dependency Injection (DI) to manage skill instances and their dependencies.
// // This is crucial for a pluggable system, allowing skills to be discovered and loaded.
// // (Inspired by how OpenClaw uses `api.registerTool` and `api.registerService`)

// public class AgentService : IAgentService
// {
//     private readonly ILogger<AgentService> _logger;

//     public AgentService(ILogger<AgentService> logger)
//     {
//         _logger = logger;
//     }

//     public Task SendMessageToUser(string userId, string message)
//     {
//         _logger.LogInformation($"Agent sending message to {userId}: {message}");
//         // In a real system, this would interact with a communication channel
//         return Task.CompletedTask;
//     }
// }

// public class Program
// {
//     public static async Task Main(string[] args)
//     {
//         var serviceCollection = new ServiceCollection();
//         ConfigureServices(serviceCollection);
//         var serviceProvider = serviceCollection.BuildServiceProvider();

//         // Get the SkillManager and register skills.
//         var skillManager = serviceProvider.GetRequiredService<SkillManager>();
//         skillManager.RegisterSkill(new DiffSkill());
//         skillManager.RegisterSkill(new FeishuDocSkill());

//         // Simulate an agent using skills
//         var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
//         var agentService = serviceProvider.GetRequiredService<IAgentService>();
//         var skillExecutionContext = new SkillExecutionContext(logger, agentService);

//         logger.LogInformation("\n--- Agent attempting to use Diff Skill ---");
//         try
//         {
//             var diffSkill = skillManager.GetSkill("diffs");
//             var diffResult = await diffSkill.Execute(skillExecutionContext, new Dictionary<string, object>
//             {
//                 { "action", "renderDiff" }, // Although CanExecute checks specific action name, passing it as param
//                 { "before", "Line 1\nLine 2\nLine 3" },
//                 { "after", "Line A\nLine 2\nLine B" }
//             });

//             if (diffResult.Success)
//             {
//                 logger.LogInformation($"Diff Skill Result: {diffResult.Message}");
//                 foreach (var detail in diffResult.Details)
//                 {
//                     logger.LogInformation($"  {detail.Key}: {detail.Value}");
//                 }
//             }
//             else
//             {
//                 logger.LogError($"Diff Skill Failed: {diffResult.Message}");
//             }
//         }
//         catch (Exception ex)
//         {
//             logger.LogError($"Error using Diff Skill: {ex.Message}");
//         }

//         logger.LogInformation("\n--- Agent attempting to use Feishu Doc Skill ---");
//         try
//         {
//             var feishuDocSkill = skillManager.GetSkill("feishu_doc");
//             var feishuCreateResult = await feishuDocSkill.Execute(skillExecutionContext, new Dictionary<string, object>
//             {
//                 { "action", "create" },
//                 { "title", "My New Document" }
//             });

//             if (feishuCreateResult.Success)
//             {
//                 logger.LogInformation($"Feishu Doc Create Result: {feishuCreateResult.Message}");
//                 if (feishuCreateResult.Details.TryGetValue("doc_token", out var docToken))
//                 {
//                     logger.LogInformation($"  New Doc Token: {docToken}");

//                     var feishuWriteResult = await feishuDocSkill.Execute(skillExecutionContext, new Dictionary<string, object>
//                     {
//                         { "action", "write" },
//                         { "doc_token", docToken },
//                         { "content", "Hello, this is the document content." }
//                     });
//                     logger.LogInformation($"Feishu Doc Write Result: {feishuWriteResult.Message}");
//                 }
//             }
//             else
//             {
//                 logger.LogError($"Feishu Doc Create Failed: {feishuCreateResult.Message}");
//             }
//         }
//         catch (Exception ex)
//         {
//             logger.LogError($"Error using Feishu Doc Skill: {ex.Message}");
//         }

//         logger.LogInformation("\n--- Agent attempting to use non-existent Skill ---");
//         try
//         {
//             var unknownSkill = skillManager.GetSkill("non_existent_skill");
//             logger.LogInformation("This line should not be reached.");
//         }
//         catch (ArgumentException ex)
//         {
//             logger.LogWarning($"Expected error caught: {ex.Message}");
//         }
//     }

//     private static void ConfigureServices(IServiceCollection services)
//     {
//         services.AddLogging(configure => configure.AddConsole())
//                 .Configure<LoggerFilterOptions>(options => options.MinLevel = LogLevel.Information);

//         services.AddSingleton<IAgentService, AgentService>();
//         services.AddSingleton<SkillManager>();
//         // Skills themselves could be transient or scoped depending on their statefulness
//         // For simplicity, registering as singletons here.
//         services.AddSingleton<DiffSkill>();
//         services.AddSingleton<FeishuDocSkill>();
//     }
// }

