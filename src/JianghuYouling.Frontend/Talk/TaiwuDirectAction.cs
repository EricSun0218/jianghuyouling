using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using GameData.Domains.Item;
using JianghuYouling.Core.Tools;
using JianghuYouling.Effects;
using UnityEngine;

namespace JianghuYouling
{
    internal enum TaiwuDirectActionKind
    {
        GiveItem,
        Teach,
        WriteBook,
    }

    internal sealed class TaiwuDirectActionSelection
    {
        internal TaiwuDirectActionKind Kind;
        internal int TargetId;
        internal string TargetName;
        internal GiftableItem Item;
        internal LearnableSkill Skill;
        internal string SkillKind;
        internal int Amount = 1;
    }

    /// <summary>
    /// 界面按钮在模型调用前已经由代码真实执行的太吾行动。它是权威游戏回执，
    /// 不是让模型再次决定要不要执行的玩家请求。
    /// </summary>
    public sealed class TaiwuDirectActionReceipt
    {
        internal TaiwuDirectActionKind Kind;
        internal int TargetId;
        internal string TargetName;
        internal string SubjectName;
        internal string OperationId;
        internal bool Succeeded;
        internal bool Confirmed;
        internal string PlayerText;
        internal string ToolResultText;
        internal int BatchTotal;
        internal int BatchConfirmed;
        internal int BatchSucceeded;
        internal List<TaiwuDirectActionReceipt> Entries;

        internal string PromptInstruction()
        {
            int total = BatchTotal > 0 ? BatchTotal : 1;
            int confirmed = BatchTotal > 0 ? BatchConfirmed : (Confirmed ? 1 : 0);
            int succeeded = BatchTotal > 0 ? BatchSucceeded : (Succeeded ? 1 : 0);
            string state = confirmed < total
                ? ("已确认 " + confirmed + "/" + total + "，其中成功 " + succeeded
                    + "，其余回执仍待确认")
                : succeeded == total
                    ? "全部成功"
                    : succeeded > 0
                        ? ("部分成功（" + succeeded + "/" + total + "）")
                        : "全部失败";
            string executedTool = Kind == TaiwuDirectActionKind.GiveItem
                ? "taiwu_give_item"
                : Kind == TaiwuDirectActionKind.Teach
                    ? "taiwu_teach"
                    : "taiwu_write_book";
            return "【界面代码已执行的太吾主动行动 · 权威回执】\n"
                + (ToolResultText ?? "本轮行动结果不可用") + "\n"
                + "状态：" + state + "。这项行动发生在本轮模型调用之前，不得机械重复回执中"
                + "已经落地的同一批选择。"
                + "请以收件人的身份对这个真实结果自然回应；若回执失败或未确认，不得假称成功。"
                + "按钮只是增加玩家直选入口；包括 " + executedTool
                + " 在内的完整工具表都必须保留，仍可正常用于额外查询或新的合理行动。";
        }

        internal static TaiwuDirectActionReceipt Combine(
            IList<TaiwuDirectActionReceipt> receipts)
        {
            if (receipts == null || receipts.Count == 0) return null;
            var flattened = new List<TaiwuDirectActionReceipt>();
            foreach (TaiwuDirectActionReceipt receipt in receipts)
            {
                if (receipt == null) continue;
                if (receipt.Entries != null && receipt.Entries.Count > 0)
                {
                    foreach (TaiwuDirectActionReceipt entry in receipt.Entries)
                        if (entry != null) flattened.Add(entry);
                }
                else flattened.Add(receipt);
            }
            if (flattened.Count == 0) return null;
            TaiwuDirectActionReceipt first = null;
            var player = new StringBuilder();
            var results = new StringBuilder();
            var subjects = new List<string>();
            var operationIds = new List<string>();
            bool allConfirmed = true;
            bool allSucceeded = true;
            int total = 0;
            int confirmed = 0;
            int succeeded = 0;
            foreach (TaiwuDirectActionReceipt receipt in flattened)
            {
                if (receipt == null) continue;
                if (first == null) first = receipt;
                total++;
                if (receipt.Confirmed) confirmed++;
                if (receipt.Succeeded) succeeded++;
                if (!string.IsNullOrWhiteSpace(receipt.PlayerText))
                {
                    if (player.Length > 0) player.Append('\n');
                    player.Append(receipt.PlayerText.Trim());
                }
                if (!string.IsNullOrWhiteSpace(receipt.ToolResultText))
                {
                    if (results.Length > 0) results.Append('\n');
                    results.Append("- ").Append(receipt.ToolResultText.Trim());
                }
                if (!string.IsNullOrWhiteSpace(receipt.SubjectName))
                    subjects.Add(receipt.SubjectName.Trim());
                if (!string.IsNullOrWhiteSpace(receipt.OperationId))
                    operationIds.Add(receipt.OperationId.Trim());
                allConfirmed &= receipt.Confirmed;
                allSucceeded &= receipt.Succeeded;
            }
            if (first == null) return null;
            return new TaiwuDirectActionReceipt
            {
                Kind = first.Kind,
                TargetId = first.TargetId,
                TargetName = first.TargetName,
                SubjectName = string.Join("、", subjects.ToArray()),
                OperationId = string.Join(",", operationIds.ToArray()),
                Confirmed = allConfirmed,
                Succeeded = allSucceeded,
                BatchTotal = total,
                BatchConfirmed = confirmed,
                BatchSucceeded = succeeded,
                Entries = flattened,
                PlayerText = player.ToString(),
                ToolResultText = results.ToString(),
            };
        }

