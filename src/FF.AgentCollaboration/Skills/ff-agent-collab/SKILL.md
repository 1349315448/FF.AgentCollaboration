---
name: ff-agent-collab
description: 在 FF.Agent 工作台使用窗口专属 MCP 查找协作者、交接任务与文档、报告进展、关联回信或交付图片时使用。
---

<!-- Managed by FF.AgentCollaboration -->
# FF.Agent 工作台协作

## 回答用户如何操作工作台

用户问功能或入口时，从下表定位。先说工作台里的按钮和点击顺序，再说明适用条件。用户只提问时，直接解释操作。用户明确要求执行协作操作时，才调用对应 MCP 工具。

| 用户想做什么 | 工作台入口及结果 |
| --- | --- |
| 添加项目、设置知识库 | 左侧「＋ 项目」→ 打开目录 → 选知识库模式 →「选用此文件夹」；以后双击左侧项目可修改。知识库管理使用运行工作台的电脑。 |
| 新建或恢复 Agent | 打开小组 →「本组成员」旁点「＋Agent」→ 选引擎并按提示登录 → 新建或恢复会话。电源按钮停止或恢复原窗格。 |
| 删除已归档小组 | 左侧展开「已归档」，在小组行点「删除」并确认。该组终端会关闭，项目文件、归档文档和原生会话会保留。 |
| 更换模型与会话 | 双击 Agent →「基本设置」→「启动模型」→ 保存，下次开启同一 Agent 时应用。改会话用「会话管理」。原生 `/model` 在当前 CLI 会话生效，是否记住由引擎决定。 |
| 小组交接、查看结果 | 用户在群聊输入 `@` 并确认收件人，可选多人或附文件。消息下方显示接收和处理进度。Agent 在 CLI 用 `send_to_agent` 指定收件人，回信关联原消息 ID。 |
| 组长发通知 | 红旗组长从本话题的 `@Agent` 选择器选 `@全部`。用户明确要求群发时调用 `send_to_agent(target="@全部", broadcast=true)`，每位本地组员有自己的消息 ID。只有用户点名的授权外部组员才写入 `externalTargets`。群发保留此前的待办。 |
| 添加外部 Agent | 提供方从目标 Agent 分享链接。申请方点自己小组的「＋Agent → 协作 Agent」，粘贴链接并发送申请。提供方在群聊点「同意」，确认权限和次数后生效，结果回到申请方群聊。 |
| 跨端整组合作 | 提供方点小组行的「协作分享」并复制邀请。申请方点自己小组的「＋协作小组 → 外部小组」并发送申请。双方全组参与，提供方在群聊审批，结果回到申请方群聊。通过群聊 `@` 或 MCP 联系获批成员，正文 `#` 仅引用名称。 |
| 分工、移交和查任务记录 | 组长按 `read_team_skill` 发起团队提案，由用户确认。点顶栏「知识库」打开总计划与成员任务记录。知识库接口与写权限通过 `read_knowledge_skill` 查看。 |
| 看文件、Git、MCP 与技能 | 「查看 → 代码模式」打开项目文件、差异和只读媒体预览。MCP 管理和 Skills 管理在「工具」菜单，按引擎、项目查看或设置。 |

## 1. 身份与寻址

用户定义窗口身份、职责及本次授权范围。发件身份由本窗口的连接凭据决定。同组使用 `@名称`。本机跨组要由用户先关联同项目小组，双方获准的成员用 `@小组/名称` 联系。重名时使用完整窗口或远程胶囊 ID。跨机回信用原信封的 `@reply-<route>`。专用审阅窗口只能访问当前授权来信和回信地址。

## 2. 信封与两类状态

受限协助的 `list_windows` 会列出已授权的申请方联系人并标记 `returnContact: true`。来信中的 `RETURN_CONTACT` 是发件 Agent 的联系人 ID。常驻协作的对象标记为 `permanentGroup: true`，使用返回的 `target` 精确寻址，避免同名串组。双方选中的成员都可以直接发起协作。回应原任务使用原信封的 `@reply-…` 和 `replyTo`，可附获准的 Markdown。

