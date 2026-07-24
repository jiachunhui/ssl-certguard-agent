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

# 解析参数
while [[ $# -gt 0 ]]; do
    case $1 in
        --token) TOKEN="$2"; shift 2 ;;
        --server) SERVER="$2"; shift 2 ;;
        --dir) INSTALL_DIR="$2"; shift 2 ;;
        *) shift ;;
    esac
done

if [ -z "$TOKEN" ]; then
    echo "错误: 需要 --token 参数"
    echo "用法: curl ... | bash -s -- --token ct_reg_xxxxxx [--server http://your-platform:port]"
    exit 1
fi

echo "================================================"
echo "  TopSSL-CertGuard-Agent 安装程序"
echo "================================================"
echo ""
echo "安装目录: ${INSTALL_DIR}"
echo "数据目录: ${DATA_DIR}"
echo "平台地址: ${SERVER}"
echo ""

# ── 0. 停止旧服务 ──────────────────────────────────
echo "[0/6] 停止旧服务..."
if systemctl is-active --quiet "${SERVICE_NAME}" 2>/dev/null; then
    echo "  服务已存在，正在停止并禁用旧服务..."
    systemctl stop "${SERVICE_NAME}" 2>/dev/null || true
    systemctl disable "${SERVICE_NAME}" 2>/dev/null || true
    sleep 2
    echo "  旧服务已清理"
else
    echo "  无需清理"
fi

# ── 1. 创建目录 ──────────────────────────────────
echo "[1/6] 创建目录..."
mkdir -p "${INSTALL_DIR}"
mkdir -p "${DATA_DIR}"

# ── 2. 下载 Agent 二进制文件 ──────────────────────
echo "[2/6] 下载 Agent 二进制文件..."
ARCH=$(uname -m)
case $ARCH in
    x86_64)  AGENT_ARCH="linux-x64" ;;
    aarch64) AGENT_ARCH="linux-arm64" ;;
    *)       echo "不支持的架构: $ARCH"; exit 1 ;;
esac

DOWNLOAD_URL="https://agent.topssl.cn/releases/certguard-agent-${AGENT_ARCH}.tar.gz"

curl -fsSL "${DOWNLOAD_URL}" -o /tmp/certguard-agent.tar.gz
tar xzf /tmp/certguard-agent.tar.gz -C "${INSTALL_DIR}"
chmod +x "${INSTALL_DIR}/certguard-agent"
rm /tmp/certguard-agent.tar.gz
echo "  下载完成"

# ── 3. 首次注册 ──────────────────────────────────
echo "[3/6] 注册 Agent..."
"${INSTALL_DIR}/certguard-agent" --token "${TOKEN}" --server "${SERVER}" --data-dir "${DATA_DIR}" --register-only

# 注册成功后，如果 DataDir 下有 agent.json 且安装目录还没有，复制一份到安装目录
DATA_DIR_CONFIG="${DATA_DIR}/agent.json"
INSTALL_DIR_CONFIG="${INSTALL_DIR}/agent.json"
if [ -f "$DATA_DIR_CONFIG" ] && [ ! -f "$INSTALL_DIR_CONFIG" ]; then
    cp "$DATA_DIR_CONFIG" "$INSTALL_DIR_CONFIG"
    echo "  配置文件已复制到: $INSTALL_DIR_CONFIG"
fi

# ── 4. 创建 systemd 服务 ─────────────────────────
echo "[4/6] 创建 systemd 服务..."
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

echo "  服务文件已创建"

# ── 5. 启动服务 ──────────────────────────────────
echo "[5/6] 启动服务..."
systemctl daemon-reload
systemctl enable "${SERVICE_NAME}"
systemctl start "${SERVICE_NAME}"

# ── 6. 将安装目录加入系统 PATH ──────────────────
echo "[6/6] 将安装目录加入系统 PATH..."
PROFILE_D="/etc/profile.d/topssl-certguard-agent.sh"
if [ ! -f "$PROFILE_D" ]; then
    echo "# TopSSL-CertGuard-Agent PATH" > "$PROFILE_D"
    echo "export PATH=\"\$PATH:${INSTALL_DIR}\"" >> "$PROFILE_D"
    chmod 644 "$PROFILE_D"
    echo "  已添加: $PROFILE_D"
    echo "  请重新登录或执行 'source $PROFILE_D' 使 PATH 生效"
else
    # 检查是否已经在 PATH 中
    if grep -q "${INSTALL_DIR}" "$PROFILE_D" 2>/dev/null; then
        echo "  已在 PATH 中，跳过"
    else
        echo "export PATH=\"\$PATH:${INSTALL_DIR}\"" >> "$PROFILE_D"
        echo "  已追加到: $PROFILE_D"
    fi
fi

# ── 验证 ──────────────────────────────────────
sleep 2
if systemctl is-active --quiet "${SERVICE_NAME}"; then
    echo ""
    echo "✅ TopSSL-CertGuard-Agent 安装成功！"
    echo "   程序目录: ${INSTALL_DIR}"
    echo "   配置文件: ${INSTALL_DIR}/agent.json"
    echo "   数据目录: ${DATA_DIR}"
    echo ""
    echo "   服务管理:"
    echo "     systemctl status ${SERVICE_NAME}"
    echo "     systemctl restart ${SERVICE_NAME}"
    echo ""
    echo "   查看日志:"
    echo "     journalctl -u ${SERVICE_NAME} -f"
    echo ""
    echo "请登录 TOPSSL.CN 控制台确认 Agent 已上线。"
else
    echo ""
    echo "⚠️  服务启动失败，请检查:"
    echo "   1. journalctl -u ${SERVICE_NAME} -n 50"
    echo "   2. ${INSTALL_DIR}/config.json"
    echo "   3. 手动运行: ${INSTALL_DIR}/certguard-agent --data-dir ${DATA_DIR}"
fi
