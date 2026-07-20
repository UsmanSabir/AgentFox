param(
    [string]$RepoUrl = $env:AGENTFOX_REPO_URL,
    [string]$Branch = $env:AGENTFOX_BRANCH,
    [string]$InstallDir = $env:AGENTFOX_INSTALL_DIR,
    [string]$BinaryUrl = $env:AGENTFOX_BINARY_URL,
    [switch]$BuildFromSource,
    [switch]$SkipService,
    [switch]$NoTrading,
    [switch]$SkipOnboarding
)

$ErrorActionPreference = 'Stop'

# Allow the one-liner (irm | iex) to opt out via env vars.
if (-not $NoTrading -and $env:AGENTFOX_NO_TRADING -eq '1') { $NoTrading = $true }
if (-not $SkipOnboarding -and $env:AGENTFOX_SKIP_ONBOARDING -eq '1') { $SkipOnboarding = $true }

if (-not $RepoUrl) { $RepoUrl = 'https://github.com/UsmanSabir/AgentFox.git' }

function Write-Info([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-ArchSuffix {
    switch -Regex ($env:PROCESSOR_ARCHITECTURE) {
        'ARM64' { 'win-arm64' }
        'AMD64' { 'win-x64' }
        default { 'win-x64' }
    }
}

function Get-DefaultBinaryUrl([string]$rid) {
    if ($RepoUrl -match 'github\.com[:/]+([^/]+)/([^/.]+)') {
        $owner = $Matches[1]
        $repo = $Matches[2]
        return "https://github.com/$owner/$repo/releases/latest/download/agentfox-$rid.zip"
    }
    return $null
}

function Install-Prebuilt([string]$rid, [string]$destination) {
    $url = if ($BinaryUrl) { $BinaryUrl } else { Get-DefaultBinaryUrl $rid }
    if (-not $url) {
        Write-Info 'Could not derive a prebuilt binary URL; building from source.'
        return $false
    }

    Write-Info "Looking for a prebuilt binary at $url"
    $archive = Join-Path $env:TEMP "agentfox-$rid.zip"
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $archive -ErrorAction Stop
    }
    catch {
        Write-Info "No prebuilt binary available ($($_.Exception.Message)). Building from source instead."
        return $false
    }

    Write-Info 'Downloaded prebuilt binary. Extracting ...'
    $extractDir = Join-Path $env:TEMP "agentfox-prebuilt-$rid"
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
    Expand-Archive -Path $archive -DestinationPath $extractDir -Force

    $binary = Get-ChildItem -Path $extractDir -Recurse -Include 'AgentFox.exe', 'AgentFox.dll' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $binary) {
        Write-Info 'Archive did not contain an AgentFox binary; building from source instead.'
        return $false
    }

    Copy-Item (Join-Path $binary.Directory.FullName '*') $destination -Recurse -Force
    Write-Info 'Prebuilt binary installed.'
    return $true
}

function Ensure-Git {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        Write-Info "Found git $((& git --version) | Select-Object -First 1)"
        return
    }

    Write-Info 'Installing Git ...'
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id Git.Git -e --source winget
    }
    elseif (Get-Command choco -ErrorAction SilentlyContinue) {
        choco install git -y
    }
    else {
        throw 'Git is required to clone the repository. Install Git and re-run the installer.'
    }
}

function Ensure-Dotnet {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        $version = (& dotnet --version).Trim()
        if ($version -match '^10\.') {
            Write-Info "Found dotnet $version"
            return
        }
    }

    $targetDir = if ($InstallDir) { $InstallDir } else { Join-Path $HOME '.dotnet' }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    Write-Info 'Installing .NET SDK 10.0 ...'
    $tempScript = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $tempScript
    & $tempScript -Channel '10.0' -InstallDir $targetDir -Quality 'GA'

    $env:PATH = "$targetDir;$env:PATH"
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath -notlike "*$targetDir*") {
        [Environment]::SetEnvironmentVariable('Path', "$targetDir;$userPath", 'User')
    }

    $env:DOTNET_ROOT = $targetDir
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $targetDir, 'User')
    Write-Info 'dotnet SDK installed.'
}

function Resolve-SourceRoot {
    $candidate = $PSScriptRoot
    if (Test-Path (Join-Path $candidate 'src/Agent/AgentFox.csproj')) {
        return $candidate
    }

    if (-not $RepoUrl) {
        throw "Could not find the AgentFox source tree and no -RepoUrl was supplied. Pass -RepoUrl https://github.com/<owner>/AgentFox.git"
    }

    $workRoot = Join-Path $env:TEMP 'agentfox-source'
    if (Test-Path $workRoot) {
        Remove-Item $workRoot -Recurse -Force
    }

    Write-Info "Cloning AgentFox from $RepoUrl"
    if ($Branch) {
        git clone --branch $Branch --depth 1 $RepoUrl $workRoot
    }
    else {
        git clone --depth 1 $RepoUrl $workRoot
    }

    return $workRoot
}

