# AgentFox Developer Guide

Everything you need to build, extend, and debug AgentFox. For end-user installation, see the
[README](../README.md).

## Project Structure

```
AgentFox/
├── install.ps1 / install.sh      # End-user installers
├── RELEASING.md                  # How release binaries are built & published
└── src/
    ├── AgentFox.sln              # Solution file
    ├── Agent/                    # Main host application
    │   ├── Program.cs            # Entry point, module/plugin loading
    │   ├── Agents/               # FoxAgent, orchestrator, sub-agent manager
    │   ├── Memory/               # Short-term / long-term / hybrid memory
    │   ├── Tools/                # Tool registry and built-in tools
    │   ├── Skills/               # Skill system (Composio dev skills)
    │   ├── MCP/                  # Model Context Protocol client
    │   ├── Channels/             # Telegram, Discord, WhatsApp, Teams, Slack
    │   ├── LLM/                  # Provider factory (OpenAI, Anthropic, Ollama, …)
    │   ├── Modules/              # cli / web / webhook app modules + plugin loader
    │   └── appsettings.json      # Configuration
    ├── Plugins/
    │   ├── TradingAgent/         # Trading signal parsing + AHK broker execution
    │   ├── AgentFox.BraveSearch/
    │   ├── AgentFox.TavilySearch/
    │   ├── AgentFox.DuckDuckGoSearch/
    │   └── PageAgent/            # Browser automation plugin
    ├── LocalEmbeddings/          # Local embedding model support
    └── frontend/                 # SvelteKit web UI
```

## Building & Running

```bash
git clone https://github.com/UsmanSabir/AgentFox.git
cd AgentFox

# Build the solution
dotnet build src/AgentFox.sln

# Run in interactive mode
dotnet run --project src/Agent

# Execute a single task
dotnet run --project src/Agent -- "Your task here"
```

### Interactive Mode Commands

- `help` - Show help message
- `status` - Show agent status
- `history` - Show conversation history
- `memory` - Show agent memory
- `tools` - List available tools
- `clear` - Clear conversation history
- `exit` - Exit the program

### Debug in VS Code

Add a launch configuration in `.vscode/launch.json`:

```json
{
    "version": "0.2.0",
    "configurations": [
        {
            "name": "C#: AgentFox Debug",
            "type": "dotnet",
            "request": "launch",
            "projectPath": "${workspaceFolder}/src/Agent/AgentFox.csproj"
        }
    ]
}
```

## Programmatic Usage

```csharp
using AgentFox.Agents;
using AgentFox.Memory;
using AgentFox.Tools;
using AgentFox.Skills;

// Create tool registry and add tools
var toolRegistry = new ToolRegistry();
toolRegistry.Register(new ShellCommandTool());
toolRegistry.Register(new ReadFileTool());

// Create skill registry
var skillRegistry = new SkillRegistry(toolRegistry);

// Enable skills
await skillRegistry.EnableSkillAsync("git");
await skillRegistry.EnableSkillAsync("docker");

// Create agent with memory
var agent = new AgentBuilder(toolRegistry)
    .WithName("MyAgent")
    .WithSystemPrompt("You are a helpful assistant.")
    .WithHybridMemory(100, "memory.json")
    .Build();

// Execute tasks
var result = await agent.ExecuteAsync("Write a file called hello.txt with 'Hello World'");
Console.WriteLine(result.Output);
```

### MCP Integration

```csharp
var mcpClient = new MCPClient(toolRegistry);
await mcpClient.AddServerAsync("my-mcp-server", "http://localhost:3000");
```

### Channel Integration

```csharp
// WhatsApp with QR pairing
var whatsapp = new WhatsAppChannel(phoneNumberId, accessToken, businessAccountId);
var qrCode = whatsapp.GeneratePairingQRCode();
await whatsapp.ConnectAsync();

// Telegram bot
var telegram = new TelegramChannel(botToken, chatId);
await telegram.ConnectAsync();

// Microsoft Teams
var teams = new TeamsChannel(tenantId, clientId, clientSecret, serviceUrl);
await teams.ConnectAsync();

// Create channel manager
var channelManager = new ChannelManager(agent);
channelManager.AddChannel(whatsapp);
channelManager.AddChannel(telegram);
await channelManager.ConnectAllAsync();
```

### Skills Usage

```csharp
var skillRegistry = new SkillRegistry(toolRegistry);

// Enable specific skills
await skillRegistry.EnableSkillAsync("git");      // Git operations
await skillRegistry.EnableSkillAsync("docker");   // Docker operations
await skillRegistry.EnableSkillAsync("testing");  // Test execution
await skillRegistry.EnableSkillAsync("deployment"); // CI/CD

// Disable skills
skillRegistry.DisableSkill("docker");
```

## Configuration

### Agent Configuration

```csharp
var config = new AgentConfig
{
    Name = "AgentFox",
    Description = "My AI Agent",
    SystemPrompt = "You are a helpful assistant specialized in coding.",
    MaxTokens = 4096,
    Temperature = 0.7,
    MaxIterations = 10
};
```

### Memory Configuration

```csharp
// Short-term only
agent.WithMemory(new ShortTermMemory(100));

// Long-term only (persistent)
agent.WithMemory(new LongTermMemory("memory.json"));

// Hybrid (recommended)
agent.WithHybridMemory(shortTermSize: 50, longTermPath: "memory.json");
```

### Modules & Plugins

Modules are enabled through `appsettings.json`. All discovered modules (built-in + plugins) are
enabled by default; opt out specific ones with a `DisabledModules` CSV (e.g. `"web,webhook"`).
The legacy opt-in `Modules` key is still honored: if present, ONLY the listed modules are enabled
(e.g. `"Modules": "cli,web,trading-agent"`).

Plugins are discovered from the `plugins/` folder next to the AgentFox binary. Copy each plugin's
entire publish output (DLL + `.deps.json` + dependencies) into its own subfolder — e.g.
`plugins/TradingAgent/`.

## Example Prompts

### File Operations

```bash
> read_file path/to/file.cs
> write_file newfile.txt "Hello World"
> list_files .
> search_files "*.cs" "class"
```

### Shell Commands

```bash
> shell "dotnet build"
> shell "git status"
```

### Spawn Sub-agents

```bash
> spawn a subagent to analyze the codebase
> delegate code review task to a subagent
```

## Building & Publishing Release Binaries

To produce the prebuilt archives the installers download (and publish them to GitHub Releases for
all six OS/arch targets), see [RELEASING.md](../RELEASING.md). In short — push a tag and CI does
the rest:

```bash
git tag v1.0.0
git push origin v1.0.0   # triggers .github/workflows/release.yml
```

## Roadmap: Multi-Agent Orchestration — "Coordinator Mode"

TODO: Have a full **multi-agent orchestration system**

| Phase | Who | Purpose |
|-------|-----|---------|
| **Research** | Workers (parallel) | Investigate codebase, find files, understand problem |
| **Synthesis** | **Coordinator** | Read findings, understand the problem, craft specs |
| **Implementation** | Workers | Make targeted changes per spec, commit |
| **Verification** | Workers | Test changes work |
