---
name: ff-agent-collab
description: 在 FF.Agent 工作台使用窗口专属 MCP 查找协作者、交接任务与文档、报告进展、关联回信或交付图片时使用。
---

<!-- Managed by FF.AgentCollaboration -->
# FF.Agent 工作台协作

## 1. 身份与寻址

用户定义窗口身份、职责及本次授权范围。窗口显示名可以修改；并行任务用消息编号关联。发件身份由本窗口的连接凭据决定。同话题使用 `@名称`，跨话题使用 `@话题/名称`，重名时使用完整窗口或远程胶囊 ID。跨机回信用原信封给出的 `@reply-<route>`。专用审阅窗口只能访问当前授权来信和回信地址。

## 2. 信封与两类状态

`MSG_ID` 标识本条消息，`FROM_AGENT` 是来源，`MSG` 是正文。`SUMMARY` 配合 `HANDOFF_DOC` 指向交接文档，`ATTACH_FILES` 列出附带路径，`RETURN_HINT` 说明回送要求。关联回信的 `REPLY_TO` 指向原任务，`REPLY_KIND` 区分进展和最终结果。遇到 `EXTERNAL_COLLAB_POLICY: READ_ONLY`，仅审阅并回信，禁止写文件、提交或推送。

投递成功返回 `queued`，终端确认提交后显示已送达。协作状态独立记录。开始处理某条任务时上报 `working`；无须回信的任务做完后上报 `completed`，要求回信的任务用关联的最终结果结案。同一窗口收到多个任务时，逐条使用各自的 MSG_ID。普通回信无须再次回执。

## 3. MCP 接口

以下工具都由窗口专属的 `lx_collab` 提供。工具结果在 MCP 的 `content[0].text` 内；结构化结果是 JSON 文本。窗口在线改名或修改身份后，后续成功调用会在额外的文本块中提示最新身份。

| 工具 | 调用参数 | 成功结果 |
|---|---|---|
| `list_windows` | `detail?`、`allTopics?` 均为布尔值，默认 false。本地条目默认含 name、online；role 有值才返回。detail 增加 id、engine、project；allTopics 增加 topic。当前话题的远程条目含胶囊 id、node，detail 时带 remote。 | 窗口对象数组 |
| `send_to_agent` | 新消息必填字符串 target、message。可选 document 为项目或所属话题归档内已有 Markdown 路径，此时摘要最多 500 字符。files 为发件项目内已有文件路径数组，最多 20 个；跨端使用单份 document。autoReturnHint 为布尔值或不超过 1000 字符的自定义回信要求。supersedePrevious 默认 true，独立任务显式设 false。replyTo 为原 MSG_ID（8 位或完整）；finalReply 默认 true，仅用于关联回信。 | `{"id":"…","status":"queued","document":null}`，可能附 hint |
| `send_to_agent` 重发／撤回 | resend 为自己原消息的完整 ID，可附 target 改投；原文档与附件复用。cancel 传完整 ID，或传 true 加 target 撤回最近一条可撤回消息。 | 重发返回新的 queued 编号；撤回返回 id、status 为 cancelled |
| `report_task_status` | 必填 messageId、status，两者为字符串。status 取 working 或 completed；只对本窗口实际收到的任务有效。 | `{"status":"working"}` 或 `{"status":"completed"}` |
| `read_handoff_doc` | 必填字符串 pathOrId，使用实际 HANDOFF_DOC 路径、完整编号或唯一的 8 位 MSG_ID。 | Markdown 正文 |
| `finish_topic` | 必填字符串 summary，最多 2000 字符。仅在获准结束当前话题时调用。 | 含 id、status、summary 的对象 |
| `read_collaboration_skill` | 空对象 | 当前运行目录中的本手册原文 |

## 4. JSON 调用示例

每行是一次 tools/call 的 params。名称、编号及文件路径替换为现场值；按当前任务选用对应调用。

```jsonl
{"name":"list_windows","arguments":{"detail":true,"allTopics":true}}
{"name":"send_to_agent","arguments":{"target":"@接口","message":"请核对协议版本与兼容性","document":"docs/protocol.md","autoReturnHint":true}}
{"name":"send_to_agent","arguments":{"target":"@接口","message":"另查错误码的覆盖范围","supersedePrevious":false}}
{"name":"report_task_status","arguments":{"messageId":"1258ab45","status":"working"}}
{"name":"read_handoff_doc","arguments":{"pathOrId":"1258ab45"}}
{"name":"send_to_agent","arguments":{"target":"@发起方","replyTo":"1258ab45","message":"已核对请求格式，正在核对响应","finalReply":false}}
{"name":"send_to_agent","arguments":{"target":"@发起方","replyTo":"1258ab45","message":"协议兼容，回归通过","document":"docs/review.md","finalReply":true}}
{"name":"report_task_status","arguments":{"messageId":"a317f091","status":"completed"}}
{"name":"send_to_agent","arguments":{"cancel":"1258ab4512344321ab1234567890cdef"}}
{"name":"send_to_agent","arguments":{"resend":"1258ab4512344321ab1234567890cdef","supersedePrevious":false}}
{"name":"send_to_agent","arguments":{"target":"@reply-0123456789abcdef0123456789abcdef","replyTo":"01234567","message":"只读审阅完成，接口字段一致","finalReply":true}}
{"name":"read_collaboration_skill","arguments":{}}
{"name":"finish_topic","arguments":{"summary":"用户已验收，本话题结束"}}
```

向当前用户交付图片时，先确认最终图片文件存在。当前客户端如果提供 `show_image`，使用其实际工具名和参数显示图片；没有此工具时，回复可打开的本地文件链接。用户粘贴截图询问问题时，直接回答问题。调试中间图留作自查。

## 5. 常见错误

- `delivery_uncertain` 表示终端内可能已有消息，先核对原会话，确认未执行再重发；一次重发会产生新编号。
- 撤回和重发是独立调用。撤回参数不与正文或文档混传。覆盖仅针对自己发给同一目标的可撤回消息，无法中断已开始的工作。
- 带 RETURN_HINT 的任务用 replyTo 回信结案。中途的“收到”显式传 finalReply=false，关联回信不用 autoReturnHint。
- 只有 MSG 的信封直接阅读正文。短编号有冲突时使用完整编号，路径不存在或超出授权目录时先更正路径。
