# 工作台和信令服务更新

工作台用 `FF.AgentCollaboration workbench-managed`，信令服务用 `FF.AgentCollaboration.Signaling signaling-managed`。仍可在命令后追加原启动参数。Windows 工作台的发布版启动脚本已使用受管命令。

启动器留在首次解包的安装目录。它把新程序解包在安装目录的 `.ff-updates` 下，启动检查通过后切换活动版本。失败时回到上次通过检查的版本。继续用原安装目录的启动器启动。数据库、工作台配置、信令证书和配对数据仍由原安装目录或之前设置的数据路径管理。升级包中的数据库变更须允许上一个软件版本继续打开。

## 部署配置

从 Base v1.0.3 起，GitHub 官方发布包随附 `update-public.pem`，包内配置已指向该文件。首次部署或从旧版手动迁移时，请核对发布页的校验清单。保留原配置升级时，可将 `Updates:PublicKeyFile` 设置为 `update-public.pem`，然后按下表配置本机维护密钥。[官方验签公钥](update-public.pem)

在原安装目录的 `appsettings.json` 中配置以下字段。工作台和信令服务的维护密钥分别生成，用 64 位高熵十六进制文本。两个配置文件分别保存该文本 UTF-8 编码的 SHA-256 十六进制摘要。维护密钥原文通过安全渠道交给操作人。

| 配置 | 值 |
|---|---|
| `Updates:PublicKeyFile` | 发布签名的 ECDSA PEM 公钥文件。 |
| `Updates:ManifestUrl` | 受信任的 HTTPS 发布清单地址。省略时使用本项目 GitHub Releases 的 `ff-acp-update.json`。 |
| `Updates:AdminKeyHash` | 本组件独立的维护密钥摘要，用于浏览器维护操作。 |
| `Signaling:PairingsPath` | 信令持久化配对文件，默认 `data/signaling-pairings.json`。单个信令宿主使用一份文件，服务账号需要写权限。 |

信令服务升级会短暂重启，客户端使用现有配对重连。保留原信令配置、证书、公钥指纹和配对文件。首次启用自动续票需要更新两端工作台及信令服务。已经过期且从未完成续接登记的旧关系仍需用新邀请迁移一次。

将工作台安装和数据目录放在受信任的本机位置，保持签名公钥与维护配置的写权限由管理人员控制。

## 生成发布清单

发布机为每个目标系统和架构打好自包含包。Windows 使用 ZIP，Linux 和 macOS 使用带执行权限的 tar.gz。清单版本需要和包内应用版本一致。发布可共用一份清单，`assets` 逐项填写程序和架构。

```json
{
  "version": "1.0.3",
  "notes": "本次发布说明",
  "assets": [
    {
      "product": "workbench",
      "rid": "win-x64",
      "file": "FF.AgentCollaboration-v1.0.3-windows-x64.zip",
      "url": "https://example.org/releases/FF.AgentCollaboration-v1.0.3-windows-x64.zip"
    }
  ]
}
```

信令包的 `product` 为 `signaling`。常用 `rid` 为 `win-x64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。将 `file` 路径相对于清单输入文件存放。

```sh
python scripts/sign_update.py release-input.json /secure/path/release-private.pem ff-acp-update.json
```

上传清单和它列出的发布包。签名私钥存放于发布环境。客户端通过配置的公钥验证清单，并校验下载包的 SHA-256。内部开发可使用独立 HTTPS 发布渠道和独立签名密钥。

## Agent 更新闭环

```json
{"message":"重启后继续跑接口回归，结果写进原任务记录。"}
```

将上述参数发给 `resume_after_restart`。随后调用 `workbench_update` 检查版本，获准的 Agent 可提交以下更新请求。

```json
{"action":"apply","version":"1.0.3","mode":"idle"}
```

`action=restart` 用于原版本重启。默认等待已知安全的终端状态。正在输入、审批或宿主无法确认终端状态时，管理员可以在版本弹窗取消安排。Agent 更新只关联自己的待恢复说明。
