# LLM Provider 协议矩阵（2026-07-25）

本文件记录 `OpenAiCompatibleClient` 只在厂商官方协议已确认时启用的差异。未知 OpenAI 兼容端保持最小请求面，并通过显式错误做能力降级；不得用宽泛的 `HTTP 400 invalid request` 猜测某字段不受支持。

下列官方依据均于 **2026-07-25（Asia/Shanghai）** 实际访问。

所有供应商共用同一套工具选择策略：工具工作流始终发送当前现场完整、稳定、有序的工具表并使用 `auto`；玩家点名某个动作或内部纠偏只会补充目标提示，不会把工具表缩成一个函数，模型仍可先查询前置事实或追加紧密相关的后续动作。由调用方已经确定为纯正文的轮次完全省略工具表。客户端和编排器不再接受、发送、模拟或重试 `tool_choice=required`。

## DeepSeek V4

- 官方模型：`deepseek-v4-flash`、`deepseek-v4-pro`。
- `thinking.type` 为 `enabled` / `disabled`；思考档仅支持 `high` / `max`。
- thinking + tools 时省略 `tool_choice`（省略即 auto），并保留完整现场工具表；纯正文轮省略工具表。
- 工具轮的 `reasoning_content` 必须原样回灌。
- `deepseek-chat` / `deepseek-reasoner` 只发迁移告警，不改写模型名，也保留旧别名默认的非思考 / 思考行为。

官方依据：

- https://api-docs.deepseek.com/guides/thinking_mode/
- https://api-docs.deepseek.com/api/create-chat-completion/
- https://api-docs.deepseek.com/updates/

## MiniMax M3 / M2.x

- M3 使用 `thinking.type=adaptive|disabled`；M2.x 是 thinking-only，不虚构可关闭。
- 始终请求 `reasoning_split=true`，让思考与正文从协议层分离。
- M3 流式 `content` 与 `reasoning_details[].text` 是累计值，客户端只派发相对上一帧的新后缀。
- 工具轮把完整 `reasoning_details` 作为 assistant 消息级字段跨轮回灌。
- M3 使用 `max_completion_tokens`；流式 usage 通过 `stream_options.include_usage` 请求。

官方依据：https://platform.minimax.io/docs/api-reference/text-openai-api

## Qwen

- Hybrid 模型：`Off` 发 `enable_thinking=false`；`Low` 发 `enable_thinking=true` 与 1024 的 `thinking_budget`；`Auto` 沿用该模型官方默认。
- thinking-only / QwQ 模型不发送虚假的关闭字段；当前 `qwen3.8-max-preview` 及带 `qwen/` 命名空间的同一模型均按 thinking-only 处理。
- 百炼思考模型的工具调用使用完整工具表与 `auto`，并继续做完成原因、工具名和 schema 后置校验。
- DashScope 某些 SSE 中间帧把完成原因写成字符串 `"null"`；只对 Qwen 将它视为“尚未完成”，仍须最终 `stop|tool_calls` 和 `[DONE]`。

官方依据：

- https://help.aliyun.com/en/model-studio/deep-thinking
- https://help.aliyun.com/en/model-studio/qwen-function-calling
- https://help.aliyun.com/en/model-studio/stream

### 本机 Ollama · 千问新开源 Qwen3.8

- 仅当 endpoint 为本机回环 Ollama 且模型名为 `qwen3.8`、`qwen3.8:27b` 或其官方变体时启用；云端 Qwen 和其他本地 OpenAI-compatible 服务不继承该分支。
- 请求固定发往 Ollama 原生 `/api/chat`，避开该模型在 `/v1/chat/completions` 上可能长时间无响应的问题。`Off` 转为 `think=false`，`Low` 转为 `think=low`，`Auto` 沿用模型默认思考。
- 原生 `message.thinking` 与可见正文严格分离；工具轮的思考、工具调用和工具结果会在后续轮转回原生 `thinking`、对象参数、`tool_name` 与 `tool_call_id`，不丢失工具链上下文。
- 原生 NDJSON 流在进入既有执行循环前转为统一的严格流式信封；仍须有 `done=true`、合法结束原因、完整工具名/参数/schema。原生响应省略工具 ID 时，以响应时间戳、模型、序号、函数名与参数的摘要生成稳定调用 ID，绝不猜测参数。
- 官方模型上下文为 262144；设置页给出精确的 Ollama 地址、模型名、空 Key 与上下文填写示例。

