<#
  Fix-AgentFoxEmbedding.ps1
  Diagnoses "Local embedding model unavailable - NativeMethods threw an exception".
  Run ELEVATED on the host. Report-only by default; pass -Apply to repair.
  Compatible with Windows PowerShell 5.1 and PowerShell 7. ASCII only.

  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Fix-AgentFoxEmbedding.ps1
# then, only if it found something removable:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Fix-AgentFoxEmbedding.ps1 -Apply

#>
[CmdletBinding()]
param([switch]$Apply, [string]$ServiceName = 'AgentFox', [string]$ExePath)

Add-Type -Namespace Native -Name Loader -MemberDefinition @'
[DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)]
public static extern IntPtr LoadLibraryW(string path);
[DllImport("kernel32", SetLastError=true)]
public static extern bool FreeLibrary(IntPtr h);
'@

function Test-Native([string]$path) {
    if (-not (Test-Path $path)) {
        return New-Object psobject -Property @{ Path=$path; Size=0; Verdict='MISSING'; Err=$null }
    }
    $len = (Get-Item $path).Length
    $isPe = $false
    if ($len -ge 2) {
        $fs = [System.IO.File]::OpenRead($path)
        try {
            $b = New-Object byte[] 2
            if ($fs.Read($b, 0, 2) -eq 2) { $isPe = ($b[0] -eq 0x4D -and $b[1] -eq 0x5A) }
        } finally { $fs.Close() }
    }
    if ($len -lt 1MB -or -not $isPe) {
        return New-Object psobject -Property @{ Path=$path; Size=$len; Verdict='CORRUPT'; Err='truncated or not a PE image' }
    }
    $h = [Native.Loader]::LoadLibraryW($path)
    if ($h -ne [IntPtr]::Zero) {
        [void][Native.Loader]::FreeLibrary($h)
        return New-Object psobject -Property @{ Path=$path; Size=$len; Verdict='OK'; Err=$null }
    }
    $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    switch ($e) {
        126     { $why = 'ERROR_MOD_NOT_FOUND - a DEPENDENCY is missing. Almost always the VC++ 2015-2022 x64 redistributable.' }
        1114    { $why = 'ERROR_DLL_INIT_FAILED - the library loaded and bound, but its initialization failed. MEASURED cause: a Visual C++ runtime older than the toolset ONNX Runtime was built with. Imports still resolve by name, so nothing reports a missing dependency.' }
        193     { $why = 'ERROR_BAD_EXE_FORMAT - corrupt file, or wrong architecture (x86/arm64 vs x64).' }
        5       { $why = 'ERROR_ACCESS_DENIED - blocked by an ACL or by antivirus.' }
        default { $why = "Win32 error $e" }
    }
    # 193 is a statement about the BYTES (corrupt image or wrong architecture), so a cache delete
    # is the right repair for it. Every other code is about the environment the DLL loads into,
    # where re-extracting an identical copy accomplishes nothing.
    if ($e -eq 193) { $verdict = 'CORRUPT' } else { $verdict = 'WILL NOT LOAD' }
    New-Object psobject -Property @{ Path=$path; Size=$len; Verdict=$verdict; Err=$why }
}

Write-Host ''
Write-Host '== PowerShell / OS ==' -ForegroundColor Cyan
Write-Host ("  PSVersion {0}   OS {1}" -f $PSVersionTable.PSVersion, [Environment]::OSVersion.Version)

# ---- 1. VC++ runtime ---------------------------------------------------------
Write-Host ''
Write-Host '== Visual C++ runtime ==' -ForegroundColor Cyan
$sys = [System.IO.Path]::Combine($env:SystemRoot, 'System32')
$vcMissing = @()
foreach ($n in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
    $p = [System.IO.Path]::Combine($sys, $n)
    if (Test-Path $p) { Write-Host ("  {0,-22} {1}" -f $n, (Get-Item $p).VersionInfo.FileVersion) }
    else { Write-Host ("  {0,-22} MISSING" -f $n) -ForegroundColor Red; $vcMissing += $n }
}
if ($vcMissing.Count -gt 0) {
    Write-Host '  Install VC++ 2015-2022 x64: https://aka.ms/vs/17/release/vc_redist.x64.exe' -ForegroundColor Yellow
}

# ---- 2. Locate the install ---------------------------------------------------
Write-Host ''
Write-Host '== AgentFox install ==' -ForegroundColor Cyan
$svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
$exe = $ExePath
if ($svc) {
    Write-Host ("  service '{0}': {1}, runs as {2}" -f $ServiceName, $svc.State, $svc.StartName)
    if (-not $exe) {
        $pn = $svc.PathName
        $i = $pn.ToLower().IndexOf('agentfox.exe')
        if ($i -ge 0) { $exe = $pn.Substring(0, $i + 12).Trim('"').Trim() }
    }
} else {
    Write-Host ("  no '{0}' service found - assuming an interactive install" -f $ServiceName)
}
if (-not $exe) { $c = Get-Command AgentFox -ErrorAction SilentlyContinue; if ($c) { $exe = $c.Source } }
if (-not $exe) { $exe = [System.IO.Path]::Combine($env:USERPROFILE, '.agentfox', 'AgentFox.exe') }
Write-Host ("  exe: {0}" -f $exe)
if (-not (Test-Path $exe)) {
    Write-Host '  NOT FOUND. Re-run with -ExePath "<full path to AgentFox.exe>"' -ForegroundColor Red
    return
}
$dir   = Split-Path $exe -Parent
$loose = [System.IO.Path]::Combine($dir, 'runtimes', 'win-x64', 'native', 'onnxruntime.dll')
$singleFile = -not (Test-Path $loose)
if ($singleFile) { Write-Host '  layout: single-file bundle (natives self-extract under <TEMP>)' }
else             { Write-Host '  layout: framework-dependent (natives in the runtimes folder)' }

