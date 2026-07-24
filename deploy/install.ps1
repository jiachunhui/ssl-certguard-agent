<# ============================================================
# TopSSL-CertGuard-Agent Windows 一键安装脚本
# 用法: powershell -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; iex ([System.Text.Encoding]::UTF8.GetString((New-Object System.Net.WebClient).DownloadData('https://localhost:5002/agent/install.ps1'))); Install-CertGuardAgent -Token ct_reg_xxxxxx"
# ============================================================ #>

# 兼容 iex 远程执行：param() 在 iex 中不生效，改用直接赋值
$Token        = ""
$InstallDir   = "$env:ProgramFiles\TopSSL-CertGuard-Agent"
$DataDir      = "$env:ProgramData\TopSSL-CertGuard-Agent"
$Server       = "http://localhost:5003"

$Script:Version     = "1.0.2"
$Script:ServiceName = "TopSSLCertGuardAgent"

# ───────────────────────────────────────────────
# 输出格式化辅助函数（统一规范）
# ───────────────────────────────────────────────
function Write-Banner {
    param([string]$Title)
    $line = "=" * 64
    Write-Host ""
    Write-Host $line -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host $line -ForegroundColor Cyan
    Write-Host ""
}

function Write-Step {
    param([Parameter(Mandatory)][string]$Message, [Parameter(Mandatory)][int]$Step, [Parameter(Mandatory)][int]$Total)
    Write-Host ""
    Write-Host ("[{0}/{1}] {2}" -f $Step, $Total, $Message) -ForegroundColor Cyan
}

function Write-SubInfo {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Gray
}

function Write-OK {
    param([string]$Message)
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "  [!] $Message" -ForegroundColor Yellow
}

function Write-Err {
    param([string]$Message)
    Write-Host "  [X] $Message" -ForegroundColor Red
}

function Format-Bytes {
    param([long]$Bytes)
    if     ($Bytes -ge 1GB) { return ("{0:N2} GB" -f ($Bytes / 1GB)) }
    elseif ($Bytes -ge 1MB) { return ("{0:N2} MB" -f ($Bytes / 1MB)) }
    elseif ($Bytes -ge 1KB) { return ("{0:N2} KB" -f ($Bytes / 1KB)) }
    else                    { return "$Bytes B" }
}

