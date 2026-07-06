#!/bin/bash
# ============================================================
# CertGuard Agent Linux 一键安装脚本
# 用法: curl -fsSL https://agent.topssl.cn/install | bash -s -- --token ct_reg_xxxxxx
# ============================================================

set -e

TOKEN=""
INSTALL_DIR="/opt/certguard-agent"
DATA_DIR="/var/lib/certguard"
SERVICE_NAME="certguard-agent"
 VERSION="1.0.2"

# 解析参数
while [[ $# -gt 0 ]]; do
    case $1 in
        --token) TOKEN="$2"; shift 2 ;;
        --dir) INSTALL_DIR="$2"; shift 2 ;;
        *) shift ;;
    esac
done

if [ -z "$TOKEN" ]; then
    echo "错误: 需要 --token 参数"
    echo "用法: curl ... | bash -s -- --token ct_reg_xxxxxx"
    exit 1
fi

echo "================================================"
echo "  CertGuard Agent v${VERSION} 安装程序"
echo "================================================"
echo ""
echo "安装目录: ${INSTALL_DIR}"
echo "数据目录: ${DATA_DIR}"
echo ""

# 创建目录
mkdir -p "${INSTALL_DIR}"
mkdir -p "${DATA_DIR}"

# 下载 Agent 二进制文件
echo "[1/4] 下载 Agent 二进制文件..."
ARCH=$(uname -m)
case $ARCH in
    x86_64)  AGENT_ARCH="linux-x64" ;;
    aarch64) AGENT_ARCH="linux-arm64" ;;
    *)       echo "不支持的架构: $ARCH"; exit 1 ;;
esac

DOWNLOAD_URL="https://agent.topssl.cn/releases/${VERSION}/certguard-agent-${AGENT_ARCH}.tar.gz"

# 如果官方 URL 不可用，尝试 GitHub
if ! curl -fsSL --head "${DOWNLOAD_URL}" > /dev/null 2>&1; then
    DOWNLOAD_URL="https://github.com/topssl/certguard-agent/releases/download/v${VERSION}/certguard-agent-${AGENT_ARCH}.tar.gz"
fi

curl -fsSL "${DOWNLOAD_URL}" -o /tmp/certguard-agent.tar.gz
tar xzf /tmp/certguard-agent.tar.gz -C "${INSTALL_DIR}"
chmod +x "${INSTALL_DIR}/certguard-agent"
rm /tmp/certguard-agent.tar.gz

# 首次注册
echo "[2/4] 注册 Agent..."
"${INSTALL_DIR}/certguard-agent" --token "${TOKEN}" --data-dir "${DATA_DIR}" --register-only

# 创建 systemd 服务
echo "[3/4] 创建 systemd 服务..."
cat > /etc/systemd/system/${SERVICE_NAME}.service << EOF
[Unit]
Description=CertGuard Agent - 证书自动部署守护进程
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

# 启动服务
echo "[4/4] 启动服务..."
systemctl daemon-reload
systemctl enable "${SERVICE_NAME}"
systemctl start "${SERVICE_NAME}"

# 验证
sleep 2
if systemctl is-active --quiet "${SERVICE_NAME}"; then
    echo ""
    echo "✅ CertGuard Agent 安装成功！"
    echo "   服务状态: systemctl status ${SERVICE_NAME}"
    echo "   查看日志: journalctl -u ${SERVICE_NAME} -f"
    echo ""
    echo "请在 TOPSSL.CN 控制台确认 Agent 已上线。"
else
    echo "⚠️  服务启动失败，请检查日志: journalctl -u ${SERVICE_NAME} -n 50"
fi