`MSG_ID` 标识本条消息，`FROM_AGENT` 是来源，`MSG` 是正文。`SUMMARY` 配合 `HANDOFF_DOC` 指向交接文档，`ATTACH_FILES` 列出附带路径，`RETURN_HINT` 说明回送要求。关联回信的 `REPLY_TO` 指向原任务，`REPLY_KIND` 区分进展和最终结果。遇到 `EXTERNAL_COLLAB_POLICY: READ_ONLY`，仅审阅并回信，禁止写文件、提交或推送。

发送后返回 `queued`。收到原生信箱通知时，调用 `receive_message` 领取对应的完整消息；也可不传编号领取最早可收取的一封。返回 null 时结束本次检查。工具响应写出后，工作台登记已送达。协作状态独立记录。开始处理某条任务时上报 `working`；无须回信的任务做完后上报 `completed`，要求回信的任务用关联的最终结果结案。同一窗口收到多个任务时，逐条使用各自的 MSG_ID。普通回信无须再次回执。

### 投递与接收任务规范

- 发任务时写清项目、模块和入口，以及要完成的目标、范围、约束和验收方式。细节较多时用 `document` 附原文，正文保留摘要。收到「待补充」回信后，补上对方缺少的具体位置或前提。
- 询问或派任务须指定 `target` 或 `targets`。群聊无 @ 的消息留在本组。工作台用户的提问按 `GROUP_REPLY` 或 `RETURN_HINT` 中指定的地址和原 MSG_ID 回复。本机群聊使用 `@工作台用户`，跨端常驻协作按原信封使用 `@reply-…`。平台自动登记接收状态，省去「收到」回信。
- 对熟悉的任务，或能顺着入口读源码确定做法的任务，直接推进。只有会话和知识库仍无法确定位置或前提时，才上报 `need_info`，用 `replyTo` 关联原消息并加 `finalReply: false`，请发件人补充。澄清后对原消息重新上报 `working`。
- 碰到值得复用的新入口、业务规则或排查结论时，调用 `read_knowledge_skill`，按对应层级记录。已有准确记录的内容无需重复归档。

## 3. MCP 接口

以下工具都由窗口专属的 `lx_collab` 提供。工具结果在 MCP 的 `content[0].text` 内；结构化结果是 JSON 文本。窗口在线改名或修改身份后，后续成功调用会在额外的文本块中提示最新身份。

| 工具 | 调用参数 | 成功结果 |
|---|---|---|
| `list_windows` | `detail?`、`allTopics?` 均为布尔值，默认 false。本地条目含 name、online，按需返回 role 和 leader。detail 增加 id、engine、project；allTopics 增加 topic。当前话题的远程条目含胶囊 id、node，detail 时带 remote。 | 窗口对象数组 |
| `send_to_agent` | 新消息必填字符串 target、message。可选 document 为项目或所属话题归档内已有 Markdown 路径，此时摘要最多 500 字符。files 为发件项目内已有文件路径数组，最多 20 个。受限协助使用单份 document，常驻协作使用 document 或已获授权的 files。autoReturnHint 为布尔值或不超过 1000 字符的自定义回信要求。supersedePrevious 默认 true，独立任务显式设 false。replyTo 为原 MSG_ID（8 位或完整）；finalReply 默认 true，仅用于关联回信。 | `{"id":"…","status":"queued","document":null}`，可能附 hint |
| `send_to_agent` 重发／撤回 | resend 为自己原消息的完整 ID，可附 target 改投；原文档与附件复用。cancel 传完整 ID，或传 true 加 target 撤回最近一条可撤回消息。 | 重发返回新的 queued 编号；撤回返回 id、status 为 cancelled |
| `send_to_agent` 多收件人 | 新消息可用 `targets` 指定多个收件地址。默认保留已有待办，可用 `supersedePrevious: true` 覆盖同方向待办。 | 每位收件人有单独的投递编号 |
| `update_team_context` | 当前组长可传 `goal` 更新小组目标，或传 `roles` 数组更新本组成员职责。每项含 `agentId` 和 `role`。 | `{"status":"updated"}` |
| `send_peer_files` | 用于受限协助。新消息传远程 `target` 和 `message`，追加附件传原 `attachTo` ID。常驻协作通过 `send_to_agent` 的 `document` 发送 Markdown，普通文件可由工作台用户直接发送，或在 Agent 已获本机发文件授权时通过 `files` 发送。 | `awaiting_user` 时提醒用户核对接收方和文件。`queued` 表示已入队。 |
| `show_external_collaboration_approval` | `applicationId` 是系统通知给出的完整编号，只能打开本窗口收到的待审申请。 | `status: "awaiting_user"`。请让用户在工作台决定。 |
| `report_task_status` | 必填 messageId、status，两者为字符串。status 可取 working、need_info 或 completed，作用于本窗口实际收到的任务。 | 返回当前状态，如 `{"status":"need_info"}` |
| `read_handoff_doc` | 必填字符串 pathOrId，使用实际 HANDOFF_DOC 路径、完整编号或唯一的 8 位 MSG_ID。 | Markdown 正文 |
| `receive_message` | 可传通知内的完整 messageId 和 channelReceipt。空参数领取本窗口最早可接收的消息。 | 含 id、message 的信封，或 null |
| `finish_topic` | 必填字符串 summary，最多 2000 字符。仅在获准结束当前话题时调用。 | 含 id、status、summary 的对象 |
| `read_collaboration_skill` | 空对象 | 当前运行目录中的本手册原文 |
| `read_team_skill` | 空对象。平台按当前话题的实际身份选择手册。 | 组长或组员手册，含分工、记录、交出及接任职责的步骤 |
| `list_available_engines` | 空对象。查看本机已安装引擎及工作台已缓存的登录、额度和模型信息。 | 引擎目录，尚未检查的项目显示为未知 |
| `propose_team_agents` | 本话题组长可调用。必填 `summary` 和 `members`。每次提议 1 至 8 名成员，名称在话题内唯一。成员有 `name`、`role`（最多 200 字）和 `taskSummary`（最多 500 字），可带 `suggestedEngine`、`suggestedModel`、`suggestedEffort`。 | `proposalId`、`status: "awaiting_user_confirmation"`、`count` |