# 流式下载 + 进度条（百分比 / 已下载·总大小 / 速率 / ETA）+ 里程碑兜底
function Show-DownloadProgress {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$OutFile
    )

    # 关键：强制开启进度条（屏蔽外部 $ProgressPreference=SilentlyContinue 的影响）
    $savedProgressPref = $ProgressPreference
    $ProgressPreference = 'Continue'

    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.UserAgent         = "TopSSL-CertGuard-Agent-Installer/$Script:Version"
    $request.AllowAutoRedirect = $true

    try {
        $response = $request.GetResponse()
    }
    catch {
        $ProgressPreference = $savedProgressPref
        throw $_
    }

    $total            = $response.ContentLength     # 可能 -1（chunked 未知大小）
    $reader           = $response.GetResponseStream()
    $writer           = [System.IO.File]::Create($OutFile)
    $buffer           = New-Object byte[] 81920
    $received         = 0L
    $lastBytes        = 0L
    $lastTime         = [DateTime]::UtcNow
    $lastMilestonePct = -1
    $lastMilestoneTime = [DateTime]::UtcNow

    try {
        while (($read = $reader.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $writer.Write($buffer, 0, $read)
            $received += $read

            $now     = [DateTime]::UtcNow
            $elapsed = ($now - $lastTime).TotalSeconds
            if ($elapsed -ge 0.4) {
                $delta   = $received - $lastBytes
                $speed   = if ($delta -gt 0) { [long]($delta / $elapsed) } else { 0 }
                $percent = if ($total -gt 0) { [int]($received * 100 / $total) } else { 0 }

                $recvStr  = Format-Bytes $received
                $totalStr = if ($total -gt 0) { Format-Bytes $total } else { "未知" }
                $speedStr = if ($speed -gt 0) { "$(Format-Bytes $speed)/s" } else { "计算中..." }
                $etaStr   = "未知"
                if ($speed -gt 0 -and $total -gt 0) {
                    $remain  = $total - $received
                    $etaSec  = [int]([Math]::Max(0, $remain) / $speed)
                    $etaStr  = "{0:mm\:ss}" -f [TimeSpan]::FromSeconds($etaSec)
                }

                $status = "$percent% | $recvStr / $totalStr | $speedStr | ETA: $etaStr"
                Write-Progress -Activity "下载 Agent 二进制文件" -Status $status -PercentComplete $(if ($total -gt 0) { $percent } else { 100 })

                # 里程碑文字输出（双保险：Write-Progress 不渲染时也能看到推进）
                $shouldMilestone = $false
                if ($total -gt 0) {
                    $milestone = [int]($percent / 10) * 10
                    if ($milestone -gt $lastMilestonePct -and $milestone -le 90) {
                        $lastMilestonePct = $milestone
                        $shouldMilestone  = $true
                    }
                }
                elseif (($now - $lastMilestoneTime).TotalSeconds -ge 3) {
                    # 大小未知时，每 3 秒输出一次已下载量
                    $lastMilestoneTime = $now
                    $shouldMilestone   = $true
                }
                if ($shouldMilestone) {
                    if ($total -gt 0) {
                        Write-SubInfo "下载中: $recvStr / $totalStr ($percent%)"
                    } else {
                        Write-SubInfo "下载中: 已接收 $recvStr（服务器未返回总大小）"
                    }
                }

                $lastBytes = $received
                $lastTime  = $now
            }
        }
        Write-Progress -Activity "下载 Agent 二进制文件" -Completed
        Write-SubInfo "下载完成，共 $(Format-Bytes $received)"
    }
    finally {
        $writer.Close()
        $reader.Close()
        $response.Close()
        $ProgressPreference = $savedProgressPref
    }
}

