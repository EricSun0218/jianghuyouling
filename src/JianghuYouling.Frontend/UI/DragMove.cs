using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace JianghuYouling
{
    /// <summary>挂在标题栏上:拖动它来移动 target 面板。ScreenSpaceOverlay + CanvasScaler 下,
    /// 鼠标位移是屏幕像素,需除以画布 scaleFactor 才与面板 anchoredPosition 同尺度。</summary>
    internal sealed class DragMove : MonoBehaviour, IDragHandler
    {
        public RectTransform target;
        public Action<Vector2> onPositionChanged;

        public void OnDrag(PointerEventData e)
        {
            if (target == null) return;
            var canvas = target.GetComponentInParent<Canvas>();
            float s = (canvas != null && canvas.scaleFactor > 0f) ? canvas.scaleFactor : 1f;
            target.anchoredPosition += e.delta / s;
            onPositionChanged?.Invoke(target.anchoredPosition);
        }
    }
}
