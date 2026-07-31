#!/bin/bash
# ============================================================
# TopSSL-CertGuard-Agent Linux 一键安装脚本
# 用法: curl -fsSL https://agent.topssl.cn/install | bash -s -- --token ct_reg_xxxxxx [--server http://your-platform:port]
# ============================================================

set -e

# ───────────────────────────────────────────────
# 临时文件清理陷阱（无论脚本如何退出，都清理残留文件）
# ───────────────────────────────────────────────
TMP_DOWNLOAD_DIR=""
TMP_FILES=""

cleanup() {
    local exit_code=$?
    for f in $TMP_FILES; do
        rm -f "$f" 2>/dev/null || true
    done
    if [ -n "$TMP_DOWNLOAD_DIR" ] && [ -d "$TMP_DOWNLOAD_DIR" ] && [ "$TMP_DOWNLOAD_DIR" != "/" ]; then
        rm -rf "$TMP_DOWNLOAD_DIR" 2>/dev/null || true
    fi
    exit $exit_code
}
trap cleanup EXIT INT TERM

# 全局关闭 .NET 全球化依赖(libicu)。覆盖 install.sh 子进程内的所有 Agent 调用
# (如注册 --register-only)。注意:systemd 服务进程不继承此 export,由 unit 文件内
# 的 Environment= 单独覆盖;交互式 shell 由 /etc/profile.d 覆盖。自带
# InvariantGlobalization=true 的二进制(>=1.1.7)不受影响,此项属双保险。
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

TOKEN=""
KEEP_IDENTITY=false
SERVER="http://localhost:5003"
INSTALL_DIR="/opt/TopSSL-CertGuard-Agent"
DATA_DIR="/var/lib/TopSSL-CertGuard-Agent"
SERVICE_NAME="topssl-certguard-agent"
VERSION="1.0.5"
MIN_SPACE_MB=200

# ───────────────────────────────────────────────
# 临时目录选择与空间检查
# 优先级: $TMPDIR > /var/tmp > /tmp
# ───────────────────────────────────────────────
select_temp_dir() {
    local candidates=""
    [ -n "$TMPDIR" ] && candidates="$candidates $TMPDIR"
    candidates="$candidates /var/tmp /tmp"

    for dir in $candidates; do
        mkdir -p "$dir" 2>/dev/null || continue
        local avail_kb
        avail_kb=$(df -k "$dir" 2>/dev/null | tail -1 | awk '{print $4}')
        [ -z "$avail_kb" ] && continue
        local avail_mb=$((avail_kb / 1024))
        if [ "$avail_mb" -ge "$MIN_SPACE_MB" ]; then
            echo "$dir"
            return 0
        fi
    done
    return 1
}

# ───────────────────────────────────────────────
# 输出格式化辅助函数（统一规范）
# ───────────────────────────────────────────────
if [ -t 1 ]; then
    _C_RESET="\033[0m"
    _C_CYAN="\033[36m"
    _C_GREEN="\033[32m"
    _C_YELLOW="\033[33m"
    _C_RED="\033[31m"
    _C_GRAY="\033[90m"
    _C_WHITE="\033[1;37m"
else
    _C_RESET=""; _C_CYAN=""; _C_GREEN=""; _C_YELLOW=""; _C_RED=""; _C_GRAY=""; _C_WHITE=""
fi

echo_banner() {
    local line="============================================================"
    echo ""
    printf "%b%s%b\n" "$_C_CYAN" "$line" "$_C_RESET"
    printf "%b  %s%b\n" "$_C_CYAN" "$1" "$_C_RESET"
    printf "%b%s%b\n" "$_C_CYAN" "$line" "$_C_RESET"
    echo ""
}

echo_step() {
    local step=$1 total=$2 msg=$3
    echo ""
    printf "%b[%s/%s] %s%b\n" "$_C_CYAN" "$step" "$total" "$msg" "$_C_RESET"
}

echo_sub_info() {
    printf "%b  %s%b\n" "$_C_GRAY" "$1" "$_C_RESET"
}

echo_ok() {
    printf "%b  [OK] %s%b\n" "$_C_GREEN" "$1" "$_C_RESET"
}