function Add-ToUserPath([string]$dir) {
    # Persist to the user PATH (survives reboots / new terminals) and update the
    # current session so `agentfox` resolves without opening a new shell.
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @()
    if ($userPath) { $entries = $userPath -split ';' | Where-Object { $_ -ne '' } }
    if ($entries -notcontains $dir) {
        $newPath = (@($dir) + $entries) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Write-Info "Added $dir to your user PATH."
    }
    else {
        Write-Info "$dir is already on your user PATH."
    }
    if (($env:PATH -split ';') -notcontains $dir) {
        $env:PATH = "$dir;$env:PATH"
    }
}

$resolvedInstallDir = if ($InstallDir) { $InstallDir } else { Join-Path $HOME '.agentfox' }
$resolvedInstallDir = [System.IO.Path]::GetFullPath($resolvedInstallDir)
New-Item -ItemType Directory -Path $resolvedInstallDir -Force | Out-Null

# The framework-dependent binary needs the .NET runtime whether it was prebuilt or built here.
Ensure-Dotnet
$rid = Get-ArchSuffix

$installed = $false
if (-not $BuildFromSource) {
    $installed = Install-Prebuilt $rid $resolvedInstallDir
}

if (-not $installed) {
    Ensure-Git
    $sourceRoot = Resolve-SourceRoot
    $projectPath = Join-Path $sourceRoot 'src/Agent/AgentFox.csproj'

    if (-not (Test-Path $projectPath)) {
        throw "Could not find $projectPath"
    }

    Write-Info "Publishing AgentFox to $resolvedInstallDir"
    & dotnet publish $projectPath -c Release -r $rid --self-contained false -p:PublishSingleFile=false -p:UseAppHost=true --verbosity minimal

    $publishDir = Join-Path $sourceRoot ("src/Agent/bin/Release/net10.0/{0}/publish" -f $rid)
    if (-not (Test-Path $publishDir)) {
        throw "Publish output was not created at $publishDir"
    }

    Copy-Item "$publishDir/*" $resolvedInstallDir -Recurse -Force

    # Publish the Trading plugin into plugins/ so the runtime plugin loader discovers it.
    $pluginProject = Join-Path $sourceRoot 'src/Plugins/TradingAgent/TradingAgent.csproj'
    if (-not $NoTrading -and (Test-Path $pluginProject)) {
        $pluginDir = Join-Path $resolvedInstallDir 'plugins/TradingAgent'
        Write-Info 'Publishing Trading plugin into plugins/TradingAgent'
        & dotnet publish $pluginProject -c Release -r $rid --self-contained false -o $pluginDir --verbosity minimal
    }

    # Publish the default bundled plugins into plugins/ so the runtime loader discovers them.
    # Each lands in its own plugins/<Name> folder with its .deps.json + dependencies. They are
    # enabled via the "Modules" list in appsettings.json; the key-only search plugins
    # (Brave/Tavily) stay inert until their API key is configured.
    $defaultPlugins = @(
        @{ Project = 'src/Plugins/PageAgent/PageAgent.csproj';                       Dir = 'PageAgent' }
        @{ Project = 'src/Plugins/AgentFox.BraveSearch/AgentFox.BraveSearch.csproj'; Dir = 'BraveSearch' }
        @{ Project = 'src/Plugins/AgentFox.TavilySearch/AgentFox.TavilySearch.csproj'; Dir = 'TavilySearch' }
        @{ Project = 'src/Plugins/AgentFox.DuckDuckGoSearch/AgentFox.DuckDuckGoSearch.csproj'; Dir = 'DuckDuckGoSearch' }
    )
    foreach ($p in $defaultPlugins) {
        $proj = Join-Path $sourceRoot $p.Project
        if (Test-Path $proj) {
            $dir = Join-Path $resolvedInstallDir ('plugins/' + $p.Dir)
            Write-Info ("Publishing default plugin into plugins/" + $p.Dir)
            & dotnet publish $proj -c Release -r $rid --self-contained false -o $dir --verbosity minimal
        }
    }
}

# The prebuilt archive bundles the Trading plugin; strip it for a core-only install.
$tradingPluginDir = Join-Path $resolvedInstallDir 'plugins/TradingAgent'
if ($NoTrading -and (Test-Path $tradingPluginDir)) {
    Write-Info 'Removing Trading plugin (-NoTrading)'
    Remove-Item $tradingPluginDir -Recurse -Force
}

$launcher = Join-Path $resolvedInstallDir 'agentfox.cmd'
@"
@echo off
setlocal
set "AGENTFOX_HOME=%~dp0"
if exist "%AGENTFOX_HOME%\AgentFox.exe" (
  "%AGENTFOX_HOME%\AgentFox.exe" %*
) else (
  dotnet "%AGENTFOX_HOME%\AgentFox.dll" %*
)
"@ | Set-Content -Path $launcher -Encoding Ascii

