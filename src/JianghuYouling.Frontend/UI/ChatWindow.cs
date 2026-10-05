using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using JianghuYouling.Effects;   // EffectHandler.QueryIsTeammate(人设入口队友判定)
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Diagnostics;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;   // TalkTurn(按轮删除/重试关联会话 turn)
using JianghuYouling.Core.Text;
using JianghuYouling.Core.Tools;
using JianghuYouling.Core.Web;

namespace JianghuYouling
{
    /// <summary>
    /// 聊天窗:自建 ScreenSpaceOverlay Canvas + 面板 + 滚动气泡区 + 输入框 + 递话/关闭。
    /// NPC 回话直接显示在背景上(无框),玩家消息为右侧动态宽度气泡。流式逐字 + 上下文压缩在 TalkOrchestrator。
    /// </summary>
    /// <summary>单个聊天页签:一段独立对话(NPC单聊/千里传音/群聊/灵儿助手)的全部状态与 UI。
    /// 每个实例自建一套面板(共享屏幕中心位),由 ChatWindow 外壳门面管理多页签的增删/切换/页签栏。
    /// 后台页签的生成协程挂在 TalkEntryHost 上、各写各自实例的气泡,互不串扰 → 支持多对话并发生成。</summary>
    public sealed class ChatTab
    {
        internal const float PanelWidth = 1040f;
        internal const float PanelHeight = 760f;
        internal const float SidebarWidth = 252f;
        internal const float SidebarGap = 0f;

        static bool _dictationHintShown;
        static Texture2D _roundedSkinTexture;
        static Sprite _roundedSkinSprite;
        GameObject _root;
        RectTransform _panelRt;           // 本页签面板的 RectTransform(供页签栏跟随其拖拽/位置)
        public RectTransform PanelRt => _panelRt;
        TextMeshProUGUI _title;
        SafeChatInputField _input;
        ScrollRect _scroll;
        RectTransform _chatScrollRect;
        RectTransform _content;           // 气泡容器(VerticalLayoutGroup)
        Button _sendBtn;
        TMP_FontAsset _font;
        int _npcId, _taiwuId;
        string _npcName;
        bool _busy;
        TalkOrchestrator _currentOrch;   // 当前轮编排器(供【中断】取消)
        bool _groupMode;                  // 群聊模式:多 NPC 轮流发言,走 GroupChatOrchestrator
        GroupChatOrchestrator _group;     // 群聊编排器(开窗时建,持有成员与共享记录)
        List<KeyValuePair<int, string>> _groupRoster; // 初始化完成前也保留请求成员,供左栏稳定识别同一群
        string _requestedGroupId;         // 初始化完成前保留新群/持久群的独立身份，避免按成员集合串群
        bool _groupHistoryOnly;           // 成员当前不在同处时仍可回看该群，发送/重试保持关闭
        GameObject _groupBtnGo;           // 「加人」按钮:单聊、传音、群聊和助手页均可从统一候选池组群
        GameObject _monthlyCandidateBtnGo; // 当前 NPC 的过月主动人物候选开关
        Button _monthlyCandidateBtn;
        TextMeshProUGUI _monthlyCandidateLabel;
        GameObject _novelButtonGo;          // 「往事成书」：当前 NPC 的聊天与记忆写作入口
        Button _detailButton;
        GameObject _detailButtonGo;
        GameObject _taiwuActionBarGo;
        readonly List<Button> _taiwuActionButtons = new List<Button>();
        readonly List<GameObject> _taiwuDirectActionControls = new List<GameObject>();
        readonly List<Button> _assistantActionButtons = new List<Button>();
        readonly List<GameObject> _assistantActionControls = new List<GameObject>();
        bool _assistantDiagnosticExportBusy;
        Button _personaButton;
        GameObject _personaButtonGo;
        TextMeshProUGUI _taiwuActionHint;
        ChatInputAutoGrow _inputGrow;
        RectTransform _inputTextViewport;
        bool _discardSubmitLineBreakOnRefocus;
        int _monthlyCandidateRequestVersion;
        bool _assistantMode;              // 助手模式:与悬浮 AI 助手对话(非 NPC、非群聊)
        AssistantOrchestrator _currentAsst;
        System.IDisposable _assistantPriorityLease; // 助手交互轮期间阻止主动消息并发改写同一历史
        LlmMessage _lastRenderedAssistantHistoryMessage;
        bool _assistantHistoryViewInitialized;
        bool _remote;                     // 千里传音(远程对话):去掉需当面/物理接触的工具
        bool _soulMode;                   // 已故旧识的灵魂会话：保留聊天，完全关闭查询、行动与过月
        Coroutine _contactModeCoroutine;  // 单聊实时距离判定；仅走游戏权威数据，不经过模型
        int _contactModeRequestVersion;
        Button _suggestBtn;               // 「代笔」:据聊天记录替太吾拟一句话填入输入框
        Button _dictationBtn;              // 「口述」:唤起 Windows Win+H 原生语音键入,只填输入框、不自动发送
        bool _suggesting;
        CancellationTokenSource _suggestCancellation;
        Coroutine _suggestCoroutine;
        long _suggestVersion;
        CancellationTokenSource _dictationCancellation;
        Coroutine _dictationCoroutine;
        Coroutine _runningCoroutine;
        int _combatOfferVersion;           // 页签级一次性约战版本；新一轮/删除/清空会让旧按钮失效
        bool _combatTransitionBusy;        // 原生入场预检/等待回执期间锁住发送、重试和删除
        TextMeshProUGUI _sendLabel;       // 递话键文字(忙时切「中断」)
        string _progressLabel = "思忖";   // 思考行当前进度文字(随工具变)
        string _baseTitle = "";           // 标题正文
        bool _persistedHistoryRefreshPending; // 原生游戏互动在聊天窗外写盘后，闲时重绘当前单聊
        int _proxySourceNpcId;             // 玩家实际点到的固定人物；发送首句时才交给后端建副本
        bool _proxyIdentityChecked;        // 已用当前活人显示数据确认是否需要副本
        bool _proxyIdentityReady;          // 普通人物或已完成副本迁移，可真正发送消息
        bool _requiresProxyBeforeMessage;  // 固定模板人物：首句发送前必须创建合法普通副本
        bool _proxyHintShown;
        bool _proxyPrepareBusy;

        // 流式:当前正在追加的 NPC 回话行(随 delta 刷新);_dotsActive=思考点动画进行中
        TextMeshProUGUI _streamText;
        string _streamRawText;           // NPC 流式正文原文；显示层会按「」/『』只给真正台词着色
        GameObject _streamBubble;
        GameObject _groupNameRow;   // 群聊当前发言者的名签行(供其沉默时连同思考气泡一并拆除)
        bool _dotsActive;
        // 流式:思考过程行(灰、置于回话行之上;本轮内累加,随历史留存)
        TextMeshProUGUI _thinkText;
        GameObject _thinkBubble;
        const int VisibleThinkingCharacterLimit = 8000;
        const float ThinkingFlushIntervalSeconds = 0.08f;
        readonly BoundedTailTextBuffer _thinkingVisibleBuffer =
            new BoundedTailTextBuffer(VisibleThinkingCharacterLimit);
        readonly StringBuilder _thinkingPendingBuffer = new StringBuilder(512);
        Coroutine _thinkingFlushCoroutine;
        // 是否展示思考过程(全局开关)已上移至 ChatWindow 外壳门面(多页签共享);内部引用走 ChatWindow.ShowThinking。

        // ===== 按轮删除 / 重试 =====
        enum RMode { Single, Group, Assistant }
        sealed class Round
        {
            public RMode mode;
            public string playerInput;                                          // 重试用;主动捎话/无玩家输入轮为 null(不可重试)
            public readonly List<GameObject> rows = new List<GameObject>();      // 本轮在 _content 下的所有行(删除时一并销毁,含控制条)
            public readonly List<TalkTurn> turns = new List<TalkTurn>();         // 单聊:本轮占用的会话 turn(按引用删,删中间轮也稳)
            public readonly List<string> memoryIds = new List<string>();        // 单聊:本轮沉淀的长期记忆 id(删除时硬抹)
            public string exchangeId;                                           // 单聊:编排器实际提交的稳定轮次 id
            public readonly List<GroupChatOrchestrator.Line> groupLines = new List<GroupChatOrchestrator.Line>();   // 群聊:本轮 transcript 行(按引用删)
            public readonly List<LlmMessage> assistantMessages = new List<LlmMessage>();   // 助手:本轮成功写盘的精确对象引用
            public bool assistantHistoryCommitted;                             // true=编排器确认本轮已写盘；无引用时必须 fail-closed
            public GameObject bar;        // 控制条行
            public GameObject retryGo;    // 「重试」按钮(仅最后一可重试轮显示)
            public bool proactive;        // 主动捎话/无玩家输入轮:可删不可重试
            public string replyText;      // 本轮 NPC/助手回话正文(供「语音」朗读)
            public int replyNpcId;        // 该回话的 NPC(单聊);助手轮见 isAsst
            public bool isAsst;           // 助手(灵儿)轮
            public Button voiceBtn;       // 「语音」按钮(生成中置灰)
            public bool voiceGen;         // 该轮语音是否正在生成
            public readonly object voiceOwner = new object();
            public long voiceRequestVersion;
            public Button imageBtn;
            public bool imageGen;
            public bool imagePromptOpen;
            public long imageRequestVersion;
            public CancellationTokenSource imageCancellation;
            public GameObject imageRow;
            public string imageFileName;
        }
        string _lastReplyText;     // 最近一轮回话正文(收尾时记到 Round 上,供语音)
        GameObject _voiceStatusRow; // 「配音中…」临时状态行(出声/失败即移除)
        readonly List<Round> _rounds = new List<Round>();
        long _voiceLifecycleVersion;
        long _imageLifecycleVersion;
        Round _cur;            // 进行中的一轮(BeginRound→Done)
        int _curStartChild;    // BeginRound 时 _content 子节点数(收尾据此快照本轮所有行)
        int _curGroupFrom;     // 群聊:BeginRound 时 transcript 行数(收尾据此快照本轮群聊行)

        // 配色
        readonly Color ColPanel = new Color(0.10f, 0.11f, 0.10f, 0.97f);
        readonly Color ColPlayerBubble = new Color(0.18f, 0.30f, 0.46f, 0.96f);
        readonly Color ColNpcBubble = new Color(0.22f, 0.21f, 0.17f, 0.96f);
        readonly Color ColSysBubble = new Color(0.16f, 0.16f, 0.16f, 0.55f);
        readonly Color ColPlayerText = new Color(0.90f, 0.95f, 1.00f, 1f);
        readonly Color ColNpcText = Color.white;                                // 具体层次由富文本分段控制
        const string NpcDialogueColorTag = "#F5F2EAFF";                       // 说出口的话:清晰宣纸白
        const string NpcNarrationColorTag = "#D8C58CD2";                      // 动作叙述:暖金且略降透明度
        readonly Color ColSysText = new Color(0.62f, 0.62f, 0.60f, 1f);
        readonly Color ColThinkText = new Color(0.70f, 0.80f, 0.88f, 1f);   // 思考过程:清亮的冷蓝灰,正立不倾斜、足够醒目又与暖色正文区分
        readonly Color ColNameText = new Color(0.55f, 0.78f, 0.72f, 1f);       // 群聊发言者名签:青绿
        readonly Color ColAccent = new Color(0.30f, 0.38f, 0.36f, 0.95f);

        public void Open(int npcId, int taiwuId, string npcName, TMP_FontAsset font,
            int proxySourceNpcId = -1)
        {
            _groupMode = false; _assistantMode = false; _group = null; _groupRoster = null;
            _requestedGroupId = null;
            _groupHistoryOnly = false; _remote = false;
            _npcId = npcId; _taiwuId = taiwuId;
            _proxySourceNpcId = proxySourceNpcId >= 0 ? proxySourceNpcId : npcId;
            _proxyIdentityChecked = false;
            _proxyIdentityReady = false;
            _requiresProxyBeforeMessage = false;
            _proxyHintShown = false;
            _proxyPrepareBusy = false;
            _soulMode = ArchivedCharacterStatusStore.IsDead(taiwuId, npcId);
            if (_soulMode) _proxyIdentityReady = true;
            _npcName = TalkOrchestrator.IsUnresolvedNpcName(npcName) ? "姓名载入中…" : npcName.Trim();
            Key = "npc:" + WorldLifecycle.Generation + ":" + taiwuId + ":" + npcId;
            if (font != null) _font = font;
            if (_font == null) _font = ResolveFont();   // 兜底:游戏没传字体(1.0.17 改了名签布局取到 null)时满场借一个,
                                                        // 否则每个 TMP 文本都走"无字体"最坏路径(刷屏 LoadFontAsset 警告 + 额外 GPU/CPU 压力)
            if (_root == null) Build();
            _root.SetActive(true);
            if (_groupBtnGo != null) _groupBtnGo.SetActive(true);
            if (_monthlyCandidateBtnGo != null) _monthlyCandidateBtnGo.SetActive(true);
            if (_novelButtonGo != null) _novelButtonGo.SetActive(true);
            if (_detailButtonGo != null) _detailButtonGo.SetActive(true);
            RefreshMonthlyCandidateButton();
            RefreshTaiwuActionBar();
            _baseTitle = _soulMode ? "与 " + _npcName + " 的灵魂对话" : "与 " + _npcName + " 对话"; ApplyTitle();
            ClearLog();
            _streamText = null; _streamRawText = null; _streamBubble = null; _dotsActive = false; _thinkText = null; _thinkBubble = null;
            RenderHistory();                       // 开窗即载入历史对话(含月份分割线)
            ClearComposer(true);

            // 预热画像:开窗即后台蒸馏,与玩家读招呼/打字并行,藏掉首句的蒸馏延迟
            var host = TalkEntryHost.Instance;
            if (host != null && !_soulMode) host.StartCoroutine(WarmPortrait(_npcId));
            if (!_soulMode) RequestContactModeRefresh();
        }

        /// <summary>开「千里传音」远程对话:与 NPC 远隔传声,去掉需当面/物理接触的工具(传功授艺/赠物/疗伤/换装/买卖 等)。</summary>
        public void OpenRemote(int npcId, int taiwuId, string npcName, TMP_FontAsset font,
            int proxySourceNpcId = -1)
        {
            Open(npcId, taiwuId, npcName, font, proxySourceNpcId);
            if (_soulMode) return;
            _remote = true;
            if (_groupBtnGo != null) _groupBtnGo.SetActive(true);
            if (_monthlyCandidateBtnGo != null) _monthlyCandidateBtnGo.SetActive(true);
            if (_novelButtonGo != null) _novelButtonGo.SetActive(true);
            if (_detailButtonGo != null) _detailButtonGo.SetActive(true);
            RefreshMonthlyCandidateButton();
            Key = "rmt:" + WorldLifecycle.Generation + ":" + taiwuId + ":" + npcId;
            _baseTitle = "千里传音 · " + _npcName; ApplyTitle();
            RefreshTaiwuActionBar();
        }

        void CancelContactModeRefresh()
        {
            unchecked { _contactModeRequestVersion++; }
            try
            {
                if (_contactModeCoroutine != null && TalkEntryHost.Instance != null)
                    TalkEntryHost.Instance.StopCoroutine(_contactModeCoroutine);
            }
            catch { }
            _contactModeCoroutine = null;
        }

        public void RequestContactModeRefresh()
        {
            if (_assistantMode || _groupMode || _soulMode || _busy || _taiwuId <= 0 || _npcId < 0) return;
            var host = TalkEntryHost.Instance;
            if (host == null) return;
            CancelContactModeRefresh();
            int requestVersion = _contactModeRequestVersion;
            int generation = WorldLifecycle.Generation;
            _contactModeCoroutine = host.StartCoroutine(RefreshContactMode(requestVersion, generation));
        }

        IEnumerator RefreshContactMode(int requestVersion, int generation)
        {
            bool resolved = false;
            bool remote = true;
            yield return ConversationContactModeResolver.Resolve(_taiwuId, _npcId, generation,
                CancellationToken.None, value => { remote = value; resolved = true; },
                null, EwReflect.HasTargetCharacterContext(_npcId));
            if (requestVersion != _contactModeRequestVersion
                || !WorldLifecycle.IsSameWorld(generation)) yield break;
            _contactModeCoroutine = null;
            if (resolved) ApplyContactMode(remote);
        }

        void ApplyContactMode(bool remote)
        {
            if (_assistantMode || _groupMode || _taiwuId <= 0 || _npcId < 0) return;
            if (_soulMode)
            {
                _remote = false;
                Key = "npc:" + WorldLifecycle.Generation + ":" + _taiwuId + ":" + _npcId;
                _baseTitle = "与 " + _npcName + " 的灵魂对话";
                ApplyTitle();
                RefreshTaiwuActionBar();
                return;
            }
            _remote = remote;
            Key = (remote ? "rmt:" : "npc:") + WorldLifecycle.Generation + ":"
                + _taiwuId + ":" + _npcId;
            _baseTitle = remote ? "千里传音 · " + _npcName : "与 " + _npcName + " 对话";
            ApplyTitle();
            RefreshTaiwuActionBar();
        }

        internal bool UpdateKnownNpcName(int npcId, string npcName)
        {
            if (npcId < 0 || TalkOrchestrator.IsUnresolvedNpcName(npcName)) return false;
            string resolved = npcName.Trim();
            bool changed = false;
            if (!_assistantMode && !_groupMode && _npcId == npcId
                && !string.Equals(_npcName, resolved, System.StringComparison.Ordinal))
            {
                _npcName = resolved;
                _baseTitle = _soulMode ? "与 " + _npcName + " 的灵魂对话"
                    : _remote ? "千里传音 · " + _npcName : "与 " + _npcName + " 对话";
                changed = true;
            }
            if (_groupMode)
            {
                if (_group?.Members != null)
                    foreach (GroupChatOrchestrator.Member member in _group.Members)
                        if (member != null && member.Id == npcId
                            && !string.Equals(member.Name, resolved, System.StringComparison.Ordinal))
                        {
                            member.Name = resolved;
                            changed = true;
                        }
                if (_groupRoster != null)
                    for (int i = 0; i < _groupRoster.Count; i++)
                        if (_groupRoster[i].Key == npcId
                            && !string.Equals(_groupRoster[i].Value, resolved, System.StringComparison.Ordinal))
                        {
                            _groupRoster[i] = new KeyValuePair<int, string>(npcId, resolved);
                            changed = true;
                        }
                if (changed) _baseTitle = GroupConversationTitle();
            }
            if (changed) ApplyTitle();
            return changed;
        }

        internal void RefreshCharacterArchiveStatus()
        {
            if (_assistantMode || _groupMode || _taiwuId <= 0 || _npcId < 0) return;
            bool soul = ArchivedCharacterStatusStore.IsDead(_taiwuId, _npcId);
            if (_soulMode == soul) return;
            _soulMode = soul;
            if (soul)
            {
                CancelContactModeRefresh();
                _remote = false;
                Key = "npc:" + WorldLifecycle.Generation + ":" + _taiwuId + ":" + _npcId;
                _baseTitle = "与 " + _npcName + " 的灵魂对话";
            }
            else
            {
                _baseTitle = "与 " + _npcName + " 对话";
                RequestContactModeRefresh();
            }
            ApplyTitle();
            RefreshMonthlyCandidateButton();
            RefreshTaiwuActionBar();
        }

        IEnumerator WarmPortrait(int npcId)
        {
            int generation = WorldLifecycle.Generation;
            int taiwuId = _taiwuId;
            NpcSnapshot snap = null;
            yield return NpcSnapshotReader.Fetch(npcId, s => snap = s);
            if (!WorldLifecycle.IsSameWorld(generation) || taiwuId != _taiwuId) yield break;
            ApplyProxyIdentityProbe(npcId, snap);
            // 固定模板人物的源实体可能因剧情阶段/回档落在本体死者状态，但它仍是
            // “发送消息后建立普通人物副本”的来源，不能先把源 ID 固化成灵魂。
            // 真正生成的普通副本 CreatingType=1，死亡后仍按正常灵魂规则处理。
            if (snap?.IsDead == true && snap.CreatingType == 1)
            {
                ArchivedCharacterStatusStore.ApplyAuthoritative(taiwuId, null,
                    new[] { npcId }, out _);
                ChatWindow.RevealNewSoulConversation(taiwuId, npcId);
                RefreshCharacterArchiveStatus();
                ChatWindow.NotifyCharacterArchiveStatusChanged(taiwuId);
                yield break;
            }
            if (snap != null && !TalkOrchestrator.IsUnresolvedNpcName(snap.Name))
            {
                UpdateKnownNpcName(npcId, snap.Name);
                TalkOrchestrator.TryUpdateExistingConversationNameIfCurrent(
                    taiwuId, npcId, snap.Name, generation);
            }
            if (snap != null && snap.TaiwuId > 0)
                yield return PortraitService.EnsureSeeded(snap, _ => { });
        }

        void ApplyProxyIdentityProbe(int probedNpcId, NpcSnapshot snap)
        {
            if (_assistantMode || _groupMode || _soulMode || probedNpcId != _npcId
                || snap == null || !snap.DisplayLoaded)
                return;
            _proxyIdentityChecked = true;
            _requiresProxyBeforeMessage = snap.CreatingType != 1;
            _proxyIdentityReady = !_requiresProxyBeforeMessage;
            if (_requiresProxyBeforeMessage && !_proxyHintShown)
            {
                _proxyHintShown = true;
                AppendSys("（特殊人物提示：如果对话就会复制为合法普通人物。复制人会出现在太吾当前所在地；若当前位置无效，则出现在太吾村。首次发送消息或开启互动记录（主动记录）时开始复制；单纯打开窗口不会复制。）");
            }
            RefreshMonthlyCandidateButton();
            RefreshTaiwuActionBar();
        }

        /// <summary>开小队群聊:roster=(charId,名字),名字可空(由成员快照补全)。多 NPC 轮流发言。</summary>
        public void OpenGroup(List<KeyValuePair<int, string>> roster, int taiwuId, TMP_FontAsset font,
            string persistedGroupId = null)
        {
            _groupMode = true; _assistantMode = false; _npcId = 0; _taiwuId = taiwuId; _npcName = "小队";
            _soulMode = false;
            _requestedGroupId = persistedGroupId;
            _groupHistoryOnly = false;
            _groupRoster = roster != null
                ? new List<KeyValuePair<int, string>>(roster)
                : new List<KeyValuePair<int, string>>();
            if (font != null) _font = font;
            if (_font == null) _font = ResolveFont();
            if (_root == null) Build();
            _root.SetActive(true);
            if (_groupBtnGo != null) _groupBtnGo.SetActive(true);
            if (_monthlyCandidateBtnGo != null) _monthlyCandidateBtnGo.SetActive(false);
            if (_novelButtonGo != null) _novelButtonGo.SetActive(false);
            if (_detailButtonGo != null) _detailButtonGo.SetActive(false);
            RefreshTaiwuActionBar();
            _baseTitle = GroupConversationTitle(); ApplyTitle();
            ClearLog();
            _streamText = null; _streamRawText = null; _streamBubble = null; _dotsActive = false; _thinkText = null; _thinkBubble = null;
            ClearComposer(true);

            _group = new GroupChatOrchestrator();
            _group.GrantSink = AddGrantNotice; _group.SysSink = AddSysNotice;   // 多页签:成员落地绿提示落回本群页签
            _currentOrch = null;
            _busy = true; SetSendMode(true);
            GameObject setupBubble = AddBubble(BubbleKind.Sys, "（正在生成人物画像…）");
            TextMeshProUGUI setupText = setupBubble != null ? setupBubble.GetComponent<TextMeshProUGUI>() : null;
            System.Action<string> showSetup = value =>
            {
                string visible = string.IsNullOrWhiteSpace(value) ? "正在生成人物画像…" : value.Trim();
                if (setupText != null) setupText.text = "（" + visible + "）";
                SetProgress(visible);
                ScrollDown();
            };
            var host = TalkEntryHost.Instance;
            if (host != null)
                _runningCoroutine = host.StartCoroutine(_group.Open(taiwuId, roster,
                    prog => showSetup(prog),
                    () =>
                    {
                        int n = _group != null ? _group.Members.Count : 0;
                        _groupRoster = GroupRosterSnapshot();
                        RenderGroupHistory();   // 载入并回放此前群聊
                        if (setupBubble != null) setupBubble.transform.SetAsLastSibling();
                        showSetup(n > 0
                            ? ("人物画像已准备完成：" + JoinMemberNames() + "。请太吾开口")
                            : "无人接入，群聊作罢");
                        _baseTitle = GroupConversationTitle(); ApplyTitle();
                        RefreshTaiwuActionBar();
                        ChatWindow.NotifyNavigationStorageChanged();
                        Done();
                    },
                    err =>
                    {
                        string historyError = null;
                        if (_group != null && _group.OpenPersistedHistory(
                            taiwuId, _groupRoster, out historyError, persistedGroupId))
                        {
                            _groupHistoryOnly = true;
                            ClearLog();
                            RenderGroupHistory();
                            AppendSys("（本群当前未能恢复实时对话，本页暂时只回看历史；稍后可从左侧会话重新进入续聊。）");
                            _baseTitle = GroupConversationTitle() + " · 仅回看"; ApplyTitle();
                        }
                        else
                        {
                            string visibleError = string.IsNullOrWhiteSpace(err) ? "群聊成员验证失败" : err;
                            if (!string.IsNullOrWhiteSpace(historyError)) visibleError += "；" + historyError;
                            showSetup(visibleError);
                        }
                        Done();
                    },
                    persistedGroupId));
            else
            {
                showSetup("群聊宿主尚未就绪，请稍后重试");
                Done();
            }
        }

        string JoinMemberNames()
        {
            if (_group == null) return "";
            var sb = new StringBuilder();
            foreach (var m in _group.Members) { if (sb.Length > 0) sb.Append('、'); sb.Append(m.Name); }
            return sb.ToString();
        }

        string GroupConversationTitle()
        {
            var names = new List<string>();
            IReadOnlyList<KeyValuePair<int, string>> roster = GroupRosterSnapshot();
            if (roster != null)
                foreach (KeyValuePair<int, string> member in roster)
                {
                    if (member.Key <= 0) continue;
                    string name = TalkOrchestrator.IsUnresolvedNpcName(member.Value)
                        ? "姓名载入中…" : member.Value.Trim();
                    if (!names.Contains(name)) names.Add(name);
                }
            return names.Count > 0 ? "群聊 · " + string.Join("、", names) : "群聊";
        }

        // 群聊一轮:全体并发生成 → 依次揭示；最后一位等待群聊摘要或原文回退写入个人上下文后才结束生成。
        void RunGroupTurn(string text, TaiwuDirectActionReceipt directTaiwuAction = null,
            GameObject playerBubble = null)
        {
            if (_groupHistoryOnly)
            {
                AppendSys("（当前仅回看群聊历史；请稍后从左侧会话重新进入再续聊。）");
                return;
            }
            _busy = true; SetSendMode(true);
            var host = TalkEntryHost.Instance;
            if (host == null || _group == null) { AddBubble(BubbleKind.Npc, "（群聊未就绪）"); Done(); return; }
            _runningCoroutine = host.StartCoroutine(_group.ProcessGroupTurn(text,
                prog => SetProgress(prog),
                name => StartGroupSpeaker(name),                                  // 某成员开口:名签 + 思考点
                (name, delta) => AppendStreamingDelta(delta),                     // 该成员逐字
                (name, full, line) =>
                {
                    EndStreamingReply(full, line?.Date ?? 0, line?.LocationText);
                    _lastReplyText = full;
                },   // 该成员定稿(末位即本轮可朗读文)
                () => Done(),                                                     // 全员说完
                err => { if (_streamText != null) EndStreamingReply("（" + err + "）"); else AppendSys("（" + err + "）"); Done(); },
                () => CancelGroupSpeaker(),                                        // (并发模型沉默成员不显气泡,基本用不到;保留兼容)
                () => StartGroupThinking(),                                        // 本轮并发开始:集体「众人思量中…」气泡
                () => EndGroupThinking(),                                         // 本轮并发结束:移除集体思考气泡,转入逐位揭示
                (name, results) =>                                                // 无正文但有执行结果时仍标明结果归属并直接显示绿字
                {
                    AddSpeakerLabel(name);
                    AddPersistedExecutionRows(null, results);
                },
                directTaiwuAction,
                line => AddMessageMetadataAfter(playerBubble, BubbleKind.Player,
                    line?.Date ?? 0, line?.LocationText)));
            host.StartCoroutine(RefocusInput());
        }

        // 群聊:某成员【开口前】—— 先加一行青绿名签 + 一条思考气泡(DotsCo 在生成全程显示「某某 思忖中…/默忆所学中…」)
        void StartGroupSpeaker(string name)
        {
            _groupNameRow = AddSpeakerLabel(name);
            _progressLabel = (string.IsNullOrEmpty(name) ? "某人" : name) + " 思忖";
            StartReplyBubble();
        }

        // 群聊并发:本轮众成员【同时】生成期间,显示一个集体「众人思量中…」思考气泡(DotsCo 动画)
        void StartGroupThinking()
        {
            _progressLabel = "众人思量";
            StartReplyBubble();
        }

        // 群聊并发:本轮生成全部完成 → 移除集体思考气泡(随后逐位揭示各成员回话)
        void EndGroupThinking()
        {
            _dotsActive = false;
            if (_streamBubble != null) { try { Object.Destroy(_streamBubble); } catch { } }
            _streamBubble = null; _streamText = null; _streamRawText = null;
        }

        // 群聊:该成员这一轮【沉默没接话】→ 拆掉刚为他起的名签 + 思考气泡,让下一位从干净状态重起
        void CancelGroupSpeaker()
        {
            _dotsActive = false;
            if (_streamBubble != null) { try { Object.Destroy(_streamBubble); } catch { } }
            _streamBubble = null; _streamText = null; _streamRawText = null;
            if (_groupNameRow != null) { try { Object.Destroy(_groupNameRow); } catch { } _groupNameRow = null; }
        }

        // 群聊发言者名签(青绿小字),供流式开口与历史回放共用;返回名签行 GameObject(供沉默时拆除)
        GameObject AddSpeakerLabel(string name)
        {
            if (_content == null) return null;
            var t = NewText("NameRow", _content, 14, TextAlignmentOptions.TopLeft);
            t.raycastTarget = false; t.extraPadding = true;
            t.margin = new Vector4(16f, 6f, 12f, 0f);
            t.color = ColNameText; t.fontStyle = FontStyles.Bold;
            t.text = "「" + (string.IsNullOrEmpty(name) ? "某人" : name) + "」";
            return t.gameObject;
        }

        // 重开群聊:只回放【当前成员组合】的持久化对话。
        // 其他群是左侧导航里的独立会话，绝不能再拼进本群正文。
        void RenderGroupHistory()
        {
            if (_group == null || _content == null) return;
            bool anyShown = false;
            // 本支队伍此前的群聊(走分轮逻辑,可删除/重试),先标本队参与人。
            var hist = _group.Transcript;
            if (hist != null && hist.Count > 0)
            {
                AddDivider("群聊 · " + JoinMemberNames());
                Round cur = null;
                int start = 0;
                int lastMonth = -1, lastTurnMonth = -1;
                string replayExchangeId = null;
                void Close()
                {
                    if (cur == null) return;
                    for (int k = start; k < _content.childCount; k++) cur.rows.Add(_content.GetChild(k).gameObject);
                    BindGroupVoiceFromLastLine(cur);
                    AddRoundBar(cur); _rounds.Add(cur); cur = null; replayExchangeId = null;
                }
                foreach (var l in hist)
                {
                    if (l == null || (string.IsNullOrEmpty(l.Text)
                        && !HasPersistedToolResults(l.ToolResults))) continue;
                    if (l.Date > 0 && l.Date != lastMonth)
                    {
                        Close();
                        AddDivider(FormatDate(l.Date));
                        lastMonth = l.Date;
                    }
                    bool hasExchangeId = !string.IsNullOrWhiteSpace(l.ExchangeId);
                    if (hasExchangeId
                        && (cur == null || !string.Equals(replayExchangeId, l.ExchangeId, System.StringComparison.Ordinal)))
                    {
                        Close();
                        start = _content.childCount;
                        cur = new Round { mode = RMode.Group, proactive = true };
                        replayExchangeId = l.ExchangeId;
                    }
                    else if (!hasExchangeId && cur != null && replayExchangeId != null)
                    {
                        // Version 1-4 lines have no exchange identity. Never absorb them into a
                        // modern exchange: only those legacy lines retain the old player-line split.
                        Close();
                    }
                    if (l.IsTaiwu)
                    {
                        if (!hasExchangeId)
                        {
                            Close();
                            start = _content.childCount;
                            cur = new Round { mode = RMode.Group };
                        }
                        if (cur == null)
                        {
                            start = _content.childCount;
                            cur = new Round { mode = RMode.Group };
                        }
                        // A modern exchange may contain Taiwu interjections. They belong to the
                        // same editable round; only the first Taiwu line is the retry seed.
                        if (string.IsNullOrEmpty(cur.playerInput)) cur.playerInput = l.Text;
                        cur.proactive = false;
                        cur.groupLines.Add(l);
                        if (!string.IsNullOrEmpty(l.Text))
                        {
                            GameObject bubble = AddBubble(BubbleKind.Player, l.Text);
                            AddMessageMetadataAfter(bubble, BubbleKind.Player, l.Date, l.LocationText);
                        }
                    }
                    else
                    {
                        if (cur == null) { start = _content.childCount; cur = new Round { mode = RMode.Group, proactive = true }; }
                        cur.groupLines.Add(l);
                        AddSpeakerLabel(l.Speaker);
                        if (!string.IsNullOrEmpty(l.Text))
                        {
                            GameObject bubble = AddBubble(BubbleKind.Npc, l.Text);
                            AddMessageMetadataAfter(bubble, BubbleKind.Npc, l.Date, l.LocationText);
                        }
                        AddPersistedExecutionRows(l.Actions, l.ToolResults);
                        if (!string.IsNullOrWhiteSpace(l.ImageFileName))
                            AddPersistedRoundImage(cur, l.ImageFileName);
                    }
                    if (l.Date > 0) lastTurnMonth = l.Date;
                }
                Close();
                int currentMonth = CurrentDate();
                if (lastTurnMonth > 0 && currentMonth > 0 && lastTurnMonth < currentMonth)
                    AddDivider("如今 " + FormatDate(currentMonth));
                anyShown = true;
            }
            if (anyShown) AddDivider("以下为本次新说");
            ScrollDown();
            RefreshRoundBars();
        }

        void SetMonthlyCandidateButtonState(string label, bool interactable)
        {
            if (_monthlyCandidateBtn != null) _monthlyCandidateBtn.interactable = interactable;
            if (_monthlyCandidateLabel != null)
                _monthlyCandidateLabel.text = label ?? "加入主动";
        }

        internal void RefreshMonthlyCandidateButton()
        {
            if (_assistantMode || _groupMode || _npcId < 0 || _monthlyCandidateBtnGo == null) return;
            if (_requiresProxyBeforeMessage)
            {
                SetMonthlyCandidateButtonState("先发消息", false);
                return;
            }
            int taiwu = _taiwuId > 0 ? _taiwuId : ResolveTaiwuId();
            if (_soulMode || ArchivedCharacterStatusStore.IsDead(taiwu, _npcId))
            {
                _soulMode = true;
                SetMonthlyCandidateButtonState("灵魂不可主动", false);
                return;
            }
            int npc = _npcId;
            int generation = WorldLifecycle.Generation;
            int requestVersion = ++_monthlyCandidateRequestVersion;
            if (taiwu <= 0 || !WorldLifecycle.IsSameWorld(generation))
            {
                SetMonthlyCandidateButtonState("主动不可用", false);
                return;
            }
            SetMonthlyCandidateButtonState("主动状态…", false);
            EffectHandler.QueryNonBabyCompanionGroup(taiwu, (ok, ids) =>
            {
                if (requestVersion != _monthlyCandidateRequestVersion
                    || !WorldLifecycle.IsSameWorld(generation) || _npcId != npc) return;
                if (!ok)
                {
                    SetMonthlyCandidateButtonState("主动重试", true);
                    return;
                }
                bool companion = ids != null && ids.Contains(npc);
                CompanionMonthlyCandidateStore.Snapshot state =
                    CompanionMonthlyCandidateStore.Load(taiwu);
                bool selected = state.Included.Contains(npc)
                    || companion && !state.Excluded.Contains(npc);
                SetMonthlyCandidateButtonState(selected ? "移除主动" : "加入主动", true);
            });
        }

        void ToggleCurrentNpcMonthlyCandidate()
        {
            if (_assistantMode || _groupMode || _npcId < 0) return;
            if (_requiresProxyBeforeMessage)
            {
                AppendSys("（请先发送一条消息或开启互动记录，完成特殊人物复制后再加入主动名单。）");
                return;
            }
            int taiwu = _taiwuId > 0 ? _taiwuId : ResolveTaiwuId();
            if (_soulMode || ArchivedCharacterStatusStore.IsDead(taiwu, _npcId))
            {
                _soulMode = true;
                SetMonthlyCandidateButtonState("灵魂不可主动", false);
                AppendSys("（此人已故，只能以灵魂状态继续交谈，不能加入主动名单。）");
                return;
            }
            int npc = _npcId;
            int generation = WorldLifecycle.Generation;
            int requestVersion = ++_monthlyCandidateRequestVersion;
            if (taiwu <= 0 || !WorldLifecycle.IsSameWorld(generation))
            { AppendSys("（当前存档身份不可用，暂时无法修改主动名单）"); return; }
            SetMonthlyCandidateButtonState("正在处理…", false);
            EffectHandler.QueryNonBabyCompanionGroup(taiwu, (teamOk, companions) =>
            {
                if (requestVersion != _monthlyCandidateRequestVersion
                    || !WorldLifecycle.IsSameWorld(generation) || _npcId != npc) return;
                if (!teamOk)
                {
                    AppendSys("（同道名单读取失败，请稍后重试）");
                    SetMonthlyCandidateButtonState("主动重试", true);
                    return;
                }

                bool companion = companions != null && companions.Contains(npc);
                CompanionMonthlyCandidateStore.Snapshot state =
                    CompanionMonthlyCandidateStore.Load(taiwu);
                bool selected = state.Included.Contains(npc)
                    || companion && !state.Excluded.Contains(npc);
                if (selected)
                {
                    if (!CompanionMonthlyCandidateStore.Remove(taiwu, npc, companion))
                    {
                        AppendSys("（主动人物名单保存失败，请稍后重试）");
                        RefreshMonthlyCandidateButton();
                        return;
                    }
                    SetMonthlyCandidateButtonState("加入主动", true);
                    AppendSys(companion
                        ? "（已将「" + _npcName + "」从主动名单中移除；同道只是默认候选，仍可随时重新加入。）"
                        : "（已将「" + _npcName + "」从主动人物名单中移除。）");
                    ChatWindow.NotifyMonthlyCandidateChanged(taiwu, npc);
                    return;
                }

                System.Action addCandidate = () =>
                {
                    if (requestVersion != _monthlyCandidateRequestVersion
                        || !WorldLifecycle.IsSameWorld(generation) || _npcId != npc) return;
                    if (!CompanionMonthlyCandidateStore.Add(taiwu, npc))
                    {
                        AppendSys("（主动人物名单保存失败，请稍后重试）");
                        RefreshMonthlyCandidateButton();
                        return;
                    }
                    SetMonthlyCandidateButtonState("移除主动", true);
                    AppendSys(companion
                        ? "（已将同道「" + _npcName + "」重新加入主动名单。）"
                        : "（已将「" + _npcName + "」加入主动人物名单；主动来信与过月主动行事仍可在设置中分别关闭。）");
                    ChatWindow.NotifyMonthlyCandidateChanged(taiwu, npc);
                };
                if (companion)
                {
                    addCandidate();
                    return;
                }

                AppendSys("（正在确认此人能否加入主动名单…）");
                EffectHandler.QueryMonthlyAgentEligibility(new List<int> { npc }, (ok, ids, rejected) =>
                {
                    if (requestVersion != _monthlyCandidateRequestVersion
                        || !WorldLifecycle.IsSameWorld(generation) || _npcId != npc) return;
                    if (!ok || ids == null || !ids.Contains(npc))
                    {
                        AppendSys("（此人当前不能加入主动名单：婴儿、动物、死亡或失效人物会被排除）");
                        SetMonthlyCandidateButtonState("加入主动", true);
                        return;
                    }
                    addCandidate();
                });
            });
        }

