# 代码结构

工作台是一个 .NET 10 控制台程序。浏览器连接本机 HTTP 和 WebSocket。每个窗格拥有一个 ConPTY 子进程，每个 CLI 通过独立的 stdio MCP 子进程收发交接。

显式指定 `--lan-http-port` 后，宿主在私网网卡开设独立的 HTTP 开发入口。仅环回端可签发配对码和撤销远程浏览器。配对会话绑定客户端地址，覆盖静态页面、API、SSE 与终端 WebSocket，撤销后宿主断开已建立的连接。联邦 HTTPS 仍独立校验分享凭据和节点证书指纹。

联邦在选定网卡上复用工作台端口并单独绑定 HTTPS。默认使用私网 IPv4。提供方可在本机配置对外 HTTPS 地址，分享链接使用该地址的主机与端口，支持直接监听和 TCP 端口映射。宿主按匹配的端点、实际监听地址和 Host 隔离控制面。分享链接携带节点、窗口和证书指纹。提供方用单窗授权控制收信，用 SQLite 短事务处理去重、配额和回执。请求方通过出站 HTTPS 提交任务，通过出站 SSE 接收回信，每次落库时一起提交游标。工作台数据库及其备份含节点私钥和对端凭据，应限本机用户访问。

跨端 MCP 通过 SQLite 发件箱持久化。请求方先上传 Markdown，再用同一请求编号提交消息。提供方在同一事务内绑定文档、扣额、登记回信地址并创建本地收件。回信的附件引用与 SSE 游标同时落库，由独立的待下载记录补收。原发件窗口已关闭时，仍可从信箱取回回信文档。受管文件只存放在数据目录的 `federation-inbox` 下。专用审阅窗口按已领取请求限制 MCP 可见范围，共享读写会话同时显示只读协议提示和 Git 审计结果。

`POST /api/federation/uploads` 的回执包含 `uploadId` 和 `pathHint`。后者展示相对于对端数据目录的受管位置。随后发送消息时用 `uploadId` 绑定文件，接收终端由本机解析实际路径。

### 原生会话的投递回执

引擎通过 `Engine.SubmissionReceipts` 为已绑定的会话提供可选回执。投递前，`ISubmissionReceiptSource.Begin` 记录该会话日志的读取起点。投递后只检查新增的原生用户消息，核对消息 ID 和完整正文。Codex 与 CodeBuddy 已接入。能够读取权威记录时，单纯的屏幕重绘不能确认送达。处于原生队列的消息也可以用该队列明确显示的消息编号确认接收。

窗口的自动接收开关和收件方式独立保存。默认等待空闲。选「立即接收」时，适配器还须确认 CLI 正在执行且原生编辑器空白可用，才会将消息交给它。手工草稿和审批界面始终阻止自动写入。收件等待、原生提交与任务执行完成分别判断。

## 从功能定位代码

以下目录均位于 `src/FF.AgentCollaboration`。

| 改动内容 | 入口 | 职责 |
| --- | --- | --- |
| 命令与启动 | `Program.cs`、`Application/WorkbenchStartup.cs` | 选择运行模式、识别旧库、初始化并持有数据目录锁 |
| HTTP、SSE 与 WebSocket | `Presentation/Http/` | 注册路由、校验本机来源、推送页面状态变更、传递终端字节与尺寸 |
| MCP | `Presentation/Mcp/`、`Application/HandoffService.cs` | 解析 stdio 调用，按窗口授权发送、重发、读取交接文档 |
| 领域模型与规则 | `Domain/` | 话题、窗口、收件、快捷指令及消息文本格式。领域代码只依赖标准库 |
| 窗口与会话 | `Application/Workbench.cs`、`Application/SessionCatalog.cs` | 创建、恢复、切换、释放窗口，汇总状态，查询历史、模型和额度 |
| 原生引擎 | `Engines/` 下的各 CLI 适配器 | 将引擎统一接口转换为各自的启动参数、协议请求和历史操作 |
| SQLite | `Infrastructure/Storage/` | 模式升级、事务、投递认领、配置与一致性备份。按窗口、收件、偏好和话题分别维护 SQL |
| 终端与文件 | `Infrastructure/Terminal/`、`Infrastructure/Windows/`、`Infrastructure/Files/` | 管理 ConPTY 与进程树、回放终端输出、校验路径、读写归档 |
| 浏览器 | 仓库根目录的 `web/src/` | React 组件负责工作台界面。`store/workbenchStore.ts` 保存快照和话题布局，`services/terminalRegistry.ts` 按窗口 ID 管理 xterm 与 WebSocket。`runtime/terminal.js` 处理 ConPTY 字节流、提示符和提交确认 |
| 命令行协作验收 | `Coordination/` | 独立的双向交接与断线恢复场景，使用自己的运行目录和数据库 |

