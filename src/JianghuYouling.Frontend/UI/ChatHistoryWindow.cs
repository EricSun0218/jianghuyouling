using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>只读聊天记录查看窗:从人物详情页"聊天记录"入口打开,展示太吾与该 NPC 的
    /// 单聊和此 NPC 实际参与过的群聊,不可输入。</summary>
    public static class ChatHistoryWindow
    {
        static GameObject _root;
        static TextMeshProUGUI _title;
        static RectTransform _content;
        static ScrollRect _scroll;
        static TMP_FontAsset _font;
        static int _taiwuId, _npcId;
        static string _npcName;
        static Button _earlierButton, _newerButton;
        static GroupChatOrchestrator.MemberHistoryCursor _groupCursor;
        static GroupChatOrchestrator.MemberHistoryCursor _earlierGroupCursor;
        static readonly Stack<GroupChatOrchestrator.MemberHistoryCursor> NewerGroupCursors
            = new Stack<GroupChatOrchestrator.MemberHistoryCursor>();
        static bool _hasEarlierGroupPage, _hasNewerGroupPage;
        static long _requestEpoch;
        static int _worldGeneration = -1;
        static uint _worldId;
        const int GroupPageLines = 400;

        static readonly Color ColPanel = new Color(0.10f, 0.11f, 0.10f, 0.98f);
        static readonly Color ColNpc = new Color(0.94f, 0.91f, 0.80f, 1f);
        static readonly Color ColPlayer = new Color(0.80f, 0.89f, 1f, 1f);
        static readonly Color ColSys = new Color(0.64f, 0.64f, 0.60f, 1f);
        static readonly Color ColResult = new Color(0.78f, 0.86f, 0.66f, 1f);
        static readonly Color ColAccent = new Color(0.30f, 0.38f, 0.36f, 0.95f);

        public static bool IsOpen => _root != null && _root.activeSelf;

        public static void Open(int npcId, int taiwuId, string npcName, TMP_FontAsset font)
        {
            if (font != null) _font = font;
            if (_font == null) _font = FindFont();
            if (_root == null) Build();
            _root.SetActive(true);
            string nm = string.IsNullOrEmpty(npcName) ? ("NPC#" + npcId) : npcName;
            _taiwuId = taiwuId; _npcId = npcId; _npcName = nm;
            _groupCursor = null;
            _earlierGroupCursor = null;
            NewerGroupCursors.Clear();
            bool deceased = ArchivedCharacterStatusStore.IsDead(taiwuId, npcId);
            _title.text = "聊天记录 · " + nm + (deceased ? "（已故）" : "");
            _worldGeneration = WorldLifecycle.Generation;
            _worldId = WorldLifecycle.WorldId;
            long epoch = unchecked(++_requestEpoch);
            for (int i = _content.childCount - 1; i >= 0; i--)
                Object.Destroy(_content.GetChild(i).gameObject);
            AddRow("（正在后台载入完整聊天索引……）", ColSys, 16);
            var host = TalkEntryHost.Instance;
            if (host != null) host.StartCoroutine(RenderWhenIndexesReady(
                taiwuId, npcId, nm, epoch, _worldGeneration, _worldId));
            else Render(taiwuId, npcId, nm);
        }

        public static void Hide()
        {
            unchecked { _requestEpoch++; }
            if (_root != null) _root.SetActive(false);
        }

        public static void ResetForWorldExit()
        {
            Hide();
            _taiwuId = 0; _npcId = 0; _npcName = null;
            _groupCursor = null;
            _earlierGroupCursor = null;
            _worldGeneration = -1; _worldId = 0;
            NewerGroupCursors.Clear();
            if (_content != null)
                for (int i = _content.childCount - 1; i >= 0; i--) Object.Destroy(_content.GetChild(i).gameObject);
        }

        static void Export()
            => StartExport(exportAll: false);

        static void ExportAllChats()
            => StartExport(exportAll: true);

        static void StartExport(bool exportAll)
        {
            var host = TalkEntryHost.Instance;
            if (host == null) { AddRow("（导出失败：聊天宿主缺失）", ColSys, 15); return; }
            int taiwuId = _taiwuId, npcId = _npcId; string npcName = _npcName;
            long requestEpoch = _requestEpoch;
            AddRow(exportAll ? "（正在后台导出全部聊天记录……）" : "（正在后台导出此人的聊天记录……）", ColSys, 15);
            host.StartCoroutine(StartExportWhenIndexesReady(
                exportAll, taiwuId, npcId, npcName,
                requestEpoch, _worldGeneration, _worldId));
        }

        static IEnumerator StartExportWhenIndexesReady(bool exportAll,
            int taiwuId, int npcId, string npcName, long requestEpoch,
            int generation, uint worldId)
        {
            if (!exportAll)
                yield return ChatWindow.EnsureConversationIndexesReady(
                    taiwuId, generation, worldId);
            if (requestEpoch != _requestEpoch || !IsOpen
                || !WorldLifecycle.IsSameWorld(generation)
                || WorldLifecycle.WorldId != worldId
                || _taiwuId != taiwuId || _npcId != npcId)
                yield break;
            if (!exportAll && !ChatWindow.ConversationIndexesReady(taiwuId))
            {
                AddRow("（聊天索引仍在恢复，已拒绝生成不完整导出；请稍后重试）", ColSys, 15);
                yield break;
            }
            ChatExportService.ExportScope scope = ChatExportService.CaptureScope(taiwuId);
            if (scope == null)
            {
                AddRow("（存档状态已变化，已取消本次导出）", ColSys, 15);
                yield break;
            }
            Task<ChatExportService.Result> task = Task.Run(() => exportAll
                ? ChatExportService.ExportAll(scope, taiwuId)
                : ChatExportService.ExportNpcHistory(scope, taiwuId, npcId, npcName));
            yield return ExportCo(task, exportAll, taiwuId, npcId,
                requestEpoch, generation, worldId);
        }

        static IEnumerator ExportCo(Task<ChatExportService.Result> task, bool exportAll,
            int taiwuId, int npcId, long requestEpoch, int generation, uint worldId)
        {
            while (!task.IsCompleted) yield return null;
            if (requestEpoch != _requestEpoch || !IsOpen
                || !WorldLifecycle.IsSameWorld(generation)
                || WorldLifecycle.WorldId != worldId
                || _taiwuId != taiwuId || _npcId != npcId)
                yield break;
            var r = task.IsFaulted ? null : task.Result;
            if (r != null && r.Ok)
            {
                try { GUIUtility.systemCopyBuffer = r.Path; } catch { }
                if (exportAll)
                    AddRow("（已导出 " + r.ConversationCount + " 场聊天，路径已复制：" + r.Path
                        + (r.SkippedCount > 0 ? ("；另有 " + r.SkippedCount + " 场损坏/跳过") : "") + "）", ColSys, 15);
                else AddRow("（已导出此人的聊天记录，路径已复制：" + r.Path + "）", ColSys, 15);
            }
            else AddRow("（导出失败：" + (r?.Error ?? "未知错误") + "）", ColSys, 15);
        }

        static TMP_FontAsset FindFont()
        {
            try { foreach (var t in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()) if (t != null && t.font != null && t.gameObject.scene.IsValid()) return t.font; } catch { }
            return null;
        }

        static IEnumerator RenderWhenIndexesReady(int taiwuId, int npcId,
            string npcName, long epoch, int generation, uint worldId)
        {
            yield return ChatWindow.EnsureConversationIndexesReady(
                taiwuId, generation, worldId);
            if (epoch != _requestEpoch || !IsOpen
                || !WorldLifecycle.IsSameWorld(generation)
                || WorldLifecycle.WorldId != worldId
                || _taiwuId != taiwuId || _npcId != npcId) yield break;
            if (!ChatWindow.ConversationIndexesReady(taiwuId))
            {
                for (int i = _content.childCount - 1; i >= 0; i--)
                    Object.Destroy(_content.GetChild(i).gameObject);
                AddRow("（聊天索引仍在后台恢复，请稍后重新打开）", ColSys, 16);
                yield break;
            }
            Render(taiwuId, npcId, npcName);
        }

        static void Render(int taiwuId, int npcId, string npcName)
        {
            for (int i = _content.childCount - 1; i >= 0; i--) Object.Destroy(_content.GetChild(i).gameObject);

            if (ArchivedCharacterStatusStore.IsDead(taiwuId, npcId))
                AddRow("（此人已故；聊天、群聊、记忆、人设与画像均完整保留，也可由太吾主动打开聊天窗口继续交谈。灵魂不能主动发消息、调用任何工具、改变现实或参与过月。）",
                    ColSys, 16);

            string summary = TalkOrchestrator.SummaryOf(taiwuId, npcId);
            var turns = TalkOrchestrator.History(taiwuId, npcId);
            var groupSessions = GroupSessionsFor(taiwuId, npcId, npcName,
                out _earlierGroupCursor);
            _hasEarlierGroupPage = _earlierGroupCursor != null;
            _hasNewerGroupPage = NewerGroupCursors.Count > 0;
            if (_earlierButton != null) _earlierButton.gameObject.SetActive(_hasEarlierGroupPage);
            if (_newerButton != null) _newerButton.gameObject.SetActive(_hasNewerGroupPage);
            bool hasSingle = !string.IsNullOrEmpty(summary) || (turns != null && turns.Count > 0);
            bool hasGroup = groupSessions.Count > 0;
            if (!hasSingle && !hasGroup) { AddRow("（你与此人尚无聊天记录）", ColSys, 18); return; }

            if (hasSingle)
            {
                AddRow("【单聊】", ColSys, 17);
                if (!string.IsNullOrEmpty(summary)) AddRow("此前交谈梗概:" + summary, ColSys, 16);
                if (turns != null)
                    foreach (var t in turns)
                    {
                        if (TalkTurnKinds.IsGroupChat(t)) continue;
                        bool hasText = t != null && !string.IsNullOrWhiteSpace(t.Text);
                        bool hasExecution = t != null && (HasToolResults(t.ToolResults)
                            || (t.Actions != null && t.Actions.Exists(x => !string.IsNullOrWhiteSpace(x))));
                        if (t == null || (!hasText && !hasExecution)) continue;
                        string who = TalkTurnKinds.ContextSpeaker(t, npcName);
                        Color rowColor = t.Kind == TalkTurnKinds.NativeGameText ? ColSys
                            : (t.FromPlayer ? ColPlayer : ColNpc);
                        if (hasText) AddRow(who + "：" + t.Text, rowColor, 19);
                        else AddRow("「" + who + "的行动结果」", t.FromPlayer ? ColPlayer : ColNpc, 15);
                        if (!t.FromPlayer && !TalkTurnKinds.IsNative(t)) AddExecutionRows(t.Actions, t.ToolResults);
                    }
            }

            if (hasGroup)
            {
                AddRow("【群聊 · 每页最多 " + GroupPageLines + " 行】", ColSys, 17);
                foreach (var session in groupSessions)
                {
                    AddRow("参与者：" + (string.IsNullOrWhiteSpace(session.Participants) ? "未详" : session.Participants), ColSys, 15);
                    if (session.Lines == null) continue;
                    foreach (var line in session.Lines)
                    {
                        if (line == null || (string.IsNullOrWhiteSpace(line.Text)
                            && !HasToolResults(line.ToolResults))) continue;
                        string who = line.IsTaiwu ? "太吾" : (!string.IsNullOrWhiteSpace(line.Speaker) ? line.Speaker : "江湖中人");
                        Color color = line.IsTaiwu ? ColPlayer
                            : (line.SpeakerId == npcId || string.Equals(who, npcName, System.StringComparison.Ordinal) ? ColNpc : ColSys);
                        if (!string.IsNullOrWhiteSpace(line.Text)) AddRow(who + "：" + line.Text, color, 19);
                        else AddRow("「" + who + "」", color, 15);
                        if (!line.IsTaiwu) AddExecutionRows(line.Actions, line.ToolResults);
                    }
                }
            }
            Canvas.ForceUpdateCanvases();
            if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;   // 从最早一条开始看
        }

        static List<GroupChatOrchestrator.GroupSession> GroupSessionsFor(int taiwuId, int npcId,
            string npcName, out GroupChatOrchestrator.MemberHistoryCursor earlierCursor)
        {
            earlierCursor = null;
            try
            {
                GroupChatOrchestrator.MemberHistoryPage page = new GroupChatOrchestrator()
                    .LoadSessionsForMemberPage(taiwuId, npcId, npcName, GroupPageLines, _groupCursor);
                earlierCursor = page?.EarlierCursor;
                return page?.Sessions ?? new List<GroupChatOrchestrator.GroupSession>();
            }
            catch { }
            return new List<GroupChatOrchestrator.GroupSession>();
        }

        static void ShowEarlierGroupPage()
        {
            if (!_hasEarlierGroupPage || _earlierGroupCursor == null) return;
            NewerGroupCursors.Push(_groupCursor);
            _groupCursor = _earlierGroupCursor;
            Render(_taiwuId, _npcId, _npcName);
        }

        static void ShowNewerGroupPage()
        {
            if (!_hasNewerGroupPage || NewerGroupCursors.Count == 0) return;
            _groupCursor = NewerGroupCursors.Pop();
            Render(_taiwuId, _npcId, _npcName);
        }

        static void AddRow(string text, Color color, float size)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(_content, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            UiFontSizeStore.Bind(t, size); t.alignment = TextAlignmentOptions.TopLeft; t.richText = true;
            t.color = color; t.enableWordWrapping = true; t.extraPadding = true;
            t.margin = new Vector4(10, 2, 10, 3); t.raycastTarget = false;
            t.text = GlyphSanitizer.Clean(text ?? "");
        }

        static bool HasToolResults(IList<string> toolResults)
        {
            if (toolResults == null) return false;
            foreach (string result in toolResults)
                if (!string.IsNullOrWhiteSpace(result)) return true;
            return false;
        }

        static void AddExecutionRows(IList<string> actions, IList<string> toolResults)
        {
            IList<string> source = toolResults != null && toolResults.Count > 0 ? toolResults : actions;
            if (source == null || source.Count == 0) return;
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string raw in source)
            {
                string value = (raw ?? string.Empty).Trim();
                if (value.Length == 0 || !seen.Add(value)) continue;
                AddRow("（结果：" + value + "）", ColResult, 17);
            }
        }

        static void Build()
        {
            _root = new GameObject("JHYL_ChatHistoryCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30013;   // 高于人物菜单
            PopupRegistry.Register(_root, Hide);   // 右键逐个关闭(从最上层依次)
            var sc = _root.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            prt.sizeDelta = new Vector2(680, 560);
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().color = ColPanel;

            _title = NewText("Title", panel.transform, 24, TextAlignmentOptions.Left);
            Anchor(_title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -46), new Vector2(-400, -8));
            _title.text = "聊天记录";

            var close = NewButton("Close", panel.transform, "X", 22, out var closeBtn);
            var crt = close.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1, 1);
            crt.anchoredPosition = new Vector2(-8, -8); crt.sizeDelta = new Vector2(36, 36);
            closeBtn.onClick.AddListener(Hide);

            var export = NewButton("Export", panel.transform, "导出此人", 16, out var exportBtn);
            var ert = export.GetComponent<RectTransform>();
            ert.anchorMin = ert.anchorMax = ert.pivot = new Vector2(1, 1);
            ert.anchoredPosition = new Vector2(-144, -8); ert.sizeDelta = new Vector2(92, 36);
            exportBtn.onClick.AddListener(Export);

            var exportAll = NewButton("ExportAll", panel.transform, "导出全部", 16, out var exportAllBtn);
            var xrt = exportAll.GetComponent<RectTransform>();
            xrt.anchorMin = xrt.anchorMax = xrt.pivot = new Vector2(1, 1);
            xrt.anchoredPosition = new Vector2(-48, -8); xrt.sizeDelta = new Vector2(92, 36);
            exportAllBtn.onClick.AddListener(ExportAllChats);

            var earlier = NewButton("Earlier", panel.transform, "更早", 16, out _earlierButton);
            var eart = earlier.GetComponent<RectTransform>();
            eart.anchorMin = eart.anchorMax = eart.pivot = new Vector2(1, 1);
            eart.anchoredPosition = new Vector2(-240, -8); eart.sizeDelta = new Vector2(72, 36);
            _earlierButton.onClick.AddListener(ShowEarlierGroupPage);
            _earlierButton.gameObject.SetActive(false);

            var newer = NewButton("Newer", panel.transform, "较新", 16, out _newerButton);
            var nwrt = newer.GetComponent<RectTransform>();
            nwrt.anchorMin = nwrt.anchorMax = nwrt.pivot = new Vector2(1, 1);
            nwrt.anchoredPosition = new Vector2(-316, -8); nwrt.sizeDelta = new Vector2(72, 36);
            _newerButton.onClick.AddListener(ShowNewerGroupPage);
            _newerButton.gameObject.SetActive(false);

            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            scrollGo.transform.SetParent(panel.transform, false);
            Anchor(scrollGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 1), new Vector2(10, 14), new Vector2(-10, -56));
            scrollGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.22f);
            _scroll = scrollGo.GetComponent<ScrollRect>();
            _scroll.horizontal = false; _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 26f;

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            _content = contentGo.GetComponent<RectTransform>();
            _content.SetParent(scrollGo.transform, false);
            _content.anchorMin = new Vector2(0, 1); _content.anchorMax = new Vector2(1, 1); _content.pivot = new Vector2(0.5f, 1);
            _content.anchoredPosition = Vector2.zero; _content.sizeDelta = Vector2.zero;
            var vlg = contentGo.GetComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.spacing = 8f; vlg.padding = new RectOffset(4, 4, 10, 10);
            contentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll.content = _content;
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            UiFontSizeStore.Bind(t, size); t.alignment = align; t.richText = true;
            t.color = new Color(0.95f, 0.92f, 0.82f, 1f);
            return t;
        }

        static GameObject NewButton(string name, Transform parent, string label, float size, out Button btn)
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

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
        {
            rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = offMin; rt.offsetMax = offMax;
        }
    }
}
