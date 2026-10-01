using System.Collections;
using FrameWork;
using FrameWork.UISystem.UIElements;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>
    /// 使用本体 EventWindow/OptionHelp 的同一套图标资源与 TooltipInvoker 提示框。
    /// 动态界面无法直接引用序列化预制体，因此这里只按预制体契约装配原生组件。
    /// </summary>
    internal static class NativeTooltipHelpIcon
    {
        private const string ContentName = "JHYL_HelpContent";
        private const string ObjectName = "JHYL_OptionHelp";
        internal const string NormalSpriteName = "ui9_icon_event_option_help_0";
        internal const string HighlightedSpriteName = "ui9_icon_event_option_help_1";
        internal const string DisabledSpriteName = "ui9_icon_event_option_help_3";

        internal static void Attach(GameObject buttonObject, TextMeshProUGUI mainLabel,
            TMP_FontAsset fallbackFont, string title, string description, bool showOnLeft = false)
        {
            if (buttonObject == null || buttonObject.transform.Find(ContentName) != null) return;

            RectTransform buttonRt = buttonObject.transform as RectTransform;
            float buttonHeight = buttonRt != null
                ? Mathf.Max(buttonRt.rect.height, buttonRt.sizeDelta.y)
                : 0f;
            float iconSize = buttonHeight >= 44f ? 30f : 26f;

            // 本体克隆按钮会在运行时重算 OptionName 的 RectTransform。把原文字和问号
            // 交给同一个横向布局组，二者占位由 Unity LayoutSystem 一次计算，杜绝覆盖。
            var content = new GameObject(ContentName, typeof(RectTransform),
                typeof(LayoutElement), typeof(HorizontalLayoutGroup));
            content.layer = buttonObject.layer;
            content.transform.SetParent(buttonObject.transform, false);
            RectTransform contentRt = content.GetComponent<RectTransform>();
            contentRt.anchorMin = Vector2.zero;
            contentRt.anchorMax = Vector2.one;
            contentRt.offsetMin = new Vector2(8f, 0f);
            contentRt.offsetMax = new Vector2(-8f, 0f);
            LayoutElement contentLayout = content.GetComponent<LayoutElement>();
            contentLayout.ignoreLayout = true;
            HorizontalLayoutGroup row = content.GetComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(0, 0, 0, 0);
            row.spacing = 5f;
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;

            if (mainLabel != null)
            {
                mainLabel.transform.SetParent(content.transform, false);
                mainLabel.transform.SetAsFirstSibling();
                Vector2 preferred = mainLabel.GetPreferredValues(mainLabel.text ?? string.Empty);
                LayoutElement labelLayout = mainLabel.GetComponent<LayoutElement>();
                if (labelLayout == null) labelLayout = mainLabel.gameObject.AddComponent<LayoutElement>();
                labelLayout.ignoreLayout = false;
                labelLayout.minWidth = Mathf.Ceil(preferred.x) + 2f;
                labelLayout.preferredWidth = labelLayout.minWidth;
                labelLayout.minHeight = Mathf.Max(iconSize, Mathf.Ceil(preferred.y));
                labelLayout.preferredHeight = labelLayout.minHeight;
                labelLayout.flexibleWidth = 0f;
                labelLayout.flexibleHeight = 0f;
            }

            var help = new GameObject(ObjectName, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(CImage), typeof(TooltipInvoker), typeof(LayoutElement), typeof(CButton),
                typeof(NativeTooltipHelpBridge));
            help.layer = buttonObject.layer;
            help.transform.SetParent(content.transform, false);
            help.transform.SetAsLastSibling();

            RectTransform rt = help.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(iconSize, iconSize);

            CImage image = help.GetComponent<CImage>();
            image.type = Image.Type.Simple;
            image.fillCenter = true;
            image.preserveAspect = false;
            image.raycastTarget = true;
            image.color = Color.white;
            image.AutoSize = false;
            image.SetSprite(NormalSpriteName);

            // 使用本体 OptionHelp 的方形占位，但按当前按钮高度等比缩放。
            LayoutElement layout = help.GetComponent<LayoutElement>();
            layout.ignoreLayout = false;
            layout.minWidth = iconSize;
            layout.minHeight = iconSize;
            layout.preferredWidth = iconSize;
            layout.preferredHeight = iconSize;
            layout.flexibleWidth = 0f;
            layout.flexibleHeight = 0f;
            layout.layoutPriority = 1;

            TooltipInvoker invoker = help.GetComponent<TooltipInvoker>();
            invoker.Type = TipType.Simple;
            invoker.IsLanguageKey = false;
            invoker.NeedRefresh = false;
            invoker.ShowOnLeft = showOnLeft;
            invoker.ShowOnTop = false;
            invoker.PresetParam = new[] { title ?? string.Empty, description ?? string.Empty };
            // 动态按钮未来若增加子节点，也仍由帮助图标根节点触发本体提示。
            invoker.triggerByChildRaycast = true;

            CButton nativeButton = help.GetComponent<CButton>();
            nativeButton.targetGraphic = image;
            nativeButton.transition = Selectable.Transition.None;
            nativeButton.autoListen = false;

            help.GetComponent<NativeTooltipHelpBridge>().Configure(image, invoker);

        }
    }

    /// <summary>
    /// 用本体三态资源响应指针，并在自建 Canvas 未被 TooltipManager 的轮询命中时
    /// 仍调用原生 TooltipInvoker。提示框本身完全由本体 MouseTipSimple 生成。
    /// </summary>
    internal sealed class NativeTooltipHelpBridge : MonoBehaviour, IPointerEnterHandler,
        IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        private CImage _image;
        private TooltipInvoker _invoker;
        private Coroutine _showRoutine;
        private bool _hovering;

        internal void Configure(CImage image, TooltipInvoker invoker)
        {
            _image = image;
            _invoker = invoker;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovering = true;
            SetSprite(NativeTooltipHelpIcon.HighlightedSpriteName);
            if (_showRoutine != null) StopCoroutine(_showRoutine);
            _showRoutine = StartCoroutine(ShowWithNativeDelay());
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovering = false;
            SetSprite(NativeTooltipHelpIcon.NormalSpriteName);
            Hide();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            SetSprite(NativeTooltipHelpIcon.HighlightedSpriteName);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            SetSprite(_hovering
                ? NativeTooltipHelpIcon.HighlightedSpriteName
                : NativeTooltipHelpIcon.NormalSpriteName);
        }

        private IEnumerator ShowWithNativeDelay()
        {
            float delay = 0.2f;
            try
            {
                GlobalSettings settings = SingletonObject.getInstance<GlobalSettings>();
                if (settings != null) delay = settings.GetCurrentTipsTriggerTime() + 0.01f;
            }
            catch
            {
                // UI 尚未完全初始化时使用本体常见的短延迟；提示内容仍走原生组件。
            }
            yield return new WaitForSecondsRealtime(Mathf.Max(0.01f, delay));
            if (_hovering && isActiveAndEnabled && _invoker != null)
            {
                TooltipManager manager = SingletonObject.getInstance<TooltipManager>();
                if (manager == null || !manager.IsTipsVisible(_invoker.Type))
                    _invoker.ShowTips();
                RaiseNativeTooltip(manager);
                // ShowTips 可能在本帧末尾才完成原生 UI 激活，再校正一次显示层级。
                yield return null;
                if (_hovering) RaiseNativeTooltip(manager);
            }
            _showRoutine = null;
        }

        private void RaiseNativeTooltip(TooltipManager manager)
        {
            try
            {
                if (manager == null) manager = SingletonObject.getInstance<TooltipManager>();
                MouseTipBase tooltip = manager?.GetTipsUi(_invoker.Type);
                if (tooltip == null) return;
                Canvas canvas = tooltip.GetComponent<Canvas>();
                if (canvas == null) canvas = tooltip.gameObject.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                // 聊天窗口为 30000、会话栏为 30001；原生提示必须位于二者之上。
                canvas.sortingOrder = 31000;
            }
            catch
            {
                // 提示 UI 正处于创建/销毁阶段时交还本体下一帧处理。
            }
        }

        private void SetSprite(string spriteName)
        {
            if (_image != null) _image.SetSprite(spriteName);
        }

        private void Hide()
        {
            if (_showRoutine != null)
            {
                StopCoroutine(_showRoutine);
                _showRoutine = null;
            }
            try
            {
                if (_invoker != null) _invoker.HideTips();
            }
            catch
            {
                // 游戏退出或 UI 单例销毁阶段无需继续隐藏。
            }
        }

        private void OnDisable()
        {
            _hovering = false;
            Hide();
        }
    }
}
