using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>给多行 TMP_InputField 加一根竖直滚动条。TMP_InputField 的滚轮滚动依赖 verticalScrollbar 非空,
    /// 不挂就既看不到滚动条、滚轮也不滚 —— 把返回值赋给 input.verticalScrollbar 即可既显示也启用滚动。</summary>
    internal static class UiScroll
    {
        public static Scrollbar AddVertical(GameObject inputGo, float width = 12f)
        {
            var sb = BuildVertical(inputGo, "VScrollbar", width,
                new Color(1, 1, 1, 0.07f), new Color(0.62f, 0.68f, 0.64f, 0.75f));
            // TMP_InputField 的滚动语义是 value 0=文首、1=文末(官方 InputField 预制体即
            // TopToBottom)。设成 BottomToTop 会让手柄位置与文本位置颠倒、拖动方向相反。
            sb.direction = Scrollbar.Direction.TopToBottom;
            return sb;
        }

        /// <summary>给普通 ScrollRect 加一根始终可辨认、可拖动的右侧竖直滚动条。</summary>
        public static Scrollbar AddVerticalForScrollRect(GameObject scrollGo, float width = 14f)
        {
            var sb = BuildVertical(scrollGo, "MessageVScrollbar", width,
                new Color(1, 1, 1, 0.14f), new Color(0.68f, 0.72f, 0.65f, 0.90f));
            // ScrollRect 的 verticalNormalizedPosition 是 1=顶部、0=底部，手柄方向与 TMP 输入框相反。
            sb.direction = Scrollbar.Direction.BottomToTop;
            return sb;
        }

        private static Scrollbar BuildVertical(GameObject owner, string name, float width,
            Color trackColor, Color handleColor)
        {
            var sbGo = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            sbGo.transform.SetParent(owner.transform, false);
            var sbRt = sbGo.GetComponent<RectTransform>();
            sbRt.anchorMin = new Vector2(1, 0); sbRt.anchorMax = new Vector2(1, 1); sbRt.pivot = new Vector2(1, 1);
            sbRt.sizeDelta = new Vector2(width, -8f); sbRt.anchoredPosition = new Vector2(-2, -4);
            sbGo.GetComponent<Image>().color = trackColor;

            var area = new GameObject("SlidingArea", typeof(RectTransform));
            area.transform.SetParent(sbGo.transform, false);
            var areaRt = area.GetComponent<RectTransform>();
            areaRt.anchorMin = Vector2.zero; areaRt.anchorMax = Vector2.one;
            areaRt.offsetMin = new Vector2(1, 1); areaRt.offsetMax = new Vector2(-1, -1);

            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(area.transform, false);
            var hRt = handle.GetComponent<RectTransform>();
            hRt.sizeDelta = Vector2.zero;
            handle.GetComponent<Image>().color = handleColor;

            var sb = sbGo.GetComponent<Scrollbar>();
            sb.handleRect = hRt;
            sb.targetGraphic = handle.GetComponent<Image>();
            return sb;
        }
    }
}
