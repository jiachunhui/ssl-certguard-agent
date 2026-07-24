#!/bin/bash
# ============================================================
# TopSSL-CertGuard-Agent Linux 一键安装脚本
# 用法: curl -fsSL https://agent.topssl.cn/install | bash -s -- --token ct_reg_xxxxxx [--server http://your-platform:port]
# ============================================================

set -e

TOKEN=""
SERVER="http://localhost:5003"
INSTALL_DIR="/opt/TopSSL-CertGuard-Agent"
DATA_DIR="/var/lib/TopSSL-CertGuard-Agent"
SERVICE_NAME="topssl-certguard-agent"
VERSION="1.0.2"

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
while [[ $# -gt 0 ]]; do
    case $1 in
        --token)  TOKEN="$2";      shift 2 ;;
        --server) SERVER="$2";     shift 2 ;;
        --dir)    INSTALL_DIR="$2"; shift 2 ;;
        *) shift ;;
    esac
done

if [ -z "$TOKEN" ]; then
    echo_err "需要 --token 参数"
    echo "    用法: curl ... | bash -s -- --token ct_reg_xxxxxx [--server http://your-platform:port]"
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
TAR_PATH="/tmp/certguard-agent.tar.gz"

echo_sub_info "下载地址: ${DOWNLOAD_URL}"
echo_sub_info "开始下载，请稍候..."
# 注意：不加 -s/-S 标志，让 curl 对 TTY 输出默认详细进度（含速率/ETA/百分比/已下载）
# 非 TTY 环境（如管道）curl 会自动跳过进度，不影响脚本继续执行
if ! curl -fL "${DOWNLOAD_URL}" -o "${TAR_PATH}"; then
    rm -f "${TAR_PATH}" 2>/dev/null || true
    echo_err "下载失败，请检查网络或服务器地址: ${DOWNLOAD_URL}"
    exit 1
fi

if [ -f "${TAR_PATH}" ]; then
    FILE_SIZE=$(du -h "${TAR_PATH}" | cut -f1)
    echo_ok "下载完成，文件大小: ${FILE_SIZE}"
else
    echo_err "下载文件不存在: ${TAR_PATH}"
    exit 1
fi

tar xzf "${TAR_PATH}" -C "${INSTALL_DIR}"
chmod +x "${INSTALL_DIR}/certguard-agent"
rm -f "${TAR_PATH}"
echo_ok "解压完成"

# ── 4. 首次注册 ──────────────────────────────────
echo_step 4 $TOTAL_STEPS "注册 Agent..."

AGENT_BIN="${INSTALL_DIR}/certguard-agent"
if [ ! -x "${AGENT_BIN}" ]; then
    echo_err "未找到 certguard-agent 可执行文件，请检查安装包内容。"
    exit 1
fi

echo_sub_info "执行首次注册..."
if "${AGENT_BIN}" --token "${TOKEN}" --server "${SERVER}" --data-dir "${DATA_DIR}" --register-only; then
    echo_ok "注册完成"
else
    echo_warn "注册失败（退出码: $?），请检查 Token 是否有效。"
fi

# 注册成功后，如果 DataDir 下有 agent.json 且安装目录还没有，复制一份到安装目录
DATA_DIR_CONFIG="${DATA_DIR}/agent.json"
INSTALL_DIR_CONFIG="${INSTALL_DIR}/agent.json"
if [ -f "$DATA_DIR_CONFIG" ] && [ ! -f "$INSTALL_DIR_CONFIG" ]; then
    cp "$DATA_DIR_CONFIG" "$INSTALL_DIR_CONFIG"
    echo_ok "配置文件已复制到: $INSTALL_DIR_CONFIG"
fi

# ── 5. 创建 systemd 服务 ─────────────────────────
echo_step 5 $TOTAL_STEPS "创建 systemd 服务..."
cat > /etc/systemd/system/${SERVICE_NAME}.service << EOF
[Unit]
Description=TopSSL.cn CertGuard Agent - SSL证书自动部署守护进程
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=${INSTALL_DIR}/certguard-agent --data-dir ${DATA_DIR}
Restart=always
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
if [ ! -f "$PROFILE_D" ]; then
    echo "# TopSSL-CertGuard-Agent PATH" > "$PROFILE_D"
    echo "export PATH=\"\$PATH:${INSTALL_DIR}\"" >> "$PROFILE_D"
    chmod 644 "$PROFILE_D"
    echo_ok "已添加: $PROFILE_D"
    echo_warn "请重新登录或执行 'source $PROFILE_D' 使 PATH 生效"
else
    if grep -q "${INSTALL_DIR}" "$PROFILE_D" 2>/dev/null; then
        echo_ok "已在 PATH 中，跳过"
    else
        echo "export PATH=\"\$PATH:${INSTALL_DIR}\"" >> "$PROFILE_D"
        echo_ok "已追加到: $PROFILE_D"
        echo_warn "请执行 'source $PROFILE_D' 使 PATH 生效"
    fi
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
    printf "%b    命令行名  : certguard-agent （已加入系统 PATH，重新登录后可直接使用）%b\n" "$_C_GRAY" "$_C_RESET"

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
    cmd_line "systemctl stop ${SERVICE_NAME}; systemctl disable ${SERVICE_NAME}"
    cmd_line "rm -f /etc/systemd/system/${SERVICE_NAME}.service"
    cmd_line "systemctl daemon-reload"
    cmd_line "rm -rf ${INSTALL_DIR} ${DATA_DIR}"
    cmd_line "rm -f /etc/profile.d/topssl-certguard-agent.sh"

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
    printf "%b         ${AGENT_BIN} --data-dir ${DATA_DIR}%b\n" "$_C_WHITE" "$_C_RESET"
    echo "    2. 查看 systemd 日志:"
    printf "%b         journalctl -u ${SERVICE_NAME} -n 50%b\n" "$_C_WHITE" "$_C_RESET"
    echo "    3. 查看 Agent 文件日志:"
    printf "%b         tail -n 100 \$(ls -t ${LOG_DIR}/*.log 2>/dev/null | head -1)%b\n" "$_C_WHITE" "$_C_RESET"
    echo "    4. 检查配置文件:"
    printf "%b         vi ${INSTALL_DIR}/agent.json%b\n" "$_C_WHITE" "$_C_RESET"
    echo ""
fi
