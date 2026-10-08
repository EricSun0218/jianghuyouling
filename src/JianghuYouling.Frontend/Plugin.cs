using HarmonyLib;
using TaiwuModdingLib.Core.Plugin;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 《江湖有灵》前端插件入口。NPC 由 AI 扮演，一切影响皆其真实反应。
    /// 见 docs/specs/2026-06-18-quantan-design.md
    /// </summary>
    [PluginConfig("JianghuYouling", "jianghuyouling", "0.34.0.29")]
    public class Plugin : TaiwuRemakePlugin
    {
        public static Plugin Instance { get; private set; }

        private Harmony _harmony;

        public override void Initialize()
        {
            Instance = this;
            // 字体档位必须早于任何 Mod 界面创建载入，确保首帧新建文本直接采用持久化字号。
            UiFontSizeStore.Initialize();
            _harmony = new Harmony(GetGuid());
            _harmony.PatchAll(typeof(Plugin).Assembly);
            TalkEntryHost.Initialize();   // 轮询宿主:注入"AI 对话"按钮
            LegacyDataNoticeHost.Initialize();   // 旧版数据迁移的玩家可见提示(认领冲突/标记损坏/持续失败)
            ChatHistoryEntryHost.Initialize();   // 轮询宿主:人物详情页注入“往事成书 / 千里传音”入口
            AssistantWidget.Initialize();        // 悬浮 AI 助手挂件(可拖/置顶;点击开助手对话)
            NpcProactiveChatScheduler.Initialize(); // 已聊 NPC 低频主动来信（完整单聊上下文）
            ConfigHost.Initialize();      // 配置面板宿主(协程/字体载体;不占用游戏快捷键,设置由聊天窗"设置"键打开)
            ImeFollow.Initialize();       // 仅清理旧版输入法宿主/Checker；当前 TMP 原生负责候选窗与光标
            MonthlySettlement.Initialize();  // 过月车道:行为意图 → 引擎目标
            LoadSettingsFromStores();   // 配置一律读 mod 内设置窗存盘(难度/篇幅/流式);mod 管理页不再有任何设置项
            Debug.Log("[江湖有灵] Initialized: " + GetGuid()
                + " mod=0.34.0.29 targetGame=1.1.21 targetBuild=25596993");
        }

        public override void Dispose()
        {
            WorldLifecycle.MarkWorldLeaving();
            MonthlySettlement.Shutdown();
            ImeFollow.Shutdown();
            _harmony?.UnpatchSelf();
            _harmony = null;
            Debug.Log("[江湖有灵] Disposed");
        }

        // 配置不再走 mod 管理页(Config.lua 已清空 DefaultSettings)→ 此回调通常不再触发,留作兜底重载
        public override void OnModSettingUpdate()
        {
            LlmService.Reload();
            LoadSettingsFromStores();
        }

        // 从 mod 内设置窗的本地存盘载入难度/篇幅/流式(游戏内 ConfigWindow 即时存盘,这里启动时载入)
        public static void LoadSettingsFromStores()
        {
            TalkOrchestrator.Difficulty = DifficultyStore.Load();
            TalkOrchestrator.StreamingEnabled = StreamStore.Load();
            ChatWindow.ShowThinking = ThinkingStore.Load();   // 默认开:展示思考过程
            TalkOrchestrator.TaiwuVoice = TaiwuVoiceStore.Load();
            TalkOrchestrator.ReplyLength = ReplyLenStore.Load();
            AssistantWidget.NotifySettingsChanged();
            NpcProactiveChatScheduler.NotifySettingsChanged();
        }

        public override void OnEnterNewWorld() { }
    }
}
