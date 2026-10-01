using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using JianghuYouling.Effects;     // EffectHandler
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>「江湖纪事」查看窗：按年月回看江湖事件故事、真实动作结果、风闻与同道行止。
    /// 人物对话记录由人物页的独立「聊天记录」窗口查看，不再混入纪事。</summary>
    public static class HistoryWindow
    {
        static GameObject _root;
        static TextMeshProUGUI _title;
        static RectTransform _content;
        static ScrollRect _scroll;
        static GameObject _conversationRoot;
        static GameObject _eventSplitRoot;
        static RectTransform _eventNavContent;
        static RectTransform _eventDetailContent;
        static ScrollRect _eventNavScroll;
        static ScrollRect _eventDetailScroll;
        static TMP_FontAsset _font;
        static int _taiwuId;
        static int _worldGeneration = -1;
        static uint _worldId;
        static long _requestVersion;
        static int _selectedEventDate = int.MinValue;
        static readonly Dictionary<string, CancellationTokenSource> CompanionDetailRequests =
            new Dictionary<string, CancellationTokenSource>(System.StringComparer.Ordinal);
        static readonly HashSet<string> ExpandedCompanionDetails =
            new HashSet<string>(System.StringComparer.Ordinal);
        static readonly Dictionary<string, string> CompanionDetailErrors =
            new Dictionary<string, string>(System.StringComparer.Ordinal);
        static readonly Dictionary<string, CancellationTokenSource> EventDetailRequests =
            new Dictionary<string, CancellationTokenSource>(System.StringComparer.Ordinal);
        static readonly HashSet<string> ExpandedEventDetails =
            new HashSet<string>(System.StringComparer.Ordinal);
        static readonly Dictionary<string, string> EventDetailErrors =
            new Dictionary<string, string>(System.StringComparer.Ordinal);

        static readonly Color ColPanel = new Color(0.10f, 0.11f, 0.10f, 0.98f);
        static readonly Color ColRow = new Color(0.16f, 0.17f, 0.16f, 0.96f);
        static readonly Color ColRowHi = new Color(0.22f, 0.26f, 0.24f, 0.98f);
        static readonly Color ColText = new Color(0.93f, 0.90f, 0.80f, 1f);
        static readonly Color ColSys = new Color(0.64f, 0.64f, 0.60f, 1f);
        static readonly Color ColTabOn = new Color(0.30f, 0.44f, 0.41f, 0.98f);
        static readonly Color ColTabOff = new Color(0.18f, 0.20f, 0.20f, 0.95f);
        static readonly Color ColAccent = new Color(0.30f, 0.38f, 0.36f, 0.95f);

        public static bool IsOpen => _root != null && _root.activeSelf;
        public static bool IsViewingEventDate(int taiwuId, int date)
            => IsOpen && _taiwuId == taiwuId && _selectedEventDate == date
                && WorldLifecycle.IsSameWorld(_worldGeneration) && WorldLifecycle.WorldId == _worldId;

        public static void Open(int taiwuId, TMP_FontAsset font)
        {
            _worldGeneration = WorldLifecycle.Generation;
            _worldId = WorldLifecycle.WorldId;
            if (!WorldLifecycle.IsSameWorld(_worldGeneration) || _worldId == 0) return;
            unchecked { _requestVersion++; }
            if (font != null) _font = font;
            if (_font == null) _font = FindFont();
            ApplyTmpDefaultFont(_font);
            GlyphSanitizer.SetFont(_font);
            _taiwuId = taiwuId;
            if (_root == null) Build();
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            Render();
        }

        // 直达「纪事」页并展开最新一条(供过月弹窗「查看详情」)
        public static void OpenLatestEvent(int taiwuId, TMP_FontAsset font)
        {
            _selectedEventDate = int.MinValue;
            Open(taiwuId, font);
        }

        public static void OpenEventDate(int taiwuId, int date, TMP_FontAsset font)
        {
            _selectedEventDate = date;
            Open(taiwuId, font);
        }

        public static void RefreshEventsIfOpen(int taiwuId, int date)
        {
            if (!IsOpen || _taiwuId != taiwuId
                || !WorldLifecycle.IsSameWorld(_worldGeneration) || WorldLifecycle.WorldId != _worldId) return;
            if (_selectedEventDate == int.MinValue) _selectedEventDate = date;
            float navPosition = _eventNavScroll == null ? 1f : _eventNavScroll.verticalNormalizedPosition;
            float detailPosition = _eventDetailScroll == null ? 1f : _eventDetailScroll.verticalNormalizedPosition;
            Render();
            Canvas.ForceUpdateCanvases();
            if (_eventNavScroll != null) _eventNavScroll.verticalNormalizedPosition = navPosition;
            if (_eventDetailScroll != null) _eventDetailScroll.verticalNormalizedPosition = detailPosition;
        }

        public static void Hide()
        {
            unchecked { _requestVersion++; }
            if (_root != null) _root.SetActive(false);
            MonthlyDigestPopup.NotifyHistoryClosed();
        }

        public static void ResetForWorldExit()
        {
            foreach (CancellationTokenSource request in CompanionDetailRequests.Values)
            {
                try { request.Cancel(); } catch { }
                try { request.Dispose(); } catch { }
            }
            CompanionDetailRequests.Clear();
            ExpandedCompanionDetails.Clear();
            CompanionDetailErrors.Clear();
            foreach (CancellationTokenSource request in EventDetailRequests.Values)
            {
                try { request.Cancel(); } catch { }
                try { request.Dispose(); } catch { }
            }
            EventDetailRequests.Clear();
            ExpandedEventDetails.Clear();
            EventDetailErrors.Clear();
            Hide();
            _taiwuId = 0; _worldGeneration = -1; _worldId = 0; _selectedEventDate = int.MinValue;
            _nameCache.Clear();
            ClearChildren(_content);
            ClearChildren(_eventNavContent);
            ClearChildren(_eventDetailContent);
        }

        static bool RequestCurrent(long requestVersion, int generation, uint worldId)
            => requestVersion == _requestVersion && _root != null && _root.activeSelf
               && worldId > 0 && WorldLifecycle.IsSameWorld(generation) && WorldLifecycle.WorldId == worldId;

        static void Render()
        {
            if (!WorldLifecycle.IsSameWorld(_worldGeneration) || WorldLifecycle.WorldId != _worldId) { Hide(); return; }
            unchecked { _requestVersion++; }
            _title.text = "江湖纪事";
            if (_eventSplitRoot != null) _eventSplitRoot.SetActive(true);
            if (_conversationRoot != null) _conversationRoot.SetActive(false);
            ClearChildren(_eventNavContent);
            ClearChildren(_eventDetailContent);
            RenderEvents();
            Canvas.ForceUpdateCanvases();
            if (_eventNavScroll != null) _eventNavScroll.verticalNormalizedPosition = 1f;
            if (_eventDetailScroll != null) _eventDetailScroll.verticalNormalizedPosition = 1f;
        }

        static void RenderEvents()
        {
            var evs = EventLogStore.Load(_taiwuId);
            if (evs == null) evs = new List<EventLogEntry>();

            // JHYL_HISTORY_TIMELINE_SPLIT: 左侧年月导航只列唯一月份；右侧显示该月总览
            // 的全部正文、执行结果、风闻与同道行止，不再逐条折叠。
            var dates = new List<int>();
            var seenDates = new HashSet<int>();
            foreach (EventLogEntry entry in evs)
                if (entry != null && seenDates.Add(entry.Date)) dates.Add(entry.Date);
            // 正在生成但还未来得及落盘的月份也进入历史导航；玩家无需等待归档完成，
            // 打开同一窗口就能看见江湖事件和主动行事各自的实时状态。
            foreach (int date in MonthlyChronicleProgressStore.Dates(_taiwuId))
                if (seenDates.Add(date)) dates.Add(date);
            // 事务恢复可能把较早月份重新 upsert 到文件头；时间导航必须按游戏日期，
            // 不能把持久化写入顺序误当成世界时间顺序。
            dates.Sort((left, right) => right.CompareTo(left));
            if (dates.Count == 0)
            {
                AddInfoTo(_eventDetailContent, "（江湖暂还无大事记下；每逢月转，事件与主动行事会按年月留在这里。）");
                return;
            }
            if (!seenDates.Contains(_selectedEventDate)) _selectedEventDate = dates[0];
            // 选择即阅读：先建立该月当前内容的已读快照，再绘制左侧月份徽记，避免
            // 用户已经打开月份后仍在同一窗口看见陈旧的“未读”。
            MonthlyChronicleNoticeStore.MarkDateRead(_taiwuId, _selectedEventDate);

            foreach (int date in dates)
            {
                int eventCount = 0, companionCount = 0;
                string area = null;
                var companionKeys = new HashSet<string>();
                foreach (EventLogEntry entry in evs)
                {
                    if (entry == null || entry.Date != date) continue;
                    if (HasEventContent(entry)) eventCount++;
                    if (string.IsNullOrWhiteSpace(area) && !string.IsNullOrWhiteSpace(entry.Area)) area = entry.Area;
                    if (entry.CompanionActions != null)
                        foreach (CompanionMonthlyLogEntry companion in entry.CompanionActions)
                            if (companion != null && companionKeys.Add(CompanionKey(companion))) companionCount++;
                }
                bool selected = date == _selectedEventDate;
                bool generating = MonthlyChronicleProgressStore.TryGet(_taiwuId, date, out MonthlyChronicleProgress monthProgress)
                    && monthProgress.IsGenerating;
                bool stopped = !string.IsNullOrWhiteSpace(monthProgress?.StopReason)
                    || evs.Exists(entry => entry != null && entry.Date == date
                        && !string.IsNullOrWhiteSpace(entry.StopReason));
                int unread = MonthlyChronicleNoticeStore.UnreadCountForDate(_taiwuId, date);
                string meta = "\n<size=72%><color=#8a948f>"
                    + (string.IsNullOrWhiteSpace(area) ? "" : area + " · ")
                    + "事件 " + eventCount + " · 主动行事 " + companionCount
                    + (generating ? " · 生成中" : stopped ? " · 已中止" : "") + "</color>"
                    + (unread > 0 ? "<color=#F2C15E> · 未读 " + unread + "</color>" : "")
                    + "</size>";
                int chosenDate = date;
                AddButtonTo(_eventNavContent, "<color=#C7A86B>" + FmtDate(date) + "</color>" + meta,
                    selected ? ColRowHi : ColRow, 17, () => { _selectedEventDate = chosenDate; Render(); });
            }

            AddInfoTo(_eventDetailContent, "<size=130%><b>" + FmtDate(_selectedEventDate)
                + "</b></size>\n<size=75%><color=#8a948f>本月卷宗</color></size>"
                + "\n<size=78%><color=#C7A86B><b>与事件人物交谈、介入其行动，即可亲自参与并影响后续走向；你的选择还可能改变太吾的江湖名望（名誉）。</b></color></size>", 19, ColText);

            MonthlyChronicleProgressStore.TryGet(_taiwuId, _selectedEventDate,
                out MonthlyChronicleProgress selectedProgress);
            string stopReason = selectedProgress?.StopReason;
            if (string.IsNullOrWhiteSpace(stopReason))
                foreach (EventLogEntry entry in evs)
                    if (entry != null && entry.Date == _selectedEventDate
                        && !string.IsNullOrWhiteSpace(entry.StopReason))
                    { stopReason = entry.StopReason.Trim(); break; }
            if (!string.IsNullOrWhiteSpace(stopReason))
                AddInfoCard(_eventDetailContent,
                    "<color=#D7B873><b>本月生成已中止</b></color>\n\n<color=#B8C2BD>"
                    + stopReason + "</color>");

            bool wroteEvent = false;
            foreach (EventLogEntry entry in evs)
            {
                if (entry == null || entry.Date != _selectedEventDate || !HasEventContent(entry)) continue;
                wroteEvent = true;
                AddEventCard(_eventDetailContent, entry);
            }
            if (!wroteEvent)
            {
                string eventState;
                if (!string.IsNullOrWhiteSpace(stopReason))
                    eventState = "本月江湖事件未完成，具体原因见上方。";
                else if (selectedProgress != null && selectedProgress.EventExpected && !selectedProgress.EventDone)
                    eventState = "江湖事件生成中……";
                else if (selectedProgress != null && !selectedProgress.EventExpected)
                    eventState = "本月未安排江湖事件生成。";
                else if (selectedProgress != null && selectedProgress.EventExpected)
                    eventState = "本月江湖事件已经结束，暂无可归档内容。";
                else
                    eventState = "本月没有已归档的江湖事件。";
                AddInfoCard(_eventDetailContent, "<color=#C7A86B><b>本月江湖事件</b></color>\n\n<color=#8a948f>"
                    + eventState + "</color>");
            }

            var writtenCompanions = new HashSet<string>();
            bool wroteCompanion = false;
            foreach (EventLogEntry entry in evs)
            {
                if (entry == null || entry.Date != _selectedEventDate || entry.CompanionActions == null) continue;
                foreach (CompanionMonthlyLogEntry companion in entry.CompanionActions)
                {
                    if (companion == null || !writtenCompanions.Add(CompanionKey(companion))) continue;
                    wroteCompanion = true;
                    AddCompanionCard(_eventDetailContent, _selectedEventDate, companion);
                }
            }
            if (selectedProgress != null && selectedProgress.CompanionExpected && !selectedProgress.CompanionDone)
            {
                int pending = System.Math.Max(1, selectedProgress.CompanionPending);
                AddInfoCard(_eventDetailContent, "<color=#C7A86B><b>主动行事</b></color>\n\n<color=#8a948f>主动行事生成中……还有 "
                    + pending + " 位尚未完成。</color>");
            }
            else if (!wroteCompanion)
            {
                string companionState = !string.IsNullOrWhiteSpace(stopReason)
                    ? "本月主动行事未完成，具体原因见上方。"
                    : selectedProgress != null && !selectedProgress.CompanionExpected
                    ? "本月未安排主动行事。" : "本月没有已归档的主动行事。";
                AddInfoCard(_eventDetailContent, "<color=#C7A86B><b>主动行事</b></color>\n\n<color=#8a948f>"
                    + companionState + "</color>");
            }
        }

        static string EventDetailKey(EventLogEntry entry)
            => WorldLifecycle.WorldId + ":" + _taiwuId + ":"
                + (string.IsNullOrWhiteSpace(entry?.EventId)
                    ? "date:" + (entry?.Date ?? -1) + ":" + (entry?.Area ?? "")
                    : entry.EventId.Trim());

        static void AddEventCard(RectTransform parent, EventLogEntry entry)
        {
            if (parent == null || entry == null) return;
            string key = EventDetailKey(entry);
            bool expanded = ExpandedEventDetails.Contains(key);
            bool generating = EventDetailRequests.ContainsKey(key);

            var card = new GameObject("EventMonthCard", typeof(RectTransform), typeof(Image),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            card.transform.SetParent(parent, false);
            card.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.035f);
            var layout = card.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childAlignment = TextAnchor.UpperLeft; layout.spacing = 10f;
            layout.padding = new RectOffset(18, 18, 14, 18);
            card.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var header = new GameObject("Header", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            header.transform.SetParent(card.transform, false);
            var headerLayout = header.GetComponent<HorizontalLayoutGroup>();
            headerLayout.childForceExpandWidth = false; headerLayout.childForceExpandHeight = false;
            headerLayout.childControlWidth = true; headerLayout.childControlHeight = true;
            headerLayout.childAlignment = TextAnchor.MiddleLeft; headerLayout.spacing = 10f;
            header.GetComponent<LayoutElement>().minHeight = 38f;

            string heading = "本月江湖事件" + (string.IsNullOrWhiteSpace(entry.Area) ? "" : " · 【" + entry.Area.Trim() + "】");
            var title = NewText("Title", header.transform, 18, TextAlignmentOptions.Left);
            title.text = GlyphSanitizer.Clean("<color=#C7A86B><b>" + heading + "</b></color>");
            title.raycastTarget = false;
            var titleLayout = title.gameObject.AddComponent<LayoutElement>();
            titleLayout.flexibleWidth = 1f; titleLayout.minWidth = 180f;

            bool canGenerate = !string.IsNullOrWhiteSpace(entry.EventId);
            string detailLabel = generating ? "生成中"
                : expanded && !string.IsNullOrWhiteSpace(entry.Detail) ? "收起详情"
                : EventDetailErrors.ContainsKey(key) ? "重试详情"
                : canGenerate || !string.IsNullOrWhiteSpace(entry.Detail) ? "查看详情" : "暂无详情";
            Button detailButton = AddCardButton(header.transform, "Detail", detailLabel, 100f,
                new Color(0.24f, 0.34f, 0.40f, 0.96f), () => ToggleOrGenerateEventDetail(entry));
            detailButton.interactable = !generating && (canGenerate || !string.IsNullOrWhiteSpace(entry.Detail));

            var body = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(entry.Brief))
                body.Append("<color=#C7A86B>事件概要:</color>\n").Append(ReadableParagraphs(entry.Brief));
            if (entry.Actions != null && entry.Actions.Count > 0)
            {
                body.Append(body.Length > 0 ? "\n\n" : "").Append("<color=#8CC7B8>执行结果:</color>");
                foreach (string action in entry.Actions)
                    if (!string.IsNullOrWhiteSpace(action))
                        body.Append("\n<color=#8CC7B8>· ").Append(action.Trim()).Append("</color>");
            }
            if (expanded)
            {
                string story = !string.IsNullOrWhiteSpace(entry.Detail) ? entry.Detail : entry.Text;
                if (!string.IsNullOrWhiteSpace(story))
                    body.Append(body.Length > 0 ? "\n\n" : "")
                        .Append("<color=#C7A86B>事件正文:</color>\n")
                        .Append(ReadableParagraphs(story));
                if (generating)
                    body.Append(body.Length > 0 ? "\n\n" : "")
                        .Append("<color=#C7A86B>正文生成中……</color>");
                else if (EventDetailErrors.TryGetValue(key, out string error))
                    body.Append(body.Length > 0 ? "\n\n" : "")
                        .Append("<color=#D58A72>").Append(error).Append("</color>");
            }
            body.Append(body.Length > 0 ? "\n\n" : "")
                .Append("<size=72%><color=#8a948f>江湖上约 ")
                .Append(entry.Heard < 0 ? 0 : entry.Heard).Append(" 人风闻此事</color></size>");

            var text = NewText("Body", card.transform, 17, TextAlignmentOptions.TopLeft);
            text.enableWordWrapping = true; text.extraPadding = true; text.raycastTarget = false;
            text.color = ColText; text.text = GlyphSanitizer.Clean(body.ToString());
        }

        static void ToggleOrGenerateEventDetail(EventLogEntry entry)
        {
            if (entry == null) return;
            string key = EventDetailKey(entry);
            string existing = !string.IsNullOrWhiteSpace(entry.Detail) ? entry.Detail : entry.Text;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                if (!ExpandedEventDetails.Add(key)) ExpandedEventDetails.Remove(key);
                RefreshEventsIfOpen(_taiwuId, entry.Date);
                return;
            }
            if (string.IsNullOrWhiteSpace(entry.EventId) || EventDetailRequests.ContainsKey(key)) return;

            ExpandedEventDetails.Add(key);
            EventDetailErrors.Remove(key);
            var host = TalkEntryHost.Instance != null ? (MonoBehaviour)TalkEntryHost.Instance : ConfigHost.Instance;
            if (host == null)
            {
                EventDetailErrors[key] = "当前无法开始生成，请稍后重试。";
                RefreshEventsIfOpen(_taiwuId, entry.Date);
                return;
            }
            var cancellation = new CancellationTokenSource();
            EventDetailRequests[key] = cancellation;
            int taiwuId = _taiwuId;
            host.StartCoroutine(GenerateEventDetail(taiwuId, entry, key, cancellation,
                WorldLifecycle.Generation, WorldLifecycle.WorldId));
            RefreshEventsIfOpen(taiwuId, entry.Date);
        }

        static IEnumerator GenerateEventDetail(int taiwuId, EventLogEntry entry, string key,
            CancellationTokenSource cancellation, int generation, uint worldId)
        {
            var client = LlmService.GetBackgroundClient();
            if (client == null)
            {
                FinishEventDetail(key, cancellation, "尚未配置可用的后台模型。", taiwuId, entry.Date);
                yield break;
            }
            List<LlmMessage> messages = MonthlyEventNarrativePrompt.Build(FmtDate(entry.Date),
                entry.Area, entry.Roster, entry.Actions, entry.Brief);
            var task = client.SendAsync(messages, 4096, 0.75, cancellation.Token, 120, false,
                "江湖事件·按需正文", LlmReasoningPolicy.Off);
            yield return new WaitUntil(() => task.IsCompleted || cancellation.IsCancellationRequested
                || !WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId);
            if (cancellation.IsCancellationRequested || !WorldLifecycle.IsSameWorld(generation)
                || WorldLifecycle.WorldId != worldId)
            {
                FinishEventDetail(key, cancellation, null, taiwuId, entry.Date, false);
                yield break;
            }
            string narrative = null;
            try
            {
                LlmResult response = task.Result;
                if (response != null && response.Ok && !string.IsNullOrWhiteSpace(response.Content))
                    narrative = response.Content.Trim();
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(narrative) && narrative.Length > 16000)
                narrative = narrative.Substring(0, 16000).Trim();
            if (string.IsNullOrWhiteSpace(narrative) || narrative.Length < 20)
            {
                FinishEventDetail(key, cancellation, "正文生成失败，请稍后重试。", taiwuId, entry.Date);
                yield break;
            }
            if (!EventLogStore.TryUpdateEventDetail(taiwuId, entry.EventId, narrative))
            {
                FinishEventDetail(key, cancellation, "正文已经生成，但未能写入纪事，请重试。", taiwuId, entry.Date);
                yield break;
            }
            MonthlyChronicleNoticeStore.NotifyArchiveChanged(taiwuId);
            FinishEventDetail(key, cancellation, null, taiwuId, entry.Date);
        }

        static void FinishEventDetail(string key, CancellationTokenSource cancellation, string error,
            int taiwuId, int date, bool refresh = true)
        {
            EventDetailRequests.Remove(key);
            if (string.IsNullOrWhiteSpace(error)) EventDetailErrors.Remove(key);
            else EventDetailErrors[key] = error;
            try { cancellation?.Dispose(); } catch { }
            if (refresh) RefreshEventsIfOpen(taiwuId, date);
        }

        static bool HasEventContent(EventLogEntry entry)
            => entry != null
                && !(entry.EventId ?? string.Empty).StartsWith("monthly-digest:", System.StringComparison.Ordinal)
                && (!string.IsNullOrWhiteSpace(entry.Text) || !string.IsNullOrWhiteSpace(entry.Detail)
                || !string.IsNullOrWhiteSpace(entry.Brief) || entry.Actions != null && entry.Actions.Count > 0);

        static string CompanionKey(CompanionMonthlyLogEntry value)
            => value == null ? "" : value.NpcId > 0 ? "npc:" + value.NpcId : "name:" + (value.Name ?? "");

        static string CompanionDetailKey(int date, int npcId)
            => WorldLifecycle.WorldId + ":" + _taiwuId + ":" + date + ":" + npcId;

        static void AddCompanionCard(RectTransform parent, int date, CompanionMonthlyLogEntry companion)
        {
            if (parent == null || companion == null) return;
            string name = string.IsNullOrWhiteSpace(companion.Name) ? "同道" : companion.Name.Trim();
            string key = CompanionDetailKey(date, companion.NpcId);
            bool expanded = ExpandedCompanionDetails.Contains(key);
            bool generating = CompanionDetailRequests.ContainsKey(key);

            var card = new GameObject("CompanionMonthCard", typeof(RectTransform), typeof(Image),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            card.transform.SetParent(parent, false);
            card.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.035f);
            var layout = card.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childAlignment = TextAnchor.UpperLeft; layout.spacing = 10f;
            layout.padding = new RectOffset(18, 18, 14, 18);
            card.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var header = new GameObject("Header", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            header.transform.SetParent(card.transform, false);
            var headerLayout = header.GetComponent<HorizontalLayoutGroup>();
            headerLayout.childForceExpandWidth = false; headerLayout.childForceExpandHeight = false;
            headerLayout.childControlWidth = true; headerLayout.childControlHeight = true;
            headerLayout.childAlignment = TextAnchor.MiddleLeft; headerLayout.spacing = 10f;
            header.GetComponent<LayoutElement>().minHeight = 38f;

            var title = NewText("Title", header.transform, 18, TextAlignmentOptions.Left);
            title.text = GlyphSanitizer.Clean("<color=#C7A86B><b>主动行事 · " + name + "</b></color>");
            title.raycastTarget = false;
            var titleLayout = title.gameObject.AddComponent<LayoutElement>();
            titleLayout.flexibleWidth = 1f; titleLayout.minWidth = 180f;

            string detailLabel = generating ? "生成中"
                : expanded && !string.IsNullOrWhiteSpace(companion.Detail) ? "收起详情"
                : CompanionDetailErrors.ContainsKey(key) ? "重试详情" : "查看详情";
            Button detailButton = AddCardButton(header.transform, "Detail", detailLabel, 100f,
                new Color(0.24f, 0.34f, 0.40f, 0.96f), () => ToggleOrGenerateCompanionDetail(date, companion));
            detailButton.interactable = !generating;

            AddCardButton(header.transform, "Chat", "去聊聊", 92f, ColTabOn,
                () => OpenConvoFromList(companion.NpcId, name));

            var body = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(companion.LocationText))
                body.Append("<size=80%><color=#78847f>所在地点 · ")
                    .Append(GlyphSanitizer.Clean(companion.LocationText.Trim()))
                    .Append("</color></size>");
            if (companion.Outcomes != null && companion.Outcomes.Count > 0)
            {
                if (body.Length > 0) body.Append("\n\n");
                body.Append("<color=#8CC7B8>行动结果:</color>");
                foreach (string outcome in companion.Outcomes)
                    if (!string.IsNullOrWhiteSpace(outcome))
                        body.Append("\n<color=#8CC7B8>· ").Append(outcome.Trim()).Append("</color>");
            }

            string story = !string.IsNullOrWhiteSpace(companion.Detail) ? companion.Detail : companion.Summary;
            if (expanded)
            {
                if (!string.IsNullOrWhiteSpace(story))
                    body.Append(body.Length > 0 ? "\n\n" : "")
                        .Append("<color=#C7A86B>本月故事:</color>\n")
                        .Append(ReadableParagraphs(story));
                if (generating)
                    body.Append(body.Length > 0 ? "\n\n" : "")
                        .Append("<color=#C7A86B>正文生成中……</color>");
                else if (CompanionDetailErrors.TryGetValue(key, out string error))
                    body.Append(body.Length > 0 ? "\n\n" : "")
                        .Append("<color=#D58A72>").Append(error).Append("</color>");
            }
            else if (!string.IsNullOrWhiteSpace(story))
            {
                body.Append(body.Length > 0 ? "\n\n" : "")
                    .Append("<color=#C7A86B>本月概要:</color>\n")
                    .Append(StoryPreview(story));
            }

            if (body.Length == 0) body.Append("<color=#8a948f>暂无可显示的行动回执。</color>");
            var text = NewText("Body", card.transform, 17, TextAlignmentOptions.TopLeft);
            text.enableWordWrapping = true; text.extraPadding = true; text.raycastTarget = false;
            text.color = ColText; text.text = GlyphSanitizer.Clean(body.ToString());
        }

        static Button AddCardButton(Transform parent, string name, string label, float width,
            Color background, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>(); image.color = background;
            var element = go.GetComponent<LayoutElement>();
            element.preferredWidth = width; element.preferredHeight = 38f; element.flexibleWidth = 0f;
            var button = go.GetComponent<Button>(); button.targetGraphic = image;
            if (onClick != null) button.onClick.AddListener(onClick);
            var text = NewText("Label", go.transform, 15, TextAlignmentOptions.Center);
            Anchor(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            text.text = GlyphSanitizer.Clean(label ?? ""); text.raycastTarget = false;
            return button;
        }

        static string StoryPreview(string value)
        {
            string text = ReadableParagraphs(value);
            if (string.IsNullOrWhiteSpace(text)) return "";
            int firstBreak = text.IndexOf("\n\n", System.StringComparison.Ordinal);
            if (firstBreak > 0) text = text.Substring(0, firstBreak).Trim();
            text = text.Replace("\n", " ").Trim();
            return text.Length <= 240 ? text : text.Substring(0, 240).Trim() + "…";
        }

        static void ToggleOrGenerateCompanionDetail(int date, CompanionMonthlyLogEntry companion)
        {
            if (companion == null || companion.NpcId <= 0) return;
            string key = CompanionDetailKey(date, companion.NpcId);
            if (!string.IsNullOrWhiteSpace(companion.Detail))
            {
                if (!ExpandedCompanionDetails.Add(key)) ExpandedCompanionDetails.Remove(key);
                RefreshEventsIfOpen(_taiwuId, date);
                return;
            }
            if (CompanionDetailRequests.ContainsKey(key)) return;

            ExpandedCompanionDetails.Add(key);
            CompanionDetailErrors.Remove(key);
            var host = TalkEntryHost.Instance != null ? (MonoBehaviour)TalkEntryHost.Instance : ConfigHost.Instance;
            if (host == null)
            {
                CompanionDetailErrors[key] = "当前无法开始生成，请稍后重试。";
                RefreshEventsIfOpen(_taiwuId, date);
                return;
            }

            var cancellation = new CancellationTokenSource();
            CompanionDetailRequests[key] = cancellation;
            int taiwuId = _taiwuId;
            host.StartCoroutine(GenerateCompanionDetail(date, taiwuId, companion, key, cancellation,
                WorldLifecycle.Generation, WorldLifecycle.WorldId));
            RefreshEventsIfOpen(taiwuId, date);
        }

        static IEnumerator GenerateCompanionDetail(int date, int taiwuId, CompanionMonthlyLogEntry companion,
            string key, CancellationTokenSource cancellation, int generation, uint worldId)
        {
            var client = LlmService.GetBackgroundClient();
            if (client == null)
            {
                FinishCompanionDetail(key, cancellation, "尚未配置可用的大模型接口。", taiwuId, date);
                yield break;
            }
            List<LlmMessage> messages = CompanionMonthlyNarrativePrompt.Build(
                FmtDate(date), companion.Name, companion.Outcomes, companion.Summary);
            var task = client.SendAsync(messages, 4096, 0.75, cancellation.Token, 120, false,
                "主动行事·按需正文", LlmReasoningPolicy.Off);
            yield return new WaitUntil(() => task.IsCompleted || cancellation.IsCancellationRequested
                || !WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId);
            if (cancellation.IsCancellationRequested || !WorldLifecycle.IsSameWorld(generation)
                || WorldLifecycle.WorldId != worldId)
            {
                FinishCompanionDetail(key, cancellation, null, taiwuId, date, false);
                yield break;
            }

            string narrative = null;
            try
            {
                LlmResult response = task.Result;
                if (response != null && response.Ok && !string.IsNullOrWhiteSpace(response.Content))
                    narrative = response.Content.Trim();
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(narrative) && narrative.Length > 12000)
                narrative = narrative.Substring(0, 12000).Trim();
            if (string.IsNullOrWhiteSpace(narrative) || narrative.Length < 20)
            {
                FinishCompanionDetail(key, cancellation, "正文生成失败，请稍后重试。", taiwuId, date);
                yield break;
            }
            if (!EventLogStore.TryUpdateCompanionDetail(taiwuId, date, companion.NpcId, narrative))
            {
                FinishCompanionDetail(key, cancellation, "正文已经生成，但未能写入纪事，请重试。", taiwuId, date);
                yield break;
            }
            MonthlyChronicleNoticeStore.NotifyArchiveChanged(taiwuId);
            FinishCompanionDetail(key, cancellation, null, taiwuId, date);
        }

        static void FinishCompanionDetail(string key, CancellationTokenSource cancellation, string error,
            int taiwuId, int date, bool refresh = true)
        {
            CompanionDetailRequests.Remove(key);
            if (string.IsNullOrWhiteSpace(error)) CompanionDetailErrors.Remove(key);
            else CompanionDetailErrors[key] = error;
            try { cancellation?.Dispose(); } catch { }
            if (refresh) RefreshEventsIfOpen(taiwuId, date);
        }

        static string EventBrief(EventLogEntry ev)
        {
            if (ev == null) return "";
            if (!string.IsNullOrWhiteSpace(ev.Text)) return ev.Text;
            return ev.Brief ?? "";
        }

        static string EventBody(EventLogEntry ev)
        {
            if (ev == null) return "";
            if (!string.IsNullOrWhiteSpace(ev.Detail)) return ReadableParagraphs(ev.Detail);
            return ReadableParagraphs(EventBrief(ev));
        }

        static readonly Dictionary<string, string> _nameCache = new Dictionary<string, string>();

        static void RenderConvos(long renderVersion)
        {
            List<TalkOrchestrator.ConversedPartner> raw = null;
            try
            {
                if (!TalkOrchestrator.TryLoadIndexedConversedPartners(_taiwuId, out raw))
                {
                    ChatWindow.PrewarmConversationIndexes(_taiwuId);
                    raw = new List<TalkOrchestrator.ConversedPartner>();
                }
            }
            catch { }
            if (raw == null || raw.Count == 0) { AddInfo("（你还没有和谁聊过；与江湖人交谈后,这里会列出你们的对话。）"); return; }
            AddInfo("点一个人即【开始对话】:近旁或同道=当面交谈,远隔=自动「千里传音」(窗口标题可辨)。", 14, ColSys);

            var list = new List<TalkOrchestrator.ConversedPartner>();
            foreach (var p in raw) if (p != null) list.Add(p);
            var rows = new List<TextMeshProUGUI>();
            foreach (var p in list)
            {
                int npc = p.NpcId;
                var tmp = AddButton(ConvoLabel(p, p.Name), ColRow, 18,
                    () => OpenConvoFromList(npc, p.Name));
                rows.Add(tmp);
            }
            // 异步把旧记录里的缺失/编号占位名解析成真名(已有真名的不再重复解析)
            var host = TalkEntryHost.Instance;
            if (host != null) host.StartCoroutine(ResolveConvoNames(list, rows,
                renderVersion, _worldGeneration, _worldId));
        }

        static string ConvoLabel(TalkOrchestrator.ConversedPartner p, string shownName)
            => "<color=#E8E2CC>"
                + (ArchivedCharacterStatusStore.IsDead(_taiwuId, p.NpcId) ? "已故 · " : "")
                + (TalkOrchestrator.IsUnresolvedNpcName(shownName) ? "姓名载入中…" : shownName)
                + "</color>  <size=70%><color=#8a948f>" + p.Turns + " 句 · 最近 "
                + FmtDate(p.LastDate) + "</color></size>";

        // 把缺失/编号占位名解析成真名并刷新行;已有真名的跳过,省异步调用
        static IEnumerator ResolveConvoNames(List<TalkOrchestrator.ConversedPartner> ps, List<TextMeshProUGUI> rows,
            long requestVersion, int generation, uint worldId)
        {
            for (int i = 0; i < rows.Count && i < ps.Count; i++)
            {
                if (!RequestCurrent(requestVersion, generation, worldId)) yield break;
                var tmp = rows[i]; var p = ps[i];
                if (tmp == null || p == null) continue;
                if (!TalkOrchestrator.IsUnresolvedNpcName(p.Name)) continue;   // 已有真名
                string nm = null;
                yield return ResolveName(p.NpcId, generation, worldId, n => nm = n);
                if (!RequestCurrent(requestVersion, generation, worldId)) yield break;
                if (tmp == null) continue;   // 行已销毁(切页/关窗)
                if (!TalkOrchestrator.IsUnresolvedNpcName(nm))
                {
                    p.Name = nm.Trim();
                    TalkOrchestrator.TryUpdateExistingConversationNameIfCurrent(
                        _taiwuId, p.NpcId, p.Name, generation);
                    tmp.text = ConvoLabel(p, p.Name);
                }
            }
        }

        // 点击对话行:开一场【实时对话】(非只读记录),按此人位置选当面/千里传音
        static void OpenConvoFromList(int npc, string fallbackName)
        {
            var host = TalkEntryHost.Instance;
            if (host == null) return;
            if (!LlmService.IsConfigured) { try { ConfigWindow.Open(_font); } catch { } return; }
            long requestVersion = unchecked(++_requestVersion);
            host.StartCoroutine(OpenConvo(npc, fallbackName, requestVersion, _worldGeneration, _worldId));
        }

        static IEnumerator OpenConvo(int npc, string fallbackName, long requestVersion, int generation, uint worldId)
        {
            int taiwu = _taiwuId;
            if (!RequestCurrent(requestVersion, generation, worldId)) yield break;
            // 取显示名 + 存活态(GetCharacterDisplayData 对死者容错返回底座 + AliveState)
            string nm = null; bool alive = true;
            {
                GameData.Domains.Character.Display.CharacterDisplayData dd = null; bool done = false;
                GameData.Domains.Character.CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, npc, (offset, pool) =>
                { try { GameData.Serializer.Serializer.Deserialize(pool, offset, ref dd); } catch { } finally { done = true; } });
                float ddl = Time.unscaledTime + 3f; while (!done && Time.unscaledTime < ddl) yield return null;
                if (!RequestCurrent(requestVersion, generation, worldId)) yield break;
                if (dd != null) { try { nm = NameCenter.GetMonasticTitleOrDisplayName(dd, false); } catch { } alive = !(dd.AliveState == 1 || dd.AliveState == 2); }
            }
            if (TalkOrchestrator.IsUnresolvedNpcName(nm))
                nm = TalkOrchestrator.IsUnresolvedNpcName(fallbackName) ? "江湖人" : fallbackName;
            // 已故旧识保留全部内容，并进入无工具、无现实行动的灵魂会话。
            if (!alive)
            {
                ArchivedCharacterStatusStore.ApplyAuthoritative(taiwu, null,
                    new[] { npc }, out _);
                Hide();
                ChatWindow.Open(npc, taiwu, nm, _font);
                yield break;
            }

            // 初始模式使用与聊天轮次相同的单次权威现场快照，避免队伍/地块跨帧矛盾。
            bool normal = false, presenceDone = false, presenceReliable = false;
            List<int> present = null;
            EffectHandler.QueryTaiwuScenePresence(taiwu, new[] { npc },
                (ok, ids) => { presenceReliable = ok; present = ids; presenceDone = true; });
            float presenceDeadline = Time.unscaledTime + 3f;
            while (!presenceDone && Time.unscaledTime < presenceDeadline) yield return null;
            if (!RequestCurrent(requestVersion, generation, worldId)) yield break;
            normal = presenceReliable && present != null && present.Contains(npc);

            if (!RequestCurrent(requestVersion, generation, worldId)) yield break;
            Hide();   // 收起见闻录,露出对话窗
            if (normal) ChatWindow.Open(npc, taiwu, nm, _font);
            else ChatWindow.OpenRemote(npc, taiwu, nm, _font);
        }

        // 取该角色当前显示名(含法名/称号);取不到回 null。带缓存,避免重复异步。
        static IEnumerator ResolveName(int charId, int generation, uint worldId, System.Action<string> cb)
        {
            string cacheKey = worldId + ":" + charId;
            if (_nameCache.TryGetValue(cacheKey, out var cached)) { cb(cached); yield break; }
            string nm = null;
            GameData.Domains.Character.Display.CharacterDisplayData dd = null; bool done = false;
            GameData.Domains.Character.CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, charId, (offset, pool) =>
            { try { GameData.Serializer.Serializer.Deserialize(pool, offset, ref dd); } catch { } finally { done = true; } });
            float dl = Time.unscaledTime + 3f;
            while (!done && Time.unscaledTime < dl && WorldLifecycle.IsSameWorld(generation)) yield return null;
            if (!WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId) yield break;
            if (dd != null) { try { nm = NameCenter.GetMonasticTitleOrDisplayName(dd, false); } catch { } }
            if (!string.IsNullOrWhiteSpace(nm)) _nameCache[cacheKey] = nm;
            cb(nm);
        }

        static string FmtDate(int date)
        {
            if (date < 0) return "某年某月";
            int year = date / 12 + 1, month = date % 12 + 1;
            return "第" + year + "年" + month + "月";
        }

        static string OneLine(string s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Replace("\n", " ").Replace("\r", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        static string ReadableParagraphs(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string t = GlyphSanitizer.Clean(text.Replace("\r\n", "\n").Replace("\r", "\n").Trim());
            var lines = t.Split('\n');
            var sb = new System.Text.StringBuilder();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    if (sb.Length > 0 && !EndsWithBlankLine(sb)) sb.Append("\n\n");
                    continue;
                }
                AppendWrappedLine(sb, line);
            }
            return sb.ToString().Trim();
        }

        static void AppendWrappedLine(System.Text.StringBuilder sb, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            int start = 0;
            bool split = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                bool sentenceEnd = c == '。' || c == '！' || c == '？' || c == '!' || c == '?' || c == '；' || c == ';';
                if (sentenceEnd && i - start >= 38)
                {
                    AppendParagraph(sb, line.Substring(start, i - start + 1).Trim());
                    start = i + 1;
                    split = true;
                }
                else if (i - start >= 120)
                {
                    AppendParagraph(sb, line.Substring(start, i - start + 1).Trim());
                    start = i + 1;
                    split = true;
                }
            }
            if (start < line.Length) AppendParagraph(sb, line.Substring(start).Trim());
            else if (!split) AppendParagraph(sb, line.Trim());
        }

        static void AppendParagraph(System.Text.StringBuilder sb, string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            if (sb.Length > 0 && !EndsWithBlankLine(sb)) sb.Append("\n\n");
            sb.Append(p);
        }

        static bool EndsWithBlankLine(System.Text.StringBuilder sb)
        {
            return sb != null && sb.Length >= 2 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 2] == '\n';
        }

        // —— 行:可点按钮(行高随文字自适应:内层 VLG+ContentSizeFitter 撑高,外层 _content 的 VLG 读其首选高)——
        static TextMeshProUGUI AddButton(string text, Color bg, float size, UnityEngine.Events.UnityAction onClick)
            => AddButtonTo(_content, text, bg, size, onClick);

        static TextMeshProUGUI AddButtonTo(RectTransform parent, string text, Color bg, float size,
            UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(Image), typeof(Button),
                                    typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>(); img.color = bg;
            var btn = go.GetComponent<Button>(); btn.targetGraphic = img; btn.onClick.AddListener(onClick);
            var vlg = go.GetComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.padding = new RectOffset(12, 12, 8, 8);
            go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var t = NewText("L", go.transform, size, TextAlignmentOptions.TopLeft);
            t.enableWordWrapping = true; t.color = ColText; t.raycastTarget = false;
            t.text = GlyphSanitizer.Clean(text ?? "");
            return t;
        }

        // —— 行:只读说明/详情(不可点)——
        static void AddInfo(string text, float size = 16, Color? color = null)
            => AddInfoTo(_content, text, size, color);

        static void AddInfoTo(RectTransform parent, string text, float size = 16, Color? color = null)
        {
            var go = new GameObject("Info", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            var f = _font != null ? _font : FindFont();
            if (f != null) t.font = f;
            UiFontSizeStore.Bind(t, size); t.alignment = TextAlignmentOptions.TopLeft; t.richText = true;
            t.color = color ?? ColSys; t.enableWordWrapping = true; t.extraPadding = true;
            t.margin = new Vector4(16, 2, 16, 8); t.raycastTarget = false;
            t.text = GlyphSanitizer.Clean(text ?? "");
        }

        static void AddInfoCard(RectTransform parent, string text)
        {
            var card = new GameObject("MonthCard", typeof(RectTransform), typeof(Image),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            card.transform.SetParent(parent, false);
            card.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.035f);
            var layout = card.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.padding = new RectOffset(18, 18, 16, 18);
            card.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var body = NewText("Body", card.transform, 17, TextAlignmentOptions.TopLeft);
            body.enableWordWrapping = true; body.extraPadding = true; body.raycastTarget = false;
            body.color = ColText; body.text = GlyphSanitizer.Clean(text ?? "");
        }

        static void ClearChildren(RectTransform parent)
        {
            if (parent == null) return;
            for (int i = parent.childCount - 1; i >= 0; i--) Object.Destroy(parent.GetChild(i).gameObject);
        }

        static TMP_FontAsset FindFont()
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
            _root = new GameObject("JHYL_HistoryCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30011;   // 低于聊天记录窗(30013),点对话行能让其盖在上面
            PopupRegistry.Register(_root, Hide);
            var sc = _root.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            prt.sizeDelta = new Vector2(1320, 780);
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().color = ColPanel;
            panel.AddComponent<DragMove>().target = prt;

            _title = NewText("Title", panel.transform, 24, TextAlignmentOptions.Left);
            Anchor(_title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -46), new Vector2(-54, -8));
            _title.text = "江湖纪事";

            var close = NewButton("Close", panel.transform, "X", 22, out var closeBtn);
            var crt = close.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1, 1);
            crt.anchoredPosition = new Vector2(-8, -8); crt.sizeDelta = new Vector2(36, 36);
            closeBtn.onClick.AddListener(Hide);

            // 纪事页：左侧时间导航，右侧为选中月份的完整“本月动向”卷宗。
            _eventSplitRoot = new GameObject("EventTimelineSplit", typeof(RectTransform));
            _eventSplitRoot.transform.SetParent(panel.transform, false);
            Anchor(_eventSplitRoot.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(10, 14), new Vector2(-10, -56));

            var navScrollGo = new GameObject("TimeNavigation", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            navScrollGo.transform.SetParent(_eventSplitRoot.transform, false);
            Anchor(navScrollGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 1),
                Vector2.zero, new Vector2(286, 0));
            navScrollGo.GetComponent<Image>().color = new Color(0.05f, 0.055f, 0.05f, 0.74f);
            _eventNavScroll = navScrollGo.GetComponent<ScrollRect>();
            _eventNavScroll.horizontal = false; _eventNavScroll.vertical = true;
            _eventNavScroll.movementType = ScrollRect.MovementType.Clamped;
            _eventNavScroll.scrollSensitivity = 28f;
            var navContentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            navContentGo.transform.SetParent(navScrollGo.transform, false);
            _eventNavContent = navContentGo.GetComponent<RectTransform>();
            PrepareVerticalContent(_eventNavContent, navContentGo.GetComponent<VerticalLayoutGroup>(), 8f,
                new RectOffset(8, 8, 10, 10));
            navContentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _eventNavScroll.content = _eventNavContent;

            var detailScrollGo = new GameObject("MonthContents", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            detailScrollGo.transform.SetParent(_eventSplitRoot.transform, false);
            Anchor(detailScrollGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(300, 0), Vector2.zero);
            detailScrollGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.22f);
            _eventDetailScroll = detailScrollGo.GetComponent<ScrollRect>();
            _eventDetailScroll.horizontal = false; _eventDetailScroll.vertical = true;
            _eventDetailScroll.movementType = ScrollRect.MovementType.Clamped;
            _eventDetailScroll.scrollSensitivity = 32f;
            var detailContentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            detailContentGo.transform.SetParent(detailScrollGo.transform, false);
            _eventDetailContent = detailContentGo.GetComponent<RectTransform>();
            PrepareVerticalContent(_eventDetailContent, detailContentGo.GetComponent<VerticalLayoutGroup>(), 12f,
                new RectOffset(12, 12, 12, 14));
            detailContentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _eventDetailScroll.content = _eventDetailContent;

            var scrollGo = new GameObject("ConversationScroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            scrollGo.transform.SetParent(panel.transform, false);
            _conversationRoot = scrollGo;
            Anchor(scrollGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 1), new Vector2(10, 14), new Vector2(-10, -98));
            scrollGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.22f);
            _scroll = scrollGo.GetComponent<ScrollRect>();
            _scroll.horizontal = false; _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 28f;

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            _content = contentGo.GetComponent<RectTransform>();
            _content.SetParent(scrollGo.transform, false);
            _content.anchorMin = new Vector2(0, 1); _content.anchorMax = new Vector2(1, 1); _content.pivot = new Vector2(0.5f, 1);
            _content.anchoredPosition = Vector2.zero; _content.sizeDelta = Vector2.zero;
            var vlg = contentGo.GetComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.spacing = 8f; vlg.padding = new RectOffset(6, 6, 10, 10);
            contentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll.content = _content;
            _conversationRoot.SetActive(false);
        }

        static void PrepareVerticalContent(RectTransform content, VerticalLayoutGroup layout, float spacing,
            RectOffset padding)
        {
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1); content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.spacing = spacing; layout.padding = padding;
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            var f = _font != null ? _font : FindFont();
            if (f != null) t.font = f;
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