echo_warn() {
    printf "%b  [!] %s%b\n" "$_C_YELLOW" "$1" "$_C_RESET"
}

echo_err() {
    printf "%b  [X] %s%b\n" "$_C_RED" "$1" "$_C_RESET" >&2
}

cmd_line() {
    # 主推命令（白） + 备注（灰）
    printf "%b  %s%b\n" "$_C_WHITE" "$1" "$_C_RESET"
    if [ -n "$2" ]; then
        printf "%b      %s%b\n" "$_C_GRAY" "$2" "$_C_RESET"
    fi
}

# ───────────────────────────────────────────────
# 解析参数
# ───────────────────────────────────────────────
while [ $# -gt 0 ]; do
    case $1 in
        --token)         TOKEN="$2";       shift 2 ;;
        --server)        SERVER="$2";      shift 2 ;;
        --dir)           INSTALL_DIR="$2"; shift 2 ;;
        --keep-identity) KEEP_IDENTITY=true; shift ;;
        *) shift ;;
    esac
done

if [ -z "$TOKEN" ] && [ "$KEEP_IDENTITY" != "true" ]; then
    echo_err "需要 --token 参数"
    echo "    用法: curl ... | bash -s -- --token ct_reg_xxxxxx [--server http://your-platform:port]"
    echo "    升级保留身份: curl ... | sudo bash -s -- --keep-identity [--server http://your-platform:port]"
    exit 1
fi

# 权限检查
if [ "$(id -u)" -ne 0 ]; then
    echo_err "安装 TopSSL-CertGuard-Agent 需要 root 权限，请使用 sudo 或以 root 身份运行。"
    echo_warn "示例: curl ... | sudo bash -s -- --token ct_reg_xxx"
    exit 1
fi

TOTAL_STEPS=7

echo_banner "TopSSL-CertGuard-Agent 安装程序  v${VERSION}"
echo_sub_info "安装目录: ${INSTALL_DIR}"
echo_sub_info "数据目录: ${DATA_DIR}"
echo_sub_info "平台地址: ${SERVER}"
echo_sub_info "日志目录: ${DATA_DIR}/logs"
echo_sub_info "服务名称: ${SERVICE_NAME}"

# ── 1. 停止旧服务 ──────────────────────────────────
echo_step 1 $TOTAL_STEPS "停止旧服务..."
if systemctl is-active --quiet "${SERVICE_NAME}" 2>/dev/null; then
    echo_sub_info "服务已存在，正在停止并禁用旧服务..."
    systemctl stop "${SERVICE_NAME}" 2>/dev/null || true
    systemctl disable "${SERVICE_NAME}" 2>/dev/null || true
    sleep 2
    echo_ok "旧服务已清理"
else
    echo_sub_info "无需清理（服务不存在或未运行）"
fi

# ── 2. 创建目录 ──────────────────────────────────
echo_step 2 $TOTAL_STEPS "创建目录..."
mkdir -p "${INSTALL_DIR}"
mkdir -p "${DATA_DIR}"
echo_ok "安装目录与数据目录已创建"

# ── 3. 下载 Agent 二进制文件 ──────────────────────
echo_step 3 $TOTAL_STEPS "下载 Agent 二进制文件..."

ARCH=$(uname -m)
case $ARCH in
    x86_64)  AGENT_ARCH="linux-x64" ;;
    aarch64) AGENT_ARCH="linux-arm64" ;;
    *)
        echo_err "不支持的架构: $ARCH"
        exit 1
        ;;
esac

DOWNLOAD_URL="${SERVER}/agent/certguard-agent-${AGENT_ARCH}.tar.gz"

# 选择临时下载目录（优先级：$TMPDIR > $INSTALL_DIR/.tmp > /var/tmp > /tmp）
TMP_DOWNLOAD_DIR=$(select_temp_dir)
if [ -z "$TMP_DOWNLOAD_DIR" ]; then
    echo_err "所有候选临时目录空间均不足（需要至少 ${MIN_SPACE_MB}MB）"
    echo_err ""
    echo_err "请尝试以下任一方法后重试："
    echo_err "  1. 设置 TMPDIR 指向空间充足的分区："
    echo_err "     TMPDIR=/var/tmp curl ... | sudo bash -s -- --token ct_reg_xxx"
    echo_err "  2. 清理 /tmp 目录：sudo rm -rf /tmp/*"
    echo_err "  3. 扩展 /tmp 大小（tmpfs）：sudo mount -o remount,size=2G /tmp"
    exit 1