本节官方依据于 **2026-08-31（Asia/Shanghai）** 访问：

- https://ollama.com/library/qwen3.8
- https://docs.ollama.com/api/chat
- https://docs.ollama.com/capabilities/thinking
- https://github.com/ollama/ollama/blob/main/api/types.go

## OpenAI o1 / o3 / o4 / GPT-5 家族

- Chat Completions 使用 `max_completion_tokens`。
- 不发送 `temperature`、`frequency_penalty`、`presence_penalty`。
- `Low` / `Off` 在当前客户端不能可靠关闭的推理 Chat 模型上均安全退化为 `reasoning_effort=low`。
- `gpt-5` 原始家族与官方已发布的 `gpt-5.1`、`gpt-5.2`、`gpt-5.4`、`gpt-5.5`、`gpt-5.6` 家族及其变体、快照按推理 Chat Completions 处理；`-chat` 变体不发送 `reasoning_effort`。
- 官方 Chat Completions 虽提供更多工具选择值，客户端只使用公共子集：完整工具表加 `auto`、无工具；并行工具调用仍可由 `auto` 返回。每个调用的 ID、名称、参数和 schema 都在本地校验。
- 流式使用 SSE；`stream_options.include_usage=true` 的最终 usage 帧可以是空 `choices`，但成功仍须完整 `finish_reason` 和 `[DONE]`。

官方依据：

- https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create
- https://developers.openai.com/api/docs/guides/function-calling
- https://developers.openai.com/api/docs/guides/reasoning
- https://developers.openai.com/api/docs/guides/streaming-responses
- https://developers.openai.com/api/docs/models
- https://developers.openai.com/api/docs/models/gpt-5.6-sol
- https://developers.openai.com/api/docs/guides/latest-model

## Gemini

- 官方 Gemini API 的 OpenAI 兼容基址为 `https://generativelanguage.googleapis.com/v1beta/openai/`，聊天端点为其下的 `chat/completions`，使用 `Authorization: Bearer <GEMINI_API_KEY>`。设置中填写官方主机根地址、`/v1beta` 或完整 OpenAI 基址都会规范到同一个聊天端点；未知 Google 路径不会被猜测改写。
- 官方 OpenAI 兼容层支持 `reasoning_effort`。需要显示思考摘要时改用 `extra_body.google.thinking_config.include_thoughts=true`；`reasoning_effort` 与 `thinking_level` / `thinking_budget` 绝不同时发送。Gemini 3 系列和 2.5 Pro 不可关闭推理，关闭策略只降至 `low`；可关闭的 Gemini 2.5 变体使用 `none`。
- 工具请求统一使用完整现场工具表与 `auto`；纯正文轮不发送工具。并行调用顺序和 tool result 的 `tool_call_id` 保持不变。
- 官方兼容层支持流式响应和 OpenAI `response_format` 结构化输出；流式必须读到完整结束事件，不能因空文本签名片段提前收尾。
- `reasoning_effort` 与 `extra_body.google.thinking_config` 互斥，绝不同时发送。
- Gemini 3 工具轮要求首个并行 function call 原位保存并回灌 `extra_content.google.thought_signature`；缺失时副作用 fail-closed。
- 非 function-call 的签名只属推荐、不会触发服务端校验。当前 Mod 的持久聊天历史是纯文本 `TalkTurn`，无法在不破坏 Part 位置的前提下保存它；因此刻意不把该签名拼进正文或伪造到其他 Part。客户端仍读到流末尾，确保空文本签名 Part 不会造成提前收尾。若未来历史格式升级为原始 Part 序列，再启用其回灌。