### 组队与交接

团队开始分工，或用户指定、移交、取消组长时，调用 `read_team_skill`。窗口凭据决定返回的手册。组长按手册拆分工作、跟踪验收和移交总计划；组员按原消息编号提交证据和回信。知识库读写细节见 `read_knowledge_skill`。

## 4. JSON 调用示例

每行是一次 tools/call 的 params。名称、编号及文件路径替换为现场值；按当前任务选用对应调用。

```jsonl
{"name":"list_windows","arguments":{"detail":true,"allTopics":true}}
{"name":"list_available_engines","arguments":{}}
{"name":"propose_team_agents","arguments":{"summary":"并行核对原生终端交互与文档","members":[{"name":"终端验证","role":"核对收件和提交行为，记录可复现的步骤","taskSummary":"验证长消息提交和终端回执，不改动投递协议","suggestedEngine":"codex"},{"name":"文档核对","role":"核对操作指南并整理差异","taskSummary":"列出界面和文档不一致的入口","suggestedEngine":"claude"}]}}
{"name":"receive_message","arguments":{}}
{"name":"show_external_collaboration_approval","arguments":{"applicationId":"0123456789abcdef0123456789abcdef"}}
{"name":"send_peer_files","arguments":{"target":"@联调","message":"请核对打包后的前端文件。","files":["release.zip","docs/screen.png"]}}
{"name":"send_peer_files","arguments":{"attachTo":"0123456789abcdef0123456789abcdef","files":["docs/cases.xlsx"]}}
{"name":"send_to_agent","arguments":{"target":"@接口","message":"请核对协议版本与兼容性","document":"docs/protocol.md","autoReturnHint":true}}
{"name":"send_to_agent","arguments":{"target":"@接口","message":"另查错误码的覆盖范围","supersedePrevious":false}}
{"name":"send_to_agent","arguments":{"targets":["@接口","@测试"],"message":"核对调试接口与测试覆盖范围。"}}
{"name":"send_to_agent","arguments":{"target":"@工作台用户","replyTo":"1258ab45","message":"接口验证通过，回归结果已确认。"}}
{"name":"report_task_status","arguments":{"messageId":"1258ab45","status":"working"}}
{"name":"report_task_status","arguments":{"messageId":"4b0a542c","status":"need_info"}}
{"name":"send_to_agent","arguments":{"target":"@发起方","replyTo":"4b0a542c","message":"请补充接口所属项目、路由与复现入口。","finalReply":false}}
{"name":"report_task_status","arguments":{"messageId":"4b0a542c","status":"working"}}
{"name":"read_handoff_doc","arguments":{"pathOrId":"1258ab45"}}
{"name":"send_to_agent","arguments":{"target":"@发起方","replyTo":"1258ab45","message":"已核对请求格式，正在核对响应","finalReply":false}}
{"name":"send_to_agent","arguments":{"target":"@发起方","replyTo":"1258ab45","message":"协议兼容，回归通过","document":"docs/review.md","finalReply":true}}
{"name":"report_task_status","arguments":{"messageId":"a317f091","status":"completed"}}
{"name":"send_to_agent","arguments":{"cancel":"1258ab4512344321ab1234567890cdef"}}
{"name":"send_to_agent","arguments":{"resend":"1258ab4512344321ab1234567890cdef","supersedePrevious":false}}
{"name":"send_to_agent","arguments":{"target":"@reply-0123456789abcdef0123456789abcdef","replyTo":"01234567","message":"只读审阅完成，接口字段一致","finalReply":true}}
{"name":"read_collaboration_skill","arguments":{}}
{"name":"read_knowledge_skill","arguments":{}}
{"name":"send_to_agent","arguments":{"target":"@全部","broadcast":true,"message":"请继续各自任务，联调验收以三条回归用例通过为准。"}}
{"name":"finish_topic","arguments":{"summary":"用户已验收，本话题结束"}}
```