        internal List<int> TargetIds()
        {
            var result = new List<int>();
            if (Entries != null && Entries.Count > 0)
            {
                foreach (TaiwuDirectActionReceipt entry in Entries)
                    if (entry != null && entry.TargetId > 0 && !result.Contains(entry.TargetId))
                        result.Add(entry.TargetId);
            }
            else if (TargetId > 0) result.Add(TargetId);
            return result;
        }

        internal TaiwuDirectActionReceipt ForTarget(int targetId)
        {
            if (targetId <= 0) return null;
            if (Entries == null || Entries.Count == 0)
                return TargetId == targetId ? this : null;
            var matching = new List<TaiwuDirectActionReceipt>();
            foreach (TaiwuDirectActionReceipt entry in Entries)
                if (entry != null && entry.TargetId == targetId)
                    matching.Add(entry);
            return Combine(matching);
        }
    }

    internal static class TaiwuDirectActionExecutor
    {
        private const float MutationReceiptWaitSeconds = 16f;

        internal static IEnumerator Execute(int taiwuId, TaiwuDirectActionSelection selection,
            Action<TaiwuDirectActionReceipt> onDone)
        {
            if (selection == null || taiwuId <= 0 || selection.TargetId <= 0
                || selection.TargetId == taiwuId)
            {
                onDone?.Invoke(Failed(selection, "行动对象无效"));
                yield break;
            }

            int worldGeneration = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            if (!WorldLifecycle.IsSameWorld(worldGeneration) || worldId == 0)
            {
                onDone?.Invoke(Failed(selection, "当前不在有效存档中"));
                yield break;
            }

            bool contactResolved = false;
            bool remote = true;
            yield return ConversationContactModeResolver.Resolve(taiwuId, selection.TargetId,
                worldGeneration, CancellationToken.None,
                value => { remote = value; contactResolved = true; },
                null, EwReflect.HasTargetCharacterContext(selection.TargetId));
            if (!WorldLifecycle.IsSameWorld(worldGeneration))
            {
                onDone?.Invoke(Failed(selection, "已经切换存档，行动已取消"));
                yield break;
            }
            if (!contactResolved || remote)
            {
                onDone?.Invoke(Failed(selection,
                    "此刻只能千里传音，太吾无法当面交付物品、传授或赠书"));
                yield break;
            }

            string operationId = Guid.NewGuid().ToString("N");
            if (!EffectHandler.PrepareOperationIdentity(worldId, taiwuId, operationId))
            {
                onDone?.Invoke(Failed(selection, "未能建立可靠的行动回执"));
                yield break;
            }

            ToolOutcome outcome = null;
            if (!EffectHandler.ObserveOperationOutcome(operationId, value => outcome = value))
            {
                EffectHandler.DiscardPreparedOperation(operationId);
                onDone?.Invoke(Failed(selection, "未能订阅行动回执"));
                yield break;
            }

            int actualAmount = 0;
            bool businessOk = false;
            string businessMessage = null;
            string actualBookName = null;
            int lostPages = 0;
            try
            {
                switch (selection.Kind)
                {
                    case TaiwuDirectActionKind.GiveItem:
                        if (selection.Item == null)
                        {
                            EffectHandler.DiscardPreparedOperation(operationId);
                            EffectHandler.ForgetOperationOutcomeObserver(operationId);
                            onDone?.Invoke(Failed(selection, "没有选择要赠送的物品"));
                            yield break;
                        }
                        EffectHandler.ApplyTaiwuGiveItem(taiwuId, selection.TargetId,
                            selection.Item.Key, Math.Max(1, selection.Amount),
                            actual =>
                            {
                                actualAmount = actual;
                                businessOk = actual > 0;
                            }, operationId);
                        break;
                    case TaiwuDirectActionKind.Teach:
                        if (selection.Skill == null)
                        {
                            EffectHandler.DiscardPreparedOperation(operationId);
                            EffectHandler.ForgetOperationOutcomeObserver(operationId);
                            onDone?.Invoke(Failed(selection, "没有选择要传授的武学或技艺"));
                            yield break;
                        }
                        EffectHandler.ApplyTaiwuTeach(taiwuId, selection.TargetId,
                            selection.SkillKind ?? "combat", selection.Skill.TemplateId,
                            (ok, message) =>
                            {
                                businessOk = ok;
                                businessMessage = message;
                            }, operationId);
                        break;
                    case TaiwuDirectActionKind.WriteBook:
                        if (selection.Skill == null)
                        {
                            EffectHandler.DiscardPreparedOperation(operationId);
                            EffectHandler.ForgetOperationOutcomeObserver(operationId);
                            onDone?.Invoke(Failed(selection, "没有选择要回忆成书的武学或技艺"));
                            yield break;
                        }
                        EffectHandler.ApplyWriteBook(taiwuId, taiwuId,
                            selection.SkillKind ?? "combat", selection.Skill.TemplateId,
                            (ok, bookName, lost) =>
                            {
                                businessOk = ok;
                                actualBookName = bookName;
                                lostPages = lost;
                                if (!ok) businessMessage = bookName;
                            }, selection.TargetId, operationId);
                        break;
                }
            }
            catch (Exception exception)
            {
                EffectHandler.DiscardPreparedOperation(operationId);
                EffectHandler.ForgetOperationOutcomeObserver(operationId);
                onDone?.Invoke(Failed(selection, "行动派发异常：" + exception.GetType().Name));
                yield break;
            }

            float deadline = Time.unscaledTime + MutationReceiptWaitSeconds;
            while (outcome == null && Time.unscaledTime < deadline
                && WorldLifecycle.IsSameWorld(worldGeneration))
                yield return null;

            if (outcome == null && WorldLifecycle.IsSameWorld(worldGeneration))
            {
                bool queryDone = false;
                EffectHandler.QueryOperation(worldId, taiwuId, operationId,
                    value => { outcome = value; queryDone = true; });
                float queryDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
                while (!queryDone && Time.unscaledTime < queryDeadline
                    && WorldLifecycle.IsSameWorld(worldGeneration))
                    yield return null;
            }

            EffectHandler.ForgetOperationOutcomeObserver(operationId);
            if (!WorldLifecycle.IsSameWorld(worldGeneration))
            {
                onDone?.Invoke(Failed(selection, "已经切换存档，行动结果不再写入当前聊天"));
                yield break;
            }

            if (outcome == null)
                outcome = ToolOutcome.Unknown(operationId, "后端行动回执暂未返回");
            bool confirmed = outcome.IsTerminal;
            bool succeeded = confirmed && outcome.IsSucceeded && businessOk;
            string reason = !string.IsNullOrWhiteSpace(businessMessage)
                ? businessMessage.Trim()
                : !string.IsNullOrWhiteSpace(outcome.Message)
                    ? outcome.Message.Trim()
                    : outcome.Code;

            TaiwuDirectActionReceipt receipt = BuildReceipt(selection, operationId,
                succeeded, confirmed, actualAmount, actualBookName, lostPages, reason);
            if (confirmed) EffectHandler.AcknowledgeOperation(worldId, taiwuId, operationId);
            onDone?.Invoke(receipt);
        }