        // 点“加人”:单聊/传音/助手页会创建新群；现有群聊则原地扩充成员，沿用同一个
        // GroupId 和共享记录。候选统一为“所有聊过的人 ∪ 打开面板时当前地块人物”。
        void StartAddMembers()
        {
            if (_busy) return;
            if (!_assistantMode && !_groupMode && _requiresProxyBeforeMessage)
            {
                AppendSys("（请先发送一条消息或开启互动记录，完成特殊人物复制后再加入群聊。）");
                return;
            }
            int taiwu = _taiwuId > 0 ? _taiwuId : ResolveTaiwuId();
            if (taiwu <= 0) { AppendSys("（找不到太吾，无法群聊）"); return; }
            int worldGeneration = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            if (worldId == 0 || !WorldLifecycle.IsSameWorld(worldGeneration)) return;

            var baseRoster = new List<KeyValuePair<int, string>>();
            var reserved = new HashSet<int>();
            if (_groupMode)
            {
                IReadOnlyList<KeyValuePair<int, string>> current = GroupRosterSnapshot();
                if (current != null)
                    foreach (KeyValuePair<int, string> member in current)
                        if (member.Key > 0 && member.Key != taiwu && reserved.Add(member.Key))
                            baseRoster.Add(member);
            }
            else if (!_assistantMode && !_groupMode && _npcId >= 0 && _npcId != taiwu && reserved.Add(_npcId))
            {
                baseRoster.Add(new KeyValuePair<int, string>(_npcId, _npcName));
            }
            if (baseRoster.Count >= 8)
            {
                AppendSys("（这个群已经有八人，不能再加了）");
                return;
            }

            GroupMemberPicker.Show(taiwu, _font, picked =>
            {
                if (!RootActive || !ChatWindow.IsActiveTab(this)
                    || !WorldLifecycle.IsSameWorld(worldGeneration) || WorldLifecycle.WorldId != worldId) return;
                if (picked == null) return;
                if (picked.Count == 0) { AppendSys("（没有选择要加入的人）"); return; }
                var additions = new List<KeyValuePair<int, string>>();
                foreach (int id in picked)
                    if (id > 0 && id != taiwu && !reserved.Contains(id))
                        additions.Add(new KeyValuePair<int, string>(id, null));
                var host = TalkEntryHost.Instance;
                if (host == null)
                {
                    AppendSys("（群聊宿主尚未就绪，请稍后重试）");
                    return;
                }
                _busy = true;
                SetSendMode(true);
                AppendSys("（正在确认群聊成员身份…）");
                _runningCoroutine = host.StartCoroutine(ValidateAndAddGroupMembers(
                    additions, baseRoster, reserved, taiwu, worldGeneration, worldId));
            }, reserved, _groupMode, reason =>
            {
                if (!RootActive || !ChatWindow.IsActiveTab(this)
                    || !WorldLifecycle.IsSameWorld(worldGeneration) || WorldLifecycle.WorldId != worldId) return;
                AppendSys("（" + (string.IsNullOrWhiteSpace(reason)
                    ? "加人窗口暂时无法打开，请稍后重试" : reason) + "）");
            });
        }

        int ResolveTaiwuId()
        {
            try { return SingletonObject.getInstance<BasicGameData>().TaiwuCharId; } catch { return -1; }
        }

        /// <summary>聊天窗是否打开(供右键拦截 Harmony patch 与宿主轮询读取,零分配)。</summary>
        public bool IsOpen => _root != null && _root.activeSelf;

        /// <summary>当前打开的是不是「灵儿」自己的对话框(助手模式)。灵儿主动消息只在这种情况下避让;
        /// 打开 NPC 单聊/群聊或没开窗口时,灵儿照常能主动捎话。</summary>
        public bool IsAssistantOpen => IsOpen && _assistantMode;

        // 临时让位/复原(HideForOverlay/RestoreFromOverlay)与关窗(Hide)已上移至 ChatWindow 外壳门面:
        // 多页签下「显隐/关闭」是窗口级动作,由门面统一对当前活动页签 + 页签栏处理。本页签只负责自己这段对话的收尾。

        // 本页签根物体的显隐(门面切换页签时调用;后台页签隐藏不影响其协程继续写自己的气泡)
        public GameObject Root => _root;
        public TMP_FontAsset Font => _font;
        public bool RootActive => _root != null && _root.activeSelf;
        public void SetRootActive(bool on)
        {
            if (!on)
            {
                CancelPendingDictation();
                InvalidateAllVoice();
                InvalidateAllImages();
            }
            if (_root != null) _root.SetActive(on);
            if (on) SyncPersistedHistoryIfIdle();
            if (on) RequestContactModeRefresh();
        }

        internal void NotifyPersistedHistoryChanged()
        {
            if (_assistantMode || _groupMode) return;
            _persistedHistoryRefreshPending = true;
            SyncPersistedHistoryIfIdle();
        }

        void SyncPersistedHistoryIfIdle()
        {
            if (!_persistedHistoryRefreshPending || _busy || _assistantMode || _groupMode
                || _root == null || !_root.activeSelf) return;
            _persistedHistoryRefreshPending = false;
            ClearLog();
            RenderHistory();
        }
        public bool Busy => _busy;
        public bool AssistantMode => _assistantMode;
        public bool GroupMode => _groupMode;
        public bool GroupHistoryOnly => _groupHistoryOnly;
        public bool RemoteMode => _remote;
        public int TaiwuId => _taiwuId;
        public int NpcId => _npcId;
        public string GroupId => _groupMode
            ? (!string.IsNullOrWhiteSpace(_group?.GroupId) ? _group.GroupId : _requestedGroupId)
            : null;
        public long NavigationSortKey;
        public List<KeyValuePair<int, string>> GroupRosterSnapshot()
        {
            var roster = new List<KeyValuePair<int, string>>();
            if (!_groupMode) return roster;
            if (_group?.Members != null && _group.Members.Count > 0)
            {
                foreach (var member in _group.Members)
                    if (member != null && member.Id > 0)
                        roster.Add(new KeyValuePair<int, string>(member.Id, member.Name));
            }
            else if (_groupRoster != null)
            {
                foreach (var member in _groupRoster)
                    if (member.Key > 0) roster.Add(member);
            }
            return roster;
        }
        public bool TryGetSingleContext(out int taiwuId, out int npcId, out string npcName)
        {
            taiwuId = _taiwuId; npcId = _npcId; npcName = _npcName;
            return !_assistantMode && !_groupMode && !_remote && _taiwuId > 0 && _npcId >= 0;
        }
        public bool Unread;                 // 后台页签生成完成且非当前页 → 标未读点,切回即清
        public string Key;                  // 门面用:会话标识(npc:id / rmt:id / asst / group)去重
        public long LastUsed;               // 最近一次被激活的使用序号(越大=越近用过);达页签上限时淘汰最小者(LRU)
        // 页签 chip 短名:单聊/千里传音=NPC名,群聊统一列出具体人名,助手=灵儿名。
        // 不用罕见符号作前缀:游戏字体缺字时会显示成「口」。
        public string TabTitle
        {
            get
            {
                if (_assistantMode) return string.IsNullOrEmpty(_npcName) ? "灵儿" : _npcName;
                if (_groupMode) return GroupConversationTitle();
                string nm = TalkOrchestrator.IsUnresolvedNpcName(_npcName) ? "姓名载入中…" : _npcName;
                return nm;
            }
        }

        /// <summary>结束本页签这段对话的收尾(记忆固化/刷新),不负责显隐。
        /// 新建群聊从不改写来源页签，因此本方法不再把群页就地变回单聊。</summary>
        public bool CloseSelf(bool allowReopenSingle = true)
        {
            InvalidateAllVoice();
            InvalidateAllImages();
            TalkEntryInjector.RefreshNpcDisplay(_npcId);   // 只刷新仍在原生事件窗中的同一 NPC；奇遇换人后不碰新目标
            if (!_groupMode && _npcId >= 0) { try { NativeUiRefresh.AfterRelationChange(_npcId); } catch { } }   // 关窗即促原生互动菜单按新关系重评
            // 一段对话结束:把新沉淀的对话记忆固化进长期记忆画像(后台)
            var host = TalkEntryHost.Instance;
            if (host != null && !_groupMode && _npcId >= 0) host.StartCoroutine(ConsolidatePortrait(_npcId));
            // 群聊收尾:把这场群聊写回每位参与 NPC 的个人记忆+画像
            if (host != null && _groupMode && !_groupHistoryOnly && _group != null)
                host.StartCoroutine(_group.ConsolidateMembers());
            return false;
        }

        /// <summary>群聊切页/最小化时触发持久化恢复门。
        /// 正常回合会在最后一位生成阶段压缩并保存；这里只恢复已中断的回合。</summary>
        public void ConsolidateGroupMemoryNow()
        {
            try
            {
                if (_groupMode && !_groupHistoryOnly && _group != null) _group.ConsolidateMemoryOnly();
            }
            catch { }
        }

        // 清空按钮"确认清空"态 4 秒未再点 → 自动复位回「清空」(期间又点过=已真清/重新计时,则不动)。
        IEnumerator DisarmClear(float[] armAt, TMPro.TextMeshProUGUI lbl, Image img, Color col0)
        {
            float a = armAt[0];
            yield return new WaitForSecondsRealtime(4f);
            if (armAt[0] == a) { armAt[0] = -99f; if (lbl != null) lbl.text = "清空"; if (img != null) img.color = col0; }
        }

        IEnumerator ConsolidatePortrait(int npcId)
        {
            NpcSnapshot snap = null;
            yield return NpcSnapshotReader.Fetch(npcId, s => snap = s);
            if (snap != null && snap.TaiwuId > 0)
                yield return PortraitService.Consolidate(snap);
        }

        // 递话键三用:空闲=发送;群聊忙且输入框有字=插话(插进当前对话流、不打断);其余忙=中断当前轮
        void OnSendOrInterrupt()
        {
            if (_combatTransitionBusy) return;
            if (!_busy) { Send(); return; }
            if (_groupMode && _group != null && _input != null && !string.IsNullOrWhiteSpace(_input.text)) GroupInterject();
            else Interrupt();
        }

        // 群聊途中插话:回复进行中,太吾再发一句 → 立刻显示、插进对话流,接下来的人顺着这句往下接(无需先中断)。
        void GroupInterject()
        {
            if (!_groupMode || _groupHistoryOnly || _group == null || _content == null || _input == null) return;
            CancelPendingSuggestion();
            CancelPendingDictation();
            string text = (_input.text ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return;
            GroupChatOrchestrator.Line committedLine;
            if (!_group.Interject(text, out committedLine))
            {
                // Keep the draft intact when the bounded response window has closed or
                // the transcript write failed. A visible bubble always means durable.
                AppendSys("（本轮群聊已收尾或记录尚未可靠保存，这句尚未发出；可在本轮结束后重新发送。）");
                RefreshSendLabel();
                return;
            }
            ClearComposer(false);
            GameObject bubble = AppendPlayer(text); // 只有绑定当前 exchange 且持久化成功后才显示
            AddMessageMetadataAfter(bubble, BubbleKind.Player,
                committedLine?.Date ?? 0, committedLine?.LocationText);
            ChatWindow.OnMessageSent(this);
            QueueStandaloneSubmitRefocus();
            RefreshSendLabel();
        }

        void Send()
        {
            if (_busy) return;
            if (_groupMode && _groupHistoryOnly)
            {
                AppendSys("（当前仅回看群聊历史；请稍后从左侧会话重新进入再续聊。）");
                return;
            }
            CancelPendingSuggestion();
            CancelPendingDictation();
            string text = _input.text == null ? null : _input.text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            // JHYL_CHAT_HELP_COMMAND:玩家功能帮助(所有构建、单聊/群聊/助手页签一致)。
            // 帮助是纯本地只读文本,不产生轮次、不进聊天记录、不触发任何工具。
            if (TryShowHelpCommand(text))
            {
                ClearComposer(false);
                QueueStandaloneSubmitRefocus();
                return;
            }
            // JHYL_DEBUG_COMMAND_LOCAL_SINGLE_ONLY:调试暗号只在存活人物的当面普通单聊中执行。
            // 群聊若让每位成员各自消费同一句暗号，会把动作重复落地；助手、灵魂与千里传音也没有
            // 与“本地单聊完整工具超集”一致的现场。这里统一本地拒绝，绝不把暗号送给模型。
            string slashCommand = NormalizeSlashCommand(text);
            if (slashCommand != null)
            {
                if (_assistantMode || _groupMode || _soulMode || _remote)
                {
                    AppendSys("（调试暗号只在存活人物的当面普通单聊中执行；请切换到目标人物单聊后再试。）");
                    ClearComposer(false);
                    QueueStandaloneSubmitRefocus();
                    return;
                }
                text = slashCommand;
            }
            ClearComposer(false);
            _discardSubmitLineBreakOnRefocus = true;
            Resend(text);
        }

        // /帮助、/help、/?、单个 / (含全角/／)都弹玩家功能速览。
        bool TryShowHelpCommand(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Replace('／', '/');
            if (s.Length == 0 || s[0] != '/') return false;
            string cmd = s.Substring(1).Trim().ToLowerInvariant();
            if (cmd.Length != 0 && cmd != "帮助" && cmd != "help" && cmd != "?" && cmd != "？") return false;
            string help = PlayerHelpText();
            // JHYL_HELP_DEBUG_APPEND:作者要求 /帮助 直接在玩家速览之后附上【完整调试暗号表】,
            // 所有构建(含 Release,即部署进游戏的构建)一致,省得作者再记 /列表。调试暗号是玩家键入、
            // LLM 不可达的单机作弊面,与 LLM 信任边界无关(见 TalkOrchestrator.HandleTestCommand 注释)。
            help += "\n\n" + TalkOrchestrator.TestHelp();
            AppendSys(help);
            return true;
        }

        // 返回标准半角斜杠命令；普通台词返回 null。全角斜杠与 /帮助 的既有输入体验保持一致。
        static string NormalizeSlashCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = text.Trim();
            if (s[0] == '／') s = "/" + s.Substring(1);
            return s[0] == '/' ? s : null;
        }

        // 玩家效果向的功能速览(0.30)。只描述玩家能感知的效果,不写内部术语;
        // 功能增减时与 deploy/announcement 和灵儿知识库一起更新。
        internal static string PlayerHelpText()
        {
            return
                "【功能速览】输入 /帮助 随时重看\n" +
                "· 说话:回车发送;Ctrl+Enter 只换行不发送;群聊回复进行中再按回车可直接插话\n" +
                "· 口述:点「口述」用 Windows 语音键入,识别文字只进输入框,看清楚再发\n" +
                "· 代笔:点「代笔」替太吾拟话;默认会从单聊、群聊和灵儿页的真实发言逐渐模仿你的用词、口气和字数,设置中可关闭\n" +
                "· 悔棋:每轮回话都可「重试」或「删除」,只抹掉那一轮沉淀的记忆,已办成的事不回退\n" +
                "· 配音与生图:NPC 回话可一键配音或生图;火山配音按人物特征和每段台词动态调整音色、语气与语速;生图会把 NPC 与太吾形象合成参考图并结合最近聊天生成场景\n" +
                "· 多线聊:左侧永久保留每段会话,点名字切换,他们在后台各想各的回话\n" +
                "· 群聊:每组群聊在左侧单独保存;任何单聊或群聊都能继续加人,候选含所有聊过的人和当前地块人物;远方成员也能说话,当面之事仍须碰面\n" +
                "· 主动来信:人物会依记忆、关系和处境主动找你,未读会在会话列表标出\n" +
                "· 人物委托:NPC 可在正常对话中自主提出请求;灵儿按设置的现实时间间隔用纯代码随机生成委托,品级概率随本体世界进度变化且各品级始终有机会;任务只进入当前上下文、不写长期记忆,仍可随时询问完成指导;右侧点「委」查看真实进度,达到或超过目标即可直接领取随机奖励\n" +
                "· 生平经历:人物查阅自己过往时会取得较早经历摘要与最近本体原文;摘要由后台模型按人物独立维护,回档后自动按真实记录重建\n" +
                "· 动手:当面单聊可由你或对方提出切磋/相搏/生死斗,点确认就进游戏本体战斗\n" +
                "· 千里传音:人物页有「千里传音」键,与远方之人隔空叙旧、打听、托事;传功赠物等当面之事隔空办不到\n" +
                "· 说得动就真办:赠物、传功、结义、做媒、买卖、疗伤、赴约等说成了都会真实发生,以游戏面板为准\n" +
                "· 快捷行动:单聊/群聊下方可由太吾直接赠物、传授或写书赠送;赠物列表不会显示太吾当前装备;单聊顶部还能管理主动名单、查看详情、编辑人设、加人和导出\n" +
                "· 梳头修面:当面提出并说定后点回执按钮进入游戏本体界面自行装扮;过月与主动来信不会替你改外貌\n" +
                "· 主动名单:当前同道默认加入但可移除,聊过的其他人物也可手动加入;名单同时用于主动来信与过月主动行事,两项可分别关闭;江湖大事逐月连载,每回至少办成3件且覆盖2类不同实事\n" +
                "· 身后旧话:聊过的人去世后不会删除聊天、人设、记忆或画像;首次确认死亡时会恢复曾隐藏的聊天入口并标为灵魂状态,之后仍可再次隐藏;灵魂只能被动交谈,不能主动发消息、查询、行动、调用任何工具或参与过月\n" +
                "· 特殊人物:固定模板人物单纯打开对话不会复制;首次发送消息或开启互动记录时,才会为当前人物独立生成合法普通副本(位置失效时落在太吾村),并保留原角色形象\n" +
                "· 往事成书:人物页或单聊顶部点「往事成书」,把你与当前人物的聊天、此人参与的群聊和此人的记忆写成小说;文风可改,成稿可导出\n" +
                "· 灵儿与支持:灵儿页可导出诊断日志并查看 Mod 介绍;对话窗右上可打开自愿支持说明和二维码\n" +
                "· 设置:聊天窗右上「设置」页配接口与模型、语音、生图、世界书、人设、难度和回复篇幅;字体可选小(默认)、中、大并即时生效";
        }

        // 语音输入只负责让 Windows 把识别文本键入当前输入框；后续仍由玩家确认并点「递话」。
        // 单聊、群聊和灵儿共用本 ChatTab 输入框，因此一次接入覆盖三种模式，也不会绕过原有安全工具链。
        void StartDictation()
        {
            if (_dictationCoroutine != null) return;
            if (_input == null || !_input.interactable || _input.readOnly || !IsOpen)
            {
                AddSysNotice("（当前输入框不可用，未启动语音键入）");
                return;
            }

            var host = TalkEntryHost.Instance;
            if (host == null)
            {
                AddSysNotice("（宿主缺失，无法启动语音键入）");
                return;
            }
            _dictationCancellation = new CancellationTokenSource();
            _dictationCoroutine = host.StartCoroutine(StartDictationAfterFocus(_dictationCancellation));
        }

        IEnumerator StartDictationAfterFocus(CancellationTokenSource cancellation)
        {
            if (_dictationBtn != null) _dictationBtn.interactable = false;

            // Button 的点击流程可能短暂抢走 EventSystem 选中对象；再延一帧确认输入框已恢复焦点。
            try
            {
                if (_input != null)
                {
                    _input.Select();
                    _input.ActivateInputField();
                }
            }
            catch { }
            yield return null;

            string error = null;
            Task<string> task = null;
            if (cancellation == null || cancellation.IsCancellationRequested)
                error = WindowsDictation.CanceledMessage;
            else if (!IsOpen || !ChatWindow.IsActiveTab(this) || !InputOwnsFocus())
                error = "聊天页已切换，未发送 Win+H";
            else if (!WindowsDictation.TryStart(cancellation.Token, out task, out error))
                task = null;

            while (task != null && !task.IsCompleted) yield return null;
            if (task != null)
            {
                try { error = task.Result; }
                catch (System.Exception ex) { error = "语音键入任务失败：" + ex.GetBaseException().Message; }
            }

            if (ReferenceEquals(_dictationCancellation, cancellation)) _dictationCancellation = null;
            try { cancellation?.Dispose(); } catch { }
            _dictationCoroutine = null;
            if (_dictationBtn != null) _dictationBtn.interactable = !_combatTransitionBusy;
            if (error == WindowsDictation.CanceledMessage)
            {
                Debug.Log("[JHYL_VOICE_INPUT] pending Win+H sequence canceled before dictation start");
            }
            else if (!string.IsNullOrEmpty(error))
            {
                Debug.LogWarning("[JHYL_VOICE_INPUT] " + error);
                if (IsOpen) AddSysNotice("（语音输入：" + error + "）");
            }
            else
            {
                Debug.Log("[JHYL_VOICE_INPUT] Windows Win+H shortcut sent; transcript stays in input field");
                if (!_dictationHintShown && IsOpen)
                {
                    _dictationHintShown = true;
                    AddSysNotice("（已发送 Windows Win+H。请确保系统语音键入已开启、联网且麦克风可用；说完再次点“口述”或按 Win+H 停止。识别文字只留在输入框，确认后再递话。）");
                }
            }
        }

        bool InputOwnsFocus()
        {
            try
            {
                // EventSystem 在场景切换时缺失也必须 fail-closed；仅凭 TMP 残留的 isFocused 不足以证明听写目标仍正确。
                return _input != null && _input.interactable && !_input.readOnly && _input.isFocused
                    && EventSystem.current != null && EventSystem.current.currentSelectedGameObject == _input.gameObject;
            }
            catch
            {
                // Unity 已 Destroy 的组件仍可能保留非 CLR-null 引用；销毁竞态一律当作失焦。
                return false;
            }
        }

        void CancelPendingDictation()
        {
            var cancellation = _dictationCancellation;
            if (cancellation == null || cancellation.IsCancellationRequested) return;
            try { cancellation.Cancel(); } catch { }
        }

        // 重新发送一句(发送 / 重试共用):开新一轮 → 显示玩家气泡 → 按模式分发生成。
        // retryAvoid:重试时传入上一回话原文,单聊轮会显式要求换说法(免逐字雷同)。
        void Resend(string text, string retryAvoid = null)
        {
            if (string.IsNullOrEmpty(text) || _busy || _content == null) return;
            if (!_assistantMode && !_groupMode && !_soulMode && !_proxyIdentityReady)
            {
                BeginProxyPreparationForMessage(text, retryAvoid);
                return;
            }
            ResendPrepared(text, retryAvoid);
        }

        void ResendPrepared(string text, string retryAvoid)
        {
            if (string.IsNullOrEmpty(text) || _busy || _content == null) return;
            if (!_assistantMode && !_groupMode
                && NpcProactiveChatScheduler.IsGeneratingFor(_taiwuId, _npcId))
            {
                AppendSys("（对方正在给你发来消息，请稍候片刻再发送）");
                RestoreInputFocus();
                return;
            }
            InvalidateCombatOffers();
            BeginRound(text);
            GameObject playerBubble = AppendPlayer(text);
            ChatWindow.OnMessageSent(this);
            if (_assistantMode) RunAssistantTurn(text);
            else if (_groupMode) RunGroupTurn(text, null, playerBubble);
            else RunTurn(text, retryAvoid, null, playerBubble);
        }

        void BeginProxyPreparationForMessage(string text, string retryAvoid)
        {
            if (_proxyPrepareBusy || _busy || string.IsNullOrEmpty(text)) return;
            var host = TalkEntryHost.Instance;
            if (host == null)
            {
                RestoreUnsentProxyDraft(text, "对话宿主尚未就绪，未能准备人物副本");
                return;
            }

            _proxyPrepareBusy = true;
            _busy = true;
            if (_input != null) _input.interactable = false;
            SetSendMode(true);
            GameObject statusBubble = AddBubble(BubbleKind.Sys,
                _requiresProxyBeforeMessage ? "（正在复制中…）" : "（正在确认人物身份…）");
            TextMeshProUGUI statusText = statusBubble != null
                ? statusBubble.GetComponent<TextMeshProUGUI>() : null;
            ScrollDown();

            if (!_proxyIdentityChecked)
            {
                _runningCoroutine = host.StartCoroutine(ProbeIdentityBeforeMessage(
                    text, retryAvoid, statusText));
                return;
            }
            ContinueProxyPreparationForMessage(text, retryAvoid, statusText);
        }

        IEnumerator ProbeIdentityBeforeMessage(string text, string retryAvoid,
            TextMeshProUGUI statusText)
        {
            int generation = WorldLifecycle.Generation;
            int probedNpcId = _npcId;
            NpcSnapshot snap = null;
            yield return NpcSnapshotReader.FetchDisplayOnly(probedNpcId, value => snap = value,
                () => _proxyPrepareBusy && WorldLifecycle.IsSameWorld(generation)
                    && _npcId == probedNpcId);
            _runningCoroutine = null;
            if (!_proxyPrepareBusy || !WorldLifecycle.IsSameWorld(generation)) yield break;
            ApplyProxyIdentityProbe(probedNpcId, snap);
            if (!_proxyIdentityChecked)
            {
                RestoreUnsentProxyDraft(text, "人物身份读取失败，这句话尚未发出", statusText);
                yield break;
            }
            ContinueProxyPreparationForMessage(text, retryAvoid, statusText);
        }

        void ContinueProxyPreparationForMessage(string text, string retryAvoid,
            TextMeshProUGUI statusText)
        {
            if (!_proxyPrepareBusy) return;
            if (_proxyIdentityReady)
            {
                FinishProxyPreparationUi();
                ResendPrepared(text, retryAvoid);
                return;
            }

            if (statusText != null) statusText.text = "（正在复制中…）";
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            int sourceNpcId = _proxySourceNpcId >= 0 ? _proxySourceNpcId : _npcId;
            CharacterProxyIdentityService.EnsureForConversation(_taiwuId, sourceNpcId,
                (resolvedId, error) =>
                {
                    if (!_proxyPrepareBusy || !WorldLifecycle.IsSameWorld(generation)
                        || WorldLifecycle.WorldId != worldId)
                        return;
                    if (!string.IsNullOrWhiteSpace(error) || resolvedId <= 0)
                    {
                        RestoreUnsentProxyDraft(text,
                            "复制失败：" + (string.IsNullOrWhiteSpace(error)
                                ? "人物身份解析失败" : error), statusText);
                        return;
                    }

                    if (_npcId != resolvedId)
                        TryAdoptMigratedIdentity(sourceNpcId, resolvedId);
                    _proxySourceNpcId = resolvedId;
                    _proxyIdentityChecked = true;
                    _proxyIdentityReady = true;
                    _requiresProxyBeforeMessage = false;
                    FinishProxyPreparationUi();
                    AppendSys("（复制完成，正在递出这句话…）");
                    ResendPrepared(text, retryAvoid);
                });
        }

        void FinishProxyPreparationUi()
        {
            _proxyPrepareBusy = false;
            _busy = false;
            _runningCoroutine = null;
            if (_input != null) _input.interactable = true;
            SetSendMode(false);
            RefreshMonthlyCandidateButton();
            RefreshTaiwuActionBar();
        }

        void RestoreUnsentProxyDraft(string text, string reason,
            TextMeshProUGUI statusText = null)
        {
            FinishProxyPreparationUi();
            string visible = "（" + (reason ?? "复制失败，这句话尚未发出") + "）";
            if (statusText != null) statusText.text = visible;
            else AppendSys(visible);
            if (_input != null)
            {
                _input.text = text ?? string.Empty;
                _input.ResetEmptyVisualState();
            }
            RestoreInputFocus();
        }

        internal bool TryAdoptMigratedIdentity(int oldNpcId, int newNpcId)
        {
            if (_assistantMode || _groupMode || oldNpcId < 0 || newNpcId < 0
                || _npcId != oldNpcId || (_busy && !_proxyPrepareBusy))
                return false;
            CancelContactModeRefresh();
            _npcId = newNpcId;
            _proxySourceNpcId = newNpcId;
            _proxyIdentityChecked = true;
            _proxyIdentityReady = true;
            _requiresProxyBeforeMessage = false;
            Key = (_remote ? "rmt:" : "npc:") + WorldLifecycle.Generation + ":"
                + _taiwuId + ":" + _npcId;
            ClearLog();
            RenderHistory();
            RefreshCharacterArchiveStatus();
            RefreshMonthlyCandidateButton();
            RefreshTaiwuActionBar();
            RequestContactModeRefresh();
            return true;
        }

        void OpenTaiwuDirectAction(TaiwuDirectActionKind kind)
        {
            if (_assistantMode || _busy || _combatTransitionBusy || _taiwuId <= 0) return;
            if (!_groupMode && _requiresProxyBeforeMessage)
            {
                AppendSys("（请先发送一条消息或开启互动记录，完成特殊人物复制后再执行行动。）");
                return;
            }
            if (!_groupMode && NpcProactiveChatScheduler.IsGeneratingFor(_taiwuId, _npcId))
            {
                AppendSys("（对方正在给你发来消息，请稍候片刻再行动）");
                return;
            }
            if (_groupHistoryOnly)
            {
                AppendSys("（当前群聊只能回看历史，不能执行太吾行动）");
                return;
            }
            if (!_groupMode && (_npcId < 0 || _remote))
            {
                AppendSys("（此刻只能千里传音，需当面才能赠物、传授或赠书）");
                return;
            }

            var targets = new List<KeyValuePair<int, string>>();
            if (_groupMode)
            {
                IReadOnlyList<KeyValuePair<int, string>> roster = GroupRosterSnapshot();
                if (roster != null)
                    foreach (KeyValuePair<int, string> member in roster)
                        if (member.Key > 0 && member.Key != _taiwuId)
                            targets.Add(member);
            }
            else targets.Add(new KeyValuePair<int, string>(_npcId, _npcName));
            if (targets.Count == 0)
            {
                AppendSys("（当前没有可选择的行动对象）");
                return;
            }

            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            TaiwuDirectActionWindow.Open(_font, _taiwuId, targets, _groupMode, kind,
                (selections, accompanyingText) =>
                {
                    if (!RootActive || _busy || !WorldLifecycle.IsSameWorld(generation)
                        || WorldLifecycle.WorldId != worldId
                        || selections == null || selections.Count == 0) return;
                    StartTaiwuDirectActionBatch(selections, accompanyingText);
                },
                reason =>
                {
                    if (!RootActive || !WorldLifecycle.IsSameWorld(generation)
                        || WorldLifecycle.WorldId != worldId) return;
                    AppendSys("（" + (string.IsNullOrWhiteSpace(reason)
                        ? "行动选择窗暂时无法打开" : reason) + "）");
                });
        }

        void StartTaiwuDirectActionBatch(IList<TaiwuDirectActionSelection> selections,
            string accompanyingText)
        {
            if (_busy || selections == null || selections.Count == 0) return;
            ConfigHost host = ConfigHost.Instance;
            if (host == null)
            {
                AppendSys("（行动执行宿主尚未就绪，请稍后重试）");
                return;
            }
            _busy = true;
            if (_input != null) _input.interactable = false;
            SetSendMode(true);
            AppendSys("（正在按所选清单执行太吾行动……）");
            _runningCoroutine = host.StartCoroutine(ExecuteTaiwuDirectActionBatch(
                selections, accompanyingText));
        }

        IEnumerator ExecuteTaiwuDirectActionBatch(IList<TaiwuDirectActionSelection> selections,
            string accompanyingText)
        {
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            int taiwuId = _taiwuId;
            var receipts = new List<TaiwuDirectActionReceipt>();
            foreach (TaiwuDirectActionSelection selection in selections)
            {
                if (!WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId
                    || taiwuId != _taiwuId) break;
                TaiwuDirectActionReceipt receipt = null;
                yield return TaiwuDirectActionExecutor.Execute(taiwuId, selection,
                    value => receipt = value);
                if (receipt != null) receipts.Add(receipt);
            }
            if (!WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId
                || taiwuId != _taiwuId)
            {
                FinishDirectActionWithoutReply("（存档已切换，本批行动不再写入当前聊天）");
                yield break;
            }

            TaiwuDirectActionReceipt combined = TaiwuDirectActionReceipt.Combine(receipts);
            if (combined == null)
            {
                FinishDirectActionWithoutReply("（本批行动没有取得可用结果，请查看日志）");
                yield break;
            }

            _runningCoroutine = null;
            if (_input != null) _input.interactable = true;
            // 代码结果先成为本轮玩家可见行动，再交给目标 NPC。单聊只由当前人物回应；
            // 群聊则由父编排器严格路由到玩家刚才选中的收件人。选择窗里附带的话
            // 与行动回执合成同一条玩家消息，保证可见记录、持久化和模型上下文一致。
            string playerText = ComposeDirectActionPlayerText(
                combined.PlayerText, accompanyingText);
            BeginRound(playerText);
            GameObject playerBubble = AppendPlayer(playerText);
            AddGrantNotice(combined.ToolResultText, null, null);
            ChatWindow.OnMessageSent(this);
            if (_groupMode) RunGroupTurn(playerText, combined, playerBubble);
            else RunTurn(playerText, null, combined, playerBubble);
        }

        static string ComposeDirectActionPlayerText(string actionText,
            string accompanyingText)
        {
            string action = (actionText ?? string.Empty).Trim();
            string note = (accompanyingText ?? string.Empty).Trim();
            if (note.Length == 0) return action;
            if (action.Length == 0) return note;
            return action + "\n\n太吾说道：\n" + note;
        }

        void FinishDirectActionWithoutReply(string message)
        {
            _busy = false;
            _runningCoroutine = null;
            if (_input != null) _input.interactable = true;
            SetSendMode(false);
            if (!string.IsNullOrWhiteSpace(message)) AppendSys(message);
            RequestContactModeRefresh();
            RestoreInputFocus();
        }

        // 跑一轮(发送或重试共用)。玩家输入由调用方负责显示,这里不重复显示。
        void RunTurn(string text, string retryAvoid = null,
            TaiwuDirectActionReceipt directTaiwuAction = null,
            GameObject playerBubble = null)
        {
            // 主动来信期间，发送入口会先等待其完整提交，避免真实动作已经落地后又因玩家
            // 输入取消而丢失对应正文或工具回执；因此这里不会中断另一个编排器。
            CancelContactModeRefresh();
            _busy = true; SetSendMode(true);
            var host = TalkEntryHost.Instance;
            if (host == null) { AddBubble(BubbleKind.Npc, "(宿主缺失,无法对话)"); Done(); return; }
            StartReplyBubble();   // NPC 回话行,先显示进度行(思考/查资料/落地期间)
            var orch = new TalkOrchestrator();
            orch.Remote = _remote;
            orch.DirectTaiwuAction = directTaiwuAction;
            orch.GrantSink = AddGrantNotice; orch.SysSink = AddSysNotice;   // 多页签:绿提示/系统提示落回本页签,不串到当前活动页
            _currentOrch = orch;
            _runningCoroutine = host.StartCoroutine(orch.ProcessTurn(_npcId, text,
                reply =>
                {
                    AddMessageMetadataAfter(playerBubble, BubbleKind.Player,
                        orch.LastTurnDate, orch.LastPlayerLocationText);
                    EndStreamingReply(reply, orch.LastTurnDate, orch.LastNpcLocationText);
                    _lastReplyText = reply;
                    if (_cur != null) _cur.exchangeId = orch.LastCommittedExchangeId;
                    var combat = orch.TakePendingCombat();
                    if (combat != null) AddCombatOffer(combat);   // 必须在 Done 前加入，才能归入本轮删除/重试范围
                    var grooming = orch.TakePendingGrooming();
                    if (grooming != null) AddGroomingOffer(grooming);
                    Done();
                },
                err => { EndStreamingReply("(" + err + ")"); Done(); },
                delta => AppendStreamingDelta(delta),                                                    // 最终回话逐字显示
                prog => SetProgress(prog),                                                               // 进度行动态文字
                null,                                                                                    // token 用量不再显示到聊天窗标题
                think => AppendThinkingDelta(think),                                                     // 思考过程逐字(流式)
                () => ResetStreamingReply(),                                                            // 本轮非终稿 → 清掉已显示的临时正文
                retryAvoid,                                                                              // #5 重试:避免与上一回话雷同
                remote => ApplyContactMode(remote)));                                                    // 模型/工具路由前发布本轮代码权威距离
            host.StartCoroutine(RefocusInput());
        }

        void AddCombatOffer(TalkOrchestrator.PendingCombatRequest request)
        {
            if (request == null) return;
            string mode = request.Mode == "play" ? "切磋" : (request.Mode == "beat" ? "相搏" : "生死斗");
            string button = request.Mode == "play" ? "进入切磋" : (request.Mode == "beat" ? "迎战" : "确认死斗");
            int offerVersion = ++_combatOfferVersion;
            AddActionNotice("已与 " + (request.NpcName ?? _npcName) + " 约定" + mode + "（尚未开战）", button, () =>
            {
                if (offerVersion != _combatOfferVersion) { AddSysNotice("（这份旧约战已经失效，请在最新一轮重新约战）"); return; }
                if (_combatTransitionBusy || _busy) return;
                var host = TalkEntryHost.Instance;
                if (host == null) { AddSysNotice("（宿主缺失，无法进入战斗）"); return; }
                if (!BeginCombatTransition())
                {
                    AddSysNotice("（另一对话页签正在进入战斗；请等待该次入场完成）");
                    return;
                }
                _runningCoroutine = host.StartCoroutine(StartNativeCombat(request, offerVersion));
            });
        }

        void InvalidateCombatOffers()
        {
            unchecked { _combatOfferVersion++; }
        }

        bool BeginCombatTransition()
        {
            if (!ChatWindow.TryAcquireCombatTransition(this)) return false;
            CancelPendingSuggestion();
            _combatTransitionBusy = true;
            _busy = true;
            if (_input != null) _input.interactable = false;
            if (_suggestBtn != null) _suggestBtn.interactable = false;
            if (_dictationBtn != null) _dictationBtn.interactable = false;
            SetSendMode(true);
            return true;
        }

