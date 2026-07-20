# AgentFox 🦊

A powerful multi-agent AI framework in C# with support for sub-agents, memory, MCP, skills, and channel integrations.

## Features

- 🤖 **Multi-agent system** — main agents, sub-agents with inherited capabilities, hierarchical management
- 🧠 **Memory** — short-term, persistent long-term, and hybrid memory with auto-consolidation
- 🔧 **Tool calling** — extensible registry with built-in file, shell, web, and utility tools
- 🔌 **MCP** — connect external Model Context Protocol servers; their tools register automatically
- 🎯 **Skills** — Git, Docker, code review, debugging, API, database, testing, deployment (Composio)
- 📱 **Channels** — WhatsApp, Telegram, Microsoft Teams, Discord, Slack
- 📈 **Trading plugin (optional)** — trading signal parsing with automated broker execution

## Installation

The installer checks dependencies (.NET SDK 10.0, Git), downloads a prebuilt binary from GitHub
Releases (or builds from source), drops an `agentfox` launcher into `~/.agentfox`, and **adds that
directory to your PATH** so you can run `agentfox` from anywhere (open a new terminal after install
for the PATH change to take effect). When run in a terminal it finishes by launching the
interactive setup wizard, which configures the LLM provider, plugin credentials (e.g. the Trading
plugin's AHK login, PIN and allowed symbols), and optionally installs the system service — then
either starts the agent or, if the service is already running, points you at the web UI. Pass
`-SkipOnboarding` / `--skip-onboarding` to skip the wizard; re-run it any time with
`agentfox --onboarding`.

### Windows (PowerShell)

Run in an **elevated** PowerShell:

```powershell
# With the Trading plugin (default)
irm https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.ps1 | iex

# Without the Trading plugin
$env:AGENTFOX_NO_TRADING = '1'; irm https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.ps1 | iex
```

Or download first and use parameters:

```powershell
irm https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.ps1 -OutFile install.ps1
.\install.ps1 -NoTrading                          # core only, no Trading plugin
.\install.ps1 -InstallDir "C:\Tools\AgentFox"     # custom install dir
```

Run it (open a new terminal first so PATH is picked up):

```powershell
agentfox                    # start the agent (web UI on port 8080 by default)
agentfox --onboarding       # re-run the interactive setup wizard
agentfox --install-service  # optional: run as a Windows service
```

### Linux / macOS (bash)

```bash
# With the Trading plugin (default)
curl -fsSL https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.sh | bash

# Without the Trading plugin
curl -fsSL https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.sh | bash -s -- --no-trading
```

Run it (open a new terminal, or `source ~/.bashrc` / `~/.zshrc`, so PATH is picked up):

```bash
agentfox                    # start the agent (web UI on port 8080 by default)
agentfox --onboarding       # re-run the interactive setup wizard
agentfox --install-service  # optional: run as a system service
```

### From a local clone

```bash
git clone https://github.com/UsmanSabir/AgentFox.git
cd AgentFox

# Windows
powershell -ExecutionPolicy Bypass -File .\install.ps1            # with trading
powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoTrading # without trading

# Linux / macOS
bash ./install.sh                # with trading
bash ./install.sh --no-trading   # without trading
```

### Installer options

| Option (PowerShell) | Flag / env var (bash) | Default | Description |
|---|---|---|---|
| `-NoTrading` | `--no-trading` / `AGENTFOX_NO_TRADING=1` | trading installed | Install without the Trading plugin. |
| `-InstallDir <path>` | `AGENTFOX_INSTALL_DIR` | `~/.agentfox` | Where AgentFox is installed. |
| `-BinaryUrl <url>` | `AGENTFOX_BINARY_URL` | GitHub Releases latest | Direct URL to a prebuilt archive. |
| `-BuildFromSource` | `AGENTFOX_BUILD_FROM_SOURCE=1` | off | Skip the prebuilt download and build from source. |
| `-RepoUrl <url>` | `AGENTFOX_REPO_URL` | this repo | Source repo to clone when building from source. |
| `-Branch <name>` | `AGENTFOX_BRANCH` | default branch | Branch to clone (shallow). |
| `-SkipService` | — | off | Suppress the Windows service hint (Windows only). |
| `-SkipOnboarding` | `--skip-onboarding` / `AGENTFOX_SKIP_ONBOARDING=1` | wizard runs | Don't launch the interactive setup wizard after install. |

### Updating

The installer writes an `update` script into the install dir that re-downloads the latest release
and overwrites the install in place (no wizard). It preserves your `appsettings.json` config.

```powershell
# Windows
powershell -File "$HOME\.agentfox\update.ps1"
```

```bash
# Linux / macOS
~/.agentfox/update.sh
```

Re-running the original install one-liner (`irm … | iex` / `curl … | bash`) does the same thing.

### Uninstalling

The installer writes an `uninstall` script into the install dir. It removes the system service (if
installed), drops the PATH entry, and deletes the install directory.

```powershell
# Windows
powershell -File "$HOME\.agentfox\uninstall.ps1"
```

```bash
# Linux / macOS
~/.agentfox/uninstall.sh
```

> Open a new terminal afterwards for the PATH change to take effect. If a custom `-InstallDir` /
> `AGENTFOX_INSTALL_DIR` was used, the `update` and `uninstall` scripts live in that directory.

### ⚠️ Trading plugin safety

When installed, the Trading plugin is configured for **LIVE auto-execution**
(`AutoExecute: true`, `ExecutionMode: "BoundedAuto"`). Before sending any signal you **must**
review and set `AllowedSymbols`, the `Ahk` broker credentials, and the order value caps in
`appsettings.json` — see [src/Plugins/TradingAgent/README.md](src/Plugins/TradingAgent/README.md).
Live order placement also requires Google Chrome / Chromium. To keep the plugin installed but
inactive, set `ExecutionMode: "Disabled"` (or `"Paper"` for simulation) and `AutoExecute: false`.

## Quick Start (dev mode)

```bash
git clone https://github.com/UsmanSabir/AgentFox.git
cd AgentFox
dotnet build src/AgentFox.sln
dotnet run --project src/Agent          # interactive mode
dotnet run --project src/Agent -- "your task here"
```

## Requirements

- .NET SDK 10.0 or later (the installer fetches it automatically if missing)
- Windows / macOS / Linux
- Git — only for building from source (installed automatically by the installer)
- Google Chrome or Chromium — only for live trading execution

## Documentation

- [Developer Guide](docs/DEVELOPMENT.md) — project structure, programmatic usage, configuration, debugging
- [Trading Plugin](src/Plugins/TradingAgent/README.md) — trading agent setup and safety configuration
- [Releasing](RELEASING.md) — building and publishing release binaries

## License

MIT License - See LICENSE file for details

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request. See the
[Developer Guide](docs/DEVELOPMENT.md) to get started.