        private static TaiwuDirectActionReceipt BuildReceipt(TaiwuDirectActionSelection selection,
            string operationId, bool succeeded, bool confirmed, int actualAmount,
            string actualBookName, int lostPages, string reason)
        {
            string target = SafeTarget(selection);
            string subject = selection.Kind == TaiwuDirectActionKind.GiveItem
                ? (selection.Item?.Name ?? "所选物品")
                : (selection.Skill?.Name ?? "所选本事");
            string playerText;
            string result;
            if (!confirmed)
            {
                playerText = "（太吾对" + target + "发起了"
                    + ActionLabel(selection.Kind) + "，后端回执仍在确认中。）";
                result = "太吾主动" + ActionLabel(selection.Kind) + "「" + subject
                    + "」给" + target + "：回执尚未确认，不能断言成功或失败";
            }
            else if (!succeeded)
            {
                playerText = "（太吾尝试向" + target + ActionSentence(selection.Kind, subject)
                    + "，但没有成功。）";
                result = "太吾主动" + ActionLabel(selection.Kind) + "「" + subject
                    + "」给" + target + "失败：" + (string.IsNullOrWhiteSpace(reason) ? "原因不明" : reason);
            }
            else if (selection.Kind == TaiwuDirectActionKind.GiveItem)
            {
                int count = actualAmount > 0 ? actualAmount : Math.Max(1, selection.Amount);
                string amount = count > 1 ? " ×" + count : "";
                playerText = "（太吾将「" + subject + "」" + amount + "赠给了" + target + "。）";
                result = "太吾已将「" + subject + "」" + amount + "真实赠给" + target;
            }
            else if (selection.Kind == TaiwuDirectActionKind.Teach)
            {
                playerText = "（太吾亲自把「" + subject + "」传授给了" + target + "。）";
                result = "太吾已将" + SkillKindLabel(selection.SkillKind) + "「" + subject
                    + "」真实传授给" + target;
            }
            else
            {
                string book = string.IsNullOrWhiteSpace(actualBookName) ? subject : actualBookName.Trim();
                string pages = lostPages > 0 ? "（残缺 " + lostPages + " 页）" : "（书页完整）";
                playerText = "（太吾回忆「" + subject + "」写成《" + book + "》，赠给了"
                    + target + pages + "。）";
                result = "太吾已把" + SkillKindLabel(selection.SkillKind) + "「" + subject
                    + "」回忆成《" + book + "》并真实赠给" + target + pages;
            }

            return new TaiwuDirectActionReceipt
            {
                Kind = selection.Kind,
                TargetId = selection.TargetId,
                TargetName = target,
                SubjectName = subject,
                OperationId = operationId,
                Succeeded = succeeded,
                Confirmed = confirmed,
                PlayerText = playerText,
                ToolResultText = result,
            };
        }

