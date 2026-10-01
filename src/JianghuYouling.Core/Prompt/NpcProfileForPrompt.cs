namespace JianghuYouling.Core.Prompt
{
    /// <summary>提示词所需的 NPC 信息(由前端 NpcSnapshot 映射而来,Core 不依赖游戏域)。</summary>
    public sealed class NpcProfileForPrompt
    {
        public int NpcId { get; set; }
        public int TaiwuId { get; set; }
        public bool IsDead { get; set; }
        public string Name { get; set; }
        public string Gender { get; set; }
        public string SexualOrientation { get; set; } // 权威性取向：异性向 / 兼有同性与异性取向
        public string TaiwuName { get; set; }
        public string TaiwuGender { get; set; }
        public int PhysiologicalAge { get; set; }  // 身龄：人物当前呈现出的年龄
        public int ActualAge { get; set; }         // 命龄：人物实际已生存的总年数
        public int Charm { get; set; } = -1;       // 本体当前魅力值，负值为未知；不进入稳定画像
        public int Age { get => PhysiologicalAge; set => PhysiologicalAge = value; } // 旧调用兼容，固定指向身龄
        public string Behavior { get; set; }        // 立场:刚正/仁善/中庸/叛逆/唯我
        public string OrgTitle { get; set; }         // 门派头衔
        public string SectLore { get; set; }         // 门派设定/门风简介(门派NPC人设注入;详情走 query_sect_lore)
        public string GradeName { get; set; }        // 品级别称
        public string Relation { get; set; }         // 与太吾关系
        public string FavorLevel { get; set; }       // 好感档名
        public string PersonalitiesText { get; set; } // 七元赋性概述
        public string FeaturesText { get; set; }      // 稳定秉性/特性（与临时状态分开，供人物圣经）
        public string StatusText { get; set; }       // 相枢/心魔/心情/健康 概述
        public int Happiness { get; set; }           // 本轮权威心情值；动态决策上下文，不进入稳定画像
        public string FameText { get; set; }         // 本轮权威江湖名誉/侠名
        public string CustomPersona { get; set; }     // 玩家为这名队友亲手设定的人设(最高权威,优先于蒸馏画像),可空
        public string CustomPersonaMode { get; set; } = "append"; // append=保留内置特殊人设；replace=替换内置特殊人设（两者均保留低优先级自动演化画像）
        public string SpecialPersona { get; set; }    // 按 Config.Character.TemplateId 命中的内置固定人设,可空
        public string Portrait { get; set; }         // 蒸馏画像(高优先级人设锚点),可空
        public string GiftableItemsText { get; set; } // 未穿戴的可主动赠物；不含只有装备槽里才有的物品
        public string EquippedItemsText { get; set; } // 当前穿戴；仅玩家本轮明确索要该装备时可卸下相赠
        public string LearnableSkillsText { get; set; } // 所习武学名字(顿号分隔),供 AI 决定传哪门
        public string LearnableLifeSkillsText { get; set; } // 所习技艺名字(顿号分隔),供 AI 决定传哪门技艺
        public string TrainableSkillsText { get; set; } // 实时尚未练满的已会武学；成功查询但为空时为“无”
        public string UnreadBooksText { get; set; } // 实时背包中尚未读完的武学/技艺书；成功查询但为空时为“无”
        public string UsableItemsText { get; set; } // 实时可由 NPC 自己使用的消耗品；成功查询但为空时为“无”
        public string TellableSecretsText { get; set; } // 可吐露秘闻的编号列表,供 AI 用 secret_index 指明吐露哪条
        public string TaiwuInfoText { get; set; }     // 眼前太吾本人信息(实时:姓名/性别/年龄/立场/门派/侠名)
        public string WorldTimeText { get; set; }     // 当下时令(实时:第N年 季 月)
        public string LocationText { get; set; }      // NPC 当前所在地；每轮实时注入，不能依赖画像或旧历史
        public string ExternalExperienceText { get; set; } // 可选第三方 Mod 只读经历；每轮动态读取，永不固化进画像/记忆
        public int ConsummateLevel { get; set; } = -1; // 自身武学精纯 0..18(杀/绑硬门槛:不得低于对方，相等可成;-1=未知)
        public bool HasRope { get; set; }              // 随身是否备有绳索(可绑缚擒人)——供攻击性工具调用前自知
        public bool HasPoison { get; set; }            // 随身是否备有毒药(可下毒)
    }
}
