using GameData.Domains.LifeRecord;
using GameData.Domains.LifeRecord.GeneralRecord;
using HarmonyLib;

namespace JianghuYouling
{
    /// <summary>
    /// 太吾 1.0.56 的生平参数生产端已定义 51=七元赋性，
    /// 但前端 GameMessageUtils.ReadArguments 的单条渲染 switch 仍只处理到 49。
    /// 只补日志实际命中的性格参数语义，其余参数继续走本体实现。
    /// </summary>
    [HarmonyPatch(typeof(GameMessageUtils), nameof(GameMessageUtils.ReadArguments))]
    internal static class LifeRecordArgumentRenderFix
    {
        // JHYL_LIFE_RECORD_ARGUMENT_51
        private static bool Prefix(sbyte type, int index, TransferableRecordDataBase data, ref string __result)
        {
            if (type == ParameterType.PersonalityType)
            {
                try { __result = index >= 0 && index < 7 ? (DisplayConfig.Personality.Instance[index]?.Name ?? "") : ""; }
                catch { __result = ""; }
                return false;
            }

            return true;
        }
    }
}
