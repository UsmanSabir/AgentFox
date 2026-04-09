# Onboarding Guide
.\AgentFox.exe --onboarding
.\AgentFox.exe --onboarding --provider OpenAI --model gpt-4 --apikey sk-xxxx

# Install as service
## CLI Flags
dotnet run -- --install-service

- Check status
dotnet run -- --service-status

- Start/stop/restart
dotnet run -- --start-service
dotnet run -- --stop-service
dotnet run -- --restart-service

- Uninstall
dotnet run -- --uninstall-service
.\AgentFox.exe uninstall-service
- 
## Interactive REPL:
> install-service
✓ Service 'AgentFox' installed successfully on port 8080

> service-status
✓ Status of 'AgentFox'
Output: SERVICE_NAME: AgentFox
        TYPE               : 10  WIN32_OWN_PROCESS
        STATE              : 4  RUNNING
        WIN32_EXIT_CODE    : 0  (0x0)
        ...

> service-config
✓ Service Configuration
  Service Name:        AgentFox
  Display Name:        AgentFox - Multi-Agent AI Framework
  Port:                8080
  Run as Admin:        True
  Auto-Start:          True
  Heartbeat Interval:  300s
  Platform:            Windows


# Configuration
"Services": {
  "Enabled": true,
  "ServiceName": "AgentFox",
  "DisplayName": "AgentFox - Multi-Agent AI Framework",
  "Port": 8080,
  "RunAsAdmin": true,
  "AutoStart": true,
  "LogPath": "{workspace}/logs/service.log",
  "HeartbeatIntervalSeconds": 300,
  "EnvironmentVariables": {}
}

Platform-Specific Notes
Platform	Installation	Service File	Command
Windows	Built-in, no dependencies	Registry	sc.exe
Linux	Requires sudo	/etc/systemd/system/	systemctl
macOS	Per-user or system-wide	~/Library/LaunchAgents/ or /Library/LaunchDaemons/	launchctl
What Happens Wh
