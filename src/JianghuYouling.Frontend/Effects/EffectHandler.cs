using System;
using System.Collections.Generic;
using GameData.Domains.Character;        // CharacterDomainMethod, ResourceInts
using GameData.Domains.Taiwu;            // TaiwuDomainMethod(追随/解除追随)
using GameData.Domains.Item;             // ItemKey
using GameData.Domains.Item.Display;     // ItemDisplayData(读 NPC 可换装备)
using GameData.Domains.Information;       // InformationDomainMethod, SecretInformationId
using GameData.Domains.Mod;              // ModDomainMethod, SerializableModData
using GameData.Serializer;               // Serializer
using GameData.Utilities;                // RawDataPool
using UnityEngine;
using JianghuYouling.Shared;
using JianghuYouling.Rpc;
using JianghuYouling.Core.Tools;
using JianghuYouling.Core.Commission;

namespace JianghuYouling.Effects
{
    public sealed class AreaCharsQueryResult
    {
        public List<int> Ids = new List<int>();
        public int BlockCount;
        public int AliveLocationCount;
        public int AliveScanned;
        public string Source;
    }

    public sealed class MonthlyActionPreflight
    {
        public bool ActorAlive, TargetAlive, ActorAdult, TargetAdult;
        public bool Enemy, Spouse, Sworn, Friend, Adored, Mentor;
        public bool AdoptiveParent, AdoptiveChild, CanAdoptiveParent, CanAdoptiveChild;
        public bool ActorAdoresTarget, TargetAdoresActor;
        public bool ActorInfected, TargetInfected, StrongEnough, TargetKidnapped, TargetIsTaiwu;
        public bool ActorHasRope, ActorHasPoison, TargetPoisonImmune, CanMarry;
        public bool ActorCanTravel;
        public int ActorConsummate, TargetConsummate;
        public int ActorArea = -1, ActorBlock = -1, TargetArea = -1, TargetBlock = -1;
        public bool SameValidLocation;
        public int TargetHealth, TargetLeftMaxHealth, TargetInjuryMarks;
        public bool TargetNeedsHealing;
        public int ActorFavor;
        public string RelationText, ActorTravelCode, ActorTravelReason;
        public string AdoptiveParentReason, AdoptiveChildReason;
    }

    public sealed class HealPreflight
    {
        public bool TargetAlive, TargetNeedsHealing;
        public int TargetHealth, TargetLeftMaxHealth, TargetInjuryMarks;
    }

    public sealed class NpcHealthStatus
    {
        public int Health, LeftMaxHealth, InjuryMarks, QiDisorder, QiDisorderShow;
        public string QiDisorderLevel, PoisonSummary;
        public int[] Poisons = new int[6];
    }

    public sealed class MerchantGoodsQueryResult
    {
        public bool Ok;
        public int OwnerType = -1;
        public int OwnerId = -1;
        public List<KeyValuePair<ItemKey, int>> Goods = new List<KeyValuePair<ItemKey, int>>();
    }

    public sealed class CommissionSnapshot
    {
        public bool Ok;
        public int Favor;
        /// <summary>本体 WorldDomain.GetXiangshuLevel()，同时是当前允许入队品级。</summary>
        public int WorldProgress;
        public int[] Resources = new int[8];
        public bool TargetValid;
        public bool TargetDead;
        public int TargetNpcId = -1;
        public int TargetConsummate = -1;
        public int TargetRewardGrade = -1;

        public int CurrentValue(CommissionRecord record)
        {
            if (record == null) return 0;
            switch (record.Kind)
            {
                case "deliver_resource":
                case "collect_resource":
                    return record.ResourceType >= 0 && record.ResourceType < Resources.Length
                        ? Resources[record.ResourceType] : 0;
                case "earn_money": return Resources[6];
                case "gain_prestige": return Resources[7];
                case "increase_favor": return Favor;
                case "kill_npc": return TargetDead ? 1 : 0;
                default: return 0;
            }
        }

        public int ProgressValue(CommissionRecord record)
        {
            if (record == null) return 0;
            return CommissionProgressPolicy.ProgressValue(record.Kind, CurrentValue(record),
                record.BaselineValue);
        }
    }

    public sealed class CommissionClaimResult
    {
        public bool Success;
        public string Code;
        public string Message;
        public string RewardKind;
        public string RewardName;
        public int RewardAmount;
        public int RewardGrade = -1;
    }

    /// <summary>
    /// M1 即时效果落地:好感 + 即时关系。
    /// 好感 = 前端直调 GM 命令;关系(挚友/结义/师徒)= 需后端权限,走 RPC。
    /// </summary>
    public static class EffectHandler
    {
        /// <summary>
        /// CallGm 的只读请求允许一次 4 秒等待、0.35 秒退避和一次 1.75 秒有界重放。
        /// 外层协程必须等到这条链路自行回调完毕；更短的本地 deadline 会在合法重放
        /// 结束前抢先把“尚未返回”误判成“没有数据”。
        /// </summary>
        public const float ReadOnlyQueryWaitSeconds = OperationRpcClient.ReadOnlyCallerWaitSeconds;

        private sealed class OperationOutcomeObserver
        {
            public Action<ToolOutcome> Callback;
            public bool Dispatched;
        }

        private static readonly object OperationOutcomeGate = new object();
        private static readonly Dictionary<string, OperationOutcomeObserver> OperationOutcomeObservers
            = new Dictionary<string, OperationOutcomeObserver>(StringComparer.Ordinal);
        private sealed class RosterRelationRead
        {
            public bool Ok;
            public string Text;
        }
        private sealed class ScenePresenceRead
        {
            public bool Ok;
            public List<int> Present;
        }
        private sealed class CharacterIdentityRead
        {
            public bool Ok;
            public List<string> Names;
            public List<string> Genders;
        }

        private static string SharedReadKey(string query, string identity)
        {
            int date = -1;
            try
            {
                var basic = SingletonObject.getInstance<BasicGameData>();
                if (basic != null) date = basic.CurrDate;
            }
            catch { }
            return WorldLifecycle.WorldId + ":" + WorldLifecycle.Generation + ":" + date
                + ":" + (query ?? "") + ":" + (identity ?? "");
        }

        /// <summary>
        /// 为对话 durable journal 订阅某个稳定 operationId 的结构化结果。订阅必须发生在调用
        /// Apply* 之前；底层无论返回 count/bool/string，都由 CallGm/CallBoolRpc 在业务回调前
        /// 发布完整 ToolOutcome，因而 UNKNOWN 不会再被压成 0/false/空串。
        /// </summary>
        public static bool ObserveOperationOutcome(string operationId, Action<ToolOutcome> onOutcome)
        {
            operationId = (operationId ?? "").Trim();
            if (!OperationRpcClient.IsValidOperationId(operationId) || onOutcome == null) return false;
            lock (OperationOutcomeGate)
                OperationOutcomeObservers[operationId] = new OperationOutcomeObserver { Callback = onOutcome };
            return true;
        }

        /// <summary>仅供调用方区分“本地前置校验未派发”与“已派发、正等权威回执”。</summary>
        public static bool WasOperationDispatched(string operationId)
        {
            lock (OperationOutcomeGate)
                return OperationOutcomeObservers.TryGetValue(operationId ?? "", out var observer)
                    && observer.Dispatched;
        }

        public static void ForgetOperationOutcomeObserver(string operationId)
        {
            lock (OperationOutcomeGate) OperationOutcomeObservers.Remove(operationId ?? "");
        }

        /// <summary>
        /// 世界或太吾身份切换时立刻释放所有旧世界回调闭包。已派发项保留无回调的轻量
        /// Dispatched 标记，供尚在协作收尾的 worker 正确写成 UNKNOWN 后自行 Forget；
        /// 未派发项可直接移除。
        /// </summary>
        public static void DetachOperationOutcomeObserversForWorldExit()
        {
            lock (OperationOutcomeGate)
            {
                var remove = new List<string>();
                foreach (var pair in OperationOutcomeObservers)
                {
                    if (pair.Value == null || !pair.Value.Dispatched) remove.Add(pair.Key);
                    else pair.Value.Callback = null;
                }
                foreach (string operationId in remove)
                    OperationOutcomeObservers.Remove(operationId);
            }
            SharedReadQueryCache.InvalidateAll();
        }

        /// <summary>完成一个通过本地语义校验、但无需触碰后端的幂等 no-op（例如数值变化全为零）。</summary>
        public static void CompleteObservedOperation(ToolOutcome outcome)
        {
            if (outcome == null || string.IsNullOrWhiteSpace(outcome.OperationId)) return;
            Action<ToolOutcome> callback = null;
            lock (OperationOutcomeGate)
            {
                if (OperationOutcomeObservers.TryGetValue(outcome.OperationId, out var observer))
                {
                    callback = observer.Callback;
                    observer.Callback = null;
                }
            }
            try { callback?.Invoke(outcome); } catch { }
        }

        private static void MarkOperationDispatched(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return;
            lock (OperationOutcomeGate)
                if (OperationOutcomeObservers.TryGetValue(operationId, out var observer)) observer.Dispatched = true;
        }

        private static void PublishOperationOutcome(SerializableModData response, string fallbackOperationId)
        {
            string operationId = null;
            response?.Get(RpcConst.OperationIdField, out operationId);
            if (string.IsNullOrWhiteSpace(operationId)) operationId = fallbackOperationId;
            if (string.IsNullOrWhiteSpace(operationId)) return;

            Action<ToolOutcome> callback = null;
            lock (OperationOutcomeGate)
            {
                if (OperationOutcomeObservers.TryGetValue(operationId, out var observer))
                {
                    callback = observer.Callback;
                    // 回调只消费一次，但保留 Dispatched 位，直到调用方显式 Forget。
                    // 恢复派发必须能在同步终态回调之后仍区分“已触及后端”和
                    // “本地前置拒绝”，否则会把真实已执行动作错误取消。
                    observer.Callback = null;
                }
            }
            ToolOutcome outcome = ToToolOutcome(response);
            if (string.IsNullOrWhiteSpace(outcome.OperationId)) outcome.OperationId = operationId;
            if (outcome.IsSucceeded) SharedReadQueryCache.InvalidateAll();
            if (callback == null) return;
            try { callback(outcome); }
            catch (Exception e) { Debug.LogWarning("[江湖有灵] ToolOutcome observer 异常:" + e.GetType().Name); }
        }

        /// <summary>
        /// 赠予好意加成:NPC 主动赠物/赠银给太吾时,额外给 NPC→太吾 的好感。
        /// 修复 bug —— 游戏的"人情债"机制(TransferXxxWithDebt → UpdateDebtByXxxTransfer)在 dest 为太吾时
        /// 会因 NPC "失物/失财"而扣其对太吾好感;但被说动后的赠予本是好意,理应增进情谊,故在此补正令其净增。
        /// </summary>
        public const int GiftGoodwillFavor = 1500;

        /// <summary>改好感(单向:npc 对 taiwu)。前端直调,瞬时排队,无回调。</summary>
        public static void ApplyFavor(int npcId, int taiwuId, int delta)
            => ApplyFavor(npcId, taiwuId, delta, null);

        public static void ApplyFavor(int npcId, int taiwuId, int delta, string stableOperationId)
            => ApplyFavor(npcId, taiwuId, delta, null, stableOperationId);

