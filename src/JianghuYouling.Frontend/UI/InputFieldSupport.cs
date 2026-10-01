using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace JianghuYouling
{
    /// <summary>
    /// 旧版输入法宿主的兼容清理器。
    /// b24185552 的 TMP_InputField 已会根据真实光标为 ScreenSpaceOverlay 画布定位候选窗，
    /// 不需要本 Mod 再覆盖 compositionCursorPos，也不能再挂载 CustomInputFieldChecker。
    /// </summary>
    public sealed class ImeFollow : MonoBehaviour
    {
        /// <summary>不再创建宿主；只清理旧版运行时遗留。</summary>
        public static void Initialize() => Shutdown();

        public static void Shutdown()
        {
            // 按类型扫描可覆盖同一进程热重载前由旧版创建、但静态字段已丢失的宿主。
            try
            {
                foreach (var host in Resources.FindObjectsOfTypeAll<ImeFollow>())
                    if (host != null) Destroy(host.gameObject);
            }
            catch { }

            // 旧版可能已经给打开的 JHYL 输入框挂过这个组件。只移除本 Mod 画布下的实例，
            // 绝不触碰游戏本体或其他 Mod 的输入框。
            try
            {
                foreach (var checker in Resources.FindObjectsOfTypeAll<CustomInputFieldChecker>())
                {
                    if (checker == null) continue;
                    var canvas = checker.GetComponentInParent<Canvas>();
                    var root = canvas != null && canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
                    if (root == null || string.IsNullOrEmpty(root.name)
                        || !root.name.StartsWith("JHYL_", System.StringComparison.Ordinal)) continue;
                    // OnDisable 属于游戏组件自身；即便旧实例内部状态已坏，也必须继续销毁。
                    try { checker.enabled = false; } catch { }
                    try { Destroy(checker); } catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 聊天输入框专用的输入法候选窗跟随器。游戏本体的 TMPInputCursorPosAdjust 只取整个
    /// 文本矩形左下角，TMP 内置逻辑又只在换行时刷新；横向移动光标或回到上一行时，候选窗
    /// 会留在旧位置。这里每个 LateUpdate 从 TMP 字符几何算出真实插入点，仅写当前 JHYL
    /// 聊天框的 BaseInput.compositionCursorPos，不向游戏本体或其他 Mod 挂组件。
    /// </summary>
    [RequireComponent(typeof(TMP_InputField))]
    public sealed class ChatImeCaretFollower : MonoBehaviour
    {
        public TMP_InputField Input;
        private bool _hasGeometrySnapshot;
        private string _lastInputText;
        private string _lastRenderedText;
        private string _lastComposition;
        private int _lastCaret = -1;
        private int _lastStringPosition = -1;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private Rect _lastTextRect;
        private Matrix4x4 _lastLocalToWorld;

        private BaseInput InputSystem
        {
            get
            {
                var current = EventSystem.current;
                return current != null && current.currentInputModule != null
                    ? current.currentInputModule.input : null;
            }
        }

        private void LateUpdate()
        {
            TMP_InputField input = Input != null ? Input : GetComponent<TMP_InputField>();
            BaseInput inputSystem = InputSystem;
            if (input == null || inputSystem == null || !input.isFocused || input.textComponent == null)
            {
                _hasGeometrySnapshot = false;
                return;
            }
            try
            {
                TMP_Text text = input.textComponent;
                string inputText = input.text ?? string.Empty;
                string renderedText = text.text ?? string.Empty;
                string composition = UnityEngine.Input.compositionString ?? string.Empty;
                Rect textRect = text.rectTransform.rect;
                Matrix4x4 localToWorld = text.rectTransform.localToWorldMatrix;
                if (_hasGeometrySnapshot
                    && string.Equals(_lastInputText, inputText, StringComparison.Ordinal)
                    && string.Equals(_lastRenderedText, renderedText, StringComparison.Ordinal)
                    && string.Equals(_lastComposition, composition, StringComparison.Ordinal)
                    && _lastCaret == input.caretPosition
                    && _lastStringPosition == input.stringPosition
                    && _lastScreenWidth == Screen.width && _lastScreenHeight == Screen.height
                    && _lastTextRect == textRect && _lastLocalToWorld == localToWorld)
                    return;
                text.ForceMeshUpdate(false, true);
                TMP_TextInfo info = text.textInfo;
                int count = info != null ? info.characterCount : 0;
                int caret = Mathf.Clamp(input.caretPosition, 0, count);
                Vector3 local;
                // TMP renders an internal U+200B sentinel even when committed input is empty.
                // Treat only that lone sentinel as empty: while an IME composition is active the
                // committed string can still be empty but textInfo also contains the candidate,
                // whose end is the correct candidate-window anchor.
                if (count <= 0 || (string.IsNullOrEmpty(input.text) && count == 1))
                {
                    Rect rect = text.rectTransform.rect;
                    local = new Vector3(rect.xMin, rect.yMax - text.fontSize * 1.15f, 0f);
                }
                else if (caret < count)
                {
                    TMP_CharacterInfo character = info.characterInfo[caret];
                    local = new Vector3(character.origin, character.descender, 0f);
                }
                else
                {
                    TMP_CharacterInfo character = info.characterInfo[count - 1];
                    local = new Vector3(character.xAdvance, character.descender, 0f);
                }

                Vector3 world = text.rectTransform.TransformPoint(local);
                Canvas canvas = input.GetComponentInParent<Canvas>();
                Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera : null;
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, world);
                screen.x = Mathf.Clamp(screen.x, 0f, Screen.width - 1f);
                screen.y = Mathf.Clamp(Screen.height - screen.y, 0f, Screen.height - 1f);
                inputSystem.compositionCursorPos = screen;
                _lastInputText = inputText;
                _lastRenderedText = text.text ?? string.Empty;
                _lastComposition = composition;
                _lastCaret = input.caretPosition;
                _lastStringPosition = input.stringPosition;
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
                _lastTextRect = text.rectTransform.rect;
                _lastLocalToWorld = text.rectTransform.localToWorldMatrix;
                _hasGeometrySnapshot = true;
            }
            catch { _hasGeometrySnapshot = false; }
        }
    }

    /// <summary>
    /// TMP 原生 caret 在多行视口重建、中文输入法提交和按钮短暂抢焦点时偶尔不重建。
    /// 聊天输入框用一条受同一 RectMask2D 裁剪的实体竖线表示插入点；聚焦期间常亮，
    /// 位置始终取 TMP 当前字符几何，因此点击、方向键移动、自动换行与内部滚动后都可见。
    /// </summary>
    [RequireComponent(typeof(TMP_InputField))]
    public sealed class ChatVisibleCaret : MonoBehaviour
    {
        public TMP_InputField Input;
        public RectTransform Viewport;
        public TMP_Text Text;
        private Image _line;
        private RectTransform _lineRect;
        private string _lastInputText;
        private string _lastRenderedText;
        private int _lastCaret = -1;
        private int _lastStringPosition = -1;
        private Vector2 _lastTextOffset;
        private Rect _lastViewportRect;
        private bool _hasGeometrySnapshot;

        private void EnsureLine()
        {
            TMP_InputField input = Input != null ? Input : GetComponent<TMP_InputField>();
            RectTransform viewport = Viewport != null ? Viewport : input != null ? input.textViewport : null;
            if (_line != null || viewport == null) return;
            var go = new GameObject("StableCaret", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(viewport, false);
            _lineRect = go.GetComponent<RectTransform>();
            _lineRect.anchorMin = _lineRect.anchorMax = new Vector2(0f, 0f);
            _lineRect.pivot = new Vector2(0f, 0f);
            _line = go.GetComponent<Image>();
            _line.color = new Color(0.92f, 0.82f, 0.56f, 1f);
            _line.raycastTarget = false;
            go.transform.SetAsLastSibling();
        }

        private void OnEnable()
        {
            _hasGeometrySnapshot = false;
            EnsureLine();
        }

        private void LateUpdate()
        {
            TMP_InputField input = Input != null ? Input : GetComponent<TMP_InputField>();
            TMP_Text text = Text != null ? Text : input != null ? input.textComponent : null;
            RectTransform viewport = Viewport != null ? Viewport : input != null ? input.textViewport : null;
            EnsureLine();
            if (_line == null || _lineRect == null || input == null || text == null || viewport == null
                || !input.isFocused || !input.interactable || input.readOnly)
            {
                if (_line != null) _line.enabled = false;
                return;
            }

            try
            {
                string inputText = input.text ?? string.Empty;
                string renderedText = text.text ?? string.Empty;
                int inputCaret = input.caretPosition;
                int stringPosition = input.stringPosition;
                Vector2 textOffset = text.rectTransform.anchoredPosition;
                Rect viewportRect = viewport.rect;
                if (_hasGeometrySnapshot
                    && string.Equals(_lastInputText, inputText, StringComparison.Ordinal)
                    && string.Equals(_lastRenderedText, renderedText, StringComparison.Ordinal)
                    && _lastCaret == inputCaret
                    && _lastStringPosition == stringPosition
                    && _lastTextOffset == textOffset
                    && _lastViewportRect == viewportRect)
                {
                    _line.enabled = true;
                    return;
                }

                input.ForceLabelUpdate();
                text.ForceMeshUpdate(false, true);
                TMP_TextInfo info = text.textInfo;
                int count = info != null ? info.characterCount : 0;
                int caret = Mathf.Clamp(input.caretPosition, 0, count);
                float x, bottom, height;
                // The rendered TMP text contains a virtual U+200B sentinel for an empty field.
                // A live IME candidate adds more characters even while inputText is still empty,
                // so only the lone sentinel selects the empty first-line geometry.
                if (count <= 0 || (inputText.Length == 0 && count == 1))
                {
                    Rect rect = text.rectTransform.rect;
                    x = rect.xMin;
                    height = Mathf.Max(18f, text.fontSize * 1.18f);
                    bottom = rect.yMax - height - 1f;
                }
                else
                {
                    TMP_CharacterInfo character = info.characterInfo[caret < count ? caret : count - 1];
                    x = caret < count ? character.origin : character.xAdvance;
                    bottom = character.descender;
                    height = character.ascender - character.descender;
                    if (height < 8f) height = Mathf.Max(18f, text.fontSize * 1.18f);
                }

                Vector3 world = text.rectTransform.TransformPoint(new Vector3(x, bottom, 0f));
                Vector3 local = viewport.InverseTransformPoint(world);
                // StableCaret is anchored to the viewport's lower-left corner, while
                // InverseTransformPoint returns coordinates relative to the viewport pivot.
                _lineRect.anchoredPosition = new Vector2(
                    local.x - viewportRect.xMin,
                    local.y - viewportRect.yMin);
                _lineRect.sizeDelta = new Vector2(2.4f, height);
                _line.enabled = true;
                _line.transform.SetAsLastSibling();
                _lastInputText = inputText;
                _lastRenderedText = renderedText;
                _lastCaret = inputCaret;
                _lastStringPosition = stringPosition;
                _lastTextOffset = text.rectTransform.anchoredPosition;
                _lastViewportRect = viewportRect;
                _hasGeometrySnapshot = true;
            }
            catch
            {
                _line.enabled = false;
                _hasGeometrySnapshot = false;
            }
        }
    }

    /// <summary>聊天输入卡片的静默/聚焦视觉状态；只改变框线与底色，不干预输入和 IME。</summary>
    public sealed class ChatComposerChrome : MonoBehaviour
    {
        public TMP_InputField Input;
        public Image Frame;
        public Image Surface;
        private bool _lastFocused;
        private bool _initialized;

        private void LateUpdate()
        {
            bool focused = Input != null && Input.isFocused && Input.interactable && !Input.readOnly;
            if (_initialized && focused == _lastFocused) return;
            _initialized = true;
            _lastFocused = focused;
            if (Frame != null)
                Frame.color = focused
                    ? new Color(0.56f, 0.48f, 0.30f, 0.98f)
                    : new Color(0.23f, 0.29f, 0.27f, 0.96f);
            if (Surface != null)
                Surface.color = focused
                    ? new Color(0.085f, 0.095f, 0.087f, 0.995f)
                    : new Color(0.072f, 0.080f, 0.075f, 0.99f);
        }
    }
}
