<#
  Diag-Onnx.ps1
  Why does onnxruntime.dll fail to load? Reads the PE import table, checks every
  imported symbol against the DLL actually installed on this machine, and reports
  CPU features. Read-only. Windows PowerShell 5.1 compatible. ASCII only.

  Usage: .\Diag-Onnx.ps1 -Path <full path to onnxruntime.dll>

  # AgentFox must run once first, to re-extract what -Apply deleted
$dll = (Get-ChildItem "$env:TEMP\.net\AgentFox" -Recurse -Filter onnxruntime.dll | Select-Object -First 1).FullName
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Diag-Onnx.ps1 -Path $dll


#>
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Path)

Add-Type -Namespace Native -Name Diag -MemberDefinition @'
[DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)]
public static extern IntPtr LoadLibraryW(string path);
[DllImport("kernel32", SetLastError=true, CharSet=CharSet.Ansi)]
public static extern IntPtr GetProcAddress(IntPtr h, string name);
[DllImport("kernel32")]
public static extern bool IsProcessorFeaturePresent(uint feature);
'@

if (-not (Test-Path $Path)) { Write-Host "Not found: $Path" -ForegroundColor Red; return }

Write-Host ''
Write-Host '== Target ==' -ForegroundColor Cyan
$fi = Get-Item $Path
Write-Host ("  {0}" -f $fi.FullName)
Write-Host ("  {0:N0} bytes   FileVersion {1}   ProductVersion {2}" -f `
    $fi.Length, $fi.VersionInfo.FileVersion, $fi.VersionInfo.ProductVersion)

# ---- CPU features ------------------------------------------------------------
Write-Host ''
Write-Host '== CPU ==' -ForegroundColor Cyan
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
Write-Host ("  {0}" -f $cpu.Name)
$feats = @{ 'SSE3'=13; 'SSSE3'=36; 'SSE4.1'=37; 'SSE4.2'=38; 'AVX'=39; 'AVX2'=40; 'AVX512F'=41 }
foreach ($k in @('SSE3','SSSE3','SSE4.1','SSE4.2','AVX','AVX2','AVX512F')) {
    $has = [Native.Diag]::IsProcessorFeaturePresent($feats[$k])
    if ($has) { $col = 'Green'; $txt = 'yes' } else { $col = 'Yellow'; $txt = 'NO' }
    Write-Host ("  {0,-9} {1}" -f $k, $txt) -ForegroundColor $col
}

# ---- Installed VC++ redistributables ----------------------------------------
Write-Host ''
Write-Host '== Installed VC++ redistributable (registry) ==' -ForegroundColor Cyan
$any = $false
foreach ($arch in @('x64','x86')) {
    $k = 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\' + $arch
    $v = Get-ItemProperty -Path $k -ErrorAction SilentlyContinue
    if ($v) { $any = $true; Write-Host ("  {0,-4} Version {1}  Installed={2}" -f $arch, $v.Version, $v.Installed) }
}
if (-not $any) { Write-Host '  none registered' -ForegroundColor Yellow }

# ---- PE import table ---------------------------------------------------------
Write-Host ''
Write-Host '== Imports ==' -ForegroundColor Cyan
$b = [System.IO.File]::ReadAllBytes($Path)
$peOff = [BitConverter]::ToInt32($b, 0x3C)
if ([BitConverter]::ToUInt32($b, $peOff) -ne 0x00004550) { Write-Host '  not a PE file' -ForegroundColor Red; return }
$coff      = $peOff + 4
$numSec    = [BitConverter]::ToUInt16($b, $coff + 2)
$optSize   = [BitConverter]::ToUInt16($b, $coff + 16)
$opt       = $coff + 20
$magic     = [BitConverter]::ToUInt16($b, $opt)
$isPe32Plus = ($magic -eq 0x20B)
if ($isPe32Plus) { $ddOff = $opt + 112 } else { $ddOff = $opt + 96 }
Write-Host ("  machine: {0}" -f $(if ($isPe32Plus) { 'x64 (PE32+)' } else { 'x86 (PE32)' }))
$impRva = [BitConverter]::ToUInt32($b, $ddOff + 8)

$secOff = $opt + $optSize
$secs = @()
for ($i = 0; $i -lt $numSec; $i++) {
    $s = $secOff + ($i * 40)
    $secs += New-Object psobject -Property @{
        VA   = [BitConverter]::ToUInt32($b, $s + 12)
        VSz  = [BitConverter]::ToUInt32($b, $s + 8)
        Raw  = [BitConverter]::ToUInt32($b, $s + 20)
        RSz  = [BitConverter]::ToUInt32($b, $s + 16)
    }
}
function Rva2Off($rva) {
    foreach ($s in $secs) {
        $span = [Math]::Max($s.VSz, $s.RSz)
        if ($rva -ge $s.VA -and $rva -lt ($s.VA + $span)) { return ($rva - $s.VA + $s.Raw) }
    }
    return -1
}
function ReadAsciiz($off) {
    $sb = New-Object System.Text.StringBuilder
    while ($off -lt $b.Length -and $b[$off] -ne 0) { [void]$sb.Append([char]$b[$off]); $off++ }
    $sb.ToString()
}

$descOff = Rva2Off $impRva
$deps = @()
while ($descOff -ge 0) {
    $oft  = [BitConverter]::ToUInt32($b, $descOff)
    $nRva = [BitConverter]::ToUInt32($b, $descOff + 12)
    $ft   = [BitConverter]::ToUInt32($b, $descOff + 16)
    if ($oft -eq 0 -and $nRva -eq 0 -and $ft -eq 0) { break }
    $name = ReadAsciiz (Rva2Off $nRva)
    $thunkRva = $(if ($oft -ne 0) { $oft } else { $ft })
    $names = @()
    $t = Rva2Off $thunkRva
    if ($t -ge 0) {
        while ($true) {
            if ($isPe32Plus) {
                $val = [BitConverter]::ToUInt64($b, $t); $t += 8
                if ($val -eq 0) { break }
                $isOrd = (($val -band 0x8000000000000000) -ne 0)
                $rva = [uint32]($val -band 0x7FFFFFFF)
            } else {
                $val = [BitConverter]::ToUInt32($b, $t); $t += 4
                if ($val -eq 0) { break }
                $isOrd = (($val -band 0x80000000) -ne 0)
                $rva = [uint32]($val -band 0x7FFFFFFF)
            }
            if (-not $isOrd) { $names += (ReadAsciiz ((Rva2Off $rva) + 2)) }
        }
    }
    $deps += New-Object psobject -Property @{ Dll = $name; Names = $names }
    $descOff += 20
}

$sys = [System.IO.Path]::Combine($env:SystemRoot, 'System32')
$problems = @()
foreach ($d in ($deps | Sort-Object Dll)) {
    $p = [System.IO.Path]::Combine($sys, $d.Dll)
    if (Test-Path $p) { $ver = (Get-Item $p).VersionInfo.FileVersion } else { $ver = '(not in System32)' }
    Write-Host ("  {0,-34} {1,4} imports   {2}" -f $d.Dll, $d.Names.Count, $ver)

    $h = [Native.Diag]::LoadLibraryW($d.Dll)
    if ($h -eq [IntPtr]::Zero) {
        Write-Host ("      -> CANNOT LOAD this dependency (Win32 {0})" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error()) -ForegroundColor Red
        $problems += ("dependency " + $d.Dll + " will not load")
        continue
    }
    $missing = @()
    foreach ($n in $d.Names) {
        if ([Native.Diag]::GetProcAddress($h, $n) -eq [IntPtr]::Zero) { $missing += $n }
    }
    if ($missing.Count -gt 0) {
        Write-Host ("      -> {0} MISSING export(s) in the installed copy:" -f $missing.Count) -ForegroundColor Red
        foreach ($m in ($missing | Select-Object -First 8)) { Write-Host ("         {0}" -f $m) -ForegroundColor Red }
        if ($missing.Count -gt 8) { Write-Host ("         ... and {0} more" -f ($missing.Count - 8)) -ForegroundColor Red }
        $problems += ($d.Dll + ' is too old: ' + $missing.Count + ' missing export(s)')
    }
}

# ---- Verdict -----------------------------------------------------------------
Write-Host ''
Write-Host '== Verdict ==' -ForegroundColor Cyan
$h = [Native.Diag]::LoadLibraryW($Path)
if ($h -ne [IntPtr]::Zero) { Write-Host '  It loads now.' -ForegroundColor Green; return }
$err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Host ("  LoadLibrary failed, Win32 {0}" -f $err) -ForegroundColor Red
if ($problems.Count -gt 0) {
    foreach ($p in $problems) { Write-Host ("  - " + $p) -ForegroundColor Yellow }
    Write-Host '  FIX: install VC++ 2015-2022 x64: https://aka.ms/vs/17/release/vc_redist.x64.exe' -ForegroundColor Yellow
} else {
    Write-Host '  Every import resolves, so this is not a missing dependency.' -ForegroundColor Yellow
    Write-Host '  1114 = the DLL initialization routine itself failed. Check the CPU rows above' -ForegroundColor Yellow
    Write-Host '  and send this whole output back.' -ForegroundColor Yellow
}
