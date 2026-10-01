using System;
using System.Collections.Generic;
using JianghuYouling.Shared;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// 群聊成员可并行思考，但真实副作用按导演顺序提交；同时对同一生命/关系/太吾库存资源
    /// 做冲突占用。它不判断业务合法性，后端仍须在提交瞬间重新校验真实状态。
    /// </summary>
    public sealed class GroupActionCoordinator
    {
        private sealed class ClaimOwner
        {
            public int MemberId;
            public string DisplayName;
        }

        private readonly object _gate = new object();
        private readonly HashSet<int> _completedOrders = new HashSet<int>();
        private readonly Dictionary<string, ClaimOwner> _claims = new Dictionary<string, ClaimOwner>(StringComparer.OrdinalIgnoreCase);

        public bool CanDispatch(int order)
        {
            lock (_gate)
            {
                for (int i = 0; i < order; i++) if (!_completedOrders.Contains(i)) return false;
                return true;
            }
        }

        public void CompleteMember(int order)
        {
            lock (_gate) _completedOrders.Add(order);
        }

        public bool TryClaim(int memberId, string memberName, string toolName, string argsJson, out string conflict)
        {
            var keys = ClaimKeys(memberId, toolName, argsJson);
            return TryClaimKeys(memberId, memberName, keys, out conflict);
        }

        /// <summary>
        /// 人物名由游戏权威解析成 charId 后的二次仲裁。原始 JSON claim 只是一道廉价预筛，
        /// 不能承担正确性：别名、称谓、A→B/B→A 都必须在实际 mutation 前落到 canonical footprint。
        /// </summary>
        public bool TryClaimResolved(int memberId, string memberName, GroupActionFootprint footprint, out string conflict)
        {
            return TryClaimKeys(memberId, memberName, footprint?.Keys, out conflict);
        }

        /// <summary>
        /// 只有权威回执已经证明动作未发生时才释放本次占用，让后位成员可依据失败原因改用
        /// 其它有效方案。UNKNOWN 不得调用此方法：副作用可能已经发生，必须继续保留冲突锁。
        /// </summary>
        public void ReleaseResolved(int memberId, GroupActionFootprint footprint)
        {
            if (footprint?.Keys == null) return;
            lock (_gate)
            {
                foreach (string key in footprint.Keys)
                    if (!string.IsNullOrWhiteSpace(key)
                        && _claims.TryGetValue(key, out ClaimOwner owner)
                        && owner.MemberId == memberId)
                        _claims.Remove(key);
            }
        }

        private bool TryClaimKeys(int memberId, string memberName, IEnumerable<string> keys, out string conflict)
        {
            conflict = null;
            if (keys == null) return true;
            var normalizedKeys = new List<string>();
            foreach (string key in keys)
                if (!string.IsNullOrWhiteSpace(key) && !normalizedKeys.Contains(key)) normalizedKeys.Add(key);
            if (normalizedKeys.Count == 0) return true;
            string ownerName = string.IsNullOrWhiteSpace(memberName) ? ("成员" + memberId) : memberName.Trim();
            lock (_gate)
            {
                foreach (string key in normalizedKeys)
                    if (_claims.TryGetValue(key, out ClaimOwner prior) && prior.MemberId != memberId)
                    {
                        conflict = "资源冲突：" + prior.DisplayName + " 已先处理同一目标（" + key + "）";
                        return false;
                    }
                foreach (string key in normalizedKeys)
                    _claims[key] = new ClaimOwner { MemberId = memberId, DisplayName = ownerName };
                return true;
            }
        }

        private static List<string> ClaimKeys(int memberId, string toolName, string argsJson)
        {
            var keys = new List<string>();
            string name = toolName ?? "";
            JObject args = null;
            try { args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson); }
            catch { args = new JObject(); }

            if (name == "kill" || name == "capture" || name == "poison" || name == "heal"
                || name == "detox" || name == "regulate_breath")
            {
                keys.Add("生命:" + Target(args, "target", "to", "person"));
            }
            else if (name == "start_combat")
            {
                // 游戏同时只能进入一场战斗；同轮多名群成员各自发起会导致第二个请求
                // 在场景切换竞态中得到不确定回执，故整轮只允许首个已仲裁的发起者。
                keys.Add("战斗:全局");
            }
            else if (name == "set_relation" || name == "dissolve_relation" || name == "relate_npc"
                || name == "set_enmity" || name == "matchmake")
            {
                keys.Add("关系:" + memberId + ":" + Target(args, "target", "partner", "to", "person"));
            }
            else if (name == "taiwu_give_item" || name == "taiwu_teach" || name == "taiwu_write_book")
            {
                string resource = Target(args, "name", "item", "skill", "type");
                keys.Add("太吾资源:" + resource);
            }
            return keys;
        }

        private static string Target(JObject args, params string[] names)
        {
            foreach (string name in names)
            {
                string value = args?[name]?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return Normalize(value);
            }
            return "太吾";
        }

        private static string Normalize(string value)
            => (value ?? "").Trim().ToLowerInvariant().Replace(" ", "");
    }

    /// <summary>
    /// 已经由游戏权威解析后的动作资源集合。只使用 charId 与规范物名，避免模型原始称谓参与锁键。
    /// 一个动作可同时声明多项资源（例如 barter 的两方库存）。
    /// </summary>
    public sealed class GroupActionFootprint
    {
        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal IEnumerable<string> Keys => _keys;

        public GroupActionFootprint AddLife(int targetCharId)
        {
            if (targetCharId > 0) _keys.Add("生命:char:" + targetCharId);
            return this;
        }

        public GroupActionFootprint AddRelation(int firstCharId, int secondCharId)
        {
            if (firstCharId <= 0 || secondCharId <= 0 || firstCharId == secondCharId) return this;
            int low = Math.Min(firstCharId, secondCharId);
            int high = Math.Max(firstCharId, secondCharId);
            _keys.Add("关系:pair:" + low + ":" + high);
            return this;
        }

        public GroupActionFootprint AddInventory(int ownerCharId, string itemName)
        {
            if (ownerCharId <= 0) return this;
            string item = ItemNameMatcher.Normalize(itemName ?? "");
            if (string.IsNullOrWhiteSpace(item)) item = "*";
            _keys.Add("库存:owner:" + ownerCharId + ":item:" + item);
            return this;
        }

        public GroupActionFootprint AddCombat()
        {
            _keys.Add("战斗:全局");
            return this;
        }

        public GroupActionFootprint AddCharacterState(int ownerCharId, string scope)
        {
            if (ownerCharId <= 0) return this;
            string normalized = ItemNameMatcher.Normalize(scope ?? "");
            if (string.IsNullOrWhiteSpace(normalized)) normalized = "*";
            _keys.Add("人物状态:owner:" + ownerCharId + ":scope:" + normalized);
            return this;
        }
    }

    /// <summary>
    /// 普通群聊物理动作的纯策略门。ParticipantIds 只证明“被邀请进本次群”。调用方传入的
    /// authoritativePresenceIds 是“权威当前同块 ∪ 权威当前同道”：同道按同行语义视作与太吾
    /// 同地，普通频道成员则不能借邀请身份伪装在场。除太吾外，每个动作端点必须同时属于
    /// 本次 roster 与该权威 presence 集合。
    /// </summary>
    public static class GroupPhysicalPresencePolicy
    {
        public static bool Allows(int taiwuId, IEnumerable<int> actionCharacterIds,
            ISet<int> participantIds, ISet<int> authoritativePresenceIds, out int rejectedCharId, out string reason)
        {
            rejectedCharId = 0;
            reason = null;
            if (taiwuId <= 0 || actionCharacterIds == null || participantIds == null || authoritativePresenceIds == null)
            {
                reason = "presence_context_missing";
                return false;
            }

            foreach (int charId in actionCharacterIds)
            {
                if (charId <= 0)
                {
                    rejectedCharId = charId;
                    reason = "invalid_character";
                    return false;
                }
                if (charId == taiwuId) continue;
                if (!participantIds.Contains(charId))
                {
                    rejectedCharId = charId;
                    reason = "not_group_participant";
                    return false;
                }
                if (!authoritativePresenceIds.Contains(charId))
                {
                    rejectedCharId = charId;
                    reason = "not_same_block_or_companion";
                    return false;
                }
            }
            return true;
        }
    }
}
