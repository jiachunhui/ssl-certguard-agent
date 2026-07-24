<# ============================================================
# TopSSL-CertGuard-Agent Windows 一键安装脚本
 用法: powershell -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; iex ([System.Text.Encoding]::UTF8.GetString((New-Object System.Net.WebClient).DownloadData('https://localhost:5002/agent/install.ps1'))); Install-CertGuardAgent -Token ct_reg_xxxxxx"
 ============================================================ #>

# 兼容 iex 远程执行：param() 在 iex 中不生效，改用直接赋值
$Token = ""
$InstallDir = "$env:ProgramFiles\TopSSL-CertGuard-Agent"
$DataDir = "$env:ProgramData\TopSSL-CertGuard-Agent"
$Server = "http://localhost:5003"

 $Script:Version = "1.0.2"
$Script:ServiceName = "TopSSLCertGuardAgent"

function Write-Step {
    param([string]$Message, [int]$Step, [int]$Total)
    Write-Host "[$Step/$Total] $Message"
}

function Install-CertGuardAgent {
    param(
        [string]$Token = $script:Token,
        [string]$InstallDir = $script:InstallDir,
        [string]$DataDir = $script:DataDir,
        [string]$Server = $script:Server
    )

    if ([string]::IsNullOrEmpty($Token)) {
        Write-Host "错误: 需要 --token / -Token 参数" -ForegroundColor Red
        Write-Host "用法: Install-CertGuardAgent -Token ct_reg_xxxxxx [-Server http://your-platform:port]"
        Write-Host "或: powershell ... install.ps1 -Token ct_reg_xxxxxx -Server http://your-platform:port"
        exit 1
    }

    Write-Host "================================================"
    Write-Host "  TopSSL-CertGuard-Agent 安装程序"
    Write-Host "================================================"
    Write-Host ""
    Write-Host "安装目录: $InstallDir"
    Write-Host "数据目录: $DataDir"
    Write-Host "平台地址: $Server"
    Write-Host ""

    # 检查管理员权限
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "错误: 安装 TopSSL-CertGuard-Agent 需要管理员权限，请以管理员身份运行 PowerShell。" -ForegroundColor Red
        Write-Host "     右键点击 PowerShell → 以管理员身份运行" -ForegroundColor Yellow
        exit 1
    }

    $totalSteps = 7
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    # ── 0. 停止旧服务（提前到解压前）─────────────────
    Write-Step -Message "停止旧服务..." -Step 1 -Total $totalSteps

    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Host "  服务已存在，正在停止并删除旧服务..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        # 确保所有 MMC 窗口关闭，防止 SCM 锁
        Get-Process -Name mmc -ErrorAction SilentlyContinue | Stop-Process -Force

        sc.exe delete $ServiceName 2>&1 | Out-Null

        # 等待 SCM 释放删除标记
        Write-Host "  等待服务标记清除..."
        $waited = 0
        while ($waited -lt 60) {
            $query = sc.exe query $ServiceName 2>&1 | Out-String
            if ($query -match "1060|未安装|not been installed") { break }
            Start-Sleep -Seconds 3
            $waited += 3
        }
        Write-Host "  旧服务已清理"
    }
    else {
        Write-Host "  无需清理"
    }

    # ── 1. 创建目录 ──────────────────────────────────
    Write-Step -Message "创建目录..." -Step 2 -Total $totalSteps
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

    # ── 1b. 设置目录权限（允许服务写入配置文件）───────
    Write-Step -Message "设置目录权限..." -Step 3 -Total $totalSteps
    try {
        $acl = Get-Acl -Path $InstallDir
        $permission = "NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow"
        $accessRule = New-Object System.Security.AccessControl.FileSystemAccessRule $permission
        $acl.SetAccessRule($accessRule)
        Set-Acl -Path $InstallDir -AclObject $acl
        Write-Host "  安装目录权限已设置（SYSTEM: 完全控制）"

        $acl2 = Get-Acl -Path $DataDir
        $accessRule2 = New-Object System.Security.AccessControl.FileSystemAccessRule $permission
        $acl2.SetAccessRule($accessRule2)
        Set-Acl -Path $DataDir -AclObject $acl2
        Write-Host "  数据目录权限已设置（SYSTEM: 完全控制）"
    }
    catch {
        Write-Host "  设置权限失败: $_" -ForegroundColor Yellow
    }

    # ── 2. 下载 Agent 二进制文件 ────────────────────
    Write-Step -Message "下载 Agent 二进制文件..." -Step 4 -Total $totalSteps

    $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        "X64"   { "win-x64" }
        "Arm64" { "win-arm64" }
        default { $null }
    }
    if (-not $arch) {
        Write-Host "不支持的架构: $([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)" -ForegroundColor Red
        exit 1
    }

    $downloadUrl = "https://localhost:5002/agent/certguard-agent-$arch.zip"
    $zipPath = Join-Path $env:TEMP "certguard-agent.zip"

    Write-Host "  下载: $downloadUrl"
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
    }
    catch {
        Write-Host "  下载失败: $_" -ForegroundColor Red
        exit 1
    }


    # 解压前先清理旧文件（此时服务已停止，文件不再被锁定）
    if (Test-Path $InstallDir) {
        Get-ChildItem -Path $InstallDir -Recurse | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    }

    Write-Host "  解压到 $InstallDir"
    Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
    Remove-Item -Path $zipPath -Force
    Write-Host "  下载完成" -ForegroundColor Green

    # ── 3. 首次注册 ──────────────────────────────────
    Write-Step -Message "注册 Agent..." -Step 5 -Total $totalSteps

    $agentExe = Join-Path $InstallDir "certguard-agent.exe"
    if (-not (Test-Path $agentExe)) {
        Write-Host "  错误: 未找到 certguard-agent.exe，请检查安装包内容。" -ForegroundColor Red
        exit 1
    }

    Write-Host "  执行首次注册..."
    $proc = Start-Process -FilePath $agentExe -ArgumentList "--token `"$Token`" --server `"$Server`" --data-dir `"$DataDir`" --register-only" -PassThru -NoNewWindow -Wait
    if ($proc.ExitCode -ne 0) {
        Write-Host "  注册失败（退出码: $($proc.ExitCode)），请检查 Token 是否有效。" -ForegroundColor Yellow
    }
    else {
        Write-Host "  注册完成" -ForegroundColor Green

        # 注册成功后，如果 DataDir 下有 agent.json 且安装目录还没有，复制一份到安装目录
        $dataDirConfig = "$DataDir\agent.json"
        $installDirConfig = "$InstallDir\agent.json"
        if ((Test-Path $dataDirConfig) -and !(Test-Path $installDirConfig)) {
            Copy-Item -Path $dataDirConfig -Destination $installDirConfig -Force
            Write-Host "  配置文件已复制到: $installDirConfig" -ForegroundColor Green
        }
    }

    # ── 4. 创建并启动 Windows 服务 ──────────────────
    Write-Step -Message "创建并启动 Windows 服务..." -Step 6 -Total $totalSteps

    $binaryPath = "`"$agentExe`" --data-dir `"$DataDir`""
    $displayName = "TopSSL.cn CertGuard Agent - SSL证书自动部署守护进程"
    $description = "TopSSL.cn (https://topssl.cn) — SSL certificate auto-deployment daemon (Nginx/Apache/IIS)"

    Write-Host "  创建服务: $ServiceName"

    try {
        $newServiceParams = @{
            Name           = $ServiceName
            BinaryPathName = $binaryPath
            DisplayName    = $displayName
            StartupType    = "Automatic"
            Description    = $description
        }
        New-Service @newServiceParams
        Write-Host "  服务创建完成" -ForegroundColor Green
    }
    catch {
        Write-Host "  创建服务失败: $_" -ForegroundColor Red
        exit 1
    }

    Write-Host "  启动服务..."

    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        Start-Sleep -Seconds 3
    }
    catch {
        Write-Host "  服务启动失败，错误: $_" -ForegroundColor Yellow
    }
    # ── 5. 添加系统 PATH ─────────────────────────
            # $─$─ 5. 添加系统 PATH $─$─$─$─$─$─$─$─$─$─$─$─$─$─$─$─$─$─$─$─$─
    Write-Step -Message "添加系统 PATH..." -Step 7 -Total 7
    try {
        $machinePath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::Machine)
        $installDirNormalized = $InstallDir.TrimEnd('\')
        $paths = $machinePath -split ';' | ForEach-Object { $_.TrimEnd('\') }
        if ($InstallDir -notin $paths -and $installDirNormalized -notin $paths) {
            [Environment]::SetEnvironmentVariable("Path", "$machinePath;$InstallDir", [EnvironmentVariableTarget]::Machine)
            Write-Host "  已添加到系统 PATH" -ForegroundColor Green
            # 刷新当前进程的 PATH
            $env:Path = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::Machine)
        }
        else {
            Write-Host "  已在 PATH 中，跳过" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "  添加 PATH 失败: $_" -ForegroundColor Yellow
    }

# ── 验证 ──────────────────────────────────────
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Host ""
        Write-Host "✅ TopSSL-CertGuard-Agent 安装成功！" -ForegroundColor Green
        Write-Host "   程序目录: $InstallDir"
        Write-Host "   配置文件: $InstallDir\agent.json"
        Write-Host "   数据目录: $DataDir"
        Write-Host ""
        Write-Host "   服务管理:"
        Write-Host "     Get-Service $ServiceName"
        Write-Host "     Restart-Service $ServiceName"
        Write-Host ""
        Write-Host "   查看日志:"
        Write-Host "     Get-WinEvent -LogName Application | Where-Object { `$_.Source -eq 'CertGuardAgent' }"
        Write-Host ""
        Write-Host "   修改 $InstallDir\agent.json 后请重启服务生效。"
    }
    else {
        Write-Host ""
        Write-Host "⚠️  服务未运行，请检查:" -ForegroundColor Yellow
        Write-Host "   1. 手动运行: & `"$agentExe`" --data-dir `"$DataDir`""
        Write-Host "   2. 事件日志: Get-WinEvent -LogName Application -MaxEvents 20 | Where-Object { `$_.Message -like '*CertGuard*' }"
        Write-Host "   3. 配置文件: $InstallDir\agent.json"
    }
}

# 仅在作为脚本文件直接运行时自动调用（iex 远程执行时跳过）
if ($MyInvocation.InvocationName) {
    Install-CertGuardAgent
}
