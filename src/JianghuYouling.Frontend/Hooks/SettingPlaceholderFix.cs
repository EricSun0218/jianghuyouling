using System.Reflection;
using FrameWork.ModSystem;
using Game.Views.Mod;
using HarmonyLib;
using TMPro;

namespace JianghuYouling
{
    /// <summary>
    /// 模组管理设置页的 InputField 复用了游戏预制件,空值占位文字硬编码为"输入人物姓氏",对 API Key 等很误导。
    /// 这里 Postfix InputFieldSettingItem.Initialize:仅对本 mod 的 llm_* 设置项,把占位文字改成该项的 Description。
    /// 只动自己的设置项(按 Key 前缀 gate),绝不影响其它 mod。
    /// </summary>
    [HarmonyPatch(typeof(InputFieldSettingItem), "Initialize")]
    internal static class SettingPlaceholderFix
    {
        private static readonly FieldInfo InputFieldF =
            AccessTools.Field(typeof(InputFieldSettingItem), "inputField");

        private static void Postfix(InputFieldSettingItem __instance, SettingEntry entry)
        {
            if (entry == null || entry.Key == null || !entry.Key.StartsWith("llm_")) return;
            try
            {
                var input = InputFieldF?.GetValue(__instance) as TMP_InputField;
                if (input != null && input.placeholder is TMP_Text ph)
                    ph.text = string.IsNullOrEmpty(entry.Description) ? "" : entry.Description;
            }
            catch { }
        }
    }
}
