# 江湖有灵

《太吾绘卷》的 AI NPC Mod：让人物围绕游戏内的真实状态对话、记忆和行动。

当前 Mod 版本：**0.34.0.38**，适配游戏 **1.1.21**。

## 功能

- NPC 对话、流式回复、长期记忆与人物设定。
- NPC 单聊和千里传音支持手动总结：合并旧梗概与未压缩内容，显示累计梗概；压缩后的逐字原文继续显示在当前聊天窗口，也可完整导出。
- 通过游戏后端校验的工具调用，让行动产生真实结果。
- 多人群聊、个人上下文衔接与群聊记录。
- 同道过月行为、江湖大事和月度摘要。
- 灵儿助手、自定义世界书、内置特殊人设。
- 多模型接口配置、本地密钥保护及可选本地图片生成。

## 安装与使用

在 [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3747674580)
订阅并在游戏中启用。模型地址、模型名称和 API Key 在游戏内点击灵儿头像配置。
模型服务由玩家自行选择和承担费用；本仓库不提供密钥或模型账户。

完整功能说明见 [Mod 介绍](docs/workshop-description.md)。

## 开发

需要 Windows、.NET SDK，以及合法安装的《太吾绘卷》。项目使用 C# / .NET Framework 4.8。

```powershell
git clone https://github.com/EricSun0218/jianghuyouling.git
cd jianghuyouling
pwsh -File tools/bootstrap-development.ps1 -GameRoot "你的太吾绘卷安装目录" -SkipDecompile
dotnet build src/JianghuYouling.Frontend/JianghuYouling.Frontend.csproj -c Debug -warnaserror
dotnet build src/JianghuYouling.Backend/JianghuYouling.Backend.csproj -c Debug -warnaserror
dotnet run --project tools/JianghuYouling.DevTest/JianghuYouling.DevTest.csproj -c Debug -- --offline
```

Debug 不部署到游戏；Release 构建会自动复制到本机 Mod 目录，并在配置的工坊目录存在时同步文件。
验证源码时使用 Debug，或显式覆盖部署目录。详见 [开发与测试](docs/development.md)。

## 目录

- `src/JianghuYouling.Core`：模型协议、记忆、提示词、技能与纯逻辑。
- `src/JianghuYouling.Frontend`：游戏前端、界面与对话调度。
- `src/JianghuYouling.Backend`：游戏数据查询、动作校验与执行。
- `src/Shared`：前后端共享契约。
- `tools`：构建、契约测试、回归和发布校验工具。
- `deploy`：Mod 清单、内置头像及工坊展示资源。
- `docs`：使用、开发及测试说明。

## 隐私与反馈

对话及所需游戏信息会发送至玩家配置的模型服务。请自行评估服务方的数据政策。

Mod 默认开启 AI 使用计数上报，不提供游戏内关闭开关。每次 AI 调用结束后异步发送日期、
随机客户端 ID、随机去重 ID、固定白名单中的模型分类、是否成功及接口返回的输入/输出 Token 数。
统计与对话日志独立，不上传聊天、提示词、存档、Steam ID、API Key、接口地址或自定义模型名称。
客户端仅持久保存随机 ID（位于 `%LOCALAPPDATA%/JianghuYouling/Telemetry/client_id.json`），
不保存统计缓存，上传失败或退出时不补报，也不阻塞游戏。重新订阅工坊不会主动重置这个 ID。
服务端用于生成活跃客户端、增长、模型用量、使用强度和 7 日留存汇总；“客户端数”不等于真实人数，
Token 统计不等于完整账单。明细去重记录保留到次日，按日活跃记录最多保留 120 天，汇总长期保留。
网络请求仍由托管平台处理，其基础设施日志可能包含连接信息；应用本身不保存 IP 地址。

不要在 Issue、截图或提交中附带 API Key、个人配置、聊天记录、Player.log 或存档；
报告问题前先删除敏感内容。私有配置、运行数据、游戏程序集及反编译产物不应提交。

问题和改进建议可提交至 [Issues](https://github.com/EricSun0218/jianghuyouling/issues)。

## 许可证

项目使用 [MIT License](LICENSE)。第三方依赖与游戏知识产权边界见
[NOTICE](NOTICE) 和 [第三方说明](THIRD_PARTY_NOTICES.md)。