fi

TAR_PATH="${TMP_DOWNLOAD_DIR}/certguard-agent.tar.gz"
SHA256_PATH="${TMP_DOWNLOAD_DIR}/certguard-agent.tar.gz.sha256"
TMP_FILES="$TMP_FILES $TAR_PATH $SHA256_PATH"

echo_sub_info "下载地址: ${DOWNLOAD_URL}"
echo_sub_info "临时目录: ${TMP_DOWNLOAD_DIR}"
echo_sub_info "开始下载，请稍候..."
# 注意：不加 -s/-S 标志，让 curl 对 TTY 输出默认详细进度（含速率/ETA/百分比/已下载）
# 非 TTY 环境（如管道）curl 会自动跳过进度，不影响脚本继续执行
if ! curl -fL "${DOWNLOAD_URL}" -o "${TAR_PATH}"; then
    echo_err "下载失败，请检查网络或服务器地址"
    echo_err "  ${DOWNLOAD_URL}"
    echo_err "提示：检查服务器是否可达: curl -I ${SERVER}"
    exit 1
fi

if [ ! -f "${TAR_PATH}" ]; then
    echo_err "下载文件不存在: ${TAR_PATH}"
    exit 1
fi

FILE_SIZE=$(du -h "${TAR_PATH}" | cut -f1)
echo_sub_info "文件大小: ${FILE_SIZE}"

# 下载并校验 SHA256（服务器可能未提供 .sha256 文件，此时跳过校验而非报错）
echo_sub_info "校验文件完整性..."
if curl -fsSL "${DOWNLOAD_URL}.sha256" -o "${SHA256_PATH}" 2>/dev/null; then
    if [ -s "${SHA256_PATH}" ]; then
        EXPECTED=$(awk '{print $1}' "${SHA256_PATH}" | tr -d '\r\n' | tr '[:upper:]' '[:lower:]')
        ACTUAL=$(sha256sum "${TAR_PATH}" | awk '{print $1}' | tr '[:upper:]' '[:lower:]')
        if [ -z "$EXPECTED" ]; then
            echo_warn "SHA256 校验文件为空，跳过校验"
        elif [ "$EXPECTED" != "$ACTUAL" ]; then
            echo_err "SHA256 校验失败！安装包可能已损坏或被篡改。"
            echo_err "  期望: ${EXPECTED}"
            echo_err "  实际: ${ACTUAL}"
            echo_err "安装已中止。请重新下载或联系技术支持。"
            exit 1
        else
            echo_ok "SHA256 校验通过 (${EXPECTED})"
        fi
    else
        echo_warn "SHA256 校验文件为空，跳过校验"
    fi
    rm -f "${SHA256_PATH}"
else
    echo_warn "无法获取 SHA256 校验文件（服务器可能未提供），跳过校验"
fi

# --keep-identity 模式下，解压前先备份 InstallDir 的 agent.json
# （InstallDir 的配置可能比 DataDir 更新，因为 --update-secret / PersistConfig 优先写入 InstallDir）
SAVED_AGENT_JSON=""
INSTALL_DIR_CONFIG="${INSTALL_DIR}/agent.json"
DATA_DIR_CONFIG="${DATA_DIR}/agent.json"
if [ "$KEEP_IDENTITY" = "true" ] && [ -f "$INSTALL_DIR_CONFIG" ]; then
    SAVED_AGENT_JSON=$(cat "$INSTALL_DIR_CONFIG")
    echo_sub_info "已备份现有配置文件 (InstallDir)"
fi

