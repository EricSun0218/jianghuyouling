# 本机图生图（ComfyUI）

《江湖有灵》main 分支直接连接本机 ComfyUI，不依赖其他 Agent 框架或常驻 sidecar。Mod 会上传 NPC 与太吾合成后的参考图，提交由玩家提供的可信工作流，轮询完成状态，再把生成图片写入聊天记录。

## 推荐配置

RTX 4060 Laptop 8 GB 等 8 GB 显存设备建议先使用 SDXL 图生图工作流，输出按 1024×576 的 16:9 配置；需要更稳定地保留人物特征时，可在工作流内增加 IP-Adapter 或 ControlNet。大型编辑模型若依赖大量量化和内存卸载，速度与稳定性不适合作为默认方案。

## 工作流要求

1. 在 ComfyUI 中完成并跑通图生图工作流，最后使用 `SaveImage` 输出图片。
2. 将工作流保存为 API 格式 JSON，而不是普通界面工作流 JSON。
3. 在 API JSON 的对应输入值中放入以下精确标记。推荐使用双层花括号；部分工作流编辑器或模板会保存为单层花括号，Mod 也会按同一含义识别：

   - `{{JHYL_PROMPT}}` 或 `{JHYL_PROMPT}`：必须，放在正向提示词节点的 `text` 输入。
   - `{{JHYL_REFERENCE_IMAGE}}` 或 `{JHYL_REFERENCE_IMAGE}`：必须，放在 `LoadImage` 节点的 `image` 输入。
   - `{{JHYL_WIDTH}}`、`{{JHYL_HEIGHT}}`（也接受对应单层花括号写法）：必须，放在缩放或潜空间尺寸输入，分别替换为 1024、576，保证输出比例固定。
   - `{{JHYL_MODEL}}` 或 `{JHYL_MODEL}`：可选，放在 checkpoint/model 输入；由设置页的“生图模型”替换。用了该标记就必须填写模型。

示例片段：

```json
{
  "1": {
    "class_type": "LoadImage",
    "inputs": { "image": "{{JHYL_REFERENCE_IMAGE}}" }
  },
  "2": {
    "class_type": "CLIPTextEncode",
    "inputs": { "text": "{{JHYL_PROMPT}}" }
  },
  "3": {
    "class_type": "EmptyLatentImage",
    "inputs": {
      "width": "{{JHYL_WIDTH}}",
      "height": "{{JHYL_HEIGHT}}"
    }
  }
}
```

Mod 只替换值完全等于上述标记的节点输入，不把模型生成的文本当成工作流 JSON，也不会让模型添加或改写节点。上传的两人参考图本身也是 1024×576；即使图生图链直接继承参考图尺寸，也不会退回方图。

## 游戏内设置

- 生图协议：`comfyui`
- 生图接口：`http://127.0.0.1:8188`
- API Key：本机服务可留空
- 生图模型：仅在工作流使用 `{{JHYL_MODEL}}` 时需要填写
- ComfyUI 工作流路径：导出的 API JSON 绝对路径

若本机服务已经实现 OpenAI `/v1/images/edits` 参考图协议，也可继续使用 `provider=openai`、本机回环 endpoint 和空密钥。只提供 `/v1/images/generations` 的纯文生图服务无法接收两人参考图，因此不作为本功能的图生图后端。

## 边界

- ComfyUI 模式只允许 `localhost`、`127.0.0.1` 或其他回环地址，不能配置远程主机。
- 工作流最大 8 MB；参考图、响应和输出图片均有独立字节上限。
- HTTP 重定向会被拒绝；任务轮询总时长最多 8 分钟。
- 图片仍按原逻辑完整等比显示，不拉伸、不裁切。

接口依据：ComfyUI 官方服务端的 `/upload/image`、`/prompt`、`/history/{prompt_id}` 与 `/view` 契约。
