using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>Lists hidden navigation entries without opening, deleting or rewriting their conversations.</summary>
    internal static class ConversationArchiveWindow
    {
        const int PageSize = 24;
        static GameObject _root;
        static RectTransform _list;
        static ScrollRect _scroll;
        static TMP_FontAsset _font;
        static TextMeshProUGUI _status, _pageLabel;
        static Button _close, _previous, _next;
        static GameObject _previousFocus;
        static readonly List<ChatWindow.SessionNavEntry> _entries = new List<ChatWindow.SessionNavEntry>();
        static readonly List<Button> _restoreButtons = new List<Button>();
        static Func<ChatWindow.SessionNavEntry, bool> _restore;
        static int _taiwuId, _generation, _page;
        static uint _worldId;
        static long _session, _renderRevision;
        static string _loadMessage;

        internal static bool IsOpen => _root != null && _root.activeSelf;

        internal static bool IsCurrent(int taiwuId)
            => IsOpen && _taiwuId == taiwuId && _worldId != 0
                && _worldId == WorldLifecycle.WorldId
                && _worldId == JianghuYoulingPaths.CurrentWorldId
                && WorldLifecycle.IsSameWorld(_generation);

        internal static void Show(int taiwuId, TMP_FontAsset font,
            Func<ChatWindow.SessionNavEntry, bool> restore)
        {
            Close(false);
            if (taiwuId <= 0 || WorldLifecycle.WorldId == 0 || restore == null) return;
            _taiwuId = taiwuId;
            _worldId = WorldLifecycle.WorldId;
            _generation = WorldLifecycle.Generation;
            _font = font;
            _restore = restore;
            _previousFocus = EventSystem.current != null
                ? EventSystem.current.currentSelectedGameObject : null;
            _page = 0;
            _loadMessage = "正在读取归档会话…";
            Build();
            RenderPage();
        }

        internal static void SetEntries(int taiwuId, IList<ChatWindow.SessionNavEntry> entries,
            string loadMessage)
        {
            if (!IsCurrent(taiwuId)) return;
            bool changed = !string.Equals(_loadMessage, loadMessage, StringComparison.Ordinal)
                || _entries.Count != (entries?.Count ?? 0);
            if (!changed && entries != null)
                for (int i = 0; i < entries.Count; i++)
                {
                    var before = _entries[i];
                    var after = entries[i];
                    if (before.Identity != after.Identity || before.Title != after.Title
                        || before.Subtitle != after.Subtitle || before.IsDead != after.IsDead)
                    { changed = true; break; }
                }
            if (!changed) return;
            _entries.Clear();
            if (entries != null) _entries.AddRange(entries);
            _loadMessage = loadMessage;
            RenderPage();
        }

        internal static void Close(bool restoreFocus = true)
        {
            unchecked { _session++; _renderRevision++; }
            GameObject root = _root;
            GameObject focus = _previousFocus;
            _root = null;
            _previousFocus = null;
            _restore = null;
            _entries.Clear();
            _restoreButtons.Clear();
            _list = null;
            _scroll = null;
            _status = _pageLabel = null;
            _close = _previous = _next = null;
            if (root != null)
            {
                root.SetActive(false);
                PopupRegistry.Unregister(root);
                UnityEngine.Object.Destroy(root);
            }
            if (restoreFocus && focus != null && focus.activeInHierarchy && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(focus);
        }

        static void Build()
        {
            _root = new GameObject("JHYL_ConversationArchive", typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30060;
            CanvasScaler scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            PopupRegistry.Register(_root, () => Close());

            GameObject dim = Image("Dim", _root.transform, new Color(0, 0, 0, 0.65f));
            Anchor(dim.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            GameObject panel = Image("Panel", _root.transform, new Color(0.10f, 0.11f, 0.10f, 0.99f));
            RectTransform panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = panelRt.anchorMax = panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(760f, 740f);

            GameObject header = Image("Header", panel.transform, new Color(0.14f, 0.16f, 0.15f, 1f));
            Anchor(header.GetComponent<RectTransform>(), new Vector2(0, 1), Vector2.one,
                new Vector2(0, -64), Vector2.zero);
            TextMeshProUGUI title = Text("Title", "归档会话", header.transform, 25f);
            Anchor(title.rectTransform, Vector2.zero, Vector2.one, new Vector2(20, 0), new Vector2(-110, 0));
            _close = Button("Close", "关闭", header.transform, () => Close());
            Anchor(_close.GetComponent<RectTransform>(), new Vector2(1, 0), Vector2.one,
                new Vector2(-98, 12), new Vector2(-14, -12));

            _status = Text("Status", _loadMessage, panel.transform, 17f);
            _status.enableWordWrapping = true;
            Anchor(_status.rectTransform, new Vector2(0, 1), Vector2.one,
                new Vector2(20, -140), new Vector2(-20, -74));

            GameObject scrollGo = Image("Scroll", panel.transform, new Color(0.035f, 0.04f, 0.038f, 0.35f));
            Anchor(scrollGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(16, 74), new Vector2(-16, -148));
            _scroll = scrollGo.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 32f;
            GameObject viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(scrollGo.transform, false);
            _scroll.viewport = viewport.GetComponent<RectTransform>();
            Anchor(_scroll.viewport, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-14, 0));
            GameObject list = new GameObject("Entries", typeof(RectTransform),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            list.transform.SetParent(viewport.transform, false);
            _list = list.GetComponent<RectTransform>();
            _list.anchorMin = new Vector2(0, 1); _list.anchorMax = Vector2.one;
            _list.pivot = new Vector2(0.5f, 1);
            _list.sizeDelta = Vector2.zero;
            VerticalLayoutGroup layout = list.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            list.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll.content = _list;
            _scroll.verticalScrollbar = UiScroll.AddVerticalForScrollRect(scrollGo);
            _scroll.verticalScrollbar.navigation = new Navigation
            {
                mode = Navigation.Mode.Explicit,
                selectOnLeft = _close, selectOnRight = _close,
            };

            _previous = Button("PreviousPage", "上一页", panel.transform, () => ChangePage(-1));
            Anchor(_previous.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero,
                new Vector2(20, 18), new Vector2(128, 58));
            _next = Button("NextPage", "下一页", panel.transform, () => ChangePage(1));
            Anchor(_next.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-128, 18), new Vector2(-20, 58));
            _pageLabel = Text("Page", "", panel.transform, 17f);
            _pageLabel.alignment = TextAlignmentOptions.Center;
            Anchor(_pageLabel.rectTransform, Vector2.zero, new Vector2(1, 0),
                new Vector2(140, 18), new Vector2(-140, 58));
        }

        static void ChangePage(int delta)
        {
            if (!IsCurrent(_taiwuId)) { Close(false); return; }
            _page += delta;
            RenderPage();
        }

        static void RenderPage()
        {
            if (!IsCurrent(_taiwuId) || _list == null) return;
            long revision = unchecked(++_renderRevision);
            long session = _session;
            _restoreButtons.Clear();
            for (int i = _list.childCount - 1; i >= 0; i--)
            {
                GameObject row = _list.GetChild(i).gameObject;
                row.SetActive(false);
                UnityEngine.Object.Destroy(row);
            }
            int pages = Math.Max(1, (_entries.Count + PageSize - 1) / PageSize);
            _page = Math.Max(0, Math.Min(_page, pages - 1));
            int end = Math.Min(_entries.Count, (_page + 1) * PageSize);
            for (int i = _page * PageSize; i < end; i++)
            {
                ChatWindow.SessionNavEntry entry = _entries[i];
                GameObject row = Image("ArchivedConversation", _list, new Color(0.16f, 0.19f, 0.17f, 1f));
                LayoutElement height = row.AddComponent<LayoutElement>();
                height.minHeight = height.preferredHeight = 82f;
                TextMeshProUGUI name = Text("Name", (entry.IsDead ? "已故 · " : "") + entry.Title,
                    row.transform, 20f);
                Anchor(name.rectTransform, new Vector2(0, 0.45f), Vector2.one,
                    new Vector2(14, 0), new Vector2(-118, -4));
                TextMeshProUGUI detail = Text("Detail", entry.Subtitle, row.transform, 15f);
                detail.color = new Color(0.67f, 0.73f, 0.67f, 1f);
                Anchor(detail.rectTransform, Vector2.zero, new Vector2(1, 0.45f),
                    new Vector2(14, 4), new Vector2(-118, 0));
                Button restore = Button("Restore", "还原", row.transform,
                    () => Restore(entry, session, revision));
                Anchor(restore.GetComponent<RectTransform>(), new Vector2(1, 0), Vector2.one,
                    new Vector2(-100, 20), new Vector2(-14, -20));
                var focus = restore.gameObject.AddComponent<ConversationArchiveRowFocus>();
                focus.scroll = _scroll;
                focus.row = row.GetComponent<RectTransform>();
                _restoreButtons.Add(restore);
            }
            _status.text = !string.IsNullOrEmpty(_loadMessage) ? _loadMessage
                : _entries.Count == 0 ? "暂无归档会话。隐藏只收起入口，不会删除聊天记录。"
                : "共 " + _entries.Count + " 个隐藏会话。点击还原即可移出归档、回到会话列表。";
            _pageLabel.text = (_page + 1) + " / " + pages;
            _previous.interactable = _page > 0;
            _next.interactable = _page + 1 < pages;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_list);
            _scroll.verticalNormalizedPosition = 1f;
            var controls = new List<Button> { _close };
            controls.AddRange(_restoreButtons);
            if (_previous.interactable) controls.Add(_previous);
            if (_next.interactable) controls.Add(_next);
            for (int i = 0; i < controls.Count; i++)
            {
                Button before = controls[(i + controls.Count - 1) % controls.Count];
                Button after = controls[(i + 1) % controls.Count];
                controls[i].navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = before, selectOnLeft = before,
                    selectOnDown = after, selectOnRight = after,
                };
            }
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(_close.gameObject);
        }

        static void Restore(ChatWindow.SessionNavEntry entry, long session, long revision)
        {
            if (!IsCurrent(entry.TaiwuId) || session != _session || revision != _renderRevision) return;
            bool saved = false;
            try { saved = _restore != null && _restore(entry); }
            catch { }
            if (!IsCurrent(entry.TaiwuId) || session != _session) return;
            if (!saved)
            {
                _status.text = "还原未能保存，请稍后重试；该会话仍在归档中，聊天记录没有变化。";
                return;
            }
            Close();
        }

        static GameObject Image(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go;
        }

        static TextMeshProUGUI Text(string name, string text, Transform parent, float size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) label.font = _font;
            UiFontSizeStore.Bind(label, size);
            label.color = new Color(0.94f, 0.92f, 0.84f, 1f);
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.richText = false;
            label.raycastTarget = false;
            label.text = GlyphSanitizer.Clean(text ?? "");
            return label;
        }

        static Button Button(string name, string text, Transform parent, Action onClick)
        {
            GameObject go = Image(name, parent, new Color(0.24f, 0.33f, 0.28f, 1f));
            Button button = go.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1.3f, 1.3f, 1.2f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;
            button.onClick.AddListener(() => onClick());
            TextMeshProUGUI label = Text("Label", text, go.transform, 17f);
            label.alignment = TextAlignmentOptions.Center;
            Anchor(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return button;
        }

        static void Anchor(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = min; rect.anchorMax = max;
            rect.offsetMin = offsetMin; rect.offsetMax = offsetMax;
        }
    }

    internal sealed class ConversationArchiveRowFocus : MonoBehaviour, ISelectHandler
    {
        internal ScrollRect scroll;
        internal RectTransform row;

        public void OnSelect(BaseEventData eventData)
        {
            if (scroll == null || scroll.content == null || scroll.viewport == null || row == null) return;
            Canvas.ForceUpdateCanvases();
            float extent = Mathf.Max(0f, scroll.content.rect.height - scroll.viewport.rect.height);
            if (extent < 1f) return;
            float top = -row.anchoredPosition.y - (1f - row.pivot.y) * row.rect.height;
            float position = scroll.content.anchoredPosition.y;
            float next = top < position ? top
                : top + row.rect.height > position + scroll.viewport.rect.height
                    ? top + row.rect.height - scroll.viewport.rect.height : position;
            scroll.verticalNormalizedPosition = 1f - Mathf.Clamp01(next / extent);
        }
    }
}
