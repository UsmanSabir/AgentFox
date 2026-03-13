# AgentFox 🦊

A powerful multi-agent AI framework in C# with support for sub-agents, memory, MCP, skills, and channel integrations.

## Features

### 🤖 Multi-Agent System
- Create main agents and spawn sub-agents with inherited capabilities
- Hierarchical agent management
- Agent state tracking and status monitoring

### 🧠 Memory System
- **Short-term memory**: Conversation context with configurable size
- **Long-term memory**: Persistent storage with automatic saving
- **Hybrid memory**: Combines both with auto-consolidation

### 🔧 Tool Calling
- Extensible tool registry
- Built-in tools for file operations, shell commands, and more
- Custom tool creation via `ITool` interface

### 🔌 MCP (Model Context Protocol)
- Connect to external MCP servers
- Automatic tool registration from MCP servers
- Support for multiple concurrent MCP connections

### 🎯 Skills (Composio Dev Skills)
Enable powerful developer capabilities:
- **Git**: commit, push, pull, branch, merge
- **Docker**: build, run, stop, logs
- **Code Review**: Automated code quality analysis
- **Debugging**: Trace and profile applications
- **API Integration**: REST and GraphQL support
- **Database**: Query and migration tools
- **Testing**: Run tests and generate coverage
- **Deployment**: CI/CD pipeline execution

### 📱 Channel Integrations
Connect agents to multiple platforms:
- **WhatsApp**: Pair via QR code, send/receive messages
- **Telegram**: Bot integration with webhook support
- **Microsoft Teams**: Enterprise messaging and meeting creation
- **Slack** (bonus): Channel messaging and attachments

## Project Structure

```
AgentFox/
├── AgentFox.csproj           # .NET 8 project file
├── Program.cs                 # Main entry point with CLI
├── Models/
│   └── AgentModels.cs        # Core data models
├── Memory/
│   └── IMemory.cs            # Memory system interfaces
├── Tools/
│   ├── ITool.cs              # Tool interface and registry
│   ├── BuiltInTools.cs       # File, shell, search tools
│   └── CustomTools.cs        # Web, calculator, UUID tools
├── Agents/
│   ├── Agent.cs              # Main agent class
│   └── SubAgentSystem.cs     # Sub-agent management
├── MCP/
│   └── MCPClient.cs          # MCP protocol support
├── Skills/
│   └── SkillSystem.cs        # Composio dev skills
└── Channels/
    └── Channels.cs           # WhatsApp, Telegram, Teams, Slack
```

## Installation

```bash
# Clone the repository
git clone https://github.com/yourusername/AgentFox.git
cd AgentFox

# Build the project
dotnet build

# Run in interactive mode
dotnet run
```

## Usage

### Command Line Mode

```bash
# Execute a single task
dotnet run -- "Your task here"

# Check agent status
dotnet run -- status

# List available tools
dotnet run -- list tools

# Say hello
dotnet run -- "say hello"
```

### Interactive Mode

```bash
dotnet run
```

Available commands:
- `help` - Show help message
- `status` - Show agent status
- `history` - Show conversation history
- `memory` - Show agent memory
- `tools` - List available tools
- `clear` - Clear conversation history
- `exit` - Exit the program

### Debug in VS Code
Add launch configuration in .vscode\launch.json

{
    // Use IntelliSense to learn about possible attributes.
    // Hover to view descriptions of existing attributes.
    // For more information, visit: https://go.microsoft.com/fwlink/?linkid=830387
    "version": "0.2.0",
    "configurations": [ 
        {
            "name": "C#: AgentFox Debug",
            "type": "dotnet",
            "request": "launch",
            "projectPath": "${workspaceFolder}/AgentFox.csproj"
        }
    ]
}

### Programmatic Usage

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

## Examples

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

## Requirements

- .NET 8.0 or later
- Windows/macOS/Linux

## License

MIT License - See LICENSE file for details

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