        void EndCombatTransition(bool restoreFocus)
        {
            ChatWindow.ReleaseCombatTransition(this);
            _combatTransitionBusy = false;
            _busy = false;
            _runningCoroutine = null;
            if (_input != null) _input.interactable = true;
            if (_suggestBtn != null) _suggestBtn.interactable = !_suggesting;
            if (_dictationBtn != null) _dictationBtn.interactable = _dictationCoroutine == null;
            SetSendMode(false);
            if (restoreFocus) RestoreInputFocus();
        }

        static bool NativeCombatVisible()
            => UIElement.Combat.Exist || UIElement.CombatBegin.Exist || UIElement.CombatResult.Exist;

        static bool BlockingCombatUi()
        {
            if (NativeCombatVisible()
                || UIElement.LifeSkillCombatOld.Exist || UIElement.LifeSkillCombatBegin.Exist || UIElement.LifeSkillCombatPrepare.Exist
                || UIElement.Debate.Exist || UIElement.DebateResult.Exist
                || UIElement.CricketCombat.Exist || UIElement.CricketCombatResult.Exist
                || UIElement.BuildingArea.Exist || UIElement.MonthNotify.Exist || UIElement.AdventurePrepareRemake.Exist)
                return true;
            try { if (SingletonObject.getInstance<BasicGameData>().AdvancingMonthState != 0) return true; } catch { }
            try
            {
                var adventure = SingletonObject.getInstance<AdventureRemakeModel>();
                if (adventure != null && (adventure.AdventureTaiwu.InAdventure || adventure.AdventureMajorEventTaiwu.InAdventure)) return true;
            }
            catch { }
            return false;
        }

        IEnumerator StartNativeCombat(TalkOrchestrator.PendingCombatRequest request, int offerVersion)
        {
            string Reject(string reason)
            {
                ChatWindow.CancelPendingNativeCombatResult(request);
                if (ChatWindow.IsActiveTab(this) && !RootActive) ChatWindow.RestoreHiddenActive();
                AddSysNotice("（未能开战：" + reason + "）");
                EndCombatTransition(true);
                return reason;
            }
            if (!_combatTransitionBusy || offerVersion != _combatOfferVersion)
            { Reject("这份约战已经失效"); yield break; }
            if (request == null || !OperationId.IsValid(request.OperationId) || request.WorldId == 0
                || _assistantMode || _groupMode || _remote || _npcId != request.NpcId || _taiwuId != request.TaiwuId)
            { Reject("原对话已改变，请重新与对方约战"); yield break; }
            if (!ChatWindow.IsActiveTab(this)) { Reject("请先切回发起约战的对话页签"); yield break; }
            if (!WorldLifecycle.IsSameWorld(request.WorldGeneration) || WorldLifecycle.WorldId != request.WorldId)
            { Reject("已切换存档，旧约战已失效"); yield break; }
            if (BlockingCombatUi()) { Reject("当前界面正处于战斗、较艺、辩论、促织、奇遇或过月流程"); yield break; }

            ChatWindow.Hide();
            yield return null;   // 先让自建 Canvas 隐去，再交给带回执的后端权威入口
            if (!WorldLifecycle.IsSameWorld(request.WorldGeneration) || WorldLifecycle.WorldId != request.WorldId
                || !_combatTransitionBusy || offerVersion != _combatOfferVersion || BlockingCombatUi())
            { Reject("战斗入口状态已变化"); yield break; }

            // From this point the offer is consumed forever.  Timeout/rejection may query this
            // exact operation id, but no UI path is allowed to mint and dispatch a replacement.
            InvalidateCombatOffers();
            if (!ChatWindow.ArmPendingNativeCombatResult(request))
            {
                Reject("战斗会话上下文已失效，请重新与对方约战");
                yield break;
            }
            bool callbackDone = false, startSucceeded = false;
            string startMessage = null;
            try
            {
                EffectHandler.StartCombat(request.NpcId, request.CombatConfig, (ok, message) =>
                {
                    if (callbackDone) return;
                    startSucceeded = ok;
                    startMessage = message;
                    callbackDone = true;
                }, request.OperationId);
                Debug.Log("[江湖有灵] 约战已派发到带账本后端 npc=" + request.NpcId
                    + " config=" + request.CombatConfig + " initiator=" + request.Initiator
                    + " gen=" + request.WorldGeneration + " op=" + request.OperationId);
            }
            catch (System.Exception ex)
            {
                // Dispatch may already have crossed the bridge.  Never retry with a new id;
                // reconcile below using the same operation identity.
                startMessage = "UNKNOWN:dispatch_exception:" + ex.GetType().Name;
            }

            float receiptDeadline = Time.unscaledTime + 18f;
            while (!callbackDone && Time.unscaledTime < receiptDeadline
                && WorldLifecycle.IsSameWorld(request.WorldGeneration) && _combatTransitionBusy)
                yield return null;
            if (!WorldLifecycle.IsSameWorld(request.WorldGeneration) || WorldLifecycle.WorldId != request.WorldId)
            { EndCombatTransition(false); yield break; }

            bool terminal = callbackDone && (startSucceeded
                || string.IsNullOrWhiteSpace(startMessage)
                || !startMessage.StartsWith("UNKNOWN:", System.StringComparison.OrdinalIgnoreCase));
            if (!callbackDone || !terminal)
            {
                ToolOutcome reconciled = null; bool queryDone = false;
                EffectHandler.QueryOperation(request.WorldId, request.TaiwuId, request.OperationId,
                    value => { reconciled = value; queryDone = true; });
                float queryDeadline = Time.unscaledTime + 8f;
                while (!queryDone && Time.unscaledTime < queryDeadline
                    && WorldLifecycle.IsSameWorld(request.WorldGeneration) && _combatTransitionBusy)
                    yield return null;
                if (reconciled != null && reconciled.IsTerminal)
                {
                    terminal = true;
                    startSucceeded = reconciled.IsSucceeded;
                    startMessage = reconciled.Message ?? reconciled.Code;
                }
                else
                {
                    terminal = false;
                    startSucceeded = false;
                    startMessage = reconciled?.Message ?? startMessage ?? "回执暂不可得";
                }
            }

            if (terminal)
                EffectHandler.AcknowledgeOperation(request.WorldId, request.TaiwuId, request.OperationId);
            if (terminal && !startSucceeded)
            {
                Reject(startMessage ?? "后端权威检查拒绝了这次约战");
                yield break;
            }

            // 非终态表示派发可能已经跨过 RPC 边界。此时绝不能把它当成失败恢复聊天、
            // 取消战果上下文或另发一场；持续用同一 operation id 对账，直到原生界面出现、
            // 后端给出明确拒绝，或世界生命周期结束。
            if (!terminal)
            {
                Debug.LogWarning("[JHYL_COMBAT_ENTRY_PENDING] receipt unavailable; keep observing op="
                    + request.OperationId + " message=" + (startMessage ?? "unknown"));
                while (!NativeCombatVisible()
                    && WorldLifecycle.IsSameWorld(request.WorldGeneration) && _combatTransitionBusy)
                {
                    ToolOutcome late = null; bool lateDone = false;
                    EffectHandler.QueryOperation(request.WorldId, request.TaiwuId, request.OperationId,
                        value => { late = value; lateDone = true; });
                    float lateDeadline = Time.unscaledTime + 8f;
                    while (!lateDone && !NativeCombatVisible() && Time.unscaledTime < lateDeadline
                        && WorldLifecycle.IsSameWorld(request.WorldGeneration) && _combatTransitionBusy)
                        yield return null;
                    if (NativeCombatVisible()) break;
                    if (late != null && late.IsTerminal)
                    {
                        EffectHandler.AcknowledgeOperation(request.WorldId, request.TaiwuId, request.OperationId);
                        terminal = true;
                        startSucceeded = late.IsSucceeded;
                        startMessage = late.Message ?? late.Code;
                        if (!startSucceeded)
                        {
                            Reject(startMessage ?? "后端权威检查拒绝了这次约战");
                            yield break;
                        }
                        break;
                    }
                    yield return new WaitForSecondsRealtime(1f);
                }
            }

            // Backend receipt is authoritative.  The UI event may arrive a few frames later;
            // delayed loading is not a rejection and never reopens/cancels the consumed operation.
            float slowEntryWarningAt = Time.unscaledTime + 8f;
            bool slowEntryLogged = false;
            while (!NativeCombatVisible()
                && WorldLifecycle.IsSameWorld(request.WorldGeneration) && _combatTransitionBusy)
            {
                if (!slowEntryLogged && Time.unscaledTime >= slowEntryWarningAt)
                {
                    slowEntryLogged = true;
                    Debug.LogWarning("[JHYL_COMBAT_ENTRY_DELAYED] backend committed; waiting for native UI op="
                        + request.OperationId);
                }
                yield return null;
            }
            if (!WorldLifecycle.IsSameWorld(request.WorldGeneration) || WorldLifecycle.WorldId != request.WorldId)
            {
                EndCombatTransition(false);
                yield break;
            }
            if (!_combatTransitionBusy || !NativeCombatVisible()) yield break;

            EndCombatTransition(false);
            // 原生生死斗可能在结果阶段直接销毁对手。旧实现只是把聊天 Canvas 隐藏，
            // 页签、回调和被击杀 NPC 的会话仍常驻；战斗结果清理与这些活引用交错时会让
            // 原生 CombatResult/Character 清理链卡死。确认原生战斗已经出现后，消费并销毁
            // 发起页签（不做画像蒸馏、不在战斗中重读死者），无论胜负都由玩家事后重新开聊。
            // 上面的入场循环已经亲眼见到 CombatBegin / Combat / CombatResult；把这个事实
            // 交给战后监视器，避免它恰好从 CombatBegin→StateCombat 的空帧开始而误判未入场。
            ChatWindow.ConsumeSourceTabForNativeCombat(this, nativeCombatObserved: true);
        }

        /// <summary>打开「助手模式」——与悬浮 AI 助手对话(攻略/百晓策/改设置/功能建议),复用本聊天窗。</summary>
        public void OpenAssistant(TMP_FontAsset font = null)
        {
            _assistantMode = true; _groupMode = false; _group = null; _currentOrch = null; _currentAsst = null;
            _soulMode = false;
            _groupRoster = null; _requestedGroupId = null; _groupHistoryOnly = false;
            _npcId = 0; _taiwuId = ResolveTaiwuId();
            string nm = AssistantNameStore.Load();
            _npcName = nm;
            if (font != null) _font = font;
            if (_font == null) _font = ResolveFont();
            GlyphSanitizer.SetFont(_font);
            if (_root == null) Build();
            _root.SetActive(true);
            if (_groupBtnGo != null) _groupBtnGo.SetActive(true);
            if (_monthlyCandidateBtnGo != null) _monthlyCandidateBtnGo.SetActive(false);
            if (_novelButtonGo != null) _novelButtonGo.SetActive(false);
            if (_detailButtonGo != null) _detailButtonGo.SetActive(false);
            RefreshTaiwuActionBar();
            _baseTitle = nm + " · 江湖有灵助手"; ApplyTitle();
            ClearLog();
            _streamText = null; _streamRawText = null; _streamBubble = null; _dotsActive = false; _thinkText = null; _thinkBubble = null;
            // 回放灵儿历史对话(把她当 NPC 一样有持久历史);无历史才显示开场白
            var ah = AssistantOrchestrator.HistorySnapshot();
            bool anyHist = false;
            if (ah != null && ah.Count > 0)
                foreach (var m in ah)
                    if (m != null && !string.IsNullOrEmpty(m.Content)) { AddBubble(m.Role == "user" ? BubbleKind.Player : BubbleKind.Npc, m.Content); anyHist = true; }
            MarkAssistantHistoryRendered(ah);
            if (anyHist) ScrollDown();
            else AppendSys("（我是「" + nm + "」——问我攻略、查百晓册,或让我帮你改设置;不知道这 mod 能玩啥,也尽管问。）");
            ClearComposer(true);
        }

        void MarkAssistantHistoryRendered(IList<LlmMessage> history = null)
        {
            if (history == null) history = AssistantOrchestrator.HistorySnapshot();
            _lastRenderedAssistantHistoryMessage = null;
            if (history != null)
                for (int i = history.Count - 1; i >= 0; i--)
                    if (history[i] != null && !string.IsNullOrEmpty(history[i].Content))
                    {
                        _lastRenderedAssistantHistoryMessage = history[i];
                        break;
                    }
            _assistantHistoryViewInitialized = true;
        }

        /// <summary>
        /// 把已持久化但尚未显示的灵儿主动消息增量补进现有助手页签。
        /// 忙碌时严格延后，避免新气泡落入 _curStartChild 之后而被本轮删除/重试误收。
        /// 用消息对象引用而不是数量作游标，历史达到 40 条滚动上限后仍能识别新尾项。
        /// </summary>
        internal void SyncAssistantHistoryIfIdle()
        {
            if (!_assistantMode || _busy || _content == null) return;
            List<LlmMessage> history = AssistantOrchestrator.HistorySnapshot();
            if (history == null) history = new List<LlmMessage>();
            int start = 0;
            if (_assistantHistoryViewInitialized && _lastRenderedAssistantHistoryMessage != null)
            {
                int anchor = -1;
                for (int i = history.Count - 1; i >= 0; i--)
                    if (object.ReferenceEquals(history[i], _lastRenderedAssistantHistoryMessage))
                    {
                        anchor = i;
                        break;
                    }
                if (anchor >= 0) start = anchor + 1;
                else
                {
                    // 页签闲置超过滚动历史上限，旧锚点已被裁掉；只重建当前 40 条 UI，
                    // 不改磁盘，也不把旧轮控制条绑定到错误的新消息上。
                    ClearLog();
                    start = 0;
                }
            }
            bool appended = false;
            for (int i = start; i < history.Count; i++)
            {
                LlmMessage message = history[i];
                if (message == null || string.IsNullOrEmpty(message.Content)) continue;
                AddBubble(message.Role == "user" ? BubbleKind.Player : BubbleKind.Npc,
                    message.Content);
                appended = true;
            }
            MarkAssistantHistoryRendered(history);
            if (appended) ScrollDown();
        }

        void ReleaseAssistantPriority()
        {
            try { _assistantPriorityLease?.Dispose(); } catch { }
            _assistantPriorityLease = null;
        }

        // 助手模式跑一轮(非流式):复用回话气泡 + 进度行
        void RunAssistantTurn(string text)
        {
            ReleaseAssistantPriority();
            _assistantPriorityLease = AssistantWidget.BeginInteractivePriority();
            _busy = true; SetSendMode(true);
            var host = TalkEntryHost.Instance;
            if (host == null) { AddBubble(BubbleKind.Npc, "(宿主缺失,无法对话)"); Done(); return; }
            StartReplyBubble();
            var asst = new AssistantOrchestrator();
            _currentAsst = asst;
            _runningCoroutine = host.StartCoroutine(asst.ProcessTurn(text,
                reply =>
                {
                    EndStreamingReply(reply);
                    _lastReplyText = reply;
                    var linked = asst.LastCommittedExchangeSnapshot();
                    if (_cur != null) _cur.assistantHistoryCommitted = true;
                    if (_cur != null && linked.Count == 2
                        && linked[0] != null && linked[0].Role == "user" && linked[0].Content == text
                        && linked[1] != null && linked[1].Role == "assistant" && linked[1].Content == reply)
                        _cur.assistantMessages.AddRange(linked);
                    else Debug.LogWarning("[江湖有灵] 助手轮已提交但缺少精确历史引用，将拒绝删除或重试以免误删");
                    // 这一对气泡已经由当前轮显示；推进游标，避免 Done 的增量同步重复追加。
                    MarkAssistantHistoryRendered();
                    Done();
                },
                err => { EndStreamingReply("(" + err + ")"); Done(); },
                prog => SetProgress(prog)));
            host.StartCoroutine(RefocusInput());
        }

        void AddGroomingOffer(TalkOrchestrator.PendingGroomingRequest request)
        {
            if (request == null) return;
            int offerVersion = ++_combatOfferVersion;
            AddActionNotice("已与 " + (request.NpcName ?? _npcName) + " 说定梳头修面（尚未进入本体互动）", "梳头修面", () =>
            {
                if (offerVersion != _combatOfferVersion)
                {
                    AddSysNotice("（这份旧梳妆约定已经失效，请在最新一轮重新提出）");
                    return;
                }
                if (_combatTransitionBusy || _busy) return;
                var host = TalkEntryHost.Instance;
                if (host == null) { AddSysNotice("（宿主缺失，无法打开本体互动）"); return; }
                if (!BeginCombatTransition())
                {
                    AddSysNotice("（另一对话页签正在进入原生互动，请稍候）");
                    return;
                }
                _runningCoroutine = host.StartCoroutine(StartNativeGrooming(request, offerVersion));
            });
        }

        IEnumerator ValidateAndAddGroupMembers(List<KeyValuePair<int, string>> additions,
            List<KeyValuePair<int, string>> baseRoster, HashSet<int> reserved, int taiwu,
            int worldGeneration, uint worldId)
        {
            var normalized = new List<KeyValuePair<int, string>>();
            var normalizedIds = new HashSet<int>(reserved);
            var blocked = new List<string>();
            foreach (KeyValuePair<int, string> addition in additions)
            {
                if (!RootActive || !ChatWindow.IsActiveTab(this)
                    || !WorldLifecycle.IsSameWorld(worldGeneration)
                    || WorldLifecycle.WorldId != worldId)
                {
                    FinishGroupMemberValidation();
                    yield break;
                }
                int resolvedId = CharacterProxyIdentityService.ResolveKnown(taiwu, addition.Key);
                if (resolvedId <= 0 || resolvedId == taiwu || !normalizedIds.Add(resolvedId))
                    continue;
                NpcSnapshot snap = null;
                yield return NpcSnapshotReader.FetchDisplayOnly(resolvedId, value => snap = value,
                    () => RootActive && ChatWindow.IsActiveTab(this)
                        && WorldLifecycle.IsSameWorld(worldGeneration)
                        && WorldLifecycle.WorldId == worldId);
                if (snap == null || !snap.DisplayLoaded)
                {
                    FinishGroupMemberValidation();
                    AppendSys("（人物身份读取失败，尚未改动群聊成员。）");
                    yield break;
                }
                if (snap.CreatingType != 1)
                {
                    string name = !TalkOrchestrator.IsUnresolvedNpcName(snap.Name)
                        ? snap.Name : "所选特殊人物";
                    if (!blocked.Contains(name)) blocked.Add(name);
                    continue;
                }
                normalized.Add(new KeyValuePair<int, string>(resolvedId,
                    !TalkOrchestrator.IsUnresolvedNpcName(snap.Name) ? snap.Name : addition.Value));
            }

            if (blocked.Count > 0)
            {
                FinishGroupMemberValidation();
                AppendSys("（" + string.Join("、", blocked)
                    + "尚未生成普通人物副本。请先在单聊中发送消息或开启互动记录，再将其加入群聊。）");
                yield break;
            }
            if (_groupMode)
            {
                if (_groupHistoryOnly || _group == null)
                {
                    FinishGroupMemberValidation();
                    AppendSys("（当前群聊只能回看历史，暂时不能加人）");
                    yield break;
                }
                AppendSys("（正在把新人加入当前群聊；新人会看到本群此前聊天记录。）");
                var host = TalkEntryHost.Instance;
                if (host == null)
                {
                    FinishGroupMemberValidation();
                    AppendSys("（群聊宿主尚未就绪，请稍后重试）");
                    yield break;
                }
                _runningCoroutine = host.StartCoroutine(_group.AddMembers(normalized,
                    progress => SetProgress(progress),
                    () =>
                    {
                        _groupRoster = GroupRosterSnapshot();
                        _baseTitle = GroupConversationTitle();
                        ApplyTitle();
                        FinishGroupMemberValidation();
                        AppendSys("（已加入当前群聊；新人可以看到本群此前聊天记录。）");
                        ChatWindow.OnGroupMembershipChanged(this);
                        RestoreInputFocus();
                    },
                    error =>
                    {
                        FinishGroupMemberValidation();
                        AppendSys("（" + (string.IsNullOrWhiteSpace(error)
                            ? "加入群聊失败，原群成员没有改变" : error) + "）");
                        ChatWindow.NotifyTabChanged(this);
                        RestoreInputFocus();
                    }));
                yield break;
            }

            var roster = new List<KeyValuePair<int, string>>(baseRoster);
            var seen = new HashSet<int>(reserved);
            foreach (KeyValuePair<int, string> addition in normalized)
                if (seen.Add(addition.Key)) roster.Add(addition);
            FinishGroupMemberValidation();
            if (roster.Count == 0) { AppendSys("（没选中任何人，群聊作罢）"); yield break; }
            if (roster.Count == 1) { AppendSys("（只一个人成不了群——再加一位再来）"); yield break; }
            ChatWindow.OpenGroup(roster, taiwu, _font);
        }

        void FinishGroupMemberValidation()
        {
            _busy = false;
            _runningCoroutine = null;
            SetSendMode(false);
        }

        IEnumerator StartNativeGrooming(TalkOrchestrator.PendingGroomingRequest request, int offerVersion)
        {
            void Reject(string reason)
            {
                if (ChatWindow.IsActiveTab(this) && !RootActive) ChatWindow.RestoreHiddenActive();
                AddSysNotice("（未能打开梳头修面：" + reason + "）");
                EndCombatTransition(true);
            }

            if (!_combatTransitionBusy || offerVersion != _combatOfferVersion
                || request == null || request.WorldId == 0 || _assistantMode || _groupMode || _remote
                || _npcId != request.NpcId || _taiwuId != request.TaiwuId)
            { Reject("原对话现场已改变，请重新与对方说定"); yield break; }
            if (!ChatWindow.IsActiveTab(this))
            { Reject("请先切回发起梳头修面的对话页签"); yield break; }
            if (!WorldLifecycle.IsSameWorld(request.WorldGeneration) || WorldLifecycle.WorldId != request.WorldId)
            { Reject("已切换存档，旧约定已失效"); yield break; }
            if (BlockingCombatUi())
            { Reject("当前正处于战斗、较艺、辩论、奇遇或过月流程"); yield break; }

            ChatWindow.Hide();
            yield return null;
            if (UIElement.CharacterMenu.Exist)
            {
                UIManager.Instance.HideUI(UIElement.CharacterMenu);
                float menuDeadline = Time.unscaledTime + 3f;
                while (UIElement.CharacterMenu.Exist && Time.unscaledTime < menuDeadline
                    && WorldLifecycle.IsSameWorld(request.WorldGeneration) && _combatTransitionBusy)
                    yield return null;
                if (UIElement.CharacterMenu.Exist)
                { Reject("人物菜单尚未关闭，请关闭后重试"); yield break; }
            }
            bool done = false, accepted = false;
            NativeGroomingInteraction.Start(request.TaiwuId, request.NpcId,
                ok => { accepted = ok; done = true; });
            float deadline = Time.unscaledTime + 12f;
            while (!done && Time.unscaledTime < deadline
                && WorldLifecycle.IsSameWorld(request.WorldGeneration) && _combatTransitionBusy)
                yield return null;

            if (!WorldLifecycle.IsSameWorld(request.WorldGeneration) || WorldLifecycle.WorldId != request.WorldId)
            { EndCombatTransition(false); yield break; }
            if (!done)
            { Reject("本体入口暂未返回，请稍后从人物原生互动中重试"); yield break; }
            if (!accepted)
            { Reject("原生梳头修面界面未能创建"); yield break; }

            InvalidateCombatOffers();
            EndCombatTransition(false);
        }

        void IntroduceMod()
        {
            if (!_assistantMode || _busy || _combatTransitionBusy) return;
            ModIntroductionWindow.Open(_font);
        }

        void OpenSourceProject()
        {
            if (_busy || _combatTransitionBusy) return;
            ModIntroductionWindow.OpenOpenSource(_font);
        }

        void SupportDevelopment()
        {
            if (_busy || _combatTransitionBusy) return;
            ModIntroductionWindow.OpenSupport(_font);
        }

        void ExportAssistantDiagnosticLogs()
        {
            if (!_assistantMode || _busy || _combatTransitionBusy || _assistantDiagnosticExportBusy) return;
            var host = TalkEntryHost.Instance;
            if (host == null) { AppendSys("（导出失败：聊天宿主缺失）"); return; }
            _assistantDiagnosticExportBusy = true;
            RefreshTaiwuActionBar();
            AppendSys("（正在后台导出诊断日志……）");
            Task<DiagnosticLogExportResult> task = Task.Run(
                () => DiagnosticLogExportService.ExportToDesktop(CancellationToken.None));
            host.StartCoroutine(ExportAssistantDiagnosticLogsCo(task));
        }

        IEnumerator ExportAssistantDiagnosticLogsCo(Task<DiagnosticLogExportResult> task)
        {
            while (!task.IsCompleted) yield return null;
            _assistantDiagnosticExportBusy = false;
            RefreshTaiwuActionBar();
            if (task.IsCanceled)
            {
                AppendSys("（日志导出已取消）");
                yield break;
            }
            if (task.IsFaulted || task.Result == null)
            {
                AppendSys("（日志导出失败：后台复制任务异常）");
                yield break;
            }
            DiagnosticLogExportResult result = task.Result;
            bool pathCopied = false;
            if (result.Ok && !string.IsNullOrWhiteSpace(result.Path))
                try { GUIUtility.systemCopyBuffer = result.Path; pathCopied = true; } catch { }
            string message = result.ToUserMessage();
            if (result.Ok && !string.IsNullOrWhiteSpace(result.Path))
                message += pathCopied
                    ? "\n导出文件夹的完整路径已复制；粘贴到资源管理器地址栏即可打开。"
                    : "\n未能自动复制路径，请手动复制上面的完整路径到资源管理器地址栏。";
            AppendSys(message);
        }

        // 进度行:把当前工具的动作文字喂给思考行(DotsCo 会带动画点显示)
        void SetProgress(string label)
        {
            _progressLabel = string.IsNullOrEmpty(label) ? "思忖" : label.TrimEnd('…', '.', '·');
        }

        void ApplyTitle()
        {
            if (_title == null) return;
            _title.text = _baseTitle;
            ChatWindow.NotifyTabChanged(this);   // 模式/人数变 → 刷新页签 chip 文字
        }

        // 清空当前 NPC 的聊天记录(会话+梗概+文件)并清屏(任务7)
        void ClearHistory()
        {
            if (_busy) return;
            if (_assistantMode)
            {
                if (AssistantOrchestrator.ResetHistory())
                {
                    ClearLog();
                    MarkAssistantHistoryRendered();
                    AppendSys("（已清空与助手的对话）");
                    ChatWindow.NotifyNavigationStorageChanged();
                }
                else AppendSys("（清空失败：助手记录未写入磁盘，原对话仍保留，请查看日志并重试）");
                return;
            }
            if (_groupMode)
            {
                if (_group != null && _group.ClearHistory())
                {
                    DeleteRenderedRoundImageFiles();
                    ClearLog();
                    AppendSys("（已清空本群聊天记录）");
                    ChatWindow.NotifyNavigationStorageChanged();
                }
                else AppendSys("（清空失败：群聊记录未从磁盘完整移除，请查看日志并重试）");
                return;
            }
            if (TalkOrchestrator.ClearConversation(_taiwuId, _npcId))
            {
                DeleteRenderedRoundImageFiles();
                ClearLog();
                AppendSys("（已清空与 " + _npcName + " 的聊天记录）");
                ChatWindow.NotifyNavigationStorageChanged();
            }
            else AppendSys("（清空失败：请查看日志并重试）");
        }

        void DeleteRenderedRoundImageFiles()
        {
            foreach (Round round in _rounds)
                if (round != null && !string.IsNullOrWhiteSpace(round.imageFileName))
                    DeleteGeneratedImageFile(round.imageFileName);
        }

        void ExportCurrent()
        {
            if (_busy) { AppendSys("（本轮仍在生成，请等回话完成后再导出）"); return; }
            int taiwuId = _taiwuId, npcId = _npcId;
            string npcName = _npcName;
            bool assistantMode = _assistantMode, groupMode = _groupMode;
            IReadOnlyList<LlmMessage> assistantHistory = assistantMode
                ? new List<LlmMessage>(AssistantOrchestrator.HistorySnapshot() ?? new List<LlmMessage>()) : null;
            List<GroupChatOrchestrator.Member> members = null;
            List<GroupChatOrchestrator.Line> lines = null;
            if (groupMode)
            {
                members = new List<GroupChatOrchestrator.Member>();
                if (_group?.Members != null) foreach (var member in _group.Members)
                    if (member != null) members.Add(new GroupChatOrchestrator.Member { Id = member.Id, Name = member.Name });
                lines = new List<GroupChatOrchestrator.Line>();
                if (_group?.Transcript != null) foreach (var line in _group.Transcript)
                    if (line != null) lines.Add(new GroupChatOrchestrator.Line
                    {
                        Id = line.Id, ExchangeId = line.ExchangeId, CommitAttemptId = line.CommitAttemptId,
                        SpeakerId = line.SpeakerId, Speaker = line.Speaker, IsTaiwu = line.IsTaiwu,
                        Text = line.Text, Date = line.Date, CreatedUtc = line.CreatedUtc,
                        MemoryIds = line.MemoryIds == null ? null : new List<string>(line.MemoryIds),
                        Actions = line.Actions == null ? null : new List<string>(line.Actions),
                        ToolResults = line.ToolResults == null ? null : new List<string>(line.ToolResults),
                    });
            }
            var host = TalkEntryHost.Instance;
            if (host == null) { AppendSys("（导出失败：聊天宿主缺失）"); return; }
            AppendSys("（正在后台导出聊天记录……）");
            ChatExportService.ExportScope scope = ChatExportService.CaptureScope(taiwuId);
            if (scope == null)
            {
                AppendSys("（存档状态已变化，已取消本次导出）");
                return;
            }
            Task<ChatExportService.Result> task = Task.Run(() => assistantMode
                ? ChatExportService.ExportAssistant(scope, taiwuId, assistantHistory)
                : groupMode ? ChatExportService.ExportGroup(scope, taiwuId, members, lines)
                : ChatExportService.ExportSingle(scope, taiwuId, npcId, npcName));
            host.StartCoroutine(ExportCurrentCo(task, scope));
        }

        IEnumerator ExportCurrentCo(Task<ChatExportService.Result> task,
            ChatExportService.ExportScope scope)
        {
            while (!task.IsCompleted) yield return null;
            if (scope == null || !scope.IsCurrent) yield break;
            ChatExportService.Result result = task.IsFaulted ? null : task.Result;
            if (result != null && result.Ok)
            {
                try { GUIUtility.systemCopyBuffer = result.Path; } catch { }
                AppendSys("（聊天记录已导出，路径已复制：" + result.Path + "）");
            }
            else AppendSys("（导出失败：" + (result?.Error ?? "未知错误") + "）");
        }

        public void Interrupt()
        {
            CancelContactModeRefresh();
            CancelPendingSuggestion();
            CancelPendingDictation();
            InvalidateCombatOffers();
            try
            {
                if (_assistantMode) _currentAsst?.Cancel();
                else if (_groupMode) _group?.Cancel();
                else
                {
                    _currentOrch?.Cancel();
                    _currentOrch?.AbandonCurrentTurnOperationObservers();
                }
            }
            catch { }
            try { if (_runningCoroutine != null && TalkEntryHost.Instance != null) TalkEntryHost.Instance.StopCoroutine(_runningCoroutine); } catch { }
            _runningCoroutine = null;
            _currentOrch = null; _currentAsst = null;
            // UI 收尾:StopCoroutine 只停了生成协程,思考点动画(DotsCo)仍在转、在制回话行也没定稿。
            // 停掉思考点并给在制回话行定稿——已流出正文则保留,仅思考中(无正文)则标「已中断」;
            // 再让这半截交流走正常收尾(FinalizeRound)拿到删除/重试控制条,与错误轮/正常轮一致。
            try
            {
                if (_streamText != null)
                {
                    bool hasPartial = !_dotsActive && !string.IsNullOrWhiteSpace(_streamText.text);
                    EndStreamingReply(hasPartial ? null : "(已中断)");
                }
                else _dotsActive = false;   // 无回话行(如群聊接通阶段被打断)也要停掉可能残留的思考点
            }
            catch { }
            try { FinalizeRound(); } catch { }
            if (_combatTransitionBusy) EndCombatTransition(false);
            else ChatWindow.ReleaseCombatTransition(this);
            if (_input != null) _input.interactable = true;
            _busy = false;
            SetSendMode(false);
            SyncAssistantHistoryIfIdle();
            SyncPersistedHistoryIfIdle();
            ReleaseAssistantPriority();
            RequestContactModeRefresh();
        }

        bool IsCanceled(string err) => !string.IsNullOrEmpty(err) && (err.Contains("已中断") || err.Contains("已取消"));

        void Done()
        {
            _busy = false; _runningCoroutine = null; _currentOrch = null; _currentAsst = null;
            SetSendMode(false);
            FinalizeRound();
            SyncAssistantHistoryIfIdle();
            SyncPersistedHistoryIfIdle();
            ReleaseAssistantPriority();
            ChatWindow.OnTabDone(this);
            RequestContactModeRefresh();
        }

        // —— 按轮删除 / 重试:轮次生命周期 ——
        RMode CurMode() => _assistantMode ? RMode.Assistant : (_groupMode ? RMode.Group : RMode.Single);

        // 开一轮:记下起点(收尾据此快照本轮所有行)。在 Resend 显示玩家气泡前调用。
        void BeginRound(string playerInput)
        {
            _cur = new Round { mode = CurMode(), playerInput = playerInput };
            _curStartChild = _content != null ? _content.childCount : 0;
            _curGroupFrom = (_groupMode && _group != null) ? _group.Transcript.Count : 0;
            _lastReplyText = null;
        }

        // 收尾一轮(Done 时):快照本轮所有行 + 关联(turn 引用 / 记忆 id / 群聊行),挂上控制条。
        void FinalizeRound()
        {
            if (_cur == null) return;
            var r = _cur; _cur = null;
            if (_content == null) return;
            for (int i = _curStartChild; i < _content.childCount; i++)
                r.rows.Add(_content.GetChild(i).gameObject);
            if (r.rows.Count == 0) return;   // 没产出任何行(异常),不挂条
            r.replyText = _lastReplyText; r.replyNpcId = _npcId; r.isAsst = (r.mode == RMode.Assistant);   // 供「语音」朗读本轮回话
            if (r.mode == RMode.Single) CaptureSingleLinkage(r);
            else if (r.mode == RMode.Group && _group != null)
            {
                var tr = _group.Transcript;
                for (int i = _curGroupFrom; i < tr.Count; i++) if (tr[i] != null) r.groupLines.Add(tr[i]);
                BindGroupVoiceFromLastLine(r);
            }
            try { AddRoundBar(r); }
            catch (System.Exception e) { Debug.LogWarning("[江湖有灵] 聊天轮控制条创建失败:" + e.GetType().Name); }
            if (r.bar == null) Debug.LogWarning("[江湖有灵] 聊天轮控制条缺失: mode=" + r.mode + " input=" + (string.IsNullOrEmpty(r.playerInput) ? 0 : r.playerInput.Length) + " reply=" + (string.IsNullOrEmpty(r.replyText) ? 0 : r.replyText.Length));
            else Debug.Log("[JHYL_CHAT_ROUND_BAR] mode=" + r.mode + " retry=" + (!r.proactive && !string.IsNullOrEmpty(r.playerInput)) + " voice=" + (!string.IsNullOrWhiteSpace(r.replyText) && !LooksLikeError(r.replyText)));
            _rounds.Add(r);
            RefreshRoundBars();
            ScrollDown();   // 收尾后(含「语音/重试/删除」控制条)滚到底,让动作行随消息一起露出,不必下滑
            var sh = TalkEntryHost.Instance;
            if (sh != null) sh.StartCoroutine(ScrollDownNextFrame());   // 再等一帧让布局结算,确保控制条完全露出
        }

        // 群聊语音必须跟随本轮最后一位真正发言的成员，不能使用群聊页签的 npcId=0
        // 或太吾的途中插话。历史回放和新生成轮共用同一绑定规则。
        static void BindGroupVoiceFromLastLine(Round r)
        {
            if (r == null) return;
            r.replyText = null;
            r.replyNpcId = -1;
            r.isAsst = false;
            for (int i = r.groupLines.Count - 1; i >= 0; i--)
            {
                var line = r.groupLines[i];
                if (line == null || line.IsTaiwu || string.IsNullOrWhiteSpace(line.Text)) continue;
                r.replyText = line.Text;
                r.replyNpcId = line.SpeakerId;
                break;
            }
        }

        // 单聊:取会话末尾刚落的这一轮(玩家+回话)的 turn 引用 + 记忆 id
        void CaptureSingleLinkage(Round r)
        {
            // 新生成轮只有在持久化成功后才会获得 ExchangeId。错误提示、调试暗号、
            // 清空竞态等无 id 轮绝不能回退绑定历史尾部，否则点“删除”会误删上一轮真对话。
            if (string.IsNullOrEmpty(r.exchangeId)) return;
            var turns = TalkOrchestrator.History(_taiwuId, _npcId);
            if (turns == null || turns.Count == 0) return;
            foreach (var turn in turns)
            {
                if (turn == null || turn.ExchangeId != r.exchangeId) continue;
                r.turns.Add(turn);
                if (!turn.FromPlayer && turn.MemoryIds != null) r.memoryIds.AddRange(turn.MemoryIds);
            }
        }

