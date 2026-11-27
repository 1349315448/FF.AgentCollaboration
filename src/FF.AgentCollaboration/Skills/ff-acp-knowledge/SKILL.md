---
name: ff-acp-knowledge
description: 在 FF.Agent 工作台查找项目知识、读取话题计划及个人记忆、记录可复用经验，或为确实缺少前提的协作任务回信澄清。
---

# 工作台知识库

团队分工、接任和卸任步骤见 `read_team_skill` 返回的当前角色手册。

## 优先使用已有知识库

先查项目或 CLI 是否已有维护中的知识库或记忆系统。有就沿用原位置和使用方式。经本话题组长确认，可在 `project/INDEX.md` 简要登记名称、实际路径与查阅入口，方便其他成员找到原文。

项目和 CLI 缺少对应记录时，可在平台 `.ff-acp-knowledge` 保存有复用价值的知识。团队协作的 `TEAM.md`、`PLAN.md` 和个人任务经验也可以留在平台，既有资料以原系统为准。

能从当前对话、熟悉的模块或源码确定位置和方案时直接处理。需要补足项目背景时，先用 `kb_team_context` 了解当前话题的组长、成员、任务计划和个人记忆，再用短查询找相关条目。

只有当前上下文、源码和知识库仍不足以确定目标或前提时，才将原消息上报为 `need_info`，并用 `replyTo` 与 `finalReply: false` 回信，明确说出需要补充的位置、目标或约束。收到澄清后，对原消息重新上报 `working`。

## 查询与写入

| 工具 | 参数与结果 |
|---|---|
| `kb_team_context` | 参数为空。返回当前话题组长、成员和共享计划摘要。已有个人 `MEMORY.md` 时还会返回其摘要。按任务保存的个人记录用 `kb_search` 查询。 |
| `kb_search` | 可传 `query`、`scope`、`theme`、`keywords`、`topicId`、`limit` 和 `offset`。传 `recordsOnly=true` 浏览本话题的成员任务记录。结果含知识库相对路径、层级、标题、主题、关键词、状态、更新时间和摘要。 |
| `kb_read` | 传查询返回的相对 `path`。结果含 Markdown 原文、Frontmatter 和文档版本。 |
| `kb_write` | 必填 `scope`、`path`、`content`。可传 `title`、`theme`、`keywords`、`status`。更新已有总计划、团队名册或话题任务记录时，必传 `kb_read` 返回的 `version` 到 `expectedVersion`。版本冲突时重读，并合并自己的修改。 |

`scope` 可取 `project`、`topic` 或 `agent`。项目知识以及当前话题的 `PLAN.md`、`TEAM.md`、`SUMMARY.md` 由人工指定的组长维护。成员在当前话题的 `records/` 记录供团队查阅的进度、证据和交付结果。在自己的 Agent 目录下按任务写入 `tasks/`，保存个人排查过程与经验。组长自己的任务经验也按个人记录处理。文档的分享范围由该项目的知识库设置决定。调用窗口所能看到的内容以服务返回为准。

记录新发现的模块入口、经过验证的排查结论、关键决策和能复用的任务经验。给记录填写 `theme` 与少量 `keywords`，方便后续查找。状态可用 `planned`、`working`、`done`、`blocked`。日常小改动沿用正常协作回执。

## JSONL 调用示例

```jsonl
{"name":"kb_team_context","arguments":{}}
{"name":"kb_search","arguments":{"query":"终端投递","scope":"topic","theme":"消息投递","keywords":["确认回执"],"limit":8}}
{"name":"kb_read","arguments":{"path":"topics/原生终端-a1b2c3/PLAN.md"}}
{"name":"kb_write","arguments":{"scope":"agent","path":"tasks/终端回执排查.md","title":"终端回执排查","theme":"消息投递","keywords":["确认回执","终端"],"status":"done","content":"以接收端的投递确认区分排队和已接收。"}}
{"name":"report_task_status","arguments":{"messageId":"aabbccdd","status":"need_info"}}
{"name":"send_to_agent","arguments":{"target":"@发件人","replyTo":"aabbccdd","finalReply":false,"message":"请补充受影响的项目、页面入口和预期结果。"}}
{"name":"report_task_status","arguments":{"messageId":"aabbccdd","status":"working"}}
```
