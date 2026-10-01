using System;
using System.Collections.Generic;
using System.Reflection;
using CharacterDataMonitor;
using Game.Components.ListStyleGeneralScroll.CellContent;
using Game.Components.ListStyleGeneralScroll.Item;
using Game.Views.CharacterMenu;
using Game.Views.CharacterMenu.Kidnap;
using Game.Views.MouseTips;
using Game.Views.VillagerRoleView;
using GameData.Domains.Character.AvatarSystem;
using GameData.Domains.Character.Display;
using GameData.Domains.Item.Display;
using HarmonyLib;
using UICommon.Character;

namespace JianghuYouling
{
    /// <summary>
    /// 固定人物副本在后端始终保持普通人物模板；只在头像渲染调用栈内替换模板参数。
    /// 线程局部作用域避免修改共享 CharacterDisplayData/Monitor，从根上隔离菜单权限与玩法数据。
    /// </summary>
    internal static class CharacterProxyAvatarRenderScope
    {
        [ThreadStatic] private static bool _active;
        [ThreadStatic] private static short _displayTemplateId;

        internal readonly struct State
        {
            internal readonly bool Active;
            internal readonly short DisplayTemplateId;

            internal State(bool active, short displayTemplateId)
            {
                Active = active;
                DisplayTemplateId = displayTemplateId;
            }
        }

        internal static State Enter(int characterId)
        {
            var state = new State(_active, _displayTemplateId);
            int taiwuId = 0;
            try { taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId; }
            catch { }
            if (taiwuId > 0 && characterId >= 0
                && CharacterProxyIdentityService.TryGetDisplayTemplate(
                    taiwuId, characterId, out short displayTemplateId))
            {
                _active = true;
                _displayTemplateId = displayTemplateId;
            }
            else
            {
                _active = false;
                _displayTemplateId = -1;
            }
            return state;
        }

        internal static void Exit(State state)
        {
            _active = state.Active;
            _displayTemplateId = state.DisplayTemplateId;
        }

        internal static bool TryGet(out short displayTemplateId)
        {
            displayTemplateId = _displayTemplateId;
            return _active && displayTemplateId >= 0;
        }

        internal static void ReplaceCellTemplate(AvatarWithNameCellData cell)
        {
            if (cell == null || cell.CharacterId < 0) return;
            int taiwuId = 0;
            try { taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId; }
            catch { }
            if (taiwuId > 0 && CharacterProxyIdentityService.TryGetDisplayTemplate(
                taiwuId, cell.CharacterId, out short displayTemplateId))
                cell.TemplateId = displayTemplateId;
        }
    }

    [HarmonyPatch(typeof(Game.Components.Avatar.Avatar), nameof(Game.Components.Avatar.Avatar.Refresh),
        new[] { typeof(CharacterDisplayData), typeof(bool) })]
    internal static class CharacterProxyDisplayDataAvatarScopePatch
    {
        private static void Prefix(CharacterDisplayData displayData,
            ref CharacterProxyAvatarRenderScope.State __state)
        {
            __state = CharacterProxyAvatarRenderScope.Enter(displayData?.CharacterId ?? 0);
        }