        public static void ApplyFavor(int npcId, int taiwuId, int delta, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (delta == 0 || npcId <= 0 || taiwuId <= 0 || npcId == taiwuId)
            { onDone?.Invoke(false, "参数无效"); return; }
            // 走后端 Gm 通道:好感命令对失效角色会 GetElement_Objects 抛异常冲垮后端,这里在 RPC 内 try/catch 兜住
            CallGm("favor", d => { d.Set("self", npcId); d.Set("related", taiwuId); d.Set("delta", delta); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未能改变好感")); }, stableOperationId);
            Debug.Log("[江湖有灵] favor " + npcId + "->" + taiwuId + " delta=" + delta);
        }

        // 通用 GM 通道:把会崩/会损坏的写操作收进后端 Gm RPC(try/catch),失败仅记日志,绝不冲垮后端主循环。
        // onResp 可选:把后端返回的整包数据回传给调用方(如赠银钱要读"实际到账数额");resp 反序列化失败时回传 null。
        private static bool IsWorldRpcBlocked()
        {
            return !JianghuYouling.WorldLifecycle.IsActive && JianghuYouling.WorldLifecycle.Generation > 0;
        }

        private static SerializableModData MakeWorldExitResponse()
        {
            return MakeLocalOutcome(false, "canceled", "world_inactive", false, null, "已离开存档,取消后台动作");
        }

        private static SerializableModData MakeLocalOutcome(bool success, string status, string code, bool retryable,
            string operationId, string message)
        {
            var resp = new SerializableModData();
            resp.Set("success", success);
            resp.Set("message", message ?? "");
            resp.Set(RpcConst.OperationStatusField, status ?? (success ? "succeeded" : "failed"));
            resp.Set(RpcConst.OperationCodeField, code ?? (success ? "ok" : "operation_failed"));
            resp.Set(RpcConst.OperationRetryableField, retryable);
            resp.Set(RpcConst.OperationIdField, operationId ?? "");
            resp.Set("protocol_version", RpcConst.OperationProtocolVersion);
            resp.Set(RpcConst.OperationReceiptPersistedField, false);
            // A client-side rejection/cancellation is not an authoritative backend receipt.
            resp.Set(RpcConst.OperationReceiptField, "");
            return resp;
        }

        private static ToolOutcome ToToolOutcome(SerializableModData data)
        {
            bool success = false, retryable = false, receiptPersisted = false;
            string status = null, code = null, operationId = null, message = null, receipt = null;
            data?.Get("success", out success);
            data?.Get(RpcConst.OperationStatusField, out status);
            data?.Get(RpcConst.OperationCodeField, out code);
            data?.Get(RpcConst.OperationRetryableField, out retryable);
            data?.Get(RpcConst.OperationIdField, out operationId);
            data?.Get("message", out message);
            data?.Get(RpcConst.OperationReceiptField, out receipt);
            data?.Get(RpcConst.OperationReceiptPersistedField, out receiptPersisted);
            if (string.IsNullOrWhiteSpace(status)) status = success ? "succeeded" : "failed";
            if (string.IsNullOrWhiteSpace(code)) code = success ? "ok" : "operation_failed";
            return new ToolOutcome
            {
                Status = status,
                Code = code,
                Retryable = retryable,
                OperationId = operationId,
                Receipt = receiptPersisted ? receipt : null,
                Message = message,
            };
        }

        /// <summary>
        /// 在保留旧 bool/count callback 签名的前提下，把 pending/unknown 编成稳定机器前缀。
        /// 调用方必须将 UNKNOWN: 当作第三态，不得当成终态失败后换 operationId 重发。
        /// </summary>
        private static string OutcomeMessage(SerializableModData data, string message)
        {
            string status = null, code = null;
            data?.Get(RpcConst.OperationStatusField, out status);
            data?.Get(RpcConst.OperationCodeField, out code);
            if (status == "pending" || status == "unknown")
            {
                string current = message ?? "副作用回执尚未确认";
                return current.StartsWith("UNKNOWN:", StringComparison.Ordinal)
                    ? current
                    : ("UNKNOWN:" + (string.IsNullOrWhiteSpace(code) ? "receipt_unavailable" : code) + ":" + current);
            }
            return message;
        }

        private static bool TryAbortWorldRpc(string label, Action<SerializableModData> onResp)
        {
            if (!IsWorldRpcBlocked()) return false;
            try { onResp?.Invoke(MakeWorldExitResponse()); } catch { }
            Debug.LogWarning("[江湖有灵] RPC " + label + " 已取消:当前不在存档世界");
            return true;
        }

        private static bool TryAbortWorldRpc(string label, Action<bool, string> onDone)
        {
            if (!IsWorldRpcBlocked()) return false;
            try { onDone?.Invoke(false, "已离开存档,取消后台动作"); } catch { }
            Debug.LogWarning("[江湖有灵] RPC " + label + " 已取消:当前不在存档世界");
            return true;
        }

        private static void CallGm(string op, Action<SerializableModData> fill, Action<SerializableModData> onResp = null,
            string stableOperationId = null)
        {
            if (IsWorldRpcBlocked())
            {
                var canceled = MakeWorldExitResponse();
                PublishOperationOutcome(canceled, stableOperationId);
                try { onResp?.Invoke(canceled); } catch { }
                Debug.LogWarning("[江湖有灵] RPC gm:" + op + " 已取消:当前不在存档世界");
                return;
            }

            var p = new SerializableModData();
            p.Set("op", op);
            fill?.Invoke(p);
            bool journaled = IsJournaledGmMutation(op);
            bool dispatched = OperationRpcClient.Call(RpcConst.GmMethod, p,
                resp =>
                {
                    try
                    {
                        bool ok = false; string msg = null, status = null, code = null, operationId = null;
                        resp?.Get("success", out ok); resp?.Get("message", out msg);
                        resp?.Get(RpcConst.OperationStatusField, out status); resp?.Get(RpcConst.OperationCodeField, out code);
                        resp?.Get(RpcConst.OperationIdField, out operationId);
                        msg = OutcomeMessage(resp, msg);
                        if (resp != null && msg != null) resp.Set("message", msg);
                        if (!ok) Debug.LogWarning("[江湖有灵] GM " + op + " 失败: status=" + (status ?? "")
                            + " code=" + (code ?? "") + " op=" + (operationId ?? "") + " " + (msg ?? ""));
                    }
                    catch { }
                    PublishOperationOutcome(resp, stableOperationId);
                    try { onResp?.Invoke(resp); } catch { }
                }, stableOperationId, requireStructuredMutationReceipt: journaled,
                readOnlyRequest: !journaled);
            if (dispatched) MarkOperationDispatched(stableOperationId);
        }

        private static bool IsJournaledGmMutation(string op)
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

        /// <summary>改 NPC 心情(Happiness)。delta 正=转好,负=低落。走后端 Gm 通道(ChangeHappiness 仅后端,内部 Clamp 安全)。
        /// 注:游戏自带联动——心情持续低落会加速其相枢传染;心魔(气机)不受对话影响,本 mod 不再直接动心魔/相枢。</summary>
        public static void ApplyHappiness(int npcId, int delta)
            => ApplyHappiness(npcId, delta, null);

        public static void ApplyHappiness(int npcId, int delta, string stableOperationId)
        {
            if (delta == 0 || npcId <= 0) return;
            CallGm("happiness", d => { d.Set("npc", npcId); d.Set("delta", delta); }, null, stableOperationId);
            Debug.Log("[江湖有灵] 心情 " + npcId + " delta=" + delta);
        }

        /// <summary>改变任意有效人物（包括太吾）的当前心情，并返回结构化成败。</summary>
        public static void ApplyCharacterHappiness(int characterId, int delta,
            Action<bool, string> onDone, string stableOperationId = null)
        {
            if (characterId <= 0 || delta == 0)
            {
                onDone?.Invoke(false, "人物或心情变化无效");
                return;
            }
            CallGm("happiness", d =>
            {
                d.Set("character", characterId);
                d.Set("delta", delta);
            }, resp =>
            {
                bool ok = false;
                string message = null;
                resp?.Get("success", out ok);
                resp?.Get("message", out message);
                onDone?.Invoke(ok, message ?? (ok ? "心情已改变" : "心情没有改变"));
            }, stableOperationId);
            Debug.Log("[江湖有灵] 人物心情 " + characterId + " delta=" + delta);
        }

        /// <summary>
        /// 将一轮对话引发的好感/心情/立场/戒备作为一个后端副作用批量落地。
        /// 回调只在原回包或 QueryOperation 恢复得到权威结果后触发，且严格只触发一次。
        /// stableOperationId 可由持久队列传入；留空则自动生成 32hex id。
        /// </summary>
        public static void ApplyReaction(int npcId, int taiwuId, int favorDelta, int happinessDelta,
            int moralityDelta, int alertnessDelta, Action<ToolOutcome> onDone,
            string stableOperationId = null)
        {
            if (npcId <= 0 || taiwuId <= 0 || npcId == taiwuId)
            {
                onDone?.Invoke(ToToolOutcome(MakeLocalOutcome(false, "failed", "bad_args", false,
                    stableOperationId, "反应角色参数无效")));
                return;
            }
            CallGm("reaction", d =>
            {
                d.Set("npc", npcId); d.Set("taiwu", taiwuId);
                d.Set("favor_delta", favorDelta); d.Set("happiness_delta", happinessDelta);
                d.Set("morality_delta", moralityDelta); d.Set("alertness_delta", alertnessDelta);
            }, response => onDone?.Invoke(ToToolOutcome(response)), stableOperationId);
        }

        /// <summary>
        /// 只读对账某个已派发 operationId。适用于下月/重启后恢复 durable journal：
        /// succeeded/failed/rejected 可移除 pending；pending/unknown 继续隔离，绝不换 id 重发副作用。
        /// 内部同样受 world generation 和有界回执恢复保护，回调 exactly-once。
        /// </summary>
        public static void QueryOperation(string operationId, Action<ToolOutcome> onDone)
        {
            operationId = (operationId ?? "").Trim();
            if (!OperationRpcClient.IsValidOperationId(operationId))
            {
                onDone?.Invoke(ToToolOutcome(MakeLocalOutcome(false, "failed", "invalid_operation_id", false,
                    operationId, "查询缺少 32 位 operation_id")));
                return;
            }
            OperationRpcClient.QueryExistingReceipt(operationId,
                response => onDone?.Invoke(ToToolOutcome(response)));
        }

        /// <summary>按 durable journal 的原始身份只读查询；允许同世界太吾传承后的旧回执对账。</summary>
        public static void QueryOperation(uint worldId, int taiwuId, string operationId, Action<ToolOutcome> onDone)
        {
            operationId = (operationId ?? "").Trim();
            if (!OperationRpcClient.IsValidOperationId(operationId) || worldId == 0 || taiwuId <= 0)
            {
                onDone?.Invoke(ToToolOutcome(MakeLocalOutcome(false, "failed", "invalid_operation_identity", false,
                    operationId, "查询缺少完整原始 WorldId/TaiwuId/operation_id")));
                return;
            }
            OperationRpcClient.QueryExistingReceipt(worldId, taiwuId, operationId,
                response => onDone?.Invoke(ToToolOutcome(response)));
        }

        /// <summary>调用方 durable journal 已成功提交终态后确认后端回执；ACK 失败不影响原动作，只保留回执待下次重试。</summary>
        public static bool AcknowledgeOperation(string operationId, Action<bool> onDone = null)
            => OperationRpcClient.AcknowledgeExistingReceipt(operationId, onDone);

        /// <summary>按 durable journal 保留的原始世界/太吾身份确认回执；仅允许同世界传承后清理旧太吾记录。</summary>
        public static bool AcknowledgeOperation(uint worldId, int taiwuId, string operationId,
            Action<bool> onDone = null)
            => OperationRpcClient.AcknowledgeExistingReceipt(worldId, taiwuId, operationId, onDone);

        /// <summary>Bind a prepared durable mutation to the journal's captured identity before any RPC call.</summary>
        public static bool PrepareOperationIdentity(uint worldId, int taiwuId, string operationId)
            => OperationRpcClient.RegisterOperationIdentityExpectation(operationId, worldId, taiwuId);

        public static void DiscardPreparedOperation(string operationId)
            => OperationRpcClient.DiscardPreparedOperation(operationId);

        /// <summary>套出秘闻:让太吾知晓 NPC 吐露的一条秘闻。走后端 Gm(校验太吾有效 + 回传真实结果:广播态/已知会 false)。</summary>
        public static void ApplyShareSecret(int taiwuId, SecretInformationId secretId, Action<bool, string> onDone = null)
            => ApplyShareSecret(taiwuId, secretId, onDone, null);

        public static void ApplyShareSecret(int taiwuId, SecretInformationId secretId, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (taiwuId <= 0) { onDone?.Invoke(false, "太吾无效"); return; }
            CallGm("sharesecret", d => { d.Set("taiwu", taiwuId); d.Set("sid", (int)secretId); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未能套出此秘")); }, stableOperationId);
            Debug.Log("[江湖有灵] 套出秘闻 → 太吾知晓");
        }

        /// <summary>NPC 在其门派为太吾表态支持(提高太吾在该门派的支持率,效果看其门派地位)。走后端 Gm(校验门派身份/关系/在囚 + try/catch)。</summary>
        public static void ApplySectSupport(int npcId, Action<bool, string> onDone = null)
            => ApplySectSupport(npcId, onDone, null);

        public static void ApplySectSupport(int npcId, Action<bool, string> onDone, string stableOperationId)
        {
            if (npcId <= 0) { onDone?.Invoke(false, "角色无效"); return; }
            CallGm("sect_support", d => d.Set("npc", npcId),
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未能表态")); }, stableOperationId);
            Debug.Log("[江湖有灵] 门派表态支持太吾 npc=" + npcId);
        }

        /// <summary>投奔太吾门派(太吾村 OrgTemplateId=16)。走后端 Gm(校验角色有效 + try/catch)。</summary>
        public static void ApplyJoinTaiwuSect(int npcId)
            => ApplyJoinTaiwuSect(npcId, null);

        public static void ApplyJoinTaiwuSect(int npcId, string stableOperationId)
            => ApplyJoinTaiwuSect(npcId, null, stableOperationId);

        public static void ApplyJoinTaiwuSect(int npcId, Action<bool, string> onDone, string stableOperationId)
        {
            if (npcId <= 0) { onDone?.Invoke(false, "角色无效"); return; }
            CallGm("joinsect", d => d.Set("npc", npcId), resp =>
            {
                bool ok = false; string msg = null;
                resp?.Get("success", out ok); resp?.Get("message", out msg);
                onDone?.Invoke(ok, msg ?? (ok ? "已投奔" : "投奔未成"));
            }, stableOperationId);
            Debug.Log("[江湖有灵] 投奔太吾门派 npc=" + npcId);
        }

        /// <summary>NPC 投奔【指定门派】(按门派/据点名 force-change)。ok=false→名字没匹配上,投不成。</summary>
        public static void ApplyChangeOrgByName(int npcId, string sectName, Action<bool, string> onDone)
            => ApplyChangeOrgByName(npcId, sectName, onDone, null);

        public static void ApplyChangeOrgByName(int npcId, string sectName, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (npcId <= 0 || string.IsNullOrWhiteSpace(sectName)) { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("changeorgbyname", d => { d.Set("npc", npcId); d.Set("sect", sectName.Trim()); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, msg ?? (ok ? "已投奔" : "投不成")); }, stableOperationId);
        }

        /// <summary>只读:NPC 对某人的好感(供第三方追杀/营救前端预检)。取不到回 0。</summary>
        public static void QueryFavorTowards(int npcId, int targetId, Action<int> onResult)
        {
            if (npcId <= 0 || targetId <= 0) { onResult?.Invoke(0); return; }
            CallGm("favor_of", d => { d.Set("self", npcId); d.Set("target", targetId); },
                resp => { int fav = 0; resp?.Get("favor", out fav); onResult?.Invoke(fav); });
        }

        /// <summary>NPC 解囊/借予太吾银钱(NPC→太吾)。后端会按 NPC 实有钱封顶,故 onActual 回传"真实到账数额"
        /// (0=NPC 没钱没给成);调用方据此显示提示,避免显示 AI 请求的虚高数。</summary>
        public static void ApplyGiveSilver(int npcId, int taiwuId, int amount, Action<int, string> onActual = null, int toId = 0)
            => ApplyGiveSilver(npcId, taiwuId, amount, onActual, toId, null);

        public static void ApplyGiveSilver(int npcId, int taiwuId, int amount, Action<int, string> onActual, int toId,
            string stableOperationId)
        {
            if (amount <= 0 || npcId <= 0 || taiwuId <= 0 || npcId == taiwuId) { onActual?.Invoke(0, "参数无效"); return; }
            int bonus = toId > 0 ? 0 : GiftGoodwillFavor;   // 赠第三方 NPC 不刷对太吾的好感
            // 走后端 Gm 通道:无人情债转移 + 净加好感;后端按 NPC 实有钱封顶并回传实际数额 + 失败真因(如 no_money)
            CallGm("givesilver",
                d => { d.Set("npc", npcId); d.Set("taiwu", taiwuId); d.Set("amount", amount); d.Set("favor_bonus", bonus); if (toId > 0) d.Set("recipient", toId); },
                resp =>
                {
                    int actual = 0; bool ok = false; string msg = null;
                    resp?.Get("success", out ok);
                    if (ok) resp?.Get("amount", out actual);
                    resp?.Get("message", out msg);
                    onActual?.Invoke(actual, msg);
                }, stableOperationId);
            Debug.Log("[江湖有灵] NPC 赠/借银钱 " + npcId + "→"
                + (toId > 0 ? ("#" + toId) : "太吾") + " amount=" + amount
                + " (+好感" + bonus + ")");
        }

        /// <summary>NPC 把指定的随身物品赠予太吾(ItemKey 由对话层按名选定;count 可>1,资源/食材类常复数)。
        /// 后端按 NPC 实有数量封顶,onActual 回传"真实赠出数量"(0=没给成),调用方据此显示。</summary>
        public static void ApplyGiveItemKey(int npcId, int taiwuId, ItemKey key, int count = 1, Action<int> onActual = null)
            => ApplyGiveItemKey(npcId, taiwuId, key, count, onActual, null);

        public static void ApplyGiveItemKey(int npcId, int taiwuId, ItemKey key, int count, Action<int> onActual,
            string stableOperationId)
        {
            if (npcId <= 0 || taiwuId <= 0 || npcId == taiwuId) { onActual?.Invoke(0); return; }
            if (count < 1) count = 1;
            // 走后端 Gm 通道:无人情债转移 + 净加好感;后端按"类型+模板"在真实背包定位、按实有数量封顶并回传
            CallGm("giveitem",
                d => { d.Set("npc", npcId); d.Set("taiwu", taiwuId);
                    d.Set("it", (int)key.ItemType); d.Set("mod", (int)key.ModificationState); d.Set("t", (int)key.TemplateId); d.Set("id", key.Id);
                    d.Set("amount", count); d.Set("favor_bonus", GiftGoodwillFavor); },
                resp =>
                {
                    int actual = 0; bool ok = false;
                    resp?.Get("success", out ok);
                    if (ok) resp?.Get("amount", out actual);
                    onActual?.Invoke(actual);
                }, stableOperationId);
            Debug.Log("[江湖有灵] NPC 赠物 " + npcId + "→太吾 tpl=" + key.TemplateId + " x" + count + " (+好感" + GiftGoodwillFavor + ")");
        }

        /// <summary>全实时赠物:只传物名,后端按 NPC【当前】持有实时解析(物品随送随变,不依赖前端缓存)+按实有封顶 + 净加好感。
        /// onDone 回传(真实赠出数量, 失败原因);0=没给成,失败原因供如实回话。</summary>
        public static void ApplyGiveItemByName(int npcId, int taiwuId, string name, int count, Action<int, string> onDone, int toId = 0)
            => ApplyGiveItemByName(npcId, taiwuId, name, count, onDone, toId, null);

        public static void ApplyGiveItemByName(int npcId, int taiwuId, string name, int count, Action<int, string> onDone,
            int toId, string stableOperationId)
            => ApplyGiveItemByName(npcId, taiwuId, name, count, onDone, toId,
                allowEquipped: false, stableOperationId: stableOperationId);

        /// <summary>
        /// allowEquipped 只能由前端根据玩家本轮原话计算，绝不暴露给模型参数。
        /// 月度事件、同道自主行事和旧调用默认 false。
        /// </summary>
        public static void ApplyGiveItemByName(int npcId, int taiwuId, string name, int count,
            Action<int, string> onDone, int toId, bool allowEquipped, string stableOperationId)
        {
            if (npcId <= 0 || taiwuId <= 0 || npcId == taiwuId) { onDone?.Invoke(0, "角色无效"); return; }
            if (count < 1) count = 1;
            string nm = (name ?? "").Trim();
            int bonus = toId > 0 ? 0 : GiftGoodwillFavor;
            CallGm("giveitem",
                d => { d.Set("npc", npcId); d.Set("taiwu", taiwuId); d.Set("name", nm);
                    d.Set("amount", count); d.Set("favor_bonus", bonus);
                    d.Set("allow_equipped", allowEquipped ? 1 : 0);
                    if (toId > 0) d.Set("recipient", toId); },
                resp =>
                {
                    int actual = 0; bool ok = false; string msg = null;
                    resp?.Get("success", out ok);
                    if (ok) resp?.Get("amount", out actual); else resp?.Get("message", out msg);
                    onDone?.Invoke(actual, msg);
                }, stableOperationId);
            Debug.Log("[江湖有灵] NPC 赠物(按名) " + npcId + "→"
                + (toId > 0 ? ("#" + toId) : "太吾") + " name=" + nm + " x" + count
                + " allow_equipped=" + allowEquipped);
        }

        /// <summary>NPC 对某人结仇(makeEnemy=true)或放下旧怨(false)。单向仇敌位 Enemy=32768。
        /// 走后端 Gm:原 TryAdd/RemoveOneWayRelation 内部 GetElement_Objects(第三方) 对失效/已死者会抛 KeyNotFound 冲垮后端,故先校验再执行。</summary>
        public static void ApplyEnmity(int npcId, int targetId, bool makeEnemy, Action<bool, string> onDone = null)
            => ApplyEnmity(npcId, targetId, makeEnemy, onDone, null);

        public static void ApplyEnmity(int npcId, int targetId, bool makeEnemy, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (npcId <= 0 || targetId <= 0 || npcId == targetId) { onDone?.Invoke(false, "对象无效"); return; }
            CallGm("enmity", d => { d.Set("npc", npcId); d.Set("target", targetId); d.Set("make", makeEnemy ? 1 : 0); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "对方角色或已失效")); }, stableOperationId);
            Debug.Log("[江湖有灵] " + (makeEnemy ? "结仇" : "化解仇怨") + " " + npcId + "→" + targetId);
        }

        /// <summary>NPC 决意追随太吾(follow=true)或愤而离去(false)。
        /// 追随:前端直调 TaiwuFollowNpc(安全,带名单上限校验)。
        /// 离去:走后端 RPC——既退地图跟随名单、又(若在队伍)退队伍;后者 LeaveGroup 对不在队伍者会抛异常冲垮后端,故先判后退。</summary>
        public static void ApplyFollow(int npcId, bool follow, Action<bool, string> onDone = null)
            => ApplyFollow(npcId, follow, onDone, null);

        public static void ApplyFollow(int npcId, bool follow, Action<bool, string> onDone, string stableOperationId)
        {
            if (npcId <= 0) { onDone?.Invoke(false, "无效角色"); return; }
            if (follow)
            {
                // 入队:走后端 JoinGroup,让 NPC 真正加入太吾队伍(此前只调 TaiwuFollowNpc 加跟随名单、不入队 = "入队没生效")
                CallBoolRpc(RpcConst.JoinTeamMethod, d => d.Set("npc_id", npcId),
                    (ok, msg) => { Debug.Log("[江湖有灵] 入队 npc=" + npcId + ": " + ok + " " + msg); onDone?.Invoke(ok, msg); }, stableOperationId);
            }
            else
            {
                CallBoolRpc(RpcConst.LeaveMethod, d => d.Set("npc_id", npcId),
                    (ok, msg) => { Debug.Log("[江湖有灵] 离去 npc=" + npcId + ": " + ok + " " + msg); onDone?.Invoke(ok, msg); }, stableOperationId);
            }
        }

        /// <summary>NPC 把所知一条秘闻透露给某人(target)。走后端 Gm:校验目标有效(失效第三方会令引擎索引抛异常)+ 回传真实结果。</summary>
        public static void ApplyDiscloseSecret(SecretInformationId secretId, int sourceCharId, int targetCharId, Action<bool, string> onDone = null)
            => ApplyDiscloseSecret(secretId, sourceCharId, targetCharId, onDone, null);

        public static void ApplyDiscloseSecret(SecretInformationId secretId, int sourceCharId, int targetCharId,
            Action<bool, string> onDone, string stableOperationId)
        {
            if (targetCharId <= 0) { onDone?.Invoke(false, "对象无效"); return; }
            CallGm("disclosesecret", d => { d.Set("sid", (int)secretId); d.Set("src", sourceCharId); d.Set("target", targetCharId); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未能传达")); }, stableOperationId);
            Debug.Log("[江湖有灵] 传秘闻 → " + targetCharId);
        }

        /// <summary>只读预检:讲述者确知、秘闻未广播且接收者尚未知情时才允许真正传播。</summary>
        public static void QueryCanDiscloseSecret(SecretInformationId secretId, int sourceCharId, int targetCharId,
            Action<bool, string> onDone)
        {
            if (sourceCharId <= 0 || targetCharId <= 0 || sourceCharId == targetCharId)
            { onDone?.Invoke(false, "秘闻讲述者或接收者无效"); return; }
            CallGm("can_disclose_secret", d =>
                { d.Set("sid", (int)secretId); d.Set("src", sourceCharId); d.Set("target", targetCharId); },
                resp =>
                {
                    bool ok = false, can = false; string msg = null;
                    resp?.Get("success", out ok); resp?.Get("can", out can); resp?.Get("message", out msg);
                    onDone?.Invoke(ok && can, msg ?? (ok ? "当前不可传播" : "未能确认秘闻状态"));
                });
        }

        /// <summary>男媒女约:撮合 aId、bId 两个角色成婚(太吾做媒)。需后端权限+校验,异步回调。</summary>
        public static void ApplyMatchmake(int aId, int bId, Action<bool, string> onDone)
            => ApplyMatchmake(aId, bId, onDone, null);

        public static void ApplyMatchmake(int aId, int bId, Action<bool, string> onDone, string stableOperationId)
        {
            if (aId <= 0 || bId <= 0 || aId == bId) { onDone?.Invoke(false, "参数无效"); return; }
            CallBoolRpc(RpcConst.MatchmakeMethod, d => { d.Set("a_id", aId); d.Set("b_id", bId); }, onDone, stableOperationId);
        }

        /// <summary>队友判定:回调 (isTeammate, 提示语)。供"自定义人设"入口前置校验——是队友才放行编辑,否则提示需先入队。
        /// JHYL_ISTEAMMATE_READONLY_QUERY: 这是只读查询,直连后端读接口(与 QueryHostility/QueryDeadCharacters 一致),
        /// 绝不套 OperationRpcClient 的 operation_id 副作用信封——否则后端非队友(Fail 带 status 无 operation_id)回包会被
        /// 判为 operation_id 不匹配而丢弃,进而超时误报"存档或太吾身份已变化",拖垮同道月度队友判定。</summary>
        public static void QueryIsTeammate(int npcId, Action<bool, string> onResult)
        {
            if (npcId <= 0) { onResult?.Invoke(false, "无效角色"); return; }
            string modId = Plugin.Instance?.ModIdStr;
            if (string.IsNullOrWhiteSpace(modId)) modId = RpcConst.FallbackModId;
            if (IsWorldRpcBlocked())
            {
                try { onResult?.Invoke(false, "已离开存档,取消队友判定"); } catch { }
                Debug.LogWarning("[江湖有灵] RPC " + RpcConst.IsTeammateMethod + " 已取消:当前不在存档世界");
                return;
            }
            var p = new SerializableModData();
            p.Set("npc_id", npcId);
            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                null, modId, RpcConst.IsTeammateMethod, p,
                delegate (int offset, RawDataPool pool)
                {
                    try
                    {
                        SerializableModData resp = null;
                        Serializer.Deserialize(pool, offset, ref resp);
                        bool ok = false; string msg = null;
                        resp?.Get("success", out ok);
                        resp?.Get("message", out msg);
                        onResult?.Invoke(ok, msg ?? (ok ? "是队友" : "非队友"));
                    }
                    catch { onResult?.Invoke(false, "队友判定失败"); }
                });
        }

        /// <summary>NPC 对某第三方的好感升降(GmCmd_ChangeFavorability,relatedCharId 指第三方)。前端直调。</summary>
        public static void ApplyThirdPartyFavor(int npcId, int targetId, int delta)
            => ApplyThirdPartyFavor(npcId, targetId, delta, null);

        public static void ApplyThirdPartyFavor(int npcId, int targetId, int delta, string stableOperationId)
            => ApplyThirdPartyFavor(npcId, targetId, delta, null, stableOperationId);

        public static void ApplyThirdPartyFavor(int npcId, int targetId, int delta, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (delta == 0 || npcId <= 0 || targetId <= 0 || npcId == targetId)
            { onDone?.Invoke(false, "参数无效"); return; }
            // 走后端 Gm 通道:第三方目标可能已死/失效,直调 GmCmd 会崩;try/catch 兜住
            CallGm("favor", dd => { dd.Set("self", npcId); dd.Set("related", targetId); dd.Set("delta", delta); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未能改变好感")); }, stableOperationId);
            Debug.Log("[江湖有灵] NPC " + npcId + " 对第三方 " + targetId + " 好感 " + delta);
        }

        /// <summary>
        /// NPC 把指定的一门武学传授太吾(templateId 由对话层从其所习清单按名选定)。
        /// 走后端 RPC:① 按师父(NPC)自己的练法(正练/逆练 + 进度)传授;② RPC 内 try/catch 兜住
        /// LearnCombatSkill 对"已学过"等情形抛出的异常——直调 GM 命令时该异常会冲垮后端主循环导致游戏卡死(线上 bug)。
        /// </summary>
        public static void ApplyTeachSkillId(int npcId, int taiwuId, short templateId, Action<bool, string> onDone = null, int toId = 0)
            => ApplyTeachSkillId(npcId, taiwuId, templateId, onDone, toId, null);

        public static void ApplyTeachSkillId(int npcId, int taiwuId, short templateId, Action<bool, string> onDone,
            int toId, string stableOperationId)
        {
            if (taiwuId <= 0 || templateId < 0) { onDone?.Invoke(false, "参数无效"); return; }
            CallBoolRpc(RpcConst.TeachSkillMethod,
                d => { d.Set("npc_id", npcId); d.Set("taiwu_id", taiwuId); d.Set("template_id", (int)templateId); if (toId > 0) d.Set("learner_id", toId); },
                (ok, msg) => { Debug.Log("[江湖有灵] 传功 → 习得武学 " + templateId + ": " + ok + " " + msg); onDone?.Invoke(ok, msg); }, stableOperationId);
        }

        /// <summary>NPC 主动解除与某人(默认太吾)的一种现有关系(绝交/断义/和离/分手/逐出师门/化解仇怨)。
        /// 走后端 Gm "dissolve":用 Character.ApplySever*/ApplyEndRelation_* 带完整副作用断绝,先 HasRelation 校验免空操作。</summary>
        public static void ApplyDissolveRelation(int npcId, int taiwuId, string rel, int targetId, Action<bool, string> onDone)
            => ApplyDissolveRelation(npcId, taiwuId, rel, targetId, onDone, null);

        public static void ApplyDissolveRelation(int npcId, int taiwuId, string rel, int targetId,
            Action<bool, string> onDone, string stableOperationId)
        {
            if (npcId <= 0 || taiwuId <= 0 || string.IsNullOrEmpty(rel)) { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("dissolve",
                d => { d.Set("npc", npcId); d.Set("taiwu", taiwuId); d.Set("target", targetId > 0 ? targetId : taiwuId); d.Set("rel", rel); },
                resp =>
                {
                    bool ok = false; string msg = null;
                    resp?.Get("success", out ok); resp?.Get("message", out msg);
                    onDone?.Invoke(ok, msg);
                }, stableOperationId);
            Debug.Log("[江湖有灵] 解除关系 " + rel + " " + npcId + "→" + (targetId > 0 ? targetId : taiwuId));
        }

        /// <summary>只读:某角色随身熟食(EatingItems,荷包蛋等)的 ItemKey 列表。熟食不在背包显示数据里,故经后端读。</summary>
        /// <summary>只读:该 NPC 是否为太吾的阶下囚(绳缚俘虏 KidnapperId==太吾,或关在太吾村牢中)。供对话注入"身陷囹圄"语境。</summary>
        public static void QueryCaptiveByTaiwu(int charId, Action<bool> onResult)
        {
            if (charId <= 0) { onResult?.Invoke(false); return; }
            CallGm("captive_state", d => d.Set("npc", charId),
                resp => { bool ok = false, cap = false; resp?.Get("success", out ok); if (ok) resp?.Get("captive", out cap); onResult?.Invoke(cap); });
        }

        public static void QueryCharFood(int charId, Action<bool, List<ItemKey>> onResult)
        {
            if (charId <= 0) { onResult?.Invoke(false, new List<ItemKey>()); return; }
            CallGm("char_food", d => d.Set("char", charId),
                resp =>
                {
                    var list = new List<ItemKey>(); bool ok = false; string food = null;
                    resp?.Get("success", out ok); if (ok) resp?.Get("food", out food);
                    if (!string.IsNullOrEmpty(food))
                        foreach (var part in food.Split(';'))
                        {
                            var f = part.Split(',');
                            if (f.Length == 4 && int.TryParse(f[0], out int it) && int.TryParse(f[1], out int mod) && int.TryParse(f[2], out int t) && int.TryParse(f[3], out int id))
                                list.Add(new ItemKey((sbyte)it, (byte)mod, (short)t, id));
                        }
                    // 空清单与查询失败语义不同；调用方只有在 success=true 时才能把
                    // “没有熟食”当作权威负向事实。
                    onResult?.Invoke(ok, list);
                });
        }

        /// <summary>严格只读:商人已由游戏本体建立的 MerchantData.GoodsList0..6；不会因查询创建/换季刷新货架。</summary>
        public static void QueryMerchantGoods(int npcId, int merchantTemplateId, Action<List<KeyValuePair<ItemKey, int>>> onResult)
            => QueryMerchantGoodsWithSource(npcId, merchantTemplateId,
                result => onResult?.Invoke(result?.Goods ?? new List<KeyValuePair<ItemKey, int>>()));

        public static void QueryMerchantGoodsWithSource(int npcId, int merchantTemplateId,
            Action<MerchantGoodsQueryResult> onResult)
        {
            if (npcId <= 0) { onResult?.Invoke(new MerchantGoodsQueryResult()); return; }
            CallGm("merchant_goods", d => { d.Set("npc", npcId); d.Set("tpl", merchantTemplateId); },
                resp =>
                {
                    var result = new MerchantGoodsQueryResult(); string goods = null;
                    resp?.Get("success", out result.Ok);
                    if (result.Ok)
                    {
                        resp?.Get("goods", out goods);
                        resp?.Get("owner_type", out result.OwnerType);
                        resp?.Get("owner_id", out result.OwnerId);
                    }
                    if (!string.IsNullOrEmpty(goods))
                        foreach (var part in goods.Split(';'))
                        {
                            var f = part.Split(',');
                            if (f.Length >= 2 && int.TryParse(f[0], out int it) && int.TryParse(f[1], out int t))
                            {
                                int amt = (f.Length >= 3 && int.TryParse(f[2], out int a)) ? a : 0;   // 货架编码 "it,t,amount",第三段=库存数量
                                result.Goods.Add(new KeyValuePair<ItemKey, int>(new ItemKey((sbyte)it, (byte)0, (short)t, 0), amt));
                            }
                        }
                    onResult?.Invoke(result);
                });
        }

        /// <summary>只读:查 speaker 与 target 之间的关系名 + speaker 对 target 的好感(归一)。供"查熟人"。</summary>
        public static void QueryPersonRel(int speakerId, int targetId, Action<string, int> onResult)
        {
            if (speakerId <= 0 || targetId <= 0) { onResult?.Invoke("", 0); return; }
            CallGm("person_info", d => { d.Set("speaker", speakerId); d.Set("target", targetId); },
                resp =>
                {
                    string rel = null; int favor = 0; bool ok = false;
                    resp?.Get("success", out ok);
                    if (ok) { resp?.Get("rel", out rel); resp?.Get("favor", out favor); }
                    onResult?.Invoke(rel ?? "", favor);
                });
        }

        /// <summary>
        /// 首次打开 AI 对话前解析人物身份。普通人物原样返回；固定模板人物由后端建立或复用
        /// 永久独立副本。回调中的 original/resolved 用于前端执行一次性聊天与记忆迁移。
        /// </summary>
        public static void EnsureCharacterProxy(int npcId,
            Action<bool, int, int, bool, bool, int, short, string> onDone)
        {
            if (npcId <= 0)
            { onDone?.Invoke(false, npcId, npcId, false, false, 0, -1, "无效角色"); return; }
            CallBoolRpcDetailed(RpcConst.EnsureCharacterProxyMethod,
                d => d.Set("npc_id", npcId),
                (ok, message, response) =>
                {
                    int originalId = npcId, resolvedId = npcId;
                    int migrationFromId = 0, displayTemplateRaw = -1;
                    bool created = false, isProxy = false;
                    response?.Get("original_npc_id", out originalId);
                    response?.Get("resolved_npc_id", out resolvedId);
                    response?.Get("proxy_created", out created);
                    response?.Get("is_proxy", out isProxy);
                    response?.Get("migration_from_npc_id", out migrationFromId);
                    response?.Get("display_template_id", out displayTemplateRaw);
                    if (originalId <= 0) originalId = npcId;
                    if (resolvedId <= 0) resolvedId = npcId;
                    short displayTemplateId = displayTemplateRaw >= short.MinValue
                        && displayTemplateRaw <= short.MaxValue
                        ? (short)displayTemplateRaw : (short)-1;
                    onDone?.Invoke(ok, originalId, resolvedId, created, isProxy,
                        migrationFromId, displayTemplateId, message);
                });
        }

        /// <summary>只读：读取画像生成所需且 CharacterDisplayData 未公开的稳定人物字段。</summary>
        public static void QueryPersonaTraits(int charId, Action<bool, bool> onResult)
        {
            if (charId <= 0) { onResult?.Invoke(false, false); return; }
            CallGm("persona_traits", d => d.Set("char", charId), resp =>
            {
                bool ok = false, bisexual = false;
                resp?.Get("success", out ok);
                if (ok) resp?.Get("bisexual", out bisexual);
                onResult?.Invoke(ok, bisexual);
            });
        }

        /// <summary>只读:批量读一串 NPC 对太吾的好感(同序返回;未互动/陌路=0)。供候选排序与诊断使用。</summary>
        public static void QueryFavors(IList<int> ids, int taiwuId, Action<List<int>> onResult)
        {
            if (ids == null || ids.Count == 0) { onResult?.Invoke(new List<int>()); return; }
            CallGm("favors", d => { d.Set("ids", string.Join(",", ids)); d.Set("taiwu", taiwuId); },
                resp =>
                {
                    var outv = new List<int>(); bool ok = false; string csv = null;
                    resp?.Get("success", out ok); if (ok) resp?.Get("favors", out csv);
                    if (!string.IsNullOrEmpty(csv)) foreach (var s in csv.Split(',')) { int v; outv.Add(int.TryParse(s, out v) ? v : 0); }
                    onResult?.Invoke(outv);
                });
        }

        /// <summary>只读:给一串 charId,按同序返回各自姓名(供群聊选人列表显示)。onResult 回传与 ids 同序的名字列表(取不到的为空串占位)。</summary>
        public static void QueryCharNames(IList<int> ids, Action<List<string>> onResult)
            => QueryCharNamesAndGenders(ids, (names, genders) => onResult?.Invoke(names));

        /// <summary>只读：按输入顺序同时返回人物姓名与权威性别，供多人物提示词建立身份账本。</summary>
        public static void QueryCharNamesAndGenders(IList<int> ids,
            Action<List<string>, List<string>> onResult)
        {
            if (ids == null || ids.Count == 0)
            { onResult?.Invoke(new List<string>(), new List<string>()); return; }
            var requested = new List<int>(ids.Count);
            foreach (int id in ids) requested.Add(id);
            string cacheKey = SharedReadKey("char_names", string.Join(",", requested));
            var invalidated = new CharacterIdentityRead
            {
                Ok = false,
                Names = new List<string>(),
                Genders = new List<string>(),
            };
            if (SharedReadQueryCache.TryServeOrJoin(cacheKey, (CharacterIdentityRead value) =>
            {
                bool ok = value != null && value.Ok;
                onResult?.Invoke(ok && value.Names != null
                        ? new List<string>(value.Names) : new List<string>(),
                    ok && value.Genders != null
                        ? new List<string>(value.Genders) : new List<string>());
            }, invalidated)) return;
            CallGm("char_names", d => d.Set("ids", string.Join(",", ids)),
                resp =>
                {
                    var names = new List<string>();
                    var genders = new List<string>();
                    bool ok = false; string joined = null, joinedGenders = null;
                    resp?.Get("success", out ok);
                    if (ok) { resp?.Get("names", out joined); resp?.Get("genders", out joinedGenders); }
                    if (!string.IsNullOrEmpty(joined)) foreach (var s in joined.Split((char)1)) names.Add(s);
                    if (!string.IsNullOrEmpty(joinedGenders))
                        foreach (var s in joinedGenders.Split((char)1)) genders.Add(s);
                    while (genders.Count < names.Count) genders.Add("未知");
                    SharedReadQueryCache.Complete(cacheKey,
                        new CharacterIdentityRead
                        {
                            Ok = ok,
                            Names = names,
                            Genders = genders,
                        },
                        ok, TimeSpan.FromSeconds(30));
                });
        }

        /// <summary>只读:给一串 charId,返回他们两两之间的显著关系文本(夫妻/结义/师徒/挚友/情愫/仇敌;分号分隔),供过月事件据实写人物纠葛。无则空串。</summary>
        public static void QueryRosterRelations(IList<int> ids, Action<string> onResult)
            => QueryRosterRelations(ids, false, onResult);

        /// <summary>只读关系矩阵。includeEmpty=true 时每一对都明确返回“无显著关系”，供群聊每轮完整核对。</summary>
        public static void QueryRosterRelations(IList<int> ids, bool includeEmpty, Action<string> onResult)
            => QueryRosterRelations(ids, includeEmpty, (ok, value) => onResult?.Invoke(ok ? value : null));

        /// <summary>只读关系矩阵，并显式回传后端读取是否成功；群聊不能把读取失败伪装成“无关系”。</summary>
        public static void QueryRosterRelations(IList<int> ids, bool includeEmpty, Action<bool, string> onResult)
        {
            if (ids == null || ids.Count < 2) { onResult?.Invoke(true, ""); return; }
            var sorted = new SortedSet<int>();
            foreach (int id in ids) if (id > 0) sorted.Add(id);
            string cacheKey = SharedReadKey("roster_relations",
                (includeEmpty ? "1:" : "0:") + string.Join(",", sorted));
            var invalidated = new RosterRelationRead { Ok = false, Text = null };
            if (SharedReadQueryCache.TryServeOrJoin(cacheKey,
                (RosterRelationRead value) => onResult?.Invoke(value != null && value.Ok,
                    value != null && value.Ok ? value.Text : null), invalidated)) return;
            CallGm("roster_relations", d => { d.Set("ids", string.Join(",", ids)); d.Set("include_empty", includeEmpty); },
                resp =>
                {
                    bool ok = false; string rel = null;
                    resp?.Get("success", out ok); if (ok) resp?.Get("relations", out rel);
                    SharedReadQueryCache.Complete(cacheKey,
                        new RosterRelationRead { Ok = ok, Text = ok ? (rel ?? "") : null },
                        ok, TimeSpan.FromSeconds(3));
                });
        }

        /// <summary>全实时:枚举 NPC【当前】可赠/可换/可装备之物名(背包+资源+熟食+佩带),按 keyword 后端过滤。
        /// onResult 回传(过滤后名字列表, 未过滤总数);物品随送/换而变,故每次实时读、绝不缓存。</summary>
        public static void QueryNpcItems(int npcId, string keyword, Action<List<string>, int> onResult)
        {
            if (npcId <= 0) { onResult?.Invoke(new List<string>(), 0); return; }
            CallGm("npc_items", d => { d.Set("char", npcId); d.Set("keyword", keyword ?? ""); },
                resp =>
                {
                    var names = new List<string>(); int total = 0; bool ok = false; string joined = null;
                    resp?.Get("success", out ok);
                    if (ok) { resp?.Get("names", out joined); resp?.Get("total", out total); }
                    if (!string.IsNullOrEmpty(joined))
                        foreach (var s in joined.Split((char)1)) if (!string.IsNullOrWhiteSpace(s)) names.Add(s);
                    onResult?.Invoke(names, total);
                });
        }

        /// <summary>实时读取与后端 use_item 共用判定的 NPC 可用消耗品；读取失败不会伪装成空列表。</summary>
        public static void QueryNpcUsableItems(int npcId,
            Action<bool, List<string>, string> onResult)
        {
            if (npcId <= 0)
            {
                onResult?.Invoke(false, null, "角色无效");
                return;
            }
            CallGm("query_npc_usable_items", d => d.Set("char", npcId), resp =>
            {
                bool ok = false; string names = null, message = null;
                resp?.Get("success", out ok); resp?.Get("message", out message);
                if (ok) resp?.Get("names", out names);
                var parsed = new List<string>();
                if (!string.IsNullOrEmpty(names))
                    foreach (string raw in names.Split((char)1))
                        if (!string.IsNullOrWhiteSpace(raw)) parsed.Add(raw.Trim());
                onResult?.Invoke(ok, ok ? parsed : null,
                    ok ? null : (message ?? "可用物品读取失败"));
            });
        }

        /// <summary>只读:某角色拥有的奇书(LegendaryBook,单独域、不在背包 Items 里)名字。供物品查询补全。</summary>
        public static void QueryLegendaryBooks(int charId, Action<List<string>> onResult)
        {
            if (charId <= 0) { onResult?.Invoke(new List<string>()); return; }
            CallGm("char_books", d => d.Set("char", charId),
                resp =>
                {
                    var names = new List<string>(); string joined = null; resp?.Get("names", out joined);
                    if (!string.IsNullOrEmpty(joined))
                        foreach (var s in joined.Split((char)1)) if (!string.IsNullOrWhiteSpace(s)) names.Add(s);
                    onResult?.Invoke(names);
                });
        }

        /// <summary>只读:某角色当前真正正在运功的内功 templateId; -1=未运功/读不到。供 query_npc_build 区分"会"与"正在运"。</summary>
        public static void QueryLoopingNeigong(int charId, Action<short> onResult)
        {
            if (charId <= 0) { onResult?.Invoke(-1); return; }
            CallGm("looping_neigong", d => d.Set("char", charId),
                resp =>
                {
                    bool ok = false; int skill = -1;
                    resp?.Get("success", out ok);
                    if (ok) resp?.Get("skill", out skill);
                    onResult?.Invoke((short)skill);
                });
        }

        /// <summary>只读:太吾当前所在地块上的全部可交互角色 id(已剔除太吾)。
        /// 现场、距离、群聊挑人和敌对动作都不能隐藏仇敌；只有明确做“友好主动联系”
        /// 候选筛选的调用方才可显式传 includeHostile=false。</summary>
        public static void QuerySameBlockChars(int taiwuId, Action<List<int>> onResult, bool includeHostile = true)
        {
            if (taiwuId <= 0) { onResult?.Invoke(new List<int>()); return; }
            // 两种语义都显式发给后端。省略 false 会在后端“现场默认完整”后
            // 误把友好主动候选查询也变成含仇敌名单。
            CallGm("block_chars", d => { d.Set("taiwu", taiwuId); d.Set("allow_hostile", includeHostile ? 1 : 0); },
                resp =>
                {
                    var list = new List<int>(); bool ok = false; string ids = null;
                    resp?.Get("success", out ok); if (ok) resp?.Get("ids", out ids);
                    if (!string.IsNullOrEmpty(ids))
                        foreach (var part in ids.Split(','))
                            if (int.TryParse(part.Trim(), out int id) && id > 0) list.Add(id);
                    onResult?.Invoke(CharacterProxyIdentityService.NormalizeKnownIds(taiwuId, list));
                });
        }

        /// <summary>任意角色自己所在的真实地块名册；用于非同道的月度主动行动。</summary>
        public static void QueryTaiwuScenePresence(int taiwuId, IEnumerable<int> characterIds,
            Action<bool, List<int>> onResult)
        {
            var requested = new SortedSet<int>();
            if (characterIds != null)
                foreach (int id in characterIds) if (id > 0) requested.Add(id);
            if (taiwuId <= 0 || requested.Count == 0 || requested.Count > 16)
            {
                onResult?.Invoke(false, new List<int>());
                return;
            }
            string cacheKey = SharedReadKey("taiwu_scene_presence",
                taiwuId + ":" + string.Join(",", requested));
            var invalidated = new ScenePresenceRead { Ok = false, Present = new List<int>() };
            if (SharedReadQueryCache.TryServeOrJoin(cacheKey, (ScenePresenceRead value) =>
            {
                bool ok = value != null && value.Ok;
                onResult?.Invoke(ok, ok && value.Present != null
                    ? new List<int>(value.Present) : new List<int>());
            }, invalidated)) return;
            CallGm("taiwu_scene_presence",
                d => { d.Set("taiwu", taiwuId); d.Set("ids", string.Join(",", requested)); },
                resp =>
                {
                    bool ok = false; string csv = null;
                    resp?.Get("success", out ok);
                    if (ok) resp?.Get("ids", out csv);
                    var present = new List<int>();
                    if (!string.IsNullOrWhiteSpace(csv))
                        foreach (string part in csv.Split(','))
                            if (int.TryParse(part.Trim(), out int id) && id > 0) present.Add(id);
                    SharedReadQueryCache.Complete(cacheKey,
                        new ScenePresenceRead { Ok = ok, Present = present },
                        ok, TimeSpan.FromSeconds(1));
                });
        }

        public static void QueryActorBlockChars(int actorId,
            Action<bool, short, short, List<int>> onResult)
        {
            if (actorId <= 0) { onResult?.Invoke(false, -1, -1, new List<int>()); return; }
            CallGm("actor_block_chars", d => d.Set("actor", actorId), resp =>
            {
                bool ok = false; int area = -1, block = -1; string csv = null;
                resp?.Get("success", out ok);
                if (ok) { resp?.Get("area", out area); resp?.Get("block", out block); resp?.Get("ids", out csv); }
                var ids = new List<int>();
                if (!string.IsNullOrWhiteSpace(csv))
                    foreach (string part in csv.Split(','))
                        if (int.TryParse(part.Trim(), out int id) && id > 0) ids.Add(id);
                int taiwuId = 0;
                try { taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId; } catch { }
                onResult?.Invoke(ok, (short)area, (short)block,
                    CharacterProxyIdentityService.NormalizeKnownIds(taiwuId, ids));
            });
        }

        /// <summary>只读:过月同道候选。后端按本体 Character.GetAgeGroup 排除 Baby(0)，
        /// 并按 ProfessionSkillHandle.AnimalCharacterTemplateIds 排除动物角色；保留太吾作为名册完整性哨兵。</summary>
        public static void QueryNonBabyCompanionGroup(int taiwuId, Action<bool, List<int>> onResult)
        {
            if (taiwuId <= 0) { onResult?.Invoke(false, new List<int>()); return; }
            CallGm("companion_group_non_baby", d => d.Set("taiwu", taiwuId),
                resp =>
                {
                    var list = new List<int>(); bool ok = false; string ids = null;
                    resp?.Get("success", out ok); if (ok) resp?.Get("ids", out ids);
                    if (!string.IsNullOrEmpty(ids))
                        foreach (var part in ids.Split(','))
                            if (int.TryParse(part.Trim(), out int id) && id > 0) list.Add(id);
                    onResult?.Invoke(ok, CharacterProxyIdentityService.NormalizeKnownIds(
                        taiwuId, list, includeTaiwu: true));
                });
        }

        /// <summary>校验任意手动加入的主动人物；婴儿、动物与已失效角色会被权威后端剔除。</summary>
        public static void QueryMonthlyAgentEligibility(IList<int> characterIds,
            Action<bool, List<int>, string> onResult)
        {
            var valid = new SortedSet<int>();
            if (characterIds != null)
                foreach (int id in characterIds) if (id > 0) valid.Add(id);
            if (valid.Count == 0) { onResult?.Invoke(true, new List<int>(), string.Empty); return; }
            CallGm("monthly_agent_eligibility", d => d.Set("ids", string.Join(",", valid)),
                resp =>
                {
                    var list = new List<int>(); bool ok = false; string ids = null, rejected = null;
                    resp?.Get("success", out ok);
                    if (ok) { resp?.Get("ids", out ids); resp?.Get("rejected", out rejected); }
                    if (!string.IsNullOrEmpty(ids))
                        foreach (string part in ids.Split(','))
                            if (int.TryParse(part.Trim(), out int id) && id > 0) list.Add(id);
                    onResult?.Invoke(ok,
                        CharacterProxyIdentityService.NormalizeKnownIds(
                            SingletonObject.getInstance<BasicGameData>().TaiwuCharId, list),
                        rejected ?? string.Empty);
                });
        }

        /// <summary>只读:角色真实后端 Location。用于过月事件地点,避免前端 DisplayData.Location 滞后。</summary>
        public static void QueryCharLocation(int charId, Action<short, short, bool> onResult)
        {
            if (charId <= 0) { onResult?.Invoke(-1, -1, false); return; }
            CallGm("char_location", d => d.Set("char", charId),
                resp =>
                {
                    bool ok = false; int area = -1, block = -1;
                    resp?.Get("success", out ok);
                    if (ok) { resp?.Get("area", out area); resp?.Get("block", out block); }
                    onResult?.Invoke((short)area, (short)block, ok);
                });
        }

        /// <summary>只读：角色当前普通行程、动态寻人或固定赴约状态。每轮对话实时读取，
        /// 避免已经抵达/到期的旧承诺仍被历史记忆误说成“还在路上”。</summary>
        public static void QueryTravelState(int charId, Action<string> onResult)
        {
            if (charId <= 0) { onResult?.Invoke(null); return; }
            CallGm("travel_state", d => d.Set("char", charId),
                resp =>
                {
                    bool ok = false; string text = null;
                    resp?.Get("success", out ok);
                    if (ok) resp?.Get("text", out text);
                    onResult?.Invoke(ok ? text : null);
                });
        }

        /// <summary>只读:某门派(OrgTemplateId)成员 id(grade=-1 全部 / 0-8 指定品级;cap 上限)。供"查门派有谁 / 月度事件选人"。</summary>
        public static void QueryOrgMembers(int orgTemplateId, int grade, int cap, int taiwuId, Action<List<int>> onResult)
        {
            if (orgTemplateId <= 0) { onResult?.Invoke(new List<int>()); return; }
            CallGm("org_members", d => { d.Set("org", orgTemplateId); d.Set("grade", grade); d.Set("cap", cap); d.Set("taiwu", taiwuId); },
                resp =>
                {
                    var list = new List<int>(); bool ok = false; string ids = null;
                    resp?.Get("success", out ok); if (ok) resp?.Get("ids", out ids);
                    if (!string.IsNullOrEmpty(ids))
                        foreach (var part in ids.Split(','))
                            if (int.TryParse(part.Trim(), out int id) && id > 0) list.Add(id);
                    onResult?.Invoke(CharacterProxyIdentityService.NormalizeKnownIds(taiwuId, list));
                });
        }

        /// <summary>只读:某区域内的角色 id(去重、剔除太吾、上限 cap)。供"AI 月度事件按地远近知会附近人"。</summary>
        public static void QueryAreaChars(int areaId, int taiwuId, int cap, Action<List<int>> onResult)
        {
            QueryAreaCharsDetailed(areaId, taiwuId, cap, r => onResult?.Invoke(r == null ? new List<int>() : r.Ids));
        }

        public static void QueryAreaCharsDetailed(int areaId, int taiwuId, int cap, Action<AreaCharsQueryResult> onResult)
        {
            if (areaId < 0) { onResult?.Invoke(new AreaCharsQueryResult { Source = "invalid_area" }); return; }
            CallGm("area_chars", d => { d.Set("area", areaId); d.Set("cap", cap); d.Set("taiwu", taiwuId); },
                resp =>
                {
                    var r = new AreaCharsQueryResult();
                    bool ok = false; string ids = null;
                    resp?.Get("success", out ok); if (ok) resp?.Get("ids", out ids);
                    if (!string.IsNullOrEmpty(ids))
                        foreach (var part in ids.Split(','))
                            if (int.TryParse(part.Trim(), out int id) && id > 0) r.Ids.Add(id);
                    resp?.Get("block_count", out r.BlockCount);
                    resp?.Get("alive_location_count", out r.AliveLocationCount);
                    resp?.Get("alive_scanned", out r.AliveScanned);
                    resp?.Get("source", out r.Source);
                    r.Ids = CharacterProxyIdentityService.NormalizeKnownIds(taiwuId, r.Ids);
                    onResult?.Invoke(r);
                });
        }

        /// <summary>只读:查太吾现有银钱(资源位6),供商人议价前判断太吾买不买得起。onResult(-1=查询失败)。</summary>
        public static void QueryTaiwuMoney(int taiwuId, Action<int> onResult)
        {
            if (taiwuId <= 0) { onResult?.Invoke(-1); return; }
            CallGm("taiwu_money", d => d.Set("taiwu", taiwuId),
                resp => { int m = -1; bool ok = false; resp?.Get("success", out ok); if (ok) resp?.Get("money", out m); onResult?.Invoke(m); });
        }

        public static void QueryCommissionSnapshot(int taiwuId, int npcId,
            Action<CommissionSnapshot> onResult)
            => QueryCommissionSnapshot(taiwuId, npcId, -1, onResult);

        public static void QueryCommissionSnapshot(int taiwuId, int npcId, int targetNpcId,
            Action<CommissionSnapshot> onResult)
        {
            if (taiwuId <= 0 || npcId < CommissionStore.AssistantCommissionerId
                || taiwuId == npcId)
            { onResult?.Invoke(new CommissionSnapshot()); return; }
            CallGm("commission_snapshot",
                d =>
                {
                    d.Set("taiwu", taiwuId); d.Set("npc", npcId);
                    if (targetNpcId > 0) d.Set("target", targetNpcId);
                },
                resp =>
                {
                    var result = new CommissionSnapshot();
                    resp?.Get("success", out result.Ok);
                    resp?.Get("favor", out result.Favor);
                    resp?.Get("world_progress", out result.WorldProgress);
                    resp?.Get("target_valid", out result.TargetValid);
                    resp?.Get("target_dead", out result.TargetDead);
                    resp?.Get("target_id", out result.TargetNpcId);
                    resp?.Get("target_consummate", out result.TargetConsummate);
                    resp?.Get("target_reward_grade", out result.TargetRewardGrade);
                    string csv = null; resp?.Get("resources", out csv);
                    if (!string.IsNullOrWhiteSpace(csv))
                    {
                        string[] parts = csv.Split(',');
                        for (int i = 0; i < result.Resources.Length && i < parts.Length; i++)
                            int.TryParse(parts[i], out result.Resources[i]);
                    }
                    onResult?.Invoke(result);
                });
        }

        public static void ApplyCommissionClaim(CommissionRecord record,
            Action<CommissionClaimResult> onDone)
        {
            if (record == null || !record.IsActive || record.TaiwuId <= 0
                || record.NpcId < CommissionStore.AssistantCommissionerId
                || record.TaiwuId == record.NpcId)
            { onDone?.Invoke(new CommissionClaimResult { Message = "委托记录无效" }); return; }
            CallGm("commission_claim",
                d =>
                {
                    d.Set("taiwu", record.TaiwuId); d.Set("npc", record.NpcId);
                    d.Set("kind", record.Kind ?? ""); d.Set("resource", record.ResourceType);
                    d.Set("amount", record.Amount); d.Set("baseline", record.BaselineValue);
                    d.Set("reward_grade", record.RewardGrade);
                    d.Set("target", record.TargetNpcId);
                },
                resp =>
                {
                    var result = new CommissionClaimResult();
                    resp?.Get("success", out result.Success);
                    resp?.Get(RpcConst.OperationCodeField, out result.Code);
                    resp?.Get("message", out result.Message);
                    resp?.Get("reward_kind", out result.RewardKind);
                    resp?.Get("reward_name", out result.RewardName);
                    resp?.Get("reward_amount", out result.RewardAmount);
                    resp?.Get("reward_grade", out result.RewardGrade);
                    onDone?.Invoke(result);
                }, record.ClaimOperationId);
        }

        /// <summary>商人 NPC 被说动后把一件货物卖给太吾,自动按议价从太吾处扣银钱(NPC→太吾物品 + 太吾→NPC 银钱)。
        /// 走后端 Gm "trade":先校验太吾钱够、按实有封顶、部分成交按量折价,绕开 SettleTrade。onDone(成败, 实际成交量, 实付银钱, 提示)。</summary>
        public static void ApplyTrade(int npcId, int taiwuId, ItemKey key, int amount, int price, int merchantTpl, Action<bool, int, int, string> onDone)
            => ApplyTrade(npcId, taiwuId, key, amount, price, merchantTpl, onDone, null);

        public static void ApplyTrade(int npcId, int taiwuId, ItemKey key, int amount, int price, int merchantTpl,
            Action<bool, int, int, string> onDone, string stableOperationId)
            => ApplyTrade(npcId, taiwuId, key, amount, price, merchantTpl, -1, -1,
                onDone, stableOperationId);

        public static void ApplyTrade(int npcId, int taiwuId, ItemKey key, int amount, int price, int merchantTpl,
            int ownerType, int ownerId, Action<bool, int, int, string> onDone, string stableOperationId)
        {
            if (npcId <= 0 || taiwuId <= 0 || npcId == taiwuId) { onDone?.Invoke(false, 0, 0, "参数无效"); return; }
            CallGm("trade",
                d => { d.Set("npc", npcId); d.Set("taiwu", taiwuId);
                    d.Set("it", (int)key.ItemType); d.Set("mod", (int)key.ModificationState); d.Set("t", (int)key.TemplateId); d.Set("id", key.Id);
                    d.Set("amount", amount < 1 ? 1 : amount); d.Set("price", price < 0 ? 0 : price); d.Set("from_goods", 1); d.Set("tpl", merchantTpl);
                    d.Set("owner_type", ownerType); d.Set("owner_id", ownerId); },   // 商人只卖本轮查询锁定的现成真货架；成交不得暗中生成/刷新
                resp =>
                {
                    bool ok = false; int amt = 0, pay = 0; string msg = null;
                    resp?.Get("success", out ok); resp?.Get("amount", out amt); resp?.Get("price", out pay); resp?.Get("message", out msg);
                    onDone?.Invoke(ok, amt, pay, msg);
                }, stableOperationId);
            Debug.Log("[江湖有灵] 商人售货 " + npcId + "→太吾 tpl=" + key.TemplateId + " x" + amount + " 议价=" + price);
        }

        /// <summary>NPC 把一门技艺(生活技能)传授太吾。走后端 Gm "teachlife":校验师父通晓+太吾未学,LearnLifeSkill(readingState=0=0%造诣,太吾自行研习)。</summary>
        public static void ApplyTeachLifeSkill(int npcId, int taiwuId, short templateId, Action<bool, string> onDone = null, int toId = 0)
            => ApplyTeachLifeSkill(npcId, taiwuId, templateId, onDone, toId, null);

        public static void ApplyTeachLifeSkill(int npcId, int taiwuId, short templateId, Action<bool, string> onDone,
            int toId, string stableOperationId)
        {
            if (taiwuId <= 0 || templateId < 0) { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("teachlife",
                d => { d.Set("npc", npcId); d.Set("taiwu", taiwuId); d.Set("tpl", (int)templateId); if (toId > 0) d.Set("recipient", toId); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, msg); }, stableOperationId);
            Debug.Log("[江湖有灵] 传技艺 → 太吾 tpl=" + templateId);
        }

        /// <summary>太吾把自己的物品/资源送给 NPC(NPC 收下)。后端校验太吾确有此物、按实有封顶,onActual 回传真实赠出数量(0=没给成)。</summary>
        public static void ApplyTaiwuGiveItem(int taiwuId, int npcId, ItemKey key, int count, Action<int> onActual = null)
            => ApplyTaiwuGiveItem(taiwuId, npcId, key, count, onActual, null);

        public static void ApplyTaiwuGiveItem(int taiwuId, int npcId, ItemKey key, int count, Action<int> onActual,
            string stableOperationId)
        {
            if (taiwuId <= 0 || npcId <= 0 || taiwuId == npcId) { onActual?.Invoke(0); return; }
            if (count < 1) count = 1;
            CallGm("taiwu_give_item",
                d => { d.Set("npc", npcId); d.Set("taiwu", taiwuId);
                    d.Set("it", (int)key.ItemType); d.Set("mod", (int)key.ModificationState); d.Set("t", (int)key.TemplateId); d.Set("id", key.Id);
                    d.Set("amount", count); },
                resp => { int actual = 0; bool ok = false; resp?.Get("success", out ok); if (ok) resp?.Get("amount", out actual); onActual?.Invoke(actual); }, stableOperationId);
            Debug.Log("[江湖有灵] 太吾赠物→NPC " + taiwuId + "→" + npcId + " tpl=" + key.TemplateId + " x" + count);
        }

        /// <summary>任意两人以物换物。后端先校验双方当前真实持有,再执行双向转移;名称按实时清单解析。</summary>
        public static void ApplyBarter(int aId, int bId, string aItem, int aAmount, string bItem, int bAmount, Action<bool, int, int, string, string, string> onDone = null)
            => ApplyBarter(aId, bId, aItem, aAmount, bItem, bAmount, onDone, null);

        public static void ApplyBarter(int aId, int bId, string aItem, int aAmount, string bItem, int bAmount,
            Action<bool, int, int, string, string, string> onDone, string stableOperationId)
        {
            if (aId <= 0 || bId <= 0 || aId == bId) { onDone?.Invoke(false, 0, 0, null, null, "交换双方无效"); return; }
            string ai = (aItem ?? "").Trim(), bi = (bItem ?? "").Trim();
            if (ai.Length == 0 || bi.Length == 0) { onDone?.Invoke(false, 0, 0, null, null, "未指明双方要交换之物"); return; }
            if (aAmount < 1) aAmount = 1;
            if (bAmount < 1) bAmount = 1;
            CallGm("barter",
                d =>
                {
                    d.Set("a", aId); d.Set("b", bId);
                    d.Set("a_item", ai); d.Set("b_item", bi);
                    d.Set("a_amount", aAmount); d.Set("b_amount", bAmount);
                },
                resp =>
                {
                    bool ok = false; int aa = 0, ba = 0; string msg = null, an = null, bn = null;
                    resp?.Get("success", out ok);
                    if (ok) { resp?.Get("a_amount", out aa); resp?.Get("b_amount", out ba); resp?.Get("a_name", out an); resp?.Get("b_name", out bn); }
                    resp?.Get("message", out msg);
                    Debug.Log("[江湖有灵] 以物换物回执 ok=" + ok + " a_amount=" + aa + " b_amount=" + ba
                        + " a_name=" + (an ?? "") + " b_name=" + (bn ?? "") + " msg=" + (msg ?? ""));
                    onDone?.Invoke(ok, aa, ba, an, bn, msg);
                }, stableOperationId);
            Debug.Log("[江湖有灵] 以物换物 " + aId + "(" + ai + " x" + aAmount + ") ↔ " + bId + "(" + bi + " x" + bAmount + ")");
        }

        /// <summary>任意两人偷取真实持有物。后端按本体价值警觉度与三阶段检定；成功转移，失败会被发现并降低被偷者对偷者的好感。</summary>
        public static void ApplySteal(int thiefId, int victimId, string item, int amount, Action<bool, int, string, string, bool, int> onDone = null)
            => ApplySteal(thiefId, victimId, item, amount, onDone, null);

        public static void ApplySteal(int thiefId, int victimId, string item, int amount,
            Action<bool, int, string, string, bool, int> onDone, string stableOperationId)
        {
            if (thiefId <= 0 || victimId <= 0 || thiefId == victimId) { onDone?.Invoke(false, 0, null, "偷窃双方无效", false, 0); return; }
            string it = (item ?? "").Trim();
            if (it.Length == 0) { onDone?.Invoke(false, 0, null, "未指明要偷之物", false, 0); return; }
            if (amount < 1) amount = 1;
            CallGm("steal",
                d =>
                {
                    d.Set("thief", thiefId); d.Set("victim", victimId);
                    d.Set("item", it); d.Set("amount", amount);
                },
                resp =>
                {
                    bool ok = false, detected = false; int got = 0, chance = 0; string msg = null, realName = null;
                    resp?.Get("success", out ok);
                    resp?.Get("message", out msg);
                    resp?.Get("detected", out detected);
                    resp?.Get("chance", out chance);
                    if (ok) { resp?.Get("amount", out got); resp?.Get("name", out realName); }
                    Debug.Log("[江湖有灵] 偷窃回执 ok=" + ok + " thief=" + thiefId + " victim=" + victimId
                        + " item=" + (realName ?? it) + " amount=" + got + " detected=" + detected + " chance=" + chance + " msg=" + (msg ?? ""));
                    onDone?.Invoke(ok, got, realName, msg, detected, chance);
                }, stableOperationId);
            Debug.Log("[江湖有灵] 偷窃 " + thiefId + " <- " + victimId + " 「" + it + "」x" + amount);
        }

        /// <summary>太吾把自己会的一门武学(kind=combat)或技艺(kind=life)传给 NPC,NPC 造诣承太吾。后端校验太吾确通晓 + try/catch 已学。</summary>
        public static void ApplyTaiwuTeach(int taiwuId, int npcId, string kind, short templateId, Action<bool, string> onDone = null)
            => ApplyTaiwuTeach(taiwuId, npcId, kind, templateId, onDone, null);

        public static void ApplyTaiwuTeach(int taiwuId, int npcId, string kind, short templateId,
            Action<bool, string> onDone, string stableOperationId)
        {
            if (taiwuId <= 0 || npcId <= 0 || templateId < 0) { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("taiwu_teach",
                d => { d.Set("npc", npcId); d.Set("taiwu", taiwuId); d.Set("tpl", (int)templateId); d.Set("kind", kind ?? "combat"); },
                resp =>
                {
                    bool ok = false; string msg = null;
                    resp?.Get("success", out ok); resp?.Get("message", out msg);
                    onDone?.Invoke(ok, msg);
                }, stableOperationId);
            Debug.Log("[江湖有灵] 太吾传" + (kind == "life" ? "技艺" : "功") + "→NPC " + taiwuId + "→" + npcId + " tpl=" + templateId);
        }

        /// <summary>NPC 回忆一门它会的武学(kind=combat)/技艺(kind=life)秘籍——凭空造出完整可读的书并赠太吾。
        /// 后端权威校验:NPC 确通晓 + 该技确有秘籍 + 太吾尚无此书 + 走游戏自带 CreateSkillBook(pageIncompleteState=0)。
        /// onDone(成败, 书名 / 失败原因)。</summary>
        public static void ApplyWriteBook(int npcId, int taiwuId, string kind, short templateId, Action<bool, string, int> onDone, int toId = 0)
            => ApplyWriteBook(npcId, taiwuId, kind, templateId, onDone, toId, null);

        public static void ApplyWriteBook(int npcId, int taiwuId, string kind, short templateId,
            Action<bool, string, int> onDone, int toId, string stableOperationId)
        {
            if (npcId <= 0 || taiwuId <= 0 || templateId < 0) { onDone?.Invoke(false, "参数无效", 0); return; }
            CallGm("writebook",
                d => { d.Set("npc", npcId); d.Set("taiwu", taiwuId); d.Set("tpl", (int)templateId); d.Set("kind", kind ?? "combat"); if (toId > 0) d.Set("recipient", toId); },
                resp =>
                {
                    bool ok = false; string name = null, msg = null; int lost = 0;
                    resp?.Get("success", out ok); resp?.Get("name", out name); resp?.Get("message", out msg); resp?.Get("lost", out lost);
                    onDone?.Invoke(ok, ok ? (name ?? "秘籍") : (msg ?? "未能回忆成书"), lost);   // lost:缺页数,供 NPC 自知、对话体现
                }, stableOperationId);
            Debug.Log("[江湖有灵] NPC回忆" + (kind == "life" ? "技艺" : "武学") + "书 " + npcId + "→" + (toId > 0 ? toId : taiwuId) + " tpl=" + templateId);
        }

        /// <summary>下毒:actor(须持毒药)对 target 施毒并消耗一份毒药。actor 可为被说动的 NPC 或太吾本人。onDone(成败,失败原因)。</summary>
        public static void ApplyPoison(int actorId, int targetId, Action<bool, string> onDone)
            => ApplyPoison(actorId, targetId, onDone, null);

        public static void ApplyPoison(int actorId, int targetId, Action<bool, string> onDone, string stableOperationId)
            => ApplyPoisonCore(actorId, targetId, null, onDone, stableOperationId, false);

        public static void ApplyMonthlyPoison(int actorId, int targetId, Action<bool, string> onDone,
            string stableOperationId)
            => ApplyPoisonCore(actorId, targetId, null, onDone, stableOperationId, true);

        public static void ApplyMonthlyPoison(int actorId, int targetId, string poisonType,
            Action<bool, string> onDone, string stableOperationId)
            => ApplyPoisonCore(actorId, targetId, poisonType, onDone, stableOperationId, true);

        private static void ApplyPoisonCore(int actorId, int targetId, string poisonType,
            Action<bool, string> onDone, string stableOperationId, bool monthlyOnlyPurity)
        {
            if (actorId <= 0 || targetId <= 0 || actorId == targetId) { onDone?.Invoke(false, "对象无效"); return; }
            CallGm("poison", d =>
                {
                    d.Set("actor", actorId);
                    d.Set("target", targetId);
                    if (monthlyOnlyPurity) d.Set("monthly_only_purity", 1);
                    if (!string.IsNullOrWhiteSpace(poisonType)) d.Set("poison_type", poisonType.Trim());
                },
                resp =>
                {
                    bool ok = false; string msg = null, poisonName = null;
                    resp?.Get("success", out ok);
                    resp?.Get("message", out msg);
                    resp?.Get("poison_name", out poisonName);
                    onDone?.Invoke(ok, ok ? (poisonName ?? msg ?? "毒") : (msg ?? "未能下毒"));
                }, stableOperationId);
            Debug.Log("[江湖有灵] 下毒 " + actorId + "→" + targetId);
        }

        /// <summary>疗伤:healer 为 target 恢复气血并清创。target 默认太吾。onDone(成败,失败原因)。</summary>
        public static void ApplyHeal(int healerId, int targetId, Action<bool, string> onDone)
            => ApplyHeal(healerId, targetId, onDone, null);

        public static void ApplyHeal(int healerId, int targetId, Action<bool, string> onDone, string stableOperationId)
        {
            if (targetId <= 0) { onDone?.Invoke(false, "对象无效"); return; }
            CallGm("heal", d => { d.Set("healer", healerId); d.Set("target", targetId); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未能疗伤")); }, stableOperationId);
            Debug.Log("[江湖有灵] 疗伤 " + healerId + "→" + targetId);
        }

        /// <summary>NPC 为另一名在场人物驱毒；actor 固定为当前 NPC，不能让太吾代为执行。</summary>
        public static void ApplyDetox(int healerId, int targetId, Action<bool, string> onDone,
            string stableOperationId = null)
        {
            if (healerId <= 0 || targetId <= 0 || healerId == targetId)
            { onDone?.Invoke(false, "驱毒对象无效"); return; }
            CallGm("detox", d => { d.Set("healer", healerId); d.Set("target", targetId); },
                resp =>
                {
                    bool ok = false; string message = null;
                    resp?.Get("success", out ok); resp?.Get("message", out message);
                    onDone?.Invoke(ok, message ?? (ok ? "已驱毒" : "未能驱毒"));
                }, stableOperationId);
            Debug.Log("[江湖有灵] 驱毒 " + healerId + "→" + targetId);
        }

        /// <summary>NPC 为另一名在场人物调息，使用本体医术/毒术与内息疗愈公式。</summary>
        public static void ApplyRegulateBreath(int healerId, int targetId,
            Action<bool, string> onDone, string stableOperationId = null)
        {
            if (healerId <= 0 || targetId <= 0 || healerId == targetId)
            { onDone?.Invoke(false, "调息对象无效"); return; }
            CallGm("regulate_breath",
                d => { d.Set("healer", healerId); d.Set("target", targetId); },
                resp =>
                {
                    bool ok = false; string message = null;
                    resp?.Get("success", out ok); resp?.Get("message", out message);
                    onDone?.Invoke(ok, message ?? (ok ? "已调息" : "未能调息"));
                }, stableOperationId);
            Debug.Log("[江湖有灵] 调息 " + healerId + "→" + targetId);
        }

        /// <summary>太吾把自己知道的一桩秘闻讲给 NPC(NPC 收到该秘闻)。onDone(成败,失败原因)。</summary>
        public static void ApplyReceiveSecret(int npcId, SecretInformationId secretId, Action<bool, string> onDone = null)
            => ApplyReceiveSecret(npcId, secretId, onDone, null);

        public static void ApplyReceiveSecret(int npcId, SecretInformationId secretId, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (npcId <= 0) { onDone?.Invoke(false, "对象无效"); return; }
            CallGm("npcsecret", d => { d.Set("npc", npcId); d.Set("sid", (int)secretId); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未能告知")); }, stableOperationId);
            Debug.Log("[江湖有灵] 太吾告知秘闻→NPC " + npcId + " sid=" + (int)secretId);
        }

        /// <summary>月度事件:A 传 B 一门 A 会而 B 不会的武学(后端自动选)。onDone(成败,失败原因)。</summary>
        public static void ApplyEventTeach(int aId, int bId, Action<bool, string> onDone)
            => ApplyEventTeach(aId, bId, onDone, null);

        public static void ApplyEventTeach(int aId, int bId, Action<bool, string> onDone, string stableOperationId)
        {
            if (aId <= 0 || bId <= 0 || aId == bId) { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("event_teach", d => { d.Set("a", aId); d.Set("b", bId); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未传成")); }, stableOperationId);
        }

        /// <summary>月度事件:A 赠 B 一件 A 身上价值较高的物(后端自动选)。onDone(成败,失败原因)。</summary>
        public static void ApplyEventGift(int aId, int bId, Action<bool, string> onDone)
            => ApplyEventGift(aId, bId, onDone, null);

        public static void ApplyEventGift(int aId, int bId, Action<bool, string> onDone, string stableOperationId)
        {
            if (aId <= 0 || bId <= 0 || aId == bId) { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("event_gift", d => { d.Set("a", aId); d.Set("b", bId); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未赠成")); }, stableOperationId);
        }

        /// <summary>过月连载:太吾卷入本回故事、依其善恶给太吾名望月度增减(delta>0行善涨、<0作恶跌)。
        /// 经名望行为记录实时算分(天然临时、有上限、随时间回落)。onDone(成败)。</summary>
        public static void ApplyTaiwuFame(int taiwuId, int delta, Action<bool> onDone = null)
            => ApplyTaiwuFame(taiwuId, delta, onDone, null);

        public static void ApplyTaiwuFame(int taiwuId, int delta, Action<bool> onDone, string stableOperationId)
        {
            if (taiwuId <= 0 || delta == 0) { onDone?.Invoke(false); return; }
            CallGm("taiwu_fame", d => { d.Set("taiwu", taiwuId); d.Set("delta", delta); },
                resp => { bool ok = false; resp?.Get("success", out ok); onDone?.Invoke(ok); }, stableOperationId);
        }

        /// <summary>按本体名望行为记录改变任意有效人物（包括太吾）的江湖名望。</summary>
        public static void ApplyCharacterFame(int characterId, int delta,
            Action<bool, string> onDone, string stableOperationId = null)
        {
            if (characterId <= 0 || delta == 0 || delta % 3 != 0)
            {
                onDone?.Invoke(false, "人物或名望变化无效");
                return;
            }
            CallGm("character_fame", d =>
            {
                d.Set("character", characterId);
                d.Set("delta", delta);
            }, resp =>
            {
                bool ok = false;
                string message = null;
                resp?.Get("success", out ok);
                resp?.Get("message", out message);
                onDone?.Invoke(ok, message ?? (ok ? "名望已改变" : "名望没有改变"));
            }, stableOperationId);
        }

        /// <summary>NPC(a)主动与第三方 NPC(b)缔结关系:befriend 挚友 / sworn 结义 / mentor 师徒(a=师 b=徒)/
        /// lover 表达 a→b 的单向爱慕(仅当 b→a 已存在时才自然成为两情相悦)/spouse 夫妻。
        /// 后端复用与太吾同套原生原子操作 + 事后校验。onDone(成败, 权威结果或失败原因)。</summary>
        public static void ApplyRelateNpc(int aId, int bId, string action, Action<bool, string> onDone)
            => ApplyRelateNpc(aId, bId, action, onDone, null);

        public static void ApplyRelateNpc(int aId, int bId, string action, Action<bool, string> onDone, string stableOperationId)
        {
            if (aId <= 0 || bId <= 0 || aId == bId) { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("relate_npc",
                d => { d.Set("a", aId); d.Set("b", bId); d.Set("action", action ?? ""); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, msg ?? (ok ? "关系已建立" : "未能缔结")); }, stableOperationId);
            Debug.Log("[江湖有灵] NPC关系 " + aId + "→" + bId + " " + action);
        }

        /// <summary>对话说动 → 给 NPC 添加一个良性特性。后端权威校验(仅 Good 型 + 性别相符 + 未具备,按名匹配,自动去互斥)。
        /// onDone(成败, 实际授予的特性名 / 失败原因)。</summary>
        public static void ApplyAddFeature(int npcId, string featureName, Action<bool, string> onDone)
            => ApplyAddFeature(npcId, featureName, onDone, null);

        public static void ApplyAddFeature(int npcId, string featureName, Action<bool, string> onDone, string stableOperationId)
        {
            if (npcId <= 0 || string.IsNullOrWhiteSpace(featureName)) { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("addfeature",
                d => { d.Set("npc", npcId); d.Set("name", featureName.Trim()); },
                resp =>
                {
                    bool ok = false; string name = null, msg = null;
                    resp?.Get("success", out ok); resp?.Get("name", out name); resp?.Get("message", out msg);
                    onDone?.Invoke(ok, ok ? (name ?? featureName) : (msg ?? "未能添加"));
                }, stableOperationId);
            Debug.Log("[江湖有灵] 添加特性 npc=" + npcId + " name=" + featureName);
        }

        /// <summary>对话改变"商队累计好感"(按商人品类共享,0-100)。后端从 NPC 反查 merchantType,单次 ±100 封顶。
        /// onDone(成败, 变更后好感值 / 失败原因)。</summary>
        public static void ApplyMerchantFavor(int npcId, int delta, Action<bool, string> onDone)
            => ApplyMerchantFavor(npcId, delta, onDone, null);

        public static void ApplyMerchantFavor(int npcId, int delta, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (npcId <= 0 || delta == 0) { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("merchantfavor",
                d => { d.Set("npc", npcId); d.Set("delta", delta); },
                resp =>
                {
                    bool ok = false; int after = 0; string msg = null;
                    resp?.Get("success", out ok); resp?.Get("after", out after); resp?.Get("message", out msg);
                    onDone?.Invoke(ok, ok ? after.ToString() : (msg ?? "未能改变"));
                }, stableOperationId);
            Debug.Log("[江湖有灵] 商队好感 npc=" + npcId + " delta=" + delta);
        }

        /// <summary>只读:查江湖可前往地名。keyword 空=列一批主要地名;非空=搜含该词的地名(与 goto_place 同源 Config.MapArea,查到的名可原样填 place)。剔除"过去的…/梦中"等时空副本。不发 RPC。</summary>
        public static void QueryPlaces(string keyword, Action<List<string>> onResult)
        {
            var names = new List<string>();
            try
            {
                string q = (keyword ?? "").Trim();
                string qc = q.TrimEnd('城', '县', '州', '镇', '村', '山', '的', '那', '里', '地'); if (qc.Length == 0) qc = q;
                foreach (var a in Config.MapArea.Instance)
                {
                    string an = a?.Name;
                    if (string.IsNullOrEmpty(an)) continue;
                    if (an.Contains("过去") || an.Contains("曾经") || an.Contains("往昔") || an.Contains("梦中")) continue;   // 时空副本,不可前往
                    if (q.Length == 0) { if (!names.Contains(an)) names.Add(an); if (names.Count >= 40) break; }   // 列一批主要地名
                    else if ((an.Contains(q) || an.Contains(qc) || (qc.Length >= 2 && qc.Contains(an))) && !names.Contains(an)) names.Add(an);
                }
            }
            catch { }
            onResult?.Invoke(names);
        }

        /// <summary>把地点名解析成 (areaId, 中心格 blockId, 真实地名)。枚举 Config.MapArea:精确名优先,其次互含(名长≥2)。
        /// 返回 false=认不出该地。供"前往某地"前端即时校验(认不出立刻回话,不发 RPC)。</summary>
        public static bool ResolveAreaId(string word, out short areaId, out short blockId, out string name)
        {
            areaId = -1; blockId = 0; name = null;
            if (string.IsNullOrWhiteSpace(word)) return false;
            string raw = word.Trim();
            string q = raw.TrimEnd('城', '县', '州', '镇', '村', '山', '的', '那', '里', '地');   // 容忍"大理城/武当山"等口语后缀
            if (q.Length == 0) q = raw;
            try
            {
                Config.MapAreaItem exact = null, best = null; int bestScore = int.MinValue;
                foreach (var a in Config.MapArea.Instance)
                {
                    string an = a?.Name;
                    if (string.IsNullOrEmpty(an)) continue;
                    if (an == raw || an == q) { exact = a; break; }   // 精确名最优(如"太吾村"原样命中)
                    // 跳过"过去的…/曾经的/梦中"等时空副本——它们不是可前往的现世地点,选中会无效("去过去的太吾村"那个 bug)
                    if (an.Contains("过去") || an.Contains("曾经") || an.Contains("往昔") || an.Contains("梦中")) continue;
                    if (q.Length >= 2 && (an.Contains(q) || q.Contains(an)))
                    {
                        // 打分挑最贴切:正好"词+城/村/山…"最优,其次前缀匹配,名字越短越贴切
                        int score = -an.Length;
                        if (an == q + "村" || an == q + "城" || an == q + "山" || an == q + "镇" || an == q + "县" || an == q + "州" || an == q + "堡") score += 300;
                        if (an.StartsWith(q, StringComparison.Ordinal)) score += 100;
                        if (score > bestScore) { bestScore = score; best = a; }
                    }
                }
                var pick = exact ?? best;
                if (pick == null) return false;
                // MapArea.TemplateId 是配置模板命名空间；Location.AreaId / AddGoal / MapDomain 使用的是运行时区域 id。
                // 1.0.56 权威实现由 WorldMapModel.GetAreaIdByAreaTemplateId 完成二者映射，不能把模板 id 直接塞进 Location。
                var wm = SingletonObject.getInstance<WorldMapModel>();
                areaId = wm == null ? (short)-1 : wm.GetAreaIdByAreaTemplateId(pick.TemplateId);   // JHYL_RESOLVE_PLACE_RUNTIME_AREA_ID
                blockId = pick.CenterBlock; name = pick.Name;
                // 有些区(如大明山)CenterBlock=-1(未定中心格)→ 退到【主城/据点核心块】SettlementBlockCore[0](真正的门派/主城所在),
                // 免前端传 -1、后端兜底到 0 号块把 NPC 送到该区的错地方。再兜底到已开发块。
                if (blockId < 0 && pick.SettlementBlockCore != null && pick.SettlementBlockCore.Length > 0)
                    blockId = pick.SettlementBlockCore[0];   // short[]:该区主城/据点核心块,取首块=真正的门派/主城所在
                return areaId >= 0;
            }
            catch { return false; }
        }

        /// <summary>安排普通行程。固定地点用 NpcTravelTarget(Location)，寻人用
        /// NpcTravelTarget(charId)，不会制造太吾赴约登记。</summary>
        public static void ApplyGotoPlace(int npcId, short areaId, short blockId, Action<bool, string> onDone, bool home = false, bool followTaiwu = false, int destCharId = 0)
            => ApplyGotoPlaceCore(npcId, areaId, blockId, onDone, home, followTaiwu, destCharId, null);

        /// <summary>供持久队列重放：stableOperationId 必须沿用首次派发的 32hex id。</summary>
        public static void ApplyGotoPlace(int npcId, short areaId, short blockId, Action<bool, string> onDone,
            bool home, bool followTaiwu, int destCharId, string stableOperationId)
            => ApplyGotoPlaceCore(npcId, areaId, blockId, onDone, home, followTaiwu, destCharId, stableOperationId);

        private static void ApplyGotoPlaceCore(int npcId, short areaId, short blockId, Action<bool, string> onDone,
            bool home, bool followTaiwu, int destCharId, string stableOperationId)
        {
            if (npcId <= 0 || (!home && !followTaiwu && destCharId <= 0 && areaId < 0)) { onDone?.Invoke(false, "目的地无效"); return; }
            if (TryAbortWorldRpc(RpcConst.AddGoalMethod, onDone)) return;
            var p = new SerializableModData();
            p.Set("npc_id", npcId);
            p.Set("max_duration_months", 6);
            if (followTaiwu)
            {
                p.Set("travel_mode", "char");
                p.Set("target_mode", "taiwu");
                p.Set("target_char_id", 0);
            }
            else if (destCharId > 0)
            {
                p.Set("travel_mode", "char");
                p.Set("target_mode", "char");
                p.Set("target_char_id", destCharId);
            }
            else
            {
                p.Set("travel_mode", "fixed");
                p.Set("target_mode", "char");
                p.Set("target_char_id", 0);
            }
            if (home) p.Set("home", 1);
            p.Set("area_id", (int)areaId);
            p.Set("block_id", (int)blockId);
            bool dispatched = OperationRpcClient.Call(RpcConst.AddGoalMethod, p,
                resp =>
                {
                    PublishOperationOutcome(resp, stableOperationId);
                    try
                    {
                        bool ok = false; string msg = null;
                        resp?.Get("success", out ok); resp?.Get("message", out msg);
                        msg = OutcomeMessage(resp, msg);
                        onDone?.Invoke(ok, msg ?? (ok ? "行程已安排" : "未能前往"));
                    }
                    catch (Exception e) { onDone?.Invoke(false, "结果解析失败:" + e.GetType().Name); }
                }, stableOperationId, requireStructuredMutationReceipt: true);
            if (dispatched) MarkOperationDispatched(stableOperationId);
        }

        /// <summary>仅用于“与太吾在固定地点赴约”。本体 254 需要太吾预约登记与固定坐标成对写入。</summary>
        public static void ApplyAppointmentWithTaiwu(int npcId, short areaId, short blockId,
            Action<bool, string> onDone, bool home = false, string stableOperationId = null)
        {
            if (npcId <= 0 || (!home && areaId < 0)) { onDone?.Invoke(false, "约定地点无效"); return; }
            if (TryAbortWorldRpc(RpcConst.AddGoalMethod, onDone)) return;
            var p = new SerializableModData();
            p.Set("npc_id", npcId);
            p.Set("template_id", 254);
            p.Set("target_mode", "taiwu");
            p.Set("target_char_id", 0);
            if (home) p.Set("home", 1);
            p.Set("area_id", (int)areaId);
            p.Set("block_id", (int)blockId);
            bool dispatched = OperationRpcClient.Call(RpcConst.AddGoalMethod, p,
                resp =>
                {
                    PublishOperationOutcome(resp, stableOperationId);
                    try
                    {
                        bool ok = false; string msg = null;
                        resp?.Get("success", out ok); resp?.Get("message", out msg);
                        msg = OutcomeMessage(resp, msg);
                        onDone?.Invoke(ok, msg ?? (ok ? "约定已登记" : "未能立约"));
                    }
                    catch (Exception e) { onDone?.Invoke(false, "结果解析失败:" + e.GetType().Name); }
                }, stableOperationId, requireStructuredMutationReceipt: true);
            if (dispatched) MarkOperationDispatched(stableOperationId);
        }

        /// <summary>NPC 换上指定的一件装备(ItemKey 由对话层从其随身物品按名选定;槽位按物品类型自动归位)。前端直调。</summary>
        public static void ApplyEquipItem(int npcId, ItemKey key)
            => ApplyEquipItem(npcId, key, null);

        public static void ApplyEquipItem(int npcId, ItemKey key, string stableOperationId)
        {
            if (npcId <= 0) return;
            // 走后端 Gm 通道:槽位由后端按真实子类型定 + IsItemMeetSlot 校验,杜绝错槽损坏装备(C1)
            CallGm("equip", d => { d.Set("npc", npcId);
                d.Set("it", (int)key.ItemType); d.Set("mod", (int)key.ModificationState); d.Set("t", (int)key.TemplateId); d.Set("id", key.Id); }, null, stableOperationId);
            Debug.Log("[江湖有灵] NPC " + npcId + " 换上 tpl=" + key.TemplateId);
        }

        /// <summary>全实时换装:只传物名,后端按 NPC【当前】持有实时解析(装备随穿随脱,不依赖前端缓存)+ 定槽校验。
        /// onDone 回传(是否换上, 失败原因)。</summary>
        public static void ApplyEquipByName(int npcId, string name, Action<bool, string> onDone = null)
            => ApplyEquipByName(npcId, name, onDone, null);

        public static void ApplyEquipByName(int npcId, string name, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (npcId <= 0) { onDone?.Invoke(false, "角色无效"); return; }
            string nm = (name ?? "").Trim();
            CallGm("equip", d => { d.Set("npc", npcId); d.Set("name", nm); },
                resp =>
                {
                    bool ok = false; string msg = null;
                    resp?.Get("success", out ok); if (!ok) resp?.Get("message", out msg);
                    onDone?.Invoke(ok, msg);
                }, stableOperationId);
            Debug.Log("[江湖有灵] NPC " + npcId + " 换上(按名) " + nm);
        }

        /// <summary>NPC 卸下某类当前装备到背包(weapon/clothing/armor/accessory/carrier)。前端直调。</summary>
        public static void ApplyEquipTakeOff(int npcId, string part, Action<bool, string> onDone = null)
            => ApplyEquipTakeOff(npcId, part, onDone, null);

        public static void ApplyEquipTakeOff(int npcId, string part, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (npcId <= 0) { onDone?.Invoke(false, "角色无效"); return; }
            // 走后端 Gm 通道:护具展开全部 4 槽(修 H3:不再只卸躯干),并经 try/catch
            CallGm("takeoff", d => { d.Set("npc", npcId); d.Set("part", part ?? "weapon"); },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未能卸下")); }, stableOperationId);
            Debug.Log("[江湖有灵] NPC " + npcId + " 卸下 " + part);
        }

        /// <summary>
        /// NPC 自己使用真实背包中的食物、药毒、茶酒或本体允许服用的特殊物品。
        /// 后端实时解析名称并以库存精确变化确认一次执行。
        /// </summary>
        public static void ApplyNpcUseItem(int npcId, string item,
            Action<bool, string, string, int, int, int, string> onDone,
            string stableOperationId = null)
        {
            string requested = (item ?? string.Empty).Trim();
            if (npcId <= 0 || requested.Length == 0)
            {
                onDone?.Invoke(false, requested, null, 0, 0, 0, "参数无效");
                return;
            }
            CallGm("use_item", d => { d.Set("char", npcId); d.Set("item", requested); }, resp =>
            {
                bool ok = false;
                string message = null, name = null, kind = null;
                int amount = 0, before = 0, after = 0;
                resp?.Get("success", out ok); resp?.Get("message", out message);
                resp?.Get("name", out name); resp?.Get("kind", out kind);
                resp?.Get("amount", out amount); resp?.Get("before", out before);
                resp?.Get("after", out after);
                onDone?.Invoke(ok, name ?? requested, kind, amount, before, after,
                    ok ? null : (message ?? "物品未能使用"));
            }, stableOperationId);
        }

        /// <summary>把某角色已会武学的当前激活突破页正逆练颠倒。改的是角色已学功法状态,不是秘籍书页。</summary>
        public static void ApplyFlipPractice(int charId, string skill, Action<bool, string, string, string, int, string> onDone = null)
            => ApplyFlipPractice(charId, skill, onDone, null);

        public static void ApplyFlipPractice(int charId, string skill,
            Action<bool, string, string, string, int, string> onDone, string stableOperationId)
        {
            string sk = (skill ?? "").Trim();
            if (charId <= 0 || sk.Length == 0) { onDone?.Invoke(false, sk, null, null, 0, "参数无效"); return; }
            CallGm("flip_practice",
                d => { d.Set("char", charId); d.Set("skill", sk); },
                resp =>
                {
                    bool ok = false; string msg = null, name = null, before = null, after = null; int flipped = 0;
                    resp?.Get("success", out ok);
                    resp?.Get("message", out msg);
                    resp?.Get("name", out name);
                    resp?.Get("before", out before);
                    resp?.Get("after", out after);
                    resp?.Get("flipped", out flipped);
                    Debug.Log("[江湖有灵] 正逆练颠倒 char=" + charId + " skill=" + (name ?? sk) + " ok=" + ok
                        + " " + (before ?? "") + "→" + (after ?? "") + " flipped=" + flipped + " msg=" + (msg ?? ""));
                    onDone?.Invoke(ok, name ?? sk, before, after, flipped, msg);
                }, stableOperationId);
        }

        /// <summary>
        /// NPC 专用修炼：把一门已会武学的研读页补全，并完成全部可激活页的突破。
        /// 后端再次拒绝太吾，前端不能绕过。
        /// </summary>
        public static void ApplyNpcTrainSkill(int charId, string skill,
            Action<bool, string, int, int, string, string> onDone,
            string stableOperationId = null)
        {
            string requested = (skill ?? "").Trim();
            if (charId <= 0 || requested.Length == 0)
            {
                onDone?.Invoke(false, requested, 0, 0, null, "参数无效");
                return;
            }
            CallGm("npc_train_skill",
                d => { d.Set("char", charId); d.Set("skill", requested); },
                resp =>
                {
                    bool ok = false;
                    string message = null, name = null, direction = null;
                    int before = 0, after = 0;
                    resp?.Get("success", out ok);
                    resp?.Get("message", out message);
                    resp?.Get("name", out name);
                    resp?.Get("read_pages_before", out before);
                    resp?.Get("read_pages_after", out after);
                    resp?.Get("direction", out direction);
                    onDone?.Invoke(ok, name ?? requested, before, after,
                        direction, ok ? null : (message ?? "修炼未完成"));
                }, stableOperationId);
        }

        /// <summary>
        /// NPC 专用读书：把 NPC 背包中指定的一本武学/技艺书直接读完。
        /// </summary>
        public static void ApplyNpcReadBook(int charId, string book,
            Action<bool, string, string, int, int, string> onDone,
            string stableOperationId = null)
        {
            string requested = (book ?? "").Trim();
            if (charId <= 0 || requested.Length == 0)
            {
                onDone?.Invoke(false, requested, null, 0, 0, "参数无效");
                return;
            }
            CallGm("npc_read_book",
                d => { d.Set("char", charId); d.Set("book", requested); },
                resp =>
                {
                    bool ok = false;
                    string message = null, name = null, kind = null;
                    int before = 0, after = 0;
                    resp?.Get("success", out ok);
                    resp?.Get("message", out message);
                    resp?.Get("name", out name);
                    resp?.Get("kind", out kind);
                    resp?.Get("pages_before", out before);
                    resp?.Get("pages_after", out after);
                    onDone?.Invoke(ok, name ?? requested, kind, before, after,
                        ok ? null : (message ?? "阅读未完成"));
                }, stableOperationId);
        }

        /// <summary>
        /// 只读查询某角色真正可颠倒正逆练的武学。后端按本体 SetActivePage 的同一条件检查：
        /// 已突破、存在当前激活常页，且该页相反方向已经读过。读取失败与“确认没有”分开返回。
        /// </summary>
        public static void QueryFlippablePracticeSkills(int charId,
            Action<bool, HashSet<short>, string> onDone)
        {
            if (charId <= 0)
            {
                onDone?.Invoke(false, null, "角色无效");
                return;
            }
            CallGm("query_flip_practice", d => d.Set("char", charId), resp =>
            {
                bool ok = false;
                string ids = null, message = null;
                resp?.Get("success", out ok);
                resp?.Get("ids", out ids);
                resp?.Get("message", out message);
                if (!ok)
                {
                    onDone?.Invoke(false, null, message ?? "正逆练资格读取失败");
                    return;
                }
                var parsed = new HashSet<short>();
                foreach (string raw in (ids ?? string.Empty).Split(','))
                    if (short.TryParse((raw ?? string.Empty).Trim(), out short id)) parsed.Add(id);
                onDone?.Invoke(true, parsed, null);
            });
        }

        /// <summary>
        /// 只读查询 NPC 尚未完整研读/突破的已会武学，以及背包里尚未读完的普通秘籍或技艺书。
        /// 这是 train_skill/read_book 的内置候选查询；读取失败不能伪装成“没有候选”。
        /// </summary>
        public static void QueryNpcStudyProgress(int charId,
            Action<bool, HashSet<short>, List<string>, string> onDone)
        {
            if (charId <= 0)
            {
                onDone?.Invoke(false, null, null, "角色无效");
                return;
            }
            CallGm("query_npc_study_progress", d => d.Set("char", charId), resp =>
            {
                bool ok = false;
                string ids = null, names = null, message = null;
                resp?.Get("success", out ok);
                resp?.Get("skill_ids", out ids);
                resp?.Get("book_names", out names);
                resp?.Get("message", out message);
                if (!ok)
                {
                    onDone?.Invoke(false, null, null, message ?? "修炼与阅读进度读取失败");
                    return;
                }
                var parsedSkills = new HashSet<short>();
                foreach (string raw in (ids ?? string.Empty).Split(','))
                    if (short.TryParse((raw ?? string.Empty).Trim(), out short id))
                        parsedSkills.Add(id);
                var parsedBooks = new List<string>();
                foreach (string raw in (names ?? string.Empty).Split((char)1))
                    if (!string.IsNullOrWhiteSpace(raw)) parsedBooks.Add(raw.Trim());
                onDone?.Invoke(true, parsedSkills, parsedBooks, null);
            });
        }

        /// <summary>春宵一刻:太吾与 NPC 共度(原生 MakeLove;同性亦可,是否有孕交由本体判定)。后端硬校验成年+恋人/夫妻之情或足够情意,异步回调(ok,msg)。</summary>
        public static void ApplySpendNight(int taiwuId, int npcId, Action<bool, string> onDone)
            => ApplySpendNight(taiwuId, npcId, onDone, null);

        public static void ApplySpendNight(int taiwuId, int npcId, Action<bool, string> onDone,
            string stableOperationId)
            => ApplySpendNightBetween(npcId, taiwuId, taiwuId, onDone, stableOperationId);

        /// <summary>
        /// 任意两名角色共度春宵。taiwuId 只绑定存档/operation 身份，不再被误作实际对象；
        /// actorId/targetId 才是游戏动作的真实端点。单聊旧入口仍由上面的包装保持兼容。
        /// </summary>
        public static void ApplySpendNightBetween(int actorId, int targetId, int taiwuId,
            Action<bool, string> onDone, string stableOperationId = null)
        {
            if (actorId <= 0 || targetId <= 0 || taiwuId <= 0 || actorId == targetId)
            { onDone?.Invoke(false, "参数无效"); return; }
            CallGm("spend_night", d =>
                {
                    d.Set("npc", actorId);
                    d.Set("target", targetId);
                    d.Set("taiwu", taiwuId);
                },
                resp => { bool ok = false; string msg = null; resp?.Get("success", out ok); resp?.Get("message", out msg); onDone?.Invoke(ok, ok ? null : (msg ?? "未能成事")); }, stableOperationId);
        }

        /// <summary>查 NPC 的关系网(父母/子女/兄弟/结义/配偶/心上人/师父/挚友/仇敌等),返回多行文本(空=无)。供 query_npc_relationships。</summary>
        public static void QueryNpcRelations(int npcId, Action<string> onResult)
            => QueryNpcRelations(npcId, (ok, relations) => onResult?.Invoke(ok ? relations : null));

        /// <summary>带权威成功位的关系网读取；月度查询不得把 RPC 失败折叠成“无关系”。</summary>
        public static void QueryNpcRelations(int npcId, Action<bool, string> onResult)
            => QueryNpcRelationsWithHeart(npcId, (ok, relations, adored) => onResult?.Invoke(ok, relations));

        /// <summary>权威关系网读取，同时把角色自己指向他人的 Adored 集合单列为“心之所系”。</summary>
        public static void QueryNpcRelationsWithHeart(int npcId, Action<bool, string, string> onResult)
        {
            CallGm("npc_relations", d => d.Set("npc", npcId),
                resp =>
                {
                    string rels = "", adored = ""; bool ok = false;
                    resp?.Get("success", out ok);
                    if (ok)
                    {
                        resp?.Get("relations", out rels);
                        resp?.Get("adored", out adored);
                    }
                    onResult?.Invoke(ok, ok ? (rels ?? "") : null, ok ? (adored ?? "") : null);
                });
        }

        /// <summary>月度行动的一次性权威前置读取：角色存活、成年、两两关系与发起者好感。</summary>
        public static void QueryMonthlyActionPreflight(int actorId, int targetId,
            Action<MonthlyActionPreflight> onResult)
        {
            if (actorId <= 0 || targetId <= 0 || actorId == targetId) { onResult?.Invoke(null); return; }
            CallGm("monthly_action_preflight", d => { d.Set("actor", actorId); d.Set("target", targetId); },
                resp =>
                {
                    bool ok = false;
                    resp?.Get("success", out ok);
                    if (!ok) { onResult?.Invoke(null); return; }
                    var value = new MonthlyActionPreflight();
                    resp.Get("actor_alive", out value.ActorAlive);
                    resp.Get("target_alive", out value.TargetAlive);
                    resp.Get("actor_adult", out value.ActorAdult);
                    resp.Get("target_adult", out value.TargetAdult);
                    resp.Get("actor_infected", out value.ActorInfected);
                    resp.Get("target_infected", out value.TargetInfected);
                    resp.Get("actor_consummate", out value.ActorConsummate);
                    resp.Get("target_consummate", out value.TargetConsummate);
                    resp.Get("actor_area", out value.ActorArea);
                    resp.Get("actor_block", out value.ActorBlock);
                    resp.Get("target_area", out value.TargetArea);
                    resp.Get("target_block", out value.TargetBlock);
                    resp.Get("same_valid_location", out value.SameValidLocation);
                    resp.Get("strong_enough", out value.StrongEnough);
                    resp.Get("target_kidnapped", out value.TargetKidnapped);
                    resp.Get("target_health", out value.TargetHealth);
                    resp.Get("target_left_max_health", out value.TargetLeftMaxHealth);
                    resp.Get("target_injury_marks", out value.TargetInjuryMarks);
                    resp.Get("target_needs_healing", out value.TargetNeedsHealing);
                    resp.Get("target_is_taiwu", out value.TargetIsTaiwu);
                    resp.Get("actor_has_rope", out value.ActorHasRope);
                    resp.Get("actor_has_poison", out value.ActorHasPoison);
                    resp.Get("target_poison_immune", out value.TargetPoisonImmune);
                    resp.Get("can_marry", out value.CanMarry);
                    resp.Get("actor_can_travel", out value.ActorCanTravel);
                    resp.Get("actor_travel_code", out value.ActorTravelCode);
                    resp.Get("actor_travel_reason", out value.ActorTravelReason);
                    resp.Get("enemy", out value.Enemy);
                    resp.Get("spouse", out value.Spouse);
                    resp.Get("sworn", out value.Sworn);
                    resp.Get("friend", out value.Friend);
                    resp.Get("adored", out value.Adored);
                    resp.Get("actor_adores_target", out value.ActorAdoresTarget);
                    resp.Get("target_adores_actor", out value.TargetAdoresActor);
                    resp.Get("mentor", out value.Mentor);
                    resp.Get("adoptive_parent", out value.AdoptiveParent);
                    resp.Get("adoptive_child", out value.AdoptiveChild);
                    resp.Get("can_adoptive_parent", out value.CanAdoptiveParent);
                    resp.Get("can_adoptive_child", out value.CanAdoptiveChild);
                    resp.Get("adoptive_parent_reason", out value.AdoptiveParentReason);
                    resp.Get("adoptive_child_reason", out value.AdoptiveChildReason);
                    resp.Get("actor_favor", out value.ActorFavor);
                    resp.Get("relation", out value.RelationText);
                    onResult?.Invoke(value);
                });
        }

        /// <summary>只读疗伤前置；支持为自己疗伤，未知态不会折叠为健康。</summary>
        public static void QueryHealPreflight(int targetId, Action<HealPreflight> onResult)
        {
            if (targetId <= 0) { onResult?.Invoke(null); return; }
            CallGm("heal_preflight", d => d.Set("target", targetId), resp =>
            {
                bool ok = false;
                resp?.Get("success", out ok);
                if (!ok) { onResult?.Invoke(null); return; }
                var value = new HealPreflight();
                resp.Get("target_alive", out value.TargetAlive);
                resp.Get("target_health", out value.TargetHealth);
                resp.Get("target_left_max_health", out value.TargetLeftMaxHealth);
                resp.Get("target_injury_marks", out value.TargetInjuryMarks);
                resp.Get("target_needs_healing", out value.TargetNeedsHealing);
                onResult?.Invoke(value);
            });
        }

        /// <summary>只读当前 NPC 自身的气血、伤势、内息紊乱与六类中毒。</summary>
        public static void QueryNpcHealthStatus(int actorId, Action<NpcHealthStatus> onResult)
        {
            if (actorId <= 0) { onResult?.Invoke(null); return; }
            CallGm("health_status", d => d.Set("actor", actorId), resp =>
            {
                bool ok = false; resp?.Get("success", out ok);
                if (!ok) { onResult?.Invoke(null); return; }
                var value = new NpcHealthStatus();
                resp.Get("health", out value.Health);
                resp.Get("left_max_health", out value.LeftMaxHealth);
                resp.Get("injury_marks", out value.InjuryMarks);
                resp.Get("qi_disorder", out value.QiDisorder);
                resp.Get("qi_disorder_show", out value.QiDisorderShow);
                resp.Get("qi_disorder_level", out value.QiDisorderLevel);
                resp.Get("poison_summary", out value.PoisonSummary);
                string csv = null; resp.Get("poisons", out csv);
                if (!string.IsNullOrWhiteSpace(csv))
                {
                    string[] parts = csv.Split(',');
                    for (int i = 0; i < value.Poisons.Length && i < parts.Length; i++)
                        int.TryParse(parts[i], out value.Poisons[i]);
                }
                onResult?.Invoke(value);
            });
        }

        /// <summary>只读:某 NPC 显著关系网中的真实角色 id。供过月同道列出可千里传音的远方联系人。</summary>
        public static void QueryNpcRelationIds(int npcId, Action<List<int>> onResult)
            => QueryNpcRelationIdsWithContext(npcId,
                (ok, ids, relations) => onResult?.Invoke(ids ?? new List<int>()));

        /// <summary>一次只读 RPC 同时取得远方联系人和不含单向爱慕边的显著关系网。</summary>
        public static void QueryNpcRelationIdsWithContext(int npcId,
            Action<bool, List<int>, string> onResult)
            => QueryNpcRelationIdsWithBehaviorContext(npcId,
                (ok, ids, relations, motiveFacts) => onResult?.Invoke(ok, ids, relations));

        /// <summary>关系网外再返回结构化敌对边：enemy:id / enemy_of:id。</summary>
        public static void QueryNpcRelationIdsWithBehaviorContext(int npcId,
            Action<bool, List<int>, string, string> onResult)
        {
            if (npcId <= 0) { onResult?.Invoke(false, new List<int>(), null, null); return; }
            CallGm("npc_relation_ids", d => d.Set("npc", npcId),
                resp =>
                {
                    var ids = new List<int>(); string csv = null, relations = null, motiveFacts = null; bool ok = false;
                    resp?.Get("success", out ok);
                    if (ok)
                    {
                        resp?.Get("ids", out csv);
                        resp?.Get("relations", out relations);
                        resp?.Get("motive_facts", out motiveFacts);
                    }
                    if (!string.IsNullOrWhiteSpace(csv))
                        foreach (string part in csv.Split(','))
                            if (int.TryParse(part.Trim(), out int id) && id > 0 && !ids.Contains(id)) ids.Add(id);
                    int taiwuId = 0;
                    try { taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId; } catch { }
                    onResult?.Invoke(ok, CharacterProxyIdentityService.NormalizeKnownIds(taiwuId, ids),
                        ok ? (relations ?? "") : null,
                        ok ? (motiveFacts ?? "") : null);
                });
        }

        /// <summary>查主线进度(相枢之劫,公知)+ NPC 所属门派(orgTemplateId 1-15)的门派主线状态。
        /// onResult(mainProgress, sectStatus):main=-1 失败;sectStatus -1=无门派主线 / 0未启 / 1进行中 / 2善果了结 / 3恶果了结。</summary>
        public static void QueryStoryStatus(int orgTemplateId, Action<int, int> onResult)
        {
            CallGm("story_status", d => d.Set("org", orgTemplateId),
                resp => { int main = -1, sect = -1; bool ok = false; resp?.Get("success", out ok); if (ok) { resp?.Get("main", out main); resp?.Get("sect_status", out sect); } onResult?.Invoke(main, sect); });
        }

        /// <summary>NPC 对太吾戒备升降。需后端权限,异步回调。</summary>
        public static void ApplyChangeAlertness(int npcId, int delta, Action<bool, string> onDone)
            => ApplyChangeAlertness(npcId, delta, onDone, null);

        public static void ApplyChangeAlertness(int npcId, int delta, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (npcId <= 0 || delta == 0) { onDone?.Invoke(false, "参数无效"); return; }
            CallBoolRpc(RpcConst.ChangeAlertnessMethod, d => { d.Set("npc_id", npcId); d.Set("delta", delta); }, onDone, stableOperationId);
        }

        /// <summary>NPC 放走所掳之人(target)。需后端权限,异步回调。</summary>
        public static void ApplyRelease(int npcId, int targetId, Action<bool, string> onDone)
            => ApplyRelease(npcId, targetId, onDone, null);

        public static void ApplyRelease(int npcId, int targetId, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (npcId <= 0 || targetId <= 0) { onDone?.Invoke(false, "参数无效"); return; }
            CallBoolRpc(RpcConst.ReleaseMethod, d => { d.Set("npc_id", npcId); d.Set("target_id", targetId); }, onDone, stableOperationId);
        }

        /// <summary>杀人:NPC 取目标性命。后端硬校验精纯>=目标(相等可成) + try/catch,异步回调(ok,msg)。</summary>
        public static void ApplyKill(int npcId, int targetId, Action<bool, string> onDone)
            => ApplyKill(npcId, targetId, onDone, null);

        public static void ApplyKill(int npcId, int targetId, Action<bool, string> onDone, string stableOperationId)
            => ApplyKillCore(npcId, targetId, onDone, stableOperationId, false);

        public static void ApplyMonthlyKill(int npcId, int targetId, Action<bool, string> onDone,
            string stableOperationId)
            => ApplyKillCore(npcId, targetId, onDone, stableOperationId, true);

        public static void ApplyKillDetailed(int npcId, int targetId,
            Action<bool, string, string> onDone, string stableOperationId = null)
            => ApplyKillCoreDetailed(npcId, targetId, onDone, stableOperationId, false);

        public static void ApplyMonthlyKillDetailed(int npcId, int targetId,
            Action<bool, string, string> onDone, string stableOperationId)
            => ApplyKillCoreDetailed(npcId, targetId, onDone, stableOperationId, true);

        private static void ApplyKillCore(int npcId, int targetId, Action<bool, string> onDone,
            string stableOperationId, bool monthlyOnlyPurity)
        {
            if (npcId <= 0 || targetId <= 0 || npcId == targetId) { onDone?.Invoke(false, "参数无效"); return; }
            CallBoolRpc(RpcConst.KillMethod, d =>
                {
                    d.Set("npc_id", npcId);
                    d.Set("target_id", targetId);
                    if (monthlyOnlyPurity) d.Set("monthly_only_purity", 1);
                }, onDone, stableOperationId);
        }

        private static void ApplyKillCoreDetailed(int npcId, int targetId,
            Action<bool, string, string> onDone, string stableOperationId, bool monthlyOnlyPurity)
        {
            if (npcId <= 0 || targetId <= 0 || npcId == targetId)
            { onDone?.Invoke(false, "参数无效", null); return; }
            CallBoolRpcDetailed(RpcConst.KillMethod, d =>
                {
                    d.Set("npc_id", npcId);
                    d.Set("target_id", targetId);
                    if (monthlyOnlyPurity) d.Set("monthly_only_purity", 1);
                }, (ok, msg, response) =>
                {
                    string lootName = null;
                    response?.Get("loot_name", out lootName);
                    onDone?.Invoke(ok, msg, string.IsNullOrWhiteSpace(lootName) ? null : lootName.Trim());
                }, stableOperationId);
        }

        /// <summary>绑人/擒拿:NPC 将目标掳为俘虏。后端硬校验精纯>=目标(相等可成) + 背包有绳子 + try/catch,异步回调(ok,msg)。</summary>
        public static void ApplyCapture(int npcId, int targetId, Action<bool, string> onDone)
            => ApplyCapture(npcId, targetId, onDone, null);

        public static void ApplyCapture(int npcId, int targetId, Action<bool, string> onDone, string stableOperationId)
            => ApplyCaptureCore(npcId, targetId, onDone, stableOperationId, false);

        public static void ApplyMonthlyCapture(int npcId, int targetId, Action<bool, string> onDone,
            string stableOperationId)
            => ApplyCaptureCore(npcId, targetId, onDone, stableOperationId, true);

        private static void ApplyCaptureCore(int npcId, int targetId, Action<bool, string> onDone,
            string stableOperationId, bool monthlyOnlyPurity)
        {
            if (npcId <= 0 || targetId <= 0 || npcId == targetId) { onDone?.Invoke(false, "参数无效"); return; }
            CallBoolRpc(RpcConst.CaptureMethod, d =>
                {
                    d.Set("npc_id", npcId);
                    d.Set("target_id", targetId);
                    if (monthlyOnlyPurity) d.Set("monthly_only_purity", 1);
                }, onDone, stableOperationId);
        }

        /// <summary>
        /// 带存档级 operation ledger 的对话战斗入口。调用方对一次确认必须持久复用
        /// stableOperationId；回包超时后查询同一 id，绝不能另发一个新的战斗操作。
        /// config: 0=切磋、1=相搏、2=生死斗。
        /// </summary>
        public static void StartCombat(int targetId, int config, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (targetId <= 0 || config < 0 || config > 2)
            {
                onDone?.Invoke(false, "战斗目标或配置无效");
                return;
            }
            CallBoolRpc(RpcConst.StartCombatMethod,
                d => { d.Set("target_id", targetId); d.Set("config", config); },
                onDone, stableOperationId);
        }

        /// <summary>令 NPC 认可太吾(归心)。需后端权限(EventHelper),异步回调。</summary>
        public static void ApplyRecognizeTaiwu(int npcId, Action<bool, string> onDone)
            => ApplyRecognizeTaiwu(npcId, onDone, null);

        public static void ApplyRecognizeTaiwu(int npcId, Action<bool, string> onDone, string stableOperationId)
        {
            if (npcId <= 0) { onDone?.Invoke(false, "参数无效"); return; }
            CallBoolRpc(RpcConst.RecognizeMethod, d => d.Set("npc_id", npcId), onDone, stableOperationId);
        }

        /// <summary>过月立场漂移:改 NPC 道德值。需后端权限,异步回调。</summary>
        public static void ApplyChangeMorality(int npcId, int delta, Action<bool, string> onDone)
            => ApplyChangeMorality(npcId, delta, onDone, null);

        /// <summary>供持久过月队列重放：同一意图重试时传回原 stableOperationId。</summary>
        public static void ApplyChangeMorality(int npcId, int delta, Action<bool, string> onDone, string stableOperationId)
        {
            if (npcId <= 0 || delta == 0) { onDone?.Invoke(false, "参数无效"); return; }
            CallBoolRpc(RpcConst.ChangeMoralityMethod, d => { d.Set("npc_id", npcId); d.Set("delta", delta); }, onDone, stableOperationId);
        }

        /// <summary>在说话者关系网里把具名第三方解析成 charId(-1=未解析)。后端只读,异步回调。</summary>
        public static void ResolveChar(int speakerId, string targetText, Action<int> onResolved)
            => ResolveChar(speakerId, targetText, false, (cid, reason) => onResolved?.Invoke(cid));

        /// <summary>解析具名第三方,回 (charId, reason)。allowBlock=true 时放开「同块陌生人」兜底，
        /// 供所有须当面且可作用于现场第三方的动作使用，不因关系好恶隐藏在场者。
        /// reason:found / not_found / ambiguous(近旁同名数人) / resolve_error(读关系异常,勿断言不认识)。</summary>
        public static void ResolveChar(int speakerId, string targetText, bool allowBlock, Action<int, string> onResolved)
            => ResolveChar(speakerId, targetText, allowBlock, false, onResolved);

        /// <summary>解析具名第三方。allowGlobal 只接受全局唯一严格全名；执行层仍须独立校验目标是否在场。</summary>
        public static void ResolveChar(int speakerId, string targetText, bool allowBlock, bool allowGlobal,
            Action<int, string> onResolved)
        {
            if (speakerId <= 0) { onResolved?.Invoke(-1, "not_found"); return; }
            string modId = Plugin.Instance?.ModIdStr;
            if (string.IsNullOrWhiteSpace(modId)) modId = RpcConst.FallbackModId;
            if (IsWorldRpcBlocked())
            {
                try { onResolved?.Invoke(-1, "not_found"); } catch { }
                Debug.LogWarning("[江湖有灵] RPC " + RpcConst.ResolveCharMethod + " 已取消:当前不在存档世界");
                return;
            }
            var p = new SerializableModData();
            p.Set("speaker_id", speakerId);
            p.Set("target_text", targetText ?? "");
            p.Set("allow_block", allowBlock ? 1 : 0);
            p.Set("allow_global", allowGlobal ? 1 : 0);
            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                null, modId, RpcConst.ResolveCharMethod, p,
                delegate (int offset, RawDataPool pool)
                {
                    try
                    {
                        SerializableModData resp = null;
                        Serializer.Deserialize(pool, offset, ref resp);
                        int cid = -1; resp?.Get("char_id", out cid);
                        string reason = ""; resp?.Get("reason", out reason);
                        onResolved?.Invoke(cid, string.IsNullOrEmpty(reason) ? (cid > 0 ? "found" : "not_found") : reason);
                    }
                    catch { onResolved?.Invoke(-1, "not_found"); }
                });
        }

        /// <summary>
        /// Read-only authoritative hostile-relation check.  known=false is deliberately distinct
        /// from hostile=false so the final irreversible-action gate can fail closed on RPC/read
        /// failure instead of treating missing evidence as a harmless relationship.
        /// </summary>
        public static void QueryHostility(int speakerId, int targetId, Action<bool, bool> onResult)
        {
            if (speakerId <= 0 || targetId <= 0 || speakerId == targetId)
            { onResult?.Invoke(false, false); return; }
            string modId = Plugin.Instance?.ModIdStr;
            if (string.IsNullOrWhiteSpace(modId)) modId = RpcConst.FallbackModId;
            if (IsWorldRpcBlocked())
            {
                try { onResult?.Invoke(false, false); } catch { }
                Debug.LogWarning("[江湖有灵] RPC " + RpcConst.ResolveCharMethod + " 敌对关系查询已取消:当前不在存档世界");
                return;
            }
            var p = new SerializableModData();
            p.Set("speaker_id", speakerId);
            p.Set("target_id", targetId);
            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                null, modId, RpcConst.ResolveCharMethod, p,
                delegate (int offset, RawDataPool pool)
                {
                    try
                    {
                        SerializableModData resp = null;
                        Serializer.Deserialize(pool, offset, ref resp);
                        bool known = false, hostile = false;
                        resp?.Get("hostility_known", out known);
                        resp?.Get("hostile", out hostile);
                        onResult?.Invoke(known, known && hostile);
                    }
                    catch { onResult?.Invoke(false, false); }
                });
        }

        /// <summary>从后端 Character 权威状态读取太吾完整已学武学/技艺，不依赖前端菜单显示切片。</summary>
        public static void QueryTaiwuLearnedSkills(int taiwuId,
            Action<List<short>, List<short>, string> onResult)
        {
            if (taiwuId <= 0)
            { onResult?.Invoke(null, null, "无效太吾"); return; }
            string modId = Plugin.Instance?.ModIdStr;
            if (string.IsNullOrWhiteSpace(modId)) modId = RpcConst.FallbackModId;
            if (IsWorldRpcBlocked())
            { onResult?.Invoke(null, null, "已离开存档"); return; }
            var p = new SerializableModData();
            p.Set("taiwu_id", taiwuId);
            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                null, modId, RpcConst.QueryTaiwuSkillsMethod, p,
                delegate (int offset, RawDataPool pool)
                {
                    try
                    {
                        SerializableModData resp = null;
                        Serializer.Deserialize(pool, offset, ref resp);
                        bool ok = false; string combatRaw = null, lifeRaw = null, message = null;
                        resp?.Get("success", out ok);
                        resp?.Get("combat_ids", out combatRaw);
                        resp?.Get("life_ids", out lifeRaw);
                        resp?.Get("message", out message);
                        if (!ok) { onResult?.Invoke(null, null, message ?? "读取失败"); return; }
                        List<short> ParseIds(string raw)
                        {
                            var ids = new List<short>();
                            if (string.IsNullOrWhiteSpace(raw)) return ids;
                            foreach (string part in raw.Split(','))
                                if (short.TryParse(part, out short id) && !ids.Contains(id)) ids.Add(id);
                            return ids;
                        }
                        onResult?.Invoke(ParseIds(combatRaw), ParseIds(lifeRaw), null);
                    }
                    catch (Exception e)
                    { onResult?.Invoke(null, null, "读取异常:" + e.GetType().Name); }
                });
        }

        // 通用:调一个真正改写游戏态的副作用 RPC(返回 success/message)。JHYL_CALLBOOLRPC_MUTATION_ONLY:
        // 此助手只服务副作用动作,一律要求结构化 v2 回执;只读查询(如 IsTeammate)必须直连后端读接口,不得走此路。
        private static void CallBoolRpc(string method, Action<SerializableModData> fill, Action<bool, string> onDone,
            string stableOperationId = null)
            => CallBoolRpcDetailed(method, fill,
                (ok, message, response) => onDone?.Invoke(ok, message), stableOperationId);

        private static void CallBoolRpcDetailed(string method, Action<SerializableModData> fill,
            Action<bool, string, SerializableModData> onDone, string stableOperationId = null)
        {
            if (IsWorldRpcBlocked())
            {
                var canceled = MakeWorldExitResponse();
                PublishOperationOutcome(canceled, stableOperationId);
                try { onDone?.Invoke(false, "已离开存档,取消后台动作", canceled); } catch { }
                Debug.LogWarning("[江湖有灵] RPC " + method + " 已取消:当前不在存档世界");
                return;
            }

            var p = new SerializableModData();
            fill?.Invoke(p);
            bool dispatched = OperationRpcClient.Call(method, p,
                resp =>
                {
                    PublishOperationOutcome(resp, stableOperationId);
                    try
                    {
                        bool ok = false; string msg = null;
                        resp?.Get("success", out ok);
                        resp?.Get("message", out msg);
                        msg = OutcomeMessage(resp, msg);
                        onDone?.Invoke(ok, msg ?? (ok ? "已完成" : "未完成"), resp);
                    }
                    catch (Exception e)
                    { onDone?.Invoke(false, "结果解析失败:" + e.GetType().Name, resp); }
                }, stableOperationId,
                requireStructuredMutationReceipt: true);
            if (dispatched) MarkOperationDispatched(stableOperationId);
        }

        /// <summary>兼容旧调用：只回传本体死者表中确认死亡的人物，不把剧情退场/临时移除者算成死者。</summary>
        public static void QueryDeadCharacters(string idsCsv, Action<string> onDead)
            => QueryDeadCharacters(idsCsv, (ok, dead) => onDead?.Invoke(ok ? dead : ""));

        /// <summary>
        /// 过月权威活性核验。ok=false 表示读取失败，调用者不得把失败误当作“所有角色仍有效”。
        /// </summary>
        public static void QueryDeadCharacters(string idsCsv, Action<bool, string> onDone)
            => QueryCharacterArchiveStates(idsCsv,
                (ok, alive, dead) => onDone?.Invoke(ok, ok ? dead : ""));

        /// <summary>
        /// 过月连载名册只关心“当前不能继续作为活动演员”的人物，因此同时返回真死者与
        /// 剧情阶段退场/临时失效人物。此结果只能用于当期事件名册，严禁据此删除人物档案。
        /// </summary>
        public static void QueryUnavailableCharacters(string idsCsv, Action<bool, string> onDone)
            => QueryCharacterArchiveStatesDetailed(idsCsv,
                (ok, alive, dead, unavailable) =>
                {
                    if (!ok) { onDone?.Invoke(false, ""); return; }
                    if (string.IsNullOrWhiteSpace(dead)) onDone?.Invoke(true, unavailable ?? "");
                    else if (string.IsNullOrWhiteSpace(unavailable)) onDone?.Invoke(true, dead);
                    else onDone?.Invoke(true, dead + "," + unavailable);
                });

        /// <summary>
        /// 权威人物档案状态核验。alive/dead 都是逗号分隔 id；既不在活人表也不在死者表的
        /// 剧情退场或失效人物不会混入任一集合，调用者必须保留其既有资料与状态。
        /// </summary>
        public static void QueryCharacterArchiveStates(string idsCsv,
            Action<bool, string, string> onDone)
            => QueryCharacterArchiveStatesDetailed(idsCsv,
                (ok, alive, dead, unavailable) => onDone?.Invoke(ok, alive, dead));

        private static void QueryCharacterArchiveStatesDetailed(string idsCsv,
            Action<bool, string, string, string> onDone)
        {
            if (string.IsNullOrWhiteSpace(idsCsv)) { onDone?.Invoke(true, "", "", ""); return; }

            string modId = Plugin.Instance?.ModIdStr;
            if (string.IsNullOrWhiteSpace(modId)) modId = RpcConst.FallbackModId;
            if (IsWorldRpcBlocked())
            {
                try { onDone?.Invoke(false, "", "", ""); } catch { }
                Debug.LogWarning("[江湖有灵] RPC " + RpcConst.FilterDeadMethod + " 已取消:当前不在存档世界");
                return;
            }

            var p = new SerializableModData();
            p.Set("ids", idsCsv);

            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                null, modId, RpcConst.FilterDeadMethod, p,
                delegate (int offset, RawDataPool pool)
                {
                    try
                    {
                        SerializableModData resp = null;
                        Serializer.Deserialize(pool, offset, ref resp);
                        bool success = false;
                        string alive = null;
                        string dead = null;
                        string unavailable = null;
                        resp?.Get("success", out success);
                        resp?.Get("alive", out alive);
                        resp?.Get("dead", out dead);
                        resp?.Get("unavailable", out unavailable);
                        onDone?.Invoke(success, success ? (alive ?? "") : "",
                            success ? (dead ?? "") : "",
                            success ? (unavailable ?? "") : "");
                    }
                    catch { onDone?.Invoke(false, "", "", ""); }
                });
        }

        /// <summary>过月给 NPC 注入行动目标 goal。targetMode="taiwu" 让后端取实时太吾为目标。必须后端 RPC,异步回调。</summary>
        public static void ApplyAddGoal(int npcId, int templateId, string targetMode, int targetCharId, Action<bool, string> onDone)
            => ApplyAddGoalCore(npcId, templateId, targetMode, targetCharId, onDone, null);

        /// <summary>供持久过月队列重放：重试必须传回原 stableOperationId。</summary>
        public static void ApplyAddGoal(int npcId, int templateId, string targetMode, int targetCharId,
            Action<bool, string> onDone, string stableOperationId)
            => ApplyAddGoalCore(npcId, templateId, targetMode, targetCharId, onDone, stableOperationId);

        private static void ApplyAddGoalCore(int npcId, int templateId, string targetMode, int targetCharId,
            Action<bool, string> onDone, string stableOperationId)
        {
            if (npcId <= 0 || templateId <= 0)
            { onDone?.Invoke(false, "目标请求不完整"); return; }

            if (TryAbortWorldRpc(RpcConst.AddGoalMethod, onDone)) return;

            var p = new SerializableModData();
            p.Set("npc_id", npcId);
            p.Set("template_id", templateId);
            p.Set("target_mode", targetMode ?? "taiwu");
            p.Set("target_char_id", targetCharId);

            bool dispatched = OperationRpcClient.Call(RpcConst.AddGoalMethod, p,
                resp =>
                {
                    PublishOperationOutcome(resp, stableOperationId);
                    try
                    {
                        bool ok = false; string msg = null;
                        resp?.Get("success", out ok);
                        resp?.Get("message", out msg);
                        msg = OutcomeMessage(resp, msg);
                        onDone?.Invoke(ok, msg ?? (ok ? "已完成" : "未完成"));
                    }
                    catch (Exception e) { onDone?.Invoke(false, "结果解析失败:" + e.GetType().Name); }
                }, stableOperationId, requireStructuredMutationReceipt: true);
            if (dispatched) MarkOperationDispatched(stableOperationId);
        }

        /// <summary>建立关系(action: best_friend / sworn_sibling / mentor)。必须后端 RPC,异步回调。</summary>
        public static void ApplyRelation(int npcId, int taiwuId, string action, Action<bool, string> onDone)
            => ApplyRelationCore(npcId, taiwuId, action, onDone, null);

        public static void ApplyRelation(int npcId, int taiwuId, string action, Action<bool, string> onDone,
            string stableOperationId)
            => ApplyRelationCore(npcId, taiwuId, action, onDone, stableOperationId);

        private static void ApplyRelationCore(int npcId, int taiwuId, string action, Action<bool, string> onDone,
            string stableOperationId)
        {
            if (npcId <= 0 || taiwuId <= 0 || npcId == taiwuId || string.IsNullOrEmpty(action))
            { onDone?.Invoke(false, "关系请求不完整"); return; }

            if (TryAbortWorldRpc(RpcConst.ExecuteRelationMethod, onDone)) return;

            var p = new SerializableModData();
            p.Set("npc_id", npcId);
            p.Set("taiwu_id", taiwuId);
            p.Set("relation_action", action);

            bool dispatched = OperationRpcClient.Call(RpcConst.ExecuteRelationMethod, p,
                resp =>
                {
                    PublishOperationOutcome(resp, stableOperationId);
                    try
                    {
                        bool ok = false; string msg = null;
                        resp?.Get("success", out ok);
                        resp?.Get("message", out msg);
                        msg = OutcomeMessage(resp, msg);
                        onDone?.Invoke(ok, msg ?? (ok ? "已完成" : "未完成"));
                    }
                    catch (Exception e) { onDone?.Invoke(false, "结果解析失败:" + e.GetType().Name); }
                }, stableOperationId, requireStructuredMutationReceipt: true);
            if (dispatched) MarkOperationDispatched(stableOperationId);
        }
    }
}
