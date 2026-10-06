using System;
using GameData.Common;              // DataContext
using GameData.Domains;             // DomainManager
using GameData.Domains.Mod;         // ModDomain, SerializableModData
using GameData.Domains.Character;   // Character, CharacterDomain, EquipmentSlotHelper, ResourceInts
using GameData.Domains.Character.Creation; // IntelligentCharacterCreationInfo(合法普通人物副本)
using GameData.Domains.Character.Alertness; // 1.0.72 偷窃三阶段成功率中的太吾戒心修正
using GameData.Domains.Character.Ai; // NpcTravelTarget (bounded fixed/dynamic travel)
using GameData.Domains.Item;        // ItemKey(Gm 分发器:赠物/换装)
using GameData.Domains.Taiwu;       // EItemAutoOperationSource(交易时禁用自动转仓)
using GameData.Domains.Information;  // SecretInformationId(Gm 分发器:秘闻/传秘闻)
using GameData.Domains.Organization; // OrganizationDomain.IsSect / SettlementCharacter(Gm 分发器:门派支持率)
using GameData.Domains.Character.Relation; // RelationTypeHelper.AllowAddingHusbandOrWifeRelation(男媒女约)
using GameData.Domains.Taiwu.Profession;
using GameData.Utilities;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GameData.Domains.Character.Ai.GeneralAction.BehaviorAction; // BecomeFriendAction, BecomeSwornAction
using GameData.Domains.Character.Display;  // RelatedCharactersForRelations, CharacterSet
using GameData.ActionPlanning.MonthlyAI; // PlanningContextArg, CharacterGoalData
using GameData.Domains.Map;          // Location(前往某地:构造目的地坐标)
using GameData.DomainEvents;         // Events(迁移旧固定模板副本的地图索引)
using GameData.Domains.TaiwuEvent;
using GameData.Domains.TaiwuEvent.DisplayEvent;
using HarmonyLib;
using TaiwuModdingLib.Core.Plugin;  // TaiwuRemakePlugin, PluginConfig
                                     // 注:Config 命名空间(CharacterFeature 等)不 using——与 GameData..Character 同名冲突,就地全限定
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Commission;
using JianghuYouling.Shared;

namespace JianghuYouling.Backend
{
    /// <summary>
    /// 《江湖有灵》后端薄插件:跑在后端逻辑线程,持有真实游戏域(Character/Taiwu…)。
    /// 仅暴露需后端权限的写操作 + RPC 入口。M1 先只有一个 Ping 验证前后端通路。
    /// </summary>
    [PluginConfig("江湖有灵 Backend", "jianghuyouling", "0.34.0.28")]
    public sealed class BackendPluginMain : TaiwuRemakePlugin
    {
        private const int DefaultNpcTravelDurationMonths = 6;
        private const string OperationLedgerDataName = "operation_receipts_v2";
        private const int OperationLedgerCapacity = 128;
        // b24185552 CharacterInteraction_Oppose: no-guard deathmatch completion.
        // The native result page invokes this event only after loot selection has finished;
        // the event then exits the interaction before it authoritatively kills the loser.
        private const string DialogueDeathCombatCompleteEventGuid =
            "f84d7117-51d4-4eab-98e7-c8f70d00547f";
        // Exact recent tombstones preserve original status/identity for late callbacks.
        // A fixed Bloom filter retains all older operation ids with no false negatives,
        // so total save keys stay bounded while a resurrected old frontend journal can
        // never re-execute an acknowledged mutation. False positives conservatively
        // reject a fresh random id; 1 MiBit/7 hashes keeps that negligible at normal scale.
        private const int OperationRecentTombstoneCapacity = 256;
        private const int OperationTombstoneBloomBitCount = 1024 * 1024;
        private const int OperationTombstoneBloomByteCount = OperationTombstoneBloomBitCount / 8;
        // SerializableModData prefixes each string with ushort length. Split the 128 KiB
        // filter into four independently Base64-encoded 32 KiB chunks (~43.7k chars each).
        private const int OperationTombstoneBloomChunkBytes = 32 * 1024;
        private const int OperationTombstoneBloomChunkCount =
            OperationTombstoneBloomByteCount / OperationTombstoneBloomChunkBytes;
        private const int OperationTombstoneBloomHashCount = 7;
        private const int OperationTombstoneIndexVersion = 1;
        private const string OperationTombstoneIndexField = "operation_tombstones_v2";
        private const string OperationTombstoneCountField = "operation_tombstone_count";
        private const string OperationTombstoneVersionField = "operation_tombstone_index_version";
        private const string OperationTombstoneBloomField = "operation_tombstone_bloom";
        private const string OperationTombstoneBloomChunksField = "operation_tombstone_bloom_chunks";
        private const string OperationTombstoneBloomBitsField = "operation_tombstone_bloom_bits";
        private const string OperationTombstoneBloomHashesField = "operation_tombstone_bloom_hashes";
        private const string OperationCompactionPendingField = "operation_compaction_pending";
        private const string OperationLedgerIntegrityField = "operation_ledger_integrity_sha256";
        private const string OperationAcknowledgedField = "operation_acknowledged";
        private static string _modIdStr;
        private static uint _legacyTombstoneCacheWorldId;
        private static string _legacyTombstoneCacheSource;
        private static readonly Dictionary<string, string[]> LegacyTombstoneCache
            = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static readonly object InFlightOperationGate = new object();
        private static readonly HashSet<string> InFlightOperationIds = new HashSet<string>(StringComparer.Ordinal);
        private Harmony _harmony;

        private sealed class OperationLedgerLoad
        {
            public bool Reliable;
            public bool Exists;
            public SerializableModData Data;
        }

        private sealed class OperationTombstoneEntry
        {
            public string OperationId;
            public string OperationKind;
            public string Status;
            public string Code;
            public uint WorldId;
            public int TaiwuId;
        }

        public override void Initialize()
        {
            // JHYL_REGISTERED_RPC_METHOD_COUNT_21：旧直改外貌 RPC 已移除，只保留当前正式接口。
            _modIdStr = base.ModIdStr;
            _harmony = new Harmony(GetGuid() + ".native-interaction-audit");
            _harmony.PatchAll(typeof(BackendPluginMain).Assembly);
            // 注册带参带返回的 RPC 方法。内部 key = ModIdStr + ".Method." + "Ping"
            DomainManager.Mod.AddModMethod(base.ModIdStr, RpcConst.PingMethod, Ping);
            AddMutationMethod(RpcConst.ExecuteRelationMethod, ExecuteRelation);
            AddMutationMethod(RpcConst.AddGoalMethod, AddGoal);
            DomainManager.Mod.AddModMethod(base.ModIdStr, RpcConst.FilterDeadMethod, FilterDead);
            AddMutationMethod(RpcConst.RecognizeMethod, Recognize);
            AddMutationMethod(RpcConst.ChangeMoralityMethod, ChangeMorality);
            DomainManager.Mod.AddModMethod(base.ModIdStr, RpcConst.ResolveCharMethod, ResolveChar);
            AddMutationMethod(RpcConst.ChangeAlertnessMethod, ChangeAlertness);
            AddMutationMethod(RpcConst.ReleaseMethod, Release);
            AddMutationMethod(RpcConst.TeachSkillMethod, TeachSkill);
            DomainManager.Mod.AddModMethod(base.ModIdStr, RpcConst.GmMethod, Gm);
            AddMutationMethod(RpcConst.LeaveMethod, Leave);
            AddMutationMethod(RpcConst.EnsureCharacterProxyMethod, EnsureCharacterProxy);
            AddMutationMethod(RpcConst.JoinTeamMethod, JoinTeam);
            AddMutationMethod(RpcConst.MatchmakeMethod, Matchmake);
            DomainManager.Mod.AddModMethod(base.ModIdStr, RpcConst.IsTeammateMethod, IsTeammate);
            DomainManager.Mod.AddModMethod(base.ModIdStr, RpcConst.QueryTaiwuSkillsMethod, QueryTaiwuSkills);
            AddMutationMethod(RpcConst.KillMethod, Kill);
            AddMutationMethod(RpcConst.CaptureMethod, Capture);
            AddMutationMethod(RpcConst.StartCombatMethod, StartCombat);
            DomainManager.Mod.AddModMethod(base.ModIdStr, RpcConst.QueryOperationMethod, QueryOperation);
            DomainManager.Mod.AddModMethod(base.ModIdStr, RpcConst.AckOperationMethod, AcknowledgeOperation);
            DomainManager.Mod.AddModMethod(base.ModIdStr, RpcConst.QueryNativeInteractionAuditMethod,
                NativeInteractionMutationAudit.Query);
        }

        private void AddMutationMethod(string methodName, Func<DataContext, SerializableModData, SerializableModData> handler)
        {
            DomainManager.Mod.AddModMethod(base.ModIdStr, methodName,
                (context, parameter) => ExecuteJournaled(context, parameter, methodName, handler));
        }

        // 通用 GM 分发器:把"会崩/会损坏数据/会凭空捏造"的写操作统一收进 mod 自己的 try/catch 通道执行,
        // 任何游戏异常都被这一处接住,绝不冲垮后端主循环(传功卡死同源)。0-throw 已验证安全的命令
        // (改门派/秘闻/追随/单向结仇)仍走前端直调,不在此列。
        // 凭空捏造防线:givesilver 按 NPC 实有银钱封顶、giveitem/equip 先校验 NPC 真持有该物——一切以 NPC 真实上下文为准。
        private static SerializableModData Gm(DataContext context, SerializableModData p)
        {
            string op = null;
            p?.Get("op", out op);
            return IsGmMutationOp(op)
                ? ExecuteJournaled(context, p, "gm:" + op, GmCore)
                : GmCore(context, p);
        }

        private static SerializableModData GmCore(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "GM 请求不完整");
            string op = null; p.Get("op", out op);
            if (string.IsNullOrEmpty(op)) return Fail("missing_op", "缺少 op");
            try
            {
                if (TryGetPhysicalInteractionPair(op, p, out int physicalActorId,
                    out int physicalTargetId)
                    && physicalActorId != physicalTargetId)
                {
                    Character physicalActor, physicalTarget;
                    if (!DomainManager.Character.TryGetElement_Objects(physicalActorId,
                            out physicalActor) || physicalActor == null
                        || !DomainManager.Character.TryGetElement_Objects(physicalTargetId,
                            out physicalTarget) || physicalTarget == null)
                        return Fail("invalid_char", "当面行动人物已失效");
                    if (!SameValidLocation(physicalActor, physicalTarget))
                        return Fail("not_co_located",
                            "双方当前不在同一有效地块，不能完成这项当面行动");
                }
                switch (op)
                {
                    case "favor":   // 好感:失效角色时 GetElement_Objects 会抛 → 这里接住
                    {
                        int self = 0, related = 0, delta = 0;
                        p.Get("self", out self); p.Get("related", out related); p.Get("delta", out delta);
                        DomainManager.Character.GmCmd_ChangeFavorability(context, self, related, ClampShort(delta));
                        break;
                    }
                    case "givesilver":   // 赠/借银钱:无人情债转移 + 净加好感(根治 +1500 补偿 band-aid)
                    {
                        int npcId = 0, taiwuId = 0, amount = 0, bonus = 0, rcpt = 0;
                        p.Get("npc", out npcId); p.Get("taiwu", out taiwuId); p.Get("amount", out amount); p.Get("favor_bonus", out bonus); p.Get("recipient", out rcpt);
                        if (amount <= 0) return Fail("bad_args", "金额无效");
                        int dstId = rcpt > 0 ? rcpt : taiwuId;   // recipient>0 → 赠予第三方 NPC;否则赠太吾(原行为)
                        if (npcId == dstId) return Fail("bad_args", "施受同体");
                        Character src, dst;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out src) || src == null ||
                            !DomainManager.Character.TryGetElement_Objects(dstId, out dst) || dst == null)
                            return Fail("invalid_char", "角色无效");
                        // 凭空捏造防线:TransferResource 不校验余额,NPC 没钱也照转→钱变负数=无限给。按 NPC 实有银钱封顶。
                        int npcMoney = src.GetResource(6);   // ResourceType.Money = 6:NPC 现有银钱
                        if (npcMoney <= 0) return Fail("no_money", "NPC 囊中羞涩,无钱可赠");
                        if (amount > npcMoney) amount = npcMoney;   // 不能赠出超过自己所有的
                        TransferResourceChecked(context, src, dst, (sbyte)6, amount);
                        if (bonus != 0) DomainManager.Character.ChangeFavorabilityOptional(context, src, dst, bonus, -1);
                        var okSilver = new SerializableModData(); okSilver.Set("success", true); okSilver.Set("message", "ok"); okSilver.Set("amount", amount); return okSilver;
                    }
                    case "happiness":   // 改任意有效人物心情（含 NPC 与太吾）；ChangeHappiness 内部 Clamp[-119,119]。
                    {
                        int characterId = 0, npcId = 0, delta = 0;
                        p.Get("character", out characterId); p.Get("npc", out npcId); p.Get("delta", out delta);
                        if (characterId <= 0) characterId = npcId; // 兼容旧前端/旧 operation envelope。
                        if (delta == 0) return Fail("noop", "无变化");
                        Character c;
                        if (!DomainManager.Character.TryGetElement_Objects(characterId, out c) || c == null)
                            return Fail("invalid_char", "角色无效");
                        c.ChangeHappiness(context, delta);
                        break;
                    }
                    case "reaction":   // 单轮对话反应：先完整校验，再在同一后端域 RPC 内批量落地
                    {
                        int npcId = 0, taiwuId = 0, favorDelta = 0, happinessDelta = 0, moralityDelta = 0, alertnessDelta = 0;
                        p.Get("npc", out npcId); p.Get("taiwu", out taiwuId);
                        p.Get("favor_delta", out favorDelta); p.Get("happiness_delta", out happinessDelta);
                        p.Get("morality_delta", out moralityDelta); p.Get("alertness_delta", out alertnessDelta);
                        // Defense in depth: a forged/bypassed frontend request must not turn one
                        // ordinary chat reaction into a sever-friend/divorce-scale alertness jump.
                        // b24185552 routine interaction magnitude tops out at 1000; 8000..30000
                        // belongs to authoritative major relationship events.
                        alertnessDelta = Math.Max(-1000, Math.Min(1000, alertnessDelta));
                        if (npcId <= 0 || taiwuId <= 0 || npcId == taiwuId)
                            return Fail("bad_args", "反应角色参数无效");
                        if (favorDelta == 0 && happinessDelta == 0 && moralityDelta == 0 && alertnessDelta == 0)
                            return Fail("noop", "本轮无反应变化");
                        if (DomainManager.Taiwu.GetTaiwuCharId() != taiwuId)
                            return Fail("taiwu_changed", "太吾身份已变化");
                        Character reactionNpc, reactionTaiwu;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out reactionNpc) || reactionNpc == null
                            || !DomainManager.Character.TryGetElement_Objects(taiwuId, out reactionTaiwu) || reactionTaiwu == null)
                            return Fail("invalid_char", "角色已失效");
                        if (alertnessDelta != 0 && reactionNpc.GetCreatingType() != 1)
                            return Fail("no_alertness", "此人非常规江湖人物，没有戒备机制");

                        try
                        {
                            if (favorDelta != 0)
                                DomainManager.Character.GmCmd_ChangeFavorability(context, npcId, taiwuId, ClampShort(favorDelta));
                            if (happinessDelta != 0) reactionNpc.ChangeHappiness(context, happinessDelta);
                            if (moralityDelta != 0) reactionNpc.ChangeBaseMorality(context, moralityDelta);
                            if (alertnessDelta != 0) DomainManager.Character.ChangeAlertness(context, npcId, alertnessDelta);
                        }
                        catch (Exception e) { return Indeterminate("reaction_indeterminate", "反应可能已部分落地:" + e.GetType().Name); }

                        var reactionDone = new SerializableModData();
                        reactionDone.Set("success", true);
                        reactionDone.Set("message", "本轮反应已落地");
                        reactionDone.Set("favor_delta", favorDelta);
                        reactionDone.Set("happiness_delta", happinessDelta);
                        reactionDone.Set("morality_delta", moralityDelta);
                        reactionDone.Set("alertness_delta", alertnessDelta);
                        reactionDone.Set("happiness_after", (int)reactionNpc.GetHappiness());
                        reactionDone.Set("morality_after", (int)reactionNpc.GetBaseMorality());
                        if (alertnessDelta != 0)
                            reactionDone.Set("alertness_after", DomainManager.Character.GetAlertnessValue(npcId));
                        return reactionDone;
                    }
                    case "giveitem":   // 赠物:无人情债转移 + 净加好感;支持复数(资源/食材类)
                    {
                        int npcId = 0, taiwuId = 0, it = 0, modSt = 0, t = 0, id = 0, bonus = 0, want = 0, rcpt = 0;
                        int allowEquipped = 0;
                        p.Get("npc", out npcId); p.Get("taiwu", out taiwuId);
                        p.Get("it", out it); p.Get("mod", out modSt); p.Get("t", out t); p.Get("id", out id);
                        p.Get("favor_bonus", out bonus); p.Get("amount", out want); p.Get("recipient", out rcpt);
                        p.Get("allow_equipped", out allowEquipped);
                        if (want <= 0) want = 1;
                        int dstId = rcpt > 0 ? rcpt : taiwuId;   // recipient>0 → 赠予第三方 NPC;否则赠太吾(原行为)
                        if (npcId == dstId) return Fail("bad_args", "施受同体");
                        Character src, dst;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out src) || src == null ||
                            !DomainManager.Character.TryGetElement_Objects(dstId, out dst) || dst == null)
                            return Fail("invalid_char", "角色无效");
                        // 全实时:若给了物名(而非现成 ItemKey),按 NPC【当前】持有实时解析(物品会随送随变,故不依赖前端缓存)
                        string gName; p.Get("name", out gName);
                        if (!string.IsNullOrWhiteSpace(gName))
                        {
                            bool fnd; var rk = ResolveNpcItemByName(src, gName, out fnd);
                            if (!fnd) return Fail("not_owned", "你此刻并无「" + gName.Trim() + "」可赠——以 query_npc_items 的实时清单为准,挑确切名重填");
                            it = (int)rk.ItemType; modSt = (int)rk.ModificationState; t = (int)rk.TemplateId; id = rk.Id;
                        }
                        // 资源类(食材/木料/金石/玉石/布料/药材/银钱/威望)存于 ResourceInts、不在背包 Items 里:
                        // 显示行编码为 ItemKey(12, 0, 资源类型, 0)(见 ItemDisplayData.CreateResource),须像银钱一样走 TransferResource。
                        if (ItemTemplateHelper.IsMiscResource((sbyte)it, (short)t))
                        {
                            sbyte resType = (sbyte)t;   // CreateResource 把资源类型(0..7)编进 TemplateId,GetMiscResourceType 为恒等
                            int haveRes = src.GetResource(resType);
                            if (haveRes <= 0) return Fail("not_owned", "NPC 此资源已无,无法相赠");
                            int giveRes = want < haveRes ? want : haveRes;   // 不能赠出超过其所有
                            TransferResourceChecked(context, src, dst, resType, giveRes);
                            if (bonus != 0) DomainManager.Character.ChangeFavorabilityOptional(context, src, dst, bonus, -1);
                            var okRes = new SerializableModData(); okRes.Set("success", true); okRes.Set("message", "ok"); okRes.Set("amount", giveRes); return okRes;
                        }
                        var reqKey = new ItemKey((sbyte)it, (byte)modSt, (short)t, id);
                        // 真实背包物品:在 NPC 背包里定位那一项(可堆叠物的 Id/品质态可能与快照键对不上,故按类型+模板容错匹配)。
                        ItemKey realKey; int have;
                        if (FindOwnedItem(src, reqKey, out realKey, out have))
                        {
                            int give = want < have ? want : have;   // 不能赠出超过其所有(同给钱)
                            if (give <= 0) return Fail("not_owned", "NPC 此物已无存货");
                            ApplyTransferThing(context, npcId, src, dst,
                                new TransferThing { Key = realKey, Amount = give },
                                TransferIntent.Gift);
                            if (bonus != 0) DomainManager.Character.ChangeFavorabilityOptional(context, src, dst, bonus, -1);
                            var okGive = new SerializableModData(); okGive.Set("success", true); okGive.Set("message", "ok"); okGive.Set("amount", give); return okGive;
                        }
                        // 背包没有 → 看是不是"随身熟食"(荷包蛋等存于 EatingItems,不在背包 Items 里):从 NPC 餐位移除,放进太吾背包
                        int eslot = src.GetEatingItems().IndexOf(reqKey);
                        if (eslot >= 0)
                        {
                            try
                            {
                                if (EatingItems.IsWug(reqKey)) return Fail("unsupported_item", "蛊虫不能作为普通物品相赠");
                                ApplyTransferThing(context, npcId, src, dst, new TransferThing
                                {
                                    Key = reqKey, Amount = 1, IsEating = true, EatingSlot = eslot,
                                    EatingDuration = src.GetEatingItems().GetDuration(eslot)
                                }, TransferIntent.Gift);
                                if (bonus != 0) DomainManager.Character.ChangeFavorabilityOptional(context, src, dst, bonus, -1);
                                var okFd = new SerializableModData(); okFd.Set("success", true); okFd.Set("message", "ok"); okFd.Set("amount", 1); return okFd;
                            }
                            catch (Exception fe) { return Indeterminate("food_transfer_indeterminate", "熟食转移状态无法确认:" + fe.GetType().Name); }
                        }
                        // 背包没有 → 看是不是"身上佩带之物"。只有前端根据玩家本轮原话
                        // 明确授权时才可卸下相赠；月度/过月自主赠礼和普通闲聊一律不能
                        // 让模型借 gift 擅自扒下 NPC 正在使用的兵器、衣甲或佩饰。
                        sbyte eqSlot = FindEquippedSlot(src, reqKey);
                        if (eqSlot < 0) return Fail("not_owned", "NPC 并无此物,无法相赠");
                        if (allowEquipped != 1)
                            return Fail("equipped_item_protected",
                                "此物正在穿戴；除非太吾本轮明确要求你赠出这件装备，否则不能自行卸下相赠");
                        var wornKey = src.GetEquipment()[eqSlot];
                        ApplyTransferThing(context, npcId, src, dst, new TransferThing
                        {
                            Key = wornKey, Amount = 1, IsEquipped = true, EquipSlot = eqSlot
                        }, TransferIntent.Gift);
                        if (bonus != 0) DomainManager.Character.ChangeFavorabilityOptional(context, src, dst, bonus, -1);
                        var okEq = new SerializableModData(); okEq.Set("success", true); okEq.Set("message", "ok"); okEq.Set("amount", 1); return okEq;
                    }
                    case "equip":   // 换装:按真实子类型定槽 + IsItemMeetSlot 校验,杜绝错槽损坏装备(C1)
                    {
                        int npcId = 0, it = 0, modSt = 0, t = 0, id = 0;
                        p.Get("npc", out npcId); p.Get("it", out it); p.Get("mod", out modSt); p.Get("t", out t); p.Get("id", out id);
                        // 凭空捏造防线:只有 NPC 随身真有该装备才换上(否则 ChangeEquipment 从库存 OfflineRemove 会抛 KeyNotFound)
                        Character eqChar;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out eqChar) || eqChar == null)
                            return Fail("invalid_char", "角色无效");
                        // 全实时:若给了物名,按 NPC【当前】持有实时解析(装备会随穿随脱,故不依赖前端缓存)
                        string eqName; p.Get("name", out eqName);
                        if (!string.IsNullOrWhiteSpace(eqName))
                        {
                            bool efnd; var erk = ResolveNpcItemByName(eqChar, eqName, out efnd);
                            if (!efnd) return Fail("not_owned", "你此刻并无「" + eqName.Trim() + "」可换上——以 query_npc_items 的实时清单为准,挑确切名重填");
                            it = (int)erk.ItemType; modSt = (int)erk.ModificationState; t = (int)erk.TemplateId; id = erk.Id;
                        }
                        var key = new ItemKey((sbyte)it, (byte)modSt, (short)t, id);
                        if (!eqChar.GetInventory().Items.ContainsKey(key)) return Fail("not_owned", "NPC 随身并无此装备");
                        sbyte slot = ResolveEquipSlot(key);
                        if (slot < 0 || !EquipmentSlotHelper.IsItemMeetSlot(slot, key)) return Fail("slot_mismatch", "物品与装备槽不符,拒绝写入");
                        DomainManager.Character.ChangeEquipment(context, npcId, (sbyte)(-1), slot, key);
                        // #11 换装后强制刷新头像:ChangeEquipment 只动 equipment 字段,而头像按 avatar 字段 hash 失效重渲染。
                        // 不回写 avatar,前端就不重渲染,新衣(尤其贴身衣着槽4=ClothDisplayId 来源)显示不出来——看着像没穿/没身体。
                        try { eqChar.SetAvatar(eqChar.GetAvatar(), context); } catch { }
                        break;
                    }
                    case "takeoff":   // 卸下:护具展开全部 4 槽(修 H3:不再只卸躯干)。注:贴身衣着(槽4)前端禁卸,避免头像无身体。
                    {
                        int npcId = 0; string part = null;
                        p.Get("npc", out npcId); p.Get("part", out part);
                        Character chTk = null;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out chTk) || chTk == null)
                            return Fail("invalid_char", "角色无效");
                        var eqTk = chTk.GetEquipment();   // 17 槽,按 slot 索引
                        int removed = 0;
                        bool removalIndeterminate = false;
                        foreach (sbyte slot in SlotsForPart(part))
                        {
                            if (slot == 4) continue;   // 双保险:绝不卸贴身衣着(槽4=ClothDisplayId 来源,卸空头像就没身体)
                            // 关键修复:空槽不可调 ChangeEquipment——游戏内 GetBaseEquipment(Invalid) 会抛异常("takeoff 执行失败")。
                            // 故只对【确有装备】的槽卸下;单槽失败也 try/catch 兜住,不让一个槽崩掉整次卸装。
                            try
                            {
                                if (eqTk == null || slot < 0 || slot >= eqTk.Length) continue;
                                if (!eqTk[slot].IsValid()) continue;   // 空槽跳过
                                DomainManager.Character.ChangeEquipment(context, npcId, slot, (sbyte)(-1), default(ItemKey));
                                removed++;
                            }
                            catch { removalIndeterminate = true; }
                        }
                        if (removalIndeterminate)
                            return Indeterminate("takeoff_indeterminate", "部分装备槽的卸下状态无法确认；不得自动重试");
                        // #11 卸下后同样强制刷新头像,免前端停在旧渲染(护甲/佩饰卸了却还显示在身)。
                        if (removed > 0) { try { chTk.SetAvatar(chTk.GetAvatar(), context); } catch { } }
                        var okTk = new SerializableModData();
                        okTk.Set("success", removed > 0);
                        okTk.Set("message", removed > 0 ? ("已卸下 " + removed + " 件") : "该处本无装备可卸");
                        return okTk;
                    }
                    case "use_item":   // NPC 自用消耗品：实时解析、原生食用路径、精确库存后验。
                    {
                        int charId = 0; string requested = null;
                        p.Get("char", out charId); p.Get("item", out requested);
                        if (charId <= 0 || string.IsNullOrWhiteSpace(requested))
                            return Fail("bad_args", "未指定人物或物品");
                        if (charId == DomainManager.Taiwu.GetTaiwuCharId())
                            return Fail("taiwu_forbidden", "使用物品工具只允许 NPC 自己使用，不能替太吾操作");
                        Character actor;
                        if (!DomainManager.Character.TryGetElement_Objects(charId, out actor) || actor == null)
                            return Fail("invalid_char", "角色无效");
                        if (!TryResolveNpcUsableInventoryItem(actor, requested,
                                out ItemKey realKey, out int beforeCount,
                                out int consumeAmount, out string useKind,
                                out string resolveCode, out string unavailableReason))
                            return Fail(resolveCode ?? "item_unavailable", unavailableReason
                                ?? "此物品当前不能由 NPC 直接使用");

                        string realName = StripTags(ItemTemplateHelper.GetName(realKey.ItemType,
                            realKey.TemplateId)) ?? requested.Trim();
                        try
                        {
                            if (ItemTemplateHelper.IsTianJieFuLu(realKey.ItemType, realKey.TemplateId))
                                DomainManager.Extra.EatTianJieFuLu(context, charId, realKey, consumeAmount);
                            else
                                DomainManager.Character.AddEatingItem(context, charId, realKey, null);
                        }
                        catch (Exception e)
                        {
                            if (!TryInventoryCount(actor, realKey, out int uncertainAfter)
                                || uncertainAfter != beforeCount - consumeAmount)
                                return Indeterminate("use_item_indeterminate", "物品使用状态无法确认:"
                                    + e.GetType().Name);
                            // 本体在通知/尾处理抛错，但库存精确满足唯一一次消耗时，禁止前端重试。
                        }
                        if (!TryInventoryCount(actor, realKey, out int afterCount))
                            return Indeterminate("use_item_postcondition_indeterminate",
                                "使用接口已返回，但背包数量无法确认");
                        if (afterCount != beforeCount - consumeAmount)
                            return Indeterminate("use_item_postcondition_indeterminate",
                                "使用接口已返回，但背包数量变化不符合一次使用");
                        var used = new SerializableModData();
                        used.Set("success", true); used.Set("message", "ok");
                        used.Set("name", realName); used.Set("kind", useKind);
                        used.Set("amount", consumeAmount); used.Set("before", beforeCount);
                        used.Set("after", afterCount);
                        return used;
                    }
                    case "poison":   // 下毒:actor(须持毒药)对 target 施毒,消耗一份毒药。actor 可为 NPC(被说动)或太吾。
                    {
                        int actorId = 0, targetId = 0, monthlyOnlyPurity = 0;
                        string requestedPoisonType = null;
                        p.Get("actor", out actorId); p.Get("target", out targetId);
                        p.Get("monthly_only_purity", out monthlyOnlyPurity);
                        p.Get("poison_type", out requestedPoisonType);
                        if (actorId <= 0 || targetId <= 0 || actorId == targetId) return Fail("bad_args", "参数无效");
                        Character actor, target;
                        if (!DomainManager.Character.TryGetElement_Objects(actorId, out actor) || actor == null ||
                            !DomainManager.Character.TryGetElement_Objects(targetId, out target) || target == null)
                            return Fail("invalid_char", "角色无效");
                        if (IsActorRestrained(actor, actorId))
                            return Fail("actor_restrained", "此人正被囚禁或绑架，无法向他人下毒");
                        if (monthlyOnlyPurity == 1)
                        {
                            sbyte actorLv = actor.GetConsummateLevel();
                            sbyte targetLv = target.GetConsummateLevel();
                            if (actorLv < targetLv)
                                return Fail("not_strong_enough", "你的精纯不及对方(" + actorLv + "<" + targetLv + "),下毒难成");
                            sbyte monthlyPoisonType = ParsePoisonType(requestedPoisonType);
                            try
                            {
                                // 过月危险行动只以精纯作为玩法门槛：不要求背包毒药，也不把
                                // 仇怨、毒抗或百毒不侵当成硬拒绝。沿用本体事件函数
                                // SpecifyPoisoned 的权威写法，直接写入一种确定性毒素。
                                if (monthlyPoisonType < 0)
                                {
                                    monthlyPoisonType = (sbyte)((actorId ^ targetId
                                        ^ DomainManager.World.GetCurrDate()) % 6);
                                    if (monthlyPoisonType < 0) monthlyPoisonType = (sbyte)(-monthlyPoisonType);
                                }
                                ref PoisonInts poisoned = ref target.GetPoisoned();
                                int poisonBefore = poisoned[monthlyPoisonType];
                                if (!MonthlyPoisonPolicy.TryGetNextValue(poisonBefore, out int requestedPoisonAfter))
                                    return Fail("poison_saturated", PoisonTypeName(monthlyPoisonType) + "已达上限，无法继续加深");
                                poisoned[monthlyPoisonType] = requestedPoisonAfter;
                                target.SetPoisoned(ref poisoned, context);
                                ref PoisonInts confirmedPoisoned = ref target.GetPoisoned();
                                int poisonAfter = confirmedPoisoned[monthlyPoisonType];
                                if (poisonAfter <= poisonBefore)
                                    return Indeterminate("monthly_poison_no_effect",
                                        "过月下毒未能确认毒性增加，已停止后续叙事");
                            }
                            catch (Exception pe)
                            {
                                return Indeterminate("monthly_poison_indeterminate",
                                    "过月下毒状态无法确认:" + pe.GetType().Name);
                            }
                            var monthlyDone = new SerializableModData();
                            monthlyDone.Set("success", true);
                            string monthlyPoisonName = PoisonTypeName(monthlyPoisonType);
                            monthlyDone.Set("message", "已下" + monthlyPoisonName);
                            monthlyDone.Set("poison_name", monthlyPoisonName);
                            return monthlyDone;
                        }
                        // 背包找毒药(不自动造):用游戏权威判定——GetMedicineItemPoisonType 对「Medicine(ItemType=8)且子类型为毒(801)」的物品返回其毒型(≥0=可施毒的毒药),否则 -1。
                        // 兼容一切品级/名目的毒药(百毒砂/毒娘子砂/金蟾五毒砂/野鬼毒砂…),不再硬编码 476-481 那套早已不符的模板号(旧法把毒药当 ItemType12 故几乎永远查不到 → 「手边没有毒药」误报)。
                        ItemKey poisonKey = default(ItemKey); bool hasPoison = false; sbyte poisonType = 0;
                        try
                        {
                            var inv = actor.GetInventory();
                            if (inv != null && inv.Items != null)
                                foreach (var kv in inv.Items)
                                {
                                    var k = kv.Key;
                                    sbyte pt = ItemTemplateHelper.GetMedicineItemPoisonType(k.ItemType, k.TemplateId);
                                    if (kv.Value > 0 && pt >= 0)
                                    { poisonKey = k; poisonType = pt; hasPoison = true; break; }
                                }
                        }
                        catch { }
                        if (!hasPoison) return Fail("no_poison", "手边没有毒药,无从下毒(须先备一份毒药)");
                        // 施毒后验:游戏 Character.ChangePoisoned→CalcChangedPoisoned 对【百毒不侵】者(配置免疫/毒抗≥1000/额外免疫,见反编译 Character.cs HasPoisonImmunity)
                        // 静默 return、毫无效果。故先按游戏权威判定预检——免疫则如实失败、且【不白扔毒药】,而非假报"已下毒"。
                        if (target.HasPoisonImmunity(poisonType))
                            return Fail("immune", "对方百毒不侵,这毒奈何他不得——换个目标,或换别的法子(毒药未耗)");
                        int normalPoisonBefore;
                        int poisonItemBefore;
                        bool poisonPureStackable;
                        try
                        {
                            normalPoisonBefore = target.GetPoisoned()[poisonType];
                            if (!TryInventoryCount(actor, poisonKey, out poisonItemBefore))
                                return Indeterminate("poison_inventory_unreadable",
                                    "无法读取施术者毒药库存，已在下毒前中止");
                            poisonPureStackable = ItemDomain.IsPureStackable(
                                DomainManager.Item.GetBaseItem(poisonKey));
                        }
                        catch (Exception pe)
                        {
                            return Indeterminate("poison_precondition_indeterminate",
                                "无法读取下毒前权威状态:" + pe.GetType().Name);
                        }
                        if (normalPoisonBefore >= 25000)
                            return Fail("poison_saturated",
                                PoisonTypeName(poisonType) + "已达上限，毒药未耗");
                        if (poisonItemBefore <= 0)
                            return Fail("no_poison", "毒药库存已变化，未执行下毒");

                        int poisonApplied;
                        try
                        {
                            target.ChangePoisoned(context, poisonType, (sbyte)3, 60);
                            poisonApplied = target.GetPoisoned()[poisonType];
                            if (poisonApplied <= normalPoisonBefore)
                                return Fail("poison_no_effect", "毒性没有增加，毒药未耗");
                        }
                        catch (Exception pe)
                        {
                            try
                            {
                                int poisonNow = target.GetPoisoned()[poisonType];
                                if (poisonNow == normalPoisonBefore)
                                    return Fail("poison_apply_failed",
                                        "下毒在写入前中止，毒药未耗:" + pe.GetType().Name);
                                if (poisonNow > normalPoisonBefore
                                    && TryRestorePoisonValue(context, target, poisonType,
                                        poisonNow, normalPoisonBefore))
                                    return Fail("poison_apply_failed",
                                        "下毒尾调用异常，毒性已恢复且毒药未耗:" + pe.GetType().Name);
                            }
                            catch { }
                            return Indeterminate("poison_indeterminate",
                                "下毒状态无法确认，已停止后续写入:" + pe.GetType().Name);
                        }

                        Exception consumeError = null;
                        try { actor.RemoveInventoryItem(context, poisonKey, 1, deleteItem: true); }
                        catch (Exception pe) { consumeError = pe; }
                        int normalPoisonAfter;
                        int poisonItemAfter;
                        try
                        {
                            normalPoisonAfter = target.GetPoisoned()[poisonType];
                            if (!TryInventoryCount(actor, poisonKey, out poisonItemAfter))
                                return Indeterminate("poison_postcondition_unreadable",
                                    "下毒后无法读取毒药库存，不得自动重试");
                        }
                        catch (Exception pe)
                        {
                            return Indeterminate("poison_postcondition_unreadable",
                                "下毒后无法读取权威状态，不得自动重试:" + pe.GetType().Name);
                        }
                        bool poisonEntityStillExists = false;
                        if (!poisonPureStackable)
                        {
                            try { poisonEntityStillExists = DomainManager.Item.ItemExists(poisonKey); }
                            catch
                            {
                                return Indeterminate("poison_entity_postcondition_unreadable",
                                    "下毒后无法读取唯一毒药实体状态，不得自动重试");
                            }
                        }
                        bool inventoryConsumptionComplete = normalPoisonAfter == poisonApplied
                            && poisonItemAfter == poisonItemBefore - 1;
                        if (inventoryConsumptionComplete && !poisonPureStackable
                            && poisonEntityStillExists)
                        {
                            // RemoveInventoryItem 的顺序是先扣背包、最后删唯一实体。尾调用
                            // 若在两者之间抛错，仅凭数量少一仍不能算成功；补完并验证删除。
                            try
                            {
                                if (DomainManager.Item.ItemExists(poisonKey))
                                    DomainManager.Item.RemoveItem(context, poisonKey);
                                poisonEntityStillExists = DomainManager.Item.ItemExists(poisonKey);
                            }
                            catch { poisonEntityStillExists = true; }
                        }
                        bool consumptionComplete = inventoryConsumptionComplete
                            && (poisonPureStackable || !poisonEntityStillExists);
                        if (consumptionComplete)
                        {
                            // RemoveInventoryItem 的尾调用即使抛错，只要双方权威后验精确满足，
                            // 事务仍已完整完成，不能把成功误报 UNKNOWN。
                        }
                        else if (normalPoisonAfter == poisonApplied
                            && poisonItemAfter == poisonItemBefore
                            && (poisonPureStackable || poisonEntityStillExists)
                            && TryRestorePoisonValue(context, target, poisonType,
                                poisonApplied, normalPoisonBefore))
                        {
                            return Fail("poison_consumption_failed",
                                "毒药未能消耗，已恢复目标毒性"
                                + (consumeError != null ? ":" + consumeError.GetType().Name : ""));
                        }
                        else
                        {
                            return Indeterminate("poison_partial_commit",
                                "下毒与毒药消耗未形成一致后验，不得自动重试"
                                + (consumeError != null ? ":" + consumeError.GetType().Name : ""));
                        }
                        var okP = new SerializableModData();
                        string poisonName = PoisonTypeName(poisonType);
                        okP.Set("success", true);
                        okP.Set("message", "已下" + poisonName);
                        okP.Set("poison_name", poisonName);
                        return okP;
                    }
                    case "heal":   // 疗伤:healer 为 target 恢复气血并清创。target 缺省=太吾。直接调引擎,无需药材/医术。
                    {
                        int healerId = 0, targetId = 0;
                        p.Get("healer", out healerId); p.Get("target", out targetId);
                        if (targetId <= 0) return Fail("bad_args", "参数无效");
                        Character target;
                        if (!DomainManager.Character.TryGetElement_Objects(targetId, out target) || target == null)
                            return Fail("invalid_char", "角色无效");
                        short beforeHealth = target.GetHealth();
                        short leftMaxHealth = target.GetLeftMaxHealth();
                        int beforeInjuries = target.GetInjuries().GetSum();
                        if (beforeHealth >= leftMaxHealth && beforeInjuries <= 0)
                            return Fail("no_treatment_needed", "未执行：目标当前气血已满且没有伤势，无需疗伤");
                        try
                        {
                            // 游戏的可恢复气血上限是 GetLeftMaxHealth，而不是未经寿命扣减的
                            // GetMaxHealth（见最新版 PersonalNeedHelper.HasHealthDemand）。
                            if (beforeHealth < leftMaxHealth) target.SetHealth(leftMaxHealth, context);
                            var inj = default(global::GameData.Domains.Character.Injuries);
                            inj.Initialize();
                            if (beforeInjuries > 0) target.SetInjuries(inj, context);
                        }
                        catch (Exception he) { return Indeterminate("heal_indeterminate", "气血或伤势可能已部分恢复:" + he.GetType().Name); }
                        short afterHealth = target.GetHealth();
                        int afterInjuries = target.GetInjuries().GetSum();
                        if (afterHealth == beforeHealth && afterInjuries == beforeInjuries)
                            return Fail("heal_no_change", "未执行：权威状态没有发生可确认的疗伤变化");
                        var okH = new SerializableModData(); okH.Set("success", true); okH.Set("message", "已疗伤"); return okH;
                    }
                    case "detox":   // NPC 以本体医毒造诣为另一名在场人物驱毒。
                    {
                        int healerId = 0, targetId = 0;
                        p.Get("healer", out healerId); p.Get("target", out targetId);
                        if (healerId <= 0 || targetId <= 0 || healerId == targetId)
                            return Fail("bad_args", "驱毒只能由当前 NPC 为另一名人物施行");
                        Character healer, target;
                        if (!DomainManager.Character.TryGetElement_Objects(healerId, out healer)
                            || healer == null
                            || !DomainManager.Character.TryGetElement_Objects(targetId, out target)
                            || target == null)
                            return Fail("invalid_char", "驱毒人物已失效");
                        PoisonInts before = target.GetPoisoned();
                        if (!before.IsNonZero())
                            return Fail("no_poison", "未执行：目标当前并未中毒，无需驱毒");
                        bool changed;
                        try { changed = healer.DoHealAction(context, EHealActionType.Detox, target); }
                        catch (Exception e)
                        { return Indeterminate("detox_indeterminate", "驱毒后的毒素状态无法确认:" + e.GetType().Name); }
                        PoisonInts after = target.GetPoisoned();
                        if (!changed || !PoisonReduced(before, after))
                            return Fail("detox_no_effect", "未执行：医毒造诣不足，未能驱除任何毒素");
                        var ok = new SerializableModData();
                        ok.Set("success", true);
                        ok.Set("message", "已驱毒（" + PoisonSummary(before) + "→" + PoisonSummary(after) + "）");
                        return ok;
                    }
                    case "regulate_breath":   // NPC 以本体疗愈公式为另一名在场人物调理内息。
                    {
                        int healerId = 0, targetId = 0;
                        p.Get("healer", out healerId); p.Get("target", out targetId);
                        if (healerId <= 0 || targetId <= 0 || healerId == targetId)
                            return Fail("bad_args", "调息只能由当前 NPC 为另一名人物施行");
                        Character healer, target;
                        if (!DomainManager.Character.TryGetElement_Objects(healerId, out healer)
                            || healer == null
                            || !DomainManager.Character.TryGetElement_Objects(targetId, out target)
                            || target == null)
                            return Fail("invalid_char", "调息人物已失效");
                        short before = target.GetDisorderOfQi();
                        if (before <= DisorderLevelOfQi.MinValue)
                            return Fail("no_qi_disorder", "未执行：目标当前内息顺畅，无需调息");
                        bool changed;
                        try { changed = healer.DoHealAction(context, EHealActionType.Breathing, target); }
                        catch (Exception e)
                        { return Indeterminate("regulate_breath_indeterminate", "调息后的内息状态无法确认:" + e.GetType().Name); }
                        short after = target.GetDisorderOfQi();
                        if (!changed || after >= before)
                            return Fail("regulate_breath_no_effect", "未执行：医毒造诣不足，未能缓解内息紊乱");
                        var ok = new SerializableModData();
                        ok.Set("success", true);
                        ok.Set("message", "已调息（内息紊乱"
                            + DisorderLevelOfQi.GetShowValue(before) + "→"
                            + DisorderLevelOfQi.GetShowValue(after) + "）");
                        return ok;
                    }
                    case "enmity":   // 结仇/说和：对玩家呈现为双方关系；底层 Enemy 位仍可能只存在于任一方向。
                    {
                        int npcId = 0, targetId = 0, make = 0;
                        p.Get("npc", out npcId); p.Get("target", out targetId); p.Get("make", out make);
                        Character a, b;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out a) || a == null ||
                            !DomainManager.Character.TryGetElement_Objects(targetId, out b) || b == null)
                            return Fail("invalid_char", "角色无效");
                        // 统一预检与执行的“双向任一边即为仇敌”语义。旧存档和游戏原生
                        // 行为都可能只写 target→npc；只看 npc→target 会让预检明明查到
                        // 仇怨，执行却回“本无仇怨可解”。
                        bool forward = DomainManager.Character.HasRelation(npcId, targetId, (ushort)32768);
                        bool reverse = DomainManager.Character.HasRelation(targetId, npcId, (ushort)32768);
                        bool already = forward || reverse;
                        if (make != 0)
                        {
                            if (already) return Fail("already_enemy", "二人本已结仇,无需再结");
                            DomainManager.Character.TryAddAndApplyOneWayRelation(context, npcId, targetId, (ushort)32768);
                            if (!DomainManager.Character.HasRelation(npcId, targetId, (ushort)32768)
                                && !DomainManager.Character.HasRelation(targetId, npcId, (ushort)32768))
                                return Indeterminate("enmity_postcondition_indeterminate", "结仇写 API 已返回但关系位未满足");
                        }
                        else
                        {
                            if (!already) return Fail("no_feud", "二人本无仇怨可解");
                            try
                            {
                                if (forward)
                                    DomainManager.Character.TryRemoveOneWayRelation(
                                        context, npcId, targetId, (ushort)32768);
                                if (reverse)
                                    DomainManager.Character.TryRemoveOneWayRelation(
                                        context, targetId, npcId, (ushort)32768);
                            }
                            catch (Exception ex)
                            {
                                return Indeterminate("reconcile_indeterminate",
                                    "化解仇怨时可能已部分改变关系:" + ex.GetType().Name);
                            }
                            if (DomainManager.Character.HasRelation(npcId, targetId, (ushort)32768)
                                || DomainManager.Character.HasRelation(targetId, npcId, (ushort)32768))
                                return Indeterminate("reconcile_postcondition_indeterminate", "化解写 API 已返回但双方仍存在仇敌关系位");
                        }
                        var okEnmity = new SerializableModData(); okEnmity.Set("success", true); okEnmity.Set("message", make != 0 ? "已结仇" : "已化解"); return okEnmity;
                    }
                    case "joinsect":   // 投奔太吾门派(OrgTemplateId=16)
                    {
                        int npcId = 0;
                        p.Get("npc", out npcId);
                        Character c;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out c) || c == null) return Fail("invalid_char", "角色无效");
                        DomainManager.Character.GmCmd_ForceChangeOrganization(context, npcId, (sbyte)16);
                        // 落地校验:确认其门派确实变为太吾村(16),否则如实回拒(不假报成功)
                        if (c.GetOrganizationInfo().OrgTemplateId != 16)
                            return Indeterminate("join_sect_postcondition_indeterminate", "改投写 API 已返回但组织状态未达预期");
                        break;
                    }
                    case "changeorgbyname":   // 投奔【指定门派】:按门派/据点名 force-change(GmCmd_ForceChangeOrganizationByName,遍历 Settlement 按名匹配)。名字没匹配上=false→如实回拒。
                    {
                        int npcIdO = 0; string sectName = null;
                        p.Get("npc", out npcIdO); p.Get("sect", out sectName);
                        Character cO;
                        if (!DomainManager.Character.TryGetElement_Objects(npcIdO, out cO) || cO == null) return Fail("invalid_char", "角色无效");
                        if (string.IsNullOrWhiteSpace(sectName)) return Fail("bad_args", "未指定门派名");
                        bool matched = DomainManager.Character.GmCmd_ForceChangeOrganizationByName(context, npcIdO, sectName.Trim());
                        if (!matched) return Fail("sect_not_found", "认不出「" + sectName + "」这个门派/据点,投不成");
                        break;
                    }
                    case "favor_of":   // 只读:NPC 对某人的好感(供第三方追杀/营救的前端预检);未互动/哨兵归一为 0。
                    {
                        int spF = 0, tgF = 0;
                        p.Get("self", out spF); p.Get("target", out tgF);
                        short fav0 = 0;
                        try { short f = DomainManager.Character.GetFavorability(spF, tgF); fav0 = (f == short.MinValue) ? (short)0 : f; } catch { fav0 = 0; }
                        var okF = new SerializableModData(); okF.Set("success", true); okF.Set("favor", (int)fav0); return okF;
                    }
                    case "sharesecret":   // 太吾套出一条秘闻(回传真实结果:广播态/已知会 false)
                    {
                        int taiwuId = 0, sid = 0;
                        p.Get("taiwu", out taiwuId); p.Get("sid", out sid);
                        Character t;
                        if (!DomainManager.Character.TryGetElement_Objects(taiwuId, out t) || t == null) return Fail("invalid_char", "太吾无效");
                        bool got = DomainManager.Information.GmCmd_MakeCharacterReceiveSecretInformation(context, taiwuId, (SecretInformationId)sid);
                        var rs = new SerializableModData(); rs.Set("success", got); rs.Set("message", got ? "ok" : "未获取(可能已知/广播态)"); return rs;
                    }
                    case "npcsecret":   // 太吾把自己知道的一桩秘闻讲给 NPC(NPC 收到)
                    {
                        int npcId = 0, sid = 0;
                        p.Get("npc", out npcId); p.Get("sid", out sid);
                        Character nc;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out nc) || nc == null) return Fail("invalid_char", "NPC 无效");
                        bool gotN = DomainManager.Information.GmCmd_MakeCharacterReceiveSecretInformation(context, npcId, (SecretInformationId)sid);
                        var rsN = new SerializableModData(); rsN.Set("success", gotN); rsN.Set("message", gotN ? "ok" : "未获取(可能已知/广播态)"); return rsN;
                    }
                    case "disclosesecret":   // NPC 把秘闻透露给第三方(回传真实结果)
                    {
                        int sid = 0, srcId = 0, targetId = 0;
                        p.Get("sid", out sid); p.Get("src", out srcId); p.Get("target", out targetId);
                        Character tg;
                        if (!DomainManager.Character.TryGetElement_Objects(targetId, out tg) || tg == null) return Fail("invalid_char", "目标无效");
                        bool ok2 = DomainManager.Information.DisseminateSecretInformation(context, (SecretInformationId)sid, srcId, targetId);
                        var rd = new SerializableModData(); rd.Set("success", ok2); rd.Set("message", ok2 ? "ok" : "未传播(对方已知情、或此秘已广为人知)"); return rd;
                    }
                    case "can_disclose_secret":   // 只读:讲述者确知、秘闻未广播且接收者尚未知情
                    {
                        int sid = 0, srcId = 0, targetId = 0;
                        p.Get("sid", out sid); p.Get("src", out srcId); p.Get("target", out targetId);
                        Character src, target;
                        if (!DomainManager.Character.TryGetElement_Objects(srcId, out src) || src == null
                            || !DomainManager.Character.TryGetElement_Objects(targetId, out target) || target == null
                            || srcId == targetId)
                            return Fail("invalid_char", "秘闻讲述者或接收者无效");

                        SecretInformationId secretId = (SecretInformationId)sid;
                        bool sourceKnows = false, targetKnows = false;
                        bool targetKnowsSameOccurrence = false;
                        bool broadcast = false;
                        SecretOccurenceId occurrenceId = SecretOccurenceId.Invalid;
                        var sourcePackage = DomainManager.Information.GetSecretInformationDisplayPackageFromCharacter(srcId);
                        if (sourcePackage != null && sourcePackage.SecretInformationDisplayDataList != null)
                            foreach (var item in sourcePackage.SecretInformationDisplayDataList)
                                if (item != null && item.SecretInformationId.Equals(secretId))
                                {
                                    sourceKnows = true;
                                    broadcast = item.IsInBroadcast;
                                    occurrenceId = item.OccurenceId;
                                    break;
                                }
                        var targetPackage = DomainManager.Information.GetSecretInformationDisplayPackageFromCharacter(targetId);
                        if (targetPackage != null && targetPackage.SecretInformationDisplayDataList != null)
                            foreach (var item in targetPackage.SecretInformationDisplayDataList)
                                if (item != null)
                                {
                                    if (item.SecretInformationId.Equals(secretId)) targetKnows = true;
                                    if (occurrenceId.Valid && item.OccurenceId.Equals(occurrenceId))
                                        targetKnowsSameOccurrence = true;
                                    if (targetKnows && targetKnowsSameOccurrence) break;
                                }

                        // b24185552 权威实现 ReceiveSecretInformation 还会拒绝婴幼儿，以及已经
                        // 知道同一 SecretOccurence 的另一个版本。预查必须与真正派发完全一致，
                        // 否则 Agent 会得到“可传播”后仍在落地阶段失败。
                        bool targetCanReceive = target.GetAgeGroup() != 0;
                        bool can = sourceKnows && !broadcast && targetCanReceive
                            && !targetKnows && !targetKnowsSameOccurrence;
                        string reason = !sourceKnows ? "讲述者并不确知这桩秘闻"
                            : broadcast ? "这桩秘闻已经广为人知"
                            : !targetCanReceive ? "接收者年纪尚幼，无法理解这桩秘闻"
                            : targetKnowsSameOccurrence ? "接收者早已知道同一件事"
                            : targetKnows ? "接收者早已知情" : "ok";
                        var check = new SerializableModData();
                        check.Set("success", true); check.Set("can", can); check.Set("message", reason);
                        return check;
                    }
                    case "sect_support":   // NPC 在其门派为太吾表态支持(提高太吾在该门派支持率;地位 Grade 越高 InfluencePower 越大、贡献越多)
                    {
                        int npcId = 0; p.Get("npc", out npcId);
                        Character c;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out c) || c == null) return Fail("invalid_char", "角色无效");
                        sbyte org = c.GetOrganizationInfo().OrgTemplateId;
                        if (!OrganizationDomain.IsSect(org)) return Fail("not_in_sect", "该角色不属任何门派,无法为太吾说项");
                        SettlementCharacter sc;
                        if (!DomainManager.Organization.TryGetSettlementCharacter(npcId, out sc) || sc == null) return Fail("no_settlement_char", "门派成员数据缺失");
                        if (DomainManager.Organization.GetPrisonerSect(npcId) >= 0) return Fail("imprisoned", "其正被囚禁,无法表态");
                        if (sc.GetApprovedTaiwu()) { var r0 = new SerializableModData(); r0.Set("success", true); r0.Set("message", "其本已支持太吾"); return r0; }
                        sc.SetApprovedTaiwu(context, true);
                        DomainManager.Organization.ForceUpdateInfluencePowers(context, false);   // 立即刷新影响力,使支持率即时反映
                        if (!sc.GetApprovedTaiwu())
                            return Indeterminate("sect_support_postcondition_indeterminate", "支持写 API 已返回但批准位未满足");
                        short contrib = sc.GetApprovingRate();
                        var settle = DomainManager.Organization.GetSettlementByOrgTemplateId(org);
                        short total = settle != null ? settle.CalcApprovingRate() : (short)0;
                        var rs = new SerializableModData(); rs.Set("success", true);
                        rs.Set("message", "已为太吾在本门表态(地位" + c.GetOrganizationInfo().Grade + ",贡献支持率" + contrib + ",门派现支持率" + total + ")");
                        return rs;
                    }
                    case "dissolve":   // 解除关系:用 Character.ApplySever*/ApplyEndRelation_* 带完整副作用断绝(降好感/反目/秘闻),先 HasRelation 校验免空操作
                    {
                        int npcId = 0, taiwuId = 0, targetId = 0; string rel = null;
                        p.Get("npc", out npcId); p.Get("taiwu", out taiwuId); p.Get("target", out targetId); p.Get("rel", out rel);
                        if (targetId <= 0) targetId = taiwuId;   // 默认解除与太吾的关系
                        Character self, target;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out self) || self == null ||
                            !DomainManager.Character.TryGetElement_Objects(targetId, out target) || target == null)
                            return Fail("invalid_char", "角色无效");
                        sbyte bt = self.GetBehaviorType();
                        bool selfTw = DomainManager.Character.IsTaiwuPeople(npcId);
                        bool tgtTw = DomainManager.Character.IsTaiwuPeople(targetId);
                        string relation = (rel ?? "").ToLowerInvariant();
                        ushort relationType;
                        try
                        {
                            switch (relation)
                            {
                                case "friend":
                                {
                                    relationType = 8192;
                                    bool selfToTarget = DomainManager.Character.HasRelation(
                                        npcId, targetId, relationType);
                                    bool targetToSelf = DomainManager.Character.HasRelation(
                                        targetId, npcId, relationType);
                                    if (!selfToTarget && !targetToSelf)
                                        return Fail("no_relation", "并无挚友之谊可绝");
                                    // Native sever guards target→self. Reverse arguments when only
                                    // the opposite legacy/native direction is present.
                                    if (targetToSelf)
                                        Character.ApplySeverFriend(context, self, target, bt, selfTw, tgtTw);
                                    else
                                        Character.ApplySeverFriend(context, target, self,
                                            target.GetBehaviorType(), tgtTw, selfTw);
                                    break;
                                }
                                case "sworn":
                                {
                                    relationType = 512;
                                    bool selfToTarget = DomainManager.Character.HasRelation(
                                        npcId, targetId, relationType);
                                    bool targetToSelf = DomainManager.Character.HasRelation(
                                        targetId, npcId, relationType);
                                    if (!selfToTarget && !targetToSelf)
                                        return Fail("no_relation", "并无结义之情可断");
                                    if (targetToSelf)
                                        Character.ApplySeverSwornBrotherOrSister(
                                            context, self, target, bt, selfTw, tgtTw);
                                    else
                                        Character.ApplySeverSwornBrotherOrSister(
                                            context, target, self, target.GetBehaviorType(),
                                            tgtTw, selfTw);
                                    break;
                                }
                                case "spouse":
                                {
                                    relationType = 1024;
                                    bool selfToTarget = DomainManager.Character.HasRelation(
                                        npcId, targetId, relationType);
                                    bool targetToSelf = DomainManager.Character.HasRelation(
                                        targetId, npcId, relationType);
                                    if (!selfToTarget && !targetToSelf)
                                        return Fail("no_relation", "并无夫妻之名可解");
                                    if (targetToSelf)
                                        Character.ApplySeverHusbandOrWife(
                                            context, self, target, bt, selfTw, tgtTw);
                                    else
                                        Character.ApplySeverHusbandOrWife(
                                            context, target, self, target.GetBehaviorType(),
                                            tgtTw, selfTw);
                                    break;
                                }
                                case "lover":
                                {
                                    relationType = 16384;
                                    // 恋人位 16384 是单向存储(opposite=0,不镜像),常落在
                                    // target→npc 方向；解除函数的原生守卫也按该方向读取。
                                    bool loveTN = DomainManager.Character.HasRelation(
                                        targetId, npcId, relationType);
                                    bool loveNT = DomainManager.Character.HasRelation(
                                        npcId, targetId, relationType);
                                    if (!loveTN && !loveNT) return Fail("no_relation", "并无恋慕可断");
                                    if (loveTN)
                                        Character.ApplyBreakupWithBoyOrGirlFriend(
                                            context, self, target, bt, false, selfTw, tgtTw);
                                    else
                                        Character.ApplyBreakupWithBoyOrGirlFriend(
                                            context, target, self, target.GetBehaviorType(),
                                            false, tgtTw, selfTw);
                                    break;
                                }
                                case "mentor":
                                    relationType = 2048;
                                    // 最新本体语义：2048(Mentor) 存于【徒弟→师父】方向，
                                    // ApplyEndRelation_Mentor 要求徒弟作为 self。
                                    if (DomainManager.Character.HasRelation(
                                        npcId, targetId, relationType))
                                        Character.ApplyEndRelation_Mentor(
                                            context, self, target, bt, selfTw, tgtTw);
                                    else if (DomainManager.Character.HasRelation(
                                        targetId, npcId, relationType))
                                        Character.ApplyEndRelation_Mentor(
                                            context, target, self, target.GetBehaviorType(),
                                            tgtTw, selfTw);
                                    else return Fail("no_relation", "并无师徒名分可断");
                                    break;
                                case "adoptive_parent":
                                    relationType = 64;
                                    if (!DomainManager.Character.HasRelation(npcId, targetId, relationType))
                                        return Fail("no_relation", "对方并非你的义父母，无此义亲名分可断");
                                    Character.ApplyEndRelation_AdoptiveParent(
                                        context, self, target, bt, selfTw, tgtTw);
                                    break;
                                case "adoptive_child":
                                    relationType = 128;
                                    if (!DomainManager.Character.HasRelation(npcId, targetId, relationType))
                                        return Fail("no_relation", "对方并非你的义子女，无此义亲名分可断");
                                    Character.ApplyEndRelation_AdoptiveChild(
                                        context, self, target, bt, selfTw, tgtTw);
                                    break;
                                case "enemy":
                                {
                                    relationType = 32768;
                                    bool selfToTarget = DomainManager.Character.HasRelation(
                                        npcId, targetId, relationType);
                                    bool targetToSelf = DomainManager.Character.HasRelation(
                                        targetId, npcId, relationType);
                                    if (!selfToTarget && !targetToSelf)
                                        return Fail("no_relation", "并无仇怨可解");
                                    // Enemy is directional. Resolve every existing direction,
                                    // matching the native monthly relation-removal behavior.
                                    if (selfToTarget)
                                        Character.ApplySeverEnemy(context, self, target, bt, selfTw);
                                    if (targetToSelf)
                                        Character.ApplySeverEnemy(context, target, self,
                                            target.GetBehaviorType(), tgtTw);
                                    break;
                                }
                                default: return Fail("bad_rel", "未知关系类型");
                            }
                        }
                        catch (Exception e)
                        {
                            relationType = RelationTypeForDissolve(relation);
                            if (relationType == 0
                                || HasRelationEither(npcId, targetId, relationType))
                                return Indeterminate("dissolve_indeterminate",
                                    "解除关系状态无法确认:" + e.GetType().Name);
                        }
                        relationType = RelationTypeForDissolve(relation);
                        if (relationType == 0) return Fail("bad_rel", "未知关系类型");
                        if (HasRelationEither(npcId, targetId, relationType))
                            return Indeterminate("dissolve_postcondition_indeterminate",
                                "解除关系接口已返回但关系仍然存在");
                        var rok = new SerializableModData(); rok.Set("success", true); rok.Set("message", "已断"); return rok;
                    }
                    case "teachlife":   // 传技艺:校验师父确通晓 + 受学者未学 → LearnLifeSkill。#1 NPC→第三方 NPC 继承师父造诣;NPC→太吾 现改默写成册不走此路径,兜底从 0 起。
                    {
                        int npcId = 0, taiwuId = 0, tpl = -1, rcpt = 0;
                        p.Get("npc", out npcId); p.Get("taiwu", out taiwuId); p.Get("tpl", out tpl); p.Get("recipient", out rcpt);
                        int learnerId = rcpt > 0 ? rcpt : taiwuId;   // recipient>0 → 传给第三方 NPC;否则传太吾
                        // b24185552 uses -1 as the invalid skill-template sentinel; template 0 is valid.
                        if (learnerId <= 0 || tpl < 0) return Fail("bad_args", "参数无效");
                        if (npcId == learnerId) return Fail("bad_args", "师徒同体");
                        Character npcC, twC;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out npcC) || npcC == null ||
                            !DomainManager.Character.TryGetElement_Objects(learnerId, out twC) || twC == null)
                            return Fail("invalid_char", "角色无效");
                        short stpl = (short)tpl;
                        bool npcKnows = false; byte npcProf = 0;
                        var npcLs = npcC.GetLearnedLifeSkills();
                        if (npcLs != null) foreach (var ls in npcLs) if (ls.SkillTemplateId == stpl) { npcKnows = true; npcProf = ls.ReadingState; break; }
                        if (!npcKnows) return Fail("npc_not_known", "NPC 并不通晓此技艺,无从传授");
                        if (twC.FindLearnedLifeSkillIndex(stpl) >= 0) return Fail("already", "对方已通晓此技艺");
                        byte prof = learnerId != taiwuId ? npcProf : (byte)0;   // 第三方 NPC 继承师父造诣;太吾(兜底)从 0 起
                        try { DomainManager.Character.LearnLifeSkill(context, learnerId, stpl, prof); }
                        catch (Exception e)
                        {
                            // LearnLifeSkill 对“已经学会”会在写入前抛异常；若调用后权威状态已经满足，
                            // 则说明本次写入已经落地，不把已完成动作误报成 UNKNOWN。
                            if (twC.FindLearnedLifeSkillIndex(stpl) < 0)
                                return Indeterminate("teach_life_indeterminate", "技艺传授状态无法确认:" + e.GetType().Name);
                        }
                        if (twC.FindLearnedLifeSkillIndex(stpl) < 0)
                            return Indeterminate("teach_life_postcondition_indeterminate", "技艺传授接口已返回但受学者仍未学会");
                        var tok = new SerializableModData(); tok.Set("success", true); tok.Set("message", "learned"); return tok;
                    }
                    case "trade":   // 商人被说动后主动售货给太吾:转移物品(背包/资源) + 太吾按议价付银钱。
                        // 绕开 SettleTrade:先按 NPC 实有封顶并计算实际价款、再校验太吾钱够;
                        // 部分成交按量向上折价，非零总价不会因整数截断变成免费。NPC 为动作主体,契合 NPC-driven。
                    {
                        int npcId = 0, taiwuId = 0, it = 0, modSt = 0, t = 0, id = 0, amount = 0, price = 0, fromGoods = 0;
                        int expectedOwnerType = -1, expectedOwnerId = -1;
                        p.Get("npc", out npcId); p.Get("taiwu", out taiwuId);
                        p.Get("it", out it); p.Get("mod", out modSt); p.Get("t", out t); p.Get("id", out id);
                        p.Get("amount", out amount); p.Get("price", out price); p.Get("from_goods", out fromGoods);
                        if (!p.Get("owner_type", out expectedOwnerType)) expectedOwnerType = -1;
                        if (!p.Get("owner_id", out expectedOwnerId)) expectedOwnerId = -1;
                        if (amount <= 0) amount = 1;
                        if (price < 0) price = 0;
                        Character npcC, twC;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out npcC) || npcC == null ||
                            !DomainManager.Character.TryGetElement_Objects(taiwuId, out twC) || twC == null)
                            return Fail("invalid_char", "角色无效");
                        int twMoney = twC.GetResource(6);   // 太吾现有银钱
                        int gave = 0, pay = 0;
                        int npcMoney = npcC.GetResource(6);
                        int sellerBefore = 0, buyerBefore = 0;
                        int tradeTransferKind = 0; // 1=货架实物,2=角色资源,3=角色背包
                        ItemKey tradedKey = default(ItemKey);
                        sbyte tradedResourceType = (sbyte)(-1);
                        GameData.Domains.Merchant.MerchantData tradedMerchantData = null;
                        int tradedGoodsIndex = -1, tradedGoodsBefore = 0;
                        int tradedOwnerType = -1, tradedOwnerId = -1;
                        int tplT = -1; p.Get("tpl", out tplT);   // 商人模板(货未生成时供解析)
                        // 真·行商货架售货(from_goods):把货架上那件【真货】移给太吾并从货架扣库存(同游戏 SettleTrade/DeleteCaravanItem:清归属→入背包→OfflineRemove→持久化)
                        if (fromGoods != 0)
                        {
                            int ot, oid;
                            // 成交也只能消费 query_merchant_goods 已能只读看见的既有货架。
                            // 若在这里才生成/换季刷新，随后又因物品不匹配返回 sold_out，
                            // 就会产生“失败回执但 RNG/商店已写”的隐藏部分副作用。
                            var smd = ResolveExistingMerchantStore(npcId, out ot, out oid);
                            if (smd == null) return Fail("sold_out", "货架上没有现货");
                            if ((expectedOwnerType >= 0 && expectedOwnerType != ot)
                                || (expectedOwnerId >= 0 && expectedOwnerId != oid))
                                return Fail("merchant_source_changed", "商人的货架来源已经变化，请重新查看现货后再成交");
                            ItemKey realKey = default(ItemKey); int gi2 = -1, have2 = 0; bool found = false;
                            for (int gi = 0; gi <= 6 && !found; gi++)
                            {
                                var inv = smd.GetGoodsList(gi);
                                if (inv == null || inv.Items == null) continue;
                                foreach (var kv in inv.Items)
                                {
                                    var k = kv.Key;
                                    // 1.0.72 的 bit8“额外商品”还绑定 ExtraDomain 登记；原生
                                    // 成交会注销登记，且可堆叠模板会删除该唯一实体再创建普通栈。
                                    // 本自定义议价没有可逆的实体删除事务，因此必须只消费普通货架，
                                    // 不能留下悬空登记或把额外商品标记带进太吾背包。
                                    if (!IsSupportedCustomMerchantShelfItem(k)) continue;
                                    if (k.ItemType == (sbyte)it && k.TemplateId == (short)t) { realKey = k; gi2 = gi; have2 = kv.Value; found = true; break; }
                                }
                            }
                            if (!found || have2 <= 0) return Fail("sold_out", "此货已售罄");
                            gave = amount < have2 ? amount : have2;
                            pay = CalculateTradePayment(amount, price, gave, twMoney);
                            if (pay > 0 && npcMoney > NativeResourceCap - pay)
                                return Fail("merchant_money_full", "商人银钱已接近上限，无法完整收款");
                            ItemOwnerType merchantOwnerType =
                                ot == 1 ? ItemOwnerType.Caravan : ItemOwnerType.Merchant;
                            if (!ItemOwnerMatches(realKey, merchantOwnerType, oid))
                                return Fail("merchant_owner_changed", "货物持有人状态已变化，请重新查看现货");
                            sellerBefore = have2;
                            buyerBefore = InventoryCount(twC, realKey);
                            tradeTransferKind = 1;
                            tradedKey = realKey;
                            tradedMerchantData = smd;
                            tradedGoodsIndex = gi2;
                            tradedGoodsBefore = have2;
                            tradedOwnerType = ot;
                            tradedOwnerId = oid;
                            try
                            {
                                var baseItem = DomainManager.Item.GetBaseItem(realKey);
                                if (baseItem != null) baseItem.RemoveOwner(merchantOwnerType, oid);   // 纯堆叠为空操作;同 SettleTrade/DeleteCaravanItem
                                smd.GetGoodsList(gi2).OfflineRemove(realKey, gave);   // 先扣【实时】货架字典(同 DeleteCaravanItem 顺序,消除瞬时重影窗口)
                                int goodsAfter = 0;
                                smd.GetGoodsList(gi2).Items.TryGetValue(realKey, out goodsAfter);
                                if (goodsAfter != have2 - gave)
                                    throw new InvalidOperationException("货架扣减未完整落地");
                                if (!twC.AddInventoryItem(context, realKey, gave, false, GameData.Domains.Taiwu.EItemAutoOperationSource.Invalid))
                                    throw new InvalidOperationException("太吾背包拒绝货物");
                                if (InventoryCount(twC, realKey) != buyerBefore + gave
                                    || !TryEnsureInventoryOwner(realKey, taiwuId))
                                    throw new InvalidOperationException("太吾背包或唯一物品持有人未完整落地");
                                PersistMerchantData(context, ot, oid, smd);
                            }
                            catch (Exception me)
                            {
                                if (TryRestoreMerchantGoodsTrade(context, twC, smd, gi2, realKey,
                                        have2, buyerBefore, ot, oid))
                                    return Fail("trade_move_failed",
                                        "货架扣减或入包未完成，已恢复原库存:" + me.GetType().Name);
                                return Indeterminate("trade_move_indeterminate",
                                    "货架扣减或入包状态无法确认且补偿未完整恢复:" + me.GetType().Name);
                            }
                        }
                        else if (ItemTemplateHelper.IsMiscResource((sbyte)it, (short)t))
                        {
                            sbyte resType = (sbyte)t;
                            int have = npcC.GetResource(resType);
                            if (have <= 0) return Fail("sold_out", "此货已售罄");
                            gave = amount < have ? amount : have;
                            pay = CalculateTradePayment(amount, price, gave, twMoney);
                            if (pay > 0 && npcMoney > NativeResourceCap - pay)
                                return Fail("merchant_money_full", "商人银钱已接近上限，无法完整收款");
                            sellerBefore = have;
                            buyerBefore = twC.GetResource(resType);
                            tradeTransferKind = 2;
                            tradedResourceType = resType;
                            TransferResourceChecked(context, npcC, twC, resType, gave);
                        }
                        else
                        {
                            var reqKey = new ItemKey((sbyte)it, (byte)modSt, (short)t, id);
                            ItemKey realKey; int have;
                            if (!FindOwnedItem(npcC, reqKey, out realKey, out have)) return Fail("sold_out", "你并无此货,无法成交");
                            gave = amount < have ? amount : have;
                            if (gave <= 0) return Fail("sold_out", "此货已无存货");
                            pay = CalculateTradePayment(amount, price, gave, twMoney);
                            if (pay > 0 && npcMoney > NativeResourceCap - pay)
                                return Fail("merchant_money_full", "商人银钱已接近上限，无法完整收款");
                            sellerBefore = InventoryCount(npcC, realKey);
                            buyerBefore = InventoryCount(twC, realKey);
                            tradeTransferKind = 3;
                            tradedKey = realKey;
                            TransferInventoryChecked(context, npcC, twC, realKey, gave,
                                TransferIntent.Trade);
                        }
                        if (pay > 0)
                        {
                            try { TransferResourceChecked(context, twC, npcC, (sbyte)6, pay); }
                            catch (TransferPreconditionException paymentError)
                            {
                                bool restored = tradeTransferKind == 1
                                    ? TryRestoreMerchantGoodsTrade(context, twC, tradedMerchantData,
                                        tradedGoodsIndex, tradedKey, tradedGoodsBefore, buyerBefore,
                                        tradedOwnerType, tradedOwnerId)
                                    : tradeTransferKind == 2
                                        ? TryRestoreResourcePair(context, npcC, twC, tradedResourceType,
                                            sellerBefore, buyerBefore)
                                        : tradeTransferKind == 3
                                            && TryRestoreInventoryPair(context, npcC, twC, tradedKey,
                                                sellerBefore, buyerBefore,
                                                restoreSourceInventoryOwner: true);
                                if (restored)
                                    return Fail("trade_payment_failed",
                                        "付款未发生，已完整撤销货物转移:" + paymentError.Message);
                                return Indeterminate("trade_rollback_indeterminate",
                                    "付款未发生，但货物补偿未完整恢复:" + paymentError.GetType().Name);
                            }
                            catch (Exception paymentError)
                            {
                                // TransferResourceChecked 只有在付款发生未知的部分写入且自身补偿
                                // 未能验证时才会到这里；此时不能再撤销货物，否则可能造成收款成功但
                                // 货物被收回的另一种单边交易。
                                return Indeterminate("trade_payment_indeterminate",
                                    "货物已交付，但付款状态无法确认:" + paymentError.GetType().Name);
                            }
                        }
                        var rtr = new SerializableModData(); rtr.Set("success", true); rtr.Set("message", "ok"); rtr.Set("amount", gave); rtr.Set("price", pay); return rtr;
                    }
                    case "merchant_goods":   // 查看真实货架：等价于打开原生商店，允许首次建立/按季刷新库存
                    {
                        int npcId = 0, tplM = -1; p.Get("npc", out npcId); p.Get("tpl", out tplM);
                        var parts = new List<string>();
                        var seen = new HashSet<int>();
                        int ot, oid;
                        // 先按同地、同商号识别行商；没有行商时才初始化普通角色商店。
                        // 返回 owner_type/owner_id，随后的 trade 必须消费同一货架来源。
                        var md = ResolveMerchantStoreForQuery(context, npcId, out ot, out oid);
                        if (md != null) CollectMerchantGoods(md, parts, seen);
                        var okMg = new SerializableModData(); okMg.Set("success", true);
                        okMg.Set("goods", string.Join(";", parts));
                        okMg.Set("owner_type", md == null ? -1 : ot);
                        okMg.Set("owner_id", md == null ? -1 : oid);
                        return okMg;
                    }
                    case "taiwu_money":   // 只读:太吾现有银钱(资源位6),供商人议价前判太吾买不买得起
                    {
                        int taiwuId = 0; p.Get("taiwu", out taiwuId);
                        Character tw;
                        if (!DomainManager.Character.TryGetElement_Objects(taiwuId, out tw) || tw == null) return Fail("invalid_char", "太吾无效");
                        var rm2 = new SerializableModData(); rm2.Set("success", true); rm2.Set("money", tw.GetResource(6)); return rm2;
                    }
                    case "commission_snapshot":
                    {
                        int taiwuId = 0, npcId = 0, targetId = -1;
                        p.Get("taiwu", out taiwuId); p.Get("npc", out npcId);
                        p.Get("target", out targetId);
                        bool assistantCommission = npcId == 0;
                        Character tw, npc = null;
                        if (taiwuId <= 0 || npcId < 0 || taiwuId == npcId
                            || !DomainManager.Character.TryGetElement_Objects(taiwuId, out tw) || tw == null
                            || (!assistantCommission
                                && (!DomainManager.Character.TryGetElement_Objects(npcId, out npc)
                                    || npc == null)))
                            return Fail("invalid_char", "委托人物已失效");
                        var resources = new string[8];
                        for (sbyte resourceType = 0; resourceType < 8; resourceType++)
                            resources[resourceType] = tw.GetResource(resourceType).ToString(CultureInfo.InvariantCulture);
                        int favor = 0;
                        try
                        {
                            if (!assistantCommission)
                            {
                                short rawFavor = DomainManager.Character.GetFavorability(npcId, taiwuId);
                                favor = rawFavor == short.MinValue ? 0 : rawFavor;
                            }
                        }
                        catch { favor = 0; }
                        var snapshot = new SerializableModData();
                        snapshot.Set("success", true);
                        snapshot.Set("resources", string.Join(",", resources));
                        snapshot.Set("favor", favor);
                        snapshot.Set("world_progress", (int)DomainManager.World.GetXiangshuLevel());
                        bool targetValid = false, targetDead = false;
                        int targetConsummate = -1, targetRewardGrade = -1;
                        if (targetId > 0 && targetId != taiwuId && targetId != npcId)
                        {
                            Character target;
                            targetValid = DomainManager.Character.TryGetElement_Objects(
                                targetId, out target) && target != null;
                            if (targetValid)
                            {
                                targetConsummate = target.GetConsummateLevel();
                                try
                                {
                                    targetRewardGrade = Config.ConsummateLevel.Instance[
                                        (sbyte)targetConsummate].Grade;
                                }
                                catch
                                {
                                    targetRewardGrade = Math.Max(0, Math.Min(8,
                                        targetConsummate <= 3 ? 0 : (targetConsummate - 2) / 2));
                                }
                            }
                            else targetDead = DomainManager.Character.TryGetDeadCharacter(targetId) != null;
                        }
                        snapshot.Set("target_valid", targetValid);
                        snapshot.Set("target_dead", targetDead);
                        snapshot.Set("target_id", targetId);
                        snapshot.Set("target_consummate", targetConsummate);
                        snapshot.Set("target_reward_grade", targetRewardGrade);
                        return snapshot;
                    }
                    case "commission_claim":
                        return ClaimCommissionReward(context, p);
                    case "taiwu_give_item":   // 太吾把自己的物品/资源送给 NPC(NPC 接受)。先校验太吾确有此物,按实有封顶。
                    {
                        int npcId = 0, taiwuId = 0, it = 0, modSt = 0, t = 0, id = 0, want = 0;
                        p.Get("npc", out npcId); p.Get("taiwu", out taiwuId);
                        p.Get("it", out it); p.Get("mod", out modSt); p.Get("t", out t); p.Get("id", out id); p.Get("amount", out want);
                        if (want <= 0) want = 1;
                        Character src, dst;
                        if (!DomainManager.Character.TryGetElement_Objects(taiwuId, out src) || src == null ||
                            !DomainManager.Character.TryGetElement_Objects(npcId, out dst) || dst == null)
                            return Fail("invalid_char", "角色无效");
                        if (it == 12 && t == 6)   // 银钱(资源槽6=Money):太吾给 NPC 些银钱/盘缠
                        {
                            int haveMoney = src.GetResource((sbyte)6);
                            if (haveMoney <= 0) return Fail("not_owned", "太吾身上没有银钱,无可相赠");
                            int giveMoney = want < haveMoney ? want : haveMoney;
                            TransferResourceChecked(context, src, dst, (sbyte)6, giveMoney);
                            var okM = new SerializableModData(); okM.Set("success", true); okM.Set("message", "ok"); okM.Set("amount", giveMoney); return okM;
                        }
                        if (ItemTemplateHelper.IsMiscResource((sbyte)it, (short)t))   // 食材/药材/布料/矿石等资源
                        {
                            sbyte resType = (sbyte)t; int haveRes = src.GetResource(resType);
                            if (haveRes <= 0) return Fail("not_owned", "太吾此资源已无,无可相赠");
                            int giveRes = want < haveRes ? want : haveRes;
                            TransferResourceChecked(context, src, dst, resType, giveRes);
                            var okR = new SerializableModData(); okR.Set("success", true); okR.Set("message", "ok"); okR.Set("amount", giveRes); return okR;
                        }
                        var reqKey = new ItemKey((sbyte)it, (byte)modSt, (short)t, id);
                        ItemKey realKey; int have;
                        if (FindOwnedItem(src, reqKey, out realKey, out have) && have > 0)
                        {
                            int give = want < have ? want : have;
                            ApplyTransferThing(context, taiwuId, src, dst,
                                new TransferThing { Key = realKey, Amount = give },
                                TransferIntent.Gift);   // 太吾为源:仅真实赠礼保留本体赠礼登记
                            var okI = new SerializableModData(); okI.Set("success", true); okI.Set("message", "ok"); okI.Set("amount", give); return okI;
                        }
                        // 不在背包 → 可能是太吾当前穿戴/武具页装备;先卸下再转移。
                        sbyte twEqSlot = FindEquippedSlot(src, reqKey);
                        if (twEqSlot >= 0)
                        {
                            var wornKey = src.GetEquipment()[twEqSlot];
                            ApplyTransferThing(context, taiwuId, src, dst, new TransferThing
                            {
                                Key = wornKey, Amount = 1, IsEquipped = true, EquipSlot = twEqSlot
                            }, TransferIntent.Gift);
                            var okW = new SerializableModData(); okW.Set("success", true); okW.Set("message", "ok"); okW.Set("amount", 1); return okW;
                        }
                        // 不在背包 → 可能是随身熟食(荷包蛋等),存于 EatingItems:从太吾餐位移除,放进 NPC 背包
                        int eslot = src.GetEatingItems().IndexOf(reqKey);
                        if (eslot >= 0)
                        {
                            try
                            {
                                if (EatingItems.IsWug(reqKey)) return Fail("unsupported_item", "蛊虫不能作为普通物品相赠");
                                ApplyTransferThing(context, taiwuId, src, dst, new TransferThing
                                {
                                    Key = reqKey, Amount = 1, IsEating = true, EatingSlot = eslot,
                                    EatingDuration = src.GetEatingItems().GetDuration(eslot)
                                }, TransferIntent.Gift);
                                var okE = new SerializableModData(); okE.Set("success", true); okE.Set("message", "ok"); okE.Set("amount", 1); return okE;
                            }
                            catch (Exception fe) { return Indeterminate("food_transfer_indeterminate", "熟食转移状态无法确认:" + fe.GetType().Name); }
                        }
                        return Fail("not_owned", "太吾并无此物,无可相赠");
                    }
                    case "barter":   // 任意两人以物换物:A 给 B 一物,B 给 A 一物。先双边预检,再落地转移。
                    {
                        int aId = 0, bId = 0, aWant = 0, bWant = 0;
                        string aItem = null, bItem = null;
                        p.Get("a", out aId); p.Get("b", out bId);
                        p.Get("a_item", out aItem); p.Get("b_item", out bItem);
                        p.Get("a_amount", out aWant); p.Get("b_amount", out bWant);
                        if (aWant <= 0) aWant = 1;
                        if (bWant <= 0) bWant = 1;
                        if (aId <= 0 || bId <= 0 || aId == bId) return Fail("bad_args", "交换双方无效");
                        if (string.IsNullOrWhiteSpace(aItem) || string.IsNullOrWhiteSpace(bItem)) return Fail("bad_args", "未指明双方要交换之物");
                        Character ca, cb;
                        if (!DomainManager.Character.TryGetElement_Objects(aId, out ca) || ca == null ||
                            !DomainManager.Character.TryGetElement_Objects(bId, out cb) || cb == null)
                            return Fail("invalid_char", "角色无效");

                        TransferThing ta = null, tb = null; string msgA = null, msgB = null;
                        bool gotA = TryResolveTransferThing(ca, aItem, aWant, out ta, out msgA);
                        bool gotB = gotA && TryResolveTransferThing(cb, bItem, bWant, out tb, out msgB);
                        bool autoSwapped = false;
                        if (!gotA || !gotB)
                        {
                            // 模型常把“太吾用 A 换 NPC 的 B”填成 person/item 方向反了。只有反向两边都真实持有时才自动纠正。
                            TransferThing sa, sb; string smA, smB;
                            bool swapA = TryResolveTransferThing(ca, bItem, bWant, out sa, out smA);
                            bool swapB = TryResolveTransferThing(cb, aItem, aWant, out sb, out smB);
                            if (swapA && swapB)
                            {
                                ta = sa; tb = sb;
                                string ti = aItem; aItem = bItem; bItem = ti;
                                autoSwapped = true;
                            }
                            else
                            {
                                if (!gotA) return Fail("a_not_owned", "甲方此刻并无「" + (aItem ?? "").Trim() + "」可换出:" + msgA);
                                return Fail("b_not_owned", "乙方此刻并无「" + (bItem ?? "").Trim() + "」可换出:" + msgB);
                            }
                        }
                        try
                        {
                            ValidateTransferThingPreconditions(aId, ca, cb, ta);
                            ValidateTransferThingPreconditions(bId, cb, ca, tb);
                        }
                        catch (TransferPreconditionException pe)
                        {
                            return Fail("barter_preflight_failed",
                                "交换双方预检未通过，没有物品落地:" + pe.Message);
                        }

                        TransferReceipt firstReceipt = null;
                        try
                        {
                            firstReceipt = ApplyTransferThing(context, aId, ca, cb, ta,
                                TransferIntent.Barter);
                            ApplyTransferThing(context, bId, cb, ca, tb,
                                TransferIntent.Barter);
                        }
                        catch (Exception be)
                        {
                            // DataContext 不提供跨两个 CharacterDomain 调用的事务回滚。只有确认第二边
                            // 零写入时才能主动撤销第一边；未知状态必须立即停写。
                            if (firstReceipt != null)
                            {
                                // checked helpers 只有在确认第二边零写入或已完整恢复时才抛
                                // TransferPreconditionException。其他异常代表第二边可能仍有部分写入；
                                // 此时绝不能再用未知状态作为输入反转第一边，否则会扩大损坏。
                                if (!(be is TransferPreconditionException))
                                    return Indeterminate("barter_second_transfer_indeterminate",
                                        "交换第二边可能已部分落地，已停止任何追加补偿写入:"
                                        + be.GetType().Name);
                                try { RollbackTransferThing(context, firstReceipt); }
                                catch (Exception re)
                                {
                                    return Indeterminate("barter_rollback_indeterminate", "交换中断且补偿回滚失败:" + be.GetType().Name + "/" + re.GetType().Name);
                                }
                                return Fail("barter_failed",
                                    "交换第二边在写入前失败，且已完整撤销先行转移:"
                                    + be.GetType().Name);
                            }
                            // 第一边只有在 ApplyTransferThing 完整返回后才会赋 firstReceipt；若其在写后验
                            // 抛错，这里无法判定是否已有物品落地，也没有安全回滚凭据。
                            if (be is TransferPreconditionException)
                                return Fail("barter_failed_before_transfer", "交换第一边在写入前即失败，没有物品落地:" + be.GetType().Name);
                            return Indeterminate("barter_first_transfer_indeterminate", "交换第一边状态无法确认:" + be.GetType().Name);
                        }
                        var okB = new SerializableModData();
                        okB.Set("success", true); okB.Set("message", autoSwapped ? "ok_swapped_direction" : "ok");
                        okB.Set("a_amount", ta.Amount); okB.Set("b_amount", tb.Amount);
                        okB.Set("a_name", ta.DisplayName ?? aItem); okB.Set("b_name", tb.DisplayName ?? bItem);
                        return okB;
                    }
                    case "steal":   // 任意两人偷窃:使用 1.0.72 本体的物品警觉度 + 三阶段检定；失败降 victim→thief 好感。
                    {
                        int thiefId = 0, victimId = 0, want = 0; string item = null;
                        p.Get("thief", out thiefId); p.Get("victim", out victimId); p.Get("item", out item); p.Get("amount", out want);
                        if (want <= 0) want = 1;
                        if (thiefId <= 0 || victimId <= 0 || thiefId == victimId) return Fail("bad_args", "偷窃双方无效");
                        if (string.IsNullOrWhiteSpace(item)) return Fail("bad_args", "未指明要偷之物");
                        Character thief, victim;
                        if (!DomainManager.Character.TryGetElement_Objects(thiefId, out thief) || thief == null ||
                            !DomainManager.Character.TryGetElement_Objects(victimId, out victim) || victim == null)
                            return Fail("invalid_char", "角色无效");
                        TransferThing thing; string fail;
                        if (!TryResolveTransferThing(victim, item, want, out thing, out fail))
                            return Fail("not_owned", "对方此刻并无「" + (item ?? "").Trim() + "」可偷:" + fail);

                        StealCheck check;
                        sbyte phase;
                        try
                        {
                            // EventHelper.GetStealActionPhase 先把有护卫的目标替换成权威护卫，再进入
                            // Character.GetStealActionPhase。这里显式保留同一计算目标，避免为了回传概率
                            // 重复抽一次护卫随机数；真正成败仍由本体方法执行。
                            Character calculationTarget = victim.GetGuardForCalculation(context.Random);
                            int alertFactor = thing.IsResource
                                ? victim.GetResourceAlertFactor(thing.ResourceType)
                                : victim.GetItemAlertFactor(thing.Key, thing.Amount);
                            check = BuildStealCheck(thiefId, victimId, thief, calculationTarget, alertFactor);
                            phase = thief.GetStealActionPhase(context.Random, calculationTarget, alertFactor, false);
                        }
                        catch (Exception ce)
                        {
                            return Indeterminate("steal_native_check_indeterminate",
                                "本体偷窃检定状态无法确认:" + ce.GetType().Name);
                        }
                        bool ok = phase == 5;
                        if (!ok)
                        {
                            try { DomainManager.Character.ChangeFavorabilityOptional(context, victim, thief, (short)-2000, -1); }
                            catch (Exception fe)
                            {
                                return Indeterminate("steal_detection_penalty_indeterminate",
                                    "偷窃已败露，但受害者态度变化状态无法确认:" + fe.GetType().Name);
                            }
                            var failRet = new SerializableModData();
                            failRet.Set("success", false);
                            failRet.Set("message", "偷窃败露");
                            failRet.Set("detected", true);
                            WriteStealCheck(failRet, check, phase);
                            return failRet;
                        }
                        try
                        {
                            ApplyTransferThing(context, victimId, victim, thief, thing,
                                TransferIntent.Steal);
                        }
                        catch (Exception se) { return Indeterminate("steal_transfer_indeterminate", "偷窃转移状态无法确认:" + se.GetType().Name); }
                        var okS = new SerializableModData();
                        okS.Set("success", true); okS.Set("message", "ok");
                        okS.Set("amount", thing.Amount);
                        okS.Set("name", thing.DisplayName ?? item);
                        WriteStealCheck(okS, check, phase);
                        return okS;
                    }
                    case "taiwu_teach":   // 太吾把自己会的一门武学/技艺传给 NPC(NPC 学到,造诣继承太吾)。先校验太吾确通晓。
                    {
                        int npcId = 0, taiwuId = 0, tpl = -1; string kind = null;
                        p.Get("npc", out npcId); p.Get("taiwu", out taiwuId); p.Get("tpl", out tpl); p.Get("kind", out kind);
                        // Config skill tables are zero-based; only a negative template id is missing.
                        if (tpl < 0) return Fail("bad_args", "未指定要传的武学/技艺");
                        Character taiwu2, npc2;
                        if (!DomainManager.Character.TryGetElement_Objects(taiwuId, out taiwu2) || taiwu2 == null ||
                            !DomainManager.Character.TryGetElement_Objects(npcId, out npc2) || npc2 == null)
                            return Fail("invalid_char", "角色无效");
                        short tplS = (short)tpl;
                        if (kind == "life")
                        {
                            byte prof = 0; bool known = false;
                            var taiwuLifeSkills = taiwu2.GetLearnedLifeSkills();
                            if (taiwuLifeSkills != null)
                                foreach (var ls in taiwuLifeSkills)
                                    if (ls.SkillTemplateId == tplS)
                                    { prof = ls.ReadingState; known = true; break; }
                            if (!known) return Fail("not_known", "太吾并未通晓此技艺");
                            if (npc2.FindLearnedLifeSkillIndex(tplS) >= 0) return Fail("already", "对方已通晓此技艺");
                            try { DomainManager.Character.LearnLifeSkill(context, npcId, tplS, prof); }
                            catch (Exception e)
                            {
                                if (npc2.FindLearnedLifeSkillIndex(tplS) < 0)
                                    return Indeterminate("teach_life_indeterminate", "技艺传授状态无法确认:" + e.GetType().Name);
                            }
                            if (npc2.FindLearnedLifeSkillIndex(tplS) < 0)
                                return Indeterminate("teach_life_postcondition_indeterminate", "技艺传授接口已返回但受学者仍未学会");
                        }
                        else
                        {
                            if (!HasLearnedCombatSkill(taiwu2, tplS)) return Fail("not_known", "太吾并未习得此武学");
                            if (HasLearnedCombatSkill(npc2, tplS)) return Fail("already", "对方已习得此武学");
                            ushort prof = 0;
                            var cs = DomainManager.CombatSkill.GetCharCombatSkills(taiwuId);
                            if (cs != null && cs.ContainsKey(tplS)) prof = cs[tplS].GetReadingState();
                            try { DomainManager.Character.LearnCombatSkill(context, npcId, tplS, prof); }
                            catch (Exception e)
                            {
                                if (!HasLearnedCombatSkill(npc2, tplS))
                                    return Indeterminate("teach_combat_indeterminate", "武学传授状态无法确认:" + e.GetType().Name);
                            }
                            if (!HasLearnedCombatSkill(npc2, tplS))
                                return Indeterminate("teach_combat_postcondition_indeterminate", "武学传授接口已返回但受学者仍未学会");
                        }
                        var okT = new SerializableModData(); okT.Set("success", true); okT.Set("message", "ok"); return okT;
                    }
                    case "query_npc_study_progress":   // 只读：NPC 尚未完成的武学修炼与背包阅读候选。
                    {
                        int charId = 0;
                        p.Get("char", out charId);
                        if (charId <= 0) return Fail("bad_args", "未指定人物");
                        Character ch;
                        if (!DomainManager.Character.TryGetElement_Objects(charId, out ch) || ch == null)
                            return Fail("invalid_char", "角色无效");
                        var incompleteSkills = new List<short>();
                        var unreadBooks = new SortedSet<string>(StringComparer.Ordinal);
                        try
                        {
                            var skills = DomainManager.CombatSkill.GetCharCombatSkills(charId);
                            if (skills != null)
                                foreach (var pair in skills)
                                {
                                    var skill = pair.Value;
                                    if (skill == null || skill.GetRevoked()) continue;
                                    ushort beforeReading = skill.GetReadingState();
                                    ushort beforeActivation = skill.GetActivationState();
                                    sbyte beforeBreakout = skill.GetBreakoutStepsCount();
                                    ushort completeReading = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                        .CompleteReadingState;
                                    sbyte outline = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                        .GetActiveOutlinePageType(beforeActivation);
                                    if (outline < 0 || outline > 4) outline = ch.GetBehaviorType();
                                    if (outline < 0 || outline > 4) outline = 0;
                                    ushort completeActivation = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                        .SetPageActive(0, global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                            .GetOutlinePageInternalIndex(outline));
                                    sbyte direction = skill.GetDirection();
                                    if (direction != 0 && direction != 1)
                                        direction = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                            .GetCombatSkillDirection(beforeActivation);
                                    if (direction != 0 && direction != 1) direction = 0;
                                    for (byte page = 1; page <= 5; page++)
                                    {
                                        sbyte activeDirection = ActiveNormalPageDirection(beforeActivation, page);
                                        if (activeDirection != 0 && activeDirection != 1)
                                            activeDirection = direction;
                                        completeActivation = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                            .SetPageActive(completeActivation,
                                                global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                                    .GetNormalPageInternalIndex(activeDirection, page));
                                    }
                                    sbyte completeBreakout = ch.GetSkillBreakoutAvailableStepsCount(pair.Key);
                                    if (beforeReading != completeReading
                                        || beforeActivation != completeActivation
                                        || beforeBreakout != completeBreakout)
                                        incompleteSkills.Add(pair.Key);
                                }

                            foreach (var pair in ch.GetInventory().Items)
                            {
                                if (pair.Value <= 0 || pair.Key.ItemType != 10) continue;
                                var book = DomainManager.Item.TryGetBaseItem(pair.Key)
                                    as GameData.Domains.Item.SkillBook;
                                if (book == null) continue;
                                int maxPages = book.IsCombatSkillBook() ? 6 : 5;
                                int readingPage = book.IsCombatSkillBook()
                                    ? ch.GetCombatSkillBookCurrReadingInfo(book).readingPage
                                    : ch.GetLifeSkillBookCurrReadingInfo(book).readingPage;
                                if (readingPage >= maxPages) continue;
                                string name = null;
                                try { name = StripTags(ItemTemplateHelper.GetName(
                                    pair.Key.ItemType, pair.Key.TemplateId)); } catch { }
                                if (!string.IsNullOrWhiteSpace(name)) unreadBooks.Add(name.Trim());
                            }
                        }
                        catch (Exception e)
                        {
                            return Fail("query_failed", "修炼与阅读进度读取失败:" + e.GetType().Name);
                        }
                        incompleteSkills.Sort();
                        var study = new SerializableModData();
                        study.Set("success", true);
                        study.Set("message", "ok");
                        study.Set("skill_ids", string.Join(",", incompleteSkills));
                        study.Set("book_names", string.Join(((char)1).ToString(), unreadBooks));
                        return study;
                    }
                    case "query_flip_practice":   // 只读：列出至少有一个当前激活常页可颠倒、且对应反页已读的武学。
                    {
                        int charId = 0;
                        p.Get("char", out charId);
                        if (charId <= 0) return Fail("bad_args", "未指定人物");
                        Character ch;
                        if (!DomainManager.Character.TryGetElement_Objects(charId, out ch) || ch == null)
                            return Fail("invalid_char", "角色无效");
                        var flippable = new List<short>();
                        try
                        {
                            var skills = DomainManager.CombatSkill.GetCharCombatSkills(charId);
                            if (skills != null)
                            {
                                foreach (var pair in skills)
                                {
                                    var skill = pair.Value;
                                    if (skill == null) continue;
                                    ushort activation = skill.GetActivationState();
                                    if (!global::GameData.Domains.CombatSkill.CombatSkillStateHelper.IsBrokenOut(activation))
                                        continue;
                                    ushort reading = skill.GetReadingState();
                                    bool canFlip = false;
                                    for (byte page = 1; page <= 5 && !canFlip; page++)
                                    {
                                        sbyte activeDirection = ActiveNormalPageDirection(activation, page);
                                        if (activeDirection != 0 && activeDirection != 1) continue;
                                        byte oppositePage = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                            .GetPageInternalIndex(-1, (sbyte)(1 - activeDirection), page);
                                        canFlip = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                            .IsPageRead(reading, oppositePage);
                                    }
                                    if (canFlip) flippable.Add(pair.Key);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            return Fail("query_failed", "正逆练资格读取失败:" + e.GetType().Name);
                        }
                        flippable.Sort();
                        var okQ = new SerializableModData();
                        okQ.Set("success", true);
                        okQ.Set("message", "ok");
                        okQ.Set("ids", string.Join(",", flippable));
                        return okQ;
                    }
                    case "flip_practice":   // 已会武学当前突破页正逆练颠倒。只改激活页,并走 SetActivePage 校验读页与太吾突破盘。
                    {
                        int charId = 0; string skillName = null;
                        p.Get("char", out charId); p.Get("skill", out skillName);
                        if (charId <= 0 || string.IsNullOrWhiteSpace(skillName)) return Fail("bad_args", "未指定人物或功法");
                        Character ch;
                        if (!DomainManager.Character.TryGetElement_Objects(charId, out ch) || ch == null)
                            return Fail("invalid_char", "角色无效");
                        short skillId; string realName;
                        if (!TryResolveKnownCombatSkill(charId, skillName, out skillId, out realName))
                            return Fail("not_known", "此人并未习得「" + skillName.Trim() + "」");
                        var skills = DomainManager.CombatSkill.GetCharCombatSkills(charId);
                        if (skills == null || !skills.ContainsKey(skillId)) return Fail("not_known", "此人并未习得「" + realName + "」");
                        var sk = skills[skillId];
                        ushort beforeState = sk.GetActivationState();
                        if (!global::GameData.Domains.CombatSkill.CombatSkillStateHelper.IsBrokenOut(beforeState))
                            return Fail("not_broken", "此功法尚未突破,无正逆练可改");
                        sbyte beforeDir = global::GameData.Domains.CombatSkill.CombatSkillStateHelper.GetCombatSkillDirection(beforeState);
                        int active = 0, flipped = 0;
                        bool flipIndeterminate = false;
                        for (byte page = 1; page <= 5; page++)
                        {
                            sbyte dir = ActiveNormalPageDirection(beforeState, page);
                            if (dir != 0 && dir != 1) continue;
                            active++;
                            try
                            {
                                if (DomainManager.CombatSkill.SetActivePage(context, charId, skillId, page, (sbyte)(1 - dir)))
                                    flipped++;
                            }
                            catch { flipIndeterminate = true; }
                        }
                        if (flipIndeterminate)
                            return Indeterminate("flip_practice_indeterminate", "至少一页正逆练写入的最终状态无法确认");
                        if (active <= 0) return Fail("no_active_page", "此功法没有已激活的常页可颠倒");
                        if (flipped <= 0) return Fail("no_reverse_page", "未读过对应正/逆页,无法颠倒当前练法");
                        ushort afterState = beforeState;
                        try
                        {
                            var afterSkills = DomainManager.CombatSkill.GetCharCombatSkills(charId);
                            if (afterSkills != null && afterSkills.ContainsKey(skillId)) afterState = afterSkills[skillId].GetActivationState();
                        }
                        catch { }
                        sbyte afterDir = global::GameData.Domains.CombatSkill.CombatSkillStateHelper.GetCombatSkillDirection(afterState);
                        var okF = new SerializableModData();
                        okF.Set("success", true); okF.Set("message", "ok");
                        okF.Set("name", realName);
                        okF.Set("flipped", flipped);
                        okF.Set("before", DirectionText(beforeDir));
                        okF.Set("after", DirectionText(afterDir));
                        return okF;
                    }
                    case "npc_train_skill":   // NPC 专用：把一门已会武学研读、突破到当前版本可表达的完整状态。
                    {
                        int charId = 0; string skillName = null;
                        p.Get("char", out charId); p.Get("skill", out skillName);
                        int taiwuId = DomainManager.Taiwu.GetTaiwuCharId();
                        if (charId <= 0 || string.IsNullOrWhiteSpace(skillName))
                            return Fail("bad_args", "未指定人物或功法");
                        if (charId == taiwuId)
                            return Fail("taiwu_forbidden", "修炼工具只允许 NPC 使用，不能替太吾直接拉满");
                        Character ch;
                        if (!DomainManager.Character.TryGetElement_Objects(charId, out ch) || ch == null)
                            return Fail("invalid_char", "角色无效");
                        short skillId; string realName;
                        if (!TryResolveKnownCombatSkill(charId, skillName, out skillId, out realName))
                            return Fail("not_known", "此人并未习得「" + skillName.Trim() + "」");
                        var skills = DomainManager.CombatSkill.GetCharCombatSkills(charId);
                        if (skills == null || !skills.TryGetValue(skillId, out var skill) || skill == null)
                            return Fail("not_known", "此人并未习得「" + realName + "」");
                        if (skill.GetRevoked()) return Fail("revoked", "此功法当前已废除，不能修炼");

                        ushort beforeReading = skill.GetReadingState();
                        ushort beforeActivation = skill.GetActivationState();
                        sbyte beforeBreakoutSteps = skill.GetBreakoutStepsCount();
                        ushort completeReading = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                            .CompleteReadingState;
                        sbyte outline = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                            .GetActiveOutlinePageType(beforeActivation);
                        if (outline < 0 || outline > 4) outline = ch.GetBehaviorType();
                        if (outline < 0 || outline > 4) outline = 0;
                        ushort completeActivation = 0;
                        completeActivation = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                            .SetPageActive(completeActivation,
                                global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                    .GetOutlinePageInternalIndex(outline));
                        sbyte preferredDirection = skill.GetDirection();
                        if (preferredDirection != 0 && preferredDirection != 1)
                            preferredDirection = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                .GetCombatSkillDirection(beforeActivation);
                        if (preferredDirection != 0 && preferredDirection != 1) preferredDirection = 0;
                        for (byte page = 1; page <= 5; page++)
                        {
                            sbyte direction = ActiveNormalPageDirection(beforeActivation, page);
                            if (direction != 0 && direction != 1) direction = preferredDirection;
                            completeActivation = global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                .SetPageActive(completeActivation,
                                    global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                        .GetNormalPageInternalIndex(direction, page));
                        }
                        sbyte completeBreakoutSteps = ch.GetSkillBreakoutAvailableStepsCount(skillId);
                        if (beforeReading == completeReading
                            && beforeActivation == completeActivation
                            && beforeBreakoutSteps == completeBreakoutSteps)
                            return Fail("already_complete", "「" + realName + "」已经修炼完整");
                        skill.SetReadingState(completeReading, context);
                        skill.SetActivationState(completeActivation, context);
                        skill.SetBreakoutStepsCount(completeBreakoutSteps, context);
                        if (skill.GetReadingState() != completeReading
                            || skill.GetActivationState() != completeActivation
                            || skill.GetBreakoutStepsCount() != completeBreakoutSteps)
                            return Indeterminate("npc_train_postcondition_indeterminate",
                                "修炼写入接口已返回，但完整研读/突破进度无法确认");
                        var trained = new SerializableModData();
                        trained.Set("success", true);
                        trained.Set("message", "ok");
                        trained.Set("name", realName);
                        trained.Set("read_pages_before",
                            global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                .GetReadPagesCount(beforeReading));
                        trained.Set("read_pages_after", 15);
                        trained.Set("direction", DirectionText(preferredDirection));
                        trained.Set("breakout_steps_before", beforeBreakoutSteps);
                        trained.Set("breakout_steps_after", completeBreakoutSteps);
                        return trained;
                    }
                    case "npc_read_book":   // NPC 专用：直接把背包中的一本武学/技艺书读完。
                    {
                        int charId = 0; string bookName = null;
                        p.Get("char", out charId); p.Get("book", out bookName);
                        int taiwuId = DomainManager.Taiwu.GetTaiwuCharId();
                        if (charId <= 0 || string.IsNullOrWhiteSpace(bookName))
                            return Fail("bad_args", "未指定人物或书籍");
                        if (charId == taiwuId)
                            return Fail("taiwu_forbidden", "阅读工具只允许 NPC 使用，不能替太吾直接读完");
                        Character ch;
                        if (!DomainManager.Character.TryGetElement_Objects(charId, out ch) || ch == null)
                            return Fail("invalid_char", "角色无效");
                        GameData.Domains.Item.SkillBook selected = null;
                        string realBookName = null;
                        int bestNameLength = -1;
                        bool matchedCompletedBook = false;
                        try
                        {
                            foreach (var pair in ch.GetInventory().Items)
                            {
                                if (pair.Value <= 0 || pair.Key.ItemType != 10) continue;
                                var candidate = DomainManager.Item.TryGetBaseItem(pair.Key)
                                    as GameData.Domains.Item.SkillBook;
                                if (candidate == null) continue;
                                string candidateName = null;
                                try { candidateName = StripTags(ItemTemplateHelper.GetName(
                                    pair.Key.ItemType, pair.Key.TemplateId)); } catch { }
                                if (string.IsNullOrWhiteSpace(candidateName)) continue;
                                bool exact = string.Equals(candidateName.Trim(), bookName.Trim(),
                                    StringComparison.OrdinalIgnoreCase);
                                if (!exact && !NameHit(candidateName, bookName)) continue;
                                int candidateMaxPages = candidate.IsCombatSkillBook() ? 6 : 5;
                                int candidateReadingPage = candidate.IsCombatSkillBook()
                                    ? ch.GetCombatSkillBookCurrReadingInfo(candidate).readingPage
                                    : ch.GetLifeSkillBookCurrReadingInfo(candidate).readingPage;
                                if (candidateReadingPage >= candidateMaxPages)
                                {
                                    matchedCompletedBook = true;
                                    continue;
                                }
                                if (exact || candidateName.Length > bestNameLength)
                                {
                                    selected = candidate;
                                    realBookName = candidateName.Trim();
                                    bestNameLength = exact ? int.MaxValue : candidateName.Length;
                                    if (exact) break;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            return Fail("book_inventory_unreadable",
                                "背包书籍读取失败:" + e.GetType().Name);
                        }
                        if (selected == null && matchedCompletedBook)
                            return Fail("already_complete", "「" + bookName.Trim() + "」已经读完");
                        if (selected == null)
                            return Fail("book_not_owned", "此人背包中没有「" + bookName.Trim() + "」");
                        int maxPages = selected.IsCombatSkillBook() ? 6 : 5;
                        int beforePage = selected.IsCombatSkillBook()
                            ? ch.GetCombatSkillBookCurrReadingInfo(selected).readingPage
                            : ch.GetLifeSkillBookCurrReadingInfo(selected).readingPage;
                        try
                        {
                            if (selected.IsCombatSkillBook())
                            {
                                short templateId = selected.GetCombatSkillTemplateId();
                                var skills = DomainManager.CombatSkill.GetCharCombatSkills(charId);
                                GameData.Domains.CombatSkill.CombatSkill learned = null;
                                if (skills == null || !skills.TryGetValue(templateId, out learned)
                                    || learned == null)
                                    learned = ch.LearnNewCombatSkill(context, templateId, 0);
                                if (learned == null)
                                    return Indeterminate("npc_read_book_skill_missing",
                                        "秘籍研读已开始，但对应武学状态未能建立");

                                byte pageTypes = selected.GetPageTypes();
                                for (byte page = 0; page < 6; page++)
                                {
                                    byte internalIndex = global::GameData.Domains.CombatSkill
                                        .CombatSkillStateHelper.GetPageInternalIndex(pageTypes, page);
                                    ushort readingState = learned.GetReadingState();
                                    if (global::GameData.Domains.CombatSkill.CombatSkillStateHelper
                                        .IsPageRead(readingState, internalIndex)) continue;
                                    ushort nextState = global::GameData.Domains.CombatSkill
                                        .CombatSkillStateHelper.SetPageRead(readingState, internalIndex);
                                    DomainManager.CombatSkill.SetCombatSkillReadingState(
                                        context, learned, nextState);
                                    DomainManager.CombatSkill.TryActivateCombatSkillBookPageWhenSetReadingState(
                                        context, charId, templateId, internalIndex);
                                }
                            }
                            else
                            {
                                short templateId = selected.GetLifeSkillTemplateId();
                                int learnedIndex = ch.GetLifeSkillBookCurrReadingInfo(selected)
                                    .learnedSkillIndex;
                                if (learnedIndex < 0)
                                {
                                    ch.LearnNewLifeSkill(context, templateId, 0);
                                    learnedIndex = ch.GetLifeSkillBookCurrReadingInfo(selected)
                                        .learnedSkillIndex;
                                }
                                if (learnedIndex < 0)
                                    return Indeterminate("npc_read_book_skill_missing",
                                        "技艺研读已开始，但对应技艺状态未能建立");
                                for (byte page = 0; page < 5; page++)
                                    ch.ReadLifeSkillPage(context, learnedIndex, page);
                            }
                        }
                        catch (Exception e)
                        {
                            int now = selected.IsCombatSkillBook()
                                ? ch.GetCombatSkillBookCurrReadingInfo(selected).readingPage
                                : ch.GetLifeSkillBookCurrReadingInfo(selected).readingPage;
                            if (now < maxPages)
                                return Indeterminate("npc_read_book_indeterminate",
                                    "读书过程未能完整确认:" + e.GetType().Name);
                        }
                        int afterPage = selected.IsCombatSkillBook()
                            ? ch.GetCombatSkillBookCurrReadingInfo(selected).readingPage
                            : ch.GetLifeSkillBookCurrReadingInfo(selected).readingPage;
                        if (afterPage < maxPages)
                            return Indeterminate("npc_read_book_postcondition_indeterminate",
                                "阅读接口已返回，但尚未确认读完全部书页");
                        var readDone = new SerializableModData();
                        readDone.Set("success", true);
                        readDone.Set("message", "ok");
                        readDone.Set("name", realBookName ?? bookName.Trim());
                        readDone.Set("pages_before", Math.Min(beforePage, maxPages));
                        readDone.Set("pages_after", maxPages);
                        readDone.Set("kind", selected.IsCombatSkillBook() ? "武学" : "技艺");
                        return readDone;
                    }
                    case "writebook":   // NPC/太吾回忆一门自己会的武学(combat)/技艺(life)秘籍:凭空造出一本可研习的书并交给收书人(按品级随机带残页,非缺页)。
                    {                   // 走游戏自带 CreateSkillBook;Id 引擎内部分配,绝不自造 ItemKey.Id(否则空书坏档)。
                        int npcId = 0, taiwuId = 0, tpl = 0, rcpt = 0; string kind = null;
                        p.Get("npc", out npcId); p.Get("taiwu", out taiwuId); p.Get("tpl", out tpl); p.Get("kind", out kind); p.Get("recipient", out rcpt);
                        if (tpl < 0) return Fail("bad_args", "未指定要回忆成书的武学/技艺");
                        int dstId = rcpt > 0 ? rcpt : taiwuId;   // recipient>0 → 成书赠第三方 NPC;否则赠太吾(原行为)
                        Character npcC, taiwuC;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out npcC) || npcC == null ||
                            !DomainManager.Character.TryGetElement_Objects(dstId, out taiwuC) || taiwuC == null)
                            return Fail("invalid_char", "角色无效");
                        short tplS = (short)tpl;
                        short bookTpl;
                        byte pageTypes = 0;   // 正逆练:武学按 NPC 自己的练法逐页设置;技艺无正逆(恒 0)
                        if (kind == "life")
                        {
                            bool known = false;
                            var npcLifeSkills = npcC.GetLearnedLifeSkills();
                            if (npcLifeSkills != null)
                                foreach (var ls in npcLifeSkills)
                                    if (ls.SkillTemplateId == tplS) { known = true; break; }
                            if (!known) return Fail("not_known", "你并未通晓此技艺,无从回忆成书");
                            bookTpl = Config.LifeSkill.Instance[tplS].SkillBookId;        // 技艺→技艺书 templateId
                        }
                        else
                        {
                            if (!HasLearnedCombatSkill(npcC, tplS)) return Fail("not_known", "你并未习得此武学,无从回忆成书");
                            bookTpl = Config.CombatSkill.Instance[tplS].BookId;            // 武学→秘籍 templateId
                            // 正逆练:取 NPC 实际所练方向(0=正练 1=逆练),把书的每一常页都设成该方向 → 默写出的书与其练法一致
                            try
                            {
                                var cs = DomainManager.CombatSkill.GetCharCombatSkills(npcId);
                                if (cs != null && cs.TryGetValue(tplS, out var sk) && sk != null)
                                {
                                    sbyte dir = sk.GetDirection();
                                    if (dir != 0 && dir != 1) dir = 0;
                                    for (byte pg = 1; pg <= 5; pg++) pageTypes = global::GameData.Domains.Item.SkillBookStateHelper.SetNormalPageType(pageTypes, pg, dir);
                                }
                            }
                            catch { pageTypes = 0; }
                            // 最新版原生 SkillBook(pageTypes, ...) 会直接读取低 3 bit 作为总纲类型并钳在 0..4；
                            // pageTypes 默认 0 会令每本回忆秘籍都固定为同一种（界面显示“承”）。按原生合法范围随机一类。
                            pageTypes = global::GameData.Domains.Item.SkillBookStateHelper.SetOutlinePageType(
                                pageTypes, (sbyte)context.Random.Next(5));
                        }
                        // 武学/技艺都有对应秘籍可回忆成书;此处仅作防御性兜底,正常绝不会命中(无书武学并不存在)。
                        if (bookTpl < 0) return Fail("no_book", "无法回忆成书");
                        // 写书只看"写书的人会不会"(上面 kind 分支已校验 NPC 确通晓);不再看太吾会不会/有没有此书——
                        // 太吾已有同书也照写不误(只是多一本),别因"对方已有此书"挡住能写之人。
                        // #2 按品级随机【残页】(武学/技艺书同理):789品(grade0-2 九八七)无残;456品(grade3-5 六五四)残1~2页;123品(grade6-8 三二一)残2~3页。残页位置由引擎随机。
                        // 残页(每常页 state=1)≠缺页(state=2):残页仍在、可照常研习,只是字迹漫漶须慢慢参详补全;缺页则整页佚失。回忆成书凭记忆,记不真处是"残"而非"佚",故 lostPagesCount 恒传 0、损页一律落在 state=1 残缺(引擎据 completePagesCount 把其余 5-completeC 页判为残页)。
                        sbyte bookGrade = 0; try { bookGrade = Config.SkillBook.Instance[bookTpl].Grade; } catch { }
                        int damaged = bookGrade <= 2 ? 0 : (bookGrade <= 5 ? (1 + context.Random.Next(2)) : (2 + context.Random.Next(2)));
                        if (damaged > 4) damaged = 4; if (damaged < 0) damaged = 0;   // 共 5 常页,至多残 4(留 1 完整)
                        sbyte completeC = (sbyte)(5 - damaged);   // 完整页数;其余 damaged 页由引擎随机判为残页
                        // pageTypes:武学按 NPC 正逆练;completeC 完整页 + 余者随机残页 + 0 缺页;source=Invalid 绕开自动操作的静默丢弃
                        ItemKey bookKey = DomainManager.Item.CreateSkillBook(context, bookTpl, pageTypes, completeC, (sbyte)0, true);
                        if (!TryInventoryCount(taiwuC, bookKey, out int bookInventoryBefore))
                        {
                            if (TryRemoveCreatedItemEntity(context, bookKey))
                                return Fail("book_recipient_inventory_unreadable",
                                    "无法读取收书人的背包，秘籍未交付且已撤销创建");
                            return Indeterminate("book_creation_cleanup_indeterminate",
                                "无法读取收书人的背包，且未能确认新建秘籍已清理");
                        }
                        if (bookInventoryBefore != 0)
                            return Indeterminate("book_creation_preowned",
                                "新建秘籍在交付前已意外出现在收书人背包，状态无法安全补偿");
                        Exception deliveryError = null;
                        try
                        {
                            if (!taiwuC.AddInventoryItem(context, bookKey, 1, false,
                                    GameData.Domains.Taiwu.EItemAutoOperationSource.Invalid))
                                deliveryError = new InvalidOperationException("收书人背包拒绝秘籍");
                        }
                        catch (Exception be) { deliveryError = be; }

                        bool bookExists;
                        if (!TryInventoryCount(taiwuC, bookKey, out int bookInventoryAfter)
                            || !TryItemExists(bookKey, out bookExists))
                            return Indeterminate("book_delivery_postcondition_unreadable",
                                "写书后无法读取秘籍库存或实体状态，不得自动重试");

                        // AddInventoryItem(1.0.72) 先写背包、再写 Owner，最后才通知太吾/自动读书。
                        // 因此尾部抛错不能直接判 UNKNOWN：数量和 Owner 均已完整落地时就是成功；
                        // Owner 阶段中断则先尝试修复，仍失败时必须把新建实体与背包一并精确撤销。
                        bool bookDelivered = bookInventoryAfter == bookInventoryBefore + 1
                            && bookExists
                            && TryEnsureInventoryOwner(bookKey, dstId);
                        if (!bookDelivered)
                        {
                            if (TryDiscardCreatedInventoryItem(context, taiwuC, bookKey,
                                    bookInventoryBefore))
                                return Fail("book_delivery_failed",
                                    "秘籍未能完整交付，已恢复收书人原背包并撤销新建秘籍"
                                    + (deliveryError == null ? "" : ":" + deliveryError.GetType().Name));
                            return Indeterminate("book_delivery_indeterminate",
                                "秘籍交付只完成了部分写入，且未能确认补偿已完整恢复"
                                + (deliveryError == null ? "" : ":" + deliveryError.GetType().Name));
                        }
                        string bname = null; try { bname = ItemTemplateHelper.GetName((sbyte)10, bookTpl); } catch { }
                        var okW = new SerializableModData(); okW.Set("success", true); okW.Set("message", "ok"); okW.Set("name", bname ?? "秘籍"); okW.Set("lost", damaged); return okW;   // lost:残页数,供 NPC 自知、对话体现
                    }
                    case "character_fame":
                    case "taiwu_fame":   // 旧太吾专用 op 保留给已持久化回执；新入口可改任意有效人物名望。
                    {
                        int characterId = 0, taiwuId = 0, delta = 0;
                        p.Get("character", out characterId); p.Get("taiwu", out taiwuId); p.Get("delta", out delta);
                        if (characterId <= 0) characterId = taiwuId;
                        if (characterId <= 0 || delta == 0) return Fail("bad_args", "参数无效");
                        if (delta % 3 != 0) return Fail("bad_delta", "名望变化必须是3的倍数");
                        Character fameCharacter;
                        if (!DomainManager.Character.TryGetElement_Objects(characterId, out fameCharacter)
                            || fameCharacter == null) return Fail("invalid_char", "角色无效");
                        try
                        {
                            int mag = delta > 0 ? delta : -delta;
                            int m = mag / 3; if (m < 1) m = 1; if (m > 50) m = 50;   // 行为单次 Fame=±3；只接受3的倍数，投影值与实际值严格一致
                            short fameActionId = (short)(delta > 0 ? 83 : 82);            // 83=CaptureCriminals(济世擒凶,+3)/82=CommitCrime(作恶,−3):语义温和通用、可叠加
                            DomainManager.Character.GmCmd_RecordFameAction(context, characterId, fameActionId, -1, (short)m);
                        }
                        catch (Exception e) { return Indeterminate("fame_indeterminate", "名望行为可能已记录:" + e.GetType().Name); }
                        var okFame = new SerializableModData(); okFame.Set("success", true); okFame.Set("message", "ok"); okFame.Set("actual_delta", delta); return okFame;
                    }
                    case "relate_npc":   // NPC(a)主动与第三方 NPC(b)缔结关系:挚友/结义/师徒(a=师 b=徒)/义亲/恋人/夫妻。复用与太吾同套原生原子操作,只是两端都是任意角色。
                    {
                        int aId = 0, bId = 0; string action = null;
                        p.Get("a", out aId); p.Get("b", out bId); p.Get("action", out action);
                        if (aId <= 0 || bId <= 0 || aId == bId) return Fail("bad_args", "关系双方无效");
                        Character ca, cb;
                        bool haveA = DomainManager.Character.TryGetElement_Objects(aId, out ca) && ca != null;
                        bool haveB = DomainManager.Character.TryGetElement_Objects(bId, out cb) && cb != null;
                        if (!haveA || !haveB)
                        {
                            // 哪一方取不到活体对象?多半是【对方】只存在于关系记忆里(已故/已离散/祖辈等历史人物),非当下在场之人,无法当面缔结。
                            int miss = !haveA ? aId : bId; string side = !haveA ? "你自己" : "对方";
                            string nm = NameOf(miss); if (string.IsNullOrEmpty(nm)) nm = "此人";
                            Grave grv;
                            if (DomainManager.Character.TryGetElement_Graves(miss, out grv) && grv != null)
                                return Fail("char_dead", side + "(" + nm + ")已不在人世——无法与逝者结此关系。把这实情如实告诉太吾,别假称已结。");
                            return Fail("char_absent", side + "(" + nm + ")此刻并不在场、只是旧相识在你记忆里的名字(或已离散/系前尘故人),无法当面缔结此关系。如实告知太吾,别假称已结。");
                        }
                        bool aT = DomainManager.Character.IsTaiwuPeople(aId), bT = DomainManager.Character.IsTaiwuPeople(bId);
                        // 本体各 Apply* 入口在关系不允许时会静默 no-op。必须在写入前使用同一
                        // RelationTypeHelper 判定，把确定性拒绝如实返回，不能在事后误报 UNKNOWN。
                        switch (action)
                        {
                            case "befriend":
                                if (DomainManager.Character.HasRelation(aId, bId, (ushort)8192))
                                    return Fail("already", "双方已经是挚友");
                                // Native ApplyBecomeFriend(self=a,target=b) validates
                                // AllowAddingFriendRelation(target,self), not (self,target).
                                if (!RelationTypeHelper.AllowAddingFriendRelation(bId, aId))
                                    return Fail("relation_not_allowed", "本体关系规则不允许双方结为挚友");
                                break;
                            case "sworn":
                                if (DomainManager.Character.HasRelation(aId, bId, (ushort)512))
                                    return Fail("already", "双方已经结义");
                                if (!RelationTypeHelper.AllowAddingSwornBrotherOrSisterRelation(bId, aId))
                                    return Fail("relation_not_allowed", "双方的现有亲缘或关系不允许再结义");
                                break;
                            case "mentor":
                                if (DomainManager.Character.HasRelation(bId, aId, (ushort)2048))
                                    return Fail("already", "双方已经存在所请求的师徒名分");
                                if (!RelationTypeHelper.AllowAddingMentorRelation(bId, aId))
                                    return Fail("relation_not_allowed", "本体关系规则不允许建立这层师徒名分");
                                break;
                            case "adoptive_parent":
                                if (!ValidateAdoptiveRelation(aId, ca, bId, cb, true,
                                    out string adoptParentCode, out string adoptParentReason))
                                    return Fail(adoptParentCode, adoptParentReason);
                                break;
                            case "adoptive_child":
                                if (!ValidateAdoptiveRelation(aId, ca, bId, cb, false,
                                    out string adoptChildCode, out string adoptChildReason))
                                    return Fail(adoptChildCode, adoptChildReason);
                                break;
                            case "lover":
                            {
                                if (DomainManager.Character.HasRelation(aId, bId, (ushort)16384))
                                    return Fail("already", HasMutualAdoration(aId, bId)
                                        ? "双方已经两情相悦"
                                        : "发起者已经在单方面爱慕对方");
                                if (!RelationTypeHelper.AllowAddingAdoredRelation(aId, bId))
                                    return Fail("relation_not_allowed", "本体关系或人物特性不允许发起者爱慕对方");
                                break;
                            }
                            case "spouse":
                                if (DomainManager.Character.HasRelation(aId, bId, (ushort)1024))
                                    return Fail("already", "双方已经是夫妻");
                                if (ca.GetAgeGroup() != 2 || cb.GetAgeGroup() != 2)
                                    return Fail("not_adult", "双方并非都已成年，不能成婚");
                                if (ca.IsCompletelyInfected() || cb.IsCompletelyInfected())
                                    return Fail("infected", "有人已完全入魔，不能成婚");
                                if (!RelationTypeHelper.AllowAddingHusbandOrWifeRelation(bId, aId))
                                    return Fail("relation_not_allowed", "双方已有配偶、属于禁婚亲缘，或现有关系不允许成婚");
                                break;
                            default:
                                return Fail("unknown_action", "未知关系");
                        }
                        try
                        {
                            switch (action)
                            {
                                case "befriend": new BecomeFriendAction().ApplyChanges(context, ca, cb); break;
                                case "sworn": new BecomeSwornAction().ApplyChanges(context, ca, cb); break;
                                // 原生 2048(Mentor) 是【徒弟→师父】；a 收 b 为徒时必须以 b 为 self、a 为 target。
                                case "mentor": Character.ApplyAddRelation_Mentor(context, cb, ca, cb.GetBehaviorType(), bT, aT); break;
                                // adoptive_parent = a 认 b 为义父母；adoptive_child = a 收 b 为义子女。
                                case "adoptive_parent": Character.ApplyAddRelation_AdoptiveParent(context, ca, cb, ca.GetBehaviorType(), aT, bT); break;
                                case "adoptive_child": Character.ApplyAddRelation_AdoptiveChild(context, ca, cb, ca.GetBehaviorType(), aT, bT); break;
                                case "lover":
                                    if (!TryApplyDirectedAdoration(context, ca, cb, aT, bT))
                                        return Fail("adoration_not_applied", "发起者的爱慕未能建立");
                                    break;
                                case "spouse": Character.ApplyBecomeHusbandOrWife(context, ca, cb, ca.GetBehaviorType(), true, aT, bT); break;
                            }
                        }
                        catch (Exception e) { return Indeterminate("relation_indeterminate", "关系可能已部分建立:" + e.GetType().Name); }
                        // Adored(16384) 是单向位；“恋人”必须双方都有该位，单方爱慕不等于恋人。
                        bool ok =
                            (action == "befriend" && DomainManager.Character.HasRelation(aId, bId, (ushort)8192)) ||
                            (action == "sworn" && DomainManager.Character.HasRelation(aId, bId, (ushort)512)) ||
                            (action == "mentor" && DomainManager.Character.HasRelation(bId, aId, (ushort)2048)) ||
                            (action == "adoptive_parent" && DomainManager.Character.HasRelation(aId, bId, (ushort)64)) ||
                            (action == "adoptive_child" && DomainManager.Character.HasRelation(aId, bId, (ushort)128)) ||
                            (action == "lover" && DomainManager.Character.HasRelation(aId, bId, (ushort)16384)) ||
                            (action == "spouse" && DomainManager.Character.HasRelation(aId, bId, (ushort)1024));
                        if (!ok) return Indeterminate("relation_postcondition_indeterminate", "关系写 API 已返回但权威关系位未满足；不得断言完全未改动");
                        string aName = ShownName(aId); if (string.IsNullOrWhiteSpace(aName)) aName = "#" + aId;
                        string bName = ShownName(bId); if (string.IsNullOrWhiteSpace(bName)) bName = "#" + bId;
                        var okR = new SerializableModData();
                        okR.Set("success", true);
                        okR.Set("message", action == "lover"
                            ? (HasMutualAdoration(aId, bId)
                                ? aName + "与" + bName + "如今两情相悦"
                                : aName + "开始单方面爱慕" + bName)
                            : action == "adoptive_parent"
                                ? aName + "认" + bName + "为" + AdoptiveParentTitle(cb)
                                : action == "adoptive_child"
                                    ? aName + "收" + bName + "为" + AdoptiveChildTitle(cb)
                                    : "ok");
                        return okR;
                    }
                    case "addfeature":   // 对话说动 → 给 NPC 添加一个可见、永久且真正进阶的良性特性
                    {
                        int npcId = 0; string fname = null;
                        p.Get("npc", out npcId); p.Get("name", out fname);
                        if (string.IsNullOrWhiteSpace(fname)) return Fail("bad_args", "未指定特性");
                        Character c;
                        if (!DomainManager.Character.TryGetElement_Objects(npcId, out c) || c == null) return Fail("invalid_char", "角色无效");
                        var owned = c.GetFeatureIds() ?? new List<short>();
                        sbyte gender = c.GetGender();
                        sbyte orgTemplateId = c.GetOrganizationInfo().OrgTemplateId;
                        var ownedByMutexGroup = new Dictionary<short, Config.CharacterFeatureItem>();
                        foreach (short ownedId in owned)
                        {
                            Config.CharacterFeatureItem current = Config.CharacterFeature.Instance[ownedId];
                            if (current != null && current.MutexGroupId >= 0)
                                ownedByMutexGroup[current.MutexGroupId] = current;
                        }
                        string q = fname.Trim();
                        // 搜 → 去重 → 选合适：只收集此人此刻确实能获得的可见永久良性品性。
                        // 最新版 GM 入口会无条件替换同互斥组特性，所以这里必须先拒绝平级/降级；
                        // 允许负面转正、低阶正面升级，但不能覆盖同组特殊/临时特性。
                        // 再按模型给的名挑(精确>包含);名没对上就从候选里挑一个真实的,绝不再因"凭空想的名字不存在"而失败。
                        Config.CharacterFeatureItem chosen = null, contains = null;
                        var candidates = new List<Config.CharacterFeatureItem>();
                        foreach (var f in Config.CharacterFeature.Instance)
                        {
                            if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                            if (f.Type != ECharacterFeatureType.Good || f.Level <= 0) continue;
                            if (f.Hidden || f.BelongAdventure || f.Duration != 0 || f.MutexGroupId < 0 || f.IsChickenFeature) continue;
                            if (f.Gender != -1 && f.Gender != gender) continue;
                            if (!f.IsAllowedForOrganization(orgTemplateId)) continue;
                            if (owned.Contains(f.TemplateId)) continue;
                            Config.CharacterFeatureItem ownedInGroup;
                            if (ownedByMutexGroup.TryGetValue(f.MutexGroupId, out ownedInGroup))
                            {
                                if (ownedInGroup.Type != ECharacterFeatureType.Good
                                    && ownedInGroup.Type != ECharacterFeatureType.Bad) continue;
                                if (ownedInGroup.Level >= f.Level) continue;
                            }
                            candidates.Add(f);
                            if (f.Name == q) { chosen = f; break; }
                            if (contains == null && q.Length >= 2 && (f.Name.Contains(q) || q.Contains(f.Name))) contains = f;
                        }
                        if (chosen == null) chosen = contains;
                        if (chosen == null && candidates.Count > 0) chosen = candidates[context.Random.Next(candidates.Count)];
                        if (chosen == null) return Fail("feature_not_grantable", "此人已具备所有适合的良性品性,实在无可再添");
                        try
                        {
                            DomainManager.Character.GmCmd_AddFeature(context, npcId, chosen.TemplateId);
                            var after = c.GetFeatureIds();
                            if (after == null || !after.Contains(chosen.TemplateId))
                                return Indeterminate("feature_postcondition_indeterminate",
                                    "特性写入 API 已返回，但角色最终并未持有该特性；不得假报成功");
                        }
                        catch (Exception e)
                        {
                            return Indeterminate("feature_write_indeterminate",
                                "特性写入后无法确认最终状态：" + e.GetType().Name);
                        }
                        var okF = new SerializableModData(); okF.Set("success", true); okF.Set("message", "ok"); okF.Set("name", chosen.Name); return okF;
                    }
                    case "merchantfavor":   // 对话改变商队显示好感(按商人品类共享,0-100):从 NPC charId 反查 merchantType
                    {
                        int npcId = 0, delta = 0;
                        p.Get("npc", out npcId); p.Get("delta", out delta);
                        if (delta == 0) return Fail("noop", "无变化");
                        sbyte mtype;
                        if (!DomainManager.Extra.TryGetMerchantCharToType(npcId, out mtype)) return Fail("not_merchant", "对方并非行商,无商队好感可言");
                        if (delta > 100) delta = 100; else if (delta < -100) delta = -100;
                        int before = DomainManager.Merchant.GetCurFavorability(mtype);
                        int target = Math.Max(0, Math.Min(100, before + delta));
                        if (target == before)
                            return Fail("favor_at_boundary", "商队好感已在边界 " + before + "/100,本次没有变化");

                        // b24185552 ChangeMerchantCumulativeMoney 的 delta 单位是“累计交易额”，
                        // 绝不是 UI 的 0..100 好感点。原实现把两者直接相加，中高等级时
                        // 往往静默不动却回 success。也不能用 GetCumulativeMoney(target) 换算：
                        // 它对非整十好感按【前缀和】插值(MerchantDomain.GetCumulativeMoney)，
                        // 而显示好感按【逐段需求】消耗(ShopExchange.GetFavorability：每扣满一段
                        // MerchantFavorabilityMoneyRequirements[i] 得 10 点，段内按已付比例取整)，
                        // 两种语义对 0..100 的多数中间值不互逆、写后回读≠target。这里按消耗
                        // 语义自行求精确逆：target=10k+m → 前 k 段全额 + 第 k 段的 m/10
                        // (各段配置均可被 10 整除，除法无余数)，使写后显示值精确等于 target；
                        // 再用 SetMerchantFavorability 一次写回。先 clone，
                        // 避免直接改动 GetMerchantFavorability 返回的域内数组引用。
                        int[] current = DomainManager.Merchant.GetMerchantFavorability();
                        if (current == null || mtype < 0 || mtype >= current.Length)
                            return Fail("merchant_type_invalid", "商队类型无效,无法调整好感");
                        int[] requirements = GlobalConfig.Instance.MerchantFavorabilityMoneyRequirements;
                        if (requirements == null || requirements.Length == 0)
                            return Fail("merchant_config_invalid", "商队好感段位配置缺失,无法换算累计额");
                        int fullSegments = target / 10, partial = target % 10;
                        int targetCumulative = 0;
                        for (int i = 0; i < fullSegments && i < requirements.Length; i++) targetCumulative += requirements[i];
                        if (partial > 0 && fullSegments < requirements.Length)
                            targetCumulative += partial * requirements[fullSegments] / 10;
                        int[] updated = (int[])current.Clone();
                        updated[mtype] = targetCumulative;
                        DomainManager.Merchant.SetMerchantFavorability(updated, context);
                        int after = DomainManager.Merchant.GetCurFavorability(mtype);
                        if (after != target)
                            return Indeterminate("merchant_favor_postcondition_indeterminate",
                                "商队好感写 API 已返回但显示值未达到目标(" + after + "!=" + target + ")");
                        // 正常交易路径 b24185552 ChangeMerchantCumulativeMoney 在累计额达满值时
                        // 按 merchantType 0..6 → statId 17..23 置成就统计;直写 setter 绕过了
                        // 该钩子,满好感时按同一映射补齐(游戏对映射外的类型直接 throw,这里不猜、跳过)。
                        if (mtype <= 6 && targetCumulative == DomainManager.Merchant.GetCumulativeMoney(100))
                            GameData.Achievement.AchievementManager.RequestSetStat(context, (short)(17 + mtype), 1);
                        var okM = new SerializableModData(); okM.Set("success", true); okM.Set("message", "ok");
                        okM.Set("before", before); okM.Set("after", after); okM.Set("target", target);
                        okM.Set("cumulative", targetCumulative); return okM;
                    }
                    case "captive_state":   // 只读:该 NPC 是否为【太吾】的阶下囚——绳缚俘虏(KidnapperId==太吾,即太吾 Capture 工具的产物)或关在太吾村(16)牢中。
                    {                       // 注:ExternalRelationState 的 bit32 只表"某门派牢中"、不证是太吾(自家门派惩戒也置位),故按捕者查实。
                        int cnpc = 0; p.Get("npc", out cnpc);
                        bool byTw = false;
                        try
                        {
                            int tw = DomainManager.Taiwu.GetTaiwuCharId();
                            Character cc;
                            if (cnpc > 0 && DomainManager.Character.TryGetElement_Objects(cnpc, out cc) && cc != null)
                            {
                                if (cc.GetKidnapperId() == tw) byTw = true;                                       // 绳缚俘虏,捕者即太吾
                                else if (DomainManager.Organization.GetPrisonerSect(cnpc) == (sbyte)16) byTw = true;   // 关在太吾村(16)牢中
                            }
                        }
                        catch { }
                        var okCap = new SerializableModData(); okCap.Set("success", true); okCap.Set("captive", byTw); return okCap;
                    }
                    case "companion_group_non_baby":   // 兼容旧 RPC 名：过月主动行动权威候选，排除婴儿与本体动物角色。
                    {
                        int taiwuId = 0; p.Get("taiwu", out taiwuId);
                        Character taiwu;
                        if (taiwuId <= 0 || !DomainManager.Character.TryGetElement_Objects(taiwuId, out taiwu)
                            || taiwu == null) return Fail("invalid_char", "太吾无效");
                        var group = DomainManager.Character.GetGroupSet(context, taiwuId);
                        if (group == null) return Fail("group_unavailable", "当前同道名册不可用");
                        var eligible = new SortedSet<int> { taiwuId };
                        foreach (int cid in group)
                        {
                            if (cid <= 0 || cid == taiwuId) continue;
                            Character companion;
                            if (!DomainManager.Character.TryGetElement_Objects(cid, out companion)
                                || !IsEligibleMonthlyAgentCharacter(cid, companion)) continue;
                            eligible.Add(cid);
                        }
                        var okGroup = new SerializableModData();
                        okGroup.Set("success", true);
                        okGroup.Set("ids", string.Join(",", eligible));
                        return okGroup;
                    }
                    case "monthly_agent_eligibility": // 任意手动候选的权威资格：活体、非婴儿、非动物。
                    {
                        string csv = null; p.Get("ids", out csv);
                        var eligible = new SortedSet<int>();
                        var rejected = new List<string>();
                        foreach (int cid in ParseCsvInts(csv, 512))
                        {
                            Character candidate;
                            if (!DomainManager.Character.TryGetElement_Objects(cid, out candidate) || candidate == null)
                            { rejected.Add(cid + ":missing"); continue; }
                            if (candidate.GetAgeGroup() == AgeGroup.Baby)
                            { rejected.Add(cid + ":baby"); continue; }
                            // 普通动物仍不启动 NPC Agent；玩家已经和固定模板动物聊过并建立的
                            // 永久副本属于显式选择，允许进入过月与剧情候选。
                            if (IsAnimalCharacter(candidate)
                                && !TryGetCharacterProxySource(cid, out _))
                            { rejected.Add(cid + ":animal"); continue; }
                            eligible.Add(cid);
                        }
                        var result = new SerializableModData();
                        result.Set("success", true);
                        result.Set("ids", string.Join(",", eligible));
                        result.Set("rejected", string.Join(",", rejected));
                        return result;
                    }
                    case "block_chars":   // 只读:太吾当前所在地块上的角色 id(剔除太吾)。现场名单默认完整保留仇敌；只有显式 allow_hostile=0 的友好候选查询才过滤。
                    {
                        int taiwuId = 0, allowHostile = 1; p.Get("taiwu", out taiwuId); p.Get("allow_hostile", out allowHostile);
                        Character tw;
                        if (!DomainManager.Character.TryGetElement_Objects(taiwuId, out tw) || tw == null) return Fail("invalid_char", "太吾无效");
                        var loc = tw.GetLocation();
                        MapBlockData blk;
                        var sceneCandidates = new SortedSet<int>();
                        var idsList = new SortedSet<int>();
                        if (loc.IsValid() && DomainManager.Map.TryGetBlock(loc, out blk) && blk != null && blk.CharacterSet != null)
                            foreach (var cid in blk.CharacterSet)
                                if (cid > 0) sceneCandidates.Add(cid);
                        // JHYL_BLOCK_CHARS_ALIVE_LOCATION_SCAN:大更新后城镇/聚落中的活人
                        // 不一定进入 MapBlockData.CharacterSet。展示名单必须复用最终物理动作的
                        // IsAtTaiwuScene 权威，否则会出现“行动层判定人在现场，搜索层却说没人”。
                        AddAllCharactersAtTaiwuScene(taiwuId, tw, sceneCandidates);
                        foreach (var cid in sceneCandidates)
                        {
                            if (cid <= 0 || cid == taiwuId) continue;
                            if (!IsValidSceneCharacter(cid)) continue;
                            if (allowHostile != 0) { idsList.Add(cid); continue; }   // 现场/距离/敌对动作/手动挑人：在场者全部保留
                            // 剔除敌对者:单向仇敌位(32768)或确知的负好感(鄙视及以下,fav<=-6000)——他们不会"主动友好捎话",留下会变成满口仇恨。
                            // 但 GetFavorability 对"素未谋面"者返回 short.MinValue,这类陌路人是中性的、应保留(归一化后即"陌路")。
                            bool hostile = false;
                            try
                            {
                                if (DomainManager.Character.HasRelation(cid, taiwuId, (ushort)32768)) hostile = true;
                                else { short fav = DomainManager.Character.GetFavorability(cid, taiwuId); if (fav != short.MinValue && fav <= -6000) hostile = true; }
                            }
                            catch { hostile = false; }
                            if (!hostile) idsList.Add(cid);
                        }
                        // 同道按游戏语义正与太吾同行，即使引擎没有把成员挂进当前
                        // MapBlock.CharacterSet，也必须进入“同地块/在场”查询结果。
                        // GetGroupSet 是当前反编译权威 API；队伍成员无需再做敌意过滤。
                        try
                        {
                            var companions = DomainManager.Character.GetGroupSet(context, taiwuId);
                            if (companions != null)
                                foreach (int cid in companions)
                                {
                                    if (cid <= 0 || cid == taiwuId) continue;
                                    if (!IsValidSceneCharacter(cid)) continue;
                                    idsList.Add(cid);
                                }
                        }
                        catch { }
                        var okB = new SerializableModData(); okB.Set("success", true); okB.Set("ids", string.Join(",", idsList)); return okB;
                    }
                    case "taiwu_scene_presence": // 只读:物理动作统一现场判定（同块、同道、太吾所擒或太吾村牢）。
                    {
                        int taiwuId = 0; string requestedCsv = null;
                        p.Get("taiwu", out taiwuId); p.Get("ids", out requestedCsv);
                        if (taiwuId <= 0 || taiwuId != DomainManager.Taiwu.GetTaiwuCharId())
                            return Fail("invalid_taiwu", "太吾身份已变化");
                        Character taiwu;
                        if (!DomainManager.Character.TryGetElement_Objects(taiwuId, out taiwu) || taiwu == null)
                            return Fail("invalid_taiwu", "太吾无效");
                        var requested = new SortedSet<int>();
                        foreach (string part in (requestedCsv ?? string.Empty).Split(','))
                            if (int.TryParse(part.Trim(), out int cid) && cid > 0) requested.Add(cid);
                        if (requested.Count == 0 || requested.Count > 16)
                            return Fail("invalid_presence_request", "现场复核人物数量无效");
                        var present = new SortedSet<int>();
                        foreach (int cid in requested)
                        {
                            Character endpoint = null;
                            if (cid != taiwuId
                                && (!DomainManager.Character.TryGetElement_Objects(cid, out endpoint) || endpoint == null))
                                continue;
                            if (IsAtTaiwuScene(cid, taiwuId, endpoint, taiwu)) present.Add(cid);
                        }
                        var presence = new SerializableModData();
                        presence.Set("success", true);
                        presence.Set("ids", string.Join(",", present));
                        return presence;
                    }
                    case "actor_block_chars": // 任意主动候选自己的真实地块，不把太吾同道队伍强行并入。
                    {
                        int actorId = 0; p.Get("actor", out actorId);
                        Character actor;
                        if (actorId <= 0 || !DomainManager.Character.TryGetElement_Objects(actorId, out actor)
                            || actor == null) return Fail("invalid_char", "行动者无效");
                        var ids = new SortedSet<int> { actorId };
                        Location loc = GetPhysicalSceneLocation(actor);
                        if (!loc.IsValid())
                            return WithCharacterState(Fail("actor_location_unavailable",
                                "行动者当前没有可确认的有效所在地"), actor);
                        MapBlockData blockData;
                        if (loc.IsValid() && DomainManager.Map.TryGetBlock(loc, out blockData)
                            && blockData != null && blockData.CharacterSet != null)
                            foreach (int cid in blockData.CharacterSet)
                                if (IsValidSceneCharacter(cid)) ids.Add(cid);
                        // JHYL_ACTOR_BLOCK_CHARS_ALIVE_LOCATION_SCAN:同样补齐未挂入
                        // CharacterSet 的聚落人物，并与最终 actor-scene 动作校验共用真值。
                        AddAllCharactersAtActorScene(actorId, ids);
                        // Native kidnapping invalidates the captive's Location and removes
                        // them from MapBlockData.CharacterSet.  They still travel in the
                        // kidnapper's physical scene and must remain targetable there.
                        try
                        {
                            foreach (KidnappedCharacter captive in
                                DomainManager.Character.GetKidnappedCharacters(actorId).GetCollection())
                            {
                                Character captured;
                                if (captive.CharId > 0
                                    && DomainManager.Character.TryGetElement_Objects(captive.CharId, out captured)
                                    && captured != null
                                    && captured.GetKidnapperId() == actorId
                                    && IsValidSceneCharacter(captive.CharId))
                                    ids.Add(captive.CharId);
                            }
                        }
                        catch { }
                        var blockResult = new SerializableModData();
                        blockResult.Set("success", loc.IsValid());
                        blockResult.Set("ids", string.Join(",", ids));
                        blockResult.Set("area", loc.IsValid() ? loc.AreaId : (short)-1);
                        blockResult.Set("block", loc.IsValid() ? loc.BlockId : (short)-1);
                        return blockResult;
                    }
                    case "char_location":   // 只读:角色真实后端 Location。前端 DisplayData.Location 可能滞后,过月事件地点以此为准。
                    {
                        int cid = 0; p.Get("char", out cid);
                        Character c;
                        if (cid <= 0 || !DomainManager.Character.TryGetElement_Objects(cid, out c) || c == null) return Fail("invalid_char", "角色无效");
                        var loc = GetPhysicalSceneLocation(c);
                        // No map position is a valid query result, not an RPC failure.
                        var okLoc = new SerializableModData(); okLoc.Set("success", true);
                        okLoc.Set("location_valid", loc.IsValid());
                        okLoc.Set("area", loc.IsValid() ? loc.AreaId : (short)-1);
                        okLoc.Set("block", loc.IsValid() ? loc.BlockId : (short)-1);
                        if (!loc.IsValid()) WithCharacterState(okLoc, c);
                        return okLoc;
                    }
                    case "travel_state":   // 只读:当前有效普通行程/动态寻人/固定赴约状态，供每轮对话淘汰旧承诺。
                    {
                        int cid = 0; p.Get("char", out cid);
                        Character c;
                        if (cid <= 0 || !DomainManager.Character.TryGetElement_Objects(cid, out c) || c == null)
                            return Fail("invalid_char", "角色无效");

                        Location appointmentLocation;
                        bool hasAppointmentRegistration =
                            DomainManager.Taiwu.TryGetElement_Appointments(cid, out appointmentLocation);
                        int taiwuId = DomainManager.Taiwu.GetTaiwuCharId();
                        CharacterGoalData anyAppointmentGoal = c.GetGoal(254);
                        // b24185552 AppointmentAction only represents the same appointment when
                        // goal 254 carries the same Taiwu + location arguments as the registry.
                        // Orphan or mismatched halves are stale state, not a valid appointment.
                        bool hasAppointment = hasAppointmentRegistration
                            && c.GetGoal(254, (PlanningContextArg)taiwuId,
                                (PlanningContextArg)appointmentLocation) != null;
                        bool hasStaleAppointmentGoal = anyAppointmentGoal != null && !hasAppointment;
                        var stateResult = new SerializableModData();
                        stateResult.Set("success", true);
                        stateResult.Set("stale_appointment_registry",
                            hasAppointmentRegistration && anyAppointmentGoal == null);
                        if (hasAppointment)
                        {
                            Location current = c.GetLocation();
                            bool waiting = current.IsValid() && current.Equals(appointmentLocation);
                            stateResult.Set("kind", "appointment");
                            stateResult.Set("state", waiting ? "waiting" : "traveling");
                            stateResult.Set("text", waiting
                                ? "与太吾的固定地点赴约仍有效：你已抵达约地，正在等太吾前来与你交谈"
                                : "与太吾的固定地点赴约仍有效：你尚在赴约途中；只有抵达约地后太吾前来交谈才会完成");
                            return stateResult;
                        }
                        if (hasStaleAppointmentGoal)
                        {
                            // Native TravelToActionTargetLocation precedes TravelToTargets. An
                            // orphan/mismatched goal can therefore still preempt an ordinary trip,
                            // but it must never be presented to the model as a valid appointment.
                            stateResult.Set("kind", "stale_appointment");
                            stateResult.Set("state", "invalid");
                            stateResult.Set("text", "检测到不完整或目标不一致的旧赴约状态：它不是有效约定，不能继续当作赴约承诺；重新安排普通行程会清理它");
                            return stateResult;
                        }

                        List<NpcTravelTarget> travelTargets = c.GetNpcTravelTargets();
                        if (travelTargets == null || travelTargets.Count == 0)
                        {
                            stateResult.Set("kind", "none");
                            stateResult.Set("state", "none");
                            stateResult.Set("text", "当前没有尚未完成的普通前往、寻人或固定赴约安排；旧行程不得继续当作仍在执行");
                            return stateResult;
                        }

                        NpcTravelTarget active = default(NpcTravelTarget);
                        bool hasExecutableTravelTarget = false;
                        Location realTarget = Location.Invalid;
                        // Match Character.TravelToTargets exactly: preserve list order, resolve a
                        // dynamic target, apply the beggar-skill forbidden-block fallback, then
                        // skip targets whose resulting destination is invalid.
                        foreach (NpcTravelTarget candidate in travelTargets)
                        {
                            Location candidateTarget = ResolveExecutableTravelTargetLocation(
                                candidate.GetRealTargetLocation());
                            if (!candidateTarget.IsValid()) continue;
                            active = candidate;
                            realTarget = candidateTarget;
                            hasExecutableTravelTarget = true;
                            break;
                        }
                        if (!hasExecutableTravelTarget)
                        {
                            stateResult.Set("kind", "none");
                            stateResult.Set("state", "none");
                            stateResult.Set("text", "当前没有尚未完成的普通前往、寻人或固定赴约安排；旧行程不得继续当作仍在执行");
                            return stateResult;
                        }
                        Location currentLocation = c.GetLocation();
                        bool reached = realTarget.IsValid() && currentLocation.IsValid()
                            && currentLocation.Equals(realTarget);
                        Location fixedLocation;
                        bool fixedTarget = active.TryGetFixedLocation(out fixedLocation);
                        string targetLabel = fixedTarget ? "固定地点" : (NameOf(active.TargetCharId) ?? "人物");
                        if (string.IsNullOrWhiteSpace(targetLabel)) targetLabel = "人物#" + active.TargetCharId;
                        stateResult.Set("kind", fixedTarget ? "fixed" : "character");
                        stateResult.Set("state", reached ? "arrived" : "traveling");
                        stateResult.Set("remaining_months", active.RemainingMonth);
                        stateResult.Set("target_char_id", active.TargetCharId);
                        stateResult.Set("text", reached
                            ? "前往" + targetLabel + "的行程已抵达，游戏将在本次移动结算后结束该行程"
                            : "正在逐月前往" + targetLabel + "，最多还保留" + active.RemainingMonth + "个月；抵达或到期即结束");
                        return stateResult;
                    }
                    case "area_chars":   // JHYL_AREA_CHARS_ALIVE_LOCATION_SCAN:先扫地块 CharacterSet,再扫全活人当前位置兜底;城镇/聚落角色常不挂在地块 CharacterSet 上。
                    {
                        int area = -1, cap = 50, twId = 0; p.Get("area", out area); p.Get("cap", out cap); p.Get("taiwu", out twId);
                        if (cap <= 0) cap = 50;
                        var ids = new List<int>(); var seen = new HashSet<int>();
                        int blockAccepted = 0, aliveAccepted = 0, allAliveScanned = 0;
                        bool AddCandidate(int cid, ref int accepted)
                        {
                            if (cid <= 0 || cid == twId || seen.Contains(cid)) return false;
                            try
                            {
                                Character cc;
                                if (!DomainManager.Character.TryGetElement_Objects(cid, out cc) || cc == null) return false;
                                if (cc.GetAgeGroup() == 0) return false;
                                if (!cc.IsInteractableAsIntelligentCharacter()) return false;
                                seen.Add(cid);
                                ids.Add(cid);
                                accepted++;
                                return true;
                            }
                            catch { return false; }
                        }
                        if (area >= 0)
                        {
                            try
                            {
                                var blocks = new List<MapBlockData>();
                                DomainManager.Map.GetPassableBlocksInArea((short)area, blocks);
                                foreach (var b in blocks)
                                {
                                    if (b == null || b.CharacterSet == null) continue;
                                    foreach (var cid in b.CharacterSet)
                                    {
                                        AddCandidate(cid, ref blockAccepted);
                                        if (ids.Count >= cap) break;
                                    }
                                    if (ids.Count >= cap) break;
                                }
                            }
                            catch { }
                            if (ids.Count < cap)
                            {
                                try
                                {
                                    var all = DomainManager.Character.GmCmd_GetAllCharacterName();
                                    allAliveScanned = all == null ? 0 : all.Count;
                                    if (all != null)
                                    {
                                        foreach (var cn in all)
                                        {
                                            if (ids.Count >= cap) break;
                                            int cid = cn.CharId;
                                            if (cid <= 0 || cid == twId || seen.Contains(cid)) continue;
                                            Character cc;
                                            if (!DomainManager.Character.TryGetElement_Objects(cid, out cc) || cc == null) continue;
                                            var loc2 = cc.GetLocation();
                                            if (!loc2.IsValid() || loc2.AreaId != (short)area) continue;
                                            AddCandidate(cid, ref aliveAccepted);
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        var okAc = new SerializableModData();
                        okAc.Set("success", true);
                        okAc.Set("ids", string.Join(",", ids));
                        okAc.Set("block_count", blockAccepted);
                        okAc.Set("alive_location_count", aliveAccepted);
                        okAc.Set("alive_scanned", allAliveScanned);
                        okAc.Set("source", blockAccepted > 0 ? (aliveAccepted > 0 ? "block+alive_location" : "block") : (aliveAccepted > 0 ? "alive_location" : "empty"));
                        return okAc;
                    }
                    case "org_members":   // 只读:某门派(OrgTemplateId)成员 id(grade=-1 全部 / 0-8 指定品级;cap 上限),供"查门派有谁 / 事件选人"
                    {
                        int orgTpl = 0, grade = -1, cap = 50, twId = 0;
                        p.Get("org", out orgTpl); p.Get("grade", out grade); p.Get("cap", out cap); p.Get("taiwu", out twId);
                        if (cap <= 0) cap = 50;
                        var ids = new List<int>(); var seen = new HashSet<int>();
                        try
                        {
                            var settle = DomainManager.Organization.GetSettlementByOrgTemplateId((sbyte)orgTpl);
                            if (settle != null)
                            {
                                var mc = settle.GetMembers();
                                if (mc != null)
                                {
                                    var all = new List<int>();
                                    if (grade >= 0 && grade <= 8) { var g = mc.GetMembers((sbyte)grade); if (g != null) all.AddRange(g); }
                                    else mc.GetAllMembers(all);
                                    foreach (var cid in all) { if (cid <= 0 || cid == twId || !seen.Add(cid)) continue; ids.Add(cid); if (ids.Count >= cap) break; }
                                }
                            }
                        }
                        catch { }
                        var okOm = new SerializableModData(); okOm.Set("success", true); okOm.Set("ids", string.Join(",", ids)); return okOm;
                    }
                    case "event_teach":   // 月度事件:A 传 B 一门 A 会而 B 不会的武学(后端自动选一门)
                    {
                        int aId = 0, bId = 0; p.Get("a", out aId); p.Get("b", out bId);
                        if (aId <= 0 || bId <= 0 || aId == bId) return Fail("bad_args", "参数无效");
                        Character ca, cb;
                        if (!DomainManager.Character.TryGetElement_Objects(aId, out ca) || ca == null ||
                            !DomainManager.Character.TryGetElement_Objects(bId, out cb) || cb == null) return Fail("invalid_char", "角色无效");
                        short pick = -1;
                        try
                        {
                            var aSkills = DomainManager.CombatSkill.GetCharCombatSkills(aId);
                            var bLearned = cb.GetLearnedCombatSkills();
                            if (aSkills != null) foreach (var kv in aSkills) { short tpl = kv.Key; if (bLearned == null || !bLearned.Contains(tpl)) { pick = tpl; break; } }
                        }
                        catch { }
                        if (pick < 0) return Fail("no_skill", "A 无可传 B 的武学");
                        if (HasLearnedCombatSkill(cb, pick)) return Fail("already", "B 已习得这门武学");
                        try { DomainManager.Character.LearnCombatSkill(context, bId, pick, (ushort)0); }
                        catch (Exception te)
                        {
                            if (!HasLearnedCombatSkill(cb, pick))
                                return Indeterminate("event_teach_indeterminate", "月度传授状态无法确认:" + te.GetType().Name);
                        }
                        if (!HasLearnedCombatSkill(cb, pick))
                            return Indeterminate("event_teach_postcondition_indeterminate", "月度传授接口已返回但 B 仍未学会");
                        var okT = new SerializableModData(); okT.Set("success", true); okT.Set("message", "ok"); return okT;
                    }
                    case "event_gift":   // 月度事件:A 赠 B 一件 A 身上价值较高的物
                    {
                        int aId = 0, bId = 0; p.Get("a", out aId); p.Get("b", out bId);
                        if (aId <= 0 || bId <= 0 || aId == bId) return Fail("bad_args", "参数无效");
                        Character ca, cb;
                        if (!DomainManager.Character.TryGetElement_Objects(aId, out ca) || ca == null ||
                            !DomainManager.Character.TryGetElement_Objects(bId, out cb) || cb == null) return Fail("invalid_char", "角色无效");
                        ItemKey best = default(ItemKey); int bestv = int.MinValue; bool found = false;
                        try
                        {
                            var inv = ca.GetInventory();
                            if (inv != null && inv.Items != null)
                                foreach (var kv in inv.Items)
                                {
                                    if (kv.Value <= 0) continue;
                                    var k = kv.Key;
                                    if (!global::GameData.Domains.Item.ItemTemplateHelper.IsTransferable(k.ItemType, k.TemplateId)
                                        || global::GameData.Domains.Item.ItemTemplateHelper.GetBaseValue(k.ItemType, k.TemplateId) == 0
                                        || global::GameData.Domains.Item.ItemTemplateHelper.GetIcon(k.ItemType, k.TemplateId) == null
                                        || global::GameData.Domains.Item.ItemTemplateHelper.IsSpecial(k.ItemType, k.TemplateId))
                                        continue;
                                    int v = global::GameData.Domains.Item.ItemTemplateHelper.GetBaseValue(k.ItemType, k.TemplateId);
                                    if (v > bestv) { bestv = v; best = k; found = true; }
                                }
                        }
                        catch { }
                        if (!found) return Fail("no_item", "A 身上无物可赠");
                        try
                        {
                            TransferInventoryChecked(context, ca, cb, best, 1,
                                TransferIntent.Gift);
                        }
                        catch (TransferPreconditionException ge)
                        {
                            // The checked transfer throws this type before invoking the authoritative
                            // write (for example locked/disappeared stock), so zero-write failure is known.
                            return Fail("event_gift_precondition", "月度赠物未发生:" + ge.Message);
                        }
                        catch (Exception ge) { return Indeterminate("event_gift_indeterminate", "月度赠物状态无法确认:" + ge.GetType().Name); }
                        var okG = new SerializableModData(); okG.Set("success", true); okG.Set("message", "ok"); return okG;
                    }
                    case "char_names":   // 只读:给一串 charId(逗号),按同序返回各自姓名( 分隔;取不到回空占位)。供群聊选人列表显示。
                    {
                        string idsStr = null; p.Get("ids", out idsStr);
                        var nm = new List<string>();
                        var genders = new List<string>();
                        if (!string.IsNullOrEmpty(idsStr))
                            foreach (var seg in idsStr.Split(','))
                            {
                                if (int.TryParse(seg, out int cid) && cid > 0)
                                {
                                    nm.Add(ShownName(cid) ?? "");
                                    Character c;
                                    genders.Add(DomainManager.Character.TryGetElement_Objects(cid, out c) && c != null
                                        ? (c.GetGender() == 1 ? "男" : c.GetGender() == 0 ? "女" : "未知")
                                        : "未知");
                                }
                                else { nm.Add(""); genders.Add("未知"); }
                            }
                        var okN = new SerializableModData(); okN.Set("success", true);
                        okN.Set("names", string.Join("", nm));
                        okN.Set("genders", string.Join("", genders));
                        return okN;
                    }
                    case "roster_relations":   // 只读:给一串 charId(逗号),返回他们【两两之间】的显著关系(夫妻/结义/师徒/挚友/情愫/仇敌),供过月事件叙述据实写人物纠葛。分号分隔。
                    {
                        string idsStr = null; bool includeEmpty = false;
                        p.Get("ids", out idsStr); p.Get("include_empty", out includeEmpty);
                        var ids = new List<int>(); var nms = new List<string>();
                        if (!string.IsNullOrEmpty(idsStr))
                            foreach (var seg in idsStr.Split(','))
                                if (int.TryParse(seg, out int cid) && cid > 0) { ids.Add(cid); nms.Add(ShownName(cid) ?? ("#" + cid)); }
                        var pairs = new List<string>();
                        for (int i = 0; i < ids.Count; i++)
                            for (int j = i + 1; j < ids.Count; j++)
                            {
                                string lab = RelationPairLabel(ids[i], ids[j], nms[i], nms[j]);
                                if (!string.IsNullOrEmpty(lab)) pairs.Add(lab);
                                else if (includeEmpty) pairs.Add(nms[i] + "与" + nms[j] + "无显著关系");
                            }
                        var okR = new SerializableModData(); okR.Set("success", true); okR.Set("relations", string.Join(";", pairs)); return okR;
                    }
                    case "monthly_action_preflight":
                    {
                        int actor = 0, target = 0; p.Get("actor", out actor); p.Get("target", out target);
                        if (actor <= 0 || target <= 0 || actor == target) return Fail("bad_args", "月度行动人物无效");
                        Character ac, tc;
                        bool actorAlive = DomainManager.Character.TryGetElement_Objects(actor, out ac) && ac != null;
                        bool targetAlive = DomainManager.Character.TryGetElement_Objects(target, out tc) && tc != null;
                        var result = new SerializableModData();
                        result.Set("success", true);
                        result.Set("actor_alive", actorAlive); result.Set("target_alive", targetAlive);
                        result.Set("actor_restrained", actorAlive && IsActorRestrained(ac, actor));
                        result.Set("actor_adult", actorAlive && ac.GetAgeGroup() == 2);
                        result.Set("target_adult", targetAlive && tc.GetAgeGroup() == 2);
                        bool actorInfected = actorAlive && ac.IsCompletelyInfected();
                        bool targetInfected = targetAlive && tc.IsCompletelyInfected();
                        Location actorLocation = actorAlive ? GetPhysicalSceneLocation(ac) : Location.Invalid;
                        Location targetLocation = targetAlive ? GetPhysicalSceneLocation(tc) : Location.Invalid;
                        bool sameValidLocation = actorAlive && targetAlive
                            && SameValidLocation(ac, tc);
                        int actorConsummate = actorAlive ? ac.GetConsummateLevel() : -1;
                        int targetConsummate = targetAlive ? tc.GetConsummateLevel() : -1;
                        bool targetKidnapped = targetAlive
                            && (tc.GetKidnapperId() >= 0
                                || DomainManager.Organization.GetPrisonerSect(target) >= 0);
                        int targetHealth = targetAlive ? tc.GetHealth() : 0;
                        int targetLeftMaxHealth = targetAlive ? tc.GetLeftMaxHealth() : 0;
                        int targetInjuryMarks = targetAlive ? tc.GetInjuries().GetSum() : 0;
                        bool targetNeedsHealing = targetAlive
                            && (targetHealth < targetLeftMaxHealth || targetInjuryMarks > 0);
                        bool actorHasRope = false, actorHasPoison = false, targetPoisonImmune = false;
                        sbyte selectedPoisonType = -1;
                        if (actorAlive)
                        {
                            try
                            {
                                var inventory = ac.GetInventory();
                                if (inventory != null && inventory.Items != null)
                                    foreach (var item in inventory.Items)
                                    {
                                        if (item.Value <= 0) continue;
                                        ItemKey key = item.Key;
                                        if (key.ItemType == (sbyte)12 && key.TemplateId >= 82 && key.TemplateId <= 90)
                                            actorHasRope = true;
                                        sbyte poisonType = ItemTemplateHelper.GetMedicineItemPoisonType(key.ItemType, key.TemplateId);
                                        if (!actorHasPoison && poisonType >= 0)
                                        { actorHasPoison = true; selectedPoisonType = poisonType; }
                                    }
                            }
                            catch { }
                        }
                        if (targetAlive && selectedPoisonType >= 0)
                            try { targetPoisonImmune = tc.HasPoisonImmunity(selectedPoisonType); } catch { }
                        bool canMarry = false;
                        if (actorAlive && targetAlive && ac.GetAgeGroup() == 2 && tc.GetAgeGroup() == 2
                            && !actorInfected && !targetInfected)
                            try { canMarry = RelationTypeHelper.AllowAddingHusbandOrWifeRelation(actor, target); }
                            catch { canMarry = false; }
                        result.Set("actor_infected", actorInfected); result.Set("target_infected", targetInfected);
                        result.Set("actor_area", actorLocation.IsValid() ? actorLocation.AreaId : (short)-1);
                        result.Set("actor_block", actorLocation.IsValid() ? actorLocation.BlockId : (short)-1);
                        result.Set("target_area", targetLocation.IsValid() ? targetLocation.AreaId : (short)-1);
                        result.Set("target_block", targetLocation.IsValid() ? targetLocation.BlockId : (short)-1);
                        result.Set("same_valid_location", sameValidLocation);
                        result.Set("actor_consummate", actorConsummate); result.Set("target_consummate", targetConsummate);
                        result.Set("strong_enough", actorAlive && targetAlive && actorConsummate >= targetConsummate);
                        result.Set("target_kidnapped", targetKidnapped);
                        result.Set("target_health", targetHealth);
                        result.Set("target_left_max_health", targetLeftMaxHealth);
                        result.Set("target_injury_marks", targetInjuryMarks);
                        result.Set("target_needs_healing", targetNeedsHealing);
                        result.Set("target_is_taiwu", target == DomainManager.Taiwu.GetTaiwuCharId());
                        result.Set("actor_has_rope", actorHasRope); result.Set("actor_has_poison", actorHasPoison);
                        result.Set("target_poison_immune", targetPoisonImmune); result.Set("can_marry", canMarry);
                        string travelCode = "invalid_char", travelReason = "行动者已失效";
                        bool actorCanTravel = actorAlive
                            && TryGetNpcTravelEligibility(actor, ac, out travelCode, out travelReason);
                        result.Set("actor_can_travel", actorCanTravel);
                        result.Set("actor_travel_code", travelCode ?? string.Empty);
                        result.Set("actor_travel_reason", travelReason ?? string.Empty);
                        bool enemy = false, spouse = false, sworn = false, friend = false, adored = false, mentor = false;
                        bool adoptiveParent = false, adoptiveChild = false;
                        bool canAdoptiveParent = false, canAdoptiveChild = false;
                        string adoptiveParentReason = string.Empty, adoptiveChildReason = string.Empty;
                        if (actorAlive && targetAlive)
                        {
                            var chars = DomainManager.Character;
                            enemy = chars.HasRelation(actor, target, (ushort)32768) || chars.HasRelation(target, actor, (ushort)32768);
                            spouse = chars.HasRelation(actor, target, (ushort)1024) || chars.HasRelation(target, actor, (ushort)1024);
                            sworn = chars.HasRelation(actor, target, (ushort)512) || chars.HasRelation(target, actor, (ushort)512);
                            friend = chars.HasRelation(actor, target, (ushort)8192) || chars.HasRelation(target, actor, (ushort)8192);
                            bool actorAdoresTarget = chars.HasRelation(actor, target, (ushort)16384);
                            bool targetAdoresActor = chars.HasRelation(target, actor, (ushort)16384);
                            adored = actorAdoresTarget && targetAdoresActor;
                            result.Set("actor_adores_target", actorAdoresTarget);
                            result.Set("target_adores_actor", targetAdoresActor);
                            mentor = chars.HasRelation(actor, target, (ushort)2048) || chars.HasRelation(target, actor, (ushort)2048);
                            adoptiveParent = chars.HasRelation(actor, target, (ushort)64);
                            adoptiveChild = chars.HasRelation(actor, target, (ushort)128);
                            string adoptiveCode;
                            canAdoptiveParent = ValidateAdoptiveRelation(actor, ac, target, tc, true,
                                out adoptiveCode, out adoptiveParentReason);
                            canAdoptiveChild = ValidateAdoptiveRelation(actor, ac, target, tc, false,
                                out adoptiveCode, out adoptiveChildReason);
                        }
                        result.Set("enemy", enemy); result.Set("spouse", spouse); result.Set("sworn", sworn);
                        result.Set("friend", friend); result.Set("adored", adored); result.Set("mentor", mentor);
                        result.Set("adoptive_parent", adoptiveParent);
                        result.Set("adoptive_child", adoptiveChild);
                        result.Set("can_adoptive_parent", canAdoptiveParent);
                        result.Set("can_adoptive_child", canAdoptiveChild);
                        result.Set("adoptive_parent_reason", adoptiveParentReason ?? string.Empty);
                        result.Set("adoptive_child_reason", adoptiveChildReason ?? string.Empty);
                        int actorFavor = 0;
                        try
                        {
                            short raw = DomainManager.Character.GetFavorability(actor, target);
                            actorFavor = raw == short.MinValue ? 0 : raw;
                        }
                        catch { }
                        result.Set("actor_favor", actorFavor);
                        result.Set("relation", actorAlive && targetAlive
                            ? (RelationPairLabel(actor, target, ShownName(actor) ?? ("#" + actor), ShownName(target) ?? ("#" + target)) ?? "无显著关系")
                            : "角色已失效");
                        return result;
                    }
                    case "heal_preflight":   // 只读：疗伤目标可与行动者相同，不能复用要求人物不同的 pair preflight。
                    {
                        int target = 0; p.Get("target", out target);
                        Character tc = null;
                        bool targetAlive = target > 0
                            && DomainManager.Character.TryGetElement_Objects(target, out tc) && tc != null;
                        var result = new SerializableModData();
                        result.Set("success", true);
                        result.Set("target_alive", targetAlive);
                        int health = targetAlive ? tc.GetHealth() : 0;
                        int leftMax = targetAlive ? tc.GetLeftMaxHealth() : 0;
                        int injuries = targetAlive ? tc.GetInjuries().GetSum() : 0;
                        result.Set("target_health", health);
                        result.Set("target_left_max_health", leftMax);
                        result.Set("target_injury_marks", injuries);
                        result.Set("target_needs_healing", targetAlive
                            && (health < leftMax || injuries > 0));
                        return result;
                    }
                    case "health_status":   // 只读：当前 NPC 自省伤势、内息与六类中毒。
                    {
                        int actor = 0; p.Get("actor", out actor);
                        Character character;
                        if (actor <= 0 || !DomainManager.Character.TryGetElement_Objects(actor,
                                out character) || character == null)
                            return Fail("invalid_char", "当前人物已失效");
                        PoisonInts poisons = character.GetPoisoned();
                        short qi = character.GetDisorderOfQi();
                        var result = new SerializableModData();
                        result.Set("success", true);
                        result.Set("health", (int)character.GetHealth());
                        result.Set("left_max_health", (int)character.GetLeftMaxHealth());
                        result.Set("injury_marks", character.GetInjuries().GetSum());
                        result.Set("qi_disorder", (int)qi);
                        result.Set("qi_disorder_show", (int)DisorderLevelOfQi.GetShowValue(qi));
                        result.Set("qi_disorder_level", QiDisorderLevelName(
                            DisorderLevelOfQi.GetDisorderLevelOfQi(qi)));
                        result.Set("poisons", PoisonValuesCsv(poisons));
                        result.Set("poison_summary", PoisonSummary(poisons));
                        return result;
                    }
                    case "char_food":   // 只读:某角色随身熟食(EatingItems,荷包蛋等)的 ItemKey,逗号编码、分号分隔。供"赠物"补全(熟食不在背包 Items 里)
                    {
                        int charId = 0; p.Get("char", out charId);
                        Character c;
                        if (!DomainManager.Character.TryGetElement_Objects(charId, out c) || c == null) return Fail("invalid_char", "角色无效");
                        var eat = c.GetEatingItems();
                        var parts = new List<string>();
                        for (int i = 0; i < 9; i++)
                        {
                            var k = eat.GetItem(i);
                            if (!EatingItems.IsValid(k)) continue;
                            parts.Add((int)k.ItemType + "," + (int)k.ModificationState + "," + (int)k.TemplateId + "," + k.Id);
                        }
                        var okFood = new SerializableModData(); okFood.Set("success", true); okFood.Set("food", string.Join(";", parts)); return okFood;
                    }
                    case "npc_items":   // 全实时:枚举 NPC【当前】全部可赠/可换之物(背包+资源+熟食+佩带),按 keyword 过滤,回名字( 分隔)+ 总数
                    {
                        int charId = 0; string kw = null; p.Get("char", out charId); p.Get("keyword", out kw);
                        Character c;
                        if (!DomainManager.Character.TryGetElement_Objects(charId, out c) || c == null) { var f = new SerializableModData(); f.Set("success", true); f.Set("names", ""); f.Set("total", 0); return f; }
                        var hold = new List<NpcHolding>();
                        EnumerateNpcHoldings(c, hold);
                        string q = (kw ?? "").Trim();
                        var names = new List<string>();
                        foreach (var h in hold) if (HoldingHit(h, q)) { string disp = ItemNameWithGrade(h.Name, h.Key); names.Add(h.Count > 1 ? (disp + "×" + h.Count) : disp); }
                        // 奇书(LegendaryBook):单独域管理、不在背包 Items 里,寻常物品枚举会漏——单列于末尾,标明乃异宝
                        var books = OwnedLegendaryBookNames(charId);
                        foreach (var bn in books) if (q.Length == 0 || NameHit(bn, q) || q.Contains("书") || q.Contains("奇书") || q.Contains("异宝")) names.Add("【奇书·异宝,不可赠予】" + bn);   // 奇书无转移途径,标明免模型徒劳尝试赠送
                        var okNi = new SerializableModData(); okNi.Set("success", true); okNi.Set("names", string.Join("", names)); okNi.Set("total", hold.Count + books.Count); return okNi;
                    }
                    case "query_npc_usable_items":   // 只读：与 use_item 完全共用的 NPC 实时可用候选。
                    {
                        int charId = 0; p.Get("char", out charId);
                        Character c;
                        if (charId <= 0 || charId == DomainManager.Taiwu.GetTaiwuCharId()
                            || !DomainManager.Character.TryGetElement_Objects(charId, out c) || c == null)
                            return Fail("invalid_char", "NPC 角色无效");
                        var names = new List<string>();
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in c.GetInventory().Items)
                        {
                            if (pair.Value <= 0
                                || !TryCanNpcUseItemNow(c, pair.Key, pair.Value,
                                    out _, out _, out _)) continue;
                            string itemName = null;
                            try { itemName = StripTags(ItemTemplateHelper.GetName(
                                pair.Key.ItemType, pair.Key.TemplateId)); } catch { }
                            if (string.IsNullOrWhiteSpace(itemName) || !seen.Add(itemName.Trim())) continue;
                            names.Add(itemName.Trim());
                        }
                        names.Sort(StringComparer.CurrentCultureIgnoreCase);
                        var usable = new SerializableModData();
                        usable.Set("success", true); usable.Set("message", "ok");
                        usable.Set("names", string.Join("", names));
                        return usable;
                    }
                    case "char_books":   // 只读:某角色拥有的奇书(LegendaryBook)名字, 分隔。供物品查询补全(奇书不在背包 Items 里)。
                    {
                        int cbId = 0; p.Get("char", out cbId);
                        var okCb = new SerializableModData(); okCb.Set("success", true);
                        okCb.Set("names", string.Join("", OwnedLegendaryBookNames(cbId))); return okCb;
                    }
                    case "looping_neigong":   // 只读:某角色当前真正正在运功的内功 templateId; -1=未运功。
                    {
                        int charId = 0; p.Get("char", out charId);
                        Character c;
                        if (!DomainManager.Character.TryGetElement_Objects(charId, out c) || c == null) return Fail("invalid_char", "角色无效");
                        var okLn = new SerializableModData(); okLn.Set("success", true);
                        okLn.Set("skill", (int)c.GetLoopingNeigong()); return okLn;
                    }
                    case "favors":   // 批量读一串 NPC 对太吾的好感(未互动→哨兵→归一为0),csv 同序返回,供过月主动消息按好感选人
                    {
                        int twId = 0; string idsCsv = null; p.Get("taiwu", out twId); p.Get("ids", out idsCsv);
                        var fv = new List<string>();
                        if (!string.IsNullOrEmpty(idsCsv))
                            foreach (var s in idsCsv.Split(','))
                            {
                                int cid; if (!int.TryParse(s, out cid)) { fv.Add("0"); continue; }
                                int norm = 0;
                                try { short f = DomainManager.Character.IsInteractedWithTaiwu(cid) ? DomainManager.Character.GetFavorability(cid, twId) : short.MinValue; norm = (f == short.MinValue) ? 0 : f; } catch { norm = 0; }
                                fv.Add(norm.ToString());
                            }
                        var okFv = new SerializableModData(); okFv.Set("success", true); okFv.Set("favors", string.Join(",", fv)); return okFv;
                    }
                    case "person_info":   // 查熟人:speaker 与 target 之间的关系名 + speaker 对 target 的好感(归一),供 query_person
                    {
                        int sp = 0, tg = 0; p.Get("speaker", out sp); p.Get("target", out tg);
                        string rel = RelationBetween(sp, tg);
                        int favST = 0;
                        try { short fvv = DomainManager.Character.GetFavorability(sp, tg); favST = (fvv == short.MinValue) ? 0 : fvv; } catch { favST = 0; }
                        var okPi = new SerializableModData(); okPi.Set("success", true); okPi.Set("rel", rel ?? ""); okPi.Set("favor", favST); return okPi;
                    }
                    case "persona_traits":   // 只读：画像生成需要、显示数据未公开的稳定人物字段。
                    {
                        int charId = 0; p.Get("char", out charId);
                        Character character;
                        if (charId <= 0 || !DomainManager.Character.TryGetElement_Objects(charId, out character)
                            || character == null)
                            return Fail("invalid_char", "人物不存在或已失效");
                        var traits = new SerializableModData();
                        traits.Set("success", true);
                        // 本体字段名虽叫 Bisexual，真实语义是“性取向同时包含同性与异性”；
                        // false 则为异性向。只回传事实，不由模型从性别/关系猜测。
                        traits.Set("bisexual", character.GetBisexual());
                        return traits;
                    }
                    case "spend_night":   // 春宵一刻:两名角色共度(原生 MakeLove;是否有孕交由本体判定)。须双方成年、且有恋人/夫妻之情或发起者对对方情意足够。
                    {
                        int actorId = 0, targetId = 0, taiwuId = 0;
                        p.Get("npc", out actorId); p.Get("target", out targetId); p.Get("taiwu", out taiwuId);
                        // 旧调用没有 target，实际对象就是太吾；新调用把太吾仅作为存档身份锚点。
                        if (targetId <= 0) targetId = taiwuId;
                        if (actorId <= 0 || targetId <= 0 || taiwuId <= 0 || actorId == targetId)
                            return Fail("bad_args", "参数无效");
                        Character actorC, targetC;
                        if (!DomainManager.Character.TryGetElement_Objects(actorId, out actorC) || actorC == null ||
                            !DomainManager.Character.TryGetElement_Objects(targetId, out targetC) || targetC == null)
                            return Fail("invalid_char", "角色无效");
                        if (actorC.GetAgeGroup() != 2 || targetC.GetAgeGroup() != 2)   // 双方须成年,绝不涉及未成年
                            return Fail("not_adult", "未及成年,此事作罢");
                        // 须有亲密之情:恋人(16384)或夫妻(1024),任一方向;或发起者对对方好感已达"喜爱"(>=18000)。
                        bool intimate = DomainManager.Character.HasRelation(targetId, actorId, (ushort)1024)
                            || DomainManager.Character.HasRelation(actorId, targetId, (ushort)1024)
                            || DomainManager.Character.HasRelation(targetId, actorId, (ushort)16384)
                            || DomainManager.Character.HasRelation(actorId, targetId, (ushort)16384);
                        if (!intimate)
                        {
                            short favNT = DomainManager.Character.GetFavorability(actorId, targetId);
                            if (favNT >= 18000) intimate = true;
                        }
                        if (!intimate) return Fail("not_intimate", "发起者对对方尚无那般情意,不肯共度春宵");
                        try { actorC.MakeLove(context, targetC, false); }   // 原生:记录春宵;若本体判定可孕则按概率致孕,同性无可孕父母组合时仅不致孕。
                        catch (Exception e) { return Indeterminate("spend_night_indeterminate", "春宵记录或连带状态无法确认:" + e.GetType().Name); }
                        var okSn = new SerializableModData(); okSn.Set("success", true); okSn.Set("message", "ok"); return okSn;
                    }
                    case "npc_relations":   // NPC 的关系网(父母/子女/兄弟/结义/配偶/心上人/师父/挚友/仇敌等),供 query_npc_relationships 真返数据
                    {
                        int rnpc = 0; p.Get("npc", out rnpc);
                        if (rnpc <= 0) return Fail("bad_args", "参数无效");
                        RelatedCharactersForRelations rrel = null;
                        try { rrel = DomainManager.Character.GetRelatedCharactersForRelations(rnpc); } catch { }
                        RelatedCharacters rawRel = null;
                        int rawRelationOwnerId = RelationOwnerId(rnpc);
                        try { rawRel = DomainManager.Character.GetRelatedCharacters(rawRelationOwnerId); } catch { }
                        var rsb = new System.Text.StringBuilder();
                        if (rrel != null)
                        {
                            // 标签一律写明【辈分/方向】,免大模型把父子、师徒说反("此人"=被查者本人)
                            RelFamilyBuckets(rsb, rawRel, rrel);
                            RelBucket(rsb, "结义兄弟姐妹", rrel.SwornBrothersAndSisters);
                            RelBucket(rsb, "配偶", rrel.HusbandsAndWives);
                            RelBucket(rsb, "心上人(此人所爱慕者)", rrel.Adored);
                            RelBucket(rsb, "爱慕此人的人", rrel.RelatedAdored);
                            RelBucket(rsb, "师父(传授此人武艺的长辈;此人是其徒弟)", rrel.Mentors);
                            if (rawRel != null) RelBucket(rsb, "徒弟(此人所收弟子;此人是其师父)", rawRel.Mentees); // JHYL_RELATION_RESOLVE_MENTEE_BUCKET
                            RelBucket(rsb, "挚友", rrel.Friends);
                            RelBucket(rsb, "仇敌(此人视为仇的人)", rrel.Enemies);
                            RelBucket(rsb, "视此人为仇者", rrel.RelatedEnemies);
                        }
                        string relHead = rsb.Length > 0 ? "(下列关系均【从此人视角】列出,括注即方向——『师父』是教他的长辈、『徒弟』是此人收的弟子、『子女』是他的儿女,据实陈述、切勿把辈分或师徒方向说反)\n" : "";
                        var okRel = new SerializableModData(); okRel.Set("success", true);
                        okRel.Set("relations", relHead + rsb.ToString());
                        // “心之所系”必须来自被查者自己指向他人的 Adored 集合；
                        // RelatedAdored 是别人爱慕此人，方向相反，不能混用。
                        okRel.Set("adored", rrel == null ? "" : RelNames(rrel.Adored, 16));
                        return okRel;
                    }
                    case "npc_relation_ids":   // 只读:NPC 的显著关系网实体 id。供过月同道列出可千里传音的真实远方联系人。
                    {
                        int rnpc = 0; p.Get("npc", out rnpc);
                        if (rnpc <= 0) return Fail("bad_args", "参数无效");
                        RelatedCharactersForRelations rrel = null;
                        try { rrel = DomainManager.Character.GetRelatedCharactersForRelations(rnpc); } catch { }
                        RelatedCharacters rawRel = null;
                        int rawRelationOwnerId = RelationOwnerId(rnpc);
                        try { rawRel = DomainManager.Character.GetRelatedCharacters(rawRelationOwnerId); } catch { }
                        var relatedIds = new HashSet<int>();
                        var motiveFacts = new List<string>();
                        Action<CharacterSet> addSet = set =>
                        {
                            HashSet<int> values = null;
                            try { values = set.GetCollection(); } catch { }
                            if (values == null) return;
                            foreach (int id in values)
                            {
                                if (id <= 0 || id == rnpc || relatedIds.Count >= 96) continue;
                                try
                                {
                                    Character person;
                                    if (!DomainManager.Character.TryGetElement_Objects(id, out person) || person == null
                                        || person.GetAgeGroup() == 0 || !person.IsInteractableAsIntelligentCharacter()) continue;
                                    relatedIds.Add(id);
                                }
                                catch { }
                            }
                        };
                        if (rrel != null)
                        {
                            addSet(rrel.Parents); addSet(rrel.Children); addSet(rrel.BrothersAndSisters);
                            addSet(rrel.SwornBrothersAndSisters); addSet(rrel.HusbandsAndWives);
                            addSet(rrel.Mentors);
                            addSet(rrel.Friends); addSet(rrel.Enemies); addSet(rrel.RelatedEnemies);
                            Action<string, CharacterSet> addMotiveSet = (kind, set) =>
                            {
                                HashSet<int> values = null;
                                try { values = set.GetCollection(); } catch { }
                                if (values == null) return;
                                foreach (int id in values)
                                {
                                    if (id <= 0 || id == rnpc || motiveFacts.Count >= 64) continue;
                                    motiveFacts.Add(kind + ":" + id);
                                }
                            };
                            addMotiveSet("enemy", rrel.Enemies);
                            addMotiveSet("enemy_of", rrel.RelatedEnemies);
                        }
                        if (rawRel != null) addSet(rawRel.Mentees);
                        var sortedRelatedIds = new List<int>(relatedIds); sortedRelatedIds.Sort();
                        var relationText = new System.Text.StringBuilder();
                        if (rrel != null)
                        {
                            RelFamilyBuckets(relationText, rawRel, rrel);
                            RelBucket(relationText, "结义兄弟姐妹", rrel.SwornBrothersAndSisters);
                            RelBucket(relationText, "配偶", rrel.HusbandsAndWives);
                            RelBucket(relationText, "师父(传授此人武艺的长辈;此人是其徒弟)", rrel.Mentors);
                            if (rawRel != null) RelBucket(relationText, "徒弟(此人所收弟子;此人是其师父)", rawRel.Mentees);
                            RelBucket(relationText, "挚友", rrel.Friends);
                            RelBucket(relationText, "仇敌(此人视为仇的人)", rrel.Enemies);
                            RelBucket(relationText, "视此人为仇者", rrel.RelatedEnemies);
                        }
                        string relationHead = relationText.Length > 0
                            ? "(下列关系均【从此人视角】列出,括注即方向——『师父』是教他的长辈、『徒弟』是此人收的弟子、『子女』是他的儿女,据实陈述、切勿把辈分或师徒方向说反)\n"
                            : "";
                        var okRelated = new SerializableModData(); okRelated.Set("success", true);
                        okRelated.Set("ids", string.Join(",", sortedRelatedIds));
                        okRelated.Set("relations", relationHead + relationText.ToString());
                        okRelated.Set("motive_facts", string.Join(";", motiveFacts));
                        return okRelated;
                    }
                    case "story_status":   // 主线进度(相枢之劫,世道公知)+ NPC 所属门派的门派主线状态(本门中人知晓),供 query_world_progress 扩展
                    {
                        int org = 0; p.Get("org", out org);
                        short mainProg = -1;
                        try { mainProg = DomainManager.World.GetMainStoryLineProgress(); } catch { }
                        int sectStatus = -1;   // -1=该 NPC 无门派主线 / 不适用;0=未启 1=进行中 2=已了结(善果) 3=已了结(恶果)
                        try { if (org >= 1 && org <= 15) sectStatus = DomainManager.Story.GetSectMainStoryTaskStatus((sbyte)org); } catch { }
                        var okSt = new SerializableModData(); okSt.Set("success", true); okSt.Set("main", (int)mainProg); okSt.Set("sect_status", sectStatus); return okSt;
                    }
                    default: return Fail("unknown_op", "未知 op: " + op);
                }
            }
            catch (Exception e)
            {
                if (e is TransferPreconditionException)
                    return Fail("gm_precondition_failed", op + " 未执行:" + e.Message);
                return IsGmMutationOp(op)
                    ? Indeterminate("gm_mutation_indeterminate", op + " 写入状态无法确认:" + e.GetType().Name)
                    : Fail("gm_failed", op + " 执行失败:" + e.GetType().Name);
            }
            var ret = new SerializableModData(); ret.Set("success", true); ret.Set("message", "ok"); return ret;
        }

        // 只有真正改写游戏态的 Gm op 才进 operation ledger；query_* 类读取不会挤掉有价值的副作用回执。
        private static bool IsGmMutationOp(string op)
        {
            switch (op)
            {
                case "favor": case "givesilver": case "happiness": case "reaction": case "giveitem":
                case "equip": case "takeoff": case "poison": case "heal": case "detox":
                case "regulate_breath": case "enmity":
                case "joinsect": case "changeorgbyname": case "sharesecret": case "npcsecret":
                case "disclosesecret": case "sect_support": case "dissolve": case "teachlife":
                case "trade": case "taiwu_give_item": case "barter": case "steal": case "taiwu_teach":
                case "flip_practice": case "npc_train_skill": case "npc_read_book": case "use_item":
                case "writebook": case "taiwu_fame": case "character_fame": case "relate_npc":
                case "addfeature": case "merchantfavor": case "event_teach": case "event_gift":
                case "spend_night": case "commission_claim":
                    return true;
                default:
                    return false;
            }
        }

        private sealed class CommissionRewardGrant
        {
            public string Kind;
            public string Name;
            public int Amount;
            public int Grade;
            public sbyte ResourceType = -1;
            public int ResourceBefore;
            public ItemKey ItemKey;
            public int InventoryBefore;
            public bool ItemCreated;
        }

        private sealed class CommissionRewardTemplate
        {
            public sbyte ItemType;
            public short TemplateId;
        }

        private static SerializableModData ClaimCommissionReward(DataContext context,
            SerializableModData parameter)
        {
            int taiwuId = 0, npcId = 0, resourceType = -1, amount = 0, targetId = -1;
            int baseline = 0, rewardGrade = -1;
            string kind = null;
            parameter.Get("taiwu", out taiwuId); parameter.Get("npc", out npcId);
            parameter.Get("kind", out kind); parameter.Get("resource", out resourceType);
            parameter.Get("amount", out amount); parameter.Get("baseline", out baseline);
            parameter.Get("reward_grade", out rewardGrade);
            parameter.Get("target", out targetId);
            bool assistantCommission = npcId == 0;
            if (taiwuId <= 0 || npcId < 0 || taiwuId == npcId
                || taiwuId != DomainManager.Taiwu.GetTaiwuCharId()
                || !CommissionAmountRange(kind, out int minAmount, out int maxAmount)
                || amount < minAmount || amount > maxAmount || baseline < 0
                || rewardGrade < 0 || rewardGrade > 8
                || (CommissionNeedsResource(kind) && (resourceType < 0 || resourceType > 5))
                || (string.Equals(kind, "kill_npc", StringComparison.Ordinal)
                    && (targetId <= 0 || targetId == taiwuId || targetId == npcId))
                || (assistantCommission && (string.Equals(kind, "deliver_resource",
                        StringComparison.Ordinal)
                    || string.Equals(kind, "increase_favor", StringComparison.Ordinal))))
                return Fail("bad_args", "委托领奖参数无效");

            Character taiwu, npc = null;
            if (!DomainManager.Character.TryGetElement_Objects(taiwuId, out taiwu) || taiwu == null
                || (!assistantCommission
                    && (!DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null)))
                return Fail("invalid_char", "委托人物已失效");

            int current = CommissionCurrentValue(taiwu, npcId, taiwuId, kind, resourceType,
                targetId);
            int target = CommissionProgressPolicy.TargetValue(kind, baseline, amount);
            bool completed = CommissionProgressPolicy.IsCompleted(kind, current, baseline, amount);
            if (!completed)
                return Fail("commission_incomplete", "委托尚未完成（当前 " + current
                    + " / 目标 " + target + "）");

            int[] taiwuResourcesBefore = new int[8];
            for (sbyte rt = 0; rt < 8; rt++) taiwuResourcesBefore[rt] = taiwu.GetResource(rt);
            int npcDeliveryBefore = npc != null && resourceType >= 0 && resourceType < 8
                ? npc.GetResource((sbyte)resourceType) : 0;
            var rewards = new List<CommissionRewardGrant>();
            bool deliveryMoved = false;
            try
            {
                if (string.Equals(kind, "deliver_resource", StringComparison.Ordinal))
                {
                    TransferResourceChecked(context, taiwu, npc, (sbyte)resourceType, amount);
                    deliveryMoved = true;
                }
                bool killCommission = string.Equals(kind, "kill_npc", StringComparison.Ordinal);
                int rewardCount = killCommission ? 3 + context.Random.Next(2) : 2;
                for (int rewardIndex = 0; rewardIndex < rewardCount; rewardIndex++)
                {
                    CommissionRewardGrant reward = GrantRandomCommissionReward(context, taiwu,
                        rewardGrade);
                    if (reward == null) throw new InvalidOperationException("没有可生成的有效奖励");
                    rewards.Add(reward);
                }
                string rewardSummary = string.Join("、", rewards.Select(x =>
                    (x.Name ?? "奖励") + (x.Amount > 1 ? " ×" + x.Amount : string.Empty)));

                var result = new SerializableModData();
                result.Set("success", true);
                result.Set("message", "ok");
                result.Set("reward_kind", rewards.Count > 1 ? "bundle"
                    : (rewards[0].Kind ?? "reward"));
                result.Set("reward_name", rewardSummary);
                result.Set("reward_amount", 1);
                result.Set("reward_grade", rewardGrade);
                result.Set("current", current);
                result.Set("target", target);
                return result;
            }
            catch (Exception error)
            {
                bool rewardRestored = true;
                for (int i = rewards.Count - 1; i >= 0; i--)
                    rewardRestored &= RollbackCommissionReward(context, taiwu, rewards[i]);
                bool deliveryRestored = true;
                if (deliveryMoved)
                    deliveryRestored = TryRestoreResourcePair(context, taiwu, npc,
                        (sbyte)resourceType, taiwuResourcesBefore[resourceType], npcDeliveryBefore);
                if (rewardRestored && deliveryRestored)
                    return Fail("commission_reward_failed",
                        "奖励生成未完成，已恢复交付前状态:" + error.GetType().Name);
                return Indeterminate("commission_claim_indeterminate",
                    "委托交付或奖励出现部分写入，且补偿未能完整确认:" + error.GetType().Name);
            }
        }

        private static bool CommissionAmountRange(string kind, out int min, out int max)
        {
            switch (kind)
            {
                case "deliver_resource": min = 50; max = 5000; return true;
                case "collect_resource": min = 100; max = 10000; return true;
                case "earn_money": min = 500; max = 30000; return true;
                case "gain_prestige": min = 100; max = 8000; return true;
                case "increase_favor": min = 300; max = 5000; return true;
                case "kill_npc": min = max = 1; return true;
                default: min = max = 0; return false;
            }
        }

        private static bool CommissionNeedsResource(string kind)
            => string.Equals(kind, "deliver_resource", StringComparison.Ordinal)
                || string.Equals(kind, "collect_resource", StringComparison.Ordinal);

        private static int CommissionCurrentValue(Character taiwu, int npcId, int taiwuId,
            string kind, int resourceType, int targetId)
        {
            if (string.Equals(kind, "deliver_resource", StringComparison.Ordinal)
                || string.Equals(kind, "collect_resource", StringComparison.Ordinal))
                return taiwu.GetResource((sbyte)resourceType);
            if (string.Equals(kind, "earn_money", StringComparison.Ordinal))
                return taiwu.GetResource((sbyte)6);
            if (string.Equals(kind, "gain_prestige", StringComparison.Ordinal))
                return taiwu.GetResource((sbyte)7);
            if (string.Equals(kind, "kill_npc", StringComparison.Ordinal))
                return targetId > 0 && DomainManager.Character.TryGetDeadCharacter(targetId) != null
                    ? 1 : 0;
            short favor = DomainManager.Character.GetFavorability(npcId, taiwuId);
            return favor == short.MinValue ? 0 : Math.Max(0, (int)favor);
        }

        private static CommissionRewardGrant GrantRandomCommissionReward(DataContext context,
            Character taiwu, int grade)
        {
            // 资源与物品都进入奖励池。资源数量随品级显著上涨；物品严格使用该品级
            // 的可转移本体模板。随机失败时从物品池回退资源池，不让高品级委托落空。
            if (context.Random.Next(100) < 45)
                return GrantCommissionResource(context, taiwu, grade);

            var templates = new List<CommissionRewardTemplate>();
            for (sbyte itemType = 0; itemType <= 12; itemType++)
            {
                IList<int> keys = null;
                try { keys = ItemTemplateHelper.GetTemplateDataAllKeys(itemType); }
                catch { }
                if (keys == null) continue;
                foreach (int rawTemplateId in keys)
                {
                    if (rawTemplateId < short.MinValue || rawTemplateId > short.MaxValue) continue;
                    short templateId = (short)rawTemplateId;
                    try
                    {
                        if (!ItemTemplateHelper.CheckTemplateValid(itemType, templateId)
                            || ItemTemplateHelper.GetGrade(itemType, templateId) != grade
                            || !ItemTemplateHelper.IsTransferable(itemType, templateId)
                            || ItemTemplateHelper.GetBaseValue(itemType, templateId) <= 0
                            || string.IsNullOrWhiteSpace(ItemTemplateHelper.GetIcon(itemType, templateId))
                            || ItemTemplateHelper.IsSpecial(itemType, templateId)
                            || ItemTemplateHelper.IsMiscResource(itemType, templateId)) continue;
                        templates.Add(new CommissionRewardTemplate
                        {
                            ItemType = itemType,
                            TemplateId = templateId,
                        });
                    }
                    catch { }
                }
            }

            while (templates.Count > 0)
            {
                int index = context.Random.Next(templates.Count);
                CommissionRewardTemplate template = templates[index];
                templates.RemoveAt(index);
                ItemKey key = default(ItemKey);
                bool created = false;
                int inventoryBefore = 0;
                try
                {
                    key = DomainManager.Item.CreateItem(context, template.ItemType,
                        template.TemplateId);
                    created = true;
                    inventoryBefore = InventoryCount(taiwu, key);
                    int rewardAmount = ItemTemplateHelper.IsStackable(template.ItemType,
                        template.TemplateId) ? 1 + grade / 2 : 1;
                    Exception addError = null;
                    try
                    {
                        if (!taiwu.AddInventoryItem(context, key, rewardAmount, false,
                                EItemAutoOperationSource.Invalid))
                            addError = new InvalidOperationException("太吾背包拒绝奖励");
                    }
                    catch (Exception e) { addError = e; }
                    bool delivered = InventoryCount(taiwu, key) == inventoryBefore + rewardAmount
                        && TryEnsureInventoryOwner(key, taiwu.GetId());
                    if (!delivered)
                    {
                        var failedGrant = new CommissionRewardGrant
                        {
                            ItemKey = key, InventoryBefore = inventoryBefore, ItemCreated = created,
                        };
                        if (!RollbackCommissionReward(context, taiwu, failedGrant))
                            throw new InvalidOperationException("奖励物品交付失败且未能清理", addError);
                        continue;
                    }
                    return new CommissionRewardGrant
                    {
                        Kind = "item",
                        Name = StripTags(ItemTemplateHelper.GetName(template.ItemType,
                            template.TemplateId)) ?? "物品",
                        Amount = rewardAmount,
                        Grade = grade,
                        ItemKey = key,
                        InventoryBefore = inventoryBefore,
                        ItemCreated = created,
                    };
                }
                catch
                {
                    if (created)
                    {
                        var failedGrant = new CommissionRewardGrant
                        {
                            ItemKey = key, InventoryBefore = inventoryBefore, ItemCreated = true,
                        };
                        if (!RollbackCommissionReward(context, taiwu, failedGrant)) throw;
                    }
                }
            }
            return GrantCommissionResource(context, taiwu, grade);
        }

        private static CommissionRewardGrant GrantCommissionResource(DataContext context,
            Character taiwu, int grade)
        {
            // 九品到一品逐级对应不同的基础数量；同品级保留少量波动，避免奖励完全固定。
            var candidates = new List<sbyte>();
            for (sbyte resourceType = 0; resourceType < 8; resourceType++)
                if (taiwu.GetResource(resourceType) < NativeResourceCap)
                    candidates.Add(resourceType);
            while (candidates.Count > 0)
            {
                int index = context.Random.Next(candidates.Count);
                sbyte resourceType = candidates[index];
                candidates.RemoveAt(index);
                int amount = CommissionRewardPolicy.BaseResourceAmount(grade, resourceType)
                    * context.Random.Next(85, 126) / 100;
                int before = taiwu.GetResource(resourceType);
                amount = Math.Min(amount, NativeResourceCap - before);
                if (amount <= 0) continue;
                taiwu.SpecifyResource(context, resourceType, before + amount);
                if (taiwu.GetResource(resourceType) != before + amount)
                {
                    try { taiwu.SpecifyResource(context, resourceType, before); } catch { }
                    continue;
                }
                return new CommissionRewardGrant
                {
                    Kind = "resource",
                    Name = ResourceDisplayName(resourceType) ?? "资源",
                    Amount = amount,
                    Grade = grade,
                    ResourceType = resourceType,
                    ResourceBefore = before,
                };
            }
            throw new TransferPreconditionException("太吾的全部资源均已达到上限");
        }

        private static bool RollbackCommissionReward(DataContext context, Character taiwu,
            CommissionRewardGrant reward)
        {
            if (reward == null) return true;
            try
            {
                if (reward.ResourceType >= 0)
                {
                    taiwu.SpecifyResource(context, reward.ResourceType, reward.ResourceBefore);
                    return taiwu.GetResource(reward.ResourceType) == reward.ResourceBefore;
                }
                if (!reward.ItemCreated) return true;
                int current = InventoryCount(taiwu, reward.ItemKey);
                if (current < reward.InventoryBefore) return false;
                if (current > reward.InventoryBefore)
                    taiwu.RemoveInventoryItem(context, reward.ItemKey,
                        current - reward.InventoryBefore, deleteItem: false);
                if (InventoryCount(taiwu, reward.ItemKey) != reward.InventoryBefore) return false;
                if (ItemTemplateHelper.IsPureStackable(reward.ItemKey)) return true;
                return TryRemoveCreatedItemEntity(context, reward.ItemKey);
            }
            catch { return false; }
        }

        private static short ClampShort(int v) => (short)(v < short.MinValue ? short.MinValue : (v > short.MaxValue ? short.MaxValue : v));

        // 在 NPC 背包里定位"要赠的那一项",返回真实字典键 + 现有数量。三级匹配,容忍可堆叠资源的 Id/品质态与快照键不一致:
        // ① 精确键(装备等唯一物正好命中) ② 同 类型+品质态+模板 ③ 同 类型+模板。装备走①保唯一性,食材/材料等堆叠物走②③。
        // ===== 全实时:按名实时枚举/解析 NPC 持有(物品会随送/换而变,绝不缓存)=====
        private static string StripTags(string s) => string.IsNullOrEmpty(s) ? s : System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "").Trim();
        private static string NormName(string s)
        {
            return ItemNameMatcher.Normalize(s);
        }
        private static string StripItemQueryNoise(string t)
        {
            return ItemNameMatcher.StripItemQueryNoise(t);
        }
        private static IEnumerable<string> ItemQueryAliases(string q)
        {
            return ItemNameMatcher.QueryAliases(q);
        }
        private static bool NameHit(string cand, string q)
        {
            return ItemNameMatcher.IsHit(cand, q);
        }
        // 一件持有:真实键 + 显示名 + 现有数量(供 query_npc_items 把数量也回给模型,好让 NPC 据实告知玩家)
        private sealed class NpcHolding { public ItemKey Key; public string Name; public int Count; }

        private sealed class TransferThing
        {
            public ItemKey Key;
            public string DisplayName;
            public int Amount;
            public bool IsResource;
            public sbyte ResourceType;
            public bool IsEating;
            public int EatingSlot;
            public short EatingDuration;
            public bool IsEquipped;
            public sbyte EquipSlot;
            public int SourceInventoryBefore;
            public int DestinationInventoryBefore;
        }

        private sealed class TransferReceipt
        {
            public int SourceId;
            public Character Source;
            public Character Destination;
            public TransferThing Thing;
        }

        private enum TransferIntent
        {
            Gift,
            Trade,
            Barter,
            Steal,
            Loot,
            Compensation
        }

        private sealed class TransferPreconditionException : InvalidOperationException
        {
            public TransferPreconditionException(string message) : base(message) { }
        }

        private static int SafeCurrMainAttr(Character c, sbyte attr)
        {
            try { return c != null ? c.GetCurrMainAttribute(attr) : 0; }
            catch { return 0; }
        }

        private sealed class StealCheck
        {
            public int AlertFactor;
            public int Phase1;
            public int Phase2;
            public int Phase3;
            public int ChancePercent;
            public int ChanceBasisPoints;
        }

        /// <summary>
        /// 复刻 1.0.72 Character.GetStealActionPhase 的三段概率，仅用于把权威检定概率回传给前端。
        /// 真正成败仍调用本体方法，避免本 Mod 的随机实现与游戏分叉。全程用 long 计算组合概率，
        /// 高价值/大数量物品即使把 AlertFactor 推到 int.MaxValue 也不会溢出或变成负成功率。
        /// </summary>
        private static StealCheck BuildStealCheck(int thiefId, int victimId, Character thief,
            Character calculationTarget, int alertFactor)
        {
            var selfAttainment = thief.GetCombatSkillAttainments();
            var targetAttainment = calculationTarget.GetCombatSkillAttainments();
            var selfHit = thief.GetHitValues();
            var targetAvoid = calculationTarget.GetAvoidValues();
            var personalities = thief.GetPersonalities();
            bool taiwu = thiefId == DomainManager.Taiwu.GetTaiwuCharId();
            bool thiefRing = false;
            if (taiwu)
            {
                var equipment = thief.GetEquipment();
                for (int i = 8; equipment != null && i <= 10 && i < equipment.Length; i++)
                    if (equipment[i].IsValid() && equipment[i].TemplateId == 298)
                    { thiefRing = true; break; }
            }

            int p1 = NativeStealPhaseRate(selfAttainment[1], targetAttainment[1], alertFactor,
                personalities[4], taiwu, victimId, false, thiefRing);
            int p2 = NativeStealPhaseRate(selfHit[2], targetAvoid[2], alertFactor,
                personalities[0], taiwu, victimId, false, thiefRing);
            int p3 = NativeStealPhaseRate(thief.GetMoveSpeed(), calculationTarget.GetMoveSpeed(), alertFactor,
                personalities[2], taiwu, victimId, true, thiefRing);
            long product = (long)p1 * p2 * p3;
            int basisPoints = (int)System.Math.Min(10000L, (product + 50L) / 100L);
            return new StealCheck
            {
                AlertFactor = alertFactor,
                Phase1 = p1,
                Phase2 = p2,
                Phase3 = p3,
                ChanceBasisPoints = basisPoints,
                ChancePercent = (basisPoints + 50) / 100
            };
        }

        private static int NativeStealPhaseRate(int selfValue, int targetValue, int alertFactor,
            int personality, bool taiwu, int victimId, bool divideByThree, bool thiefRing)
        {
            long target = System.Math.Max(1, targetValue);
            long alert = System.Math.Max(25, alertFactor);
            long rate = (long)selfValue * GlobalConfig.Instance.HarmfulActionPhaseBaseSuccessRate
                / target * 100L / alert;
            if (divideByThree) rate /= 3L;
            rate = rate * (personality + 100L) / 100L;
            if (taiwu)
            {
                int alertness = DomainManager.Character.GetAlertnessValue(victimId);
                int effect = CharacterAlertnessData.GetEffectInteract(CharacterAlertnessData.GetLevel(alertness));
                rate = rate * effect / 100L;
            }
            if (thiefRing) rate += rate / 4L;
            return (int)System.Math.Max(0L, System.Math.Min(100L, rate));
        }

        private static void WriteStealCheck(SerializableModData result, StealCheck check, sbyte phase)
        {
            result.Set("chance", check.ChancePercent);
            result.Set("chance_basis_points", check.ChanceBasisPoints);
            result.Set("alert_factor", check.AlertFactor);
            result.Set("phase1_chance", check.Phase1);
            result.Set("phase2_chance", check.Phase2);
            result.Set("phase3_chance", check.Phase3);
            result.Set("failure_phase", phase == 5 ? 0 : (int)phase);
        }

        private static sbyte ActiveNormalPageDirection(ushort activationState, byte pageId)
        {
            if (pageId < 1 || pageId > 5) return -1;
            int directBit = 5 + (pageId - 1);
            int reverseBit = 10 + (pageId - 1);
            bool direct = (activationState & (1 << directBit)) != 0;
            bool reverse = (activationState & (1 << reverseBit)) != 0;
            if (direct && !reverse) return 0;
            if (reverse && !direct) return 1;
            return -1;
        }

        private static string DirectionText(sbyte dir)
        {
            return dir == 0 ? "正练" : dir == 1 ? "逆练" : "混练";
        }

        private static bool TryResolveKnownCombatSkill(int charId, string raw, out short skillId, out string displayName)
        {
            skillId = -1; displayName = null;
            string q = (raw ?? "").Trim();
            if (q.Length == 0) return false;
            try
            {
                var skills = DomainManager.CombatSkill.GetCharCombatSkills(charId);
                if (skills == null || skills.Count == 0) return false;
                short bestId = -1; string bestName = null; int bestLen = -1;
                foreach (var kv in skills)
                {
                    string name = null;
                    try { name = StripTags(Config.CombatSkill.Instance[kv.Key].Name); } catch { }
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!NameHit(name, q)) continue;
                    int len = name.Length;
                    if (len > bestLen) { bestId = kv.Key; bestName = name; bestLen = len; }
                }
                if (bestId >= 0) { skillId = bestId; displayName = bestName; return true; }
            }
            catch { }
            return false;
        }

        private static bool IsSilverName(string name)
        {
            string q = NormName(name);
            string raw = (name ?? "").Trim().ToLowerInvariant();
            return q == "银" || q == "银钱" || q == "银两" || q == "钱" || q == "文钱" || q == "盘缠" || raw == "silver" || raw == "money";
        }

        private static string ResourceDisplayName(sbyte resourceType)
        {
            if (resourceType == 6) return "银钱";
            try { return StripTags(Config.ResourceType.Instance[resourceType].Name); } catch { return null; }
        }

        private static string ItemDisplayName(ItemKey key)
        {
            if (ItemTemplateHelper.IsMiscResource(key.ItemType, key.TemplateId)) return ResourceDisplayName((sbyte)key.TemplateId) ?? "资源";
            try { return StripTags(ItemTemplateHelper.GetName(key.ItemType, key.TemplateId)); } catch { return null; }
        }

        private static sbyte ParsePoisonType(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "烈毒": case "烈": case "hot": return 0;
                case "郁毒": case "郁": case "gloomy": return 1;
                case "寒毒": case "寒": case "cold": return 2;
                case "赤毒": case "赤": case "red": return 3;
                case "腐毒": case "腐": case "rotten": return 4;
                case "幻毒": case "幻": case "illusory": return 5;
                default: return -1;
            }
        }

        private static string PoisonTypeName(sbyte poisonType)
        {
            switch (poisonType)
            {
                case 0: return "烈毒";
                case 1: return "郁毒";
                case 2: return "寒毒";
                case 3: return "赤毒";
                case 4: return "腐毒";
                case 5: return "幻毒";
                default: return "毒";
            }
        }

        private static string QiDisorderLevelName(sbyte level)
        {
            switch (level)
            {
                case DisorderLevelOfQi.Smooth: return "顺畅";
                case DisorderLevelOfQi.Sluggish: return "滞碍";
                case DisorderLevelOfQi.Blocked: return "逆阻";
                case DisorderLevelOfQi.Disordered: return "紊乱";
                case DisorderLevelOfQi.Cutoff: return "绝断";
                default: return "未知";
            }
        }

        private static string PoisonValuesCsv(PoisonInts poisons)
        {
            var values = new string[6];
            for (int i = 0; i < values.Length; i++)
                values[i] = poisons[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", values);
        }

        private static string PoisonSummary(PoisonInts poisons)
        {
            var values = new List<string>();
            for (sbyte i = 0; i < 6; i++)
                if (poisons[i] > 0) values.Add(PoisonTypeName(i) + poisons[i]);
            return values.Count == 0 ? "无中毒" : string.Join("、", values);
        }

        private static bool PoisonReduced(PoisonInts before, PoisonInts after)
        {
            bool reduced = false;
            for (int i = 0; i < 6; i++)
            {
                if (after[i] > before[i]) return false;
                if (after[i] < before[i]) reduced = true;
            }
            return reduced;
        }

        private static bool TryResolveTransferThing(Character src, string name, int want, out TransferThing thing, out string fail)
        {
            thing = null; fail = null;
            if (src == null) { fail = "角色无效"; return false; }
            string q = ItemNameMatcher.StripCountSuffix((name ?? "").Trim());
            if (q.Length == 0) { fail = "物名为空"; return false; }
            if (want <= 0) want = 1;

            if (IsSilverName(q))
            {
                int haveMoney = src.GetResource((sbyte)6);
                if (haveMoney <= 0) { fail = "银钱不足"; return false; }
                thing = new TransferThing
                {
                    Key = new ItemKey((sbyte)12, (byte)0, (short)6, 0),
                    DisplayName = "银钱",
                    Amount = want < haveMoney ? want : haveMoney,
                    IsResource = true,
                    ResourceType = (sbyte)6
                };
                return true;
            }

            bool found;
            var reqKey = ResolveNpcItemByName(src, q, out found);
            if (!found) { fail = "不在其真实随身清单中"; return false; }

            if (ItemTemplateHelper.IsMiscResource(reqKey.ItemType, reqKey.TemplateId))
            {
                sbyte rt = (sbyte)reqKey.TemplateId;
                int haveRes = src.GetResource(rt);
                if (haveRes <= 0) { fail = "此资源已无"; return false; }
                thing = new TransferThing
                {
                    Key = reqKey,
                    DisplayName = ResourceDisplayName(rt) ?? q,
                    Amount = want < haveRes ? want : haveRes,
                    IsResource = true,
                    ResourceType = rt
                };
                return true;
            }

            ItemKey realKey; int have;
            if (FindOwnedItem(src, reqKey, out realKey, out have) && have > 0)
            {
                thing = new TransferThing
                {
                    Key = realKey,
                    DisplayName = ItemDisplayName(realKey) ?? q,
                    Amount = want < have ? want : have
                };
                return true;
            }

            int eslot = src.GetEatingItems().IndexOf(reqKey);
            if (eslot >= 0)
            {
                // 蛊类有移除事件、逃逸/掉落与即时状态，不能按普通实物跨人物转移。
                if (EatingItems.IsWug(reqKey)) { fail = "蛊虫不能作为普通物品交换"; return false; }
                thing = new TransferThing
                {
                    Key = reqKey,
                    DisplayName = ItemDisplayName(reqKey) ?? q,
                    Amount = 1,
                    IsEating = true,
                    EatingSlot = eslot,
                    EatingDuration = src.GetEatingItems().GetDuration(eslot)
                };
                return true;
            }

            sbyte eqSlot = FindEquippedSlot(src, reqKey);
            if (eqSlot >= 0)
            {
                var wornKey = src.GetEquipment()[eqSlot];
                thing = new TransferThing
                {
                    Key = wornKey,
                    DisplayName = ItemDisplayName(wornKey) ?? q,
                    Amount = 1,
                    IsEquipped = true,
                    EquipSlot = eqSlot
                };
                return true;
            }

            fail = "未能定位到背包/熟食/装备页中的实物";
            return false;
        }

        private static void ValidateTransferThingPreconditions(int srcId, Character src,
            Character dst, TransferThing thing)
        {
            if (thing == null || thing.Amount <= 0)
                throw new TransferPreconditionException("无效转移物");
            if (src == null || dst == null || src.GetId() == dst.GetId()
                || src.GetId() != srcId)
                throw new TransferPreconditionException("无效转移双方");
            if (thing.IsResource)
            {
                if (thing.ResourceType < 0 || thing.ResourceType >= 8)
                    throw new TransferPreconditionException("资源类型无效");
                int srcAmount = src.GetResource(thing.ResourceType);
                int dstAmount = dst.GetResource(thing.ResourceType);
                if (srcAmount < thing.Amount)
                    throw new TransferPreconditionException("来源资源不足");
                if (dstAmount > NativeResourceCap - thing.Amount)
                    throw new TransferPreconditionException("接收方资源已接近上限");
                return;
            }
            if (thing.IsEating)
            {
                ref EatingItems eating = ref src.GetEatingItems();
                if (thing.Amount != 1 || thing.EatingSlot < 0 || thing.EatingSlot >= 9
                    || !eating.Get(thing.EatingSlot).Equals(thing.Key))
                    throw new TransferPreconditionException("服食槽状态已变化");
                if (EatingItems.IsWug(thing.Key))
                    throw new TransferPreconditionException("蛊虫不可转移");
                if (!ItemOwnerMatches(thing.Key, ItemOwnerType.CharacterEatingItem, srcId))
                    throw new TransferPreconditionException("服食物持有人状态异常");
                return;
            }
            if (thing.IsEquipped)
            {
                var equipment = src.GetEquipment();
                if (thing.Amount != 1 || equipment == null || thing.EquipSlot < 0
                    || thing.EquipSlot >= equipment.Length
                    || !equipment[thing.EquipSlot].Equals(thing.Key))
                    throw new TransferPreconditionException("装备状态已变化");
                return;
            }
            if (InventoryCount(src, thing.Key) < thing.Amount)
                throw new TransferPreconditionException("来源物品不足");
            if (!InventoryOwnerMatches(thing.Key, srcId))
                throw new TransferPreconditionException("物品持有人状态与来源背包不一致");
        }

        private static TransferReceipt ApplyTransferThing(DataContext context, int srcId,
            Character src, Character dst, TransferThing thing, TransferIntent intent)
        {
            ValidateTransferThingPreconditions(srcId, src, dst, thing);
            var receipt = new TransferReceipt { SourceId = srcId, Source = src, Destination = dst, Thing = thing };
            if (thing.IsResource)
            {
                TransferResourceChecked(context, src, dst, thing.ResourceType, thing.Amount);
                return receipt;
            }
            if (thing.IsEating)
            {
                TransferEatingItemToInventory(context, srcId, src, dst, thing, intent);
                return receipt;
            }
            if (thing.IsEquipped)
            {
                var equipment = src.GetEquipment();
                if (thing.EquipSlot < 0 || thing.EquipSlot >= equipment.Length || !equipment[thing.EquipSlot].Equals(thing.Key))
                    throw new TransferPreconditionException("装备状态已变化");
                thing.SourceInventoryBefore = InventoryCount(src, thing.Key);
                thing.DestinationInventoryBefore = InventoryCount(dst, thing.Key);
                Exception unequipError = null;
                try
                {
                    DomainManager.Character.ChangeEquipment(context, srcId, thing.EquipSlot, (sbyte)(-1), default(ItemKey));
                }
                catch (Exception e) { unequipError = e; }

                var equipmentAfter = src.GetEquipment();
                bool slotCleared = equipmentAfter != null
                    && thing.EquipSlot >= 0 && thing.EquipSlot < equipmentAfter.Length
                    && !equipmentAfter[thing.EquipSlot].IsValid();
                bool inventoryReceived = InventoryCount(src, thing.Key)
                    == thing.SourceInventoryBefore + 1;
                if (!slotCleared || !inventoryReceived)
                {
                    if (TryRestoreEquippedTransferOrigin(context, srcId, src, dst, thing))
                        throw new TransferPreconditionException("本体卸装未完整落地，已恢复原装备与库存");
                    throw new InvalidOperationException("本体卸装部分落地且补偿恢复失败", unequipError);
                }

                try
                {
                    // 即使 ChangeEquipment 在写完槽位与背包后由通知尾调用抛错，
                    // 权威后验满足时也应继续同一事务，不能把已卸下的装备遗留在来源背包。
                    TransferInventoryChecked(context, src, dst, thing.Key, 1, intent);
                    return receipt;
                }
                catch (Exception transferError)
                {
                    // Inventory helper 的普通异常表示物品/持有人/赠礼登记仍可能部分落地。
                    // 未证明库存已回到“卸装后”状态前，不得继续改装备槽扩大未知写入。
                    if (!(transferError is TransferPreconditionException))
                        throw new InvalidOperationException(
                            "装备转移状态无法确认，已停止追加装备补偿", transferError);
                    if (TryRestoreEquippedTransferOrigin(context, srcId, src, dst,
                            thing))
                        throw new TransferPreconditionException(
                            "装备转移未完成，已恢复原装备与双方库存");
                    throw new InvalidOperationException("装备转移失败且补偿恢复未完整落地",
                        transferError);
                }
            }
            TransferInventoryChecked(context, src, dst, thing.Key, thing.Amount, intent);
            return receipt;
        }

        private const int NativeResourceCap = 999999999;

        private static int CalculateTradePayment(int requestedAmount, int requestedPrice,
            int deliveredAmount, int buyerMoney)
        {
            if (requestedAmount <= 0 || deliveredAmount <= 0
                || deliveredAmount > requestedAmount || requestedPrice < 0)
                throw new TransferPreconditionException("交易数量或价格无效");
            // price 是整单总价。部分交付必须向上取整，否则“3 件共 1 钱、只交 1 件”
            // 会被整数除法截成 0 钱，形成免费货物。
            int pay = deliveredAmount < requestedAmount && requestedPrice > 0
                ? (int)(((long)requestedPrice * deliveredAmount + requestedAmount - 1L)
                    / requestedAmount)
                : requestedPrice;
            if (pay < 0 || pay > buyerMoney)
                throw new TransferPreconditionException("太吾银钱不足，无法完整付款");
            return pay;
        }

        private static bool TryRestoreResourcePair(DataContext context, Character first,
            Character second, sbyte resourceType, int firstExpected, int secondExpected)
        {
            try
            {
                first.SpecifyResource(context, resourceType, firstExpected);
                second.SpecifyResource(context, resourceType, secondExpected);
                return first.GetResource(resourceType) == firstExpected
                    && second.GetResource(resourceType) == secondExpected;
            }
            catch { return false; }
        }

        private static void PersistMerchantData(DataContext context, int ownerType, int ownerId,
            GameData.Domains.Merchant.MerchantData data)
        {
            if (ownerType == 1) DomainManager.Merchant.SetCaravanData(ownerId, data, context);
            else DomainManager.Merchant.SetMerchantData(ownerId, data, context);
        }

        private static bool TryRestoreMerchantGoodsTrade(DataContext context, Character buyer,
            GameData.Domains.Merchant.MerchantData data, int goodsIndex, ItemKey key,
            int goodsExpected, int buyerExpected, int ownerType, int ownerId)
        {
            try
            {
                if (buyer == null || data == null || goodsIndex < 0 || goodsIndex > 6
                    || goodsExpected < 0 || buyerExpected < 0) return false;
                var goods = data.GetGoodsList(goodsIndex);
                if (goods == null || goods.Items == null) return false;

                int buyerNow = InventoryCount(buyer, key);
                if (buyerNow < buyerExpected) return false;
                if (buyerNow > buyerExpected)
                    buyer.RemoveInventoryItem(context, key, buyerNow - buyerExpected,
                        deleteItem: false);
                if (InventoryCount(buyer, key) != buyerExpected) return false;

                int goodsNow = 0;
                goods.Items.TryGetValue(key, out goodsNow);
                if (goodsNow > goodsExpected)
                    goods.OfflineRemove(key, goodsNow - goodsExpected);
                else if (goodsNow < goodsExpected)
                    goods.OfflineAdd(key, goodsExpected - goodsNow);

                ItemOwnerType expectedOwner =
                    ownerType == 1 ? ItemOwnerType.Caravan : ItemOwnerType.Merchant;
                if (!TryEnsureItemOwner(key, expectedOwner, ownerId)) return false;
                PersistMerchantData(context, ownerType, ownerId, data);
                goodsNow = 0;
                goods.Items.TryGetValue(key, out goodsNow);
                return goodsNow == goodsExpected
                    && InventoryCount(buyer, key) == buyerExpected
                    && ItemOwnerMatches(key, expectedOwner, ownerId);
            }
            catch { return false; }
        }

        private static void TransferResourceChecked(DataContext context, Character src, Character dst,
            sbyte resourceType, int amount)
        {
            if (src == null || dst == null || src.GetId() == dst.GetId()
                || resourceType < 0 || resourceType >= 8 || amount <= 0)
                throw new TransferPreconditionException("资源转移参数无效");
            int srcBefore = src.GetResource(resourceType);
            int dstBefore = dst.GetResource(resourceType);
            if (srcBefore < amount) throw new TransferPreconditionException("资源已不足");
            if (dstBefore > NativeResourceCap - amount)
                throw new TransferPreconditionException("接收方资源已接近上限，无法完整收取");

            Exception transferError = null;
            try { DomainManager.Character.TransferResource(context, src, dst, resourceType, amount); }
            catch (Exception e) { transferError = e; }

            int srcAfter;
            int dstAfter;
            try
            {
                srcAfter = src.GetResource(resourceType);
                dstAfter = dst.GetResource(resourceType);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("资源转移后无法读取权威余额", transferError ?? e);
            }

            // 最新本体给太吾增加资源后还会触发新手引导。引导尾部可抛异常，但此时双方余额
            // 已经完整落地；必须以权威后验余额为准，不能把一次成功赠银误报 UNKNOWN，更不能
            // 让交换器错误回滚另一边后留下单边转移。
            if (srcAfter == srcBefore - amount && dstAfter == dstBefore + amount) return;
            if (srcAfter == srcBefore && dstAfter == dstBefore)
                throw new TransferPreconditionException("本体在写入资源前中止，余额未变化");

            // CharacterDomain.TransferResource 是“先扣来源、再加目标”的两步写入，没有跨角色
            // 事务。若任一步只落了一半，用不会触发引导尾调用的 SpecifyResource 精确补偿，
            // 并且只有验证双方都回到原值后才把它归类为可安全重试的零写入失败。
            try
            {
                dst.SpecifyResource(context, resourceType, dstBefore);
                src.SpecifyResource(context, resourceType, srcBefore);
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException("资源转移部分落地且补偿回滚失败", rollbackError);
            }
            if (src.GetResource(resourceType) != srcBefore || dst.GetResource(resourceType) != dstBefore)
                throw new InvalidOperationException("资源转移部分落地且补偿回滚未完整恢复");
            throw new TransferPreconditionException("资源转移未完整落地，已恢复双方原余额");
        }

        private static bool TryGetTaiwuGiftItemAmount(int targetCharId, ItemKey key,
            out int amount)
        {
            amount = 0;
            try
            {
                IReadOnlyDictionary<ItemKey, int> gifts =
                    DomainManager.Extra.GetTaiwuGiftItems(targetCharId);
                if (gifts != null) gifts.TryGetValue(key, out amount);
                return true;
            }
            catch { return false; }
        }

        private static bool TryRestoreTaiwuGiftItemAmount(DataContext context, int targetCharId,
            ItemKey key, int expectedAmount)
        {
            try
            {
                if (!TryGetTaiwuGiftItemAmount(targetCharId, key, out int current))
                    return false;
                if (current == expectedAmount) return true;
                // 1.0.72 exposes an authoritative single-entry setter. Native transfer creates
                // the target ledger before moving an item, so this also safely removes a newly
                // created zero-before entry when a non-gift transfer or failed gift is restored.
                DomainManager.Extra.SetTaiwuGiftItemAmount(context, targetCharId, key,
                    expectedAmount);
                return TryGetTaiwuGiftItemAmount(targetCharId, key, out int restored)
                    && restored == expectedAmount;
            }
            catch { return false; }
        }

        private static void TransferInventoryChecked(DataContext context, Character src,
            Character dst, ItemKey key, int amount, TransferIntent intent)
        {
            if (src == null || dst == null || src.GetId() == dst.GetId() || amount <= 0)
                throw new TransferPreconditionException("物品转移参数无效");
            if (InventoryCount(src, key) < amount) throw new TransferPreconditionException("物品已不足");
            int srcBefore = InventoryCount(src, key);
            int dstBefore = InventoryCount(dst, key);
            if (!InventoryOwnerMatches(key, src.GetId()))
                throw new TransferPreconditionException("物品持有人状态与来源背包不一致");
            // 1.0.72 的赠送/偷窃/索取/抢夺/诈骗动作均显式 ignoreLocked:true；锁定只保护
            // 太吾界面的非意图操作，已经确认的动作仍可转移。必须走本体转移链，让唯一物品
            // 先从来源移除、再改写持有人。Invalid 禁止太吾自动整理，目标入包不会被重定向。
            bool taiwuSource = src.GetId() == DomainManager.Taiwu.GetTaiwuCharId();
            int giftAmountBefore = 0;
            if (taiwuSource
                && !TryGetTaiwuGiftItemAmount(dst.GetId(), key, out giftAmountBefore))
                throw new TransferPreconditionException(
                    "无法读取太吾赠礼登记，已在物品写入前中止");
            Exception transferError = null;
            try
            {
                DomainManager.Character.TransferInventoryItem(context, src, dst, key, amount,
                    EItemAutoOperationSource.Invalid, ignoreLocked: true);
            }
            catch (Exception e)
            {
                transferError = e;
            }

            int srcAfter = InventoryCount(src, key);
            int dstAfter = InventoryCount(dst, key);
            bool fullyTransferred = srcAfter == srcBefore - amount
                && dstAfter == dstBefore + amount;
            bool keepGiftLedger = taiwuSource && intent == TransferIntent.Gift
                && fullyTransferred;
            if (taiwuSource && !keepGiftLedger
                && !TryRestoreTaiwuGiftItemAmount(context, dst.GetId(), key,
                    giftAmountBefore))
                throw new InvalidOperationException(
                    "非赠礼转移或失败赠礼未能恢复太吾赠礼登记", transferError);

            if (transferError != null)
            {
                if (srcAfter == srcBefore - amount && dstAfter == dstBefore + amount)
                {
                    if (TryEnsureInventoryOwner(key, dst.GetId()))
                        return; // native completed both writes before a later notification threw
                    if (TryRestoreInventoryPair(context, src, dst, key, srcBefore,
                            dstBefore, restoreSourceInventoryOwner: true))
                    {
                        if (taiwuSource && intent == TransferIntent.Gift
                            && !TryRestoreTaiwuGiftItemAmount(context, dst.GetId(),
                                key, giftAmountBefore))
                            throw new InvalidOperationException(
                                "物品已恢复，但失败赠礼的太吾赠礼登记未能恢复",
                                transferError);
                        throw new TransferPreconditionException(
                            "物品持有人后验失败，已恢复双方原库存:"
                            + transferError.GetType().Name);
                    }
                    throw new InvalidOperationException(
                        "物品数量已转移但唯一物品持有人及库存补偿未能确认",
                        transferError);
                }
                if (srcAfter == srcBefore && dstAfter == dstBefore)
                    throw new TransferPreconditionException(
                        "物品转移未发生:" + transferError.GetType().Name);
                if (TryRestoreInventoryPair(context, src, dst, key, srcBefore, dstBefore,
                        restoreSourceInventoryOwner: true))
                    throw new TransferPreconditionException(
                        "物品转移部分写入后已完整恢复:" + transferError.GetType().Name);
                throw new InvalidOperationException("物品转移部分落地且补偿恢复失败", transferError);
            }
            if (srcAfter == srcBefore - amount
                && dstAfter == dstBefore + amount
                && TryEnsureInventoryOwner(key, dst.GetId()))
                return;
            if (TryRestoreInventoryPair(context, src, dst, key, srcBefore, dstBefore,
                    restoreSourceInventoryOwner: true))
            {
                // A real Taiwu gift kept its native ledger while counts looked complete.
                // If the unique-owner postcondition then failed but inventory compensation
                // succeeded, the ledger must join that rollback before this is a safe retry.
                if (taiwuSource && intent == TransferIntent.Gift
                    && fullyTransferred
                    && !TryRestoreTaiwuGiftItemAmount(context, dst.GetId(), key,
                        giftAmountBefore))
                    throw new InvalidOperationException(
                        "物品已恢复，但失败赠礼的太吾赠礼登记未能恢复");
                throw new TransferPreconditionException("物品转移未完整落地，已恢复双方原库存");
            }
            throw new InvalidOperationException("物品转移未完整落地且补偿恢复失败");
        }

        private static bool TryRestoreInventoryPair(DataContext context, Character src, Character dst,
            ItemKey key, int srcExpected, int dstExpected, bool restoreSourceInventoryOwner = false)
        {
            try
            {
                int srcNow = InventoryCount(src, key);
                int dstNow = InventoryCount(dst, key);
                if (dstNow > dstExpected)
                    dst.RemoveInventoryItem(context, key, dstNow - dstExpected, deleteItem: false);
                if (srcNow > srcExpected)
                    src.RemoveInventoryItem(context, key, srcNow - srcExpected, deleteItem: false);
                srcNow = InventoryCount(src, key);
                dstNow = InventoryCount(dst, key);
                if (srcNow < srcExpected)
                    src.AddInventoryItem(context, key, srcExpected - srcNow, offLine: false,
                        EItemAutoOperationSource.Invalid);
                if (dstNow < dstExpected)
                    dst.AddInventoryItem(context, key, dstExpected - dstNow, offLine: false,
                        EItemAutoOperationSource.Invalid);
                if (InventoryCount(src, key) != srcExpected
                    || InventoryCount(dst, key) != dstExpected)
                    return false;
                return !restoreSourceInventoryOwner
                    || TryEnsureInventoryOwner(key, src.GetId());
            }
            catch { return false; }
        }

        private static bool InventoryOwnerMatches(ItemKey key, int characterId)
            => ItemOwnerMatches(key, ItemOwnerType.CharacterInventory, characterId);

        private static bool ItemOwnerMatches(ItemKey key, ItemOwnerType ownerType, int ownerId)
        {
            try
            {
                ItemBase item = DomainManager.Item.GetBaseItem(key);
                return ItemDomain.IsPureStackable(item)
                    || (item.Owner.OwnerType == ownerType && item.Owner.OwnerId == ownerId);
            }
            catch { return false; }
        }

        private static bool TryEnsureInventoryOwner(ItemKey key, int characterId)
        {
            return TryEnsureItemOwner(key, ItemOwnerType.CharacterInventory, characterId);
        }

        private static bool TryEnsureItemOwner(ItemKey key, ItemOwnerType ownerType, int ownerId)
        {
            if (ItemOwnerMatches(key, ownerType, ownerId)) return true;
            try
            {
                ItemBase item = DomainManager.Item.GetBaseItem(key);
                if (ItemDomain.IsPureStackable(item)) return true;
                DomainManager.Item.SetOwner(key, ownerType, ownerId);
                return ItemOwnerMatches(key, ownerType, ownerId);
            }
            catch { return false; }
        }

        private static int InventoryCount(Character character, ItemKey key)
        {
            if (character == null || character.GetInventory() == null || character.GetInventory().Items == null) return 0;
            return character.GetInventory().Items.TryGetValue(key, out int count) ? count : 0;
        }

        private static bool TryInventoryCount(Character character, ItemKey key, out int count)
        {
            count = 0;
            try
            {
                if (character == null || character.GetInventory() == null
                    || character.GetInventory().Items == null) return false;
                count = character.GetInventory().Items.TryGetValue(key, out int found) ? found : 0;
                return true;
            }
            catch { return false; }
        }

        private static bool TryItemExists(ItemKey key, out bool exists)
        {
            exists = false;
            try
            {
                exists = DomainManager.Item.ItemExists(key);
                return true;
            }
            catch { return false; }
        }

        private static bool TryRemoveCreatedItemEntity(DataContext context, ItemKey key)
        {
            try
            {
                if (DomainManager.Item.ItemExists(key))
                    DomainManager.Item.RemoveItem(context, key);
                return !DomainManager.Item.ItemExists(key);
            }
            catch { return false; }
        }

        private static bool TryDiscardCreatedInventoryItem(DataContext context,
            Character recipient, ItemKey key, int inventoryExpected)
        {
            try
            {
                // CreateSkillBook 返回全新的唯一键；交付前的权威数量必须是 0。
                // 若不是 0，绝不能在这里删除实体，以免制造指向不存在实体的库存项。
                if (inventoryExpected != 0) return false;
                if (!TryInventoryCount(recipient, key, out int inventoryNow)
                    || inventoryNow < inventoryExpected)
                    return false;
                if (inventoryNow > inventoryExpected)
                {
                    try
                    {
                        recipient.RemoveInventoryItem(context, key,
                            inventoryNow - inventoryExpected, deleteItem: false);
                    }
                    catch { }
                }
                if (!TryInventoryCount(recipient, key, out inventoryNow)
                    || inventoryNow != inventoryExpected)
                    return false;
                return TryRemoveCreatedItemEntity(context, key)
                    && TryInventoryCount(recipient, key, out inventoryNow)
                    && inventoryNow == inventoryExpected;
            }
            catch { return false; }
        }

        private static bool TryRestorePoisonValue(DataContext context, Character target,
            sbyte poisonType, int expectedCurrent, int restoreValue)
        {
            try
            {
                ref PoisonInts poisoned = ref target.GetPoisoned();
                if (poisoned[poisonType] != expectedCurrent) return false;
                poisoned[poisonType] = restoreValue;
                target.SetPoisoned(ref poisoned, context);
                return target.GetPoisoned()[poisonType] == restoreValue;
            }
            catch { return false; }
        }

        private static void TransferEatingItemToInventory(DataContext context, int srcId,
            Character src, Character dst, TransferThing thing, TransferIntent intent)
        {
            ref EatingItems live = ref src.GetEatingItems();
            if (thing.EatingSlot < 0 || thing.EatingSlot >= 9 || !live.Get(thing.EatingSlot).Equals(thing.Key))
                throw new TransferPreconditionException("服食槽状态已变化");
            if (EatingItems.IsWug(thing.Key)) throw new TransferPreconditionException("蛊虫不可转移");

            var updated = live;
            short duration = updated.GetDuration(thing.EatingSlot);
            thing.EatingDuration = duration;
            thing.SourceInventoryBefore = InventoryCount(src, thing.Key);
            thing.DestinationInventoryBefore = InventoryCount(dst, thing.Key);
            bool recordTaiwuGift = intent == TransferIntent.Gift
                && srcId == DomainManager.Taiwu.GetTaiwuCharId();
            int giftAmountBefore = 0;
            if (recordTaiwuGift
                && !TryGetTaiwuGiftItemAmount(dst.GetId(), thing.Key,
                    out giftAmountBefore))
                throw new TransferPreconditionException(
                    "无法读取太吾赠礼登记，已在熟食写入前中止");
            updated.Clear(thing.EatingSlot);
            bool ownerReleaseAttempted = false;
            try
            {
                src.SetEatingItems(ref updated, context);
                ownerReleaseAttempted = true;
                DomainManager.Item.RemoveOwner(thing.Key, ItemOwnerType.CharacterEatingItem, srcId);
                if (!dst.AddInventoryItem(context, thing.Key, 1, offLine: false, source: EItemAutoOperationSource.Invalid))
                    throw new InvalidOperationException("目标背包拒绝物品");
                ref EatingItems after = ref src.GetEatingItems();
                if (EatingItems.IsValid(after.Get(thing.EatingSlot))
                    || InventoryCount(dst, thing.Key) != thing.DestinationInventoryBefore + 1)
                    throw new InvalidOperationException("熟食转移后验未满足");
                if (recordTaiwuGift)
                {
                    DomainManager.Extra.RecordTaiwuGiftItem(context, dst.GetId(),
                        thing.Key, 1);
                    if (!TryGetTaiwuGiftItemAmount(dst.GetId(), thing.Key,
                            out int giftAmountAfter)
                        || giftAmountAfter != giftAmountBefore + 1)
                        throw new InvalidOperationException("熟食赠礼登记未完整落地");
                }
            }
            catch (Exception transferError)
            {
                bool giftRestored = !recordTaiwuGift
                    || TryRestoreTaiwuGiftItemAmount(context, dst.GetId(), thing.Key,
                        giftAmountBefore);
                if (giftRestored && TryRestoreEatingOrigin(context, srcId, src, dst,
                        thing, ownerReleaseAttempted))
                    throw new TransferPreconditionException("熟食转移未完成，已恢复原槽位与双方库存");
                throw new InvalidOperationException("熟食转移部分落地且补偿恢复失败",
                    transferError);
            }
        }

        private static void RestoreEatingOrigin(DataContext context, int srcId, Character src, Character currentHolder, TransferThing thing)
        {
            if (!TryRestoreEatingOrigin(context, srcId, src, currentHolder, thing,
                    ownerReleaseAttempted: true))
                throw new InvalidOperationException("回滚熟食未完整恢复原槽位");
        }

        private static bool TryRestoreEatingOrigin(DataContext context, int srcId, Character src,
            Character currentHolder, TransferThing thing, bool ownerReleaseAttempted)
        {
            try
            {
                int holderNow = InventoryCount(currentHolder, thing.Key);
                if (holderNow < thing.DestinationInventoryBefore) return false;
                if (holderNow > thing.DestinationInventoryBefore)
                {
                    currentHolder.RemoveInventoryItem(context, thing.Key,
                        holderNow - thing.DestinationInventoryBefore, deleteItem: false);
                    if (InventoryCount(currentHolder, thing.Key)
                        != thing.DestinationInventoryBefore) return false;
                }

                if (InventoryCount(src, thing.Key) != thing.SourceInventoryBefore)
                {
                    if (!TryRestoreInventoryPair(context, src, currentHolder, thing.Key,
                            thing.SourceInventoryBefore, thing.DestinationInventoryBefore))
                        return false;
                }

                ref EatingItems live = ref src.GetEatingItems();
                var restored = live;
                ItemKey current = restored.Get(thing.EatingSlot);
                if (EatingItems.IsValid(current) && !current.Equals(thing.Key)) return false;
                if (!current.Equals(thing.Key)
                    || restored.GetDuration(thing.EatingSlot) != thing.EatingDuration)
                {
                    restored.Set(thing.EatingSlot, thing.Key, thing.EatingDuration);
                    src.SetEatingItems(ref restored, context);
                }
                if (ownerReleaseAttempted)
                    DomainManager.Item.SetOwner(thing.Key, ItemOwnerType.CharacterEatingItem, srcId);
                ref EatingItems verified = ref src.GetEatingItems();
                return verified.Get(thing.EatingSlot).Equals(thing.Key)
                    && verified.GetDuration(thing.EatingSlot) == thing.EatingDuration
                    && InventoryCount(src, thing.Key) == thing.SourceInventoryBefore
                    && InventoryCount(currentHolder, thing.Key)
                        == thing.DestinationInventoryBefore;
            }
            catch { return false; }
        }

        private static void RollbackTransferThing(DataContext context, TransferReceipt receipt)
        {
            if (receipt == null || receipt.Thing == null)
                throw new InvalidOperationException("交换回滚凭据无效");
            var thing = receipt.Thing;
            if (thing.IsResource)
            {
                TransferResourceChecked(context, receipt.Destination, receipt.Source, thing.ResourceType, thing.Amount);
                return;
            }
            if (thing.IsEating)
            {
                RestoreEatingOrigin(context, receipt.SourceId, receipt.Source, receipt.Destination, thing);
                return;
            }
            if (thing.IsEquipped)
            {
                if (!TryRestoreEquippedTransferOrigin(context, receipt.SourceId,
                        receipt.Source, receipt.Destination, thing))
                    throw new InvalidOperationException("交换回滚未能恢复原装备与双方库存");
                return;
            }
            TransferInventoryChecked(context, receipt.Destination, receipt.Source, thing.Key,
                thing.Amount, TransferIntent.Compensation);
        }

        private static bool TryRestoreEquippedTransferOrigin(DataContext context, int sourceId,
            Character source, Character destination, TransferThing thing)
        {
            try
            {
                if (source == null || destination == null || thing == null || !thing.IsEquipped)
                    return false;
                var equipment = source.GetEquipment();
                if (equipment == null || thing.EquipSlot < 0 || thing.EquipSlot >= equipment.Length)
                    return false;
                ItemKey current = equipment[thing.EquipSlot];
                if (current.Equals(thing.Key))
                    return TryRestoreInventoryPair(context, source, destination, thing.Key,
                        thing.SourceInventoryBefore, thing.DestinationInventoryBefore);
                if (current.IsValid()) return false;
                if (!TryRestoreInventoryPair(context, source, destination, thing.Key,
                        thing.SourceInventoryBefore + 1, thing.DestinationInventoryBefore))
                    return false;
                RestoreEquippedOrigin(context, sourceId, source, thing);
                return source.GetEquipment()[thing.EquipSlot].Equals(thing.Key)
                    && InventoryCount(source, thing.Key) == thing.SourceInventoryBefore
                    && InventoryCount(destination, thing.Key)
                        == thing.DestinationInventoryBefore;
            }
            catch { return false; }
        }

        private static void RestoreEquippedOrigin(DataContext context, int sourceId, Character source, TransferThing thing)
        {
            if (source == null || thing == null || !thing.IsEquipped) throw new InvalidOperationException("装备回滚凭据无效");
            var equipment = source.GetEquipment();
            if (equipment == null || thing.EquipSlot < 0 || thing.EquipSlot >= equipment.Length)
                throw new InvalidOperationException("原装备槽无效");
            if (equipment[thing.EquipSlot].IsValid()) throw new InvalidOperationException("原装备槽已被占用");
            if (!thing.Key.IsValid() || !DomainManager.Item.ItemExists(thing.Key) || DomainManager.Item.TryGetBaseItem(thing.Key) == null)
                throw new InvalidOperationException("回滚装备实体已不存在");
            if (InventoryCount(source, thing.Key) < 1) throw new InvalidOperationException("回滚装备不在原主背包");
            if (!EquipmentSlotHelper.IsItemMeetSlot(thing.EquipSlot, thing.Key)) throw new InvalidOperationException("回滚装备与原槽不匹配");
            DomainManager.Character.ChangeEquipment(context, sourceId, (sbyte)(-1), thing.EquipSlot, thing.Key);
            if (!source.GetEquipment()[thing.EquipSlot].Equals(thing.Key)) throw new InvalidOperationException("装备回滚未完整落地");
        }

        private static bool HoldingHit(NpcHolding h, string query)
        {
            if (h == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            return NameHit(h.Name, query) || ItemCategoryHit(h.Key, query);
        }

        private static bool ItemCategoryHit(ItemKey key, string query)
        {
            string q = ItemNameMatcher.StripItemQueryNoise(ItemNameMatcher.Normalize(query));
            if (string.IsNullOrEmpty(q)) return false;
            sbyte it = key.ItemType; short tpl = key.TemplateId;
            bool isResource = false; try { isResource = ItemTemplateHelper.IsMiscResource(it, tpl); } catch { }
            sbyte resType = isResource ? (sbyte)tpl : (sbyte)(-1);
            bool isEquip = it >= 0 && it <= 4;
            bool isFood = it == 7 || it == 9 || (isResource && resType == 0);
            bool isMedicine = it == 8 || (isResource && resType == 5);
            bool isPoison = false; try { isPoison = ItemTemplateHelper.GetMedicineItemPoisonType(it, tpl) >= 0; } catch { }
            bool isMaterial = it == 5 || (isResource && resType >= 1 && resType <= 5);
            bool isTool = it == 6;
            bool isBook = it == 10;
            bool isOther = it == 11 || (it == 12 && !isResource);

            if (q.Contains("资源")) return isResource;
            if (q.Contains("食物") || q.Contains("食材") || q.Contains("吃食") || q.Contains("酒") || q.Contains("茶")) return isFood;
            if (q.Contains("药毒")) return isMedicine || isPoison;
            if (q.Contains("毒药") || q == "毒") return isPoison;
            if (q.Contains("药")) return isMedicine;
            if (q.Contains("装备") || q.Contains("武具") || q.Contains("兵器") || q.Contains("衣甲") || q.Contains("佩饰") || q.Contains("衣着")) return isEquip;
            if (q.Contains("书籍") || q == "书" || q.Contains("秘籍") || q.Contains("书本")) return isBook;
            if (q.Contains("工具")) return isTool;
            if (q.Contains("材料") || q.Contains("木料") || q.Contains("金石") || q.Contains("玉石") || q.Contains("布料") || q.Contains("药材") || q.Contains("矿")) return isMaterial;
            if (q.Contains("其他") || q.Contains("杂物") || q.Contains("虫")) return isOther;
            return false;
        }
        // 实时枚举 NPC 全部可赠/可换持有:背包成品 + 资源(食材0..药材5)+ 随身熟食 + 佩带装备。按名去重,同名(多来源)累加数量。
        private static void EnumerateNpcHoldings(Character npc, List<NpcHolding> outList)
        {
            if (npc == null) return;
            var byName = new Dictionary<string, NpcHolding>();
            Action<ItemKey, int, string> addCore = (k, cnt, nmIn) =>
            {
                if (cnt <= 0) return;
                string nm = nmIn;
                if (nm == null) { try { nm = ItemTemplateHelper.GetName(k.ItemType, k.TemplateId); } catch { return; } }
                nm = StripTags(nm);
                if (string.IsNullOrWhiteSpace(nm)) return;
                NpcHolding ex;
                if (byName.TryGetValue(nm, out ex)) { ex.Count += cnt; return; }   // 同名累加数量,保留首个真实键
                var h = new NpcHolding { Key = k, Name = nm, Count = cnt };
                byName[nm] = h; outList.Add(h);
            };
            Action<ItemKey, int> add = (k, cnt) => addCore(k, cnt, null);
            try { var inv = npc.GetInventory(); if (inv != null && inv.Items != null) foreach (var kv in inv.Items) if (kv.Value > 0) add(kv.Key, kv.Value); } catch { }
            // 资源(食材/木料/金石/玉石/布料/药材/银钱)用规范名;威望槽不参与赠予/交换。
            for (sbyte rt = 0; rt <= 6; rt++) { try { int amt = npc.GetResource(rt); if (amt > 0) { string rn = rt == 6 ? "银钱" : null; try { if (rn == null) rn = Config.ResourceType.Instance[rt].Name; } catch { } addCore(new ItemKey((sbyte)12, (byte)0, (short)rt, 0), amt, rn); } } catch { } }
            try { var eat = npc.GetEatingItems(); for (int i = 0; i < 9; i++) { var k = eat.GetItem(i); if (EatingItems.IsValid(k)) add(k, 1); } } catch { }
            try { var eq = npc.GetEquipment(); if (eq != null) for (int i = 0; i < eq.Length; i++) { var k = eq[i]; if (k.IsValid()) add(k, 1); } } catch { }
        }
        // 角色拥有的奇书(LegendaryBook,单独域,不在背包 Items 里)名字列表;无则空。供物品查询补全。
        private static List<string> OwnedLegendaryBookNames(int charId)
        {
            var outNames = new List<string>();
            try
            {
                var types = DomainManager.LegendaryBook.GetCharOwnedBookTypes(charId);
                if (types != null)
                    foreach (var bt in types)
                    {
                        string bn = null;
                        try { bn = StripTags(Config.Misc.Instance[240 + bt].Name); } catch { }
                        if (!string.IsNullOrWhiteSpace(bn)) outNames.Add(bn);
                    }
            }
            catch { }
            return outNames;
        }

        // 在 NPC 实时持有里按名解析出真实 ItemKey(精确名优先,再模糊)。找不到 found=false。
        private static ItemKey ResolveNpcItemByName(Character npc, string name, out bool found)
        {
            found = false; var r = default(ItemKey);
            string q = (name ?? "").Trim();
            // 容错:模型可能把 query_npc_items 的「上等蜀锦×3」整串当物名填进来 → 剥掉尾部「×3/x3」数量后缀再匹配
            q = System.Text.RegularExpressions.Regex.Replace(q, @"[×xX]\s*\d+\s*$", "").Trim();
            if (npc == null || q.Length == 0) return r;
            var hold = new List<NpcHolding>();
            EnumerateNpcHoldings(npc, hold);
            foreach (var h in hold) if (h.Name == q) { found = true; return h.Key; }
            // 模糊命中取「最长/最具体」者,避免短名(铁剑)吞掉长名(玄铁剑)、按字典序撞错
            NpcHolding best = null;
            foreach (var h in hold) if (NameHit(h.Name, q) && (best == null || (h.Name ?? "").Length > (best.Name ?? "").Length)) best = h;
            if (best != null) { found = true; return best.Key; }
            return r;
        }

        // build 24769549 / game 1.0.72 的人物界面 CommonUtils.CanItemEat 权威边界：
        // NPC 可直接使用食物、药毒、茶酒、天劫符箓，以及确有内力/五行效果的杂物。
        // 高品药材只在“太吾装备制药技艺 54”时开放，NPC 明确不在范围内；装备和书籍
        // 分别由 change_equipment/read_book 处理，不能被泛化成 use_item。
        private static bool TryGetNpcUsableItem(ItemKey key, out int consumeAmount,
            out string kind, out string reason)
        {
            consumeAmount = 1;
            kind = null;
            reason = null;
            if (!key.IsValid()) { reason = "物品键无效"; return false; }
            try
            {
                if (ItemTemplateHelper.IsTianJieFuLu(key.ItemType, key.TemplateId))
                {
                    consumeAmount = ItemTemplateHelper.GetTianJieFuLuCountUnit();
                    kind = "天劫符箓";
                    return consumeAmount > 0;
                }
                switch (key.ItemType)
                {
                    case 7: kind = "食物"; return true;
                    case 8: kind = "药物"; return true;
                    case 9: kind = "茶酒"; return true;
                    case 12:
                    {
                        var misc = Config.Misc.Instance[key.TemplateId];
                        int element = misc.FiveElementTransfer.First;
                        bool canUse = misc.Neili > 0 || misc.MaxNeili > 0
                            || element >= 0 && element < 5
                                && misc.FiveElementTransfer.Second > 0;
                        if (canUse) { kind = "内力物品"; return true; }
                        reason = "普通杂物没有可直接使用的本体效果";
                        return false;
                    }
                    case 0: case 1: case 2: case 3: case 4:
                        reason = "装备须使用更换装备工具"; return false;
                    case 10:
                        reason = "秘籍须使用阅读工具"; return false;
                    case 5:
                        reason = "高品药材的直接服用是太吾专属职业能力，NPC 不能使用"; return false;
                    case 6:
                        reason = "制作用工具不能作为消耗品直接使用"; return false;
                    case 11:
                        reason = "促织须在对应玩法中使用，不能直接消耗"; return false;
                    default:
                        reason = "此类物品没有 NPC 可调用的本体使用路径"; return false;
                }
            }
            catch
            {
                reason = "物品配置无法可靠读取";
                return false;
            }
        }

        // 与 query_npc_usable_items 和 use_item 共用的最终资格判定。这里必须覆盖
        // 原生调用会在扣物/应用即时效果之后才检查的条件，避免重演读书、修炼那种
        // “候选看似可用、落地却必败”的前后端漂移。天劫符箓会先转换为 432 号
        // 持续药物再加入服食栏，因此同样需要在消耗符箓之前预留栏位。
        private static bool TryCanNpcUseItemNow(Character actor, ItemKey key, int ownedCount,
            out int consumeAmount, out string kind, out string reason)
        {
            if (!TryGetNpcUsableItem(key, out consumeAmount, out kind, out reason)) return false;
            if (actor == null) { reason = "人物状态不可用"; return false; }
            if (ownedCount < consumeAmount)
            {
                reason = "至少需要 " + consumeAmount + " 份，当前只有 " + ownedCount + " 份";
                return false;
            }
            try
            {
                if (key.ItemType == 8)
                {
                    var medicine = Config.Medicine.Instance[key.TemplateId];
                    sbyte attributeType = medicine.RequiredMainAttributeType;
                    if (attributeType >= 0)
                    {
                        sbyte required = medicine.RequiredMainAttributeValue;
                        if (attributeType >= 6
                            || actor.GetCurrMainAttributes()[attributeType] < required)
                        {
                            reason = "当前主属性不足";
                            return false;
                        }
                    }
                }
                bool occupiesEatingSlot = ItemTemplateHelper.IsTianJieFuLu(
                    key.ItemType, key.TemplateId) || key.GetConfig().IsEat();
                if (occupiesEatingSlot
                    && actor.GetEatingItems().GetAvailableEatingSlot(
                        actor.GetCurrMaxEatingSlotsCount()) < 0)
                {
                    reason = "当前服食栏位已满";
                    return false;
                }
                return true;
            }
            catch
            {
                reason = "物品或人物的使用规则无法可靠读取";
                return false;
            }
        }

        // 不能复用通用持有物解析：背包可能有同名但不同模板的物品，而查询只展示
        // 其中确实可用的项。执行也必须在同一实时可用集合中选键，否则会重演
        // “查询有候选、落地却拿到同名不可用项”的前后端漂移。
        private static bool TryResolveNpcUsableInventoryItem(Character actor, string requested,
            out ItemKey realKey, out int ownedCount, out int consumeAmount,
            out string kind, out string code, out string reason)
        {
            realKey = default(ItemKey); ownedCount = 0; consumeAmount = 0;
            kind = null; code = null; reason = null;
            string query = System.Text.RegularExpressions.Regex.Replace(
                (requested ?? string.Empty).Trim(), @"[×xX]\s*\d+\s*$", "").Trim();
            if (actor == null || query.Length == 0)
            { code = "bad_args"; reason = "未指定可用物品"; return false; }

            bool exactSeen = false, fuzzySeen = false;
            string exactFailure = null, fuzzyFailure = null;
            ItemKey fuzzyKey = default(ItemKey); int fuzzyCount = 0, fuzzyAmount = 0;
            string fuzzyKind = null, fuzzyName = null;
            try
            {
                foreach (var pair in actor.GetInventory().Items)
                {
                    if (pair.Value <= 0) continue;
                    string itemName = null;
                    try { itemName = StripTags(ItemTemplateHelper.GetName(
                        pair.Key.ItemType, pair.Key.TemplateId)); } catch { }
                    if (string.IsNullOrWhiteSpace(itemName)) continue;
                    itemName = itemName.Trim();
                    bool exact = string.Equals(itemName, query,
                        StringComparison.OrdinalIgnoreCase);
                    bool fuzzy = !exact && NameHit(itemName, query);
                    if (!exact && !fuzzy) continue;
                    if (exact) exactSeen = true; else fuzzySeen = true;
                    if (!TryCanNpcUseItemNow(actor, pair.Key, pair.Value,
                            out int amount, out string itemKind, out string why))
                    {
                        if (exact && exactFailure == null) exactFailure = why;
                        else if (!exact && fuzzyFailure == null) fuzzyFailure = why;
                        continue;
                    }
                    if (exact)
                    {
                        realKey = pair.Key; ownedCount = pair.Value;
                        consumeAmount = amount; kind = itemKind;
                        return true;
                    }
                    if (!exactSeen && (fuzzyName == null || itemName.Length > fuzzyName.Length))
                    {
                        fuzzyKey = pair.Key; fuzzyCount = pair.Value;
                        fuzzyAmount = amount; fuzzyKind = itemKind; fuzzyName = itemName;
                    }
                }
            }
            catch
            { code = "item_unavailable"; reason = "背包物品无法可靠读取"; return false; }

            if (!exactSeen && fuzzyName != null)
            {
                realKey = fuzzyKey; ownedCount = fuzzyCount;
                consumeAmount = fuzzyAmount; kind = fuzzyKind;
                return true;
            }
            code = exactSeen || fuzzySeen ? "item_unavailable" : "not_owned";
            reason = exactSeen ? exactFailure : fuzzySeen ? fuzzyFailure : null;
            if (string.IsNullOrWhiteSpace(reason))
                reason = code == "not_owned"
                    ? "此刻背包中没有「" + query + "」；只能从实时可用候选中选择"
                    : "「" + query + "」此刻不满足 NPC 使用条件";
            return false;
        }

        private static bool FindOwnedItem(Character npc, ItemKey req, out ItemKey realKey, out int have)
        {
            realKey = default(ItemKey); have = 0;
            var items = npc.GetInventory().Items;
            int n;
            if (items.TryGetValue(req, out n)) { realKey = req; have = n; return true; }
            ItemKey byModTpl = default(ItemKey), byTpl = default(ItemKey); int haveModTpl = 0, haveTpl = 0; bool fModTpl = false, fTpl = false;
            foreach (var kv in items)
            {
                var k = kv.Key;
                if (k.ItemType != req.ItemType || k.TemplateId != req.TemplateId) continue;
                if (!fTpl) { byTpl = k; haveTpl = kv.Value; fTpl = true; }
                if (!fModTpl && k.ModificationState == req.ModificationState) { byModTpl = k; haveModTpl = kv.Value; fModTpl = true; }
            }
            if (fModTpl) { realKey = byModTpl; have = haveModTpl; return true; }
            if (fTpl) { realKey = byTpl; have = haveTpl; return true; }
            return false;
        }

        // 在 NPC 已装备物(GetEquipment 17 槽)里按 类型+模板 找匹配项,返回槽位 index;无则 -1。供"赠/换 身上佩带之物"用。
        private static sbyte FindEquippedSlot(Character npc, ItemKey req)
        {
            var eq = npc.GetEquipment();
            if (eq == null) return -1;
            for (int i = 0; i < eq.Length; i++)
            {
                var k = eq[i];
                if (k.IsValid() && k.ItemType == req.ItemType && k.TemplateId == req.TemplateId) return (sbyte)i;
            }
            return -1;
        }

        // build 24185552 权威规则：逐个真实槽调用 IsItemMeetSlot。这样同时覆盖三兵器槽、
        // 普通/囊袋佩饰(8..10/14..16)与三类代步(11..13)，并自然排除不能装备的子类。
        private static sbyte ResolveEquipSlot(ItemKey key)
        {
            for (sbyte slot = 0; slot < 17; slot++)
                if (EquipmentSlotHelper.IsItemMeetSlot(slot, key)) return slot;
            return -1;
        }

        private static sbyte[] SlotsForPart(string part)
        {
            switch (part)
            {
                case "weapon": return new sbyte[] { 0, 1, 2 };
                case "clothing": return new sbyte[] { 4 };
                case "armor": return new sbyte[] { 3, 5, 6, 7 };   // 护具全槽
                case "accessory": return new sbyte[] { 8, 9, 10, 14, 15, 16 };
                case "carrier": return new sbyte[] { 11, 12, 13 };
                // 未知部位必须失败关闭，不能把拼错/过期参数静默解释成“卸下兵器”。
                default: return new sbyte[0];
            }
        }

        // 传功:按 NPC 自己的练法(正练/逆练 + 进度 = readingState)把武学传给太吾。
        // 全程 try/catch:LearnCombatSkill 对"已学过"等情形会抛异常,直调 GM 命令时该异常会冲垮后端主循环→游戏卡死(线上 bug);
        // 这里在 mod 自己的 RPC 内接住,绝不冲垮后端。画像层已排除太吾已会的武学,正常流程不会走到异常。
        // 原生 Character.ApplyAddRelation_Adoptive* 真正写入时只复核
        // RelationTypeHelper；年龄、好感、是否已有在世父母/子女仅用于本体 AI 自主择亲，
        // 不是写入契约。玩家或 Agent 明确选择认亲时放宽这些叙事前置，仍保留会让原生
        // 写入静默 no-op 的关系冲突校验，避免误报成功或产生不完整关系。
        private static bool ValidateAdoptiveRelation(int actorId, Character actor,
            int targetId, Character target, bool targetIsParent,
            out string code, out string reason)
        {
            code = "relation_not_allowed";
            reason = "本体义亲规则不允许建立这层名分";
            if (actorId <= 0 || targetId <= 0 || actorId == targetId
                || actor == null || target == null)
            {
                code = "bad_args"; reason = "义亲双方无效"; return false;
            }

            ushort expected = targetIsParent ? (ushort)64 : (ushort)128;
            if (DomainManager.Character.HasRelation(actorId, targetId, expected))
            {
                code = "already";
                reason = targetIsParent ? "对方已经是你的义父母" : "对方已经是你的义子女";
                return false;
            }

            bool allowed = targetIsParent
                ? RelationTypeHelper.AllowAddingAdoptiveParentRelation(actorId, targetId)
                : RelationTypeHelper.AllowAddingAdoptiveChildRelation(actorId, targetId);
            if (!allowed)
            {
                code = "relation_not_allowed";
                reason = "双方已有血亲、继亲、义亲、结义、婚恋或其它冲突关系，不能再结义亲";
                return false;
            }
            return true;
        }

        private static string AdoptiveParentTitle(Character parent)
            => parent != null && parent.GetGender() == 1 ? "义父" : "义母";

        private static string AdoptiveChildTitle(Character child)
            => child != null && child.GetGender() == 1 ? "义子" : "义女";

        private static ushort RelationTypeForDissolve(string relation)
        {
            switch ((relation ?? string.Empty).ToLowerInvariant())
            {
                case "friend": return 8192;
                case "sworn": return 512;
                case "spouse": return 1024;
                case "lover": return 16384;
                case "mentor": return 2048;
                case "adoptive_parent": return 64;
                case "adoptive_child": return 128;
                case "enemy": return 32768;
                default: return 0;
            }
        }

        private static bool HasRelationEither(int firstId, int secondId,
            ushort relationType)
        {
            return firstId > 0 && secondId > 0 && relationType != 0
                && (DomainManager.Character.HasRelation(firstId, secondId, relationType)
                    || DomainManager.Character.HasRelation(secondId, firstId, relationType));
        }

        private static bool HasLearnedCombatSkill(Character character, short skillTemplateId)
        {
            List<short> learned = character?.GetLearnedCombatSkills();
            return learned != null && learned.Contains(skillTemplateId);
        }

        private static SerializableModData TeachSkill(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "传功请求不完整");
            int npcId = 0, taiwuId = 0, tpl = 0, rcpt = 0;
            if (!p.Get("npc_id", out npcId) || !p.Get("taiwu_id", out taiwuId) || !p.Get("template_id", out tpl))
                return Fail("missing_parameter", "缺少参数");
            p.Get("learner_id", out rcpt);
            int learnerId = rcpt > 0 ? rcpt : taiwuId;   // learner_id>0 → 传给第三方 NPC;否则传太吾
            // b24185552 Character/CombatSkill paths treat templateId >= 0 as valid.
            if (learnerId <= 0 || tpl < 0) return Fail("bad_args", "参数无效");
            if (npcId == learnerId) return Fail("bad_args", "师徒同体");

            Character learner = null;
            if (!DomainManager.Character.TryGetElement_Objects(learnerId, out learner) || learner == null)
                return Fail("no_taiwu", "受学者无效");

            short skillTpl = (short)tpl;
            if (HasLearnedCombatSkill(learner, skillTpl))
                return Fail("already", "受学者已习得此武学");

            // 凭空捏造防线:NPC 自己都不会这门武学 → 拒绝传授,不能无中生有。
            // #1 进度规则:NPC→第三方 NPC 当面亲授 → 受学者直接继承师父当前修习进度(GetReadingState);
            //    NPC→太吾 现已不走本路径(改默写成秘籍、太吾读书自行研习,从 0 起),故仅在 learnerId==taiwuId 的兜底分支才清零。
            ushort readingState = 0;
            try
            {
                var npcSkills = DomainManager.CombatSkill.GetCharCombatSkills(npcId);
                if (npcSkills == null || !npcSkills.ContainsKey(skillTpl))
                    return Fail("npc_not_known", "NPC 并不会这门武学,无从传授");
                if (learnerId != taiwuId) readingState = npcSkills[skillTpl].GetReadingState();   // 第三方 NPC 继承师父进度
            }
            catch (Exception e) { return Fail("read_skill_failed", "读取师父武学失败:" + e.GetType().Name); }

            try { DomainManager.Character.LearnCombatSkill(context, learnerId, skillTpl, readingState); }
            catch (Exception e)
            {
                if (!HasLearnedCombatSkill(learner, skillTpl))
                    return Indeterminate("teach_skill_indeterminate", "传功状态无法确认:" + e.GetType().Name);
            }
            if (!HasLearnedCombatSkill(learner, skillTpl))
                return Indeterminate("teach_skill_postcondition_indeterminate", "传功接口已返回但受学者仍未习得此武学");

            var done = new SerializableModData();
            done.Set("success", true);
            done.Set("message", "learned");
            done.Set("reading_state", (int)readingState);
            return done;
        }

        // 离去:让 NPC 不再追随太吾。两种归属都要解:① 地图跟随名单(TaiwuUnfollowNpc,不在名单则内部空操作,安全);
        // ② 队伍随从(LeaveGroup——但它对"不在队伍"者会抛 "not in the group" 异常,直调会冲垮后端,故先 IsInGroup 判定再退,全程 try/catch)。
        private static SerializableModData Leave(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "离去请求不完整");
            int npcId = 0;
            if (!p.Get("npc_id", out npcId) || npcId <= 0) return Fail("missing_parameter", "缺少参数");
            npcId = ResolveCharacterProxyId(npcId);

            bool wasFollowing = false, wasInGroup = false;
            try
            {
                wasFollowing = DomainManager.Taiwu.IsCharacterFollowedByTaiwu(npcId);
                wasInGroup = DomainManager.Taiwu.IsInGroup(npcId);
                // 0.33.0.4 已经建立并入过队的副本还没有这个新状态位。离队前补写，
                // 避免升级后第一次离队又被误判为“尚未首次入队”。
                if (wasInGroup && !MarkCharacterProxyEverJoined(context, npcId,
                        out string joinedStateError))
                    return Indeterminate("character_proxy_join_state_indeterminate",
                        joinedStateError);
                DomainManager.Taiwu.TaiwuUnfollowNpc(context, npcId);     // 安全:不在名单则空操作
                if (wasInGroup) DomainManager.Taiwu.LeaveGroup(context, npcId);   // 仅在队伍中才退,避免 "not in the group" 异常
            }
            catch (Exception e)
            {
                // LeaveGroup 的通知/装备方案收尾可能在权威队伍位已经删除后抛错。
                // 先以后置状态判定，避免把已经完整离队的人永久记成 unknown。
                bool stillFollowing = true, stillInGroup = true;
                try
                {
                    stillFollowing = DomainManager.Taiwu.IsCharacterFollowedByTaiwu(npcId);
                    stillInGroup = DomainManager.Taiwu.IsInGroup(npcId);
                }
                catch { }
                if (!stillFollowing && !stillInGroup)
                {
                    var landed = new SerializableModData();
                    landed.Set("success", true);
                    landed.Set("status", "executed");
                    landed.Set("message", "已离去");
                    return landed;
                }
                return Indeterminate("leave_indeterminate", "跟随或队伍状态可能已部分改变:" + e.GetType().Name);
            }

            var done = new SerializableModData();
            done.Set("success", true);
            done.Set("status", "executed");
            done.Set("message", (wasFollowing || wasInGroup)
                ? ("已离去(" + (wasFollowing ? "退跟随" : "") + (wasInGroup ? (wasFollowing ? "+退队伍" : "退队伍") : "") + ")")
                : "该 NPC 本就未追随/未在队伍");
            return done;
        }

        // 入队:让 NPC 真正加入太吾队伍(JoinGroup —— 设其 LeaderId=太吾、入太吾村、列入战斗队伍)。
        // 本体 JoinGroup 首步会恢复离队时保存的装备方案。旧方案若因物品变化损坏，Apply 会抛错且坏记录不删除，
        // 此后每次入队都卡在同一记录。故先单独执行：成功则保持本体语义；失败则只丢弃旧方案、保留当前装备再入队。
        private const string CharacterProxySourcePrefix = "character_proxy_source_v1_";
        private const string CharacterProxyReversePrefix = "character_proxy_reverse_v1_";
        private const string CharacterProxyEverJoinedPrefix = "character_proxy_ever_joined_v1_";
        private const string CharacterProxyReplacementPrefix = "character_proxy_replacement_v2_";
        private const string CharacterProxyPendingLegacyPrefix = "character_proxy_pending_legacy_v2_";

        private static string CharacterProxySourceKey(int sourceId)
            => CharacterProxySourcePrefix + sourceId.ToString(CultureInfo.InvariantCulture);

        private static string CharacterProxyReverseKey(int proxyId)
            => CharacterProxyReversePrefix + proxyId.ToString(CultureInfo.InvariantCulture);

        private static string CharacterProxyEverJoinedKey(int proxyId)
            => CharacterProxyEverJoinedPrefix + proxyId.ToString(CultureInfo.InvariantCulture);

        private static string CharacterProxyReplacementKey(int legacyProxyId)
            => CharacterProxyReplacementPrefix + legacyProxyId.ToString(CultureInfo.InvariantCulture);

        private static string CharacterProxyPendingLegacyKey(int sourceId)
            => CharacterProxyPendingLegacyPrefix + sourceId.ToString(CultureInfo.InvariantCulture);


        private static bool TryGetReplacementProxy(int legacyProxyId, out int replacementId)
        {
            replacementId = 0;
            if (legacyProxyId <= 0) return false;
            string modId = string.IsNullOrWhiteSpace(_modIdStr)
                ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                return DomainManager.Mod.TryGet(modId,
                    CharacterProxyReplacementKey(legacyProxyId), true, out replacementId)
                    && replacementId > 0;
            }
            catch { replacementId = 0; return false; }
        }

        private static bool TryGetPendingLegacyProxy(int sourceId, out int legacyProxyId)
        {
            legacyProxyId = 0;
            if (sourceId <= 0) return false;
            string modId = string.IsNullOrWhiteSpace(_modIdStr)
                ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                return DomainManager.Mod.TryGet(modId,
                    CharacterProxyPendingLegacyKey(sourceId), true, out legacyProxyId)
                    && legacyProxyId > 0;
            }
            catch { legacyProxyId = 0; return false; }
        }

        private static bool PersistCharacterProxySource(DataContext context,
            int sourceId, int proxyId, out string error)
        {
            error = null;
            string modId = string.IsNullOrWhiteSpace(_modIdStr)
                ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                DomainManager.Mod.SetInt(context, modId,
                    CharacterProxySourceKey(sourceId), true, proxyId);
                if (!TryGetPersistedCharacterProxy(sourceId, out int verified)
                    || verified != proxyId)
                {
                    error = "人物阶段到永久副本的映射尚未可靠写入";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = "人物阶段映射写入异常:" + e.GetType().Name;
                return false;
            }
        }

        private static bool TryGetPersistedCharacterProxy(int sourceId, out int proxyId)
        {
            proxyId = 0;
            if (sourceId <= 0) return false;
            string modId = string.IsNullOrWhiteSpace(_modIdStr) ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                return DomainManager.Mod.TryGet(modId, CharacterProxySourceKey(sourceId), true, out proxyId)
                    && proxyId > 0;
            }
            catch { proxyId = 0; return false; }
        }

        internal static bool TryGetCharacterProxySource(int proxyId, out int sourceId)
        {
            sourceId = 0;
            if (proxyId <= 0) return false;
            string modId = string.IsNullOrWhiteSpace(_modIdStr) ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                return DomainManager.Mod.TryGet(modId, CharacterProxyReverseKey(proxyId), true, out sourceId)
                    && sourceId > 0;
            }
            catch { sourceId = 0; return false; }
        }

        internal static bool IsCharacterProxy(int characterId)
            => TryGetCharacterProxySource(characterId, out _);

        /// <summary>
        /// 固定人物副本刚建立时可能没有可用 Location。玩家第一次见到的原人物本就是当面，
        /// 因此副本在首次成功入队前统一视为仍在太吾现场；一旦成功入队，状态永久落入存档，
        /// 后续离队便恢复使用副本自己的实时位置，不会因为无地址永久冒充在场。
        /// </summary>
        private static bool IsCharacterProxyAwaitingFirstJoin(int proxyId)
        {
            if (!TryGetCharacterProxySource(proxyId, out _)) return false;
            string modId = string.IsNullOrWhiteSpace(_modIdStr)
                ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                return !DomainManager.Mod.TryGet(modId,
                    CharacterProxyEverJoinedKey(proxyId), true, out int joined)
                    || joined == 0;
            }
            catch
            {
                // 读取失败不能把可能已经离队的人重新判成当面。
                return false;
            }
        }

        private static bool MarkCharacterProxyEverJoined(DataContext context, int proxyId,
            out string error)
        {
            error = null;
            if (!TryGetCharacterProxySource(proxyId, out _)) return true;
            string modId = string.IsNullOrWhiteSpace(_modIdStr)
                ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                DomainManager.Mod.SetInt(context, modId,
                    CharacterProxyEverJoinedKey(proxyId), true, 1);
                if (!DomainManager.Mod.TryGet(modId,
                        CharacterProxyEverJoinedKey(proxyId), true, out int joined)
                    || joined != 1)
                {
                    error = "首次入队状态尚未可靠写入";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = "首次入队状态写入异常:" + e.GetType().Name;
                return false;
            }
        }

        private static bool IsLegalIntelligentProxy(Character character)
        {
            if (character == null || character.GetCreatingType() != 1) return false;
            try
            {
                short templateId = character.GetTemplateId();
                return templateId >= 0 && templateId < Config.Character.Instance.Count
                    && Config.Character.Instance[templateId].CreatingType == 1;
            }
            catch { return false; }
        }

        private static bool HasUsableProxyMainAttributes(MainAttributes attributes)
        {
            // 本体最终属性会把每项钳到至少 1；若基础值为全 0，界面恰好表现为“六项全 1”。
            // 普通创建器生成值远高于该保守下限，固定模板缺字段时则通常是 0。
            return attributes.GetSum() >= 60;
        }

        private static bool HasUsableProxyLifeSkillQualifications(LifeSkillShorts qualifications)
        {
            return qualifications.GetSum() >= 80;
        }

        private static bool HasUsableProxyCombatSkillQualifications(CombatSkillShorts qualifications)
        {
            return qualifications.GetSum() >= 70;
        }

        private static void CopySafeProxyName(DataContext context, Character source,
            Character proxy)
        {
            FullName safeName = source.GetFullName();
            safeName.Type = (sbyte)(safeName.Type & ~FullNameType.NoNameInfant);

            // 固定人物的姓名有时只存在于模板配置中，FullName 本身是 None。把配置姓名
            // 转成普通人物可持久化的自定义姓名，不能把“无名婴儿”或固定模板哨兵值带过去。
            if ((safeName.Type & (FullNameType.Han | FullNameType.Zang)) == 0)
            {
                short sourceTemplateId = source.GetTemplateId();
                Config.CharacterItem sourceConfig = sourceTemplateId >= 0
                    && sourceTemplateId < Config.Character.Instance.Count
                    ? Config.Character.Instance[sourceTemplateId] : null;
                if (sourceConfig != null)
                {
                    int customSurnameId = string.IsNullOrEmpty(sourceConfig.Surname)
                        ? -1 : DomainManager.World.RegisterCustomText(context,
                            sourceConfig.Surname);
                    int customGivenNameId = string.IsNullOrEmpty(sourceConfig.GivenName)
                        ? -1 : DomainManager.World.RegisterCustomText(context,
                            sourceConfig.GivenName);
                    if (customSurnameId >= 0 && customGivenNameId >= 0)
                    {
                        safeName = new FullName(customSurnameId, customGivenNameId,
                            -1, -1, -1, -1);
                    }
                    else
                    {
                        // 本体 FullName 的汉名构造器会在缺姓时使用传入的随机 SurnameId。
                        // 单名固定人物必须走“无姓的自定义藏名”存储形态，否则会变成随机姓+原名。
                        int singleNameId = customGivenNameId >= 0
                            ? customGivenNameId : customSurnameId;
                        if (singleNameId >= 0)
                            safeName = new FullName(singleNameId, -1, -1);
                    }
                }
                safeName.Type = (sbyte)(safeName.Type & ~FullNameType.NoNameInfant);
            }
            proxy.SetFullName(safeName, context);
        }

        private static void CopySafeProxyFeatures(DataContext context, Character source,
            Character proxy)
        {
            List<short> sourceFeatures = source.GetFeatureIds();
            if (sourceFeatures == null || sourceFeatures.Count == 0) return;
            sbyte proxyOrg = proxy.GetOrganizationInfo().OrgTemplateId;
            foreach (short featureId in sourceFeatures)
            {
                if (featureId < 0 || featureId >= Config.CharacterFeature.Instance.Count)
                    continue;
                Config.CharacterFeatureItem feature = Config.CharacterFeature.Instance[featureId];
                // 仅迁移本体定义的普通永久品性/天赋。剧情、冒险、相枢、鸡、隐藏、临时和
                // 组织专属特性全部留在原剧情锚点，避免把 Boss 生命周期带入普通人物。
                if (feature == null || !feature.IsNormal() || feature.Hidden
                    || feature.BelongAdventure || feature.Duration != 0 || feature.IsChickenFeature
                    || !feature.IsAllowedForOrganization(proxyOrg)
                    || (feature.Gender >= 0 && feature.Gender != proxy.GetGender())
                    // 七元赋性必须保留普通创建器的随机结果；任何会修改冷静、聪颖、
                    // 热情、勇壮、坚毅、福缘或合道的源特性都不迁移。
                    || feature.PersonalityCalm != 0 || feature.PersonalityClever != 0
                    || feature.PersonalityEnthusiastic != 0 || feature.PersonalityBrave != 0
                    || feature.PersonalityFirm != 0 || feature.PersonalityLucky != 0
                    || feature.PersonalityPerceptive != 0)
                    continue;
                proxy.AddFeature(context, featureId, removeMutexFeature: true);
            }
        }

        private static bool IsBossProxyCombatSkill(short skillId)
        {
            try
            {
                if (skillId < 0 || skillId >= Config.CombatSkill.Instance.Count) return false;
                Config.CombatSkillItem skill = Config.CombatSkill.Instance[skillId];
                if (skill == null || skill.EquipType != 0) return false;
                int[] effectIds = { skill.DirectEffectID, skill.ReverseEffectID };
                foreach (int effectId in effectIds)
                {
                    if (effectId < 0 || effectId >= Config.SpecialEffect.Instance.Count) continue;
                    Config.SpecialEffectItem effect = Config.SpecialEffect.Instance[(short)effectId];
                    if (effect != null && !string.IsNullOrEmpty(effect.ClassName)
                        && effect.ClassName.IndexOf("Neigong.Boss",
                            StringComparison.Ordinal) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool CopySafeProxyCombatSkills(DataContext context, Character source,
            Character proxy, out string error)
        {
            error = null;
            List<short> learned = source.GetLearnedCombatSkills();
            if (learned == null || learned.Count == 0) return true;

            Dictionary<short, GameData.Domains.CombatSkill.CombatSkill> sourceSkills =
                DomainManager.CombatSkill.GetCharCombatSkills(source.GetId());
            var copiedSkills = new List<GameData.Domains.CombatSkill.CombatSkill>();
            var copiedIds = new List<short>();
            var copiedIdSet = new HashSet<short>();
            var bossSkills = new HashSet<short>();
            foreach (short skillId in learned)
            {
                if (!copiedIdSet.Add(skillId)) continue;
                if (skillId < 0 || skillId >= Config.CombatSkill.Instance.Count
                    || sourceSkills == null
                    || !sourceSkills.TryGetValue(skillId, out var sourceSkill)
                    || sourceSkill == null)
                {
                    error = "原人物功法资料不完整:" + skillId.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                var copy = GameData.Serializer.Serializer.CreateCopy(sourceSkill);
                copy.OfflineSetCharId(proxy.GetId());
                copy.OfflineSetSpecialEffectId(-1L);
                copiedSkills.Add(copy);
                copiedIds.Add(skillId);
                if (IsBossProxyCombatSkill(skillId)) bossSkills.Add(skillId);
            }
            if (copiedSkills.Count == 0) return true;

            try
            {
                // 先清除普通创建器随机生成的装配和功法，避免人物页留下指向已删除功法的槽位。
                proxy.ClearCombatSkillEquipment(context);
                DomainManager.SpecialEffect.RemoveAllBrokenSkillEffects(context, proxy);
                DomainManager.CombatSkill.RemoveAllCombatSkills(proxy.GetId());
                DomainManager.CombatSkill.RegisterCombatSkills(proxy.GetId(), copiedSkills);
                DomainManager.Character.AutoActivateReadCombatSkillNormalPages(context,
                    copiedSkills, proxy.GetId());
                proxy.SetLearnedCombatSkills(copiedIds, context);
                DomainManager.Extra.CopyCombatSkillProficiency(context,
                    source.GetId(), proxy.GetId());

                short[] sourcePanels = source.GetCombatSkillAttainmentPanels();
                short[] proxyPanels = proxy.GetCombatSkillAttainmentPanels();
                if (proxyPanels != null)
                {
                    short[] safePanels = sourcePanels != null
                        && sourcePanels.Length == proxyPanels.Length
                        ? (short[])sourcePanels.Clone()
                        : new short[proxyPanels.Length];
                    for (int i = 0; i < safePanels.Length; i++)
                    {
                        if (sourcePanels == null || sourcePanels.Length != proxyPanels.Length
                            || (safePanels[i] >= 0 && !copiedIdSet.Contains(safePanels[i])))
                            safePanels[i] = -1;
                    }
                    proxy.SetCombatSkillAttainmentPanels(safePanels, context);
                }

                foreach (var skill in copiedSkills)
                {
                    short skillId = skill.GetId().SkillTemplateId;
                    if (bossSkills.Contains(skillId))
                    {
                        // Boss 内功保留为可见资料，但不得运转、装备或挂载二阶段特效。
                        skill.SetRevoked(true, context);
                    }
                    else if (skill.GetDirection() >= 0)
                    {
                        // 本体 AddAllBrokenSkillEffects 对全部已学功法执行，不以 Revoked 为门；
                        // 这里只额外排除已识别的 Boss 内功，保持普通功法的原生常驻效果。
                        DomainManager.SpecialEffect.Add(context, proxy.GetId(), skillId, 3, -1);
                    }
                }

                short loopingNeigong = source.GetLoopingNeigong();
                if (loopingNeigong >= 0 && copiedIdSet.Contains(loopingNeigong)
                    && !bossSkills.Contains(loopingNeigong))
                    proxy.SetLoopingNeigong(loopingNeigong, context);
                else
                    proxy.SetLoopingNeigong(-1, context);

                Dictionary<short, GameData.Domains.CombatSkill.CombatSkill> verified =
                    DomainManager.CombatSkill.GetCharCombatSkills(proxy.GetId());
                if (verified == null || verified.Count != copiedIds.Count)
                {
                    error = "人物副本功法数量后置校验失败";
                    return false;
                }
                foreach (short skillId in copiedIds)
                {
                    if (!verified.ContainsKey(skillId))
                    {
                        error = "人物副本功法后置校验失败:"
                            + skillId.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                error = "人物副本功法迁移异常:" + e.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// 只在新副本尚未落盘映射前运行。能从固定人物安全读取的数值会迁移；缺字段时保留
        /// 普通创建器的随机值。任何异常值都在本次创建事务内重建，既有存档绝不在访问时自愈。
        /// </summary>
        private static bool FinalizeNewLegalProxyData(DataContext context, Character source,
            Character proxy, out string error)
        {
            error = null;
            if (context == null || source == null || proxy == null)
            { error = "人物副本资料初始化参数不完整"; return false; }
            try
            {
                CopySafeProxyName(context, source, proxy);

                MainAttributes sourceMain = source.GetBaseMainAttributes();
                if (HasUsableProxyMainAttributes(sourceMain))
                    proxy.SetBaseMainAttributes(sourceMain, context);
                if (!HasUsableProxyMainAttributes(proxy.GetBaseMainAttributes()))
                    proxy.RecreateMainAttributes(context, 0, 8);

                LifeSkillShorts sourceLife = source.GetBaseLifeSkillQualifications();
                if (HasUsableProxyLifeSkillQualifications(sourceLife))
                    proxy.SetBaseLifeSkillQualifications(ref sourceLife, context);
                if (!HasUsableProxyLifeSkillQualifications(proxy.GetBaseLifeSkillQualifications()))
                    proxy.RecreateLifeSkillQualifications(context, 0, 8);

                CombatSkillShorts sourceCombat = source.GetBaseCombatSkillQualifications();
                if (HasUsableProxyCombatSkillQualifications(sourceCombat))
                    proxy.SetBaseCombatSkillQualifications(ref sourceCombat, context);
                if (!HasUsableProxyCombatSkillQualifications(proxy.GetBaseCombatSkillQualifications()))
                    proxy.RecreateCombatSkillQualifications(context, 0, 8);

                List<SkillQualificationBonus> sourceBonuses = source.GetSkillQualificationBonuses();
                if (sourceBonuses != null && sourceBonuses.Count > 0)
                    proxy.SetSkillQualificationBonuses(
                        new List<SkillQualificationBonus>(sourceBonuses), context);

                sbyte mainInterest = source.GetMainAttributeInterest();
                if (mainInterest >= 0 && mainInterest < 6)
                    proxy.SetMainAttributeInterest(mainInterest, context);
                sbyte lifeInterest = source.GetLifeSkillTypeInterest();
                if (lifeInterest >= 0 && lifeInterest < Config.LifeSkillType.Instance.Count)
                    proxy.SetLifeSkillTypeInterest(lifeInterest, context);
                sbyte combatInterest = source.GetCombatSkillTypeInterest();
                if (combatInterest >= 0
                    && combatInterest < Config.CombatSkillType.Instance.Count)
                    proxy.SetCombatSkillTypeInterest(combatInterest, context);
                CopySafeProxyFeatures(context, source, proxy);

                // CreateIntelligentCharacter 会初始化当前属性；上面若迁移/重建了基础属性，必须
                // 同步一次当前值，否则人物详情会继续显示创建前的旧上限。
                proxy.SetCurrMainAttributes(proxy.GetMaxMainAttributes(), context);

                if (!HasUsableProxyMainAttributes(proxy.GetBaseMainAttributes())
                    || !HasUsableProxyLifeSkillQualifications(proxy.GetBaseLifeSkillQualifications())
                    || !HasUsableProxyCombatSkillQualifications(proxy.GetBaseCombatSkillQualifications()))
                {
                    error = "普通人物创建器未能生成完整属性与资质";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = "人物副本资料初始化异常:" + e.GetType().Name;
                return false;
            }
        }

        private static bool IsLegalProxyIdentity(OrganizationInfo identity, sbyte gender,
            short physiologicalAge)
        {
            try
            {
                sbyte orgId = identity.OrgTemplateId;
                sbyte grade = identity.Grade;
                if (orgId < 0 || orgId >= Config.Organization.Instance.Count
                    || grade < 0 || grade > 8 || !identity.Principal)
                    return false;

                Config.OrganizationItem organization = Config.Organization.Instance[orgId];
                if (organization == null || organization.Members == null
                    || grade >= organization.Members.Length
                    || !OrganizationDomain.MeetGenderRestriction(orgId, gender))
                    return false;

                // 16 是太吾村。随机命中它会触发本体入村事件并把副本登记成村民，不属于
                // 普通江湖身份随机的范围；0 则是本体明确支持的无门无派身份。
                if (orgId == 16) return false;
                if (orgId != 0)
                {
                    if (organization.IsSect || !organization.IsCivilian) return false;
                    short currentSettlementId =
                        DomainManager.Organization.GetSettlementIdByOrgTemplateId(orgId);
                    if (currentSettlementId < 0 || identity.SettlementId != currentSettlementId)
                        return false;
                }
                else if (identity.SettlementId >= 0)
                {
                    return false;
                }

                short memberId = organization.Members[grade];
                if (memberId < 0 || memberId >= Config.OrganizationMember.Instance.Count)
                    return false;
                Config.OrganizationMemberItem member = Config.OrganizationMember.Instance[memberId];
                if (member == null || member.Organization != orgId || member.Grade != grade
                    || member.RestrictPrincipalAmount
                    || (member.Gender >= 0 && member.Gender != gender)
                    || (physiologicalAge >= 0
                        && physiologicalAge < member.IdentityActiveAge))
                    return false;
                return true;
            }
            catch { return false; }
        }

        private static bool TryPickLegalProxyIdentity(DataContext context, sbyte gender,
            short physiologicalAge, out OrganizationInfo identity)
        {
            identity = OrganizationInfo.None;
            var candidates = new List<OrganizationInfo>();
            try
            {
                for (sbyte orgId = 0; orgId < Config.Organization.Instance.Count; orgId++)
                {
                    if (orgId == 16) continue;
                    Config.OrganizationItem organization = Config.Organization.Instance[orgId];
                    if (organization == null || organization.Members == null
                        || !OrganizationDomain.MeetGenderRestriction(orgId, gender))
                        continue;

                    short settlementId = -1;
                    if (orgId != 0)
                    {
                        // 副本不能凭空取得门派成员身份；只允许本体平民组织中的普通身份。
                        if (organization.IsSect || !organization.IsCivilian) continue;
                        settlementId =
                            DomainManager.Organization.GetSettlementIdByOrgTemplateId(orgId);
                        if (settlementId < 0) continue;
                    }

                    int gradeCount = Math.Min(9, organization.Members.Length);
                    for (sbyte grade = 0; grade < gradeCount; grade++)
                    {
                        var candidate = new OrganizationInfo(orgId, grade,
                            principal: true, settlementId);
                        if (IsLegalProxyIdentity(candidate, gender, physiologicalAge))
                            candidates.Add(candidate);
                    }
                }
            }
            catch { }

            if (candidates.Count == 0) return false;
            identity = candidates[context.Random.Next(candidates.Count)];
            return true;
        }

        private static bool TryPickLegalProxyTemplate(Location location, sbyte gender,
            sbyte orgTemplateId, out short templateId)
        {
            templateId = -1;
            if (gender != 0 && gender != 1) gender = 0;
            // 与本体批量创建普通人物一致：身份配置提供了人物模板时优先使用；未配置才
            // 回退到人物落点所在州的智能人物模板。
            try
            {
                if (orgTemplateId >= 0 && orgTemplateId < Config.Organization.Instance.Count)
                {
                    short[] orgTemplates = Config.Organization.Instance[orgTemplateId].CharTemplateIds;
                    if (orgTemplates != null && gender < orgTemplates.Length)
                    {
                        short orgTemplate = orgTemplates[gender];
                        if (orgTemplate >= 0 && orgTemplate < Config.Character.Instance.Count
                            && Config.Character.Instance[orgTemplate].CreatingType == 1)
                        {
                            templateId = orgTemplate;
                            return true;
                        }
                    }
                }
            }
            catch { }
            try
            {
                sbyte stateTemplateId = location.IsValid()
                    ? DomainManager.Map.GetStateTemplateIdByAreaId(location.AreaId)
                    : DomainManager.World.GetTaiwuVillageStateTemplateId();
                short regional = MapDomain.GetCharacterTemplateId(stateTemplateId, gender);
                if (regional >= 0 && regional < Config.Character.Instance.Count
                    && Config.Character.Instance[regional].CreatingType == 1)
                {
                    templateId = regional;
                    return true;
                }
            }
            catch { }

            // 配置更新或特殊地图没有地域模板时，只在本体明确标记为智能人物的模板中兜底。
            // 先匹配性别，仍无结果才使用任一合法模板；绝不再借用固定角色/动物/敌人模板。
            try
            {
                for (short id = 0; id < Config.Character.Instance.Count; id++)
                {
                    Config.CharacterItem item = Config.Character.Instance[id];
                    if (item.CreatingType == 1 && item.Gender == gender)
                    { templateId = id; return true; }
                }
                for (short id = 0; id < Config.Character.Instance.Count; id++)
                {
                    if (Config.Character.Instance[id].CreatingType == 1)
                    { templateId = id; return true; }
                }
            }
            catch { }
            return false;
        }

        private static Character CreateLegalCharacterProxy(DataContext context,
            Character source, out string error)
        {
            error = null;
            if (context == null || source == null)
            { error = "人物副本创建参数不完整"; return null; }

            Character taiwu = DomainManager.Taiwu.GetTaiwu();
            // 副本是在太吾与特殊人物实际接触时建立。只采用太吾当前有效位置，失效时
            // 回退太吾村；绝不读取固定人物的奇遇、教程或已消失区域 Location。
            Location location = taiwu == null ? Location.Invalid : taiwu.GetValidLocation();
            if (!location.IsValid()) location = DomainManager.Taiwu.GetTaiwuVillageLocation();
            if (!location.IsValid())
            { error = "当前没有可供普通人物落地的有效位置"; return null; }

            sbyte gender = source.GetGender();
            if (gender != 0 && gender != 1) gender = (sbyte)context.Random.Next(0, 2);
            short actualAge = source.GetActualAge();
            if (actualAge < 10 || actualAge > 120) actualAge = -1;
            short physiologicalAge = source.GetPhysiologicalAge();
            if (physiologicalAge < 0 || physiologicalAge > 120) physiologicalAge = -1;
            if (!TryPickLegalProxyIdentity(context, gender, physiologicalAge,
                    out OrganizationInfo identity))
            { error = "本体配置中没有可用的普通人物身份"; return null; }
            if (!TryPickLegalProxyTemplate(location, gender, identity.OrgTemplateId,
                    out short templateId))
            { error = "本体配置中没有可用的普通人物模板"; return null; }

            var info = new IntelligentCharacterCreationInfo(location,
                identity, templateId)
            {
                Gender = gender,
                Transgender = source.GetTransgender(),
                InitializeSectSkills = true,
                AllowRandomGrowingGradeAdjust = false,
                DisableBeReincarnatedBySavedSoul = true,
            };

            // 年龄、出生月只有落在普通人物可解释范围内才继承。特殊模板缺字段、哨兵值或
            // 超常数值全部交回本体创建器，在合法范围内随机生成。
            if (actualAge >= 0) info.Age = actualAge;
            sbyte birthMonth = source.GetBirthMonth();
            if (birthMonth >= 0 && birthMonth < 12) info.BirthMonth = birthMonth;

            Character proxy = null;
            try
            {
                proxy = DomainManager.Character.CreateIntelligentCharacter(context, ref info);
                if (proxy == null || proxy.GetId() <= 0)
                { error = "本体创建接口未返回有效人物"; return null; }
                DomainManager.Character.CompleteCreatingCharacter(proxy.GetId());
                OrganizationInfo actualIdentity = proxy.GetOrganizationInfo();
                if (actualIdentity.OrgTemplateId != identity.OrgTemplateId
                    || actualIdentity.Grade != identity.Grade
                    || actualIdentity.Principal != identity.Principal
                    || actualIdentity.SettlementId != identity.SettlementId
                    || !IsLegalProxyIdentity(actualIdentity, gender,
                        proxy.GetPhysiologicalAge()))
                { error = "普通人物身份后置校验失败"; return null; }

                // 固定人物的立场数值不进入副本；在本体合法 [-500,500] 区间内独立均匀
                // 随机。七元赋性则保留 CreateIntelligentCharacter 生成的普通人物随机值。
                proxy.SetBaseMorality((short)(context.Random.Next(1001) - 500), context);
                if (proxy.GetBaseMorality() < -500 || proxy.GetBaseMorality() > 500)
                { error = "普通人物立场后置校验失败"; return null; }
            }
            catch (Exception e)
            {
                error = "合法普通人物创建异常:" + e.GetType().Name;
                return null;
            }

            // 身份白名单：姓名、普通永久特性以及真实已学功法/技艺可安全迁移；外观、
            // 七元赋性、立场、组织、装备与特殊数值均保留本次普通创建流程的合法随机值。
            if (!FinalizeNewLegalProxyData(context, source, proxy, out string finalizeError))
            { error = finalizeError; return null; }
            try
            {
                var lifeSkills = new List<LifeSkillItem>();
                List<LifeSkillItem> sourceLifeSkills = source.GetLearnedLifeSkills();
                if (sourceLifeSkills != null)
                {
                    foreach (LifeSkillItem skill in sourceLifeSkills)
                    {
                        if (skill.SkillTemplateId >= 0
                            && skill.SkillTemplateId < Config.LifeSkill.Instance.Count)
                            lifeSkills.Add(skill);
                    }
                }
                if (lifeSkills.Count > 0) proxy.SetLearnedLifeSkills(lifeSkills, context);
            }
            catch { }
            if (!CopySafeProxyCombatSkills(context, source, proxy, out string combatSkillError))
            { error = combatSkillError; return null; }
            try
            {
                sbyte sourceLevel = source.GetConsummateLevel();
                sbyte proxyMaxLevel = proxy.GetMaxConsummateLevel();
                if (sourceLevel >= 0 && proxyMaxLevel >= 0)
                    proxy.SetConsummateLevel((sbyte)Math.Min(sourceLevel, proxyMaxLevel), context);
            }
            catch { }
            try { DomainManager.Extra.AddInteractedCharacter(context, proxy.GetId()); } catch { }

            if (!IsLegalIntelligentProxy(proxy))
            { error = "本体创建的人物未通过普通人物后置校验"; return null; }
            return proxy;
        }

        private static bool PersistProxyIdentity(DataContext context, int sourceId, int proxyId,
            int legacyProxyId, out string error)
        {
            error = null;
            string modId = string.IsNullOrWhiteSpace(_modIdStr)
                ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                DomainManager.Mod.SetInt(context, modId,
                    CharacterProxySourceKey(sourceId), true, proxyId);
                DomainManager.Mod.SetInt(context, modId,
                    CharacterProxyReverseKey(proxyId), true, sourceId);
                if (legacyProxyId > 0 && legacyProxyId != proxyId)
                {
                    DomainManager.Mod.SetInt(context, modId,
                        CharacterProxyReplacementKey(legacyProxyId), true, proxyId);
                    DomainManager.Mod.SetInt(context, modId,
                        CharacterProxyPendingLegacyKey(sourceId), true, legacyProxyId);
                }
            }
            catch (Exception e)
            { error = "人物副本身份映射写入异常:" + e.GetType().Name; return false; }

            if (!TryGetPersistedCharacterProxy(sourceId, out int checkedProxy)
                || checkedProxy != proxyId
                || !TryGetCharacterProxySource(proxyId, out int checkedSource)
                || checkedSource != sourceId)
            { error = "人物副本身份映射尚未可靠写入"; return false; }
            if (legacyProxyId > 0 && legacyProxyId != proxyId
                && (!TryGetReplacementProxy(legacyProxyId, out int checkedReplacement)
                    || checkedReplacement != proxyId))
            { error = "旧人物副本替换映射尚未可靠写入"; return false; }
            return true;
        }

        private static bool FinalizeLegacyCharacterProxy(DataContext context,
            Character legacyProxy, Character newProxy, out string error)
        {
            error = null;
            if (legacyProxy == null || newProxy == null)
            { error = "旧副本或替换人物已经失效"; return false; }
            int legacyId = legacyProxy.GetId();
            int newId = newProxy.GetId();
            bool legacyInGroup = false, legacyFollowed = false;
            try
            {
                legacyInGroup = DomainManager.Taiwu.IsInGroup(legacyId);
                legacyFollowed = DomainManager.Taiwu.IsCharacterFollowedByTaiwu(legacyId);

                // 先让新实体承接队伍位，再移走旧特殊队员；JoinGroup 自身没有人数守卫，
                // 因此中途异常也不会把已经在队的人凭空踢出且丢失“原本在队”这一事实。
                if (legacyInGroup && !DomainManager.Taiwu.IsInGroup(newId))
                    DomainManager.Taiwu.JoinGroup(context, newId, showNotification: false);

                if (legacyFollowed)
                {
                    DomainManager.Taiwu.TaiwuUnfollowNpc(context, legacyId);
                    if (!DomainManager.Taiwu.IsCharacterFollowedByTaiwu(newId))
                        DomainManager.Taiwu.TaiwuFollowNpc(context, newId);
                    if (!DomainManager.Taiwu.IsCharacterFollowedByTaiwu(newId))
                    { error = "旧副本的跟随状态未能迁移"; return false; }
                }

                if (legacyInGroup && DomainManager.Taiwu.IsInGroup(legacyId))
                {
                    // 读档防崩补丁会先把旧副本标记为 converted；但它在队伍存档中仍位于
                    // “特殊队员”集合。离队这一瞬临时恢复固定生成类型，确保本体走正确集合。
                    legacyProxy.OfflineSetCreatingType(0);
                    try
                    {
                        DomainManager.Taiwu.LeaveGroup(context, legacyId, bringWards: false,
                            showNotification: false, moveToRandomAdjacentBlock: false);
                    }
                    finally { legacyProxy.OfflineSetCreatingType(1); }
                }
                if ((legacyInGroup && !DomainManager.Taiwu.IsInGroup(newId))
                    || DomainManager.Taiwu.IsInGroup(legacyId))
                { error = "旧副本的队伍状态未能完整迁移"; return false; }

                // 旧 DeepCopy 对象必须退出固定角色索引，否则下次读档会与原角色争用同一模板键。
                Location oldLocation = legacyProxy.GetLocation();
                if (oldLocation.IsValid())
                {
                    Events.RaiseFixedCharacterLocationChanged(context, legacyId,
                        oldLocation, Location.Invalid);
                    legacyProxy.SetLocation(Location.Invalid, context);
                }
                legacyProxy.OfflineSetCreatingType(1);
                return true;
            }
            catch (Exception e)
            {
                try { legacyProxy.OfflineSetCreatingType(1); } catch { }
                error = "旧人物副本收敛异常:" + e.GetType().Name;
                return false;
            }
        }

        private static bool CompletePendingLegacyMigration(DataContext context,
            int sourceId, Character newProxy, out int migrationFromId, out string error)
        {
            migrationFromId = 0;
            error = null;
            if (!TryGetPendingLegacyProxy(sourceId, out int legacyId)) return true;
            migrationFromId = legacyId;
            if (!DomainManager.Character.TryGetElement_Objects(legacyId,
                out Character legacyProxy) || legacyProxy == null)
            {
                error = "待迁移的旧人物副本已经失效";
                return false;
            }
            bool shouldMarkJoined = false;
            try
            {
                shouldMarkJoined = DomainManager.Taiwu.IsInGroup(legacyId)
                    || DomainManager.Taiwu.IsCharacterFollowedByTaiwu(legacyId)
                    || DomainManager.Taiwu.IsInGroup(newProxy.GetId())
                    || DomainManager.Taiwu.IsCharacterFollowedByTaiwu(newProxy.GetId());
            }
            catch { }
            if (!FinalizeLegacyCharacterProxy(context, legacyProxy, newProxy, out error))
                return false;
            if (shouldMarkJoined
                && !MarkCharacterProxyEverJoined(context, newProxy.GetId(), out error))
                return false;
            string modId = string.IsNullOrWhiteSpace(_modIdStr)
                ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                DomainManager.Mod.SetInt(context, modId,
                    CharacterProxyPendingLegacyKey(sourceId), true, 0);
            }
            catch (Exception e)
            { error = "旧人物副本迁移完成标记写入异常:" + e.GetType().Name; return false; }
            if (TryGetPendingLegacyProxy(sourceId, out _))
            {
                error = "旧人物副本迁移完成标记尚未可靠写入";
                return false;
            }
            return true;
        }

        private static SerializableModData MigrateLegacyCharacterProxy(DataContext context,
            int requestedSourceId, int identitySourceId, Character legacyProxy)
        {
            if (legacyProxy == null)
                return Indeterminate("character_proxy_legacy_missing", "旧人物副本已经失效");
            int legacyId = legacyProxy.GetId();
            if (TryGetReplacementProxy(legacyId, out int existingReplacement)
                && DomainManager.Character.TryGetElement_Objects(existingReplacement,
                    out Character replacement) && IsLegalIntelligentProxy(replacement))
            {
                int existingCanonicalSourceId = identitySourceId > 0
                    ? identitySourceId : requestedSourceId;
                // 上一次可能在“新实体与映射已落盘”后中断。这里必须重新确认完整双向映射，
                // 再幂等完成队伍/跟随迁移和旧固定角色退场，不能只返回 replacement。
                if (!PersistProxyIdentity(context, existingCanonicalSourceId, existingReplacement,
                    legacyId, out string identityRepairError))
                    return Indeterminate("character_proxy_replacement_mapping_indeterminate",
                        identityRepairError);
                if (!PersistCharacterProxySource(context, requestedSourceId,
                    existingReplacement, out string repairError))
                    return Indeterminate("character_proxy_replacement_mapping_indeterminate", repairError);
                if (!CompletePendingLegacyMigration(context, existingCanonicalSourceId, replacement,
                    out int completedLegacyId, out string completionError))
                    return Indeterminate("character_proxy_legacy_finalize_indeterminate",
                        completionError);
                return CharacterProxyResult(requestedSourceId, existingReplacement,
                    true, false, "已迁移到合法人物副本",
                    completedLegacyId > 0 ? completedLegacyId : legacyId);
            }

            Character source = null;
            if (identitySourceId > 0)
                DomainManager.Character.TryGetElement_Objects(identitySourceId, out source);
            if (source == null) source = legacyProxy;
            Character newProxy = CreateLegalCharacterProxy(context, source, out string createError);
            if (newProxy == null)
                return Indeterminate("character_proxy_legal_create_indeterminate", createError);
            int newId = newProxy.GetId();

            int canonicalSourceId = identitySourceId > 0 ? identitySourceId : requestedSourceId;
            if (!PersistProxyIdentity(context, canonicalSourceId, newId, legacyId,
                out string mapError))
                return Indeterminate("character_proxy_mapping_indeterminate", mapError);
            if (requestedSourceId != canonicalSourceId
                && !PersistCharacterProxySource(context, requestedSourceId, newId,
                    out string requestedMapError))
                return Indeterminate("character_proxy_mapping_indeterminate", requestedMapError);
            if (!CompletePendingLegacyMigration(context, canonicalSourceId, newProxy,
                out _, out string finalizeError))
                return Indeterminate("character_proxy_legacy_finalize_indeterminate", finalizeError);
            return CharacterProxyResult(requestedSourceId, newId, true, true,
                "旧固定模板副本已迁移为合法普通人物", legacyId);
        }

        /// <summary>
        /// 本体为剧情、战斗与事件创建的临时智慧副本会把原人物写入 SrcCharId。
        /// 该显式关系是唯一可用于跨临时 id 归一身份的权威；模板、姓名与外观都可能合法重复。
        /// </summary>
        private static bool TryResolveNativeCharacterSource(int requestedId,
            out int canonicalSourceId, out Character source, out string error)
        {
            canonicalSourceId = requestedId;
            source = null;
            error = null;
            var visited = new HashSet<int>();
            Character current = null;
            while (true)
            {
                if (!visited.Add(canonicalSourceId))
                {
                    error = "人物复制来源形成循环，已停止建立副本";
                    return false;
                }
                if (!DomainManager.Character.TryGetElement_Objects(canonicalSourceId,
                    out current) || current == null)
                {
                    error = canonicalSourceId == requestedId
                        ? "人物已失效" : "人物的原始复制来源已失效";
                    return false;
                }
                int sourceId;
                try { sourceId = current.GetSrcCharId(); }
                catch (Exception e)
                {
                    error = "人物复制来源读取异常:" + e.GetType().Name;
                    return false;
                }
                if (sourceId < 0)
                {
                    source = current;
                    return true;
                }
                canonicalSourceId = sourceId;
            }
        }

        /// <summary>
        /// 固定模板人物(剧情 NPC、Boss、动物等)不能安全地直接转换或深拷贝：两者都会污染本体
        /// 的固定模板缓存和剧情索引。玩家发送首句或开启互动记录时通过本体智能人物创建器生成合法实体，只迁移
        /// 安全身份字段，视觉仍由前端显示原固定模板。原实体继续作为本体剧情锚点。
        /// </summary>
        private static SerializableModData EnsureCharacterProxy(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "人物副本请求不完整");
            if (!p.Get("npc_id", out int requestedId) || requestedId <= 0)
                return Fail("missing_parameter", "缺少人物参数");
            int taiwuId = DomainManager.Taiwu.GetTaiwuCharId();
            if (requestedId == taiwuId)
                return CharacterProxyResult(requestedId, requestedId, false, false, "太吾无需建立副本");
            try
            {
                if (DomainManager.Character.IsTemporaryEnemy(requestedId))
                    return Fail("character_proxy_temporary_enemy",
                        "剧情中的临时人物不能建立永久副本");
            }
            catch (Exception e)
            {
                return Indeterminate("character_proxy_temporary_classification_indeterminate",
                    "人物的临时状态无法确认:" + e.GetType().Name);
            }

            if (TryGetReplacementProxy(requestedId, out int directReplacement)
                && DomainManager.Character.TryGetElement_Objects(directReplacement,
                    out Character directReplacementCharacter)
                && IsLegalIntelligentProxy(directReplacementCharacter))
            {
                int directSourceId = requestedId;
                if (!TryGetCharacterProxySource(requestedId, out directSourceId))
                    directSourceId = requestedId;
                if (!CompletePendingLegacyMigration(context, directSourceId,
                    directReplacementCharacter, out int directMigrationFrom,
                    out string directFinalizeError))
                    return Indeterminate("character_proxy_legacy_finalize_indeterminate",
                        directFinalizeError);
                return CharacterProxyResult(directSourceId, directReplacement,
                    true, false, "已使用迁移后的人物副本",
                    directMigrationFrom > 0 ? directMigrationFrom : requestedId);
            }

            // 已经拿副本 id 再进来时保持幂等，不得复制副本的副本。
            if (TryGetCharacterProxySource(requestedId, out int existingSource))
            {
                int directProxyMigrationFrom = 0;
                Character requestedProxy;
                if (DomainManager.Character.TryGetElement_Objects(requestedId, out requestedProxy)
                    && requestedProxy != null)
                {
                    if (!IsLegalIntelligentProxy(requestedProxy))
                    {
                        return MigrateLegacyCharacterProxy(context, requestedId, existingSource,
                            requestedProxy);
                    }
                    if (!CompletePendingLegacyMigration(context, existingSource,
                        requestedProxy, out directProxyMigrationFrom,
                        out string directProxyFinalizeError))
                        return Indeterminate("character_proxy_legacy_finalize_indeterminate",
                            directProxyFinalizeError);
                }
                return CharacterProxyResult(existingSource, requestedId, true, false,
                    "已使用人物副本", directProxyMigrationFrom);
            }

            if (!TryResolveNativeCharacterSource(requestedId, out int canonicalSourceId,
                out Character source, out string sourceError))
                return Fail("character_proxy_source_invalid", sourceError);
            if (canonicalSourceId == taiwuId)
                return Fail("character_proxy_taiwu_projection",
                    "剧情中的太吾投影不能建立人物副本");
            try
            {
                if (DomainManager.Character.IsTemporaryEnemy(canonicalSourceId))
                    return Fail("character_proxy_temporary_enemy",
                        "剧情中的临时人物不能建立永久副本");
            }
            catch (Exception e)
            {
                return Indeterminate("character_proxy_temporary_classification_indeterminate",
                    "原人物的临时状态无法确认:" + e.GetType().Name);
            }
            if (source.GetTemplateId() == Config.Character.DefKey.CrossArchiveFuyuHilt
                || source.GetTemplateId() == Config.Character.DefKey.OverwrittenTaiwu)
                return Fail("character_proxy_non_person_anchor",
                    "当前显示对象是剧情地图标记，不能建立人物副本");

            // 临时智慧副本可能以一个已经建立的江湖有灵副本为源。直接回到该副本，
            // 绝不再复制副本的副本。
            if (canonicalSourceId != requestedId
                && TryGetCharacterProxySource(canonicalSourceId, out int canonicalProxySource))
            {
                if (!IsLegalIntelligentProxy(source))
                    return MigrateLegacyCharacterProxy(context, canonicalSourceId,
                        canonicalProxySource, source);
                if (!CompletePendingLegacyMigration(context, canonicalProxySource, source,
                    out int canonicalMigrationFrom, out string canonicalFinalizeError))
                    return Indeterminate("character_proxy_legacy_finalize_indeterminate",
                        canonicalFinalizeError);
                return CharacterProxyResult(canonicalProxySource, canonicalSourceId,
                    true, false, "已使用人物副本", canonicalMigrationFrom > 0
                        ? canonicalMigrationFrom : requestedId);
            }

            if (canonicalSourceId != requestedId
                && TryGetReplacementProxy(canonicalSourceId, out int canonicalReplacement)
                && DomainManager.Character.TryGetElement_Objects(canonicalReplacement,
                    out Character canonicalReplacementCharacter)
                && IsLegalIntelligentProxy(canonicalReplacementCharacter))
            {
                if (!CompletePendingLegacyMigration(context, canonicalSourceId,
                    canonicalReplacementCharacter, out int canonicalReplacementMigrationFrom,
                    out string canonicalReplacementFinalizeError))
                    return Indeterminate("character_proxy_legacy_finalize_indeterminate",
                        canonicalReplacementFinalizeError);
                return CharacterProxyResult(canonicalSourceId, canonicalReplacement,
                    true, false, "已使用迁移后的人物副本",
                    canonicalReplacementMigrationFrom > 0
                        ? canonicalReplacementMigrationFrom : requestedId);
            }

            if (TryGetPersistedCharacterProxy(canonicalSourceId, out int existingProxy))
            {
                Character mapped;
                if (!DomainManager.Character.TryGetElement_Objects(existingProxy, out mapped) || mapped == null)
                    return Indeterminate("character_proxy_missing",
                        "既有人物副本已失效，为避免重复造人已停止；请提交日志排查");
                if (!IsLegalIntelligentProxy(mapped))
                {
                    int legacySource = canonicalSourceId;
                    TryGetCharacterProxySource(existingProxy, out legacySource);
                    return MigrateLegacyCharacterProxy(context, canonicalSourceId, legacySource,
                        mapped);
                }
                int mappedIdentitySource = canonicalSourceId;
                if (!TryGetCharacterProxySource(existingProxy, out mappedIdentitySource))
                    mappedIdentitySource = canonicalSourceId;
                if (!CompletePendingLegacyMigration(context, mappedIdentitySource,
                    mapped, out int mappedMigrationFrom, out string mappedFinalizeError))
                    return Indeterminate("character_proxy_legacy_finalize_indeterminate",
                        mappedFinalizeError);
                // 上次可能在 source→proxy 已落盘、reverse 写入前中断；用既有实体补齐映射，
                // 绝不创建第二个副本。每个固定人物严格保持一对一身份。
                bool hasReverse = TryGetCharacterProxySource(existingProxy, out int reverseSource);
                if (!hasReverse || reverseSource != canonicalSourceId)
                {
                    string repairModId = string.IsNullOrWhiteSpace(_modIdStr)
                        ? RpcConst.FallbackModId : _modIdStr;
                    try
                    {
                        DomainManager.Mod.SetInt(context, repairModId,
                            CharacterProxyReverseKey(existingProxy), true, canonicalSourceId);
                    }
                    catch (Exception e)
                    {
                        return Indeterminate("character_proxy_reverse_mapping_indeterminate",
                            "既有人物副本的反向身份映射无法确认:" + e.GetType().Name);
                    }
                    if (!TryGetCharacterProxySource(existingProxy, out reverseSource)
                        || reverseSource != canonicalSourceId)
                        return Indeterminate("character_proxy_reverse_mapping_indeterminate",
                            "既有人物副本的反向身份映射尚未可靠写入");
                }
                return CharacterProxyResult(canonicalSourceId, existingProxy, true, false,
                    "已使用人物副本", mappedMigrationFrom > 0
                        ? mappedMigrationFrom
                        : (requestedId != canonicalSourceId ? requestedId : 0));
            }

            if (!source.IsCreatedWithFixedTemplate())
                return CharacterProxyResult(canonicalSourceId, canonicalSourceId,
                    false, false, "普通人物直接使用原实体",
                    requestedId != canonicalSourceId ? requestedId : 0);

            Character proxy = CreateLegalCharacterProxy(context, source, out string createError);
            if (proxy == null || proxy.GetId() <= 0 || proxy.GetId() == canonicalSourceId)
                return Indeterminate("character_proxy_create_postcondition",
                    string.IsNullOrWhiteSpace(createError) ? "人物副本接口未返回有效实体" : createError);

            int proxyId = proxy.GetId();
            if (!PersistProxyIdentity(context, canonicalSourceId, proxyId, 0,
                out string identityError))
                return Indeterminate("character_proxy_mapping_indeterminate", identityError);
            return CharacterProxyResult(canonicalSourceId, proxyId, true, true,
                "已建立人物副本", requestedId != canonicalSourceId ? requestedId : 0);
        }

        private static SerializableModData CharacterProxyResult(int originalId, int resolvedId,
            bool isProxy, bool created, string message, int migrationFromId = 0)
        {
            var result = new SerializableModData();
            result.Set("success", true);
            result.Set("status", "executed");
            result.Set("message", message ?? "人物身份已解析");
            result.Set("original_npc_id", originalId);
            result.Set("resolved_npc_id", resolvedId);
            result.Set("is_proxy", isProxy);
            result.Set("proxy_created", created);
            result.Set("migration_from_npc_id", migrationFromId);
            short displayTemplateId = -1;
            if (isProxy)
            {
                int displaySourceId = originalId;
                if (TryGetCharacterProxySource(resolvedId, out int persistedSourceId))
                    displaySourceId = persistedSourceId;
                if (DomainManager.Character.TryGetElement_Objects(displaySourceId,
                    out Character displaySource) && displaySource != null)
                    displayTemplateId = displaySource.GetTemplateId();
            }
            result.Set("display_template_id", (int)displayTemplateId);
            return result;
        }

        private static int ResolveCharacterProxyId(int npcId)
        {
            if (npcId <= 0) return npcId;
            if (TryGetReplacementProxy(npcId, out int replacementId)) return replacementId;
            if (TryGetCharacterProxySource(npcId, out _)) return npcId;
            if (!TryGetPersistedCharacterProxy(npcId, out int proxyId)) return npcId;
            return TryGetReplacementProxy(proxyId, out replacementId) ? replacementId : proxyId;
        }

        private static SerializableModData JoinTeam(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "入队请求不完整");
            int npcId = 0;
            if (!p.Get("npc_id", out npcId) || npcId <= 0) return Fail("missing_parameter", "缺少参数");
            int requestedId = npcId;
            npcId = ResolveCharacterProxyId(npcId);
            if (npcId == requestedId)
            {
                Character requested;
                if (DomainManager.Character.TryGetElement_Objects(requestedId, out requested)
                    && requested != null && requested.IsCreatedWithFixedTemplate())
                {
                    SerializableModData proxy = EnsureCharacterProxy(context, p);
                    bool proxyOk = false;
                    proxy?.Get("success", out proxyOk);
                    if (!proxyOk) return proxy;
                    proxy.Get("resolved_npc_id", out npcId);
                    if (npcId <= 0) return Indeterminate("character_proxy_resolution_indeterminate",
                        "人物副本已经准备，但入队目标身份无法确认");
                }
            }
            if (npcId == DomainManager.Taiwu.GetTaiwuCharId()) return Fail("is_taiwu", "太吾本人无需入队");

            Character npc = null;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null)
                return Fail("character_not_found", "NPC 失效");
            if (npc.GetKidnapperId() >= 0)
                return Fail("kidnapped", "其仍被绑架，获释后才能加入队伍");
            if (DomainManager.Organization.GetPrisonerSect(npcId) >= 0)
                return Fail("imprisoned", "其仍被关押，获释后才能加入队伍");

            if (DomainManager.Taiwu.IsInGroup(npcId))
            {
                if (!MarkCharacterProxyEverJoined(context, npcId,
                        out string joinedStateError))
                    return Indeterminate("character_proxy_join_state_indeterminate",
                        joinedStateError);
                var already = new SerializableModData(); already.Set("success", true);
                already.Set("status", "executed"); already.Set("message", "已在队伍中");
                already.Set("original_npc_id", requestedId); already.Set("resolved_npc_id", npcId);
                return already;
            }
            string prepareError;
            if (!PrepareJoinEquipmentPlan(context, npcId, out prepareError))
                return Indeterminate("join_team_equipment_plan_indeterminate", "旧装备方案清理状态无法确认:" + prepareError);
            try { DomainManager.Taiwu.JoinGroup(context, npcId); }
            catch (Exception e)
            {
                // 原生 JoinGroup 在队伍位写入后的通知、功法装配等收尾也可能抛错。
                // 以后置队伍状态为准，已落地就不可再让模型用新 operationId 重复执行。
                bool joined = false;
                try { joined = DomainManager.Taiwu.IsInGroup(npcId); }
                catch { }
                if (!joined)
                    return Indeterminate("join_team_indeterminate", "入队及组织归属状态无法确认:" + e.GetType().Name);
            }

            if (!DomainManager.Taiwu.IsInGroup(npcId))
                return Indeterminate("join_team_postcondition_indeterminate", "入队写 API 已返回但队伍位未满足");
            if (!MarkCharacterProxyEverJoined(context, npcId,
                    out string persistedJoinStateError))
                return Indeterminate("character_proxy_join_state_indeterminate",
                    persistedJoinStateError);
            var done = new SerializableModData();
            done.Set("success", true); done.Set("status", "executed"); done.Set("message", "已加入太吾队伍");
            done.Set("original_npc_id", requestedId);
            done.Set("resolved_npc_id", npcId);
            return done;
        }

        private static bool PrepareJoinEquipmentPlan(DataContext context, int npcId, out string error)
        {
            error = null;
            try
            {
                // JoinGroup 稍后会再次调用；成功时原生实现会自行删除记录，第二次成为安全空操作。
                DomainManager.Taiwu.ApplyGroupCharEquipmentPlan(context, npcId);
                return true;
            }
            catch (Exception applyError)
            {
                try
                {
                    // RemoveManualChangeEquipGroupChar 只有在 manual 集合中才会删除装备记录。
                    // 先登记再以 keepRecord:false 移除，是本体公开 API 提供的最小安全清理路径。
                    DomainManager.Taiwu.AddManualChangeEquipGroupChar(context, npcId);
                    DomainManager.Taiwu.RemoveManualChangeEquipGroupChar(context, npcId, keepRecord: false);
                    return true;
                }
                catch (Exception discardError)
                {
                    error = applyError.GetType().Name + "→" + discardError.GetType().Name;
                    return false;
                }
            }
        }

        // 男媒女约:撮合 aId、bId 两个角色成婚(太吾做媒)。前置硬校验缺一即如实回拒(否则 ApplyBecomeHusbandOrWife 外层守卫会静默 no-op=假报成功);
        // 全程 try/catch:成婚牵动继子女/养子女连带、改门派归属、月度AI等,异常数据下可能抛,绝不冲垮后端(传功卡死同源)。
        private static SerializableModData Matchmake(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "说媒请求不完整");
            int aId = 0, bId = 0;
            if (!p.Get("a_id", out aId) || !p.Get("b_id", out bId)) return Fail("missing_parameter", "缺少参数");
            if (aId <= 0 || bId <= 0 || aId == bId) return Fail("bad_args", "参数无效");

            Character a, b;
            if (!DomainManager.Character.TryGetElement_Objects(aId, out a) || a == null ||
                !DomainManager.Character.TryGetElement_Objects(bId, out b) || b == null)
                return Fail("character_not_found", "角色失效");

            // 不再硬卡异性:引擎 AllowAddingHusbandOrWifeRelation 才是权威婚配门(只管血亲/已有配偶);
            // 且本 mod 的 set_relation spouse / relate_npc spouse 两条路径本就允许同性,做媒不应独家不一致。
            if (a.GetAgeGroup() != 2 || b.GetAgeGroup() != 2) return Fail("not_adult", "须双方皆已成年");
            if (a.IsCompletelyInfected() || b.IsCompletelyInfected()) return Fail("infected", "有人已堕相枢魔道,不可成婚");
            if (!RelationTypeHelper.AllowAddingHusbandOrWifeRelation(aId, bId))
                return Fail("not_allowed", "二人不可成婚(已有配偶/直系血亲/已是夫妻)");

            try { global::GameData.Domains.TaiwuEvent.EventHelper.EventHelper.ApplyRelationBecomeHusbandOrWife(a, b, true); }
            catch (Exception e) { return Indeterminate("matchmake_indeterminate", "婚配及其连带关系状态无法确认:" + e.GetType().Name); }

            if (!DomainManager.Character.HasRelation(aId, bId, (ushort)1024))
                return Indeterminate("matchmake_postcondition_indeterminate", "成婚写 API 已返回但夫妻关系位未满足");
            var done = new SerializableModData();
            done.Set("success", true); done.Set("status", "executed"); done.Set("message", "已结为夫妻");
            return done;
        }

        // 队友判定:供前端"自定义人设"入口前置校验。IsInGroup 对失效角色会抛(GetElement_Objects),故 try/catch。
        // 约定:是队友 → success=true;非队友/失效 → success=false 且 message 为提示语(前端据此弹"需先成为队友")。
        private static SerializableModData IsTeammate(DataContext context, SerializableModData p)
        {
            if (p == null) return Fail("invalid_request", "请求不完整");
            int npcId = 0;
            if (!p.Get("npc_id", out npcId) || npcId <= 0) return Fail("missing_parameter", "缺少参数");
            npcId = ResolveCharacterProxyId(npcId);
            bool inGroup;
            try { inGroup = DomainManager.Taiwu.IsInGroup(npcId); }
            catch (Exception e) { return Fail("query_failed", "队友判定失败:" + e.GetType().Name); }
            if (!inGroup) return Fail("not_teammate", "需先让其加入太吾队伍(对话中入队)才能自定义人设");
            var done = new SerializableModData();
            done.Set("success", true); done.Set("message", "是队友");
            return done;
        }

        // 杀人:应太吾之请,NPC 取目标性命。硬校验:施法者精纯不得低于目标(GetConsummateLevel，相等可成);合理性由 AI 依人设自判(提示词约束)。
        // 权威杀敌包装牵动死亡、归因、生平、秘密、葬礼/继承/关系移除等；异常时终态 unknown，不可加害太吾。
        private static bool IsActorRestrained(Character actor, int actorId)
        {
            return actor == null || actorId <= 0
                || actor.GetKidnapperId() >= 0
                || DomainManager.Organization.GetPrisonerSect(actorId) >= 0;
        }

        private static SerializableModData WithCharacterState(SerializableModData response, Character character)
        {
            if (response == null || character == null) return response;
            try
            {
                Location raw = character.GetLocation();
                Location scene = GetPhysicalSceneLocation(character);
                // Scalar state only: enough to distinguish a carrier, prison, travel and
                // story ownership without exporting the save, dialogue or custom persona.
                response.Set("character_state", "char_id=" + character.GetId()
                    + " raw_area=" + raw.AreaId + " raw_block=" + raw.BlockId
                    + " scene_area=" + scene.AreaId + " scene_block=" + scene.BlockId
                    + " kidnapper=" + character.GetKidnapperId()
                    + " leader=" + character.GetLeaderId()
                    + " prison_sect=" + DomainManager.Organization.GetPrisonerSect(character.GetId())
                    + " external_state=" + character.GetExternalRelationState()
                    + " cross_area=" + character.IsCrossAreaTraveling());
            }
            catch (Exception e)
            {
                // Diagnostics must not turn a definite refusal into an uncertain mutation.
                response.Set("character_state", "char_id=" + character.GetId()
                    + " state_read_error=" + e.GetType().Name);
            }
            return response;
        }

        private static Location GetPhysicalSceneLocation(Character character)
        {
            if (character == null) return Location.Invalid;
            bool resolved = CharacterSceneAnchorResolver.TryResolve(character.GetId(), id =>
            {
                Character current;
                if (!DomainManager.Character.TryGetElement_Objects(id, out current)
                    || current == null) return null;
                int carrier = current.GetKidnapperId();
                if (carrier >= 0) return carrier;
                if (DomainManager.Organization.GetPrisonerSect(id) >= 0) return id;
                int leader = current.GetLeaderId();
                if (leader > 0 && leader != id)
                {
                    // Special followers may have no independent map location. Require
                    // actual native membership, not merely a leftover leader field.
                    bool follows = DomainManager.Character.IsSpecialGroupMember(current);
                    if (!follows)
                    {
                        try { follows = DomainManager.Character.GetGroup(leader).Contains(id); }
                        catch (KeyNotFoundException) { }
                    }
                    if (!follows && leader == DomainManager.Taiwu.GetTaiwuCharId())
                        follows = DomainManager.Taiwu.IsInGroup(id);
                    if (follows) return leader;
                }
                return id;
            }, out int anchorId);
            Character anchor;
            if (!resolved || !DomainManager.Character.TryGetElement_Objects(anchorId, out anchor)
                || anchor == null) return Location.Invalid;
            Location direct = anchor.GetLocation();
            if (direct.IsValid()) return direct;
            if (anchor.IsActiveExternalRelationState(32uL))
            {
                sbyte prisonSect = DomainManager.Organization.GetPrisonerSect(anchorId);
                if (prisonSect >= 0)
                {
                    var prison = DomainManager.Organization.GetSettlementByOrgTemplateId(prisonSect);
                    if (prison != null) return prison.GetLocation();
                }
            }
            // A travel/adventure map anchor is not proof of physical presence in a tile.
            return Location.Invalid;
        }

        private static bool SameValidLocation(Character actor, Character target)
        {
            if (actor == null || target == null) return false;
            Location actorLocation = GetPhysicalSceneLocation(actor);
            Location targetLocation = GetPhysicalSceneLocation(target);
            if (actorLocation.IsValid() && targetLocation.IsValid()
                && actorLocation.Equals(targetLocation)) return true;

            // 新建的固定人物副本可能尚未取得 Location，但初见原人物的现场是确定的。
            // 该兜底仅允许副本与太吾彼此当面，不会凭空把副本与第三方人物判在一起；
            // 首次成功入队后状态位关闭兜底，离队即恢复严格实时位置判断。
            int taiwuId;
            try { taiwuId = DomainManager.Taiwu.GetTaiwuCharId(); }
            catch { return false; }
            int actorId = actor.GetId();
            int targetId = target.GetId();
            int proxyId = actorId == taiwuId ? targetId
                : targetId == taiwuId ? actorId : 0;
            return proxyId > 0 && IsCharacterProxyAwaitingFirstJoin(proxyId);
        }

        private static bool TryGetPhysicalInteractionPair(string op,
            SerializableModData parameter, out int actorId, out int targetId)
        {
            actorId = 0;
            targetId = 0;
            if (parameter == null || string.IsNullOrWhiteSpace(op)) return false;
            int recipient = 0, taiwu = 0;
            switch (op)
            {
                case "givesilver":
                case "giveitem":
                    parameter.Get("npc", out actorId);
                    parameter.Get("recipient", out recipient);
                    parameter.Get("taiwu", out taiwu);
                    targetId = recipient > 0 ? recipient : taiwu;
                    return actorId > 0 && targetId > 0;
                case "teachlife":
                case "writebook":
                    parameter.Get("npc", out actorId);
                    parameter.Get("recipient", out recipient);
                    parameter.Get("taiwu", out taiwu);
                    targetId = recipient > 0 ? recipient : taiwu;
                    return actorId > 0 && targetId > 0;
                case "taiwu_give_item":
                case "taiwu_teach":
                    parameter.Get("taiwu", out actorId);
                    parameter.Get("npc", out targetId);
                    return actorId > 0 && targetId > 0;
                case "barter":
                case "event_teach":
                case "event_gift":
                case "spend_night":
                    parameter.Get("a", out actorId);
                    parameter.Get("b", out targetId);
                    return actorId > 0 && targetId > 0;
                case "steal":
                    parameter.Get("thief", out actorId);
                    parameter.Get("victim", out targetId);
                    return actorId > 0 && targetId > 0;
                case "poison":
                    parameter.Get("actor", out actorId);
                    parameter.Get("target", out targetId);
                    int monthlyOnlyPurity = 0;
                    parameter.Get("monthly_only_purity", out monthlyOnlyPurity);
                    return actorId > 0 && targetId > 0
                        && MonthlyDangerActionPolicy.RequiresCoLocation(op, monthlyOnlyPurity);
                case "heal":
                    parameter.Get("healer", out actorId);
                    parameter.Get("target", out targetId);
                    return actorId > 0 && targetId > 0;
                case "detox":
                case "regulate_breath":
                    parameter.Get("healer", out actorId);
                    parameter.Get("target", out targetId);
                    return actorId > 0 && targetId > 0;
                case "trade":
                    parameter.Get("npc", out actorId);
                    parameter.Get("taiwu", out targetId);
                    return actorId > 0 && targetId > 0;
                default:
                    return false;
            }
        }

        private static SerializableModData Kill(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "行凶请求不完整");
            int npcId = 0, targetId = 0, monthlyOnlyPurity = 0;
            if (!p.Get("npc_id", out npcId) || !p.Get("target_id", out targetId)) return Fail("missing_parameter", "缺少参数");
            p.Get("monthly_only_purity", out monthlyOnlyPurity);
            if (npcId <= 0 || targetId <= 0 || npcId == targetId) return Fail("bad_args", "参数无效");
            if (targetId == DomainManager.Taiwu.GetTaiwuCharId()) return Fail("is_taiwu", "不可加害太吾");

            Character npc, target;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null ||
                !DomainManager.Character.TryGetElement_Objects(targetId, out target) || target == null)
                return Fail("character_not_found", "目标已不在世或失效");
            if (IsActorRestrained(npc, npcId))
                return Fail("actor_restrained", "此人正被囚禁或绑架，无法行凶");
            if (MonthlyDangerActionPolicy.RequiresCoLocation("kill", monthlyOnlyPurity)
                && !SameValidLocation(npc, target))
                return Fail("not_co_located", "双方当前不在同一有效地块，不能当面行凶");

            sbyte npcLv = npc.GetConsummateLevel(), tgtLv = target.GetConsummateLevel();
            // 精纯门槛是 mod 自加平衡闸(反编译核对:游戏 MakeCharacterDead 本身不校验精纯)。放宽为【严格低于才拒】——
            // 精纯相等(尤其多人并列满级 18)也可成,减少"高手云集时本可成立的杀伐被误拒"导致的故事/现实出入。
            if (npcLv < tgtLv) return Fail("not_strong_enough", "你的精纯不及对方(" + npcLv + "<" + tgtLv + "),奈何他不得");

            // 杀人战利品是既有契约：在死亡/继承接管背包前，把目标背包中基础价值
            // 最高的一件真实物品转给行凶者。没有可夺物品时仍可完成取命。
            ItemKey lootKey = default(ItemKey);
            string lootName = null;
            bool hasLootCandidate = false;
            try
            {
                int bestValue = int.MinValue;
                var inventory = target.GetInventory();
                if (inventory != null && inventory.Items != null)
                    foreach (var pair in inventory.Items)
                    {
                        if (pair.Value <= 0) continue;
                        ItemKey candidate = pair.Key;
                        if (!ItemTemplateHelper.IsTransferable(candidate.ItemType, candidate.TemplateId)
                            || ItemTemplateHelper.GetBaseValue(candidate.ItemType, candidate.TemplateId) == 0
                            || ItemTemplateHelper.GetIcon(candidate.ItemType, candidate.TemplateId) == null
                            || ItemTemplateHelper.IsSpecial(candidate.ItemType, candidate.TemplateId))
                            continue;
                        int value = ItemTemplateHelper.GetBaseValue(candidate.ItemType, candidate.TemplateId);
                        if (!hasLootCandidate || value > bestValue)
                        {
                            hasLootCandidate = true;
                            bestValue = value;
                            lootKey = candidate;
                        }
                    }
                if (hasLootCandidate)
                    lootName = ItemDisplayName(lootKey) ?? "一件财物";
            }
            catch
            {
                hasLootCandidate = false;
                lootName = null;
            }

            bool looted = false;
            if (hasLootCandidate)
            {
                try
                {
                    TransferInventoryChecked(context, target, npc, lootKey, 1,
                        TransferIntent.Loot);
                    looted = true;
                }
                catch (TransferPreconditionException)
                {
                    // 战利品不是取命的硬前置；转移失败不伪报获得，也不阻断权威死亡链。
                    looted = false;
                    lootName = null;
                }
                catch (Exception e)
                {
                    return Indeterminate("kill_loot_indeterminate",
                        "战利品预取发生无法确认的部分写入，已停止取命:" + e.GetType().Name);
                }
            }
            bool RollbackLoot()
            {
                if (!looted) return true;
                try
                {
                    TransferInventoryChecked(context, npc, target, lootKey, 1,
                        TransferIntent.Compensation);
                    looted = false;
                    lootName = null;
                    return true;
                }
                catch { return false; }
            }

            // 使用游戏权威的私下杀敌语义：它会设置 CharacterDeathInfo.KillerId、写入
            // 杀手/死者双方生平记录，并登记私下杀人秘密；不可退回裸 MakeCharacterDead。
            try { DomainManager.Character.CombatResultHandle_KillEnemy(context, npc, target, false); }
            catch (Exception e)
            {
                bool targetAlive;
                try { targetAlive = DomainManager.Character.IsCharacterAlive(targetId); }
                catch
                {
                    return Indeterminate("kill_indeterminate",
                        "死亡状态与战利品归属均无法确认:" + e.GetType().Name);
                }
                if (targetAlive)
                {
                    bool rolledBack = RollbackLoot();
                    return Indeterminate("kill_indeterminate",
                        "权威取命未完成，目标仍存活；"
                        + (rolledBack ? "预取战利品已回滚:" : "预取战利品回滚状态无法确认:")
                        + e.GetType().Name);
                }
                return Indeterminate("kill_indeterminate",
                    "目标死亡已经落地，但死亡后的生平、秘密或继承连带状态无法确认:"
                    + e.GetType().Name);
            }
            bool aliveAfterKill;
            try { aliveAfterKill = DomainManager.Character.IsCharacterAlive(targetId); }
            catch
            {
                return Indeterminate("kill_postcondition_unknown", "权威取命返回后无法确认目标生死");
            }
            if (aliveAfterKill)
            {
                bool rolledBack = RollbackLoot();
                return Indeterminate("kill_postcondition_failed",
                    "权威取命返回但目标仍存活；"
                    + (rolledBack ? "预取战利品已回滚" : "预取战利品回滚状态无法确认"));
            }

            var done = new SerializableModData();
            done.Set("success", true); done.Set("status", "executed");
            done.Set("message", looted ? ("已取其性命，并夺得「" + lootName + "」") : "已取其性命");
            if (looted) done.Set("loot_name", lootName);
            return done;
        }

        // 绑人/擒拿:普通对话仍要求真实绳索；过月 monthly_only_purity=1 时只以精纯为
        // 玩法门槛，走本体 CombatResultHandle_KidnapEnemy 自动取得绳索并完整登记权威记录。
        private static SerializableModData Capture(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "擒拿请求不完整");
            int npcId = 0, targetId = 0, monthlyOnlyPurity = 0;
            if (!p.Get("npc_id", out npcId) || !p.Get("target_id", out targetId)) return Fail("missing_parameter", "缺少参数");
            p.Get("monthly_only_purity", out monthlyOnlyPurity);
            if (npcId <= 0 || targetId <= 0 || npcId == targetId) return Fail("bad_args", "参数无效");
            if (targetId == DomainManager.Taiwu.GetTaiwuCharId()) return Fail("is_taiwu", "不可擒拿太吾");

            Character npc, target;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null ||
                !DomainManager.Character.TryGetElement_Objects(targetId, out target) || target == null)
                return Fail("character_not_found", "目标已不在世或失效");
            if (IsActorRestrained(npc, npcId))
                return Fail("actor_restrained", "此人正被囚禁或绑架，无法擒拿他人");
            if (MonthlyDangerActionPolicy.RequiresCoLocation("capture", monthlyOnlyPurity)
                && !SameValidLocation(npc, target))
                return Fail("not_co_located", "双方当前不在同一有效地块，不能当面擒拿");

            sbyte npcLv = npc.GetConsummateLevel(), tgtLv = target.GetConsummateLevel();
            // 同杀人:精纯门槛为 mod 自加闸(反编译核对游戏 AddKidnappedCharacter 不校验精纯),放宽为严格低于才拒(相等可成)
            if (npcLv < tgtLv) return Fail("not_strong_enough", "你的精纯不及对方(" + npcLv + "<" + tgtLv + "),拿他不下");
            if (target.GetKidnapperId() >= 0
                || DomainManager.Organization.GetPrisonerSect(targetId) >= 0)
                return Fail("already_kidnapped", "其人已被掳走或囚禁");

            if (monthlyOnlyPurity == 1)
            {
                try
                {
                    // 最新本体包装会在没有绳索时通过 GetInventoryRope 自动取得合适绳索，
                    // 并一次性完成掳获、失踪通知、生平与秘密；因此过月没有物品门槛。
                    DomainManager.Character.CombatResultHandle_KidnapEnemy(context, npc, target, false);
                    if (target.GetKidnapperId() != npcId)
                        return Indeterminate("monthly_capture_postcondition_indeterminate",
                            "过月擒拿权威 API 已返回但掳获者状态未满足");
                }
                catch (Exception e)
                {
                    return Indeterminate("monthly_capture_indeterminate",
                        "过月擒拿权威状态无法确认:" + e.GetType().Name);
                }
                var monthlyDone = new SerializableModData();
                monthlyDone.Set("success", true);
                monthlyDone.Set("status", "executed");
                monthlyDone.Set("message", "已将其擒下");
                return monthlyDone;
            }

            // 背包找真绳子(不自动造):ItemType==12 且 templateId 82-90(GetInventoryRope 没绳会自动造,故不用它)
            ItemKey ropeKey = default; bool hasRope = false;
            try
            {
                var inv = npc.GetInventory();
                if (inv != null && inv.Items != null)
                    foreach (var kv in inv.Items)
                    { var k = kv.Key; if (kv.Value > 0 && k.ItemType == (sbyte)12 && k.TemplateId >= 82 && k.TemplateId <= 90) { ropeKey = k; hasRope = true; break; } }
            }
            catch { }
            if (!hasRope) return Fail("no_rope", "你手边没有绳索,绑缚不得(须先备一根绳子)");

            try
            {
                // b24185552 CharacterDomain.CombatResultHandle_KidnapEnemy 的权威顺序。
                // 不能直接调用该包装器：GetInventoryRope 会在没有绳索时自动生成，而本
                // Mod 的契约要求施术者确实持有并消耗上面选中的真实绳索。
                var lifeRecords = DomainManager.LifeRecord.GetLifeRecordCollection();
                int currDate = DomainManager.World.GetCurrDate();
                Location location = npc.GetLocation();
                if (DomainManager.Character.IsTaiwuPeople(npcId))
                {
                    var notifications = DomainManager.World.GetMonthlyNotificationCollection();
                    notifications.AddDisappear(targetId, location);
                }
                DomainManager.Character.AddKidnappedCharacter(context, npcId, targetId, ropeKey);
                lifeRecords.AddKidnapInPrivate(npcId, currDate, targetId, location,
                    ropeKey.ItemType, ropeKey.TemplateId);
                var secrets = DomainManager.Information.GetSecretInformationCollection();
                int dataOffset = secrets.AddKidnapInPrivate(npcId, targetId);
                DomainManager.Information.AddSecretInformation(context, dataOffset);
                if (target.GetKidnapperId() != npcId)
                    return Indeterminate("capture_postcondition_indeterminate",
                        "擒拿及权威记录 API 已返回但掳获者状态未满足");
            }
            catch (Exception e) { return Indeterminate("capture_indeterminate", "绑架、绳索消耗或权威记录状态无法确认:" + e.GetType().Name); }
            var done = new SerializableModData();
            done.Set("success", true); done.Set("status", "executed"); done.Set("message", "已将其绑缚擒下");
            return done;
        }

        /// <summary>
        /// A dialogue combat may only consume the native event that is still displaying the same NPC.
        /// This check must happen before combat dispatch: closing an unrelated event after combat has
        /// already started would leave the event and combat state machines irreconcilable.
        /// </summary>
        private static bool TryValidateCombatInteractionAnchor(int targetId, out bool finishAfterDispatch,
            out string code, out string message)
        {
            finishAfterDispatch = false;
            code = null;
            message = null;
            var eventDomain = DomainManager.TaiwuEvent;
            if (eventDomain == null || !eventDomain.IsShowingEvent) return true;

            var showing = eventDomain.ShowingEvent;
            if (showing == null || showing.IsEmpty || showing.EventConfig == null || showing.ArgBox == null)
            {
                code = "combat_interaction_invalid";
                message = "当前人物互动状态不完整，已拒绝进入战斗";
                return false;
            }

            string targetRoleKey = showing.EventConfig.TargetRoleKey;
            int eventTargetId = -1;
            if (string.IsNullOrEmpty(targetRoleKey) || !showing.ArgBox.Get(targetRoleKey, ref eventTargetId))
            {
                code = "combat_interaction_target_missing";
                message = "当前互动无法确认战斗对象，已拒绝进入战斗";
                return false;
            }
            if (eventTargetId != targetId)
            {
                code = "combat_interaction_target_changed";
                message = "当前互动对象已经改变，已拒绝进入战斗";
                return false;
            }

            finishAfterDispatch = true;
            return true;
        }

        /// <summary>
        /// 对话战斗的唯一权威提交入口。ExecuteJournaled 已先验证并持久预写
        /// (worldId,taiwuId,operationId)；这里在真正进入战斗前再次读取活动角色、
        /// 绑架状态与现场，堵住前端确认至后端执行之间的移动/死亡竞态。
        /// </summary>
        private static SerializableModData StartCombat(DataContext context, SerializableModData p)
        {
            if (context == null || p == null)
                return Fail("invalid_request", "战斗请求不完整");
            int targetId = 0, combatConfig = -1;
            if (!p.Get("target_id", out targetId) || !p.Get("config", out combatConfig))
                return Fail("missing_parameter", "战斗请求缺少目标或配置");
            if (targetId <= 0 || combatConfig < 0 || combatConfig > 2)
                return Fail("bad_args", "战斗目标或配置无效");

            int taiwuId;
            Character taiwu, target;
            bool finishInteractionAfterDispatch;
            try
            {
                taiwuId = DomainManager.Taiwu.GetTaiwuCharId();
                if (taiwuId <= 0 || targetId == taiwuId)
                    return Fail("invalid_target", "不可向太吾本人发起战斗");
                // 死亡流程会从活动角色集合 RemoveElement_Objects；因此只有仍在
                // Objects 中的目标才同时满足“存在且存活”。
                if (!DomainManager.Character.TryGetElement_Objects(taiwuId, out taiwu) || taiwu == null
                    || !DomainManager.Character.TryGetElement_Objects(targetId, out target) || target == null)
                    return Fail("combat_target_unavailable", "战斗目标已死亡、失效或不在当前世界");
                if (target.GetKidnapperId() >= 0
                    || DomainManager.Organization.GetPrisonerSect(targetId) >= 0)
                    return Fail("combat_target_kidnapped", "被绑架角色不能进入对话战斗");
                if (!IsAtTaiwuScene(targetId, taiwuId, target, taiwu))
                    return Fail("combat_target_not_present", "战斗目标已离开太吾当前现场");
                string anchorCode, anchorMessage;
                if (!TryValidateCombatInteractionAnchor(targetId, out finishInteractionAfterDispatch,
                        out anchorCode, out anchorMessage))
                    return Fail(anchorCode, anchorMessage);
                if (combatConfig == 2)
                {
                    var completion = DomainManager.TaiwuEvent.GetEvent(DialogueDeathCombatCompleteEventGuid);
                    if (completion == null || completion.IsEmpty)
                        return Fail("combat_completion_event_unavailable", "本体死斗结算事件尚未加载，已拒绝进入战斗");
                }
            }
            catch (Exception e)
            {
                return Fail("combat_validation_failed", "无法可靠复核战斗现场:" + e.GetType().Name);
            }

            try
            {
                if (combatConfig == 2)
                {
                    // Match b24185552's native no-guard deathmatch lifecycle. The listener must be
                    // registered before the old interaction is exited. ViewCombatResult first settles
                    // loot, then triggers CombatOver; only the completion event may remove the loser.
                    var combatArgs = new global::GameData.Domains.TaiwuEvent.EventArgBox();
                    combatArgs.Set("CharacterId", targetId);
                    global::GameData.Domains.TaiwuEvent.EventHelper.EventHelper.StartCombat(
                        targetId, (short)2, DialogueDeathCombatCompleteEventGuid, combatArgs, true);
                }
                else
                {
                    // b24185552 EventHelper.StartCombat 的普通战斗生命周期在 CombatEntry 前会
                    // RecordCharacterEnterCombat。旧实现直接走 GM 入口，漏了这一步且没有消费
                    // 原人物互动；切磋/相搏结束后原事件仍占着状态机，表现为游戏卡死。
                    DomainManager.TaiwuEvent.RecordCharacterEnterCombat();
                    DomainManager.Combat.GmCmd_FightCharacter(context, targetId, (short)combatConfig);
                }
                // 三种战斗都从同一个人物互动锚点进入，派发后必须统一结束旧互动。
                // 目标核对已在派发前完成，不能只为死斗关闭而让切磋/相搏留下悬挂事件。
                if (finishInteractionAfterDispatch)
                {
                    DomainManager.TaiwuEvent.ToEvent(string.Empty);
                    if (DomainManager.TaiwuEvent.IsShowingEvent)
                        throw new InvalidOperationException("dialogue combat interaction did not close");
                }
            }
            catch (Exception e)
            {
                return Indeterminate("start_combat_indeterminate",
                    "战斗入口可能已部分提交，不得自动重试:" + e.GetType().Name);
            }

            var done = new SerializableModData();
            done.Set("success", true);
            done.Set("status", "executed");
            done.Set("message", "已进入战斗");
            return done;
        }

        /// <summary>
        /// 只读返回太吾 Character 上的完整已学武学/技艺 ID。b24185552 的权威来源分别是
        /// Character.GetLearnedCombatSkills / GetLearnedLifeSkills；前端菜单 DisplayData 只作
        /// 显示，不再承担“太吾是否真的会”这一动作前置判断。
        /// </summary>
        private static SerializableModData QueryTaiwuSkills(DataContext context, SerializableModData p)
        {
            var ret = new SerializableModData();
            try
            {
                int requestedTaiwuId = 0;
                p?.Get("taiwu_id", out requestedTaiwuId);
                int taiwuId = DomainManager.Taiwu.GetTaiwuCharId();
                if (taiwuId <= 0 || (requestedTaiwuId > 0 && requestedTaiwuId != taiwuId))
                    return Fail("taiwu_changed", "当前太吾身份已变化");
                Character taiwu;
                if (!DomainManager.Character.TryGetElement_Objects(taiwuId, out taiwu) || taiwu == null)
                    return Fail("taiwu_unavailable", "当前太吾不存在或尚未载入");

                var combatIds = taiwu.GetLearnedCombatSkills();
                var lifeIds = new List<string>();
                var learnedLifeSkills = taiwu.GetLearnedLifeSkills();
                if (learnedLifeSkills != null)
                    foreach (var learned in learnedLifeSkills)
                        lifeIds.Add(learned.SkillTemplateId.ToString(CultureInfo.InvariantCulture));
                ret.Set("success", true);
                ret.Set("message", "ok");
                ret.Set("combat_ids", combatIds == null ? string.Empty
                    : string.Join(",", combatIds.ConvertAll(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()));
                ret.Set("life_ids", string.Join(",", lifeIds.ToArray()));
                return ret;
            }
            catch (Exception e)
            {
                return Fail("query_taiwu_skills_failed", "未能读取太吾已学功法:" + e.GetType().Name);
            }
        }

        public override void Dispose()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            NativeInteractionMutationAudit.Reset();
        }

        // 在后端逻辑线程执行;可安全读写 DomainManager.Character / Taiwu 等真实游戏数据
        private static SerializableModData Ping(DataContext context, SerializableModData parameter)
        {
            var ret = new SerializableModData();
            try
            {
                int nonce = 0;
                parameter?.Get("nonce", out nonce);   // 真签名为 out;Get 返回 bool=key 是否存在

                ret.Set("success", true);
                ret.Set("status", "pong");
                ret.Set("message", "江湖有灵后端 RPC 已通");
                ret.Set("echo_nonce", nonce);
            }
            catch (Exception ex)
            {
                ret.Set("success", false);
                ret.Set("status", "exception");
                ret.Set("message", "Ping 执行失败: " + ex.GetType().Name);
            }
            return ret;
        }

        // 建立关系(挚友/结义/师徒)。DataContext 由引擎注入,在域线程内原子执行原生 Action。
        private static SerializableModData ExecuteRelation(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "关系请求不完整");

            int npcId = 0, taiwuId = 0; string action = null;
            if (!p.Get("npc_id", out npcId) || !p.Get("taiwu_id", out taiwuId) || !p.Get("relation_action", out action))
                return Fail("missing_parameter", "缺少参数");

            if (DomainManager.Taiwu.GetTaiwuCharId() != taiwuId)
                return Fail("taiwu_changed", "太吾身份已变化");

            Character npc = null, taiwu = null;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out npc)
                || !DomainManager.Character.TryGetElement_Objects(taiwuId, out taiwu)
                || taiwu != DomainManager.Taiwu.GetTaiwu())
                return Fail("character_not_found", "角色失效");

            int relationHolderId = npcId;
            int relationTargetId = taiwuId;
            ushort relationType = 0;
            switch (action)
            {
                case "best_friend": relationType = 8192; break;
                case "sworn_sibling": relationType = 512; break;
                case "mentor":
                case "apprentice": relationType = 2048; break;
                case "take_disciple":
                    relationType = 2048; relationHolderId = taiwuId; relationTargetId = npcId; break;
                case "adoptive_parent":
                    if (!ValidateAdoptiveRelation(npcId, npc, taiwuId, taiwu, true,
                        out string adoptParentCode, out string adoptParentReason))
                        return Fail(adoptParentCode, adoptParentReason);
                    break;
                case "adoptive_child":
                    if (!ValidateAdoptiveRelation(npcId, npc, taiwuId, taiwu, false,
                        out string adoptChildCode, out string adoptChildReason))
                        return Fail(adoptChildCode, adoptChildReason);
                    break;
                case "lover":
                    if (DomainManager.Character.HasRelation(npcId, taiwuId, (ushort)16384))
                        return Fail("already", HasMutualAdoration(npcId, taiwuId)
                            ? "双方已经两情相悦"
                            : "此人已经在单方面爱慕太吾");
                    if (!RelationTypeHelper.AllowAddingAdoredRelation(npcId, taiwuId))
                        return Fail("relation_not_allowed", "本体关系或人物特性不允许此人爱慕太吾");
                    break;
                case "spouse":
                    if (DomainManager.Character.HasRelation(npcId, taiwuId, (ushort)1024))
                        return Fail("already", "双方已经是夫妻");
                    if (npc.GetAgeGroup() != 2 || taiwu.GetAgeGroup() != 2)
                        return Fail("not_adult", "双方并非都已成年，不能成婚");
                    if (npc.IsCompletelyInfected() || taiwu.IsCompletelyInfected())
                        return Fail("infected", "有人已完全入魔，不能成婚");
                    if (!RelationTypeHelper.AllowAddingHusbandOrWifeRelation(taiwuId, npcId))
                        return Fail("relation_not_allowed", "双方已有配偶、属于禁婚亲缘，或现有关系不允许成婚");
                    break;
                default:
                    return Fail("unknown_action", "未知关系行为");
            }
            if (relationType != 0)
            {
                if (DomainManager.Character.HasRelation(relationHolderId, relationTargetId, relationType))
                    return Fail("already", "双方已经存在所请求关系");
                // Native friendship/sworn writers validate target first, holder second.
                // Mentor uses its own directed holder/target convention.
                bool allowed = relationType == 8192
                    ? RelationTypeHelper.AllowAddingFriendRelation(relationTargetId, relationHolderId)
                    : relationType == 512
                        ? RelationTypeHelper.AllowAddingSwornBrotherOrSisterRelation(relationTargetId, relationHolderId)
                        : RelationTypeHelper.AllowAddingMentorRelation(relationHolderId, relationTargetId);
                if (!allowed)
                    return Fail("relation_not_allowed", "双方的现有亲缘或关系不允许建立所请求关系");
            }

            try
            {
                switch (action)
                {
                    case "best_friend":
                        new BecomeFriendAction().ApplyInitialChangesForTaiwu(context, npc, taiwu);
                        break;
                    case "sworn_sibling":
                        new BecomeSwornAction().ApplyInitialChangesForTaiwu(context, npc, taiwu);
                        break;
                    case "mentor":      // 兼容旧值 = NPC 拜太吾为师(太吾=师父, NPC=徒弟)
                    case "apprentice":  // 原生 self 持 Mentor 位，故 self=NPC(徒弟), target=太吾(师父)
                        Character.ApplyAddRelation_Mentor(context, npc, taiwu,
                            npc.GetBehaviorType(),
                            DomainManager.Character.IsTaiwuPeople(npc.GetId()),
                            DomainManager.Character.IsTaiwuPeople(taiwu.GetId()));
                        break;
                    case "take_disciple": // NPC 收太吾为徒:self=太吾(徒弟), target=NPC(师父)
                        Character.ApplyAddRelation_Mentor(context, taiwu, npc,
                            taiwu.GetBehaviorType(),
                            DomainManager.Character.IsTaiwuPeople(taiwu.GetId()),
                            DomainManager.Character.IsTaiwuPeople(npc.GetId()));
                        break;
                    case "adoptive_parent": // NPC 认太吾为义父母。
                        Character.ApplyAddRelation_AdoptiveParent(context, npc, taiwu,
                            npc.GetBehaviorType(),
                            DomainManager.Character.IsTaiwuPeople(npc.GetId()),
                            DomainManager.Character.IsTaiwuPeople(taiwu.GetId()));
                        break;
                    case "adoptive_child": // NPC 收太吾为义子女。
                        Character.ApplyAddRelation_AdoptiveChild(context, npc, taiwu,
                            npc.GetBehaviorType(),
                            DomainManager.Character.IsTaiwuPeople(npc.GetId()),
                            DomainManager.Character.IsTaiwuPeople(taiwu.GetId()));
                        break;
                    case "lover": // NPC 只能决定自己的爱慕；太吾若此前也爱慕此人，写入后才自然成为两情相悦。
                        if (!TryApplyDirectedAdoration(context, npc, taiwu,
                            DomainManager.Character.IsTaiwuPeople(npc.GetId()),
                            DomainManager.Character.IsTaiwuPeople(taiwu.GetId())))
                            return Fail("adoration_not_applied", "此人的爱慕未能建立");
                        break;
                    case "spouse": // NPC 与太吾结为夫妻
                        Character.ApplyBecomeHusbandOrWife(context, npc, taiwu,
                            npc.GetBehaviorType(), true,
                            DomainManager.Character.IsTaiwuPeople(npc.GetId()),
                            DomainManager.Character.IsTaiwuPeople(taiwu.GetId()));
                        break;
                }
            }
            catch (Exception e) { return Indeterminate("relation_indeterminate", "关系及连带状态无法确认:" + e.GetType().Name); }

            bool ok =
                (action == "best_friend" && DomainManager.Character.HasRelation(npcId, taiwuId, (ushort)8192)) ||
                (action == "sworn_sibling" && DomainManager.Character.HasRelation(npcId, taiwuId, (ushort)512)) ||
                ((action == "mentor" || action == "apprentice") && DomainManager.Character.HasRelation(npcId, taiwuId, (ushort)2048)) ||
                (action == "take_disciple" && DomainManager.Character.HasRelation(taiwuId, npcId, (ushort)2048)) ||
                (action == "adoptive_parent" && DomainManager.Character.HasRelation(npcId, taiwuId, (ushort)64)) ||
                (action == "adoptive_child" && DomainManager.Character.HasRelation(npcId, taiwuId, (ushort)128)) ||
                (action == "lover" && DomainManager.Character.HasRelation(npcId, taiwuId, (ushort)16384)) ||
                (action == "spouse" && DomainManager.Character.HasRelation(npcId, taiwuId, (ushort)1024));
            if (!ok) return Indeterminate("relation_postcondition_indeterminate", "关系写 API 已返回但预期关系位未满足");

            string npcName = ShownName(npcId); if (string.IsNullOrWhiteSpace(npcName)) npcName = "#" + npcId;
            var done = new SerializableModData();
            done.Set("success", true);
            done.Set("status", "executed");
            done.Set("message", action == "lover"
                ? (HasMutualAdoration(npcId, taiwuId)
                    ? npcName + "与太吾如今两情相悦"
                    : npcName + "开始单方面爱慕太吾")
                : action == "adoptive_parent"
                    ? npcName + "认太吾为" + AdoptiveParentTitle(taiwu)
                    : action == "adoptive_child"
                        ? npcName + "收太吾为" + AdoptiveChildTitle(taiwu)
                        : "关系已建立");
            return done;
        }

        // 过月给 NPC 注入行动目标 goal(templateId 262 保护 / 271 追杀太吾 等)。
        // target_mode="taiwu" → 目标 charId 取实时太吾;否则取 target_char_id。在域线程内原子执行。
        private static SerializableModData AddGoal(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "目标请求不完整");

            int npcId = 0, templateId = 0, targetCharId = 0; string targetMode = null, travelMode = null;
            if (!p.Get("npc_id", out npcId))
                return Fail("missing_parameter", "缺少参数");
            p.Get("travel_mode", out travelMode);
            if (!string.IsNullOrWhiteSpace(travelMode))
                return AddNpcTravelTarget(context, p, npcId, travelMode);
            if (!p.Get("template_id", out templateId))
                return Fail("missing_parameter", "缺少参数");
            p.Get("target_mode", out targetMode);
            p.Get("target_char_id", out targetCharId);
            switch (templateId)
            {
                // These are the only b24185552 CharacterGoal templates exposed by this Mod.
                case 254: case 262: case 263: case 271: case 274: break;
                default: return Fail("unsupported_goal", "不支持的行动目标:" + templateId);
            }
            if (targetMode != "taiwu" && targetMode != "char")
                return Fail("bad_target_mode", "目标类型无效");
            if ((templateId == 254 || templateId == 271) && targetMode != "taiwu")
                return Fail("bad_target_mode", "该行动目标只能指向太吾");
            int areaId = -1, blockId = 0, home = 0;                // 显式固定地点赴约；home=1 表示动态解析太吾村家园
            bool hasArea = p.Get("area_id", out areaId);
            p.Get("block_id", out blockId); p.Get("home", out home);

            Character npc = null;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null)
                return Fail("character_not_found", "NPC 失效");  // 仅活人在 Objects 表中
            if (npc.GetKidnapperId() >= 0
                || DomainManager.Organization.GetPrisonerSect(npcId) >= 0)
                return Fail("actor_restrained", "此人正被囚禁或绑架，无法自行赴约、追杀、营救或护卫");

            int arg0;
            if (targetMode == "taiwu")
            {
                arg0 = DomainManager.Taiwu.GetTaiwuCharId();
                if (arg0 <= 0) return Fail("no_taiwu", "无有效太吾");
            }
            else
            {
                arg0 = targetCharId;
                if (arg0 <= 0) return Fail("bad_target", "目标无效");
            }

            if (arg0 == npcId) return Fail("self_target", "不能以自己为行动目标");
            Character target = null;
            if (!DomainManager.Character.TryGetElement_Objects(arg0, out target) || target == null)
                return Fail("target_not_found", "行动目标已失效");

            // Mirror the validity gates used by the authoritative b24185552 action implementations.
            // Appointment/Protect/Rescue all require friendly favor; the Mod's UI and game threshold
            // both use 10000 for FavorabilityType >= 2.
            short targetFavor = DomainManager.Character.GetFavorability(npcId, arg0);
            if ((templateId == 254 || templateId == 262 || templateId == 263) && targetFavor < 10000)
                return Fail("insufficient_favor", "交情不足，行动目标不会成立");
            if (templateId == 263 && target.GetKidnapperId() == npcId)
                return Fail("target_kidnapped_by_self", "不能营救自己扣押的人");
            if (templateId == 271 && !DomainManager.World.GetWorldFunctionsStatus(4))
                return Fail("world_function_locked", "当前世界进度尚不能触发追杀太吾行动");
            if (templateId == 274)
            {
                var vengeanceTargets = DomainManager.Character.GetRelatedCharIds(npcId, 32768);
                if (targetFavor > -6000 || vengeanceTargets == null || !vengeanceTargets.Contains(arg0))
                    return Fail("not_enemy", "双方没有足以寻仇的真实仇怨");
            }

            Location goalLocation = Location.Invalid;
            try
            {
                if (templateId == 254)
                {
                    // 队友无从单独赴约:游戏 AppointmentAction.OnStart 对【在太吾队中者】直接跳过"离队上路"逻辑(他随太吾同行),
                    // 注入了 goal 也不会动身。故在队者直接回绝,免"注入成功却不上路"的静默失败。
                    if (npc.IsInTaiwuGroup())
                        return Fail("in_team", "此人已在太吾队中、一路随你同行,无从单独动身去别处赴约(要单独派他去某地,得先让他离队)");
                    // 254 只表示“与太吾在固定地点赴约”。普通前往与动态寻人必须走 NpcTravelTarget，
                    // 不允许旧前端再借第三人坐标或太吾当前坐标制造永久等待的幽灵预约。
                    if (home == 0 && (!hasArea || areaId < 0))
                        return Fail("appointment_requires_fixed_location", "赴约必须说定一个固定地点");
                    Location loc = home != 0 ? DomainManager.Taiwu.GetTaiwuVillageLocation()
                        : new Location((short)areaId, (short)blockId);
                    if (!loc.IsValid() && areaId >= 0) loc = new Location((short)areaId, 0);   // 块号缺失/越界兜底:退到该区 0 号块,至少能成行
                    if (!loc.IsValid()) return Fail("bad_location", "目的地无效(area=" + areaId + " block=" + blockId + " home=" + home + ")");
                    goalLocation = loc;
                    // 约定注册表与 254 goal 在本体中成对读写:AppointmentAction.CheckValid 要求注册表
                    // 坐标与 goal 目的地一致,撤约事件(TaiwuFunctions 50)也是 RemoveGoal(254)+
                    // RemoveAppointment 成对撤销。若此人已有旧 254 goal,AddGoal 会因同模板去重静默
                    // 返回 null——先按同一配对语义整体撤旧约再全新登记,免得注册表被覆盖成新地而
                    // goal 仍指旧地、新旧两约一起废掉。
                    bool replacedExisting = npc.GetGoal(254) != null;
                    // 必须先快照旧登记。旧实现先 RemoveAppointment 再 TryGet，导致回滚永远读不到旧约。
                    Location priorAppointment = Location.Invalid;
                    bool hadPriorAppointment = DomainManager.Taiwu.TryGetElement_Appointments(npcId, out priorAppointment);
                    if (replacedExisting)
                    {
                        npc.RemoveGoal(context, 254);
                        DomainManager.Taiwu.RemoveAppointment(context, npcId);
                    }
                    // 记下登记前的注册项:goal 注入被本体拒绝(模板不可达/被禁用等)时回滚
                    // AddAppointment——恢复旧注册项或移除新项,不留"已登记约定却无 goal"的半写。
                    DomainManager.Taiwu.AddAppointment(context, npcId, loc);
                    npc.AddGoal(context, 254, (PlanningContextArg)arg0, (PlanningContextArg)loc);
                    if (npc.GetGoal(254, (PlanningContextArg)arg0, (PlanningContextArg)loc) == null)
                    {
                        if (hadPriorAppointment)
                        {
                            DomainManager.Taiwu.AddAppointment(context, npcId, priorAppointment);
                            npc.AddGoal(context, 254, (PlanningContextArg)arg0, (PlanningContextArg)priorAppointment);
                        }
                        else DomainManager.Taiwu.RemoveAppointment(context, npcId);
                        return Fail("goal_rejected", replacedExisting
                            ? (hadPriorAppointment
                                ? "新的赴约目标未被游戏接受,旧约及其行动目标均已恢复;此人仍会按旧约动身"
                                : "旧约缺少可恢复的登记,新的赴约目标也未被游戏接受;此人现在没有任何约定")
                            : (hadPriorAppointment
                                ? "新的赴约目标未被游戏接受,约定登记已回退到先前的旧约;旧约仍然有效,此人届时仍会按旧约动身"
                                : "赴约目标未被游戏接受,约定登记已回退,双方没有立下任何约定"));
                    }
                }
                else
                {
                    npc.AddGoal(context, templateId, (PlanningContextArg)arg0);   // 内部设脏标记/同步
                }
            }
            catch (Exception e) { return Indeterminate("goal_indeterminate", "约定或行动目标可能已部分写入:" + e.GetType().Name); }

            // Template-only presence can match an older goal aimed at another character. Query by the
            // same authoritative argument tuple used for AddGoal so the receipt cannot overclaim.
            CharacterGoalData applied = templateId == 254
                ? npc.GetGoal(templateId, (PlanningContextArg)arg0, (PlanningContextArg)goalLocation)
                : npc.GetGoal(templateId, (PlanningContextArg)arg0);
            if (applied == null)
                return Indeterminate("goal_postcondition_indeterminate", "目标写 API 已返回但精确目标后验未满足");

            var done = new SerializableModData();
            done.Set("success", true);
            done.Set("status", "executed");
            done.Set("message", "目标已注入:" + templateId);
            return done;
        }

        // 普通前往/寻人不是 Taiwu Appointment。b24185552 的权威移动载体 NpcTravelTarget
        // 会逐月移动、动态追踪人物当前位置、抵达后自行移除，并在 RemainingMonth 到期时清理。
        // 只有显式固定地点赴太吾之约才继续走上面的 254 goal。
        // Mirrors b24185552 Character.GetValidAndNotForbiddenByBeggarSkillLocation.
        // travel_state must describe the destination that Character.TravelToTargets will actually
        // execute, including its nearest-block substitution for a forbidden beggar-skill block.
        private static Location ResolveExecutableTravelTargetLocation(Location location)
        {
            if (!location.IsValid()) return location;
            if (!ProfessionSkillHandle.IsLocationForbiddenByBeggarSkill(location)) return location;

            var neighbors = new List<MapBlockData>();
            DomainManager.Map.GetRealNeighborBlocks(location.AreaId, location.BlockId, neighbors, 2);
            byte areaSize = DomainManager.Map.GetAreaSize(location.AreaId);
            ByteCoordinate center = ByteCoordinate.IndexToCoordinate(location.BlockId, areaSize);
            neighbors.Sort(delegate(MapBlockData a, MapBlockData b)
            {
                byte aAreaSize = DomainManager.Map.GetAreaSize(a.AreaId);
                ByteCoordinate aCoordinate = ByteCoordinate.IndexToCoordinate(a.BlockId, aAreaSize);
                byte bAreaSize = DomainManager.Map.GetAreaSize(b.AreaId);
                ByteCoordinate bCoordinate = ByteCoordinate.IndexToCoordinate(b.BlockId, bAreaSize);
                return center.GetManhattanDistance(aCoordinate)
                    .CompareTo(center.GetManhattanDistance(bCoordinate));
            });
            foreach (MapBlockData neighbor in neighbors)
            {
                Location candidate = neighbor.GetLocation();
                if (candidate.IsValid()
                    && !ProfessionSkillHandle.IsLocationForbiddenByBeggarSkill(candidate))
                    return candidate;
            }
            return Location.Invalid;
        }

        private static void RestoreAppointmentComponents(DataContext context, Character npc, int npcId,
            CharacterGoalData goalSnapshot, int goalIndex,
            PlanningContextArg goalTargetArg, Location goalDestination,
            CharacterActionData goalCurrentAction,
            bool interruptedActionsWasNull, List<CharacterActionData> interruptedActionsSnapshot,
            bool hadRegistration, Location registrationLocation)
        {
            if (hadRegistration)
            {
                Location currentRegistration;
                if (!DomainManager.Taiwu.TryGetElement_Appointments(npcId, out currentRegistration))
                {
                    DomainManager.Taiwu.AddAppointment(context, npcId, registrationLocation);
                }
                else if (!currentRegistration.Equals(registrationLocation))
                {
                    DomainManager.Taiwu.RemoveAppointment(context, npcId);
                    DomainManager.Taiwu.AddAppointment(context, npcId, registrationLocation);
                }
            }

            if (goalSnapshot != null)
            {
                CharacterGoalData currentGoal = npc.GetGoal(254);
                bool sameGoal = object.ReferenceEquals(currentGoal, goalSnapshot)
                    && currentGoal.ContextArgs != null
                    && currentGoal.ContextArgs.Length >= 2
                    && currentGoal.ContextArgs[0].Equals(goalTargetArg)
                    && ((Location)currentGoal.ContextArgs[1]).Equals(goalDestination);
                if (!sameGoal)
                {
                    if (currentGoal != null) npc.RemoveGoal(context, 254);
                    List<CharacterGoalData> goals = npc.ActionPlanningData.Goals;
                    if (goals == null)
                    {
                        goals = new List<CharacterGoalData>();
                        npc.ActionPlanningData.Goals = goals;
                    }
                    goals.Insert(Math.Max(0, Math.Min(goalIndex, goals.Count)), goalSnapshot);
                }
                // Character.RemoveGoal calls SetGoalActionInterrupted: restore both pieces it
                // mutates so a failed replacement returns to the exact pre-write goal state.
                goalSnapshot.CurrentAction = goalCurrentAction;
                npc.ActionPlanningData.InterruptedActions = interruptedActionsWasNull
                    ? null : new List<CharacterActionData>(interruptedActionsSnapshot);
                npc.SetActionPlanningModified(context);
            }
        }

        private static bool TryGetNpcTravelEligibility(int npcId, Character npc,
            out string code, out string reason)
        {
            code = null;
            reason = null;
            if (npcId <= 0 || npc == null)
            {
                code = "character_not_found"; reason = "NPC 失效"; return false;
            }
            // 与 Character.UpdateIntelligentCharacterMovement 的确定性停止条件保持一致。
            if (npc.GetCreatingType() != 1 || DomainManager.Character.IsTemporaryIntelligentCharacter(npcId))
            {
                code = "actor_not_mobile_npc"; reason = "此人不是会逐月自主移动的江湖人物"; return false;
            }
            if (CharacterDomain.IsLockMovementChar(npcId))
            {
                code = "actor_movement_locked"; reason = "此人的移动正被游戏剧情锁定"; return false;
            }
            if (npc.GetAgeGroup() == 0)
            {
                code = "actor_infant"; reason = "年幼角色无法独自远行"; return false;
            }
            if (npc.GetKidnapperId() >= 0
                || DomainManager.Organization.GetPrisonerSect(npcId) >= 0)
            {
                code = "actor_kidnapped"; reason = "此人正被囚禁或绑架，无法动身"; return false;
            }
            int leaderId = npc.GetLeaderId();
            if (leaderId >= 0 && leaderId != npcId)
            {
                code = "actor_in_group"; reason = "此人正随队同行，无法单独动身"; return false;
            }
            if (npc.IsActiveExternalRelationState(188uL))
            {
                code = "actor_movement_locked"; reason = "此人当前状态无法远行"; return false;
            }
            if (npc.IsCompletelyInfected() && npc.GetLocation().IsValid())
            {
                code = "actor_infected"; reason = "此人当前状态无法按约远行"; return false;
            }
            if (npc.GetLegendaryBookOwnerState() >= 2)
            {
                code = "actor_movement_locked"; reason = "此人当前职责使其无法远行"; return false;
            }
            code = "ok";
            reason = "可以自主远行";
            return true;
        }

        private static SerializableModData AddNpcTravelTarget(DataContext context, SerializableModData p,
            int npcId, string travelMode)
        {
            Character npc;
            if (npcId <= 0 || !DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null)
                return Fail("character_not_found", "NPC 失效");

            if (!TryGetNpcTravelEligibility(npcId, npc,
                out string travelCode, out string travelReason))
                return WithCharacterState(Fail(travelCode, travelReason), npc);

            int duration = DefaultNpcTravelDurationMonths;
            int requestedDuration;
            if (p.Get("max_duration_months", out requestedDuration)) duration = requestedDuration;
            duration = Math.Clamp(duration, 1, 12);

            NpcTravelTarget requested;
            string destinationText;
            if (string.Equals(travelMode, "fixed", StringComparison.Ordinal))
            {
                int areaId = -1, blockId = 0, home = 0;
                bool hasArea = p.Get("area_id", out areaId);
                p.Get("block_id", out blockId);
                p.Get("home", out home);
                if (home == 0 && !hasArea)
                    return Fail("missing_location", "固定行程缺少目的地区域");
                Location location = home != 0
                    ? DomainManager.Taiwu.GetTaiwuVillageLocation()
                    : new Location((short)areaId, (short)blockId);
                if (!location.IsValid() && areaId >= 0)
                    location = new Location((short)areaId, 0);
                if (!location.IsValid())
                    return Fail("bad_location", "目的地无效(area=" + areaId + " block=" + blockId + ")");
                requested = new NpcTravelTarget(location, duration);
                destinationText = "固定地点 " + location.AreaId + ":" + location.BlockId;
            }
            else if (string.Equals(travelMode, "char", StringComparison.Ordinal))
            {
                string targetMode = null;
                int targetCharId = 0;
                p.Get("target_mode", out targetMode);
                p.Get("target_char_id", out targetCharId);
                if (string.Equals(targetMode, "taiwu", StringComparison.Ordinal))
                    targetCharId = DomainManager.Taiwu.GetTaiwuCharId();
                else if (!string.Equals(targetMode, "char", StringComparison.Ordinal))
                    return Fail("bad_target_mode", "寻人目标类型无效");
                if (targetCharId <= 0 || targetCharId == npcId)
                    return Fail("bad_target", "寻人目标无效");
                Character target;
                if (!DomainManager.Character.TryGetElement_Objects(targetCharId, out target) || target == null)
                    return Fail("target_not_found", "要寻找的人已经失效");
                requested = new NpcTravelTarget(targetCharId, duration);
                destinationText = "人物 #" + targetCharId;
            }
            else return Fail("bad_travel_mode", "行程类型无效");

            // 254 goal 会先于普通 travel target 驱动行动；Appointments 登记也独立参与
            // 原生赴约流程。两者无论成对还是孤立都必须分别取代，并保留精确快照回滚。
            CharacterGoalData supersededGoal = npc.GetGoal(254);
            bool supersededAppointmentGoal = supersededGoal != null;
            int supersededGoalIndex = supersededAppointmentGoal
                ? npc.ActionPlanningData.Goals.IndexOf(supersededGoal) : -1;
            CharacterActionData supersededGoalCurrentAction = supersededAppointmentGoal
                ? supersededGoal.CurrentAction : null;
            bool supersededInterruptedActionsWasNull =
                npc.ActionPlanningData.InterruptedActions == null;
            List<CharacterActionData> supersededInterruptedActions =
                supersededInterruptedActionsWasNull ? null
                : new List<CharacterActionData>(npc.ActionPlanningData.InterruptedActions);
            PlanningContextArg supersededGoalTargetArg = default(PlanningContextArg);
            Location supersededGoalDestination = Location.Invalid;
            if (supersededAppointmentGoal)
            {
                // Goal 254 stores [target character, appointment destination]. The registry can
                // be missing or stale, so rollback must read the destination from ContextArgs.
                if (supersededGoal.ContextArgs == null || supersededGoal.ContextArgs.Length < 2)
                    return Indeterminate("travel_stale_goal_unreadable",
                        "旧赴约目标数据不完整，无法在失败时安全恢复");
                supersededGoalTargetArg = supersededGoal.ContextArgs[0];
                supersededGoalDestination = (Location)supersededGoal.ContextArgs[1];
            }
            Location supersededAppointmentRegistration = Location.Invalid;
            bool hadSupersededAppointmentRegistration =
                DomainManager.Taiwu.TryGetElement_Appointments(npcId,
                    out supersededAppointmentRegistration);
            List<NpcTravelTarget> priorTravelTargets = null;
            bool travelWriteAttempted = false;
            try
            {
                List<NpcTravelTarget> current = npc.GetNpcTravelTargets();
                priorTravelTargets = current == null
                    ? new List<NpcTravelTarget>()
                    : new List<NpcTravelTarget>(current);
                // Goal 254 and the Taiwu registry each affect native appointment behavior. Clear
                // both independently so either orphan cannot block this explicit ordinary trip.
                if (supersededAppointmentGoal) npc.RemoveGoal(context, 254);
                if (hadSupersededAppointmentRegistration)
                    DomainManager.Taiwu.RemoveAppointment(context, npcId);
                var updated = new List<NpcTravelTarget>(priorTravelTargets);
                for (int i = 0; i < updated.Count; i++)
                {
                    if (!requested.IsSameTargetWith(updated[i])) continue;
                    updated.RemoveAt(i);
                    break;
                }
                // 玩家/Agent 的明确新行程应优先执行，但保留全部不相关的原生行程。
                updated.Insert(0, requested);
                travelWriteAttempted = true;
                npc.SetNpcTravelTargets(updated, context);

                bool applied = false;
                List<NpcTravelTarget> verified = npc.GetNpcTravelTargets();
                if (verified != null)
                    foreach (NpcTravelTarget value in verified)
                        if (requested.IsSameTargetWith(value) && value.RemainingMonth == duration)
                        { applied = true; break; }
                if (!applied)
                {
                    npc.SetNpcTravelTargets(new List<NpcTravelTarget>(priorTravelTargets), context);
                    RestoreAppointmentComponents(context, npc, npcId,
                        supersededGoal, supersededGoalIndex, supersededGoalTargetArg,
                        supersededGoalDestination, supersededGoalCurrentAction,
                        supersededInterruptedActionsWasNull, supersededInterruptedActions,
                        hadSupersededAppointmentRegistration,
                        supersededAppointmentRegistration);
                    return Indeterminate("travel_postcondition_indeterminate", "行程写入后未能确认");
                }
            }
            catch (Exception e)
            {
                if (travelWriteAttempted && priorTravelTargets != null)
                {
                    try { npc.SetNpcTravelTargets(new List<NpcTravelTarget>(priorTravelTargets), context); }
                    catch { }
                }
                if (supersededAppointmentGoal || hadSupersededAppointmentRegistration)
                {
                    try
                    {
                        RestoreAppointmentComponents(context, npc, npcId,
                            supersededGoal, supersededGoalIndex, supersededGoalTargetArg,
                            supersededGoalDestination, supersededGoalCurrentAction,
                            supersededInterruptedActionsWasNull, supersededInterruptedActions,
                            hadSupersededAppointmentRegistration,
                            supersededAppointmentRegistration);
                    }
                    catch { }
                }
                return Indeterminate("travel_indeterminate", "行程可能已写入但无法确认:" + e.GetType().Name);
            }

            var done = new SerializableModData();
            done.Set("success", true);
            done.Set("status", "executed");
            done.Set("action_state", "scheduled");
            done.Set("remaining_months", duration);
            done.Set("travel_mode", travelMode);
            done.Set("message", "行程已安排:" + destinationText + "（最多 " + duration + " 个月，抵达即结束）"
                + (supersededAppointmentGoal || hadSupersededAppointmentRegistration
                    ? "；先前约定已由本次新行程取代" : ""));
            return done;
        }

        // 档案状态核验：必须区分真正进入本体 DeadCharacters 的死者与剧情阶段退场/临时移除人物。
        // 后者既不在 Objects 也不在 DeadCharacters，但绝不能因此误标“已故”，更不能删除聊天或记忆。
        // 入参 ids=逗号分隔 charId；返回 alive/dead/unavailable 三个 csv。只读，不改游戏态。
        private static SerializableModData FilterDead(DataContext context, SerializableModData p)
        {
            var ret = new SerializableModData();
            string csv = null;
            p?.Get("ids", out csv);
            if (string.IsNullOrWhiteSpace(csv))
            {
                ret.Set("success", true); ret.Set("alive", ""); ret.Set("dead", "");
                ret.Set("unavailable", ""); return ret;
            }

            var aliveIds = new System.Text.StringBuilder();
            var dead = new System.Text.StringBuilder();
            var unavailable = new System.Text.StringBuilder();
            foreach (var part in csv.Split(','))
            {
                if (!int.TryParse(part.Trim(), out int id) || id <= 0) continue;
                Character c;
                bool alive = DomainManager.Character.TryGetElement_Objects(id, out c) && c != null;
                if (alive)
                {
                    if (aliveIds.Length > 0) aliveIds.Append(',');
                    aliveIds.Append(id);
                    continue;
                }
                // 最新版 CharacterDomain.TryGetDeadCharacter 是“真正死亡”的权威来源；
                // 单纯离开活动人物表的剧情 NPC/旧阶段不会出现在这里。
                DeadCharacter archived = DomainManager.Character.TryGetDeadCharacter(id);
                bool ordinaryDead = false;
                try
                {
                    ordinaryDead = archived != null
                        && Config.Character.Instance[archived.TemplateId].CreatingType == 1;
                }
                catch { ordinaryDead = false; }
                // 固定模板人物的源实体可能仅因剧情阶段或读档回退进入死者表；它仍可
                // 作为合法普通副本的复制来源，不能把源 ID 耐久化成灵魂。真正的普通
                // 人物与已建立副本 CreatingType=1，仍严格按本体死者表确认死亡。
                if (ordinaryDead)
                {
                    if (dead.Length > 0) dead.Append(',');
                    dead.Append(id);
                }
                else
                {
                    if (unavailable.Length > 0) unavailable.Append(',');
                    unavailable.Append(id);
                }
            }

            ret.Set("success", true);
            ret.Set("alive", aliveIds.ToString());
            ret.Set("dead", dead.ToString());
            ret.Set("unavailable", unavailable.ToString());
            return ret;
        }

        // 令 NPC 认可太吾(归心)。仅门派/据点成员有效(EventHelper 自动建与太吾关系后置位);非成员则无效。
        private static SerializableModData Recognize(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "认可请求不完整");
            int npcId = 0;
            if (!p.Get("npc_id", out npcId)) return Fail("missing_parameter", "缺少参数");

            Character npc = null;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null)
                return Fail("character_not_found", "NPC 失效");

            // EventHelper.SetSectCharApprovedTaiwu writes only an existing SectCharacter; checking the
            // exact storage object avoids its null-conditional setter becoming a silent no-op.
            SectCharacter sectCharacter;
            if (!DomainManager.Organization.TryGetElement_SectCharacters(npcId, out sectCharacter)
                || sectCharacter == null)
                return Fail("not_sect", "该 NPC 非门派/据点之人,无法令其归心");

            if (sectCharacter.GetApprovedTaiwu())
            {
                var already = new SerializableModData();
                already.Set("success", true); already.Set("status", "already_applied");
                already.Set("message", "此人已经认可太吾");
                return already;
            }

            try
            {
                global::GameData.Domains.TaiwuEvent.EventHelper.EventHelper.SetSectCharApprovedTaiwu(npcId, true);
            }
            catch (Exception e) { return Indeterminate("recognize_indeterminate", "认可状态无法确认:" + e.GetType().Name); }

            SectCharacter verified;
            if (!DomainManager.Organization.TryGetElement_SectCharacters(npcId, out verified)
                || verified == null || !verified.GetApprovedTaiwu())
                return Indeterminate("recognize_postcondition_indeterminate", "认可写 API 已返回但认可后验未满足");

            var done = new SerializableModData();
            done.Set("success", true);
            done.Set("status", "executed");
            done.Set("message", "已令其归心、认可太吾");
            return done;
        }

        // 过月立场漂移:改 NPC 道德值(_baseMorality,引擎内部 Clamp -500..500),立场由道德值实时派生。
        private static SerializableModData ChangeMorality(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "立场请求不完整");
            int npcId = 0, delta = 0;
            if (!p.Get("npc_id", out npcId) || !p.Get("delta", out delta)) return Fail("missing_parameter", "缺少参数");
            if (delta == 0) return Fail("noop", "无漂移");

            Character npc = null;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null)
                return Fail("character_not_found", "NPC 失效");

            try { npc.ChangeBaseMorality(context, delta); }
            catch (Exception e) { return Indeterminate("morality_indeterminate", "立场数值写入状态无法确认:" + e.GetType().Name); }

            var done = new SerializableModData();
            done.Set("success", true);
            done.Set("status", "executed");
            done.Set("message", "立场漂移 delta=" + delta + " 当前立场=" + npc.GetBehaviorType());
            return done;
        }

        // NPC 对太吾的戒备升降(delta 负=被打动降戒备、解锁更高好感上限;正=被冒犯/识破升戒备)。
        private static SerializableModData ChangeAlertness(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "戒备请求不完整");
            int npcId = 0, delta = 0;
            if (!p.Get("npc_id", out npcId) || !p.Get("delta", out delta)) return Fail("missing_parameter", "缺少参数");
            if (delta == 0) return Fail("noop", "无变化");

            Character npc = null;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null)
                return Fail("character_not_found", "NPC 失效");

            // 游戏的 ChangeAlertness 只对 CreatingType==1(常规江湖人物)生效,其它角色(太吾村民/家眷/特殊角色等)
            // 会静默跳过、什么都不做。先拦下如实回拒,免得"返回成功却没生效"。
            if (npc.GetCreatingType() != 1)
                return Fail("no_alertness", "此人非常规江湖人物,没有戒备机制,无法调整其戒心");

            try { DomainManager.Character.ChangeAlertness(context, npcId, delta); }
            catch (Exception e) { return Indeterminate("alertness_indeterminate", "戒备数值写入状态无法确认:" + e.GetType().Name); }

            int after = DomainManager.Character.GetAlertnessValue(npcId);
            var done = new SerializableModData();
            done.Set("success", true); done.Set("status", "executed");
            done.Set("message", (after >= 0 && after < 1000000)
                ? ("戒心调整后当前值≈" + after + "(隐藏值,越低越敞开、好感上限越高;面板不显示)")
                : ("戒备 delta=" + delta + "(隐藏值,面板不显示)"));
            return done;
        }

        // NPC 被劝说后放走所掳之人(target=被掳者,npc=掳人者)。非实际掳持关系则引擎内部忽略。
        private static SerializableModData Release(DataContext context, SerializableModData p)
        {
            if (context == null || p == null) return Fail("invalid_request", "释放请求不完整");
            int npcId = 0, targetId = 0;
            if (!p.Get("npc_id", out npcId) || !p.Get("target_id", out targetId)) return Fail("missing_parameter", "缺少参数");
            if (npcId <= 0 || targetId <= 0 || npcId == targetId) return Fail("bad_target", "目标无效");

            Character kidnapper, target;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out kidnapper) || kidnapper == null
                || !DomainManager.Character.TryGetElement_Objects(targetId, out target) || target == null)
                return Fail("character_not_found", "掳人者或被掳者已失效");
            if (target.GetKidnapperId() != npcId)
                return Fail("not_kidnapped_by_actor", "此人当前并非由该 NPC 扣押");

            try { DomainManager.Character.RemoveKidnappedCharacter(context, targetId, npcId, false); }
            catch (Exception e) { return Indeterminate("release_indeterminate", "释放及绳索/关系连带状态无法确认:" + e.GetType().Name); }

            if (target.GetKidnapperId() >= 0)
                return Indeterminate("release_postcondition_indeterminate", "释放 API 已返回但俘虏关系后验未满足");

            var done = new SerializableModData();
            done.Set("success", true); done.Set("status", "executed"); done.Set("message", "已释放 " + targetId);
            return done;
        }

        // 在【说话者的关系网】里把对话提到的具名第三方解析成 charId(不做全局模糊搜索)。
        // 先按关系词(我女儿/师父/仇人…)命中关系桶,桶内多人再按姓名缩;无关系词则在全部关系候选里按姓名匹配。
        private static SerializableModData ResolveChar(DataContext context, SerializableModData p)
        {
            var ret = new SerializableModData();
            int speakerId = 0; string text = null;
            if (p == null || !p.Get("speaker_id", out speakerId))
            {
                ret.Set("success", true); ret.Set("char_id", -1); ret.Set("name", ""); ret.Set("reason", "not_found");
                SetHostilityEvidence(ret, speakerId, -1);
                return ret;
            }
            // The frontend may already have resolved a local alias (for example "你" -> Taiwu).
            // Reusing this read-only RPC with target_id lets the final hostile-action gate obtain
            // exact backend relationship evidence without adding another RPC surface.
            int directTargetId = 0;
            if (p.Get("target_id", out directTargetId) && directTargetId > 0)
            {
                Character speaker, target;
                bool valid = false;
                try
                {
                    valid = IsValidSceneCharacter(speakerId) && IsValidSceneCharacter(directTargetId)
                        && DomainManager.Character.TryGetElement_Objects(speakerId, out speaker) && speaker != null
                        && DomainManager.Character.TryGetElement_Objects(directTargetId, out target) && target != null;
                }
                catch { valid = false; }
                ret.Set("success", true);
                ret.Set("char_id", valid ? directTargetId : -1);
                ret.Set("name", valid ? NameOf(directTargetId) : "");
                ret.Set("reason", valid ? "found" : "not_found");
                SetHostilityEvidence(ret, speakerId, valid ? directTargetId : -1);
                return ret;
            }
            p.Get("target_text", out text);
            int allowBlock = 0; p.Get("allow_block", out allowBlock);   // 放开「同块陌生人」兜底
            int allowGlobal = 0; p.Get("allow_global", out allowGlobal); // 仅明确要求任意具名人物时，严格全名全局兜底
            text = text ?? "";
            int taiwuId = -1; try { taiwuId = DomainManager.Taiwu.GetTaiwuCharId(); } catch { }

            string name = ""; string reason = "not_found";
            if (taiwuId > 0 && IsTaiwuReference(text, taiwuId, out name))
            {
                ret.Set("success", true);
                ret.Set("char_id", taiwuId);
                ret.Set("name", string.IsNullOrWhiteSpace(name) ? "太吾" : name);
                ret.Set("reason", "found");
                SetHostilityEvidence(ret, speakerId, taiwuId);
                return ret;
            }
            // ① 说话者关系网(身份最可信)
            int resolved = ResolveInRelations(speakerId, text, out name, ref reason);
            // ② 队友兜底:说话者在太吾队伍里,可从【同队队友】按名解析(两人同为太吾队友即可缔结,不必先相识)。
            if (resolved == -1)
            {
                try
                {
                    if (DomainManager.Taiwu.IsInGroup(speakerId))
                    {
                        var team = DomainManager.Character.GetGroupSet(context, speakerId);
                        if (team != null)
                        {
                            var teamSet = FilterResolvableCharacterIds(team, speakerId, taiwuId);
                            string tn; int byTeam = MatchByName(teamSet, text, out tn);
                            if (byTeam > 0) { resolved = byTeam; name = tn; }
                            else if (byTeam == -2) { resolved = -2; name = tn; }
                        }
                    }
                }
                catch { }
            }
            // ③ 太吾关系网兜底:太吾认识此人、并在对话里提了他,即够缔结由头。
            if (resolved == -1 && taiwuId > 0 && taiwuId != speakerId)
            {
                string r2 = "not_found", tn;
                int byTaiwu = ResolveInRelations(taiwuId, text, out tn, ref r2);
                if (byTaiwu > 0) { resolved = byTaiwu; name = tn; }
                else if (byTaiwu == -2) { resolved = -2; name = tn; }
            }
            // ④ 本地块兜底(所有当面第三方动作):说话者所在地块 + 太吾所在地块的原始同处者,严格全名匹配、歧义不猜。
            if (resolved == -1 && allowBlock != 0)
            {
                string bn, br = "not_found";
                int byBlock = ResolveInBlocks(context, speakerId, taiwuId, text, out bn, ref br);
                if (byBlock > 0) { resolved = byBlock; name = bn; }
                else if (byBlock == -2) { resolved = -2; name = bn; }
            }
            // ⑤ 任意具名人物兜底：只接受全局唯一的严格全名；同名多人一律返回歧义。
            // 过月同道用它联系远方人物，后续执行层仍按实时位置阻止任何隔空物理行为。
            if (resolved == -1 && allowGlobal != 0)
            {
                string gn, gr = "not_found";
                int byGlobal = ResolveGlobalByName(speakerId, taiwuId, text, out gn, ref gr);
                if (byGlobal > 0) { resolved = byGlobal; name = gn; }
                else if (byGlobal == -2) { resolved = -2; name = gn; }
            }
            if (resolved == -2) reason = "ambiguous";
            else if (resolved > 0) reason = "found";
            // resolved == -1 时保留 reason(not_found / resolve_error)

            ret.Set("success", true);
            ret.Set("char_id", resolved > 0 ? resolved : -1);   // 歧义(-2)按 -1 回报、附 reason=ambiguous
            ret.Set("name", name ?? "");
            ret.Set("reason", reason);
            SetHostilityEvidence(ret, speakerId, resolved > 0 ? resolved : -1);
            return ret;
        }

        private static void SetHostilityEvidence(SerializableModData response, int speakerId, int targetId)
        {
            bool known = false, hostile = false;
            try
            {
                Character speaker, target;
                known = speakerId > 0 && targetId > 0 && speakerId != targetId
                    && DomainManager.Character.TryGetElement_Objects(speakerId, out speaker) && speaker != null
                    && DomainManager.Character.TryGetElement_Objects(targetId, out target) && target != null;
                if (known)
                    hostile = DomainManager.Character.HasRelation(speakerId, targetId, (ushort)32768)
                        || DomainManager.Character.HasRelation(targetId, speakerId, (ushort)32768);
            }
            catch { known = false; hostile = false; }
            response.Set("hostility_known", known);
            response.Set("hostile", hostile);
        }

        // 当前现场兜底:复用最终物理动作的 actor/Taiwu scene 权威，严格匹配具名第三方。
        // 比关系网更严(陌生人池大量同姓):只认「原文字面含全名」或「严格全名相等」,禁松散包含/敬语剥离/裸姓单字;≥2 同名即歧义不猜。
        private static int ResolveInBlocks(DataContext context, int speakerId, int taiwuId, string text, out string matchedName, ref string reason)
        {
            matchedName = "";
            string q = NormFullNameOnly(text);
            int explicitId;
            bool hasExplicitId = TryExtractExplicitCharacterId(text, out explicitId);
            if (!hasExplicitId && explicitId == -2) { reason = "ambiguous"; return -2; }
            if (string.IsNullOrEmpty(text) || (!hasExplicitId && q.Length < 2)) return -1;
            var considered = new HashSet<int>();
            var hits = new HashSet<int>();
            string lastName = null; int scanned = 0; const int CAP = 200;
            // JHYL_RESOLVE_BLOCKS_ALIVE_LOCATION_SCAN:CharacterSet 只是快速索引，不再是
            // 搜索真值。城镇活人可能缺席该集合，但只要最终动作权威认定与任一端同场，
            // 就必须能够被姓名或 #编号解析出来。
            var sceneCandidates = new SortedSet<int>();
            try
            {
                Character taiwu = null;
                if (taiwuId > 0)
                    DomainManager.Character.TryGetElement_Objects(taiwuId, out taiwu);
                AddAllCharactersAtActorScene(speakerId, sceneCandidates);
                AddAllCharactersAtTaiwuScene(taiwuId, taiwu, sceneCandidates);
            }
            catch { }
            foreach (var cid in sceneCandidates)
            {
                // 模糊姓名搜索保留防御性上限；名单返回的 #人物编号则是稳定主键，
                // 必须遍历到精确目标，不能因拥挤现场的枚举顺序把真实在场者截掉。
                if (!hasExplicitId && scanned >= CAP) break;
                if (cid <= 0 || cid == speakerId || cid == taiwuId) continue;   // 太吾绝不靠现场名命中(绕过「不可加害太吾」)
                if (!considered.Add(cid)) continue;
                if (!hasExplicitId) scanned++;
                Character c;
                if (!IsValidSceneCharacter(cid)
                    || !DomainManager.Character.TryGetElement_Objects(cid, out c) || c == null) continue;
                if (hasExplicitId)
                {
                    if (cid == explicitId)
                    {
                        hits.Add(cid);
                        lastName = ShownName(cid);
                        break;
                    }
                    continue;
                }
                string mn;
                if (StrictNameHit(cid, text, q, out mn)) { hits.Add(cid); lastName = mn; }   // 兼比真名+显示名(法号/外号)
            }
            if (hits.Count == 1) { foreach (var only in hits) { matchedName = NameOf(only); return only; } }
            if (hits.Count >= 2) { matchedName = lastName; reason = "ambiguous"; return -2; }
            return -1;
        }

        // 全局按名解析:基于最新版权威 CharacterDomain.GmCmd_GetAllCharacterName 枚举存活实体，严格全名匹配。
        // 唯一命中=锁定该人(不必相识/同处一地);≥2 同名=歧义不猜。
        private static int ResolveGlobalByName(int speakerId, int taiwuId, string text, out string matchedName, ref string reason)
        {
            matchedName = "";
            string q = NormFullNameOnly(text);
            int explicitId;
            bool hasExplicitId = TryExtractExplicitCharacterId(text, out explicitId);
            if (!hasExplicitId && explicitId == -2) { reason = "ambiguous"; return -2; }
            if (string.IsNullOrEmpty(text) || (!hasExplicitId && q.Length < 2)) return -1;
            // #人物编号是稳定主键，不需要先枚举全世界姓名。直接读取并套用与
            // 姓名路径相同的存活、年龄、智能人物及太吾/说话者排除规则。
            if (hasExplicitId)
            {
                if (explicitId <= 0 || explicitId == speakerId || explicitId == taiwuId) return -1;
                try
                {
                    Character exact;
                    if (!IsValidSceneCharacter(explicitId)
                        || !DomainManager.Character.TryGetElement_Objects(explicitId, out exact) || exact == null)
                        return -1;
                    matchedName = NameOf(explicitId);
                    return explicitId;
                }
                catch
                {
                    reason = "resolve_error";
                    return -1;
                }
            }
            var hits = new HashSet<int>();
            string lastName = null;
            try
            {
                var all = DomainManager.Character.GmCmd_GetAllCharacterName();
                if (all != null)
                {
                    foreach (var entry in all)
                    {
                        int cid = entry.CharId;
                        if (cid <= 0 || cid == speakerId || cid == taiwuId) continue;
                        Character person;
                        if (!IsValidSceneCharacter(cid)
                            || !DomainManager.Character.TryGetElement_Objects(cid, out person) || person == null) continue;
                        if (hasExplicitId)
                        {
                            if (cid == explicitId) { hits.Add(cid); lastName = ShownName(cid); }
                            continue;
                        }
                        string mn;
                        if (StrictNameHit(cid, text, q, out mn)) { hits.Add(cid); lastName = mn; }
                    }
                }
            }
            catch { }
            if (hits.Count == 1) { foreach (var only in hits) { matchedName = NameOf(only); return only; } }
            if (hits.Count >= 2) { matchedName = lastName; reason = "ambiguous"; return -2; }
            return -1;
        }

        private static int ResolveInRelations(int speakerId, string text, out string matchedName, ref string reason)
        {
            matchedName = "";
            RelatedCharactersForRelations rel;
            try { rel = DomainManager.Character.GetRelatedCharactersForRelations(speakerId); }
            catch { reason = "resolve_error"; return -1; }
            if (rel == null) return -1;
            RelatedCharacters rawRel = null;
            try { rawRel = DomainManager.Character.GetRelatedCharacters(RelationOwnerId(speakerId)); } catch { }
            int taiwuId = -1;
            try { taiwuId = DomainManager.Taiwu.GetTaiwuCharId(); } catch { }

            var buckets = new List<(string[] words, CharacterSet set)>();
            // 师父必须在父母前,否则「师父」会被单字「父」先命中父母桶。
            buckets.Add((new[]{ "师父", "师傅", "恩师", "师尊", "授业" }, rel.Mentors));
            if (rawRel != null) buckets.Add((new[]{ "徒弟", "弟子", "门徒", "徒儿" }, rawRel.Mentees)); // JHYL_RELATION_RESOLVE_MENTEE_BUCKET
            buckets.Add((new[]{ "义子", "义女", "养子", "养女", "女儿", "儿子", "孩儿", "孩子", "子女", "骨肉" }, rel.Children));
            buckets.Add((new[]{ "义父", "义母", "养父", "养母", "父", "爹", "母", "娘", "双亲", "爹娘" }, rel.Parents));
            buckets.Add((new[]{ "义兄", "义弟", "义姐", "义妹", "结义" }, rel.SwornBrothersAndSisters));
            buckets.Add((new[]{ "兄", "弟", "姐", "妹", "手足" }, rel.BrothersAndSisters));
            buckets.Add((new[]{ "夫君", "娘子", "妻", "丈夫", "夫人", "配偶", "郎君" }, rel.HusbandsAndWives));
            buckets.Add((new[]{ "挚友", "好友", "朋友", "故交", "知交" }, rel.Friends));
            buckets.Add((new[]{ "仇", "敌", "冤家" }, rel.Enemies));
            buckets.Add((new[]{ "爱慕", "心上人", "意中人", "倾慕" }, rel.Adored));

            // 1) 关系词命中:桶内按姓名缩;缩不到时——唯一则取,≥2 则歧义不猜(不再盲取第一个,避免静默认错人)
            foreach (var b in buckets)
            {
                if (!TextContainsAny(text, b.words)) continue;
                var ids = FilterResolvableCharacterIds(b.set.GetCollection(), speakerId, taiwuId);
                if (ids == null || ids.Count == 0) continue;
                int byName = MatchByName(ids, text, out matchedName);
                if (byName > 0) return byName;
                if (byName == -2) return -2;
                if (ids.Count == 1) { foreach (var id in ids) { matchedName = NameOf(id); return id; } }
                matchedName = ""; return -2;   // 关系词命中但桶内多人、无名可缩 → 歧义
            }

            // 2) 无关系词:全部关系候选里按姓名精确/包含匹配
            var all = new HashSet<int>();
            foreach (var b in buckets)
            {
                var c = FilterResolvableCharacterIds(b.set.GetCollection(), speakerId, taiwuId);
                foreach (var id in c) all.Add(id);
            }
            foreach (var set in new[] { rel.RelatedEnemies, rel.RelatedAdored, rel.FactionMembers })
            {
                var c = FilterResolvableCharacterIds(set.GetCollection(), speakerId, taiwuId);
                foreach (var id in c) all.Add(id);
            }
            return MatchByName(all, text, out matchedName);
        }

        private static HashSet<int> FilterResolvableCharacterIds(
            IEnumerable<int> ids, int speakerId, int taiwuId)
        {
            var filtered = new HashSet<int>();
            if (ids == null) return filtered;
            foreach (int id in ids)
                if (id > 0 && id != speakerId && id != taiwuId
                    && IsValidSceneCharacter(id))
                    filtered.Add(id);
            return filtered;
        }

        private static bool IsTaiwuReference(string text, int taiwuId, out string matchedName)
        {
            matchedName = "";
            string raw = (text ?? "").Trim();
            string q = NormPersonName(raw);
            if (q.Length == 0 || taiwuId <= 0) return false;
            if (q == "太吾" || q == "太吾传人" || q == "玩家" || string.Equals(raw, "taiwu", StringComparison.OrdinalIgnoreCase))
            {
                matchedName = "太吾";
                return true;
            }
            foreach (var n0 in CandidateNames(taiwuId))
            {
                string n = (n0 ?? "").Trim();
                if (n.Length == 0) continue;
                string nn = NormPersonName(n);
                if (nn.Length == 0) continue;
                if (raw.IndexOf(n, StringComparison.Ordinal) >= 0 || q == nn)
                {
                    matchedName = n;
                    return true;
                }
                if (q == "太吾" + nn || (nn.StartsWith("太吾", StringComparison.Ordinal) && q == nn.Substring(2)))
                {
                    matchedName = n;
                    return true;
                }
                if (q.StartsWith("太吾", StringComparison.Ordinal) && q.Substring(2) == nn)
                {
                    matchedName = n;
                    return true;
                }
            }
            return false;
        }

        // 名称解析鲁棒(代称/别名/模糊):原文含真名最稳;否则归一化(去标点+敬语前后缀)后双向包含,
        // 让"萧夫人/小翠姑娘/老周"等也能落到真名,匹配不中再交由前端把"没找到此人"回喂模型自纠。
        // 返回:>0=命中 id;-1=未命中;-2=歧义(多个模糊命中,绑错比找不到更糟,交上层请模型给完整名)。
        private static int MatchByName(HashSet<int> ids, string text, out string matchedName)
        {
            matchedName = "";
            if (ids == null || string.IsNullOrEmpty(text)) return -1;
            int explicitId;
            if (TryExtractExplicitCharacterId(text, out explicitId))
            {
                if (!ids.Contains(explicitId)) return -1;
                matchedName = ShownName(explicitId);
                return explicitId;
            }
            if (explicitId == -2) return -2;
            // 候选名一律含真名+显示名(法号/外号),模型常只知显示名。
            // 1) 原文直接含名:只接受唯一人物的「最长」命中(最具体),避免「周武」
            // 抢了「周武林」；两名人物同名时必须要求 #实体ID，绝不能按 HashSet 枚举序选人。
            var literalIds = new HashSet<int>();
            string litName = null; int litLen = -1;
            foreach (var id in ids)
                foreach (var n in CandidateNames(id))
                {
                    if (string.IsNullOrEmpty(n) || text.IndexOf(n, StringComparison.Ordinal) < 0) continue;
                    if (n.Length > litLen)
                    {
                        literalIds.Clear();
                        litLen = n.Length;
                        litName = n;
                    }
                    if (n.Length == litLen) literalIds.Add(id);
                }
            if (literalIds.Count == 1)
            {
                foreach (int id in literalIds) { matchedName = litName; return id; }
            }
            if (literalIds.Count > 1) return -2;
            // 2) 归一化精确相等也必须按 distinct 人物 ID 消歧；同名时要求 #实体ID。
            string q = NormPersonName(text);
            if (q.Length == 0) return -1;
            var exactIds = new HashSet<int>();
            string exactName = null;
            foreach (var id in ids)
                foreach (var n in CandidateNames(id))
                    if (NormPersonName(n) == q)
                    {
                        exactIds.Add(id);
                        if (exactName == null) exactName = n;
                    }
            if (exactIds.Count == 1)
            {
                foreach (int id in exactIds) { matchedName = exactName; return id; }
            }
            if (exactIds.Count > 1) return -2;
            // 3) 松散双向包含:≥2 命中 = 歧义,不猜首个(原先盲取首个会静默认错人)
            int looseId = -1; string looseName = null; int looseCount = 0;
            foreach (var id in ids)
            {
                bool hit = false;
                foreach (var n in CandidateNames(id))
                {
                    string nn = NormPersonName(n);
                    if (nn.Length < 2 || q.Length < 2) continue;
                    if (nn.IndexOf(q, StringComparison.Ordinal) >= 0 || q.IndexOf(nn, StringComparison.Ordinal) >= 0)
                    { hit = true; if (looseId < 0) { looseId = id; looseName = n; } break; }
                }
                if (hit) looseCount++;
            }
            if (looseCount == 1) { matchedName = looseName; return looseId; }
            if (looseCount >= 2) return -2;
            return -1;
        }

        // 查询名单统一输出“显示名(#实体id)”。显示名会因法号/改名而变化，同名也可
        // 同时存在；只接受带 # 的十进制正整数，且每个解析阶段仍会独立校验该 id
        // 确实属于关系网/队伍/现场/全局允许范围，绝不把任意编号当成授权。
        private static bool TryExtractExplicitCharacterId(string text, out int charId)
        {
            charId = -1;
            if (string.IsNullOrWhiteSpace(text)) return false;
            int found = -1;
            int hash = text.IndexOf('#');
            while (hash >= 0 && hash + 1 < text.Length)
            {
                long value = 0;
                int i = hash + 1, digits = 0;
                while (i < text.Length && text[i] >= '0' && text[i] <= '9')
                {
                    value = value * 10 + (text[i] - '0');
                    digits++; i++;
                    if (value > int.MaxValue)
                    {
                        while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
                        break;
                    }
                }
                if (digits > 0 && value > 0 && value <= int.MaxValue)
                {
                    int parsed = (int)value;
                    if (found > 0 && found != parsed)
                    {
                        charId = -2;
                        return false;
                    }
                    found = parsed;
                }
                hash = text.IndexOf('#', hash + 1);
            }
            charId = found;
            return found > 0;
        }

        // 严格全名归一化:只去标点/书名号/引号/空白,保留全部名字字符;不削敬语前后缀。供「不可信的同块陌生人」严格匹配用。
        private static string NormFullNameOnly(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            const string strip = "『』「」《》<>“”\"‘’'·、，,。.！!? \t　";
            foreach (var ch in s) if (strip.IndexOf(ch) < 0) sb.Append(ch);
            return sb.ToString();
        }

        // 归一化人名:去标点/书名号/引号/空白,削常见敬语前缀与后缀(关系词另有桶处理,这里只削称谓客套)。
        private static readonly string[] HonorPrefix = { "小", "老", "阿" };
        private static readonly string[] HonorSuffix = { "姑娘", "公子", "先生", "大侠", "前辈", "夫人", "老爷", "掌门", "帮主", "道长", "师太", "大师", "姐姐", "哥哥", "兄长", "大人" };
        private static string NormPersonName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            const string strip = "『』「」《》<>“”\"‘’'·、，,。.！!? \t　";
            foreach (var ch in s) if (strip.IndexOf(ch) < 0) sb.Append(ch);
            string t = sb.ToString();
            foreach (var pre in HonorPrefix) if (t.Length > 2 && t.StartsWith(pre, StringComparison.Ordinal)) { t = t.Substring(pre.Length); break; }
            foreach (var suf in HonorSuffix) if (t.Length > suf.Length && t.EndsWith(suf, StringComparison.Ordinal)) { t = t.Substring(0, t.Length - suf.Length); break; }
            return t;
        }

        private static string NameOf(int charId)
        {
            try { var t = DomainManager.Character.GetRealName(charId); return (t.Item1 ?? "") + (t.Item2 ?? ""); }
            catch { return ""; }
        }

        // 显示名(法号/外号):模型在快照里看到的是 GetMonasticTitleOrDisplayName,而非真名。
        // 杀/绑/下毒/结仇等按名解析若只比真名,法号/外号 NPC 永远匹配不上 → 报「没找到」。故解析须兼比显示名。
        private static string DisplayNameOf(int charId)
        {
            try { var t = DomainManager.Character.GetMonasticTitleOrDisplayName(charId); return (t.Item1 ?? "") + (t.Item2 ?? ""); }
            catch { return ""; }
        }

        // 给模型看的名字:优先显示名(法号/外号,与对话快照/live chat 一致),取不到再退真名。
        // 用于一切"列人给模型看"的场合(在场/门派/关系网名单),免同一人在不同工具下现两个名(困惑+泄露人设不用的真名)。
        private static string ShownName(int charId)
        { var dn = DisplayNameOf(charId); return string.IsNullOrEmpty(dn) ? NameOf(charId) : dn; }

        private static string ListedCharacterName(int charId)
        {
            string name = ShownName(charId);
            return string.IsNullOrWhiteSpace(name)
                ? ("#" + charId)
                : (name + "(#" + charId + ")");
        }

        // 一个角色用于按名匹配的候选名:真名 + 显示名(去空、去重)。两者都要能命中。
        private static System.Collections.Generic.IEnumerable<string> CandidateNames(int charId)
        {
            string n = NameOf(charId);
            if (!string.IsNullOrEmpty(n)) yield return n;
            string dn = DisplayNameOf(charId);
            if (!string.IsNullOrEmpty(dn) && dn != n) yield return dn;
        }

        // 同块/全局兜底用的严格命中:某候选名「字面含于原文」或「严格全名(NormFullNameOnly)相等」即命中。
        private static bool StrictNameHit(int cid, string text, string q, out string matched)
        {
            matched = null;
            foreach (var n in CandidateNames(cid))
                if (text.IndexOf(n, StringComparison.Ordinal) >= 0 || NormFullNameOnly(n) == q) { matched = n; return true; }
            return false;
        }

        // 物品名 + 品阶(资源 ItemType==12 不显;其余按 ItemTemplateHelper.GetGrade 0-8→九品~一品)。供查询据实显示品阶。
        private static string ItemNameWithGrade(string name, ItemKey key)
        {
            if (string.IsNullOrWhiteSpace(name) || key.ItemType == (sbyte)12) return name;
            try { string g = GradeShort(ItemTemplateHelper.GetGrade(key.ItemType, key.TemplateId)); return string.IsNullOrEmpty(g) ? name : (name + "(" + g + ")"); }
            catch { return name; }
        }

        private static string GradeShort(sbyte g)
        {
            switch (g)
            {
                case 0: return "九品"; case 1: return "八品"; case 2: return "七品"; case 3: return "六品"; case 4: return "五品";
                case 5: return "四品"; case 6: return "三品"; case 7: return "二品"; case 8: return "一品"; default: return "";
            }
        }

        // 关系网某一桶 → "标签:名1、名2…"(每桶至多 8 人;空桶不输出)。供 npc_relations。
        private static void RelBucket(System.Text.StringBuilder sb, string label, CharacterSet set)
        {
            System.Collections.Generic.HashSet<int> ids = null;
            try { ids = set.GetCollection(); } catch { }
            if (ids == null || ids.Count == 0) return;
            var names = new System.Collections.Generic.List<string>();
            foreach (var id in ids)
            {
                if (names.Count >= 8) break;
                if (id > 0) names.Add(ListedCharacterName(id));
            }
            if (names.Count == 0) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(label).Append(":").Append(string.Join("、", names.ToArray()));
        }

        private static void RelFamilyBuckets(System.Text.StringBuilder sb,
            RelatedCharacters raw, RelatedCharactersForRelations merged)
        {
            if (raw == null)
            {
                if (merged == null) return;
                RelBucket(sb, "父母(此人的长辈双亲)", merged.Parents);
                RelBucket(sb, "子女(此人的晚辈儿女)", merged.Children);
                RelBucket(sb, "兄弟姐妹", merged.BrothersAndSisters);
                return;
            }
            RelBucket(sb, "亲生父母(此人的长辈双亲)", raw.BloodParents);
            RelBucket(sb, "继父母(此人的长辈双亲)", raw.StepParents);
            RelBucket(sb, "义父母(此人所认的义父或义母)", raw.AdoptiveParents);
            RelBucket(sb, "亲生子女(此人的晚辈儿女)", raw.BloodChildren);
            RelBucket(sb, "继子女(此人的晚辈儿女)", raw.StepChildren);
            RelBucket(sb, "义子女(此人所收的义子或义女)", raw.AdoptiveChildren);
            RelBucket(sb, "亲生兄弟姐妹", raw.BloodBrothersAndSisters);
            RelBucket(sb, "继兄弟姐妹", raw.StepBrothersAndSisters);
            RelBucket(sb, "义兄弟姐妹(由义亲关系衍生)", raw.AdoptiveBrothersAndSisters);
        }

        private static string RelNames(CharacterSet set, int limit)
        {
            System.Collections.Generic.HashSet<int> ids = null;
            try { ids = set.GetCollection(); } catch { }
            if (ids == null || ids.Count == 0) return "";
            var sorted = new System.Collections.Generic.List<int>(ids);
            sorted.Sort();
            var names = new System.Collections.Generic.List<string>();
            foreach (int id in sorted)
            {
                if (names.Count >= limit) break;
                if (id > 0) names.Add(ListedCharacterName(id));
            }
            return string.Join("、", names.ToArray());
        }

        // GetRelatedCharactersForRelations 会按最新版体规则把有 SrcCharId 的映射人物切回
        // 关系源角色；补取原始 Mentees 桶时必须做同样映射，否则画像/映射人物会漏掉徒弟。
        private static int RelationOwnerId(int charId)
        {
            try
            {
                Character character;
                if (DomainManager.Character.TryGetElement_Objects(charId, out character) && character != null)
                {
                    int sourceId = character.GetSrcCharId();
                    if (sourceId >= 0) return sourceId;
                }
            }
            catch { }
            return charId;
        }

        // a 与 b 之间的主要关系名(供"查熟人")。最新本体中持 Mentor(2048) 位者是徒弟、被指向者是师父。
        // 无明确关系返回 null(=泛泛相识)。
        private static string RelationBetween(int a, int b)
        {
            if (a <= 0 || b <= 0 || a == b) return null;
            var C = DomainManager.Character;
            try
            {
                if (C.HasRelation(a, b, (ushort)73)) return "他是你的父母长辈";
                if (C.HasRelation(a, b, (ushort)146)) return "他是你的子女";
                if (C.HasRelation(a, b, (ushort)292) || C.HasRelation(b, a, (ushort)292)) return "兄弟姐妹";
                if (C.HasRelation(a, b, (ushort)1024) || C.HasRelation(b, a, (ushort)1024)) return "夫妻";
                if (C.HasRelation(a, b, (ushort)512) || C.HasRelation(b, a, (ushort)512)) return "结义兄弟姐妹";
                // ApplyAddRelation_Mentor(self,target) 会 AddRelation(self,target,2048)：
                // self 拜 target 为师，故 HasRelation(a,b,2048)=b 是 a 的师父。
                if (C.HasRelation(a, b, (ushort)2048)) return "他是你师父";
                if (C.HasRelation(b, a, (ushort)2048)) return "他是你徒弟";
                if (C.HasRelation(a, b, (ushort)8192) || C.HasRelation(b, a, (ushort)8192)) return "挚友";
                bool aAdoresB = C.HasRelation(a, b, (ushort)16384);
                bool bAdoresA = C.HasRelation(b, a, (ushort)16384);
                if (aAdoresB && bAdoresA) return "恋人（两情相悦）";
                if (aAdoresB) return "你爱慕他（单向）";
                if (bAdoresA) return "他爱慕你（单向）";
                bool aEnemiesB = C.HasRelation(a, b, (ushort)32768);
                bool bEnemiesA = C.HasRelation(b, a, (ushort)32768);
                if (aEnemiesB && bEnemiesA) return "你们互为仇敌";
                if (aEnemiesB) return "你视他为仇敌（单向）";
                if (bEnemiesA) return "他视你为仇敌（单向）";
            }
            catch { }
            return null;
        }

        private static bool HasMutualAdoration(int a, int b)
        {
            if (a <= 0 || b <= 0 || a == b) return false;
            try
            {
                return DomainManager.Character.HasRelation(a, b, (ushort)16384)
                    && DomainManager.Character.HasRelation(b, a, (ushort)16384);
            }
            catch { return false; }
        }

        // 本体 Adored(16384) 是有方向的“谁爱慕谁”。NPC 主动表意只能写 self→target；
        // targetLovesBack 必须为 false，不能替目标作出爱慕决定。若 target→self 早已存在，
        // 这次写入后双向位自然成立，权威关系查询才会把双方显示为两情相悦。
        private static bool TryApplyDirectedAdoration(DataContext context, Character self, Character target,
            bool selfIsTaiwuPeople, bool targetIsTaiwuPeople)
        {
            if (context == null || self == null || target == null || self.GetId() == target.GetId()) return false;
            int selfId = self.GetId(), targetId = target.GetId();
            if (DomainManager.Character.HasRelation(selfId, targetId, (ushort)16384)) return true;
            Character.ApplyAddRelation_Adore(context, self, target, self.GetBehaviorType(), false,
                selfIsTaiwuPeople, targetIsTaiwuPeople);
            return DomainManager.Character.HasRelation(selfId, targetId, (ushort)16384);
        }

        private static bool IsAnimalCharacter(Character character)
        {
            if (character == null) return false;
            short templateId = character.GetTemplateId();
            var animalTemplates = ProfessionSkillHandle.AnimalCharacterTemplateIds;
            if (animalTemplates == null) return false;
            for (int i = 0; i < animalTemplates.Count; i++)
                if (animalTemplates[i] == templateId) return true;
            return false;
        }

        private static bool IsEligibleMonthlyAgentCharacter(int characterId, Character character)
            => character != null && character.GetAgeGroup() != AgeGroup.Baby
                && (!IsAnimalCharacter(character)
                    || TryGetCharacterProxySource(characterId, out _));

        private static List<int> ParseCsvInts(string csv, int limit)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(csv) || limit <= 0) return result;
            foreach (string part in csv.Split(','))
            {
                int value;
                if (!int.TryParse((part ?? string.Empty).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value) || value <= 0 || !seen.Add(value)) continue;
                result.Add(value);
                if (result.Count >= limit) break;
            }
            return result;
        }

        // MapBlock.CharacterSet can retain an id after the Character object has already been
        // removed during monthly settlement. Never publish those stale ids as live scene
        // participants: frontend native domain calls index the Character dictionary directly.
        private static bool IsValidSceneCharacter(int charId)
        {
            if (charId <= 0) return false;
            try
            {
                Character character;
                return DomainManager.Character.TryGetElement_Objects(charId, out character)
                    && character != null
                    && character.GetAgeGroup() != AgeGroup.Baby
                    && character.IsInteractableAsIntelligentCharacter();
            }
            catch { return false; }
        }

        // a、b 之间显著关系的【中性第三人称】标签(供过月叙述列人物纠葛)。无明确关系返回 null。
        // 与 RelationBetween 同集合、同方向(2048 存于徒弟→师父方向:HasRelation(a,b,2048)=b 是 a 的师父)。
        private static string RelationPairLabel(int a, int b, string na, string nb)
        {
            if (a <= 0 || b <= 0 || a == b) return null;
            var C = DomainManager.Character;
            try
            {
                if (C.HasRelation(a, b, (ushort)64)) return nb + "是" + na + "的义父母";
                if (C.HasRelation(a, b, (ushort)128)) return nb + "是" + na + "的义子女";
                if (C.HasRelation(a, b, (ushort)9)) return nb + "是" + na + "的父母长辈";
                if (C.HasRelation(a, b, (ushort)18)) return nb + "是" + na + "的子女";
                if (C.HasRelation(a, b, (ushort)292) || C.HasRelation(b, a, (ushort)292)) return na + "与" + nb + "是兄弟姐妹";
                if (C.HasRelation(a, b, (ushort)1024) || C.HasRelation(b, a, (ushort)1024)) return na + "与" + nb + "是夫妻";
                if (C.HasRelation(a, b, (ushort)512) || C.HasRelation(b, a, (ushort)512)) return na + "与" + nb + "是结义兄弟姐妹";
                if (C.HasRelation(a, b, (ushort)2048)) return nb + "是" + na + "的师父";
                if (C.HasRelation(b, a, (ushort)2048)) return na + "是" + nb + "的师父";
                if (C.HasRelation(a, b, (ushort)8192) || C.HasRelation(b, a, (ushort)8192)) return na + "与" + nb + "是挚友";
                bool aAdoresB = C.HasRelation(a, b, (ushort)16384);
                bool bAdoresA = C.HasRelation(b, a, (ushort)16384);
                if (aAdoresB && bAdoresA) return na + "与" + nb + "是恋人（两情相悦）";
                if (aAdoresB) return na + "单向爱慕" + nb;
                if (bAdoresA) return nb + "单向爱慕" + na;
                bool aEnemiesB = C.HasRelation(a, b, (ushort)32768);
                bool bEnemiesA = C.HasRelation(b, a, (ushort)32768);
                if (aEnemiesB && bEnemiesA) return na + "与" + nb + "互为仇敌";
                if (aEnemiesB) return na + "单向视" + nb + "为仇敌";
                if (bEnemiesA) return nb + "单向视" + na + "为仇敌";
            }
            catch { }
            return null;
        }

        private static bool TextContainsAny(string s, string[] keys)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var k in keys) if (s.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool HasAnyGoods(GameData.Domains.Merchant.MerchantData md)
        {
            if (md == null) return false;
            for (int gi = 0; gi <= 6; gi++) { var inv = md.GetGoodsList(gi); if (inv != null && inv.Items != null && inv.Items.Count > 0) return true; }
            return false;
        }

        // 严格只读解析当前商人的既有真实货架：先查角色店铺，再查与该商人同地块、
        // 同商人类型的行商商队。b24185552 的公开 GetMerchantInfoCaravanDataList 可能补建
        // CaravanExtraData，GetCaravanMerchantData 又可能换季刷新；所以这里只反射读取游戏
        // 已存在的 _caravanDict/_caravanData，不创建库存、不推进 RNG。query 与 trade 共用本方法。
        private static GameData.Domains.Merchant.MerchantData ResolveExistingMerchantStore(
            int npcId, out int ownerType, out int ownerId)
        {
            ownerType = 0; ownerId = npcId;
            GameData.Domains.Merchant.MerchantData md;
            if (DomainManager.Merchant.TryGetMerchantData(npcId, out md) && md != null && HasAnyGoods(md)) { ownerType = 0; ownerId = npcId; return md; }
            if (!TryGetSameBlockCaravan(npcId, out int caravanId, out md) || md == null || !HasAnyGoods(md))
                return null;
            ownerType = 1; ownerId = caravanId; return md;
        }

        // 查询货架采用原生“打开商店”语义：普通商人首次建立角色货架，现有行商按季刷新。
        // 这一步只会在前端权威快照已确认当前对话对象是商人后调用；不会拿 NPC 背包冒充货架。
        private static GameData.Domains.Merchant.MerchantData ResolveMerchantStoreForQuery(
            DataContext context, int npcId, out int ownerType, out int ownerId)
        {
            var existing = ResolveExistingMerchantStore(npcId, out ownerType, out ownerId);
            if (existing != null) return existing;
            if (!DomainManager.Character.TryGetElement_Objects(npcId, out Character npc) || npc == null
                || !DomainManager.Extra.TryGetMerchantCharToType(npcId, out sbyte merchantType)
                || merchantType < 0)
            {
                ownerType = -1; ownerId = -1; return null;
            }
            if (TryGetSameBlockCaravan(npcId, out int caravanId, out var caravanData))
            {
                var refreshed = DomainManager.Merchant.GetCaravanMerchantData(context, caravanId) ?? caravanData;
                ownerType = 1; ownerId = caravanId; return refreshed;
            }
            var characterStore = DomainManager.Merchant.GetMerchantData(context, npcId);
            ownerType = 0; ownerId = npcId; return characterStore;
        }

        private static bool TryGetSameBlockCaravan(int npcId, out int caravanId,
            out GameData.Domains.Merchant.MerchantData merchantData)
        {
            caravanId = -1;
            merchantData = null;
            try
            {
                Character npc;
                sbyte merchantType;
                if (!DomainManager.Character.TryGetElement_Objects(npcId, out npc) || npc == null
                    || !DomainManager.Extra.TryGetMerchantCharToType(npcId, out merchantType)
                    || merchantType < 0) return false;
                Location npcLocation = npc.GetValidLocation();
                object domain = DomainManager.Merchant;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var dataField = domain.GetType().GetField("_caravanData", flags);
                var pathField = domain.GetType().GetField("_caravanDict", flags);
                var data = dataField?.GetValue(domain) as IDictionary;
                var paths = pathField?.GetValue(domain) as IDictionary;
                if (data == null || paths == null) return false;

                var candidates = new List<int>();
                foreach (DictionaryEntry entry in paths)
                {
                    if (!(entry.Key is int discoveredId)
                        || !(entry.Value is GameData.Domains.Merchant.CaravanPath path)
                        || path.GetCurrLocation() != npcLocation) continue;
                    candidates.Add(discoveredId);
                }
                candidates.Sort();
                foreach (int candidateId in candidates)
                {
                    if (!data.Contains(candidateId)) continue;
                    var caravanData = data[candidateId] as GameData.Domains.Merchant.MerchantData;
                    if (caravanData == null || caravanData.MerchantType != merchantType) continue;
                    caravanId = candidateId;
                    merchantData = caravanData;
                    return true;
                }
            }
            catch { }
            return false;
        }

        // 把一份 MerchantData 的 7 档货架(GoodsList0..6)按种类去重收集成 "it,t,amount"。
        private static void CollectMerchantGoods(GameData.Domains.Merchant.MerchantData md, List<string> parts, HashSet<int> seen)
        {
            if (md == null) return;
            for (int gi = 0; gi <= 6; gi++)
            {
                var inv = md.GetGoodsList(gi);
                if (inv == null || inv.Items == null) continue;
                foreach (var kv in inv.Items)
                {
                    if (kv.Value <= 0) continue;
                    var k = kv.Key;
                    // 与 trade 选择器保持同一能力面：bit8 额外商品需要原生
                    // MerchantExtraGoodsData + 实体归一化事务，不能由自定义议价暴露。
                    if (!IsSupportedCustomMerchantShelfItem(k)) continue;
                    int sig = ((int)k.ItemType << 16) ^ (k.TemplateId & 0xFFFF);
                    if (!seen.Add(sig)) continue;   // 同种货跨档去重
                    parts.Add((int)k.ItemType + "," + (int)k.TemplateId + "," + kv.Value);
                }
            }
        }

        private static bool IsSupportedCustomMerchantShelfItem(ItemKey key)
        {
            try
            {
                ItemBase item = DomainManager.Item.GetBaseItem(key);
                return item != null
                    && !ModificationStateHelper.IsActive(item.GetModificationState(), 8);
            }
            catch
            {
                // 货物元数据读不到时封闭失败；把未知实体交给自定义交易比少展示一项
                // 更危险，可能破坏额外商品登记或唯一物品归属。
                return false;
            }
        }

        // ===== 副作用 RPC v2：存档级幂等回执 =====
        // ModDomain.CallModMethodWithParamAndRet 在后端域线程同步调用 delegate 后才序列化返回。
        // 因此“副作用已落地，前端回包丢失”必须由后端持久回执解歧，不能靠前端猜测。
        private static SerializableModData ExecuteJournaled(DataContext context, SerializableModData parameter, string operationKind,
            Func<DataContext, SerializableModData, SerializableModData> handler)
        {
            string operationId = null;
            parameter?.Get(RpcConst.OperationIdField, out operationId);
            uint worldId;
            int taiwuId;
            string bindingCode, bindingMessage;
            if (!TryValidateOperationBinding(parameter, out worldId, out taiwuId, out bindingCode, out bindingMessage))
                return BuildOperationOutcome(false, "rejected", bindingCode, false, operationId, operationKind,
                    bindingMessage, worldId, taiwuId);
            if (!IsValidOperationId(operationId))
                return BuildOperationOutcome(false, "failed", "invalid_operation_id", false, operationId, operationKind,
                    "副作用请求缺少 32 位 operation_id", worldId, taiwuId);

            OperationLedgerLoad ledgerLoad = LoadOperationLedger();
            if (!ledgerLoad.Reliable || ledgerLoad.Data == null)
                return BuildOperationOutcome(false, "unknown", "operation_ledger_unavailable", true,
                    operationId, operationKind, "副作用 ledger 暂时无法可靠读取；动作没有派发，请仅用同 operation_id 查询", worldId, taiwuId);
            SerializableModData ledger = ledgerLoad.Data;
            if (!CompletePendingReceiptCompaction(context, ledger))
                return BuildOperationOutcome(false, "unknown", "operation_compaction_cleanup_pending", true,
                    operationId, operationKind, "旧回执压缩清理尚未可靠完成；动作没有派发", worldId, taiwuId);
            SerializableModData existing;
            bool receiptReadReliable;
            bool existingFound = TryLoadOperationReceipt(operationId, out existing, out receiptReadReliable);
            if (!receiptReadReliable)
                return BuildOperationOutcome(false, "unknown", "operation_receipt_read_failed", true,
                    operationId, operationKind, "无法可靠判断同 operation_id 是否已有回执；为防重放，动作没有执行", worldId, taiwuId);
            if (existingFound && existing != null)
            {
                if (!StoredOperationBindingMatchesExact(existing, worldId, taiwuId))
                    return BuildOperationOutcome(false, "rejected", "operation_receipt_identity_mismatch", false,
                        operationId, operationKind, "已有回执缺少当前精确身份或绑定到另一存档；为防重放已拒绝执行", worldId, taiwuId);
                string existingKind = null;
                existing.Get("operation_kind", out existingKind);
                if (!string.IsNullOrWhiteSpace(existingKind) && !string.Equals(existingKind, operationKind, StringComparison.Ordinal))
                    return BuildOperationOutcome(false, "rejected", "operation_id_conflict", false, operationId, operationKind,
                        "operation_id 已被另一类副作用占用", worldId, taiwuId);
                if (!HasPersistedReceiptProof(existing))
                {
                    OperationReceiptCommitState migrated = StoreOperationReceipt(context, ledger, operationId, existing);
                    if (!migrated.ExactReceiptVerified)
                        return BuildOperationOutcome(false, "unknown", "legacy_receipt_migration_failed", true,
                            operationId, operationKind, "旧回执尚未能加盖持久化完整性证明；请继续按同一 id 查询",
                            worldId, taiwuId);
                }
                string existingStatus = null;
                existing.Get(RpcConst.OperationStatusField, out existingStatus);
                if (existingStatus == "pending")
                {
                    bool inFlight;
                    lock (InFlightOperationGate) inFlight = InFlightOperationIds.Contains(operationId);
                    if (!inFlight)
                    {
                        var orphan = BuildOperationOutcome(false, "unknown", "orphaned_pending_after_restart", false,
                            operationId, operationKind,
                            "后端发现没有同进程执行器持有的 pending；副作用可能在上次中断前部分落地，已保守封存为不可重试未知",
                            worldId, taiwuId);
                        OperationReceiptCommitState orphanStored = StoreOperationReceipt(context, ledger, operationId, orphan);
                        if (orphanStored.ExactReceiptVerified) return orphan;
                        return BuildOperationOutcome(false, "unknown", "orphan_pending_terminalization_failed", true,
                            operationId, operationKind, "孤儿 pending 尚未能可靠终态化；绝不重执行，请继续按同一 id 查询",
                            worldId, taiwuId);
                    }
                }
                else
                {
                    // Repair a missing index without ever replacing the exact business payload.
                    StoreOperationReceipt(context, ledger, operationId, existing);
                }
                return CloneOperationData(existing); // 只读对象不得原地 Stamp/修改
            }
            string compactedKind, compactedStatus, compactedCode;
            bool tombstoneReliable;
            if (TryFindOperationTombstone(ledger, operationId, worldId, taiwuId,
                out compactedKind, out compactedStatus, out compactedCode, out tombstoneReliable))
            {
                if (!string.IsNullOrWhiteSpace(compactedKind)
                    && !string.Equals(compactedKind, operationKind, StringComparison.Ordinal))
                    return BuildOperationOutcome(false, "rejected", "operation_id_conflict", false, operationId, operationKind,
                        "operation_id 已被另一类副作用占用并完成确认", worldId, taiwuId);
                bool priorSuccess = compactedStatus == "succeeded";
                string priorStatus = string.IsNullOrWhiteSpace(compactedStatus) ? "rejected" : compactedStatus;
                return BuildOperationOutcome(priorSuccess, priorStatus,
                    string.IsNullOrWhiteSpace(compactedCode) ? "operation_already_acknowledged" : compactedCode,
                    false, operationId, operationKind,
                    "此副作用此前已有终态并由前端确认；完整业务回执已压缩，绝不再次执行", worldId, taiwuId);
            }
            if (!tombstoneReliable)
                return BuildOperationOutcome(false, "unknown", "operation_tombstone_read_failed", true,
                    operationId, operationKind, "防重墓碑暂时无法可靠读取；动作没有执行", worldId, taiwuId);
            if (OperationLedgerContains(ledger, operationId))
                return BuildOperationOutcome(false, "unknown", "indexed_operation_receipt_missing", true,
                    operationId, operationKind, "ledger 仍记录此 operation_id，但精确回执与墓碑不可得；为防重放，动作没有执行",
                    worldId, taiwuId);

            // Reserve the operation before the durable pending prewrite and keep the
            // reservation until a terminal receipt (or conservative fallback) has been
            // resolved. Otherwise a concurrent duplicate can observe the small gaps on
            // either side of the handler and misclassify a live pending receipt as a
            // restart orphan.
            bool registeredInFlight;
            lock (InFlightOperationGate) registeredInFlight = InFlightOperationIds.Add(operationId);
            if (!registeredInFlight)
                return BuildOperationOutcome(false, "pending", "operation_in_flight", true,
                    operationId, operationKind, "同一 operation_id 正在本进程执行；本请求没有重复派发动作",
                    worldId, taiwuId);

            try
            {
                // Reserve bounded receipt capacity before producing any terminal
                // rejection. Every terminal mutation response must itself have exact
                // durable evidence; an unjournaled "ledger full" rejection would leave
                // the frontend ACK outbox retrying a receipt that never existed.
                if (!EnsureOperationLedgerSlot(context, ledger, worldId, taiwuId))
                    return BuildOperationOutcome(false, "unknown", "operation_ledger_full_unacked", true,
                        operationId, operationKind,
                        "副作用回执簿已被未确认操作占满；动作没有执行，请保留同一 operation_id 稍后对账",
                        worldId, taiwuId);

                string groupGuardCode, groupGuardMessage;
                if (!ValidateGroupPhysicalEnvelope(parameter, operationKind, taiwuId,
                    out groupGuardCode, out groupGuardMessage))
                {
                    var rejected = BuildOperationOutcome(false, "rejected", groupGuardCode, false,
                        operationId, operationKind, groupGuardMessage, worldId, taiwuId);
                    OperationReceiptCommitState rejectedStored = StoreOperationReceipt(context, ledger, operationId, rejected);
                    if (rejectedStored.ExactReceiptVerified) return rejected;
                    return BuildOperationOutcome(false, "unknown", "group_presence_rejection_store_failed", true,
                        operationId, operationKind, "群聊现场复核已拒绝动作，但拒绝回执未能可靠落盘；动作没有执行",
                        worldId, taiwuId);
                }

            var pending = BuildOperationOutcome(false, "pending", "operation_pending", true, operationId, operationKind,
                "副作用已入后端执行通道", worldId, taiwuId);
            OperationReceiptCommitState pendingVerified = StoreOperationReceipt(context, ledger, operationId, pending);
            if (OperationReceiptCommitPolicy.ResolvePrewrite(pendingVerified, default(OperationReceiptCommitState))
                != OperationReceiptCommitDecision.ProceedWithMutation)
            {
                // SetSerializableModData may have written only the receipt or only the ledger before verification failed.
                // The handler has not run yet, so first try to turn any orphan pending receipt into a proven terminal rejection.
                var rejected = BuildOperationOutcome(false, "rejected", "prewrite_incomplete_no_execution", false,
                    operationId, operationKind, "副作用预写未完整提交；已确认没有执行，并封存为拒绝终态", worldId, taiwuId);
                OperationReceiptCommitState rejectionVerified = StoreOperationReceipt(context, ledger, operationId, rejected);
                if (OperationReceiptCommitPolicy.ResolvePrewrite(default(OperationReceiptCommitState), rejectionVerified)
                    == OperationReceiptCommitDecision.ReturnFallbackTerminal) return rejected;
                return BuildOperationOutcome(false, "unknown", "prewrite_reconciliation_pending", true,
                    operationId, operationKind, "副作用尚未执行，但预写回执未能可靠终态化；请仅按同一 operation_id 查询",
                    worldId, taiwuId);
            }

                SerializableModData result;
                try
                {
                    result = handler != null ? handler(context, parameter) : null;
                }
                catch (Exception e)
                {
                    // 执行器异常时不能安全断言“一定没执行”，故不标为可重试失败。
                    result = BuildOperationOutcome(false, "unknown", "handler_exception", false, operationId, operationKind,
                        operationKind + " 执行异常:" + e.GetType().Name, worldId, taiwuId);
                }

            result = NormalizeOperationResult(result, operationId, operationKind, worldId, taiwuId);
            OperationReceiptCommitState terminalVerified = StoreOperationReceipt(context, ledger, operationId, result);
            if (OperationReceiptCommitPolicy.ResolveTerminal(terminalVerified, default(OperationReceiptCommitState))
                == OperationReceiptCommitDecision.ReturnPrimaryTerminal) return result;
            // The mutation may have landed while the full terminal receipt did not. Persist the smallest safe terminal
            // fallback before exposing a terminal result to the frontend; otherwise the frontend must keep reconciling.
            var fallback = BuildOperationOutcome(false, "unknown", "operation_terminal_store_indeterminate", false,
                operationId, operationKind, "副作用可能已落地；完整终态回执写入失败，已封存为不可重试未知",
                worldId, taiwuId);
            OperationReceiptCommitState fallbackVerified = StoreOperationReceipt(context, ledger, operationId, fallback);
            if (OperationReceiptCommitPolicy.ResolveTerminal(default(OperationReceiptCommitState), fallbackVerified)
                == OperationReceiptCommitDecision.ReturnFallbackTerminal) return fallback;
                return BuildOperationOutcome(false, "unknown", "operation_terminal_receipt_unavailable", true,
                    operationId, operationKind, "副作用可能已落地且终态回执仍不可靠读取；不得 ACK，只能按同一 operation_id 查询",
                    worldId, taiwuId);
            }
            finally
            {
                lock (InFlightOperationGate) InFlightOperationIds.Remove(operationId);
            }
        }

        private static SerializableModData QueryOperation(DataContext context, SerializableModData parameter)
        {
            string operationId = null;
            parameter?.Get(RpcConst.OperationIdField, out operationId);
            uint worldId;
            int taiwuId;
            string bindingCode, bindingMessage;
            if (!TryValidateQueryBinding(parameter, out worldId, out taiwuId, out bindingCode, out bindingMessage))
                return BuildOperationOutcome(false, "rejected", bindingCode, false, operationId, "query",
                    bindingMessage, worldId, taiwuId);
            if (!IsValidOperationId(operationId))
                return BuildOperationOutcome(false, "failed", "invalid_operation_id", false, operationId, "query",
                    "查询缺少 32 位 operation_id", worldId, taiwuId);

            OperationLedgerLoad ledgerLoad = LoadOperationLedger();
            if (!ledgerLoad.Reliable || ledgerLoad.Data == null)
                return BuildOperationOutcome(false, "unknown", "operation_ledger_unavailable", true,
                    operationId, "query", "副作用 ledger 暂时无法可靠读取，不能证明回执不存在", worldId, taiwuId);
            SerializableModData ledger = ledgerLoad.Data;
            if (!CompletePendingReceiptCompaction(context, ledger))
                return BuildOperationOutcome(false, "unknown", "operation_compaction_cleanup_pending", true,
                    operationId, "query", "旧回执压缩清理尚未可靠完成，暂不返回业务终态", worldId, taiwuId);
            SerializableModData receipt;
            bool receiptReadReliable;
            bool receiptFound = TryLoadOperationReceipt(operationId, out receipt, out receiptReadReliable);
            if (!receiptReadReliable)
                return BuildOperationOutcome(false, "unknown", "operation_receipt_read_failed", true,
                    operationId, "query", "回执存储暂时无法可靠读取，不能报告不存在", worldId, taiwuId);
            if (receiptFound && receipt != null)
            {
                bool exactBinding = StoredOperationBindingMatchesExact(receipt, worldId, taiwuId);
                if (!exactBinding)
                {
                    if (!StoredOperationBindingMatchesOrLegacy(receipt, worldId, taiwuId))
                        return BuildOperationOutcome(false, "rejected", "operation_receipt_identity_mismatch", false,
                            operationId, "query", "已有回执绑定到另一存档；已拒绝返回业务终态", worldId, taiwuId);
                    // Pre-binding protocol-v2 receipts are accepted only through this
                    // read-only query path. The caller must already possess the exact
                    // durable operation id; bind it once to that journal's original
                    // world/taiwu identity, persist+digest it, and verify readback before
                    // exposing the business result. Execute and ACK remain exact-only.
                    receipt = CloneOperationData(receipt);
                    StampOperationBinding(receipt, worldId, taiwuId);
                    OperationReceiptCommitState bound = StoreOperationReceipt(context, ledger, operationId, receipt);
                    if (!bound.ExactReceiptVerified)
                        return BuildOperationOutcome(false, "unknown", "legacy_receipt_binding_failed", true,
                            operationId, "query", "旧回执未能可靠绑定原始存档身份；请保留同一 operation_id 稍后重查",
                            worldId, taiwuId);
                }
                string receiptStatus = null, receiptKind = null;
                receipt.Get(RpcConst.OperationStatusField, out receiptStatus);
                receipt.Get("operation_kind", out receiptKind);
                if (!HasPersistedReceiptProof(receipt))
                {
                    OperationReceiptCommitState migrated = StoreOperationReceipt(context, ledger, operationId, receipt);
                    if (!migrated.ExactReceiptVerified)
                        return BuildOperationOutcome(false, "unknown", "legacy_receipt_migration_failed", true,
                            operationId, "query", "旧回执尚未能加盖持久化完整性证明；请稍后重查", worldId, taiwuId);
                }
                if (receiptStatus == "pending")
                {
                    bool inFlight;
                    lock (InFlightOperationGate) inFlight = InFlightOperationIds.Contains(operationId);
                    if (!inFlight)
                    {
                        var orphan = BuildOperationOutcome(false, "unknown", "orphaned_pending_after_restart", false,
                            operationId, string.IsNullOrWhiteSpace(receiptKind) ? "unknown" : receiptKind,
                            "pending 已无同进程执行器持有；可能在上次中断前部分落地，已封存为不可重试未知",
                            worldId, taiwuId);
                        OperationReceiptCommitState orphanStored = StoreOperationReceipt(context, ledger, operationId, orphan);
                        if (orphanStored.ExactReceiptVerified) return orphan;
                        return BuildOperationOutcome(false, "unknown", "orphan_pending_terminalization_failed", true,
                            operationId, "query", "孤儿 pending 尚未可靠终态化，请继续按同一 id 查询", worldId, taiwuId);
                    }
                }
                else StoreOperationReceipt(context, ledger, operationId, receipt);
                return CloneOperationData(receipt);
            }
            string compactedKind, compactedStatus, compactedCode;
            bool tombstoneReliable;
            if (TryFindOperationTombstone(ledger, operationId, worldId, taiwuId,
                out compactedKind, out compactedStatus, out compactedCode, out tombstoneReliable))
            {
                string priorStatus = string.IsNullOrWhiteSpace(compactedStatus) ? "rejected" : compactedStatus;
                return BuildOperationOutcome(priorStatus == "succeeded", priorStatus,
                    string.IsNullOrWhiteSpace(compactedCode) ? "operation_receipt_compacted" : compactedCode,
                    false, operationId, compactedKind ?? "query",
                    "此操作此前已有终态并由前端确认；完整回执已压缩为防重墓碑", worldId, taiwuId);
            }
            if (!tombstoneReliable)
                return BuildOperationOutcome(false, "unknown", "operation_tombstone_read_failed", true,
                    operationId, "query", "防重墓碑暂时无法可靠读取，不能报告不存在", worldId, taiwuId);
            if (OperationLedgerContains(ledger, operationId))
                return BuildOperationOutcome(false, "unknown", "indexed_operation_receipt_missing", true,
                    operationId, "query", "ledger 仍记录此 operation_id，不能把缺失回执解释为从未执行",
                    worldId, taiwuId);
            bool operationInFlight;
            lock (InFlightOperationGate) operationInFlight = InFlightOperationIds.Contains(operationId);
            if (operationInFlight)
                return BuildOperationOutcome(false, "pending", "operation_in_flight", true,
                    operationId, "query", "同一 operation_id 已由本进程执行器持有，尚不能报告回执不存在",
                    worldId, taiwuId);
            return BuildOperationOutcome(false, "unknown", "operation_not_found", true, operationId, "query",
                "后端尚无此 operation_id 回执", worldId, taiwuId);
        }

        private static SerializableModData AcknowledgeOperation(DataContext context, SerializableModData parameter)
        {
            string operationId = null;
            parameter?.Get(RpcConst.OperationIdField, out operationId);
            uint worldId;
            int taiwuId;
            string bindingCode, bindingMessage;
            if (!TryValidateAcknowledgementBinding(parameter, out worldId, out taiwuId, out bindingCode, out bindingMessage))
                return BuildOperationOutcome(false, "rejected", bindingCode, false, operationId, "ack",
                    bindingMessage, worldId, taiwuId);
            if (!IsValidOperationId(operationId))
                return BuildOperationOutcome(false, "failed", "invalid_operation_id", false, operationId, "ack",
                    "确认缺少 32 位 operation_id", worldId, taiwuId);

            OperationLedgerLoad ledgerLoad = LoadOperationLedger();
            if (!ledgerLoad.Reliable || ledgerLoad.Data == null)
                return BuildOperationOutcome(false, "unknown", "operation_ledger_unavailable", true,
                    operationId, "ack", "副作用 ledger 暂时无法可靠读取，未确认回执", worldId, taiwuId);
            SerializableModData ledger = ledgerLoad.Data;
            if (!CompletePendingReceiptCompaction(context, ledger))
                return BuildOperationOutcome(false, "unknown", "operation_compaction_cleanup_pending", true,
                    operationId, "ack", "旧回执压缩清理尚未可靠完成，ACK outbox 必须保留", worldId, taiwuId);
            SerializableModData receipt;
            bool receiptReadReliable;
            bool receiptLoaded = TryLoadOperationReceipt(operationId, out receipt, out receiptReadReliable) && receipt != null;
            if (!receiptReadReliable)
                return BuildOperationOutcome(false, "unknown", "operation_receipt_read_failed", true,
                    operationId, "ack", "回执存储暂时无法可靠读取，未执行 ACK", worldId, taiwuId);
            if (receiptLoaded && !StoredOperationBindingMatchesExact(receipt, worldId, taiwuId))
                return BuildOperationOutcome(false, "rejected", "operation_receipt_identity_mismatch", false,
                    operationId, "ack", "已有回执绑定到另一存档身份；已拒绝确认", worldId, taiwuId);
            if (!receiptLoaded)
            {
                string compactedKind, compactedStatus, compactedCode;
                bool tombstoneReliable;
                if (TryFindOperationTombstone(ledger, operationId, worldId, taiwuId,
                    out compactedKind, out compactedStatus, out compactedCode, out tombstoneReliable))
                {
                    if (compactedCode == "operation_tombstone_identity_mismatch"
                        || compactedCode == "operation_tombstone_identity_unbound")
                        return BuildOperationOutcome(false, "rejected", compactedCode, false, operationId, "ack",
                            "墓碑并未精确绑定请求的原始太吾身份，已拒绝 ACK", worldId, taiwuId);
                    return BuildOperationOutcome(true, "succeeded", "already_acknowledged", false, operationId, "ack",
                        "回执此前已经确认并压缩", worldId, taiwuId);
                }
                if (!tombstoneReliable)
                    return BuildOperationOutcome(false, "unknown", "operation_tombstone_read_failed", true,
                        operationId, "ack", "防重墓碑暂时无法可靠读取，未执行 ACK", worldId, taiwuId);
                if (OperationLedgerContains(ledger, operationId))
                    return BuildOperationOutcome(false, "unknown", "indexed_operation_receipt_missing", true,
                        operationId, "ack", "ledger 仍记录此 operation_id，但没有可精确确认的回执或墓碑；ACK 未执行",
                        worldId, taiwuId);
                return BuildOperationOutcome(false, "unknown", "operation_not_found", true, operationId, "ack",
                    "尚无可确认的 operation_id 回执", worldId, taiwuId);
            }

            string status = null, operationKind = null;
            bool retryable = false, alreadyAcknowledged = false;
            receipt.Get(RpcConst.OperationStatusField, out status);
            receipt.Get(RpcConst.OperationRetryableField, out retryable);
            receipt.Get("operation_kind", out operationKind);
            receipt.Get(OperationAcknowledgedField, out alreadyAcknowledged);
            if (alreadyAcknowledged)
            {
                OperationReceiptCommitState repaired = StoreOperationReceipt(context, ledger, operationId, receipt);
                if (!repaired.FullyIndexed)
                    return BuildOperationOutcome(false, "unknown", "operation_ack_index_repair_failed", true,
                        operationId, "ack", "回执虽已标记 ACK，但 ledger 索引未能可靠修复；outbox 必须保留", worldId, taiwuId);
                return BuildOperationOutcome(true, "succeeded", "already_acknowledged", false, operationId, "ack",
                    "回执此前已经确认", worldId, taiwuId);
            }
            bool terminal = status == "succeeded" || status == "failed" || status == "rejected" || status == "canceled"
                || (status == "unknown" && !retryable);
            if (!terminal)
                return BuildOperationOutcome(false, "rejected", "operation_not_terminal", false, operationId, "ack",
                    "pending 或仍可重试的 unknown 回执不能确认或淘汰", worldId, taiwuId);

            receipt = CloneOperationData(receipt);
            receipt.Set(OperationAcknowledgedField, true);
            receipt.Set("operation_acknowledged_utc_ticks", DateTime.UtcNow.Ticks.ToString());
            StampOperationBinding(receipt, worldId, taiwuId);
            if (!StoreOperationReceipt(context, ledger, operationId, receipt).FullyIndexed)
                return BuildOperationOutcome(false, "unknown", "operation_ack_store_failed", true, operationId, "ack",
                    "回执确认未能可靠写入存档", worldId, taiwuId);

            // ACK 本身不必立刻删除完整回执；只在容量需要时压缩，给迟到的同 id 网络包留出窗口。
            return BuildOperationOutcome(true, "succeeded", "acknowledged", false, operationId,
                "ack", "前端 durable journal 已确认终态",
                worldId, taiwuId);
        }

        private static SerializableModData NormalizeOperationResult(SerializableModData result, string operationId, string operationKind,
            uint worldId, int taiwuId)
        {
            if (result == null)
                return BuildOperationOutcome(false, "unknown", "empty_backend_result", false, operationId, operationKind,
                    "后端未返回执行结果", worldId, taiwuId);

            bool success = false;
            result.Get("success", out success);
            string legacyStatus = null, code = null, message = null;
            bool existingRetryable = false;
            result.Get(RpcConst.OperationStatusField, out legacyStatus);
            result.Get(RpcConst.OperationCodeField, out code);
            result.Get(RpcConst.OperationRetryableField, out existingRetryable);
            result.Get("message", out message);
            if (string.IsNullOrWhiteSpace(code))
            {
                if (success)
                    code = string.IsNullOrWhiteSpace(legacyStatus) || legacyStatus == "executed" || legacyStatus == "succeeded" ? "ok" : legacyStatus;
                else
                    code = string.IsNullOrWhiteSpace(legacyStatus) || legacyStatus == "failed" ? "operation_failed" : legacyStatus;
            }

            string normalizedStatus;
            if (legacyStatus == "pending" || legacyStatus == "unknown" || legacyStatus == "canceled" || legacyStatus == "rejected")
                normalizedStatus = legacyStatus;
            else
                normalizedStatus = success ? "succeeded" : "failed";
            bool normalizedRetryable = (normalizedStatus == "pending" || normalizedStatus == "unknown") && existingRetryable;
            result.Set(RpcConst.OperationStatusField, normalizedStatus);
            result.Set(RpcConst.OperationCodeField, code);
            result.Set(RpcConst.OperationRetryableField, normalizedRetryable);
            result.Set(RpcConst.OperationIdField, operationId);
            result.Set("protocol_version", RpcConst.OperationProtocolVersion);
            result.Set("operation_kind", operationKind ?? "");
            StampOperationBinding(result, worldId, taiwuId);
            result.Set(RpcConst.OperationReceiptField,
                BuildReceiptSummary(success, normalizedStatus, code, normalizedRetryable, operationId, operationKind, message));
            result.Set(RpcConst.OperationReceiptPersistedField, false);
            StampOperationReceiptIntegrity(result);
            return result;
        }

        private static SerializableModData BuildOperationOutcome(bool success, string status, string code, bool retryable,
            string operationId, string operationKind, string message, uint worldId = 0, int taiwuId = -1)
        {
            var result = new SerializableModData();
            result.Set("success", success);
            result.Set("message", message ?? "");
            result.Set(RpcConst.OperationStatusField, status ?? (success ? "succeeded" : "failed"));
            result.Set(RpcConst.OperationCodeField, code ?? (success ? "ok" : "operation_failed"));
            result.Set(RpcConst.OperationRetryableField, retryable);
            result.Set(RpcConst.OperationIdField, operationId ?? "");
            result.Set("protocol_version", RpcConst.OperationProtocolVersion);
            result.Set("operation_kind", operationKind ?? "");
            StampOperationBinding(result, worldId, taiwuId);
            result.Set(RpcConst.OperationReceiptField,
                BuildReceiptSummary(success, status, code, retryable, operationId, operationKind, message));
            result.Set(RpcConst.OperationReceiptPersistedField, false);
            StampOperationReceiptIntegrity(result);
            return result;
        }

        private static string BuildReceiptSummary(bool success, string status, string code, bool retryable,
            string operationId, string operationKind, string message)
        {
            return "status=" + (status ?? "") + ";code=" + (code ?? "") + ";retryable=" + (retryable ? "true" : "false")
                + ";operation_id=" + (operationId ?? "") + ";operation_kind=" + (operationKind ?? "") + ";message=" + (message ?? "");
        }

        private static bool TryValidateOperationBinding(SerializableModData parameter, out uint worldId, out int taiwuId,
            out string code, out string message)
        {
            worldId = 0;
            taiwuId = -1;
            code = null;
            message = null;

            string requestedWorldText = null;
            int requestedTaiwuId, requestedProtocolVersion;
            uint requestedWorldId;
            if (parameter == null
                || !parameter.Get("protocol_version", out requestedProtocolVersion)
                || requestedProtocolVersion != RpcConst.OperationProtocolVersion
                || !parameter.Get(RpcConst.OperationWorldIdField, out requestedWorldText)
                || !uint.TryParse(requestedWorldText, out requestedWorldId)
                || requestedWorldId == 0
                || !parameter.Get(RpcConst.OperationTaiwuIdField, out requestedTaiwuId)
                || requestedTaiwuId <= 0)
            {
                code = "operation_protocol_or_identity_invalid";
                message = "副作用协议必须完整绑定 protocol_version=2 与有效 WorldId/TaiwuId；缺失或错版请求已拒绝";
                try { worldId = DomainManager.World.GetWorldId(); } catch { }
                try { taiwuId = DomainManager.Taiwu.GetTaiwuCharId(); } catch { }
                return false;
            }

            try
            {
                worldId = DomainManager.World.GetWorldId();
                taiwuId = DomainManager.Taiwu.GetTaiwuCharId();
            }
            catch (Exception e)
            {
                code = "operation_identity_unavailable";
                message = "后端权威存档身份尚不可用:" + e.GetType().Name;
                return false;
            }
            if (worldId == 0 || taiwuId <= 0)
            {
                code = "operation_identity_unavailable";
                message = "后端权威存档身份尚未就绪";
                return false;
            }
            if (requestedWorldId != worldId)
            {
                code = "operation_world_mismatch";
                message = "副作用请求来自另一份存档，已拒绝";
                return false;
            }
            if (requestedTaiwuId != taiwuId)
            {
                code = "operation_taiwu_mismatch";
                message = "副作用请求绑定的太吾已变化，已拒绝";
                return false;
            }
            return true;
        }

        private static bool TryValidateAcknowledgementBinding(SerializableModData parameter, out uint worldId,
            out int taiwuId, out string code, out string message)
        {
            worldId = 0;
            taiwuId = -1;
            code = null;
            message = null;
            string requestedWorldText = null;
            uint requestedWorldId;
            int requestedTaiwuId, requestedProtocolVersion;
            if (parameter == null
                || !parameter.Get("protocol_version", out requestedProtocolVersion)
                || requestedProtocolVersion != RpcConst.OperationProtocolVersion
                || !parameter.Get(RpcConst.OperationWorldIdField, out requestedWorldText)
                || !uint.TryParse(requestedWorldText, out requestedWorldId) || requestedWorldId == 0
                || !parameter.Get(RpcConst.OperationTaiwuIdField, out requestedTaiwuId) || requestedTaiwuId <= 0)
            {
                code = "operation_protocol_or_identity_invalid";
                message = "ACK 必须完整绑定 protocol_version=2 与原始 WorldId/TaiwuId";
                try { worldId = DomainManager.World.GetWorldId(); } catch { }
                return false;
            }
            uint currentWorldId;
            try { currentWorldId = DomainManager.World.GetWorldId(); }
            catch (Exception e)
            {
                code = "operation_identity_unavailable";
                message = "后端存档身份不可用:" + e.GetType().Name;
                return false;
            }
            worldId = requestedWorldId;
            taiwuId = requestedTaiwuId;
            if (currentWorldId == 0 || currentWorldId != requestedWorldId)
            {
                code = "operation_world_mismatch";
                message = "ACK 只能清理当前 WorldId 中、按原始太吾身份绑定的回执";
                return false;
            }
            // Cleanup-only ACK intentionally permits an earlier TaiwuId in the same world.
            // Exact receipt/tombstone binding below prevents replaying it as the successor Taiwu.
            return true;
        }

        private static bool TryValidateQueryBinding(SerializableModData parameter, out uint worldId,
            out int taiwuId, out string code, out string message)
        {
            worldId = 0;
            taiwuId = -1;
            code = null;
            message = null;
            string requestedWorldText = null;
            uint requestedWorldId;
            int requestedTaiwuId, requestedProtocolVersion;
            if (parameter == null
                || !parameter.Get("protocol_version", out requestedProtocolVersion)
                || requestedProtocolVersion != RpcConst.OperationProtocolVersion
                || !parameter.Get(RpcConst.OperationWorldIdField, out requestedWorldText)
                || !uint.TryParse(requestedWorldText, out requestedWorldId) || requestedWorldId == 0
                || !parameter.Get(RpcConst.OperationTaiwuIdField, out requestedTaiwuId) || requestedTaiwuId <= 0)
            {
                code = "operation_protocol_or_identity_invalid";
                message = "只读回执查询必须完整绑定 protocol_version=2 与原始 WorldId/TaiwuId";
                try { worldId = DomainManager.World.GetWorldId(); } catch { }
                return false;
            }
            uint currentWorldId;
            try { currentWorldId = DomainManager.World.GetWorldId(); }
            catch (Exception e)
            {
                code = "operation_identity_unavailable";
                message = "后端存档身份不可用:" + e.GetType().Name;
                return false;
            }
            worldId = requestedWorldId;
            taiwuId = requestedTaiwuId;
            if (currentWorldId == 0 || currentWorldId != requestedWorldId)
            {
                code = "operation_world_mismatch";
                message = "只读回执查询只能访问当前 WorldId 中、按原始太吾身份绑定的回执";
                return false;
            }
            // Read-only reconciliation intentionally permits an earlier TaiwuId after
            // succession. Exact receipt/tombstone binding below prevents cross-identity reads.
            return true;
        }

        private static void StampOperationBinding(SerializableModData data, uint worldId, int taiwuId)
        {
            if (data == null) return;
            data.Set(RpcConst.OperationWorldIdField, worldId.ToString());
            data.Set(RpcConst.OperationTaiwuIdField, taiwuId);
        }

        private static bool StoredOperationBindingMatchesOrLegacy(SerializableModData data, uint worldId, int taiwuId)
        {
            if (data == null) return false;
            string storedWorldText = null;
            int storedTaiwuId = -1;
            bool hasWorld = data.Get(RpcConst.OperationWorldIdField, out storedWorldText);
            bool hasTaiwu = data.Get(RpcConst.OperationTaiwuIdField, out storedTaiwuId);
            if (!hasWorld && !hasTaiwu) return true; // Receipts from pre-binding protocol v2 remain queryable.
            uint storedWorldId;
            return hasWorld && hasTaiwu && uint.TryParse(storedWorldText, out storedWorldId)
                && storedWorldId == worldId && storedTaiwuId == taiwuId;
        }

        private static bool StoredOperationBindingIsLegacyUnbound(SerializableModData data)
        {
            if (data == null) return false;
            string worldText;
            int taiwuId;
            return !data.Get(RpcConst.OperationWorldIdField, out worldText)
                && !data.Get(RpcConst.OperationTaiwuIdField, out taiwuId);
        }

        private static bool StoredOperationBindingMatchesExact(SerializableModData data, uint worldId, int taiwuId)
        {
            if (data == null || worldId == 0 || taiwuId <= 0) return false;
            string storedWorldText = null;
            int storedTaiwuId = -1;
            uint storedWorldId;
            return data.Get(RpcConst.OperationWorldIdField, out storedWorldText)
                && data.Get(RpcConst.OperationTaiwuIdField, out storedTaiwuId)
                && uint.TryParse(storedWorldText, out storedWorldId)
                && storedWorldId == worldId && storedTaiwuId == taiwuId;
        }

        private static bool ValidateGroupPhysicalEnvelope(SerializableModData parameter, string operationKind,
            int boundTaiwuId,
            out string code, out string message)
        {
            code = null;
            message = null;
            int enabled = 0;
            if (parameter == null || !parameter.Get(RpcConst.OperationGroupPhysicalGuardField, out enabled)
                || enabled == 0) return true;
            int actorId = 0;
            int actorScene = 0;
            string endpointsCsv = null;
            if (!parameter.Get(RpcConst.OperationGroupActorIdField, out actorId) || actorId <= 0
                || !parameter.Get(RpcConst.OperationGroupPhysicalEndpointsField, out endpointsCsv)
                || string.IsNullOrWhiteSpace(endpointsCsv))
            {
                code = "group_physical_envelope_invalid";
                message = "群聊物理动作缺少完整的权威复核端点";
                return false;
            }
            parameter.Get(RpcConst.OperationGroupPhysicalActorSceneField, out actorScene);
            int currentTaiwuId;
            Character taiwu;
            try
            {
                currentTaiwuId = DomainManager.Taiwu.GetTaiwuCharId();
                if (currentTaiwuId != boundTaiwuId
                    || !DomainManager.Character.TryGetElement_Objects(currentTaiwuId, out taiwu) || taiwu == null)
                {
                    code = "group_physical_taiwu_changed";
                    message = "群聊动作提交时太吾身份已变化";
                    return false;
                }
            }
            catch
            {
                code = "group_physical_presence_unavailable";
                message = "群聊动作提交时无法读取太吾现场";
                return false;
            }

            var endpoints = new HashSet<int>();
            foreach (string raw in endpointsCsv.Split(','))
            {
                int id;
                if (!int.TryParse((raw ?? string.Empty).Trim(), out id) || id <= 0 || !endpoints.Add(id))
                {
                    code = "group_physical_envelope_invalid";
                    message = "群聊物理动作端点格式无效或重复";
                    return false;
                }
            }
            if (!endpoints.Contains(actorId))
            {
                code = "group_physical_actor_missing";
                message = "群聊物理动作未把实际行动者列入复核端点";
                return false;
            }
            if (!ValidateGroupPhysicalPayload(parameter, operationKind, boundTaiwuId,
                actorId, endpoints, out code, out message)) return false;
            foreach (int endpointId in endpoints)
            {
                if (actorScene == 0 && endpointId == currentTaiwuId) continue;
                Character endpoint;
                if (!DomainManager.Character.TryGetElement_Objects(endpointId, out endpoint) || endpoint == null)
                {
                    code = "group_physical_endpoint_missing";
                    message = "群聊物理动作对象在提交时已失效:#" + endpointId;
                    return false;
                }
                bool present = actorScene != 0
                    ? IsAtActorScene(endpointId, actorId, endpoint)
                    : IsAtTaiwuScene(endpointId, currentTaiwuId, endpoint, taiwu);
                if (!present)
                {
                    code = "group_physical_presence_changed";
                    message = actorScene != 0
                        ? "月度人物物理动作对象在前端校验后已离开行动者所在地点:#" + endpointId
                        : "群聊物理动作对象在前端校验后已离开同块且不再是同道:#" + endpointId;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Authoritative physical-presence rule shared by target resolution and the
        /// pre-mutation race check. Companions (同道) and characters currently carried
        /// captive by Taiwu are treated as travelling in Taiwu's scene; everyone else
        /// must have the exact same valid engine Location as Taiwu.
        /// </summary>
        private static bool IsAtTaiwuScene(int charId, int taiwuId,
            Character endpoint = null, Character taiwu = null)
        {
            if (charId <= 0 || taiwuId <= 0) return false;
            bool isTaiwu = charId == taiwuId;
            bool isCompanion = false;
            try { isCompanion = DomainManager.Taiwu.IsInGroup(charId); }
            catch { }
            if (TaiwuScenePresencePolicy.IsPresent(isTaiwu, isCompanion, false, false, false))
                return true;
            // 固定模板人物第一次见面时原实体就在玩家面前，但新建副本可能没有合法
            // Location。首次成功入队前延续这次当面现场；首次入队后不再走该兜底。
            if (IsCharacterProxyAwaitingFirstJoin(charId)) return true;
            try
            {
                if (endpoint == null
                    && (!DomainManager.Character.TryGetElement_Objects(charId, out endpoint) || endpoint == null))
                    return false;
                bool kidnappedByTaiwu = endpoint.GetKidnapperId() == taiwuId;
                bool inTaiwuVillagePrison =
                    DomainManager.Organization.GetPrisonerSect(charId) == (sbyte)16;
                if (taiwu == null
                    && (!DomainManager.Character.TryGetElement_Objects(taiwuId, out taiwu) || taiwu == null))
                    return TaiwuScenePresencePolicy.IsPresent(false, false,
                        kidnappedByTaiwu, false, false);
                Location endpointLocation = GetPhysicalSceneLocation(endpoint);
                Location taiwuLocation = GetPhysicalSceneLocation(taiwu);
                Location villageLocation = DomainManager.Taiwu.GetTaiwuVillageLocation();
                bool inTaiwuVillagePrisonWithTaiwuPresent = inTaiwuVillagePrison
                    && taiwuLocation.IsValid() && villageLocation.IsValid()
                    && taiwuLocation.Equals(villageLocation);
                bool sameValidLocation = endpointLocation.IsValid() && taiwuLocation.IsValid()
                    && endpointLocation.Equals(taiwuLocation);
                return TaiwuScenePresencePolicy.IsPresent(false, false,
                    kidnappedByTaiwu, inTaiwuVillagePrisonWithTaiwuPresent,
                    sameValidLocation);
            }
            catch { return false; }
        }

        private static bool IsAtActorScene(int charId, int actorId, Character endpoint = null)
        {
            if (charId <= 0 || actorId <= 0) return false;
            if (charId == actorId) return true;
            try
            {
                Character actor;
                if (!DomainManager.Character.TryGetElement_Objects(actorId, out actor) || actor == null)
                    return false;
                if (endpoint == null
                    && (!DomainManager.Character.TryGetElement_Objects(charId, out endpoint) || endpoint == null))
                    return false;
                if (endpoint.GetKidnapperId() == actorId) return true;
                Location actorLocation = GetPhysicalSceneLocation(actor);
                Location endpointLocation = GetPhysicalSceneLocation(endpoint);
                return actorLocation.IsValid() && endpointLocation.IsValid()
                    && actorLocation.Equals(endpointLocation);
            }
            catch { return false; }
        }

        private static void AddAllCharactersAtTaiwuScene(int taiwuId, Character taiwu,
            ISet<int> destination)
        {
            if (destination == null || taiwuId <= 0) return;
            try
            {
                var all = DomainManager.Character.GmCmd_GetAllCharacterName();
                if (all == null) return;
                foreach (var entry in all)
                {
                    int cid = entry.CharId;
                    if (cid <= 0 || !IsValidSceneCharacter(cid)) continue;
                    Character person = null;
                    if (cid != taiwuId
                        && (!DomainManager.Character.TryGetElement_Objects(cid, out person)
                            || person == null))
                        continue;
                    if (IsAtTaiwuScene(cid, taiwuId, person, taiwu)) destination.Add(cid);
                }
            }
            catch { }
        }

        private static void AddAllCharactersAtActorScene(int actorId, ISet<int> destination)
        {
            if (destination == null || actorId <= 0) return;
            try
            {
                var all = DomainManager.Character.GmCmd_GetAllCharacterName();
                if (all == null) return;
                foreach (var entry in all)
                {
                    int cid = entry.CharId;
                    if (cid <= 0 || !IsValidSceneCharacter(cid)) continue;
                    Character person = null;
                    if (cid != actorId
                        && (!DomainManager.Character.TryGetElement_Objects(cid, out person)
                            || person == null))
                        continue;
                    if (IsAtActorScene(cid, actorId, person)) destination.Add(cid);
                }
            }
            catch { }
        }

        /// <summary>
        /// Bind the presence envelope to the actual authoritative handler payload.  The
        /// frontend may resolve a model-supplied name while preparing the group guard and
        /// resolve it again while building the RPC; only this backend comparison prevents
        /// a changed/ambiguous second resolution from mutating a different character.
        /// </summary>
        private static bool ValidateGroupPhysicalPayload(SerializableModData parameter,
            string operationKind, int boundTaiwuId, int envelopeActorId, HashSet<int> envelopeEndpoints,
            out string code, out string message)
        {
            code = null;
            message = null;
            if (parameter == null || string.IsNullOrWhiteSpace(operationKind)
                || envelopeEndpoints == null || envelopeEndpoints.Count == 0)
                return GroupPayloadMismatch("group_physical_payload_invalid",
                    "群聊现场信封无法绑定到实际动作参数", out code, out message);

            int first = 0, second = 0, taiwu = 0, optional = 0;
            int expectedActor = 0;
            var expected = new HashSet<int>();
            switch (operationKind)
            {
                case "gm:givesilver":
                case "gm:giveitem":
                    if (!PositiveInt(parameter, "npc", out first)
                        || !PositiveInt(parameter, "taiwu", out taiwu) || taiwu != boundTaiwuId)
                        return GroupPayloadInvalidTaiwu(out code, out message);
                    parameter.Get("recipient", out optional);
                    second = optional > 0 ? optional : taiwu;
                    expectedActor = first;
                    break;
                case "gm:barter":
                    if (!PositiveInt(parameter, "a", out first) || !PositiveInt(parameter, "b", out second))
                        return GroupPayloadMissing(out code, out message);
                    expectedActor = first;
                    break;
                case "gm:steal":
                    if (!PositiveInt(parameter, "thief", out first) || !PositiveInt(parameter, "victim", out second))
                        return GroupPayloadMissing(out code, out message);
                    expectedActor = first;
                    break;
                case RpcConst.TeachSkillMethod:
                    if (!PositiveInt(parameter, "npc_id", out first)
                        || !PositiveInt(parameter, "taiwu_id", out taiwu) || taiwu != boundTaiwuId)
                        return GroupPayloadInvalidTaiwu(out code, out message);
                    parameter.Get("learner_id", out optional);
                    second = optional > 0 ? optional : taiwu;
                    expectedActor = first;
                    break;
                case "gm:teachlife":
                    if (!PositiveInt(parameter, "npc", out first)
                        || !PositiveInt(parameter, "taiwu", out taiwu) || taiwu != boundTaiwuId)
                        return GroupPayloadInvalidTaiwu(out code, out message);
                    parameter.Get("recipient", out optional);
                    second = optional > 0 ? optional : taiwu;
                    expectedActor = first;
                    break;
                case "gm:writebook":
                    if (!PositiveInt(parameter, "npc", out first)
                        || !PositiveInt(parameter, "taiwu", out taiwu) || taiwu != boundTaiwuId)
                        return GroupPayloadInvalidTaiwu(out code, out message);
                    parameter.Get("recipient", out optional);
                    second = optional > 0 ? optional : taiwu;
                    // taiwu_write_book writes npc=taiwu and recipient=the speaking member.
                    expectedActor = first == boundTaiwuId && optional > 0 ? optional : first;
                    break;
                case RpcConst.ExecuteRelationMethod:
                    if (!PositiveInt(parameter, "npc_id", out first)
                        || !PositiveInt(parameter, "taiwu_id", out second) || second != boundTaiwuId)
                        return GroupPayloadInvalidTaiwu(out code, out message);
                    expectedActor = first;
                    break;
                case "gm:spend_night":
                    if (!PositiveInt(parameter, "npc", out first)
                        || !PositiveInt(parameter, "taiwu", out taiwu) || taiwu != boundTaiwuId)
                        return GroupPayloadInvalidTaiwu(out code, out message);
                    parameter.Get("target", out optional);
                    second = optional > 0 ? optional : taiwu;
                    expectedActor = first;
                    break;
                case "gm:dissolve":
                    if (!PositiveInt(parameter, "npc", out first)
                        || !PositiveInt(parameter, "taiwu", out taiwu) || taiwu != boundTaiwuId
                        || !PositiveInt(parameter, "target", out second))
                        return GroupPayloadInvalidTaiwu(out code, out message);
                    expectedActor = first;
                    break;
                case "gm:relate_npc":
                    if (!PositiveInt(parameter, "a", out first) || !PositiveInt(parameter, "b", out second))
                        return GroupPayloadMissing(out code, out message);
                    expectedActor = first;
                    break;
                case "gm:enmity":
                    if (!PositiveInt(parameter, "npc", out first) || !PositiveInt(parameter, "target", out second))
                        return GroupPayloadMissing(out code, out message);
                    expectedActor = first;
                    break;
                case RpcConst.MatchmakeMethod:
                    if (!PositiveInt(parameter, "a_id", out first) || !PositiveInt(parameter, "b_id", out second))
                        return GroupPayloadMissing(out code, out message);
                    expectedActor = first;
                    break;
                case RpcConst.KillMethod:
                case RpcConst.CaptureMethod:
                    if (!PositiveInt(parameter, "npc_id", out first) || !PositiveInt(parameter, "target_id", out second))
                        return GroupPayloadMissing(out code, out message);
                    expectedActor = first;
                    break;
                case "gm:poison":
                    if (!PositiveInt(parameter, "actor", out first) || !PositiveInt(parameter, "target", out second))
                        return GroupPayloadMissing(out code, out message);
                    expectedActor = first;
                    break;
                case "gm:heal":
                    if (!PositiveInt(parameter, "healer", out first) || !PositiveInt(parameter, "target", out second))
                        return GroupPayloadMissing(out code, out message);
                    expectedActor = first;
                    break;
                case "gm:trade":
                case "gm:taiwu_give_item":
                case "gm:taiwu_teach":
                    if (!PositiveInt(parameter, "npc", out first)
                        || !PositiveInt(parameter, "taiwu", out second) || second != boundTaiwuId)
                        return GroupPayloadInvalidTaiwu(out code, out message);
                    expectedActor = first;
                    break;
                case "gm:addfeature":
                    if (!PositiveInt(parameter, "npc", out first))
                        return GroupPayloadMissing(out code, out message);
                    // 同道月度 add_feature 是行动者改变自身；Taiwu 作为该现场
                    // 信封的权威锚点，不是被修改对象。
                    second = boundTaiwuId;
                    expectedActor = first;
                    break;
                case "gm:flip_practice":
                    if (!PositiveInt(parameter, "char", out second))
                        return GroupPayloadMissing(out code, out message);
                    first = envelopeActorId;
                    // 同道月度自改练法使用 actor+Taiwu 现场信封；群聊替另一位
                    // 参与者改练法仍保持 actor+target 两个真实端点。
                    if (second == envelopeActorId) second = boundTaiwuId;
                    expectedActor = envelopeActorId;
                    break;
                case "gm:equip":
                case "gm:takeoff":
                case "gm:merchantfavor":
                    if (!PositiveInt(parameter, "npc", out first))
                        return GroupPayloadMissing(out code, out message);
                    second = boundTaiwuId;
                    expectedActor = first;
                    break;
                case RpcConst.StartCombatMethod:
                    // 原生 GmCmd_FightCharacter 的 payload 只携带对手 id；对话中的
                    // speaking NPC 必须就是该目标，Taiwu 则是现场第二端点。
                    if (!PositiveInt(parameter, "target_id", out first))
                        return GroupPayloadMissing(out code, out message);
                    second = boundTaiwuId;
                    expectedActor = first;
                    break;
                default:
                    return GroupPayloadMismatch("group_physical_operation_unmapped",
                        "群聊物理动作尚无后端载荷绑定规则:" + operationKind, out code, out message);
            }

            if (first <= 0 || second <= 0 || first == second)
                return GroupPayloadMismatch("group_physical_payload_invalid",
                    "群聊动作的实际角色端点无效", out code, out message);
            expected.Add(first);
            expected.Add(second);
            if (expectedActor != envelopeActorId || !expected.SetEquals(envelopeEndpoints))
                return GroupPayloadMismatch("group_physical_payload_mismatch",
                    "群聊现场信封与实际动作角色不一致，动作已拒绝", out code, out message);
            return true;
        }

        private static bool PositiveInt(SerializableModData data, string name, out int value)
        {
            value = 0;
            return data != null && data.Get(name, out value) && value > 0;
        }

        private static bool GroupPayloadMissing(out string code, out string message)
            => GroupPayloadMismatch("group_physical_payload_missing",
                "群聊动作缺少可与现场信封核对的角色参数", out code, out message);

        private static bool GroupPayloadInvalidTaiwu(out string code, out string message)
            => GroupPayloadMismatch("group_physical_payload_taiwu_mismatch",
                "群聊动作参数中的太吾身份与 operation 绑定不一致", out code, out message);

        private static bool GroupPayloadMismatch(string value, string text,
            out string code, out string message)
        {
            code = value;
            message = text;
            return false;
        }

        private static OperationLedgerLoad LoadOperationLedger()
        {
            string modId = string.IsNullOrWhiteSpace(_modIdStr) ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                SerializableModData loaded;
                bool found = DomainManager.Mod.TryGet(modId, OperationLedgerDataName, true, out loaded);
                if (!found)
                    return new OperationLedgerLoad { Reliable = true, Exists = false, Data = new SerializableModData() };
                if (loaded == null)
                    return new OperationLedgerLoad { Reliable = false, Exists = true, Data = null };
                SerializableModData clone = CloneOperationData(loaded);
                if (!ValidateOperationLedger(clone))
                    return new OperationLedgerLoad { Reliable = false, Exists = true, Data = null };
                string integrity;
                if (!clone.Get(OperationLedgerIntegrityField, out integrity))
                {
                    // One-time migration for ledgers written by the immediately preceding
                    // bounded-tombstone schema. Their complete structural checks already
                    // passed above; every subsequent write persists this in-memory seal.
                    if (!StampOperationLedgerIntegrity(clone))
                        return new OperationLedgerLoad { Reliable = false, Exists = true, Data = null };
                }
                return new OperationLedgerLoad { Reliable = true, Exists = true, Data = clone };
            }
            catch
            {
                return new OperationLedgerLoad { Reliable = false, Exists = false, Data = null };
            }
        }

        private static bool ValidateOperationLedger(SerializableModData ledger)
        {
            if (ledger == null) return false;
            string storedIntegrity = null;
            bool hasIntegrity = ledger.Get(OperationLedgerIntegrityField, out storedIntegrity);
            // Missing integrity is the explicitly supported one-time predecessor schema;
            // LoadOperationLedger seals it in memory after all structural checks pass.
            if (hasIntegrity)
            {
                string computed = CanonicalOperationPayloadDigest(ledger, OperationLedgerIntegrityField);
                if (!IsSha256Hex(storedIntegrity) || string.IsNullOrEmpty(computed)
                    || !string.Equals(storedIntegrity, computed, StringComparison.Ordinal)) return false;
            }
            string orderText = null;
            int count = -1, protocolVersion = -1;
            if (!ledger.Get("order", out orderText) || !ledger.Get("count", out count)
                || !ledger.Get("protocol_version", out protocolVersion)
                || count < 0 || protocolVersion != RpcConst.OperationProtocolVersion) return false;
            if (string.IsNullOrEmpty(orderText) && count != 0) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(orderText))
            {
                string[] values = orderText.Split('\n');
                if (values.Length != count) return false;
                foreach (string raw in values)
                {
                    string operationId = (raw ?? string.Empty).Trim();
                    if (!IsValidOperationId(operationId) || !seen.Add(operationId)) return false;
                }
            }
            if (seen.Count != count) return false;
            List<OperationTombstoneEntry> tombstones;
            byte[] bloom;
            bool initialized;
            List<string> pendingCompaction;
            return TryParseBoundedTombstones(ledger, out tombstones, out bloom, out initialized)
                && TryParseCompactionPending(ledger, out pendingCompaction);
        }

        private static bool TryParseCompactionPending(SerializableModData ledger, out List<string> operationIds)
        {
            operationIds = new List<string>();
            if (ledger == null) return false;
            string text;
            if (!ledger.Get(OperationCompactionPendingField, out text) || string.IsNullOrEmpty(text)) return true;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                string id = (raw ?? string.Empty).Trim();
                if (!IsValidOperationId(id) || !seen.Add(id)
                    || operationIds.Count >= OperationLedgerCapacity) return false;
                operationIds.Add(id);
            }
            return true;
        }

        private static bool TryParseBoundedTombstones(SerializableModData ledger,
            out List<OperationTombstoneEntry> entries, out byte[] bloom, out bool initialized)
        {
            entries = new List<OperationTombstoneEntry>();
            bloom = null;
            initialized = false;
            if (ledger == null) return false;
            int version = -1, count = -1, bloomBits = -1, bloomHashes = -1, bloomChunks = -1;
            string text = null;
            bool hasVersion = ledger.Get(OperationTombstoneVersionField, out version);
            bool hasCount = ledger.Get(OperationTombstoneCountField, out count);
            bool hasText = ledger.Get(OperationTombstoneIndexField, out text);
            bool hasBloomBits = ledger.Get(OperationTombstoneBloomBitsField, out bloomBits);
            bool hasBloomHashes = ledger.Get(OperationTombstoneBloomHashesField, out bloomHashes);
            bool hasBloomChunks = ledger.Get(OperationTombstoneBloomChunksField, out bloomChunks);
            var chunkTexts = new string[OperationTombstoneBloomChunkCount];
            int presentChunks = 0;
            for (int i = 0; i < chunkTexts.Length; i++)
                if (ledger.Get(OperationTombstoneBloomField + "_" + i, out chunkTexts[i])) presentChunks++;
            int present = (hasVersion ? 1 : 0) + (hasCount ? 1 : 0) + (hasText ? 1 : 0)
                + (hasBloomBits ? 1 : 0) + (hasBloomHashes ? 1 : 0) + (hasBloomChunks ? 1 : 0)
                + presentChunks;
            if (present == 0) return true; // Lazy upgrade of a pre-index ledger.
            if (present != 6 + OperationTombstoneBloomChunkCount
                || version != OperationTombstoneIndexVersion
                || count < 0 || count > OperationRecentTombstoneCapacity
                || bloomBits != OperationTombstoneBloomBitCount
                || bloomHashes != OperationTombstoneBloomHashCount
                || bloomChunks != OperationTombstoneBloomChunkCount) return false;
            bloom = new byte[OperationTombstoneBloomByteCount];
            for (int i = 0; i < chunkTexts.Length; i++)
            {
                byte[] chunk;
                try { chunk = Convert.FromBase64String(chunkTexts[i] ?? string.Empty); }
                catch { return false; }
                if (chunk == null || chunk.Length != OperationTombstoneBloomChunkBytes) return false;
                Buffer.BlockCopy(chunk, 0, bloom, i * OperationTombstoneBloomChunkBytes, chunk.Length);
            }

            if (string.IsNullOrEmpty(text))
            {
                if (count != 0) return false;
                initialized = true;
                return true;
            }
            string[] lines = text.Split('\n');
            if (lines.Length != count) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                string[] fields = (line ?? string.Empty).Split('|');
                uint worldId;
                int taiwuId;
                if (fields.Length != 6 || !IsValidOperationId(fields[0]) || !ids.Add(fields[0])
                    || !SafeTombstoneField(fields[1], 48) || !IsTerminalOperationStatus(fields[2])
                    || !SafeTombstoneField(fields[3], 96)
                    || !uint.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out worldId)
                    || !int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out taiwuId)
                    || !((worldId == 0 && taiwuId == 0) || (worldId > 0 && taiwuId > 0))) return false;
                if (!BloomMayContain(bloom, fields[0])) return false;
                entries.Add(new OperationTombstoneEntry
                {
                    OperationId = fields[0], OperationKind = fields[1], Status = fields[2],
                    Code = fields[3], WorldId = worldId, TaiwuId = taiwuId,
                });
            }
            initialized = true;
            return true;
        }

        private static bool AppendBoundedOperationTombstone(SerializableModData ledger, string operationId,
            string operationKind, string status, string code, uint worldId, int taiwuId)
        {
            if (ledger == null || !IsValidOperationId(operationId)
                || !SafeTombstoneField(operationKind, 48) || !IsTerminalOperationStatus(status)
                || !SafeTombstoneField(code, 96)
                || !((worldId == 0 && taiwuId == 0) || (worldId > 0 && taiwuId > 0))) return false;
            List<OperationTombstoneEntry> entries;
            byte[] bloom;
            bool initialized;
            if (!TryParseBoundedTombstones(ledger, out entries, out bloom, out initialized)) return false;
            if (!initialized) bloom = new byte[OperationTombstoneBloomByteCount];
            entries.RemoveAll(entry => entry != null
                && string.Equals(entry.OperationId, operationId, StringComparison.Ordinal));
            entries.Add(new OperationTombstoneEntry
            {
                OperationId = operationId, OperationKind = operationKind, Status = status, Code = code,
                WorldId = worldId, TaiwuId = taiwuId,
            });
            BloomAdd(bloom, operationId);
            while (entries.Count > OperationRecentTombstoneCapacity) entries.RemoveAt(0);
            try { WriteBoundedTombstones(ledger, entries, bloom); return true; }
            catch { return false; }
        }

        private static void WriteBoundedTombstones(SerializableModData ledger,
            List<OperationTombstoneEntry> entries, byte[] bloom)
        {
            var lines = new List<string>();
            if (entries != null)
                foreach (OperationTombstoneEntry entry in entries)
                    lines.Add(entry.OperationId + "|" + entry.OperationKind + "|" + entry.Status + "|"
                        + entry.Code + "|" + entry.WorldId.ToString(CultureInfo.InvariantCulture) + "|"
                        + entry.TaiwuId.ToString(CultureInfo.InvariantCulture));
            ledger.Set(OperationTombstoneVersionField, OperationTombstoneIndexVersion);
            ledger.Set(OperationTombstoneCountField, lines.Count);
            string index = string.Join("\n", lines);
            if (index.Length > 60000) throw new InvalidOperationException("bounded tombstone index exceeds string limit");
            ledger.Set(OperationTombstoneIndexField, index);
            ledger.Set(OperationTombstoneBloomChunksField, OperationTombstoneBloomChunkCount);
            for (int i = 0; i < OperationTombstoneBloomChunkCount; i++)
            {
                var chunk = new byte[OperationTombstoneBloomChunkBytes];
                Buffer.BlockCopy(bloom, i * OperationTombstoneBloomChunkBytes, chunk, 0, chunk.Length);
                string encoded = Convert.ToBase64String(chunk);
                if (encoded.Length > ushort.MaxValue) throw new InvalidOperationException("Bloom chunk exceeds string limit");
                ledger.Set(OperationTombstoneBloomField + "_" + i, encoded);
            }
            ledger.Set(OperationTombstoneBloomBitsField, OperationTombstoneBloomBitCount);
            ledger.Set(OperationTombstoneBloomHashesField, OperationTombstoneBloomHashCount);
        }

        private static bool SafeTombstoneField(string value, int maxLength)
            => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && value.IndexOf('|') < 0
                && value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0;

        private static bool IsTerminalOperationStatus(string status)
            => status == "succeeded" || status == "failed" || status == "rejected"
                || status == "canceled" || status == "unknown";

        private static void BloomAdd(byte[] bloom, string operationId)
        {
            if (bloom == null || bloom.Length != OperationTombstoneBloomByteCount) return;
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.ASCII.GetBytes(operationId));
            ulong first = ReadUInt64LittleEndian(digest, 0);
            ulong step = ReadUInt64LittleEndian(digest, 8) | 1UL;
            for (int i = 0; i < OperationTombstoneBloomHashCount; i++)
            {
                int bit = (int)((first + (ulong)i * step) % (ulong)OperationTombstoneBloomBitCount);
                bloom[bit >> 3] |= (byte)(1 << (bit & 7));
            }
        }

        private static bool BloomMayContain(byte[] bloom, string operationId)
        {
            if (bloom == null || bloom.Length != OperationTombstoneBloomByteCount
                || !IsValidOperationId(operationId)) return false;
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.ASCII.GetBytes(operationId));
            ulong first = ReadUInt64LittleEndian(digest, 0);
            ulong step = ReadUInt64LittleEndian(digest, 8) | 1UL;
            for (int i = 0; i < OperationTombstoneBloomHashCount; i++)
            {
                int bit = (int)((first + (ulong)i * step) % (ulong)OperationTombstoneBloomBitCount);
                if ((bloom[bit >> 3] & (1 << (bit & 7))) == 0) return false;
            }
            return true;
        }

        private static ulong ReadUInt64LittleEndian(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++) value |= (ulong)bytes[offset + i] << (i * 8);
            return value;
        }

        private static bool TryLoadOperationReceipt(string operationId, out SerializableModData receipt,
            out bool reliable)
        {
            receipt = null;
            reliable = false;
            if (!IsValidOperationId(operationId)) return false;
            string modId = string.IsNullOrWhiteSpace(_modIdStr) ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                SerializableModData loaded;
                bool found = DomainManager.Mod.TryGet(modId, OperationReceiptDataName(operationId), true, out loaded);
                if (!found) { reliable = true; return false; }
                if (loaded == null) return false;
                receipt = CloneOperationData(loaded);
                if (!ValidateLoadedOperationReceipt(operationId, receipt))
                {
                    receipt = null;
                    return false;
                }
                reliable = true;
                return true;
            }
            catch { receipt = null; reliable = false; return false; }
        }

        private static OperationReceiptCommitState StoreOperationReceipt(DataContext context, SerializableModData ledger,
            string operationId, SerializableModData receipt)
        {
            var state = new OperationReceiptCommitState(false, false);
            if (context == null || ledger == null || !IsValidOperationId(operationId) || receipt == null) return state;
            receipt.Set(RpcConst.OperationReceiptPersistedField, true);
            if (!StampOperationReceiptIntegrity(receipt)
                || !ValidateLoadedOperationReceipt(operationId, receipt)) return state;
            string modId = string.IsNullOrWhiteSpace(_modIdStr) ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                DomainManager.Mod.SetSerializableModData(context, modId,
                    OperationReceiptDataName(operationId), true, receipt);
            }
            catch { return state; }

            try
            {
                SerializableModData receiptCheck;
                if (!DomainManager.Mod.TryGet(modId, OperationReceiptDataName(operationId), true, out receiptCheck)
                    || receiptCheck == null
                    || !OperationReceiptSemanticallyMatches(receipt, receiptCheck, operationId)) return state;
                state = new OperationReceiptCommitState(true, false);
            }
            catch { return state; }

            string orderText = null;
            ledger.Get("order", out orderText);
            var order = ParseOperationOrder(orderText);
            order.Remove(operationId);
            order.Add(operationId);
            try
            {
                ledger.Set("order", string.Join("\n", order));
                ledger.Set("count", order.Count);
                ledger.Set("protocol_version", RpcConst.OperationProtocolVersion);
                if (!StampOperationLedgerIntegrity(ledger)) return state;
                DomainManager.Mod.SetSerializableModData(context, modId, OperationLedgerDataName, true, ledger);

                SerializableModData ledgerCheck;
                if (!DomainManager.Mod.TryGet(modId, OperationLedgerDataName, true, out ledgerCheck)
                    || ledgerCheck == null) return state;
                return new OperationReceiptCommitState(true,
                    OperationLedgerSemanticallyMatches(ledger, ledgerCheck));
            }
            catch { return state; }
        }

        private static bool OperationLedgerSemanticallyMatches(SerializableModData expected,
            SerializableModData persisted)
        {
            if (expected == null || persisted == null || !ValidateOperationLedger(persisted)) return false;
            string expectedDigest = CanonicalOperationPayloadDigest(expected);
            string persistedDigest = CanonicalOperationPayloadDigest(persisted);
            return !string.IsNullOrEmpty(expectedDigest)
                && string.Equals(expectedDigest, persistedDigest, StringComparison.Ordinal);
        }

        private static bool OperationReceiptSemanticallyMatches(SerializableModData expected,
            SerializableModData persisted, string operationId)
        {
            if (expected == null || persisted == null || !IsValidOperationId(operationId)
                || !ValidateLoadedOperationReceipt(operationId, expected)
                || !ValidateLoadedOperationReceipt(operationId, persisted)) return false;
            string expectedId = null, persistedId = null;
            string expectedStatus = null, persistedStatus = null;
            string expectedKind = null, persistedKind = null;
            string expectedWorld = null, persistedWorld = null;
            string expectedCode = null, persistedCode = null;
            int expectedTaiwu = -1, persistedTaiwu = -1;
            bool expectedRetryable = false, persistedRetryable = false;
            expected.Get(RpcConst.OperationIdField, out expectedId);
            persisted.Get(RpcConst.OperationIdField, out persistedId);
            expected.Get(RpcConst.OperationStatusField, out expectedStatus);
            persisted.Get(RpcConst.OperationStatusField, out persistedStatus);
            expected.Get("operation_kind", out expectedKind);
            persisted.Get("operation_kind", out persistedKind);
            expected.Get(RpcConst.OperationWorldIdField, out expectedWorld);
            persisted.Get(RpcConst.OperationWorldIdField, out persistedWorld);
            expected.Get(RpcConst.OperationTaiwuIdField, out expectedTaiwu);
            persisted.Get(RpcConst.OperationTaiwuIdField, out persistedTaiwu);
            expected.Get(RpcConst.OperationCodeField, out expectedCode);
            persisted.Get(RpcConst.OperationCodeField, out persistedCode);
            expected.Get(RpcConst.OperationRetryableField, out expectedRetryable);
            persisted.Get(RpcConst.OperationRetryableField, out persistedRetryable);
            if (!string.Equals(expectedId, operationId, StringComparison.Ordinal)
                || !string.Equals(persistedId, operationId, StringComparison.Ordinal)
                || !string.Equals(expectedStatus, persistedStatus, StringComparison.Ordinal)
                || !string.Equals(expectedKind, persistedKind, StringComparison.Ordinal)
                || !string.Equals(expectedWorld, persistedWorld, StringComparison.Ordinal)
                || expectedTaiwu != persistedTaiwu
                || !string.Equals(expectedCode, persistedCode, StringComparison.Ordinal)
                || expectedRetryable != persistedRetryable) return false;

            bool expectedAcknowledged = false, persistedAcknowledged = false;
            bool expectedHasAcknowledged = expected.Get(OperationAcknowledgedField, out expectedAcknowledged);
            bool persistedHasAcknowledged = persisted.Get(OperationAcknowledgedField, out persistedAcknowledged);
            bool expectedSuccess = false, persistedSuccess = false;
            string expectedMessage = null, persistedMessage = null;
            string expectedSummary = null, persistedSummary = null;
            int expectedProtocol = -1, persistedProtocol = -1;
            bool requiredPayloadMatches = expected.Get("success", out expectedSuccess)
                && persisted.Get("success", out persistedSuccess) && expectedSuccess == persistedSuccess
                && expected.Get("message", out expectedMessage) && persisted.Get("message", out persistedMessage)
                && string.Equals(expectedMessage, persistedMessage, StringComparison.Ordinal)
                && expected.Get(RpcConst.OperationReceiptField, out expectedSummary)
                && persisted.Get(RpcConst.OperationReceiptField, out persistedSummary)
                && string.Equals(expectedSummary, persistedSummary, StringComparison.Ordinal)
                && expected.Get("protocol_version", out expectedProtocol)
                && persisted.Get("protocol_version", out persistedProtocol)
                && expectedProtocol == RpcConst.OperationProtocolVersion && persistedProtocol == expectedProtocol;
            if (!requiredPayloadMatches || expectedHasAcknowledged != persistedHasAcknowledged
                || (expectedHasAcknowledged && expectedAcknowledged != persistedAcknowledged)) return false;
            string expectedDigest = CanonicalOperationPayloadDigest(expected);
            string persistedDigest = CanonicalOperationPayloadDigest(persisted);
            return !string.IsNullOrEmpty(expectedDigest)
                && string.Equals(expectedDigest, persistedDigest, StringComparison.Ordinal);
        }

        private static SerializableModData CloneOperationData(SerializableModData source)
        {
            if (source == null) return null;
            var clone = new SerializableModData();
            CopyPrimitiveMap<int>(source, clone, "_intValues", (d, k, v) => d.Set(k, v));
            CopyPrimitiveMap<float>(source, clone, "_floatValues", (d, k, v) => d.Set(k, v));
            CopyPrimitiveMap<bool>(source, clone, "_boolValues", (d, k, v) => d.Set(k, v));
            CopyPrimitiveMap<string>(source, clone, "_stringValues", (d, k, v) => d.Set(k, v));
            IDictionary objects = PrivateMap(source, "_serializableGameDataValues");
            if (objects != null && objects.Count != 0)
                throw new InvalidOperationException("operation receipt unexpectedly contains nested objects");
            return clone;
        }

        private static void CopyPrimitiveMap<T>(SerializableModData source, SerializableModData target,
            string fieldName, Action<SerializableModData, string, T> setter)
        {
            IDictionary map = PrivateMap(source, fieldName);
            if (map == null) throw new InvalidOperationException("SerializableModData field unavailable: " + fieldName);
            foreach (DictionaryEntry pair in map)
                setter(target, (string)pair.Key, (T)pair.Value);
        }

        private static IDictionary PrivateMap(SerializableModData data, string fieldName)
        {
            FieldInfo field = typeof(SerializableModData).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(data) as IDictionary;
        }

        private static string CanonicalOperationPayloadDigest(SerializableModData data,
            string excludedKey = null)
        {
            try
            {
                var canonical = new StringBuilder();
                AppendCanonicalMap(canonical, "i", PrivateMap(data, "_intValues"), excludedKey);
                AppendCanonicalMap(canonical, "f", PrivateMap(data, "_floatValues"), excludedKey);
                AppendCanonicalMap(canonical, "b", PrivateMap(data, "_boolValues"), excludedKey);
                AppendCanonicalMap(canonical, "s", PrivateMap(data, "_stringValues"), excludedKey);
                IDictionary objects = PrivateMap(data, "_serializableGameDataValues");
                if (objects == null || objects.Count != 0) return null;
                byte[] digest;
                using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                var hex = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest) hex.Append(value.ToString("x2"));
                return hex.ToString();
            }
            catch { return null; }
        }

        private static void AppendCanonicalMap(StringBuilder destination, string typeTag, IDictionary map,
            string excludedKey = null)
        {
            if (map == null) throw new InvalidOperationException("missing primitive map");
            var keys = new List<string>();
            foreach (DictionaryEntry pair in map) keys.Add((string)pair.Key);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                if (!string.IsNullOrEmpty(excludedKey)
                    && string.Equals(key, excludedKey, StringComparison.Ordinal)) continue;
                object value = map[key];
                string rendered;
                if (value is float f) rendered = f.ToString("R", CultureInfo.InvariantCulture);
                else if (value is bool b) rendered = b ? "true" : "false";
                else if (value is IFormattable formattable) rendered = formattable.ToString(null, CultureInfo.InvariantCulture);
                else rendered = value as string ?? string.Empty;
                destination.Append(typeTag).Append(':').Append(key.Length).Append(':').Append(key)
                    .Append(':').Append(rendered.Length).Append(':').Append(rendered).Append('\n');
            }
        }

        private static bool StampOperationReceiptIntegrity(SerializableModData receipt)
        {
            if (receipt == null) return false;
            string digest = CanonicalOperationPayloadDigest(receipt, RpcConst.OperationReceiptIntegrityField);
            if (!IsSha256Hex(digest)) return false;
            receipt.Set(RpcConst.OperationReceiptIntegrityField, digest);
            return true;
        }

        private static bool StampOperationLedgerIntegrity(SerializableModData ledger)
        {
            if (ledger == null) return false;
            string digest = CanonicalOperationPayloadDigest(ledger, OperationLedgerIntegrityField);
            if (!IsSha256Hex(digest)) return false;
            ledger.Set(OperationLedgerIntegrityField, digest);
            return true;
        }

        private static bool IsSha256Hex(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool ValidateLoadedOperationReceipt(string operationId, SerializableModData receipt)
        {
            if (!IsValidOperationId(operationId) || receipt == null) return false;
            string actualOperationId = null, status = null, code = null, operationKind = null;
            string message = null, summary = null, worldText = null, integrity = null;
            int protocol = -1, taiwuId = -1;
            bool success = false, retryable = false, persisted = false;
            bool hasPersisted = receipt.Get(RpcConst.OperationReceiptPersistedField, out persisted);
            bool hasIntegrity = receipt.Get(RpcConst.OperationReceiptIntegrityField, out integrity);
            bool hasWorld = receipt.Get(RpcConst.OperationWorldIdField, out worldText);
            bool hasTaiwu = receipt.Get(RpcConst.OperationTaiwuIdField, out taiwuId);
            if (!receipt.Get(RpcConst.OperationIdField, out actualOperationId)
                || !string.Equals(actualOperationId, operationId, StringComparison.Ordinal)
                || !receipt.Get("protocol_version", out protocol) || protocol != RpcConst.OperationProtocolVersion
                || !receipt.Get("success", out success)
                || !receipt.Get(RpcConst.OperationStatusField, out status)
                || !receipt.Get(RpcConst.OperationCodeField, out code)
                || !receipt.Get(RpcConst.OperationRetryableField, out retryable)
                || !receipt.Get("operation_kind", out operationKind)
                || !receipt.Get("message", out message)
                || !receipt.Get(RpcConst.OperationReceiptField, out summary)
                || string.IsNullOrWhiteSpace(operationKind) || string.IsNullOrWhiteSpace(code)
                || !ValidOperationStatus(status)) return false;

            bool statusSuccess = string.Equals(status, "succeeded", StringComparison.Ordinal);
            if (success != statusSuccess) return false;
            if ((status == "pending" && !retryable)
                || (status != "pending" && status != "unknown" && retryable)) return false;
            string expectedSummary = BuildReceiptSummary(success, status, code, retryable,
                operationId, operationKind, message);
            if (!string.Equals(summary, expectedSummary, StringComparison.Ordinal)) return false;

            // Only explicitly unbound pre-integrity receipts are accepted for one-time
            // migration. Every bound/current receipt must carry durable provenance and a
            // full primitive-map digest.
            if (!hasPersisted || !hasIntegrity)
            {
                if (hasPersisted || hasIntegrity || hasWorld != hasTaiwu) return false;
                if (hasWorld)
                {
                    uint legacyWorldId;
                    if (!uint.TryParse(worldText, out legacyWorldId) || legacyWorldId == 0 || taiwuId <= 0)
                        return false;
                }
                return true;
            }
            uint worldId;
            if (!persisted || !hasWorld || !hasTaiwu || !uint.TryParse(worldText, out worldId)
                || worldId == 0 || taiwuId <= 0 || !IsSha256Hex(integrity)) return false;
            string computed = CanonicalOperationPayloadDigest(receipt, RpcConst.OperationReceiptIntegrityField);
            if (!string.Equals(integrity, computed, StringComparison.Ordinal)) return false;

            bool acknowledged;
            if (receipt.Get(OperationAcknowledgedField, out acknowledged) && acknowledged)
            {
                string acknowledgedTicks;
                long ticks;
                if (!receipt.Get("operation_acknowledged_utc_ticks", out acknowledgedTicks)
                    || !long.TryParse(acknowledgedTicks, out ticks) || ticks <= 0) return false;
            }
            return true;
        }

        private static bool ValidOperationStatus(string status)
            => status == "pending" || status == "succeeded" || status == "failed"
                || status == "rejected" || status == "canceled" || status == "unknown";

        private static bool HasPersistedReceiptProof(SerializableModData receipt)
        {
            bool persisted;
            string integrity;
            return receipt != null
                && receipt.Get(RpcConst.OperationReceiptPersistedField, out persisted) && persisted
                && receipt.Get(RpcConst.OperationReceiptIntegrityField, out integrity) && IsSha256Hex(integrity)
                && string.Equals(integrity,
                    CanonicalOperationPayloadDigest(receipt, RpcConst.OperationReceiptIntegrityField),
                    StringComparison.Ordinal);
        }

        private static bool EnsureOperationLedgerSlot(DataContext context, SerializableModData ledger, uint worldId, int taiwuId)
        {
            if (context == null || ledger == null) return false;
            if (!CompletePendingReceiptCompaction(context, ledger)) return false;
            string orderText = null;
            ledger.Get("order", out orderText);
            var order = ParseOperationOrder(orderText);
            if (order.Count < OperationLedgerCapacity) return true;

            string modId = string.IsNullOrWhiteSpace(_modIdStr) ? RpcConst.FallbackModId : _modIdStr;
            bool changed = false;
            var compactedIds = new List<string>();
            for (int i = 0; i < order.Count && order.Count >= OperationLedgerCapacity;)
            {
                string id = order[i];
                SerializableModData receipt;
                bool acknowledged = false;
                string kind = null, status = null, code = null;
                string receiptWorldText = null;
                uint receiptWorldId = 0;
                int receiptTaiwuId = -1;
                bool receiptReliable;
                bool receiptExists = TryLoadOperationReceipt(id, out receipt, out receiptReliable) && receipt != null;
                if (!receiptReliable) return false;
                if (receiptExists)
                {
                    receipt.Get(OperationAcknowledgedField, out acknowledged);
                    receipt.Get("operation_kind", out kind);
                    receipt.Get(RpcConst.OperationStatusField, out status);
                    receipt.Get(RpcConst.OperationCodeField, out code);
                    receipt.Get(RpcConst.OperationWorldIdField, out receiptWorldText);
                    receipt.Get(RpcConst.OperationTaiwuIdField, out receiptTaiwuId);
                    uint.TryParse(receiptWorldText, out receiptWorldId);
                }

                if (receiptExists && acknowledged)
                {
                    // Add exact recent evidence plus the fixed-size no-false-negative
                    // history filter before the order can forget the full receipt. A
                    // pre-binding acknowledged receipt is compacted as explicit 0/0
                    // unbound evidence: every future identity is rejected, so capacity
                    // can recover without ever guessing which Taiwu originally owned it.
                    bool unboundLegacy = receiptWorldId == 0 && receiptTaiwuId == -1;
                    if (!unboundLegacy && (receiptWorldId == 0 || receiptTaiwuId <= 0))
                    {
                        i++;
                        continue;
                    }
                    if (!AppendBoundedOperationTombstone(ledger, id, kind, status, code,
                        unboundLegacy ? 0u : receiptWorldId, unboundLegacy ? 0 : receiptTaiwuId))
                    {
                        i++;
                        continue;
                    }
                }
                else if (receiptExists)
                {
                    i++;
                    continue;
                }
                else
                {
                    bool tombstoneReliable;
                    if (!TryFindOperationTombstone(ledger, id, worldId, taiwuId,
                        out kind, out status, out code, out tombstoneReliable))
                    {
                        if (!tombstoneReliable) return false;
                        i++;
                        continue;
                    }
                }

                order.RemoveAt(i);
                compactedIds.Add(id);
                changed = true;
            }
            if (changed)
            {
                ledger.Set("order", string.Join("\n", order));
                ledger.Set("count", order.Count);
                ledger.Set("protocol_version", RpcConst.OperationProtocolVersion);
                ledger.Set(OperationCompactionPendingField, string.Join("\n", compactedIds));
                if (!StampOperationLedgerIntegrity(ledger)) return false;
                // 先可靠写入有界防重索引/Bloom 和新 order，再删除完整回执。若在两步之间崩溃，最多留下
                // 一个不再索引的完整终态回执；查询仍优先返回它，同 id 也绝不会再次执行。
                // 反过来先删回执会在 ledger 写入失败时丢掉唯一的幂等证据。
                DomainManager.Mod.SetSerializableModData(context, modId, OperationLedgerDataName, true, ledger);
                SerializableModData persistedLedger;
                if (!DomainManager.Mod.TryGet(modId, OperationLedgerDataName, true, out persistedLedger)
                    || persistedLedger == null || !OperationLedgerSemanticallyMatches(ledger, persistedLedger))
                    return false;
                foreach (string remainingId in order)
                {
                    SerializableModData remainingReceipt;
                    bool remainingReliable;
                    if (!TryLoadOperationReceipt(remainingId, out remainingReceipt, out remainingReliable)
                        || !remainingReliable || remainingReceipt == null) return false;
                }

                // The pending marker was committed with the shortened order. Deletion is
                // now restart-safe: a crash at any point replays this exact cleanup list.
                if (!CompletePendingReceiptCompaction(context, ledger)) return false;
            }
            return order.Count < OperationLedgerCapacity;
        }

        private static bool CompletePendingReceiptCompaction(DataContext context, SerializableModData ledger)
        {
            if (context == null || ledger == null) return false;
            List<string> pending;
            if (!TryParseCompactionPending(ledger, out pending)) return false;
            if (pending.Count == 0) return true;
            List<OperationTombstoneEntry> tombstones;
            byte[] bloom;
            bool initialized;
            if (!TryParseBoundedTombstones(ledger, out tombstones, out bloom, out initialized)
                || !initialized) return false;
            foreach (string operationId in pending)
            {
                OperationTombstoneEntry exact = null;
                for (int i = tombstones.Count - 1; i >= 0; i--)
                    if (tombstones[i] != null && string.Equals(tombstones[i].OperationId,
                        operationId, StringComparison.Ordinal)) { exact = tombstones[i]; break; }
                // A Bloom hit is only probabilistic and must never authorize deletion.
                // Pending compactions are bounded to <=128, so every just-added marker is
                // guaranteed to remain in the exact recent index (capacity 256).
                if (exact == null || !BloomMayContain(bloom, operationId)) return false;

                SerializableModData receipt;
                bool receiptReliable;
                bool found = TryLoadOperationReceipt(operationId, out receipt, out receiptReliable);
                if (!receiptReliable) return false;
                if (found && receipt != null)
                {
                    bool acknowledged = false;
                    string kind = null, status = null, code = null;
                    receipt.Get(OperationAcknowledgedField, out acknowledged);
                    receipt.Get("operation_kind", out kind);
                    receipt.Get(RpcConst.OperationStatusField, out status);
                    receipt.Get(RpcConst.OperationCodeField, out code);
                    bool bindingMatches = exact.WorldId == 0 && exact.TaiwuId == 0
                        ? StoredOperationBindingIsLegacyUnbound(receipt)
                        : StoredOperationBindingMatchesExact(receipt, exact.WorldId, exact.TaiwuId);
                    if (!acknowledged || !bindingMatches
                        || !string.Equals(kind, exact.OperationKind, StringComparison.Ordinal)
                        || !string.Equals(status, exact.Status, StringComparison.Ordinal)
                        || !string.Equals(code, exact.Code, StringComparison.Ordinal)) return false;
                }
                // If this exact receipt is already absent, a previous cleanup pass deleted
                // it before crashing. The integrity-protected exact marker makes continuation
                // idempotent; no new deletion authorization is inferred from absence alone.
            }

            string modId = string.IsNullOrWhiteSpace(_modIdStr) ? RpcConst.FallbackModId : _modIdStr;
            foreach (string operationId in pending)
            {
                try { DomainManager.Mod.RemoveSerializableModData(context, modId, OperationReceiptDataName(operationId), true); }
                catch { return false; }
                SerializableModData stillPresent;
                try
                {
                    if (DomainManager.Mod.TryGet(modId, OperationReceiptDataName(operationId), true, out stillPresent)
                        && stillPresent != null) return false;
                }
                catch { return false; }
            }

            ledger.Set(OperationCompactionPendingField, string.Empty);
            try
            {
                if (!StampOperationLedgerIntegrity(ledger)) return false;
                DomainManager.Mod.SetSerializableModData(context, modId, OperationLedgerDataName, true, ledger);
                SerializableModData persisted;
                return DomainManager.Mod.TryGet(modId, OperationLedgerDataName, true, out persisted)
                    && persisted != null && OperationLedgerSemanticallyMatches(ledger, persisted);
            }
            catch { return false; }
        }

        private static List<string> ParseOperationOrder(string orderText)
        {
            var order = new List<string>();
            if (string.IsNullOrWhiteSpace(orderText)) return order;
            foreach (string raw in orderText.Split('\n'))
            {
                string id = (raw ?? "").Trim();
                if (IsValidOperationId(id) && !order.Contains(id)) order.Add(id);
            }
            return order;
        }

        private static bool OperationLedgerContains(SerializableModData ledger, string operationId)
        {
            if (ledger == null || !IsValidOperationId(operationId)) return false;
            string orderText = null;
            return ledger.Get("order", out orderText)
                && ParseOperationOrder(orderText).Contains(operationId);
        }

        private static bool TryFindOperationTombstone(SerializableModData ledger, string operationId,
            uint worldId, int taiwuId, out string operationKind, out string status, out string code,
            out bool reliable)
        {
            reliable = false;
            operationKind = null; status = null; code = null;
            if (!IsValidOperationId(operationId)) return false;

            List<OperationTombstoneEntry> bounded;
            byte[] bloom;
            bool boundedInitialized;
            if (!TryParseBoundedTombstones(ledger, out bounded, out bloom, out boundedInitialized)) return false;
            reliable = true;
            if (boundedInitialized)
            {
                for (int i = bounded.Count - 1; i >= 0; i--)
                {
                    OperationTombstoneEntry entry = bounded[i];
                    if (entry == null || !string.Equals(entry.OperationId, operationId, StringComparison.Ordinal)) continue;
                    if (entry.WorldId == 0 && entry.TaiwuId == 0)
                    {
                        operationKind = "identity_unbound";
                        status = "rejected";
                        code = "operation_tombstone_identity_unbound";
                        return true;
                    }
                    if (entry.WorldId != worldId || entry.TaiwuId != taiwuId)
                    {
                        operationKind = "identity_mismatch";
                        status = "rejected";
                        code = "operation_tombstone_identity_mismatch";
                        return true;
                    }
                    operationKind = entry.OperationKind;
                    status = entry.Status;
                    code = entry.Code;
                    return true;
                }
            }

            // Compatibility for development builds that wrote one Mod-data key per
            // tombstone. New code never creates such keys; the bounded index + Bloom
            // above is the sole growing path.
            SerializableModData tombstone;
            bool tombstoneReadReliable;
            bool tombstoneFound = TryLoadOperationTombstone(operationId, out tombstone, out tombstoneReadReliable);
            if (!tombstoneReadReliable) { reliable = false; return false; }
            reliable = true;
            if (tombstoneFound && tombstone != null)
            {
                string storedId = null;
                tombstone.Get(RpcConst.OperationIdField, out storedId);
                if (string.Equals(storedId, operationId, StringComparison.Ordinal))
                {
                    int protocolVersion = -1;
                    tombstone.Get("protocol_version", out protocolVersion);
                    tombstone.Get("operation_kind", out operationKind);
                    tombstone.Get(RpcConst.OperationStatusField, out status);
                    tombstone.Get(RpcConst.OperationCodeField, out code);
                    bool terminal = status == "succeeded" || status == "failed" || status == "rejected"
                        || status == "canceled" || status == "unknown";
                    if (protocolVersion != RpcConst.OperationProtocolVersion
                        || string.IsNullOrWhiteSpace(operationKind) || !terminal || string.IsNullOrWhiteSpace(code))
                    {
                        reliable = false;
                        return false;
                    }
                    if (!StoredOperationBindingMatchesExact(tombstone, worldId, taiwuId))
                    {
                        operationKind = "identity_mismatch";
                        status = "rejected";
                        code = "operation_tombstone_identity_mismatch";
                        return true;
                    }
                    return true;
                }
                reliable = false;
                return false;
            }

            if (!TryFindLegacyOperationTombstone(ledger, operationId, out operationKind, out status, out code))
            {
                if (boundedInitialized && BloomMayContain(bloom, operationId))
                {
                    // The exact recent payload has aged out, but this id is definitely
                    // present unless the Bloom result is a conservative false positive.
                    // Either way, never execute it. ACK may safely treat it as compacted.
                    operationKind = null;
                    status = "unknown";
                    code = "operation_history_filter_match";
                    return true;
                }
                return false;
            }
            operationKind = "identity_unbound";
            status = "rejected";
            code = "operation_tombstone_identity_unbound";
            return true;
        }

        private static bool TryLoadOperationTombstone(string operationId, out SerializableModData tombstone,
            out bool reliable)
        {
            tombstone = null;
            reliable = false;
            if (!IsValidOperationId(operationId)) return false;
            string modId = string.IsNullOrWhiteSpace(_modIdStr) ? RpcConst.FallbackModId : _modIdStr;
            try
            {
                bool found = DomainManager.Mod.TryGet(modId, OperationTombstoneDataName(operationId), true, out tombstone);
                reliable = true;
                return found && tombstone != null;
            }
            catch
            {
                tombstone = null;
                reliable = false;
                return false;
            }
        }

        // Compatibility only: old releases stored tombstones as a finite newline list in the ledger. Parse it once per
        // authoritative world, then retain O(1) lookups. New code never appends to, truncates, or deletes that legacy list.
        private static bool TryFindLegacyOperationTombstone(SerializableModData ledger, string operationId,
            out string operationKind, out string status, out string code)
        {
            operationKind = null; status = null; code = null;
            if (ledger == null || !IsValidOperationId(operationId)) return false;
            uint worldId = 0;
            try { worldId = DomainManager.World.GetWorldId(); } catch { }
            string text = null;
            ledger.Get("tombstones", out text);

            if (_legacyTombstoneCacheSource == null || _legacyTombstoneCacheWorldId != worldId
                || !string.Equals(_legacyTombstoneCacheSource, text ?? "", StringComparison.Ordinal))
            {
                LegacyTombstoneCache.Clear();
                _legacyTombstoneCacheWorldId = worldId;
                _legacyTombstoneCacheSource = text ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                {
                    foreach (string raw in text.Split('\n'))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0) continue;
                        string[] fields = line.Split('|');
                        string id = fields.Length > 0 ? fields[0] : null;
                        if (!IsValidOperationId(id)) continue;
                        LegacyTombstoneCache[id] = new[]
                        {
                            fields.Length > 1 ? fields[1] : null,
                            fields.Length > 2 ? fields[2] : null,
                            fields.Length > 3 ? fields[3] : null
                        };
                    }
                }
            }

            string[] found;
            if (!LegacyTombstoneCache.TryGetValue(operationId, out found)) return false;
            operationKind = found.Length > 0 ? found[0] : null;
            status = found.Length > 1 ? found[1] : null;
            code = found.Length > 2 ? found[2] : null;
            return true;
        }

        private static string OperationReceiptDataName(string operationId) => "operation_receipt_v2_" + operationId;
        private static string OperationTombstoneDataName(string operationId) => "operation_tombstone_v2_" + operationId;

        private static bool IsValidOperationId(string operationId)
        {
            if (string.IsNullOrEmpty(operationId) || operationId.Length != 32) return false;
            for (int i = 0; i < operationId.Length; i++)
            {
                char c = operationId[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static SerializableModData Fail(string status, string msg)
        {
            var d = new SerializableModData();
            d.Set("success", false);
            d.Set("status", status);
            d.Set("message", msg);
            return d;
        }

        /// <summary>
        /// 已进入游戏写 API 后无法证明“零写入”或“完整补偿”的结果。它是不可重试的终态 unknown：
        /// 后端永久保留 operationId 防重，但前端不得把它叙述成确定成功或确定失败。
        /// </summary>
        private static SerializableModData Indeterminate(string code, string msg)
        {
            var d = new SerializableModData();
            d.Set("success", false);
            d.Set(RpcConst.OperationStatusField, "unknown");
            d.Set(RpcConst.OperationCodeField, string.IsNullOrWhiteSpace(code) ? "mutation_indeterminate" : code);
            d.Set(RpcConst.OperationRetryableField, false);
            d.Set("message", msg ?? "副作用可能已部分落地；不得自动重试");
            return d;
        }
    }

    /// <summary>
    /// 本体关系创建接口会在任一方不是普通智慧人物时直接抛异常。固定剧情人物、事件投影
    /// 或第三方遗留实体一旦误入同地结识、过月演算或赠礼流程，异常会退出整个后端进程。
    /// 在这条权威边界拒绝非法组合；两个合法智慧人物仍完整执行本体逻辑。
    /// </summary>
    [HarmonyPatch(typeof(CharacterDomain), nameof(CharacterDomain.TryCreateGeneralRelation))]
    internal static class InvalidGeneralRelationCrashGuardPatch
    {
        private static bool _loggedInvalidPair;

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Character selfChar, Character relatedChar)
        {
            if (selfChar != null && relatedChar != null
                && selfChar.GetCreatingType() == 1 && relatedChar.GetCreatingType() == 1)
                return true;

            if (!_loggedInvalidPair)
            {
                _loggedInvalidPair = true;
                int selfId = selfChar == null ? -1 : selfChar.GetId();
                int relatedId = relatedChar == null ? -1 : relatedChar.GetId();
                int selfType = selfChar == null ? -1 : selfChar.GetCreatingType();
                int relatedType = relatedChar == null ? -1 : relatedChar.GetCreatingType();
                AdaptableLog.Warning("[JHYL_INVALID_GENERAL_RELATION_SKIPPED] self=" + selfId
                    + " type=" + selfType + " related=" + relatedId
                    + " relatedType=" + relatedType);
            }
            return false;
        }
    }

    /// <summary>
    /// 兼容旧版本已建立的固定模板副本。本体会把这些非机人“特殊队员”误当成猎人动物；
    /// 它们并没有 AnimalCharIdToItemKey 映射，执行原方法会令后端主循环崩溃。
    /// </summary>
    [HarmonyPatch(typeof(ProfessionSkillHandle), nameof(ProfessionSkillHandle.HunterSkill_AnimalCharacterToItem))]
    internal static class CharacterProxyAnimalDowngradeGuardPatch
    {
        private static bool Prefix(DataContext context, Character character, ref ItemKey __result)
        {
            if (character == null || !BackendPluginMain.IsCharacterProxy(character.GetId()))
                return true;
            try
            {
                int characterId = character.GetId();
                if (DomainManager.Taiwu.IsInGroup(characterId))
                    DomainManager.Taiwu.LeaveGroup(context, characterId);
            }
            catch
            {
                // 保护后端主循环优先；保留人物实体，仍可从江湖有灵的离队工具重试。
            }
            __result = default;
            return false;
        }
    }

    /// <summary>
    /// 0.33.0.8 及以前曾用 DeepCopy 建立固定模板副本。只允许存档中带江湖有灵反向映射的
    /// 实体进入旧副本迁移；本体会合法生成多个同模板动物/敌人，绝不能再按“模板重复”猜测，
    /// 否则猎人自动驱灭后新增的动物会被错误改成智慧人物并污染地图与过月关系。
    /// </summary>
    [HarmonyPatch(typeof(CharacterDomain), "InitializeFixedCharactersCache")]
    internal static class LegacyCharacterProxyLoadGuardPatch
    {
        private static void Prefix(CharacterDomain __instance)
        {
            if (__instance?.Characters == null) return;
            foreach (KeyValuePair<int, Character> pair in __instance.Characters)
            {
                Character character = pair.Value;
                if (character == null || character.GetCreatingType() != 0) continue;
                short templateId = character.GetTemplateId();
                if (templateId < 0 || templateId >= Config.Character.Instance.Count
                    || Config.Character.Instance[templateId].CreatingType != 0) continue;
                if (BackendPluginMain.TryGetCharacterProxySource(pair.Key, out _))
                    character.OfflineSetCreatingType(1);
            }
        }
    }

    /// <summary>
    /// 原版人物互动的后端落地回执。事件窗口会把“打开子菜单、展示候选、等待确认”也当作
    /// EventSelect；只有该调用实际写入了事件域以外的游戏数据，才算真正发生过行为。
    /// </summary>
    internal static class NativeInteractionMutationAudit
    {
        private const ushort TaiwuEventDomainId = 12;
        private const int MaxReceipts = 256;

        internal sealed class Frame
        {
            internal string EventGuid;
            internal string OptionKey;
            internal int MutationCount;
        }

        private sealed class Receipt
        {
            internal uint WorldId;
            internal string EventGuid;
            internal string OptionKey;
            internal bool Changed;
            internal bool Pending;
            internal int MutationCount;
        }

        [ThreadStatic]
        private static Stack<Frame> _frames;
        private static readonly object Gate = new object();
        private static readonly List<Receipt> Receipts = new List<Receipt>();
        private static readonly FieldInfo DomainIdField = AccessTools.Field(typeof(BaseGameDataDomain), "DomainId");
        private static readonly FieldInfo SelectInformationField = AccessTools.Field(typeof(TaiwuEventDomain), "_selectInformationData");
        private static readonly FieldInfo CricketBettingField = AccessTools.Field(typeof(TaiwuEventDomain), "_cricketBettingData");
        private static readonly FieldInfo WaitConfirmOptionKeyField = AccessTools.Field(typeof(TaiwuEventDomain), "_waitConfirmOptionKey");

        internal static Frame Begin(string eventGuid, string optionKey)
        {
            if (_frames == null) _frames = new Stack<Frame>();
            var frame = new Frame { EventGuid = eventGuid ?? "", OptionKey = optionKey ?? "" };
            _frames.Push(frame);
            return frame;
        }

        internal static void MarkObject(BaseGameDataObject value)
        {
            ushort domainId = ushort.MaxValue;
            try
            {
                if (value?.CollectionHelperData != null) domainId = value.CollectionHelperData.DomainId;
            }
            catch { }
            MarkDomain(domainId);
        }

        internal static void MarkDomain(BaseGameDataDomain value)
        {
            ushort domainId = ushort.MaxValue;
            try
            {
                object raw = DomainIdField?.GetValue(value);
                if (raw is ushort parsed) domainId = parsed;
            }
            catch { }
            MarkDomain(domainId);
        }

        private static void MarkDomain(ushort domainId)
        {
            if (domainId == TaiwuEventDomainId || _frames == null || _frames.Count == 0) return;
            _frames.Peek().MutationCount++;
        }

        internal static void End(TaiwuEventDomain domain, Frame frame)
        {
            if (frame == null) return;
            if (_frames != null && _frames.Count > 0)
            {
                if (ReferenceEquals(_frames.Peek(), frame)) _frames.Pop();
                else
                {
                    var preserved = new Stack<Frame>();
                    while (_frames.Count > 0 && !ReferenceEquals(_frames.Peek(), frame)) preserved.Push(_frames.Pop());
                    if (_frames.Count > 0) _frames.Pop();
                    while (preserved.Count > 0) _frames.Push(preserved.Pop());
                }
            }

            uint worldId = 0;
            try { worldId = DomainManager.World.GetWorldId(); } catch { }
            var receipt = new Receipt
            {
                WorldId = worldId,
                EventGuid = frame.EventGuid,
                OptionKey = frame.OptionKey,
                Changed = frame.MutationCount > 0,
                Pending = IsPending(domain),
                MutationCount = frame.MutationCount,
            };
            lock (Gate)
            {
                Receipts.Add(receipt);
                if (Receipts.Count > MaxReceipts)
                    Receipts.RemoveRange(0, Receipts.Count - MaxReceipts);
            }
        }

        private static bool IsPending(TaiwuEventDomain domain)
        {
            if (domain == null) return false;
            try
            {
                if (domain.ShowInteractCheckAnimation) return true;
                var information = SelectInformationField?.GetValue(domain) as EventSelectInformationData;
                if (information != null && information.AvailableData && !information.SelectComplete
                    && !information.IsForShopping) return true;
                var cricket = CricketBettingField?.GetValue(domain) as EventCricketBettingData;
                if (cricket != null && cricket.IsValid && !cricket.IsComplete) return true;
                string waitKey = WaitConfirmOptionKeyField?.GetValue(domain) as string;
                return !string.IsNullOrEmpty(waitKey);
            }
            catch { return false; }
        }

        internal static SerializableModData Query(DataContext context, SerializableModData parameter)
        {
            uint requestedWorldId = 0;
            string requestedWorldIdText = null;
            string eventGuid = null;
            string optionKey = null;
            parameter?.Get("world_id", out requestedWorldIdText);
            uint.TryParse(requestedWorldIdText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out requestedWorldId);
            parameter?.Get("event_guid", out eventGuid);
            parameter?.Get("option_key", out optionKey);

            uint currentWorldId = 0;
            try { currentWorldId = DomainManager.World.GetWorldId(); } catch { }
            var result = new SerializableModData();
            result.Set("success", requestedWorldId != 0 && requestedWorldId == currentWorldId);
            if (requestedWorldId == 0 || requestedWorldId != currentWorldId)
            {
                result.Set("available", false);
                result.Set("changed", false);
                result.Set("pending", false);
                result.Set("mutation_count", 0);
                return result;
            }

            Receipt found = null;
            lock (Gate)
            {
                int index = Receipts.FindIndex(x => x.WorldId == requestedWorldId
                    && string.Equals(x.EventGuid, eventGuid ?? "", StringComparison.Ordinal)
                    && string.Equals(x.OptionKey, optionKey ?? "", StringComparison.Ordinal));
                if (index >= 0)
                {
                    found = Receipts[index];
                    Receipts.RemoveAt(index);
                }
            }
            result.Set("available", found != null);
            result.Set("changed", found?.Changed ?? false);
            result.Set("pending", found?.Pending ?? false);
            result.Set("mutation_count", found?.MutationCount ?? 0);
            return result;
        }

        internal static void Reset()
        {
            lock (Gate) Receipts.Clear();
            _frames?.Clear();
        }
    }

    [HarmonyPatch(typeof(TaiwuEventDomain), nameof(TaiwuEventDomain.EventSelect),
        new Type[] { typeof(string), typeof(string), typeof(bool) })]
    internal static class NativeInteractionEventSelectAuditPatch
    {
        private static void Prefix(string eventGuid, string optionKey,
            out NativeInteractionMutationAudit.Frame __state)
            => __state = NativeInteractionMutationAudit.Begin(eventGuid, optionKey);

        private static void Postfix(TaiwuEventDomain __instance, NativeInteractionMutationAudit.Frame __state)
            => NativeInteractionMutationAudit.End(__instance, __state);
    }

    [HarmonyPatch]
    internal static class NativeInteractionObjectMutationPatch
    {
        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(BaseGameDataObject), "SetModifiedAndInvalidateInfluencedCache",
                new Type[] { typeof(ushort), typeof(DataContext) });

        private static void Prefix(BaseGameDataObject __instance)
            => NativeInteractionMutationAudit.MarkObject(__instance);
    }

    [HarmonyPatch]
    internal static class NativeInteractionDomainMutationPatch
    {
        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(BaseGameDataDomain), "SetModifiedAndInvalidateInfluencedCache",
                new Type[] { typeof(int), typeof(byte[]), typeof(GameData.Dependencies.DataInfluence[][]), typeof(DataContext) });

        private static void Prefix(BaseGameDataDomain __instance)
            => NativeInteractionMutationAudit.MarkDomain(__instance);
    }
}
