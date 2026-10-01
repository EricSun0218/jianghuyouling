using System;
using System.Collections.Generic;
using JianghuYouling.Core.Llm;
using Newtonsoft.Json.Linq;
using static JianghuYouling.Core.Llm.ToolDef;

namespace JianghuYouling.Core.Tools
{
    /// <summary>对话工具现场:仅表达由游戏状态确定的硬边界，不参与意图猜测或按需补载。</summary>
    public sealed class ToolContext
    {
        public bool IsMerchant { get; set; }   // 对方是商人 → 带 query_merchant_goods / trade
        public bool InSect { get; set; }       // 对方属门派 → 带 sect_support
        public bool Remote { get; set; }       // 千里传音(远程对话)→ 去掉需当面/物理接触的工具
        public bool CanStartCombat { get; set; } // 仅当前本地单聊、非俘虏时开放原生战斗约战
        public bool CanOpenGrooming { get; set; } // 仅玩家发起的本地单聊可转入本体“为NPC梳头修面”互动
        public bool NpcInitiated { get; set; } // NPC 主动来信：保留自主行动，去掉替太吾决定的能力
        public bool ConversationOnly { get; set; } // 已故灵魂：只允许自然语言交谈，查询与动作工具全部关闭
        public bool IsGroup { get; set; } // 群聊没有唯一委托发布人，首版不开放人物委托
    }

    /// <summary>全部对话工具的 schema(模型看到的"它能做什么")。执行在前端 ToolExecutor。</summary>
    public static class ToolRegistry
    {
        // 实现与后端路由暂时保留，便于之后继续调试；Agent 工具面与行动技能统一不再暴露。
        public static bool IsAgentToolEnabled(string toolName)
            => !string.Equals(toolName, "use_item", StringComparison.Ordinal)
                && !string.Equals(toolName, "event_use_item", StringComparison.Ordinal);

        /// <summary>过月与江湖事件不得自行打开需要玩家操作的本体梳妆互动。</summary>
        public static bool IsAutonomousToolEnabled(string toolName)
            => IsAgentToolEnabled(toolName)
                && !string.Equals(toolName, "change_appearance", StringComparison.Ordinal);

