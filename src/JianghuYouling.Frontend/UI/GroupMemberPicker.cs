using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using JianghuYouling.Effects;
using JianghuYouling.Core.Behavior;

namespace JianghuYouling
{
    /// <summary>群聊选人:候选 = 所有聊过的人 ∪ 打开面板时当前地块上的人;两列多选 + 全选 + 确定。
    /// 已在当前单聊/群聊中的成员会预留席位并从候选中排除；取消或无人可选回传 null。</summary>
    public static class GroupMemberPicker
    {
        const int MaxMembers = 8;
        const int PageSize = 48;
        static int _taiwuId;
        static int _selectionLimit = MaxMembers;
        static bool _confirming;
        static GameObject _root;
        static RectTransform _list;
        static ScrollRect _scroll;
        static int _page;
        static TextMeshProUGUI _pageLabel;
        static Button _previousPageButton;
        static Button _nextPageButton;
        static TMP_FontAsset _font;
        static Action<List<int>> _onConfirm;
        static Action<string> _onFailure;
        static uint _worldId;
        static int _worldGeneration = -1;
        static long _requestEpoch;
        static bool _newMembersSeeHistory;
        static readonly HashSet<int> _reservedMemberIds = new HashSet<int>();
        static readonly List<int> _ids = new List<int>();
        static readonly List<string> _names = new List<string>();
        // 1=曾聊过，2=打开选人面板时在太吾当前地块；3=两者皆是。
        static readonly List<int> _sources = new List<int>();
        static readonly List<bool> _sel = new List<bool>();
        static readonly List<int> _visibleRowIndices = new List<int>();
        static readonly List<Image> _rowBg = new List<Image>();
        static readonly List<TextMeshProUGUI> _rowLbl = new List<TextMeshProUGUI>();
        static readonly Color SelCol = new Color(0.22f, 0.54f, 0.46f, 0.98f);   // 选中=醒目青绿实色(仅靠颜色区分,不再用✔图标——字体缺该字会显示成□)
        static readonly Color UnselCol = new Color(1f, 1f, 1f, 0.04f);          // 未选=极淡

        public static void Show(int taiwuId, TMP_FontAsset font, Action<List<int>> onConfirm,
            IEnumerable<int> reservedMemberIds = null, bool newMembersSeeHistory = false,
            Action<string> onFailure = null)
        {
            // static + DontDestroyOnLoad 必须先终止上一请求；否则旧世界的晚到 RPC 会重建选人 UI。
            CancelCurrent(true);
            uint worldId = WorldLifecycle.WorldId;
            int worldGeneration = WorldLifecycle.Generation;
            if (taiwuId <= 0 || worldId == 0 || !WorldStillCurrent(worldId, worldGeneration))
            {
                InvokeCallback(onConfirm, null);
                InvokeFailure(onFailure, "当前存档尚未就绪，无法打开加人窗口");
                return;
            }

            long requestEpoch = unchecked(++_requestEpoch);
            _worldId = worldId;
            _worldGeneration = worldGeneration;
            _taiwuId = taiwuId;
            _onConfirm = onConfirm;
            _onFailure = onFailure;
            _newMembersSeeHistory = newMembersSeeHistory;
            _reservedMemberIds.Clear();
            if (reservedMemberIds != null)
                foreach (int id in reservedMemberIds)
                    if (id > 0 && id != taiwuId && _reservedMemberIds.Count < MaxMembers)
                        _reservedMemberIds.Add(id);
            _selectionLimit = Math.Max(0, MaxMembers - _reservedMemberIds.Count);
            if (_selectionLimit == 0)
            {
                var cb = _onConfirm; _onConfirm = null;
                CancelCurrent(false);
                InvokeCallback(cb, null);
                return;
            }
            if (font != null) _font = font;
            if (_font == null) _font = ResolveFont();
            var host = TalkEntryHost.Instance;
            if (host == null) { FailClosed(requestEpoch, "群聊宿主尚未就绪，请稍后重试"); return; }
            host.StartCoroutine(LoadThenShow(taiwuId, requestEpoch, worldId, worldGeneration));
        }

