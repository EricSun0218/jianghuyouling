using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>
    /// 过月纪事入口。正式月度批次统一打开 HistoryWindow 的对应月份；这里保留的自建界面
    /// 只供开发暗号预览，避免游戏里出现两套外观和两套已读规则。
    /// </summary>
    public static class MonthlyDigestPopup
    {
        static GameObject _root;
        static GameObject _eventSection;
        static TextMeshProUGUI _eventBody;
        static GameObject _companionSection;
        static RectTransform _companionList;
        static TextMeshProUGUI _title;
        static ScrollRect _mainScroll;
        static RectTransform _mainContent;
        static GameObject _detailRoot;
        static TextMeshProUGUI _detailTitle;
        static TextMeshProUGUI _detailBody;
        static ScrollRect _detailScroll;
        static RectTransform _detailContent;
        static TMP_FontAsset _font;
        static int _lastTaiwuId;
        static int _lastDate = -1;
        static string _batchKey;          // 已真正显示过的批次；排队等待时绝不提前占用
        static readonly HashSet<string> DismissedBatchKeys = new HashSet<string>(System.StringComparer.Ordinal);
        static readonly Dictionary<string, CancellationTokenSource> NarrativeRequests =
            new Dictionary<string, CancellationTokenSource>(System.StringComparer.Ordinal);
        static string _detailContextKey;
        static bool _showWhenReadyRunning;
        static int _showWhenReadyEpoch;
        static readonly List<PendingProgress> PendingQueue = new List<PendingProgress>();

        sealed class PendingProgress
        {
            public int Date, HeardCount, TaiwuId, CompanionPending, Generation;
            public uint WorldId;
            public string EventText, AreaName, Key;
            public List<CompanionMonthlyResult> CompanionActions;
            public TMP_FontAsset Font;
            public bool EventExpected, EventDone, CompanionExpected, CompanionDone;
        }

        // 兼容调试入口：传入的两段都视为已经结束。
        public static void ShowAll(string eventText, string areaName, int heardCount, List<CompanionMonthlyResult> companionActions, int taiwuId, TMP_FontAsset font)
        {
            // 手动暗号每次都是独立的一次性批次，不沿用“关闭后不重弹”的正式月度 batch key。
            _batchKey = null;
            ShowProgress(-1, eventText, areaName, heardCount, companionActions, taiwuId, font,
                !string.IsNullOrWhiteSpace(eventText), true, companionActions != null, true, 0,
                WorldLifecycle.Generation, WorldLifecycle.WorldId);
        }

        public static void ShowProgress(int date, string eventText, string areaName, int heardCount,
            List<CompanionMonthlyResult> companionActions, int taiwuId, TMP_FontAsset font,
            bool eventExpected, bool eventDone, bool companionExpected, bool companionDone,
            int companionPending, int generation, uint worldId)
        {
            if (taiwuId <= 0 || worldId == 0 || !WorldLifecycle.IsSameWorld(generation)
                || WorldLifecycle.WorldId != worldId) return;
            if (font != null) _font = font;
            string key = worldId + ":" + taiwuId + ":" + date + ":" + generation;
            var progress = new PendingProgress
            {
                Date = date, EventText = eventText, AreaName = areaName, HeardCount = heardCount,
                CompanionActions = companionActions == null ? null : new List<CompanionMonthlyResult>(companionActions),
                TaiwuId = taiwuId, Font = font, EventExpected = eventExpected, EventDone = eventDone,
                CompanionExpected = companionExpected, CompanionDone = companionDone,
                CompanionPending = companionPending, Generation = generation, WorldId = worldId, Key = key,
            };

            if (date >= 0)
            {
                // 生成状态与是否自动打开相互独立：默认关闭弹窗时，玩家仍可从右侧纪事按钮
                // 进入当前月份并看到逐项生成状态。
                MonthlyChronicleProgressStore.Publish(date, taiwuId, eventExpected, eventDone,
                    companionExpected, companionDone, companionPending, generation, worldId);
                HistoryWindow.RefreshEventsIfOpen(taiwuId, date);
                if (!MonthlyDigestPopupStore.Load())
                {
                    RemovePending(key);
                    return;
                }
                if (_font == null) _font = ResolveFont();
                ApplyTmpDefaultFont(_font);
                GlyphSanitizer.SetFont(_font);
                if (DismissedBatchKeys.Contains(key))
                {
                    RemovePending(key);
                    Debug.Log("[JHYL_MONTHLY_POPUP] late update archived after history close key=" + key);
                    return;
                }
                // 玩家已经从右侧纪事入口主动打开这个月份时，视为本批已经展示；之后不能
                // 等其关窗再把同一个月份自动打开一遍。
                if (HistoryWindow.IsViewingEventDate(taiwuId, date))
                {
                    DismissedBatchKeys.Add(key);
                    RemovePending(key);
                    return;
                }
                // 同一月份已经用历史窗口打开后，后续增量只刷新该页，不重复抢焦点。
                if (HistoryWindow.IsOpen && string.Equals(_batchKey, key, System.StringComparison.Ordinal)) return;

                bool anotherBatchVisible = HistoryWindow.IsOpen;
                if (!MonthlyUiReady() || anotherBatchVisible)
                {
                    UpsertPending(progress);
                    if (!MonthlyUiReady()) QueueShowWhenReady();
                    Debug.Log("[JHYL_MONTHLY_POPUP] queued history key=" + key);
                    return;
                }
                RemovePending(key);
                DisplayProgress(progress);
                return;
            }

            if (_font == null) _font = ResolveFont();
            ApplyTmpDefaultFont(_font);
            GlyphSanitizer.SetFont(_font);
            if (_root == null) Build();
            DisplayProgress(progress);
        }

        static void DisplayProgress(PendingProgress progress)
        {
            if (progress == null) return;
            int date = progress.Date;
            string eventText = progress.EventText;
            string areaName = progress.AreaName;
            int heardCount = progress.HeardCount;
            List<CompanionMonthlyResult> companionActions = progress.CompanionActions;
            int taiwuId = progress.TaiwuId;
            bool eventExpected = progress.EventExpected;
            bool eventDone = progress.EventDone;
            bool companionExpected = progress.CompanionExpected;
            bool companionDone = progress.CompanionDone;
            int companionPending = progress.CompanionPending;
            string key = progress.Key;
            _lastTaiwuId = taiwuId;
            _lastDate = date;
            if (progress.Font != null) _font = progress.Font;

            if (date >= 0)
            {
                _batchKey = key;
                // “自动弹出”只负责替玩家打开一次同一套历史窗口。把批次立即记为已展示，
                // 晚到的事件/同道回调只刷新内容，不会关窗后再次弹出。
                DismissedBatchKeys.Add(key);
                HistoryWindow.OpenEventDate(taiwuId, date, _font);
                Debug.Log("[JHYL_MONTHLY_POPUP] opened history key=" + key);
                return;
            }

            bool newBatch = !string.Equals(_batchKey, key, System.StringComparison.Ordinal);
            _batchKey = key;
            if (newBatch)
            {
                _root.SetActive(true);
                if (_detailRoot != null) _detailRoot.SetActive(false);
                if (_mainScroll != null) _mainScroll.verticalNormalizedPosition = 1f;
            }
            // 同一批在玩家手动关闭后只更新缓存视图，不强行再次弹出。
            if (_title != null) _title.text = "<b>" + (date >= 0 ? FmtDate(date) + " · " : "") + "本月动向</b>";

            // —— 事件段(在上,先显示)——
            if (eventExpected)
            {
                _eventSection.SetActive(true);
                // eventDone 只表示生成车道已经收口，正文仍可能由同月恢复/归档回调稍后补到。
                // 空正文一律保持等待态，不能把时序空窗误报成“本月没有事件”。
                if (!eventDone || string.IsNullOrWhiteSpace(eventText))
                    _eventBody.text = "<color=#8a948f>江湖事件生成中……</color>";
                else
                {
                    string head = string.IsNullOrWhiteSpace(areaName) ? "" : ("<color=#8CC7B8>【" + areaName + "】</color> ");
                    string tail = heardCount > 0
                        ? ("\n<size=70%><color=#8a948f>江湖上约 " + heardCount + " 人风闻此事(已入其见闻,日后或会与你说起)</color></size>")
                        : "\n<size=70%><color=#8a948f>此事偏僻,暂无相熟之人风闻</color></size>";
                    string results = LatestOutcomeList(taiwuId, date);
                    _eventBody.text = head + Teaser(eventText) + results + tail
                        + "\n<size=76%><color=#C7A86B><b>与事件人物交谈、介入其行动，即可亲自参与并影响后续走向；你的选择还可能改变太吾的江湖名望（名誉）。</b></color></size>"
                        + "\n<size=70%><color=#8a948f>点查看详情可读本月全部纪事与同道行止，再去与事件中人交谈、行动。</color></size>";
                }
            }
            else _eventSection.SetActive(false);

            // —— 同道主动行事段(在中)——
            if (companionExpected)
            {
                _companionSection.SetActive(true);
                for (int i = _companionList.childCount - 1; i >= 0; i--) Object.Destroy(_companionList.GetChild(i).gameObject);
                if (companionActions != null)
                    foreach (var r in companionActions) AddCompanionRow(r, taiwuId);
                int pending = companionDone ? 0 : System.Math.Max(1, companionPending);
                for (int i = 0; i < pending; i++) AddPendingCompanionRow();
                if (companionDone && (companionActions == null || companionActions.Count == 0))
                    AddCompanionEmptyRow();
            }
            else _companionSection.SetActive(false);

            if (newBatch) Debug.Log("[JHYL_MONTHLY_POPUP] shown key=" + key);
            else Debug.Log("[JHYL_MONTHLY_POPUP] updated key=" + key);
            if (_root.activeSelf) _root.transform.SetAsLastSibling();
            Canvas.ForceUpdateCanvases();
            if (_mainContent != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_mainContent);
        }

        public static void Hide()
        {
            if (_root != null && _root.activeSelf && !string.IsNullOrWhiteSpace(_batchKey))
                DismissedBatchKeys.Add(_batchKey);
            if (_root != null) _root.SetActive(false);
            ChatWindow.RestoreInputFocus();
            TryShowNextPending();
        }

        public static void NotifyHistoryClosed()
        {
            if (string.IsNullOrWhiteSpace(_batchKey)) return;
            _batchKey = null;
            TryShowNextPending();
        }

        static bool MonthlyUiReady()
        {
            try { if (UIElement.MonthNotify.Exist) return false; } catch { }
            try
            {
                var data = SingletonObject.getInstance<BasicGameData>();
                if (data != null && data.AdvancingMonthState != 0) return false;
            }
            catch { return false; }
            return true;
        }

        static void QueueShowWhenReady()
        {
            if (_showWhenReadyRunning) return;
            MonoBehaviour host = TalkEntryHost.Instance;
            if (host == null) host = ConfigHost.Instance;
            if (host == null) return;
            _showWhenReadyRunning = true;
            int epoch = ++_showWhenReadyEpoch;
            host.StartCoroutine(ShowWhenReady(epoch));
        }

        static System.Collections.IEnumerator ShowWhenReady(int epoch)
        {
            // MonthNotify 关闭与其 GameObject 销毁可能跨帧；再让两帧，保证自建 Canvas
            // 在原生月报之后 SetAsLastSibling，而不是被同帧创建的原生层重新盖住。
            // 玩家可能在原生月报里停留数分钟阅读。这里不能用固定 30 秒截止，
            // 否则两条生成线都已结束后不会再有回调重新排队，江湖事件就永久不弹。
            // 世界/批次门控会在切档时自然结束等待，不会把旧月份带进新存档。
            while (epoch == _showWhenReadyEpoch && HasCurrentPending() && !MonthlyUiReady()) yield return null;
            yield return null;
            yield return null;
            if (epoch != _showWhenReadyEpoch) yield break;
            _showWhenReadyRunning = false;
            if (!MonthlyUiReady()) yield break;
            TryShowNextPending();
        }

        static void UpsertPending(PendingProgress value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.Key)) return;
            for (int i = 0; i < PendingQueue.Count; i++)
                if (string.Equals(PendingQueue[i]?.Key, value.Key, System.StringComparison.Ordinal))
                {
                    PendingQueue[i] = value;
                    return;
                }
            PendingQueue.Add(value);
            PendingQueue.Sort((left, right) =>
            {
                int dateCompare = (left?.Date ?? int.MaxValue).CompareTo(right?.Date ?? int.MaxValue);
                return dateCompare != 0 ? dateCompare : string.CompareOrdinal(left?.Key, right?.Key);
            });
        }

        static void RemovePending(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            for (int i = PendingQueue.Count - 1; i >= 0; i--)
                if (string.Equals(PendingQueue[i]?.Key, key, System.StringComparison.Ordinal))
                    PendingQueue.RemoveAt(i);
        }

        static bool HasCurrentPending()
        {
            for (int i = PendingQueue.Count - 1; i >= 0; i--)
            {
                PendingProgress value = PendingQueue[i];
                if (value == null || !WorldLifecycle.IsSameWorld(value.Generation)
                    || WorldLifecycle.WorldId != value.WorldId)
                    PendingQueue.RemoveAt(i);
            }
            return PendingQueue.Count > 0;
        }

        static void TryShowNextPending()
        {
            if (!HasCurrentPending()) return;
            if (!MonthlyUiReady()) { QueueShowWhenReady(); return; }
            if (HistoryWindow.IsOpen || _root != null && _root.activeSelf) return;
            PendingProgress value = PendingQueue[0];
            RemovePending(value.Key);
            if (DismissedBatchKeys.Contains(value.Key))
            {
                TryShowNextPending();
                return;
            }
            ShowProgress(value.Date, value.EventText, value.AreaName, value.HeardCount,
                value.CompanionActions, value.TaiwuId, value.Font, value.EventExpected, value.EventDone,
                value.CompanionExpected, value.CompanionDone, value.CompanionPending,
                value.Generation, value.WorldId);
        }

        public static void ResetForWorldExit()
        {
            foreach (CancellationTokenSource request in NarrativeRequests.Values)
            {
                try { request.Cancel(); } catch { }
                try { request.Dispose(); } catch { }
            }
            NarrativeRequests.Clear();
            if (_root != null) _root.SetActive(false);
            if (_detailRoot != null) _detailRoot.SetActive(false);
            _detailContextKey = null;
            _lastTaiwuId = 0;
            _lastDate = -1;
            _batchKey = null;
            DismissedBatchKeys.Clear();
            PendingQueue.Clear();
            unchecked { _showWhenReadyEpoch++; }
            _showWhenReadyRunning = false;
            if (_eventBody != null) _eventBody.text = "";
            if (_detailBody != null) _detailBody.text = "";
            if (_companionList != null)
                for (int i = _companionList.childCount - 1; i >= 0; i--)
                    Object.Destroy(_companionList.GetChild(i).gameObject);
        }

        public static void NotifySettingsChanged()
        {
            if (MonthlyDigestPopupStore.Load()) return;
            PendingQueue.Clear();
            unchecked { _showWhenReadyEpoch++; }
            _showWhenReadyRunning = false;
            if (_root != null) _root.SetActive(false);
            if (_detailRoot != null) _detailRoot.SetActive(false);
            _detailContextKey = null;
        }

        static string LatestOutcomeList(int taiwuId, int date)
        {
            try
            {
                var evs = EventLogStore.Load(taiwuId);
                if (evs == null || evs.Count == 0) return "";
                EventLogEntry found = null;
                foreach (EventLogEntry entry in evs)
                    if (entry != null && entry.Actions != null && entry.Actions.Count > 0
                        && (date < 0 || entry.Date == date)) { found = entry; break; }
                if (found == null) return "";
                var sb = new System.Text.StringBuilder();
                sb.Append("\n\n<color=#8CC7B8>执行结果:</color>");
                foreach (var a in found.Actions)
                    if (!string.IsNullOrWhiteSpace(a)) sb.Append("\n<color=#8CC7B8>· ").Append(a).Append("</color>");
                return sb.ToString();
            }
            catch { return ""; }
        }

        static void AddPendingCompanionRow()
        {
            var row = new GameObject("CompanionPending", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            row.transform.SetParent(_companionList, false);
            row.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.025f);
            var layout = row.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.padding = new RectOffset(14, 14, 15, 15);
            row.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var text = NewText("Pending", row.transform, 16, TextAlignmentOptions.Left);
            text.text = "<color=#8a948f>同道行止 · 生成中……</color>";
        }

        static void AddCompanionEmptyRow()
        {
            var text = NewText("CompanionEmpty", _companionList, 16, TextAlignmentOptions.Left);
            text.text = "<color=#8a948f>本月同道没有形成新的主动行事。</color>";
        }

        static string FmtDate(int date)
        {
            int year = date / 12 + 1, month = date % 12 + 1;
            return "第" + year + "年" + month + "月";
        }

        static void AddCompanionRow(CompanionMonthlyResult r, int taiwuId)
        {
            if (r == null) return;
            var row = new GameObject("CompanionRow", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            row.transform.SetParent(_companionList, false);
            row.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.035f);
            var vlg = row.GetComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childAlignment = TextAnchor.UpperLeft; vlg.spacing = 5f;
            vlg.padding = new RectOffset(8, 8, 7, 7);
            row.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var header = new GameObject("CompanionHeader", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            header.transform.SetParent(row.transform, false);
            var hl = header.GetComponent<HorizontalLayoutGroup>();
            hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            hl.childControlWidth = true; hl.childControlHeight = true;
            hl.childAlignment = TextAnchor.MiddleLeft; hl.spacing = 8f;
            header.GetComponent<LayoutElement>().minHeight = 34f;

            var txt = NewText("T", header.transform, 18, TextAlignmentOptions.Left);
            txt.enableWordWrapping = true;
            // JHYL_COMPANION_TITLE_NAME_ONLY: 工具成败已经在下方绿字列出,标题旁不重复白字结果。
            txt.text = "<color=#8CC7B8>「" + (r.Name ?? "同道") + "」</color>";
            var le = txt.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f; le.preferredWidth = 360f;

            var btnDetail = new GameObject("CompanionDetail", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            btnDetail.transform.SetParent(header.transform, false);
            btnDetail.GetComponent<Image>().color = new Color(0.24f, 0.34f, 0.40f, 0.96f);
            var dle = btnDetail.GetComponent<LayoutElement>(); dle.preferredWidth = 76f; dle.preferredHeight = 38f; dle.flexibleWidth = 0f;
            var dt = NewText("L", btnDetail.transform, 15, TextAlignmentOptions.Center);
            dt.text = "详情"; dt.raycastTarget = false;
            var dtr = dt.rectTransform; dtr.anchorMin = Vector2.zero; dtr.anchorMax = Vector2.one; dtr.offsetMin = Vector2.zero; dtr.offsetMax = Vector2.zero;
            var rr = r;
            Button detailButton = btnDetail.GetComponent<Button>();
            detailButton.onClick.AddListener(() => OpenOrGenerateCompanionDetail(
                rr, taiwuId, _lastDate, detailButton, dt));

            var btnGo = new GameObject("Go", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            btnGo.transform.SetParent(header.transform, false);
            btnGo.GetComponent<Image>().color = new Color(0.30f, 0.44f, 0.41f, 0.98f);
            var gle = btnGo.GetComponent<LayoutElement>(); gle.preferredWidth = 88f; gle.preferredHeight = 38f; gle.flexibleWidth = 0f;
            var gt = NewText("L", btnGo.transform, 15, TextAlignmentOptions.Center);
            gt.text = "去聊聊"; gt.raycastTarget = false;
            var gtr = gt.rectTransform; gtr.anchorMin = Vector2.zero; gtr.anchorMax = Vector2.one; gtr.offsetMin = Vector2.zero; gtr.offsetMax = Vector2.zero;
            int npc = r.NpcId; string name = r.Name;
            btnGo.GetComponent<Button>().onClick.AddListener(() => ChatWindow.Open(npc, taiwuId, name, _font));

            if (!string.IsNullOrWhiteSpace(r.LocationText))
            {
                var meta = NewText("CompanionLocation", row.transform, 13, TextAlignmentOptions.TopLeft);
                meta.enableWordWrapping = true;
                meta.raycastTarget = false;
                meta.color = new Color(0.48f, 0.53f, 0.50f, 0.82f);
                meta.text = "所在地点 · " + GlyphSanitizer.Clean(r.LocationText.Trim());
            }

            string outcomes = CompanionOutcomeList(r, "行动结果");
            if (!string.IsNullOrWhiteSpace(outcomes))
            {
                var act = NewText("CompanionActions", row.transform, 15, TextAlignmentOptions.TopLeft);
                act.enableWordWrapping = true;
                act.text = outcomes;
            }

            string story = StoryPreview(r);
            if (!string.IsNullOrWhiteSpace(story))
            {
                var st = NewText("CompanionStory", row.transform, 16, TextAlignmentOptions.TopLeft);
                st.enableWordWrapping = true;
                st.text = "<color=#C7A86B>本月故事:</color>\n" + story;
            }
        }

        static string CompanionOutcomeList(CompanionMonthlyResult r, string title)
        {
            if (r == null || r.Outcomes == null || r.Outcomes.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append("<color=#8CC7B8>").Append(title ?? "执行结果").Append(":</color>");
            foreach (var o in r.Outcomes)
                if (!string.IsNullOrWhiteSpace(o)) sb.Append("\n<color=#8CC7B8>· ").Append(o.Trim()).Append("</color>");
            return sb.ToString();
        }

        static string StoryPreview(CompanionMonthlyResult r)
        {
            string text = r == null ? null : (!string.IsNullOrWhiteSpace(r.Detail) ? r.Detail : r.Summary);
            text = ReadableParagraphs(text);
            if (string.IsNullOrWhiteSpace(text)) return "";
            int firstBreak = text.IndexOf("\n\n", System.StringComparison.Ordinal);
            if (firstBreak > 0) text = text.Substring(0, firstBreak).Trim();
            text = text.Replace("\n", " ").Trim();
            return text.Length <= 240 ? text : text.Substring(0, 240).Trim() + "…";
        }

        static string FormatCompanionDetail(CompanionMonthlyResult r)
        {
            if (r == null) return "";
            var sb = new StringBuilder();
            if (r.Outcomes != null && r.Outcomes.Count > 0)
            {
                sb.Append("<color=#8CC7B8>真实执行结果:</color>");
                foreach (var o in r.Outcomes)
                    if (!string.IsNullOrWhiteSpace(o)) sb.Append("\n<color=#8CC7B8>· ").Append(o.Trim()).Append("</color>");
                sb.Append("\n\n");
            }
            if (!string.IsNullOrWhiteSpace(r.Detail)) sb.Append(ReadableParagraphs(r.Detail));
            else if (!string.IsNullOrWhiteSpace(r.Summary)) sb.Append(r.Summary);
            return sb.ToString().Trim();
        }

        static void OpenOrGenerateCompanionDetail(CompanionMonthlyResult result, int taiwuId, int date,
            Button button, TextMeshProUGUI label)
        {
            if (result == null) return;
            string contextKey = WorldLifecycle.WorldId + ":" + taiwuId + ":" + date + ":" + result.NpcId;
            _detailContextKey = contextKey;
            if (!string.IsNullOrWhiteSpace(result.Detail))
            {
                ShowDetail("主动行事 · " + (result.Name ?? "同道"), FormatCompanionDetail(result));
                MonthlyChronicleNoticeStore.MarkCompanionRead(taiwuId, date, result.NpcId);
                return;
            }

            string facts = FormatCompanionDetail(result);
            ShowDetail("主动行事 · " + (result.Name ?? "同道"), facts
                + (string.IsNullOrWhiteSpace(facts) ? "" : "\n\n")
                + "<color=#C7A86B>正文生成中……</color>");
            if (NarrativeRequests.ContainsKey(contextKey)) return;
            var host = TalkEntryHost.Instance != null ? (MonoBehaviour)TalkEntryHost.Instance : ConfigHost.Instance;
            if (host == null)
            {
                ShowDetail("主动行事 · " + (result.Name ?? "同道"), facts
                    + "\n\n<color=#D58A72>当前没有可用的生成宿主，请稍后重试。</color>");
                return;
            }
            var cancellation = new CancellationTokenSource();
            NarrativeRequests[contextKey] = cancellation;
            if (button != null) button.interactable = false;
            if (label != null) label.text = "生成中";
            host.StartCoroutine(GenerateCompanionDetail(result, taiwuId, date, contextKey,
                facts, button, label, cancellation, WorldLifecycle.Generation, WorldLifecycle.WorldId));
        }

        static IEnumerator GenerateCompanionDetail(CompanionMonthlyResult result, int taiwuId, int date,
            string contextKey, string facts, Button button, TextMeshProUGUI label,
            CancellationTokenSource cancellation, int generation, uint worldId)
        {
            var client = LlmService.GetBackgroundClient();
            if (client == null)
            {
                FinishNarrativeRequest(contextKey, button, label, cancellation);
                ShowNarrativeFailureIfCurrent(contextKey, result, facts, "尚未配置可用的大模型接口。");
                yield break;
            }

            List<LlmMessage> messages = CompanionMonthlyNarrativePrompt.Build(
                FmtDate(date), result.Name, result.Outcomes, result.Summary);
            var task = client.SendAsync(messages, 4096, 0.75, cancellation.Token, 120, false,
                "同道纪事·按需正文", LlmReasoningPolicy.Off);
            yield return new WaitUntil(() => task.IsCompleted || cancellation.IsCancellationRequested
                || !WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId);
            if (cancellation.IsCancellationRequested || !WorldLifecycle.IsSameWorld(generation)
                || WorldLifecycle.WorldId != worldId)
            {
                FinishNarrativeRequest(contextKey, button, label, cancellation);
                yield break;
            }

            string narrative = null;
            string failure = null;
            try
            {
                LlmResult response = task.Result;
                if (response != null && response.Ok && !string.IsNullOrWhiteSpace(response.Content))
                    narrative = response.Content.Trim();
                else failure = response?.Error ?? "模型没有返回正文";
            }
            catch (System.Exception exception) { failure = exception.GetType().Name; }
            if (!string.IsNullOrWhiteSpace(narrative) && narrative.Length > 12000)
                narrative = narrative.Substring(0, 12000).Trim();
            if (string.IsNullOrWhiteSpace(narrative) || narrative.Length < 20)
            {
                FinishNarrativeRequest(contextKey, button, label, cancellation);
                ShowNarrativeFailureIfCurrent(contextKey, result, facts,
                    string.IsNullOrWhiteSpace(failure) ? "模型没有生成完整正文" : failure);
                yield break;
            }

            if (!EventLogStore.TryUpdateCompanionDetail(taiwuId, date, result.NpcId, narrative))
            {
                FinishNarrativeRequest(contextKey, button, label, cancellation);
                ShowNarrativeFailureIfCurrent(contextKey, result, facts, "正文已经生成，但未能写入纪事，请重试。");
                yield break;
            }
            result.Detail = narrative;
            MonthlyChronicleNoticeStore.NotifyArchiveChanged(taiwuId);
            HistoryWindow.RefreshEventsIfOpen(taiwuId, date);
            FinishNarrativeRequest(contextKey, button, label, cancellation);
            if (string.Equals(_detailContextKey, contextKey, System.StringComparison.Ordinal)
                && _detailRoot != null && _detailRoot.activeSelf)
            {
                ShowDetail("主动行事 · " + (result.Name ?? "同道"), FormatCompanionDetail(result));
                MonthlyChronicleNoticeStore.MarkCompanionRead(taiwuId, date, result.NpcId);
            }
        }

        static void FinishNarrativeRequest(string key, Button button, TextMeshProUGUI label,
            CancellationTokenSource cancellation)
        {
            NarrativeRequests.Remove(key);
            if (button != null) button.interactable = true;
            if (label != null) label.text = "详情";
            try { cancellation?.Dispose(); } catch { }
        }

        static void ShowNarrativeFailureIfCurrent(string contextKey, CompanionMonthlyResult result,
            string facts, string error)
        {
            if (!string.Equals(_detailContextKey, contextKey, System.StringComparison.Ordinal)
                || _detailRoot == null || !_detailRoot.activeSelf) return;
            string safe = string.IsNullOrWhiteSpace(error) ? "生成失败" : error.Trim();
            if (safe.Length > 160) safe = safe.Substring(0, 160) + "…";
            ShowDetail("主动行事 · " + (result?.Name ?? "同道"), facts
                + "\n\n<color=#D58A72>正文生成失败：" + safe + "。关闭后可再次点击详情重试。</color>");
        }

        static void ShowDetail(string title, string body)
        {
            if (_root == null) return;
            if (_detailRoot == null) BuildDetailOverlay();
            if (_detailTitle != null) _detailTitle.text = GlyphSanitizer.Clean(title ?? "详情");
            if (_detailBody != null) _detailBody.text = GlyphSanitizer.Clean(
                string.IsNullOrWhiteSpace(body) ? "暂无详情。" : body);
            _detailRoot.SetActive(true);
            _detailRoot.transform.SetAsLastSibling();
            Canvas.ForceUpdateCanvases();
            if (_detailContent != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_detailContent);
            if (_detailScroll != null) _detailScroll.verticalNormalizedPosition = 1f;
            Canvas.ForceUpdateCanvases();
        }

        static void HideDetail()
        {
            if (_detailRoot != null) _detailRoot.SetActive(false);
            _detailContextKey = null;
        }

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
                if (_font == null)
                {
                    var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (fonts != null && fonts.Length > 0) _font = fonts[0];
                }
            }
            catch { }
            ApplyTmpDefaultFont(_font);
            GlyphSanitizer.SetFont(_font);
            return _font;
        }

        static bool _tmpDefaultFontSet;
        static void ApplyTmpDefaultFont(TMP_FontAsset f)
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

        static void Build()
        {
            _root = new GameObject("JHYL_MonthDigestCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30006;
            PopupRegistry.Register(_root, Hide);
            // Build may run while the native monthly report is still covering the screen.
            // New GameObjects start active, so hide the canvas before constructing any child;
            // ShowProgress is the only place allowed to reveal a ready monthly batch.
            _root.SetActive(false);
            var sc = _root.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);

            // JHYL_MONTHLY_DIGEST_LARGE_SCROLL: 固定大画幅 + 独立滚动卷面，避免同道较多时
            // ContentSizeFitter 把整窗顶出屏幕。
            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            prt.sizeDelta = new Vector2(1120, 780);
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.12f, 0.97f);
            panel.AddComponent<DragMove>().target = prt;

            _title = NewText("Title", panel.transform, 25, TextAlignmentOptions.Left);
            _title.text = "<b>本月动向</b>";
            _title.color = new Color(0.95f, 0.92f, 0.82f, 1f);
            var titleRt = _title.rectTransform;
            titleRt.anchorMin = new Vector2(0, 1); titleRt.anchorMax = new Vector2(1, 1);
            titleRt.pivot = new Vector2(0.5f, 1); titleRt.offsetMin = new Vector2(22, -58);
            titleRt.offsetMax = new Vector2(-64, -14);

            var scrollGo = new GameObject("DigestScroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(panel.transform, false);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero; scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(18, 18); scrollRt.offsetMax = new Vector2(-18, -68);
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.20f);
            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(scrollGo.transform, false);
            var viewportRt = viewport.GetComponent<RectTransform>();
            viewportRt.anchorMin = Vector2.zero; viewportRt.anchorMax = Vector2.one;
            viewportRt.offsetMin = new Vector2(12, 10); viewportRt.offsetMax = new Vector2(-12, -10);
            var contentGo = new GameObject("DigestContent", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewport.transform, false);
            _mainContent = contentGo.GetComponent<RectTransform>();
            _mainContent.anchorMin = new Vector2(0, 1); _mainContent.anchorMax = new Vector2(1, 1);
            _mainContent.pivot = new Vector2(0.5f, 1); _mainContent.anchoredPosition = Vector2.zero;
            _mainContent.sizeDelta = Vector2.zero;
            var mainLayout = contentGo.GetComponent<VerticalLayoutGroup>();
            mainLayout.childForceExpandWidth = true; mainLayout.childForceExpandHeight = false;
            mainLayout.childControlWidth = true; mainLayout.childControlHeight = true;
            mainLayout.spacing = 14f; mainLayout.padding = new RectOffset(8, 8, 8, 12);
            contentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _mainScroll = scrollGo.GetComponent<ScrollRect>();
            _mainScroll.viewport = viewportRt; _mainScroll.content = _mainContent;
            _mainScroll.horizontal = false; _mainScroll.vertical = true;
            _mainScroll.movementType = ScrollRect.MovementType.Clamped;
            _mainScroll.scrollSensitivity = 34f;

            // —— 事件段 ——
            _eventSection = NewSection(_mainContent, "EventSection");
            // #15 标题行:左=「本月江湖事件」标题,右=「查看详情 ›」按钮(同一行最右,而非另起一整条横幅)
            var evHeader = new GameObject("EvHeader", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            evHeader.transform.SetParent(_eventSection.transform, false);
            var ehl = evHeader.GetComponent<HorizontalLayoutGroup>();
            ehl.childForceExpandWidth = false; ehl.childForceExpandHeight = false;
            ehl.childControlWidth = true; ehl.childControlHeight = true;
            ehl.childAlignment = TextAnchor.MiddleLeft; ehl.spacing = 8f;
            evHeader.GetComponent<LayoutElement>().minHeight = 34f;
            var evTitle = NewText("EvTitle", evHeader.transform, 18, TextAlignmentOptions.Left);
            evTitle.text = "<b>本月江湖事件</b>  <size=64%><color=#C7A86B>◆ 真实发生</color></size>";
            evTitle.color = new Color(0.90f, 0.88f, 0.78f, 1f);
            var evTitleLe = evTitle.gameObject.AddComponent<LayoutElement>();
            evTitleLe.flexibleWidth = 1f;   // 标题占满左侧,把按钮顶到最右
            // 点击看详情 → 统一历史窗(直达本月事件展开,亦可翻历往纪事与对话)
            var detailGo = new GameObject("EvDetail", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            detailGo.transform.SetParent(evHeader.transform, false);
            detailGo.GetComponent<Image>().color = new Color(0.24f, 0.34f, 0.40f, 0.96f);
            var detailLe = detailGo.GetComponent<LayoutElement>();
            detailLe.preferredHeight = 30f; detailLe.preferredWidth = 132f; detailLe.flexibleWidth = 0f;
            var dbt = NewText("L", detailGo.transform, 14, TextAlignmentOptions.Center);
            dbt.text = "查看详情"; dbt.raycastTarget = false;   // 去掉尾部 ›:游戏字体无此字形,会渲染成豆腐块「口」
            var dbtr = dbt.rectTransform; dbtr.anchorMin = Vector2.zero; dbtr.anchorMax = Vector2.one; dbtr.offsetMin = Vector2.zero; dbtr.offsetMax = Vector2.zero;
            detailGo.GetComponent<Button>().onClick.AddListener(() =>
            {
                try
                {
                    if (_lastDate >= 0) HistoryWindow.OpenEventDate(_lastTaiwuId, _lastDate, _font);
                    else HistoryWindow.OpenLatestEvent(_lastTaiwuId, _font);
                }
                catch { }
            });
            _eventBody = NewText("EvBody", _eventSection.transform, 18, TextAlignmentOptions.Left);
            _eventBody.enableWordWrapping = true;
            _eventSection.SetActive(false);

            // —— 同道主动行事段 ——
            _companionSection = NewSection(_mainContent, "CompanionSection");
            var companionTitle = NewText("CompanionTitle", _companionSection.transform, 18, TextAlignmentOptions.Left);
            companionTitle.text = "<b>主动行事</b>  <size=70%><color=#8a948f>(基于记忆真实落地)</color></size>";
            companionTitle.color = new Color(0.90f, 0.88f, 0.78f, 1f);
            var companionIntro = NewText("CompanionIntro", _companionSection.transform, 14, TextAlignmentOptions.Left);
            companionIntro.text = "<color=#8a948f>本月从全部当前同道中稳定抽选,按记忆、关系与处境真正做事;点详情看因果、成败与影响。</color>";
            companionIntro.enableWordWrapping = true;
            var companionListGo = new GameObject("CompanionList", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            companionListGo.transform.SetParent(_companionSection.transform, false);
            _companionList = companionListGo.GetComponent<RectTransform>();
            var cvlg = companionListGo.GetComponent<VerticalLayoutGroup>();
            cvlg.childForceExpandWidth = true; cvlg.childForceExpandHeight = false;
            cvlg.childControlWidth = true; cvlg.childControlHeight = true; cvlg.spacing = 6f;
            companionListGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _companionSection.SetActive(false);

            // 右上角关闭 X
            var closeGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
            closeGo.transform.SetParent(panel.transform, false);
            var crt = closeGo.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1, 1);
            crt.anchoredPosition = new Vector2(-6, -6); crt.sizeDelta = new Vector2(34, 34);
            closeGo.GetComponent<Image>().color = new Color(0.42f, 0.30f, 0.30f, 0.96f);
            var ct = NewText("X", closeGo.transform, 20, TextAlignmentOptions.Center);
            ct.text = "X"; ct.raycastTarget = false;
            var ctr = ct.rectTransform; ctr.anchorMin = Vector2.zero; ctr.anchorMax = Vector2.one; ctr.offsetMin = Vector2.zero; ctr.offsetMax = Vector2.zero;
            closeGo.GetComponent<Button>().onClick.AddListener(Hide);
        }

        static void BuildDetailOverlay()
        {
            _detailRoot = new GameObject("CompanionDetailOverlay", typeof(RectTransform), typeof(Image));
            _detailRoot.transform.SetParent(_root.transform, false);
            var rrt = _detailRoot.GetComponent<RectTransform>();
            rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one; rrt.offsetMin = Vector2.zero; rrt.offsetMax = Vector2.zero;
            _detailRoot.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_detailRoot.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(980, 720);
            prt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(0.09f, 0.10f, 0.10f, 0.98f);

            _detailTitle = NewText("Title", panel.transform, 22, TextAlignmentOptions.Left);
            _detailTitle.color = new Color(0.95f, 0.92f, 0.82f, 1f);
            var trt = _detailTitle.rectTransform;
            trt.anchorMin = trt.anchorMax = new Vector2(0, 1);
            trt.pivot = new Vector2(0, 1);
            trt.anchoredPosition = new Vector2(22, -18);
            trt.sizeDelta = new Vector2(850, 36);

            var close = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
            close.transform.SetParent(panel.transform, false);
            var crt = close.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1, 1);
            crt.anchoredPosition = new Vector2(-14, -14); crt.sizeDelta = new Vector2(38, 38);
            close.GetComponent<Image>().color = new Color(0.42f, 0.30f, 0.30f, 0.96f);
            var ct = NewText("X", close.transform, 20, TextAlignmentOptions.Center);
            ct.text = "X"; ct.raycastTarget = false;
            var ctr = ct.rectTransform; ctr.anchorMin = Vector2.zero; ctr.anchorMax = Vector2.one; ctr.offsetMin = Vector2.zero; ctr.offsetMax = Vector2.zero;
            close.GetComponent<Button>().onClick.AddListener(HideDetail);

            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(panel.transform, false);
            var srt = scrollGo.GetComponent<RectTransform>();
            srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;
            srt.offsetMin = new Vector2(20, 22); srt.offsetMax = new Vector2(-20, -68);
            scrollGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(scrollGo.transform, false);
            var vrt = viewport.GetComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one; vrt.offsetMin = new Vector2(10, 10); vrt.offsetMax = new Vector2(-10, -10);

            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            var crt2 = content.GetComponent<RectTransform>();
            crt2.anchorMin = new Vector2(0, 1); crt2.anchorMax = new Vector2(1, 1); crt2.pivot = new Vector2(0.5f, 1f);
            crt2.offsetMin = new Vector2(0, 0); crt2.offsetMax = new Vector2(0, 0);
            _detailContent = crt2;
            var cvlg = content.GetComponent<VerticalLayoutGroup>();
            cvlg.childForceExpandWidth = true; cvlg.childForceExpandHeight = false;
            cvlg.childControlWidth = true; cvlg.childControlHeight = true;
            cvlg.childAlignment = TextAnchor.UpperLeft;
            cvlg.padding = new RectOffset(0, 0, 0, 0);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _detailBody = NewText("Body", content.transform, 18, TextAlignmentOptions.TopLeft);
            _detailBody.enableWordWrapping = true;
            var ble = _detailBody.gameObject.AddComponent<LayoutElement>();
            ble.flexibleWidth = 1f; ble.minHeight = 1f;
            var brt = _detailBody.rectTransform;
            brt.anchorMin = new Vector2(0, 1); brt.anchorMax = new Vector2(1, 1); brt.pivot = new Vector2(0.5f, 1f);
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;

            var sr = scrollGo.GetComponent<ScrollRect>();
            sr.viewport = vrt;
            sr.content = crt2;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 32f;
            _detailScroll = sr;
            _detailRoot.SetActive(false);
        }

        // 一段:带细分隔上边距的纵向容器
        static GameObject NewSection(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            go.transform.SetParent(parent, false);
            var vlg = go.GetComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.spacing = 4f; vlg.padding = new RectOffset(0, 0, 6, 2);
            go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return go;
        }

        // 取一段吸引人的简介:保留【连载】前缀(若有)+ 正文开篇约一句话(到第一个句末标点,至少 ~24 字、至多 ~56 字),后接「…」。
        static string Teaser(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string t = text.Replace("\r", "").Trim();
            string prefix = "";
            if (t.StartsWith("【江湖连载"))
            {
                int nl = t.IndexOf('\n');
                if (nl > 0) { prefix = t.Substring(0, nl).Trim() + "\n"; t = t.Substring(nl + 1).Trim(); }
            }
            t = t.Replace("\n", " ");
            int cut = t.Length;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if ((c == '。' || c == '!' || c == '!' || c == '?' || c == '?' || c == ';' || c == ';') && i + 1 >= 24) { cut = i + 1; break; }
                if (i >= 56) { cut = i; break; }
            }
            string body = t.Substring(0, cut).Trim();
            if (body.Length < t.Length) body += "…";
            return prefix + body;
        }

        static string ReadableParagraphs(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string t = text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            var lines = t.Split('\n');
            var sb = new StringBuilder();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n")) sb.Append("\n\n");
                    continue;
                }
                if (line.Length > 110)
                {
                    int start = 0;
                    for (int i = 0; i < line.Length; i++)
                    {
                        char c = line[i];
                        if ((c == '。' || c == '！' || c == '？' || c == ';' || c == '；') && i - start >= 40)
                        {
                            if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n")) sb.Append("\n\n");
                            sb.Append(line.Substring(start, i - start + 1).Trim());
                            start = i + 1;
                        }
                    }
                    if (start < line.Length)
                    {
                        if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n")) sb.Append("\n\n");
                        sb.Append(line.Substring(start).Trim());
                    }
                }
                else
                {
                    if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n")) sb.Append("\n\n");
                    sb.Append(line);
                }
            }
            return sb.ToString().Trim();
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            var f = _font != null ? _font : ResolveFont();
            if (f != null) t.font = f;
            UiFontSizeStore.Bind(t, size); t.alignment = align; t.richText = true; t.extraPadding = true;
            t.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            return t;
        }
    }
}
