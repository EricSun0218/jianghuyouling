using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using GameData.GameDataBridge;
using HarmonyLib;
using UnityEngine;

// 命名空间 GameData.GameDataBridge 与其宿主类型 GameDataBridge 同名,且 NotificationHandler 是
// GameDataBridge 的**嵌套** delegate(不是命名空间成员),所以这里起别名写清楚,避免歧义。
using NativeGameDataBridge = GameData.GameDataBridge.GameDataBridge;
using NativeNotificationHandler = GameData.GameDataBridge.GameDataBridge.NotificationHandler;

namespace JianghuYouling
{
    /// <summary>
    /// 官方 GameDataBridge.ProcessNotifications 在轮询各监听者时**不隔离异常**:
    /// 只要有一个监听者的回调抛异常,整个 foreach 立刻冒泡出方法 —— 同一帧排在它后面的监听者
    /// (包括 AsyncMethodDispatcher) **整批收不到通知**,而那些通知已经在更早一步被
    /// `_processingNotificationCollections.Clear()` 丢弃,连 RawDataPool 都一起还回池子。
    ///
    /// 后果远不止"少收一次通知": AsyncMethodDispatcher 靠"每发一个异步请求就入队一个 checker"
    /// 与回来的通知做**严格 FIFO 配对**。只要有一次回包被吞,它的 checker 就永久多留一位,
    /// 此后每一个回包都配错 ⇒ 抛 mismatch ⇒ 又一次中断整帧分发 ⇒ 再吞一批回包 ⇒ 雪崩,
    /// 直到读档/切世界触发 OnWorldDataReady 重建队列为止。
    /// Player.log 里成串的 "AsyncMethodDispatcher handled mismatch notification" 就是这么来的。
    ///
    /// 本补丁只做一件事:把监听者回调包进 try/catch,让一个监听者的异常不再连累其它监听者。
    /// 正常路径的行为完全不变,监听者自己打的日志也一条不少 —— 只是异常不再向外冒泡。
    /// 与后端 Gm 分发器"任何游戏异常都被这一处接住,绝不冲垮后端主循环"是同一个思路。
    /// </summary>
    [HarmonyPatch(typeof(NativeGameDataBridge), nameof(NativeGameDataBridge.ProcessNotifications))]
    internal static class NotificationListenerIsolationPatch
    {
        // JHYL_NOTIFICATION_LISTENER_ISOLATION
        private static int _isolationReported;

        private static readonly MethodInfo HandlerInvoke = AccessTools.Method(typeof(NativeNotificationHandler), "Invoke");

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(
                typeof(NotificationListenerIsolationPatch), nameof(InvokeListenerSafely));
            int patched = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call)
                    && Equals(instruction.operand, HandlerInvoke))
                {
                    patched++;
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    continue;
                }
                yield return instruction;
            }

            if (patched == 0)
                Debug.LogWarning("[JHYL_NOTIFICATION_ISOLATION] 未命中监听者回调,补丁未生效(游戏版本可能已变)");
        }

        /// <summary>
        /// 替代原来的 <c>handler(notifications)</c> 直接调用。delegate 可能为 null
        /// (原生 `_displayEventHandler` 在未注册时就是 null),原生代码在这一步会抛 NRE,
        /// 这里一并兜住并返回。
        /// </summary>
        private static void InvokeListenerSafely(NativeNotificationHandler handler, List<NotificationWrapper> notifications)
        {
            if (handler == null) return;
            try
            {
                handler(notifications);
            }
            catch (Exception e)
            {
                // 只报第一次,之后静默 —— 避免同一次故障每帧刷屏。真凶看这里的类型名即可。
                if (System.Threading.Interlocked.Exchange(ref _isolationReported, 1) == 0)
                {
                    MethodInfo method = handler.Method;
                    Debug.LogWarning("[JHYL_NOTIFICATION_ISOLATION] 已隔离一个抛异常的监听者("
                        + (method != null && method.DeclaringType != null ? method.DeclaringType.Name : "?")
                        + "." + (method != null ? method.Name : "?") + ") : "
                        + e.GetType().Name + ": " + e.Message
                        + " —— 本次不再拖垮同帧其它监听者,异步队列不会因此错位");
                }
            }
        }
    }
}
