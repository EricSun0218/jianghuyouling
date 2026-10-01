using HarmonyLib;
using GameData.Domains.Character.AvatarSystem;          // AvatarManager
// EAvatarElementsType 在全局命名空间,无需 using

namespace JianghuYouling
{
    /// <summary>
    /// ClothSkin id=0 is a valid fallback body layer in small avatars.
    /// JHYL_CLOTHSKIN_PASS_THROUGH / JHYL_CLOTHSKIN_NO_NULL_RESULT:
    /// 旧版为了压掉 ClothSkin:Id=0 日志,把 __result 置空并跳过原方法;NPC 脱下衣着时这会让小头像身体层消失。
    /// 现在不再拦截返回值,显示正确优先;若游戏本身仍记日志,也由原方法处理。
    /// </summary>
    [HarmonyPatch(typeof(AvatarManager), "GetAsset", new[] { typeof(int), typeof(EAvatarElementsType), typeof(short[]) })]
    internal static class AvatarClothSkinLogFix
    {
        private static bool Prefix()
        {
            return true;           // 交还原方法;不再把 __result 置空
        }
    }
}
