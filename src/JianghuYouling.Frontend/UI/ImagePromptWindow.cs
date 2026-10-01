using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using JianghuYouling.Core.Web;

namespace JianghuYouling
{
    /// <summary>生图发送前的本地提示词确认窗。只有玩家确认后的文本才会交给生图服务。</summary>
    internal static class ImagePromptWindow
    {
        static readonly Color Panel = new Color(0.095f, 0.105f, 0.098f, 0.99f);
        static readonly Color Ink = new Color(0.045f, 0.052f, 0.048f, 1f);
        static readonly Color ButtonColor = new Color(0.22f, 0.34f, 0.31f, 1f);
        static readonly Color TextColor = new Color(0.91f, 0.89f, 0.82f, 1f);
        static readonly Color Muted = new Color(0.62f, 0.68f, 0.62f, 1f);

        static GameObject _root;
        static TMP_InputField _input;
        static TextMeshProUGUI _status;
        static TMP_FontAsset _font;
        static object _owner;
        static Action<string> _confirm;
        static Action _cancel;
        static bool _closing;

        internal static void Show(object owner, string prompt, TMP_FontAsset font,
            Action<string> onConfirm, Action onCancel)
        {
            Close(true);
            _owner = owner;
            _confirm = onConfirm;
            _cancel = onCancel;
            _font = font ?? ResolveFont();

            _root = new GameObject("JYL_ImagePrompt", typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30060;
            CanvasScaler scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            GameObject dim = NewImage("Dim", _root.transform, new Color(0f, 0f, 0f, 0.62f));
            Stretch(dim.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero);

            GameObject panel = NewImage("Panel", dim.transform, Panel);
            RectTransform panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(1120f, 760f);
            panelRt.anchoredPosition = Vector2.zero;

            TextMeshProUGUI title = NewText("Title", panel.transform, 28f,
                TextAlignmentOptions.Left, TextColor);
            title.text = "确认生图提示词";
            Anchor(title.rectTransform, new Vector2(0f, 1f), Vector2.one,
                new Vector2(30f, -74f), new Vector2(-330f, -18f));

            Button confirmButton = NewButton("Confirm", panel.transform, "开始生图", 20f);
            Anchor(confirmButton.GetComponent<RectTransform>(), new Vector2(1f, 1f), Vector2.one,
                new Vector2(-300f, -70f), new Vector2(-112f, -20f));
            confirmButton.onClick.AddListener(Confirm);

            Button closeButton = NewButton("Close", panel.transform, "关闭", 20f);
            Anchor(closeButton.GetComponent<RectTransform>(), new Vector2(1f, 1f), Vector2.one,
                new Vector2(-100f, -70f), new Vector2(-20f, -20f));
            closeButton.onClick.AddListener(() => Close(true));

            TextMeshProUGUI help = NewText("Help", panel.transform, 16f,
                TextAlignmentOptions.Left, Muted);
            help.text = "以下内容将作为提示词发送给生图服务。请检查并自行修改；关闭窗口不会发送。";
            Anchor(help.rectTransform, new Vector2(0f, 1f), Vector2.one,
                new Vector2(32f, -116f), new Vector2(-32f, -82f));

            GameObject inputGo = NewImage("PromptInput", panel.transform, Ink);
            Anchor(inputGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(30f, 70f), new Vector2(-30f, -128f));
            _input = UiInput.Setup(inputGo, _font, 17f, true,
                TMP_InputField.LineType.MultiLineNewline, false,
                "请输入生图提示词", ImageGenerationClient.MaxPromptChars + 1, true, out _);
            _input.text = prompt ?? string.Empty;
            _input.onValueChanged.AddListener(_ => RefreshStatus());
            _input.verticalScrollbar = UiScroll.AddVertical(inputGo);

            _status = NewText("Status", panel.transform, 15f,
                TextAlignmentOptions.Left, Muted);
            Anchor(_status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(32f, 22f), new Vector2(-32f, 58f));
            RefreshStatus();
            PopupRegistry.Register(_root, () => Close(true));
            _input.ActivateInputField();
            _input.MoveTextEnd(false);
        }

        internal static void CloseForOwner(object owner)
        {
            if (_root != null && ReferenceEquals(_owner, owner)) Close(true);
        }

        static void Confirm()
        {
            string value = (_input?.text ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > ImageGenerationClient.MaxPromptChars)
            {
                RefreshStatus(true);
                return;
            }
            Action<string> callback = _confirm;
            Close(false);
            try { callback?.Invoke(value); } catch (Exception ex)
            { Debug.LogWarning("[JHYL_IMAGE_PROMPT] confirm callback failed: " + ex.GetType().Name); }
        }

        static void RefreshStatus(bool invalid = false)
        {
            if (_status == null) return;
            int length = (_input?.text ?? string.Empty).Length;
            bool bad = invalid || length == 0 || length > ImageGenerationClient.MaxPromptChars;
            _status.color = bad ? new Color(1f, 0.52f, 0.40f, 1f) : Muted;
            _status.text = length + " / " + ImageGenerationClient.MaxPromptChars
                + (bad ? "　提示词不能为空或超过上限" : "　确认后才会发送");
        }

        static void Close(bool notifyCancel)
        {
            if (_closing) return;
            _closing = true;
            Action cancel = notifyCancel ? _cancel : null;
            GameObject root = _root;
            _root = null;
            _input = null;
            _status = null;
            _owner = null;
            _confirm = null;
            _cancel = null;
            if (root != null)
            {
                PopupRegistry.Unregister(root);
                UnityEngine.Object.Destroy(root);
            }
            _closing = false;
            if (cancel != null)
            {
                try { cancel(); } catch (Exception ex)
                { Debug.LogWarning("[JHYL_IMAGE_PROMPT] cancel callback failed: " + ex.GetType().Name); }
            }
        }

        static GameObject NewImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go;
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size,
            TextAlignmentOptions alignment, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = color;
            text.enableWordWrapping = true;
            text.raycastTarget = false;
            return text;
        }

        static Button NewButton(string name, Transform parent, string label, float size)
        {
            GameObject go = NewImage(name, parent, ButtonColor);
            Button button = go.AddComponent<Button>();
            TextMeshProUGUI text = NewText("Label", go.transform, size,
                TextAlignmentOptions.Center, TextColor);
            text.text = label;
            Stretch(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return button;
        }

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        static void Stretch(RectTransform rt, Vector2 min, Vector2 max,
            Vector2 offsetMin, Vector2 offsetMax) => Anchor(rt, min, max, offsetMin, offsetMax);

        static TMP_FontAsset ResolveFont()
        {
            TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            return fonts != null && fonts.Length > 0 ? fonts[0] : null;
        }
    }
}
