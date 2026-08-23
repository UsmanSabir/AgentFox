# AgentFox 🦊

**Your own AI agent, running on your computer.** Chat with it on WhatsApp, Telegram, Discord,
Slack, or Microsoft Teams and it handles things for you — answering questions, browsing the web,
managing files, running scheduled tasks, and more. No coding required to use it.

*(Built in C#, if you're into that — see [Features](#features) and the
[Developer Guide](docs/DEVELOPMENT.md) below.)*

> ⭐ If AgentFox helps you build, automate, or trade with more discipline, please star the repository. It helps others discover the project and supports its continued development.

## Features

- 🤖 **Multi-agent system** — main agents, sub-agents with inherited capabilities, hierarchical management
- 🧠 **Memory** — short-term, persistent long-term, and hybrid memory with auto-consolidation
- 🔧 **Tool calling** — extensible registry with built-in file, shell, web, and utility tools
- 🔌 **MCP** — connect external Model Context Protocol servers; their tools register automatically
- 🎯 **Skills** — Git, Docker, code review, debugging, API, database, testing, deployment (Composio)
- 📱 **Channels** — WhatsApp, Telegram, Microsoft Teams, Discord, Slack
- 📈 **Trading plugin (optional)** — trading signal parsing with automated broker execution

## Installation

Installing sets everything up automatically: it checks for required software and installs
anything missing (like .NET), downloads AgentFox, and finishes with a friendly setup wizard that
asks a few questions (which AI provider to use, any account logins for plugins, etc.) and then
starts the agent for you. You don't need to know what any of that means — just follow the steps
below for your operating system.

Two flavors are available:

- **Recommended** — includes the optional Trading plugin (for automated stock trading signals).
  See the **⚠️ Trading plugin safety** section further down this page before you turn it on.
- **Without Trading** — a leaner install that skips that plugin entirely.

Pick the one command for your system below, copy it, paste it into the terminal, and press Enter.

### 🪟 Windows

1. Click the **Start** button, type `PowerShell`, right-click **Windows PowerShell**, and choose
   **Run as administrator**.
2. Paste in **one** of the commands below and press Enter.

**Recommended (with the Trading plugin):**

```powershell
irm https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.ps1 | iex
```

**Without the Trading plugin:**

```powershell
$env:AGENTFOX_NO_TRADING = '1'; irm https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.ps1 | iex
```

That's it — the setup wizard will walk you through the rest. Once it's done, close and reopen your
terminal, then run:

```powershell
agentfox
```

to start (or restart) AgentFox any time. `agentfox --onboarding` re-runs the setup wizard, and
`agentfox --install-service` makes it start automatically in the background (optional).

### 🍎🐧 macOS / Linux

1. Open the **Terminal** app.
2. Paste in **one** of the commands below and press Enter.

**Recommended (with the Trading plugin):**

```bash
curl -fsSL https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.sh | bash
```

**Without the Trading plugin:**

```bash
curl -fsSL https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.sh | bash -s -- --no-trading
```

That's it — the setup wizard will walk you through the rest. Once it's done, close and reopen your
terminal (or run `source ~/.bashrc` / `source ~/.zshrc`), then run:

```bash
agentfox
```

to start (or restart) AgentFox any time. `agentfox --onboarding` re-runs the setup wizard, and
`agentfox --install-service` makes it start automatically in the background (optional).

### For developers: installing from a local clone

```bash
git clone https://github.com/UsmanSabir/AgentFox.git
cd AgentFox
```

**Windows:**

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1            # with trading
```

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoTrading # without trading
```

**Linux / macOS:**

```bash
bash ./install.sh                # with trading
```

```bash
bash ./install.sh --no-trading   # without trading
```

Or download the script first and pass options directly:

```powershell
irm https://raw.githubusercontent.com/UsmanSabir/AgentFox/main/install.ps1 -OutFile install.ps1
```

```powershell
.\install.ps1 -NoTrading -InstallDir "C:\Tools\AgentFox"
```

### Installer options

| Option (PowerShell) | Flag / env var (bash) | Default | Description |
|---|---|---|---|
| `-NoTrading` | `--no-trading` / `AGENTFOX_NO_TRADING=1` | trading installed | Install without the Trading plugin. |
| `-WithTrading` | `--with-trading` / `AGENTFOX_WITH_TRADING=1` | preserve on update | Explicitly add/retain the Trading plugin. |
| `-InstallDir <path>` | `AGENTFOX_INSTALL_DIR` | `~/.agentfox` | Where AgentFox is installed. |
| `-BinaryUrl <url>` | `AGENTFOX_BINARY_URL` | GitHub Releases latest | Direct URL to a prebuilt archive. |
| `-BuildFromSource` | `AGENTFOX_BUILD_FROM_SOURCE=1` | off | Skip the prebuilt download and build from source. |
| `-RepoUrl <url>` | `AGENTFOX_REPO_URL` | this repo | Source repo to clone when building from source. |
| `-Branch <name>` | `AGENTFOX_BRANCH` | default branch | Branch to clone (shallow). |
| `-InstallService` | `--install-service` / `AGENTFOX_INSTALL_SERVICE=1` | off | Register AgentFox as a background service after install. Windows prompts for elevation (UAC); Linux/macOS re-invoke through `sudo`. |
| `-SkipService` | `--skip-service` / `AGENTFOX_SKIP_SERVICE=1` | off | Don't install the service and suppress the service hint. |
| `-SkipOnboarding` | `--skip-onboarding` / `AGENTFOX_SKIP_ONBOARDING=1` | wizard runs | Don't launch the interactive setup wizard after install. |

### Updating

The installer writes an `update` script into the install dir that re-downloads the latest release
and stages it before updating the live install (no wizard). Release defaults live in
`appsettings.defaults.json`; your models, accounts, credentials, channels, and other overrides live
in `appsettings.user.json`, which is never owned or overwritten by a release. The first update from
an older AgentFox installation copies the existing `appsettings.json` into the user file.

Before deployment, the new AgentFox binary migrates and validates a temporary copy of the user
configuration. A timestamped copy of the pre-update configuration is retained under `backups/`.
The updater also preserves whether the Trading plugin was installed.

```powershell
# Windows
powershell -File "$HOME\.agentfox\update.ps1"
```

```bash
# Linux / macOS
~/.agentfox/update.sh
```

Re-running the original install one-liner (`irm … | iex` / `curl … | bash`) does the same thing.

Configuration can also be checked or migrated manually:

```text
agentfox config validate
agentfox config migrate
```

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