### 给用户交付文件

完成本地任务时，把供用户查看的图片、视频、文档、发布包及校验清单放在一条群聊消息中。先确认文件存在，再调用 `send_to_agent`，目标填 `@工作台用户`，用 `files` 逐项列出实际项目路径，正文简述用途。每条最多 20 个文件，超过时分批发送。文件名可直接点击，支持的格式在右侧预览，发布包等定位到项目文件夹。CLI 中也可以同时展示结果。

交付网页、在线文档、发布页或下载地址时，把完整的 HTTP 或 HTTPS 网址写在同一条消息正文中，每行一个。仅交付网址也要发群聊。点击网址会打开浏览器新标签页。发给用户的链接使用其可访问的地址。

群聊交办时带上原消息的 `replyTo`，让这条清单同时作为回信。用户直接在 CLI 交办时省略 `replyTo`。跨端交付沿用获准的回信地址及附件权限。用户发截图提问时直接回答，只在有成果交付时发送清单。

```json
{"name":"send_to_agent","arguments":{"target":"@工作台用户","message":"操作视频、使用说明和 Windows 安装包已就绪。","files":["output/demo.mp4","docs/usage.md","output/workbench.zip"]}}
```

## 5. 常见错误

- `delivery_uncertain` 表示终端内可能已有消息，先核对原会话，确认未执行再重发；一次重发会产生新编号。
- 撤回和重发是独立调用。撤回参数不与正文或文档混传。覆盖仅针对自己发给同一目标的可撤回消息，无法中断已开始的工作。
- 带 RETURN_HINT 的任务用 replyTo 回信结案。中途的“收到”显式传 finalReply=false，关联回信不用 autoReturnHint。
- 只有 MSG 的信封直接阅读正文。短编号有冲突时使用完整编号，路径不存在或超出授权目录时先更正路径。

## 6. 工作台更新后继续任务

操作维护前，先将进度和下一步写入原任务记录。用 `resume_after_restart` 登记自己的恢复说明，正文带原任务 ID 或记录路径。

```json
{"message":"重启后继续验证原任务 MSG_ID abcd1234，从任务记录中的第 3 步开始。"}
```

用 `workbench_update` 的 `{"action":"check"}` 读取当前版本和签名发布渠道的可用版本。用户在本机 Agent 设置勾选「允许此 Agent 更新或重启本机工作台」并保存后，可用 `{"action":"apply","version":"1.0.5","mode":"idle"}` 安排更新（版本填此次查询结果），或用 `{"action":"restart","mode":"idle"}` 重启当前版本。权限被拒绝时，请用户在设置中决定是否授权。调用结束后等待工作台恢复；新进程通过检查并启动原会话时，恢复消息才进入收件队列。回滚后由用户选择待处理的恢复记录。
