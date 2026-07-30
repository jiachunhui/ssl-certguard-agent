#requires -Version 5.1
# ============================================================
# TopSSL-CertGuard-Agent 跨平台打包脚本
# 在 Windows 上：dotnet 交叉编译 -> 打成 certguard-agent-<rid>.tar.gz / .zip
# 用法:
#   .\publish-agent.ps1                          # 自动找 .csproj，打四个 RID
#   .\publish-agent.ps1 -Project .\certguard-agent.csproj -Rids linux-x64
#   .\publish-agent.ps1 -Trim                    # 启用裁剪(体积更小，注意反射风险)
#   .\publish-agent.ps1 -NoSingleFile            # 不用单文件(多文件发布)
#   .\publish-agent.ps1 -FrameworkDependent      # 框架依赖(需目标机已装 .NET 运行时)
#   .\publish-agent.ps1 -Version 1.1.3            # 打版本号 -p:Version=1.1.3 -p:AssemblyVersion=1.1.3.0
#   .\publish-agent.ps1 -Version 1.1.3 -Rids linux-x64   # 只给 linux-x64 打 1.1.3
# 说明:
#   - 确保项目 <AssemblyName>certguard-agent</AssemblyName>,否则可执行文件名对不上 install 脚本
#   - AssemblyVersion 只能4段纯数字,1.1.3 会自动补成 1.1.3.0
# ============================================================
[CmdletBinding()]
param(
    [string]$Project      = "",            # 留空自动探测当前目录第一个 .csproj
    [string]$Configuration = "Release",
    [string]$DistDir      = ".\dist",       # 最终产物目录
    [string]$WorkDir      = ".\.publish",   # 临时发布目录(可删)
    [string]$SevenZip     = "7z",           # 7z 命令名或完整路径，如 C:\Program Files\7-Zip\7z.exe
    [string[]]$Rids       = @("linux-x64", "linux-arm64", "win-x64", "win-arm64"),
    [string]$Version       = "",   # 如 1.1.3 -> -p:Version=1.1.3
    [string]$AssemblyVersion = "", # 留空则由 Version 自动派生(补足4段,如 1.1.3 -> 1.1.3.0)
    [string]$FileVersion   = "",   # 留空则等于 AssemblyVersion
    [switch]$NoSingleFile,
    [switch]$Trim,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"

function W($c,$m){ Write-Host $m -ForegroundColor $c }

# AssemblyVersion 只能是 4 段纯数字(Major.Minor.Build.Revision)
# 例如 1.1.3 -> 1.1.3.0 ; 1.1.3.5 -> 1.1.3.5 ; 1.1.3-beta -> 1.1.3.0
function Get-AssemblyVer([string]$v) {
    $core = ($v -split '-')[0]                       # 去掉预发布标签
    $parts = @($core.Split('.') | Where-Object { $_ -match '^\d+$' })
    while ($parts.Count -lt 4) { $parts += '0' }     # 不足4段补0
    return ($parts[0..3] -join '.')                  # 超过4段只取前4段
}

# ---- 版本属性 ----
$verProps = @()
$vInfo = ""
if ($Version) {
    $av = if ($AssemblyVersion) { $AssemblyVersion } else { Get-AssemblyVer $Version }
    $fv = if ($FileVersion)     { $FileVersion }     else { $av }
    $verProps += "-p:Version=$Version"
    $verProps += "-p:AssemblyVersion=$av"
    $verProps += "-p:FileVersion=$fv"
    $vInfo = "版本: Version=$Version  AssemblyVersion=$av  FileVersion=$fv"
} elseif ($AssemblyVersion) {
    $verProps += "-p:AssemblyVersion=$AssemblyVersion"
    $vInfo = "版本: AssemblyVersion=$AssemblyVersion (Version沿用项目默认)"
} else {
    $vInfo = "版本: 未指定,沿用项目文件/Csproj中的默认版本"
}
W Yellow $vInfo

# ---- 1. 定位项目 ----
if (-not $Project) {
    $found = Get-ChildItem -Path . -Filter "*.csproj" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { W Red "未找到 .csproj，请用 -Project 指定路径" ; exit 1 }
    $Project = $found.FullName
}
$Project = (Resolve-Path $Project).Path
W Cyan "项目: $Project"

# 下载包前缀固定(脚本端写死了 certguard-agent)
$PkgBase = "certguard-agent"
$ExpectedLinuxExe = "certguard-agent"        # 无扩展名
$ExpectedWinExe   = "certguard-agent.exe"

# ---- 2. 工具检查 ----
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { W Red "未找到 dotnet，请装 .NET SDK"; exit 1 }
$useTar = [bool](Get-Command tar -ErrorAction SilentlyContinue)    # Windows 10+ 自带 bsdtar
W Gray ("tar.gz: " + $(if ($useTar) { "tar.exe(系统自带,一行打包)" } else { "$SevenZip 两步(tar->gzip),若也没有则 linux 包无法打" }))
W Gray ("zip:     " + $(if (Get-Command $SevenZip -ErrorAction SilentlyContinue) { "$SevenZip" } else { ".NET 内置 ZipFile(无需 7z)" }))

# ---- 3. 目录准备 ----
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$DistDirAbs = (Resolve-Path $DistDir).Path
Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# ---- 4. 发布属性 ----
$sc = if ($FrameworkDependent) { "false" } else { "true" }   # 自包含(默认)
$sf = if ($NoSingleFile)       { "false" } else { "true" }   # 单文件(默认开)
$tr = if ($Trim)               { "true"  } else { "false" }  # 裁剪(默认关)

if ($FrameworkDependent -and $Trim) {
    W Yellow "警告: 框架依赖 + 裁剪不兼容，已自动关闭裁剪"
    $tr = "false"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---- 5. 逐 RID 发布 + 打包 + 校验 ----
foreach ($rid in $Rids) {
    W Cyan ""
    W Cyan "===== $rid ====="

    $out = Join-Path $WorkDir $rid
    $pubArgs = @("publish", $Project, "-c", $Configuration, "-r", $rid,
                 "-o", $out,
                 "-p:SelfContained=$sc",
                 "-p:PublishSingleFile=$sf",
                 "-p:PublishTrimmed=$tr")
    if ($verProps.Count) { $pubArgs += $verProps }
    # 不在脚本里强制 InvariantGlobalization，沿用项目文件 / install.sh 运行时环境变量(DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1)
    W Gray ("dotnet " + ($pubArgs -join " "))
    & dotnet @pubArgs
    if ($LASTEXITCODE -ne 0) { W Red "$rid 发布失败"; exit 1 }
    W Green "$rid 发布完成 -> $out"

    # 找到主可执行文件
    $isLinux = $rid -like "linux-*"
    $expectedExe = if ($isLinux) { $ExpectedLinuxExe } else { $ExpectedWinExe }
    if (-not (Test-Path (Join-Path $out $expectedExe))) {
        W Yellow ("  [!] 发布目录里没有 $expectedExe —— 请确认项目 <AssemblyName> 或项目名是 certguard-agent，否则 install 脚本会找不到二进制")
    }

    # 打包：必须进入发布目录后打 *，保证文件位于包根目录
    Push-Location $out
    try {
        if ($isLinux) {
            $pkg = Join-Path $DistDirAbs "$PkgBase-$rid.tar.gz"
            if (Test-Path $pkg) { Remove-Item $pkg -Force }
            if ($useTar) {
                & tar -czf "$pkg" *
            } elseif (Get-Command $SevenZip -ErrorAction SilentlyContinue) {
                $tmpTar = Join-Path $WorkDir "$rid.tar"
                & $SevenZip a -ttar "$tmpTar" *
                if ($LASTEXITCODE -ne 0) { W Red "$rid tar 打包失败"; exit 1 }
                & $SevenZip a -tgzip "$pkg" "$tmpTar"
                if ($LASTEXITCODE -ne 0) { W Red "$rid gzip 打包失败"; exit 1 }
                Remove-Item $tmpTar -Force
            } else {
                W Red "${rid}: 既无 tar.exe 又无 $SevenZip,无法打 tar.gz"; exit 1
            }
        } else {
            $pkg = Join-Path $DistDirAbs "$PkgBase-$rid.zip"
            if (Test-Path $pkg) { Remove-Item $pkg -Force }
            $has7z = [bool](Get-Command $SevenZip -ErrorAction SilentlyContinue)
            if ($has7z) {
                & $SevenZip a -tzip "$pkg" *
                if ($LASTEXITCODE -ne 0) { W Red "$rid zip 打包失败"; exit 1 }
            } else {
                W Gray "  (未找到 $SevenZip,改用 .NET 内置 ZipFile 打包)"
                # includeBaseDirectory=$false:保证 certguard-agent.exe 位于 zip 根目录
                try {
                    [IO.Compression.ZipFile]::CreateFromDirectory((Get-Location).Path, $pkg, [IO.Compression.CompressionLevel]::Optimal, $false)
                } catch {
                    W Red "$rid zip 打包失败: $($_.Exception.Message)"; exit 1
                }
            }
        }
    } finally { Pop-Location }

    # ---- 校验：可执行文件必须在包根目录(不能嵌套文件夹) ----
    $entries = @()
    if ($isLinux) {
        if ($useTar) { $entries = @(& tar -tzf "$pkg") } else { $entries = @(& $SevenZip l "$pkg" | Select-String -Pattern $expectedExe) }
    } else {
        $zip = [IO.Compression.ZipFile]::OpenRead($pkg)
        try { $entries = @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }
    }

    $atRoot = $false; $nested = @()
    foreach ($e in $entries) {
        $t = "$e".Trim() -replace '^\.\\','' -replace '^\./',''
        if ($t -ieq $expectedExe) { $atRoot = $true }
        elseif ($t -ilike "*$expectedExe*" -and $t -match '[\\/]') { $nested += $t }
    }
    if ($atRoot) { W Green "  [OK] $expectedExe 位于包根目录" }
    else {
        W Red "  [X] 未见 $expectedExe 位于包根目录 —— install 脚本会失败！"
        if ($nested.Count) { W Yellow ("      仅在子目录发现: " + ($nested -join "; ")) }
        W Yellow "      打包时必须先 cd 进发布目录，再打 * (而不是打整个文件夹)"
    }

    $sizeMB = "{0:N2}" -f ((Get-Item $pkg).Length / 1MB)
    W Green ("  产物: $pkg  ($sizeMB MB)")
}

# ---- 6. 汇总 ----
W Cyan ""
W Cyan "全部完成。$vInfo"
W Cyan "产物目录: $DistDirAbs"
Get-ChildItem $DistDirAbs -File | Sort-Object Name | ForEach-Object {
    "{0,-42} {1,10:N2} MB" -f $_.Name, ($_.Length / 1MB)
}
W Gray ""
W Gray "上传放置(对应脚本里的下载路径):"
W Gray "  {服务器Web根}/agent/certguard-agent-linux-x64.tar.gz"
W Gray "  {服务器Web根}/agent/certguard-agent-linux-arm64.tar.gz"
W Gray "  {服务器Web根}/agent/certguard-agent-win-x64.zip"
W Gray "  {服务器Web根}/agent/certguard-agent-win-arm64.zip"
