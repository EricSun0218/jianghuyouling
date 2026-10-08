# 江湖有灵

《太吾绘卷》的 AI NPC Mod：让人物围绕游戏内的真实状态对话、记忆和行动。

当前 Mod 版本：**0.34.0.29**，适配游戏 **1.1.21**。

## 功能

- NPC 对话、流式回复、长期记忆与人物设定。
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
不要在 Issue、截图或提交中附带 API Key、个人配置、聊天记录、Player.log 或存档；
报告问题前先删除敏感内容。私有配置、运行数据、游戏程序集及反编译产物不应提交。

问题和改进建议可提交至 [Issues](https://github.com/EricSun0218/jianghuyouling/issues)。

## 许可证

项目使用 [MIT License](LICENSE)。第三方依赖与游戏知识产权边界见
[NOTICE](NOTICE) 和 [第三方说明](THIRD_PARTY_NOTICES.md)。