# 解压前清空安装目录旧文件（确保干净安装，agent.json 已在上步备份）
if [ -d "${INSTALL_DIR}" ]; then
    rm -rf "${INSTALL_DIR:?}"/* 2>/dev/null || true
fi

tar xzf "${TAR_PATH}" -C "${INSTALL_DIR}"
chmod +x "${INSTALL_DIR}/certguard-agent"
rm -f "${TAR_PATH}"
echo_ok "解压完成"

# --keep-identity: 恢复 InstallDir 的 agent.json（从解压前备份的内容）
if [ -n "$SAVED_AGENT_JSON" ]; then
    echo "$SAVED_AGENT_JSON" > "$INSTALL_DIR_CONFIG"
    echo_ok "已恢复配置文件到: $INSTALL_DIR_CONFIG"
fi

# ── 4. 首次注册 ──────────────────────────────────
echo_step 4 $TOTAL_STEPS "注册 Agent..."

AGENT_BIN="${INSTALL_DIR}/certguard-agent"
if [ ! -x "${AGENT_BIN}" ]; then
    echo_err "未找到 certguard-agent 可执行文件，请检查安装包内容。"
    exit 1
fi

# 注册前身份处理。
# 默认: 清理旧 agent.json,强制用本次 --token 全新注册。
#   原因:Agent 在 agent.json 已存在时会"加载旧身份并退出",忽略传入的 token,
#   导致换 token 重装后服务连不上平台("401 Agent 不存在"死循环)。
# --keep-identity: 保留现有身份(如仅升级二进制),此时 --token 可省略。
HAS_EXISTING_IDENTITY=false
if [ -f "$DATA_DIR_CONFIG" ] || [ -f "$INSTALL_DIR_CONFIG" ]; then
    HAS_EXISTING_IDENTITY=true
fi
REGISTER_SKIP=false

if [ "$KEEP_IDENTITY" = "true" ]; then
    if [ "$HAS_EXISTING_IDENTITY" = "true" ]; then
        echo_sub_info "保留现有身份,跳过注册 (--keep-identity)"
        REGISTER_SKIP=true
    else
        # 无现有身份,--keep-identity 无意义,回退到正常注册(此时必须有 token)
        if [ -z "$TOKEN" ]; then
            echo_err "--keep-identity 已指定,但未发现现有身份,且未提供 --token,无法注册"
            exit 1
        fi
        echo_sub_info "未发现现有身份,改用提供的 Token 全新注册"
    fi
else
    # 默认: 清理旧身份,强制全新注册
    if [ "$HAS_EXISTING_IDENTITY" = "true" ]; then
        rm -f "$DATA_DIR_CONFIG" "$INSTALL_DIR_CONFIG"
        echo_sub_info "已清理旧身份文件,将使用本次 Token 全新注册"
    fi
fi

if [ "$REGISTER_SKIP" != "true" ]; then
    echo_sub_info "执行首次注册..."
    if "${AGENT_BIN}" --token "${TOKEN}" --server "${SERVER}" --data-dir "${DATA_DIR}" --register-only; then
        echo_ok "注册完成"
    else
        echo_warn "注册失败（退出码: $?），请检查 Token 是否有效。"
    fi
fi

# 同步 agent.json 到两个位置
if [ "$KEEP_IDENTITY" = "true" ]; then
    # KeepIdentity: InstallDir 已有最新配置（从备份恢复），同步到 DataDir
    if [ -f "$INSTALL_DIR_CONFIG" ] && [ ! -f "$DATA_DIR_CONFIG" ]; then
        cp "$INSTALL_DIR_CONFIG" "$DATA_DIR_CONFIG"
        echo_ok "配置文件已同步到: $DATA_DIR_CONFIG"
    fi
else
    # 正常注册: DataDir 有最新配置（PersistConfig 写入），同步到 InstallDir
    if [ -f "$DATA_DIR_CONFIG" ] && [ ! -f "$INSTALL_DIR_CONFIG" ]; then
        cp "$DATA_DIR_CONFIG" "$INSTALL_DIR_CONFIG"
        echo_ok "配置文件已复制到: $INSTALL_DIR_CONFIG"
    fi
fi

# ── 5. 创建 systemd 服务 ─────────────────────────
echo_step 5 $TOTAL_STEPS "创建 systemd 服务..."
cat > /etc/systemd/system/${SERVICE_NAME}.service << EOF
[Unit]
Description=TopSSL.cn CertGuard Agent - SSL证书自动部署守护进程
After=network-online.target
Wants=network-online.target
StartLimitBurst=5
StartLimitIntervalSec=60

[Service]
Type=simple
ExecStart=${INSTALL_DIR}/certguard-agent --data-dir ${DATA_DIR}
Restart=on-failure
RestartSec=10
User=root
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

[Install]
WantedBy=multi-user.target
EOF

echo_ok "服务文件已创建: /etc/systemd/system/${SERVICE_NAME}.service"

# ── 6. 启动服务 ──────────────────────────────────
echo_step 6 $TOTAL_STEPS "启动服务..."
systemctl daemon-reload
systemctl enable "${SERVICE_NAME}"
if systemctl start "${SERVICE_NAME}"; then
    sleep 2
    echo_ok "服务已启动"
else
    echo_warn "服务启动失败，请用 journalctl -u ${SERVICE_NAME} -n 50 排查"
fi

# ── 7. 将安装目录加入系统 PATH ──────────────────
echo_step 7 $TOTAL_STEPS "将安装目录加入系统 PATH..."
PROFILE_D="/etc/profile.d/topssl-certguard-agent.sh"
PROFILE_CHANGED=false
if [ ! -f "$PROFILE_D" ]; then
    echo "# TopSSL-CertGuard-Agent PATH & 环境" > "$PROFILE_D"
    chmod 644 "$PROFILE_D"
    PROFILE_CHANGED=true
fi
if ! grep -q "${INSTALL_DIR}" "$PROFILE_D" 2>/dev/null; then
    echo "export PATH=\"\$PATH:${INSTALL_DIR}\"" >> "$PROFILE_D"
    PROFILE_CHANGED=true
fi
if ! grep -q "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT" "$PROFILE_D" 2>/dev/null; then
    echo "export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1" >> "$PROFILE_D"
    PROFILE_CHANGED=true
fi
if [ "$PROFILE_CHANGED" = "true" ]; then
    echo_ok "已更新: $PROFILE_D"
    echo_warn "请执行 'source $PROFILE_D' 或重新登录使 certguard-agent 命令生效"
else
    echo_ok "PATH 配置文件已存在"
    echo_warn "当前 shell 未生效，请执行 'source $PROFILE_D' 或重新登录"
fi

# ── 验证与最终提示 ────────────────────────────────
sleep 2
LOG_DIR="${DATA_DIR}/logs"

if systemctl is-active --quiet "${SERVICE_NAME}"; then
    LINE="============================================================"
    echo ""
    printf "%b%s%b\n" "$_C_GREEN" "$LINE" "$_C_RESET"
    printf "%b  OK  TopSSL-CertGuard-Agent 安装成功！%b\n" "$_C_GREEN" "$_C_RESET"
    printf "%b%s%b\n" "$_C_GREEN" "$LINE" "$_C_RESET"

    echo ""
    printf "%b  安装信息%b\n" "$_C_CYAN" "$_C_RESET"
    echo "    程序目录  : ${INSTALL_DIR}"
    echo "    可执行文件: ${AGENT_BIN}"
    echo "    配置文件  : ${INSTALL_DIR}/agent.json"
    echo "    数据目录  : ${DATA_DIR}"
    echo "    日志目录  : ${LOG_DIR}"
    echo "    服务名称  : ${SERVICE_NAME}"
    printf "%b    命令行名  : certguard-agent （重新登录后可直接使用）%b\n" "$_C_GRAY" "$_C_RESET"
    printf "%b               当前 shell 请先执行: source /etc/profile.d/topssl-certguard-agent.sh%b\n" "$_C_GRAY" "$_C_RESET"

    echo ""
    printf "%b  常用操作（复制对应命令到终端执行）%b\n" "$_C_CYAN" "$_C_RESET"
    echo ""
    printf "%b    --- 服务管理 ---%b\n" "$_C_GRAY" "$_C_RESET"
    cmd_line "[查看服务状态]   systemctl status ${SERVICE_NAME}"
    cmd_line "[启动服务]       systemctl start ${SERVICE_NAME}"
    cmd_line "[停止服务]       systemctl stop ${SERVICE_NAME}"
    cmd_line "[重启服务]       systemctl restart ${SERVICE_NAME}"
    cmd_line "[开机自启]       systemctl enable ${SERVICE_NAME}"
    cmd_line "[取消自启]       systemctl disable ${SERVICE_NAME}"
    echo ""
    printf "%b    --- 程序命令（短形式 / 完整路径形式 均可） ---%b\n" "$_C_GRAY" "$_C_RESET"
    cmd_line "[查看帮助]       certguard-agent --help" \
             "${AGENT_BIN} --help"
    cmd_line "[查看版本]       certguard-agent --version" \
             "${AGENT_BIN} --version"
    echo ""
    printf "%b    --- 日志查看（systemd 日志 + Agent 文件日志 双轨） ---%b\n" "$_C_GRAY" "$_C_RESET"
    cmd_line "[systemd 最近50行]   journalctl -u ${SERVICE_NAME} -n 50"
    cmd_line "[systemd 实时跟踪]   journalctl -u ${SERVICE_NAME} -f        （Ctrl+C 退出）"
    cmd_line "[Agent 最近100行]    tail -n 100 \$(ls -t ${LOG_DIR}/*.log 2>/dev/null | head -1)"
    cmd_line "[Agent 实时跟踪]     tail -f \$(ls -t ${LOG_DIR}/*.log 2>/dev/null | head -1)   （Ctrl+C 退出）"
    cmd_line "[列出所有日志文件]   ls -lhrt ${LOG_DIR}/*.log 2>/dev/null"
    echo ""
    printf "%b    --- 配置文件 ---%b\n" "$_C_GRAY" "$_C_RESET"
    cmd_line "[查看配置]       cat ${INSTALL_DIR}/agent.json"
    cmd_line "[编辑配置]       vi ${INSTALL_DIR}/agent.json"
    printf "%b    [改完配置需重启] systemctl restart ${SERVICE_NAME}%b\n" "$_C_YELLOW" "$_C_RESET"

    echo ""
    printf "%b  卸载方法%b\n" "$_C_CYAN" "$_C_RESET"
    cmd_line "[停止禁用服务]   systemctl stop ${SERVICE_NAME}; systemctl disable ${SERVICE_NAME}"
    cmd_line "[删除服务文件]   rm -f /etc/systemd/system/${SERVICE_NAME}.service"
    cmd_line "[重载服务配置]   systemctl daemon-reload"
    cmd_line "[删除程序数据]   rm -rf ${INSTALL_DIR} ${DATA_DIR}"
    cmd_line "[删除路径配置]   rm -f /etc/profile.d/topssl-certguard-agent.sh"

    echo ""
    printf "%b%s%b\n" "$_C_GREEN" "$LINE" "$_C_RESET"
    echo ""
    echo "  请登录 TOPSSL.CN 控制台确认 Agent 已上线。"
else
    LINE="============================================================"
    echo ""
    printf "%b%s%b\n" "$_C_YELLOW" "$LINE" "$_C_RESET"
    printf "%b  !   服务未运行，请排查以下问题：%b\n" "$_C_YELLOW" "$_C_RESET"
    printf "%b%s%b\n" "$_C_YELLOW" "$LINE" "$_C_RESET"
    echo "    1. 手动运行调试:"
    printf "%b         DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ${AGENT_BIN} --data-dir ${DATA_DIR}%b\n" "$_C_WHITE" "$_C_RESET"
    echo "    2. 查看 systemd 日志:"
    printf "%b         journalctl -u ${SERVICE_NAME} -n 50%b\n" "$_C_WHITE" "$_C_RESET"
    echo "    3. 查看 Agent 文件日志:"
    printf "%b         tail -n 100 \$(ls -t ${LOG_DIR}/*.log 2>/dev/null | head -1)%b\n" "$_C_WHITE" "$_C_RESET"
    echo "    4. 检查配置文件:"
    printf "%b         vi ${INSTALL_DIR}/agent.json%b\n" "$_C_WHITE" "$_C_RESET"
    echo ""
fi
