# 跨内网信令服务

独立的 `FF.AgentCollaboration.Signaling` 服务签发配对邀请，并通过 WebSocket 转发 WebRTC 的 SDP 和 ICE 信令。工作台的 Agent 授权、收件箱与附件仍由联邦协作处理。

## 分享与加入

在 Agent 标题栏开启「外部协作」，点「分享」。公网或局域网可以直接到达提供方时，选「直接连接」。两端各自在内网时，选「跨内网连接」，把生成的链接交给对方。对方在「＋ Agent → 外部协作 Agent」粘贴链接、填写用途并提交。

两种连接方式并排显示。跨内网连接不可用时，按钮下会显示对应原因，例如缺少 WebRTC 组件或签发密钥。选择可用的连接方式后，工作台才会复制对应链接。

```text
ffcp://203.0.113.10:5190/signal/<roomId>?node=<nodeId>&window=<windowId>&pin=<workbenchFingerprint>&agent=<agent>#<joinCode>.<shareToken>
```

主机和端口指向两端预先配置的信令服务器。工作台先核对服务器地址和公钥指纹，再提交一次性加入码。`#` 中的 Agent 分享口令由提供方工作台加到链接中，只经 WebRTC 通道交给对端联邦服务使用。两端完成连接后，联邦请求仍走工作台的 HTTPS 和原有授权。

收到申请时，协作信箱显示待审条目。被申请的 Agent 可收到系统通知、提醒用户并打开该申请的确认弹框。用户在弹框内选择同意或拒绝，并配置权限、配额和上传权限。

## 通信与文件

关闭网页后，工作台后台仍可收发。双方加入房间后，信令服务自动提供 STUN 地址和房间专属的临时 TURN 凭据。ICE 优先选择直连，直连受限时可通过内置 TURN 传输。两条路径都承载 WebRTC DataChannel 和两端之间的联邦 TLS。

`send_peer_files` 支持随新消息发送文件，或者凭自己已送达消息的完整 ID 追加附件。工作台弹出授权框，显示发起 Agent、接收者和文件清单。用户可单次授权、永久授权此 Agent，或拒绝。永久授权可在发件 Agent 的外部协作设置撤销，接收方仍需允许上传。

支持 Markdown、TXT、Excel、PowerPoint、常用图片和 ZIP。每批最多 20 个文件，合计最多 20 MiB。Markdown 单文件最多 1 MiB。ZIP 按原文件传输。接收方可在协作信箱下载。已有的任务回信 `replyTo` 仍支持通过 `document` 附上 Markdown。

## 部署

使用 .NET 10 发布 `src/FF.AgentCollaboration.Signaling/FF.AgentCollaboration.Signaling.csproj`。从发布目录启动程序，配置写入该目录的 `appsettings.json`。

```json
{
  "Urls": "https://0.0.0.0:5190",
  "Signaling": {
    "PublicOrigin": "https://203.0.113.10:5190",
    "CertificatePath": "data/signaling.pfx"
  },
  "InternalService": {
    "Key": "替换为部署专用的高熵密钥"
  }
}
```

替换示例公网 IP。用密码管理器生成至少 32 个随机字母和数字作为签发密钥，或在 Windows PowerShell 中生成。

```powershell
$signingBytes = New-Object byte[] 32
$signingRng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $signingRng.GetBytes($signingBytes) } finally { $signingRng.Dispose() }
[BitConverter]::ToString($signingBytes).Replace('-', '')
```

信令服务使用公网 IP 生成带 IP SAN 的自签证书，有效期 90 天，剩余 30 天内自动续期并沿用同一密钥。证书文件包含私钥，需要存放在仅服务账户可访问的目录并保留备份。首次启动后，从服务器控制台复制 `FF_SIGNALING_SPKI_SHA256` 后的值，用可信渠道核对这一指纹。

在两端电脑的本机工作台打开「设置 → 信令服务器」，输入公网 HTTPS 地址和公钥指纹。指纹可直接粘贴带 `FF_SIGNALING_SPKI_SHA256` 的整行输出。由工作台签发链接时，再填写服务器的 `InternalService.Key`。只加入的工作台留空即可。点「保存信令设置」，再点「测试信令连接」验证地址和指纹。新邀请立即使用保存的设置，正常续期无需重填指纹。地址或指纹更换时需要重新填写签发密钥。

信令设置和 STUN/TURN 配置以加密数据存入 SQLite。Windows 用运行账号的 DPAPI 保护解密密钥，密钥环保存在 `%LOCALAPPDATA%\FF.AgentCollaboration\signaling-keys`。更换电脑或 Windows 账号后，可在新工作台重新填写信令配置。

