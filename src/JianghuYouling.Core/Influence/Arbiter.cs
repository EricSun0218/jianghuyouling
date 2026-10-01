using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Influence
{
    /// <summary>裁决用到的 NPC 即时游戏态(从 NpcSnapshot 取)。</summary>
    public sealed class NpcGateState
    {
        public int Favor { get; set; }            // 当前好感(npc→taiwu)
        public ushort RelationFlag { get; set; }  // 当前关系位
        public bool IsDead { get; set; }
    }

    /// <summary>裁决后落地的效果(交前端 EffectHandler 执行)。</summary>
    public sealed class ResolvedEffect
    {
        public int FavorDelta { get; set; }        // 即时好感变化(已封顶)
        public int HappinessDelta { get; set; }    // 心情(Happiness)变化(已封顶),正=变好/负=变差;心情持续低落由游戏自带联动加速相枢传染
        public string RelationAction { get; set; } // null 或 best_friend/sworn_sibling/mentor
        public bool RecognizeTaiwu { get; set; }   // 是否令 NPC 认可太吾(归心)
        public bool ShareSecret { get; set; }      // NPC 是否吐露一条秘闻给太吾
        public int MoralityShift { get; set; }     // 过月立场漂移道德值 delta(已封顶)
        public MemoryDraft Memory { get; set; }    // null 或要写入的记忆
        public BehaviorDraft Behavior { get; set; } // null 或过月行为意图(设计§9)
        public int GiveSilverToTaiwu { get; set; }  // NPC 解囊/借予太吾的银钱(>0,已封顶)
        public string GiveItemName { get; set; }    // 非空:NPC 赠太吾的那件物品名(在其可赠清单内)
        public int GiveItemCount { get; set; } = 1;  // 赠送数量(已封顶;后端再按 NPC 实有封顶)
        public string FeudTarget { get; set; }      // 非空:NPC 与此人结仇("太吾"或具名第三方)
        public string ReconcileTarget { get; set; } // 非空:NPC 放下对此人的旧怨
        public int FollowDecision { get; set; }     // 1 追随太吾 / -1 离去 / 0 无
        public string DiscloseSecretTo { get; set; } // 非空:NPC 把秘闻透露给此第三方
        public int SecretIndex { get; set; }         // 1基:选第几条秘闻吐露/散播(0=回退)
        public int AlertnessShift { get; set; }      // NPC 对太吾戒心 delta(已封顶)
        public string TeachSkillName { get; set; }   // 非空:NPC 传授的那门武学名(在其所习清单内)
        public string ReleaseTarget { get; set; }    // 非空:NPC 放走此人
        public string FavorTarget { get; set; }      // 非空:NPC 对此第三方好感变动目标
        public int FavorTargetDelta { get; set; }    // 配合 FavorTarget(已封顶)
        public string EquipPutOnItem { get; set; }   // NPC 换上的那件物品名(空=不换上)
        public string EquipTakeOff { get; set; }     // weapon/clothing/armor/accessory/carrier(空=不卸下)
        public bool SectSupport { get; set; }        // 该 NPC 在其门派为太吾表态支持(后端校验门派身份/关系)
        public string MatchmakeTarget { get; set; }  // 非空:该 NPC 与此对象成婚(太吾保媒;后端校验异性/成年/可婚)
        public List<string> Notes { get; } = new List<string>();
    }

    public sealed class ArbiterConfig
    {
        // 门槛整体调低:玩家用本 mod 就是为了造成影响,诚意之言应较快见效(仍由画像依据门防谄媚跑偏)
        public double FavorCoefficient { get; set; } = 80;   // satisfaction → favor 系数(30→80)
        public int FavorCapPerTurn { get; set; } = 4000;     // 每轮好感封顶(1500→4000)
        // 心情(双向,封顶):Happiness 为 sbyte、HappinessType 每 30 一档,故单轮封顶取约一档以内
        public double MoodCoefficient { get; set; } = 0.25;   // mood(-100..100) → 心情 delta(100→±25)
        public int MoodCapPerTurn { get; set; } = 25;         // 单轮心情变动封顶(约一档 HappinessType 以内)
        public int MoralityShiftCapPerTurn { get; set; } = 60; // 单轮立场漂移道德值封顶(25→60,过月漂移更明显)
        public int SilverGiftCapPerTurn { get; set; } = 100000; // 单轮 NPC 赠/借银钱封顶
        public int AlertnessCapPerTurn { get; set; } = ConversationReactionPolicy.OrdinaryConversationAlertnessCap;
            // 普通对话单轮戒备封顶；重大断交/离异必须走游戏原生关系事件，不能由模型数值冒充
        public int ThirdPartyFavorCapPerTurn { get; set; } = 4000; // 单轮对第三方好感封顶
    }

    /// <summary>
    /// Arbiter:把 LLM(扮演 NPC)的"真实反应意图"忠实翻译成引擎认的效果。
    /// 设计原则(消除"嘴上答应、代码拦住"):**模型是"NPC 答没答应"的唯一裁判**;
    /// 资格/可说服度判断已前移到提示词(把当前好感、关系、够不够格结义夫妻等作为上下文喂给模型,让它自洽决定),
    /// 故此处**不再用 reasoning 缺失或好感阈值事后否决**,只做两件事:
    /// ① 数值类封顶(好感/心情/立场/戒备/第三方好感),防越界;
    /// ② 引擎合法性保护(已具备该关系→无谓重复,跳过,非失败)。
    /// "确实做不到"(物品不在身上/学不了/非门派/不可婚)由效果落地层如实回成败,不在此静默吞。纯逻辑,可独立单测。
    /// </summary>
    public sealed class Arbiter
    {
        private readonly ArbiterConfig _cfg;
        public Arbiter(ArbiterConfig cfg = null) { _cfg = cfg ?? new ArbiterConfig(); }

        public ResolvedEffect Resolve(InfluenceIntent intent, NpcGateState state)
        {
            var r = new ResolvedEffect();
            if (intent == null || state == null) { r.Notes.Add("空意图/状态"); return r; }
            if (state.IsDead) { r.Notes.Add("NPC 已逝,无效果"); return r; }

            // —— 即时好感:satisfaction × 系数,封顶(仅幅度有界,不否决) ——
            int sat = Clamp(intent.Satisfaction, -100, 100);
            r.FavorDelta = Clamp((int)Math.Round(sat * _cfg.FavorCoefficient), -_cfg.FavorCapPerTurn, _cfg.FavorCapPerTurn);

            // —— 关系:忠实执行模型的决定(够不够格已在提示词里让模型自行权衡);
            //    仅挡"已具备该关系"的无谓重复(引擎合法性,非失败、非"说了没做") ——
            string prop = (intent.RelationProposal ?? "none").Trim().ToLowerInvariant();
            if (prop != "none" && prop.Length > 0 && !AlreadyHasRelation(prop, state.RelationFlag, r))
                r.RelationAction = prop;

            // —— 心情(双向,封顶) ——
            int mood = Clamp(intent.Mood, -100, 100);
            if (mood != 0)
                r.HappinessDelta = Clamp((int)Math.Round(mood * _cfg.MoodCoefficient), -_cfg.MoodCapPerTurn, _cfg.MoodCapPerTurn);

            // —— 以下一律忠实执行模型决定;数值类封顶,非数值类原样采纳,均不再事后否决 ——
            if (intent.RecognizeTaiwu) r.RecognizeTaiwu = true;
            if (intent.ShareSecret) r.ShareSecret = true;
            if (intent.MoralityShift != 0)
                r.MoralityShift = Clamp(intent.MoralityShift, -_cfg.MoralityShiftCapPerTurn, _cfg.MoralityShiftCapPerTurn);

            if (intent.Memory != null && !string.IsNullOrWhiteSpace(intent.Memory.Content))
                r.Memory = intent.Memory;
            if (intent.Behavior != null && !string.IsNullOrWhiteSpace(intent.Behavior.Kind))
                r.Behavior = intent.Behavior;

            if (intent.GiveSilverToTaiwu > 0)
                r.GiveSilverToTaiwu = Clamp(intent.GiveSilverToTaiwu, 1, _cfg.SilverGiftCapPerTurn);
            if (!string.IsNullOrWhiteSpace(intent.GiveItemName))
            {
                r.GiveItemName = intent.GiveItemName.Trim();
                r.GiveItemCount = Clamp(intent.GiveItemCount <= 0 ? 1 : intent.GiveItemCount, 1, 99);
            }
            if (!string.IsNullOrWhiteSpace(intent.FeudTarget)) r.FeudTarget = intent.FeudTarget.Trim();
            if (!string.IsNullOrWhiteSpace(intent.ReconcileTarget)) r.ReconcileTarget = intent.ReconcileTarget.Trim();
            if (intent.FollowDecision != 0) r.FollowDecision = intent.FollowDecision > 0 ? 1 : -1;

            if (!string.IsNullOrWhiteSpace(intent.DiscloseSecretTo)) r.DiscloseSecretTo = intent.DiscloseSecretTo.Trim();
            r.SecretIndex = intent.SecretIndex;   // 选第几条秘闻吐露/散播(选择器;具体由 share_secret/disclose 驱动)
            if (intent.AlertnessShift != 0)
                r.AlertnessShift = Clamp(intent.AlertnessShift, -_cfg.AlertnessCapPerTurn, _cfg.AlertnessCapPerTurn);
            if (!string.IsNullOrWhiteSpace(intent.TeachSkillName)) r.TeachSkillName = intent.TeachSkillName.Trim();
            if (!string.IsNullOrWhiteSpace(intent.ReleaseTarget)) r.ReleaseTarget = intent.ReleaseTarget.Trim();
            if (!string.IsNullOrWhiteSpace(intent.FavorTarget) && intent.FavorTargetDelta != 0)
            {
                r.FavorTarget = intent.FavorTarget.Trim();
                r.FavorTargetDelta = Clamp(intent.FavorTargetDelta, -_cfg.ThirdPartyFavorCapPerTurn, _cfg.ThirdPartyFavorCapPerTurn);
            }
            if (!string.IsNullOrWhiteSpace(intent.EquipPutOnItem)) r.EquipPutOnItem = intent.EquipPutOnItem.Trim();
            if (!string.IsNullOrWhiteSpace(intent.EquipTakeOff)) r.EquipTakeOff = NormalizePart(intent.EquipTakeOff);
            if (intent.SectSupport) r.SectSupport = true;          // 后端再校验门派身份/关系,如实回成败
            if (!string.IsNullOrWhiteSpace(intent.MatchmakeTarget)) r.MatchmakeTarget = intent.MatchmakeTarget.Trim(); // 后端校验异性/成年/可婚
            return r;
        }

        // 仅"已具备该关系"的无谓重复保护(引擎合法性);好感阈值/资格判断已前移到提示词,这里不再卡。
        private static bool AlreadyHasRelation(string prop, ushort rel, ResolvedEffect r)
        {
            switch (prop)
            {
                case "best_friend": if ((rel & 8192) != 0) { r.Notes.Add("已是挚友"); return true; } return false;
                case "sworn_sibling": if ((rel & 512) != 0) { r.Notes.Add("已结义"); return true; } return false;
                case "mentor": if ((rel & (2048 | 4096)) != 0) { r.Notes.Add("已有师徒"); return true; } return false;
                case "lover": if ((rel & 16384) != 0) { r.Notes.Add("已是恋人"); return true; } if ((rel & 1024) != 0) { r.Notes.Add("已是夫妻"); return true; } return false;
                case "spouse": if ((rel & 1024) != 0) { r.Notes.Add("已是夫妻"); return true; } return false;
                default: r.Notes.Add("未知关系:" + prop); return true;  // 认不出的关系不落地(避免误执行)
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // 把 LLM 给的部位词归一到 weapon/clothing/armor/accessory/carrier(认不出返回空 → 不换装)
        private static string NormalizePart(string p)
        {
            p = (p ?? "").Trim().ToLowerInvariant();
            if (p.Contains("weapon") || p.Contains("武") || p.Contains("兵") || p.Contains("剑") || p.Contains("刀") || p.Contains("器")) return "weapon";
            if (p.Contains("cloth") || p.Contains("衣") || p.Contains("袍") || p.Contains("裳")) return "clothing";
            if (p.Contains("armor") || p.Contains("甲") || p.Contains("护")) return "armor";
            if (p.Contains("access") || p.Contains("饰") || p.Contains("佩") || p.Contains("环")) return "accessory";
            if (p.Contains("carrier") || p.Contains("坐骑") || p.Contains("代步") || p.Contains("牲畜")) return "carrier";
            return "";
        }
    }
}
