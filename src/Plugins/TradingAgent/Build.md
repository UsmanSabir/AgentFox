# Build
```
# Stop any running AgentFox instance first.

Push-Location .\src\Plugins\TradingAgent\ui

# Needed on first setup or after package-lock.json changes
npm ci

npm run check
npm run build

Pop-Location

 Push-Location .\src\frontend\
 npm run build

Pop-Location


# Builds the host and TradingAgent.dll.
# The plugin project copies its output into the host's plugins folder.
dotnet build .\src\AgentFox.sln -c Debug

# Use the already-built assemblies
dotnet run --project .\src\Agent\AgentFox.csproj -c Debug --no-build
```

The build chain is:
```
TradingAgent/ui
    npm run build
        ↓
TradingAgent/wwwroot
    dotnet build
        ↓ embedded into
Agent/bin/Debug/net10.0/plugins/TradingAgent.dll
```