        private static TaiwuDirectActionReceipt Failed(TaiwuDirectActionSelection selection,
            string reason)
        {
            string target = SafeTarget(selection);
            TaiwuDirectActionKind kind = selection?.Kind ?? TaiwuDirectActionKind.GiveItem;
            return new TaiwuDirectActionReceipt
            {
                Kind = kind,
                TargetId = selection?.TargetId ?? 0,
                TargetName = target,
                SubjectName = selection?.Item?.Name ?? selection?.Skill?.Name,
                Confirmed = true,
                Succeeded = false,
                PlayerText = "（太吾尝试向" + target + "进行" + ActionLabel(kind)
                    + "，但没有成功。）",
                ToolResultText = "太吾主动" + ActionLabel(kind) + "失败：" + reason,
            };
        }

        private static string SafeTarget(TaiwuDirectActionSelection selection)
            => string.IsNullOrWhiteSpace(selection?.TargetName)
                ? ("江湖人#" + (selection?.TargetId ?? 0))
                : selection.TargetName.Trim();

        private static string ActionLabel(TaiwuDirectActionKind kind)
            => kind == TaiwuDirectActionKind.GiveItem ? "赠物"
                : kind == TaiwuDirectActionKind.Teach ? "传授" : "写书赠送";

        private static string ActionSentence(TaiwuDirectActionKind kind, string subject)
            => kind == TaiwuDirectActionKind.GiveItem ? "赠送「" + subject + "」"
                : kind == TaiwuDirectActionKind.Teach ? "传授「" + subject + "」"
                : "把「" + subject + "」回忆成书赠送";

        private static string SkillKindLabel(string kind)
            => string.Equals(kind, "life", StringComparison.OrdinalIgnoreCase) ? "技艺" : "武学";
    }
}
