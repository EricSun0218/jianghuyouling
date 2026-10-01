using HarmonyLib;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 一层一层关:任一自建弹窗(聊天/聊天记录/设置/人设/世界书/过月消息)开着时,右键只关**最上层的那一个**,
    /// 不连带关掉它下面的弹窗或游戏的详情/事件窗。游戏的右键关窗最终都汇到 CommonCommandKit.RightMouse 这个静态
    /// HotKeyCommand 的 Check();在它身上做 Prefix —— 有弹窗开着时:① 在"右键按下"那一帧关掉最上层弹窗
    /// (PopupRegistry.CloseTopmost,按 sortingOrder 取最上);② 让 Check 恒返回 false(游戏当作没按右键,从而不关
    /// 下面的窗),并在关闭后再短暂拦截 0.25s,杜绝同一帧/相邻帧的连带关窗。
    /// (注:Check 每帧都被调用,不能无条件关窗,只在 GetMouseButtonDown 那帧关。)
    /// </summary>
    [HarmonyPatch(typeof(HotKeyCommand), "Check")]
    internal static class RightClickGuard
    {
        private static float _suppressUntil;

        private static bool Prefix(HotKeyCommand __instance, ref bool __result)
        {
            if (__instance != CommonCommandKit.RightMouse) return true;
            bool active = PopupRegistry.AnyOpen() || Time.unscaledTime < _suppressUntil;
            if (!active) return true;   // 没有我方弹窗开着 → 右键照常关下面的窗

            if (Input.GetMouseButtonDown(1) && PopupRegistry.AnyOpen())
            {
                PopupRegistry.CloseTopmost();               // 这一帧只关最上层的一个弹窗
                _suppressUntil = Time.unscaledTime + 0.25f; // 关后再压一会儿,避免同一次右键连带关下面的窗
            }
            __result = false;   // 让游戏以为没按右键 → 不关它下面的详情/事件窗
            return false;       // 跳过原 Check
        }
    }
}