# ───────────────────────────────────────────────
# 主安装流程
# ───────────────────────────────────────────────
function Install-CertGuardAgent {
    param(
        [string]$Token      = $script:Token,
        [string]$InstallDir = $script:InstallDir,
        [string]$DataDir    = $script:DataDir,
        [string]$Server     = $script:Server
    )

    if ([string]::IsNullOrEmpty($Token)) {
        Write-Host "[X] 错误: 需要 --token / -Token 参数" -ForegroundColor Red
        Write-Host "    用法: Install-CertGuardAgent -Token ct_reg_xxxxxx [-Server http://your-platform:port]"
        Write-Host "    或:   powershell ... install.ps1 -Token ct_reg_xxxxxx -Server http://your-platform:port"
        exit 1
    }

    Write-Banner -Title "TopSSL-CertGuard-Agent 安装程序  v$Script:Version"
    Write-SubInfo "安装目录: $InstallDir"
    Write-SubInfo "数据目录: $DataDir"
    Write-SubInfo "平台地址: $Server"
    Write-SubInfo "日志目录: $DataDir\logs"

    # 检查管理员权限
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Err "安装 TopSSL-CertGuard-Agent 需要管理员权限，请以管理员身份运行 PowerShell。"
        Write-Warn "右键点击 PowerShell → 以管理员身份运行"
        exit 1
    }

    $totalSteps = 7
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    # ── 0. 停止旧服务 ─────────────────────────────────
    Write-Step -Message "停止旧服务..." -Step 1 -Total $totalSteps

    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-SubInfo "服务已存在，正在停止并删除旧服务..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        # 确保所有 MMC 窗口关闭，防止 SCM 锁
        Get-Process -Name mmc -ErrorAction SilentlyContinue | Stop-Process -Force

        sc.exe delete $ServiceName 2>&1 | Out-Null

        # 等待 SCM 释放删除标记
        Write-SubInfo "等待服务标记清除..."
        $waited = 0
        while ($waited -lt 60) {
            $query = sc.exe query $ServiceName 2>&1 | Out-String
            if ($query -match "1060|未安装|not been installed") { break }
            Start-Sleep -Seconds 3
            $waited += 3
        }
        Write-OK "旧服务已清理"
    }
    else {
        Write-SubInfo "无需清理（服务不存在）"
    }

    # ── 1. 创建目录 ───────────────────────────────────
    Write-Step -Message "创建目录..." -Step 2 -Total $totalSteps
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    New-Item -ItemType Directory -Force -Path $DataDir    | Out-Null
    Write-OK "安装目录与数据目录已创建"

    # ── 2. 设置目录权限 ───────────────────────────────
    Write-Step -Message "设置目录权限..." -Step 3 -Total $totalSteps
    try {
        $acl = Get-Acl -Path $InstallDir
        $permission = "NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow"
        $accessRule = New-Object System.Security.AccessControl.FileSystemAccessRule $permission
        $acl.SetAccessRule($accessRule)
        Set-Acl -Path $InstallDir -AclObject $acl
        Write-OK "安装目录权限已设置（SYSTEM: 完全控制）"

        $acl2 = Get-Acl -Path $DataDir
        $accessRule2 = New-Object System.Security.AccessControl.FileSystemAccessRule $permission
        $acl2.SetAccessRule($accessRule2)
        Set-Acl -Path $DataDir -AclObject $acl2
        Write-OK "数据目录权限已设置（SYSTEM: 完全控制）"
    }
    catch {
        Write-Warn "设置权限失败: $_"
    }

    # ── 3. 下载 Agent 二进制文件 ──────────────────────
    Write-Step -Message "下载 Agent 二进制文件..." -Step 4 -Total $totalSteps

    $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        "X64"   { "win-x64" }
        "Arm64" { "win-arm64" }
        default { $null }
    }
    if (-not $arch) {
        Write-Err "不支持的架构: $([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)"
        exit 1
    }

    $baseUrl      = $Server.TrimEnd('/')
    $downloadUrl  = "$baseUrl/agent/certguard-agent-$arch.zip"
    $zipPath      = Join-Path $env:TEMP "certguard-agent.zip"

    Write-SubInfo "下载地址: $downloadUrl"
    Write-SubInfo "开始下载，请稍候..."
    try {
        # 注意：此处不再设置 $ProgressPreference='SilentlyContinue'
        # 该设置会同时屏蔽 Write-Progress，导致自定义进度条也不显示
        Show-DownloadProgress -Url $downloadUrl -OutFile $zipPath
    }
    catch {
        Write-Progress -Activity "下载 Agent 二进制文件" -Completed
        Write-Err "下载失败: $_"
        exit 1
    }
    Write-OK "下载完成"

    # 解压前先清理旧文件（此时服务已停止，文件不再被锁定）
    if (Test-Path $InstallDir) {
        Get-ChildItem -Path $InstallDir -Recurse | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    }

    Write-SubInfo "解压到 $InstallDir"
    Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
    Remove-Item -Path $zipPath -Force
    Write-OK "解压完成"

    # ── 4. 首次注册 ───────────────────────────────────
    Write-Step -Message "注册 Agent..." -Step 5 -Total $totalSteps

    $agentExe = Join-Path $InstallDir "certguard-agent.exe"
    if (-not (Test-Path $agentExe)) {
        Write-Err "未找到 certguard-agent.exe，请检查安装包内容。"
        exit 1
    }

    Write-SubInfo "执行首次注册..."
    $proc = Start-Process -FilePath $agentExe -ArgumentList "--token `"$Token`" --server `"$Server`" --data-dir `"$DataDir`" --register-only" -PassThru -NoNewWindow -Wait
    if ($proc.ExitCode -ne 0) {
        Write-Warn "注册失败（退出码: $($proc.ExitCode)），请检查 Token 是否有效。"
    }
    else {
        Write-OK "注册完成"

        # 注册成功后，如果 DataDir 下有 agent.json 且安装目录还没有，复制一份到安装目录
        $dataDirConfig    = "$DataDir\agent.json"
        $installDirConfig = "$InstallDir\agent.json"
        if ((Test-Path $dataDirConfig) -and !(Test-Path $installDirConfig)) {
            Copy-Item -Path $dataDirConfig -Destination $installDirConfig -Force
            Write-OK "配置文件已复制到: $installDirConfig"
        }
    }

    # ── 5. 创建并启动 Windows 服务 ────────────────────
    Write-Step -Message "创建并启动 Windows 服务..." -Step 6 -Total $totalSteps

    $binaryPath  = "`"$agentExe`" --data-dir `"$DataDir`""
    $displayName = "TopSSL.cn CertGuard Agent - SSL证书自动部署守护进程"
    $description = "TopSSL.cn (https://topssl.cn) — SSL certificate auto-deployment daemon (Nginx/Apache/IIS)"

    Write-SubInfo "创建服务: $ServiceName"

    try {
        $newServiceParams = @{
            Name           = $ServiceName
            BinaryPathName = $binaryPath
            DisplayName    = $displayName
            StartupType    = "Automatic"
            Description    = $description
        }
        New-Service @newServiceParams
        Write-OK "服务创建完成"
    }
    catch {
        Write-Err "创建服务失败: $_"
        exit 1
    }

    Write-SubInfo "启动服务..."
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        Start-Sleep -Seconds 3
        Write-OK "服务已启动"
    }
    catch {
        Write-Warn "服务启动失败，错误: $_"
    }

    # ── 6. 添加系统 PATH ──────────────────────────────
    Write-Step -Message "添加系统 PATH..." -Step 7 -Total $totalSteps
    try {
        $machinePath           = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::Machine)
        $installDirNormalized  = $InstallDir.TrimEnd('\')
        $paths = $machinePath -split ';' | ForEach-Object { $_.TrimEnd('\') }
        if ($InstallDir -notin $paths -and $installDirNormalized -notin $paths) {
            [Environment]::SetEnvironmentVariable("Path", "$machinePath;$InstallDir", [EnvironmentVariableTarget]::Machine)
            # 刷新当前进程的 PATH
            $env:Path = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::Machine)
            Write-OK "已添加到系统 PATH"
        }
        else {
            Write-OK "已在 PATH 中，跳过"
        }
    }
    catch {
        Write-Warn "添加 PATH 失败: $_"
    }

    # ── 验证与最终提示 ────────────────────────────────
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        $logDir = "$DataDir\logs"
        $line = "=" * 64
        Write-Host ""
        Write-Host $line -ForegroundColor Green
        Write-Host "  OK  TopSSL-CertGuard-Agent 安装成功！" -ForegroundColor Green
        Write-Host $line -ForegroundColor Green

        Write-Host ""
        Write-Host "  安装信息" -ForegroundColor Cyan
        Write-Host "    程序目录 : $InstallDir"
        Write-Host "    可执行文件: $agentExe"
        Write-Host "    配置文件 : $InstallDir\agent.json"
        Write-Host "    数据目录 : $DataDir"
        Write-Host "    日志目录 : $logDir"
        Write-Host "    服务名称 : $ServiceName"
        Write-Host "    命令行名 : certguard-agent （已加入系统 PATH，新开 PowerShell 窗口可直接使用）" -ForegroundColor DarkGray

        Write-Host ""
        Write-Host "  常用操作（复制对应命令到 PowerShell 执行）" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "    --- 服务管理 ---" -ForegroundColor DarkGray
        Write-Host "    [查看服务状态]   Get-Service $ServiceName" -ForegroundColor White
        Write-Host "    [启动服务]       Start-Service $ServiceName" -ForegroundColor White
        Write-Host "    [停止服务]       Stop-Service $ServiceName" -ForegroundColor White
        Write-Host "    [重启服务]       Restart-Service $ServiceName" -ForegroundColor White
        Write-Host ""
        Write-Host "    --- 程序命令（短形式 / 完整路径形式 均可） ---" -ForegroundColor DarkGray
        Write-Host "    [查看帮助]       certguard-agent --help" -ForegroundColor White
        Write-Host "                    & `"$agentExe`" --help" -ForegroundColor DarkGray
        Write-Host "    [查看版本]       certguard-agent --version" -ForegroundColor White
        Write-Host "                    & `"$agentExe`" --version" -ForegroundColor DarkGray
        Write-Host ""
        Write-Host "    --- 日志查看（已处理 UTF-8 乱码） ---" -ForegroundColor DarkGray
        Write-Host "    [查看最近100行日志]" -ForegroundColor White
        Write-Host "      [Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Content -Path (Get-ChildItem '$logDir\*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName -Tail 100 -Encoding UTF8" -ForegroundColor White
        Write-Host ""
        Write-Host "    [实时跟踪日志]   （Ctrl+C 退出）" -ForegroundColor White
        Write-Host "      [Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Content -Path (Get-ChildItem '$logDir\*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName -Wait -Tail 50 -Encoding UTF8" -ForegroundColor White
        Write-Host ""
        Write-Host "    [列出所有日志文件]" -ForegroundColor White
        Write-Host "      Get-ChildItem '$logDir\*.log' | Sort-Object LastWriteTime -Descending | Format-Table Name, Length, LastWriteTime" -ForegroundColor White
        Write-Host ""
        Write-Host "    --- 配置文件 ---" -ForegroundColor DarkGray
        Write-Host "    [编辑配置文件]   notepad `"$InstallDir\agent.json`"" -ForegroundColor White
        Write-Host "    [改完配置需重启] Restart-Service $ServiceName" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  卸载方法" -ForegroundColor Cyan
        Write-Host "    Stop-Service $ServiceName; sc.exe delete $ServiceName" -ForegroundColor White
        Write-Host "    之后手动删除目录:" -ForegroundColor White
        Write-Host "      Remove-Item -Recurse -Force '$InstallDir'" -ForegroundColor White
        Write-Host "      Remove-Item -Recurse -Force '$DataDir'" -ForegroundColor White

        Write-Host ""
        Write-Host $line -ForegroundColor Green
    }
    else {
        $line = "=" * 64
        Write-Host ""
        Write-Host $line -ForegroundColor Yellow
        Write-Host "  !   服务未运行，请排查以下问题：" -ForegroundColor Yellow
        Write-Host $line -ForegroundColor Yellow
        Write-Host "    1. 手动运行调试:"
        Write-Host "         & `"$agentExe`" --data-dir `"$DataDir`"" -ForegroundColor White
        Write-Host "    2. 查看日志（文件，UTF-8 编码）:"
        Write-Host "         [Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Content -Path (Get-ChildItem '$DataDir\logs\*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName -Tail 100 -Encoding UTF8" -ForegroundColor White
        Write-Host "    3. 查看 Windows 事件日志:"
        Write-Host "         Get-WinEvent -LogName Application -MaxEvents 20 | Where-Object { `$_.Message -like '*CertGuard*' }" -ForegroundColor White
        Write-Host "    4. 检查配置文件:"
        Write-Host "         notepad `"$InstallDir\agent.json`"" -ForegroundColor White
        Write-Host ""
    }
}

# 仅在作为脚本文件直接运行时自动调用（iex 远程执行时跳过）
if ($MyInvocation.InvocationName) {
    Install-CertGuardAgent
}
