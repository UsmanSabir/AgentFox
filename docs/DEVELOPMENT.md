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

All discovered modules (built-in + plugins) are enabled by default. Opt OUT specific ones with a
`DisabledModules` CSV in `appsettings.json` (e.g. `"DisabledModules": "web,webhook"`). That is the
only mechanism.

The old opt-IN `Modules` key is no longer read. It meant "only these run", and a list written before
a plugin existed can never name that plugin — so a correctly installed plugin module was discovered,
silently skipped, and the only notice went to the console. A host that still carries the key warns at
startup and is otherwise unaffected; translate it by listing what you wanted OFF in `DisabledModules`
instead.

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
all six OS/arch targets), see [RELEASING.md](../RELEASING.md). In short — start the Release
workflow by hand (it is manual-only; pushing a tag does not trigger it):

```bash
gh workflow run release.yml -f tag=v1.0.0   # or Actions > Release > Run workflow
```

CI builds five targets. `osx-x64` (Intel Mac) is left out to save Actions minutes — a macOS runner
bills at 10× a Linux one — so it is built by hand, below.

### Building the macOS Intel (osx-x64) archive

Without this archive nothing breaks: `install.sh` on an Intel Mac finds no prebuilt download and
builds from source. Build it only when you want Intel Mac users to get a prebuilt binary.

**Build it ON a Mac** — Intel or Apple Silicon both work. A macOS executable has to be code-signed
(ad-hoc is enough), and the .NET SDK only signs it when the build runs on macOS; one cross-built
from Windows or Linux may be refused by Gatekeeper.

**Prerequisites:** .NET 10 SDK, Node.js 20, and `gh` (authenticated) if you will upload it.

Run from the repository root. These are the same steps and flags the CI build uses, so the archive
matches the others in the release:

```bash
set -euo pipefail
RID=osx-x64
VERSION=1.0.42                      # match the release you are attaching it to (no leading v)
OUT="staging/$RID"
rm -rf "$OUT"

# 1. Host web UI -> src/Agent/wwwroot (embedded by the publish below, so it must come first)
( cd src/frontend && npm ci && npm run build )

# 2. The app, as one single-file executable
dotnet publish src/Agent/AgentFox.csproj -c Release -r "$RID" --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:UseAppHost=true \
  -p:Version="$VERSION" -p:InformationalVersion="$VERSION+$(git rev-parse --short HEAD)" \
  -o "$OUT"

# 3. Trading plugin UI (embedded into TradingAgent.Core), then the bundled plugins
( cd src/Plugins/TradingAgent/ui && npm ci && npm run build )
dotnet publish src/Plugins/TradingAgent/TradingAgent.csproj                   -c Release -r "$RID" --self-contained false -o "$OUT/plugins/TradingAgent"
dotnet publish src/Plugins/PageAgent/PageAgent.csproj                         -c Release -r "$RID" --self-contained false -o "$OUT/plugins/PageAgent"
dotnet publish src/Plugins/AgentFox.BraveSearch/AgentFox.BraveSearch.csproj   -c Release -r "$RID" --self-contained false -o "$OUT/plugins/BraveSearch"
dotnet publish src/Plugins/AgentFox.TavilySearch/AgentFox.TavilySearch.csproj -c Release -r "$RID" --self-contained false -o "$OUT/plugins/TavilySearch"
dotnet publish src/Plugins/AgentFox.DuckDuckGoSearch/AgentFox.DuckDuckGoSearch.csproj -c Release -r "$RID" --self-contained false -o "$OUT/plugins/DuckDuckGoSearch"

# 4. Package — the file name must be exactly this; install.sh downloads it by name
tar -czf "agentfox-$RID.tar.gz" -C "$OUT" .
```

**Check it runs** (on an Intel Mac, or under Rosetta on Apple Silicon):

```bash
mkdir -p /tmp/af && tar -xzf agentfox-osx-x64.tar.gz -C /tmp/af && /tmp/af/AgentFox --version
```

**Attach it to the release** CI already created:

```bash
gh release upload v1.0.42 agentfox-osx-x64.tar.gz --clobber
```

`install.sh` downloads from `releases/latest`, so attach it to the newest release. An archive on an
older release is not picked up.

If macOS says the app "cannot be opened" after downloading it in a browser, that is the quarantine
flag, not a bad build: `xattr -dr com.apple.quarantine <install-dir>`.

## Roadmap: Multi-Agent Orchestration — "Coordinator Mode"

TODO: Have a full **multi-agent orchestration system**

| Phase | Who | Purpose |
|-------|-----|---------|
| **Research** | Workers (parallel) | Investigate codebase, find files, understand problem |
| **Synthesis** | **Coordinator** | Read findings, understand the problem, craft specs |
| **Implementation** | Workers | Make targeted changes per spec, commit |
| **Verification** | Workers | Test changes work |
