using System;
using GameData.Common;
using GameData.Domains;
using GameData.Domains.Adventure;
using GameData.Domains.Mod;
using GameData.Utilities;
using HarmonyLib;
using JianghuYouling.Core.Commission;

namespace JianghuYouling.Backend
{
    /// <summary>清巢事实保存在本体 Mod 存档域，随存档加载与回退，不使用跨世界静态计数。</summary>
    internal static class OutlawNestCommissionProgress
    {
        internal static string ModId;

        private static string DataName(int taiwuId) => "outlaw_nest_completions_" + taiwuId;
        private static string PageName(int page) => "grades_" + page;

        private static SerializableModData Load(int taiwuId, out int count)
        {
            if (taiwuId <= 0 || string.IsNullOrWhiteSpace(ModId))
                throw new InvalidOperationException("清巢记录身份不可用");
            count = 0;
            if (!DomainManager.Mod.TryGet(ModId, DataName(taiwuId), true,
                    out SerializableModData data)) return new SerializableModData();
            if (data == null || !data.Get("count", out count) || count < 0)
                throw new InvalidOperationException("清巢记录无效");
            return data;
        }

        internal static void Read(int taiwuId, int baseline, out int count, out int grade)
        {
            SerializableModData data = Load(taiwuId, out count);
            OutlawNestProgressPolicy.TryFirstCompletionGrade(count, baseline, page =>
            {
                data.Get(PageName(page), out string grades);
                return grades;
            }, out grade);
            if (baseline >= 0 && baseline < count && grade < 0)
                throw new InvalidOperationException("清巢完成事实缺失");
        }

        internal static void Record(DataContext context, int taiwuId, int difficulty)
        {
            int grade = OutlawNestProgressPolicy.RewardGradeForDifficulty(difficulty);
            if (context == null || grade < 0) throw new InvalidOperationException("清巢结算参数无效");
            SerializableModData old = Load(taiwuId, out int count);
            if (count == int.MaxValue) throw new InvalidOperationException("清巢计数已达上限");
            var next = new SerializableModData();
            int lastPage = count / OutlawNestProgressPolicy.PageSize;
            // 构造新值后一次写入，避免修改从本体读取的旧对象造成半写入。
            for (int page = 0; page <= lastPage; page++)
            {
                old.Get(PageName(page), out string grades);
                grades = grades ?? string.Empty;
                int expected = page < lastPage ? OutlawNestProgressPolicy.PageSize
                    : count % OutlawNestProgressPolicy.PageSize;
                if (grades.Length != expected)
                    throw new InvalidOperationException("清巢记录页长度不符");
                foreach (char value in grades)
                    if (OutlawNestProgressPolicy.RewardGradeForDifficulty(value - '0') < 0)
                        throw new InvalidOperationException("清巢记录品级无效");
                next.Set(PageName(page), page == lastPage ? grades + (char)('0' + grade) : grades);
            }
            next.Set("count", count + 1);
            DomainManager.Mod.SetSerializableModData(context, ModId, DataName(taiwuId), true, next);
        }
    }

    // 只监听玩家成功通关入口；不把地图移除、超时或 NPC 清巢当作玩家完成。
    [HarmonyPatch(typeof(AdventureDomain), nameof(AdventureDomain.FinishAdventure))]
    internal static class OutlawNestCompletionPatch
    {
        internal sealed class Completion
        {
            internal int TaiwuId;
            internal int Difficulty;
        }

        private static void Prefix(AdventureDomain __instance, out Completion __state)
        {
            __state = null;
            try
            {
                var taiwu = __instance.GetAdventureTaiwu();
                if (taiwu.NotInAdventure || !__instance.TryGetElement_Adventures(
                        taiwu.AdventureId, out var runtime) || runtime == null) return;
                int grade = OutlawNestProgressPolicy.RewardGradeForDifficulty(runtime.Core.Grade);
                if (grade < 0) return;
                for (short i = 0; i < Config.EnemyNest.Instance.Count; i++)
                {
                    var nest = Config.EnemyNest.Instance[i];
                    if (!OutlawNestProgressPolicy.IsOutlawNest(runtime.CoreId,
                            nest.AdventureId, nest.NestType)) continue;
                    __state = new Completion
                    {
                        TaiwuId = DomainManager.Taiwu.GetTaiwuCharId(), Difficulty = grade,
                    };
                    return;
                }
            }
            catch (Exception error)
            {
                AdaptableLog.Warning("[JHYL_OUTLAW_NEST_CAPTURE_FAILED] " + error.GetType().Name);
            }
        }

        private static void Postfix(DataContext context, bool __result, Completion __state)
        {
            if (!__result || __state == null) return;
            try
            {
                OutlawNestCommissionProgress.Record(context, __state.TaiwuId, __state.Difficulty);
            }
            catch (Exception error)
            {
                // 记录失败不能打断本体通关，也不生成无法证明的奖励。
                AdaptableLog.Warning("[JHYL_OUTLAW_NEST_RECORD_FAILED] " + error.GetType().Name);
            }
        }
    }
}
