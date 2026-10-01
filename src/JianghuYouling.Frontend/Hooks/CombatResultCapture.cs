using FrameWork;
using Game.Views.Combat;
using HarmonyLib;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// b24185552 权威结算入口：ViewCombatResult.OnInit 从 ArgumentBox 读取
    /// CombatResult / CombatType / MainEnemyId。这里只观察并投影到已登记的聊天战斗，
    /// 不改写参数、不接管本体后续战利品与公开/秘密处置流程。
    /// </summary>
    [HarmonyPatch(typeof(ViewCombatResult), nameof(ViewCombatResult.OnInit), new[] { typeof(ArgumentBox) })]
    internal static class CombatResultCapture
    {
        private static void Postfix(ArgumentBox argsBox)
        {
            if (argsBox == null) return;
            try
            {
                if (!argsBox.Get("CombatResult", out sbyte combatResult)
                    || !argsBox.Get("CombatType", out sbyte combatType)
                    || !argsBox.Get("MainEnemyId", out int mainEnemyId))
                {
                    Debug.LogWarning("[JHYL_COMBAT_RESULT] native result arguments incomplete");
                    return;
                }
                if (!ChatWindow.RecordNativeCombatResult(mainEnemyId, combatResult, combatType))
                    NativeInteractionCapture.CaptureCombatResult(mainEnemyId, combatResult, combatType);
            }
            catch (System.Exception ex)
            {
                // 观察补丁绝不能破坏游戏本体的结算与后续处置。
                Debug.LogWarning("[JHYL_COMBAT_RESULT] capture failed: " + ex.GetType().Name);
            }
        }
    }
}
