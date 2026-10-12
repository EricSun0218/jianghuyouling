using System;
using System.Collections.Generic;
using System.IO;
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Persona;
using JianghuYouling.Effects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 固定模板人物的前端身份路由。当前后端存档是身份权威；本地文件只记录历史资料绑定。
    /// 显示与开窗先只读核验归属，实际发言再完成必要的副本创建与资料准备。
    /// </summary>
    internal static class CharacterProxyIdentityService
    {
        private const int SchemaVersion = 2;
        private const int MaxEntries = 100000;
        private const int MaxFileBytes = 8 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, List<Action<int, string>>> Pending =
            new Dictionary<string, List<Action<int, string>>>(StringComparer.Ordinal);
        private static string _scope;
        private static int _taiwuId;
        private static Dictionary<int, int> _resolved = new Dictionary<int, int>();
        private static HashSet<int> _reconciledOriginals = new HashSet<int>();
        private static Dictionary<int, short> _displayTemplates = new Dictionary<int, short>();
        private static Dictionary<int, int> _copyDates = new Dictionary<int, int>();
        // Disk mappings describe where content was saved, not who owns an id in this load.
        private static readonly Dictionary<int, CharacterProxyIdentity> Verified =
            new Dictionary<int, CharacterProxyIdentity>();
        private static readonly Dictionary<int, List<Action<CharacterProxyIdentity, string>>> ReadPending =
            new Dictionary<int, List<Action<CharacterProxyIdentity, string>>>();

        private sealed class State
        {
            public int Version = SchemaVersion;
            public uint WorldId;
            public int TaiwuId;
            public Dictionary<int, int> Resolved = new Dictionary<int, int>();
            public Dictionary<int, short> DisplayTemplates = new Dictionary<int, short>();
            public Dictionary<int, int> CopyDates = new Dictionary<int, int>();
        }

        internal static int ResolveKnown(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId < 0) return npcId;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                return Verified.TryGetValue(npcId, out CharacterProxyIdentity identity)
                    && identity.IsValidFor(taiwuId) ? identity.ResolvedId : npcId;
            }
        }

        internal static bool IsKnownProxy(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId < 0) return false;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                return Verified.TryGetValue(npcId, out CharacterProxyIdentity identity)
                    && identity.IsValidFor(taiwuId) && identity.IsProxy
                    && identity.ResolvedId == npcId;
            }
        }

        internal static bool TryGetDisplayTemplate(int taiwuId, int npcId,
            out short templateId)
        {
            templateId = -1;
            if (taiwuId <= 0 || npcId < 0) return false;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                if (!Verified.TryGetValue(npcId, out CharacterProxyIdentity identity)
                    || !identity.IsValidFor(taiwuId) || !identity.IsProxy)
                    return false;
                templateId = identity.DisplayTemplateId;
                // The story anchor may have left the live object table. Its cached skin is
                // usable only after the backend proves the exact original/proxy ownership.
                if (templateId < 0 && _resolved.TryGetValue(identity.OriginalId, out int storedId)
                    && storedId == identity.ResolvedId && ContentIdentityMatches(identity)
                    && _displayTemplates.TryGetValue(identity.ResolvedId, out short storedTemplate))
                    templateId = storedTemplate;
                return templateId >= 0;
            }
        }

        internal static bool IsIdentityReady(int taiwuId, int requestedId)
        {
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                return Verified.TryGetValue(requestedId, out CharacterProxyIdentity identity)
                    && identity.IsValidFor(taiwuId) && !identity.RequiresPreparation
                    && ContentIdentityMatches(identity)
                    && (!identity.IsProxy || (_resolved.TryGetValue(identity.OriginalId,
                        out int storedId) && storedId == identity.ResolvedId));
            }
        }

        internal static bool HasRecordedProxyIdentity(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId <= 0 || JianghuYoulingPaths.CurrentWorldId == 0) return false;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                return (_resolved.TryGetValue(npcId, out int target) && target != npcId)
                    || _displayTemplates.ContainsKey(npcId) || _copyDates.ContainsKey(npcId);
            }
        }

        // A reused destination must never inherit another person's chat, portrait or memory.
        // Ambiguous old content stays on disk; only a current authoritative alias can share it.
        private static bool ContentIdentityMatches(CharacterProxyIdentity identity)
        {
            bool recordedOwner = false;
            foreach (KeyValuePair<int, int> pair in _resolved)
            {
                if (pair.Key == pair.Value || pair.Value != identity.ResolvedId) continue;
                recordedOwner = true;
                if (pair.Key != identity.OriginalId
                    && (!Verified.TryGetValue(pair.Key, out CharacterProxyIdentity owner)
                        || owner.OriginalId != identity.OriginalId
                        || owner.ResolvedId != identity.ResolvedId))
                    return false;
            }
            return recordedOwner || (!_displayTemplates.ContainsKey(identity.ResolvedId)
                && !_copyDates.ContainsKey(identity.ResolvedId));
        }

        private static bool CanMigrateContent(CharacterProxyIdentity identity, int fromId)
            => fromId == identity.ResolvedId || ContentIdentityMatches(new CharacterProxyIdentity
            { OriginalId = identity.OriginalId, ResolvedId = fromId });

        internal static void EnsureForDisplay(int taiwuId, int npcId, Action<int, string> completed)
        {
            if (taiwuId <= 0 || npcId <= 0 || JianghuYoulingPaths.CurrentWorldId == 0)
            { completed?.Invoke(npcId, "人物或存档身份尚未就绪"); return; }
            Action<CharacterProxyIdentity, string> finish = (identity, error) =>
            {
                lock (Gate)
                {
                    if (error == null && (identity == null || !ContentIdentityMatches(identity)))
                        error = "人物编号与旧副本资料冲突，已停止串用；原聊天与记忆均已保留";
                }
                if (error == null && !ArchivedCharacterStatusStore.ApplyAuthoritative(taiwuId,
                    identity.IsDead ? null : new[] { identity.ResolvedId },
                    identity.IsDead ? new[] { identity.ResolvedId } : null, out _))
                    error = "当前人物生死状态未能可靠同步，请稍后重试";
                completed?.Invoke(error == null ? identity.ResolvedId : npcId, error);
            };
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                if (Verified.TryGetValue(npcId, out CharacterProxyIdentity identity))
                { finish(identity, null); return; }
                if (ReadPending.TryGetValue(npcId, out var callbacks))
                { callbacks.Add(finish); return; }
                ReadPending[npcId] = new List<Action<CharacterProxyIdentity, string>> { finish };
            }
            ReadBatch(taiwuId, new List<int> { npcId }, null);
        }

        internal static void Prewarm(int taiwuId)
        {
            if (taiwuId <= 0 || JianghuYoulingPaths.CurrentWorldId == 0) return;
            var ids = new List<int>();
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                var seen = new HashSet<int>();
                foreach (KeyValuePair<int, int> pair in _resolved)
                {
                    if (pair.Key > 0 && seen.Add(pair.Key)) ids.Add(pair.Key);
                    if (pair.Value > 0 && seen.Add(pair.Value)) ids.Add(pair.Value);
                }
            }
            int generation = WorldLifecycle.Generation;
            uint worldId = JianghuYoulingPaths.CurrentWorldId;
            int offset = 0;
            Action next = null;
            next = () =>
            {
                if (!WorldLifecycle.IsSameWorld(generation)
                    || worldId != JianghuYoulingPaths.CurrentWorldId) return;
                var batch = new List<int>();
                lock (Gate)
                {
                    while (offset < ids.Count && batch.Count < CharacterProxyIdentity.MaxQueryBatch)
                    {
                        int id = ids[offset++];
                        if (Verified.ContainsKey(id) || ReadPending.ContainsKey(id)) continue;
                        ReadPending[id] = new List<Action<CharacterProxyIdentity, string>>();
                        batch.Add(id);
                    }
                }
                if (batch.Count > 0) ReadBatch(taiwuId, batch, ok => { if (ok) next(); });
            };
            next();
        }

        private static void ReadBatch(int taiwuId, List<int> ids, Action<bool> completed)
        {
            int generation = WorldLifecycle.Generation;
            uint worldId = JianghuYoulingPaths.CurrentWorldId;
            Action<List<CharacterProxyIdentity>, string> receive = (identities, error) =>
            {
                if (!WorldLifecycle.IsSameWorld(generation)
                    || worldId != JianghuYoulingPaths.CurrentWorldId || _taiwuId != taiwuId)
                    return;
                var callbacks = new List<Action>();
                lock (Gate)
                {
                    if (error == null && (identities == null || identities.Count != ids.Count))
                        error = "人物身份查询结果不完整";
                    for (int index = 0; index < ids.Count; index++)
                    {
                        int requestedId = ids[index];
                        CharacterProxyIdentity identity = error == null ? identities[index] : null;
                        string rowError = error;
                        if (rowError == null && (identity == null || identity.RequestedId != requestedId
                            || !identity.IsValidFor(taiwuId)))
                            rowError = string.IsNullOrWhiteSpace(identity?.Error)
                                ? "后端人物身份不一致，未启用映射" : identity.Error;
                        if (rowError == null && !AcceptVerified(taiwuId, identity))
                            rowError = "当前人物副本归属冲突，未覆盖已有身份";
                        if (ReadPending.TryGetValue(requestedId, out var waiters))
                        {
                            ReadPending.Remove(requestedId);
                            foreach (var waiter in waiters)
                                if (waiter != null) callbacks.Add(() => waiter(identity, rowError));
                        }
                    }
                }
                foreach (Action callback in callbacks)
                    try { callback(); } catch { }
                completed?.Invoke(error == null);
            };
            try { EffectHandler.QueryCharacterProxyIdentities(ids, receive); }
            catch (Exception e) { receive(null, "人物身份查询异常:" + e.GetType().Name); }
        }

        private static bool AcceptVerified(int taiwuId, CharacterProxyIdentity identity)
        {
            if (identity == null || !identity.IsValidFor(taiwuId)) return false;
            if (!CanAcceptVerified(identity)) return false;
            Verified[identity.RequestedId] = identity;
            // These aliases are certified by the same backend observation, not by local id chains.
            foreach (int id in new[] { identity.OriginalId, identity.ResolvedId })
                Verified[id] = new CharacterProxyIdentity
                {
                    RequestedId = id, OriginalId = identity.OriginalId, ResolvedId = identity.ResolvedId,
                    DisplayTemplateId = identity.DisplayTemplateId, IsProxy = identity.IsProxy,
                    RequiresPreparation = identity.RequiresPreparation, IsDead = identity.IsDead,
                };
            return true;
        }

        private static bool CanAcceptVerified(CharacterProxyIdentity identity)
            => !Verified.TryGetValue(identity.ResolvedId, out CharacterProxyIdentity previous)
                || previous.OriginalId == identity.OriginalId;

        internal static List<int> NormalizeKnownIds(int taiwuId, IEnumerable<int> ids,
            bool includeTaiwu = false)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            if (ids == null) return result;
            foreach (int id in ids)
            {
                int resolved = ResolveKnown(taiwuId, id);
                if (resolved > 0 && (includeTaiwu || resolved != taiwuId) && seen.Add(resolved))
                    result.Add(resolved);
            }
            return result;
        }

        internal static void EnsureForConversation(int taiwuId, int npcId,
            Action<int, string> completed)
        {
            if (taiwuId <= 0 || npcId < 0)
            { completed?.Invoke(npcId, "人物身份无效"); return; }
            int known = ResolveKnown(taiwuId, npcId);
            bool knownValidated = IsIdentityReady(taiwuId, npcId);
            if (knownValidated)
            {
                if (known == npcId)
                { completed?.Invoke(known, null); return; }
                bool reconcile;
                lock (Gate) reconcile = !_reconciledOriginals.Contains(npcId);
                // 映射可能来自上次运行：把旧版本可能残留的原模板会话、导航、人设与记忆
                // 再幂等归并一次；同一运行只做一次，之后仅淘汰 UI 缓存中的旧行。
                if (reconcile && !PrepareIdentityContent(taiwuId, npcId, known))
                {
                    completed?.Invoke(npcId, "既有人物副本资料未能完整归并；请稍后重试");
                    return;
                }
                if (reconcile)
                {
                    if (ReconcileIdentityState(taiwuId, npcId, known))
                    {
                        FinalizeIdentityMigration(taiwuId, npcId);
                        lock (Gate) _reconciledOriginals.Add(npcId);
                    }
                    else
                    {
                        Debug.LogWarning("[江湖有灵] 人物副本状态归并尚未完成，将在下次开窗重试 "
                            + npcId + "→" + known);
                        completed?.Invoke(npcId, "人物副本状态归并尚未完成，请稍后重试");
                        return;
                    }
                }
                ChatWindow.NotifyConversationIdentityMigrated(taiwuId, npcId, known);
                completed?.Invoke(known, null);
                return;
            }

            // 本地映射只说明聊天资料上次归到了谁，游戏存档里的后端映射和人物实体才是
            // 当前权威。新进程第一次校验必须把玩家实际点到的原人物交给后端；若读档、
            // 阶段切换或存档回滚使旧副本不再存在，直接发送 known 会把原人物路由机会一并
            // 丢掉，只能得到“人物已失效”。后端返回后再把本地旧身份归并到权威结果。
            int backendNpcId = npcId;

            uint worldId = JianghuYoulingPaths.CurrentWorldId;
            int generation = WorldLifecycle.Generation;
            string pendingKey = generation + ":" + worldId + ":" + taiwuId + ":" + backendNpcId;
            lock (Gate)
            {
                if (Pending.TryGetValue(pendingKey, out List<Action<int, string>> waiters))
                {
                    waiters.Add(completed);
                    return;
                }
                Pending[pendingKey] = new List<Action<int, string>> { completed };
            }

            EffectHandler.EnsureCharacterProxy(backendNpcId,
                (ok, originalId, resolvedId, created, isProxy, migrationFromId,
                    displayTemplateId, message) =>
                {
                    string error = null;
                    int result = npcId;
                    try
                    {
                        if (!WorldLifecycle.IsSameWorld(generation)
                            || JianghuYoulingPaths.CurrentWorldId != worldId)
                            error = "存档已经切换，本次人物副本准备已取消";
                        else if (!ok || resolvedId <= 0)
                            error = string.IsNullOrWhiteSpace(message) ? "人物身份解析失败" : message;
                        var identity = new CharacterProxyIdentity
                        {
                            RequestedId = npcId, OriginalId = originalId, ResolvedId = resolvedId,
                            DisplayTemplateId = displayTemplateId, IsProxy = isProxy,
                        };
                        lock (Gate)
                        {
                            if (error == null && (!identity.IsValidFor(taiwuId)
                                || !CanAcceptVerified(identity) || !ContentIdentityMatches(identity)))
                                error = "人物副本归属与已有资料冲突，未搬移或覆盖聊天、记忆与立绘";
                        }
                        // 只有本次后端确实新建的副本才有可证明的复制月份。既有副本不能
                        // 用“本次重新校验月份”冒充创建月份，否则读回更早但仍包含该副本
                        // 的存档时会误删皮肤映射。
                        int copyDate = isProxy && created ? CurrentWorldDate() : -1;
                        if (error == null && isProxy && created && copyDate < 0)
                            error = "当前游戏时间尚未就绪，暂不能建立人物副本";
                        int stableOriginalId = originalId > 0 ? originalId : npcId;
                        int migrationSource = migrationFromId > 0
                            ? migrationFromId
                            : (resolvedId != backendNpcId ? backendNpcId : npcId);
                        // Only the backend's explicit source/replacement relation authorizes
                        // content migration. A stale local destination may now be someone else.
                        lock (Gate)
                        {
                            if (error == null && (!CanMigrateContent(identity, migrationSource)
                                || !CanMigrateContent(identity, stableOriginalId)
                                || !CanMigrateContent(identity, npcId)))
                                error = "来源编号下存在其他人物的旧资料，未自动归并或删除";
                        }
                        if (error == null && resolvedId != migrationSource
                            && !PrepareIdentityContent(taiwuId, migrationSource, resolvedId))
                            error = "人物副本已建立，但聊天、人设或记忆尚未完整迁移；请稍后重试";
                        else if (error == null && stableOriginalId != migrationSource
                            && stableOriginalId != resolvedId
                            && !PrepareIdentityContent(taiwuId, stableOriginalId, resolvedId))
                            error = "人物稳定身份资料尚未完整归并；请稍后重试";
                        else if (error == null && npcId != migrationSource
                            && npcId != stableOriginalId
                            && npcId != resolvedId
                            && !PrepareIdentityContent(taiwuId, npcId, resolvedId))
                            error = "人物原始身份资料尚未完整归并；请稍后重试";
                        else if (error == null && !Register(taiwuId, stableOriginalId, resolvedId,
                            displayTemplateId, isProxy, copyDate))
                            error = "人物资料已准备，但副本身份缓存未能写入；请稍后重试";
                        else if (error == null)
                        {
                            result = resolvedId;
                            bool reconciled = true;
                            if (migrationSource != resolvedId)
                                reconciled = ReconcileIdentityState(taiwuId,
                                    migrationSource, resolvedId);
                            if (reconciled && stableOriginalId != migrationSource
                                && stableOriginalId != resolvedId)
                                reconciled = ReconcileIdentityState(taiwuId,
                                    stableOriginalId, resolvedId);
                            if (reconciled && npcId != migrationSource
                                && npcId != stableOriginalId && npcId != resolvedId)
                                reconciled = ReconcileIdentityState(taiwuId, npcId, resolvedId);
                            if (reconciled)
                            {
                                if (migrationSource != resolvedId)
                                    FinalizeIdentityMigration(taiwuId, migrationSource);
                                if (stableOriginalId != migrationSource
                                    && stableOriginalId != resolvedId)
                                    FinalizeIdentityMigration(taiwuId, stableOriginalId);
                                if (npcId != migrationSource && npcId != stableOriginalId
                                    && npcId != resolvedId)
                                    FinalizeIdentityMigration(taiwuId, npcId);
                                lock (Gate)
                                {
                                    _reconciledOriginals.Add(npcId);
                                    _reconciledOriginals.Add(stableOriginalId);
                                    AcceptVerified(taiwuId, identity);
                                }
                            }
                            else
                            {
                                error = "人物副本状态归并尚未完成，请稍后重试";
                                Debug.LogWarning("[江湖有灵] 人物副本状态归并尚未完成，将在下次开窗重试 "
                                    + migrationSource + "→" + resolvedId);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        error = "人物副本资料迁移异常:" + e.GetType().Name;
                    }

                    List<Action<int, string>> callbacks;
                    lock (Gate)
                    {
                        if (!Pending.TryGetValue(pendingKey, out callbacks))
                            callbacks = new List<Action<int, string>>();
                        Pending.Remove(pendingKey);
                    }
                    foreach (Action<int, string> callback in callbacks)
                        try { callback?.Invoke(error == null ? result : npcId, error); } catch { }
                    if (error != null)
                        Debug.LogWarning("[江湖有灵] 首次聊天人物副本准备失败 npc=" + npcId
                            + " resolved=" + resolvedId + " reason=" + error);
                    else if (created)
                        Debug.Log("[江湖有灵] 固定模板人物首次聊天已建立永久副本 "
                            + npcId + "→" + resolvedId);
                });
        }

        internal static void ResetForWorldExit()
        {
            List<Action<int, string>> callbacks = new List<Action<int, string>>();
            var readers = new List<Action<CharacterProxyIdentity, string>>();
            lock (Gate)
            {
                foreach (List<Action<int, string>> waiters in Pending.Values)
                    if (waiters != null) callbacks.AddRange(waiters);
                Pending.Clear();
                foreach (var waiters in ReadPending.Values) readers.AddRange(waiters);
                ReadPending.Clear();
                Verified.Clear();
                _scope = null;
                _taiwuId = 0;
                _resolved = new Dictionary<int, int>();
                _reconciledOriginals = new HashSet<int>();
                _displayTemplates = new Dictionary<int, short>();
                _copyDates = new Dictionary<int, int>();
            }
            foreach (Action<int, string> callback in callbacks)
                try { callback?.Invoke(-1, "存档已经切换，本次人物副本准备已取消"); } catch { }
            foreach (var reader in readers)
                try { reader?.Invoke(null, "存档已经切换，本次人物身份核验已取消"); } catch { }
        }

        internal static void EnsureRoster(int taiwuId, IList<KeyValuePair<int, string>> roster,
            Action<List<KeyValuePair<int, string>>, string> completed)
        {
            var source = roster == null
                ? new List<KeyValuePair<int, string>>()
                : new List<KeyValuePair<int, string>>(roster);
            var result = new List<KeyValuePair<int, string>>();
            var seen = new HashSet<int>();
            int index = 0;
            Action next = null;
            next = () =>
            {
                if (index >= source.Count)
                { completed?.Invoke(result, null); return; }
                KeyValuePair<int, string> member = source[index++];
                EnsureForConversation(taiwuId, member.Key, (resolvedId, error) =>
                {
                    if (error != null)
                    { completed?.Invoke(null, error); return; }
                    if (resolvedId > 0 && resolvedId != taiwuId && seen.Add(resolvedId))
                        result.Add(new KeyValuePair<int, string>(resolvedId, member.Value));
                    next();
                });
            };
            next();
        }

        private static bool PrepareIdentityContent(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (oldNpcId == newNpcId) return true;
            // 第一阶段只复制核心资料，绝不清理源身份。这样画像或任一后续状态写入失败时，
            // 前端仍能安全退回旧人物，不会显示空白人设或丢失聊天。
            if (!TalkOrchestrator.MigrateConversationIdentity(taiwuId, oldNpcId, newNpcId)) return false;
            if (!MigrateMemory(taiwuId, oldNpcId, newNpcId)) return false;
            if (!MigratePersona(taiwuId, oldNpcId, newNpcId)) return false;
            if (!PortraitStore.ReplaceIdentity(taiwuId, oldNpcId, newNpcId)) return false;
            return true;
        }

        private static bool ReconcileIdentityState(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (oldNpcId == newNpcId) return true;
            if (!GroupChatOrchestrator.MigrateMemberIdentity(taiwuId, oldNpcId, newNpcId)) return false;
            if (!CompanionMonthlyCandidateStore.ReplaceIdentity(taiwuId, oldNpcId, newNpcId)) return false;
            if (!NpcChatUnreadStore.ReplaceIdentity(taiwuId, oldNpcId, newNpcId)) return false;
            if (!CommissionStore.ReplaceIdentity(taiwuId, oldNpcId, newNpcId)) return false;
            if (!PlayerTalkMarkStore.ReplaceIdentity(taiwuId, oldNpcId, newNpcId)) return false;
            if (!ConversationNavigationStore.ReplaceSingleIdentity(taiwuId, oldNpcId, newNpcId)) return false;
            if (!NativeInteractionRecordingStore.ReplaceIdentity(oldNpcId, newNpcId)) return false;
            // 左栏可能已在首次点击前把原模板人物的旧会话载入内存。磁盘资料迁移完后
            // 必须同步淘汰该旧索引，否则本进程会同时显示 original 与 proxy 两行。
            ChatWindow.NotifyConversationIdentityMigrated(taiwuId, oldNpcId, newNpcId);
            return true;
        }

        private static void FinalizeIdentityMigration(int taiwuId, int oldNpcId)
        {
            // 身份映射已经提交，下面只是收尾。任何清理失败都不再回退路由，保留源文件
            // 反而能供下次幂等重试，不能因此把已可用的副本再次判成失败。
            bool ok = TalkOrchestrator.FinalizeConversationIdentityMigration(taiwuId, oldNpcId);
            try
            {
                if (!NpcMemoryStore.DeleteFile(JianghuYoulingPaths.Memories,
                        taiwuId.ToString(), oldNpcId.ToString())) ok = false;
            }
            catch { ok = false; }
            try
            {
                if (!PersonaStore.Save(JianghuYoulingPaths.Personas, taiwuId.ToString(),
                        oldNpcId.ToString(), "", "replace")) ok = false;
            }
            catch { ok = false; }
            try { if (!PortraitStore.Delete(taiwuId, oldNpcId)) ok = false; }
            catch { ok = false; }
            if (!ok)
                Debug.LogWarning("[江湖有灵] 人物副本源资料清理未完全结束，将保留恢复数据 npc="
                    + oldNpcId);
        }

        private static bool MigratePersona(int taiwuId, int oldNpcId, int newNpcId)
        {
            string oldText = PersonaStore.Load(JianghuYoulingPaths.Personas,
                taiwuId.ToString(), oldNpcId.ToString());
            string newText = PersonaStore.Load(JianghuYoulingPaths.Personas,
                taiwuId.ToString(), newNpcId.ToString());
            if (!string.IsNullOrWhiteSpace(oldText))
            {
                string oldMode = PersonaStore.LoadMode(JianghuYoulingPaths.Personas,
                    taiwuId.ToString(), oldNpcId.ToString());
                string newMode = PersonaStore.LoadMode(JianghuYoulingPaths.Personas,
                    taiwuId.ToString(), newNpcId.ToString());
                string merged = newText;
                if (string.IsNullOrWhiteSpace(merged)) merged = oldText;
                else if (merged.IndexOf(oldText, StringComparison.Ordinal) < 0)
                    merged = merged.TrimEnd() + "\n\n" + oldText.Trim();
                if (merged.Length > PersonaStore.MaxCustomPersonaChars) return false;
                string mode = string.Equals(oldMode, "replace", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(newMode, "replace", StringComparison.OrdinalIgnoreCase)
                    ? "replace" : "append";
                if (!PersonaStore.Save(JianghuYoulingPaths.Personas, taiwuId.ToString(),
                    newNpcId.ToString(), merged, mode)) return false;
            }
            return true;
        }

        private static bool MigrateMemory(int taiwuId, int oldNpcId, int newNpcId)
        {
            NpcMemoryStore oldStore = NpcMemoryStore.Load(JianghuYoulingPaths.Memories,
                taiwuId.ToString(), oldNpcId.ToString());
            NpcMemoryStore newStore = NpcMemoryStore.Load(JianghuYoulingPaths.Memories,
                taiwuId.ToString(), newNpcId.ToString());
            if (!oldStore.LoadReliable || !newStore.LoadReliable) return false;
            foreach (MemoryEntry entry in oldStore.All)
            {
                if (entry == null) continue;
                newStore.Add(new MemoryEntry
                {
                    Id = entry.Id,
                    Content = entry.Content,
                    Type = entry.Type,
                    Keywords = entry.Keywords,
                    WorldDate = entry.WorldDate,
                    Importance = entry.Importance,
                    Valid = entry.Valid,
                    LastRecalled = entry.LastRecalled,
                    RecallCount = entry.RecallCount,
                    RecallMonths = entry.RecallMonths,
                    Core = entry.Core,
                    SourceKind = entry.SourceKind,
                    SourceId = entry.SourceId,
                    SourceLineIds = entry.SourceLineIds == null
                        ? null : new List<string>(entry.SourceLineIds),
                });
            }
            return newStore.Save();
        }

        private static string PathFor(int taiwuId)
            => Path.Combine(JianghuYoulingPaths.Intents,
                "character_proxies_" + taiwuId + ".json");

        private static bool Register(int taiwuId, int originalId, int resolvedId,
            short displayTemplateId, bool isProxy, int copyDate)
        {
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                bool hadOriginal = _resolved.TryGetValue(originalId, out int previousOriginal);
                bool hadResolved = _resolved.TryGetValue(resolvedId, out int previousResolved);
                bool hadTemplate = _displayTemplates.TryGetValue(resolvedId,
                    out short previousTemplate);
                bool hadCopyDate = _copyDates.TryGetValue(resolvedId,
                    out int previousCopyDate);
                _resolved[originalId] = resolvedId;
                _resolved[resolvedId] = resolvedId;
                if (displayTemplateId >= 0)
                    _displayTemplates[resolvedId] = displayTemplateId;
                if (isProxy && copyDate >= 0)
                    _copyDates[resolvedId] = hadCopyDate
                        ? Math.Min(previousCopyDate, copyDate) : copyDate;
                if (Save(taiwuId)) return true;

                // 内存路由和磁盘必须同生共死。否则一次磁盘写失败会让本进程提前改走
                // 尚未持久化的副本，重启后又回到原人物，形成重复副本或资料空白。
                if (hadOriginal) _resolved[originalId] = previousOriginal;
                else _resolved.Remove(originalId);
                if (hadResolved) _resolved[resolvedId] = previousResolved;
                else _resolved.Remove(resolvedId);
                if (hadTemplate) _displayTemplates[resolvedId] = previousTemplate;
                else _displayTemplates.Remove(resolvedId);
                if (hadCopyDate) _copyDates[resolvedId] = previousCopyDate;
                else _copyDates.Remove(resolvedId);
                return false;
            }
        }

        private static void EnsureLoaded(int taiwuId)
        {
            string scope = JianghuYoulingPaths.CurrentWorldId + ":" + taiwuId;
            if (string.Equals(_scope, scope, StringComparison.Ordinal) && _taiwuId == taiwuId)
            {
                return;
            }
            _scope = scope;
            _taiwuId = taiwuId;
            _resolved = new Dictionary<int, int>();
            _reconciledOriginals = new HashSet<int>();
            Verified.Clear();
            _displayTemplates = new Dictionary<int, short>();
            _copyDates = new Dictionary<int, int>();
            if (!DurableFileStore.TryReadRecoverableTextReadOnly(PathFor(taiwuId), MaxFileBytes,
                raw => IsValid(raw, taiwuId), out string json, out _, out _))
            {
                return;
            }
            try
            {
                State state = JsonConvert.DeserializeObject<State>(json);
                if (state?.Resolved != null)
                {
                    _resolved = new Dictionary<int, int>(state.Resolved);
                    _displayTemplates = state.DisplayTemplates == null
                        ? new Dictionary<int, short>()
                        : new Dictionary<int, short>(state.DisplayTemplates);
                    _copyDates = state.CopyDates == null
                        ? new Dictionary<int, int>()
                        : new Dictionary<int, int>(state.CopyDates);
                    // Keep historical content bindings untouched, but never publish them as
                    // runtime routes or skins until the current backend confirms ownership.
                }
            }
            catch
            {
                _resolved.Clear();
                _displayTemplates.Clear();
                _copyDates.Clear();
            }
        }

        private static bool Save(int taiwuId)
        {
            var state = new State
            {
                WorldId = JianghuYoulingPaths.CurrentWorldId,
                TaiwuId = taiwuId,
                Resolved = new Dictionary<int, int>(_resolved),
                DisplayTemplates = new Dictionary<int, short>(_displayTemplates),
                CopyDates = new Dictionary<int, int>(_copyDates),
            };
            string json = JsonConvert.SerializeObject(state, Formatting.Indented);
            return DurableFileStore.TryWriteTextAtomic(PathFor(taiwuId), json,
                MaxFileBytes, raw => IsValid(raw, taiwuId));
        }

        private static bool IsValid(string json, int taiwuId)
        {
            try
            {
                JObject root = JObject.Parse(json);
                State state = root.ToObject<State>();
                if (state == null
                    || state.Version != SchemaVersion
                    || state.WorldId == 0
                    || state.WorldId != JianghuYoulingPaths.CurrentWorldId
                    || state.TaiwuId != taiwuId || state.Resolved == null
                    || state.Resolved.Count > MaxEntries
                    || (state.CopyDates != null && state.CopyDates.Count > MaxEntries))
                    return false;
                foreach (KeyValuePair<int, int> pair in state.Resolved)
                    if (pair.Key <= 0 || pair.Value <= 0)
                        return false;
                if (state.CopyDates != null)
                    foreach (KeyValuePair<int, int> pair in state.CopyDates)
                        if (pair.Key <= 0 || pair.Value < 0) return false;
                if (state.DisplayTemplates != null)
                {
                    if (state.DisplayTemplates.Count > MaxEntries) return false;
                    foreach (KeyValuePair<int, short> pair in state.DisplayTemplates)
                        if (pair.Key <= 0 || pair.Value < 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static int CurrentWorldDate()
        {
            // CurrDate 在世界域 DataId=26 首包之前可能仍是默认值或上一个存档的残值；
            // 只有 WorldLifecycle 标记日期就绪后才能记录可靠的复制月份。
            if (!WorldLifecycle.HasWorldDate) return -1;
            try
            {
                BasicGameData data = SingletonObject.getInstance<BasicGameData>();
                return data != null ? data.CurrDate : -1;
            }
            catch { return -1; }
        }
    }
}
