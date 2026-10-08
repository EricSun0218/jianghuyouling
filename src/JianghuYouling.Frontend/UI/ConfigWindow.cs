using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Persona;
using JianghuYouling.Core.Prompt;
using JianghuYouling.Core.Web;

namespace JianghuYouling
{
    /// <summary>
    /// 游戏内统一设置窗(带左侧导航):接口 / 对话 / 世界书 / 人设 四页。
    /// 所有配置一律在此填写——mod 管理页不再暴露任何设置项。接口写 Settings/llm.json;
    /// 难度/篇幅/流式存各自 Store;世界书按太吾、人设按 NPC 持久化。世界书需太吾上下文、人设需 NPC 上下文(从对话中打开本窗即带上)。
    /// </summary>
    public static class ConfigWindow
    {
        const float PanelWidth = 1040f;
        const float PanelHeight = 880f;
        static GameObject _root;
        static TextMeshProUGUI _title, _status, _presetSelectionHint;
        static TMP_InputField _baseUrl, _apiKey, _model, _bgModel, _maxTokens, _contextWindow, _profileNameInput, _voiceInput, _wbInput = null, _personaInput, _asstNameInput, _asstFacePathInput, _asstPersonaInput, _ttsBase, _ttsKey, _ttsModel, _imageProvider, _imageEndpoint, _imageKey, _imageModel, _imageSize, _imageWorkflowPath, _imageStyle, _companionMonthlyCountInput;
        static GameObject _asstPersonaInputGo;
        static Button _saveBtn, _testBtn, _profilePrevBtn, _profileNextBtn, _profileActivateBtn, _profileSaveBtn, _profileDeleteBtn, _diffBtn, _streamBtn, _replyLenBtn, _fontSizeBtn, _ghostwriteLenBtn, _ghostwriteLearningBtn, _thinkBtn, _aiEventBtn, _companionMonthlyBtn, _monthlyPopupBtn, _monthlyCandidateManagerBtn, _wbStructuredBtn = null, _wbResetBtn = null, _personaResetBtn, _asstEnabledBtn, _asstFreqBtn, _asstCommissionIntervalBtn, _asstFaceResetBtn, _groupRoundsBtn, _npcProactiveEnabledBtn, _npcProactiveFreqBtn, _imageWatermarkBtn;
        static Button _thinkingPromptBtn;
        static Button _ttsDialogueOnlyBtn;
        static Button _economyPresetBtn, _balancedPresetBtn, _bestExperiencePresetBtn;
        static GameObject[] _panes;
        static Image[] _navImgs;
        static GameObject _personaInputGo, _wbInputGo = null;
        static TextMeshProUGUI _personaTitle, _personaHint, _wbHint = null, _profileSelectionLabel;
        static readonly List<string> _profileNames = new List<string>();
        static int _profileIndex = -1;
        static string _pendingProfileDelete;
        static string _wbLoadedEffective = "", _personaLoadedEffective = "";
        static bool _wbLoaded, _personaLoaded, _wbLoading, _personaLoading, _savingDocuments;
        static string _difficulty = "均衡";
        static int _replyLen = 1;   // 回复篇幅:0简短/1适中/2详细/3不限
        static int _fontSize = UiFontSizeStore.Default; // 全部 Mod 界面:0小(默认)/1中/2大
        static int _ghostwriteLen = GhostwriteLengthStore.Default; // 代笔篇幅:0简短/1适中/2详细
        static bool _ghostwriteLearning = true; // 默认开：从当前存档玩家真实发言中即时学习
        static int _groupRounds = 1;   // 群聊轮数上限 1~3
        static bool _stream = true;
        static bool _showThink = true;   // 默认开:展示思量
        static bool _aiEvent = true;      // 默认开:每月 AI 江湖事件
        static bool _companionMonthly = true; // 默认开:同道基于记忆过月主动行事
        static bool _monthlyPopup = false; // 默认关:需要时从右侧「过月纪事」查看；玩家可自行开启自动打开
        static int _companionMonthlyCount = CompanionMonthlyStore.DefaultCount;
        static bool _asstEnabled = true;  // 悬浮助手「灵儿」总开关
        static int _asstFreq = AssistantProactiveStore.Low; // 助手主动消息频率 0关/1低/2中/3高
        static int _asstCommissionInterval = AssistantCommissionIntervalStore.Default;
        static bool _npcProactiveEnabled = true;
        static int _npcProactiveFreq = NpcProactiveChatStore.DefaultFrequency;
        static bool _imageWatermark;
        static bool _ttsMigratingLegacyDefault;
        static bool _ttsMigratingCrossProviderKey;
        static bool _ttsDialogueOnly;
        static TMP_FontAsset _font;
        static bool _testing;
        static CancellationTokenSource _testCancellation;
        static long _lifecycleVersion;
        static long _activeDocumentSaveToken;
        static long _activeDocumentLoadToken;
        static int _activePaneIndex;
        static int _taiwuId = -1, _npcId = -1;
        static string _npcName;

        const string DocumentOperationOwner = "config-window";

        const string DefaultBaseUrl = "https://openspeech.bytedance.com/api/v3/tts/unidirectional";   // 语音默认接口:火山 Seed-TTS 2.0
        const string DefaultLlmBaseUrl = "https://api.deepseek.com";   // 对话默认接口:官方 DeepSeek
        const string DefaultChatPath = "/chat/completions";
        const string DefaultModel = "deepseek-v4-pro";       // 主对话默认模型:DeepSeek 强模型,新用户只需补 key
        const string DefaultBgModel = "deepseek-v4-flash";   // 后台默认模型:只做画像/记忆精排/代笔等无副作用辅助任务
        const string DefaultTtsModel = "seed-tts-2.0";
        const string DefaultMaxTokensText = "32768";          // DeepSeek V4 官方 Agent 推荐量级；思考与正文共用此预算
        const string RecommendedContextWindowText = "1000000"; // DeepSeek V4 官方标准上下文窗口
        const int MaxWorldBook = WorldBookStore.MaxCustomWorldBookChars;
        const int MaxPersona = PersonaStore.MaxCustomPersonaChars;

        static readonly Color NavOff = new Color(0.18f, 0.21f, 0.20f, 0.95f);
        static readonly Color NavOn = new Color(0.34f, 0.46f, 0.43f, 1f);
        /// <summary>打开设置窗。taiwuId/npcId 可选:有则可编辑世界书/人设;从对话中打开会自动带上。</summary>
        public static void Open(TMP_FontAsset font, int taiwuId = -1, int npcId = -1, string npcName = null)
        {
            CancelActiveTest();
            unchecked { _lifecycleVersion++; }
            if (font != null) _font = font;
            if (_font == null) _font = ResolveFont();   // 兜底:从灵儿挂件/名签取不到字体(传 null)时满场借一个,否则所有控件文字无字体=全空白
            if (_root == null) Build();
            _root.SetActive(true);
            ArmFirstClickSelectAll(_personaInput);
            if (_savingDocuments)
            {
                // The worker owns the original editor context until its transaction commits or
                // rolls back. Reopening must not reset the owner flag or prefill over that draft.
                SetEditorInteractable(false);
                SetButtons(false);
                SetStatus("上一项长文本保存仍在后台完成；当前表单会保留，完成后即可继续编辑。", false);
                return;
            }
            _taiwuId = taiwuId > 0 ? taiwuId : ResolveTaiwuId();
            _npcId = npcId; _npcName = npcName;
            _wbLoaded = false; _personaLoaded = false;
            _wbLoading = false; _personaLoading = false;
            // A newly opened editor has no comparison baseline until its pane finishes
            // lazy-loading.  Keeping the previous NPC/world baseline here let the second
            // phase of an unrelated persona save interpret the still-blank worldbook box
            // as an intentional deletion and roll the whole transaction back.
            _wbLoadedEffective = null; _personaLoadedEffective = null;
            Prefill();
            if (LongTextEditorOperationGate.IsBusy)
            {
                SetEditorInteractable(false);
                SetButtons(false);
                ShowPane(0);
                SetStatus("上一世界或另一个编辑窗的保存仍在收尾；当前设置只读，请稍候关闭后重开。", false);
                return;
            }
            SetEditorInteractable(true);
            SetButtons(true);   // a stale long-text task from the previous window cannot leave Save disabled
            ShowPane(0);
            SetStatus("在此统一配置:接口 / 对话(难度·篇幅·流式) / 世界书 / 人设。", false);
        }

        public static void OpenImageSettings(TMP_FontAsset font, int taiwuId = -1, int npcId = -1, string npcName = null)
        {
            Open(font, taiwuId, npcId, npcName);
            if (_root != null && _root.activeSelf) ShowPane(3);
        }

        public static void Hide()
        {
            unchecked { _lifecycleVersion++; }
            CancelActiveTest();
            ThinkingPromptEditorWindow.Hide();
            if (_root != null) _root.SetActive(false);
        }

        public static void ResetForWorldExit()
        {
            Hide();
            // A read holds the same shared lease as a write.  Do not release it from the world
            // callback while its worker may still be reading the old path; only abandon this
            // window's UI token and let that exact coroutine release its own lease.
            _activeDocumentLoadToken = 0;
            if (_savingDocuments)
            {
                // Invalidate only the UI owner. The shared lease stays held until the worker
                // rolls its captured old-world transaction back and releases it; therefore a
                // new world's editor can never be overwritten by the old completion.
                _activeDocumentSaveToken = 0;
                _savingDocuments = false;
            }
            _taiwuId = -1; _npcId = -1; _npcName = null;
            _wbLoadedEffective = ""; _personaLoadedEffective = "";
            _wbLoaded = false; _personaLoaded = false;
            _wbLoading = false; _personaLoading = false;
            if (_wbInput != null) _wbInput.text = "";
            if (_personaInput != null) _personaInput.text = "";
        }

        static void CancelActiveTest()
        {
            var cancellation = _testCancellation;
            _testCancellation = null;
            _testing = false;
            try { cancellation?.Cancel(); } catch { }
            SetButtons(true);
        }

        // 借游戏中文字体:调用方没传字体(灵儿挂件、或名签布局取到 null)时满场找任一已加载 TMP 字体借用,
        // 否则所有控件文字走 TMP「无字体」最坏路径——渲染成全空白(像没控件)。仿 ChatWindow.ResolveFont。
        static TMP_FontAsset ResolveFont()
        {
            if (_font != null) return _font;
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                foreach (var t in texts)
                    if (t != null && t.font != null && t.gameObject.scene.IsValid()) { _font = t.font; break; }
                if (_font == null)
                    foreach (var t in texts)
                        if (t != null && t.font != null) { _font = t.font; break; }
            }
            catch { }

            return _font;
        }

        public static void Toggle(TMP_FontAsset font)
        {
            if (_root != null && _root.activeSelf) Hide(); else Open(font);
        }

        static int ResolveTaiwuId()
        {
            try { return SingletonObject.getInstance<BasicGameData>().TaiwuCharId; } catch { return -1; }
        }

        static void ReloadConversationTextRuntimeFromDisk()
        {
            TalkOrchestrator.TaiwuVoice = TaiwuVoiceStore.Load();
        }

        static void Prefill()
        {
            JObject o = LlmService.ReadRaw();
            _baseUrl.text = Str(o, "baseUrl", DefaultLlmBaseUrl);
            _apiKey.text = Str(o, "apiKey", "");
            _model.text = Str(o, "model", DefaultModel);          // 新用户默认 deepseek-v4-pro,只需补 key
            if (_bgModel != null) _bgModel.text = Str(o, "bgModel", DefaultBgModel);   // 后台模型默认 deepseek-v4-flash;留空=同主模型
            if (_maxTokens != null)
            {
                string configuredMaxTokens = LlmService.ReadConfiguredMaxTokens(o);
                _maxTokens.text = string.IsNullOrWhiteSpace(configuredMaxTokens)
                    ? DefaultMaxTokensText : configuredMaxTokens.Trim();
            }
            if (_contextWindow != null)
            {
                // 新配置展示 1M 默认值；显式留空也由运行层采用同一个 1M 默认预算。
                JToken configuredContext = o?["contextWindow"];
                string effectiveConfiguredContext = LlmService.ReadConfiguredContextWindow(o);
                _contextWindow.text = configuredContext == null
                    ? RecommendedContextWindowText
                    : (effectiveConfiguredContext ?? "").Trim();
            }
            RefreshLlmProfiles();
            // 语音(可选):未配过则默认填好火山 Seed Audio。旧版恰好仍是内置 MiniMax
            // 默认值时也展示新默认，但绝不把旧服务商的密钥带给火山接口。
            _ttsMigratingLegacyDefault = false;
            _ttsMigratingCrossProviderKey = false;
            _ttsDialogueOnly = false;
            try
            {
                TtsSettings ttsSettings = TtsSettings.Load();
                _ttsDialogueOnly = ttsSettings.DialogueOnly;
                var (vp, vb, vk) = TtsConfig.LoadRaw();
                string tmodel = ttsSettings.Model ?? "";
                _ttsMigratingCrossProviderKey = IsLegacyMiniMaxDefault(vp, vb, tmodel);
                _ttsMigratingLegacyDefault = _ttsMigratingCrossProviderKey
                    || IsLegacySeedAudioDefault(vp, vb, tmodel);
                if (_ttsBase != null) _ttsBase.text = _ttsMigratingLegacyDefault || string.IsNullOrWhiteSpace(vb)
                    ? DefaultBaseUrl : vb;
                if (_ttsKey != null) _ttsKey.text = _ttsMigratingCrossProviderKey ? "" : (vk ?? "");
                if (_ttsModel != null) _ttsModel.text = _ttsMigratingLegacyDefault || string.IsNullOrWhiteSpace(tmodel)
                    ? DefaultTtsModel : tmodel;
            }
            catch { }

            SetTtsDialogueOnlyLabel();

            try
            {
                ImageGenerationConfig.Values image = ImageGenerationConfig.LoadRaw();
                if (_imageProvider != null) _imageProvider.text = image.Provider;
                if (_imageEndpoint != null) _imageEndpoint.text = image.Endpoint;
                if (_imageKey != null) _imageKey.text = image.ApiKey;
                if (_imageModel != null) _imageModel.text = image.Model;
                if (_imageSize != null) _imageSize.text = image.Size;
                if (_imageWorkflowPath != null) _imageWorkflowPath.text = image.WorkflowPath;
                if (_imageStyle != null) _imageStyle.text = image.Style;
                _imageWatermark = image.Watermark;
                SetImageWatermarkLabel();
            }
            catch { }

            _difficulty = DifficultyStore.Load();
            TalkOrchestrator.Difficulty = _difficulty;
            _replyLen = ReplyLenStore.Load();
            TalkOrchestrator.ReplyLength = _replyLen;
            _fontSize = UiFontSizeStore.Current;
            _ghostwriteLen = GhostwriteLengthStore.Load();
            _ghostwriteLearning = GhostwriteSelfLearningStore.Load();
            _groupRounds = GroupRoundsStore.Load(); SetGroupRoundsLabel();
            SetReplyLenLabel();
            SetFontSizeLabel();
            SetGhostwriteLenLabel();
            SetGhostwriteLearningLabel();
            SetDiffLabel();

            ReloadConversationTextRuntimeFromDisk();
            if (_voiceInput != null) _voiceInput.text = TalkOrchestrator.TaiwuVoice;

            _stream = StreamStore.Load();
            TalkOrchestrator.StreamingEnabled = _stream;
            SetStreamLabel();

            _showThink = ThinkingStore.Load();
            ChatWindow.ShowThinking = _showThink;
            SetThinkLabel();

            _aiEvent = AiEventStore.Load();
            _companionMonthly = CompanionMonthlyStore.Load(); SetCompanionMonthlyLabel();
            _monthlyPopup = MonthlyDigestPopupStore.Load(); SetMonthlyPopupLabel();
            _companionMonthlyCount = CompanionMonthlyStore.LoadCount();
            if (_companionMonthlyCountInput != null)
                _companionMonthlyCountInput.text = _companionMonthlyCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SetAiEventLabel();
            _asstEnabled = AssistantEnabledStore.Load(); SetAsstEnabledLabel();
            _asstFreq = AssistantProactiveStore.Load(); SetAsstFreqLabel();
            _asstCommissionInterval = AssistantCommissionIntervalStore.Load();
            SetAsstCommissionIntervalLabel();
            _npcProactiveEnabled = NpcProactiveChatStore.LoadEnabled(); SetNpcProactiveEnabledLabel();
            _npcProactiveFreq = NpcProactiveChatStore.LoadFrequency(); SetNpcProactiveFreqLabel();
            RefreshPresetSelectionHint();
            if (_asstNameInput != null) _asstNameInput.text = AssistantNameStore.Load();
            if (_asstFacePathInput != null) _asstFacePathInput.text = AssistantFacePathStore.Load();
            if (_asstPersonaInput != null) _asstPersonaInput.text = AssistantPersonaStore.Load();

            // 世界书:需太吾上下文
            bool hasTaiwu = _taiwuId > 0;
            if (_wbInputGo != null) _wbInputGo.SetActive(hasTaiwu);
            if (_wbHint != null) _wbHint.gameObject.SetActive(!hasTaiwu);
            if (_wbStructuredBtn != null) _wbStructuredBtn.gameObject.SetActive(hasTaiwu);
            if (_wbStructuredBtn != null) _wbStructuredBtn.interactable = true;
            if (_wbResetBtn != null) _wbResetBtn.gameObject.SetActive(hasTaiwu);
            if (_wbResetBtn != null) _wbResetBtn.interactable = true;
            if (hasTaiwu && _wbInput != null)
            {
                _wbInput.text = "";
            }

            // 人设:需 NPC 上下文。任何 NPC 都可自定义人设(最高权威,覆盖蒸馏画像)。
            bool hasNpc = _npcId >= 0 && _taiwuId > 0;
            if (!hasNpc)
            {
                if (_personaInputGo != null) _personaInputGo.SetActive(false);
                if (_personaHint != null) { _personaHint.gameObject.SetActive(true); _personaHint.text = "（人设按 NPC 设定。请从与某位 NPC 的对话窗口点「设置」打开本页。）"; }
                if (_personaTitle != null) _personaTitle.text = "自定义人设";
                if (_personaResetBtn != null) _personaResetBtn.gameObject.SetActive(false);
            }
            else
            {
                string nm = _npcName ?? ("#" + _npcId);
                if (_personaTitle != null) _personaTitle.text = "为「" + nm + "」设定人设";
                if (_personaHint != null) _personaHint.gameObject.SetActive(false);
                if (_personaResetBtn != null) _personaResetBtn.gameObject.SetActive(true);
                if (_personaResetBtn != null) _personaResetBtn.interactable = true;
                if (_personaInputGo != null) _personaInputGo.SetActive(true);
                if (_personaInput != null)
                {
                    _personaInput.text = "";
                }
            }
        }

