<#
.SYNOPSIS
    Builds and packs AgentFox exactly the way .github/workflows/release.yml does, but locally.

.DESCRIPTION
    Produces dist\<rid>\ — the same layout the release archive contains:

        AgentFox.exe                  single-file host (wwwroot + ONNX model + native libs embedded)
        appsettings.defaults.json     release-owned defaults, replaced on every update
        appsettings.user.json         YOUR settings (only if -WithUserConfig); never shipped by CI
        plugins\TradingAgent\         plugin DLL + .deps.json + its dependency closure
        plugins\PageAgent\
        plugins\BraveSearch\  ... etc

    Two things are easy to get wrong and are handled here:

      1. A plugin needs its WHOLE published folder, not just the DLL. Without the
         .deps.json and the dependency closure the loader silently skips it.
      2. A plugin also has to be named in the "Modules" config value, or it is
         never loaded even when the folder is perfect.

    The plugin web UIs are separate npm projects that must be built BEFORE dotnet
    publish, because each csproj embeds its wwwroot as an EmbeddedResource. Skipping
    them yields a working backend with no web UI — fine for API testing, not for a release.

.EXAMPLE
    .\pack-local.ps1
    Full release-equivalent build for win-x64.

.EXAMPLE
    .\pack-local.ps1 -SkipUi -WithUserConfig
    Fast backend-only repack that also copies your appsettings.user.json in, for local testing.
#>
[CmdletBinding()]
param(
    [string] $Rid = 'win-x64',
    [string] $Configuration = 'Release',

    # Skip the npm builds. Much faster; the packed exe then serves no web UI unless a
    # previously built wwwroot is still present in the source tree.
    [switch] $SkipUi,

    # Copy appsettings.user.json (credentials) into the packed folder so it runs standalone.
    # CI never does this — the release archive deliberately ships no user config.
    [switch] $WithUserConfig,

    [string] $OutputRoot = "$PSScriptRoot\dist"
)

$ErrorActionPreference = 'Stop'
$out = Join-Path $OutputRoot $Rid

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# Refuse to touch the output while something is using it, rather than deleting half of it and
# failing on the first locked file. A partial clean is worse than no clean: it took out the logs
# and the persisted browser profile on 2026-08-18 while leaving the folder looking packed.
$running = @(Get-Process AgentFox -ErrorAction SilentlyContinue)
if ($running.Count) {
    $pids = $running.Id -join ', '
    throw "AgentFox is running (PID $pids). Stop it before packing - packing over a live install " +
          "deletes its logs and forces a fresh broker login on the next start."
}
$brokerChrome = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
                  Where-Object { $_.CommandLine -like '*session_ahk*' })
if ($brokerChrome.Count) {
    $cpids = $brokerChrome.ProcessId -join ', '
    throw "The broker's Chrome is still open (PID $cpids) and holds the session profile. Close it first."
}

# Runtime state the packed folder ACCUMULATES and must survive a repack. session_ahk is the
# persisted logged-in broker session: deleting it forces a full portal login on the next start,
# and repeated logins are what got account access withdrawn once already (phase-b-runbook.md §0).
$preserve = @('session_ahk', 'logs', 'data', 'sessions', 'appsettings.user.json', 'cron.md')

Step "Cleaning $out (preserving: $($preserve -join ', '))"
if (Test-Path $out) {
    Get-ChildItem $out -Force |
        Where-Object { $preserve -notcontains $_.Name } |
        Remove-Item -Recurse -Force
} 
New-Item -ItemType Directory -Force -Path $out | Out-Null

if (-not $SkipUi) {
    Step 'Building host web UI (src/frontend -> src/Agent/wwwroot)'
    Push-Location "$PSScriptRoot\src\frontend"
    try { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' }
          npm run build; if ($LASTEXITCODE) { throw 'npm run build failed' } }
    finally { Pop-Location }

    Step 'Building Trading plugin UI (src/Plugins/TradingAgent/ui -> .../TradingAgent/wwwroot)'
    Push-Location "$PSScriptRoot\src\Plugins\TradingAgent\ui"
    try { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' }
          npm run build; if ($LASTEXITCODE) { throw 'npm run build failed' } }
    finally { Pop-Location }
} else {
    Step 'Skipping UI builds (-SkipUi)'
}

Step "Publishing AgentFox host ($Rid, single file)"
dotnet publish "$PSScriptRoot\src\Agent\AgentFox.csproj" `
    -c $Configuration -r $Rid `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:UseAppHost=true `
    -o $out
if ($LASTEXITCODE) { throw 'Host publish failed' }

# Each plugin publishes into its own plugins\<Name> folder so the loader discovers it.
$plugins = @(
    @{ Name = 'TradingAgent';      Path = 'src\Plugins\TradingAgent\TradingAgent.csproj' },
    @{ Name = 'PageAgent';         Path = 'src\Plugins\PageAgent\PageAgent.csproj' },
    @{ Name = 'BraveSearch';       Path = 'src\Plugins\AgentFox.BraveSearch\AgentFox.BraveSearch.csproj' },
    @{ Name = 'TavilySearch';      Path = 'src\Plugins\AgentFox.TavilySearch\AgentFox.TavilySearch.csproj' },
    @{ Name = 'DuckDuckGoSearch';  Path = 'src\Plugins\AgentFox.DuckDuckGoSearch\AgentFox.DuckDuckGoSearch.csproj' }
)

foreach ($p in $plugins) {
    Step "Publishing plugin $($p.Name)"
    dotnet publish "$PSScriptRoot\$($p.Path)" `
        -c $Configuration -r $Rid `
        --self-contained false `
        -o "$out\plugins\$($p.Name)"
    if ($LASTEXITCODE) { throw "Plugin $($p.Name) publish failed" }
}

if ($WithUserConfig) {
    $src = "$PSScriptRoot\src\Agent\bin\Debug\net10.0\appsettings.user.json"
    $dest = Join-Path $out 'appsettings.user.json'
    if (Test-Path $dest) {
        Step 'appsettings.user.json already present in the packed folder - left untouched'
    } elseif (Test-Path $src) {
        Copy-Item $src $dest -Force
        Step 'Copied appsettings.user.json (contains credentials - do not share this folder)'
    } else {
        Write-Warning "-WithUserConfig was passed but $src does not exist; packed build has no user config."
    }
}

Step 'Verifying the packed layout'
$exe = Join-Path $out 'AgentFox.exe'
if (-not (Test-Path $exe)) { throw "AgentFox.exe missing from $out" }
foreach ($p in $plugins) {
    $dir = "$out\plugins\$($p.Name)"
    $dll = Get-ChildItem $dir -Filter *.dll -ErrorAction SilentlyContinue | Select-Object -First 1
    $deps = Get-ChildItem $dir -Filter *.deps.json -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dll)  { throw "Plugin $($p.Name): no DLL in $dir" }
    # The loader needs this file; its absence is the classic silent "plugin does nothing" failure.
    if (-not $deps) { throw "Plugin $($p.Name): no .deps.json in $dir - the loader will skip it" }
    "{0,-18} {1,4} files" -f $p.Name, (Get-ChildItem $dir -File).Count | Write-Host
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "`nPacked to $out" -ForegroundColor Green
Write-Host "AgentFox.exe  ${size} MB"
Write-Host @"

Run it:
    cd "$out"
    .\AgentFox.exe

Plugins load only when named in the "Modules" config value, e.g.:
    `$env:Modules = "web,trading-agent"
"@
