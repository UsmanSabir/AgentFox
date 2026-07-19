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

The bundled installers build and install **AgentFox with the Trading plugin enabled by default**
(`Modules: "cli,web,trading-agent"`) and make sure every dependency is present on the machine.

**What the installer does for you**

1. Ensures the **.NET SDK 10.0** is installed — if missing, it is fetched from `dot.net` into `~/.dotnet` and added to `PATH`.
2. **Downloads a prebuilt AgentFox binary** (with the Trading plugin) from the repo's GitHub Releases for your OS/architecture.
3. If no prebuilt binary is available (or `-BuildFromSource` is set), it ensures **Git**, clones the source, and builds it locally.
4. Installs the launcher (`AgentFox.exe` / `AgentFox`) plus the Trading plugin into the install directory.
5. Drops an `agentfox` launcher you can run from anywhere.

Default install directory: `~/.agentfox` (`%USERPROFILE%\.agentfox` on Windows). The prebuilt archive is
expected at `…/releases/latest/download/agentfox-<rid>.zip` (Windows) or `agentfox-<rid>.tar.gz` (Linux/macOS),
where `<rid>` is `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64`. Override the
source with `-BinaryUrl` / `AGENTFOX_BINARY_URL`.

> ⚠️ **Trading plugin is configured for LIVE auto-execution.** The bundled `appsettings.json` ships with
> `Plugins.TradingAgent.AutoExecute: true` and `ExecutionMode: "BoundedAuto"`. Before sending any signal you
> **must** review and set `AllowedSymbols`, the `Ahk` broker credentials, and the order value caps — see
> [`src/Plugins/TradingAgent/README.md`](src/Plugins/TradingAgent/README.md). Live AHK order placement also
> requires **Google Chrome / Chromium** on the machine. To run without trading, set `ExecutionMode: "Disabled"`
> (or `"Paper"` for simulation) and `AutoExecute: false`.

### Windows (PowerShell)

Run in an **elevated** PowerShell (needed so dependencies and, optionally, the Windows service can be installed):

```powershell
# One-line install (downloads the installer and runs it; it clones + builds AgentFox)
irm https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.ps1 | iex
```

To pass options (custom install dir, branch, skip the service hint), download first and run with parameters:

```powershell
irm https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.ps1 -OutFile install.ps1
.\install.ps1 -InstallDir "C:\Tools\AgentFox" -Branch main -SkipService
```

Run it:

```powershell
& "$HOME\.agentfox\agentfox.cmd"
# Install as a Windows service (optional):
& "$HOME\.agentfox\agentfox.cmd" --install-service
```

### Linux (bash)

```bash
# One-line install (installs git + .NET 10 SDK via your package manager, then clones + builds)
curl -fsSL https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.sh | bash
```

Supported package managers for the Git/curl step: `apt-get`, `dnf`, `yum`, `apk` (uses `sudo` when not root).
To customize, download and run with environment variables:

```bash
curl -fsSL https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.sh -o install.sh
AGENTFOX_INSTALL_DIR="$HOME/apps/agentfox" AGENTFOX_BRANCH=main bash install.sh
```

Run it:

```bash
~/.agentfox/agentfox
```

### From a local clone (Windows / Linux / macOS)

If you already have the repository checked out, run the installer from inside it — no clone step is performed:

```bash
git clone https://github.com/UsmanSabir/AgentFox.git
cd AgentFox

# Windows
powershell -ExecutionPolicy Bypass -File .\install.ps1

# Linux
bash ./install.sh
```

### macOS (bash)

The shell installer now detects macOS and uses the correct `osx-arm64` / `osx-x64` runtime IDs (Git is
installed via Homebrew if missing):

```bash
curl -fsSL https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.sh -o install.sh
bash ./install.sh
~/.agentfox/agentfox
```

To force a source build instead of a prebuilt download:

```bash
AGENTFOX_BUILD_FROM_SOURCE=1 bash ./install.sh
```

### Installer options

| Option (PowerShell) | Env var (both) | Default | Description |
|---|---|---|---|
| `-RepoUrl <url>` | `AGENTFOX_REPO_URL` | `https://github.com/UsmanSabir/AgentFox.git` | Source repo to clone when building from source. |
| `-Branch <name>` | `AGENTFOX_BRANCH` | default branch | Branch to clone (shallow). |
| `-InstallDir <path>` | `AGENTFOX_INSTALL_DIR` | `~/.agentfox` | Where AgentFox is installed. |
| `-BinaryUrl <url>` | `AGENTFOX_BINARY_URL` | GitHub Releases latest | Direct URL to a prebuilt archive to download. |
| `-BuildFromSource` | `AGENTFOX_BUILD_FROM_SOURCE=1` | off | Skip the prebuilt download and always build from source. |
| `-SkipService` | — | off | Suppress the "install as a Windows service" hint (Windows only). |

### Building & publishing release binaries

To produce the prebuilt archives the installers download (and publish them to GitHub Releases for all
six OS/arch targets), see [RELEASING.md](RELEASING.md). In short — push a tag and CI does the rest:

```bash
git tag v1.0.0
git push origin v1.0.0   # triggers .github/workflows/release.yml
```

### Quick start (any platform, dev mode)

```bash
git clone https://github.com/UsmanSabir/AgentFox.git
cd AgentFox

# Build the project
dotnet build src/AgentFox.sln

# Run in interactive mode
dotnet run --project src/Agent
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

- .NET SDK 10.0 or later (the installer will fetch it automatically if missing)
- Git (installed automatically by the installer)
- Windows / macOS / Linux
- Google Chrome or Chromium — only required for live AHK trading execution

## License

MIT License - See LICENSE file for details

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Multi-Agent Orchestration - "Coordinator Mode"


TODO: Have a full **multi-agent orchestration system** 

| Phase | Who | Purpose |
|-------|-----|---------|
| **Research** | Workers (parallel) | Investigate codebase, find files, understand problem |
| **Synthesis** | **Coordinator** | Read findings, understand the problem, craft specs |
| **Implementation** | Workers | Make targeted changes per spec, commit |
| **Verification** | Workers | Test changes work |

---