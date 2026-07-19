param(
    [string]$RepoUrl = $env:AGENTFOX_REPO_URL,
    [string]$Branch = $env:AGENTFOX_BRANCH,
    [string]$InstallDir = $env:AGENTFOX_INSTALL_DIR,
    [string]$BinaryUrl = $env:AGENTFOX_BINARY_URL,
    [switch]$BuildFromSource,
    [switch]$SkipService,
    [switch]$NoTrading
)

$ErrorActionPreference = 'Stop'

# Allow the one-liner (irm | iex) to opt out of the Trading plugin via env var.
if (-not $NoTrading -and $env:AGENTFOX_NO_TRADING -eq '1') { $NoTrading = $true }

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

Write-Host ''
Write-Host 'AgentFox installed successfully.' -ForegroundColor Green
Write-Host "Install directory: $resolvedInstallDir" -ForegroundColor Green
Write-Host 'Run it with:' -ForegroundColor Yellow
Write-Host "  $resolvedInstallDir\agentfox.cmd" -ForegroundColor Yellow
Write-Host ''
if ($NoTrading) {
    Write-Host 'Trading plugin NOT installed (-NoTrading). Re-run the installer without -NoTrading to add it.' -ForegroundColor Yellow
}
else {
    Write-Host 'Trading plugin is enabled for LIVE auto-execution (AutoExecute=true, ExecutionMode=BoundedAuto).' -ForegroundColor Red
    Write-Host 'Configure Plugins.TradingAgent.AllowedSymbols and Ahk credentials in appsettings.json before sending signals.' -ForegroundColor Red
}

if (-not $SkipService) {
    Write-Host 'To install as a Windows service later, run:' -ForegroundColor DarkYellow
    Write-Host "  $resolvedInstallDir\agentfox.cmd --install-service" -ForegroundColor DarkYellow
}
