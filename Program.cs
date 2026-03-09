using AgentFox.Agents;
using AgentFox.LLM;
using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Tools;

namespace AgentFox;

/// <summary>
/// AgentFox - Multi-agent framework in C#
/// A multi-agent framework with sub-agents, memory, MCP, skills, and channel integrations
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                  AgentFox - AI Agent Framework               ║");
        Console.WriteLine("║         Multi-agent system with memory & channels            ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        
        // Check for command line mode
        if (args.Length > 0)
        {
            return await RunCommandLineMode(args);
        }
        
        // Interactive mode
        return await RunInteractiveMode();
    }
    
    static async Task<int> RunCommandLineMode(string[] args)
    {
        var toolRegistry = CreateToolRegistry();
        
        // Build system prompt with dynamic builder
        var systemPrompt = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
            .WithTools("shell", "read_file", "write_file", "list_files", "search_files", "make_directory", "delete")
            .WithConstraints(
                "Always verify changes before executing destructive operations",
                "Prioritize security and best practices",
                "Ask for clarification when requirements are ambiguous"
            )
            .Build();
        
        var agent = new AgentBuilder(toolRegistry)
            .WithName("AgentFox")
            .WithSystemPrompt(systemPrompt)
            .WithHybridMemory(100, "memory.json")
            .Build();
        
        var task = string.Join(" ", args);
        Console.WriteLine($"Executing: {task}");
        Console.WriteLine(new string('-', 50));
        
        var result = await agent.ExecuteAsync(task);
        
        Console.WriteLine("Result:");
        Console.WriteLine(result.Output);
        
        if (!string.IsNullOrEmpty(result.Error))
        {
            Console.WriteLine($"Error: {result.Error}");
            return 1;
        }
        
        return result.Success ? 0 : 1;
    }
    
    static async Task<int> RunInteractiveMode()
    {
        var toolRegistry = CreateToolRegistry();
        
        // Build comprehensive system prompt with all available tools and context
        var systemPrompt = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
            .WithTools(
                "shell: Execute shell commands",
                "read_file: Read file contents",
                "write_file: Write content to files",
                "list_files: List files in a directory",
                "search_files: Search for text in files",
                "make_directory: Create directories",
                "delete: Delete files or directories",
                "get_env_info: Get environment information",
                "spawn_subagent: Spawn a sub-agent for complex tasks"
            )
            .WithExecutionContext(
                "You are running in interactive mode and can help with:\n" +
                "- Code development and debugging\n" +
                "- File system operations\n" +
                "- System administration\n" +
                "- Architecture and design consultation"
            )
            .WithConstraints(
                "Always verify changes before executing destructive operations",
                "Protect sensitive information (API keys, credentials, etc.)",
                "Test code in isolated environments when possible",
                "Explain your reasoning and approach clearly",
                "Ask for confirmation for high-risk operations"
            )
            .Build();
        
        var agent = new AgentBuilder(toolRegistry)
            .WithName("AgentFox")
            .WithSystemPrompt(systemPrompt)
            .WithHybridMemory(100, "memory.json")
            .Build();
        
        Console.WriteLine("Type 'help' for available commands, 'exit' to quit.");
        Console.WriteLine();
        
        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(input))
                continue;
            
            if (input.Trim().ToLower() == "exit")
            {
                Console.WriteLine("Goodbye!");
                break;
            }
            
            if (input.Trim().ToLower() == "help")
            {
                ShowHelp();
                continue;
            }
            
            if (input.Trim().ToLower() == "status")
            {
                ShowStatus(agent);
                continue;
            }
            
            if (input.Trim().ToLower() == "history")
            {
                ShowHistory(agent);
                continue;
            }
            
            if (input.Trim().ToLower() == "clear")
            {
                agent.ClearHistory();
                Console.WriteLine("History cleared.");
                continue;
            }
            
            if (input.Trim().ToLower() == "memory")
            {
                await ShowMemory(agent);
                continue;
            }
            
            if (input.Trim().ToLower() == "tools")
            {
                ShowTools(toolRegistry);
                continue;
            }
            
            // Execute task
            Console.WriteLine();
            var result = await agent.ExecuteAsync(input);
            
            Console.WriteLine(result.Output);
            Console.WriteLine();
            
            if (result.SpawnedSubAgents.Count > 0)
            {
                Console.WriteLine($"Spawned {result.SpawnedSubAgents.Count} sub-agent(s)");
            }
        }
        
        return 0;
    }
    
    static ToolRegistry CreateToolRegistry()
    {
        var registry = new ToolRegistry();
        
        // Register built-in tools
        registry.Register(new ShellCommandTool());
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new ListFilesTool());
        registry.Register(new SearchFilesTool());
        registry.Register(new MakeDirectoryTool());
        registry.Register(new DeleteTool());
        registry.Register(new GetEnvironmentInfoTool());
        
        // Register custom tools
        registry.Register(new WebSearchTool());
        registry.Register(new FetchUrlTool());
        registry.Register(new CalculatorTool());
        registry.Register(new UuidTool());
        registry.Register(new TimestampTool());
        
        return registry;
    }
    
    static void ShowHelp()
    {
        Console.WriteLine(@"
Available commands:
  help     - Show this help message
  status   - Show agent status
  history  - Show conversation history
  memory   - Show agent memory
  tools    - List available tools
  clear    - Clear conversation history
  exit     - Exit the program

You can also ask the agent to:
  - Execute shell commands
  - Read/write files
  - Search for text in files
  - Spawn sub-agents for complex tasks
  - Remember information for later
");
    }
    
    static void ShowStatus(FoxAgent agent)
    {
        var info = agent.GetInfo();
        Console.WriteLine($"""
            Agent Status:
            ─────────────
            Name:        {info.Name}
            ID:          {info.Id}
            Status:      {info.Status}
            Messages:    {info.MessageCount}
            Sub-agents:  {info.SubAgentCount}
            Tools:       {info.ToolCount}
            Memory:      {(info.HasMemory ? "Enabled" : "Disabled")}
            Created:     {info.CreatedAt:yyyy-MM-dd HH:mm:ss}
            Last Active: {info.LastActiveAt:yyyy-MM-dd HH:mm:ss}
            """);
    }
    
    static void ShowHistory(FoxAgent agent)
    {
        var history = agent.GetHistory();
        Console.WriteLine($"Conversation History ({history.Count} messages):");
        Console.WriteLine(new string('-', 50));
        
        foreach (var msg in history)
        {
            Console.WriteLine($"[{msg.Timestamp:HH:mm:ss}] {msg.Role}: {msg.Content}");
            Console.WriteLine();
        }
    }
    
    static async Task ShowMemory(FoxAgent agent)
    {
        if (agent.Memory == null)
        {
            Console.WriteLine("No memory configured.");
            return;
        }
        
        var memories = await agent.Memory.GetRecentAsync(10);
        Console.WriteLine($"Agent Memory ({memories.Count} recent entries):");
        Console.WriteLine(new string('-', 50));
        
        foreach (var mem in memories)
        {
            Console.WriteLine($"[{mem.Timestamp:HH:mm:ss}] {mem.Type}: {mem.Content}");
        }
    }
    
    static void ShowTools(ToolRegistry registry)
    {
        var tools = registry.GetAll();
        Console.WriteLine($"Available Tools ({tools.Count}):");
        Console.WriteLine(new string('-', 50));
        
        foreach (var tool in tools)
        {
            Console.WriteLine($"  {tool.Name}");
            Console.WriteLine($"    {tool.Description}");
            Console.WriteLine();
        }
    }
}