        private static Exception Finalizer(Exception __exception,
            CharacterProxyAvatarRenderScope.State __state)
        {
            CharacterProxyAvatarRenderScope.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Game.Components.Avatar.Avatar), nameof(Game.Components.Avatar.Avatar.Refresh),
        new[] { typeof(AvatarRelatedData), typeof(short), typeof(sbyte) })]
    internal static class CharacterProxyAvatarTemplateArgumentPatch
    {
        private static void Prefix(ref short characterTemplateId)
        {
            if (CharacterProxyAvatarRenderScope.TryGet(out short displayTemplateId))
                characterTemplateId = displayTemplateId;
        }
    }

    [HarmonyPatch(typeof(CharacterAvatar), nameof(CharacterAvatar.FillElement))]
    internal static class CharacterProxyMonitorAvatarScopePatch
    {
        private static void Prefix(CharacterAvatar __instance,
            ref CharacterProxyAvatarRenderScope.State __state)
        {
            int characterId = __instance == null || __instance.IsCharacterDead
                ? 0 : __instance.CharacterId;
            __state = CharacterProxyAvatarRenderScope.Enter(characterId);
        }

        private static Exception Finalizer(Exception __exception,
            CharacterProxyAvatarRenderScope.State __state)
        {
            CharacterProxyAvatarRenderScope.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(AvatarInfoMonitor), "get_TemplateId")]
    internal static class CharacterProxyMonitorTemplateGetterPatch
    {
        private static bool Prefix(ref short __result)
        {
            if (!CharacterProxyAvatarRenderScope.TryGet(out short displayTemplateId)) return true;
            __result = displayTemplateId;
            return false;
        }
    }

    // 通用滚动列表先把人物 DTO 转成只用于渲染的 CellData。这里只替换返回对象，
    // 不修改 CharacterDisplayData，因此人物菜单的 CreatingType/TemplateId 权限判断不受影响。
    [HarmonyPatch(typeof(AvatarWithNameCellData),
        nameof(AvatarWithNameCellData.FromGroupCharDisplayData))]
    internal static class CharacterProxyGroupAvatarCellPatch
    {
        private static void Postfix(AvatarWithNameCellData __result)
            => CharacterProxyAvatarRenderScope.ReplaceCellTemplate(__result);
    }

    [HarmonyPatch(typeof(AvatarWithNameCellData),
        nameof(AvatarWithNameCellData.FromVillagerCharDisplayData))]
    internal static class CharacterProxyVillagerAvatarCellPatch
    {
        private static void Postfix(AvatarWithNameCellData __result)
            => CharacterProxyAvatarRenderScope.ReplaceCellTemplate(__result);
    }

    [HarmonyPatch(typeof(AvatarWithNameCellData),
        nameof(AvatarWithNameCellData.FromKidnapCharDisplayData))]
    internal static class CharacterProxyKidnapAvatarCellPatch
    {
        private static void Postfix(AvatarWithNameCellData __result)
            => CharacterProxyAvatarRenderScope.ReplaceCellTemplate(__result);
    }

    [HarmonyPatch(typeof(AvatarWithNameCellData),
        nameof(AvatarWithNameCellData.FromCharacterDisplayDataForGeneralScrollList))]
    internal static class CharacterProxyGeneralScrollAvatarCellPatch
    {
        private static void Postfix(AvatarWithNameCellData __result)
            => CharacterProxyAvatarRenderScope.ReplaceCellTemplate(__result);
    }

    [HarmonyPatch(typeof(AvatarWithNameCellData),
        nameof(AvatarWithNameCellData.FromItemNeedCharacterDisplayData))]
    internal static class CharacterProxyItemNeedAvatarCellPatch
    {
        private static void Postfix(AvatarWithNameCellData __result)
            => CharacterProxyAvatarRenderScope.ReplaceCellTemplate(__result);
    }

    [HarmonyPatch(typeof(AvatarWithNameCellData),
        nameof(AvatarWithNameCellData.FromVillagerDisplayData))]
    internal static class CharacterProxyVillagerDisplayAvatarCellPatch
    {
        private static void Postfix(AvatarWithNameCellData __result)
            => CharacterProxyAvatarRenderScope.ReplaceCellTemplate(__result);
    }

    // 下列界面直接调用 Avatar.Refresh(AvatarRelatedData, templateId, ...)，没有经过
    // CharacterDisplayData 重载；在调用栈内建立短作用域，让既有参数补丁只改本次绘制。
    [HarmonyPatch(typeof(ViewCharacterMenuGenealogy), "UpdateAvatar")]
    internal static class CharacterProxyGenealogyAvatarScopePatch
    {
        private static void Prefix(CharacterDisplayDataForRelations charData,
            ref CharacterProxyAvatarRenderScope.State __state)
        {
            __state = CharacterProxyAvatarRenderScope.Enter(
                charData?.Main?.CharacterId ?? 0);
        }

        private static Exception Finalizer(Exception __exception,
            CharacterProxyAvatarRenderScope.State __state)
        {
            CharacterProxyAvatarRenderScope.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CharacterMenuKidnapGridItem), nameof(CharacterMenuKidnapGridItem.Set))]
    internal static class CharacterProxyKidnapGridAvatarScopePatch
    {
        private static void Prefix(KidnapCharDisplayData displayData,
            ref CharacterProxyAvatarRenderScope.State __state)
        {
            __state = CharacterProxyAvatarRenderScope.Enter(displayData?.CharacterId ?? 0);
        }

        private static Exception Finalizer(Exception __exception,
            CharacterProxyAvatarRenderScope.State __state)
        {
            CharacterProxyAvatarRenderScope.Exit(__state);
            return __exception;
        }
    }

    // 普通地图与新模式共用头像绘制入口，不挂到已有多个重载的 Set 上。
    [HarmonyPatch(typeof(MainPanel), "SetAvatar",
        new[] { typeof(CharacterDisplayDataForMapBlock), typeof(bool) })]
    internal static class CharacterProxyMapTipAvatarScopePatch
    {
        private static void Prefix(CharacterDisplayDataForMapBlock data,
            ref CharacterProxyAvatarRenderScope.State __state)
        {
            __state = CharacterProxyAvatarRenderScope.Enter(data?.CharacterId ?? 0);
        }

        private static Exception Finalizer(Exception __exception,
            CharacterProxyAvatarRenderScope.State __state)
        {
            CharacterProxyAvatarRenderScope.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(AssignPageVillagerView), nameof(AssignPageVillagerView.Set))]
    internal static class CharacterProxyAssignPageAvatarScopePatch
    {
        private static void Prefix(VillagerCharDisplayData data,
            ref CharacterProxyAvatarRenderScope.State __state)
        {
            __state = CharacterProxyAvatarRenderScope.Enter(data?.CharacterId ?? 0);
        }

        private static Exception Finalizer(Exception __exception,
            CharacterProxyAvatarRenderScope.State __state)
        {
            CharacterProxyAvatarRenderScope.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class CharacterProxyHealAvatarScopePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(HealChar), nameof(HealChar.RefreshDoctorForNormal));
            yield return AccessTools.Method(typeof(HealChar), nameof(HealChar.RefreshDoctorForGear));
            yield return AccessTools.Method(typeof(HealChar), nameof(HealChar.RefreshPatientForNormal));
            yield return AccessTools.Method(typeof(HealChar), nameof(HealChar.RefreshPatientForGear));
        }

        private static void Prefix(CharacterDisplayData charData,
            ref CharacterProxyAvatarRenderScope.State __state)
        {
            __state = CharacterProxyAvatarRenderScope.Enter(charData?.CharacterId ?? 0);
        }

        private static Exception Finalizer(Exception __exception,
            CharacterProxyAvatarRenderScope.State __state)
        {
            CharacterProxyAvatarRenderScope.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CardItem), "ShowCharacterAvatar")]
    internal static class CharacterProxyCardItemAvatarScopePatch
    {
        private static void Prefix(CardItem __instance,
            ref CharacterProxyAvatarRenderScope.State __state)
        {
            int characterId = 0;
            try
            {
                ITradeableContent data = __instance?.Data;
                if (data != null) characterId = data.CharacterId;
            }
            catch { }
            __state = CharacterProxyAvatarRenderScope.Enter(characterId);
        }

        private static Exception Finalizer(Exception __exception,
            CharacterProxyAvatarRenderScope.State __state)
        {
            CharacterProxyAvatarRenderScope.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(MouseTipMonthNotify), nameof(MouseTipMonthNotify.Refresh), new Type[] { })]
    internal static class CharacterProxyMonthNotifyAvatarPatch
    {
        private static readonly FieldInfo DataListField = AccessTools.Field(
            typeof(MouseTipMonthNotify), "_dataList");

        private static void Prefix(MouseTipMonthNotify __instance)
        {
            try
            {
                var cells = DataListField?.GetValue(__instance)
                    as List<AvatarWithNameCellData>;
                if (cells == null) return;
                foreach (AvatarWithNameCellData cell in cells)
                    CharacterProxyAvatarRenderScope.ReplaceCellTemplate(cell);
            }
            catch { }
        }
    }
}
