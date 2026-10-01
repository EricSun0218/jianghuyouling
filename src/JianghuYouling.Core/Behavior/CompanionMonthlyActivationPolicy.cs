using System;
using System.Collections.Generic;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// Pure, deterministic activation policy for companion monthly planning.  Keeping this
    /// outside Unity makes the expensive planner's wake-up rules executable in offline tests.
    /// </summary>
    public static class CompanionMonthlyActivationPolicy
    {
        private static readonly string[] RequestMarkers =
            { "请你", "拜托你", "拜托", "帮我", "替我", "务必", "一定要", "必须", "别忘", "记住" };

        private static readonly string[] FutureMarkers =
            { "下月", "下个月", "来日", "日后", "以后", "改日", "将来", "有空时", "届时", "回头" };

        // Deliberately executable phrases, not broad nouns such as “功法” or one-character “教”.
        private static readonly string[] ActionPhrases =
        {
            "送我", "赠我", "给我", "送你", "赠你", "给你", "教我", "传我", "教你", "传你", "传授", "传功", "结义", "结交", "成婚",
            "杀掉", "杀死", "下毒", "毒死", "绑走", "绑架", "擒住", "偷来", "偷走", "去做", "行动"
        };

        private static readonly string[] PromiseMarkers = { "答应", "承诺", "约定" };
        private static readonly string[] InformationQuestionMarkers =
        {
            "来历", "是什么", "为何", "为什么", "怎么", "如何", "哪里", "哪一", "哪门", "哪本",
            "告诉我", "解释", "说说", "讲讲", "请教"
        };

        private static readonly string[] CompletionMarkers =
            { "亲手做了", "已确认完成", "已经完成", "已兑现", "已经兑现", "已履行", "已经履行", "已办妥" };

        private sealed class ActionCategory
        {
            internal readonly string Name;
            internal readonly string[] Terms;

            internal ActionCategory(string name, params string[] terms)
            {
                Name = name;
                Terms = terms;
            }
        }

        private static readonly ActionCategory[] Categories =
        {
            new ActionCategory("gift", "赠", "送", "给我", "银钱", "物品"),
            new ActionCategory("teach", "教我", "传我", "传授", "传功", "授艺", "功法", "技艺"),
            new ActionCategory("relate", "结义", "结交", "成婚", "结缘", "挚友"),
            new ActionCategory("kill", "杀掉", "杀死", "取命", "身死", "毙命"),
            new ActionCategory("poison", "下毒", "毒死", "毒发"),
            new ActionCategory("capture", "绑走", "绑架", "擒住", "擒下", "掳走"),
            new ActionCategory("steal", "偷来", "偷走", "偷窃", "窃得"),
            new ActionCategory("enmity", "结仇", "仇怨", "恩怨", "化解旧怨", "和解"),
            new ActionCategory("feature", "性情", "品性", "特质"),
            new ActionCategory("practice", "逆练", "正练", "运功")
        };

        public static bool HasOutstandingDialogueActionIntent(IReadOnlyList<TalkTurn> turns, int dialogueLimit = 6)
        {
            if (turns == null || turns.Count == 0) return false;
            int start = Math.Max(0, turns.Count - Math.Max(1, dialogueLimit) * 2);
            for (int i = start; i < turns.Count; i++)
            {
                TalkTurn turn = turns[i];
                if (turn == null || TalkTurnKinds.IsNative(turn) || string.IsNullOrWhiteSpace(turn.Text)) continue;
                string text = turn.Text;
                bool actionable = HasAny(text, ActionPhrases);
                bool future = HasAny(text, FutureMarkers);
                bool requested = turn.FromPlayer && actionable
                    && (HasAny(text, RequestMarkers) || future)
                    && !(LooksLikeInformationQuestion(text) && !future);
                bool promised = !turn.FromPlayer && actionable && HasAny(text, PromiseMarkers);
                if (!requested && !promised) continue;
                if (!ExchangeHasConfirmedAction(turns, i, turn)) return true;
            }
            return false;
        }

        public static bool HasLongTermActivation(IReadOnlyList<MemoryEntry> memories, long date, int recentMonths = 2)
        {
            if (memories == null) return false;
            for (int i = 0; i < memories.Count; i++)
            {
                MemoryEntry memory = memories[i];
                if (memory == null || !memory.Valid) continue;
                if (memory.Core && (memory.Type == MemoryType.Promise || memory.Type == MemoryType.Grudge))
                {
                    // A vague, category-free old sentence cannot wake a costly planner forever.
                    // Explicit unresolved promises/grudges remain durable; a later authoritative
                    // action memory with the same action category settles only that intent.
                    var categories = CategoriesFor(memory.Content);
                    if (categories.Count > 0 && !SettledByLaterEvidence(memories, memory, categories)) return true;
                    if (IsRecent(memory.WorldDate, date, recentMonths)) return true;
                    continue;
                }
                if ((memory.IsCore || memory.Importance >= 8) && IsRecent(memory.WorldDate, date, recentMonths))
                    return true;
            }
            return false;
        }

        private static bool ExchangeHasConfirmedAction(IReadOnlyList<TalkTurn> turns, int index, TalkTurn intent)
        {
            bool hasExchangeId = !string.IsNullOrWhiteSpace(intent.ExchangeId);
            var intendedCategories = CategoriesFor(intent.Text);
            int end = hasExchangeId ? turns.Count : Math.Min(turns.Count, index + 2);
            for (int j = index; j < end; j++)
            {
                TalkTurn candidate = turns[j];
                if (candidate == null) continue;
                if (hasExchangeId)
                {
                    if (!string.Equals(candidate.ExchangeId, intent.ExchangeId, StringComparison.Ordinal))
                    {
                        if (j > index) break;
                        continue;
                    }
                }
                else if (candidate.Date != intent.Date) continue;
                if (candidate.Actions == null || candidate.Actions.Count == 0) continue;
                if (intendedCategories.Count == 0) return true;
                foreach (string action in candidate.Actions)
                {
                    var completedCategories = CategoriesFor(action);
                    foreach (string category in completedCategories)
                        if (intendedCategories.Contains(category)) return true;
                }
            }
            return false;
        }

        private static bool LooksLikeInformationQuestion(string text)
            => HasAny(text, InformationQuestionMarkers);

        private static bool SettledByLaterEvidence(IReadOnlyList<MemoryEntry> memories, MemoryEntry intent,
            ISet<string> intentCategories)
        {
            for (int i = 0; i < memories.Count; i++)
            {
                MemoryEntry evidence = memories[i];
                if (evidence == null || !evidence.Valid || ReferenceEquals(evidence, intent)
                    || evidence.WorldDate < intent.WorldDate || !HasAny(evidence.Content, CompletionMarkers)) continue;
                var completed = CategoriesFor(evidence.Content);
                foreach (string category in completed)
                    if (intentCategories.Contains(category)) return true;
            }
            return false;
        }

        private static HashSet<string> CategoriesFor(string text)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (ActionCategory category in Categories)
                if (HasAny(text, category.Terms)) result.Add(category.Name);
            return result;
        }

        private static bool IsRecent(long worldDate, long date, int recentMonths)
            => worldDate > 0 && date >= worldDate && date - worldDate <= Math.Max(0, recentMonths);

        private static bool HasAny(string text, IEnumerable<string> phrases)
        {
            if (string.IsNullOrWhiteSpace(text) || phrases == null) return false;
            foreach (string phrase in phrases)
                if (!string.IsNullOrEmpty(phrase) && text.IndexOf(phrase, StringComparison.Ordinal) >= 0)
                    return true;
            return false;
        }
    }
}
