using System;
using System.Collections;
using System.Reflection;
using Game.Views.EventWindow;          // ViewEventWindow / EventWindowOption (1.0)
// 全局命名空间另有一个同名 EventWindowCharacter(: Refers,无 nameLabel),用别名锁定到 UI 组件那个
using EwChar = Game.Components.EventWindow.EventWindowCharacter;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>反射访问 1.0 ViewEventWindow / EventWindowCharacter 的 private 字段。</summary>
    internal static class EwReflect
    {
        const BindingFlags NP = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo RightCharField = typeof(ViewEventWindow).GetField("rightCharacter", NP);
        static readonly FieldInfo MainRootField = typeof(ViewEventWindow).GetField("mainWindowRoot", NP);
        static readonly FieldInfo FarewellButtonField = typeof(ViewEventWindow).GetField("commonButtonFinish", NP);
        static readonly FieldInfo CurCharIdField = typeof(EwChar).GetField("_curCharacterId", NP);

        public static EwChar Right(ViewEventWindow w) => w == null ? null : RightCharField?.GetValue(w) as EwChar;
        public static RectTransform MainRoot(ViewEventWindow w) => w == null ? null : MainRootField?.GetValue(w) as RectTransform;
        public static Button FarewellButton(ViewEventWindow w)
            => w == null ? null : FarewellButtonField?.GetValue(w) as Button;

        // —— 好感条原生动画钩子:右侧 NPC 的好感监视器 + 私有 FillFavorProgress(改完好感后手动触发升/降动画) ——
        static readonly FieldInfo BasicMonitorF = typeof(EwChar).GetField("_basicInfoMonitor", NP);
        static readonly MethodInfo FillFavorM = typeof(EwChar).GetMethod("FillFavorProgress", NP);
        static readonly FieldInfo NotedFavorF = typeof(EwChar).GetField("_notedFavorData", NP);
        static PropertyInfo _favProp;

        public static object FavorMonitor(EwChar rc) => rc == null ? null : BasicMonitorF?.GetValue(rc);

        public static short MonitorFavor(object monitor)
        {
            if (monitor == null) return short.MinValue;
            try
            {
                var prop = _favProp ?? (_favProp = monitor.GetType().GetProperty("FavorabilityToTaiwu", NP | BindingFlags.Public));
                if (prop != null) return Convert.ToInt16(prop.GetValue(monitor));
            }
            catch { }
            return short.MinValue;
        }

        /// <summary>调用右侧 NPC 的私有 FillFavorProgress():noted≠当前则播放原生升/降好感动画。</summary>
        public static void FillFavor(EwChar rc) { try { FillFavorM?.Invoke(rc, null); } catch { } }

        /// <summary>把好感条"已显示值"钉成 (charId, favor),令下次 FillFavorProgress 必触发升/降动画(修复降好感不动画)。</summary>
        public static void SetNotedFavor(EwChar rc, int charId, int favor)
        {
            try { NotedFavorF?.SetValue(rc, new int[] { charId, favor }); } catch { }
        }

        public static int ResolveNpcId(EwChar rc)
        {
            try
            {
                // EventWindowCharacter 会在每次刷新开头把该值清为 -1，且仅在
                // RefreshAsNormalCharacter 中写入真实人物 id。演员、幻象、相枢头像和
                // 纯模板立绘都会保持 -1；此处绝不能回退到事件数据中的隐藏目标人物。
                return rc != null && CurCharIdField?.GetValue(rc) is int id && id >= 0
                    ? id : -1;
            }
            catch { return -1; }
        }

        public static int TaiwuId()
        {
            try { return SingletonObject.getInstance<BasicGameData>().TaiwuCharId; } catch { return -1; }
        }

        /// <summary>
        /// 门控:此刻是否真能与该角色对话。三条任一为真即可(再排除太吾本人):
        /// ① 游戏认定的"可操作普通互动"(EventModel.IsOnNormalInteractEvent,6 个 GUID)——照常;
        /// ② 放宽给【仇敌/敌对互动】:当前事件窗【确有互动选项】且【目标正是此 NPC】。敌对互动 GUID 不在①名单里,
        ///    但同样是当面互动、有选项、有目标 NPC,故据此放行让仇敌也能开聊。翻人物详情/墓碑/过月动画等无互动选项、
        ///    或目标非此人 → 天然为假,不会误显。
        /// ③ 新版俘虏/牢房互动事件没有普通选项列表；仅当后端已确认此人确为太吾阶下囚，且事件目标正是此 NPC 时放行。
        /// </summary>
        public static bool CanChatNow(int npcId, bool allowConfirmedCaptive = false)
        {
            if (npcId < 0 || npcId == TaiwuId()) return false;
            try
            {
                var em = SingletonObject.getInstance<EventModel>();
                if (em == null) return false;
                if (em.IsOnNormalInteractEvent) return true;   // ① 普通互动
                var data = em.DisplayingEventData;              // ② 仇敌/敌对等互动:有选项 + 目标正是此 NPC
                bool exactTarget = data != null && data.TargetCharacter != null
                    && data.TargetCharacter.CharacterId == npcId;
                return exactTarget && ((data.EventOptionInfos != null && data.EventOptionInfos.Count > 0)
                    || allowConfirmedCaptive);
            }
            catch { return false; }
        }

        public static bool HasTargetCharacterContext(int npcId)
        {
            if (npcId < 0 || npcId == TaiwuId()) return false;
            try
            {
                var data = SingletonObject.getInstance<EventModel>()?.DisplayingEventData;
                return data != null && data.TargetCharacter != null
                    && data.TargetCharacter.CharacterId == npcId;
            }
            catch { return false; }
        }

        /// <summary>
        /// 原生事件窗可能在奇遇推进时于同一帧换人或退场。任何回写原生人物 UI 的操作
        /// 都必须同时绑定窗口实例、右侧人物组件和人物 id，不能只凭 EventWindow.Exist。
        /// </summary>
        public static bool IsCurrentTarget(ViewEventWindow expectedWindow, EwChar expectedCharacter,
            int npcId)
        {
            if (npcId < 0 || expectedWindow == null || expectedCharacter == null) return false;
            try
            {
                return UIElement.EventWindow.Exist
                    && ReferenceEquals(UIElement.EventWindow.UiBase, expectedWindow)
                    && expectedWindow.gameObject.activeInHierarchy
                    && expectedCharacter.gameObject.activeInHierarchy
                    && ReferenceEquals(Right(expectedWindow), expectedCharacter)
                    && ResolveNpcId(expectedCharacter) == npcId
                    && HasTargetCharacterContext(npcId);
            }
            catch { return false; }
        }
    }

    /// <summary>0.25s 轮询宿主:检测 NPC 交互窗口、注入对话与互动记录入口、跑对话协程。DontDestroyOnLoad。</summary>
    public sealed class TalkEntryHost : MonoBehaviour
    {
        public static TalkEntryHost Instance { get; private set; }
        private float _next;

        public static void Initialize()
        {
            if (Instance != null) return;
            var go = new GameObject("JHYL_TalkEntryHost");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<TalkEntryHost>();
        }

        public static void Shutdown()
        {
            TalkEntryInjector.ResetForWorldExit();
            var instance = Instance;
            Instance = null;
            if (instance != null) try { Destroy(instance.gameObject); } catch { }
        }

        private void Update()
        {
            // 右键关聊天窗的逻辑已移入 RightClickGuard(在 HotKeyCommand.Check 里原子处理,避免与游戏关窗竞态)
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            if (!WorldLifecycle.HasWorldIdentity) return;
            try
            {
                var w = UIElement.EventWindow.UiBase as ViewEventWindow;
                if (w != null && UIElement.EventWindow.Exist && w.gameObject.activeInHierarchy)
                    TalkEntryInjector.TryRefresh(w);
            }
            catch (Exception ex) { Debug.LogWarning("[江湖有灵] 轮询异常: " + ex.GetType().Name); }
        }
    }

    /// <summary>在 ViewEventWindow 注入"AI 对话"与"互动记录"按钮，保持幂等并防止换人瞬间误触。</summary>
    internal static class TalkEntryInjector
    {
        const string EntryName = "JHYL_TalkEntry";
        const string RecordingEntryName = "JHYL_NativeInteractionRecording";
        static int _shownNpcId = -1;
        static float _interactiveAt;
        static int _captiveNpcId = -1;
        static bool _captiveKnown;
        static bool _captiveByTaiwu;
        static bool _captiveQueryPending;

        public static void TryRefresh(ViewEventWindow w)
        {
            var rc = EwReflect.Right(w);
            var root = EwReflect.MainRoot(w);
            int npcId = EwReflect.ResolveNpcId(rc);

            if (_shownNpcId != npcId)
            {
                _shownNpcId = npcId;
                _interactiveAt = Time.unscaledTime + 0.2f;
                ResetCaptiveGate(npcId);
            }

            // 只有"此刻确实能对话"才显示入口(死者详情/关系网浏览/档案/过月一律不显示)
            bool ordinaryChat = EwReflect.CanChatNow(npcId);
            if (!ordinaryChat && !_captiveKnown && !_captiveQueryPending
                && EwReflect.HasTargetCharacterContext(npcId))
            {
                int queriedNpcId = npcId;
                _captiveQueryPending = true;
                JianghuYouling.Effects.EffectHandler.QueryCaptiveByTaiwu(queriedNpcId, captive =>
                {
                    if (_shownNpcId != queriedNpcId || _captiveNpcId != queriedNpcId) return;
                    _captiveQueryPending = false;
                    _captiveKnown = true;
                    _captiveByTaiwu = captive;
                });
            }
            bool confirmedCaptive = _captiveKnown && _captiveByTaiwu && _captiveNpcId == npcId;
            bool show = rc != null && rc.gameObject.activeSelf && root != null
                && (ordinaryChat || EwReflect.CanChatNow(npcId, confirmedCaptive));
            var t = root != null ? root.Find(EntryName) : null;
            var recording = root != null ? root.Find(RecordingEntryName) : null;

            if (!show)
            {
                if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
                if (recording != null && recording.gameObject.activeSelf)
                    recording.gameObject.SetActive(false);
                return;
            }
            if (t == null) { t = Create(w, rc, root); if (t == null) return; }
            if (recording == null)
            {
                recording = CreateRecordingToggle(w, rc, root);
                if (recording == null) return;
            }
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            if (!recording.gameObject.activeSelf) recording.gameObject.SetActive(true);
            EnsureInjectedOrder(root, t, recording);
            var btn = t.GetComponent<Button>();
            bool interactable = Time.unscaledTime >= _interactiveAt;
            if (btn != null && btn.interactable != interactable) btn.interactable = interactable;
            recording.GetComponent<NativeInteractionRecordingToggleView>()?.Sync(
                NativeInteractionRecordingStore.IsEnabled(ResolveRecordingNpcId(npcId)), interactable);
        }

        /// <summary>令事件窗右侧 NPC 立即重拉显示(好感/戒备等即时效果改完后调用,免得关窗重开才更新)。</summary>
        public static void RefreshNpcDisplay(int npcId)
        {
            try
            {
                var w = UIElement.EventWindow.UiBase as ViewEventWindow;
                var rc = EwReflect.Right(w);
                if (EwReflect.IsCurrentTarget(w, rc, npcId)) rc.Refresh();
                else Debug.Log("[JHYL_EVENT_CHAT_REFRESH] skipped stale target npc=" + npcId);
            }
            catch { }
        }

        public static void ResetForWorldExit()
        {
            _shownNpcId = -1;
            _interactiveAt = float.PositiveInfinity;
            ResetCaptiveGate(-1);
        }

        static void ResetCaptiveGate(int npcId)
        {
            _captiveNpcId = npcId;
            _captiveKnown = false;
            _captiveByTaiwu = false;
            _captiveQueryPending = false;
        }

        /// <summary>改完好感后轮询监视器,数据同步到新值的那一刻触发原生升/降好感动画(NPC 回复完立即变化)。</summary>
        public static IEnumerator AnimateFavorRefresh(int npcId, short prevShownFavor)
        {
            var w = UIElement.EventWindow.UiBase as ViewEventWindow;
            var rc = EwReflect.Right(w);
            if (!EwReflect.IsCurrentTarget(w, rc, npcId)) yield break;
            var monitor = EwReflect.FavorMonitor(rc);
            if (rc == null || monitor == null) yield break;
            float dl = Time.unscaledTime + 1f;   // 等后端 RPC 执行+同步回前端;1s 上限(通常一两百毫秒内即同步,超时则只是不播升降动画,数值仍正确)
            while (Time.unscaledTime < dl)
            {
                // 奇遇事件可能在模型生成期间推进到下一人；一旦窗口、组件或人物 id
                // 任一变化，立即放弃旧人物的动画，绝不刷新新事件目标。
                if (!EwReflect.IsCurrentTarget(w, rc, npcId)) yield break;
                short cur = EwReflect.MonitorFavor(monitor);
                if (cur != short.MinValue && cur != prevShownFavor)
                {
                    // 先把"已显示值"钉回变更前 → FillFavorProgress 必然 noted≠当前 → 升/降都触发(修复降好感不动画)
                    EwReflect.SetNotedFavor(rc, npcId, prevShownFavor);
                    EwReflect.FillFavor(rc);
                    yield break;
                }
                yield return null;
            }
        }

        // AI 对话直接克隆本体“道别”按钮，完整继承原版底图、边框和交互状态；
        // 仅替换文字与位置。取不到原版按钮时才使用自建备用样式。
        static Transform Create(ViewEventWindow w, EwChar rc, RectTransform parent)
        {
            Button farewell = EwReflect.FarewellButton(w);
            if (farewell != null)
            {
                GameObject cloned = UnityEngine.Object.Instantiate(farewell.gameObject, parent, false);
                cloned.name = EntryName;
                cloned.SetActive(true);
                RectTransform clonedRt = cloned.GetComponent<RectTransform>();
                clonedRt.anchorMin = clonedRt.anchorMax = new Vector2(0.5f, 0f);
                clonedRt.pivot = new Vector2(0.5f, 0f);
                clonedRt.anchoredPosition = new Vector2(0f, 8f);

                Button clonedButton = cloned.GetComponent<Button>();
                if (clonedButton == null)
                {
                    UnityEngine.Object.Destroy(cloned);
                    return null;
                }
                clonedButton.onClick.RemoveAllListeners();
                TextMeshProUGUI optionName = FindText(cloned.transform, "OptionName");
                if (optionName == null) optionName = FindText(cloned.transform, "Label");
                if (optionName != null)
                {
                    optionName.text = "AI 对话";
                    if (optionName.enableAutoSizing)
                        UiFontSizeStore.BindAutoSize(optionName, optionName.fontSizeMin,
                            optionName.fontSizeMax);
                    else UiFontSizeStore.Bind(optionName, optionName.fontSize);
                }
                WireClick(clonedButton, w);
                return cloned.transform;
            }

            var go = new GameObject(EntryName, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 20f);
            rt.sizeDelta = new Vector2(156f, 48f);
            go.GetComponent<Image>().color = new Color(0.34f, 0.43f, 0.42f, 0.98f);

            // 克隆 nameLabel 继承游戏中文字体
            TextMeshProUGUI lbl = rc != null && rc.nameLabel != null
                ? UnityEngine.Object.Instantiate(rc.nameLabel, go.transform, false)
                : new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            lbl.gameObject.name = "Label";
            if (lbl.transform.parent != go.transform) lbl.transform.SetParent(go.transform, false);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(6f, 1f); lrt.offsetMax = new Vector2(-6f, -1f);
            lbl.enableAutoSizing = true; UiFontSizeStore.BindAutoSize(lbl, 16f, 26f);   // 字也跟着大一点
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.color = new Color(0.92f, 0.92f, 0.82f, 1f);
            lbl.raycastTarget = false;
            lbl.text = "AI 对话";

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = go.GetComponent<Image>();
            WireClick(btn, w);
            return go.transform;
        }

        static TextMeshProUGUI FindText(Transform root, string objectName)
        {
            if (root == null) return null;
            TextMeshProUGUI[] labels = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] != null && string.Equals(labels[i].gameObject.name, objectName,
                    StringComparison.Ordinal)) return labels[i];
            return null;
        }

        static Transform CreateRecordingToggle(ViewEventWindow w, EwChar rc, RectTransform parent)
        {
            Button farewell = EwReflect.FarewellButton(w);
            if (farewell != null)
            {
                GameObject cloned = UnityEngine.Object.Instantiate(farewell.gameObject, parent, false);
                cloned.name = RecordingEntryName;
                cloned.SetActive(true);
                RectTransform clonedRt = cloned.GetComponent<RectTransform>();
                clonedRt.anchorMin = clonedRt.anchorMax = new Vector2(0f, 1f);
                clonedRt.pivot = new Vector2(0f, 1f);
                clonedRt.anchoredPosition = new Vector2(24f, -275f);
                clonedRt.sizeDelta = new Vector2(Mathf.Max(clonedRt.sizeDelta.x, 196f),
                    clonedRt.sizeDelta.y);

                Button clonedButton = cloned.GetComponent<Button>();
                if (clonedButton == null)
                {
                    UnityEngine.Object.Destroy(cloned);
                    return null;
                }
                clonedButton.onClick.RemoveAllListeners();
                TextMeshProUGUI optionName = FindText(cloned.transform, "OptionName");
                if (optionName == null) optionName = FindText(cloned.transform, "Label");
                if (optionName != null)
                {
                    if (optionName.enableAutoSizing)
                        UiFontSizeStore.BindAutoSize(optionName, optionName.fontSizeMin,
                            optionName.fontSizeMax);
                    else UiFontSizeStore.Bind(optionName, optionName.fontSize);
                }
                NativeInteractionRecordingToggleView clonedView
                    = cloned.AddComponent<NativeInteractionRecordingToggleView>();
                clonedView.Initialize(cloned.GetComponent<Image>(), clonedButton, optionName,
                    preserveNativeBackground: true);
                clonedView.Sync(NativeInteractionRecordingStore.IsEnabled(
                    ResolveRecordingNpcId(EwReflect.ResolveNpcId(rc))), false);
                NativeTooltipHelpIcon.Attach(cloned, optionName,
                    rc != null && rc.nameLabel != null ? rc.nameLabel.font : null,
                    "互动记录",
                    "此开关按人物单独保存。开启后，与当前人物发生的游戏互动及其结果会写入聊天记录，供以后对话参考；特殊人物会先复制为合法普通人物，普通人物不会复制；关闭后不再记录新的互动。");
                clonedButton.onClick.AddListener(() => ToggleNativeInteractionRecording(w, clonedView));
                return cloned.transform;
            }

            var go = new GameObject(RecordingEntryName, typeof(RectTransform), typeof(Image),
                typeof(Button), typeof(NativeInteractionRecordingToggleView));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            // 固定在事件正文区域左上角、场景横幅下方，避开正文中心与底部操作栏。
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(24f, -275f);
            rt.sizeDelta = new Vector2(196f, 48f);

            Image image = go.GetComponent<Image>();
            TextMeshProUGUI label = rc != null && rc.nameLabel != null
                ? UnityEngine.Object.Instantiate(rc.nameLabel, go.transform, false)
                : new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI))
                    .GetComponent<TextMeshProUGUI>();
            label.gameObject.name = "Label";
            if (label.transform.parent != go.transform) label.transform.SetParent(go.transform, false);
            RectTransform labelRt = label.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(5f, 1f);
            labelRt.offsetMax = new Vector2(-5f, -1f);
            label.enableAutoSizing = true;
            UiFontSizeStore.BindAutoSize(label, 14f, 23f);
            label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(0.94f, 0.92f, 0.80f, 1f);
            label.raycastTarget = false;

            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;
            NativeInteractionRecordingToggleView view
                = go.GetComponent<NativeInteractionRecordingToggleView>();
            view.Initialize(image, button, label, preserveNativeBackground: false);
            view.Sync(NativeInteractionRecordingStore.IsEnabled(
                ResolveRecordingNpcId(EwReflect.ResolveNpcId(rc))), false);
            NativeTooltipHelpIcon.Attach(go, label,
                rc != null && rc.nameLabel != null ? rc.nameLabel.font : null,
                "互动记录",
                "此开关按人物单独保存。开启后，与当前人物发生的游戏互动及其结果会写入聊天记录，供以后对话参考；特殊人物会先复制为合法普通人物，普通人物不会复制；关闭后不再记录新的互动。");
            button.onClick.AddListener(() => ToggleNativeInteractionRecording(w, view));
            return go.transform;
        }

        static void ToggleNativeInteractionRecording(ViewEventWindow w,
            NativeInteractionRecordingToggleView view)
        {
            if (Time.unscaledTime < _interactiveAt || view == null) return;
            EwChar selectedCharacter = EwReflect.Right(w);
            int selectedNpcId = EwReflect.ResolveNpcId(selectedCharacter);
            if (!EwReflect.HasTargetCharacterContext(selectedNpcId)) return;
            int resolvedNpcId = ResolveRecordingNpcId(selectedNpcId);
            bool enabled = !NativeInteractionRecordingStore.IsEnabled(resolvedNpcId);
            if (!enabled)
            {
                if (!NativeInteractionRecordingStore.Save(resolvedNpcId, false))
                {
                    Debug.LogWarning("[JHYL_NATIVE_INTERACTION] 互动记录开关保存失败");
                    return;
                }
                view.Sync(false, true);
                return;
            }

            // 普通人物直接开启；固定模板特殊人物只有在玩家明确开启记录时才创建副本。
            // 先读 CreatingType，避免普通人物出现任何“复制中”提示或调用副本接口。
            if (TalkEntryHost.Instance == null) return;
            view.ShowBusy("正在确认…");
            TalkEntryHost.Instance.StartCoroutine(EnableNativeInteractionRecording(
                w, selectedCharacter, selectedNpcId, view));
        }

        static IEnumerator EnableNativeInteractionRecording(ViewEventWindow w,
            EwChar selectedCharacter, int selectedNpcId,
            NativeInteractionRecordingToggleView view)
        {
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            if (!EwReflect.IsCurrentTarget(w, selectedCharacter, selectedNpcId)) yield break;
            NpcSnapshot snap = null;
            yield return NpcSnapshotReader.FetchDisplayOnly(selectedNpcId, value => snap = value,
                () => WorldLifecycle.IsSameWorld(generation)
                    && WorldLifecycle.WorldId == worldId
                    && EwReflect.IsCurrentTarget(w, selectedCharacter, selectedNpcId));
            if (!WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId)
                yield break;
            if (!EwReflect.IsCurrentTarget(w, selectedCharacter, selectedNpcId)) yield break;
            if (snap == null || !snap.DisplayLoaded)
            {
                if (view != null) view.Sync(false, true);
                LegacyDataNotice.Post("native-recording-identity-" + selectedNpcId,
                    "人物身份读取失败，互动记录尚未开启");
                yield break;
            }

            if (snap.CreatingType == 1)
            {
                if (!NativeInteractionRecordingStore.Save(selectedNpcId, true))
                {
                    if (view != null) view.Sync(false, true);
                    Debug.LogWarning("[JHYL_NATIVE_INTERACTION] 互动记录开关保存失败");
                    yield break;
                }
                if (view != null) view.Sync(true, true);
                yield break;
            }

            if (view != null) view.ShowBusy("正在复制中");
            if (!EwReflect.IsCurrentTarget(w, selectedCharacter, selectedNpcId)) yield break;
            bool completed = false;
            int resolvedNpcId = selectedNpcId;
            string failure = null;
            CharacterProxyIdentityService.EnsureForConversation(
                EwReflect.TaiwuId(), selectedNpcId, (resolved, error) =>
                {
                    resolvedNpcId = resolved;
                    failure = error;
                    completed = true;
                });
            while (!completed && WorldLifecycle.IsSameWorld(generation)
                && WorldLifecycle.WorldId == worldId
                && EwReflect.IsCurrentTarget(w, selectedCharacter, selectedNpcId))
                yield return null;
            if (!WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId)
                yield break;
            if (!EwReflect.IsCurrentTarget(w, selectedCharacter, selectedNpcId)) yield break;
            if (!completed || !string.IsNullOrWhiteSpace(failure) || resolvedNpcId < 0
                || !NativeInteractionRecordingStore.Save(resolvedNpcId, true))
            {
                if (view != null) view.Sync(false, true);
                LegacyDataNotice.Post("native-recording-copy-" + selectedNpcId,
                    "特殊人物复制失败，互动记录尚未开启："
                    + (string.IsNullOrWhiteSpace(failure) ? "人物资料未能可靠保存" : failure));
                yield break;
            }
            if (view != null) view.Sync(true, true);
        }

        static int ResolveRecordingNpcId(int npcId)
        {
            int taiwuId = EwReflect.TaiwuId();
            return CharacterProxyIdentityService.ResolveKnown(taiwuId, npcId);
        }

        static void EnsureInjectedOrder(RectTransform root, Transform talk, Transform recording)
        {
            if (root == null || talk == null || recording == null) return;
            int count = root.childCount;
            if (count >= 2 && talk.GetSiblingIndex() == count - 2
                && recording.GetSiblingIndex() == count - 1) return;
            talk.SetAsLastSibling();
            recording.SetAsLastSibling();
        }

        static void WireClick(Button btn, ViewEventWindow w)
        {
            btn.onClick.AddListener(() =>
            {
                if (Time.unscaledTime < _interactiveAt) return;
                int npcId = EwReflect.ResolveNpcId(EwReflect.Right(w));
                int taiwuId = EwReflect.TaiwuId();
                OnAiTalkClick(w, npcId, taiwuId);
            });
        }

        static void OnAiTalkClick(ViewEventWindow w, int npcId, int taiwuId)
        {
            // 点击瞬间再核一次(防 0.25s 轮询与点击之间状态漂移):不可对话则不开窗
            bool confirmedCaptive = _captiveKnown && _captiveByTaiwu && _captiveNpcId == npcId;
            if (!EwReflect.CanChatNow(npcId, confirmedCaptive)) { Debug.Log("[江湖有灵] 此刻无法与其交谈,忽略点击"); return; }
            var rc = EwReflect.Right(w);
            string npcName = rc != null && rc.nameLabel != null ? rc.nameLabel.text : ("NPC#" + npcId);
            TMP_FontAsset font = rc != null && rc.nameLabel != null ? rc.nameLabel.font : null;
            // 尚未配置 LLM(接口/密钥/模型未填)→ 弹出本 mod 自带的配置窗口(非游戏设置页),配好再聊
            if (!LlmService.IsConfigured)
            {
                Debug.Log("[江湖有灵] 尚未配置 LLM,打开配置窗口");
                ConfigWindow.Open(font);
                return;
            }
            Debug.Log("[江湖有灵] AI 对话 npcId=" + npcId + " taiwuId=" + taiwuId + " name=" + npcName);
            ChatWindow.Open(npcId, taiwuId, npcName, font);
        }
    }

    /// <summary>缓存互动记录按钮组件，仅在开关或可点击状态变化时重绘。</summary>
    internal sealed class NativeInteractionRecordingToggleView : MonoBehaviour
    {
        private static readonly Color OffColor = new Color(0.35f, 0.31f, 0.24f, 0.98f);
        private static readonly Color OnColor = new Color(0.25f, 0.45f, 0.36f, 0.98f);
        private Image _background;
        private Button _button;
        private TextMeshProUGUI _label;
        private bool _preserveNativeBackground;
        private bool? _renderedEnabled;

        internal void Initialize(Image background, Button button, TextMeshProUGUI label,
            bool preserveNativeBackground)
        {
            _background = background;
            _button = button;
            _label = label;
            _preserveNativeBackground = preserveNativeBackground;
        }

        internal void Sync(bool enabled, bool interactable)
        {
            if (_button != null && _button.interactable != interactable)
                _button.interactable = interactable;
            if (_renderedEnabled.HasValue && _renderedEnabled.Value == enabled) return;
            _renderedEnabled = enabled;
            if (_background != null && !_preserveNativeBackground)
                _background.color = enabled ? OnColor : OffColor;
            if (_label != null) _label.text = enabled ? "互动记录：开" : "互动记录：关";
        }

        internal void ShowBusy(string label)
        {
            _renderedEnabled = null;
            if (_button != null) _button.interactable = false;
            if (_label != null) _label.text = string.IsNullOrWhiteSpace(label)
                ? "正在处理…" : label;
        }
    }
}
