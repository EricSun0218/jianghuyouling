using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>
    /// 旧版数据迁移的玩家可见提示通道。迁移代码(可能在任意线程)只调用 Post 排队;
    /// LegacyDataNoticeHost 在主线程于世界身份就绪后弹出一个可关闭的小面板。
    /// 每个 key 每次游戏进程最多提示一次,避免重试循环刷屏;所有提示同时落 Player.log。
    /// </summary>
    public static class LegacyDataNotice
    {
        static readonly object Gate = new object();
        static readonly HashSet<string> PostedKeys = new HashSet<string>(StringComparer.Ordinal);
        static readonly List<string> Pending = new List<string>();
        const int MaxMessageChars = 700;

        /// <summary>线程安全;同一 key 本进程只入队一次。</summary>
        public static void Post(string key, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string trimmed = message.Trim();
            if (trimmed.Length > MaxMessageChars) trimmed = trimmed.Substring(0, MaxMessageChars) + "…";
            lock (Gate)
            {
                if (!string.IsNullOrEmpty(key) && !PostedKeys.Add(key)) return;
                Pending.Add(trimmed);
            }
            try { Debug.LogWarning("[江湖有灵] 旧数据提示: " + trimmed.Replace('\n', ' ')); } catch { }
        }

        internal static List<string> DrainPending()
        {
            lock (Gate)
            {
                if (Pending.Count == 0) return null;
                var drained = new List<string>(Pending);
                Pending.Clear();
                return drained;
            }
        }
    }

    /// <summary>提示宿主:DontDestroyOnLoad,每秒检查一次队列;进档后才建 UI(主线程)。</summary>
    public sealed class LegacyDataNoticeHost : MonoBehaviour
    {
        static LegacyDataNoticeHost _instance;
        static GameObject _root;
        static TextMeshProUGUI _body;
        static TMP_FontAsset _font;
        float _next;

        public static void Initialize()
        {
            if (_instance != null) return;
            var go = new GameObject("JHYL_LegacyDataNoticeHost");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<LegacyDataNoticeHost>();
        }

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (!WorldLifecycle.HasWorldIdentity) return;
            var messages = LegacyDataNotice.DrainPending();
            if (messages == null) return;
            try { Show(string.Join("\n\n", messages.ToArray())); }
            catch (Exception ex) { Debug.LogWarning("[江湖有灵] 旧数据提示面板创建失败: " + ex.GetType().Name); }
        }

        static void Show(string text)
        {
            if (_root == null) Build();
            if (_root == null || _body == null) return;
            _body.text = _root.activeSelf && !string.IsNullOrEmpty(_body.text)
                ? _body.text + "\n\n" + text
                : text;
            _root.SetActive(true);
        }

        static void Hide() { if (_root != null) { _root.SetActive(false); if (_body != null) _body.text = ""; } }

        static void Build()
        {
            _font = ResolveFontBeforeTmp();
            _root = new GameObject("JHYL_LegacyNoticeCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30040;   // 高于聊天/设置弹窗,提示不该被压在其它自建窗后面
            PopupRegistry.Register(_root, Hide);
            var sc = _root.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            prt.sizeDelta = new Vector2(640, 360);
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.10f, 0.97f);

            var title = NewText("Title", panel.transform, 22, TextAlignmentOptions.Center);
            Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -8), new Vector2(-16, -44));
            title.text = "江湖有灵 · 旧版数据提示";

            _body = NewText("Body", panel.transform, 17, TextAlignmentOptions.TopLeft);
            Anchor(_body.rectTransform, new Vector2(0, 0), new Vector2(1, 1), new Vector2(20, 64), new Vector2(-20, -52));
            _body.enableWordWrapping = true;

            var okGo = NewButton("Ok", panel.transform, "知道了", 19, out var okBtn);
            Anchor(okGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-70, 12), new Vector2(70, 50));
            okBtn.onClick.AddListener(Hide);
        }

        // 同 ChatHistoryEntryHost:TextMeshProUGUI.Awake 在 AddComponent 时同步执行,
        // 必须先把游戏已加载的中文字体设成 TMP 默认字体,否则中文成方块。
        static TMP_FontAsset ResolveFontBeforeTmp()
        {
            TMP_FontAsset font = null;
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                foreach (var any in texts)
                    if (any != null && any.font != null && any.gameObject.scene.IsValid()) { font = any.font; break; }
                if (font == null)
                    foreach (var any in texts)
                        if (any != null && any.font != null) { font = any.font; break; }
                if (font == null)
                {
                    var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (fonts != null && fonts.Length > 0) font = fonts[0];
                }
                if (font != null)
                {
                    var settings = TMP_Settings.instance;
                    var fld = settings == null ? null : typeof(TMP_Settings).GetField("m_defaultFontAsset", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fld != null) fld.SetValue(settings, font);
                }
            }
            catch { }
            return font;
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            UiFontSizeStore.Bind(t, size); t.alignment = align; t.richText = false;
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
