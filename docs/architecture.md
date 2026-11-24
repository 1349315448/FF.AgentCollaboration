# 代码结构

工作台是一个 .NET 10 控制台程序。浏览器连接本机 HTTP 和 WebSocket。每个窗格拥有一个 ConPTY 子进程，每个 CLI 通过独立的 stdio MCP 子进程收发交接。

## 从功能定位代码

以下目录均位于 `src/FF.AgentCollaboration`。

| 改动内容 | 入口 | 职责 |
| --- | --- | --- |
| 命令与启动 | `Program.cs`、`Application/WorkbenchStartup.cs` | 选择运行模式、识别旧库、初始化并持有数据目录锁 |
| HTTP 与 WebSocket | `Presentation/Http/` | 注册路由、校验本机来源、映射错误、传递终端字节与尺寸 |
| MCP | `Presentation/Mcp/`、`Application/HandoffService.cs` | 解析 stdio 调用，按窗口授权发送、重发、读取交接文档 |
| 领域模型与规则 | `Domain/` | 话题、窗口、收件、快捷指令及消息文本格式。领域代码只依赖标准库 |
| 窗口与会话 | `Application/Workbench.cs`、`Application/SessionCatalog.cs` | 创建、恢复、切换、释放窗口，汇总状态，查询历史、模型和额度 |
| 原生引擎 | `Engines/Codex/`、`Engines/Copilot/`、`Engines/Claude/` | 将引擎统一接口转换为各自的启动参数、协议请求和历史操作 |
| SQLite | `Infrastructure/Storage/` | 模式升级、事务、投递认领、配置与一致性备份。按窗口、收件、偏好和话题分别维护 SQL |
| 终端与文件 | `Infrastructure/Terminal/`、`Infrastructure/Windows/`、`Infrastructure/Files/` | 管理 ConPTY 与进程树、回放终端输出、校验路径、读写归档 |
| 浏览器 | `wwwroot/` | `workbench.js` 负责窗格与布局，`terminal.js` 处理原生提示符与字节流。会话抽屉、额度、设置、首次初始化、话题和交接动画有独立脚本 |
| 命令行协作验收 | `Coordination/` | 独立的双向交接与断线恢复场景，使用自己的运行目录和数据库 |

## 三条关键调用路径

**打开窗口。** HTTP 接收建窗请求，工作台服务验证项目、话题和历史绑定。对应引擎生成新会话或分支的原生参数并注入窗口 MCP 授权。终端层启动进程，浏览器通过 WebSocket 重连同一窗格。修改某个 CLI 的参数或协议字段时，从该引擎的适配器开始。Codex 使用 app-server，Copilot 使用 ACP，Claude 使用原生 CLI 和本地会话历史。

**发送交接。** MCP 从进程环境取窗口授权，应用服务校验收件地址、正文、摘要和重发参数。存储层在同一事务中核对身份、解析目标、登记附件和消息。写终端前先把消息从 `pending` 认领为 `writing`，再根据原生输入状态记录结果。只有仍处于 `pending` 的消息可重新认领。若宿主在提交期间退出，下次启动将其标为待核对。页面的两次信封飞行共享同一条消息 ID。

**首次运行。** 工作台先检查已有数据库和可迁移的旧库。全新安装只开放初始化状态和目录浏览。用户确认三个目录后，服务在目标目录中准备临时 SQLite、保存目录设置、发布数据库，再开放工作台 API 并唤醒投递循环。普通启动直接打开已有数据库。引导配置记录下次启动要使用的位置。

## 验证入口

- `tests/check_workbench.py` 覆盖首次建库、旧库迁移、窗口授权、消息重发、原生投递、进程释放和额度。
- `tests/check_recovery.py` 检查 CLI 协作丢失确认时的恢复。
- `tests/check_topics.py` 与 `--create` 分别验证工作台交互与建窗流程。在 Windows 安装 Python、Node.js、Playwright 和 Chromium 后运行。可用 `CHROME_PATH` 指定浏览器。
- `tests/check_session_history.py` 使用已安装的 CLI 和临时会话。`tests/check_claude_fork.py` 使用本地模拟模型端点验证 Claude 分支。