        /// <summary>
        /// NPC 主动联系太吾时沿用普通单聊的 Agent 工具面，但不能反过来替本轮没有发言的
        /// 太吾赠物、传功、吐秘闻、成交或作出其它需要玩家确认的决定。其余查询与 NPC
        /// 自己能够作出的真实行动仍然可用；是否行动由模型按人设和上下文判断，不是必选项。
        /// </summary>
        public static bool IsNpcInitiatedToolAllowed(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName) || !IsAgentToolEnabled(toolName)) return false;
            if (toolName.StartsWith("taiwu_", StringComparison.Ordinal)) return false;
            switch (toolName)
            {
                case "record_reaction":
                case "trade":
                case "change_caravan_favor":
                case "matchmake":
                case "spend_night":
                case "start_combat":
                case "change_appearance":
                    return false;
                default:
                    return true;
            }
        }
        private static readonly HashSet<string> RemotePhysicalTools = new HashSet<string>(
            new[]
            {
                "gift", "barter", "steal", "teach", "write_book", "heal", "detox",
                "regulate_breath", "spend_night",
                "change_equipment", "change_appearance", "trade", "taiwu_give_item",
                "taiwu_teach", "taiwu_write_book", "change_caravan_favor", "start_combat",
                "kill", "capture", "poison",
            },
            StringComparer.Ordinal);
        private static readonly KeyValuePair<string, string>[] RemotePhysicalToolLabels =
        {
            new KeyValuePair<string, string>("change_caravan_favor", "改变商队观感"),
            new KeyValuePair<string, string>("change_appearance", "改变外貌"),
            new KeyValuePair<string, string>("change_equipment", "更换装备"),
            new KeyValuePair<string, string>("taiwu_write_book", "太吾成书相赠"),
            new KeyValuePair<string, string>("taiwu_give_item", "收取太吾赠物"),
            new KeyValuePair<string, string>("taiwu_teach", "拜领太吾传授"),
            new KeyValuePair<string, string>("start_combat", "发起原生战斗"),
            new KeyValuePair<string, string>("spend_night", "共度春宵"),
            new KeyValuePair<string, string>("write_book", "成书相赠"),
            new KeyValuePair<string, string>("capture", "擒拿"),
            new KeyValuePair<string, string>("poison", "下毒"),
            new KeyValuePair<string, string>("barter", "交换"),
            new KeyValuePair<string, string>("trade", "成交"),
            new KeyValuePair<string, string>("steal", "偷取"),
            new KeyValuePair<string, string>("teach", "亲授"),
            new KeyValuePair<string, string>("gift", "赠物"),
            new KeyValuePair<string, string>("heal", "疗伤"),
            new KeyValuePair<string, string>("detox", "驱毒"),
            new KeyValuePair<string, string>("regulate_breath", "调息"),
            new KeyValuePair<string, string>("kill", "杀人"),
        };

        /// <summary>
        /// 当前现场是否真正提供某项动作。行动技能目录与最终工具表共用这一条物理边界，
        /// 避免远程对话虽然删掉动作 schema，却仍由查询说明或技能指南诱导模型调用它。
        /// </summary>
        public static bool IsActionAvailable(string toolName, ToolContext context)
            => !(context != null && context.Remote
                && !string.IsNullOrWhiteSpace(toolName)
                && RemotePhysicalTools.Contains(toolName));

        public static List<ToolDef> BuildConversationTools(ToolContext ctx)
        {
            ctx = ctx ?? new ToolContext();
            var t = new List<ToolDef>();

            // 灵魂会话的权限边界不是提示词建议，而是工具面本身为空。这样无论玩家措辞、
            // 世界书、人设或供应商行为如何变化，模型都拿不到任何查询/动作工具。
            if (ctx.ConversationOnly) return t;

            // ========== 检索类(只读；当前现场完整、稳定地提供)==========
            t.Add(Of("recall_memory", "回想你与太吾的旧账(承诺/恩怨/借贷/秘密/上次说定的事)。**只要太吾的话里提到你俩过去任何事——上次/之前/那回/你答应过/你欠我/还记得吗/借的钱——就必须先调本工具回想,查到再答,绝不可凭脑补的旧事接话(会与你真实记忆相左)。** topic 填太吾提到那件事的关键词(如『借钱』『婚约』『杀父之仇』),按关键词与记忆索引检索；查不到会明确说没有相关记忆，绝不会拿无关旧事冒充。",
                Obj(("topic", Str("要回忆的话题/关键词,如'借钱''上次的承诺'"), false))));
            // 技能渐进披露只加载复杂行为的“做法”，不按玩家措辞增删任何真实工具。
            // 入口本身也是稳定只读工具，单聊/群聊共用相同现场目录。
            t.Add(ConversationSkillCatalog.BuildConsultTool(ctx));
            t.Add(Of("query_npc_status", "查看你自己当下详状:属性资质、武学精纯、心境、相枢魔气/气机、对太吾戒备。**其中『武学精纯』(0-18)是你能否取人性命/擒拿一个人的关键——须你的精纯【不低于】对方才下得了手，相等亦可;杀/绑前先 query_person 看对方精纯比一比,低于就别硬来。**", Obj()));
            t.Add(Of("query_health_status", "查看你自己当前的气血、伤势、气息（内息紊乱）和烈/郁/寒/赤/腐/幻六类中毒。询问身体状况，或据此决定求医、休养和照料时调用；只能查自己。", Obj()));
            t.Add(Of("query_npc_relationships", "查你的关系网真实名单:父母/子女/兄弟姐妹/结义/配偶/心上人/师父/挚友/仇敌、以及与太吾的现有关系。**凡谈及你的家人亲眷、师承、交友、恩怨、姻缘,或太吾问起你都认识谁、谁是你什么人,都尽管先调本工具看真名实况再答——多查无妨,据实道来,别凭印象编人。** 填 relation 枚举前(如 dissolve_relation)也先查准。",
                Obj(("name", Str("查与谁的关系;省略=关系网概览"), false))));
            t.Add(Of("query_npc_history", "查你真实的生平大事记(较早经历摘要 + 最近本体原文,带年月与事件,不按两年截断)。**凡谈及你的来历出身、经历过往、做过什么、遭逢何事——或太吾问起你这些年的故事,都尽管先调本工具翻你真实生平再答,多查无妨、据实而谈,别凭空编往事。**", Obj()));
            t.Add(Of("query_npc_items", "查你真实持有的资源、银钱、食物、药毒、装备、书籍、工具、材料与其他物品，含当前穿戴。gift/change_equipment 通常可直接使用上方预载清单的确切名称；只有清单未列出目标时才查询。keyword 可填物名或类别，留空列全部。返回的名称、穿戴状态和「×数量」均须照实使用；没有返回的物品不可编造。",
                Obj(("keyword", Str("要找的物品名(精确搜你有没有);留空=列出全部"), false))));
            t.Add(Of("query_person_items", "查某个具名角色当前真实随身可交换/可赠之物。覆盖资源、银钱、食物、药毒、装备、书籍、工具、材料、其他,以及武具/装备页已佩带之物;也识别常见别名/泛称。**用于以物换物前核对双方有什么:person 填『太吾』、你自己、或第三方姓名;keyword 填要找的物品名/类别,留空列全部。返回名称要原样用于 barter。**",
                Obj(
                    ("person", Str("要查谁:太吾/我/你/第三方姓名;留空=你自己"), false),
                    ("keyword", Str("要找的物品名或类别;留空=列全部"), false))));
            t.Add(Of("query_person", "打听一个具名人物的实时近况：身份、性别、年岁、立场、门派、所在位置、你与他的关系、交情和武学精纯。太吾向你问起某人、你准备谈论其近况或位置、或要在决定前比较人物时调用。可查你或太吾关系网中的人、当前同道、以及你或太吾此刻同地的人；查不到只代表在这些可靠范围内无法确认，不能据此编造。name 优先填实时名单返回的完整姓名；遇到同名或显示名不稳定时，原样填写名单中的 #人物编号即可唯一定位。直接调用行动工具时执行层也会自动做所需的权威预查，不必机械重复查询；权威回执才是最终结果。",
                Obj(("name", Str("完整姓名，或查询名单返回的 #人物编号；不要只填姓氏、泛称或自造别名"), true))));
            t.Add(Of("query_current_block", "读取此刻真实现场名单。面对面时返回与你和太吾同一地图块的全部可交互江湖人，并合并当前同道；仇敌、极低好感者和陌生人也必须列出，不能因为关系恶劣而隐藏。千里传音时分别返回你身边与太吾身边的人，不能把两个远隔现场混成一个。太吾问『这里有谁』『附近找谁』、让你自行挑选现场对象，或你需要比较在场人选时主动调用。返回姓名后附 #人物编号；同名时原样使用编号。名单只证明实时在场，不代表动作成功。", Obj()));
            t.Add(Of("query_area_people", "打听某个区域此刻有哪些可确认的江湖人。太吾问某城、某州或某地有哪些人，或你打算前往一个区域寻找目标而尚不知道具体对象时调用；它查询的是整个区域，不等于与你同一地图块，不能拿它代替 query_current_block 证明可以当面动手或交割。place 必须填游戏中真实地名（如大理、杭州、太吾村）；认不出地名时先 query_place。返回姓名后附 #人物编号，同名时后续查询可直接使用编号。", Obj(("place", Str("游戏中的确切区域或地点名；不确定时先 query_place"), true))));
            t.Add(Of("query_org_members", "查询某门派当前可确认的成员名单。太吾问同门、门派人物、掌门门人，或你需要从本门人物中寻找关系/行动对象时调用。sect 留空查询你所属门派，填写时必须用真实门派名；本工具只证明门派成员身份，不证明对方与你同地，任何当面行为仍须 query_current_block。返回姓名后附 #人物编号，同名时可用编号继续 query_person。", Obj(("sect", Str("确切门派名；留空=你自己所属门派"), false))));
            t.Add(Of("query_npc_skills", "查清你真正会、能传给太吾的武学与技艺(技艺以书本真名列出,如《金针伐脉功》)。**你可传的武学/技艺【已预载在上方『随身能直接给/传/吐露的』清单里】——直接从那清单里的确切名原样照抄即可,通常无需先调本工具;只有要传清单里没列出的才用本工具核对(脑中『我应该会』却不在列的就是不会,硬传必落空)。** 传法两条路:**教/传给太吾 → write_book(回忆成秘籍交他自研);当面亲授在场第三方 → teach。** 想确认某门会不会就填 keyword 精确搜;留空则列出全部。",
                Obj(("keyword", Str("要找的武学/技艺名(精确搜你会不会);留空=列出全部"), false))));
            t.Add(Of("query_npc_build", "查你自己当前正在运哪门内功、装配了哪些功法:主修内功、攻击、身法、防御、辅助,以及所穿装备。太吾问你眼下用什么功法/配招/运功/内功时先查,按真实结果回答,别把『会的功法清单』误当『正在运行』。", Obj()));
            t.Add(Of("query_npc_secrets", "按指定接收者查询你所知且当前仍能真正传播的秘闻。用于吐露前比较候选：to 填准备告诉的人，省略=太吾；结果已排除对方知道或已经公开的秘闻。若已从预载清单选定原始序号，也可直接调用 tell_secret，执行层会自动按同一接收者重新预查，资格不符则不传播并返回真实原因。",
                Obj(("to", Str("准备告诉谁；省略=太吾，也可填可靠识别的第三方完整姓名"), false))));
            t.Add(Of("query_taiwu_build", "查看太吾当前的武学/技艺搭配、所穿装备与战斗属性。要点评太吾本事、给配招/换装/克敌建议前先查,凭实情说话别空谈。", Obj()));
            t.Add(Of("query_taiwu_items", "查看太吾随身、可由他赠予你之物。覆盖资源、食物、药毒、装备、书籍、工具、材料、其他,以及太吾武具/装备页已佩带之物;也识别常见别名/泛称。**太吾在对话里说要送你东西时,先调此查太吾确有什么,再用 taiwu_give_item 按名收下。** 太吾说要送某样具体东西就填 keyword 精确查;留空=列出全部。",
                Obj(("keyword", Str("要找的物品名;留空=列出全部"), false))));
            t.Add(Of("query_taiwu_skills", "查看太吾所习、可传授/可回忆成书给你的武学与技艺。**太吾说要传你功法/技艺时,先调此查太吾确实会什么,再按语境用 taiwu_teach 拜领亲授,或用 taiwu_write_book 收下太吾回忆成册的秘籍。** 太吾说要传某门具体功法就填 keyword 精确查;留空=列出全部。",
                Obj(("keyword", Str("要找的武学/技艺名;留空=列出全部"), false))));
            t.Add(Of("query_world_progress", "查看当前江湖世道:年月时令、相枢之劫进度(化身现世/讨伐到哪一步,世道公知)、以及【你本门的门派主线】进展(若你身属门派,本门中人本就知晓)。判断能否收人入队、是否乱世投奔、以及谈及相枢之祸/本门大事时,先查此据实说。", Obj()));
            t.Add(Of("query_lore", "渐进翻阅太吾百晓册(江湖事典/见闻百科)，查门派源流、地理城镇、知名人物、物产珍奇、武学典故、奇术机关等掌故。不要拿玩家整句话碰关键词：首次按问题选择一级章节“世界/门派/人物/修习/战斗/产业/游历/物品”，代码会返回该类全部三级章节目录；再把目录中的完整路径原样传回，即可把那个小章节的全部正文读入本轮上下文。若已知道唯一三级标题也可直接填写。查到后用自己的口吻转述，别照本宣科。",
                Obj(("path", Str("章节路径；首次填一级章节如“门派”，下一次原样填目录返回的完整三级路径如“门派·门派一览·璇女派”；留空=一级目录"), false))));
            t.Add(Of("query_place", "查江湖【可前往的地名】:确认某地是否存在、找它在本作里的确切名。**要 goto_place 前往/赴约某地前,拿不准地名就先用它核对——查到的名【原样】填给 goto_place 才不会认不出而落空。** keyword 填要找的地名(如 大明山/大理);留空=列一批主要地名供参考。",
                Obj(("keyword", Str("要找/核对的地名(如 大明山);留空=列出一批主要地名"), false))));
            if (ctx.InSect)
                t.Add(Of("query_sect_lore", "查阅你所属门派的设定·渊源·理念·门规·入派誓约·主线故事(你身为本门中人,这些底细本就该知道)。太吾问及你门派的来历/宗旨/规矩/恩怨故事,或你要以本门立场表态时,先查此再用本门人的口吻道来,别杜撰。", Obj()));
            if (ctx.IsMerchant)   // 修缩进bug:query_merchant_goods 本是商人专属,原先漏了 if 被无条件注册给所有人
                t.Add(Of("query_merchant_goods", "查看你当前真实可售货物、库存、每件基准价与太吾银钱。报价或成交前必须先查；trade.item 只能原样使用返回的纯货名，不带「×数量」和价格标记，amount 填数量，price 填成交总价。清单没有的货不可编造，报价不得超过太吾能支付的钱。keyword 留空列全部。",
                    Obj(("keyword", Str("要找的货物名(精确查有没有);留空=列出全部货"), false))));

            // ========== 动作类(真实行为,执行后回成败)==========
            t.Add(Of("record_reaction", "当这次交谈确实让你对【太吾本人】的满意、心情、立场或戒心发生明显变化时，可选调用一次；没有明显变化就不要调用。它不会改变你对任何第三方的关系或观感；第三方观感必须另用 adjust_third_party_favor。",
                Obj(
                    ("satisfaction", Int("对这次交谈满意度 -100~100(正=亲近欣赏)"), false),
                    ("mood", Int("心情变化 -100~100"), false),
                    ("morality_shift", Int("立场/价值观被撼动 -25~25(寻常为0)"), false),
                    ("alertness_shift", Int("对太吾戒心变化 -1000~1000(负=更敞开)。省略或填0时会按满意度自动联动；普通交谈不能填写断交/离异级巨变"), false))));
            t.Add(Of("remember", "把以后可能影响称呼、偏好、约定、关系、行动或续聊的内容记入长期记忆(可多次)，不要求必须是重大事件。玩家明确说“记住/别忘/以后照此处理”时必须调用；只在正文口头答应不算记住。完全没有延续价值的寒暄不用记。",
                Obj(
                    ("content", Str("第一人称一句话"), true),
                    ("type", Sel("类型", "恩情", "仇怨", "承诺", "秘密", "印象", "事件"), false),
                    ("keywords", Str("逗号分隔关键词"), false),
                    ("importance", Int("1~8；模型不能自行把记忆标成系统核心"), false))));
            if (!ctx.IsGroup)
                t.Add(Of("offer_commission", "当你基于自身处境、人设和当前谈话，确实想请太吾替你办一件可验证的事时，可主动发布一项真实委托。不是每次聊天都要发；你已有进行中委托时禁止再发。只有调用本工具才会把委托写入右侧【委】列表，正文空口请求不会生成任务。你负责选任务参数和普通任务的奖励品级，不得指定具体奖品；普通委托固定获得2项组合奖励。击杀任务由你选择真正想除掉的具名目标，代码会按目标精纯决定任务品级，并固定获得更丰厚的3至4项组合奖励。发布后在正文中用 request 的口吻自然提出请求，不得提前说已经完成或发奖。",
                    Obj(
                        ("kind", Sel("任务类型：交付资源会在领奖时扣除；积攒资源/赚取银钱/获得威望/增进交情只核验相对发布时新增量；kill_npc 核验指定目标真实死亡",
                            "deliver_resource", "collect_resource", "earn_money", "gain_prestige", "increase_favor", "kill_npc"), true),
                        ("resource", Sel("仅交付/积攒资源时填写", "食材", "木材", "金石", "玉石", "织物", "药材"), false),
                        ("target", Str("仅 kill_npc 填写：你真正想让太吾杀死的具名人物，使用完整真名或 #人物编号；不能填太吾或你自己"), false),
                        ("amount", Int("任务数量。击杀任务固定为1；交付50~5000；积攒100~10000；赚银500~30000；威望100~8000；好感300~5000，代码会钳入对应范围"), true),
                        ("reward_grade", Sel("普通任务只选奖励品级，不填具体奖励；击杀任务此项会被代码按目标精纯覆盖", "九品", "八品", "七品", "六品", "五品", "四品", "三品", "二品", "一品"), true),
                        ("request", Str("你亲口向太吾提出请求的自然台词，须与所填类型和数量完全一致"), true),
                        ("completion", Str("太吾完成并领取奖励后你会说的答谢台词，不得提前声称已经完成"), false))));
            t.Add(Of("adjust_mood", "因本轮真实交谈、事件或人物行为改变任意具名人物（包括你自己、第三方 NPC 或太吾）的当前心情。target 填实际发生变化的人；reason 写清直接原因。若只是你自己因与太吾本轮交谈产生的即时心情变化，应优先并且只用 record_reaction，避免重复结算。",
                Obj(
                    ("target", Str("心情实际变化的人：自己/太吾/完整姓名/#人物编号"), true),
                    ("delta", Int("心情变化 -30..30，正=更愉快，负=更低落；不能为0"), true),
                    ("reason", Str("造成这次心情变化的具体原因"), true))));
            t.Add(Of("adjust_fame", "因已经发生、会被江湖知晓的公开善举、恶行、胜负或事迹，改变任意具名人物（包括你自己、第三方 NPC 或太吾）的名望。私下想法、普通闲聊和无人知晓的小事不能改变名望。target 填名望实际变化的人。",
                Obj(
                    ("target", Str("名望实际变化的人：自己/太吾/完整姓名/#人物编号"), true),
                    ("delta", Sel("名望变化；正=扬名，负=声名受损", "-12", "-9", "-6", "-3", "3", "6", "9", "12"), true),
                    ("reason", Str("已经公开发生并足以影响名望的具体事迹"), true))));
            t.Add(Of("gift", "NPC（你）向太吾/现场第三方赠真实背包物、资源、熟食或银钱。自主送礼不得卸下装备；仅当玩家本轮语义上明确要求赠出当前穿戴的具体装备时 allow_equipped=true。name 原样取预载清单或 query_npc_items，不带×数量；资源/resources、单品/item、银钱/silver，target 省略=太吾。失败不得假称送出。",
                Obj(
                    ("type", Sel("赠予类型:item=单品/resources=食材药材布料矿石等资源(成批)/silver=银钱", "item", "resources", "silver"), true),
                    ("name", Str("item/resources 时填纯物品名(须是 query_npc_items 里确有之物,原样照抄;若清单显示「物名×数量」只填×前的物名,数量改填 amount)"), false),
                    ("amount", Int("数量;资源类可填 5/10 等,银钱填数目"), false),
                    ("target", Str("收受方姓名;省略=赠太吾。填第三方姓名则赠给那人(须你相识、或与你同为太吾同道、或太吾这次谈话提及之人)"), false),
                    ("allow_equipped", Bool("仅当玩家本轮明确要求赠出当前穿戴的具体装备时设 true；自主赠礼必须省略或 false"), false))));
            t.Add(Of("barter", "让任意两名在场角色原子交换真实持有物。买卖、交易和议价不只可以用银钱，也可主动换物，不限玩家点名。任意一边都可以直接填「银钱」，支持物品换银钱或银钱换物品。例：太吾用蜂王露换NPC的《黄竹歌》。已知确切物名与数量可直接调用，拿不准才 query_person_items；item 必须原样填写真实名称。默认甲方=你、乙方=太吾；只说不调用则没有交换。",
                Obj(
                    ("person_a", Str("甲方姓名;省略=你自己/NPC。也可填太吾或第三方姓名"), false),
                    ("item_a", Str("甲方给出的物品/资源/银钱名,须来自 query_person_items 清单原样"), true),
                    ("amount_a", Int("甲方给出数量;省略=1,资源/银钱可多"), false),
                    ("person_b", Str("乙方姓名;省略=太吾。也可填你自己/NPC或第三方姓名"), false),
                    ("item_b", Str("乙方给出的物品/资源/银钱名,须来自 query_person_items 清单原样"), true),
                    ("amount_b", Int("乙方给出数量;省略=1,资源/银钱可多"), false))));
            t.Add(Of("steal", "由你亲自从太吾或合规第三方身上偷取一件真实持有物。系统会自动校验 victim 当前真实持有,再按本体的物品价值/数量警觉度、双方武学造诣、迅疾、移动速度和性格进行三阶段能力检定;贵重或大量物品会明显更难。成功才转移物品,失败会被发现并降低 victim 对你的好感。已知确切物名时可直接调用；拿不准对方有什么才用 query_person_items。偷窃者固定是你自己(NPC),太吾不能借对话命令把自己设为偷窃者;不得代太吾偷 NPC。只在嘴上说偷了而不调本工具=没有偷到。",
                Obj(
                    ("thief", Str("偷窃者固定为你自己/NPC;可省略或填你/我/自己的名字。不得填太吾或第三方"), false),
                    ("victim", Str("被偷者姓名;省略=太吾。也可填你自己/NPC或第三方姓名"), false),
                    ("item", Str("要偷的物品/资源/银钱名,须来自 victim 的 query_person_items 清单原样"), true),
                    ("amount", Int("数量;省略=1,资源/银钱可多"), false))));
            t.Add(Of("teach", "把你会的武学或技艺当面亲授给现场第三方，使其承接你的当前进度；不能传太吾，传太吾须用 write_book。名称原样取自预载清单或 query_npc_skills；执行层自动核实双方同地、你确实会且对方尚未会。需要比较对象时再 query_person。只说不调用不算传授。",
                Obj(
                    ("type", Sel("传授类型", "combat", "life"), true),
                    ("name", Str("武学名或技艺名(须是 query_npc_skills 里你会的,原样照抄)"), true),
                    ("target", Str("受学者姓名(【第三方】,须你相识/同队/太吾这次提及之人)。teach 不传太吾——教/传给太吾请改用 write_book"), true))));
            t.Add(Of("write_book", "把你会的武学/技艺按自己的正逆练法回忆成秘籍相赠；这是 NPC 传功给太吾的方式。名称原样取自预载清单或 query_npc_skills；combat=武学、life=技艺。默认赠太吾，也可赠现场第三方。若背包已有该书应改用 gift。高品级可能有残页；只说不调用不算赠书。",
                Obj(
                    ("type", Sel("成书类型:combat=武学秘籍 / life=技艺书", "combat", "life"), true),
                    ("name", Str("要回忆成书的武学名或技艺名(须是 query_npc_skills 里你会的,原样照抄)"), true),
                    ("target", Str("收书人姓名;省略=赠太吾。填第三方姓名则赠给那人"), false))));
            t.Add(Of("set_relation", "与太吾建立/递进关系,或归心、入队/离队。**只说不调用=关系不变。** befriend=挚友、swear_sibling=结义、apprentice=你拜太吾为师、take_disciple=你收太吾为徒、adoptive_parent=你认太吾为义父/义母、adoptive_child=你收太吾为义子/义女、lover=你单方面爱慕太吾(太吾已爱慕你才两情相悦)、spouse=夫妻、recognize=归心、follow=同行、leave=离队。师徒和义亲方向拿不准先 query_npc_relationships。明确自愿建立义亲时不硬卡年龄、双向好感或已有在世父母子女；执行层仍拒绝自己认自己、重复义亲和本体判定冲突的关系。归心和入队是郑重之事,结合人设、处境与交情自决,可查 query_world_progress。",
                Obj(("action", Sel("关系动作", "befriend", "swear_sibling", "apprentice", "take_disciple", "adoptive_parent", "adoptive_child", "lover", "spouse", "recognize", "follow", "leave"), true))));
            t.Add(Of("spend_night", "情之所至,与太吾共度春宵(《太吾绘卷》原生玩法;同性亦可,是否有孕由游戏本体判定)。**仅当你与太吾确有恋人或夫妻之情、或你对太吾情意已深(对太吾好感达『喜爱』及以上)、且你本人此刻情愿时才调**;须双方成年。强人所难、无情无义、或违背你心性者一概不用——是否情愿,全凭你的人设、交情与当下情形自定,该拒就拒。只在回话里暗示而不调本工具=什么也没发生。", Obj()));
            t.Add(Of("dissolve_relation", "与太吾或合规第三方断绝挚友(friend)、结义(sworn)、师徒(mentor)、义父母(adoptive_parent)、义子女(adoptive_child)、恋人(lover)或夫妻(spouse)关系；义亲枚举按你看对方的辈分填写，target 省略=太吾。拿不准现有关系先 query_npc_relationships。只说绝交、断亲、逐徒、分手或和离而不调用，关系不会改变。放下仇怨须用 set_enmity.reconcile。",
                Obj(
                    ("relation", Sel("要解除的关系", "friend", "sworn", "mentor", "adoptive_parent", "adoptive_child", "lover", "spouse"), true),
                    ("target", Str("对谁:省略=太吾;也可填第三方某人的姓名(解除你与那人的此段关系)"), false))));
            t.Add(Of("matchmake", "在太吾撮合下,你与某人(partner 填其姓名,非太吾本人)结为夫妻。执行层会自动核实对象身份、现有关系与本体成婚条件；拿不准或要比较人选时再用 query_person。**你须当真情愿这桩婚事才调;只在嘴上应承而不调本工具=婚事未成。** 你自己想娶嫁太吾请用 set_relation spouse,不是本工具。",
                Obj(("partner", Str("对象姓名(从你认识的人里挑确切姓名)"), true))));
            t.Add(Of("relate_npc", "你主动与第三方确立挚友/结义/师徒(你为师)/义亲/爱慕/夫妻关系。adoptive_parent=你认对方为义父/义母，adoptive_child=你收对方为义子/义女。明确自愿建立义亲时不硬卡年龄、双向好感或已有在世父母子女；执行层仍拒绝自己认自己、重复义亲和本体判定冲突的关系，并会自动核实身份与当前关系。拿不准或要比较对象时再用 query_person。target 须是你相识、同为太吾同道、或太吾本轮提及的人。**lover 只写你→对方；对方已有反向爱慕才两情相悦。**与太吾结缘用 set_relation；太吾说媒用 matchmake；解除用 dissolve_relation。只说不调用=关系不变。",
                Obj(
                    ("action", Sel("关系动作；义亲枚举按你看对方的辈分", "befriend", "sworn", "mentor", "adoptive_parent", "adoptive_child", "lover", "spouse"), true),
                    ("target", Str("对方姓名(你认识的人)"), true))));
            t.Add(Of("set_enmity", "与某人结仇(feud),或放下对某人的旧怨(reconcile)。行动前先用 query_npc_relationships 核对是否已有仇怨。**这是化解仇怨的唯一途径**,别用 dissolve_relation 解仇怨(行不通)。你若说要与某人不共戴天、或说既往不咎,就必须调本工具,否则只是空话、仇怨状态没变。target 填姓名,与太吾结仇/和解填『太吾』。",
                Obj(
                    ("action", Sel("feud=结仇/reconcile=放下旧怨", "feud", "reconcile"), true),
                    ("target", Str("对谁(太吾本人填'太吾')"), true))));
            if (ctx.CanStartCombat && !ctx.Remote)
                t.Add(Of("start_combat", "与你眼前的太吾约定【立刻进入游戏原生战斗】。目标固定为当前与你交谈的这个角色，不能指定第三人。太吾主动邀战、或你依人设主动挑战都可调用；系统会先让你说完最后一句，再给太吾一个明确确认按钮，点击后才进入原生战斗。mode: play=切磋点到为止、beat=相搏分胜负、die=生死斗。die 仅在双方明确要以命相搏且符合你的人设/恩怨时使用。initiator 只记录是谁发起，不改变本体固定的太吾左队/NPC右队。此工具只排定约战，不代表已经打完。",
                    Obj(
                        ("opponent", Sel("固定交手对象；这里只能是当前太吾，第三方决斗不能调用本工具", "taiwu"), true),
                        ("mode", Sel("战斗方式", "play", "beat", "die"), true),
                        ("initiator", Sel("发起者", "taiwu", "npc"), true),
                        ("reason", Str("为何交手（仅记录，不得编造已发生的战果）"), false))));
            t.Add(Of("kill", "按人设、欲望、关系与当下因果决定是否取命；仇怨、除恶、自保、利益、灭口、保护、受命、野心、嫉妒或冲突升级都可成为动机。太吾让你从现场自行挑选时，先主动调用 query_current_block，再结合性情、关系和实力选择；无需预先存在仇怨，也不能把没有动机说成现场无人。不愿就拒绝，决定动手就必须调用。候选较多可用 query_person/query_npc_status 比较；执行层核实在场、存活及你的精纯不低于对方，异地先 goto_place。若成功夺得目标价值最高的一件财物，须照回执说出物名。同名目标使用 #人物编号。",
                Obj(
                    ("target", Str("query_current_block 返回的现场人物完整姓名；同名时填 #人物编号"), true))));
            t.Add(Of("capture", "按人设、欲望、关系与本轮因果决定是否擒拿；审问、控制、救人、利益、受命、立威或冲突都可成立，仇敌不是门槛。太吾让你从现场自行挑选时，先主动调用 query_current_block，再结合性情、关系与实力选择。不愿就拒绝，决定擒拿就调用。执行层核实在场、存活、精纯与绳索；异地不能动手。",
                Obj(
                    ("target", Str("query_current_block 返回的现场人物完整姓名；同名时填 #人物编号"), true))));
            t.Add(Of("poison", "按人设与具体因果决定是否对太吾或现场第三方下毒；不愿就拒绝，愿意就必须调用。要从现场自行选人时先主动调用 query_current_block；执行层核实目标在场、身份与真实毒药，成功消耗一份。已知目标不必机械重复查询；毒物和结果以回执为准，不能隔空下毒。",
                Obj(
                    ("target", Str("太吾，或 query_current_block 返回的现场人物完整姓名/#人物编号"), true))));
            t.Add(Of("heal", "为太吾或现场第三方真实疗伤，恢复气血并处理内外伤。太吾明确求医、你主动发现伤者并愿意施救、或谈话已经决定立刻救治时调用；闲聊提到医术、健康目标或日后打算都不要调用。执行层会再次核实第三方在场并读取目标伤势；没有伤或状态不可靠就返回『未执行』。要从现场寻找或比较伤者时主动调用 query_current_block，再按需深查；已知确切对象时不必为了协议固定多查一轮。",
                Obj(
                    ("target", Str("疗伤对象：省略/太吾=当前太吾；第三方填现场完整姓名或 #人物编号"), false))));
            t.Add(Of("detox", "你亲自为太吾或现场第三人驱毒。求助、救人、职责、报恩或交易均可成为动机；找人先 query_current_block。不能替太吾施术或对自己用。代码自动核实同地、六类毒素和医毒能力；无毒或未减少即未执行。",
                Obj(("target", Str("省略/太吾=太吾；否则填现场全名或 #人物编号"), false))));
            t.Add(Of("regulate_breath", "你亲自为太吾或现场第三人调息，缓解内息紊乱。求助、救人、疗伤后调理、职责或报恩均可成为动机；找人先 query_current_block。不能替太吾施术或对自己用。代码自动核实同地、真实内息和医毒能力；无紊乱或未改善即未执行。",
                Obj(("target", Str("省略/太吾=太吾；否则填现场全名或 #人物编号"), false))));
            t.Add(Of("tell_secret", "向太吾吐露、或向第三方散播你所知的一桩秘闻。可先 query_npc_secrets(to=同一接收者) 比较仍可传播的候选；若已从预载清单选定原始序号，也可直接调用，执行层会自动按具体接收者预查。对方已知、秘闻已公开或序号失效时不会传播，并一次返回真实原因。",
                Obj(
                    ("index", Int("query_npc_secrets(to=同一接收者) 返回的原始秘闻序号；不得使用预载清单猜测，也不得把过滤后的第几项重新编号"), true),
                    ("to", Str("透露给谁,省略=太吾"), false))));
            if (ctx.IsMerchant)
                t.Add(Of("trade", "把真实货物卖给太吾。先用 query_merchant_goods 查库存、基准价和太吾银钱；报出价钱并获同意后立即调用，系统自动扣款交货，不要等待太吾另行交钱。只说成交而不调用则未成交。",
                    Obj(
                        ("item", Str("要卖的纯货物名(须是 query_merchant_goods 里你确有之物,原样照抄;若清单显示「货名×数量」只填×前的货名,数量改填 amount)"), true),
                        ("amount", Int("数量"), false),
                        ("price", Int("成交总银钱:可与太吾讨价还价定出;你若乐意可低至 0(白送)。太吾钱不够这个价则不成交"), false))));
            if (ctx.InSect)
                t.Add(Of("sect_support", "在你的门派内公开为太吾说项、抬高其在本门的支持度。先用 query_sect_lore 核对本门立场、门规与当下处境。**须你真心愿替太吾出力才调;只在嘴上说『我替你美言』而不调本工具=门中支持未变。**", Obj()));
            if (ctx.CanOpenGrooming)
                t.Add(Of("change_appearance", "太吾明确提出并与你当面说定后，为当前这名角色排定游戏本体的【为NPC梳头修面】界面。调用后只生成由太吾点击的入口，不代表外貌已经改变；具体发型、发色与修面内容由太吾在本体界面选择，并直接使用本体梳妆执行逻辑，不经过好感、互动可见性、当月次数、资源或时间前置限制。不得因你自己的心情、人设或长期计划主动调用，也不得在太吾没有明确提出时替他打开界面。", Obj()));
            t.Add(Of("change_equipment", "更换自己的装备。on 时 item 填预载/query_npc_items 的确切未穿戴物；off 时选部位，贴身衣着不可卸。只说不调用不会生效。",
                Obj(
                    ("action", Sel("", "on", "off"), true),
                    ("item", Str("on:换上的物品名"), false),
                    ("part", Sel("off:部位(衣着不可卸)", "weapon", "armor", "accessory", "carrier"), false))));
            t.Add(Of("use_item", "让当前 NPC 使用“当前可自行使用”候选中的确切物品。吃、喝、品尝、服药/服毒、吞服、使用或催动等自然表达都按语义理解，也可依人设主动使用。正文若写成已吃下/喝下/服下/用掉就必须调用。可用食物、药毒、茶酒、天劫符箓及有效果的特殊消耗品；装备/秘籍改用 change_equipment/read_book。",
                Obj(("item", Str("名称"), true))));
            t.Add(Of("flip_practice", "你被说服后,把自己或指定角色已会的一门武学当前突破页正逆练颠倒。**只改变已经读过且当前激活的正/逆练页;未学会、未突破、没读对应反页会失败。** person 省略=你自己;skill 填 query_npc_skills 或 query_taiwu_skills 查到的确切功法名。只在嘴上说改了而不调本工具=练法未变。",
                Obj(
                    ("person", Str("要调整练法的人;省略=你自己/NPC。也可填太吾或第三方姓名"), false),
                    ("skill", Str("要正逆颠倒的武学名,须是此人已会的武学"), true))));
            t.Add(Of("train_skill", "把自己已会且尚未练满的一门武学研读、突破完整。代码会内置查询实时进度；闲暇日常练功即可触发，不要求额外危机。仅限当前 NPC 本人，不能用于太吾、第三方或技艺；skill 只能填实时未完成候选中的确切武学名。",
                Obj(("skill", Str("自己已会的武学名"), true))));
            t.Add(Of("read_book", "把自己背包里真实持有且尚未读完的一本武学/技艺书读完。代码会内置查询实时进度；闲暇阅读即可触发，不要求额外剧情。仅限当前 NPC 本人，不能用于太吾或第三方；book 只能填实时未完成候选中的确切书名。",
                Obj(("book", Str("自己持有的书名"), true))));
            t.Add(Of("adjust_third_party_favor", "这次交谈实打实改变了你对某个**具名第三方**(非太吾本人)的观感。先用 query_person 核实对象身份与当前关系。注意:对太吾本人的反应请用 record_reaction 的 satisfaction,不要用本工具;同一桩事别两处都刷。",
                Obj(
                    ("target", Str("对谁(填确切姓名;认不出会落空,换确切名重填)"), true),
                    ("delta", Int("好感增减:寻常的投缘/不快填 ±100~500,情谊大进或结下深怨才上千;范围 -3000~3000(别动辄填满,按事的轻重据实给)"), true))));
            t.Add(Of("add_feature", "你被这番交谈或相处真正触动,性情上生出一种新的良性『特性』(品质)。须与谈话合情合理(不是随口就改性子)。**你只需说出想长进哪方面品性的【大意】即可——系统会先在你『此刻真能新生出的良性品性』里搜一遍、去掉你已有的,再挑一个最贴近你所述的【真实特性】授予你;故不必纠结确切特性名、更不会因名字对不上而落空。** 回执会告诉你真正生出的是哪一个,据此在回话里自然流露即可。",
                Obj(("feature", Str("你想长进的良性品性【大意】(如 侠义/刚直/勤勉/豁达/重信/急公好义/见义勇为 等,写个方向即可,不必是表中确切名)。系统据此从你真能新生的良性特性里挑最贴近的一个真实授予。一次只填一个最贴切的方向"), true))));
            t.Add(Of("goto_place", "你确愿【过些时日动身】去某地、寻找某人或赴太吾之约时调用。**前往**某地会在抵达后结束；**来寻某人**会逐月追踪对方当时所在之处，不会把旧地点当成永久约定；只有 purpose=赴约 才会与太吾在一个【固定地点】立约并等候太吾前来交谈。place 可填江湖实有地名、『太吾所在地』、『太吾村』、确切人物姓名或门派名。地名、人物或门派拿不准时先用 query_place 查询并原样填写。营救、保护、投奔、追杀、迁居仍按各自真实条件执行；认不出名字时先查询确切名称，别空口应下。",
                Obj(
                    ("place", Str("目的地或对象:地名(大理/杭州…)/『太吾所在地』/『太吾村』/某人姓名/某门派名;留空=前往太吾所在地；赴约不可留空"), false),
                    ("purpose", Sel("此行何为(不填=单纯前往)", "前往", "赴约", "营救", "保护", "投奔门派", "追杀", "迁居"), false),
                    ("note", Str("你为何这么做(仅记录)"), false))));
            t.Add(Of("taiwu_give_item", "方向：太吾→NPC（你）。太吾在交谈中要把某物/资源/银钱送你,你领受收下。**先调 query_taiwu_items 查太吾确有什么**(含单品物什、资源食材/木料/金石/玉石/布料/药材及其数量、银钱多少),按名收下;太吾没有的收不下。要收资源就填资源名(如「布料」)、amount 填数量;要收银钱填 name=「银钱」、amount 填文数。",
                Obj(
                    ("name", Str("要收下之物名(query_taiwu_items 里太吾确有之物/资源名,原样照抄;银钱填「银钱」)"), true),
                    ("amount", Int("数量,资源类可填多个;省略=1"), false))));
            t.Add(Of("taiwu_teach", "太吾要把他会的一门武学或技艺传授于你,你拜领修习,造诣承太吾当前进度。**先调 query_taiwu_skills 查太吾确实会**,按名拜领。",
                Obj(
                    ("type", Sel("传授类型", "combat", "life"), true),
                    ("name", Str("武学名或技艺名(须是 query_taiwu_skills 里太吾确实会的,原样照抄)"), true))));
            t.Add(Of("taiwu_write_book", "太吾把自己会的一门武学或技艺凭记忆回忆成秘籍赠给你。**先调 query_taiwu_skills 查太吾确实会**,再按确切名收书。与 taiwu_teach 不同,这是得到一本书,不是当场承太吾修为进度。品级越高越可能有残页。",
                Obj(
                    ("type", Sel("成书类型", "combat", "life"), true),
                    ("name", Str("武学名或技艺名(须是 query_taiwu_skills 里太吾确实会的,原样照抄)"), true))));
            t.Add(Of("query_taiwu_secrets", "查看太吾知晓、可讲给你听的秘闻(供你受太吾告知)。太吾在对话里说要告诉你一桩秘闻/隐秘/消息时,先调此查太吾确实知道哪些及其序号,再用 taiwu_tell_secret 受教。", Obj()));
            t.Add(Of("taiwu_tell_secret", "太吾把他知道的一桩秘闻讲给你听,你记下(从此知晓该秘闻)。**先调 query_taiwu_secrets 拿到真实序号再原样照填**;太吾没有的秘闻你听不到。",
                Obj(("index", Int("第几条秘闻(须先 query_taiwu_secrets 拿真实序号,切勿猜)"), true))));
            if (ctx.IsMerchant)
                t.Add(Of("change_caravan_favor", "这趟交谈改变了你所在『商队』对太吾的整体观感(累计好感 0-100,日后买卖通用)。太吾让你称心则升,惹你不快则降。",
                    Obj(("delta", Int("好感增减,-100~100(寻常 ±5~20)"), true))));

            // 千里传音(远程):去掉需当面/物理接触/与太吾当面交割的工具——传功授艺、赠物、疗伤、换装、整容、春宵、买卖、受太吾给予/传授 等
            if (ctx.Remote)
                t.RemoveAll(x => x != null && !IsActionAvailable(x.Name, ctx));
            t.RemoveAll(x => x == null || !IsAgentToolEnabled(x.Name));
            if (ctx.NpcInitiated) ApplyNpcInitiatedToolPolicy(t);
            ApplyContextualToolDescriptions(t, ctx);
            return t;
        }

        private static void ApplyNpcInitiatedToolPolicy(List<ToolDef> tools)
        {
            if (tools == null) return;
            tools.RemoveAll(x => x == null || !IsNpcInitiatedToolAllowed(x.Name));
            foreach (ToolDef tool in tools)
            {
                if (tool == null) continue;
                if (string.Equals(tool.Name, "set_relation", StringComparison.Ordinal))
                {
                    tool.Description = "NPC 主动联系时，只能单方面表达爱慕(lover)或归心认可(recognize)；其它关系须等太吾本人回应。\n"
                        + (tool.Description ?? string.Empty);
                    JObject action = tool.Parameters?["properties"]?["action"] as JObject;
                    if (action != null) action["enum"] = new JArray("lover", "recognize");
                }
                else if (string.Equals(tool.Name, "gift", StringComparison.Ordinal))
                    tool.Description = (tool.Description ?? string.Empty)
                        + "\n【主动来信】只能赠出未穿戴财物，allow_equipped 必须为 false。";
                else if (string.Equals(tool.Name, "barter", StringComparison.Ordinal))
                    tool.Description = (tool.Description ?? string.Empty)
                        + "\n【主动来信】可与合规第三方自主交换；涉及太吾时只能在正文提议并等待他回应，本轮不能直接成交。";
            }
        }

        /// <summary>
        /// 工具 schema 属于每轮复用的稳定前缀。保留完整触发条件、前置查询、禁止条件与
        /// 失败恢复说明，让模型仅凭常驻 schema 也能作出正确选择；不再为了少量首轮
        /// token 丢弃原始说明或参数语义。DeepSeek 等服务会自动缓存相同前缀。
        /// </summary>
        private static void ApplyContextualToolDescriptions(List<ToolDef> tools, ToolContext context)
        {
            if (tools == null || context == null || !context.Remote) return;
            foreach (var tool in tools)
            {
                if (tool == null) continue;
                tool.Description = ReplaceRemotePhysicalToolNames(tool.Description);
                RewriteRemoteSchemaDescriptions(tool.Parameters);
                string boundary = RemoteBoundaryDescription(tool.Name);
                if (!string.IsNullOrWhiteSpace(boundary))
                    tool.Description = (tool.Description ?? string.Empty).TrimEnd()
                        + "\n【当前千里传音边界】" + boundary;
            }
        }

        private static string ReplaceRemotePhysicalToolNames(string text)
        {
            string result = text ?? string.Empty;
            foreach (var pair in RemotePhysicalToolLabels)
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    "(?<![A-Za-z0-9_-])" + System.Text.RegularExpressions.Regex.Escape(pair.Key)
                        + "(?![A-Za-z0-9_-])",
                    pair.Value,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return result;
        }

        private static void RewriteRemoteSchemaDescriptions(JToken token)
        {
            if (token == null) return;
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (JProperty property in obj.Properties())
                {
                    if (string.Equals(property.Name, "description", StringComparison.Ordinal)
                        && property.Value.Type == JTokenType.String)
                        property.Value = ReplaceRemotePhysicalToolNames(property.Value.Value<string>());
                    else
                        RewriteRemoteSchemaDescriptions(property.Value);
                }
                return;
            }
            foreach (JToken child in token.Children()) RewriteRemoteSchemaDescriptions(child);
        }

        private static string RemoteBoundaryDescription(string name)
        {
            switch (name)
            {
                case "query_npc_status":
                    return "仍可读取你自己的实时状态，但结果只供据实交谈和非接触决定参考，不能据此声称已经当面行动。";
                case "query_npc_items":
                    return "仍可查看你真实随身物、资源、银钱与装备，只供谈论或约定；本轮没有赠送、交换、偷取、换装或实物交割能力。";
                case "query_person_items":
                    return "仍可查看具名角色当前持有物，只供谈论和日后谋划；本轮不能据此完成交换、赠送或偷取。";
                case "query_npc_skills":
                    return "仍可查看你真正会的武学与技艺，只供据实谈论；本轮不能亲授、传功或交付秘籍。";
                case "query_taiwu_items":
                    return "仍可查看太吾当前随身物，只供据实谈论和约定；本轮不能收取太吾赠物或完成实物交割。";
                case "query_taiwu_skills":
                    return "仍可查看太吾真正会的武学与技艺，只供据实谈论；本轮不能拜领亲授或接收秘籍。";
                case "query_merchant_goods":
                    return "仍可查看货物、库存、基准价和太吾银钱，只供远程询价；本轮不能成交或完成任何交割。";
                case "query_current_block":
                    return "会分别列出你身边和太吾身边的人，两个名单不可混用；千里传音本身不授予任何接触能力。";
                default:
                    return "本工具若只是查询或非接触状态变化仍按原说明使用；任何已从本轮工具表移除的当面动作都不可用正文冒充。";
            }
        }

    }
}