        static string Str(JObject o, string key, string fallback)
        {
            string v = o?[key]?.ToString();
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        static bool SameText(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), System.StringComparison.Ordinal);
        }

        static string EffectiveWorldBookText(int taiwuId)
        {
            // JHYL_WORLDBOOK_EFFECTIVE_EDITOR: the settings editor shows the effective default/custom document.
            return WorldBookStore.EffectiveWorldBookText(taiwuId);
        }

        static string EffectivePersonaText(int taiwuId, int npcId)
        {
            // JHYL_PERSONA_EFFECTIVE_EDITOR: show custom persona if present, otherwise the generated portrait/default note.
            string custom = PersonaStore.Load(JianghuYoulingPaths.Personas, taiwuId.ToString(), npcId.ToString());
            if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
            string portrait = PortraitStore.GetPortrait(taiwuId, npcId);
            if (!string.IsNullOrWhiteSpace(portrait)
                && !PortraitDistiller.IsCustomPersonaAppendLayer(portrait)) return portrait.Trim();
            return "（默认人设由该角色的身份、关系、经历、特性和自动画像生成。首次对话生成画像后,这里会显示自动画像；你也可以直接改写本框内容并保存。）";
        }

        static bool SaveEffectivePersona(int taiwuId, int npcId, string text)
            => SaveEffectivePersona(JianghuYoulingPaths.Personas, taiwuId, npcId, text,
                PortraitStore.GetPortrait(taiwuId, npcId));

        static bool SaveEffectivePersona(string personasDirectory, int taiwuId, int npcId, string text,
            string capturedPortrait)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            if (!string.IsNullOrWhiteSpace(capturedPortrait) && SameText(trimmed, capturedPortrait))
            {
                return PersonaStore.Save(personasDirectory, taiwuId.ToString(), npcId.ToString(), "", "replace");
            }
            if (trimmed.StartsWith("（默认人设由该角色的身份", System.StringComparison.Ordinal)) return false;
            return PersonaStore.Save(personasDirectory, taiwuId.ToString(), npcId.ToString(), trimmed, "replace");
        }

        static void ShowPane(int idx)
        {
            if (_panes == null) return;
            _activePaneIndex = idx;
            for (int i = 0; i < _panes.Length; i++) if (_panes[i] != null) _panes[i].SetActive(i == idx);
            if (_navImgs != null) for (int i = 0; i < _navImgs.Length; i++) if (_navImgs[i] != null) _navImgs[i].color = (i == idx) ? NavOn : NavOff;
            if (_testBtn != null) _testBtn.gameObject.SetActive(idx == 0);   // 「测试接口」与模型连接配置同页显示
            if (idx == 7) EnsurePersonaLoaded();
        }

        static void EnsureWorldBookLoaded()
        {
            if (_wbLoaded || _wbLoading || _taiwuId <= 0 || _wbInput == null) return;
            if (!TryBeginDocumentLoad(out long operationToken))
            {
                SetStatus("另一个世界书、人设或设置操作仍在进行，请稍候再打开世界书页。", true);
                return;
            }
            _wbLoading = true;
            SetButtons(false);
            _wbInput.text = "读取世界书中……";
            int taiwuId = _taiwuId;
            long version = _lifecycleVersion;
            int worldGeneration = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            var host = ConfigHost.Instance;
            if (host == null)
            {
                FinishOwnedDocumentLoad(operationToken);
                _wbLoading = false; _wbInput.text = ""; SetButtons(true); return;
            }
            string personasDirectory = JianghuYoulingPaths.Personas;
            host.StartCoroutine(LoadWorldBookCo(personasDirectory, taiwuId, version,
                worldGeneration, worldId, operationToken));
        }

        static IEnumerator LoadWorldBookCo(string personasDirectory, int taiwuId, long version,
            int worldGeneration, uint worldId, long operationToken)
        {
            Task<string> task = Task.Run(() =>
                WorldBookStore.EffectiveWorldBookTextFromDirectory(personasDirectory, taiwuId));
            while (!task.IsCompleted) yield return null;
            if (!DocumentLoadMayUpdateUi(version, worldGeneration, worldId, operationToken)
                || _taiwuId != taiwuId)
            {
                AbandonDocumentLoad(operationToken);
                yield break;
            }
            if (!FinishOwnedDocumentLoad(operationToken)) yield break;
            if (task.IsFaulted)
            {
                _wbLoading = false; _wbLoaded = false; _wbInput.text = "";
                SetButtons(true);
                SetStatus("世界书读取失败，请重试。", true); yield break;
            }
            _wbLoading = false; _wbLoaded = true;
            _wbLoadedEffective = task.Result ?? "";
            _wbInput.text = _wbLoadedEffective;
            MoveInputToEnd(_wbInput);
            SetButtons(true);
            EnsureActiveLongTextLoaded();
        }

        static void EnsurePersonaLoaded()
        {
            if (_personaLoaded || _personaLoading || _taiwuId <= 0 || _npcId < 0 || _personaInput == null) return;
            if (!TryBeginDocumentLoad(out long operationToken))
            {
                SetStatus("另一个世界书、人设或设置操作仍在进行，请稍候再打开人设页。", true);
                return;
            }
            _personaLoading = true;
            SetButtons(false);
            _personaInput.text = "读取人设中……";
            int taiwuId = _taiwuId, npcId = _npcId;
            long version = _lifecycleVersion;
            int worldGeneration = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            var host = ConfigHost.Instance;
            if (host == null)
            {
                FinishOwnedDocumentLoad(operationToken);
                _personaLoading = false; _personaInput.text = ""; SetButtons(true); return;
            }
            string personasDirectory = JianghuYoulingPaths.Personas;
            string capturedPortrait = PortraitStore.GetPortrait(taiwuId, npcId);
            host.StartCoroutine(LoadPersonaCo(personasDirectory, taiwuId, npcId, capturedPortrait,
                version, worldGeneration, worldId, operationToken));
        }

        static IEnumerator LoadPersonaCo(string personasDirectory, int taiwuId, int npcId,
            string capturedPortrait, long version, int worldGeneration, uint worldId, long operationToken)
        {
            Task<string> task = Task.Run(() => EffectivePersonaTextFromDirectory(
                personasDirectory, taiwuId, npcId, capturedPortrait));
            while (!task.IsCompleted) yield return null;
            if (!DocumentLoadMayUpdateUi(version, worldGeneration, worldId, operationToken)
                || _taiwuId != taiwuId || _npcId != npcId)
            {
                AbandonDocumentLoad(operationToken);
                yield break;
            }
            if (!FinishOwnedDocumentLoad(operationToken)) yield break;
            if (task.IsFaulted)
            {
                _personaLoading = false; _personaLoaded = false; _personaInput.text = "";
                SetButtons(true);
                SetStatus("人设读取失败，请重试。", true); yield break;
            }
            _personaLoading = false; _personaLoaded = true;
            _personaLoadedEffective = task.Result ?? "";
            _personaInput.characterLimit = System.Math.Max(MaxPersona + 1, _personaLoadedEffective.Length + 1);
            _personaInput.text = _personaLoadedEffective;
            ArmFirstClickSelectAll(_personaInput);
            SetButtons(true);
            if (_personaLoadedEffective.Length > MaxPersona)
                SetStatus("旧版人设已完整载入，但超过新版 " + MaxPersona
                    + " 字上限；请删减后再保存，未保存前原文件不会改变。", true);
            EnsureActiveLongTextLoaded();
        }

        static void EnsureActiveLongTextLoaded()
        {
            if (_root == null || !_root.activeSelf || LongTextEditorOperationGate.IsBusy) return;
            if (_activePaneIndex == 7) EnsurePersonaLoaded();
        }

        static string EffectivePersonaTextFromDirectory(string personasDirectory, int taiwuId,
            int npcId, string capturedPortrait)
        {
            string custom = PersonaStore.Load(personasDirectory, taiwuId.ToString(), npcId.ToString());
            if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
            if (!string.IsNullOrWhiteSpace(capturedPortrait)) return capturedPortrait.Trim();
            return "（默认人设由该角色的身份、关系、经历、特性和自动画像生成。首次对话生成画像后,这里会显示自动画像；你也可以直接改写本框内容并保存。）";
        }

        static bool TryBeginDocumentLoad(out long operationToken)
        {
            operationToken = 0;
            if (_activeDocumentLoadToken != 0
                || !LongTextEditorOperationGate.TryAcquire(DocumentOperationOwner, out operationToken))
                return false;
            _activeDocumentLoadToken = operationToken;
            return true;
        }

        static bool DocumentLoadMayUpdateUi(long version, int worldGeneration, uint worldId,
            long operationToken)
            => _activeDocumentLoadToken == operationToken
                && LongTextEditorOperationGate.IsOwner(DocumentOperationOwner, operationToken)
                && version == _lifecycleVersion
                && WorldLifecycle.IsSameWorld(worldGeneration) && WorldLifecycle.WorldId == worldId
                && _root != null && _root.activeSelf;

        static void AbandonDocumentLoad(long operationToken)
        {
            if (_activeDocumentLoadToken == operationToken) _activeDocumentLoadToken = 0;
            LongTextEditorOperationGate.Release(DocumentOperationOwner, operationToken);
        }

        static bool FinishOwnedDocumentLoad(long operationToken)
        {
            if (_activeDocumentLoadToken != operationToken
                || !LongTextEditorOperationGate.Release(DocumentOperationOwner, operationToken)) return false;
            _activeDocumentLoadToken = 0;
            return true;
        }

        static void SetDiffLabel() { if (_diffBtn != null) { var t = _diffBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = _difficulty; } }
        static void SetReplyLenLabel() { if (_replyLenBtn != null) { var t = _replyLenBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = ReplyLenStore.Label(_replyLen); } }
        static void CycleReplyLen()
        {
            int next = (_replyLen + 1) % 4;
            if (!ReplyLenStore.Save(next))
            {
                _replyLen = ReplyLenStore.Load(); SetReplyLenLabel(); TalkOrchestrator.ReplyLength = _replyLen;
                SetStatus("回复篇幅未得到完整耐久确认；已重新读取磁盘实际值「" + ReplyLenStore.Label(_replyLen) + "」。", true); return;
            }
            _replyLen = next;
            SetReplyLenLabel();
            TalkOrchestrator.ReplyLength = _replyLen;
            string d = _replyLen == 0 ? "一两句话点到为止" : _replyLen == 2 ? "可稍展开把话说透" : _replyLen == 3 ? "长短随 NPC 随性" : "通常二三句、顺其自然";
            SetStatus("回复篇幅设为「" + ReplyLenStore.Label(_replyLen) + "」:" + d, false);
        }
        static void SetFontSizeLabel()
        {
            if (_fontSizeBtn == null) return;
            var text = _fontSizeBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = UiFontSizeStore.Label(_fontSize);
        }
        static void CycleFontSize()
        {
            int next = _fontSize >= UiFontSizeStore.Large
                ? UiFontSizeStore.Small : _fontSize + 1;
            if (!UiFontSizeStore.Save(next))
            {
                _fontSize = UiFontSizeStore.Load();
                SetFontSizeLabel();
                SetStatus("字体大小未得到完整耐久确认；已重新读取磁盘实际值「"
                    + UiFontSizeStore.Label(_fontSize) + "」。", true);
                return;
            }
            _fontSize = next;
            SetFontSizeLabel();
            SetStatus("字体大小设为「" + UiFontSizeStore.Label(_fontSize)
                + "」；《江湖有灵》已经打开和之后打开的界面均已生效。", false);
        }
        static void SetGhostwriteLenLabel()
        {
            if (_ghostwriteLenBtn == null) return;
            var text = _ghostwriteLenBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = GhostwriteLengthStore.Label(_ghostwriteLen);
        }
        static void CycleGhostwriteLen()
        {
            int next = _ghostwriteLen >= GhostwriteLengthStore.Detailed
                ? GhostwriteLengthStore.Short : _ghostwriteLen + 1;
            if (!GhostwriteLengthStore.Save(next))
            {
                _ghostwriteLen = GhostwriteLengthStore.Load();
                SetGhostwriteLenLabel();
                SetStatus("代笔篇幅未得到完整耐久确认；已重新读取磁盘实际值「"
                    + GhostwriteLengthStore.Label(_ghostwriteLen) + "」。", true);
                return;
            }
            _ghostwriteLen = next;
            SetGhostwriteLenLabel();
            SetStatus("代笔篇幅设为「" + GhostwriteLengthStore.Label(_ghostwriteLen)
                + "」；单聊、群聊与灵儿代笔统一生效。", false);
        }
        static void SetGhostwriteLearningLabel()
        {
            if (_ghostwriteLearningBtn == null) return;
            var text = _ghostwriteLearningBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = _ghostwriteLearning ? "开" : "关";
        }
        static void ToggleGhostwriteLearning()
        {
            bool next = !_ghostwriteLearning;
            if (!GhostwriteSelfLearningStore.Save(next))
            {
                _ghostwriteLearning = GhostwriteSelfLearningStore.Load();
                SetGhostwriteLearningLabel();
                SetStatus("代笔自学习开关未得到完整耐久确认，已恢复磁盘实际值。", true);
                return;
            }
            _ghostwriteLearning = next;
            SetGhostwriteLearningLabel();
            SetStatus(next
                ? "代笔自学习已开启：会从当前存档的真实玩家发言中逐渐模仿用词、内容倾向和字数。"
                : "代笔自学习已关闭：继续使用默认代笔文风、手动太吾口吻与代笔篇幅。", false);
        }
        static void SetStreamLabel() { if (_streamBtn != null) { var t = _streamBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = _stream ? "开" : "关"; } }
        static void SetGroupRoundsLabel() { if (_groupRoundsBtn != null) { var t = _groupRoundsBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = _groupRounds + " 轮"; } }
        static void CycleGroupRounds()
        {
            int next = _groupRounds >= GroupRoundsStore.Max ? GroupRoundsStore.Min : _groupRounds + 1;
            if (!GroupRoundsStore.Save(next))
            {
                _groupRounds = GroupRoundsStore.Load(); SetGroupRoundsLabel();
                SetStatus("群聊轮数未得到完整耐久确认；已重新读取磁盘实际值「" + _groupRounds + " 轮」。", true); return;
            }
            _groupRounds = next;   // 1→2→3→1 循环
            SetGroupRoundsLabel();
            RefreshPresetSelectionHint();
            SetStatus("群聊自动轮数设为「" + _groupRounds + " 轮」:太吾每发一条消息后,群内众人自动接话、彼此续聊最多 " + _groupRounds + " 轮；不限制玩家继续发言。1 轮最清爽,轮多则更热闹但回话与耗费也多。", false);
        }

        static void CycleDifficulty()
        {
            int i = System.Array.IndexOf(DifficultyStore.Options, _difficulty);
            i = (i + 1) % DifficultyStore.Options.Length;
            string next = DifficultyStore.Options[i];
            if (!DifficultyStore.Save(next))
            {
                _difficulty = DifficultyStore.Load(); SetDiffLabel(); TalkOrchestrator.Difficulty = _difficulty;
                SetStatus("难度未得到完整耐久确认；已重新读取磁盘实际值「" + _difficulty + "」。", true); return;
            }
            _difficulty = next;
            SetDiffLabel();
            TalkOrchestrator.Difficulty = _difficulty;
            SetStatus("难度已设为「" + _difficulty + "」:" +
                (_difficulty == "简单" ? "NPC 较易被打动" : _difficulty == "困难" ? "NPC 极难被说动、且拒绝道德绑架" : "依其为人如常权衡"), false);
        }

        static void ToggleStream()
        {
            bool next = !_stream;
            if (!StreamStore.Save(next))
            {
                _stream = StreamStore.Load(); SetStreamLabel(); TalkOrchestrator.StreamingEnabled = _stream;
                SetStatus("流式设置未得到完整耐久确认；已重新读取磁盘实际值「" + (_stream ? "开" : "关") + "」。", true); return;
            }
            _stream = next;
            SetStreamLabel();
            TalkOrchestrator.StreamingEnabled = _stream;
            SetStatus(_stream ? "流式已开:工具调用与逐字回话边出边显示(思考过程是否展示见下方开关)。" : "流式已关:整段回话一次显示。", false);
        }

        static void SetThinkLabel() { if (_thinkBtn != null) { var t = _thinkBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = _showThink ? "开" : "关"; } }

        static void ToggleThink()
        {
            bool next = !_showThink;
            if (!ThinkingStore.Save(next))
            {
                _showThink = ThinkingStore.Load(); SetThinkLabel(); ChatWindow.ShowThinking = _showThink;
                SetStatus("思量显示未得到完整耐久确认；已重新读取磁盘实际值「" + (_showThink ? "开" : "关") + "」。", true); return;
            }
            _showThink = next;
            SetThinkLabel();
            ChatWindow.ShowThinking = _showThink;
            SetStatus(_showThink
                ? "已开启思量展示：系统仍会按本轮复杂度动态选择推理；普通闲聊、模型未返回思考或流式降级时可能没有思量文字。"
                : "已关闭:隐去思量,只看工具调用与正文回话；这不会关闭模型完成复杂动作所需的内部推理。", false);
        }

        static void SetAiEventLabel() { if (_aiEventBtn != null) { var t = _aiEventBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = _aiEvent ? "开" : "关"; } }

        static void ToggleAiEvent()
        {
            bool next = !_aiEvent;
            if (!AiEventStore.Save(next))
            {
                _aiEvent = AiEventStore.Load(); SetAiEventLabel();
                SetStatus("AI 江湖事件设置未得到完整耐久确认；已重新读取磁盘实际值「" + (_aiEvent ? "开" : "关") + "」。", true); return;
            }
            _aiEvent = next;
            SetAiEventLabel();
            RefreshPresetSelectionHint();
            SetStatus(_aiEvent ? "已开启:每月过月会生成一桩 AI 江湖大事,并按远近知会附近 NPC。" : "已关闭:过月不再生成 AI 江湖事件(不影响同道主动行事)。", false);
        }

        static void SetCompanionMonthlyLabel() { if (_companionMonthlyBtn != null) { var t = _companionMonthlyBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = _companionMonthly ? "开" : "关"; } }

        static void SetMonthlyPopupLabel() { if (_monthlyPopupBtn != null) { var t = _monthlyPopupBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = _monthlyPopup ? "开" : "关"; } }

        static void ToggleMonthlyPopup()
        {
            bool next = !_monthlyPopup;
            if (!MonthlyDigestPopupStore.Save(next))
            {
                _monthlyPopup = MonthlyDigestPopupStore.Load(); SetMonthlyPopupLabel();
                SetStatus("过月弹窗设置未得到完整耐久确认；已重新读取磁盘实际值「" + (_monthlyPopup ? "开" : "关") + "」。", true); return;
            }
            _monthlyPopup = next;
            SetMonthlyPopupLabel();
            MonthlyDigestPopup.NotifySettingsChanged();
            SetStatus(_monthlyPopup
                ? "已开启：过月时会自动打开历史纪事中的对应月份；纪事按钮仍会显示未读数量。"
                : "已关闭：过月不再自动打开窗口；生成与归档照常进行，可从右侧过月纪事查看未读内容。", false);
        }

        static void ToggleCompanionMonthly()
        {
            bool next = !_companionMonthly;
            if (!CompanionMonthlyStore.Save(next))
            {
                _companionMonthly = CompanionMonthlyStore.Load(); SetCompanionMonthlyLabel();
                SetStatus("同道过月设置未得到完整耐久确认；已重新读取磁盘实际值「" + (_companionMonthly ? "开" : "关") + "」。", true); return;
            }
            _companionMonthly = next;
            SetCompanionMonthlyLabel();
            RefreshPresetSelectionHint();
            SetStatus(_companionMonthly ? "已开启:同道过月会按自己的记忆、近来对话和处境主动行事,并真实调用工具落地。" : "已关闭:过月不再触发同道主动行事(不影响 AI 江湖事件)。", false);
        }

        // JHYL_MONTHLY_ONE_CLICK_PRESETS: 体验档位只切换过月、灵儿主动频率、群聊轮数和
        // 每月同道数量，绝不改模型、Key、上下文窗口、回复上限或任何长文本。
        static void ApplyMonthlyPreset(string preset)
        {
            if (!ExperiencePresetPolicy.TryGet(preset, out ExperiencePresetDefinition definition))
            {
                SetStatus("未知的体验档位，未修改任何设置。", true);
                return;
            }

            SettingsBatchTransaction transaction;
            try
            {
                transaction = SettingsBatchTransaction.Capture(new[]
                {
                    AiEventStore.SettingsPath,
                    CompanionMonthlyStore.SettingsPath,
                    CompanionMonthlyStore.CountPath,
                    GroupRoundsStore.SettingsPath,
                    AssistantProactiveStore.SettingsPath,
                });
            }
            catch (System.Exception e)
            {
                SetStatus("无法建立一键档位保存事务，未修改任何设置：" + e.Message, true);
                return;
            }

            bool saved;
            bool rollbackOk = true;
            using (transaction)
            {
                saved = AiEventStore.Save(definition.MonthlyEvent);
                if (saved) saved = CompanionMonthlyStore.Save(definition.CompanionMonthly);
                if (saved) saved = CompanionMonthlyStore.SaveCount(definition.CompanionCount);
                if (saved) saved = GroupRoundsStore.Save(definition.GroupRounds);
                if (saved) saved = AssistantProactiveStore.Save(definition.AssistantFrequency);
                if (saved) transaction.Commit();
                else rollbackOk = transaction.Rollback();
            }
            if (!saved)
            {
                _aiEvent = AiEventStore.Load(); _companionMonthly = CompanionMonthlyStore.Load();
                _companionMonthlyCount = CompanionMonthlyStore.LoadCount();
                _groupRounds = GroupRoundsStore.Load();
                _asstFreq = AssistantProactiveStore.Load();
                if (_companionMonthlyCountInput != null)
                    _companionMonthlyCountInput.text = _companionMonthlyCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                SetAiEventLabel(); SetCompanionMonthlyLabel(); SetGroupRoundsLabel();
                SetAsstEnabledLabel(); SetAsstFreqLabel();
                RefreshPresetSelectionHint();
                SetStatus(rollbackOk
                    ? "一键档位未能完整保存；磁盘事务已回滚，并重新读取实际设置。"
                    : "一键档位保存失败，且磁盘回滚未能完整确认；已重新读取实际设置，请检查磁盘状态。", true);
                return;
            }

            _aiEvent = definition.MonthlyEvent;
            _companionMonthly = definition.CompanionMonthly;
            _companionMonthlyCount = definition.CompanionCount;
            _groupRounds = definition.GroupRounds;
            _asstFreq = definition.AssistantFrequency;
            if (_companionMonthlyCountInput != null)
                _companionMonthlyCountInput.text = _companionMonthlyCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SetAiEventLabel(); SetCompanionMonthlyLabel(); SetGroupRoundsLabel();
            SetAsstEnabledLabel(); SetAsstFreqLabel();
            RefreshPresetSelectionHint();
            AssistantWidget.NotifySettingsChanged();
            SetStatus("已一键切换为「" + definition.DisplayName + "」：" + PresetSettingsText(definition, true)
                + " 模型、上下文窗口和回复上限均未改动；开启的过月一律使用主模型。", false);
        }

        static void RefreshPresetSelectionHint()
        {
            if (_presetSelectionHint == null) return;
            ExperiencePresetDefinition selected = ExperiencePresetPolicy.Match(_aiEvent, _companionMonthly,
                _companionMonthlyCount, _groupRounds, _asstFreq);
            _presetSelectionHint.text = selected == null
                ? "当前为自定义组合；选择任一档位会立即应用下列五项设置。"
                : "当前档位：" + selected.DisplayName + "（选择其他档位会立即切换）";
        }

        static string PresetSettingsText(ExperiencePresetDefinition definition, bool inline)
        {
            if (definition == null) return string.Empty;
            string separator = inline ? "，" : "\n";
            string suffix = inline ? "。" : string.Empty;
            string frequency = definition.AssistantFrequency == AssistantProactiveStore.Off
                ? "关闭" : AsstFreqLabel(definition.AssistantFrequency) + "频";
            return "江湖事件：" + (definition.MonthlyEvent ? "开启" : "关闭") + separator
                + "同道过月：" + (definition.CompanionMonthly ? "开启" : "关闭") + separator
                + "灵儿主动频率：" + frequency + "（需灵儿总开关开启）" + separator
                + "群聊自动：" + definition.GroupRounds + " 轮" + separator
                + "每月同道：" + definition.CompanionCount + " 人" + suffix;
        }

        sealed class DocumentSaveResult
        {
            public bool Ok;
            public string Error;
            public SettingsBatchTransaction Transaction;
        }

        sealed class DocumentResetResult
        {
            public bool Ok;
            public string Text;
            public string Error;
            public SettingsBatchTransaction Transaction;
        }

        static List<string> BatchSettingPaths(int taiwuId, int npcId, bool includeWorldBook, bool includePersona)
        {
            var paths = new List<string>
            {
                Path.Combine(JianghuYoulingPaths.Settings, "llm.json"),
                Path.Combine(JianghuYoulingPaths.Settings, "tts.json"),
                Path.Combine(JianghuYoulingPaths.Settings, "tts_params.json"),
                Path.Combine(JianghuYoulingPaths.Settings, "image_generation.json"),
                Path.Combine(JianghuYoulingPaths.Settings, "taiwu_voice.txt"),
                Path.Combine(JianghuYoulingPaths.Settings, "difficulty.txt"),
                Path.Combine(JianghuYoulingPaths.Settings, "stream.txt"),
                Path.Combine(JianghuYoulingPaths.Settings, "replylen.txt"),
                GhostwriteLengthStore.SettingsPath,
                GhostwriteSelfLearningStore.SettingsPath,
                Path.Combine(JianghuYoulingPaths.Settings, "assistant_name.txt"),
                Path.Combine(JianghuYoulingPaths.Settings, "assistant_face_path.txt"),
                Path.Combine(JianghuYoulingPaths.Settings, "assistant_persona.txt"),
                CompanionMonthlyStore.CountPath,
            };
            if (includeWorldBook)
            {
                paths.Add(Path.Combine(JianghuYoulingPaths.Personas, "Worldbook_" + taiwuId + ".txt"));
                paths.Add(StructuredWorldBookStore.LayoutPath(JianghuYoulingPaths.Personas, taiwuId));
            }
            if (includePersona)
                paths.Add(PersonaStore.CanonicalPath(JianghuYoulingPaths.Personas,
                    npcId.ToString()));
            return paths;
        }

        static IEnumerator SaveDocumentsCo(int taiwuId, int npcId, string worldBook, bool saveWorldBook,
            string persona, bool savePersona, string capturedPersonaPortrait, long version,
            int worldGeneration, uint worldId, long operationToken, List<string> transactionPaths,
            string personasDirectory)
        {
            Task<DocumentSaveResult> task = Task.Run(() =>
            {
                SettingsBatchTransaction tx = null;
                bool handedOff = false;
                try
                {
                    tx = SettingsBatchTransaction.Capture(transactionPaths);
                    if (saveWorldBook)
                    {
                        if (!WorldBookStore.SaveEffectiveFromDirectory(personasDirectory, taiwuId, worldBook))
                            return new DocumentSaveResult { Error = "世界书保存失败:磁盘写入或语义读回校验失败" };
                        if (!StructuredWorldBookStore.InvalidateFromDirectory(personasDirectory, taiwuId))
                            return new DocumentSaveResult { Error = "世界书保存失败:条目索引失效标记未可靠写盘" };
                    }
                    if (savePersona && !SaveEffectivePersona(personasDirectory, taiwuId, npcId,
                        persona, capturedPersonaPortrait))
                        return new DocumentSaveResult { Error = "人设为空、为默认提示文本，或磁盘保存失败" };
                    handedOff = true;
                    return new DocumentSaveResult { Ok = true, Transaction = tx };
                }
                catch (System.Exception ex)
                {
                    return new DocumentSaveResult { Error = "长文本保存异常:" + ex.GetType().Name };
                }
                finally { if (!handedOff) tx?.Dispose(); }
            });
            while (!task.IsCompleted) yield return null;
            if (_activeDocumentSaveToken != operationToken
                || !WorldLifecycle.IsSameWorld(worldGeneration) || WorldLifecycle.WorldId != worldId)
            {
                // World exit invalidates the UI generation but deliberately leaves the lease to
                // this worker. Roll back only its explicitly captured old-world paths, then
                // release without touching buttons, flags, ids or drafts of the new window.
                if (!task.IsFaulted) task.Result?.Transaction?.Dispose();
                LongTextEditorOperationGate.Release(DocumentOperationOwner, operationToken);
                yield break;
            }
            if (!LongTextEditorOperationGate.IsOwner(DocumentOperationOwner, operationToken))
            {
                if (!task.IsFaulted) task.Result?.Transaction?.Commit();
                yield break;
            }
            DocumentSaveResult result = task.IsFaulted ? null : task.Result;
            if (result == null || !result.Ok)
            {
                FinishOwnedDocumentSave(operationToken);
                SetEditorInteractable(true);
                SetButtons(true);
                if (_root != null && _root.activeSelf)
                    SetStatus("(" + (result?.Error ?? "长文本保存失败") + "；原设置已回滚)", true);
                yield break;
            }
            if (saveWorldBook) _wbLoadedEffective = worldBook ?? "";
            if (savePersona)
            {
                _personaLoadedEffective = persona ?? "";
                if (_personaInput != null) _personaInput.characterLimit = MaxPersona + 1;
            }
            // Keep the same transaction open across the background document writes and all
            // remaining settings. Any later validation/write failure restores both groups.
            try
            {
                Save(result.Transaction, operationToken);
                if (savePersona && PersonaSaveMatchesDisk(personasDirectory, taiwuId, npcId,
                    persona, capturedPersonaPortrait))
                    PortraitService.Invalidate(taiwuId, npcId);
            }
            finally
            {
                FinishOwnedDocumentSave(operationToken);
                SetEditorInteractable(true);
                SetButtons(true);
                EnsureActiveLongTextLoaded();
            }
        }

        static void Save()
        {
            Save(null, 0);
        }

        static void Save(SettingsBatchTransaction inheritedTransaction, long operationToken)
        {
            if (_savingDocuments && (_activeDocumentSaveToken != operationToken
                || !LongTextEditorOperationGate.IsOwner(DocumentOperationOwner, operationToken))) return;
            if (operationToken == 0 && LongTextEditorOperationGate.IsBusy)
            {
                SetStatus("另一个世界书、人设或设置保存仍在收尾，请稍候再试。", true);
                return;
            }
            System.Action rollbackInherited = () =>
            {
                if (inheritedTransaction == null) return;
                inheritedTransaction.Dispose();
                inheritedTransaction = null;
                SetButtons(true);
            };
            if (_wbLoading || _personaLoading)
            {
                rollbackInherited();
                SetStatus("长文本仍在读取，请等内容显示后再保存。", true);
                return;
            }
            // TMP_InputField is allowed one sentinel character past the documented cap so
            // an oversized paste can be reported explicitly instead of being silently
            // accepted as a truncated setting.
            if (_wbInput != null && (_wbInput.text ?? string.Empty).Length > MaxWorldBook)
            {
                rollbackInherited();
                SetStatus("(世界书超过 " + MaxWorldBook + " 字上限，请删减后再保存)", true);
                return;
            }
            if (_personaInput != null && (_personaInput.text ?? string.Empty).Length > MaxPersona)
            {
                rollbackInherited();
                SetStatus("(角色人设超过 " + MaxPersona + " 字上限，请删减后再保存)", true);
                return;
            }
            if (_asstPersonaInput != null && (_asstPersonaInput.text ?? string.Empty).Length > MaxPersona)
            {
                rollbackInherited();
                SetStatus("(灵儿人设超过 " + MaxPersona + " 字上限，请删减后再保存)", true);
                return;
            }
            string monthlyCountText = _companionMonthlyCountInput != null
                ? (_companionMonthlyCountInput.text ?? "").Trim()
                : CompanionMonthlyStore.DefaultCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!int.TryParse(monthlyCountText, out int monthlyCount) || monthlyCount < 0
                || monthlyCount > CompanionMonthlyStore.MaxCount)
            {
                rollbackInherited();
                SetStatus("(每月触发同道数量必须是 0～"
                    + CompanionMonthlyStore.MaxCount + "；0 表示不触发)", true);
                return;
            }

            bool saveWorldBook = _wbLoaded && _taiwuId > 0 && _wbInput != null
                && !SameText(_wbInput.text, _wbLoadedEffective);
            bool savePersona = _personaLoaded && _taiwuId > 0 && _npcId >= 0 && _personaInput != null
                && _personaInputGo != null && _personaInputGo.activeSelf
                && !SameText(_personaInput.text, _personaLoadedEffective);
            if (saveWorldBook || savePersona)
            {
                string worldBook = _wbInput != null ? (_wbInput.text ?? "") : "";
                string persona = _personaInput != null ? (_personaInput.text ?? "") : "";
                if (saveWorldBook && string.IsNullOrWhiteSpace(worldBook))
                { rollbackInherited(); SetStatus("(世界书为空，未覆盖；请使用还原默认按钮明确清空自定义设置)", true); return; }
                var host = ConfigHost.Instance;
                if (host == null)
                {
                    rollbackInherited();
                    SetStatus("(宿主缺失，无法启动长文本保存)", true); return;
                }
                if (!LongTextEditorOperationGate.TryAcquire(DocumentOperationOwner, out long newOperationToken))
                {
                    rollbackInherited();
                    SetStatus("(另一个世界书、人设或设置保存仍在进行，请稍候再试)", true); return;
                }
                _activeDocumentSaveToken = newOperationToken;
                _savingDocuments = true;
                SetEditorInteractable(false);
                SetButtons(false);
                SetStatus("正在保存世界书与人设……", false);
                List<string> transactionPaths = BatchSettingPaths(_taiwuId, _npcId, saveWorldBook, savePersona);
                string personasDirectory = JianghuYoulingPaths.Personas;
                string capturedPersonaPortrait = savePersona ? PortraitStore.GetPortrait(_taiwuId, _npcId) : null;
                int worldGeneration = WorldLifecycle.Generation;
                uint worldId = WorldLifecycle.WorldId;
                host.StartCoroutine(SaveDocumentsCo(_taiwuId, _npcId, worldBook, saveWorldBook,
                    persona, savePersona, capturedPersonaPortrait, _lifecycleVersion,
                    worldGeneration, worldId, newOperationToken, transactionPaths, personasDirectory));
                return;
            }

            SettingsBatchTransaction transaction = inheritedTransaction;
            try
            {
                if (transaction == null)
                    transaction = SettingsBatchTransaction.Capture(BatchSettingPaths(_taiwuId, _npcId, false, false));
            }
            catch (System.Exception ex)
            {
                SetStatus("(无法启动设置批量事务:" + ex.GetType().Name + ")", true);
                return;
            }
            // A later field can fail after the user has already edited one of the long-text
            // documents. The disk transaction must roll back, but the editor draft should not
            // disappear: keep the draft for correction while re-reading the rolled-back disk
            // value as the next comparison baseline.
            bool preserveWorldBookDraft = _wbLoaded && _wbInput != null;
            bool preservePersonaDraft = _personaLoaded && _personaInput != null;
            string worldBookDraft = preserveWorldBookDraft ? (_wbInput.text ?? "") : null;
            string personaDraft = preservePersonaDraft ? (_personaInput.text ?? "") : null;
            System.Action<string> abort = message =>
            {
                bool rolledBack = transaction.Rollback();
                // Prefill normally restores every runtime projection. Restore the two
                // conversation-text statics independently as a fail-safe in case unrelated
                // UI prefill work throws before it reaches them.
                try
                {
                    ReloadConversationTextRuntimeFromDisk();
                }
                catch { }
                try { LlmService.Reload(); Prefill(); } catch { }
                RestoreLongTextDraftsAfterRollback(
                    preserveWorldBookDraft, worldBookDraft, preservePersonaDraft, personaDraft);
                // A failed batch may already have previewed the new face path. Force the
                // widget to discard that cached projection and reread the rolled-back file.
                try { AssistantWidget.NotifyFacePathChanged(); } catch { }
                SetStatus(message + (rolledBack
                    ? " 本次批量修改已全部回滚，并已重新读取磁盘状态。"
                    : " 回滚未完整；界面已尽力重新读取磁盘，重启前请勿假定任一项未改变。"), true);
            };
            try
            {
            var saved = new List<string>();
            string ttsWarning = null;
            string llmWarning = null;
            // 接口:base 与 model 都填了才写(本地模型 apiKey 可空);两者皆空则跳过不报错
            string baseUrl = (_baseUrl.text ?? "").Trim();
            string apiKey = (_apiKey.text ?? "").Trim();
            string model = (_model.text ?? "").Trim();
            if (baseUrl.Length > 0 || model.Length > 0)
            {
                if (baseUrl.Length == 0 || model.Length == 0) { abort("(接口:baseUrl 与 model 不可只填一个;本地模型 apiKey 可空)"); return; }
                try
                {
                    string maxTokensText = _maxTokens != null ? (_maxTokens.text ?? "").Trim() : DefaultMaxTokensText;
                    string contextWindowText = _contextWindow != null ? (_contextWindow.text ?? "").Trim() : RecommendedContextWindowText;
                    if (!int.TryParse(maxTokensText, out int maxTokens) || maxTokens < 0)
                    { abort("(接口:回复上限必须是 0 或正整数)"); return; }
                    int contextWindow = OpenAiCompatibleClient.DefaultContextWindowTokens;
                    if (contextWindowText.Length > 0
                        && (!int.TryParse(contextWindowText, out contextWindow) || contextWindow < 8192))
                    { abort("(接口:上下文窗口可留空按 1000000，或填写至少 8192 的整数)"); return; }
                    if (maxTokens > 0 && maxTokens + 1024 >= contextWindow)
                    { abort("(接口:回复上限需至少比上下文窗口小 1024，给输入和工具留出空间)"); return; }
                    var candidate = new OpenAiCompatibleClient(baseUrl, DefaultChatPath, apiKey, model);
                    if (!string.IsNullOrWhiteSpace(candidate.ConfigurationError))
                    { abort("(接口配置无效:" + candidate.ConfigurationError + ")"); return; }
                    llmWarning = candidate.ModelMigrationWarning;
                    var o = new JObject { ["baseUrl"] = baseUrl, ["chatPath"] = DefaultChatPath, ["model"] = model, ["bgModel"] = (_bgModel != null ? (_bgModel.text ?? "").Trim() : ""), ["maxTokens"] = maxTokensText, [LlmService.MaxTokensDefaultVersionField] = LlmService.MaxTokensDefaultVersion, ["contextWindow"] = contextWindowText, [LlmService.ContextWindowDefaultVersionField] = LlmService.ContextWindowDefaultVersion };
                    if (!LlmService.SaveRaw(o, apiKey, out string saveError))
                    { abort("(接口保存失败:" + (saveError ?? "安全存储失败") + ")"); return; }
                    saved.Add("接口");
                }
                catch (System.Exception ex) { abort("(接口保存失败:" + ex.GetType().Name + ")"); return; }
            }
            // 语音(TTS,可选):存到 tts.json(接口/密钥)+ 模型到 tts_params.json;音色与语速语气全自动。
            // 不填=不开语音,绝不借用主接口 key。
            if (_ttsBase != null)
            {
                string vb = (_ttsBase.text ?? "").Trim();
                string vk = ((_ttsKey != null ? _ttsKey.text : "") ?? "").Trim();
                string ttsModelText = (_ttsModel != null ? (_ttsModel.text ?? "").Trim() : "");
                if (!TtsConfig.SaveExplicit("", vb, vk, ttsModelText))   // provider 留空=按 base+model 自动判定
                { abort("(语音接口保存失败:" + (TtsConfig.LastSaveError ?? "安全存储失败") + ")"); return; }
                var ts = TtsSettings.TryLoad(out var loadedTts) ? loadedTts : new TtsSettings(); // JHYL_TTS_KEEP_VOICE_OVERRIDE
                if (_ttsMigratingLegacyDefault)
                {
                    // MiniMax 的固定 voice id 不是 Seed Audio 的自然语言音色描述，迁移时清掉，
                    // 让运行层重新按人物性别、年龄和性情生成音色。
                    ts.Voice = "";
                    ts.Emotion = "";
                }
                ts.Model = ttsModelText;
                ts.DialogueOnly = _ttsDialogueOnly;
                if (!ts.Save()) { abort("(语音参数保存失败:磁盘写入或读回校验失败)"); return; }
                _ttsMigratingLegacyDefault = false;
                _ttsMigratingCrossProviderKey = false;
                if (vk.Length > 0) ttsWarning = TtsConfigWarning(vb, ts.Model);
                saved.Add(vk.Length > 0 ? "语音(已启用)" : "语音(未填密钥=不启用)");
            }

            if (_imageEndpoint != null)
            {
                string provider = (_imageProvider?.text ?? "").Trim();
                string endpoint = (_imageEndpoint.text ?? "").Trim();
                string key = (_imageKey?.text ?? "").Trim();
                string imageModel = (_imageModel?.text ?? "").Trim();
                string imageSize = (_imageSize?.text ?? "").Trim();
                string imageWorkflowPath = (_imageWorkflowPath?.text ?? "").Trim();
                string imageStyle = _imageStyle?.text ?? "";
                bool comfyUi = string.Equals(provider, ImageGenerationClient.ProviderComfyUI,
                    System.StringComparison.OrdinalIgnoreCase);
                if (endpoint.Length == 0 || (!comfyUi && imageModel.Length == 0))
                { abort("(生图接口不可为空；非 ComfyUI 协议还必须填写模型)"); return; }
                if (!System.Uri.TryCreate(endpoint, System.UriKind.Absolute, out var imageUri)
                    || (imageUri.Scheme != System.Uri.UriSchemeHttps
                        && !(imageUri.Scheme == System.Uri.UriSchemeHttp && imageUri.IsLoopback)))
                { abort("(生图接口必须是 HTTPS 完整地址；仅本机可使用 HTTP)"); return; }
                if (!ImageGenerationConfig.SaveExplicit(new ImageGenerationConfig.Values
                {
                    Provider = provider,
                    Endpoint = endpoint,
                    ApiKey = key,
                    Model = imageModel,
                    Size = imageSize,
                    Style = imageStyle,
                    Watermark = _imageWatermark,
                    WorkflowPath = imageWorkflowPath
                }))
                { abort("(生图配置保存失败:" + (ImageGenerationConfig.LastSaveError ?? "安全存储失败") + ")"); return; }
                saved.Add(key.Length > 0 ? "生图(已启用)"
                    : (imageUri.IsLoopback ? "生图(本机服务已启用)" : "生图(未填密钥=不启用)"));
            }

            // 对话设置
            string voice = _voiceInput != null ? (_voiceInput.text ?? "").Trim() : "";
            if (!TaiwuVoiceStore.Save(voice)) { abort("(太吾口吻保存失败:磁盘写入或读回校验失败)"); return; }
            TalkOrchestrator.TaiwuVoice = voice;
            if (!DifficultyStore.Save(_difficulty) || !StreamStore.Save(_stream)
                || !ReplyLenStore.Save(_replyLen)
                || !GhostwriteLengthStore.Save(_ghostwriteLen)
                || !GhostwriteSelfLearningStore.Save(_ghostwriteLearning))
            { abort("(对话参数保存失败:磁盘写入或读回校验失败)"); return; }
            TalkOrchestrator.ReplyLength = _replyLen;
            saved.Add("对话");
            if (!CompanionMonthlyStore.SaveCount(monthlyCount))
            { abort("(每月触发同道数量保存失败:磁盘写入或读回校验失败)"); return; }
            _companionMonthlyCount = monthlyCount;
            if (_companionMonthlyCountInput != null)
                _companionMonthlyCountInput.text = monthlyCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RefreshPresetSelectionHint();
            saved.Add("过月");

            // 世界书/人设:框内显示的是当前实际生效文本(默认或玩家自定义)。
            // 未改动时不写盘,避免普通设置保存触碰玩家原有文件;改动后保存为新的完整自定义文本。
            if (_wbLoaded && _taiwuId > 0 && _wbInput != null)
            {
                string box = _wbInput.text ?? "";
                if (!SameText(box, _wbLoadedEffective))
                {
                    if (string.IsNullOrWhiteSpace(box))
                    { abort("(世界书为空，未覆盖；请使用还原默认按钮明确清空自定义设置)"); return; }
                    if (!WorldBookStore.SaveEffective(_taiwuId, box))
                    { abort("(世界书保存失败:磁盘写入或语义读回校验失败)"); return; }
                    _wbLoadedEffective = box;
                    saved.Add("世界书");
                }
            }
            // 只在确认是队友(人设输入框已激活)时保存,非队友不存
            if (_personaLoaded && _npcId >= 0 && _taiwuId > 0 && _personaInput != null && _personaInputGo != null && _personaInputGo.activeSelf)
            {
                string pbox = _personaInput.text ?? "";
                if (!SameText(pbox, _personaLoadedEffective))
                {
                    bool ps = SaveEffectivePersona(_taiwuId, _npcId, pbox);
                    if (!ps) { abort("(人设为空、为默认提示文本，或磁盘保存失败；未覆盖原设置)"); return; }
                    _personaInput.characterLimit = MaxPersona + 1;
                    _personaLoadedEffective = pbox;
                    saved.Add("人设");
                }
            }

            // 助手:名字 + 人设(开关/频率已即时存盘)
            if (_asstNameInput != null)
            {
                string nm = (_asstNameInput.text ?? "").Trim();
                if (nm.Length == 0) nm = AssistantNameStore.Default;
                if (!AssistantNameStore.Save(nm)) { abort("(助手名字保存失败，其他助手设置未继续写入)"); return; }
                _asstNameInput.text = AssistantNameStore.Load();
                string facePath = _asstFacePathInput != null ? (_asstFacePathInput.text ?? "").Trim() : "";
                if (!AssistantFacePathStore.Save(facePath)) { abort("(助手形象路径保存失败)"); return; }
                AssistantWidget.NotifyFacePathChanged();
                AssistantWidget.NotifySettingsChanged();
                if (_asstPersonaInput != null && !AssistantPersonaStore.Save(_asstPersonaInput.text))
                { abort("(助手人设保存失败)"); return; }
                saved.Add("助手");
            }

            transaction.Commit();
            string savedText = "已保存:" + string.Join("、", saved.ToArray()) + "。";
            var warningParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(llmWarning)) warningParts.Add(llmWarning);
            if (!string.IsNullOrWhiteSpace(ttsWarning)) warningParts.Add(ttsWarning);
            string warning = string.Join(" ", warningParts.ToArray());
            if (!string.IsNullOrWhiteSpace(warning)) SetStatus(savedText + " " + warning, true);
            else SetStatus(savedText, false);
            }
            catch (System.Exception ex)
            {
                // UI callbacks and future store implementations are not allowed to escape the
                // all-or-nothing boundary.  Do not surface exception text because provider/store
                // exceptions may contain sensitive configuration values.
                abort("(设置保存发生未预期异常：" + ex.GetType().Name + ")");
            }
            finally
            {
                // Success has already committed; every early return calls abort; an exception in
                // the error UI itself still reaches Dispose and restores the captured bytes.
                transaction.Dispose();
            }
        }

        private static void RestoreLongTextDraftsAfterRollback(
            bool preserveWorldBook, string worldBookDraft, bool preservePersona, string personaDraft)
        {
            // Failure handling is intentionally synchronous. These are bounded local files and
            // this path runs only after a rejected save; keeping the user's unsaved draft is more
            // important than shaving a few milliseconds from an exceptional UI path.
            if (preserveWorldBook && _wbInput != null && _taiwuId > 0)
            {
                try
                {
                    _wbLoadedEffective = EffectiveWorldBookText(_taiwuId);
                    _wbLoaded = true;
                    _wbLoading = false;
                    _wbInput.text = worldBookDraft ?? _wbLoadedEffective;
                }
                catch { _wbLoaded = false; _wbLoading = false; EnsureWorldBookLoaded(); }
            }
            if (preservePersona && _personaInput != null && _taiwuId > 0 && _npcId >= 0)
            {
                try
                {
                    _personaLoadedEffective = EffectivePersonaText(_taiwuId, _npcId);
                    _personaLoaded = true;
                    _personaLoading = false;
                    _personaInput.characterLimit = System.Math.Max(MaxPersona + 1,
                        (personaDraft ?? _personaLoadedEffective).Length + 1);
                    _personaInput.text = personaDraft ?? _personaLoadedEffective;
                    ArmFirstClickSelectAll(_personaInput);
                }
                catch { _personaLoaded = false; _personaLoading = false; EnsurePersonaLoaded(); }
            }
        }

        static string TtsConfigWarning(string baseUrl, string model)
        {
            string b = (baseUrl ?? "").Trim().ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            if (b.Length == 0) return null;

            bool seedTts2Endpoint = b.TrimEnd('/') == "https://openspeech.bytedance.com/api/v3/tts/unidirectional";
            if (seedTts2Endpoint && m != "seed-tts-2.0")
                return "火山 Seed-TTS 2.0 接口应使用资源模型 seed-tts-2.0。";
            if (m == "seed-tts-2.0" && !seedTts2Endpoint)
                return "资源模型 seed-tts-2.0 应配合 https://openspeech.bytedance.com/api/v3/tts/unidirectional 使用。";

            bool seedAudioEndpoint = b.StartsWith("https://openspeech.bytedance.com/api/v3/tts/create",
                System.StringComparison.Ordinal);
            if (seedAudioEndpoint && m != "seed-audio-1.0")
                return "火山 Seed Audio 接口当前应使用模型 seed-audio-1.0。";
            if (m == "seed-audio-1.0" && !seedAudioEndpoint)
                return "模型 seed-audio-1.0 应配合 https://openspeech.bytedance.com/api/v3/tts/create 使用。";

            if (TtsProviderUtil.IsDashScopeUnsupportedAppOrRealtime(b, m))
                return "语音配置看起来是百炼/通义 realtime、WebSocket 或 /apps 应用地址,当前版本不支持;非实时 Qwen 请填 /api/v1 地址,如 https://你的WorkspaceId.cn-beijing.maas.aliyuncs.com/api/v1。";

            if ((b.Contains("maas.aliyuncs.com") || b.Contains("dashscope.aliyuncs.com")) && m.Contains("qwen") && !m.Contains("tts"))
                return "百炼/通义语音模型名应是 qwen3-tts-flash 或 qwen3-tts-instruct-flash 这类非实时 TTS 模型。";

            if (!b.Contains("minimax") && m.StartsWith("speech-02"))
                return "语音模型 speech-02-* 是 MiniMax T2A 模型,baseUrl 通常应为 https://api.minimaxi.com/v1。";

            return null;
        }

        static bool IsLegacyMiniMaxDefault(string provider, string baseUrl, string model)
        {
            string p = (provider ?? "").Trim().ToLowerInvariant();
            string b = (baseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            if (p.Length > 0 && p != TtsProviderUtil.ProviderMiniMax) return false;
            bool oldEndpoint = b == "https://api.minimaxi.com/v1"
                || b == "https://api.minimax.com/v1";
            return oldEndpoint && m == "speech-02-turbo";
        }

        static bool IsLegacySeedAudioDefault(string provider, string baseUrl, string model)
        {
            string p = (provider ?? "").Trim().ToLowerInvariant();
            string b = (baseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            if (p.Length > 0 && p != TtsProviderUtil.ProviderVolcengineSeedAudio) return false;
            return b == "https://openspeech.bytedance.com/api/v3/tts/create"
                && m == "seed-audio-1.0";
        }

        static void Test()
        {
            if (_testing) return;
            string baseUrl = (_baseUrl.text ?? "").Trim();
            string apiKey = (_apiKey.text ?? "").Trim();
            string model = (_model.text ?? "").Trim();
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model)) { SetStatus("(请先在「模型」页填好 baseUrl 与 model 再测试;本地模型 apiKey 可空)", true); ShowPane(0); return; }
            var candidate = new OpenAiCompatibleClient(baseUrl, DefaultChatPath, apiKey, model);
            if (!string.IsNullOrWhiteSpace(candidate.ConfigurationError))
            { SetStatus("测试失败:" + candidate.ConfigurationError, true); return; }
            var host = ConfigHost.Instance;
            if (host == null) { SetStatus("(宿主缺失,无法测试)", true); return; }
            _testing = true; SetButtons(false); SetStatus("测试中…", false);
            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(System.TimeSpan.FromSeconds(45));
            _testCancellation = cancellation;
            long version = _lifecycleVersion;
            host.StartCoroutine(TestCo(baseUrl, DefaultChatPath, apiKey, model,
                candidate.ModelMigrationWarning, version, cancellation));
        }

        static bool PersonaSaveMatchesDisk(string personasDirectory, int taiwuId, int npcId,
            string submittedText, string capturedPortrait)
        {
            string submitted = (submittedText ?? "").Trim();
            bool expectedClear = !string.IsNullOrWhiteSpace(capturedPortrait)
                && SameText(submitted, capturedPortrait);
            string stored = PersonaStore.Load(personasDirectory, taiwuId.ToString(), npcId.ToString());
            return expectedClear ? string.IsNullOrWhiteSpace(stored) : SameText(stored, submitted);
        }

        static bool TryBuildLlmConfig(out JObject config, out string apiKey,
            out string warning, out string error)
        {
            config = null;
            warning = null;
            error = null;
            apiKey = (_apiKey != null ? _apiKey.text : "")?.Trim() ?? "";
            string baseUrl = (_baseUrl != null ? _baseUrl.text : "")?.Trim() ?? "";
            string model = (_model != null ? _model.text : "")?.Trim() ?? "";
            string bgModel = (_bgModel != null ? _bgModel.text : "")?.Trim() ?? "";
            string maxTokensText = (_maxTokens != null ? _maxTokens.text : DefaultMaxTokensText)?.Trim() ?? "";
            string contextWindowText = (_contextWindow != null
                ? _contextWindow.text : RecommendedContextWindowText)?.Trim() ?? "";
            if (baseUrl.Length == 0 || model.Length == 0)
            {
                error = "baseUrl 与 model 都必须填写；本地模型 apiKey 可空";
                return false;
            }
            if (!int.TryParse(maxTokensText, out int maxTokens) || maxTokens < 0)
            {
                error = "回复上限必须是 0 或正整数";
                return false;
            }
            int contextWindow = OpenAiCompatibleClient.DefaultContextWindowTokens;
            if (contextWindowText.Length > 0
                && (!int.TryParse(contextWindowText, out contextWindow) || contextWindow < 8192))
            {
                error = "上下文窗口可留空按 1000000，或填写至少 8192 的整数";
                return false;
            }
            if (maxTokens > 0 && maxTokens + 1024 >= contextWindow)
            {
                error = "回复上限需至少比上下文窗口小 1024，给输入和工具留出空间";
                return false;
            }
            var candidate = new OpenAiCompatibleClient(baseUrl, DefaultChatPath, apiKey, model);
            if (!string.IsNullOrWhiteSpace(candidate.ConfigurationError))
            {
                error = candidate.ConfigurationError;
                return false;
            }
            warning = candidate.ModelMigrationWarning;
            config = new JObject
            {
                ["baseUrl"] = baseUrl,
                ["chatPath"] = DefaultChatPath,
                ["model"] = model,
                ["bgModel"] = bgModel,
                ["maxTokens"] = maxTokensText,
                [LlmService.MaxTokensDefaultVersionField] = LlmService.MaxTokensDefaultVersion,
                ["contextWindow"] = contextWindowText,
                [LlmService.ContextWindowDefaultVersionField] = LlmService.ContextWindowDefaultVersion,
            };
            return true;
        }

        static void RefreshLlmProfiles(string prefer = null)
        {
            _profileNames.Clear();
            IReadOnlyList<string> names = LlmProfileStore.List(out string selected, out string error);
            if (error == null && names != null)
                foreach (string name in names) _profileNames.Add(name);
            string wanted = string.IsNullOrWhiteSpace(prefer) ? selected : prefer.Trim();
            _profileIndex = -1;
            for (int i = 0; i < _profileNames.Count; i++)
                if (string.Equals(_profileNames[i], wanted, System.StringComparison.OrdinalIgnoreCase))
                { _profileIndex = i; break; }
            if (_profileIndex < 0 && _profileNames.Count > 0) _profileIndex = 0;
            _pendingProfileDelete = null;
            SetButtonText(_profileDeleteBtn, "删除档案");
            UpdateLlmProfileControls(error);
        }

        static void UpdateLlmProfileControls(string error = null)
        {
            bool any = _profileIndex >= 0 && _profileIndex < _profileNames.Count;
            if (_profileSelectionLabel != null)
                _profileSelectionLabel.text = error != null ? "档案读取失败"
                    : any ? _profileNames[_profileIndex] : "暂无已保存档案";
            if (_profilePrevBtn != null) _profilePrevBtn.interactable = any && !_testing;
            if (_profileNextBtn != null) _profileNextBtn.interactable = any && !_testing;
            if (_profileActivateBtn != null) _profileActivateBtn.interactable = any && !_testing;
            if (_profileDeleteBtn != null) _profileDeleteBtn.interactable = any && !_testing;
        }

        static void CycleLlmProfile(int delta)
        {
            if (_profileNames.Count == 0) return;
            _profileIndex = (_profileIndex + delta) % _profileNames.Count;
            if (_profileIndex < 0) _profileIndex += _profileNames.Count;
            _pendingProfileDelete = null;
            SetButtonText(_profileDeleteBtn, "删除档案");
            UpdateLlmProfileControls();
        }

        static void SaveCurrentLlmProfile()
        {
            string name = (_profileNameInput != null ? _profileNameInput.text : "")?.Trim() ?? "";
            if (!TryBuildLlmConfig(out JObject config, out string key, out _, out string error))
            {
                SetStatus("模型配置档案未保存：" + error, true);
                return;
            }
            if (!LlmProfileStore.Save(name, config, key, out error))
            {
                SetStatus("模型配置档案未保存：" + (error ?? "磁盘写入失败"), true);
                return;
            }
            RefreshLlmProfiles(name);
            if (_profileNameInput != null) _profileNameInput.text = "";
            SetStatus("已加密保存模型配置档案「" + name + "」；当前正在使用的接口未被改动。", false);
        }

        static void ActivateSelectedLlmProfile()
        {
            if (_profileIndex < 0 || _profileIndex >= _profileNames.Count) return;
            string name = _profileNames[_profileIndex];
            if (!LlmProfileStore.TryLoad(name, out JObject config, out string key, out string error))
            {
                SetStatus("切换失败：" + (error ?? "配置读取失败"), true);
                return;
            }
            if (!ValidateStoredLlmProfile(config, key, out error))
            {
                SetStatus("切换失败：档案内容无效：" + (error ?? "配置校验失败"), true);
                return;
            }
            SettingsBatchTransaction transaction = null;
            try
            {
                transaction = SettingsBatchTransaction.Capture(new[]
                {
                    Path.Combine(JianghuYoulingPaths.Settings, "llm.json"),
                    LlmProfileStore.IndexPath,
                });
                if (!LlmService.SaveRaw(config, key, out error)
                    || !LlmProfileStore.Select(name, out error))
                {
                    transaction.Rollback();
                    LlmService.Reload();
                    Prefill();
                    SetStatus("切换失败：" + (error ?? "配置未能完整提交") + "；已恢复原配置。", true);
                    return;
                }
                transaction.Commit();
            }
            catch (System.Exception e)
            {
                transaction?.Rollback();
                LlmService.Reload();
                Prefill();
                SetStatus("切换失败：" + e.GetType().Name + "；已恢复原配置。", true);
                return;
            }
            finally { transaction?.Dispose(); }
            Prefill();
            RefreshLlmProfiles(name);
            SetStatus("已切换到模型配置档案「" + name + "」，主模型与后台模型立即生效。", false);
        }

        static bool ValidateStoredLlmProfile(JObject config, string apiKey, out string error)
        {
            error = null;
            string baseUrl = (config?["baseUrl"]?.ToString() ?? "").Trim();
            string model = (config?["model"]?.ToString() ?? "").Trim();
            string maxTokensText = LlmService.ReadConfiguredMaxTokens(config);
            string contextWindowText = LlmService.ReadConfiguredContextWindow(config);
            if (baseUrl.Length == 0 || model.Length == 0)
            { error = "baseUrl 或 model 为空"; return false; }
            if (!int.TryParse((maxTokensText ?? "").Trim(), out int maxTokens) || maxTokens < 0)
            { error = "回复上限不是 0 或正整数"; return false; }
            int contextWindow = OpenAiCompatibleClient.DefaultContextWindowTokens;
            if (!string.IsNullOrWhiteSpace(contextWindowText)
                && (!int.TryParse(contextWindowText.Trim(), out contextWindow) || contextWindow < 8192))
            { error = "上下文窗口不是至少 8192 的整数"; return false; }
            if (maxTokens > 0 && maxTokens + 1024 >= contextWindow)
            { error = "回复上限没有为输入与工具保留空间"; return false; }
            var candidate = new OpenAiCompatibleClient(baseUrl, DefaultChatPath,
                (apiKey ?? "").Trim(), model);
            if (!string.IsNullOrWhiteSpace(candidate.ConfigurationError))
            { error = candidate.ConfigurationError; return false; }
            return true;
        }

        static void DeleteSelectedLlmProfile()
        {
            if (_profileIndex < 0 || _profileIndex >= _profileNames.Count) return;
            string name = _profileNames[_profileIndex];
            if (!string.Equals(_pendingProfileDelete, name, System.StringComparison.Ordinal))
            {
                _pendingProfileDelete = name;
                SetButtonText(_profileDeleteBtn, "再次确认");
                SetStatus("再次点击即可删除模型配置档案「" + name + "」；当前正在使用的接口不会被删除。", false);
                return;
            }
            if (!LlmProfileStore.Delete(name, out string next, out string error))
            {
                _pendingProfileDelete = null;
                SetButtonText(_profileDeleteBtn, "删除档案");
                SetStatus("删除失败：" + (error ?? "磁盘写入失败"), true);
                return;
            }
            RefreshLlmProfiles(next);
            SetStatus("已删除模型配置档案「" + name + "」；当前正在使用的接口保持不变。", false);
        }

        static void SetButtonText(Button button, string text)
        {
            if (button == null) return;
            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = text ?? "";
        }

        static IEnumerator TestCo(string baseUrl, string chatPath, string apiKey, string model,
            string migrationWarning, long version, CancellationTokenSource cancellation)
        {
            var client = new OpenAiCompatibleClient(baseUrl, chatPath, apiKey, model);
            // Some reasoning-capable providers still spend tokens internally even when the
            // OpenAI-compatible request asks for reasoning=off. 32 tokens can therefore end in
            // finish_reason=length before the one-word health response is emitted.
            var messages = new List<LlmMessage>
            {
                LlmMessage.System("这是接口连通性测试。只回复 pong，不要解释。"),
                LlmMessage.User("ping")
            };
            var sw = Stopwatch.StartNew();
            Task<LlmResult> task = client.SendAsync(messages, maxTokens: 256, tag: "连接测试",
                ct: cancellation.Token, reasoningPolicy: LlmReasoningPolicy.Off);
            while (!task.IsCompleted && version == _lifecycleVersion
                && ReferenceEquals(_testCancellation, cancellation)) yield return null;
            bool current = version == _lifecycleVersion && ReferenceEquals(_testCancellation, cancellation);
            if (!current)
            {
                if (!task.IsCompleted)
                    task.ContinueWith(t => { var ignored = t.Exception; try { cancellation.Dispose(); } catch { } },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                else { var ignored = task.Exception; try { cancellation.Dispose(); } catch { } }
                yield break;
            }
            sw.Stop();
            LlmResult result = null; string fault = null;
            try { result = task.Result; }
            catch (System.Exception ex) { fault = (ex as System.AggregateException)?.GetBaseException()?.GetType().Name ?? ex.GetType().Name; }
            long ms = sw.ElapsedMilliseconds;
            if (fault != null) SetStatus("测试失败(" + ms + "ms):" + fault, true);
            else if (result != null && result.Ok)
            {
                string suffix = string.IsNullOrWhiteSpace(migrationWarning) ? "" : " " + migrationWarning;
                SetStatus("测试成功(" + ms + "ms)。" + suffix, !string.IsNullOrWhiteSpace(migrationWarning));
            }
            else SetStatus("测试失败(" + ms + "ms):" + (result?.Error ?? "未知错误"), true);
            if (ReferenceEquals(_testCancellation, cancellation)) _testCancellation = null;
            try { cancellation.Dispose(); } catch { }
            _testing = false; SetButtons(true);
        }

        static void SetButtons(bool on)
        {
            bool ready = on && !_wbLoading && !_personaLoading && !_savingDocuments;
            if (_saveBtn != null) _saveBtn.interactable = ready;
            if (_testBtn != null) _testBtn.interactable = ready;
            if (_profileSaveBtn != null) _profileSaveBtn.interactable = ready;
            if (_profilePrevBtn != null) _profilePrevBtn.interactable = ready && _profileNames.Count > 0;
            if (_profileNextBtn != null) _profileNextBtn.interactable = ready && _profileNames.Count > 0;
            if (_profileActivateBtn != null) _profileActivateBtn.interactable = ready && _profileNames.Count > 0;
            if (_profileDeleteBtn != null) _profileDeleteBtn.interactable = ready && _profileNames.Count > 0;
            if (_wbResetBtn != null)
                _wbResetBtn.interactable = ready && _taiwuId > 0 && _wbResetBtn.gameObject.activeSelf;
            if (_wbStructuredBtn != null)
                _wbStructuredBtn.interactable = ready && _taiwuId > 0 && _wbStructuredBtn.gameObject.activeSelf;
            if (_personaResetBtn != null)
                _personaResetBtn.interactable = ready && _taiwuId > 0 && _npcId >= 0
                    && _personaResetBtn.gameObject.activeSelf;
            if (_monthlyCandidateManagerBtn != null)
                _monthlyCandidateManagerBtn.interactable = ready && _taiwuId > 0;
        }

        static void FinishOwnedDocumentSave(long operationToken)
        {
            if (_activeDocumentSaveToken != operationToken
                || !LongTextEditorOperationGate.Release(DocumentOperationOwner, operationToken)) return;
            _activeDocumentSaveToken = 0;
            _savingDocuments = false;
        }

        static void SetEditorInteractable(bool on)
        {
            bool enabled = on && !_savingDocuments;
            foreach (TMP_InputField input in new[] { _baseUrl, _apiKey, _model, _bgModel, _maxTokens,
                _contextWindow, _profileNameInput, _voiceInput, _wbInput, _personaInput, _asstNameInput,
                _asstFacePathInput, _asstPersonaInput, _ttsBase, _ttsKey, _ttsModel,
                _imageProvider, _imageEndpoint, _imageKey, _imageModel, _imageSize, _imageWorkflowPath, _imageStyle,
                _companionMonthlyCountInput })
                if (input != null) input.interactable = enabled;
            foreach (Button button in new[] { _profilePrevBtn, _profileNextBtn, _profileActivateBtn,
                _profileSaveBtn, _profileDeleteBtn, _diffBtn, _streamBtn, _replyLenBtn, _fontSizeBtn, _ghostwriteLenBtn, _ghostwriteLearningBtn, _thinkBtn,
                _aiEventBtn, _companionMonthlyBtn, _monthlyPopupBtn, _monthlyCandidateManagerBtn,
                _wbStructuredBtn, _asstEnabledBtn, _asstFreqBtn,
                _asstFaceResetBtn, _groupRoundsBtn, _npcProactiveEnabledBtn, _npcProactiveFreqBtn,
                _economyPresetBtn, _balancedPresetBtn,
                 _bestExperiencePresetBtn, _imageWatermarkBtn, _thinkingPromptBtn, _ttsDialogueOnlyBtn })
                if (button != null) button.interactable = enabled;
        }

        static void SetStatus(string text, bool error)
        {
            if (_status == null) return;
            _status.text = text;
            _status.color = error ? new Color(0.92f, 0.55f, 0.45f, 1f) : new Color(0.70f, 0.84f, 0.66f, 1f);
        }

        static void Build()
        {
            _root = new GameObject("JHYL_ConfigCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30010;
            PopupRegistry.Register(_root, Hide);   // 右键逐个关闭(从最上层依次)
            var sc = _root.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            // 八个设置页的字段和说明已明显增多；扩大实际内容区，避免长标签、输入框和
            // 页面底部选项互相挤压。1920×1080 参考分辨率下仍保留四周安全边距。
            prt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.10f, 0.98f);
            panel.AddComponent<DragMove>().target = prt;   // 拖整窗

            _title = NewText("Title", panel.transform, 24, TextAlignmentOptions.Center);
            Anchor(_title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(48, -8), new Vector2(-48, -48));
            _title.text = "江湖有灵 · 设置";

            var close = NewButton("Close", panel.transform, "X", 22, out var closeBtn);
            var crt = close.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1, 1);
            crt.anchoredPosition = new Vector2(-8, -8); crt.sizeDelta = new Vector2(36, 36);
            closeBtn.onClick.AddListener(Hide);

            // 左侧导航
            string[] navs = { "模型", "体验档位", "语音", "生图", "对话", "过月", "世界书", "人设", "助手" };
            _navImgs = new Image[navs.Length];
            float ny = -56f; const float navH = 50f;
            for (int i = 0; i < navs.Length; i++)
            {
                int idx = i;
                var nb = NewButton("Nav_" + navs[i], panel.transform, navs[i], 19, out var b);
                var nrt = nb.GetComponent<RectTransform>();
                nrt.anchorMin = nrt.anchorMax = nrt.pivot = new Vector2(0, 1);
                nrt.anchoredPosition = new Vector2(12, ny); nrt.sizeDelta = new Vector2(118, 44);
                nb.GetComponent<Image>().color = NavOff;
                _navImgs[i] = nb.GetComponent<Image>();
                if (idx == 6) b.onClick.AddListener(OpenStructuredWorldBook);
                else b.onClick.AddListener(() => ShowPane(idx));
                ny -= navH;
            }

            // 内容区容器(导航右侧,标题下,底栏上)
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(panel.transform, false);
            Anchor(content.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 1), new Vector2(144, 70), new Vector2(-14, -52));

            _panes = new GameObject[9];
            _panes[0] = BuildLlmPane(content.transform);
            _panes[1] = BuildExperiencePresetPane(content.transform);
            _panes[2] = BuildVoicePane(content.transform);
            _panes[3] = BuildImagePane(content.transform);
            _panes[4] = BuildTalkPane(content.transform);
            _panes[5] = BuildMonthlyPane(content.transform);
            // 世界书导航直接打开唯一的结构化编辑器；不再构建或短暂闪现旧纯文本页。
            _panes[6] = NewPane("Pane_WorldBookDirect", content.transform);
            _panes[7] = BuildPersonaPane(content.transform);
            _panes[8] = BuildAssistantPane(content.transform);

            // 状态行
            _status = NewText("Status", panel.transform, 16, TextAlignmentOptions.Left);
            Anchor(_status.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(16, 60), new Vector2(-16, 96));
            _status.enableWordWrapping = true;

            // 底栏:保存 / 测试 / 关闭
            var saveGo = NewButton("Save", panel.transform, "保存", 20, out _saveBtn);
            Anchor(saveGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 0), new Vector2(16, 12), new Vector2(176, 52));
            _saveBtn.onClick.AddListener(Save);
            var testGo = NewButton("Test", panel.transform, "测试接口", 20, out _testBtn);
            Anchor(testGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 0), new Vector2(192, 12), new Vector2(352, 52));
            _testBtn.onClick.AddListener(Test);
            var cb = NewButton("CloseBottom", panel.transform, "关闭", 20, out var cbBtn);
            Anchor(cb.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0), new Vector2(-176, 12), new Vector2(-16, 52));
            cbBtn.onClick.AddListener(Hide);
            _ttsDialogueOnlyBtn.navigation = new Navigation
            {
                mode = Navigation.Mode.Explicit,
                selectOnUp = _ttsModel, selectOnDown = _saveBtn,
                selectOnLeft = _navImgs[2].GetComponent<Button>(), selectOnRight = _saveBtn,
            };
        }

        // —— 模型页:baseUrl / apiKey / model + 推荐说明 ——
        static GameObject BuildLlmPane(Transform parent)
        {
            var pane = NewPane("Pane_Llm", parent);
            var profileTitle = NewText("ProfileTitle", pane.transform, 16, TextAlignmentOptions.Left);
            Anchor(profileTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, -28), new Vector2(-4, -6));
            profileTitle.text = "模型配置档案（密钥仍以 Windows DPAPI 加密保存）";
            profileTitle.color = new Color(0.80f, 0.78f, 0.70f, 1f);

            // Use ASCII arrows: the game's Font SDF GB2312 does not contain U+2039/U+203A,
            // which otherwise emits a TMP warning every time this pane is rendered.
            GameObject prev = NewButton("ProfilePrev", pane.transform, "<", 22, out _profilePrevBtn);
            Anchor(prev.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(4, -64), new Vector2(44, -32));
            _profilePrevBtn.onClick.AddListener(() => CycleLlmProfile(-1));
            _profileSelectionLabel = NewText("ProfileSelection", pane.transform, 16,
                TextAlignmentOptions.Center);
            Anchor(_profileSelectionLabel.rectTransform, new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(50, -64), new Vector2(300, -32));
            _profileSelectionLabel.color = new Color(0.88f, 0.84f, 0.70f, 1f);
            GameObject next = NewButton("ProfileNext", pane.transform, ">", 22, out _profileNextBtn);
            Anchor(next.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(306, -64), new Vector2(346, -32));
            _profileNextBtn.onClick.AddListener(() => CycleLlmProfile(1));
            GameObject activate = NewButton("ProfileActivate", pane.transform, "立即切换", 15,
                out _profileActivateBtn);
            Anchor(activate.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(354, -64), new Vector2(464, -32));
            _profileActivateBtn.onClick.AddListener(ActivateSelectedLlmProfile);
            GameObject delete = NewButton("ProfileDelete", pane.transform, "删除档案", 15,
                out _profileDeleteBtn);
            Anchor(delete.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(472, -64), new Vector2(574, -32));
            _profileDeleteBtn.onClick.AddListener(DeleteSelectedLlmProfile);

            var nameLabel = NewText("ProfileNameLabel", pane.transform, 15, TextAlignmentOptions.Left);
            Anchor(nameLabel.rectTransform, new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(4, -94), new Vector2(142, -72));
            nameLabel.text = "新档案名称";
            _profileNameInput = BuildSingleLine(pane.transform, -96f, 144f);
            GameObject saveProfile = NewButton("ProfileSave", pane.transform, "保存当前为档案", 15,
                out _profileSaveBtn);
            Anchor(saveProfile.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-136, -126), new Vector2(-4, -98));
            _profileSaveBtn.onClick.AddListener(SaveCurrentLlmProfile);

            float top = -138f; const float rowH = 64f;
            _baseUrl = BuildField(pane.transform, "接口地址 baseUrl", top, false); top -= rowH;
            _apiKey = BuildField(pane.transform, "密钥 apiKey(本地模型可空)", top, true); top -= rowH;
            _model = BuildField(pane.transform, "模型 model", top, false); top -= rowH;
            _bgModel = BuildField(pane.transform, "后台模型(画像/记忆精排/代笔用;过月固定主模型)", top, false); top -= rowH;
            _maxTokens = BuildField(pane.transform, "回复上限 max_tokens(默认32768;0=不限,由服务端定)", top, false); top -= rowH;
            _contextWindow = BuildField(pane.transform, "上下文窗口 context_window(默认1000000;小模型请填实际值)", top, false); top -= rowH;
            var rec = NewText("Rec", pane.transform, 13, TextAlignmentOptions.TopLeft);
            Anchor(rec.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, top - 148), new Vector2(-4, top - 4));
            rec.enableWordWrapping = true;
            rec.color = new Color(0.62f, 0.66f, 0.60f, 0.95f);
            rec.text =
                "【推荐】优先使用 DeepSeek：本 Mod 对它的工具调用、多轮行动、思考协议和缓存回归最完整；默认已配好主 " + DefaultModel + " + 后台 " + DefaultBgModel + "，申请 Key 填入即可开聊。\n" +
                "· 主模型挑【强】的(工具调用越稳越好):DeepSeek-v4-pro、Claude、GPT、Gemini、Qwen-Max、Kimi 等 —— 主对话与「真去办事」都靠它。\n" +
                "· 后台模型挑【快】的:DeepSeek-v4-flash、GPT / Gemini 的 mini·flash、Qwen-flash 等 —— 只分担画像、记忆精排、代笔等无副作用辅助任务;江湖事件和同道过月始终使用主模型。\n" +
                "· 上下文窗口默认 1000000；DeepSeek V4 Pro/Flash 按官方百万上下文使用。其他模型请按官方上限调整，8K/16K 本地模型务必填实际值。\n" +
                "· 也支持本地模型(baseUrl 填本地地址、apiKey 留空,建议 ≥7B 且支持工具调用)。千问新开源 Qwen3.8 的 Ollama 配置：baseUrl=http://127.0.0.1:11434，model=qwen3.8 或 qwen3.8:27b，apiKey 留空，上下文窗口填 262144；Mod 会自动使用稳定的原生聊天、思考与工具协议。\n" +
                "★ 不会配?把本页截图发给任意 AI 助手问怎么填,照着抄即可。";
            return pane;
        }

        // —— 语音页(可选):整体开关式——填了密钥才启用,音色/语速/语气全自动 ——
        static GameObject BuildVoicePane(Transform parent)
        {
            var pane = NewPane("Pane_Voice", parent);
            float top = -8f; const float rowH = 64f;
            _ttsBase = BuildField(pane.transform, "火山 Seed-TTS 2.0 接口（默认）", top, false); top -= rowH;
            _ttsKey = BuildField(pane.transform, "语音密钥 apiKey", top, true); top -= rowH;
            _ttsModel = BuildField(pane.transform, "语音资源模型（默认 seed-tts-2.0）", top, false); top -= rowH;

            var contentLabel = NewText("SpeechContentLabel", pane.transform, 17, TextAlignmentOptions.Left);
            Anchor(contentLabel.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, top - 42), new Vector2(-180, top - 8));
            contentLabel.text = "朗读内容（默认全文）";
            contentLabel.color = new Color(0.80f, 0.78f, 0.70f, 1f);
            var contentGo = NewButton("SpeechContent", pane.transform, "全文", 18, out _ttsDialogueOnlyBtn);
            Anchor(contentGo.GetComponent<RectTransform>(), Vector2.one, Vector2.one,
                new Vector2(-164, top - 42), new Vector2(-4, top - 8));
            var contentColors = _ttsDialogueOnlyBtn.colors;
            contentColors.selectedColor = contentColors.highlightedColor = new Color(1.3f, 1.3f, 1.2f, 1f);
            _ttsDialogueOnlyBtn.colors = contentColors;
            _ttsDialogueOnlyBtn.onClick.AddListener(() =>
            {
                _ttsDialogueOnly = !_ttsDialogueOnly;
                SetTtsDialogueOnlyLabel();
                SetStatus("朗读内容已选择「" + (_ttsDialogueOnly ? "仅说话" : "全文") + "」，点击保存后生效。", false);
            });
            top -= rowH;

            var note = NewText("VoiceNote", pane.transform, 13, TextAlignmentOptions.TopLeft);
            Anchor(note.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, top - 210), new Vector2(-4, top - 4));
            note.enableWordWrapping = true; note.color = new Color(0.62f, 0.66f, 0.60f, 0.95f);
            note.text =
                "语音朗读是【可选】功能:不填密钥就不启用，聊天照常使用。\n" +
                "“全文”沿用原有朗读；“仅说话”只读聊天中以说话颜色显示的内容，跳过旁白。单聊、群聊与灵儿统一生效；没有说话内容时只提示、不合成语音。选择后需点击保存。\n" +
                "默认火山 Seed-TTS 2.0：V3 接口与 seed-tts-2.0 已填好，只需填写新版豆包语音控制台的 API Key。会按人物性别、年龄和性格选择 2.0 音色，并让语气、重音和语速随台词动态变化。\n" +
                "仍支持 MiniMax、OpenAI 兼容 /audio/speech 与百炼/通义非实时 Qwen TTS；切换时填写对应接口和模型。";
            return pane;
        }

        static void SetTtsDialogueOnlyLabel()
            => SetButtonText(_ttsDialogueOnlyBtn, _ttsDialogueOnly ? "仅说话" : "全文");

        // —— 生图页:独立凭据；云端协议与本机 ComfyUI 均由 main 自己适配 ——
        static GameObject BuildImagePane(Transform parent)
        {
            var pane = NewPane("Pane_Image", parent);
            float top = -8f; const float rowH = 64f;
            _imageProvider = BuildField(pane.transform, "生图协议（doubao-ark / openai / openrouter / comfyui）", top, false); top -= rowH;
            _imageEndpoint = BuildField(pane.transform, "生图接口 endpoint", top, false); top -= rowH;
            _imageKey = BuildField(pane.transform, "生图密钥 API Key（独立加密保存）", top, true); top -= rowH;
            _imageModel = BuildField(pane.transform, "生图模型 model（ComfyUI 未用模型标记可空）", top, false); top -= rowH;
            _imageSize = BuildField(pane.transform, "输出尺寸（固定横向 16:9）", top, false);
            _imageSize.readOnly = true;
            top -= rowH;
            _imageWorkflowPath = BuildField(pane.transform, "ComfyUI 工作流 API JSON 路径（仅本地模式）", top, false);
            top -= rowH;
            _imageProvider.onValueChanged.AddListener(provider =>
            {
                if (_imageSize != null) _imageSize.text = ImageGenerationClient.FixedSizeForProvider(provider);
            });

            var watermarkLabel = NewText("ImageWatermarkLabel", pane.transform, 17, TextAlignmentOptions.Left);
            Anchor(watermarkLabel.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, top - 34), new Vector2(-160, top - 8));
            watermarkLabel.text = "生成图片水印（默认关闭）";
            watermarkLabel.color = new Color(0.80f, 0.78f, 0.70f, 1f);
            var watermarkGo = NewButton("ImageWatermark", pane.transform, "关", 18, out _imageWatermarkBtn);
            var watermarkRt = watermarkGo.GetComponent<RectTransform>();
            watermarkRt.anchorMin = watermarkRt.anchorMax = watermarkRt.pivot = new Vector2(1, 1);
            watermarkRt.anchoredPosition = new Vector2(-4, top - 8);
            watermarkRt.sizeDelta = new Vector2(130, 34);
            _imageWatermarkBtn.onClick.AddListener(() =>
            {
                _imageWatermark = !_imageWatermark;
                SetImageWatermarkLabel();
            });
            top -= 54f;

            var styleLabel = NewText("ImageStyleLabel", pane.transform, 16, TextAlignmentOptions.Left);
            Anchor(styleLabel.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, top - 28), new Vector2(-4, top - 4));
            styleLabel.text = "附加画面风格（可空；人物、动作与场景仍由当前聊天上下文决定）";
            styleLabel.color = new Color(0.80f, 0.78f, 0.70f, 1f);
            _imageStyle = BuildFixedMultiline(pane.transform, top - 32f, 116f,
                ImageGenerationConfig.MaxStyleChars);
            top -= 162f;

            var note = NewText("ImageNote", pane.transform, 13, TextAlignmentOptions.TopLeft);
            Anchor(note.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, top - 100), new Vector2(-4, top - 2));
            note.enableWordWrapping = true;
            note.color = new Color(0.62f, 0.66f, 0.60f, 0.95f);
            note.text =
                "默认使用豆包方舟：接口与 Seedream 5.0 Pro 模型已填好，只需填写 API Key。云端输出固定 2048×1152；本机 ComfyUI 默认 1024×576，均为横向 16:9。聊天中完整等比居中显示，绝不拉伸或裁切。点击回话旁的“生图”时，系统会截图 NPC 与太吾并合成参考图，再结合最近聊天生成当前场景。\n" +
                "本机图生图：provider 填 comfyui，endpoint 填 http://127.0.0.1:8188，API Key 可空；工作流必须从 ComfyUI 导出为 API 格式，并包含 {{JHYL_PROMPT}}、{{JHYL_REFERENCE_IMAGE}}、{{JHYL_WIDTH}}、{{JHYL_HEIGHT}}，也兼容对应的单层花括号写法，模型标记 {{JHYL_MODEL}} 可选。OpenAI 官方配置为 endpoint=https://api.openai.com/v1/images/edits、model=gpt-image-2；本机兼容接口可使用同一 openai 协议且密钥可空。不会复用聊天或配音密钥。";
            return pane;
        }

        static void SetImageWatermarkLabel()
        {
            if (_imageWatermarkBtn != null)
                _imageWatermarkBtn.GetComponentInChildren<TextMeshProUGUI>().text = _imageWatermark ? "开" : "关";
        }

        // —— 对话页:难度 / 篇幅 / 流式 ——
        static GameObject BuildTalkPane(Transform parent)
        {
            var pane = NewPane("Pane_Talk", parent);

            var lc = new Color(0.80f, 0.78f, 0.70f, 1f);

            // 难度(标签 + 右侧循环按钮)
            var diffLbl = NewText("Lbl_Diff", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(diffLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -32), new Vector2(-160, -8));
            diffLbl.text = "难度(NPC 答应你请求的难易)"; diffLbl.color = lc;
            var diffGo = NewButton("Diff", pane.transform, _difficulty, 18, out _diffBtn);
            var diffRt = diffGo.GetComponent<RectTransform>();
            diffRt.anchorMin = diffRt.anchorMax = diffRt.pivot = new Vector2(1, 1);
            diffRt.anchoredPosition = new Vector2(-4, -8); diffRt.sizeDelta = new Vector2(130, 34);
            _diffBtn.onClick.AddListener(CycleDifficulty);

            // 回复篇幅(标签 + 右侧循环按钮:简短/适中/详细/不限)
            var rlLbl = NewText("Lbl_ReplyLen", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(rlLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -76), new Vector2(-160, -52));
            rlLbl.text = "回复篇幅(NPC 回话长短)"; rlLbl.color = lc;
            var rlGo = NewButton("ReplyLen", pane.transform, ReplyLenStore.Label(_replyLen), 18, out _replyLenBtn);
            var rlRt = rlGo.GetComponent<RectTransform>();
            rlRt.anchorMin = rlRt.anchorMax = rlRt.pivot = new Vector2(1, 1);
            rlRt.anchoredPosition = new Vector2(-4, -52); rlRt.sizeDelta = new Vector2(130, 34);
            _replyLenBtn.onClick.AddListener(CycleReplyLen);

            // 字体大小(当前既有字号=小号默认；切换会立即刷新所有已打开的 Mod 界面)
            var fontSizeLbl = NewText("Lbl_FontSize", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(fontSizeLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, -120), new Vector2(-160, -96));
            fontSizeLbl.text = "字体大小（江湖有灵全部界面，小号默认）";
            fontSizeLbl.color = lc;
            var fontSizeGo = NewButton("FontSize", pane.transform,
                UiFontSizeStore.Label(_fontSize), 18, out _fontSizeBtn);
            var fontSizeRt = fontSizeGo.GetComponent<RectTransform>();
            fontSizeRt.anchorMin = fontSizeRt.anchorMax = fontSizeRt.pivot = new Vector2(1, 1);
            fontSizeRt.anchoredPosition = new Vector2(-4, -96);
            fontSizeRt.sizeDelta = new Vector2(130, 34);
            _fontSizeBtn.onClick.AddListener(CycleFontSize);

            // 太吾口吻(标签 + 单行输入)
            var voiceLbl = NewText("Lbl_Voice", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(voiceLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -172), new Vector2(-4, -148));
            voiceLbl.text = "太吾口吻（手动偏好优先于自学习；空=默认）"; voiceLbl.color = lc;
            _voiceInput = BuildSingleLine(pane.transform, -174);

            var ghostwriteLenLbl = NewText("Lbl_GhostwriteLen", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(ghostwriteLenLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, -242), new Vector2(-160, -218));
            ghostwriteLenLbl.text = "代笔篇幅（自学习模仿实际字数，但不会超过此上限）";
            ghostwriteLenLbl.color = lc;
            var ghostwriteLenGo = NewButton("GhostwriteLen", pane.transform,
                GhostwriteLengthStore.Label(_ghostwriteLen), 18, out _ghostwriteLenBtn);
            var ghostwriteLenRt = ghostwriteLenGo.GetComponent<RectTransform>();
            ghostwriteLenRt.anchorMin = ghostwriteLenRt.anchorMax = ghostwriteLenRt.pivot = new Vector2(1, 1);
            ghostwriteLenRt.anchoredPosition = new Vector2(-4, -218);
            ghostwriteLenRt.sizeDelta = new Vector2(130, 34);
            _ghostwriteLenBtn.onClick.AddListener(CycleGhostwriteLen);

            var ghostwriteLearningLbl = NewText("Lbl_GhostwriteLearning", pane.transform, 18,
                TextAlignmentOptions.Left);
            Anchor(ghostwriteLearningLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, -286), new Vector2(-160, -262));
            ghostwriteLearningLbl.text = "代笔自学习（模仿玩家用词、内容与字数，默认开启）";
            ghostwriteLearningLbl.color = lc;
            var ghostwriteLearningGo = NewButton("GhostwriteLearning", pane.transform,
                _ghostwriteLearning ? "开" : "关", 18, out _ghostwriteLearningBtn);
            var ghostwriteLearningRt = ghostwriteLearningGo.GetComponent<RectTransform>();
            ghostwriteLearningRt.anchorMin = ghostwriteLearningRt.anchorMax =
                ghostwriteLearningRt.pivot = new Vector2(1, 1);
            ghostwriteLearningRt.anchoredPosition = new Vector2(-4, -262);
            ghostwriteLearningRt.sizeDelta = new Vector2(130, 34);
            _ghostwriteLearningBtn.onClick.AddListener(ToggleGhostwriteLearning);

            // 流式输出(标签 + 右侧开关)
            var streamLbl = NewText("Lbl_Stream", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(streamLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -330), new Vector2(-160, -306));
            streamLbl.text = "流式输出(思考过程+工具调用+逐字)"; streamLbl.color = lc;
            var streamGo = NewButton("Stream", pane.transform, _stream ? "开" : "关", 18, out _streamBtn);
            var srt = streamGo.GetComponent<RectTransform>();
            srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(1, 1);
            srt.anchoredPosition = new Vector2(-4, -306); srt.sizeDelta = new Vector2(130, 34);
            _streamBtn.onClick.AddListener(ToggleStream);

            // 显示思考过程(标签 + 右侧开关;与流式解耦,默认开)
            var thinkLbl = NewText("Lbl_Think", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(thinkLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -374), new Vector2(-160, -350));
            thinkLbl.text = "显示思量(仅显示已有思考,不强制每轮产生)"; thinkLbl.color = lc;
            var thinkGo = NewButton("Think", pane.transform, _showThink ? "开" : "关", 18, out _thinkBtn);
            var trt = thinkGo.GetComponent<RectTransform>();
            trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(1, 1);
            trt.anchoredPosition = new Vector2(-4, -350); trt.sizeDelta = new Vector2(130, 34);
            _thinkBtn.onClick.AddListener(ToggleThink);

            // 群聊轮数(标签 + 右侧循环按钮 1~3)
            var grLbl = NewText("Lbl_GroupRounds", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(grLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -418), new Vector2(-160, -394));
            grLbl.text = "群聊自动轮数(每条玩家消息后最多3轮,默认1)"; grLbl.color = lc;
            var grGo = NewButton("GroupRounds", pane.transform, _groupRounds + " 轮", 18, out _groupRoundsBtn);
            var grRt = grGo.GetComponent<RectTransform>();
            grRt.anchorMin = grRt.anchorMax = grRt.pivot = new Vector2(1, 1);
            grRt.anchoredPosition = new Vector2(-4, -394); grRt.sizeDelta = new Vector2(130, 34);
            _groupRoundsBtn.onClick.AddListener(CycleGroupRounds);

            var npcActiveLbl = NewText("Lbl_NpcProactiveEnabled", pane.transform, 18,
                TextAlignmentOptions.Left);
            Anchor(npcActiveLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, -462), new Vector2(-160, -438));
            npcActiveLbl.text = "主动名单人物来信（开 / 关闭）";
            npcActiveLbl.color = lc;
            var npcActiveGo = NewButton("NpcProactiveEnabled", pane.transform,
                _npcProactiveEnabled ? "开" : "关", 18, out _npcProactiveEnabledBtn);
            var npcActiveRt = npcActiveGo.GetComponent<RectTransform>();
            npcActiveRt.anchorMin = npcActiveRt.anchorMax = npcActiveRt.pivot = new Vector2(1, 1);
            npcActiveRt.anchoredPosition = new Vector2(-4, -438);
            npcActiveRt.sizeDelta = new Vector2(130, 34);
            _npcProactiveEnabledBtn.onClick.AddListener(ToggleNpcProactiveEnabled);

            var npcFreqLbl = NewText("Lbl_NpcProactiveFreq", pane.transform, 18,
                TextAlignmentOptions.Left);
            Anchor(npcFreqLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, -506), new Vector2(-160, -482));
            npcFreqLbl.text = "主动频率（极低30-40 / 低10-20 / 中3-7 / 高1-2分）";
            npcFreqLbl.color = lc;
            var npcFreqGo = NewButton("NpcProactiveFreq", pane.transform,
                NpcProactiveChatStore.Label(_npcProactiveFreq), 18, out _npcProactiveFreqBtn);
            var npcFreqRt = npcFreqGo.GetComponent<RectTransform>();
            npcFreqRt.anchorMin = npcFreqRt.anchorMax = npcFreqRt.pivot = new Vector2(1, 1);
            npcFreqRt.anchoredPosition = new Vector2(-4, -482);
            npcFreqRt.sizeDelta = new Vector2(130, 34);
            _npcProactiveFreqBtn.onClick.AddListener(CycleNpcProactiveFrequency);

            var thinkingPromptLbl = NewText("Lbl_ThinkingPrompt", pane.transform, 18,
                TextAlignmentOptions.Left);
            Anchor(thinkingPromptLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, -550), new Vector2(-160, -526));
            thinkingPromptLbl.text = "思考表达提示词（点击编辑）";
            thinkingPromptLbl.color = lc;
            var thinkingPromptGo = NewButton("ThinkingPrompt", pane.transform,
                "思考表达", 17, out _thinkingPromptBtn);
            var thinkingPromptRt = thinkingPromptGo.GetComponent<RectTransform>();
            thinkingPromptRt.anchorMin = thinkingPromptRt.anchorMax =
                thinkingPromptRt.pivot = new Vector2(1, 1);
            thinkingPromptRt.anchoredPosition = new Vector2(-4, -526);
            thinkingPromptRt.sizeDelta = new Vector2(130, 34);
            ChatTab.ApplyRoundedSkin(thinkingPromptGo.GetComponent<Image>());
            _thinkingPromptBtn.onClick.AddListener(() =>
                ThinkingPromptEditorWindow.Show(_font));

            var note = NewText("StreamNote", pane.transform, 13, TextAlignmentOptions.TopLeft);
            Anchor(note.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, -700), new Vector2(-4, -578));
            note.enableWordWrapping = true; note.color = new Color(0.58f, 0.62f, 0.56f, 0.92f);
            note.text = "思量会动态变化：普通闲聊可能少想或不显示，行动、工具链与失败后重规划通常会深入思考；开关只控制是否展示模型实际返回的思考。模型未返回思考，或流式异常自动降为整段显示时，也不会出现思量文字，这不是正文丢失。群聊轮数越多越热闹，也更耗费。NPC 主动消息只从主动人物名单与聊过对象的交集中选择，沿用普通单聊的实时人物、近期见闻、长期记忆、世界书与位置上下文；灵儿只复用原有主动气泡发出本地提醒，不额外消耗 Token。主动来信与过月主动行事可分别关闭，历史消息默认均已读。";
            return pane;
        }

        static void SetNpcProactiveEnabledLabel()
        {
            if (_npcProactiveEnabledBtn == null) return;
            var text = _npcProactiveEnabledBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = _npcProactiveEnabled ? "开" : "关";
        }

        static void SetNpcProactiveFreqLabel()
        {
            if (_npcProactiveFreqBtn == null) return;
            var text = _npcProactiveFreqBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = NpcProactiveChatStore.Label(_npcProactiveFreq);
        }

        static void ToggleNpcProactiveEnabled()
        {
            bool next = !_npcProactiveEnabled;
            if (!NpcProactiveChatStore.SaveEnabled(next))
            {
                _npcProactiveEnabled = NpcProactiveChatStore.LoadEnabled();
                SetNpcProactiveEnabledLabel();
                SetStatus("NPC 主动消息开关未得到完整耐久确认，已恢复磁盘实际值。", true);
                return;
            }
            _npcProactiveEnabled = next;
            SetNpcProactiveEnabledLabel();
            NpcProactiveChatScheduler.NotifySettingsChanged();
            SetStatus(next
                ? "已开启聊过 NPC 的主动消息，当前频率「"
                    + NpcProactiveChatStore.Label(_npcProactiveFreq) + "」。"
                : "已关闭聊过 NPC 的主动消息。", false);
        }

        static void CycleNpcProactiveFrequency()
        {
            int next = _npcProactiveFreq >= NpcProactiveChatStore.High
                ? NpcProactiveChatStore.VeryLow : _npcProactiveFreq + 1;
            if (!NpcProactiveChatStore.SaveFrequency(next))
            {
                _npcProactiveFreq = NpcProactiveChatStore.LoadFrequency();
                SetNpcProactiveFreqLabel();
                SetStatus("NPC 主动消息频率未得到完整耐久确认，已恢复磁盘实际值。", true);
                return;
            }
            _npcProactiveFreq = next;
            SetNpcProactiveFreqLabel();
            NpcProactiveChatScheduler.NotifySettingsChanged();
            SetStatus("NPC 主动消息频率设为「" + NpcProactiveChatStore.Label(next) + "」。", false);
        }

        // —— 体验档位页：只展示并切换过月、灵儿主动频率、群聊自动轮数与默认同道数量。——
        static GameObject BuildExperiencePresetPane(Transform parent)
        {
            var pane = NewPane("Pane_ExperiencePreset", parent);
            var title = NewText("PresetTitle", pane.transform, 23, TextAlignmentOptions.Center);
            Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -44), new Vector2(-4, -8));
            title.text = "选择你的江湖体验";
            title.fontStyle = FontStyles.Bold;
            title.color = new Color(0.88f, 0.82f, 0.65f, 1f);

            _presetSelectionHint = NewText("PresetSelectionHint", pane.transform, 16, TextAlignmentOptions.Center);
            Anchor(_presetSelectionHint.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(8, -78), new Vector2(-8, -50));
            _presetSelectionHint.color = new Color(0.70f, 0.74f, 0.68f, 1f);

            ExperiencePresetPolicy.TryGet(ExperiencePresetPolicy.EconomyId, out ExperiencePresetDefinition economy);
            ExperiencePresetPolicy.TryGet(ExperiencePresetPolicy.BalancedId, out ExperiencePresetDefinition balanced);
            ExperiencePresetPolicy.TryGet(ExperiencePresetPolicy.BestId, out ExperiencePresetDefinition best);

            BuildPresetColumn(pane.transform, "EconomyCard", 4f, 204f, economy.DisplayName,
                "适合控制消耗",
                PresetSettingsText(economy, false),
                new Color(0.22f, 0.28f, 0.27f, 0.98f), false, out _economyPresetBtn);
            _economyPresetBtn.onClick.AddListener(() => ApplyMonthlyPreset(economy.Id));

            BuildPresetColumn(pane.transform, "BalancedCard", 216f, 416f, balanced.DisplayName,
                "兼顾热闹与消耗",
                PresetSettingsText(balanced, false),
                new Color(0.26f, 0.34f, 0.32f, 0.98f), false, out _balancedPresetBtn);
            _balancedPresetBtn.onClick.AddListener(() => ApplyMonthlyPreset(balanced.Id));

            BuildPresetColumn(pane.transform, "BestCard", 428f, 632f, "★ " + best.DisplayName,
                "完整的活江湖",
                PresetSettingsText(best, false),
                new Color(0.47f, 0.32f, 0.12f, 0.99f), true, out _bestExperiencePresetBtn);
            _bestExperiencePresetBtn.onClick.AddListener(() => ApplyMonthlyPreset(best.Id));

            var foot = NewText("PresetFoot", pane.transform, 16, TextAlignmentOptions.Center);
            Anchor(foot.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(8, 10), new Vector2(-8, 64));
            foot.enableWordWrapping = true;
            foot.color = new Color(0.64f, 0.68f, 0.61f, 0.96f);
            foot.text = "档位只改上面列出的五项，不会改接口、模型、密钥、上下文窗口、回复上限、人设或世界书。开启的过月始终使用主模型。";
            RefreshPresetSelectionHint();
            return pane;
        }

        static void BuildPresetColumn(Transform parent, string name, float left, float right,
            string heading, string subheading, string settings, Color background, bool featured,
            out Button button)
        {
            var card = new GameObject(name, typeof(RectTransform), typeof(Image));
            card.transform.SetParent(parent, false);
            Anchor(card.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 1),
                new Vector2(left, 78), new Vector2(right, -92));
            card.GetComponent<Image>().color = background;

            var headingText = NewText(name + "Heading", card.transform, featured ? 22 : 21, TextAlignmentOptions.Center);
            Anchor(headingText.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(8, -54), new Vector2(-8, -14));
            headingText.text = heading;
            headingText.fontStyle = FontStyles.Bold;
            headingText.color = featured ? new Color(1f, 0.91f, 0.55f, 1f) : new Color(0.88f, 0.84f, 0.72f, 1f);

            var sub = NewText(name + "Subheading", card.transform, 16, TextAlignmentOptions.Center);
            Anchor(sub.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(8, -90), new Vector2(-8, -58));
            sub.text = subheading;
            sub.color = featured ? new Color(0.96f, 0.81f, 0.43f, 1f) : new Color(0.65f, 0.71f, 0.66f, 1f);

            var details = NewText(name + "Details", card.transform, 17, TextAlignmentOptions.TopLeft);
            Anchor(details.rectTransform, new Vector2(0, 0), new Vector2(1, 1), new Vector2(20, 96), new Vector2(-16, -112));
            details.enableWordWrapping = true;
            details.text = settings;
            details.lineSpacing = 18f;
            details.color = new Color(0.88f, 0.87f, 0.80f, 1f);

            var buttonGo = NewButton(name + "Select", card.transform, featured ? "选择最佳体验" : "选择此档", 17, out button);
            Anchor(buttonGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 0), new Vector2(16, 18), new Vector2(-16, 66));
            if (featured)
            {
                var image = buttonGo.GetComponent<Image>();
                if (image != null) image.color = new Color(0.78f, 0.53f, 0.16f, 1f);
                var text = buttonGo.GetComponentInChildren<TextMeshProUGUI>();
                if (text != null) { text.fontStyle = FontStyles.Bold; text.color = new Color(1f, 0.97f, 0.78f, 1f); }
            }
        }

        static GameObject BuildMonthlyPane(Transform parent)
        {
            var pane = NewPane("Pane_Monthly", parent);
            var lc = new Color(0.80f, 0.78f, 0.70f, 1f);

            var aiEvLbl = NewText("Lbl_AiEvent", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(aiEvLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -42), new Vector2(-160, -14));
            aiEvLbl.text = "每月 AI 江湖事件(默认开)"; aiEvLbl.color = lc;
            var aiEvGo = NewButton("AiEvent", pane.transform, _aiEvent ? "开" : "关", 18, out _aiEventBtn);
            var art = aiEvGo.GetComponent<RectTransform>();
            art.anchorMin = art.anchorMax = art.pivot = new Vector2(1, 1);
            art.anchoredPosition = new Vector2(-4, -14); art.sizeDelta = new Vector2(130, 34);
            _aiEventBtn.onClick.AddListener(ToggleAiEvent);

            var cmLbl = NewText("Lbl_CompanionMonthly", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(cmLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -96), new Vector2(-160, -68));
            cmLbl.text = "过月主动行事(从主动人物名单抽取,默认开)"; cmLbl.color = lc;
            var cmGo = NewButton("CompanionMonthly", pane.transform, _companionMonthly ? "开" : "关", 18, out _companionMonthlyBtn);
            var cmrt = cmGo.GetComponent<RectTransform>();
            cmrt.anchorMin = cmrt.anchorMax = cmrt.pivot = new Vector2(1, 1);
            cmrt.anchoredPosition = new Vector2(-4, -68); cmrt.sizeDelta = new Vector2(130, 34);
            _companionMonthlyBtn.onClick.AddListener(ToggleCompanionMonthly);

            var popupLbl = NewText("Lbl_MonthlyPopup", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(popupLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -150), new Vector2(-160, -122));
            popupLbl.text = "过月时自动打开对应月份纪事(默认关)"; popupLbl.color = lc;
            var popupGo = NewButton("MonthlyPopup", pane.transform, _monthlyPopup ? "开" : "关", 18, out _monthlyPopupBtn);
            var popupRt = popupGo.GetComponent<RectTransform>();
            popupRt.anchorMin = popupRt.anchorMax = popupRt.pivot = new Vector2(1, 1);
            popupRt.anchoredPosition = new Vector2(-4, -122); popupRt.sizeDelta = new Vector2(130, 34);
            _monthlyPopupBtn.onClick.AddListener(ToggleMonthlyPopup);

            var countLbl = NewText("Lbl_CompanionMonthlyCount", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(countLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -208), new Vector2(-4, -180));
            countLbl.text = "每月随机触发主动人物数量(0～8，默认 3；改完点保存)"; countLbl.color = lc;
            _companionMonthlyCountInput = BuildSingleLine(pane.transform, -212, 210f);
            _companionMonthlyCountInput.contentType = TMP_InputField.ContentType.IntegerNumber;
            _companionMonthlyCountInput.characterLimit = 10;

            var manageGo = NewButton("ManageMonthlyCandidates", pane.transform, "管理主动人物名单", 16,
                out _monthlyCandidateManagerBtn);
            var manageRt = manageGo.GetComponent<RectTransform>();
            manageRt.anchorMin = manageRt.anchorMax = manageRt.pivot = new Vector2(1, 1);
            manageRt.anchoredPosition = new Vector2(-4, -212);
            manageRt.sizeDelta = new Vector2(190, 34);
            _monthlyCandidateManagerBtn.onClick.AddListener(() =>
            {
                int taiwu = _taiwuId > 0 ? _taiwuId : ResolveTaiwuId();
                if (taiwu <= 0 || !WorldLifecycle.HasWorldIdentity)
                { SetStatus("请先进入存档，再管理主动人物名单。", true); return; }
                MonthlyCandidateWindow.Open(taiwu, _font);
            });

            var note = NewText("MonthlyNote", pane.transform, 14, TextAlignmentOptions.TopLeft);
            Anchor(note.rectTransform, new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(4, 8), new Vector2(-4, -260));
            note.enableWordWrapping = true;
            note.color = new Color(0.64f, 0.68f, 0.61f, 0.96f);
            note.text =
                "江湖事件可参与并影响后续走向；同道会按长期记忆、显著关系、人设和现场真正行事，过月不预载心系之人。"
                + "开启的过月固定使用主模型；填写数量超过同道总数时自动按全部触发。关闭自动弹窗不影响生成，右侧纪事按钮会保留未读提醒。\n\n"
                + "江湖事件和人物主动行事均以真实工具结果为准；展示正文只负责回看，不参与事实判定，也不会写入人物记忆。";
            return pane;
        }

        static string AsstFreqLabel(int lv) { switch (lv) { case 0: return "关"; case 1: return "低"; case 3: return "高"; default: return "中"; } }
        static void SetAsstEnabledLabel() { if (_asstEnabledBtn != null) { var t = _asstEnabledBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = _asstEnabled ? "开" : "关"; } }
        static void SetAsstFreqLabel() { if (_asstFreqBtn != null) { var t = _asstFreqBtn.GetComponentInChildren<TextMeshProUGUI>(); if (t != null) t.text = AsstFreqLabel(_asstFreq); } }
        static void SetAsstCommissionIntervalLabel()
        {
            if (_asstCommissionIntervalBtn == null) return;
            var text = _asstCommissionIntervalBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = AssistantCommissionIntervalStore.Label(
                _asstCommissionInterval);
        }
        static void ToggleAsstEnabled()
        {
            bool next = !_asstEnabled;
            if (!AssistantEnabledStore.Save(next))
            {
                _asstEnabled = AssistantEnabledStore.Load(); SetAsstEnabledLabel();
                SetStatus("助手开关未得到完整耐久确认；已重新读取磁盘实际值「" + (_asstEnabled ? "开" : "关") + "」。", true); return;
            }
            _asstEnabled = next; SetAsstEnabledLabel();
            AssistantWidget.NotifySettingsChanged();
            SetStatus(_asstEnabled ? "已开启悬浮助手(进存档后显示)" : "已关闭悬浮助手", false);
        }
        static void CycleAsstFreq()
        {
            int next = (_asstFreq + 1) % 4;
            if (!AssistantProactiveStore.Save(next))
            {
                _asstFreq = AssistantProactiveStore.Load(); SetAsstFreqLabel();
                SetStatus("助手主动频率未得到完整耐久确认；已重新读取磁盘实际值「" + AsstFreqLabel(_asstFreq) + "」。", true); return;
            }
            _asstFreq = next; SetAsstFreqLabel();
            RefreshPresetSelectionHint();
            AssistantWidget.NotifySettingsChanged();
            SetStatus("助手主动消息频率设为「" + AsstFreqLabel(_asstFreq) + "」", false);
        }

        static void CycleAsstCommissionInterval()
        {
            int next = (_asstCommissionInterval + 1) % 4;
            if (!AssistantCommissionIntervalStore.Save(next))
            {
                _asstCommissionInterval = AssistantCommissionIntervalStore.Load();
                SetAsstCommissionIntervalLabel();
                SetStatus("灵儿委托发送间隔未得到完整耐久确认；已恢复磁盘实际值。", true);
                return;
            }
            _asstCommissionInterval = next;
            SetAsstCommissionIntervalLabel();
            AssistantWidget.NotifySettingsChanged();
            SetStatus("灵儿委托发送频率设为「"
                + AssistantCommissionIntervalStore.Label(_asstCommissionInterval) + "」。", false);
        }

        static GameObject BuildAssistantPane(Transform parent)
        {
            var pane = NewPane("Pane_Asst", parent);
            var lc = new Color(0.80f, 0.78f, 0.70f, 1f);

            // 助手总开关(标签 + 右侧开关)
            var enLbl = NewText("Lbl_AsstEn", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(enLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -32), new Vector2(-160, -8));
            enLbl.text = "悬浮助手「灵儿」(关=不显示、不主动)"; enLbl.color = lc;
            var enGo = NewButton("AsstEn", pane.transform, _asstEnabled ? "开" : "关", 18, out _asstEnabledBtn);
            var enRt = enGo.GetComponent<RectTransform>(); enRt.anchorMin = enRt.anchorMax = enRt.pivot = new Vector2(1, 1);
            enRt.anchoredPosition = new Vector2(-4, -8); enRt.sizeDelta = new Vector2(130, 34);
            _asstEnabledBtn.onClick.AddListener(ToggleAsstEnabled);

            // 主动消息频率(标签 + 右侧循环:关/低/中/高)
            var fLbl = NewText("Lbl_AsstFreq", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(fLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -76), new Vector2(-160, -52));
            fLbl.text = "她主动找你的频率(真实时间)"; fLbl.color = lc;
            var fGo = NewButton("AsstFreq", pane.transform, AsstFreqLabel(_asstFreq), 18, out _asstFreqBtn);
            var fRt = fGo.GetComponent<RectTransform>(); fRt.anchorMin = fRt.anchorMax = fRt.pivot = new Vector2(1, 1);
            fRt.anchoredPosition = new Vector2(-4, -52); fRt.sizeDelta = new Vector2(130, 34);
            _asstFreqBtn.onClick.AddListener(CycleAsstFreq);

            // 委托发送使用独立现实时间排期，不随游戏月份或主动聊天频率变化。
            var commissionLbl = NewText("Lbl_AsstCommissionInterval", pane.transform, 18,
                TextAlignmentOptions.Left);
            Anchor(commissionLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, -120), new Vector2(-160, -96));
            commissionLbl.text = "灵儿委托发送频率（现实时间：低90 / 中60 / 高30分）";
            commissionLbl.color = lc;
            var commissionGo = NewButton("AsstCommissionInterval", pane.transform,
                AssistantCommissionIntervalStore.Label(_asstCommissionInterval), 18,
                out _asstCommissionIntervalBtn);
            var commissionRt = commissionGo.GetComponent<RectTransform>();
            commissionRt.anchorMin = commissionRt.anchorMax = commissionRt.pivot = new Vector2(1, 1);
            commissionRt.anchoredPosition = new Vector2(-4, -96);
            commissionRt.sizeDelta = new Vector2(130, 34);
            _asstCommissionIntervalBtn.onClick.AddListener(CycleAsstCommissionInterval);

            // 助手名字(标签 + 单行输入)
            var nLbl = NewText("Lbl_AsstName", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(nLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -152), new Vector2(-4, -132));
            nLbl.text = "助手名字(默认 灵儿)"; nLbl.color = lc;
            _asstNameInput = BuildSingleLine(pane.transform, -154);

            var faceLbl = NewText("Lbl_AsstFace", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(faceLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -210), new Vector2(-4, -190));
            faceLbl.text = "灵儿形象图片路径(png/jpg,留空=内置默认形象)"; faceLbl.color = lc;
            var resetGo = NewButton("AsstFaceReset", pane.transform, "恢复默认", 14, out _asstFaceResetBtn);
            var resetRt = resetGo.GetComponent<RectTransform>(); resetRt.anchorMin = resetRt.anchorMax = resetRt.pivot = new Vector2(1, 1);
            resetRt.anchoredPosition = new Vector2(-4, -212); resetRt.sizeDelta = new Vector2(120, 28);
            _asstFaceResetBtn.onClick.AddListener(ResetAssistantFace);
            _asstFacePathInput = BuildSingleLine(pane.transform, -212, 132f);

            // 灵儿人设(多行,填到底):留空=用内置默认;改完点下方「保存」
            var pLbl = NewText("Lbl_AsstPersona", pane.transform, 18, TextAlignmentOptions.Left);
            Anchor(pLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -270), new Vector2(-4, -250));
            pLbl.text = "灵儿的人设(留空=默认性格;写她的性格/说话风格/自我认知,改完点下方「保存」)"; pLbl.color = lc;
            _asstPersonaInputGo = BuildMultilineGo(pane.transform, -274, MaxPersona, out _asstPersonaInput);
            return pane;
        }

        static void ResetAssistantFace()
        {
            if (!AssistantFacePathStore.Clear())
            { SetStatus("恢复灵儿默认形象失败:清空墓碑未可靠写盘。", true); return; }
            if (_asstFacePathInput != null) _asstFacePathInput.text = "";
            AssistantWidget.NotifyFacePathChanged();
            SetStatus("已恢复灵儿默认形象。", false);
        }

        static void OpenStructuredWorldBook()
        {
            if (_taiwuId <= 0)
            {
                SetStatus("请先进入存档，再打开世界书。", true);
                return;
            }
            if (_savingDocuments) return;
            int taiwuId = _taiwuId;
            TMP_FontAsset font = _font;
            Hide();
            StructuredWorldBookWindow.Open(taiwuId, font);
        }

        static GameObject BuildPersonaPane(Transform parent)
        {
            var pane = NewPane("Pane_Persona", parent);
            _personaTitle = NewText("Lbl_Persona", pane.transform, 16, TextAlignmentOptions.Left);
            Anchor(_personaTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, -26), new Vector2(-202, -4));
            _personaTitle.text = "自定义人设"; _personaTitle.color = new Color(0.80f, 0.78f, 0.70f, 1f);
            var pmResetGo = NewButton("ResetPersona", pane.transform, "还原默认", 14, out _personaResetBtn);
            PlaceResetButton(pmResetGo, -4f);
            _personaResetBtn.onClick.AddListener(ResetPersona);
            _personaInputGo = BuildMultilineGo(pane.transform, -32, MaxPersona, out _personaInput);
            _personaHint = NewText("Persona_Hint", pane.transform, 16, TextAlignmentOptions.Center);
            Anchor(_personaHint.rectTransform, Vector2.zero, Vector2.one, new Vector2(8, 8), new Vector2(-8, -32));
            _personaHint.enableWordWrapping = true; _personaHint.color = new Color(0.62f, 0.62f, 0.58f, 1f);
            _personaHint.text = "（人设按 NPC 设定。请从与某位 NPC 的对话窗口点「设置」打开本页,方可为 TA 编辑人设。）";
            return pane;
        }

        static void PlaceResetButton(GameObject go, float x)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(x, -2); rt.sizeDelta = new Vector2(120, 24);
        }

        static void ResetWorldBook()
        {
            if (_taiwuId <= 0 || _wbLoading || _savingDocuments) return;
            var host = ConfigHost.Instance;
            if (host == null) { SetStatus("世界书还原失败:宿主缺失。", true); return; }
            if (!LongTextEditorOperationGate.TryAcquire(DocumentOperationOwner, out long operationToken))
            {
                SetStatus("另一个世界书、人设或设置操作仍在进行，请稍候再还原。", true);
                return;
            }
            int taiwuId = _taiwuId; long version = _lifecycleVersion;
            string personasDirectory = JianghuYoulingPaths.Personas;
            string targetPath = Path.Combine(personasDirectory, "Worldbook_" + taiwuId + ".txt");
            int worldGeneration = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            _activeDocumentSaveToken = operationToken;
            _savingDocuments = true;
            _wbLoading = true;
            SetEditorInteractable(false);
            SetButtons(false);
            SetStatus("正在还原世界书……", false);
            host.StartCoroutine(ResetWorldBookCo(personasDirectory, targetPath, taiwuId, version,
                worldGeneration, worldId, operationToken));
        }

        static IEnumerator ResetWorldBookCo(string personasDirectory, string targetPath, int taiwuId,
            long version, int worldGeneration, uint worldId, long operationToken)
        {
            Task<DocumentResetResult> task = Task.Run(() =>
            {
                SettingsBatchTransaction tx = null;
                bool handedOff = false;
                try
                {
                    string layoutPath = StructuredWorldBookStore.LayoutPath(personasDirectory, taiwuId);
                    tx = SettingsBatchTransaction.Capture(new[] { targetPath, layoutPath });
                    if (!WorldBookStore.ClearFromDirectory(personasDirectory, taiwuId))
                        return new DocumentResetResult { Error = "默认墓碑未能可靠写盘" };
                    if (!StructuredWorldBookStore.InvalidateFromDirectory(personasDirectory, taiwuId))
                        return new DocumentResetResult { Error = "条目索引失效标记未可靠写盘" };
                    handedOff = true;
                    return new DocumentResetResult
                    {
                        Ok = true,
                        Text = WorldBookStore.EffectiveWorldBookTextFromDirectory(personasDirectory, taiwuId),
                        Transaction = tx
                    };
                }
                catch (System.Exception ex)
                {
                    return new DocumentResetResult { Error = "世界书还原异常:" + ex.GetType().Name };
                }
                finally { if (!handedOff) tx?.Dispose(); }
            });
            while (!task.IsCompleted) yield return null;
            DocumentResetResult result = task.IsFaulted ? null : task.Result;
            if (_activeDocumentSaveToken != operationToken
                || !WorldLifecycle.IsSameWorld(worldGeneration) || WorldLifecycle.WorldId != worldId)
            {
                result?.Transaction?.Dispose();
                LongTextEditorOperationGate.Release(DocumentOperationOwner, operationToken);
                yield break;
            }
            if (!LongTextEditorOperationGate.IsOwner(DocumentOperationOwner, operationToken))
            {
                result?.Transaction?.Dispose();
                yield break;
            }
            if (result == null || !result.Ok)
            {
                result?.Transaction?.Dispose();
                _wbLoading = false;
                FinishOwnedDocumentSave(operationToken);
                SetEditorInteractable(true);
                SetButtons(true);
                if (_root != null && _root.activeSelf)
                    SetStatus("世界书还原失败:" + (result?.Error ?? "后台任务失败") + "；原设置已回滚。", true);
                yield break;
            }
            result.Transaction?.Commit();
            _wbLoading = false;
            FinishOwnedDocumentSave(operationToken);
            SetEditorInteractable(true);
            SetButtons(true);
            if (_taiwuId != taiwuId || _root == null || !_root.activeSelf) yield break;
            _wbLoaded = true;
            _wbLoadedEffective = result.Text ?? "";
            if (_wbInput != null) _wbInput.text = _wbLoadedEffective;
            MoveInputToEnd(_wbInput);
            SetStatus(version == _lifecycleVersion
                ? "世界书已还原默认太吾世界观。"
                : "后台还原已完成，已刷新当前世界书。", false);
            EnsureActiveLongTextLoaded();
        }

        static void MoveInputToEnd(TMP_InputField input)
        {
            // JHYL_WORLDBOOK_OPEN_AT_END:世界书默认打开显示末尾,方便玩家直接追加。
            if (input == null) return;
            int len = input.text == null ? 0 : input.text.Length;
            try
            {
                input.caretPosition = len;
                input.stringPosition = len;
                input.selectionStringAnchorPosition = len;
                input.selectionStringFocusPosition = len;
                if (len <= 20000) input.ForceLabelUpdate();
                if (input.verticalScrollbar != null) input.verticalScrollbar.value = 1f;   // TopToBottom 语义:1=文末
            }
            catch { }
            var host = ConfigHost.Instance;
            if (host != null) host.StartCoroutine(MoveInputToEndNextFrame(input));
        }

        static IEnumerator MoveInputToEndNextFrame(TMP_InputField input)
        {
            yield return null;
            if (input == null) yield break;
            int len = input.text == null ? 0 : input.text.Length;
            try
            {
                input.caretPosition = len;
                input.stringPosition = len;
                input.selectionStringAnchorPosition = len;
                input.selectionStringFocusPosition = len;
                if (len <= 20000) input.ForceLabelUpdate();
                if (input.verticalScrollbar != null) input.verticalScrollbar.value = 1f;   // TopToBottom 语义:1=文末
            }
            catch { }
        }

        static void ResetPersona()
        {
            if (_taiwuId <= 0 || _npcId < 0 || _personaLoading || _savingDocuments) return;
            var host = ConfigHost.Instance;
            if (host == null) { SetStatus("人设还原失败:宿主缺失。", true); return; }
            if (!LongTextEditorOperationGate.TryAcquire(DocumentOperationOwner, out long operationToken))
            {
                SetStatus("另一个世界书、人设或设置操作仍在进行，请稍候再还原。", true);
                return;
            }
            int taiwuId = _taiwuId, npcId = _npcId; long version = _lifecycleVersion;
            string personasDirectory = JianghuYoulingPaths.Personas;
            string targetPath = PersonaStore.CanonicalPath(personasDirectory,
                npcId.ToString());
            string capturedPortrait = PortraitStore.GetPortrait(taiwuId, npcId);
            int worldGeneration = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            _activeDocumentSaveToken = operationToken;
            _savingDocuments = true;
            _personaLoading = true;
            SetEditorInteractable(false);
            SetButtons(false);
            SetStatus("正在还原人设……", false);
            host.StartCoroutine(ResetPersonaCo(personasDirectory, targetPath, taiwuId, npcId,
                capturedPortrait, version, worldGeneration, worldId, operationToken));
        }

        static IEnumerator ResetPersonaCo(string personasDirectory, string targetPath, int taiwuId,
            int npcId, string capturedPortrait, long version, int worldGeneration, uint worldId,
            long operationToken)
        {
            Task<DocumentResetResult> task = Task.Run(() =>
            {
                SettingsBatchTransaction tx = null;
                bool handedOff = false;
                try
                {
                    tx = SettingsBatchTransaction.Capture(new[] { targetPath });
                    if (!PersonaStore.Save(personasDirectory, taiwuId.ToString(), npcId.ToString(), "", "replace"))
                        return new DocumentResetResult { Error = "磁盘写入或读回校验失败" };
                    handedOff = true;
                    return new DocumentResetResult
                    {
                        Ok = true,
                        Text = string.IsNullOrWhiteSpace(capturedPortrait)
                            || PortraitDistiller.IsCustomPersonaAppendLayer(capturedPortrait)
                            ? "（默认人设由该角色的身份、关系、经历、特性和自动画像生成。首次对话生成画像后,这里会显示自动画像；你也可以直接改写本框内容并保存。）"
                            : capturedPortrait.Trim(),
                        Transaction = tx
                    };
                }
                catch (System.Exception ex)
                {
                    return new DocumentResetResult { Error = "人设还原异常:" + ex.GetType().Name };
                }
                finally { if (!handedOff) tx?.Dispose(); }
            });
            while (!task.IsCompleted) yield return null;
            DocumentResetResult result = task.IsFaulted ? null : task.Result;
            if (_activeDocumentSaveToken != operationToken
                || !WorldLifecycle.IsSameWorld(worldGeneration) || WorldLifecycle.WorldId != worldId)
            {
                result?.Transaction?.Dispose();
                LongTextEditorOperationGate.Release(DocumentOperationOwner, operationToken);
                yield break;
            }
            if (!LongTextEditorOperationGate.IsOwner(DocumentOperationOwner, operationToken))
            {
                result?.Transaction?.Dispose();
                yield break;
            }
            if (result == null || !result.Ok)
            {
                result?.Transaction?.Dispose();
                _personaLoading = false;
                FinishOwnedDocumentSave(operationToken);
                SetEditorInteractable(true);
                SetButtons(true);
                if (_root != null && _root.activeSelf)
                    SetStatus("人设还原失败:" + (result?.Error ?? "后台任务失败") + "；原设置已回滚。", true);
                yield break;
            }
            result.Transaction?.Commit();
            PortraitService.Invalidate(taiwuId, npcId);
            _personaLoading = false;
            FinishOwnedDocumentSave(operationToken);
            SetEditorInteractable(true);
            SetButtons(true);
            if (_taiwuId != taiwuId || _npcId != npcId || _root == null || !_root.activeSelf) yield break;
            _personaLoaded = true;
            _personaLoadedEffective = result.Text ?? "";
            if (_personaInput != null)
            {
                _personaInput.characterLimit = MaxPersona + 1;
                _personaInput.text = _personaLoadedEffective;
                ArmFirstClickSelectAll(_personaInput);
            }
            SetStatus(version == _lifecycleVersion
                ? "人设已还原为自动画像。"
                : "后台还原已完成，已刷新当前人设。", false);
            EnsureActiveLongTextLoaded();
        }

        static GameObject NewPane(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Anchor(go.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            go.SetActive(false);
            return go;
        }

        static void ArmFirstClickSelectAll(TMP_InputField input)
        {
            var longInput = input as LongTextInputField;
            if (longInput != null)
                longInput.ArmSelectAllOnNextPointerDown(true);
        }

        // 单行输入(文风用):yTop 为输入框顶部相对页顶偏移
        static TMP_InputField BuildSingleLine(Transform parent, float yTop)
        {
            return BuildSingleLine(parent, yTop, 4f);
        }

        static TMP_InputField BuildSingleLine(Transform parent, float yTop, float rightPad)
        {
            var inputGo = new GameObject("StyleIn", typeof(RectTransform), typeof(Image));
            inputGo.transform.SetParent(parent, false);
            float pad = Mathf.Max(4f, rightPad);
            Anchor(inputGo.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, yTop - 30), new Vector2(-pad, yTop - 2));
            inputGo.GetComponent<Image>().color = new Color(1, 1, 1, 0.10f);
            TextMeshProUGUI _;
            return UiInput.Setup(inputGo, _font, 18, false, TMP_InputField.LineType.SingleLine, false, null, 0, false, out _);
        }

        static TMP_InputField BuildFixedMultiline(Transform parent, float yTop, float height, int maxLen)
        {
            var inputGo = new GameObject("LongTextIn", typeof(RectTransform), typeof(Image));
            inputGo.transform.SetParent(parent, false);
            Anchor(inputGo.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(4, yTop - height), new Vector2(-4, yTop - 2));
            inputGo.GetComponent<Image>().color = new Color(1, 1, 1, 0.10f);
            TextMeshProUGUI textArea;
            var input = UiInput.Setup(inputGo, _font, 16, true, TMP_InputField.LineType.MultiLineNewline,
                false, null, maxLen + 1, true, out textArea);
            if (textArea != null) textArea.richText = false;
            return input;
        }

        static TMP_InputField BuildStretchMultiline(Transform parent, float yTop, float bottomPad, int maxLen)
        {
            var inputGo = new GameObject("LongTextIn", typeof(RectTransform), typeof(Image));
            inputGo.transform.SetParent(parent, false);
            Anchor(inputGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(4, Mathf.Max(4f, bottomPad)), new Vector2(-4, yTop - 2));
            inputGo.GetComponent<Image>().color = new Color(1, 1, 1, 0.10f);
            TextMeshProUGUI textArea;
            var input = UiInput.Setup(inputGo, _font, 16, true, TMP_InputField.LineType.MultiLineNewline,
                false, null, maxLen + 1, true, out textArea);
            if (textArea != null) textArea.richText = false;
            return input;
        }

        // 多行输入(世界书/人设用):返回承载 GameObject(供按上下文显隐),out 出 TMP_InputField
        static GameObject BuildMultilineGo(Transform parent, float yTop, int maxLen, out TMP_InputField input)
        {
            var inputGo = new GameObject("MlIn", typeof(RectTransform), typeof(Image));
            inputGo.transform.SetParent(parent, false);
            Anchor(inputGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(4, 4), new Vector2(-4, yTop));
            inputGo.GetComponent<Image>().color = new Color(1, 1, 1, 0.10f);
            TextMeshProUGUI textArea;
            input = UiInput.Setup(inputGo, _font, 17, true, TMP_InputField.LineType.MultiLineNewline, false, null, maxLen + 1, true, out textArea);
            // 世界书/人设可能有数十万字。TMP 富文本会在每次键入时从头扫描标签，
            // 普通纯文本编辑器不需要这项开销；关闭后长文输入不再随篇幅急剧变慢。
            if (textArea != null) textArea.richText = false;
            return inputGo;
        }

        static TMP_InputField BuildField(Transform parent, string label, float yTop, bool password)
        {
            var lbl = NewText("Lbl_" + label, parent, 18, TextAlignmentOptions.Left);
            Anchor(lbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, yTop - 20), new Vector2(-4, yTop));
            lbl.text = label; lbl.color = new Color(0.80f, 0.78f, 0.70f, 1f);
            var inputGo = new GameObject("In_" + label, typeof(RectTransform), typeof(Image));
            inputGo.transform.SetParent(parent, false);
            Anchor(inputGo.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(1, 1), new Vector2(4, yTop - 50), new Vector2(-4, yTop - 22));
            inputGo.GetComponent<Image>().color = new Color(1, 1, 1, 0.10f);
            TextMeshProUGUI _;
            return UiInput.Setup(inputGo, _font, 18, false, TMP_InputField.LineType.SingleLine, password, null, 0, false, out _);
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            UiFontSizeStore.Bind(t, size); t.alignment = align; t.richText = true;
            t.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            return t;
        }

        static GameObject NewButton(string name, Transform parent, string label, float size, out Button btn)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.30f, 0.38f, 0.36f, 0.95f);
            btn = go.GetComponent<Button>();
            var t = NewText("L", go.transform, size, TextAlignmentOptions.Center);
            Anchor(t.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            t.text = label; t.raycastTarget = false;
            return go;
        }

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
        {
            rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = offMin; rt.offsetMax = offMax;
        }
    }
}
