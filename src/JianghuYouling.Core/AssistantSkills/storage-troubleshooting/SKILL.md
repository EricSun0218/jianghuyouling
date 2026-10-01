---
name: storage-troubleshooting
description: 本地存储、旧存档迁移、日志与故障排查
triggers: 路径,存储,本地,备份,清空,丢失,旧存档,迁移,WorldId,Player.log,日志,报错,失败,排查,Key,密钥,头像,形象,图片
---
# 本地存储与排障

除玩家选择的第三方模型请求外，聊天、记忆、画像、世界书和设置都存本机。数据根目录是游戏目录下 `TaiWu-JianghuYouling-Logs`：`Settings` 保存全局接口、语音、开关、灵儿名字/人设/形象路径；存档数据位于 `Worlds/World_<WorldId>`，其中有聊天、记忆、画像、世界书、纪事与行程。更新 Mod 的 DLL 和 Config 不会主动删除这些数据。

API Key 以 Windows 当前用户可解密的 DPAPI 密文保存。世界书位于当前世界目录的人设子目录并保留 `.bak`。玩家反馈世界书或历史像丢失时，先确认 WorldId 和太吾 id，再检查相应世界目录及备份，不能只按太吾 id 去旧根目录查。

【0.30 旧存档迁移·排障】更早版本数据会按太吾归属、只导入一次；完整成功后写完成标记，不再重复扫描，旧原文件始终保留。曾被清空的世界书旧内容会另存为 `.txt.cleared-<时间戳>`。旧明文 Key 自动迁为 DPAPI 密文；若迁移中断后设置页 Key 为空，玩家重新填写并保存一次即可。

游戏日志是 `%USERPROFILE%/AppData/LocalLow/Conchship/The Scroll of Taiwu/Player.log`，上次运行是同目录 `Player-prev.log`。玩家说看日志、语音失败、交换失败、流式异常或行动没执行时，灵儿应调用本地日志分析，只读取尾部相关行并先脱敏 Key、Authorization、token 和 password；不要声称看过工具没有返回的完整日志。<!-- JHYL_ASSISTANT_KB_LOG_ANALYZER -->

玩家明确说“导出日志”“把诊断日志打包/复制到桌面”时，灵儿可一键在桌面建立带时间戳的“江湖有灵日志”文件夹，复制当前 `Player.log`、上次运行的 `Player-prev.log`，以及 `llm_metrics.jsonl` 和轮转的 `.1` 指标日志。工具会明确返回文件夹路径、已复制文件、缺失文件和复制失败项；普通报错抱怨或“能不能导出”的询问不等于执行授权。日志内容只做本地复制，不交给模型读取。<!-- JHYL_ASSISTANT_KB_DIAGNOSTIC_EXPORT -->

灵儿头像支持本地 png/jpg/jpeg，最大 10MB，按圆形居中 cover 裁剪；留空或恢复默认使用内置头像。<!-- JHYL_ASSISTANT_KB_REPLACEABLE_FACE JHYL_ASSISTANT_KB_FACE_IMAGE_RULES -->
若玩家要生图提示，可建议：正方形头像构图、古风武侠少女助手、含蓄自然的东方五官、半身近景、脸部居中、宋明衣饰、宣纸墨彩与电影感光影、适合圆形裁剪、无文字无水印，避免网红脸。<!-- JHYL_ASSISTANT_KB_FACE_IMAGE_PROMPT -->
