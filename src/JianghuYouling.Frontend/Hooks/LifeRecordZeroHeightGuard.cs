using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>
    /// Taiwu 1.0.56's LoopScrollSizeUtils trusts a zero-valued ILayoutElement over the
    /// RectTransform that LifeRecordBase.Set has just sized to 16/36 px. RefillCellsFromEnd
    /// then keeps creating zero-height rows forever. Restrict the fallback to the exact native
    /// life-record row prefab observed in Player.log; every other loop list keeps vanilla sizing.
    /// </summary>
    [HarmonyPatch(typeof(LoopScrollSizeUtils), nameof(LoopScrollSizeUtils.GetPreferredHeight))]
    internal static class LifeRecordZeroHeightGuard
    {
        // JHYL_LIFE_RECORD_ZERO_HEIGHT_GUARD
        private static int _recoveryWarningEmitted;

        private static void Postfix(RectTransform item, ref float __result)
        {
            if (__result > 0f || item == null || item.name != "LifeRecordBase") return;

            float rectHeight = item.rect.height;
            __result = rectHeight > 0f ? rectHeight : 36f;
            if (System.Threading.Interlocked.Exchange(ref _recoveryWarningEmitted, 1) == 0)
                Debug.LogWarning("[JHYL_LIFE_RECORD_HEIGHT_RECOVERED] height=" + __result);
        }
    }
}
