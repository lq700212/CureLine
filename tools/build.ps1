<#
.SYNOPSIS
    CureLine 一站式构建 / 测试 / 发布打包脚本（V0.0.111 新增）。

.DESCRIPTION
    【为什么有这个脚本】
    宿主工程对子模块 extern/kaleidoscope 是「引用编译产物」而不是工程引用（net472 老式工程，
    SDK 风格无法直接投影构建）。此前 6 个 csproj 的 HintPath 硬编码 bin\Debug，导致：
      · dotnet build -c Release 也把 Debug 版 Kaleidoscope.dll 拷进输出；
      · 发布包里带着 LicenseGate 的 #if DEBUG「开发自动授权」兜底（KX_DEV_LICENSE），
        现场只要有 Debug DLL 就可能被当成已授权，属发布红线。
    修复方式 = HintPath 改成 bin\$(Configuration)，让配置跟着本工程走。但这样 Release 构建
    就**依赖子模块 bin\Release 存在且新鲜**，必须有个固定入口先把子模块按配置编出来——
    本脚本就是那个唯一入口，避免"忘了先编子模块 → 用了过期 DLL"这类静默事故。

    【它做什么（顺序固定，任何一步失败即中止）】
      1. 探测 VS MSBuild（子模块 net472 老式工程必须用 MSBuild，dotnet build 不支持）；
      2. 按 -Configuration 构建子模块主库 Kaleidoscope（bin\<配置>\Kaleidoscope.dll）；
      3. 按 -Configuration 构建 Kaleidoscope.UI（net8.0-windows，dotnet build）；
      4. 按 -Configuration 构建主解决方案 CureLine.slnx；
      5. 默认跑全量测试（-SkipTests 可跳过；Release 打包不建议跳过）；
      6. Release 断言：所有产物里的 Kaleidoscope.dll 必须不含 KX_DEV_LICENSE（否则报错退出）；
      7. -Pack 时组装发布包 out\CureLine_V<版本>_<日期>\{CureStation,CimGateway}
         （白名单拷贝：exe/dll/json + runtimes + config.sample.ini；pdb/运行态/机密一律不带），
         并对包内 Kaleidoscope.dll 再做一次兜底断言。

.PARAMETER Configuration
    Debug（默认，开发自测，子模块含开发授权兜底）或 Release（发布，兜底整段剔除）。

.PARAMETER SkipTests
    跳过 dotnet test。仅限"只想确认能编译"的临时场景。

.PARAMETER Pack
    组装发布包到 out\。隐含要求 -Configuration Release（Debug 包含开发授权兜底，禁止外发）。

.PARAMETER Version
    发布包版本号（如 V0.0.111）。留空则从 CHANGELOG.md 最新版本标题自动解析。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build.ps1
    开发自测：Debug 构建 + 全量测试。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Configuration Release -Pack
    正式发版：子模块 Release + 主程序 Release + 测试 + 无兜底断言 + 打包。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipTests,

    [switch]$Pack,

    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------- 输出辅助
function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ("===== " + $text + " =====") -ForegroundColor Cyan
}

function Write-Ok([string]$text) {
    Write-Host ("[OK]   " + $text) -ForegroundColor Green
}

