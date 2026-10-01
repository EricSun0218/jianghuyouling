using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace JianghuYouling
{
    /// <summary>
    /// 统一的输入框工厂:给输入框装上带 RectMask2D 裁剪的"视口 + 文本",杜绝长文溢出框外
    /// (单行水平滚动、多行竖向滚动,都把超出部分裁在框内,光标自动跟随)。
    /// 所有自填输入(接口/世界书/人设/聊天输入)统一走这里,免再各写各的、各漏各的裁剪。
    /// </summary>
    public static class UiInput
    {
        /// <summary>把一个已带 Image 背景的 inputGo 装成带裁剪视口的 TMP 输入框。out 出文本组件供需要者引用。</summary>
        public static SafeChatInputField Setup(GameObject inputGo, TMP_FontAsset font, float fontSize,
            bool multiline, TMP_InputField.LineType lineType, bool password,
            string placeholder, int charLimit, bool withScrollbar, out TextMeshProUGUI textComp)
        {
            // Large editable documents need the batched clipboard path.  Short fields keep the
            // stock behavior, so API keys and ordinary settings retain their native validation.
            var input = multiline && charLimit >= 100000
                ? (SafeChatInputField)inputGo.AddComponent<LongTextInputField>()
                : inputGo.AddComponent<SafeChatInputField>();

            // 视口:RectMask2D 把超出部分裁在框内;右侧若带滚动条则多留 16px
            var viewport = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(inputGo.transform, false);
            var vrt = viewport.GetComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(8, multiline ? 6 : 4);
            vrt.offsetMax = new Vector2(withScrollbar ? -24 : -8, multiline ? -6 : -4);

            var align = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.Left;
            textComp = MakeText(viewport.transform, font, fontSize, align, multiline);

            input.textViewport = vrt;
            input.textComponent = textComp;
            input.lineType = lineType;
            input.contentType = password ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
            input.richText = false;   // 玩家可编辑内容一律按纯文本处理；与 textComponent 状态保持一致
            if (password) input.asteriskChar = '*';
            if (charLimit > 0) input.characterLimit = charLimit;

            if (placeholder != null)
            {
                var ph = MakeText(viewport.transform, font, fontSize, align, multiline);
                ph.text = placeholder;
                ph.color = new Color(0.6f, 0.6f, 0.56f, 0.7f);
                ph.raycastTarget = false;
                input.placeholder = ph;
            }

            if (withScrollbar) input.verticalScrollbar = UiScroll.AddVertical(inputGo);
            return input;
        }

        static TextMeshProUGUI MakeText(Transform parent, TMP_FontAsset font, float size, TextAlignmentOptions align, bool wrap)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            UiFontSizeStore.Bind(t, size); t.richText = false; t.extraPadding = true;
            t.alignment = align; t.enableWordWrapping = wrap;
            t.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return t;
        }
    }
}
