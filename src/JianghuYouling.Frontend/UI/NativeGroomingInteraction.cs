using System;
using FrameWork;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 直接打开本体 ViewCharacterShave，并继续由 CharacterShaveAvatar 执行玩家选择的装扮。
    /// 不经过人物互动事件，因而不受好感、互动可见性、当月次数、资源或时间前置限制。
    /// </summary>
    internal static class NativeGroomingInteraction
    {
        public static void Start(int cutterTaiwuId, int targetNpcId, Action<bool> onDone)
        {
            if (cutterTaiwuId <= 0 || targetNpcId <= 0 || cutterTaiwuId == targetNpcId
                || UIElement.CharacterShave.Exist)
            {
                onDone?.Invoke(false);
                return;
            }
            try
            {
                ArgumentBox args = EasyPool.Get<ArgumentBox>();
                args.Set("CharId", targetNpcId);
                args.Set("NpcId", cutterTaiwuId);
                args.Set("time", -1);
                UIElement.CharacterShave.SetOnInitArgs(args);
                UIManager.Instance.MaskUI(UIElement.CharacterShave);
                Debug.Log("[江湖有灵] 已直接打开本体梳头修面界面 cutter="
                    + cutterTaiwuId + " target=" + targetNpcId);
                onDone?.Invoke(true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[江湖有灵] 打开本体梳头修面界面失败: " + e.GetType().Name);
                onDone?.Invoke(false);
            }
        }
    }
}