function Fail([string]$text) {
    Write-Host ("[FAIL] " + $text) -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- MSBuild 探测
function Find-MSBuild {
    # ① vswhere（VS 官方安装器索引，最可靠）
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $hit = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($hit -and (Test-Path $hit)) { return $hit }
    }
    # ② 本机已知安装路径（AGENTS.md 记录的 VS18 Enterprise 等，按盘符/版本回退）
    $globs = @(
        'D:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe'
    )
    foreach ($g in $globs) {
        $hit = Get-ChildItem $g -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    # ③ 最后兜底 PATH（Developer Command Prompt 环境）
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

# ---------------------------------------------------------------- 开发授权兜底探测
<#
    判据：.NET 程序集 #US 字符串堆是 UTF-16LE，LicenseGate 里 #if DEBUG 段的
    环境变量键名 "KX_DEV_LICENSE" 就是最直接的指纹（Release 编译时整段剔除 = 0 命中）。
    双奇偶偏移各解一次，避免 #US 堆起点奇偶造成的漏检。
    返回 $true = 干净（无兜底）；$false = 含兜底。
#>
function Test-CleanOfDevLicense([string]$path) {
    if (-not (Test-Path $path)) { Fail "断言目标不存在：$path" }
    $bytes = [IO.File]::ReadAllBytes($path)
    foreach ($off in 0, 1) {
        if ($bytes.Length -le $off) { continue }
        $t = [Text.Encoding]::Unicode.GetString($bytes, $off, $bytes.Length - $off)
        if ($t.Contains('KX_DEV_LICENSE')) { return $false }
    }
    return $true
}

# ---------------------------------------------------------------- 0. 前置信息
Write-Host ''
Write-Host 'CureLine 构建脚本' -ForegroundColor White
Write-Host ("  仓库：" + $repoRoot)
Write-Host ("  配置：" + $Configuration)
Write-Host ("  测试：" + $(if ($SkipTests) { '跳过（-SkipTests）' } else { '跑全量' }))
Write-Host ("  打包：" + $(if ($Pack) { '是' } else { '否' }))

if ($Pack -and $Configuration -ne 'Release') {
    Fail '打包必须用 -Configuration Release：Debug 包含开发自动授权兜底，禁止外发'
}

# ---------------------------------------------------------------- 1. MSBuild
Write-Step '1/7 探测 MSBuild'
$msbuild = Find-MSBuild
if (-not $msbuild) {
    Fail '未找到 MSBuild.exe。请安装 Visual Studio（含 .NET 桌面开发工作负载），或在 Developer Command Prompt 里运行本脚本。'
}
Write-Ok $msbuild

# ---------------------------------------------------------------- 2. 子模块主库
Write-Step ("2/7 构建子模块 Kaleidoscope（net472，" + $Configuration + "）")
$subProj = Join-Path $repoRoot 'extern\kaleidoscope\Kaleidoscope\Kaleidoscope.csproj'
& $msbuild $subProj "/p:Configuration=$Configuration" /p:Platform=AnyCPU /t:Build /nologo /v:m
if ($LASTEXITCODE -ne 0) { Fail "子模块主库构建失败（exit=$LASTEXITCODE）" }
$subDll = Join-Path $repoRoot ("extern\kaleidoscope\Kaleidoscope\bin\$Configuration\Kaleidoscope.dll")
if (-not (Test-Path $subDll)) { Fail "子模块主库产物缺失：$subDll" }
Write-Ok ("产出 " + $subDll)

# ---------------------------------------------------------------- 3. 子模块 UI
Write-Step ("3/7 构建子模块 Kaleidoscope.UI（net8.0-windows，" + $Configuration + "）")
$uiProj = Join-Path $repoRoot 'extern\kaleidoscope\Kaleidoscope.UI\Kaleidoscope.UI.csproj'
dotnet build $uiProj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { Fail "子模块 UI 库构建失败（exit=$LASTEXITCODE）" }
Write-Ok ('产出 ' + (Join-Path $repoRoot ("extern\kaleidoscope\Kaleidoscope.UI\bin\$Configuration\net8.0-windows\Kaleidoscope.UI.dll")))

# ---------------------------------------------------------------- 4. 主解决方案
Write-Step ("4/7 构建主解决方案 CureLine.slnx（" + $Configuration + "）")
dotnet build (Join-Path $repoRoot 'CureLine.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { Fail "主解决方案构建失败（exit=$LASTEXITCODE）" }
Write-Ok '主解决方案构建通过'

# ---------------------------------------------------------------- 5. 测试
Write-Step ("5/7 全量测试（" + $Configuration + "）")
if ($SkipTests) {
    Write-Host '  [SKIP] 已指定 -SkipTests' -ForegroundColor Yellow
    if ($Pack) { Write-Host '  [WARN] 打包前跳过测试，请自行确认改动风险' -ForegroundColor Yellow }
} else {
    dotnet test (Join-Path $repoRoot 'tests\CureLine.Tests\CureLine.Tests.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Fail "测试失败（exit=$LASTEXITCODE）" }
    Write-Ok '测试全绿'
}

# ---------------------------------------------------------------- 6. Release 无兜底断言
Write-Step '6/7 发布红线断言（Kaleidoscope.dll 不得含 KX_DEV_LICENSE）'
$probeTargets = @(
    $subDll,
    (Join-Path $repoRoot ("src\CureStation\bin\$Configuration\net8.0-windows\Kaleidoscope.dll")),
    (Join-Path $repoRoot ("src\CimGateway\bin\$Configuration\net8.0-windows\Kaleidoscope.dll"))
)
foreach ($t in $probeTargets) {
    if (-not (Test-Path $t)) { Fail "断言目标不存在：$t" }
    $clean = Test-CleanOfDevLicense $t
    if ($Configuration -eq 'Release') {
        if (-not $clean) { Fail "Release 产物含开发授权兜底（KX_DEV_LICENSE）：$t —— 检查 HintPath 是否被改回 bin\Debug" }
        Write-Ok ("无兜底  " + $t.Replace($repoRoot + '\', ''))
    } else {
        # Debug 含兜底是预期（开发便利），仅提示不作为失败
        $tip = if ($clean) { '无兜底（异常：Debug 应含兜底，请核对子模块 Debug 产物）' } else { '含开发授权兜底（Debug 预期）' }
        Write-Host ("  [INFO] " + $tip + "  " + $t.Replace($repoRoot + '\', ''))
    }
}

# ---------------------------------------------------------------- 7. 打包
if ($Pack) {
    Write-Step '7/7 组装发布包'

    # 版本号：优先参数，否则从 CHANGELOG 最新版本标题解析
    if (-not $Version) {
        $head = Get-Content (Join-Path $repoRoot 'CHANGELOG.md') -Encoding utf8 |
            Where-Object { $_ -match '^##\s*V[\d.]+' } | Select-Object -First 1
        if ($head -and $head -match '(V[\d.]+)') { $Version = $Matches[1] }
    }
    if (-not $Version) { Fail '无法确定版本号：请用 -Version 指定（如 -Version V0.0.111）' }

    $stamp = Get-Date -Format 'yyyyMMdd'
    $packRoot = Join-Path $repoRoot ("out\CureLine_" + $Version + "_" + $stamp)
    if (Test-Path $packRoot) { Remove-Item $packRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $packRoot -Force | Out-Null

    foreach ($prog in @('CureStation', 'CimGateway')) {
        $src = Join-Path $repoRoot ("src\$prog\bin\$Configuration\net8.0-windows")
        if (-not (Test-Path $src)) { Fail "输出目录不存在：$src" }
        $dst = Join-Path $packRoot $prog
        New-Item -ItemType Directory -Path $dst -Force | Out-Null

        # 白名单顶层文件：exe/dll/json（deps+runtimeconfig）；pdb 与运行态文件天然不在白名单
        Get-ChildItem $src -File | Where-Object { $_.Extension -in '.exe', '.dll', '.json' } |
            Copy-Item -Destination $dst -Force
        # 原生依赖（SQLite.Interop / System.IO.Ports 各平台）原样带入
        $rt = Join-Path $src 'runtimes'
        if (Test-Path $rt) { Copy-Item $rt $dst -Recurse -Force }
        # 配置样例（现场首启自愈依赖它，缺了起不来）
        Copy-Item (Join-Path $repoRoot 'config.sample.ini') $dst -Force

        # 包内二次断言：扫全部 DLL（不只 Kaleidoscope——宿主侧将来若误引 Debug-only
        # 成员，字符串同样会进产品 DLL，这里一并拦住）
        foreach ($dll in (Get-ChildItem $dst -Recurse -File -Filter *.dll)) {
            if (-not (Test-CleanOfDevLicense $dll.FullName)) {
                Fail ("发布包含开发授权兜底（KX_DEV_LICENSE）：" + $dll.FullName)
            }
        }
        $files = Get-ChildItem $dst -Recurse -File
        $bytes = ($files | Measure-Object Length -Sum).Sum
        Write-Ok ("$prog  " + $files.Count + " files, " + [Math]::Round($bytes / 1MB, 2) + " MB")
    }

    # 保密自检：包里绝不能出现这些名字
    $forbidden = @('hsl.dat', 'LicenseSignKey.dat', 'LicenseState.ini', 'config.ini')
    $leaks = Get-ChildItem $packRoot -Recurse -File | Where-Object { $forbidden -contains $_.Name }
    if ($leaks) { Fail ("发布包含禁止文件：" + (($leaks | ForEach-Object { $_.FullName }) -join ', ')) }
    Write-Ok '保密自检通过（无 hsl.dat / 私钥 / 授权状态 / 运行态 config.ini）'

    Write-Host ''
    Write-Host ("发布包：" + $packRoot) -ForegroundColor Green
} else {
    Write-Step '7/7 打包'
    Write-Host '  [SKIP] 未指定 -Pack（正式发版请加 -Configuration Release -Pack）' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '全部完成。' -ForegroundColor Green
exit 0