# ---- 3. Every copy of onnxruntime.dll that could be in play ------------------
Write-Host ''
Write-Host '== onnxruntime.dll ==' -ForegroundColor Cyan
$marker = [System.IO.Path]::Combine('.net', 'AgentFox')
$cands = New-Object System.Collections.ArrayList
if (-not $singleFile) { [void]$cands.Add($loose) }

$tempRoots = New-Object System.Collections.ArrayList
[void]$tempRoots.Add([System.IO.Path]::Combine($env:SystemRoot, 'Temp'))
[void]$tempRoots.Add($env:TEMP)
$usersDir = [System.IO.Path]::Combine($env:SystemDrive + [System.IO.Path]::DirectorySeparatorChar, 'Users')
foreach ($u in (Get-ChildItem -LiteralPath $usersDir -Directory -ErrorAction SilentlyContinue)) {
    [void]$tempRoots.Add([System.IO.Path]::Combine($u.FullName, 'AppData', 'Local', 'Temp'))
}
# TEMP is often redirected off the system drive on a server. Cover <drive>\Temp everywhere.
foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
    if ($d.Root) { [void]$tempRoots.Add([System.IO.Path]::Combine($d.Root, 'Temp')) }
}

foreach ($r in ($tempRoots | Sort-Object -Unique)) {
    $root = [System.IO.Path]::Combine($r, $marker)
    if (Test-Path $root -ErrorAction SilentlyContinue) {
        foreach ($f in (Get-ChildItem -LiteralPath $root -Recurse -Filter 'onnxruntime.dll' -ErrorAction SilentlyContinue)) {
            [void]$cands.Add($f.FullName)
        }
    }
}

$bad = New-Object System.Collections.ArrayList
$all = @($cands | Where-Object { $_ } | Sort-Object -Unique)
foreach ($c in $all) {
    $r = Test-Native $c
    if ($r.Verdict -eq 'OK') { $col = 'Green' } else { $col = 'Red' }
    Write-Host ("  [{0,-14}] {1,14:N0} bytes  {2}" -f $r.Verdict, $r.Size, $r.Path) -ForegroundColor $col
    if ($r.Err) { Write-Host ("      -> {0}" -f $r.Err) -ForegroundColor Yellow }
    if ($r.Verdict -ne 'OK') { [void]$bad.Add($r) }
}
if ($all.Count -eq 0) {
    Write-Host '  No copy found. If this is a single-file install, the app has not yet run' -ForegroundColor Yellow
    Write-Host '  under the account that owns the extraction folder.' -ForegroundColor Yellow
}

# ---- 4. Repair ---------------------------------------------------------------
Write-Host ''
Write-Host '== Repair ==' -ForegroundColor Cyan
if ($bad.Count -eq 0) {
    Write-Host '  Nothing to repair - every copy loads.'
    if ($vcMissing.Count -gt 0) { Write-Host '  But the VC++ runtime above is incomplete. Fix that first.' -ForegroundColor Yellow }
    return
}
# A DLL that is present and well-formed but will not load is never repaired by deleting the
# extraction cache: the bytes were correct and the re-extracted copy is byte-identical. Handle
# EVERY such verdict here, not just the missing-dependency one - a 1114 fell through to the
# delete branch below and destroyed a good cache for nothing (measured 2026-09-18).
$wontLoad = @($bad | Where-Object { $_.Verdict -eq 'WILL NOT LOAD' })
if ($wontLoad.Count -gt 0) {
    Write-Host '  DIAGNOSIS: the DLL is intact. The problem is the environment it loads into.' -ForegroundColor Yellow
    foreach ($w in $wontLoad) { Write-Host ("    " + $w.Err) -ForegroundColor Yellow }
    Write-Host '  Install VC++ 2015-2022 x64, then restart:' -ForegroundColor Yellow
    Write-Host '    https://aka.ms/vs/17/release/vc_redist.x64.exe' -ForegroundColor Yellow
    Write-Host '  Deleting the extraction cache will NOT help here, so this script will not do it.' -ForegroundColor Yellow
    Write-Host '  Run Diag-Onnx.ps1 against the same path if it is still refused afterwards.' -ForegroundColor Yellow
    return
}
$extractBad = @($bad | Where-Object { $_.Path -like ('*' + $marker + '*') })
if ($extractBad.Count -gt 0) {
    $roots = @()
    foreach ($b in $extractBad) {
        $i = $b.Path.IndexOf($marker)
        if ($i -ge 0) { $roots += $b.Path.Substring(0, $i + $marker.Length) }
    }
    foreach ($root in ($roots | Sort-Object -Unique)) {
        if (-not $Apply) {
            Write-Host ("  WOULD remove {0}   (re-run with -Apply)" -f $root)
            continue
        }
        if ($svc -and $svc.State -eq 'Running') {
            Write-Host ("  stopping {0}" -f $ServiceName)
            Stop-Service $ServiceName -Force
            Start-Sleep -Seconds 3
        }
        Write-Host ("  removing {0}" -f $root)
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    if ($Apply) {
        if ($svc) { Write-Host ("  starting {0}" -f $ServiceName); Start-Service $ServiceName }
        Write-Host '  Done. The bundle re-extracts a fresh copy on next start.'
    }
}
$looseBad = @($bad | Where-Object { $_.Path -eq $loose })
if ($looseBad.Count -gt 0) {
    Write-Host ("  {0} is bad and is NOT self-healing - redeploy AgentFox." -f $loose) -ForegroundColor Yellow
}
