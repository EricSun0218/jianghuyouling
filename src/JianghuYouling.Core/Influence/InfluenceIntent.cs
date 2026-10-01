namespace JianghuYouling.Core.Influence
{
    /// <summary>
    /// LLM 扮演 NPC 后产出的"真实反应意图"。M1 含:对本次互动的满意度(→好感)、
    /// 可选的关系提议、画像依据(reasoning,大效果必须有据)、可选的待写记忆。
    /// </summary>
    public sealed class InfluenceIntent
    {
        public int Satisfaction { get; set; }          // -100..100,本轮 NPC 对这次互动的满意度 → 即时好感
        public int Mood { get; set; }                  // -100..100,这次交谈对 NPC 心情的影响(正=宽慰舒畅,负=郁结低落)→ 改 Happiness;心情持续低落会加速相枢传染(游戏自带联动)
        public string RelationProposal { get; set; }   // none | best_friend | sworn_sibling | mentor
        public bool RecognizeTaiwu { get; set; }       // NPC 归心、认可太吾为其主(仅门派/据点成员有效;大效果,需画像依据)
        public bool ShareSecret { get; set; }          // NPC 向太吾吐露一条自己知道的秘闻(需信任,需画像依据)
        public int MoralityShift { get; set; }         // -25..25,本次交谈对其价值观的撼动(正=趋仁善刚正,负=趋叛逆唯我);过月累积漂移立场
        public string Reasoning { get; set; }          // 画像/记忆/经历依据;空则不允许大效果(防谄媚跑偏)
        public MemoryDraft Memory { get; set; }        // 可空:本轮值得长期记住的事
        public BehaviorDraft Behavior { get; set; }    // 可空:这次交谈让 NPC 决意去做的事(过月生效)

        // —— NPC 主动发起的真实反应(NPC→太吾 / NPC→第三方;均需画像依据 reasoning) ——
        public int GiveSilverToTaiwu { get; set; }     // >0:NPC 解囊/借予太吾的银钱(债由江湖人情自记)
        public string GiveItemName { get; set; }       // 非空:NPC 决意相赠太吾的那件随身物品名(须在其可赠清单内)
        public int GiveItemCount { get; set; } = 1;    // 赠送数量(资源/食材类可>1;贵重单品=1);后端按 NPC 实有封顶
        public string FeudTarget { get; set; }         // 非空:NPC 当场与此人结仇("太吾"或具名第三方)
        public string ReconcileTarget { get; set; }    // 非空:NPC 放下对此人的旧怨("太吾"或具名第三方)
        public int FollowDecision { get; set; }        // 0 无 / 1 决意追随太吾 / -1 愤而离去(解除追随)
        public string DiscloseSecretTo { get; set; }   // 非空:NPC 把所知秘闻透露给此第三方(给太吾用 share_secret)
        public int SecretIndex { get; set; }           // 1基:吐露/散播时选第几条秘闻(0=未指定→回退)
        public int AlertnessShift { get; set; }        // NPC 对太吾戒心变化:负=卸下戒备(解锁更高好感)/正=更添提防
        public string TeachSkillName { get; set; }     // 非空:NPC 决意传授太吾的那门武学名(须在其所习武学清单内)
        public string ReleaseTarget { get; set; }      // 非空:NPC 放走所掳/所擒的此人
        public string FavorTarget { get; set; }        // 非空:NPC 对此第三方观感变化的目标
        public int FavorTargetDelta { get; set; }      // 配合 FavorTarget:对其好感增减
        public string EquipPutOnItem { get; set; }     // NPC 换上的那件具体物品名(须在随身物品内;需说服力)
        public string EquipTakeOff { get; set; }       // NPC 卸下:weapon/clothing/armor/accessory/carrier(需说服力)
        public bool SectSupport { get; set; }          // 仅门派之人:在其门派为太吾表态支持(提高太吾在该门派的支持率,效果看其门派地位)
        public string MatchmakeTarget { get; set; }    // 非空:你(NPC)接受太吾保的媒、愿与此人(太吾介绍的对象名)结为夫妻
    }

    /// <summary>NPC 想长期记住的一条(仿 Claude 记忆,落地到 NpcMemoryStore)。</summary>
    public sealed class MemoryDraft
    {
        public string Content { get; set; }   // NPC 第一人称
        public string Type { get; set; }      // 恩情/仇怨/承诺/秘密/印象/事件
        public string Keywords { get; set; }  // 召回索引
        public int Importance { get; set; } = 3;
    }

    /// <summary>对话让 NPC 决意去做的事(过月生效,落地到 IntentQueue)。</summary>
    public sealed class BehaviorDraft
    {
        public string Kind { get; set; }    // 保护某人/放下仇恨/投奔门派/赴约/前往某地…
        public string Target { get; set; }  // 目标人或地点
        public string Note { get; set; }    // 为何这么做(画像依据)
    }
}