官方依据：

- https://ai.google.dev/gemini-api/docs/openai
- https://ai.google.dev/gemini-api/docs/generate-content/thought-signatures

## Anthropic Claude

- 当前官方模型按精确族名适配：Claude Opus/Sonnet 5 在 `Off` 时显式发送
  `thinking.type=disabled`，在 `Low` 时使用 `thinking.type=adaptive` 与
  `output_config.effort=low`；Claude Fable/Mythos 5 与 Mythos Preview
  始终启用 adaptive thinking，`Off` 安全降级为仅发送低 `effort`，绝不发送会被
  400 拒绝的 `thinking.type=disabled`。
- Claude Opus 4.7/4.8/5、Sonnet 5、Fable 5、Mythos 5 与 Mythos Preview
  均省略非默认 `temperature`、`top_p`、`top_k`；未知 Anthropic 型号不推测这些
  新协议能力。
- 官方 OpenAI SDK 兼容基址为 `https://api.anthropic.com/v1/`，适合测试和模型比较；Anthropic 明确建议需要完整功能的长期生产调用使用原生 Claude API。
- `stream`、`stream_options`、`parallel_tool_calls`、两种 token 上限字段及标准工具响应可用；`n` 必须为 1。
- `response_format`、`reasoning_effort` 和采样惩罚会被静默忽略。客户端不发送 `response_format` 或无效惩罚字段，避免把“未报错”误判为 JSON 约束已生效。
- 兼容层不支持 prompt caching，客户端不再把 Anthropic 原生 `cache_control` 混入 OpenAI messages。
- `Low` 对 Claude 4.6、Opus 4.7/4.8 及 Opus/Sonnet 5 使用
  `thinking.type=adaptive` + `output_config.effort=low`；Fable/Mythos 5
  只发送低 `effort`；已知 4.5 使用有上下限的手动
  `enabled + budget_tokens`；未知型号不猜协议。兼容层不会返回详细思考，
  故不能把缺少 thought 字段解释成模型未推理。
- `strict` 工具 schema 会被服务端忽略；客户端仍在执行前本地校验 required/type/enum/range/additionalProperties。

官方依据：

- https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk
- https://platform.claude.com/docs/en/build-with-claude/extended-thinking
- https://platform.claude.com/docs/en/build-with-claude/thinking
- https://platform.claude.com/docs/en/about-claude/models/migration-guide
- https://platform.claude.com/docs/en/about-claude/models/whats-new-sonnet-5

## Moonshot Kimi K3 / K2.7 Code / K2.6

