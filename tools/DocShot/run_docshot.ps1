# =====================================================================
# run_docshot.ps1 -- doc-screenshot harness runner (unattended)
# Steps: build solution -> build self -> assemble runner dir (bin deps +
# harness output) -> prepare seed dirs -> run DocShot.exe.
# Outputs: docs/images/*.png + tools/DocShot/logs/DocShot.metrics.txt
# Why runner dir: harness references built DLLs; runtime needs transitive
# deps (Newtonsoft, SQLite, Assets icons) copied from both program bins.
# =====================================================================

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $MyInvocation.MyCommand.Path)
$root = (Resolve-Path "$PSScriptRoot\..\..").Path

Write-Host "=== [1/5] build solution ==="
dotnet build "$root\CureLine.slnx" --nologo
if ($LASTEXITCODE -ne 0) { throw "solution build failed" }

Write-Host "=== [1.5] generate Cure resource dictionary (from source App.xaml) ==="
$srcApp = "$root\src\CureStation\App.xaml"
$genPath = Join-Path $PSScriptRoot 'Cure.App.Resources.xaml'
$text = Get-Content $srcApp -Raw -Encoding UTF8
$sHead = '<Application.Resources>'
$sTail = '</Application.Resources>'
$s = $text.IndexOf($sHead, [System.StringComparison]::Ordinal)
$t = $text.IndexOf($sTail, [System.StringComparison]::Ordinal)
if (($s -lt 0) -or ($t -le $s)) { throw "cannot locate Resources block in App.xaml" }
$inner = $text.Substring($s + $sHead.Length, $t - ($s + $sHead.Length)).Trim()
$dict = "<ResourceDictionary xmlns=`"http://schemas.microsoft.com/winfx/2006/xaml/presentation`" xmlns:x=`"http://schemas.microsoft.com/winfx/2006/xaml`">`n$inner`n</ResourceDictionary>"
Set-Content -Path $genPath -Value $dict -Encoding UTF8
Write-Host "generated: $genPath"

Write-Host "=== [2/5] build harness ==="
dotnet build (Join-Path $PSScriptRoot 'DocShot.csproj') --nologo
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

Write-Host "=== [3/5] assemble runner ==="
$runner = Join-Path $PSScriptRoot 'runner'
if (Test-Path $runner) { Remove-Item $runner -Recurse -Force }
New-Item $runner -ItemType Directory -Force | Out-Null

$hOut = Join-Path $PSScriptRoot 'bin\Debug\net8.0-windows'
Copy-Item "$hOut\*" $runner -Recurse -Force

foreach ($bin in @(
    "$root\src\CureStation\bin\Debug\net8.0-windows",
    "$root\src\CimGateway\bin\Debug\net8.0-windows")) {
    if (Test-Path $bin) { Copy-Item "$bin\*" $runner -Recurse -Force }
    else { Write-Warning "missing program bin: $bin" }
}

Write-Host "=== [4/5] prepare seed dirs ==="
foreach ($s in 'CureStation', 'CimGateway') {
    foreach ($d in 'config', 'DataBase', 'log') {
        New-Item (Join-Path $PSScriptRoot "seed\$s\$d") -ItemType Directory -Force | Out-Null
    }
}

Write-Host "=== [5/5] run shots ==="
$log = Join-Path $PSScriptRoot 'logs\DocShot.console.log'
New-Item (Join-Path $PSScriptRoot 'logs') -ItemType Directory -Force | Out-Null
$mFile = Join-Path $PSScriptRoot 'logs\DocShot.metrics.txt'
if (Test-Path $mFile) { Remove-Item $mFile -Force }
$proc = Start-Process -FilePath (Join-Path $runner 'DocShot.exe') -WorkingDirectory $runner `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err" -Wait -PassThru
Write-Host "exit=$($proc.ExitCode); log: $log and logs\DocShot.metrics.txt"

if ($proc.ExitCode -ne 0) { throw "DocShot has FAIL entries (see metrics log)" }
Write-Host "ALL PASS"
