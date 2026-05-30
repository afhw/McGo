# P2P 联机与中继部署

McGo 的 P2P 联机使用一个轻量 TCP 中继来打通 Minecraft Java 版的局域网联机流量。房主在游戏内“对局域网开放”后，把聊天栏显示的端口填到 McGo；加入者启动隧道后，在 Minecraft 里连接本机地址。

默认中继地址：

```text
flyliq.cn:10721
```

## 客户端使用

房主：

1. 进入单人世界，点击“对局域网开放”。
2. 复制聊天栏显示的端口，例如 `51342`。
3. 进入“联机”页面。
4. 填写中继地址 `flyliq.cn`、端口 `10721`、房间号和可选口令。
5. 将“房主 Minecraft LAN 端口”改成第 2 步的端口。
6. 点击“作为房主启动”，再把邀请信息发给加入者。

加入者：

1. 填写相同的中继地址、端口、房间号和口令。
2. 选择一个本地监听端口，默认 `25565`。
3. 点击“作为加入者启动”。
4. 在 Minecraft 多人游戏里添加服务器 `127.0.0.1:25565`，端口按实际填写为准。

## 部署服务端

服务器只需要 Python 3.10+。以下示例假设部署到 `/opt/mcgo-p2p`：

```bash
sudo mkdir -p /opt/mcgo-p2p
sudo cp p2p_tunnel.py p2p_server.py /opt/mcgo-p2p/
cd /opt/mcgo-p2p
python3 -m venv .venv
.venv/bin/python p2p_server.py --host 0.0.0.0 --port 10721
```

如果使用防火墙，放行 TCP 端口：

```bash
sudo ufw allow 10721/tcp
```

云服务器还需要在安全组里放行 `10721/tcp`。

## systemd 示例

创建 `/etc/systemd/system/mcgo-p2p.service`：

```ini
[Unit]
Description=McGo P2P Relay
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/mcgo-p2p
ExecStart=/opt/mcgo-p2p/.venv/bin/python /opt/mcgo-p2p/p2p_server.py --host 0.0.0.0 --port 10721
Restart=always
RestartSec=3
User=mcgo

[Install]
WantedBy=multi-user.target
```

启用并查看状态：

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now mcgo-p2p
sudo systemctl status mcgo-p2p
```

## Nuitka 单文件打包

Windows：

```powershell
.\scripts\build_nuitka_p2p_server.ps1
```

产物在：

```text
dist\mcgo-p2p-server.exe
```

Linux 需要在目标 Linux 机器上打包：

```bash
python3 -m pip install nuitka ordered-set zstandard
python3 -m nuitka \
  --standalone \
  --onefile \
  --assume-yes-for-downloads \
  --output-dir=dist \
  --output-filename=mcgo-p2p-server \
  p2p_server.py
```

启动单文件服务端：

```bash
./dist/mcgo-p2p-server --host 0.0.0.0 --port 10721
```

## 说明

这是中继辅助的 P2P 隧道，不是公网 Minecraft 服务器。中继只负责按房间转发 TCP 字节流，不理解 Minecraft 协议，也不保存账号信息。请只把房间号和口令发给可信玩家。
