using HarmonyLib;
using Game.Views.CharacterMenu;

namespace JianghuYouling
{
    /// <summary>
    /// 兼容升级前已产生、尚待后端迁移的固定模板副本。新副本均为本体合法智能人物，正常情况
    /// 不再命中；旧存档第一次打开人物时完成迁移前仍需避免被前端误判为猎人动物。
    /// </summary>
    [HarmonyPatch(typeof(CharacterMonitorModel), nameof(CharacterMonitorModel.IsTaiwuBeastTeammate))]
    internal static class CharacterProxyBeastClassificationPatch
    {
        private static void Postfix(int charId, ref bool __result)
        {
            if (!__result || charId <= 0) return;
            int taiwuId = 0;
            try { taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId; }
            catch { }
            if (taiwuId > 0 && CharacterProxyIdentityService.IsKnownProxy(taiwuId, charId))
                __result = false;
        }
    }

    /// <summary>
    /// 人物详情页维护自己的特殊队员快照，不经过 CharacterMonitorModel；离队按钮正是从这里
    /// 决定调用普通离队还是猎人“动物转物品”，因此必须同时修正这一条实际操作路径。
    /// </summary>
    [HarmonyPatch(typeof(ViewCharacterMenu), nameof(ViewCharacterMenu.IsTaiwuBeastTeammate))]
    internal static class CharacterProxyMenuBeastClassificationPatch
    {
        private static void Postfix(int charId, ref bool __result)
        {
            if (!__result || charId <= 0) return;
            int taiwuId = 0;
            try { taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId; }
            catch { }
            if (taiwuId > 0 && CharacterProxyIdentityService.IsKnownProxy(taiwuId, charId))
                __result = false;
        }
    }

    internal static class CharacterProxyMenuAccessPolicy
    {
        internal static bool IsKnownProxy(ViewCharacterMenu menu)
        {
            if (menu == null || menu.CurCharacterId <= 0) return false;
            int taiwuId = 0;
            try { taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId; }
            catch { }
            return taiwuId > 0
                && CharacterProxyIdentityService.IsKnownProxy(taiwuId, menu.CurCharacterId);
        }
    }

    /// <summary>
    /// 固定外观只用于画面，不得继承固定模板的页签限制。合法人物副本的社会、关系和
    /// 生平数据均由普通人物实体承载，因此仅对已登记副本恢复本体普通人物入口。
    /// </summary>
    [HarmonyPatch(typeof(ViewCharacterMenu), "GetCanViewSocial")]
    internal static class CharacterProxySocialPageAccessPatch
    {
        private static void Postfix(ViewCharacterMenu __instance, ref bool __result)
        {
            if (!__result && CharacterProxyMenuAccessPolicy.IsKnownProxy(__instance))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(ViewCharacterMenu), "GetCanViewRelation")]
    internal static class CharacterProxyRelationPageAccessPatch
    {
        private static void Postfix(ViewCharacterMenu __instance, ref bool __result)
        {
            if (!__result && CharacterProxyMenuAccessPolicy.IsKnownProxy(__instance))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(ViewCharacterMenu), "GetCanViewLifeRecord")]
    internal static class CharacterProxyLifeRecordPageAccessPatch
    {
        private static void Postfix(ViewCharacterMenu __instance, ref bool __result)
        {
            if (!__result && CharacterProxyMenuAccessPolicy.IsKnownProxy(__instance))
                __result = true;
        }
    }
}
