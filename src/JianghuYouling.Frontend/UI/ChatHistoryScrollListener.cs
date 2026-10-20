using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace JianghuYouling
{
    // ScrollRect cannot emit a position change when already clamped at the top
    // (including a short first page). Still honor an explicit upward wheel gesture.
    public sealed class ChatHistoryScrollListener : MonoBehaviour, IScrollHandler
    {
        public Action OnScrollUp;

        public void OnScroll(PointerEventData eventData)
        {
            if (eventData != null && eventData.scrollDelta.y > 0f) OnScrollUp?.Invoke();
        }
    }
}