- 官方 OpenAI 兼容基址为 `https://api.moonshot.ai/v1`；`kimi-k3` 是 thinking-only，省略 `reasoning_effort` 使用默认 `max`，`Low` / `Off` 只能降到 `low`。
- 官方要求省略固定的 `temperature=1.0`、`top_p=0.95`、`presence_penalty=0`、`frequency_penalty=0`。K3 输出上限使用 `max_completion_tokens`；K2.x 按官方模型示例继续使用兼容字段 `max_tokens`。
- 流式 `reasoning_content` 与 `content` 分离；工具轮必须把完整 assistant（含 `reasoning_content` 和全部并行 `tool_calls`）原样回灌。
- K3 工具轮同样使用完整现场工具表与公共 `auto` 策略，响应后再次校验名称。
- `kimi-k2.7-code` 与 `kimi-k2.7-code-highspeed` 始终思考且始终保留思考；不发送 `thinking`、`enable_thinking`、`reasoning_effort` 或采样字段。官方模型总览明确列出这两款不支持强制工具选择，因此客户端不保留该设计。
- `kimi-k2.5` / `kimi-k2.6` 在所有兼容端都省略固定采样字段。Moonshot 官方端使用 `thinking.type=enabled|disabled`；K2.x 工具轮统一使用 auto。DashScope 部署在低推理/关闭时分别显式发送 `enable_thinking=true/false`，自动档沿用服务端默认；未知兼容端不猜测私有思考字段。DashScope 自营裸 K2.5/K2.6/K2.7 不声明 `response_format`，而 `kimi/...` 供应商命名空间模型保留官方支持的结构化输出。
- GLM 在智谱官方端使用 `thinking.type`；百炼公共端与 Workspace/MaaS 端使用 `enable_thinking`。两类官方 GLM 端点只要携带工具定义，流式和非流式请求都会显式发送 `tool_stream=true`；`ZHIPU/GLM-*` 等命名空间模型按 GLM 协议识别。
- MiniMax M2/M3 工具轮使用完整工具表与 `tool_choice=auto`，纯正文轮省略工具表；不再用具名选择缩减工具。M 系列不发送 `response_format`，百炼端同时省略不可修改的采样字段。

官方依据：

- https://platform.kimi.ai/docs/overview
- https://platform.kimi.ai/docs/guide/kimi-k3-quickstart
- https://platform.kimi.ai/docs/guide/use-reasoning-effort
- https://platform.kimi.ai/docs/guide/use-tool-choice
- https://platform.kimi.ai/docs/guide/use-kimi-api-to-complete-tool-calls
- https://platform.kimi.com/docs/api/chat
- https://platform.kimi.com/docs/api/models-overview
- https://platform.kimi.ai/docs/guide/utilize-the-streaming-output-feature-of-kimi-api
- https://platform.kimi.ai/docs/guide/use-context-caching-feature-of-kimi-api
- https://platform.kimi.com/docs/guide/kimi-k2-7-code-quickstart
- https://platform.kimi.com/docs/guide/kimi-k2-6-quickstart
- https://platform.kimi.com/docs/guide/use-thinking-models

## 智谱 GLM

- 官方 OpenAI 兼容基址为 `https://open.bigmodel.cn/api/paas/v4/`；当前 GLM 5.2 的思考开关为 `thinking.type=enabled|disabled`，并支持 `reasoning_effort`。
- 流式正文与 `reasoning_content` 分离；流式工具参数必须同时发送 `stream=true` 和 `tool_stream=true`。
- 官方文档当前只列出 `tool_choice=auto`。客户端直接采用完整现场工具表加 `auto` 的公共模式，纯正文轮通过完全省略 tools 实现。
- 工具轮回灌 `reasoning_content`；usage 的缓存 token 继续从 OpenAI 兼容的 `prompt_tokens_details.cached_tokens` 读取。

官方依据：

- https://docs.bigmodel.cn/cn/guide/capabilities/thinking
- https://docs.bigmodel.cn/cn/guide/capabilities/thinking-mode
- https://docs.bigmodel.cn/cn/guide/capabilities/function-calling
- https://docs.bigmodel.cn/cn/guide/capabilities/stream-tool
- https://docs.bigmodel.cn/cn/guide/start/migrate-to-glm-new
- https://docs.bigmodel.cn/cn/guide/models/text/glm-5.2

## 本地安全边界

- 远程端点只允许 HTTPS；HTTP 只允许真实 loopback IP / `localhost`。
- API key 含 CR/LF 时拒绝请求；外部错误、日志、UI 错误统一经 `SecretRedactor`。
- 请求体、HTTP 响应头、响应体、SSE 单行/事件、可见正文、思考、工具数量与参数均有硬上限。
- 普通正文与工具轮都必须有唯一 choice 和完整 `finish_reason`；`length`、过滤、中断、缺失完成原因一律失败。
- 工具参数执行前按本轮 `ToolDef` 校验 required、type、enum、数值范围与 additionalProperties。
