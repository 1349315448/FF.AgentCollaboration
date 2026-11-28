# FF.AgentCollaboration

当前工作台版本 **Base v1.0.5**。

[下载 Windows x64 和 macOS Intel x64 工作台](https://github.com/1349315448/FF.AgentCollaboration/releases/tag/v1.0.6) · [Linux、Apple Silicon 和信令服务下载](https://github.com/1349315448/FF.AgentCollaboration/releases/tag/v1.0.3)

在群里点名时，信封从输入框飞向收件人。Agent 互发消息和回给用户时也会显示投递轨迹。单独添加的外部 Agent 与本机 Agent 一起列在话题的「本组成员」里，用链环图标标识。整组合作的成员可在「协作小组」下展开查看。[使用说明](docs/usage.md)

在一个浏览器窗口里，同时使用 Codex、Claude Code、GitHub Copilot 等原生 AI 终端。你可以让一个 Agent 写代码、另一个 Agent 审查，在同一页查看改动和回复。工作台可部署到 Windows、Linux 或 macOS，各 CLI 沿用自己的账号、模型和审批方式。

[操作指南](docs/usage.md)

## 快速开始

| 你拿到的是 | 如何启动 |
| --- | --- |
| Windows 发布包 | 完整解压，双击 `启动工作台.bat`。 |
| Linux / macOS 发布包 | 完整解压，在发布目录运行 `./FF.AgentCollaboration workbench-managed`，浏览器打开控制台打印的地址。 |
| GitHub 源码 | 安装 .NET 10 SDK。Windows 双击 `start-workbench.bat`，Linux 和 macOS 安装 Go 后用下方发布命令。 |

浏览器默认打开 `http://127.0.0.1:5186`。使用期间保持控制台打开，按 `Ctrl+C` 停止。

1. **初始化。** 第一次启动时，选择数据库、话题归档和交接文档的目录。默认保存在 `workbench-data` 下。
2. **打开项目。** 点左侧「＋ 项目」选择代码目录，再点项目行上的「＋」创建小组，例如「接口联调」。
3. **打开终端。** 在「Agent → 添加 Agent」中取名并选择一个 CLI。按提示登录后，选择新建或恢复会话。只需安装你要用的引擎。

独立部署包包含 .NET 运行时和 WebRTC 组件。Linux 和 macOS 包还包含 Unix PTY 程序，CLI 由你在运行工作台的机器上安装。跨内网协作可部署一套公网信令服务。两端填写服务地址和公钥指纹，分享方另填签发密钥。工作台会自动使用内置 STUN 和 TURN。[信令部署指南](docs/signaling.md#部署)

### 本地发布

发布机需要 .NET 10 SDK 和 Go 1.24 或更高版本。在仓库根目录运行以下命令，按目标电脑替换 `linux-x64`。同时支持 `win-x64`、`linux-arm64`、Apple Silicon 的 `osx-arm64` 和 Intel Mac 的 `osx-x64`。将完整输出目录复制到目标电脑。

```sh
dotnet publish src/FF.AgentCollaboration/FF.AgentCollaboration.csproj -c Release -r linux-x64 --self-contained true
```

从 Windows 复制 Linux 或 macOS 目录后，如文件没有执行权限，在目标电脑执行：

```sh
chmod +x FF.AgentCollaboration FF.AgentCollaboration.Pty FF.AgentCollaboration.WebRtc
./FF.AgentCollaboration workbench
```

工作台按本机 PATH 检测 CLI，也会检查常见用户安装目录。自定义安装可用 `FF_CODEX_EXE`、`FF_CLAUDE_EXE` 等变量指定完整路径。各引擎可用的系统与功能以实际安装的 CLI 版本为准。

## 工作台里可以做什么

| 功能 | 实际用法 |
| --- | --- |
| 多终端协作 | 小组群聊常驻，点击 Agent 在右侧打开终端。可以并排监看多个原生 CLI，切换小组时后台终端继续运行。 |
| Agent 交接 | 在群聊里输入 `@` 选择一位或多位收件人，输入 `#` 引用 Agent。消息下方显示接收人数、处理状态、回信和附件。无收件人的消息只保存到群聊。 |
| 文件与 Git 审查 | 切到「代码模式」，搜索和编辑项目文本，并排对照终端输出。可预览图片、视频、Markdown、PDF、Excel 和 PowerPoint，也可查看 Git 差异、暂存、提交和推送。 |
| 会话与身份 | 双击 Agent 名称，可修改名称和职责描述、保存启动模型、切换引擎或管理会话。启动模型在下次打开终端时生效。 |
| MCP 与 Skills | 从「工具」菜单打开「MCP 管理」或「Skills 管理」。可以查看各 CLI 的原生配置，也可以导入工具和技能并选择要使用的引擎。 |
| 跨电脑协作 | 在「小组 → 小组合作」邀请整个小组或借调指定 Agent。获准的成员可跨端点名、回信和共享附件。支持直接连接和后台 WebRTC。 |
| 组队与知识库 | 组长在本话题用 `@全部` 通知成员。成员独立维护任务记录，组长从总计划查看。 |

## 试一次代码审查

在同一小组创建「开发」和「审查」两个 Agent。在群聊输入 `@`，选中「审查」，写清楚要核对什么，再点「发给 1 位」。可粘贴截图或选择文件。

> 把这次接口改动和测试结果发给 @审查，请它检查错误处理，完成后把结论回给我。

选中的 `@Agent` 留在正文的光标位置。消息的「已接收」表示对应 Agent 已领取任务或终端已确认投递。Agent 根据正文判断是否回信，可以把答复发回原群聊。在「小组 → 小组合作」选择同项目的小组及参与成员，或者向外部工作台发出邀请。

## 支持的 CLI

| 引擎 | 命令 | 引擎 | 命令 |
| --- | --- | --- | --- |
| OpenAI Codex | `codex` | 腾讯 CodeBuddy Code | `codebuddy` |
| GitHub Copilot CLI | `copilot` | 千问 Qwen Code | `qwen` |
| Claude Code | `claude` | 智谱 ZCode | `zcode` |
| TRAE CLI 2 | `traecli` | Qoder CLI | `qodercli` |
| OpenCode | `opencode` / `opencode2` | Cursor CLI | `agent` / `cursor-agent` |
| Google Antigravity CLI | `agy` | | |

在「工作台配置 → CLI 引擎」检测安装状态、查看版本和更新方式。会话分支、额度查询等选项按各引擎的支持情况显示。

[图文使用指南](docs/usage.md) · [开发与架构](docs/architecture.md) · [Agent 协作手册](src/FF.AgentCollaboration/Skills/ff-agent-collab/SKILL.md)

项目自有代码采用 [Apache License 2.0](LICENSE)，第三方组件保留各自许可。