# ── PATH registration ──────────────────────────────────────────────────────────
# Put the install dir on PATH so users run `agentfox` from anywhere, not just from
# inside the install folder. agentfox.cmd is resolved because .CMD is in PATHEXT.
Add-ToUserPath $resolvedInstallDir

# ── Uninstaller ──────────────────────────────────────────────────────────────
# Written into the install dir. Removes the service, the PATH entry, then the folder.
$uninstaller = Join-Path $resolvedInstallDir 'uninstall.ps1'
@'
# Uninstall AgentFox: remove the service, drop the PATH entry, delete this folder.
$ErrorActionPreference = 'SilentlyContinue'
$dir = $PSScriptRoot
Write-Host "Uninstalling AgentFox from $dir" -ForegroundColor Cyan

$launcher = Join-Path $dir 'agentfox.cmd'
if (Test-Path $launcher) {
    Write-Host 'Removing the AgentFox service (if installed) ...'
    & $launcher --uninstall-service 2>$null
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath) {
    $kept = ($userPath -split ';' | Where-Object { $_ -and $_ -ne $dir }) -join ';'
    [Environment]::SetEnvironmentVariable('Path', $kept, 'User')
    Write-Host 'Removed AgentFox from your user PATH.'
}

# Leave the install dir before deleting it so the folder is not the working directory.
Set-Location $HOME
try {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop
    Write-Host 'AgentFox removed. Open a new terminal for the PATH change to take effect.' -ForegroundColor Green
}
catch {
    Write-Host "Could not delete $dir (a process may still be running). Stop AgentFox and delete it manually." -ForegroundColor Yellow
}
'@ | Set-Content -Path $uninstaller -Encoding UTF8

# ── Updater ──────────────────────────────────────────────────────────────────
# Re-runs the installer against this same install dir (prebuilt download, no wizard).
$updateBranch = if ($Branch) { $Branch } else { 'main' }
$rawInstallUrl = $null
if ($RepoUrl -match 'github\.com[:/]+([^/]+)/([^/.]+)') {
    $rawInstallUrl = "https://raw.githubusercontent.com/$($Matches[1])/$($Matches[2])/$updateBranch/install.ps1"
}
$updater = Join-Path $resolvedInstallDir 'update.ps1'
@"
# Update AgentFox in place to the latest release.
`$env:AGENTFOX_INSTALL_DIR = `$PSScriptRoot
`$env:AGENTFOX_SKIP_ONBOARDING = '1'
Write-Host 'Updating AgentFox to the latest release ...' -ForegroundColor Cyan
irm '$rawInstallUrl' | iex
"@ | Set-Content -Path $updater -Encoding UTF8

Write-Host ''
Write-Host 'AgentFox installed successfully.' -ForegroundColor Green
Write-Host "Install directory: $resolvedInstallDir" -ForegroundColor Green
Write-Host ''
if ($NoTrading) {
    Write-Host 'Trading plugin NOT installed (-NoTrading). Re-run the installer without -NoTrading to add it.' -ForegroundColor Yellow
}
else {
    Write-Host 'Trading plugin is enabled for LIVE auto-execution (AutoExecute=true, ExecutionMode=BoundedAuto).' -ForegroundColor Red
    Write-Host 'The setup wizard below can switch it to Paper mode and collect AHK credentials, PIN and allowed symbols.' -ForegroundColor Red
}

# ── Onboarding ────────────────────────────────────────────────────────────────
# The wizard configures the LLM, plugin credentials, and (optionally) the Windows
# service. It offers to start the agent when done — if it installs and starts the
# service, the gateway is already listening and no second instance is launched.
if (-not $SkipOnboarding -and [Environment]::UserInteractive) {
    Write-Host ''
    Write-Info 'Starting the AgentFox setup wizard (re-run any time with: agentfox --onboarding) ...'
    & $launcher --onboarding
}
else {
    Write-Host ''
    Write-Host 'Next steps:' -ForegroundColor Yellow
    Write-Host '  agentfox --onboarding    # interactive setup (LLM, plugin credentials, service)' -ForegroundColor Yellow
    Write-Host '  agentfox                 # start the agent (web UI on port 8080 by default)' -ForegroundColor Yellow
    if (-not $SkipService) {
        Write-Host '  agentfox --install-service    # run AgentFox as a Windows service' -ForegroundColor DarkYellow
    }
}

Write-Host ''
Write-Host "'agentfox' is now on your PATH — open a NEW terminal, then run it from anywhere." -ForegroundColor Green
Write-Host 'Manage this install:' -ForegroundColor Yellow
Write-Host "  powershell -File `"$resolvedInstallDir\update.ps1`"       # update to the latest release" -ForegroundColor Yellow
Write-Host "  powershell -File `"$resolvedInstallDir\uninstall.ps1`"    # remove AgentFox (service + PATH + files)" -ForegroundColor Yellow
