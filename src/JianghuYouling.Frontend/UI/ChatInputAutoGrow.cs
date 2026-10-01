using System;
using JianghuYouling.Core.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace JianghuYouling
{
    /// <summary>
    /// 聊天输入框自适应高度。已提交文本与中文输入法候选串都参与同一宽度测量，
    /// 默认保留约三行，内容继续增长时最多展开到约七行；更多内容由 TMP 在裁剪视口内随光标滚动。
    /// </summary>
    public sealed class ChatInputAutoGrow : MonoBehaviour
    {
        public RectTransform inputRect;
        public RectTransform scrollRect;
        public TMP_InputField input;
        public TextMeshProUGUI text;
        // Defaults cover the permanent 56px bottom toolbar. ChatTab raises these values
        // when the optional Taiwu action row is visible.
        public float minH = 140f;
        public float maxH = 236f;
        public float bottom = 12f;
        public float gapToScroll = 12f;
        public float sidePad = 18f;
        public float vertPad = 68f;

        private string _lastComposition;
        private float _lastWidth = -1f;

        public void Recompute() => Recompute(CurrentComposition());

        private void Recompute(string composition)
        {
            if (inputRect == null || text == null) return;

            string committed = input != null ? (input.text ?? string.Empty) : string.Empty;
            string pending = composition ?? string.Empty;
            int visiblePosition = committed.Length + pending.Length;
            if (input != null)
            {
                try { visiblePosition = input.stringPosition; } catch { }
            }
            string measured = ImeCompositionProjection.ForMeasurement(committed, pending, visiblePosition);

            float width = inputRect.rect.width;
            _lastComposition = pending;
            _lastWidth = width;

            float height = minH;
            if (!string.IsNullOrWhiteSpace(measured))
            {
                float measureWidth = width - sidePad;
                if (measureWidth < 40f) measureWidth = text.rectTransform.rect.width;
                if (measureWidth < 40f) measureWidth = 600f;
                float preferred = 0f;
                try { preferred = text.GetPreferredValues(measured, measureWidth, 0f).y; } catch { }
                height = Mathf.Clamp(preferred + vertPad, minH, maxH);
            }

            var inputMin = inputRect.offsetMin;
            var inputMax = inputRect.offsetMax;
            inputRect.offsetMin = new Vector2(inputMin.x, bottom);
            inputRect.offsetMax = new Vector2(inputMax.x, bottom + height);
            if (scrollRect != null)
            {
                var scrollMin = scrollRect.offsetMin;
                scrollRect.offsetMin = new Vector2(scrollMin.x, bottom + height + gapToScroll);
            }
        }

        private string CurrentComposition()
        {
            if (input == null || !input.isFocused) return string.Empty;
            try
            {
                var eventSystem = EventSystem.current;
                BaseInput inputSystem = eventSystem != null && eventSystem.currentInputModule != null
                    ? eventSystem.currentInputModule.input
                    : null;
                return (inputSystem != null ? inputSystem.compositionString : Input.compositionString)
                    ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private void LateUpdate()
        {
            if (inputRect == null || text == null) return;
            string composition = CurrentComposition();
            float width = inputRect.rect.width;
            if (!string.Equals(composition, _lastComposition, StringComparison.Ordinal)
                || Mathf.Abs(width - _lastWidth) > 0.1f)
                Recompute(composition);
        }

        private void OnEnable()
        {
            _lastComposition = null;
            _lastWidth = -1f;
            Recompute();
        }
    }
}