Windows 工作台发布时自动构建并附带 `FF.AgentCollaboration.WebRtc.exe`。发布机需要安装 Go 1.24 或更高版本，并可在 PATH 中运行 `go`。也可传 `-p:WebRtcBridgeFile=<组件绝对路径>` 使用已构建的组件。使用发布包时，将整个目录解压，工作台会加载同目录的组件。已单独部署组件时，可在 `appsettings.json` 的 `Signaling.BridgeExecutable` 指定路径。

从仓库根目录发布 Windows x64 工作台。

```powershell
dotnet publish src/FF.AgentCollaboration -c Release -r win-x64 --self-contained true
```

升级时请同时更新信令服务和两端工作台，完整复制发布目录（含 WebRTC 组件），再生成新的分享链接。两端只要能访问公网 HTTPS/WebSocket 端口，就可通过该端口建立内置 TURN 通道。服务器在同一端口号上监听 UDP STUN，开放该 UDP 端口可帮助两端发现公网映射。UDP 受限时仍可走 HTTPS 中继。使用反向代理时需允许 WebSocket 长连接。

内置 TURN 只接收持有房间票据的两端。中继地址位于房间私有的虚拟网络中，文件流量经中继时占用服务器带宽。需要使用已有的其他 STUN/TURN 服务时，可在工作台设置的「其他 STUN / TURN 服务器（可选）」中添加。

使用已有反向代理时，可留空 `CertificatePath`，默认监听 `http://127.0.0.1:5190`，并由同机代理终止 TLS 和转发 WebSocket。此时工作台配置代理对外证书的公钥指纹。签发密钥仅配置在受信任的后端，加入方使用房间的一次性码。

## 排查分享问题

| 提示 | 处理方式 |
| --- | --- |
| 未配置信令服务器 | 在「工作台配置 → 信令服务器」保存地址和公钥指纹。 |
| 缺少 WebRTC 后台组件 | 解压完整工作台发布包，确认组件与工作台程序在同一目录。 |
| 重启工作台后生效 | 组件已就位，重启工作台进程以启用监听。 |
| 生成链接需要签发密钥 | 在发起方填入与信令服务相同的签发密钥。 |
| 信令设置无法解密 | 用当前运行工作台的 Windows 账号重新保存配置。 |
| 测试信令连接失败 | 核对公网 IP、端口、防火墙及 `FF_SIGNALING_SPKI_SHA256` 指纹。 |
| 信令测试通过，申请一直显示正在申请 | 确认双方在线、三端均已升级，信令发布目录含 WebRTC 组件，反向代理允许房间的 `/connect` 和 `/relay` 两个 WebSocket 路径。 |

## 协议

| 方法与路径 | 认证与行为 |
| --- | --- |
| `POST /api/signaling/v1/invitations` | `X-Lx-Service-Key`。发送 `nodeId`、`windowId`、`pin`，返回 `roomId`、`link`、`hostTicket` 和 `expiresAt` |
| `POST /api/signaling/v1/rooms/{roomId}/claim` | 在 `Authorization: Bearer <joinCode>` 中提交一次性加入码，返回来宾 `ticket` 和 `expiresAt` |
| `GET /api/signaling/v1/rooms/{roomId}/connect` | 持 `hostTicket` 或来宾 `ticket` 升级为 WebSocket |
| `GET /api/signaling/v1/rooms/{roomId}/relay` | 持同房间票据建立二进制 WebSocket，承载 TURN/TCP；每个角色同时一条连接 |

房间自签发起有效 15 分钟。每个角色可建立一条连接，断线后可在房间有效期内用本角色的票据重连。双方连接时收到 `{"type":"ready"}`，一方断线时另一方收到 `{"type":"peer-offline"}`。重新连齐时由发起方重新发起协商。

发起方发送 `{"type":"offer","sdp":"…"}`，加入方发送 `{"type":"answer","sdp":"…"}`。协议也支持 `{"type":"ice","candidate":{…}}` 与 `{"type":"ice-complete"}`。房间票据到期后，已经建立的直连和中继连接可继续传输。信令服务重启会断开内置中继，并清除内存中的房间。需要重新配对时，由提供方生成新分享链接。

运行 `tests/check_signaling.ps1 -Dll <信令 DLL>` 可验证邀请与信令。运行 `tests/check_signaling_tls.ps1 -Dll <信令 DLL> -WorkbenchDll <工作台 DLL>` 可验证 TLS 续期及工作台指纹校验。