        // 控制条:右对齐一行小按钮(删除恒有;重试仅最后一可重试轮)
        void AddRoundBar(Round r)
        {
            if (_content == null) return;
            var row = new GameObject("RoundBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(_content, false);
            var hl = row.GetComponent<HorizontalLayoutGroup>();
            hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            hl.childControlWidth = true; hl.childControlHeight = true;
            hl.spacing = 6f; hl.padding = new RectOffset(12, 14, 0, 2);
            hl.childAlignment = TextAnchor.MiddleRight;

            // 语音:朗读本轮回话(单聊按 NPC 性别音色,助手用灵儿音色)。群聊暂读末位发言。生成中置灰,完成后可再点重生成。
            if (!string.IsNullOrWhiteSpace(r.replyText) && !LooksLikeError(r.replyText))
            {
                NewSmallBtn(row.transform, "语音", out var vb);
                r.voiceBtn = vb;
                vb.onClick.AddListener(delegate { GenRoundVoice(r); });
                if (!r.isAsst && r.replyNpcId >= 0)
                {
                    NewSmallBtn(row.transform, "生图", out var ib);
                    r.imageBtn = ib;
                    ib.onClick.AddListener(delegate { GenRoundImage(r); });
                }
            }
            if (!r.proactive && !string.IsNullOrEmpty(r.playerInput))
            {
                var retry = NewSmallBtn(row.transform, "重试", out var rb);
                rb.onClick.AddListener(delegate { RetryRound(r); });
                r.retryGo = retry;
            }
            NewSmallBtn(row.transform, "删除", out var db);
            db.onClick.AddListener(delegate { DeleteRound(r); });

            r.bar = row;
            r.rows.Add(row);
        }

        // 回话是否是错误/系统占位(整段被括号包裹的提示),那种不提供语音
        bool LooksLikeError(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return true;
            t = t.Trim();
            return (t.StartsWith("(") || t.StartsWith("（")) && (t.EndsWith(")") || t.EndsWith("）")) && t.Length < 60;
        }

        // 配语音:生成中置灰按钮 + 显示「配音中…」临时状态(出声/失败即移除);完成后按钮复位,可再点重生成。
        bool VoiceCallbackCurrent(Round r, long lifecycleVersion, long requestVersion)
            => r != null && lifecycleVersion == _voiceLifecycleVersion
                && requestVersion == r.voiceRequestVersion && _rounds.Contains(r) && Root != null;

        void InvalidateRoundVoice(Round r)
        {
            if (r == null) return;
            bool owned = VoicePlayer.IsOwnedBy(r.voiceOwner);
            unchecked { r.voiceRequestVersion++; }
            r.voiceGen = false;
            if (r.voiceBtn != null) r.voiceBtn.interactable = true;
            SetRoundVoiceButtonLabel(r, "语音");
            VoicePlayer.Stop(r.voiceOwner);
            if (owned && _voiceStatusRow != null)
            {
                Object.Destroy(_voiceStatusRow);
                _voiceStatusRow = null;
            }
        }

        public void InvalidateAllVoice()
        {
            unchecked { _voiceLifecycleVersion++; }
            foreach (Round round in _rounds)
                if (round != null)
                {
                    unchecked { round.voiceRequestVersion++; }
                    round.voiceGen = false;
                    if (round.voiceBtn != null) round.voiceBtn.interactable = true;
                    SetRoundVoiceButtonLabel(round, "语音");
                    VoicePlayer.Stop(round.voiceOwner);
                }
            if (_voiceStatusRow != null)
            {
                Object.Destroy(_voiceStatusRow);
                _voiceStatusRow = null;
            }
        }

        void GenRoundVoice(Round r)
        {
            if (r == null || r.voiceGen || string.IsNullOrWhiteSpace(r.replyText)) return;
            // 未配置语音接口:不静默失败,直接弹设置页让玩家去第一页填「语音接口」(可选项,不借用主 key)
            if (!TtsConfig.IsConfigured)
            {
                AddBubble(BubbleKind.Sys, "（未配置语音接口。请在「设置 - 接口」页填『语音接口 baseUrl / 密钥』后再用语音。）"); ScrollDown();
                ConfigWindow.Open(_font, _taiwuId, _npcId, _npcName);
                return;
            }
            r.voiceGen = true;
            if (r.voiceBtn != null) r.voiceBtn.interactable = false;
            SetRoundVoiceButtonLabel(r, "合成");
            long lifecycleVersion = _voiceLifecycleVersion;
            long requestVersion = ++r.voiceRequestVersion;
            VoicePlayer.Speak(r.voiceOwner, r.replyNpcId, r.isAsst, r.replyText,
                msg =>
                {
                    if (!VoiceCallbackCurrent(r, lifecycleVersion, requestVersion)) return;
                    if (string.IsNullOrEmpty(msg) || msg.StartsWith("配音中", System.StringComparison.Ordinal))
                    {
                        SetRoundVoiceButtonLabel(r, string.IsNullOrEmpty(msg) ? "语音" : "合成");
                        return;
                    }
                    SetRoundVoiceButtonLabel(r, "语音");
                    if (IsOpen) AddSysNotice("（语音：" + msg + "）");
                },
                () =>   // 生成结束(已出声或失败):按钮复位
                {
                    if (!VoiceCallbackCurrent(r, lifecycleVersion, requestVersion)) return;
                    r.voiceGen = false;
                    if (r.voiceBtn != null) r.voiceBtn.interactable = true;
                    SetRoundVoiceButtonLabel(r, "语音");
                });
        }

        static void SetRoundVoiceButtonLabel(Round r, string value)
        {
            if (r?.voiceBtn == null) return;
            TextMeshProUGUI label = r.voiceBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = value ?? "语音";
        }

        bool ImageCallbackCurrent(Round r, long lifecycleVersion, long requestVersion)
            => r != null && lifecycleVersion == _imageLifecycleVersion
                && requestVersion == r.imageRequestVersion && _rounds.Contains(r) && Root != null;

        void InvalidateRoundImage(Round r)
        {
            if (r == null) return;
            bool wasGenerating = r.imageGen;
            ImagePromptWindow.CloseForOwner(r);
            r.imagePromptOpen = false;
            unchecked { r.imageRequestVersion++; }
            r.imageGen = false;
            try { r.imageCancellation?.Cancel(); } catch { }
            try { r.imageCancellation?.Dispose(); } catch { }
            r.imageCancellation = null;
            if (r.imageBtn != null) r.imageBtn.interactable = true;
            if (wasGenerating)
                Debug.Log("[JHYL_IMAGE] cancelled npc=" + r.replyNpcId);
        }

        public void InvalidateAllImages()
        {
            unchecked { _imageLifecycleVersion++; }
            foreach (Round round in _rounds) InvalidateRoundImage(round);
        }

        void GenRoundImage(Round r)
        {
            if (r == null || r.imageGen || r.imagePromptOpen || r.replyNpcId < 0
                || string.IsNullOrWhiteSpace(r.replyText)) return;
            if (!ImageGenerationConfig.TryResolve(out _, out string imageConfigError))
            {
                AddBubble(BubbleKind.Sys, "（生图配置尚未就绪："
                    + (imageConfigError ?? "请检查设置 - 生图") + "。）");
                ScrollDown();
                ConfigWindow.OpenImageSettings(_font, _taiwuId, _npcId, _npcName);
                return;
            }
            r.imagePromptOpen = true;
            if (r.imageBtn != null) r.imageBtn.interactable = false;
            string initialPrompt = BuildRoundImagePrompt(r);
            ImagePromptWindow.Show(r, initialPrompt, _font,
                editedPrompt =>
                {
                    if (!_rounds.Contains(r) || Root == null) return;
                    r.imagePromptOpen = false;
                    BeginRoundImage(r, editedPrompt);
                },
                () =>
                {
                    if (!_rounds.Contains(r) || Root == null) return;
                    r.imagePromptOpen = false;
                    if (r.imageBtn != null) r.imageBtn.interactable = true;
                });
        }

        void BeginRoundImage(Round r, string confirmedPrompt)
        {
            if (r == null || r.imageGen || !_rounds.Contains(r) || Root == null) return;
            if (string.IsNullOrWhiteSpace(confirmedPrompt)
                || confirmedPrompt.Length > ImageGenerationClient.MaxPromptChars)
            {
                AddBubble(BubbleKind.Sys, "（生图失败：提示词为空或超过 12000 字）");
                if (r.imageBtn != null) r.imageBtn.interactable = true;
                ScrollDown();
                return;
            }
            var host = TalkEntryHost.Instance;
            if (host == null)
            {
                AddBubble(BubbleKind.Sys, "（生图失败：宿主尚未就绪）");
                if (r.imageBtn != null) r.imageBtn.interactable = true;
                ScrollDown();
                return;
            }
            r.imageGen = true;
            if (r.imageBtn != null) r.imageBtn.interactable = false;
            r.imageCancellation = new CancellationTokenSource(System.TimeSpan.FromMinutes(9));
            long lifecycleVersion = _imageLifecycleVersion;
            long requestVersion = ++r.imageRequestVersion;
            Debug.Log("[JHYL_IMAGE] start npc=" + r.replyNpcId + " world=" + WorldLifecycle.WorldId
                + " request=" + requestVersion);
            host.StartCoroutine(GenerateRoundImageCo(r, lifecycleVersion, requestVersion,
                confirmedPrompt.Trim()));
        }

        IEnumerator GenerateRoundImageCo(Round r, long lifecycleVersion, long requestVersion,
            string confirmedPrompt)
        {
            GameObject status = AddBubble(BubbleKind.Sys, "（正在截取 NPC 与太吾半身像……）");
            if (status != null) r.rows.Add(status);
            ScrollDown();
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            System.Func<bool> current = () => ImageCallbackCurrent(r, lifecycleVersion, requestVersion)
                && WorldLifecycle.IsSameWorld(generation) && WorldLifecycle.WorldId == worldId
                && !(r.imageCancellation?.IsCancellationRequested ?? true);

            CharacterPairReferenceCapture.Result capture = null;
            var captureWatch = System.Diagnostics.Stopwatch.StartNew();
            yield return CharacterPairReferenceCapture.Capture(r.replyNpcId, _taiwuId, current,
                value => capture = value);
            captureWatch.Stop();
            if (!current()) { AbortRoundImage(r, requestVersion, status, "参考图阶段已取消或超时"); yield break; }
            if (capture?.Png == null)
            {
                FinishRoundImage(r, lifecycleVersion, requestVersion, status,
                    "生图失败：" + (capture?.Error ?? "参考图截取失败"));
                yield break;
            }
            Debug.Log("[JHYL_IMAGE] reference-ready npc=" + r.replyNpcId
                + " bytes=" + capture.Png.Length + " elapsed_ms=" + captureWatch.ElapsedMilliseconds);
            SetImageStatus(status, "人物参考图已完成，正在联系生图服务……");

            if (!ConfiguredSecretSnapshot.TryLoad(out string[] secrets, out string secretError))
            {
                FinishRoundImage(r, lifecycleVersion, requestVersion, status,
                    secretError ?? "无法安全读取本地凭据，已停止生图");
                yield break;
            }
            string prompt = SecretRedactor.Redact(confirmedPrompt, secrets);
            ImageGenerationConfig.Values config = ImageGenerationConfig.Resolve();
            if (config == null)
            {
                FinishRoundImage(r, lifecycleVersion, requestVersion, status, "生图配置已失效，请重新保存");
                yield break;
            }
            var request = new ImageGenerationRequest
            {
                Provider = config.Provider,
                Endpoint = config.Endpoint,
                ApiKey = config.ApiKey,
                Model = config.Model,
                Prompt = prompt,
                ReferencePng = capture.Png,
                Size = config.Size,
                Watermark = config.Watermark,
                WorkflowJson = config.WorkflowJson
            };
            Debug.Log("[JHYL_IMAGE] request provider=" + SafeImageLogValue(config.Provider)
                + " model=" + SafeImageLogValue(config.Model)
                + " endpoint=" + SafeImageEndpoint(config.Endpoint)
                + " prompt_chars=" + prompt.Length + " reference_bytes=" + capture.Png.Length);
            SetImageStatus(status, "生图服务正在绘制，通常需要一至数分钟……");
            var requestWatch = System.Diagnostics.Stopwatch.StartNew();
            Task<ImageGenerationResult> task = ImageGenerationClient.GenerateAsync(request,
                r.imageCancellation.Token);
            while (!task.IsCompleted)
            {
                if (!current()) { AbortRoundImage(r, requestVersion, status, "生图请求已取消或超时"); yield break; }
                yield return null;
            }
            requestWatch.Stop();
            if (!current()) { AbortRoundImage(r, requestVersion, status, "生图请求已取消或超时"); yield break; }
            ImageGenerationResult result;
            try { result = task.Result; }
            catch (System.Exception ex)
            {
                result = new ImageGenerationResult { Ok = false, Error = ex.GetBaseException().GetType().Name };
            }
            if (result == null || !result.Ok || result.Bytes == null)
            {
                FinishRoundImage(r, lifecycleVersion, requestVersion, status,
                    result?.Error ?? "服务未返回图片");
                yield break;
            }
            if (!ImageGenerationClient.TryGetImageDimensions(result.Bytes, out int width, out int height)
                || width < 64 || height < 64 || width > 4096 || height > 4096
                || (long)width * height > 16L * 1024 * 1024)
            {
                FinishRoundImage(r, lifecycleVersion, requestVersion, status,
                    "生图结果尺寸无效或超过 4096×4096 安全上限");
                yield break;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!texture.LoadImage(result.Bytes, false))
            {
                Object.Destroy(texture);
                FinishRoundImage(r, lifecycleVersion, requestVersion, status, "游戏无法解码生图结果");
                yield break;
            }
            GeneratedImageFile saved = SaveGeneratedImage(result.Bytes, result.MediaType, r.replyNpcId);
            if (saved == null)
            {
                Object.Destroy(texture);
                FinishRoundImage(r, lifecycleVersion, requestVersion, status,
                    "图片生成成功，但无法写入当前存档的聊天图片目录");
                yield break;
            }
            if (!current())
            {
                Object.Destroy(texture);
                DeleteGeneratedImageFile(saved.FileName);
                AbortRoundImage(r, requestVersion, status, "生图结果返回时会话已失效");
                yield break;
            }
            if (!PersistRoundImageReference(r, saved.FileName, out string previousFileName))
            {
                Object.Destroy(texture);
                DeleteGeneratedImageFile(saved.FileName);
                FinishRoundImage(r, lifecycleVersion, requestVersion, status,
                    "图片生成成功，但未能写入这轮聊天记录；原记录保持不变");
                yield break;
            }
            if (r.imageRow != null)
            {
                r.rows.Remove(r.imageRow);
                Object.Destroy(r.imageRow);
            }
            r.imageRow = AddGeneratedImageRow(texture, width, height);
            if (r.bar != null && r.imageRow != null)
                r.imageRow.transform.SetSiblingIndex(r.bar.transform.GetSiblingIndex());
            if (r.imageRow != null) r.rows.Add(r.imageRow);
            if (!string.IsNullOrWhiteSpace(previousFileName)
                && !string.Equals(previousFileName, saved.FileName, System.StringComparison.Ordinal))
                DeleteGeneratedImageFile(previousFileName);
            FinishRoundImage(r, lifecycleVersion, requestVersion, status, null, false);
            Debug.Log("[JHYL_IMAGE] complete npc=" + r.replyNpcId + " bytes=" + result.Bytes.Length
                + " size=" + width + "x" + height + " elapsed_ms=" + requestWatch.ElapsedMilliseconds
                + " persisted=true file=" + saved.FileName);
            ScrollDown();
        }

        string BuildRoundImagePrompt(Round target)
        {
            var context = new StringBuilder(4096);
            int targetIndex = _rounds.IndexOf(target);
            int from = System.Math.Max(0, targetIndex - 5);
            for (int i = from; i <= targetIndex && i < _rounds.Count; i++)
            {
                Round round = _rounds[i];
                if (round == null) continue;
                AppendBounded(context, "太吾：" + (round.playerInput ?? ""), 8000);
                AppendBounded(context, "对方：" + (round.replyText ?? ""), 8000);
            }
            ImageGenerationConfig.Values config = ImageGenerationConfig.LoadRaw();
            var prompt = new StringBuilder(9000);
            prompt.Append("参考图左侧是当前与太吾交谈人物的半身像，右侧是太吾的半身像。请准确保留两人的脸、发型、服饰、年龄感与辨识特征，生成一张横向 16:9、二人同处当前对话场景的完整武侠叙事画面。根据最近聊天判断地点、动作、距离、情绪和氛围；不要照搬参考图构图，不要添加字幕、对白、界面、边框、标志或水印，不要凭空增加主要人物。画面自然、细节丰富、人物关系明确。\n\n最近聊天：\n");
            prompt.Append(context);
            if (!string.IsNullOrWhiteSpace(config.Style))
                prompt.Append("\n附加画面风格：").Append(config.Style.Trim());
            if (prompt.Length > ImageGenerationClient.MaxPromptChars)
                return prompt.ToString(0, ImageGenerationClient.MaxPromptChars);
            return prompt.ToString();
        }

        static void AppendBounded(StringBuilder target, string line, int maximum)
        {
            if (target.Length >= maximum || string.IsNullOrWhiteSpace(line)) return;
            int take = System.Math.Min(line.Length, maximum - target.Length);
            target.Append(line, 0, take).Append('\n');
        }

        GameObject AddGeneratedImageRow(Texture2D texture, int width, int height)
        {
            var row = new GameObject("GeneratedImage", typeof(RectTransform), typeof(Image),
                typeof(LayoutElement), typeof(OwnedRuntimeTexture));
            row.transform.SetParent(_content, false);
            Image background = row.GetComponent<Image>();
            background.color = new Color(0.055f, 0.060f, 0.055f, 1f);
            background.raycastTarget = false;
            var imageGo = new GameObject("GeneratedImageContent", typeof(RectTransform), typeof(RawImage));
            imageGo.transform.SetParent(row.transform, false);
            RectTransform imageRt = imageGo.GetComponent<RectTransform>();
            RawImage image = imageGo.GetComponent<RawImage>();
            image.texture = texture;
            image.color = Color.white;
            image.raycastTarget = false;
            const float targetRatio = 16f / 9f;
            float sourceRatio = Mathf.Max(0.1f, width / (float)Mathf.Max(1, height));
            if (sourceRatio > targetRatio)
            {
                float heightFraction = targetRatio / sourceRatio;
                imageRt.anchorMin = new Vector2(0f, (1f - heightFraction) * 0.5f);
                imageRt.anchorMax = new Vector2(1f, (1f + heightFraction) * 0.5f);
            }
            else
            {
                float widthFraction = sourceRatio / targetRatio;
                imageRt.anchorMin = new Vector2((1f - widthFraction) * 0.5f, 0f);
                imageRt.anchorMax = new Vector2((1f + widthFraction) * 0.5f, 1f);
            }
            imageRt.offsetMin = Vector2.zero;
            imageRt.offsetMax = Vector2.zero;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            float availableWidth = _content.rect.width > 240f ? _content.rect.width - 8f : 980f;
            float displayHeight = availableWidth / targetRatio;
            var layout = row.GetComponent<LayoutElement>();
            layout.preferredHeight = displayHeight;
            layout.minHeight = displayHeight;
            layout.flexibleHeight = 0f;
            row.GetComponent<OwnedRuntimeTexture>().Texture = texture;
            return row;
        }

        sealed class GeneratedImageFile
        {
            public string FileName;
        }

        static GeneratedImageFile SaveGeneratedImage(byte[] bytes, string mediaType, int npcId)
        {
            string temporary = null;
            try
            {
                string directory = Path.Combine(JianghuYoulingPaths.Exports, "Images");
                Directory.CreateDirectory(directory);
                string extension = string.Equals(mediaType, "image/jpeg", System.StringComparison.OrdinalIgnoreCase)
                    ? ".jpg" : ".png";
                string fileName = "JHYL_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
                    + "_npc" + npcId + "_" + System.Guid.NewGuid().ToString("N").Substring(0, 8)
                    + extension;
                if (!ChatImageReference.IsValid(fileName) || bytes == null || bytes.Length <= 0
                    || bytes.Length > 32 * 1024 * 1024) return null;
                string path = Path.Combine(directory, fileName);
                temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (new FileInfo(temporary).Length != bytes.Length)
                    throw new IOException("图片临时文件读回长度不一致");
                File.Move(temporary, path);
                if (new FileInfo(path).Length != bytes.Length)
                    throw new IOException("图片文件读回长度不一致");
                return new GeneratedImageFile { FileName = fileName };
            }
            catch
            {
                try { if (!string.IsNullOrWhiteSpace(temporary) && File.Exists(temporary)) File.Delete(temporary); }
                catch { }
                return null;
            }
        }

        bool PersistRoundImageReference(Round round, string fileName,
            out string previousFileName)
        {
            previousFileName = null;
            if (round == null || !ChatImageReference.IsValid(fileName)) return false;
            if (round.mode == RMode.Single)
            {
                TalkTurn target = null;
                for (int i = round.turns.Count - 1; i >= 0; i--)
                    if (round.turns[i] != null && !round.turns[i].FromPlayer)
                    { target = round.turns[i]; break; }
                if (target == null || string.IsNullOrWhiteSpace(target.Id)
                    || !TalkOrchestrator.SetTurnImage(_taiwuId, _npcId, target.Id,
                        fileName, out previousFileName)) return false;
                target.ImageFileName = fileName;
            }
            else if (round.mode == RMode.Group && _group != null)
            {
                GroupChatOrchestrator.Line target = null;
                for (int i = round.groupLines.Count - 1; i >= 0; i--)
                    if (round.groupLines[i] != null && !round.groupLines[i].IsTaiwu)
                    { target = round.groupLines[i]; break; }
                if (target == null || string.IsNullOrWhiteSpace(target.Id)
                    || !_group.SetLineImage(target.Id, fileName, out previousFileName)) return false;
                target.ImageFileName = fileName;
            }
            else return false;
            round.imageFileName = fileName;
            return true;
        }

        void AddPersistedRoundImage(Round round, string fileName)
        {
            if (round == null || !ChatImageReference.IsValid(fileName)) return;
            round.imageFileName = fileName;
            try
            {
                string path = GeneratedImagePath(fileName);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > 32L * 1024 * 1024) return;
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.LongLength != info.Length
                    || !ImageGenerationClient.TryGetImageDimensions(bytes, out int width, out int height)
                    || width < 64 || height < 64 || width > 4096 || height > 4096
                    || (long)width * height > 16L * 1024 * 1024) return;
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!texture.LoadImage(bytes, false)) { Object.Destroy(texture); return; }
                if (round.imageRow != null) Object.Destroy(round.imageRow);
                round.imageRow = AddGeneratedImageRow(texture, width, height);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[JHYL_IMAGE_HISTORY] load failed file=" + fileName
                    + " reason=" + ex.GetType().Name);
            }
        }

        static string GeneratedImagePath(string fileName)
        {
            if (!ChatImageReference.IsValid(fileName)) return null;
            try
            {
                string directory = Path.GetFullPath(Path.Combine(JianghuYoulingPaths.Exports, "Images"));
                string path = Path.GetFullPath(Path.Combine(directory, fileName));
                string prefix = directory.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return path.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase) ? path : null;
            }
            catch { return null; }
        }

        static void DeleteGeneratedImageFile(string fileName)
        {
            string path = GeneratedImagePath(fileName);
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[JHYL_IMAGE_HISTORY] cleanup failed file=" + fileName
                    + " reason=" + ex.GetType().Name);
            }
        }

        void FinishRoundImage(Round r, long lifecycleVersion, long requestVersion,
            GameObject status, string message, bool error = true)
        {
            if (!ImageCallbackCurrent(r, lifecycleVersion, requestVersion)) return;
            if (status != null)
            {
                r.rows.Remove(status);
                Object.Destroy(status);
            }
            r.imageGen = false;
            try { r.imageCancellation?.Dispose(); } catch { }
            r.imageCancellation = null;
            if (r.imageBtn != null) r.imageBtn.interactable = true;
            if (error)
                Debug.LogWarning("[JHYL_IMAGE] failed npc=" + r.replyNpcId
                    + " reason=" + SafeImageLogValue(message));
            if (!string.IsNullOrWhiteSpace(message))
            {
                GameObject notice = AddBubble(BubbleKind.Sys, "（" + message + "）");
                if (notice != null) r.rows.Add(notice);
            }
            ScrollDown();
        }

        void AbortRoundImage(Round r, long requestVersion, GameObject status, string reason)
        {
            if (r == null || requestVersion != r.imageRequestVersion) return;
            if (status != null)
            {
                r.rows.Remove(status);
                Object.Destroy(status);
            }
            r.imageGen = false;
            try { r.imageCancellation?.Cancel(); } catch { }
            try { r.imageCancellation?.Dispose(); } catch { }
            r.imageCancellation = null;
            if (r.imageBtn != null) r.imageBtn.interactable = true;
            Debug.LogWarning("[JHYL_IMAGE] aborted npc=" + r.replyNpcId
                + " reason=" + SafeImageLogValue(reason));
            if (Root != null && _rounds.Contains(r))
            {
                GameObject notice = AddBubble(BubbleKind.Sys, "（生图未完成：" + reason + "）");
                if (notice != null) r.rows.Add(notice);
                ScrollDown();
            }
        }

        static void SetImageStatus(GameObject status, string text)
        {
            if (status == null) return;
            TextMeshProUGUI label = status.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = "（" + (text ?? "生图中……") + "）";
        }

        static string SafeImageEndpoint(string value)
        {
            try
            {
                var uri = new System.Uri((value ?? "").Trim(), System.UriKind.Absolute);
                return uri.Scheme + "://" + uri.Host + uri.AbsolutePath;
            }
            catch { return "invalid"; }
        }

        static string SafeImageLogValue(string value)
        {
            string safe = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length > 180 ? safe.Substring(0, 180) + "…" : safe;
        }

        GameObject NewSmallBtn(Transform parent, string label, out Button btn)
        {
            var go = NewButton("RB", parent, label, 14, out btn);
            go.GetComponent<Image>().color = new Color(0.28f, 0.29f, 0.31f, 0.5f);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 60f; le.preferredHeight = 24f; le.flexibleWidth = 0f;
            var lt = go.GetComponentInChildren<TextMeshProUGUI>();
            if (lt != null) lt.color = new Color(0.82f, 0.84f, 0.86f, 0.92f);
            return go;
        }

        // 只让最后一个「可重试」轮显示重试键(中间轮重试会令其后历史错位,故只许重试末轮)
        void RefreshRoundBars()
        {
            // 每一轮(含历史)都显示删除 + 重试(凡有玩家输入、非主动捎话的轮);删除恒有
            foreach (var r in _rounds)
                if (r != null && r.retryGo != null)
                    r.retryGo.SetActive(!r.proactive && !string.IsNullOrEmpty(r.playerInput));
        }

        // 删除一轮:抹掉记忆 + 会话 turn(单聊)/ 群聊行 / 助手历史,并销毁本轮所有行。只删记忆,绝不回退任何已落地的游戏行为。
        bool DeleteRound(Round r)
        {
            if (_busy || r == null) return false;
            bool persisted = false;
            try
            {
                if (r.mode == RMode.Single)
                    persisted = TalkOrchestrator.DeleteExchange(_taiwuId, _npcId, r.turns, r.memoryIds);
                else if (r.mode == RMode.Group && _group != null)
                    persisted = _group.DeleteLines(r.groupLines);
                else if (r.mode == RMode.Assistant)
                {
                    // 成功回话按对象引用精确删，重复正文/非末轮均不误伤；错误占位轮从未入盘，只需删 UI。
                    persisted = !r.assistantHistoryCommitted
                        || (r.assistantMessages.Count > 0 && AssistantOrchestrator.DeleteExchange(r.assistantMessages));
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 按轮删除失败 mode=" + r.mode + ":" + ex.GetType().Name);
            }
            if (!persisted)
            {
                AppendSys("（删除失败：记录未从磁盘完整移除，本轮仍保留，请重试）");
                return false;
            }

            InvalidateCombatOffers();
            InvalidateRoundVoice(r);
            InvalidateRoundImage(r);
            DeleteGeneratedImageFile(r.imageFileName);
            // JHYL_RETRY_ROW_DETACH_BEFORE_DESTROY:重试(RetryRound)会在删除本轮后【同一帧】紧接着
            // Resend→BeginRound,用 _content.childCount 快照新一轮的起始行索引。Unity 的 Object.Destroy
            // 延迟到帧末执行,期间这些待销毁的旧行仍计入 childCount,会把新一轮的行捕获区间整体顶偏——
            // 收尾 FinalizeRound 时抓不到新行(甚至因 r.rows 为空提前 return),重试出来的那条消息就挂不上
            // 语音/重试/删除控制条。先 SetParent(null) 同步摘除,使 childCount 立即准确,再销毁;普通删除
            // 同样受益。这只动 UI 行,绝不回退任何已落地的游戏行为。
            foreach (var go in r.rows)
                if (go != null)
                {
                    if (go.transform != null) go.transform.SetParent(null, false);
                    Object.Destroy(go);
                }
            _rounds.Remove(r);
            if (r.mode == RMode.Assistant) MarkAssistantHistoryRendered();
            RefreshRoundBars();
            ScrollDown();
            return true;
        }

        // 重试一轮(仅末轮):删掉本轮(含记忆)后,用同一句玩家输入重新生成。
        void RetryRound(Round r)
        {
            if (_busy || r == null || string.IsNullOrEmpty(r.playerInput)) return;
            if (_groupMode && _groupHistoryOnly)
            {
                AppendSys("（历史回看模式不能重试回话；请稍后从左侧会话重新进入再续聊。）");
                return;
            }
            string input = r.playerInput;
            string prevReply = (r.mode == RMode.Single && !LooksLikeError(r.replyText)) ? r.replyText : null;   // #5 把上一回话带给下一轮,显式要求换说法
            if (!DeleteRound(r)) return; // 删除未持久化时绝不能重发，否则旧轮仍在磁盘、却又生成一轮重复对话。
            Debug.Log("[JHYL_CHAT_RETRY_ROUND] mode=" + r.mode + " input=" + input.Length + " prevReply=" + (string.IsNullOrEmpty(prevReply) ? 0 : prevReply.Length));
            Resend(input, prevReply);
        }

        // 切换递话键形态:忙=「中断」(始终可点),空闲=「递话」
        void SetSendMode(bool busy)
        {
            RefreshSendLabel();
            if (_sendBtn != null)
                _sendBtn.interactable = !_combatTransitionBusy && !_proxyPrepareBusy;
            RefreshTaiwuActionBar();
        }

        void RefreshTaiwuActionBar()
        {
            bool visible = true;
            bool directVisible = !_assistantMode && !_soulMode;
            bool assistantVisible = _assistantMode;
            if (_taiwuActionBarGo != null) _taiwuActionBarGo.SetActive(visible);
            foreach (GameObject control in _taiwuDirectActionControls)
                if (control != null) control.SetActive(directVisible);
            foreach (GameObject control in _assistantActionControls)
                if (control != null) control.SetActive(assistantVisible);
            if (_inputTextViewport != null)
                _inputTextViewport.offsetMax = new Vector2(_inputTextViewport.offsetMax.x,
                    visible ? -60f : -12f);
            if (_inputGrow != null)
            {
                _inputGrow.minH = visible ? 184f : 140f;
                _inputGrow.maxH = visible ? 280f : 236f;
                _inputGrow.vertPad = visible ? 112f : 68f;
                _inputGrow.Recompute();
            }
            bool usable = directVisible && !_busy && !_combatTransitionBusy && !_groupHistoryOnly
                && (_groupMode ? _group != null
                    : (!_remote && _npcId >= 0 && !_requiresProxyBeforeMessage));
            foreach (Button button in _taiwuActionButtons)
                if (button != null) button.interactable = usable;
            bool assistantUsable = assistantVisible && !_busy && !_combatTransitionBusy;
            foreach (Button button in _assistantActionButtons)
                if (button != null) button.interactable = assistantUsable && !_assistantDiagnosticExportBusy;
            bool personaVisible = visible && !_assistantMode && !_groupMode && _npcId >= 0;
            bool detailVisible = visible && !_assistantMode && !_groupMode && _npcId >= 0 && !_soulMode;
            if (_detailButtonGo != null) _detailButtonGo.SetActive(detailVisible);
            if (_detailButton != null)
                _detailButton.interactable = detailVisible && !_combatTransitionBusy;
            if (_personaButtonGo != null) _personaButtonGo.SetActive(personaVisible);
            if (_personaButton != null)
                _personaButton.interactable = personaVisible && !_busy && !_combatTransitionBusy;
            if (_taiwuActionHint != null)
            {
                if (_assistantMode)
                    _taiwuActionHint.text = "本地导出 · 固定介绍 · 零 Token";
                else if (_groupHistoryOnly)
                    _taiwuActionHint.text = "历史回看模式不可执行行动";
                else if (_soulMode)
                    _taiwuActionHint.text = "灵魂状态 · 仅可交谈 · 所有工具已关闭";
                else if (_requiresProxyBeforeMessage)
                    _taiwuActionHint.text = "发送首句或开启互动记录后生成普通人物副本";
                else if (!_groupMode && _remote)
                    _taiwuActionHint.text = "千里传音中 · 需当面才能执行";
                else if (_groupMode)
                    _taiwuActionHint.text = "先选群成员 · 再批量选择 · 代码直接执行";
                else
                    _taiwuActionHint.text = "可多选 · 代码直接执行 · 结果交给对方回应";
            }
        }

        // 递话键文字:空闲=递话;忙=中断;但【群聊】忙时若输入框已有字 → 显「插话」(按下即把这句插进当前对话流,不打断)
        void RefreshSendLabel()
        {
            if (_sendLabel == null) return;
            if (_combatTransitionBusy) { _sendLabel.text = "入场中"; return; }
            if (_proxyPrepareBusy) { _sendLabel.text = "复制中"; return; }
            if (!_busy) { _sendLabel.text = "递话"; return; }
            bool canInterject = _groupMode && _group != null && _input != null && !string.IsNullOrWhiteSpace(_input.text);
            _sendLabel.text = canInterject ? "插话" : "中断";
        }


        // 发送后让输入框重新获得焦点:TMP 在回车提交那帧会自动失焦,延一帧再请求激活。
        // 真正聚焦在 TMP 的 LateUpdate 才发生，首行复位由 SafeChatInputField 在激活后完成。
        IEnumerator RefocusInput()
        {
            yield return null;
            if (_input != null && IsOpen)
            {
                bool discardSubmitResidue = _discardSubmitLineBreakOnRefocus;
                _discardSubmitLineBreakOnRefocus = false;
                if (discardSubmitResidue && _input.DiscardSubmitLineBreakResidue())
                    Debug.Log("[JHYL_CHAT_INPUT] removed post-submit line-break residue");
                _input.ActivateAtTextEnd();
            }
        }

        void QueueStandaloneSubmitRefocus()
        {
            _discardSubmitLineBreakOnRefocus = true;
            var host = TalkEntryHost.Instance;
            if (host != null) host.StartCoroutine(RefocusInput());
        }

        void ClearComposer(bool activate)
        {
            if (_input == null) return;
            _input.text = string.Empty;
            _input.ResetEmptyVisualState();
            if (activate)
            {
                _input.ActivateAtTextEnd();
            }
        }

        /// <summary>外部(过月动向弹窗关闭等)抢走焦点后,把输入框焦点还给聊天窗。修弹窗关闭后打字没反应。
        /// 弹窗/原生过月 UI 出现会令 TMP 输入框失焦且不自动恢复;窗仍开且不忙时,延一帧重新激活。</summary>
        public void RestoreInputFocus()
        {
            if (!IsOpen || _busy || _input == null) return;
            try
            {
                _input.interactable = true; _input.readOnly = false;   // 兜底:绝不让它卡在不可输入态
                var host = TalkEntryHost.Instance;
                if (host != null) host.StartCoroutine(RefocusInput());
                else _input.ActivateAtTextEnd();
            }
            catch { }
        }

        // 开窗载入历史:更早交谈梗概(置顶)+ 逐字历史(按月份插分割线);若上次是本月之前,末尾标"如今"
        void RenderHistory()
        {
            string summary = TalkOrchestrator.SummaryOf(_taiwuId, _npcId);
            var turns = TalkOrchestrator.History(_taiwuId, _npcId);
            bool any = !string.IsNullOrEmpty(summary) || (turns != null && turns.Count > 0);
            if (!any) return;   // 无历史就空着,输入框占位提示已示意可打字

            if (!string.IsNullOrEmpty(summary))
                AddBubble(BubbleKind.Sys, "此前交谈梗概:" + summary);

            // 把逐字历史按【轮】分组(玩家+回话=一轮;无主之回话=一条主动捎话轮),每轮挂删除/重试控制条
            Round cur = null;
            int start = 0;
            void Close()
            {
                if (cur == null) return;
                for (int k = start; k < _content.childCount; k++) cur.rows.Add(_content.GetChild(k).gameObject);
                AddRoundBar(cur); _rounds.Add(cur); cur = null;
            }

            int lastMonth = -1, lastTurnMonth = -1;
            if (turns != null)
                foreach (var t in turns)
                {
                    // 群聊摘要或压缩失败时的完整原文只作为该 NPC 的可压缩上下文保存；玩家仍在群聊页看原始记录，
                    // 单聊窗口不重复展示，也不把多人发言伪装成这个 NPC 的私聊气泡。
                    if (TalkTurnKinds.IsGroupChat(t)) continue;
                    bool hasText = t != null && !string.IsNullOrWhiteSpace(t.Text);
                    bool hasExecution = t != null && ((t.ToolResults != null && t.ToolResults.Exists(x => !string.IsNullOrWhiteSpace(x)))
                        || (t.Actions != null && t.Actions.Exists(x => !string.IsNullOrWhiteSpace(x))));
                    if (t == null || (!hasText && !hasExecution)) continue;
                    if (t.Date > 0 && t.Date != lastMonth) { Close(); AddDivider(FormatDate(t.Date)); lastMonth = t.Date; }
                    if (TalkTurnKinds.IsNative(t))
                    {
                        if (cur == null || cur.exchangeId != t.ExchangeId)
                        {
                            Close();
                            start = _content.childCount;
                            cur = new Round
                            {
                                mode = RMode.Single,
                                proactive = true,
                                exchangeId = t.ExchangeId,
                            };
                        }
                        cur.turns.Add(t);
                        if (hasText)
                        {
                            if (t.Kind == TalkTurnKinds.NativePlayerChoice)
                            {
                                GameObject bubble = AddBubble(BubbleKind.Player, "游戏内选择：" + t.Text);
                                AddMessageMetadataAfter(bubble, BubbleKind.Player, t.Date, t.LocationText);
                            }
                            else
                            {
                                GameObject bubble = AddBubble(BubbleKind.Npc, "【游戏互动】\n" + t.Text);
                                AddMessageMetadataAfter(bubble, BubbleKind.Npc, t.Date, t.LocationText);
                            }
                        }
                        if (t.Date > 0) lastTurnMonth = t.Date;
                        continue;
                    }
                    if (t.FromPlayer)
                    {
                        Close();
                        start = _content.childCount;
                        cur = new Round { mode = RMode.Single, playerInput = t.Text ?? "" };
                        cur.turns.Add(t);
                        if (hasText)
                        {
                            GameObject bubble = AddBubble(BubbleKind.Player, t.Text);
                            AddMessageMetadataAfter(bubble, BubbleKind.Player, t.Date, t.LocationText);
                        }
                    }
                    else
                    {
                        if (cur == null) { start = _content.childCount; cur = new Round { mode = RMode.Single, proactive = true }; }
                        cur.turns.Add(t);
                        if (t.MemoryIds != null) cur.memoryIds.AddRange(t.MemoryIds);
                        cur.replyText = t.Text ?? ""; cur.replyNpcId = _npcId; cur.isAsst = false;   // 历史轮也带回话→「语音」键可用
                        if (hasText)
                        {
                            GameObject bubble = AddBubble(BubbleKind.Npc, t.Text);
                            AddMessageMetadataAfter(bubble, BubbleKind.Npc, t.Date, t.LocationText);
                        }
                        else AddBubble(BubbleKind.Sys, "「" + _npcName + "的行动结果」");
                        AddPersistedExecutionRows(t.Actions, t.ToolResults);
                        if (!string.IsNullOrWhiteSpace(t.ImageFileName))
                            AddPersistedRoundImage(cur, t.ImageFileName);
                        Close();   // 回话落定 → 本轮结束
                    }
                    if (t.Date > 0) lastTurnMonth = t.Date;
                }
            Close();

            int cur2 = CurrentDate();
            if (lastTurnMonth > 0 && cur2 > 0 && lastTurnMonth < cur2) AddDivider("如今 " + FormatDate(cur2));
            ScrollDown();
            RefreshRoundBars();
        }

        // 分割线(复用居中系统气泡样式,加破折号)
        void AddDivider(string text) { AddBubble(BubbleKind.Sys, "──────  " + text + "  ──────"); }

        string FormatDate(int linearMonth)
        {
            int m = linearMonth < 0 ? 0 : linearMonth;
            return "第" + (m / 12 + 1) + "年" + (m % 12 + 1) + "月";
        }

        int CurrentDate()
        {
            try { return SingletonObject.getInstance<BasicGameData>().CurrDate; } catch { return -1; }
        }

        // ===== NPC 回话行(与回答同样式:背景上的文字,无框)+ 思考点 + 流式 =====

        /// <summary>起一条 NPC 回话行,先显示动态思考点;增量到来后填入正文,收尾定稿。</summary>
        void StartReplyBubble()
        {
            _streamRawText = string.Empty;
            _streamBubble = AddBubble(BubbleKind.Npc, "·");
            _streamText = _streamBubble != null ? _streamBubble.GetComponentInChildren<TextMeshProUGUI>() : null;
            _dotsActive = true; _progressLabel = "思忖";
            var host = TalkEntryHost.Instance;
            if (host != null && _streamText != null) host.StartCoroutine(DotsCo());
            ScrollDown();
        }

        // 思考中:在回话行里循环 · / ·· / ···,直到首个增量或收尾
        IEnumerator DotsCo()
        {
            int n = 1;
            while (_dotsActive && _streamText != null)
            {
                _streamText.text = (_progressLabel ?? "思忖") + new string('·', n);
                n = n % 3 + 1;
                yield return new WaitForSecondsRealtime(0.35f);
            }
        }

        /// <summary>兼容入口:外部若调用也走同一回话行。</summary>
        public void BeginStreamingReply(string npcName) { if (_streamText == null) StartReplyBubble(); }

        /// <summary>把增量追加到回话行(首个增量停掉思考点、清空占位)。</summary>
        public void AppendStreamingDelta(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = GlyphSanitizer.Clean(text);   // 流式逐字也滤掉不可渲染字符(免瞬时显示口)
            if (string.IsNullOrEmpty(text)) return;
            if (_streamText == null) StartReplyBubble();
            if (_dotsActive) { _dotsActive = false; _streamRawText = string.Empty; if (_streamText != null) _streamText.text = ""; TrimThinkTail(); }   // 正文开始 → 收掉思考行尾部空行
            if (_streamText != null)
            {
                // 正文开头不留空行:正文尚空白时,新增量先 TrimStart(reasoning 模型常在正文前吐换行)
                if (string.IsNullOrWhiteSpace(_streamRawText))
                    _streamRawText = ((_streamRawText ?? string.Empty) + text).TrimStart('\n', '\r', ' ', '\t', '　');
                else _streamRawText += text;
                _streamText.text = FormatNpcVisibleText(_streamRawText);
                ScrollDown();
            }
        }

        // 思考行结束时去掉尾部空行,免思考与正文之间空出几行
        void TrimThinkTail()
        {
            FlushThinkingBuffer(false, true);
        }

        /// <summary>把思考增量追加到"思考过程"行(灰字,置于回话行之上;流式专用,可在设置页关闭)。</summary>
        public void AppendThinkingDelta(string text)
        {
            if (!ChatWindow.ShowThinking) return;   // 可在设置中关闭:隐去思考过程(流式逐字回话+工具调用照常)
            if (string.IsNullOrEmpty(text) || _content == null) return;
            bool first = false;
            if (_thinkText == null)
            {
                ResetThinkingBuffer();
                var t = NewText("ThinkRow", _content, 15, TextAlignmentOptions.TopLeft);
                t.enableWordWrapping = true; t.raycastTarget = false; t.extraPadding = true;
                t.margin = new Vector4(18f, 1f, 14f, 3f);
                t.color = ColThinkText; t.fontStyle = FontStyles.Normal;   // 正立不斜
                t.text = "【思量】";
                _thinkText = t; _thinkBubble = t.gameObject;
                // 放到当前回话行之上(回话行已先建);取不到则留在末尾
                if (_streamBubble != null) _thinkBubble.transform.SetSiblingIndex(_streamBubble.transform.GetSiblingIndex());
                first = true;
            }
            // 标记后紧跟思考正文,首块去掉前导换行,免「【思量】」与内容间空行
            text = first ? text.TrimStart('\n', '\r', ' ', '\t', '　') : text;
            if (text.Length == 0) return;
            _thinkingPendingBuffer.Append(text);
            if (_thinkingFlushCoroutine == null)
            {
                var host = TalkEntryHost.Instance;
                if (host != null)
                    _thinkingFlushCoroutine = host.StartCoroutine(FlushThinkingBufferPeriodically());
                else FlushThinkingBuffer(true, false);
            }
        }

        IEnumerator FlushThinkingBufferPeriodically()
        {
            var wait = new WaitForSecondsRealtime(ThinkingFlushIntervalSeconds);
            while (_thinkText != null)
            {
                yield return wait;
                if (_thinkText == null || _thinkingPendingBuffer.Length == 0) break;
                FlushThinkingBuffer(true, false);
            }
            _thinkingFlushCoroutine = null;
        }

        void FlushThinkingBuffer(bool scroll, bool trimTail)
        {
            if (_thinkText == null) return;
            if (_thinkingPendingBuffer.Length > 0)
            {
                _thinkingVisibleBuffer.Append(_thinkingPendingBuffer);
                _thinkingPendingBuffer.Length = 0;
            }
            if (trimTail) _thinkingVisibleBuffer.TrimEndWhitespace();
            _thinkText.text = _thinkingVisibleBuffer.WasTruncated
                ? "【思量】（前文过长，已折叠）\n" + _thinkingVisibleBuffer.Snapshot()
                : "【思量】" + _thinkingVisibleBuffer.Snapshot();
            if (scroll) ScrollDown();
        }

        void StopThinkingFlush()
        {
            if (_thinkingFlushCoroutine == null) return;
            try
            {
                var host = TalkEntryHost.Instance;
                if (host != null) host.StopCoroutine(_thinkingFlushCoroutine);
            }
            catch { }
            _thinkingFlushCoroutine = null;
        }

        void ResetThinkingBuffer()
        {
            StopThinkingFlush();
            _thinkingPendingBuffer.Length = 0;
            _thinkingVisibleBuffer.Clear();
        }

        /// <summary>本轮非终稿(还要再问一轮):清掉已逐字显示的临时正文,回到思考点等待真正的终稿。</summary>
        public void ResetStreamingReply()
        {
            if (_streamText == null) { StartReplyBubble(); return; }
            _streamRawText = string.Empty;
            _streamText.text = "·";
            if (!_dotsActive)
            {
                _dotsActive = true;
                var host = TalkEntryHost.Instance;
                if (host != null) host.StartCoroutine(DotsCo());
            }
            ScrollDown();
        }

        /// <summary>收尾:用干净回话整段定稿。</summary>
        public void EndStreamingReply(string finalReply = null, int date = 0, string locationText = null)
        {
            _dotsActive = false;
            StopThinkingFlush();
            FlushThinkingBuffer(false, true);
            if (!string.IsNullOrEmpty(finalReply)) finalReply = GlyphSanitizer.Clean(finalReply);   // 终稿滤掉不可渲染字符
            if (_streamText != null && !string.IsNullOrEmpty(finalReply))
            {
                // 流式已逐字显示过、内容一致(仅首尾空白差异)就不再重设,免收尾时文字"跳一下"/高度抖动
                string cur = _streamRawText ?? "";
                if (cur.Trim() != finalReply) _streamRawText = finalReply;
                _streamText.text = FormatNpcVisibleText(_streamRawText);
            }
            AddMessageMetadataAfter(_streamBubble, BubbleKind.Npc, date, locationText);
            _streamBubble = null;
            _streamText = null;
            _streamRawText = null;
            _thinkText = null; _thinkBubble = null;   // 思考行留存于历史视图,仅断引用,下一轮另起
            _thinkingPendingBuffer.Length = 0;
            _thinkingVisibleBuffer.Clear();
            ScrollDown();
        }

        // ===== 消息气泡(玩家 / NPC / 系统三色)=====

        enum BubbleKind { Player, Npc, Sys }

        GameObject AppendPlayer(string t)
        {
            GameObject bubble = AddBubble(BubbleKind.Player, t);
            ScrollDown();
            return bubble;
        }

        // 系统提示(居中淡字):用于错误/状态提示
        void AppendSys(string t)
        {
            AddBubble(BubbleKind.Sys, t);
            ScrollDown();
        }

        /// <summary>外部(对话协程)用:在聊天窗里加一条系统提示(居中淡字),如"流式异常已自动降级"。窗未开则忽略。</summary>
        public void AddSysNotice(string text)
        {
            if (_content == null || string.IsNullOrEmpty(text)) return;
            AppendSys(text);
        }

        /// <summary>在当前打开的聊天窗里显示一条 NPC 的话(如过月主动捎来的)。窗未开则忽略。</summary>
        public void ShowNpcLine(string text)
        {
            if (_content == null || string.IsNullOrEmpty(text)) return;
            AddBubble(BubbleKind.Npc, text);
            ScrollDown();
        }

        /// <summary>把后台主动来信连同本轮真实工具回执投影进已经打开的页签。</summary>
        public void ShowNpcProactiveTurn(string text, IList<string> actions, IList<string> toolResults)
        {
            if (_content == null || string.IsNullOrEmpty(text)) return;
            AddBubble(BubbleKind.Npc, text);
            AddPersistedExecutionRows(actions, toolResults);
            ScrollDown();
        }

        void ClearLog()
        {
            ResetThinkingBuffer();
            // TTS owns work outside the chat coroutine. Cancel/stop it before destroying
            // round controls so clearing a tab cannot leave orphan audio or late callbacks.
            InvalidateAllVoice();
            InvalidateAllImages();
            if (_content == null) return;
            InvalidateCombatOffers();
            for (int i = _content.childCount - 1; i >= 0; i--)
                Object.Destroy(_content.GetChild(i).gameObject);
            _rounds.Clear(); _cur = null; _voiceStatusRow = null;
            _streamText = null; _streamRawText = null; _streamBubble = null; _dotsActive = false; _thinkText = null; _thinkBubble = null;
        }

        void ScrollDown()
        {
            if (_content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            if (_scroll == null || _content == null) return;
            RectTransform viewport = _scroll.viewport != null
                ? _scroll.viewport : _scroll.GetComponent<RectTransform>();
            if (viewport == null) return;
            // ScrollRect.normalizedPosition 的 setter 会调用 Canvas.ForceUpdateCanvases。
            // 流式输出每个增量都走这里时，会反复重建游戏里无关的 Spine 画布并刷
            // “Skeleton Mesh has more than 8 submeshes”。内容为顶部锚定，直接设置
            // anchoredPosition 即可只滚动本聊天视口，不触发全局 Canvas 重建。
            _scroll.StopMovement();
            float hiddenHeight = Mathf.Max(0f, _content.rect.height - viewport.rect.height);
            Vector2 position = _content.anchoredPosition;
            position.y = hiddenHeight;
            _content.anchoredPosition = position;
        }

        // 等一帧让布局结算(含新加的控制条高度)再滚到底——避免控制条比消息晚一帧入布局、被留在折叠线下需手动下滑
        System.Collections.IEnumerator ScrollDownNextFrame()
        {
            yield return null;
            ScrollDown();
        }

        // 新增一行消息:玩家=右侧动态宽度气泡(带底色);NPC=左侧背景上的文字(无框、无包裹);系统=居中淡字。
        // 返回承载 TMP 文本的 GameObject(供流式取文本)。NPC/系统不再套 Bubble 包裹,避免嵌套布局把左侧文字裁掉。
        GameObject AddBubble(BubbleKind kind, string text)
        {
            if (_content == null) return null;
            // Keep persisted player text verbatim, but sanitize every visible role so an old
            // unsupported glyph cannot spam TMP warnings whenever a conversation is reopened.
            text = GlyphSanitizer.Clean(text);

            if (kind == BubbleKind.Player)
            {
                // 玩家:整行右对齐 + 动态宽度底色气泡
                var row = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
                row.transform.SetParent(_content, false);
                var hl = row.GetComponent<HorizontalLayoutGroup>();
                hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
                hl.childControlWidth = true; hl.childControlHeight = true;
                hl.padding = new RectOffset(12, 12, 2, 3);
                hl.childAlignment = TextAnchor.MiddleRight;

                var bubble = new GameObject("Bubble", typeof(RectTransform), typeof(Image),
                    typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
                bubble.transform.SetParent(row.transform, false);
                var bl = bubble.GetComponent<HorizontalLayoutGroup>();
                bl.childForceExpandWidth = false; bl.childForceExpandHeight = false;
                bl.childControlWidth = true; bl.childControlHeight = true;
                bl.padding = new RectOffset(14, 14, 8, 8);
                var bfit = bubble.GetComponent<ContentSizeFitter>();
                bfit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                bfit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                bubble.GetComponent<Image>().color = ColPlayerBubble;

                var ptxt = NewText("T", bubble.transform, 20, TextAlignmentOptions.TopLeft);
                ptxt.enableWordWrapping = true; ptxt.raycastTarget = false; ptxt.extraPadding = true; ptxt.text = text ?? "";
                ptxt.color = ColPlayerText;
                float pref = 16f; try { pref = ptxt.GetPreferredValues(ptxt.text).x + 6f; } catch { }
                var ple = ptxt.gameObject.AddComponent<LayoutElement>();
                ple.preferredWidth = Mathf.Clamp(pref, 16f, 440f); ple.flexibleWidth = 0;
                SelectableChatText.Attach(ptxt, ShowCopiedToast);
                return bubble;
            }

            // NPC / 系统:文字直接作为 content 的整行子项(VLG 强制满宽),无包裹、无底色 → 左侧不会被裁
            var t = NewText("Row", _content, kind == BubbleKind.Sys ? 17 : 20,
                kind == BubbleKind.Sys ? TextAlignmentOptions.Top : TextAlignmentOptions.TopLeft);
            t.enableWordWrapping = true; t.raycastTarget = kind == BubbleKind.Npc;   // #17 NPC 回话可点复制;系统提示不参与
            t.extraPadding = true;   // 关键:给网格留边,否则首字左侧轴承被滚动遮罩裁掉(首字"消失")
            t.margin = new Vector4(kind == BubbleKind.Sys ? 14f : 16f, 1f, 12f, 2f);   // 左内边距,文字不贴边/不裁首字
            t.text = kind == BubbleKind.Npc ? FormatNpcVisibleText(text) : (text ?? "");
            t.color = kind == BubbleKind.Npc ? ColNpcText : ColSysText;
            if (kind == BubbleKind.Npc) SelectableChatText.Attach(t, ShowCopiedToast);
            return t.gameObject;
        }

        // NPC 回话由叙述和方括台词共同组成。整段先包进半透明暖金叙述层；只给
        // 「……」/『……』/“……”及其内部文字切成宣纸白。两者保持相同字号和字重，
        // 只用克制的颜色差异凸显真正说出口的对白。
        // 流式期间即使右引号尚未到达，也在当前末尾补闭合标签，
        // 下一段增量到来时会从原文重新格式化，不会把后续旁白永久染色。
        string FormatNpcVisibleText(string text)
        {
            text = text ?? string.Empty;
            if (text.Length == 0) return text;

            var output = new StringBuilder(text.Length + 96);
            var closers = new Stack<char>();
            output.Append("<color=").Append(NpcNarrationColorTag).Append('>');
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                bool asciiQuote = ch == '"';
                bool openingAsciiQuote = asciiQuote
                    && (closers.Count == 0 || closers.Peek() != '"');
                if (ch == '「' || ch == '『' || ch == '“' || openingAsciiQuote)
                {
                    if (closers.Count == 0)
                        output.Append("</color><color=").Append(NpcDialogueColorTag).Append('>');
                    closers.Push(ch == '「' ? '」' : ch == '『' ? '』' : ch == '“' ? '”' : '"');
                    output.Append(ch);
                    continue;
                }

                output.Append(ch);
                if (closers.Count > 0 && ch == closers.Peek())
                {
                    closers.Pop();
                    if (closers.Count == 0)
                        output.Append("</color><color=").Append(NpcNarrationColorTag).Append('>');
                }
            }
            if (closers.Count > 0)
                output.Append("</color><color=").Append(NpcNarrationColorTag).Append('>');
            output.Append("</color>");
            return output.ToString();
        }

        // 新版消息在正文下方低调展示发生年月与说话者所在地。联络方式虽随记录保存，
        // 但按产品要求不显示；旧记录没有 LocationText 时也不猜测补写。
        void AddMessageMetadataAfter(GameObject anchor, BubbleKind kind, int date, string locationText)
        {
            if (_content == null || anchor == null || string.IsNullOrWhiteSpace(locationText)) return;
            string location = locationText.Trim();
            int lineBreak = location.IndexOfAny(new[] { '\r', '\n' });
            if (lineBreak >= 0) location = location.Substring(0, lineBreak).Trim();
            if (location.Length == 0) return;

            Transform row = anchor.transform;
            while (row.parent != null && row.parent != _content) row = row.parent;
            if (row.parent != _content) return;

            var meta = NewText("MessageMeta", _content, 13,
                kind == BubbleKind.Player ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft);
            meta.enableWordWrapping = true;
            meta.raycastTarget = false;
            meta.extraPadding = true;
            meta.margin = kind == BubbleKind.Player
                ? new Vector4(24f, -2f, 18f, 4f)
                : new Vector4(18f, -2f, 14f, 4f);
            meta.color = new Color(0.53f, 0.59f, 0.56f, 0.72f);
            meta.text = FormatDate(date) + " · " + GlyphSanitizer.Clean(location);
            meta.transform.SetSiblingIndex(row.GetSiblingIndex() + 1);
        }

        // #17 给一条消息挂"点击复制":点中即把纯文本(剥富文本标签)写入剪贴板并弹「已复制」。target 须能接收射线(有 Image 或文字 raycastTarget=true)。
        void AttachCopy(GameObject target, TextMeshProUGUI src)
        {
            if (target == null || src == null) return;
            var c = target.AddComponent<BubbleCopyOnClick>();
            c.getText = () => StripTags(src.text);
            c.onCopied = _ => ShowCopiedToast();
        }

        string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s ?? "";
            var sb = new System.Text.StringBuilder(s.Length);
            bool intag = false;
            foreach (char ch in s) { if (ch == '<') intag = true; else if (ch == '>') intag = false; else if (!intag) sb.Append(ch); }
            return sb.ToString();
        }

        GameObject _copyToast;
        // #17 居中弹一个「已复制」小提示,约 0.9s 后自动消失
        void ShowCopiedToast()
        {
            if (_root == null) return;
            if (_copyToast != null) Object.Destroy(_copyToast);
            var go = new GameObject("CopyToast", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_root.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0, 40); rt.sizeDelta = new Vector2(150, 50);
            go.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.12f, 0.92f);
            go.GetComponent<Image>().raycastTarget = false;
            var tx = NewText("L", go.transform, 20, TextAlignmentOptions.Center);
            tx.text = "已复制"; tx.raycastTarget = false; tx.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            var tr = tx.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
            _copyToast = go;
            var host = TalkEntryHost.Instance;
            if (host != null) host.StartCoroutine(FadeCopyToast(go));
        }

        IEnumerator FadeCopyToast(GameObject go)
        {
            yield return new WaitForSecondsRealtime(0.9f);
            if (go != null) Object.Destroy(go);
            if (_copyToast == go) _copyToast = null;
        }

        /// <summary>
        /// 在回话下方追加一条"获得物"提示行:左侧"（获得:XXX）",右侧"查看"按钮(点开太吾对应详情界面)。
        /// 供 TalkOrchestrator 在赠物/传功/赠秘闻/赠银钱成功后调用(需求A)。onView 为空则只显示文字不显示按钮。
        /// </summary>
        public void AddGrantNotice(string what, string viewLabel, System.Action onView)
            => AddNoticeRow("获得", what, viewLabel, onView, new Color(0.78f, 0.86f, 0.66f, 1f));

        public void AddActionNotice(string what, string actionLabel, System.Action onAction)
            => AddNoticeRow("行动", what, actionLabel, onAction, new Color(0.76f, 0.84f, 0.92f, 1f));

        // 重开窗口时回放已经随 NPC 轮次持久化的绿色执行结果。新记录优先使用
        // ToolResults（含失败/拒绝）；旧记录没有该字段时退回 Actions（成功动作）。
        void AddPersistedExecutionRows(IList<string> actions, IList<string> toolResults)
        {
            IList<string> source = toolResults != null && toolResults.Count > 0 ? toolResults : actions;
            if (source == null || source.Count == 0) return;
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string raw in source)
            {
                string value = (raw ?? string.Empty).Trim();
                if (value.Length == 0 || !seen.Add(value)) continue;
                AddNoticeRow("结果", value, null, null, new Color(0.78f, 0.86f, 0.66f, 1f));
            }
        }

        static bool HasPersistedToolResults(IList<string> toolResults)
        {
            if (toolResults == null) return false;
            foreach (string result in toolResults)
                if (!string.IsNullOrWhiteSpace(result)) return true;
            return false;
        }

        void AddNoticeRow(string kind, string what, string viewLabel, System.Action onView, Color color)
        {
            if (_content == null || string.IsNullOrEmpty(what)) return;
            var row = new GameObject("GrantRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(_content, false);
            var hl = row.GetComponent<HorizontalLayoutGroup>();
            hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            hl.childControlWidth = true; hl.childControlHeight = true;
            hl.spacing = 8f; hl.padding = new RectOffset(14, 12, 2, 2);
            hl.childAlignment = TextAnchor.MiddleLeft;

            var t = NewText("T", row.transform, 17, TextAlignmentOptions.Left);
            t.text = GlyphSanitizer.Clean("（" + kind + ":" + what + "）");
            t.color = color;
            t.raycastTarget = false;
            var tle = t.gameObject.AddComponent<LayoutElement>();
            tle.flexibleWidth = 1f;                          // 占满左侧,把按钮顶到右边

            if (onView != null)
            {
                var b = NewButton("View", row.transform, string.IsNullOrEmpty(viewLabel) ? "查看" : viewLabel, 16, out var btn);
                var ble = b.AddComponent<LayoutElement>();
                ble.preferredWidth = 88f; ble.preferredHeight = 32f; ble.flexibleWidth = 0f;
                btn.onClick.AddListener(delegate { try { onView(); } catch { } });
            }
            ScrollDown();
        }

        void Build()
        {
            _root = new GameObject("JHYL_ChatCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30000;
            PopupRegistry.Register(_root, () => ChatWindow.Minimize());   // 右键/ESC 关窗 = 缩到灵儿下方(生成不打断;页签与对话留存,达上限才按 LRU 淘汰;页签 × 才是真删除该会话)
            var sc = _root.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            _panelRt = prt;
            prt.sizeDelta = new Vector2(PanelWidth, PanelHeight);   // 扩大正文与输入区，长对话无需频繁滚动
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = ChatWindow.SharedPanelPosition;
            panel.GetComponent<Image>().color = ColPanel;

            // 标题栏底色条(纯色,衬出标题区)
            var titleBar = new GameObject("TitleBar", typeof(RectTransform), typeof(Image));
            titleBar.transform.SetParent(panel.transform, false);
            Anchor(titleBar.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -52), new Vector2(0, 0));
            titleBar.GetComponent<Image>().color = new Color(0.14f, 0.16f, 0.15f, 0.98f);
            var drag = titleBar.AddComponent<DragMove>();
            drag.target = prt;
            drag.onPositionChanged = ChatWindow.SetSharedPanelPosition;   // 所有页签共用同一窗口坐标

            // 标题左对齐(右侧让位给一排按钮)
            _title = NewText("Title", panel.transform, 24, TextAlignmentOptions.Left);
            // 十枚右侧操作钮最左到 -714；标题再留 4px，避免长姓名压到“加入主动”。
            Anchor(_title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -46), new Vector2(-718, -6));
            _title.color = new Color(0.95f, 0.92f, 0.82f, 1f);

            var close = NewButton("Close", panel.transform, "X", 22, out var closeBtn);
            var crt = close.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1, 1);
            crt.anchoredPosition = new Vector2(-8, -8); crt.sizeDelta = new Vector2(34, 36);
            // 窗口关闭键 = 缩到灵儿下方:隐藏窗口+页签栏,在灵儿下方留「展开聊天」钮,生成【不中断】;
            // 所有页签与对话原样留存(页签上的 × 才删除单个会话)。已不再单设最小化键——关闭即最小化。
            closeBtn.onClick.AddListener(() => ChatWindow.Minimize());

            // 自愿支持入口紧贴关闭键左侧，并与同排操作按钮复用同一套圆角、字号和交互色。
            var support = NewButton("Support", panel.transform, "支持", 17,
                out var supportBtn);
            var supportRt = support.GetComponent<RectTransform>();
            supportRt.anchorMin = supportRt.anchorMax = supportRt.pivot = new Vector2(1, 1);
            supportRt.anchoredPosition = new Vector2(-46, -8);
            supportRt.sizeDelta = new Vector2(56, 36);
            supportBtn.onClick.AddListener(SupportDevelopment);

            // 开源项目入口位于支持入口左侧，并与同排操作按钮保持统一样式。
            var openSource = NewButton("OpenSource", panel.transform, "开源", 17,
                out var openSourceBtn);
            var openSourceRt = openSource.GetComponent<RectTransform>();
            openSourceRt.anchorMin = openSourceRt.anchorMax = openSourceRt.pivot = new Vector2(1, 1);
            openSourceRt.anchoredPosition = new Vector2(-106, -8);
            openSourceRt.sizeDelta = new Vector2(56, 36);
            openSourceBtn.onClick.AddListener(OpenSourceProject);

            // 清空聊天记录(任务7)
            var clear = NewButton("Clear", panel.transform, "清空", 17, out var clearBtn);
            var clearRt = clear.GetComponent<RectTransform>();
            clearRt.anchorMin = clearRt.anchorMax = clearRt.pivot = new Vector2(1, 1);
            clearRt.anchoredPosition = new Vector2(-226, -8); clearRt.sizeDelta = new Vector2(56, 36);
            // 清空不可逆 → 两段式二次确认:先点变红显「确认清空」,4 秒内再点才真清;不点自动复位。
            var clearLbl = clear.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            var clearImg = clear.GetComponent<Image>();
            Color clearCol0 = clearImg != null ? clearImg.color : Color.white;
            float[] clearArmAt = { -99f };
            clearBtn.onClick.AddListener(() =>
            {
                if (Time.unscaledTime - clearArmAt[0] <= 4f)   // 第二次点(4s 内)→ 真清空
                {
                    clearArmAt[0] = -99f;
                    if (clearLbl != null) clearLbl.text = "清空";
                    if (clearImg != null) clearImg.color = clearCol0;
                    ClearHistory();
                }
                else                                            // 第一次点 → 亮红求证
                {
                    clearArmAt[0] = Time.unscaledTime;
                    if (clearLbl != null) clearLbl.text = "确认清空";
                    if (clearImg != null) clearImg.color = new Color(0.62f, 0.20f, 0.18f, 0.98f);
                    var host = TalkEntryHost.Instance;
                    if (host != null) host.StartCoroutine(DisarmClear(clearArmAt, clearLbl, clearImg, clearCol0));
                }
            });

            // 设置按钮:打开统一设置窗(接口/对话/世界书/人设 四页),带上当前太吾/NPC 上下文以便编辑世界书与人设
            var cfg = NewButton("Config", panel.transform, "设置", 17, out var cfgBtn);
            var cfgRt = cfg.GetComponent<RectTransform>();
            cfgRt.anchorMin = cfgRt.anchorMax = cfgRt.pivot = new Vector2(1, 1);
            cfgRt.anchoredPosition = new Vector2(-166, -8); cfgRt.sizeDelta = new Vector2(56, 36);
            cfgBtn.onClick.AddListener(delegate { ConfigWindow.Open(_font, _taiwuId, _npcId, _npcName); });

            // 「加人」按钮:单聊/传音/现有群/助手页都可从统一候选池组成新的独立群会话。
            var grp = NewButton("Group", panel.transform, "加人", 17, out var grpBtn);
            var grpRt = grp.GetComponent<RectTransform>();
            grpRt.anchorMin = grpRt.anchorMax = grpRt.pivot = new Vector2(1, 1);
            grpRt.anchoredPosition = new Vector2(-354, -8); grpRt.sizeDelta = new Vector2(64, 36);
            grpBtn.onClick.AddListener(StartAddMembers);
            _groupBtnGo = grp;

            var monthlyCandidate = NewButton("MonthlyCandidate", panel.transform, "加入主动", 16,
                out var monthlyCandidateBtn);
            var monthlyCandidateRt = monthlyCandidate.GetComponent<RectTransform>();
            monthlyCandidateRt.anchorMin = monthlyCandidateRt.anchorMax = monthlyCandidateRt.pivot = new Vector2(1, 1);
            // 与右侧“往事成书 / 详情 / 人设 / 加人 / 导出 / 清空 / 设置”统一保持 4px 间距。
            monthlyCandidateRt.anchoredPosition = new Vector2(-630, -8);
            monthlyCandidateRt.sizeDelta = new Vector2(84, 36);
            TextMeshProUGUI monthlyCandidateLabel = monthlyCandidate
                .GetComponentInChildren<TextMeshProUGUI>();
            monthlyCandidateBtn.onClick.AddListener(ToggleCurrentNpcMonthlyCandidate);
            _monthlyCandidateBtnGo = monthlyCandidate;
            _monthlyCandidateBtn = monthlyCandidateBtn;
            _monthlyCandidateLabel = monthlyCandidateLabel;

            // 当前单聊另一个写作入口：紧靠“加入/移除主动”右侧，名称与人物详情页一致。
            _novelButtonGo = NewButton("NpcNovel", panel.transform, "往事成书", 16,
                out var novelButton);
            var novelRt = _novelButtonGo.GetComponent<RectTransform>();
            novelRt.anchorMin = novelRt.anchorMax = novelRt.pivot = new Vector2(1, 1);
            novelRt.anchoredPosition = new Vector2(-542, -8);
            novelRt.sizeDelta = new Vector2(84, 36);
            novelButton.onClick.AddListener(() =>
                NpcNovelWindow.Open(_npcId, _taiwuId, _npcName, _font));

            // 详情只属于当前普通单聊对象：紧靠“加入/移除主动”右侧，打开本体原生人物详情。
            _detailButtonGo = NewButton("CharacterDetail", panel.transform, "详情", 16,
                out _detailButton);
            var detailRt = _detailButtonGo.GetComponent<RectTransform>();
            detailRt.anchorMin = detailRt.anchorMax = detailRt.pivot = new Vector2(1, 1);
            detailRt.anchoredPosition = new Vector2(-482, -8);
            detailRt.sizeDelta = new Vector2(56, 36);
            _detailButton.onClick.AddListener(() => CharacterMenuLink.OpenCharacterInfo(_npcId));

            // 人设属于当前普通单聊对象，紧靠“详情”右侧；灵儿和群聊页隐藏。
            _personaButtonGo = NewButton("Persona", panel.transform, "人设", 16,
                out _personaButton);
            var personaRt = _personaButtonGo.GetComponent<RectTransform>();
            personaRt.anchorMin = personaRt.anchorMax = personaRt.pivot = new Vector2(1, 1);
            personaRt.anchoredPosition = new Vector2(-422, -8);
            personaRt.sizeDelta = new Vector2(56, 36);
            _personaButton.onClick.AddListener(() =>
                PersonaWindow.Open(_npcId, _taiwuId, _npcName, _font));

            // 导出当前页签：单聊/群聊/灵儿均输出脱敏 Markdown；路径同时复制到剪贴板。
            var export = NewButton("Export", panel.transform, "导出", 17, out var exportBtn);
            var exportRt = export.GetComponent<RectTransform>();
            exportRt.anchorMin = exportRt.anchorMax = exportRt.pivot = new Vector2(1, 1);
            exportRt.anchoredPosition = new Vector2(-286, -8); exportRt.sizeDelta = new Vector2(64, 36);
            exportBtn.onClick.AddListener(ExportCurrent);
            // Anchor coordinates already place the controls left-to-right. Keep the actual
            // Unity sibling order identical as well so keyboard/navigation/accessibility and
            // any future layout group always see: 加入主动、往事成书、详情、人设、加人、导出、清空、设置。
            int firstActionButtonSibling = System.Math.Min(monthlyCandidate.transform.GetSiblingIndex(),
                System.Math.Min(
                    System.Math.Min(System.Math.Min(System.Math.Min(_detailButtonGo.transform.GetSiblingIndex(),
                        _personaButtonGo.transform.GetSiblingIndex()), grp.transform.GetSiblingIndex()),
                        export.transform.GetSiblingIndex()),
                    System.Math.Min(System.Math.Min(clear.transform.GetSiblingIndex(),
                        cfg.transform.GetSiblingIndex()), _novelButtonGo.transform.GetSiblingIndex())));
            monthlyCandidate.transform.SetSiblingIndex(firstActionButtonSibling);
            _novelButtonGo.transform.SetSiblingIndex(firstActionButtonSibling + 1);
            _detailButtonGo.transform.SetSiblingIndex(firstActionButtonSibling + 2);
            _personaButtonGo.transform.SetSiblingIndex(firstActionButtonSibling + 3);
            grp.transform.SetSiblingIndex(firstActionButtonSibling + 4);
            export.transform.SetSiblingIndex(firstActionButtonSibling + 5);
            clear.transform.SetSiblingIndex(firstActionButtonSibling + 6);
            cfg.transform.SetSiblingIndex(firstActionButtonSibling + 7);

            // 滚动消息区
            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(panel.transform, false);
            Anchor(scrollGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 1), new Vector2(10, 64), new Vector2(-10, -56));
            _chatScrollRect = scrollGo.GetComponent<RectTransform>();
            scrollGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.22f);
            _scroll = scrollGo.GetComponent<ScrollRect>();
            _scroll.horizontal = false; _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 24f;

            // 独立裁剪视口给最右侧永久滚动条留出固定轨道；消息再宽也不会盖住手柄。
            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGo.transform.SetParent(scrollGo.transform, false);
            var viewportRt = viewportGo.GetComponent<RectTransform>();
            Anchor(viewportRt, Vector2.zero, Vector2.one, new Vector2(4, 4), new Vector2(-20, -4));
            _scroll.viewport = viewportRt;

            // 内容容器:纵向布局 + 自适应高度,每条消息一行
            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            _content = contentGo.GetComponent<RectTransform>();
            _content.SetParent(viewportGo.transform, false);
            _content.anchorMin = new Vector2(0, 1); _content.anchorMax = new Vector2(1, 1); _content.pivot = new Vector2(0.5f, 1);
            _content.anchoredPosition = Vector2.zero; _content.sizeDelta = new Vector2(0, 0);
            var vlg = contentGo.GetComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.spacing = 10f;
            vlg.padding = new RectOffset(4, 4, 10, 10);
            var cf = contentGo.GetComponent<ContentSizeFitter>();
            cf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll.content = _content;
            _scroll.verticalScrollbar = UiScroll.AddVerticalForScrollRect(scrollGo);
            _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            // 整块输入卡片：太吾行动在第一行，文字区居中，代笔/口述/递话在最底行。
            // 非灵儿页默认比旧版多一行；长文继续向上扩展，两条操作行位置始终固定。
            // 用游戏的 DisableHotkeyInputField(TMP_InputField 子类):聚焦时屏蔽游戏快捷键,避免打字误触
            var inputGo = new GameObject("Input", typeof(RectTransform), typeof(Image));
            inputGo.transform.SetParent(panel.transform, false);
            Anchor(inputGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(10, 12), new Vector2(-10, 196));
            Image composerFrame = inputGo.GetComponent<Image>();
            ApplyRoundedSkin(composerFrame);
            composerFrame.color = new Color(0.23f, 0.29f, 0.27f, 0.96f);
            var surfaceGo = new GameObject("Surface", typeof(RectTransform), typeof(Image));
            surfaceGo.transform.SetParent(inputGo.transform, false);
            Anchor(surfaceGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(1.5f, 1.5f), new Vector2(-1.5f, -1.5f));
            Image composerSurface = surfaceGo.GetComponent<Image>();
            ApplyRoundedSkin(composerSurface);
            composerSurface.color = new Color(0.072f, 0.080f, 0.075f, 0.99f);
            composerSurface.raycastTarget = false;

            // 三个代码直执行入口与输入区共用一张卡片，不再占用聊天正文顶部，
            // 也不再使用易被误认成按钮的“太吾”方印。
            _taiwuActionBarGo = new GameObject("TaiwuActionBar", typeof(RectTransform));
            _taiwuActionBarGo.transform.SetParent(inputGo.transform, false);
            Anchor(_taiwuActionBarGo.GetComponent<RectTransform>(),
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(12, -50), new Vector2(-12, -8));

            GameObject give = NewButton("DirectGive", _taiwuActionBarGo.transform,
                "赠物", 15, out Button giveButton);
            Anchor(give.GetComponent<RectTransform>(), Vector2.zero, new Vector2(0, 1),
                new Vector2(0, 3), new Vector2(88, -3));
            StyleTaiwuActionButton(give);
            giveButton.onClick.AddListener(() =>
                OpenTaiwuDirectAction(TaiwuDirectActionKind.GiveItem));
            _taiwuActionButtons.Add(giveButton);
            _taiwuDirectActionControls.Add(give);

            GameObject teach = NewButton("DirectTeach", _taiwuActionBarGo.transform,
                "传授", 15, out Button teachButton);
            Anchor(teach.GetComponent<RectTransform>(), Vector2.zero, new Vector2(0, 1),
                new Vector2(96, 3), new Vector2(184, -3));
            StyleTaiwuActionButton(teach);
            teachButton.onClick.AddListener(() =>
                OpenTaiwuDirectAction(TaiwuDirectActionKind.Teach));
            _taiwuActionButtons.Add(teachButton);
            _taiwuDirectActionControls.Add(teach);

            GameObject writeBook = NewButton("DirectWriteBook", _taiwuActionBarGo.transform,
                "写书赠送", 15, out Button writeBookButton);
            Anchor(writeBook.GetComponent<RectTransform>(), Vector2.zero, new Vector2(0, 1),
                new Vector2(192, 3), new Vector2(316, -3));
            StyleTaiwuActionButton(writeBook);
            writeBookButton.onClick.AddListener(() =>
                OpenTaiwuDirectAction(TaiwuDirectActionKind.WriteBook));
            _taiwuActionButtons.Add(writeBookButton);
            _taiwuDirectActionControls.Add(writeBook);

            GameObject exportLogs = NewButton("AssistantExportLogs", _taiwuActionBarGo.transform,
                "导出日志", 15, out Button exportLogsButton);
            Anchor(exportLogs.GetComponent<RectTransform>(), Vector2.zero, new Vector2(0, 1),
                new Vector2(0, 3), new Vector2(104, -3));
            StyleTaiwuActionButton(exportLogs);
            exportLogsButton.onClick.AddListener(ExportAssistantDiagnosticLogs);
            _assistantActionButtons.Add(exportLogsButton);
            _assistantActionControls.Add(exportLogs);

            GameObject introduceMod = NewButton("AssistantIntroduceMod", _taiwuActionBarGo.transform,
                "介绍 Mod", 15, out Button introduceModButton);
            Anchor(introduceMod.GetComponent<RectTransform>(), Vector2.zero, new Vector2(0, 1),
                new Vector2(112, 3), new Vector2(224, -3));
            StyleTaiwuActionButton(introduceMod);
            introduceModButton.onClick.AddListener(IntroduceMod);
            _assistantActionButtons.Add(introduceModButton);
            _assistantActionControls.Add(introduceMod);

            _taiwuActionHint = NewText("Hint", _taiwuActionBarGo.transform, 13,
                TextAlignmentOptions.MidlineRight);
            Anchor(_taiwuActionHint.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(324, 2), new Vector2(-2, -2));
            _taiwuActionHint.text = "可多选 · 执行后由对方回应";
            _taiwuActionHint.color = new Color(0.61f, 0.68f, 0.62f, 0.82f);

            GameObject actionDivider = new GameObject("ActionDivider", typeof(RectTransform),
                typeof(Image));
            actionDivider.transform.SetParent(_taiwuActionBarGo.transform, false);
            Anchor(actionDivider.GetComponent<RectTransform>(), new Vector2(0, 0),
                new Vector2(1, 0), new Vector2(0, -5), new Vector2(0, -4));
            actionDivider.GetComponent<Image>().color =
                new Color(0.48f, 0.43f, 0.31f, 0.22f);
            actionDivider.GetComponent<Image>().raycastTarget = false;

            // 统一输入框工厂:带 RectMask2D 裁剪视口,长文不再溢出框外(兜住 TMP 输入法光标越界 bug 由 SafeChatInputField 负责)
            TextMeshProUGUI _ta;
            _input = UiInput.Setup(inputGo, _font, 19, true, TMP_InputField.LineType.MultiLineSubmit, false, "写下想说的话…", 0, false, out _ta);
            var imeCaretFollower = inputGo.AddComponent<ChatImeCaretFollower>();
            imeCaretFollower.Input = _input;
            // 上方 60px 留给当前页行动：NPC/群聊显示太吾行动，灵儿显示日志与 Mod 介绍。
            if (_input.textViewport != null)
            {
                var viewport = _input.textViewport;
                viewport.offsetMin = new Vector2(14f, 58f);
                viewport.offsetMax = new Vector2(-14f, -60f);
                _inputTextViewport = viewport;
            }
            _ta.extraPadding = false;
            _ta.margin = Vector4.zero;
            _ta.alignment = TextAlignmentOptions.TopLeft;
            var chatPlaceholder = _input.placeholder as TextMeshProUGUI;
            if (chatPlaceholder != null)
            {
                chatPlaceholder.extraPadding = false;
                chatPlaceholder.margin = Vector4.zero;
                chatPlaceholder.alignment = TextAlignmentOptions.TopLeft;
            }
            // TMP 原生 caret 在中文输入/多行布局重建后偶尔不重建；隐藏它，改用受同一视口裁剪的实体常亮 caret。
            _input.customCaretColor = true;
            _input.caretColor = new Color(0.95f, 0.85f, 0.55f, 0f);
            _input.caretWidth = 1;
            _input.caretBlinkRate = 0f;
            _input.selectionColor = new Color(0.45f, 0.55f, 0.75f, 0.45f);
            _input.onFocusSelectAll = false;                               // 回车提交后重新聚焦时保留明确光标，不全选草稿
            _input.resetOnDeActivation = false;                            // TMP onSubmit 返回后的同帧失活不释放/重置选区
            var stableCaret = inputGo.AddComponent<ChatVisibleCaret>();
            stableCaret.Input = _input; stableCaret.Viewport = _input.textViewport; stableCaret.Text = _ta;
            var chrome = inputGo.AddComponent<ChatComposerChrome>();
            chrome.Input = _input; chrome.Frame = composerFrame; chrome.Surface = composerSurface;
            _input.onSubmit.AddListener(delegate
            {
                if (_busy && _groupMode && _group != null) GroupInterject(); else Send();
            });   // 普通 Enter 提交；Ctrl+Enter 已由 SafeChatInputField 的 TMP 原生路径截获
            // 输入框自适应高度:长消息折行后不再把前面的字顶出框外(修输入法多字母上移 bug)
            var grow = inputGo.AddComponent<ChatInputAutoGrow>();
            _inputGrow = grow;
            grow.inputRect = inputGo.GetComponent<RectTransform>();
            grow.scrollRect = _scroll != null ? _scroll.GetComponent<RectTransform>() : null;
            grow.input = _input; grow.text = _ta;
            grow.minH = 184f;
            grow.maxH = 280f;
            grow.vertPad = 112f;
            _input.onValueChanged.AddListener(delegate { grow.Recompute(); if (_busy) RefreshSendLabel(); });   // 群聊忙时,输入框有无字→按键在「插话/中断」间切
            grow.Recompute();

            var dividerGo = new GameObject("ToolbarDivider", typeof(RectTransform), typeof(Image));
            dividerGo.transform.SetParent(inputGo.transform, false);
            Anchor(dividerGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(12, 55), new Vector2(-12, 56));
            dividerGo.GetComponent<Image>().color = new Color(0.48f, 0.43f, 0.31f, 0.26f);
            dividerGo.GetComponent<Image>().raycastTarget = false;

            var shortcutHint = NewText("ShortcutHint", inputGo.transform, 13, TextAlignmentOptions.MidlineLeft);
            Anchor(shortcutHint.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(78, 9), new Vector2(-162, 47));
            shortcutHint.text = "Enter 发送  ·  Ctrl+Enter 换行";
            shortcutHint.color = new Color(0.64f, 0.64f, 0.58f, 0.72f);

            // 「口述」复用崇祯的 Windows 原生 Win+H 路线：无额外 ASR 接口、无音频落盘、零模型 token。
            // 三个按钮都是输入卡片的真实子物体，不再悬在输入框外；FocusKeeper 在 PointerDown
            // 当帧把选中对象还给输入框，避免听写到错误位置。
            var dictation = NewButton("Dictation", inputGo.transform, "口述", 15, out _dictationBtn);
            Anchor(dictation.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-154, 10), new Vector2(-96, 46));
            StyleComposerButton(dictation, false);
            dictation.AddComponent<DictationInputFocusKeeper>().Input = _input;
            _dictationBtn.onClick.AddListener(StartDictation);

            var send = NewButton("Send", inputGo.transform, "递话", 17, out _sendBtn);
            Anchor(send.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-90, 10), new Vector2(-12, 46));
            StyleComposerButton(send, true);
            send.AddComponent<DictationInputFocusKeeper>().Input = _input;
            _sendLabel = send.GetComponentInChildren<TextMeshProUGUI>();
            _sendBtn.onClick.AddListener(OnSendOrInterrupt);

            // 「代笔」:据聊天记录替太吾拟一句话填入输入框(卡片左下角)。重复点会重新生成覆盖;生成中禁用。
            var suggest = NewButton("Suggest", inputGo.transform, "代笔", 16, out _suggestBtn);
            Anchor(suggest.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(12, 10), new Vector2(70, 46));
            StyleComposerButton(suggest, false);
            suggest.AddComponent<DictationInputFocusKeeper>().Input = _input;
            _suggestBtn.onClick.AddListener(SuggestForMe);
            RefreshTaiwuActionBar();
        }

        // 「代笔」:据聊天记录生成太吾要说的话 → 填入输入框(覆盖原内容)。生成期间禁用按钮,防重复并发。
        void SuggestForMe()
        {
            if (_suggesting) return;
            var host = TalkEntryHost.Instance;
            if (host == null) return;
            int taiwu = _taiwuId > 0 ? _taiwuId : ResolveTaiwuId();
            _suggesting = true;
            if (_suggestBtn != null) _suggestBtn.interactable = false;
            if (_input != null && _input.placeholder is TextMeshProUGUI ph) ph.text = "代笔拟话中…";
            int generation = WorldLifecycle.Generation;
            long version = unchecked(++_suggestVersion);
            var cancellation = new CancellationTokenSource();
            _suggestCancellation = cancellation;
            IEnumerator work;
            if (_assistantMode)
                // 灵儿页 _npcId=0:走 SuggestPlayerLine 会被"无效对象"拒掉(玩家所见"代笔失败:无效对象"),
                // 改用灵儿页专用代笔路径,据太吾与灵儿的对话替太吾拟一句要对灵儿说的话。
                work = AssistantOrchestrator.SuggestLine(
                    line => { if (SuggestionCurrent(version, generation, cancellation)) OnSuggestDone(line); },
                    err => { if (SuggestionCurrent(version, generation, cancellation)) OnSuggestErr(err); },
                    cancellation.Token);
            else if (_groupMode && _group != null)
                work = _group.SuggestLine(
                    line => { if (SuggestionCurrent(version, generation, cancellation)) OnSuggestDone(line); },
                    err => { if (SuggestionCurrent(version, generation, cancellation)) OnSuggestErr(err); },
                    cancellation.Token);
            else
                work = TalkOrchestrator.SuggestPlayerLine(taiwu, _npcId, _npcName,
                    line => { if (SuggestionCurrent(version, generation, cancellation)) OnSuggestDone(line); },
                    err => { if (SuggestionCurrent(version, generation, cancellation)) OnSuggestErr(err); },
                    cancellation.Token, generation);
            _suggestCoroutine = host.StartCoroutine(RunSuggestion(work, version, generation, cancellation));
        }

        IEnumerator RunSuggestion(IEnumerator work, long version, int generation, CancellationTokenSource cancellation)
        {
            try
            {
                if (work != null) yield return work;
            }
            finally
            {
                if (SuggestionCurrent(version, generation, cancellation))
                {
                    _suggestCancellation = null;
                    _suggestCoroutine = null;
                    _suggesting = false;
                    if (_suggestBtn != null) _suggestBtn.interactable = !_combatTransitionBusy;
                    RestorePlaceholder();
                }
                try { cancellation.Dispose(); } catch { }
            }
        }

        bool SuggestionCurrent(long version, int generation, CancellationTokenSource cancellation)
            => version == _suggestVersion && ReferenceEquals(_suggestCancellation, cancellation)
               && cancellation != null && !cancellation.IsCancellationRequested
               && WorldLifecycle.IsSameWorld(generation);

        void CancelPendingSuggestion()
        {
            unchecked { _suggestVersion++; }
            var cancellation = _suggestCancellation;
            _suggestCancellation = null;
            _suggestCoroutine = null;
            _suggesting = false;
            try { cancellation?.Cancel(); } catch { }
            if (_suggestBtn != null) _suggestBtn.interactable = !_combatTransitionBusy;
            RestorePlaceholder();
        }

        void OnSuggestDone(string line)
        {
            _suggesting = false;
            if (_suggestBtn != null) _suggestBtn.interactable = !_combatTransitionBusy;
            RestorePlaceholder();
            if (!string.IsNullOrWhiteSpace(line) && _input != null)
            {
                _input.text = line;
                _input.ActivateInputField();
                // 视图滚到开头(光标置首):否则长文会滚到末尾,只露最后几个字、前面全被藏住
                try { _input.caretPosition = 0; _input.stringPosition = 0; _input.ForceLabelUpdate(); } catch { }
            }
        }

        void OnSuggestErr(string err)
        {
            _suggesting = false;
            if (_suggestBtn != null) _suggestBtn.interactable = !_combatTransitionBusy;
            RestorePlaceholder();
            AppendSys("（代笔失败：" + err + "）");
        }

        void RestorePlaceholder()
        {
            if (_input != null && _input.placeholder is TextMeshProUGUI ph) ph.text = "说点什么…(回车发送，Ctrl+Enter换行)";
        }

        // 借游戏中文字体:游戏没把名签字体传进来时(1.0.17 改了布局取到 null),满场找任一已加载 TMP 字体借用,
        // 避免每个文本都走 TMP "无字体" 最坏路径(刷屏警告 + 额外 GPU 压力)。仿 ConfigHost.ResolveFont;首次开窗时本窗尚未建,只会借到游戏字体。
        TMP_FontAsset ResolveFont()
        {
            if (_font != null) { TrySetGlobalDefaultFont(_font); return _font; }
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                foreach (var t in texts)
                    if (t != null && t.font != null && t.gameObject.scene.IsValid()) { _font = t.font; break; }
                if (_font == null)
                    foreach (var t in texts)
                        if (t != null && t.font != null) { _font = t.font; break; }
                if (_font == null)
                {
                    var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (fonts != null && fonts.Length > 0) _font = fonts[0];
                }
            }
            catch { }
            TrySetGlobalDefaultFont(_font);
            GlyphSanitizer.SetFont(_font);
            return _font;
        }

        void EnsureFontReady()
        {
            if (_font == null) ResolveFont();
            TrySetGlobalDefaultFont(_font);
            GlyphSanitizer.SetFont(_font);
        }

        // 把 TMP 的「默认字体」设为游戏中文字体:任何窗口里新建的 TMP 文本即便创建时没赋字体,
        // Awake/LoadFontAsset 也能直接命中默认字体,而不会去找缺失的 LiberationSans 刷警告+堆栈(全局根治,不止聊天窗)。
        bool _tmpDefaultFontSet;
        void TrySetGlobalDefaultFont(TMP_FontAsset f)
        {
            if (_tmpDefaultFontSet || f == null) return;
            try
            {
                var settings = TMP_Settings.instance;
                if (settings == null) return;
                var fld = typeof(TMP_Settings).GetField("m_defaultFontAsset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fld != null) { fld.SetValue(settings, f); _tmpDefaultFontSet = true; }
            }
            catch { }
        }

        TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align)
        {
            // JHYL_CHAT_TEXT_FONT_READY_BEFORE_TMP:先设置 TMP 全局默认字体,再 AddComponent<TextMeshProUGUI>,
            // 否则 Awake/LoadFontAsset 会在我们 t.font=f 之前先刷 LiberationSans 缺失警告,按钮文字也可能空白。
            EnsureFontReady();
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            var f = _font != null ? _font : ResolveFont();
            if (f != null) t.font = f;
            UiFontSizeStore.Bind(t, size); t.alignment = align; t.richText = true;
            t.raycastTarget = false;   // 文字一律不挡射线:否则标题文字盖在标题栏上会吃掉拖动(对话框拖不动的根因)
            t.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            return t;
        }

        internal static void ApplyRoundedSkin(Image image)
        {
            if (image == null) return;
            Sprite sprite = GetRoundedSkin();
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
            }
            else
            {
                image.sprite = null;
                image.type = Image.Type.Simple;
            }
        }

        static Sprite GetRoundedSkin()
        {
            if (_roundedSkinSprite != null) return _roundedSkinSprite;
            try
            {
                const int size = 32;
                const float radius = 8f;
                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                {
                    float nearestY = y < radius ? radius : (y > size - 1f - radius ? size - 1f - radius : y);
                    for (int x = 0; x < size; x++)
                    {
                        float nearestX = x < radius ? radius : (x > size - 1f - radius ? size - 1f - radius : x);
                        float dx = x - nearestX;
                        float dy = y - nearestY;
                        float coverage = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                        pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(coverage * 255f));
                    }
                }

                _roundedSkinTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    name = "JHYL_RoundedSkinTexture",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _roundedSkinTexture.SetPixels32(pixels);
                _roundedSkinTexture.Apply(false, true);
                _roundedSkinSprite = Sprite.Create(_roundedSkinTexture, new Rect(0f, 0f, size, size),
                    new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect,
                    new Vector4(radius, radius, radius, radius));
                if (_roundedSkinSprite != null)
                {
                    _roundedSkinSprite.name = "JHYL_RoundedSkinSprite";
                    _roundedSkinSprite.hideFlags = HideFlags.HideAndDontSave;
                }
            }
            catch
            {
                _roundedSkinSprite = null;
            }
            return _roundedSkinSprite;
        }

        void StyleComposerButton(GameObject go, bool primary)
        {
            if (go == null) return;
            Image image = go.GetComponent<Image>();
            Button button = go.GetComponent<Button>();
            ApplyRoundedSkin(image);
            Color normal = primary
                ? new Color(0.68f, 0.57f, 0.34f, 0.98f)
                : new Color(0.18f, 0.22f, 0.21f, 0.98f);
            Color hover = primary
                ? new Color(0.77f, 0.66f, 0.42f, 1f)
                : new Color(0.25f, 0.30f, 0.28f, 1f);
            // Button 的 ColorTint 会把 targetGraphic 当前色与 ColorBlock 再相乘。
            // 以白色作为基底，避免主按钮的金色被二次相乘成发黑的褐色。
            if (image != null) image.color = Color.white;
            if (button != null)
            {
                ColorBlock colors = button.colors;
                colors.normalColor = normal;
                colors.highlightedColor = hover;
                colors.pressedColor = primary
                    ? new Color(0.58f, 0.47f, 0.27f, 1f)
                    : new Color(0.13f, 0.16f, 0.15f, 1f);
                colors.selectedColor = hover;
                colors.disabledColor = new Color(normal.r, normal.g, normal.b, 0.38f);
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0.08f;
                button.colors = colors;
                // Button 在 AddComponent 时已经用 Unity 默认白色执行过一次 OnEnable。
                // 换肤后立即同步当前正常态，避免首帧先显示整块白色再渐变到目标色。
                if (button.targetGraphic != null)
                    button.targetGraphic.CrossFadeColor(normal, 0f, true, true);
            }
            TextMeshProUGUI label = go.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
                label.color = primary
                    ? new Color(0.98f, 0.95f, 0.84f, 1f)
                    : new Color(0.87f, 0.85f, 0.77f, 1f);
        }

        void StyleTaiwuActionButton(GameObject go)
        {
            if (go == null) return;
            Image image = go.GetComponent<Image>();
            Button button = go.GetComponent<Button>();
            ApplyRoundedSkin(image);
            if (image != null) image.color = Color.white;
            if (button != null)
            {
                ColorBlock colors = button.colors;
                colors.normalColor = new Color(0.20f, 0.29f, 0.27f, 0.98f);
                colors.highlightedColor = new Color(0.29f, 0.41f, 0.36f, 1f);
                colors.pressedColor = new Color(0.14f, 0.21f, 0.19f, 1f);
                colors.selectedColor = colors.highlightedColor;
                colors.disabledColor = new Color(0.15f, 0.18f, 0.17f, 0.42f);
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0.08f;
                button.colors = colors;
                if (button.targetGraphic != null)
                    button.targetGraphic.CrossFadeColor(colors.normalColor, 0f, true, true);
            }
            TextMeshProUGUI label = go.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.color = new Color(0.91f, 0.88f, 0.78f, 1f);
        }

        GameObject NewButton(string name, Transform parent, string label, float size, out Button btn)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = ColAccent;
            btn = go.GetComponent<Button>();
            var t = NewText("L", go.transform, size, TextAlignmentOptions.Center);
            Anchor(t.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            t.text = label; t.raycastTarget = false;
            return go;
        }

        void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
        {
            rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = offMin; rt.offsetMax = offMax;
        }
    }

    /// <summary>聊天窗「外壳门面」:对外保持 ChatWindow.Open/OpenRemote/OpenGroup/OpenAssistant 等静态入口不变,
    /// 内部是「左侧永久会话导航 + 右侧独立内容」——每个已载入会话一个 ChatTab 实例,只显示当前活动内容;
    /// 后台页签的生成协程挂在 TalkEntryHost 上、各写各自实例,故支持同时与多个对象对话(后台并发生成)。
    /// 左侧会同时索引磁盘上的旧单聊与各个群聊；卸载/LRU 只释放 UI，不会让会话从导航消失。</summary>
    public static class ChatWindow
    {
        // ===== 全局:是否展示思考过程(多页签共享;由 Plugin 启动载入、设置页可切)=====
        static bool _showThinking = true;
        public static bool ShowThinking
        {
            get => _showThinking;
            set { _showThinking = value; }   // 只控制 UI 展示；是否请求推理按每次任务策略决定，避免简单寒暄也强耗思考 token。
        }

        // ===== 多页签状态 =====
        static readonly List<ChatTab> _tabs = new List<ChatTab>();
        static ChatTab _active;
        static long _useSeq;            // 单调递增使用序号:仅用于运行中页签的 LRU；不能参与左栏会话排序
        static TMP_FontAsset _font;     // 记下最近字体,新建页签 / 页签栏文字复用
        static Vector2 _sharedPanelPosition = Vector2.zero;
        static bool _overlayHidden;     // HideForOverlay 让位中(供 RestoreFromOverlay 复原判定)
        static int _groupSeq;           // 仅用于无法构造成员键时的兜底
        static long _navigationActivityClock;
        static ChatTab _combatTransitionOwner; // 全页签共享：一次只允许一个原生战斗入场

        // 原生战斗会销毁发起页签，结算页却在之后才创建。这里保存最小会话身份，
        // 让 ViewCombatResult.OnInit 的权威结果可以一次性投影回原会话，而不依赖已销毁的 UI。
        sealed class PendingNativeCombatResult
        {
            public string OperationId;
            public uint WorldId;
            public int WorldGeneration;
            public int TaiwuId;
            public int NpcId;
            public string NpcName;
            public short CombatConfig;
            public long ClearEpoch;
            public bool ResultCaptured;
            public sbyte CombatResult;
            public sbyte CombatType;
            public int ResultDate;
            public bool PersistenceInProgress;
            public bool RetryScheduled;
        }
        static readonly object NativeCombatResultGate = new object();
        static PendingNativeCombatResult _pendingNativeCombatResult;

        // 左侧会话导航(独立 Canvas,浮在右侧聊天面板之上)
        static GameObject _barRoot;
        static RectTransform _barPanel;
        static RectTransform _barRow;
        static ScrollRect _barScroll;
        static GameObject _barTopSpacer;
        static GameObject _barBottomSpacer;
        const float NavigationRowPitch = 60f; // 58px row + 2px layout spacing
        const int NavigationRowPoolSize = 32;
        static readonly List<SessionNavEntry> _visibleNavigation =
            new List<SessionNavEntry>();
        static int _navigationWindowFirst = -1;
        static bool _navigationWindowUpdating;
        static readonly List<SessionNavEntry> _persistedNavigation = new List<SessionNavEntry>();
        static readonly Dictionary<int, string> _navigationResolvedNames = new Dictionary<int, string>();
        static readonly HashSet<int> _navigationNameAttempts = new HashSet<int>();
        static int _navigationGeneration = -1;
        static int _navigationTaiwuId = -1;
        static bool _navigationDirty = true;
        static bool _navigationNameQueryPending;
        static long _navigationNameQueryEpoch;
        static Task<NavigationIndexRebuildResult> _navigationIndexRebuildTask;
        static long _navigationIndexRebuildEpoch;
        static bool _navigationIndexRetryPending;
        static int _navigationIndexFailureCount;
        static long _navigationIndexRetryNotBeforeUtcTicks;
        const int MaxNavigationIndexFailuresPerWorld = 3;
        const int NavigationIndexRetryBackoffFrames = 120;
        const int NavigationIndexFailureCooldownSeconds = 30;

        internal sealed class SessionNavEntry
        {
            public string Identity;
            public string Title;
            public string Subtitle;
            public ChatTab Tab;
            public int TaiwuId;
            public int NpcId;
            public bool IsAssistant;
            public bool IsGroup;
            public string GroupId;
            public List<KeyValuePair<int, string>> Roster;
            public long SortKey;
            public int UnreadCount;
            public bool IsDead;
        }

        sealed class NavigationIndexRebuildResult
        {
            public bool RebuildSingles;
            public bool RebuildGroups;
            public long SingleEpoch;
            public long GroupEpoch;
            public List<ConversationSessionIndexStore.SingleEntry> Singles;
            public List<ConversationSessionIndexStore.GroupEntry> Groups;
            public bool SinglesCommitted;
            public bool GroupsCommitted;
        }

        enum NavigationIndexRebuildStart
        {
            Noop,
            Started,
            Blocked,
        }

        internal static bool TryAcquireCombatTransition(ChatTab owner)
        {
            if (owner == null || _combatTransitionOwner != null) return false;
            _combatTransitionOwner = owner;
            return true;
        }

        internal static void ReleaseCombatTransition(ChatTab owner)
        {
            if (owner != null && ReferenceEquals(_combatTransitionOwner, owner))
                _combatTransitionOwner = null;
        }

        internal static bool ArmPendingNativeCombatResult(TalkOrchestrator.PendingCombatRequest request)
        {
            if (request == null || !OperationId.IsValid(request.OperationId) || request.WorldId == 0
                || request.TaiwuId <= 0 || request.NpcId < 0
                || !WorldLifecycle.IsSameWorld(request.WorldGeneration) || WorldLifecycle.WorldId != request.WorldId)
                return false;

            var pending = new PendingNativeCombatResult
            {
                OperationId = request.OperationId,
                WorldId = request.WorldId,
                WorldGeneration = request.WorldGeneration,
                TaiwuId = request.TaiwuId,
                NpcId = request.NpcId,
                NpcName = request.NpcName,
                CombatConfig = request.CombatConfig,
                ClearEpoch = TalkOrchestrator.CaptureConversationClearEpoch(request.TaiwuId, request.NpcId),
            };
            lock (NativeCombatResultGate)
            {
                if (_pendingNativeCombatResult != null)
                    Debug.LogWarning("[JHYL_COMBAT_RESULT] replacing stale pending projection op="
                        + _pendingNativeCombatResult.OperationId);
                _pendingNativeCombatResult = pending;
            }
            Debug.Log("[JHYL_COMBAT_RESULT] armed npc=" + request.NpcId + " config=" + request.CombatConfig
                + " gen=" + request.WorldGeneration + " op=" + request.OperationId);
            return true;
        }

        internal static void CancelPendingNativeCombatResult(TalkOrchestrator.PendingCombatRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.OperationId)) return;
            lock (NativeCombatResultGate)
            {
                if (_pendingNativeCombatResult != null
                    && string.Equals(_pendingNativeCombatResult.OperationId, request.OperationId,
                        System.StringComparison.Ordinal))
                    _pendingNativeCombatResult = null;
            }
        }

        /// <summary>
        /// 由原生 ViewCombatResult.OnInit Harmony 补丁调用。先核对世界、主敌人与战斗类型，
        /// 再以 operation id 幂等落盘。只有持久化成功后才消费 pending；瞬时写盘失败会保留
        /// 结果并后台重试，重复初始化也不会形成重复聊天行。
        /// </summary>
        internal static bool RecordNativeCombatResult(int mainEnemyId, sbyte combatResult, sbyte combatType)
        {
            PendingNativeCombatResult pending;
            lock (NativeCombatResultGate)
            {
                pending = _pendingNativeCombatResult;
                if (pending == null) return false;
                if (!WorldLifecycle.IsSameWorld(pending.WorldGeneration) || WorldLifecycle.WorldId != pending.WorldId)
                {
                    _pendingNativeCombatResult = null;
                    return false;
                }
                if (mainEnemyId != pending.NpcId || combatType != (sbyte)pending.CombatConfig)
                    return false;
                if (pending.ResultCaptured
                    && (pending.CombatResult != combatResult || pending.CombatType != combatType))
                    return false;
                if (!pending.ResultCaptured)
                {
                    pending.ResultCaptured = true;
                    pending.CombatResult = combatResult;
                    pending.CombatType = combatType;
                    try { pending.ResultDate = SingletonObject.getInstance<BasicGameData>().CurrDate; }
                    catch { pending.ResultDate = -1; }
                }
                if (pending.PersistenceInProgress) return false;
                pending.PersistenceInProgress = true;
            }

            bool saved = PersistPendingNativeCombatResult(pending);
            bool scheduleRetry = false;
            lock (NativeCombatResultGate)
            {
                if (ReferenceEquals(_pendingNativeCombatResult, pending))
                {
                    pending.PersistenceInProgress = false;
                    if (saved) _pendingNativeCombatResult = null;
                    else if (!pending.RetryScheduled)
                    {
                        pending.RetryScheduled = true;
                        scheduleRetry = true;
                    }
                }
            }
            if (saved)
                Debug.Log("[JHYL_COMBAT_RESULT] persisted npc=" + pending.NpcId + " result=" + combatResult
                    + " type=" + combatType + " op=" + pending.OperationId);
            else
            {
                Debug.LogWarning("[JHYL_COMBAT_RESULT] projection not persisted; retained for retry npc="
                    + pending.NpcId + " op=" + pending.OperationId);
                if (scheduleRetry)
                {
                    var host = TalkEntryHost.Instance;
                    if (host != null) host.StartCoroutine(RetryPendingNativeCombatResult(pending.OperationId));
                    else lock (NativeCombatResultGate)
                    {
                        if (ReferenceEquals(_pendingNativeCombatResult, pending)) pending.RetryScheduled = false;
                    }
                }
            }
            return saved;
        }

        private static bool PersistPendingNativeCombatResult(PendingNativeCombatResult pending)
        {
            if (pending == null || !pending.ResultCaptured) return false;
            string narrative = CombatResultProjection.Narrative(pending.CombatType);
            string receipt = CombatResultProjection.ToolResult(
                pending.NpcName, pending.CombatResult, pending.CombatType);
            string stableTurnId = "combat-result-" + pending.OperationId;
            try
            {
                return TalkOrchestrator.AppendNpcLineWithStableIdIfCurrent(
                    pending.TaiwuId, pending.NpcId, narrative, pending.ResultDate,
                    pending.WorldGeneration, pending.ClearEpoch, null,
                    new List<string> { receipt }, stableTurnId, pending.NpcName);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[JHYL_COMBAT_RESULT] persistence exception=" + ex.GetType().Name
                    + " op=" + pending.OperationId);
                return false;
            }
        }

        private static IEnumerator RetryPendingNativeCombatResult(string operationId)
        {
            int attempt = 0;
            while (true)
            {
                attempt++;
                // 前 12 次快速收敛瞬时 I/O 故障；之后低频持续重试。只有成功、换档或
                // pending 被新的权威战斗上下文取代才结束，不能因固定次数耗尽永久漏记。
                float delay = attempt <= 12 ? Mathf.Min(5f, attempt) : 30f;
                yield return new WaitForSecondsRealtime(delay);
                PendingNativeCombatResult pending;
                lock (NativeCombatResultGate)
                {
                    pending = _pendingNativeCombatResult;
                    if (pending == null || !string.Equals(pending.OperationId, operationId,
                            System.StringComparison.Ordinal)) yield break;
                    if (!WorldLifecycle.IsSameWorld(pending.WorldGeneration)
                        || WorldLifecycle.WorldId != pending.WorldId)
                    {
                        _pendingNativeCombatResult = null;
                        yield break;
                    }
                    if (!pending.ResultCaptured || pending.PersistenceInProgress) continue;
                    pending.PersistenceInProgress = true;
                }

                bool saved = PersistPendingNativeCombatResult(pending);
                lock (NativeCombatResultGate)
                {
                    if (!ReferenceEquals(_pendingNativeCombatResult, pending)) yield break;
                    pending.PersistenceInProgress = false;
                    if (saved) _pendingNativeCombatResult = null;
                }
                if (saved)
                {
                    Debug.Log("[JHYL_COMBAT_RESULT] retry persisted npc=" + pending.NpcId
                        + " attempt=" + attempt + " op=" + operationId);
                    yield break;
                }
                if (attempt == 12 || (attempt > 12 && (attempt - 12) % 10 == 0))
                    Debug.LogWarning("[JHYL_COMBAT_RESULT] persistence still unavailable; continuing low-frequency retry op="
                        + operationId + " attempt=" + attempt);
            }
        }

        // ---------- 对外入口:找到该会话的页签就激活,没有就新建 + 激活 ----------
        private static bool TryOpenSoulConversation(int npcId, int taiwuId,
            string npcName, TMP_FontAsset font)
        {
            // 死亡状态已经由本体权威核验并耐久化。此时人物可能已从活人对象表移走，
            // 不能再调用只服务于活人的固定模板副本准备，否则后端必然返回“人物已失效”，
            // 把本应可打开的灵魂旧会话挡在窗口初始化之前。
            int knownId = CharacterProxyIdentityService.ResolveKnown(taiwuId, npcId);
            bool originalDead = ArchivedCharacterStatusStore.IsDead(taiwuId, npcId);
            bool knownDead = knownId != npcId
                && ArchivedCharacterStatusStore.IsDead(taiwuId, knownId);
            if (!originalDead && !knownDead) return false;

            // 已迁移身份的聊天以耐久映射后的 ID 为准；普通人物则保持原 ID。
            // 灵魂没有“当面/千里传音”之分，统一按本地单聊键打开。
            OpenResolved(knownDead ? knownId : npcId, taiwuId, npcName, font, false);
            return true;
        }

        public static void Open(int npcId, int taiwuId, string npcName, TMP_FontAsset font)
        {
            if (font != null) _font = font;
            if (TryOpenSoulConversation(npcId, taiwuId, npcName, font)) return;
            int knownId = CharacterProxyIdentityService.ResolveKnown(taiwuId, npcId);
            OpenResolved(knownId > 0 ? knownId : npcId, taiwuId, npcName, font,
                false, npcId);
        }

        public static void OpenRemote(int npcId, int taiwuId, string npcName, TMP_FontAsset font)
        {
            if (font != null) _font = font;
            if (TryOpenSoulConversation(npcId, taiwuId, npcName, font)) return;
            int knownId = CharacterProxyIdentityService.ResolveKnown(taiwuId, npcId);
            OpenResolved(knownId > 0 ? knownId : npcId, taiwuId, npcName, font,
                true, npcId);
        }

        private static void OpenResolved(int npcId, int taiwuId, string npcName,
            TMP_FontAsset font, bool remote, int proxySourceNpcId = -1)
        {
            // 玩家可能在原版人物事件窗完成行动后立刻打开 AI 对话；先把尚未离开
            // 事件窗的真实互动链收束落盘，确保本轮上下文马上可见。
            try { NativeInteractionCapture.FlushPendingNow(); } catch { }
            if (font != null) _font = font;
            RevealNavigationIdentity(taiwuId, SingleNavigationIdentity(taiwuId, npcId));
            string key = (remote ? "rmt:" : "npc:") + WorldLifecycle.Generation + ":" + taiwuId + ":" + npcId;
            var tab = Find(key) ?? FindSingle(taiwuId, npcId);
            if (tab != null)
            {
                // 单聊与千里传音共用同一份持久历史，也必须共用一个运行中会话，
                // 否则两个页签会并发改写同一 Chat_*.json。
                // 页签可能创建于人物生前；每次复用都重新同步耐久死亡状态，避免仍以
                // 活人标题、按钮和工具面继续运行。
                tab.RefreshCharacterArchiveStatus();
                Activate(tab); tab.RequestContactModeRefresh(); tab.RestoreInputFocus(); return;
            }
            EvictLruIfNeeded(key);
            tab = new ChatTab { Key = key };
            _tabs.Add(tab);
            try
            {
                if (remote) tab.OpenRemote(npcId, taiwuId, npcName,
                    font != null ? font : _font, proxySourceNpcId);
                else tab.Open(npcId, taiwuId, npcName,
                    font != null ? font : _font, proxySourceNpcId);
            }
            catch { }
            if (_font == null) _font = tab.Font;
            Activate(tab);
        }

        public static void OpenAssistant(TMP_FontAsset font = null)
        {
            if (font != null) _font = font;
            int taiwuId = ResolveCurrentTaiwuId();
            RevealNavigationIdentity(taiwuId, AssistantNavigationIdentity(taiwuId));
            var tab = Find("asst");
            if (tab != null)
            {
                tab.SyncAssistantHistoryIfIdle();
                Activate(tab);
                tab.RestoreInputFocus();
                return;
            }
            EvictLruIfNeeded("asst");
            tab = new ChatTab { Key = "asst" };
            _tabs.Add(tab);
            try { tab.OpenAssistant(font != null ? font : _font); } catch { }
            if (_font == null) _font = tab.Font;
            Activate(tab);
        }

        public static void OpenGroup(List<KeyValuePair<int, string>> roster, int taiwuId, TMP_FontAsset font)
            => OpenGroupCore(roster, taiwuId, font,
                GroupChatOrchestrator.CreateNewGroupId(taiwuId), true);

        static void OpenGroupCore(List<KeyValuePair<int, string>> roster, int taiwuId,
            TMP_FontAsset font, string persistedGroupId, bool newlyCreated)
        {
            var resolvedRoster = new List<KeyValuePair<int, string>>();
            var seen = new HashSet<int>();
            if (roster != null)
                foreach (KeyValuePair<int, string> member in roster)
                {
                    int resolvedId = CharacterProxyIdentityService.ResolveKnown(taiwuId, member.Key);
                    if (resolvedId > 0 && resolvedId != taiwuId && seen.Add(resolvedId))
                        resolvedRoster.Add(new KeyValuePair<int, string>(resolvedId, member.Value));
                }
            // 打开或恢复群聊只采用已经存在的身份映射，绝不在没有玩家发言时创建副本。
            // 新特殊人物须先在单聊发送首句或开启互动记录，群聊选人会明确拦截并提示。
            OpenGroupCoreResolved(resolvedRoster, taiwuId, font,
                persistedGroupId, newlyCreated);
        }

        static void OpenGroupCoreResolved(List<KeyValuePair<int, string>> roster, int taiwuId,
            TMP_FontAsset font, string persistedGroupId, bool newlyCreated)
        {
            if (font != null) _font = font;
            if (newlyCreated && string.IsNullOrWhiteSpace(persistedGroupId) && taiwuId > 0)
                persistedGroupId = GroupChatOrchestrator.CreateNewGroupId(taiwuId);
            if (!string.IsNullOrWhiteSpace(persistedGroupId))
                RevealNavigationIdentity(taiwuId,
                    GroupNavigationIdentity(taiwuId, persistedGroupId));
            string key = string.IsNullOrWhiteSpace(persistedGroupId)
                ? GroupTabKey(roster, taiwuId)
                : PersistedGroupTabKey(persistedGroupId, taiwuId);
            var existing = Find(key);
            if (existing != null)
            {
                if (existing.GroupHistoryOnly && !existing.Busy)
                {
                    try
                    {
                        existing.OpenGroup(roster, taiwuId, font != null ? font : _font,
                            persistedGroupId);
                    }
                    catch { }
                }
                Activate(existing); existing.RestoreInputFocus(); return;
            }
            EvictLruIfNeeded(key);
            var tab = new ChatTab
            {
                Key = key,
                NavigationSortKey = newlyCreated ? NextNavigationActivityKey() : 0
            };
            _tabs.Add(tab);
            try
            {
                tab.OpenGroup(roster, taiwuId, font != null ? font : _font, persistedGroupId);
            }
            catch { }
            if (_font == null) _font = tab.Font;
            Activate(tab);
            // 新建群聊必须立即出现在可视窗口第一项；普通点击已有会话仍不改变顺序。
            if (newlyCreated && !TryPromoteNavigationRow(tab))
            {
                if (_barScroll != null) _barScroll.verticalNormalizedPosition = 1f;
                _navigationWindowFirst = -1;
                RefreshNavigationWindow(true);
            }
        }

        internal static void ConsumeSourceTabForNativeCombat(ChatTab tab, bool nativeCombatObserved)
        {
            if (tab == null) return;
            ReleaseCombatTransition(tab);
            try { tab.InvalidateAllVoice(); tab.InvalidateAllImages(); } catch { }
            _tabs.Remove(tab);
            if (ReferenceEquals(_active, tab))
            {
                _active = null;
                // 战斗期间不重新展示任何聊天面板；只保留一个可在战后继续使用的活动页签。
                // 旧实现直接清空活动页且永久隐藏整条页签栏，导致其他会话也像被一并关闭。
                for (int i = 0; i < _tabs.Count; i++)
                {
                    ChatTab candidate = _tabs[i];
                    if (candidate != null && (_active == null || candidate.LastUsed > _active.LastUsed))
                        _active = candidate;
                }
                if (_active != null) _active.SetRootActive(false);
            }
            try
            {
                if (tab.Root != null)
                {
                    PopupRegistry.Unregister(tab.Root);
                    Object.Destroy(tab.Root);
                }
            }
            catch { }
            if (_barRoot != null) _barRoot.SetActive(false);
            RefreshTabs();
            Debug.Log("[JHYL_COMBAT_LIFECYCLE] native_visible source_tab_disposed remaining_tabs=" + _tabs.Count);

            // 原生 DisplayEventHandler/CombatResult 完整拥有战斗 UI 的入场与退场；这里只观察，
            // 不调用 HideUI/StackBack，也不触碰原生事件窗口。战斗全部退出后恢复“页签入口”，
            // 但不擅自弹开其他聊天面板。
            var host = TalkEntryHost.Instance;
            if (host != null) host.StartCoroutine(RestoreTabBarAfterNativeCombat(nativeCombatObserved));
        }

        static IEnumerator RestoreTabBarAfterNativeCombat(bool nativeCombatObserved)
        {
            int generation = WorldLifecycle.Generation;
            bool observed = nativeCombatObserved;
            bool sawCombatBody = NativeCombatBodyActiveOrTransitioning();
            bool sawCombatResult = UIElement.CombatResult.Exist || UIElement.CombatResult.IsShowing;
            int stableInactiveFrames = 0;
            float inactiveSince = -1f;

            // 原生阶段并非无缝：CombatBegin 会先 RemoveElement，再 StackToUI(StateCombat)；
            // CombatOver 又要等本地结果数据与收尾动画才 MaskUI(CombatResult)。因此不能用
            // “某一帧三个叶子 UI 都不在”作为战斗结束。StateCombat 对 Combat 的持有关系是
            // 主判据，叶子 UI 的 Exist/IsShowing 只作补充。
            while (WorldLifecycle.Generation == generation)
            {
                bool bodyNow = NativeCombatBodyActiveOrTransitioning();
                bool resultNow = UIElement.CombatResult.Exist || UIElement.CombatResult.IsShowing;
                bool lifecycleActive = UIElement.CombatBegin.Exist || UIElement.CombatBegin.IsShowing
                    || bodyNow || resultNow;

                if (lifecycleActive)
                {
                    observed = true;
                    sawCombatBody |= bodyNow;
                    sawCombatResult |= resultNow;
                    stableInactiveFrames = 0;
                    inactiveSince = -1f;
                    yield return null;
                    continue;
                }

                // 当前调用点在进入这里前已经观察到原生 UI。保留此门槛，防止未来新增
                // 调用点时在尚未入场的空帧直接恢复 Mod 页签栏。
                if (!observed)
                {
                    yield return null;
                    continue;
                }

                if (inactiveSince < 0f) inactiveSince = Time.unscaledTime;
                stableInactiveFrames++;
                float inactiveSeconds = Time.unscaledTime - inactiveSince;

                // 正常路径必须等结果页确实出现并关闭。异常取消/原生结果页未能创建时，
                // 仍给阶段切换足够长的宽限；超过宽限且持续多帧 inactive 才兜底恢复，
                // 避免一次原生异常令 Mod 页签栏永久消失。
                bool resultSettled = sawCombatResult || inactiveSeconds >= 12f;
                if (resultSettled && stableInactiveFrames >= 30 && inactiveSeconds >= 0.75f)
                    break;
                yield return null;
            }
            if (WorldLifecycle.Generation != generation) yield break;
            // 再让 CombatResult.OnConfirm 的 StackBack/StateCombat.Hide 以及同帧事件监听收尾。
            yield return null;
            yield return null;
            EnsureBar();
            RefreshTabs();
            Debug.Log("[JHYL_COMBAT_LIFECYCLE] native_closed remaining_tabs=" + _tabs.Count
                + " saw_body=" + sawCombatBody + " saw_result=" + sawCombatResult
                + " stable_inactive_frames=" + stableInactiveFrames);
        }

        static bool NativeCombatBodyActiveOrTransitioning()
        {
            if (UIElement.Combat.Exist || UIElement.Combat.IsShowing
                || UIElement.CombatBackground.Exist || UIElement.CombatBackground.IsShowing)
                return true;
            try
            {
                // 反编译权威代码中 StateCombat 是包含 CombatBackground + Combat 的 UIGroup；
                // IsElementActive(Combat) 会在组已经成为当前状态、叶子尚未激活的空帧仍返回 true。
                var manager = UIManager.Instance;
                return manager != null && (manager.IsElementActive(UIElement.Combat)
                    || manager.IsFocusElement(UIElement.StateCombat));
            }
            catch { return false; }
        }

        static bool NativeCombatUiVisible()
            => UIElement.Combat.Exist || UIElement.CombatBegin.Exist || UIElement.CombatResult.Exist;

        static string GroupTabKey(IList<KeyValuePair<int, string>> roster, int taiwuId)
        {
            var ids = new List<int>();
            if (roster != null)
                foreach (var member in roster)
                {
                    if (ids.Count >= 8) break; // must mirror GroupChatOrchestrator.MaxGroupMembers
                    if (member.Key > 0 && member.Key != taiwuId && !ids.Contains(member.Key)) ids.Add(member.Key);
                }
            ids.Sort();
            return ids.Count > 0
                ? ("group:" + WorldLifecycle.Generation + ":" + taiwuId + ":" + string.Join(",", ids))
                : ("group:fallback:" + WorldLifecycle.Generation + ":" + taiwuId + ":" + (_groupSeq++));
        }

        static string PersistedGroupTabKey(string groupId, int taiwuId)
            => "group:" + WorldLifecycle.Generation + ":" + taiwuId + ":id:" + (groupId ?? "");

        static long NextNavigationActivityKey()
        {
            long now = System.DateTime.UtcNow.Ticks;
            if (now <= _navigationActivityClock) now = _navigationActivityClock + 1;
            _navigationActivityClock = now;
            return now;
        }

        static ChatTab Find(string key)
        {
            for (int i = 0; i < _tabs.Count; i++) if (_tabs[i] != null && _tabs[i].Key == key) return _tabs[i];
            return null;
        }

        static ChatTab FindSingle(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId < 0) return null;
            for (int i = 0; i < _tabs.Count; i++)
            {
                ChatTab tab = _tabs[i];
                if (tab != null && !tab.AssistantMode && !tab.GroupMode
                    && tab.TaiwuId == taiwuId && tab.NpcId == npcId) return tab;
            }
            return null;
        }

        // 运行中 UI 实例有界；被卸载的会话仍由磁盘索引永久列在左栏，点击时再懒加载。
        static int TabCap()
        {
            float height = (_barPanel != null && _barPanel.rect.height > 1f)
                ? _barPanel.rect.height : ChatTab.PanelHeight;
            return Mathf.Max(4, (int)((height - 56f) / 58f));
        }

        // 开新会话前腾位:已达上限则淘汰「最久未使用」的页签(不含即将激活者与当前活动页),固化其记忆后销毁。
        static void EvictLruIfNeeded(string keepKey)
        {
            int cap = TabCap();
            int guard = 0;
            while (_tabs.Count >= cap && guard++ < 64)
            {
                ChatTab victim = null;
                for (int i = 0; i < _tabs.Count; i++)
                {
                    var t = _tabs[i];
                    if (t == null || t.Key == keepKey || t == _active || t.Busy) continue;
                    if (victim == null || t.LastUsed < victim.LastUsed) victim = t;
                }
                if (victim == null) break;   // 都受保护 → 暂允许超额(下次再淘汰)
                DestroyTab(victim);
            }
        }

        // 销毁一个页签:中断在跑的编排器 → 仅固化记忆(不做退群重开单聊)→ 注销弹窗登记 + 销毁 GameObject + 移出列表。
        static void DestroyTab(ChatTab tab)
        {
            if (tab == null) return;
            try { tab.Interrupt(); } catch { }
            try { tab.CloseSelf(false); } catch { }
            _tabs.Remove(tab);
            try { if (tab.Root != null) { PopupRegistry.Unregister(tab.Root); Object.Destroy(tab.Root); } } catch { }
            if (_active == tab) _active = null;
        }

        // ---------- 页签激活 / 关闭 / 完成回调 ----------
        static void Activate(ChatTab tab)
        {
            var prev = _active;
            if (prev != null && prev != tab) prev.ConsolidateGroupMemoryNow();   // 切走群聊即固化其记忆:去和某成员单聊立刻能想起群里的事
            _active = tab;
            if (tab != null && tab.PanelRt != null)
                tab.PanelRt.anchoredPosition = _sharedPanelPosition;
            if (_minimized) { _minimized = false; AssistantWidget.SetMinimized(false); }   // 重新展示某页签 = 取消最小化
            for (int i = 0; i < _tabs.Count; i++) if (_tabs[i] != null) _tabs[i].SetRootActive(_tabs[i] == tab);
            if (tab != null)
            {
                tab.Unread = false;
                tab.LastUsed = ++_useSeq;
                tab.RefreshMonthlyCandidateButton();
                if (!tab.AssistantMode && !tab.GroupMode && tab.TaiwuId > 0 && tab.NpcId >= 0)
                {
                    if (!NpcChatUnreadStore.MarkRead(tab.TaiwuId, tab.NpcId))
                        Debug.LogWarning("[JHYL_NPC_PROACTIVE] 未读状态写盘失败 npc=" + tab.NpcId);
                    AssistantWidget.NotifyChatUnreadChanged();
                }
            }
            EnsureBar();
            RefreshTabs();
        }

        /// <summary>关闭一个页签:先做该会话收尾(记忆固化)，再销毁 UI 并切到邻近页签。</summary>
        public static void CloseTab(ChatTab tab)
        {
            if (tab == null) return;
            try { tab.Interrupt(); } catch { }   // 先中断本页签在跑的编排器(免白跑 LLM、绿提示错落、群聊半截固化),再固化收尾
            try { tab.CloseSelf(); } catch { }
            int idx = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            try { if (tab.Root != null) { PopupRegistry.Unregister(tab.Root); Object.Destroy(tab.Root); } } catch { }
            if (_tabs.Count > 0) Activate(_tabs[Mathf.Clamp(idx, 0, _tabs.Count - 1)]);
            else { _active = null; if (_barRoot != null) _barRoot.SetActive(false); }
        }

        /// <summary>离开存档/卸载 Mod：取消所有在制编排器并直接销毁页签。
        /// 不能走 CloseSelf（它会在离开世界时反而启动画像固化 LLM）。</summary>
        public static void ResetForWorldExit()
        {
            GroupMemberPicker.CancelForWorldExit();
            TaiwuDirectActionWindow.CloseForWorldExit();
            var copy = new List<ChatTab>(_tabs);
            foreach (var tab in copy) { try { tab?.InvalidateAllVoice(); tab?.InvalidateAllImages(); } catch { } }
            VoicePlayer.Stop();
            foreach (var tab in copy)
            {
                if (tab == null) continue;
                try { tab.Interrupt(); } catch { }
                try { if (tab.Root != null) { PopupRegistry.Unregister(tab.Root); Object.Destroy(tab.Root); } } catch { }
            }
            _tabs.Clear();
            _active = null;
            _overlayHidden = false;
            _minimized = false;
            _groupSeq = 0;
            _navigationActivityClock = 0;
            _visibleNavigation.Clear();
            _navigationWindowFirst = -1;
            _navigationWindowUpdating = true;
            try
            {
                if (_barScroll != null) _barScroll.verticalNormalizedPosition = 1f;
                if (_barRow != null)
                    for (int i = 0; i < _barRow.childCount; i++)
                    {
                        SidebarConversationRow row = _barRow.GetChild(i)
                            ?.GetComponent<SidebarConversationRow>();
                        row?.ClearBinding();
                    }
            }
            finally { _navigationWindowUpdating = false; }
            _persistedNavigation.Clear();
            _navigationResolvedNames.Clear();
            _navigationNameAttempts.Clear();
            _navigationGeneration = -1;
            _navigationTaiwuId = -1;
            _navigationDirty = true;
            _navigationNameQueryPending = false;
            unchecked { _navigationNameQueryEpoch++; }
            _navigationIndexRebuildTask = null;
            unchecked { _navigationIndexRebuildEpoch++; }
            _navigationIndexRetryPending = false;
            _navigationIndexFailureCount = 0;
            _navigationIndexRetryNotBeforeUtcTicks = 0;
            ConversationNavigationStore.ResetCacheForWorldExit();
            NpcChatUnreadStore.ResetForWorldExit();
            _combatTransitionOwner = null;
            lock (NativeCombatResultGate) _pendingNativeCombatResult = null;
            if (_barRoot != null) _barRoot.SetActive(false);
            try { AssistantWidget.SetMinimized(false); } catch { }
        }

        /// <summary>某页签本轮生成完成:非当前页则标未读点;刷新页签栏。</summary>
        public static void OnTabDone(ChatTab tab)
        {
            if (tab == null) return;
            if (tab != _active) tab.Unread = true;
            UpsertPersistedNavigation(NavigationForTab(tab));
            // The live tab already carries the latest title/activity. Do not rescan
            // persisted group archives when an ordinary response finishes.
            if (tab != _active) RefreshTabs();
        }

        /// <summary>玩家消息一进入会话流便置顶；点击页签本身不会改变左栏顺序。</summary>
        public static void OnMessageSent(ChatTab tab)
        {
            if (tab == null) return;
            RevealNavigationEntry(NavigationForTab(tab));
            tab.NavigationSortKey = NextNavigationActivityKey();
            UpsertPersistedNavigation(NavigationForTab(tab));
            if (!TryPromoteNavigationRow(tab)) RefreshTabs();
        }

        public static void OnGroupMembershipChanged(ChatTab tab)
        {
            if (tab == null) return;
            UpsertPersistedNavigation(NavigationForTab(tab));
            RefreshTabs();
        }

        internal static void NotifyNavigationStorageChanged()
        {
            _navigationDirty = true;
            RefreshTabs();
        }

        internal static void NotifyCharacterArchiveStatusChanged(int taiwuId)
        {
            if (taiwuId <= 0) return;
            _navigationDirty = true;
            foreach (ChatTab tab in _tabs)
                if (tab != null && tab.TaiwuId == taiwuId)
                    tab.RefreshCharacterArchiveStatus();
            if (_active != null && _active.TaiwuId == taiwuId) RefreshTabs();
        }

        /// <summary>
        /// 已经聊过的人首次确认死亡时，若玩家此前隐藏了会话，恢复一次灵魂聊天入口。
        /// 只有单聊索引确实存在才恢复；灵魂状态下再次隐藏不会被后续刷新反复撤销。
        /// </summary>
        internal static bool RevealNewSoulConversation(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId < 0) return false;
            if (!ArchivedCharacterStatusStore.NeedsSoulEntryHandling(taiwuId, npcId)) return true;
            bool conversed = false;
            bool indexReliable = false;
            try
            {
                if (ConversationSessionIndexStore.TryLoadSingles(taiwuId,
                    out List<ConversationSessionIndexStore.SingleEntry> singles))
                {
                    indexReliable = true;
                    conversed = singles.Exists(entry => entry != null && entry.NpcId == npcId);
                }
            }
            catch { }
            // 索引正在重建或损坏时稍后重试，不能提前消耗“一次恢复”机会。
            if (!indexReliable) return false;
            if (!conversed)
            {
                try
                {
                    if (!TalkOrchestrator.TryGetCompleteHistory(taiwuId, npcId,
                        out List<TalkTurn> complete, out _)) return false;
                    conversed = complete != null && complete.Count > 0;
                    if (!conversed)
                        conversed = !string.IsNullOrWhiteSpace(
                            TalkOrchestrator.SummaryOf(taiwuId, npcId));
                }
                catch { return false; }
            }
            // 仅群聊过、只有人物记忆而从未单聊的人，不凭空增加一条单聊导航。
            if (!conversed) return ArchivedCharacterStatusStore.MarkSoulEntryHandled(taiwuId, npcId);
            if (!RevealNavigationIdentity(taiwuId, SingleNavigationIdentity(taiwuId, npcId)))
                return false;
            if (!ArchivedCharacterStatusStore.MarkSoulEntryHandled(taiwuId, npcId)) return false;
            _navigationDirty = true;
            RefreshTabs();
            return true;
        }

        /// <summary>
        /// 固定模板人物迁移到永久副本后，立即清掉本进程已经缓存的旧单聊导航。
        /// 磁盘会话已由 TalkOrchestrator 原子迁移；这里仅处理 UI 内存，避免同名两行。
        /// </summary>
        internal static void NotifyConversationIdentityMigrated(int taiwuId,
            int oldNpcId, int newNpcId)
        {
            if (taiwuId <= 0 || oldNpcId < 0 || newNpcId < 0 || oldNpcId == newNpcId)
                return;
            string oldIdentity = SingleNavigationIdentity(taiwuId, oldNpcId);
            _persistedNavigation.RemoveAll(entry => entry != null
                && string.Equals(entry.Identity, oldIdentity, System.StringComparison.Ordinal));
            _visibleNavigation.RemoveAll(entry => entry != null
                && string.Equals(entry.Identity, oldIdentity, System.StringComparison.Ordinal));
            _navigationResolvedNames.Remove(oldNpcId);
            _navigationNameAttempts.Remove(oldNpcId);

            // 首句发送或开启互动记录现在可能在旧页签仍打开时完成迁移。没有同一新身份
            // 页签且旧页正处于复制准备时，原地接管新身份；其它旧页仍淘汰，避免并发改写。
            for (int i = _tabs.Count - 1; i >= 0; i--)
            {
                ChatTab tab = _tabs[i];
                if (tab == null || tab.GroupMode || tab.AssistantMode
                    || tab.TaiwuId != taiwuId || tab.NpcId != oldNpcId) continue;
                ChatTab existingNew = FindSingle(taiwuId, newNpcId);
                if ((existingNew == null || ReferenceEquals(existingNew, tab))
                    && tab.TryAdoptMigratedIdentity(oldNpcId, newNpcId))
                    continue;
                try { tab.Interrupt(); } catch { }
                _tabs.RemoveAt(i);
                if (ReferenceEquals(_active, tab)) _active = null;
                try
                {
                    if (tab.Root != null)
                    {
                        PopupRegistry.Unregister(tab.Root);
                        Object.Destroy(tab.Root);
                    }
                }
                catch { }
            }
            _navigationDirty = true;
            _navigationWindowFirst = -1;
            RefreshTabs();
        }

        internal static void NotifyAssistantHistoryChanged()
        {
            foreach (ChatTab tab in _tabs)
                if (tab != null && tab.AssistantMode)
                {
                    tab.SyncAssistantHistoryIfIdle();
                    if (tab != _active) tab.Unread = true;
                }
            // 只更新持久导航/现有页签；绝不 Reveal，隐藏的灵儿会话保持隐藏。
            NotifyNavigationStorageChanged();
        }

        internal static void NotifyNativeInteractionCommitted(int taiwuId, int npcId)
        {
            foreach (ChatTab tab in _tabs)
                if (tab != null && !tab.GroupMode && !tab.AssistantMode
                    && tab.TaiwuId == taiwuId && tab.NpcId == npcId)
                    tab.NotifyPersistedHistoryChanged();
            // 只更新持久会话导航；原生互动由玩家当面发起，不制造 NPC 主动消息未读。
            NotifyNavigationStorageChanged();
        }

        internal static void NotifyMonthlyCandidateChanged(int taiwuId, int npcId)
        {
            foreach (ChatTab tab in _tabs)
                if (tab != null && !tab.GroupMode && !tab.AssistantMode
                    && tab.TaiwuId == taiwuId && tab.NpcId == npcId)
                    tab.RefreshMonthlyCandidateButton();
        }

        /// <summary>会话标题 / 模式 / 人数变 → 刷新左侧导航。</summary>
        public static void NotifyTabChanged(ChatTab tab) { RefreshTabs(); }

        // ---------- 窗口级状态(对外保持原语义,作用于当前活动页签)----------
        public static bool IsOpen => _active != null && _active.RootActive;
        public static bool IsAssistantOpen => _active != null && _active.RootActive && _active.AssistantMode;
        public static bool TryGetActiveSingleChatContext(out int taiwuId, out int npcId, out string npcName)
        {
            taiwuId = 0; npcId = 0; npcName = null;
            return _active != null && _active.TryGetSingleContext(out taiwuId, out npcId, out npcName);
        }

        public static void HideForOverlay()
        {
            _overlayHidden = IsOpen;
            if (_active != null) _active.SetRootActive(false);
            if (_barRoot != null) _barRoot.SetActive(false);
        }
        public static void RestoreFromOverlay()
        {
            if (!_overlayHidden) return;
            _overlayHidden = false;
            if (_active != null) _active.SetRootActive(true);
            if (_barRoot != null && _tabs.Count > 0) _barRoot.SetActive(true);
            _active?.RefreshMonthlyCandidateButton();
        }
        // 关闭窗口 ≠ 关闭会话:只隐藏窗口与页签栏,所有页签连同对话原样留存(下次点 AI对话/千里传音即仍在页签里);
        // 记忆固化推迟到该会话被 LRU 淘汰(或手动 × 关掉)时再做,既不丢对话、也省去每次关窗都触发后台固化 LLM。
        public static void Hide()
        {
            if (_active != null) _active.SetRootActive(false);
            if (_barRoot != null) _barRoot.SetActive(false);
        }

        static bool _minimized;
        // 当前活动页签的面板 RectTransform(供页签栏跟随面板移动/拖拽)
        internal static RectTransform ActivePanelRt => _active != null ? _active.PanelRt : null;
        internal static Vector2 SharedPanelPosition => _sharedPanelPosition;
        internal static void SetSharedPanelPosition(Vector2 position)
        {
            _sharedPanelPosition = position;
            for (int i = 0; i < _tabs.Count; i++)
            {
                ChatTab tab = _tabs[i];
                if (tab != null && tab.PanelRt != null
                    && tab.PanelRt.anchoredPosition != position)
                    tab.PanelRt.anchoredPosition = position;
            }
        }
        internal static bool IsActiveTab(ChatTab tab) => tab != null && ReferenceEquals(_active, tab);
        internal static void RestoreHiddenActive()
        {
            if (_active != null) _active.SetRootActive(true);
            if (_barRoot != null && _tabs.Count > 0) _barRoot.SetActive(true);
        }

        /// <summary>最小化:收起聊天窗(隐藏面板+页签栏),在灵儿下方显示「展开」钮。**不打断**任何正在跑的生成
        /// (协程挂在 TalkEntryHost 上、与面板显隐无关,后台照常写各自气泡),还原时所有进度都在。</summary>
        public static void Minimize()
        {
            if (_active != null) { _active.ConsolidateGroupMemoryNow(); _active.SetRootActive(false); }   // 最小化群聊也先固化其记忆(免"缩起群聊去单聊、却想不起群里事")
            if (_barRoot != null) _barRoot.SetActive(false);
            _minimized = true;
            try { AssistantWidget.SetMinimized(true); } catch { }
        }

        /// <summary>从最小化还原:重新显示活动页签 + 页签栏,隐去「展开」钮。</summary>
        public static void RestoreFromMinimize()
        {
            if (_active == null)
            {
                OpenAssistant(_font);
                return;
            }
            _minimized = false;
            try { AssistantWidget.SetMinimized(false); } catch { }
            if (_active != null) _active.SetRootActive(true);
            if (_barRoot != null && _tabs.Count > 0) _barRoot.SetActive(true);
        }

        // ---------- 对话协程 / 外部用:作用于当前活动页签 ----------
        public static void ShowNpcLine(string text) { if (_active != null) _active.ShowNpcLine(text); }
        public static bool ShowNpcLineFor(int npcId, string text)
        {
            if (npcId < 0 || string.IsNullOrEmpty(text)) return false;
            int taiwuId = ResolveCurrentTaiwuId();
            var tab = FindSingle(taiwuId, npcId);
            if (tab == null) return false;
            tab.ShowNpcLine(text);
            if (tab != _active) { tab.Unread = true; RefreshTabs(); }
            return true;
        }

        internal static bool IsSingleConversationBusy(int taiwuId, int npcId)
        {
            ChatTab tab = FindSingle(taiwuId, npcId);
            return tab != null && (tab.Busy
                || ReferenceEquals(tab, _active) && tab.RootActive);
        }

        /// <summary>
        /// 后台 NPC 主动来信已经可靠写入单聊历史后，投影到当前 UI 与耐久未读状态。
        /// 历史会话不存在未读文件时全部视为已读；只有本入口收到的新消息才递增。
        /// </summary>
        internal static void ReceiveNpcProactiveMessage(int taiwuId, int npcId, string text,
            IReadOnlyList<string> actions = null, IReadOnlyList<string> toolResults = null)
        {
            if (taiwuId <= 0 || npcId < 0 || string.IsNullOrWhiteSpace(text)) return;
            // 主动来信属于新的单聊活动。玩家此前隐藏的只是导航入口，不是屏蔽此人；
            // 新消息到达后恢复该入口，避免未读内容被永久留在不可见会话中。
            RevealNavigationIdentity(taiwuId, SingleNavigationIdentity(taiwuId, npcId));
            ChatTab tab = FindSingle(taiwuId, npcId);
            bool visible = tab != null && ReferenceEquals(tab, _active) && tab.RootActive;
            if (tab != null) tab.ShowNpcProactiveTurn(text,
                actions == null ? null : new List<string>(actions),
                toolResults == null ? null : new List<string>(toolResults));
            if (visible)
            {
                NpcChatUnreadStore.MarkRead(taiwuId, npcId);
                tab.Unread = false;
            }
            else
            {
                if (!NpcChatUnreadStore.Add(taiwuId, npcId))
                    Debug.LogWarning("[JHYL_NPC_PROACTIVE] 未读状态写盘失败 npc=" + npcId);
                if (tab != null) tab.Unread = true;
            }
            _navigationDirty = true;
            AssistantWidget.NotifyChatUnreadChanged();
            RefreshTabs();
        }
        public static void AddSysNotice(string text) { if (_active != null) _active.AddSysNotice(text); }
        public static void AddGrantNotice(string what, string viewLabel, System.Action onView) { if (_active != null) _active.AddGrantNotice(what, viewLabel, onView); }
        public static void RestoreInputFocus() { if (_active != null) _active.RestoreInputFocus(); }

        // ================= 微信式左侧会话导航 =================
        static void EnsureBar()
        {
            if (_barRoot != null) return;
            _barRoot = new GameObject("JHYL_ConversationSidebar", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_barRoot);
            var cv = _barRoot.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30001;
            var sc = _barRoot.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);

            var panelGo = new GameObject("Sidebar", typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(_barRoot.transform, false);
            _barPanel = panelGo.GetComponent<RectTransform>();
            _barPanel.anchorMin = _barPanel.anchorMax = _barPanel.pivot = new Vector2(0.5f, 0.5f);
            _barPanel.sizeDelta = new Vector2(ChatTab.SidebarWidth, ChatTab.PanelHeight);
            // 与右侧会话面板共用同一底色，零间距拼接成一个连续的双栏窗口。
            panelGo.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.10f, 0.97f);

            var headerGo = new GameObject("Header", typeof(RectTransform), typeof(Image));
            headerGo.transform.SetParent(panelGo.transform, false);
            var headerRt = headerGo.GetComponent<RectTransform>();
            headerRt.anchorMin = new Vector2(0, 1); headerRt.anchorMax = new Vector2(1, 1);
            headerRt.pivot = new Vector2(0.5f, 1);
            headerRt.offsetMin = new Vector2(0, -52); headerRt.offsetMax = Vector2.zero;
            headerGo.GetComponent<Image>().color = new Color(0.14f, 0.16f, 0.15f, 0.98f);
            var header = ChipText("Title", "会话", headerGo.transform,
                new Color(0.94f, 0.92f, 0.84f, 1f), 21f, TextAlignmentOptions.Left);
            var headerTextRt = header.rectTransform;
            headerTextRt.anchorMin = Vector2.zero; headerTextRt.anchorMax = Vector2.one;
            headerTextRt.offsetMin = new Vector2(16, 0); headerTextRt.offsetMax = new Vector2(-8, 0);
            header.raycastTarget = false;

            var scrollGo = new GameObject("Sessions", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(panelGo.transform, false);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero; scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(0, 0); scrollRt.offsetMax = new Vector2(0, -52);
            scrollGo.GetComponent<Image>().color = new Color(0.035f, 0.04f, 0.038f, 0.35f);
            var scroll = scrollGo.GetComponent<ScrollRect>();
            _barScroll = scroll;
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGo.transform.SetParent(scrollGo.transform, false);
            var viewportRt = viewportGo.GetComponent<RectTransform>();
            viewportRt.anchorMin = Vector2.zero; viewportRt.anchorMax = Vector2.one;
            viewportRt.offsetMin = new Vector2(0, 0); viewportRt.offsetMax = new Vector2(-12, 0);
            scroll.viewport = viewportRt;

            var rowGo = new GameObject("ConversationList", typeof(RectTransform),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            rowGo.transform.SetParent(viewportGo.transform, false);
            _barRow = rowGo.GetComponent<RectTransform>();
            _barRow.anchorMin = new Vector2(0, 1); _barRow.anchorMax = new Vector2(1, 1);
            _barRow.pivot = new Vector2(0.5f, 1);
            _barRow.anchoredPosition = Vector2.zero;
            _barRow.sizeDelta = Vector2.zero;
            var vl = rowGo.GetComponent<VerticalLayoutGroup>();
            vl.spacing = 2f;
            vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
            vl.childControlWidth = true; vl.childControlHeight = true;
            vl.padding = new RectOffset(3, 3, 4, 4);
            rowGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = _barRow;
            _barTopSpacer = NavigationSpacer("TopSpacer", _barRow);
            _barBottomSpacer = NavigationSpacer("BottomSpacer", _barRow);
            scroll.onValueChanged.AddListener(_ => RefreshNavigationWindow(false));
            scroll.verticalScrollbar = UiScroll.AddVerticalForScrollRect(scrollGo);
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            var hoverScrollbar = panelGo.AddComponent<SidebarHoverScrollbar>();
            hoverScrollbar.scroll = scroll;
            hoverScrollbar.scrollbar = scroll.verticalScrollbar;
            hoverScrollbar.HideNow();

            // 分隔线画在左栏内部，不制造两块面板之间的视觉断层。
            var seamGo = new GameObject("Seam", typeof(RectTransform), typeof(Image));
            seamGo.transform.SetParent(panelGo.transform, false);
            var seamRt = seamGo.GetComponent<RectTransform>();
            seamRt.anchorMin = new Vector2(1f, 0f);
            seamRt.anchorMax = Vector2.one;
            seamRt.offsetMin = new Vector2(-1f, 0f);
            seamRt.offsetMax = Vector2.zero;
            var seamImage = seamGo.GetComponent<Image>();
            seamImage.color = new Color(0.24f, 0.27f, 0.24f, 0.86f);
            seamImage.raycastTarget = false;

            _barRoot.AddComponent<TabBarFollower>().sidebar = _barPanel;
        }

        static void RefreshTabs()
        {
            if (_barRoot == null || _barRow == null) return;
            List<SessionNavEntry> entries = BuildNavigationEntries();
            int taiwuId = _active != null && _active.TaiwuId > 0
                ? _active.TaiwuId : ResolveCurrentTaiwuId();
            var hidden = HiddenNavigationIdentities(taiwuId);
            _visibleNavigation.Clear();
            foreach (SessionNavEntry entry in entries)
            {
                if (entry != null && !hidden.Contains(entry.Identity))
                    _visibleNavigation.Add(entry);
            }
            RefreshNavigationWindow(true);
            _barRoot.SetActive(_visibleNavigation.Count > 0 && !_minimized && !_overlayHidden
                && _active != null && _active.RootActive);
        }

        static GameObject NavigationSpacer(string name, Transform parent)
        {
            var spacer = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(parent, false);
            var layout = spacer.GetComponent<LayoutElement>();
            layout.minHeight = 0f;
            layout.preferredHeight = 0f;
            layout.flexibleHeight = 0f;
            spacer.SetActive(false);
            return spacer;
        }

        static void SetNavigationSpacer(GameObject spacer, int hiddenRows)
        {
            if (spacer == null) return;
            bool visible = hiddenRows > 0;
            spacer.SetActive(visible);
            if (!visible) return;
            var layout = spacer.GetComponent<LayoutElement>();
            if (layout == null) return;
            // LayoutGroup 会在 spacer 与邻接行之间另加 2px spacing。
            float height = Mathf.Max(0f, hiddenRows * NavigationRowPitch - 2f);
            layout.minHeight = height;
            layout.preferredHeight = height;
        }

        static void RefreshNavigationWindow(bool force)
        {
            if (_navigationWindowUpdating || _barRow == null) return;
            _navigationWindowUpdating = true;
            try
            {
                int total = _visibleNavigation.Count;
                int window = Mathf.Min(total, NavigationRowPoolSize);
                int maxFirst = Mathf.Max(0, total - window);
                float normalized = _barScroll != null
                    ? Mathf.Clamp01(_barScroll.verticalNormalizedPosition) : 1f;
                int first = maxFirst == 0 ? 0
                    : Mathf.Clamp(Mathf.RoundToInt((1f - normalized) * maxFirst),
                        0, maxFirst);
                if (!force && first == _navigationWindowFirst) return;
                _navigationWindowFirst = first;

                var rows = new List<SidebarConversationRow>();
                var byIdentity = new Dictionary<string, SidebarConversationRow>(
                    System.StringComparer.Ordinal);
                for (int i = 0; i < _barRow.childCount; i++)
                {
                    Transform child = _barRow.GetChild(i);
                    SidebarConversationRow row = child != null
                        ? child.GetComponent<SidebarConversationRow>() : null;
                    if (row == null) continue;
                    rows.Add(row);
                    if (!string.IsNullOrWhiteSpace(row.identity)
                        && !byIdentity.ContainsKey(row.identity))
                        byIdentity[row.identity] = row;
                }

                var retained = new HashSet<SidebarConversationRow>();
                if (_barTopSpacer != null) _barTopSpacer.transform.SetSiblingIndex(0);
                for (int offset = 0; offset < window; offset++)
                {
                    SessionNavEntry entry = _visibleNavigation[first + offset];
                    SidebarConversationRow row = null;
                    if (!string.IsNullOrWhiteSpace(entry.Identity)
                        && byIdentity.TryGetValue(entry.Identity, out SidebarConversationRow exact)
                        && exact != null && !retained.Contains(exact))
                        row = exact;
                    if (row == null)
                    {
                        foreach (SidebarConversationRow candidate in rows)
                            if (candidate != null && !retained.Contains(candidate))
                            {
                                row = candidate;
                                break;
                            }
                    }
                    if (row == null)
                    {
                        row = AddSessionRow(entry);
                        if (row != null) rows.Add(row);
                    }
                    if (row == null) continue;
                    row.gameObject.SetActive(true);
                    BindSessionRow(row, entry);
                    retained.Add(row);
                    row.transform.SetSiblingIndex(offset + 1);
                }
                foreach (SidebarConversationRow row in rows)
                    if (row != null && !retained.Contains(row))
                    {
                        row.ClearBinding();
                        row.gameObject.SetActive(false);
                    }
                SetNavigationSpacer(_barTopSpacer, first);
                SetNavigationSpacer(_barBottomSpacer, total - first - window);
                if (_barBottomSpacer != null)
                    _barBottomSpacer.transform.SetSiblingIndex(_barRow.childCount - 1);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_barRow);
            }
            finally
            {
                _navigationWindowUpdating = false;
            }
        }

        static bool PromoteVisibleNavigation(SessionNavEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Identity)) return false;
            int index = _visibleNavigation.FindIndex(candidate => candidate != null
                && string.Equals(candidate.Identity, entry.Identity,
                    System.StringComparison.Ordinal));
            if (index < 0) return false;
            _visibleNavigation.RemoveAt(index);
            _visibleNavigation.Insert(0, entry);
            if (_barScroll != null) _barScroll.verticalNormalizedPosition = 1f;
            _navigationWindowFirst = -1;
            RefreshNavigationWindow(true);
            if (_barRoot != null)
                _barRoot.SetActive(!_minimized && !_overlayHidden
                    && _active != null && _active.RootActive);
            return true;
        }

        static List<SessionNavEntry> BuildNavigationEntries()
        {
            int taiwuId = _active != null && _active.TaiwuId > 0
                ? _active.TaiwuId : ResolveCurrentTaiwuId();
            EnsurePersistedNavigation(taiwuId);
            var byIdentity = new Dictionary<string, SessionNavEntry>(System.StringComparer.Ordinal);
            foreach (SessionNavEntry saved in _persistedNavigation)
                if (saved != null && !string.IsNullOrEmpty(saved.Identity))
                    byIdentity[saved.Identity] = CloneNavigationEntry(saved);

            foreach (ChatTab tab in _tabs)
            {
                if (tab == null) continue;
                SessionNavEntry live = NavigationForTab(tab);
                if (live == null || string.IsNullOrEmpty(live.Identity)) continue;
                if (byIdentity.TryGetValue(live.Identity, out SessionNavEntry saved))
                {
                    live.SortKey = System.Math.Max(live.SortKey, saved.SortKey);
                    // Keep the tab's durable ordering baseline as well.  Membership/title-only
                    // updates must never publish the zero used when an existing group is opened.
                    tab.NavigationSortKey = live.SortKey;
                }
                byIdentity[live.Identity] = live;
            }
            var entries = new List<SessionNavEntry>(byIdentity.Values);
            entries.Sort((a, b) =>
            {
                int order = b.SortKey.CompareTo(a.SortKey);
                return order != 0 ? order : string.Compare(a.Title, b.Title, System.StringComparison.Ordinal);
            });
            return entries;
        }

        static void EnsurePersistedNavigation(int taiwuId)
        {
            int generation = WorldLifecycle.Generation;
            if (!_navigationDirty && _navigationGeneration == generation && _navigationTaiwuId == taiwuId) return;
            // A corrupt/missing manifest is checked once before the worker starts.  While that
            // worker is scanning and staging a replacement, keep the last in-memory navigation
            // instead of reparsing the same damaged files on every Unity frame/refresh.
            if (_navigationIndexRebuildTask != null) return;
            if (taiwuId > 0 && !ConversationIndexesReady(taiwuId))
            {
                NavigationIndexRebuildStart start = EnsureNavigationIndexRebuild(
                    taiwuId, generation, true, true, probeExisting: true);
                if (start == NavigationIndexRebuildStart.Blocked)
                    QueueNavigationIndexRebuildRetry(taiwuId, generation,
                        JianghuYoulingPaths.CurrentWorldId);
                return;
            }
            _persistedNavigation.Clear();
            _navigationGeneration = generation;
            _navigationTaiwuId = taiwuId;
            _navigationDirty = false;
            if (taiwuId <= 0) return;

            var unresolvedIds = new HashSet<int>();
            var sessions = new List<GroupChatOrchestrator.GroupSession>();
            var partners = new List<TalkOrchestrator.ConversedPartner>();
            var groupNameHints = new Dictionary<int, string>();
            bool groupsIndexed = false;
            bool singlesIndexed = false;
            try
            {
                groupsIndexed = new GroupChatOrchestrator()
                    .TryLoadIndexedSessionSummaries(taiwuId, out sessions);
                foreach (GroupChatOrchestrator.GroupSession session in sessions)
                {
                    if (session?.Members == null) continue;
                    foreach (GroupChatOrchestrator.Member member in session.Members)
                        if (member != null && member.Id > 0
                            && !TalkOrchestrator.IsUnresolvedNpcName(member.Name))
                            groupNameHints[member.Id] = member.Name.Trim();
                }
            }
            catch { sessions = new List<GroupChatOrchestrator.GroupSession>(); }

            try
            {
                singlesIndexed = TalkOrchestrator.TryLoadIndexedConversedPartners(
                    taiwuId, out partners);
                foreach (TalkOrchestrator.ConversedPartner partner in partners)
                {
                    if (partner == null || partner.NpcId < 0) continue;
                    string resolvedName = ResolveNavigationName(
                        partner.NpcId, partner.Name, groupNameHints);
                    if (resolvedName == null) unresolvedIds.Add(partner.NpcId);
                    else if (TalkOrchestrator.IsUnresolvedNpcName(partner.Name))
                        TalkOrchestrator.TryUpdateExistingConversationNameIfCurrent(
                            taiwuId, partner.NpcId, resolvedName, generation);
                    _persistedNavigation.Add(new SessionNavEntry
                    {
                        Identity = SingleNavigationIdentity(taiwuId, partner.NpcId),
                        Title = resolvedName ?? "姓名载入中…",
                        Subtitle = "单聊 · " + partner.Turns + " 条",
                        TaiwuId = taiwuId,
                        NpcId = partner.NpcId,
                        IsDead = ArchivedCharacterStatusStore.IsDead(taiwuId, partner.NpcId),
                        UnreadCount = NpcChatUnreadStore.Count(taiwuId, partner.NpcId),
                        SortKey = partner.LastActivityUtc.Ticks
                    });
                }
            }
            catch { }

            try
            {
                var assistantHistory = AssistantOrchestrator.HistorySnapshot();
                if (assistantHistory != null && assistantHistory.Count > 0)
                    _persistedNavigation.Add(new SessionNavEntry
                    {
                        Identity = AssistantNavigationIdentity(taiwuId),
                        Title = "灵儿",
                        Subtitle = "助手 · " + assistantHistory.Count + " 条",
                        TaiwuId = taiwuId,
                        IsAssistant = true,
                        SortKey = AssistantHistoryStore.LastActivityUtc(taiwuId).Ticks
                    });
            }
            catch { }

            try
            {
                foreach (GroupChatOrchestrator.GroupSession session in sessions)
                {
                    if (session == null || session.MemberIds == null || session.MemberIds.Count == 0) continue;
                    var roster = new List<KeyValuePair<int, string>>();
                    if (session.Members != null)
                        foreach (GroupChatOrchestrator.Member member in session.Members)
                            if (member != null && member.Id > 0)
                            {
                                string name = ResolveNavigationName(member.Id, member.Name, groupNameHints);
                                if (name == null) unresolvedIds.Add(member.Id);
                                roster.Add(new KeyValuePair<int, string>(
                                    member.Id, name ?? "姓名载入中…"));
                            }
                    if (roster.Count == 0)
                        foreach (int memberId in session.MemberIds)
                            if (memberId > 0)
                            {
                                string name = ResolveNavigationName(memberId, null, groupNameHints);
                                if (name == null) unresolvedIds.Add(memberId);
                                roster.Add(new KeyValuePair<int, string>(
                                    memberId, name ?? "姓名载入中…"));
                            }
                    _persistedNavigation.Add(new SessionNavEntry
                    {
                        Identity = GroupNavigationIdentity(taiwuId, session.GroupId),
                        // 统一从逐成员名单生成标题；旧 Participants 可能混有“编号占位名”
                        // 或人数文案，不能再作为显示真名的权威来源。
                        Title = "群聊 · " + string.Join("、",
                            roster.ConvertAll(x => x.Value).ToArray()),
                        Subtitle = "群聊 · " + session.MemberIds.Count + " 人",
                        TaiwuId = taiwuId,
                        IsGroup = true,
                        GroupId = session.GroupId,
                        Roster = roster,
                        SortKey = session.LastActivityUtc.Ticks
                    });
                }
            }
            catch { }

            ResolveMissingNavigationNames(taiwuId, generation, unresolvedIds);
            if (!singlesIndexed || !groupsIndexed)
                EnsureNavigationIndexRebuild(taiwuId, generation,
                    !singlesIndexed, !groupsIndexed);
        }

        static NavigationIndexRebuildStart EnsureNavigationIndexRebuild(
            int taiwuId, int generation, bool rebuildSingles, bool rebuildGroups,
            bool allowRetry = false, bool probeExisting = false)
        {
            if (_navigationIndexFailureCount >= MaxNavigationIndexFailuresPerWorld)
            {
                if (System.DateTime.UtcNow.Ticks < _navigationIndexRetryNotBeforeUtcTicks)
                    return NavigationIndexRebuildStart.Blocked;
                // The cap prevents a hot loop, not permanent disablement. After a long
                // cooldown allow a fresh bounded cycle so released file locks or repaired
                // storage can recover without forcing the player to leave the world.
                _navigationIndexFailureCount = 0;
            }
            if (taiwuId <= 0 || (!rebuildSingles && !rebuildGroups))
                return NavigationIndexRebuildStart.Noop;
            if (_navigationIndexRebuildTask != null)
                return NavigationIndexRebuildStart.Started;
            if (_navigationIndexFailureCount > 0 && !allowRetry)
                return NavigationIndexRebuildStart.Blocked;
            var host = TalkEntryHost.Instance;
            if (host == null) return NavigationIndexRebuildStart.Blocked;
            uint worldId = JianghuYoulingPaths.CurrentWorldId;
            string directory = JianghuYoulingPaths.ChatLogs;
            if (worldId == 0 || string.IsNullOrWhiteSpace(directory))
                return NavigationIndexRebuildStart.Noop;
            long singleEpoch = rebuildSingles
                ? ConversationSessionIndexStore.CaptureSingleRebuildEpoch(taiwuId) : -1;
            long groupEpoch = rebuildGroups
                ? ConversationSessionIndexStore.CaptureGroupRebuildEpoch(taiwuId) : -1;
            if (rebuildSingles && singleEpoch < 0 || rebuildGroups && groupEpoch < 0)
                return NavigationIndexRebuildStart.Blocked;
            long epoch = unchecked(++_navigationIndexRebuildEpoch);
            Task<NavigationIndexRebuildResult> task = Task.Run(() =>
            {
                bool needSingles = rebuildSingles;
                bool needGroups = rebuildGroups;
                if (probeExisting)
                {
                    if (needSingles && ConversationSessionIndexStore.TryLoadSingles(
                        taiwuId, out _)) needSingles = false;
                    if (needGroups && ConversationSessionIndexStore.TryLoadGroups(
                        taiwuId, out _)) needGroups = false;
                }
                var result = new NavigationIndexRebuildResult
                {
                    RebuildSingles = needSingles,
                    RebuildGroups = needGroups,
                    SingleEpoch = singleEpoch,
                    GroupEpoch = groupEpoch,
                };
                if (needSingles)
                {
                    result.Singles = TalkOrchestrator.BuildConversedPartnerIndexSnapshot(
                        taiwuId, worldId, directory, generation);
                    result.SinglesCommitted = result.Singles != null
                        && ConversationSessionIndexStore.ReplaceSinglesIfRebuildUnchanged(
                            taiwuId, result.Singles, result.SingleEpoch, worldId, directory);
                }
                if (needGroups)
                {
                    result.Groups = new GroupChatOrchestrator().BuildSessionIndexSnapshot(
                        taiwuId, worldId, directory, generation);
                    result.GroupsCommitted = result.Groups != null
                        && ConversationSessionIndexStore.ReplaceGroupsIfRebuildUnchanged(
                            taiwuId, result.Groups, result.GroupEpoch, worldId, directory);
                }
                return result;
            });
            _navigationIndexRebuildTask = task;
            try { host.StartCoroutine(FinishNavigationIndexRebuild(
                task, epoch, worldId, taiwuId, generation)); }
            catch
            {
                _navigationIndexRebuildTask = null;
                task.ContinueWith(t => { var ignored = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted);
                return NavigationIndexRebuildStart.Blocked;
            }
            return NavigationIndexRebuildStart.Started;
        }

        static System.Collections.IEnumerator FinishNavigationIndexRebuild(
            Task<NavigationIndexRebuildResult> task,
            long epoch, uint worldId, int taiwuId, int generation)
        {
            while (task != null && !task.IsCompleted) yield return null;
            if (task == null) yield break;
            bool current = ReferenceEquals(_navigationIndexRebuildTask, task)
                && epoch == _navigationIndexRebuildEpoch;
            if (current) _navigationIndexRebuildTask = null;
            if (task.IsFaulted)
            {
                var ignored = task.Exception;
                if (current)
                {
                    RecordNavigationIndexRebuildFailure();
                    _navigationDirty = true;
                    QueueNavigationIndexRebuildRetry(taiwuId, generation, worldId);
                }
                yield break;
            }
            if (!current || task.IsCanceled
                || JianghuYoulingPaths.CurrentWorldId != worldId
                || !WorldLifecycle.IsSameWorld(generation)
                || taiwuId != ResolveCurrentTaiwuId())
            {
                if (current) _navigationDirty = true;
                yield break;
            }
            NavigationIndexRebuildResult result = task.Result;
            bool committed = result != null
                && (!result.RebuildSingles || result.SinglesCommitted)
                && (!result.RebuildGroups || result.GroupsCommitted);
            _navigationDirty = true;
            if (committed)
            {
                _navigationIndexFailureCount = 0;
                _navigationIndexRetryNotBeforeUtcTicks = 0;
                RefreshTabs();
            }
            else
            {
                RecordNavigationIndexRebuildFailure();
                QueueNavigationIndexRebuildRetry(taiwuId, generation, worldId);
            }
        }

        static void RecordNavigationIndexRebuildFailure()
        {
            _navigationIndexFailureCount = System.Math.Min(
                MaxNavigationIndexFailuresPerWorld,
                _navigationIndexFailureCount + 1);
            if (_navigationIndexFailureCount >= MaxNavigationIndexFailuresPerWorld)
                _navigationIndexRetryNotBeforeUtcTicks = System.DateTime.UtcNow
                    .AddSeconds(NavigationIndexFailureCooldownSeconds).Ticks;
        }

        internal static void PrewarmConversationIndexes(int taiwuId)
        {
            if (taiwuId <= 0 || !WorldLifecycle.HasWorldIdentity) return;
            int generation = WorldLifecycle.Generation;
            NavigationIndexRebuildStart start = EnsureNavigationIndexRebuild(
                taiwuId, generation, true, true, probeExisting: true);
            if (start == NavigationIndexRebuildStart.Blocked)
                QueueNavigationIndexRebuildRetry(taiwuId, generation,
                    JianghuYoulingPaths.CurrentWorldId);
        }

        internal static bool ConversationIndexesReady(int taiwuId)
            => taiwuId > 0
                && ConversationSessionIndexStore.IsSingleCacheReady(taiwuId)
                && ConversationSessionIndexStore.IsGroupCacheReady(taiwuId);

        internal static System.Collections.IEnumerator EnsureConversationIndexesReady(
            int taiwuId, int generation, uint worldId)
        {
            long deadlineUtcTicks = System.DateTime.UtcNow
                .AddSeconds(NavigationIndexFailureCooldownSeconds + 20).Ticks;
            while (System.DateTime.UtcNow.Ticks < deadlineUtcTicks)
            {
                if (taiwuId <= 0 || worldId == 0
                    || JianghuYoulingPaths.CurrentWorldId != worldId
                    || !WorldLifecycle.IsSameWorld(generation)) yield break;
                if (_navigationIndexRebuildTask != null)
                {
                    yield return null;
                    continue;
                }
                if (ConversationIndexesReady(taiwuId)) yield break;
                NavigationIndexRebuildStart start = EnsureNavigationIndexRebuild(
                    taiwuId, generation, true, true, probeExisting: true);
                if (start == NavigationIndexRebuildStart.Blocked)
                    QueueNavigationIndexRebuildRetry(taiwuId, generation, worldId);
                yield return null;
            }
        }

        static void QueueNavigationIndexRebuildRetry(int taiwuId, int generation, uint worldId)
        {
            if (_navigationIndexRetryPending || taiwuId <= 0 || worldId == 0) return;
            var host = TalkEntryHost.Instance;
            if (host == null) return;
            _navigationIndexRetryPending = true;
            try { host.StartCoroutine(RetryNavigationIndexRebuild(
                taiwuId, generation, worldId)); }
            catch { _navigationIndexRetryPending = false; }
        }

        static System.Collections.IEnumerator RetryNavigationIndexRebuild(
            int taiwuId, int generation, uint worldId)
        {
            int observedFailures = _navigationIndexFailureCount;
            while (true)
            {
                if (JianghuYoulingPaths.CurrentWorldId != worldId
                    || !WorldLifecycle.IsSameWorld(generation)
                    || taiwuId != ResolveCurrentTaiwuId())
                {
                    _navigationIndexRetryPending = false;
                    yield break;
                }
                if (_navigationIndexFailureCount >= MaxNavigationIndexFailuresPerWorld)
                {
                    // Keep this coroutine alive across the cooldown.  No RefreshTabs,
                    // click, or other UI event is required to wake the fourth attempt.
                    while (System.DateTime.UtcNow.Ticks < _navigationIndexRetryNotBeforeUtcTicks)
                    {
                        if (JianghuYoulingPaths.CurrentWorldId != worldId
                            || !WorldLifecycle.IsSameWorld(generation)
                            || taiwuId != ResolveCurrentTaiwuId())
                        {
                            _navigationIndexRetryPending = false;
                            yield break;
                        }
                        yield return null;
                    }
                    _navigationIndexFailureCount = 0;
                    _navigationIndexRetryNotBeforeUtcTicks = 0;
                    _navigationDirty = true;
                    observedFailures = 0;
                }
                int delayFrames = NavigationIndexRetryBackoffFrames
                    * System.Math.Max(1, observedFailures);
                while (delayFrames-- > 0)
                {
                    if (JianghuYoulingPaths.CurrentWorldId != worldId
                        || !WorldLifecycle.IsSameWorld(generation)
                        || taiwuId != ResolveCurrentTaiwuId())
                    {
                        _navigationIndexRetryPending = false;
                        yield break;
                    }
                    yield return null;
                }
                if (_navigationIndexRebuildTask != null)
                {
                    int taskWaitFrames = 600;
                    while (_navigationIndexRebuildTask != null && taskWaitFrames-- > 0)
                        yield return null;
                    if (_navigationIndexRebuildTask != null)
                    {
                        _navigationIndexRetryPending = false;
                        yield break;
                    }
                }
                if (ConversationIndexesReady(taiwuId))
                {
                    _navigationIndexRetryPending = false;
                    _navigationDirty = true;
                    RefreshTabs();
                    yield break;
                }
                NavigationIndexRebuildStart start = EnsureNavigationIndexRebuild(
                    taiwuId, generation, true, true,
                    allowRetry: true, probeExisting: true);
                if (start == NavigationIndexRebuildStart.Blocked)
                {
                    // An active transcript mutation is transient. Keep this world-bound
                    // retry alive; disposing the mutation requires no UI event to resume.
                    yield return null;
                    continue;
                }
                if (start == NavigationIndexRebuildStart.Noop) break;
                int retryWaitFrames = 600;
                while (_navigationIndexRebuildTask != null && retryWaitFrames-- > 0)
                    yield return null;
                if (_navigationIndexRebuildTask != null)
                {
                    _navigationIndexRetryPending = false;
                    yield break;
                }
                if (ConversationIndexesReady(taiwuId))
                {
                    _navigationIndexRetryPending = false;
                    _navigationDirty = true;
                    RefreshTabs();
                    yield break;
                }
                if (_navigationIndexFailureCount <= observedFailures)
                {
                    yield return null;
                    continue;
                }
                observedFailures = _navigationIndexFailureCount;
            }
            _navigationIndexRetryPending = false;
        }

        static string ResolveNavigationName(int npcId, string hint,
            IDictionary<int, string> groupNameHints = null)
        {
            if (npcId < 0) return null;
            if (!TalkOrchestrator.IsUnresolvedNpcName(hint))
            {
                string name = hint.Trim();
                _navigationResolvedNames[npcId] = name;
                return name;
            }
            if (_navigationResolvedNames.TryGetValue(npcId, out string cached)
                && !TalkOrchestrator.IsUnresolvedNpcName(cached)) return cached;
            if (groupNameHints != null && groupNameHints.TryGetValue(npcId, out string grouped)
                && !TalkOrchestrator.IsUnresolvedNpcName(grouped))
            {
                grouped = grouped.Trim();
                _navigationResolvedNames[npcId] = grouped;
                return grouped;
            }
            return null;
        }

        static void ResolveMissingNavigationNames(int taiwuId, int generation,
            IEnumerable<int> unresolvedIds)
        {
            if (_navigationNameQueryPending || taiwuId <= 0 || unresolvedIds == null) return;
            var ids = new List<int>();
            foreach (int id in unresolvedIds)
                if (id > 0 && _navigationNameAttempts.Add(id)) ids.Add(id);
            if (ids.Count == 0) return;
            _navigationNameQueryPending = true;
            long requestEpoch = unchecked(++_navigationNameQueryEpoch);
            EffectHandler.QueryCharNames(ids, names =>
            {
                if (requestEpoch != _navigationNameQueryEpoch) return;
                _navigationNameQueryPending = false;
                if (!WorldLifecycle.IsSameWorld(generation)
                    || taiwuId != ResolveCurrentTaiwuId()) return;
                bool changed = false;
                for (int i = 0; i < ids.Count; i++)
                {
                    string name = names != null && i < names.Count ? names[i] : null;
                    if (TalkOrchestrator.IsUnresolvedNpcName(name)) continue;
                    name = name.Trim();
                    _navigationResolvedNames[ids[i]] = name;
                    TalkOrchestrator.TryUpdateExistingConversationNameIfCurrent(
                        taiwuId, ids[i], name, generation);
                    foreach (ChatTab tab in _tabs)
                        if (tab != null) changed |= tab.UpdateKnownNpcName(ids[i], name);
                    changed = true;
                }
                if (!changed) return;
                _navigationDirty = true;
                RefreshTabs();
            });
        }

        static SessionNavEntry NavigationForTab(ChatTab tab)
        {
            if (tab == null) return null;
            string identity;
            int taiwuId = tab.TaiwuId > 0 ? tab.TaiwuId : ResolveCurrentTaiwuId();
            var roster = tab.GroupMode ? tab.GroupRosterSnapshot() : null;
            if (tab.AssistantMode) identity = AssistantNavigationIdentity(taiwuId);
            else if (tab.GroupMode && !string.IsNullOrWhiteSpace(tab.GroupId))
                identity = GroupNavigationIdentity(taiwuId, tab.GroupId);
            else if (tab.GroupMode) identity = "group-live:" + (tab.Key ?? tab.GetHashCode().ToString());
            else if (!tab.GroupMode && tab.NpcId >= 0)
                identity = SingleNavigationIdentity(taiwuId, tab.NpcId);
            else identity = tab.Key ?? ("live:" + tab.GetHashCode());
            string mode = tab.AssistantMode ? "助手"
                : tab.GroupMode ? "群聊 · " + (roster != null ? roster.Count : 0) + " 人"
                : tab.RemoteMode ? "千里传音" : "单聊";
            if (tab.GroupHistoryOnly) mode += " · 仅回看";
            if (tab.Busy) mode += " · 生成中";
            return new SessionNavEntry
            {
                Identity = identity,
                Title = tab.TabTitle,
                Subtitle = mode,
                Tab = tab,
                TaiwuId = taiwuId,
                NpcId = tab.NpcId,
                IsAssistant = tab.AssistantMode,
                IsGroup = tab.GroupMode,
                GroupId = tab.GroupId,
                Roster = roster,
                UnreadCount = !tab.AssistantMode && !tab.GroupMode && tab.NpcId >= 0
                    ? NpcChatUnreadStore.Count(taiwuId, tab.NpcId) : 0,
                IsDead = !tab.AssistantMode && !tab.GroupMode && tab.NpcId >= 0
                    && ArchivedCharacterStatusStore.IsDead(taiwuId, tab.NpcId),
                // 点击/切换只更新运行时 LRU，不改变本值；创建群聊或玩家消息发出时才更新。
                SortKey = tab.NavigationSortKey
            };
        }

        static SessionNavEntry CloneNavigationEntry(SessionNavEntry source)
        {
            return new SessionNavEntry
            {
                Identity = source.Identity,
                Title = source.Title,
                Subtitle = source.Subtitle,
                Tab = source.Tab,
                TaiwuId = source.TaiwuId,
                NpcId = source.NpcId,
                IsAssistant = source.IsAssistant,
                IsGroup = source.IsGroup,
                GroupId = source.GroupId,
                Roster = source.Roster != null
                    ? new List<KeyValuePair<int, string>>(source.Roster) : null,
                SortKey = source.SortKey,
                UnreadCount = source.UnreadCount,
                IsDead = source.IsDead,
            };
        }

        static void UpsertPersistedNavigation(SessionNavEntry live)
        {
            if (live == null || string.IsNullOrWhiteSpace(live.Identity)) return;
            SessionNavEntry persisted = CloneNavigationEntry(live);
            persisted.Tab = null;
            int index = _persistedNavigation.FindIndex(entry => entry != null
                && string.Equals(entry.Identity, live.Identity, System.StringComparison.Ordinal));
            if (index >= 0)
            {
                persisted.SortKey = System.Math.Max(
                    persisted.SortKey, _persistedNavigation[index].SortKey);
                if (live.Tab != null) live.Tab.NavigationSortKey = persisted.SortKey;
                _persistedNavigation[index] = persisted;
            }
            else _persistedNavigation.Add(persisted);
        }

        static string SingleNavigationIdentity(int taiwuId, int npcId)
            => "single:" + taiwuId + ":" + npcId;

        static string AssistantNavigationIdentity(int taiwuId)
            => "assistant:" + taiwuId;

        static string GroupNavigationIdentity(int taiwuId, string groupId)
            => "group:" + taiwuId + ":id:" + (groupId ?? "");

        static HashSet<string> HiddenNavigationIdentities(int taiwuId)
        {
            var hidden = new HashSet<string>(System.StringComparer.Ordinal);
            if (taiwuId <= 0) return hidden;
            try
            {
                ConversationNavigationStore.Snapshot snapshot =
                    ConversationNavigationStore.Load(taiwuId);
                // Never consume the readable subset of a damaged sharded snapshot. The store
                // serves its last complete world-scoped snapshot when one exists; on a cold
                // unreliable load, keep navigation visible instead of applying partial state.
                if (snapshot?.LoadReliable == true && snapshot.Hidden != null)
                    foreach (string identity in snapshot.Hidden)
                        if (!string.IsNullOrWhiteSpace(identity)) hidden.Add(identity);
            }
            catch { }
            return hidden;
        }

        static void RevealNavigationEntry(SessionNavEntry entry)
        {
            if (entry == null) return;
            RevealNavigationIdentity(entry.TaiwuId, entry.Identity);
        }

        static bool RevealNavigationIdentity(int taiwuId, string identity)
        {
            if (taiwuId <= 0 || string.IsNullOrWhiteSpace(identity)) return false;
            var hidden = HiddenNavigationIdentities(taiwuId);
            if (!hidden.Contains(identity)) return true;
            return ConversationNavigationStore.Restore(taiwuId, identity);
        }

        static int ResolveCurrentTaiwuId()
        {
            try { return SingletonObject.getInstance<BasicGameData>().TaiwuCharId; }
            catch { return -1; }
        }

        static void OpenNavigationEntry(SessionNavEntry entry)
        {
            if (entry == null) return;
            if (entry.Tab != null)
            {
                if (entry.IsGroup && entry.Tab.GroupHistoryOnly)
                {
                    OpenGroupCore(entry.Roster ?? new List<KeyValuePair<int, string>>(),
                        entry.TaiwuId, _font, entry.GroupId, false);
                    return;
                }
                Activate(entry.Tab);
                entry.Tab.RestoreInputFocus();
                return;
            }
            if (entry.IsAssistant) OpenAssistant(_font);
            else if (entry.IsGroup)
                OpenGroupCore(entry.Roster ?? new List<KeyValuePair<int, string>>(),
                    entry.TaiwuId, _font, entry.GroupId, false);
            else
                // 持久会话恢复只给一个 fail-closed 初值；页签激活后立即用当前同块、
                // 同道与俘虏权威状态刷新，不沿用旧会话的距离。
                OpenRemote(entry.NpcId, entry.TaiwuId, entry.Title, _font);
        }

        static void HideNavigationEntry(string boundIdentity, SessionNavEntry entry)
        {
            if (entry == null || entry.TaiwuId <= 0 || string.IsNullOrWhiteSpace(entry.Identity))
                return;
            if (!NavigationRowBindingPolicy.IsCurrent(boundIdentity, entry.Identity))
                return;
            if (!ConversationNavigationStore.Hide(entry.TaiwuId, entry.Identity))
            {
                entry.Tab?.AddSysNotice("（隐藏会话失败：状态未能写入磁盘，聊天记录没有变化）");
                return;
            }

            if (entry.Tab != null && ReferenceEquals(entry.Tab, _active))
            {
                entry.Tab.SetRootActive(false);
                SessionNavEntry replacement = null;
                var hidden = HiddenNavigationIdentities(entry.TaiwuId);
                foreach (SessionNavEntry candidate in BuildNavigationEntries())
                    if (candidate != null && candidate.Identity != entry.Identity
                        && !hidden.Contains(candidate.Identity))
                    {
                        replacement = candidate;
                        break;
                    }
                if (replacement != null) OpenNavigationEntry(replacement);
                else
                {
                    // No visible conversation remains. Keep the sidebar recovery entry available,
                    // but clear the active pointer so overlay/minimize restoration cannot reopen the
                    // just-hidden right panel behind the player's back.
                    _active = null;
                    if (_barRoot != null) _barRoot.SetActive(false);
                }
            }
            RefreshTabs();
        }

        static SidebarConversationRow AddSessionRow(SessionNavEntry entry)
        {
            if (entry == null) return null;
            var go = new GameObject("Conversation", typeof(RectTransform), typeof(Image),
                typeof(LayoutElement), typeof(Button));
            go.transform.SetParent(_barRow, false);
            var row = go.AddComponent<SidebarConversationRow>();
            row.identity = entry.Identity;
            row.background = go.GetComponent<Image>();
            var le = go.GetComponent<LayoutElement>();
            le.preferredHeight = 58f; le.minHeight = 58f; le.flexibleHeight = 0f;
            var btn = go.GetComponent<Button>();
            btn.transition = Selectable.Transition.ColorTint;
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.25f, 0.30f, 0.27f, 1f);
            colors.pressedColor = new Color(0.16f, 0.21f, 0.18f, 1f);
            btn.colors = colors;
            var openCapture = go.AddComponent<SidebarConversationRowActionCapture>();
            openCapture.row = row;
            openCapture.action = NavigationRowAction.Open;
            btn.onClick.AddListener(() =>
            {
                if (row.TryConsumeAction(NavigationRowAction.Open, out _, out SessionNavEntry current))
                    OpenNavigationEntry(current);
            });

            // 使用游戏字体稳定支持的汉字标记，避免圆点等字形显示成「口」。
            var title = ChipText("Name", "", go.transform,
                new Color(0.85f, 0.87f, 0.82f, 1f),
                17f, TextAlignmentOptions.Left);
            title.raycastTarget = false;
            title.rectTransform.anchorMin = new Vector2(0, 0.43f);
            title.rectTransform.anchorMax = Vector2.one;
            title.rectTransform.offsetMin = new Vector2(12, 0);
            title.rectTransform.offsetMax = new Vector2(-62, -3);

            var subtitle = ChipText("Summary", "", go.transform,
                new Color(0.55f, 0.61f, 0.57f, 1f), 13f, TextAlignmentOptions.Left);
            subtitle.raycastTarget = false;
            subtitle.rectTransform.anchorMin = Vector2.zero;
            subtitle.rectTransform.anchorMax = new Vector2(1, 0.43f);
            subtitle.rectTransform.offsetMin = new Vector2(12, 3);
            subtitle.rectTransform.offsetMax = new Vector2(-62, 0);

            var action = new GameObject("Hide", typeof(RectTransform), typeof(Image), typeof(Button));
            action.transform.SetParent(go.transform, false);
            var actionRt = action.GetComponent<RectTransform>();
            actionRt.anchorMin = actionRt.anchorMax = new Vector2(1f, 0.5f);
            actionRt.pivot = new Vector2(1f, 0.5f);
            actionRt.anchoredPosition = new Vector2(-8f, 0f);
            actionRt.sizeDelta = new Vector2(46f, 28f);
            action.GetComponent<Image>().color = new Color(0.22f, 0.26f, 0.23f, 0.96f);
            var actionButton = action.GetComponent<Button>();
            actionButton.transition = Selectable.Transition.ColorTint;
            var actionColors = actionButton.colors;
            actionColors.normalColor = Color.white;
            actionColors.highlightedColor = new Color(1.15f, 1.12f, 1.04f, 1f);
            actionColors.pressedColor = new Color(0.82f, 0.89f, 0.84f, 1f);
            actionButton.colors = actionColors;
            var hideCapture = action.AddComponent<SidebarConversationRowActionCapture>();
            hideCapture.row = row;
            hideCapture.action = NavigationRowAction.Hide;
            actionButton.onClick.AddListener(() =>
            {
                if (row.TryConsumeAction(NavigationRowAction.Hide,
                    out string capturedIdentity, out SessionNavEntry current))
                    HideNavigationEntry(capturedIdentity, current);
            });
            var actionLabel = ChipText("Label", "隐藏", action.transform,
                new Color(0.70f, 0.75f, 0.70f, 1f), 13f, TextAlignmentOptions.Center);
            actionLabel.rectTransform.anchorMin = Vector2.zero;
            actionLabel.rectTransform.anchorMax = Vector2.one;
            actionLabel.rectTransform.offsetMin = Vector2.zero;
            actionLabel.rectTransform.offsetMax = Vector2.zero;
            actionLabel.raycastTarget = false;
            var hover = go.AddComponent<SidebarConversationRowHover>();
            hover.action = action;
            row.title = title;
            row.subtitle = subtitle;
            row.action = action;
            row.hover = hover;
            BindSessionRow(row, entry);
            return row;
        }

        static void BindSessionRow(SidebarConversationRow row, SessionNavEntry entry)
        {
            if (row == null || entry == null) return;
            bool isActive = entry.Tab != null && ReferenceEquals(entry.Tab, _active);
            bool rebound = !string.Equals(row.identity, entry.Identity,
                System.StringComparison.Ordinal);
            if (rebound) row.hover?.ResetInteraction();
            row.binding.Bind(entry.Identity);
            row.identity = entry.Identity;
            row.entry = entry;
            if (row.background != null)
                row.background.color = isActive
                    ? new Color(0.19f, 0.25f, 0.22f, 0.99f)
                    : new Color(0.105f, 0.12f, 0.112f, 0.96f);
            if (row.title != null)
            {
                bool unread = !isActive && (entry.UnreadCount > 0
                    || entry.Tab != null && entry.Tab.Unread);
                string prefix = entry.IsDead ? "已故 · " : (unread ? "新 · " : "");
                row.title.text = prefix + Trunc(entry.Title, 13);
                row.title.color = entry.IsDead
                    ? new Color(0.68f, 0.68f, 0.62f, 1f)
                    : isActive
                    ? new Color(0.98f, 0.95f, 0.86f, 1f)
                    : new Color(0.85f, 0.87f, 0.82f, 1f);
            }
            if (row.subtitle != null)
            {
                string subtitle = entry.IsDead
                    ? "灵魂状态 · " + entry.Subtitle
                    : entry.UnreadCount > 0 && !isActive
                    ? "未读 " + entry.UnreadCount + " 条 · " + entry.Subtitle
                    : entry.Subtitle;
                row.subtitle.text = Trunc(subtitle, 24);
            }
            if (row.hover != null)
            {
                row.hover.keepVisible = isActive;
                row.hover.RefreshVisibility();
            }
        }

        static bool TryPromoteNavigationRow(ChatTab tab)
        {
            if (tab == null || _barRow == null) return false;
            SessionNavEntry entry = NavigationForTab(tab);
            if (entry == null || string.IsNullOrWhiteSpace(entry.Identity)) return false;
            return PromoteVisibleNavigation(entry);
        }

        static TextMeshProUGUI ChipText(string name, string s, Transform parent, Color col, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            UiFontSizeStore.Bind(t, size); t.color = col; t.alignment = align;
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Ellipsis;
            t.text = s;
            return t;
        }

        static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }

    internal sealed class SidebarConversationRow : MonoBehaviour
    {
        public readonly NavigationRowBindingGuard binding = new NavigationRowBindingGuard();
        public string identity;
        public ChatWindow.SessionNavEntry entry;
        public Image background;
        public TextMeshProUGUI title;
        public TextMeshProUGUI subtitle;
        public GameObject action;
        public SidebarConversationRowHover hover;

        public void CapturePointerDown(NavigationRowAction action)
            => binding.CapturePointerDown(action);

        public bool TryConsumeAction(NavigationRowAction action, out string capturedIdentity,
            out ChatWindow.SessionNavEntry currentEntry)
        {
            bool current = binding.TryConsume(action, out capturedIdentity)
                && entry != null
                && NavigationRowBindingPolicy.IsCurrent(capturedIdentity, entry.Identity);
            currentEntry = current ? entry : null;
            return current;
        }

        public void ClearBinding()
        {
            binding.Clear();
            identity = null;
            entry = null;
            if (hover != null)
            {
                hover.keepVisible = false;
                hover.ResetInteraction();
            }
            else if (action != null) action.SetActive(false);
        }
    }

    /// <summary>Captures a pooled row's binding on pointer-down before Button.onClick dispatch.</summary>
    internal sealed class SidebarConversationRowActionCapture : MonoBehaviour, IPointerDownHandler
    {
        public SidebarConversationRow row;
        public NavigationRowAction action;

        public void OnPointerDown(PointerEventData eventData)
            => row?.CapturePointerDown(action);
    }

    /// <summary>左侧会话导航跟随右侧聊天面板移动，保持微信式左右两栏为一个整体。</summary>
    internal sealed class TabBarFollower : MonoBehaviour
    {
        public RectTransform sidebar;
        void LateUpdate()
        {
            if (sidebar == null) return;
            var p = ChatWindow.ActivePanelRt;
            if (p != null)
            {
                float width = p.rect.width > 1f ? p.rect.width : ChatTab.PanelWidth;
                float height = p.rect.height > 1f ? p.rect.height : ChatTab.PanelHeight;
                var nextSize = new Vector2(ChatTab.SidebarWidth, height);
                var nextPosition = p.anchoredPosition
                    + new Vector2(-(width + ChatTab.SidebarWidth) * 0.5f - ChatTab.SidebarGap, 0f);
                if ((sidebar.sizeDelta - nextSize).sqrMagnitude > 0.01f)
                    sidebar.sizeDelta = nextSize;
                if ((sidebar.anchoredPosition - nextPosition).sqrMagnitude > 0.01f)
                    sidebar.anchoredPosition = nextPosition;
            }
        }
    }

    /// <summary>左栏滚动条只在鼠标停留于会话导航时出现；隐藏时滚轮仍由 ScrollRect 正常处理。</summary>
    internal sealed class SidebarHoverScrollbar : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public ScrollRect scroll;
        public Scrollbar scrollbar;
        bool _hovering;
        CanvasGroup _visibility;
        bool _hasVisibilityState;
        bool _lastVisible;

        public void HideNow()
        {
            _hovering = false;
            SetVisible(false);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovering = true;
            RefreshVisibility();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovering = false;
            SetVisible(false);
        }

        void OnDisable() => HideNow();

        void LateUpdate()
        {
            // Child rows/buttons consume pointer events in Unity's event hierarchy, so relying
            // on the sidebar parent receiving OnPointerEnter left the scrollbar permanently
            // visible on some game UI stacks. Geometry polling is authoritative and covers the
            // normal list, the hidden-conversation list and their child controls alike.
            var rect = transform as RectTransform;
            _hovering = rect != null && RectTransformUtility.RectangleContainsScreenPoint(
                rect, Input.mousePosition, null);
            RefreshVisibility();
        }

        void RefreshVisibility()
        {
            bool overflow = scroll != null && scroll.content != null && scroll.viewport != null
                && scroll.content.rect.height > scroll.viewport.rect.height + 0.5f;
            SetVisible(_hovering && overflow);
        }

        void SetVisible(bool visible)
        {
            if (scrollbar == null) return;
            if (_visibility == null)
                _visibility = scrollbar.gameObject.GetComponent<CanvasGroup>()
                    ?? scrollbar.gameObject.AddComponent<CanvasGroup>();
            if (_hasVisibilityState && _lastVisible == visible) return;
            _hasVisibilityState = true;
            _lastVisible = visible;
            // Keep the Scrollbar object active so ScrollRect cannot reactivate it behind this
            // policy. Alpha and raycast gating make it genuinely absent outside the left panel.
            _visibility.alpha = visible ? 1f : 0f;
            _visibility.interactable = visible;
            _visibility.blocksRaycasts = visible;
        }
    }

    /// <summary>Keep the reversible hide action quiet until a conversation row is active or hovered.</summary>
    internal sealed class SidebarConversationRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public GameObject action;
        public bool keepVisible;
        bool _hovering;

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovering = true;
            RefreshVisibility();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovering = false;
            RefreshVisibility();
        }

        public void ResetInteraction()
        {
            _hovering = false;
            RefreshVisibility();
        }

        void OnDisable()
        {
            keepVisible = false;
            _hovering = false;
            if (action != null) action.SetActive(false);
        }

        public void RefreshVisibility()
        {
            if (action != null) action.SetActive(keepVisible || _hovering);
        }
    }
}