        // 拉候选(所有聊过的人 ∪ 打开面板时同块人物)+ 名字,再建 UI。
        static IEnumerator LoadThenShow(int taiwuId, long requestEpoch, uint worldId, int worldGeneration)
        {
            if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) { FailClosed(requestEpoch); yield break; }
            _ids.Clear(); _names.Clear(); _sources.Clear(); _sel.Clear();
            yield return ChatWindow.EnsureConversationIndexesReady(
                taiwuId, worldGeneration, worldId);
            if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration))
            { FailClosed(requestEpoch); yield break; }
            if (!ChatWindow.ConversationIndexesReady(taiwuId))
            {
                Debug.LogWarning("[江湖有灵] 群聊选人等待聊天索引恢复失败，拒绝显示不完整候选");
                FailClosed(requestEpoch, "聊天记录索引尚未恢复，暂时无法列出完整候选");
                yield break;
            }
            List<TalkOrchestrator.ConversedPartner> conversed = null;
            List<GroupChatOrchestrator.GroupSession> groupSessions = null;
            bool indexesLoaded = false;
            for (int attempt = 0; attempt < 2 && !indexesLoaded; attempt++)
            {
                if (attempt > 0)
                    yield return ChatWindow.EnsureConversationIndexesReady(
                        taiwuId, worldGeneration, worldId);
                if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration))
                { FailClosed(requestEpoch); yield break; }
                if (!ChatWindow.ConversationIndexesReady(taiwuId)) continue;
                try
                {
                    bool singlesLoaded = TalkOrchestrator.TryLoadIndexedConversedPartners(
                        taiwuId, out conversed);
                    bool groupsLoaded = new GroupChatOrchestrator().TryLoadIndexedSessionSummaries(
                        taiwuId, out groupSessions);
                    indexesLoaded = singlesLoaded && groupsLoaded
                        && ChatWindow.ConversationIndexesReady(taiwuId);
                }
                catch
                {
                    indexesLoaded = false;
                }
                if (!indexesLoaded) yield return null;
            }
            if (!indexesLoaded)
            {
                Debug.LogWarning("[江湖有灵] 群聊选人读取聊天索引时索引已失效，拒绝显示不完整候选");
                FailClosed(requestEpoch, "聊天记录索引正在变化，请稍后重试");
                yield break;
            }

            bool blockDone = false;
            List<int> block = null;
            EffectHandler.QuerySameBlockChars(taiwuId, x =>
            {
                if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) return;
                block = x; blockDone = true;
            }, true);   // 手动挑人:在场的都列出(含玄灰魔道/仇敌),玩家自己决定拉谁进群
            float dl = Time.unscaledTime + 2.5f;
            while (!blockDone && Time.unscaledTime < dl
                && RequestIsCurrent(requestEpoch, worldId, worldGeneration)) yield return null;
            if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) { FailClosed(requestEpoch); yield break; }

            var indexById = new Dictionary<int, int>();
            var nameHints = new List<string>();
            if (conversed != null)
                foreach (TalkOrchestrator.ConversedPartner partner in conversed)
                {
                    int id = partner != null ? partner.NpcId : 0;
                    if (id <= 0 || id == taiwuId || _reservedMemberIds.Contains(id)
                        || indexById.ContainsKey(id)) continue;
                    indexById[id] = _ids.Count;
                    _ids.Add(id);
                    _sources.Add(1);
                    nameHints.Add(partner.Name);
                }
            // “聊过”也包括只在旧群聊里见过、尚未建立单聊文件的人。
            if (groupSessions != null)
                foreach (GroupChatOrchestrator.GroupSession session in groupSessions)
                {
                    if (session?.Members == null) continue;
                    foreach (GroupChatOrchestrator.Member member in session.Members)
                    {
                        int id = member != null ? member.Id : 0;
                        if (id <= 0 || id == taiwuId || _reservedMemberIds.Contains(id)
                            || indexById.ContainsKey(id)) continue;
                        indexById[id] = _ids.Count;
                        _ids.Add(id);
                        _sources.Add(1);
                        nameHints.Add(member.Name);
                    }
                }
            if (block != null)
                foreach (int id in block)
                {
                    if (id <= 0 || id == taiwuId || _reservedMemberIds.Contains(id)) continue;
                    if (indexById.TryGetValue(id, out int existing))
                    {
                        _sources[existing] |= 2;
                        continue;
                    }
                    indexById[id] = _ids.Count;
                    _ids.Add(id);
                    _sources.Add(2);
                    nameHints.Add(null);
                }
            if (_ids.Count == 0)
            {
                FailClosed(requestEpoch, "当前没有可加入的人：候选仅包含聊过的人和此刻同地块人物");
                yield break;
            }
            bool namesDone = false; List<string> nm = null;
            var candidateIds = new List<int>(_ids);
            EffectHandler.QueryCharNames(candidateIds, x =>
            {
                if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) return;
                nm = x; namesDone = true;
            });
            dl = Time.unscaledTime + 2.5f;
            while (!namesDone && Time.unscaledTime < dl
                && RequestIsCurrent(requestEpoch, worldId, worldGeneration)) yield return null;
            if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) { FailClosed(requestEpoch); yield break; }
            for (int i = 0; i < _ids.Count; i++)
            {
                string n = nm != null && i < nm.Count
                    && !TalkOrchestrator.IsUnresolvedNpcName(nm[i])
                    ? nm[i]
                    : i < nameHints.Count
                        && !TalkOrchestrator.IsUnresolvedNpcName(nameHints[i])
                        ? nameHints[i]
                        : "姓名暂不可用";
                _names.Add(n);
                _sel.Add(false);
            }
            Build(requestEpoch, worldId, worldGeneration);
        }

        static void Build(long requestEpoch, uint worldId, int worldGeneration)
        {
            if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) { FailClosed(requestEpoch); return; }
            DestroyRoot();
            _rowBg.Clear(); _rowLbl.Clear(); _visibleRowIndices.Clear();
            _page = 0;

            _root = new GameObject("JYL_GroupPicker", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30010;   // 必须高于聊天窗(30000),否则被它盖在底下=看不见(这就是"群聊没显示"的原因)
            var sc = _root.GetComponent<CanvasScaler>(); sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; sc.referenceResolution = new Vector2(1920, 1080);

            var dim = new GameObject("Dim", typeof(RectTransform), typeof(Image), typeof(Button));
            dim.transform.SetParent(_root.transform, false);
            var drt = dim.GetComponent<RectTransform>(); drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one; drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;
            dim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
            dim.GetComponent<Button>().onClick.AddListener(() => Close(requestEpoch));

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            panel.transform.SetParent(_root.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(440f, 560f); prt.anchoredPosition = Vector2.zero;   // 原宽即可容两列
            panel.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.10f, 0.99f);
            var pvl = panel.GetComponent<VerticalLayoutGroup>();
            pvl.padding = new RectOffset(16, 16, 14, 14); pvl.spacing = 10f;
            pvl.childForceExpandWidth = true; pvl.childForceExpandHeight = false; pvl.childControlWidth = true; pvl.childControlHeight = true;

            var title = NewText("Title", panel.transform, 22, TextAlignmentOptions.Center);
            title.text = "选择群聊成员（还可选 " + _selectionLimit + " 人）"; title.color = new Color(0.86f, 0.92f, 0.88f, 1f);
            title.gameObject.AddComponent<LayoutElement>().preferredHeight = 32f;

            if (_newMembersSeeHistory)
            {
                var historyNotice = NewText("HistoryNotice", panel.transform, 15, TextAlignmentOptions.Center);
                historyNotice.text = "加入当前群聊 · 新人会看到本群此前聊天记录";
                historyNotice.color = new Color(0.62f, 0.84f, 0.71f, 1f);
                historyNotice.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
            }

            var allGo = NewButton("All", panel.transform,
                "本页全选（总计最多 " + _selectionLimit + " 人）/ 本页全不选", 17, out var allBtn);
            allGo.AddComponent<LayoutElement>().preferredHeight = 38f;
            allBtn.onClick.AddListener(() => ToggleAll(requestEpoch, worldId, worldGeneration));

            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            scrollGo.transform.SetParent(panel.transform, false);
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.2f);
            var sle = scrollGo.AddComponent<LayoutElement>(); sle.flexibleHeight = 1f; sle.preferredHeight = 376f;
            var sr = scrollGo.GetComponent<ScrollRect>(); sr.horizontal = false; sr.vertical = true; sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 24f;
            _scroll = sr;
            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(GridLayoutGroup), typeof(ContentSizeFitter));
            _list = contentGo.GetComponent<RectTransform>();
            _list.SetParent(scrollGo.transform, false);
            _list.anchorMin = _list.anchorMax = _list.pivot = new Vector2(0.5f, 1f);   // 点锚顶部居中:宽度由网格自身决定,不再横向拉伸(拉伸+pivot0.5 会令内容左溢出被遮罩裁掉=左列被切)
            // 两列网格:面板内宽≈440-32=408,滚动区减网格内边距(8×2)后≈392,两格 + 一道 8 间距 → 每格 ≈ 192
            var grid = contentGo.GetComponent<GridLayoutGroup>();
            grid.padding = new RectOffset(8, 8, 8, 8); grid.spacing = new Vector2(8f, 8f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 2;
            grid.cellSize = new Vector2(186f, 42f); grid.childAlignment = TextAnchor.UpperLeft;   // 留足余量,免分辨率缩放挤掉第二列
            var csf = contentGo.GetComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;   // 宽度=网格实际宽,配合点锚居中,杜绝左溢出
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            sr.content = _list;
            RenderPage(requestEpoch, worldId, worldGeneration);

            if (_ids.Count > PageSize)
            {
                var pageRow = new GameObject("Pages", typeof(RectTransform), typeof(HorizontalLayoutGroup));
                pageRow.transform.SetParent(panel.transform, false);
                pageRow.AddComponent<LayoutElement>().preferredHeight = 36f;
                var layout = pageRow.GetComponent<HorizontalLayoutGroup>();
                layout.spacing = 8f;
                layout.childForceExpandWidth = true;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                NewButton("Previous", pageRow.transform, "上一页", 15, out _previousPageButton)
                    .AddComponent<LayoutElement>().preferredHeight = 34f;
                _pageLabel = NewText("Page", pageRow.transform, 15, TextAlignmentOptions.Center);
                _pageLabel.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;
                NewButton("Next", pageRow.transform, "下一页", 15, out _nextPageButton)
                    .AddComponent<LayoutElement>().preferredHeight = 34f;
                _previousPageButton.onClick.AddListener(() =>
                    ChangePage(-1, requestEpoch, worldId, worldGeneration));
                _nextPageButton.onClick.AddListener(() =>
                    ChangePage(1, requestEpoch, worldId, worldGeneration));
                RefreshPageControls();
            }

            var btnRow = new GameObject("Btns", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            btnRow.transform.SetParent(panel.transform, false);
            btnRow.AddComponent<LayoutElement>().preferredHeight = 44f;
            var brl = btnRow.GetComponent<HorizontalLayoutGroup>(); brl.spacing = 12f;
            brl.childForceExpandWidth = true; brl.childControlWidth = true; brl.childControlHeight = true;
            NewButton("Cancel", btnRow.transform, "取消", 18, out var cancelBtn); cancelBtn.onClick.AddListener(() => Close(requestEpoch));
            var okGo = NewButton("OK", btnRow.transform,
                _newMembersSeeHistory ? "加入当前群聊" : "开始群聊", 18, out var okBtn);
            okGo.GetComponent<Image>().color = new Color(0.30f, 0.50f, 0.42f, 0.98f);
            okBtn.onClick.AddListener(() => Confirm(requestEpoch, worldId, worldGeneration));

            if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) { FailClosed(requestEpoch); return; }
            try { PopupRegistry.Register(_root, () => Close(requestEpoch)); } catch { }
            Canvas.ForceUpdateCanvases();
        }

        static void RenderPage(long requestEpoch, uint worldId, int worldGeneration)
        {
            if (_list == null || !RequestIsCurrent(requestEpoch, worldId, worldGeneration)) return;
            for (int i = _list.childCount - 1; i >= 0; i--)
            {
                GameObject child = _list.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
            _visibleRowIndices.Clear();
            _rowBg.Clear();
            _rowLbl.Clear();
            int pageCount = Math.Max(1, (_ids.Count + PageSize - 1) / PageSize);
            _page = Mathf.Clamp(_page, 0, pageCount - 1);
            int first = _page * PageSize;
            int last = Math.Min(_ids.Count, first + PageSize);
            for (int i = first; i < last; i++) AddRow(i, requestEpoch, worldId, worldGeneration);
            if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;
            RefreshPageControls();
        }

        static void ChangePage(int delta, long requestEpoch, uint worldId, int worldGeneration)
        {
            if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) { FailClosed(requestEpoch); return; }
            int pageCount = Math.Max(1, (_ids.Count + PageSize - 1) / PageSize);
            int next = Mathf.Clamp(_page + delta, 0, pageCount - 1);
            if (next == _page) return;
            _page = next;
            RenderPage(requestEpoch, worldId, worldGeneration);
        }

        static void RefreshPageControls()
        {
            int pageCount = Math.Max(1, (_ids.Count + PageSize - 1) / PageSize);
            if (_pageLabel != null) _pageLabel.text = (_page + 1) + " / " + pageCount;
            if (_previousPageButton != null) _previousPageButton.interactable = _page > 0;
            if (_nextPageButton != null) _nextPageButton.interactable = _page + 1 < pageCount;
        }

        static void AddRow(int i, long requestEpoch, uint worldId, int worldGeneration)
        {
            var row = new GameObject("Row", typeof(RectTransform), typeof(Image), typeof(Button));
            row.transform.SetParent(_list, false);
            var bg = row.GetComponent<Image>(); bg.color = _sel[i] ? SelCol : UnselCol;
            row.AddComponent<LayoutElement>().preferredHeight = 40f;
            var lbl = NewText("L", row.transform, 16, TextAlignmentOptions.Left);
            lbl.raycastTarget = false; lbl.enableWordWrapping = false; lbl.overflowMode = TextOverflowModes.Ellipsis;   // 不折行,过长省略,避免首字被切
            var lrt = lbl.rectTransform; lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = new Vector2(10f, 0f); lrt.offsetMax = new Vector2(-6f, 0f);
            int idx = i;
            row.GetComponent<Button>().onClick.AddListener(delegate
            {
                if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) { FailClosed(requestEpoch); return; }
                if (!_sel[idx] && SelectedCount() >= _selectionLimit) return;
                _sel[idx] = !_sel[idx];
                Refresh(idx);
            });
            _visibleRowIndices.Add(i);
            _rowBg.Add(bg); _rowLbl.Add(lbl);
            Refresh(i);
        }

        static void Refresh(int i)
        {
            int visible = _visibleRowIndices.IndexOf(i);
            if (visible < 0 || visible >= _rowBg.Count || i < 0 || i >= _sel.Count) return;
            _rowBg[visible].color = _sel[i] ? SelCol : UnselCol;
            string source = _sources[i] == 3 ? "聊过 · 在场"
                : _sources[i] == 1 ? "聊过" : "在场";
            _rowLbl[visible].text = _names[i]
                + " <size=75%><color=#7FB89A>" + source + "</color></size>";
        }

        static void ToggleAll(long requestEpoch, uint worldId, int worldGeneration)
        {
            if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) { FailClosed(requestEpoch); return; }
            PagedSelectionPolicy.ToggleVisible(_sel, _visibleRowIndices, _selectionLimit);
            foreach (int index in _visibleRowIndices) Refresh(index);
        }

        static void Confirm(long requestEpoch, uint worldId, int worldGeneration)
        {
            if (!RequestIsCurrent(requestEpoch, worldId, worldGeneration)) { FailClosed(requestEpoch); return; }
            if (_confirming) return;
            _confirming = true;
            var picked = new List<int>();
            for (int i = 0; i < _ids.Count && picked.Count < _selectionLimit; i++) if (_sel[i]) picked.Add(_ids[i]);
            var cb = _onConfirm; _onConfirm = null;
            bool sameWorld = WorldStillCurrent(worldId, worldGeneration);
            CancelCurrent(false);
            InvokeCallback(cb, sameWorld ? picked : null);
        }

        static int SelectedCount()
        {
            int count = 0;
            for (int i = 0; i < _sel.Count; i++) if (_sel[i]) count++;
            return count;
        }

        static void Close(long requestEpoch)
        {
            if (requestEpoch != _requestEpoch) return;
            CancelCurrent(true);
        }

        /// <summary>离开/切换存档时取消静态请求并销毁 DontDestroyOnLoad UI；晚到回调由 epoch + 世界身份双重拒绝。</summary>
        public static void CancelForWorldExit()
        {
            CancelCurrent(true);
        }

        static bool WorldStillCurrent(uint worldId, int worldGeneration)
        {
            return worldId > 0 && WorldLifecycle.IsSameWorld(worldGeneration) && WorldLifecycle.WorldId == worldId;
        }

        static bool RequestIsCurrent(long requestEpoch, uint worldId, int worldGeneration)
        {
            return requestEpoch == _requestEpoch
                && _worldId == worldId && _worldGeneration == worldGeneration
                && WorldStillCurrent(worldId, worldGeneration);
        }

        static void FailClosed(long requestEpoch, string reason = null)
        {
            if (requestEpoch != _requestEpoch) return;
            Action<string> failure = _onFailure;
            CancelCurrent(true);
            if (!string.IsNullOrWhiteSpace(reason)) InvokeFailure(failure, reason);
        }

        static void CancelCurrent(bool notify)
        {
            var cb = notify ? _onConfirm : null;
            _onConfirm = null;
            _onFailure = null;
            unchecked { _requestEpoch++; }
            _worldId = 0;
            _worldGeneration = -1;
            _taiwuId = 0;
            _reservedMemberIds.Clear();
            _selectionLimit = MaxMembers;
            _confirming = false;
            DestroyRoot();
            _list = null;
            _scroll = null;
            _page = 0;
            _pageLabel = null;
            _previousPageButton = null;
            _nextPageButton = null;
            _ids.Clear(); _names.Clear(); _sources.Clear(); _sel.Clear();
            _visibleRowIndices.Clear(); _rowBg.Clear(); _rowLbl.Clear();
            InvokeCallback(cb, null);
        }

        static void DestroyRoot()
        {
            var root = _root;
            _root = null;
            if (root == null) return;
            try { PopupRegistry.Unregister(root); } catch { }
            try { UnityEngine.Object.Destroy(root); } catch { }
        }

        static void InvokeCallback(Action<List<int>> callback, List<int> value)
        {
            try { callback?.Invoke(value); }
            catch (Exception e) { Debug.LogWarning("[江湖有灵] 群聊选人回调已隔离:" + e.GetType().Name); }
        }

        static void InvokeFailure(Action<string> callback, string reason)
        {
            try { callback?.Invoke(reason); }
            catch (Exception e) { Debug.LogWarning("[江湖有灵] 群聊选人失败回调已隔离:" + e.GetType().Name); }
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            UiFontSizeStore.Bind(t, size); t.alignment = align; t.richText = true; t.extraPadding = true;
            t.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            return t;
        }

        static GameObject NewButton(string name, Transform parent, string label, float size, out Button btn)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.10f);
            btn = go.GetComponent<Button>();
            var lbl = NewText("L", go.transform, size, TextAlignmentOptions.Center);
            lbl.text = label; lbl.raycastTarget = false;
            var lrt = lbl.rectTransform; lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            return go;
        }

        static TMP_FontAsset ResolveFont()
        {
            try { foreach (var t in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()) if (t != null && t.font != null && t.gameObject.scene.IsValid()) return t.font; } catch { }
            return null;
        }
    }
}
