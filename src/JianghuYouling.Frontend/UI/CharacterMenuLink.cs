using System;
using FrameWork;                 // EasyPool, ArgumentBox
using Game.Views.CharacterMenu;  // ECharacterSubToggleBase, ECharacterSubPage, SubPageIndex
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 复用游戏原生角色菜单:打开"太吾本人"的详情界面到指定子页。
    /// 供聊天里"查看获得物"入口使用(给了马→坐骑页、给了功法→武学页、给了物品→装备页、给了秘闻→秘闻页)。
    /// 打开方式取自游戏自身(ViewBottom / SettlementBountyCharView 等):
    ///   UIElement.CharacterMenu.SetOnInitArgs(box{CharacterId, PreviousView, ViewCharacterMenuTaretPage}); UIManager.ShowUI(...)
    /// </summary>
    internal static class CharacterMenuLink
    {
        public static void OpenTaiwuPage(ECharacterSubToggleBase toggleBase, ECharacterSubPage subPage)
        {
            try
            {
                int taiwuId = EwReflect.TaiwuId();
                if (taiwuId <= 0) return;
                // PreviousView = _characterControlTemplateId(决定菜单显示哪套操作按钮)。
                // 取 2 = 主界面底栏打开自己角色面板时的"普通自我查看"上下文(见 ViewBottom);
                // 不用 6(那是赏金目标视图 SettlementBountyCharView 的上下文)。
                ShowCharacterMenu(EasyPool.Get<ArgumentBox>()
                    .Set("CharacterId", taiwuId)
                    .Set("PreviousView", 2)
                    .SetObject("ViewCharacterMenuTaretPage", new SubPageIndex(toggleBase, subPage)));
            }
            catch (Exception e) { Debug.LogWarning("[江湖有灵] 打开角色菜单失败: " + e.GetType().Name); }
        }

        /// <summary>
        /// 打开指定 NPC 的原生人物详情首页。参数形态取自最新版本体人物选择、事件人物列表等入口：
        /// 非太吾只传 CharacterId，并明确落到 CharacterBase；不冒用任何可操作模板。
        /// </summary>
        public static void OpenCharacterInfo(int characterId)
        {
            if (characterId < 0) return;
            try
            {
                ShowCharacterMenu(EasyPool.Get<ArgumentBox>()
                    .Set("CharacterId", characterId)
                    .SetObject("ViewCharacterMenuTaretPage",
                        new SubPageIndex(ECharacterSubToggleBase.CharacterBase)));
            }
            catch (Exception e) { Debug.LogWarning("[江湖有灵] 打开人物详情失败: " + e.GetType().Name); }
        }

        private static void ShowCharacterMenu(ArgumentBox args)
        {
            Action restore = null;
            try
            {
                UIElement.CharacterMenu.SetOnInitArgs(args);
                // 聊天窗 canvas sortingOrder=30000 远高于原生菜单(~600),会盖住详情页。
                // 打开查看时把聊天窗临时藏到其下，关掉原生详情页后再恢复当前聊天页签。
                if (ChatWindow.IsOpen)
                {
                    ChatWindow.HideForOverlay();
                    restore = () =>
                    {
                        try { ChatWindow.RestoreFromOverlay(); }
                        finally
                        {
                            UIElement.CharacterMenu.OnHide = (Action)Delegate.Remove(
                                UIElement.CharacterMenu.OnHide, restore);
                        }
                    };
                    UIElement.CharacterMenu.OnHide = (Action)Delegate.Combine(
                        UIElement.CharacterMenu.OnHide, restore);
                }
                UIManager.Instance.ShowUI(UIElement.CharacterMenu, true);
            }
            catch
            {
                if (restore != null)
                {
                    UIElement.CharacterMenu.OnHide = (Action)Delegate.Remove(
                        UIElement.CharacterMenu.OnHide, restore);
                    ChatWindow.RestoreFromOverlay();
                }
                throw;
            }
        }

        // 赠予太吾的物品(含装备/坐骑)都进背包,不会自动装备;而武具/车马页只显示"已装备"之物,跳过去看不到。
        // 故一切赠物查看一律去"持有"页。持有页:ItemBase 这个 toggle 在游戏里无子页(SubPages 为空),子页给 None 即切到该页。
        public static void OpenHoldingsPage() => OpenTaiwuPage(ECharacterSubToggleBase.ItemBase, ECharacterSubPage.None);

        public static void OpenSkillPage() => OpenTaiwuPage(ECharacterSubToggleBase.AttainmentBase, ECharacterSubPage.AttainmentCombatSkill);
        public static void OpenLifeSkillPage() => OpenTaiwuPage(ECharacterSubToggleBase.AttainmentBase, ECharacterSubPage.AttainmentLifeSkill);
        public static void OpenSecretPage() => OpenTaiwuPage(ECharacterSubToggleBase.InformationBase, ECharacterSubPage.Secret);
    }
}