前端开发在 `web` 目录运行 `npm ci` 和 `npm run dev`，使用 `FF_WORKBENCH_URL` 指定本机后端。`npm run build` 执行类型检查、生成 `wwwroot`，并将静态资源同步至 Debug 与 Release。仓库随附构建产物，日常双击启动脚本只需要原有的 .NET 环境。

## 三条关键调用路径

工作台通过 `GET /api/engines/capabilities` 读取各引擎的模式、会话操作、模型与推理选项支持。适配器声明能力，建窗表单和会话抽屉使用同一份清单。动态模型、历史和额度仍通过各自接口读取。额度的 `status` 区分可用、无限额、不支持和暂时不可读。

**打开窗口。** HTTP 接收建窗请求，工作台服务验证项目、话题和历史绑定。对应引擎生成新会话或分支的原生参数并注入窗口 MCP 授权。终端层启动进程，浏览器通过 WebSocket 重连同一窗格。Codex 与 TRAE CLI 2 共用 app-server 协议。Copilot 使用 ACP，Claude 和 CodeBuddy 读取项目会话文件，千问使用 `qwen sessions`。ZCode 使用自己的 app-server 协议创建、命名和查询会话，再用 `--resume` 打开终端。

ZCode 的窗口专属 MCP 配置位于工作台数据目录的 `engine-profiles/<window-id>/`。窗格沿用用户的模型凭据、会话库和项目配置，独立的用户级配置包含工作台 MCP 和会话库位置。窗口授权只通过进程环境传递。

**发送交接。** MCP 从进程环境取窗口授权，应用服务校验收件地址、正文、摘要和重发参数。存储层在同一事务中核对身份、解析目标、登记附件和消息。写终端前先把消息从 `pending` 认领为 `writing`，再根据原生输入状态记录结果。只有仍处于 `pending` 的消息可重新认领。若宿主在提交期间退出，下次启动将其标为待核对。首次看到新消息时，页面播放一次交接动画。信封从发件方指尖沿弧线旋转，水平滑入收件方邮箱。

**首次运行。** 工作台先检查已有数据库和可迁移的旧库。全新安装只开放初始化状态和目录浏览。用户确认三个目录后，服务在目标目录中准备临时 SQLite、保存目录设置、发布数据库，再开放工作台 API 并唤醒投递循环。普通启动直接打开已有数据库。引导配置记录下次启动要使用的位置。

**消息和状态更新。** MCP 提交 SQLite 事务后通过本机回环通知工作台。宿主用容量为 1 的 Channel 合并唤醒信号，读取各窗口最早的待投递消息。终端静默期到点时再检查能否写入。宿主启动时和每隔 5 秒也会核对 SQLite，以处理断线遗漏和旧版 MCP 的写入。每个浏览器页面共用一条 `/api/events` SSE 连接，收到通知或重连成功时重新取快照。各窗格的输入、输出和尺寸继续走各自的 WebSocket。

## 验证入口

- `tests/check_workbench.py` 覆盖首次建库、旧库迁移、窗口授权、消息重发、原生投递、进程释放和额度。
- `tests/check_recovery.py` 检查 CLI 协作丢失确认时的恢复。
- `tests/check_topics.py` 与 `--create` 分别验证工作台交互与建窗流程。在 Windows 安装 Python、Node.js、Playwright 和 Chromium 后运行。可用 `CHROME_PATH` 指定浏览器。
- `tests/check_session_history.py` 使用已安装的 CLI 和临时会话。`tests/check_claude_fork.py` 使用本地模拟模型端点验证 Claude 分支。
