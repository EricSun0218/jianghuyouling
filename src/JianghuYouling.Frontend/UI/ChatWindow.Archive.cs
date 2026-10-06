using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    public static partial class ChatWindow
    {
        static GameObject _emptyConversationRoot;
        static RectTransform _emptyConversationPanel;
        static bool EmptyConversationVisible
            => _emptyConversationRoot != null && _emptyConversationRoot.activeSelf;

        static void OpenConversationArchive()
        {
            int taiwuId = ResolveCurrentTaiwuId();
            if (taiwuId <= 0 || WorldLifecycle.WorldId == 0) return;
            ConversationArchiveWindow.Show(taiwuId, _font, RestoreArchivedConversation);
            RefreshTabs();
        }

        static void RefreshArchiveContents(int taiwuId, List<SessionNavEntry> entries)
        {
            if (!ConversationArchiveWindow.IsCurrent(taiwuId)) return;
            var archived = new List<SessionNavEntry>();
            string error = null;
            try
            {
                var snapshot = ConversationNavigationStore.Load(taiwuId);
                if (snapshot?.LoadReliable != true)
                    error = "归档状态读取失败，暂不能还原；聊天记录没有变化，请稍后重新打开。";
                else if (!ConversationIndexesReady(taiwuId)
                    || _navigationGeneration != WorldLifecycle.Generation || _navigationTaiwuId != taiwuId)
                    error = "会话索引正在恢复，完成后将显示全部归档；请稍候，或稍后重新打开。";
                else
                {
                    var hidden = new HashSet<string>(snapshot.Hidden, System.StringComparer.Ordinal);
                    foreach (SessionNavEntry entry in entries)
                        if (entry != null && entry.TaiwuId == taiwuId && hidden.Contains(entry.Identity))
                            archived.Add(entry);
                }
            }
            catch { error = "归档读取失败，请稍后重试；聊天记录没有变化。"; }
            ConversationArchiveWindow.SetEntries(taiwuId, archived, error);
        }

        static bool RestoreArchivedConversation(SessionNavEntry entry)
        {
            if (entry == null || entry.TaiwuId != ResolveCurrentTaiwuId()
                || !ConversationArchiveWindow.IsCurrent(entry.TaiwuId)) return false;
            // Restore must commit before either list changes. Never open/mark-read the conversation here.
            if (!ConversationNavigationStore.Restore(entry.TaiwuId, entry.Identity)) return false;
            RefreshTabs();
            return true;
        }

        static Button NavigationButton(string name, string label, Transform parent, System.Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.24f, 0.33f, 0.28f, 1f);
            var button = go.GetComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(1.3f, 1.3f, 1.2f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;
            button.onClick.AddListener(() => onClick());
            var text = ChipText("Label", label, go.transform,
                new Color(0.94f, 0.92f, 0.84f, 1f), 17f, TextAlignmentOptions.Center);
            text.raycastTarget = false;
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
            return button;
        }

        static void SetEmptyConversationVisible(bool visible)
        {
            if (visible && _emptyConversationRoot == null) BuildEmptyConversation();
            if (_emptyConversationRoot == null) return;
            if (visible) _emptyConversationPanel.anchoredPosition = _sharedPanelPosition;
            _emptyConversationRoot.SetActive(visible);
        }

        static void BuildEmptyConversation()
        {
            _emptyConversationRoot = new GameObject("JHYL_EmptyConversation", typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_emptyConversationRoot);
            var canvas = _emptyConversationRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = _emptyConversationRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            PopupRegistry.Register(_emptyConversationRoot, Minimize);
            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_emptyConversationRoot.transform, false);
            panel.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.10f, 0.97f);
            _emptyConversationPanel = panel.GetComponent<RectTransform>();
            _emptyConversationPanel.anchorMin = _emptyConversationPanel.anchorMax =
                _emptyConversationPanel.pivot = new Vector2(0.5f, 0.5f);
            _emptyConversationPanel.sizeDelta = new Vector2(ChatTab.PanelWidth, ChatTab.PanelHeight);
            var header = new GameObject("Header", typeof(RectTransform), typeof(Image));
            header.transform.SetParent(panel.transform, false);
            header.GetComponent<Image>().color = new Color(0.14f, 0.16f, 0.15f, 0.98f);
            var headerRect = header.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1); headerRect.anchorMax = Vector2.one;
            headerRect.offsetMin = new Vector2(0, -52); headerRect.offsetMax = Vector2.zero;
            var drag = header.AddComponent<DragMove>();
            drag.target = _emptyConversationPanel;
            drag.onPositionChanged = SetSharedPanelPosition;
            var title = ChipText("Title", "会话", header.transform,
                new Color(0.95f, 0.92f, 0.82f, 1f), 24f, TextAlignmentOptions.Left);
            title.raycastTarget = false;
            title.rectTransform.anchorMin = Vector2.zero;
            title.rectTransform.anchorMax = Vector2.one;
            title.rectTransform.offsetMin = new Vector2(16, 0);
            title.rectTransform.offsetMax = new Vector2(-108, 0);
            var close = NavigationButton("Close", "关闭", header.transform, Minimize);
            var closeRect = close.GetComponent<RectTransform>();
            closeRect.anchorMin = closeRect.anchorMax = closeRect.pivot = new Vector2(1, 0.5f);
            closeRect.anchoredPosition = new Vector2(-10, 0);
            closeRect.sizeDelta = new Vector2(84, 36);
            var hint = ChipText("Hint", "从左侧选择会话，或点击“归档”还原隐藏的对话。",
                panel.transform, new Color(0.72f, 0.76f, 0.70f, 1f), 23f, TextAlignmentOptions.Center);
            hint.raycastTarget = false;
            hint.enableWordWrapping = true;
            hint.rectTransform.anchorMin = new Vector2(0.1f, 0.35f);
            hint.rectTransform.anchorMax = new Vector2(0.9f, 0.65f);
            hint.rectTransform.offsetMin = hint.rectTransform.offsetMax = Vector2.zero;
        }

        static void ResetConversationArchive()
        {
            ConversationArchiveWindow.Close(false);
            if (_emptyConversationRoot != null)
            {
                _emptyConversationRoot.SetActive(false);
                PopupRegistry.Unregister(_emptyConversationRoot);
                Object.Destroy(_emptyConversationRoot);
            }
            _emptyConversationRoot = null;
            _emptyConversationPanel = null;
        }
    }
}
