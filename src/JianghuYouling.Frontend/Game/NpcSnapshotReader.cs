// NPC 底座只读快照(画像底座 + 记忆经历)。全部走异步领域方法 AsyncCall:
// requestHandler 传 null,在协程里用 done 标志 + 超时兜底。
using System;
using System.Collections;
using System.Collections.Generic;
using Config;
using Game.Components.Character.LifeRecord;     // RenderedRecordData
using Game.Components.SortAndFilter.Secret;     // SecretSortAndFilterData
using GameData.Domains.Character;
using GameData.Domains.Character.Display;
using GameData.Domains.Information;
using GameData.Domains.Item;                    // ItemKey, ItemTemplateHelper(赠物候选)
using GameData.Domains.LifeRecord;
using GameData.Serializer;
using GameData.Utilities;                        // RawDataPool, InformationUtils
using UnityEngine;
using JianghuYouling.Effects;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    public sealed class NpcSnapshot
    {
        public int NpcId, TaiwuId, CurrentDate;
        public bool DisplayLoaded; // CharacterDisplayData 权威底座是否真正反序列化成功
        // 最新版 CharacterDisplayData 的权威字段。TemplateId 用于按配置 ID 绑定特殊人设；
        // CreatingType==1 才有游戏原生戒心机制。
        public short CharacterTemplateId;
        public byte CreatingType;
        // 实时:眼前太吾本人信息 + 当下时令(让 NPC 知道和谁说话、此刻何时;纯只读)
        public string TaiwuInfoText, WorldTimeText;
        public string TaiwuLocationText; // 太吾当前所在地；保存玩家发言的说话者位置
        public string TaiwuName, TaiwuGender;
        public bool NpcAdoresTaiwu, TaiwuAdoresNpc, MutualLoverWithTaiwu;
        // 画像底座
        public string Name, Gender, Behavior /*立场*/, OrgFullTitle /*门派头衔*/, GradeLevel /*九品..一品*/, GradeName /*品级别称*/;
        public string SexualOrientation;   // 本体 GetBisexual：异性向 / 同性与异性皆可；画像生成只用权威读取
        public bool SexualOrientationLoaded;
        // 门派设定/故事/背景(门派NPC人设注入 + query_sect_lore 查详情;直读 Config.Organization)
        public sbyte OrgTemplateId; public bool IsSect;
        public string SectName, SectDesc /*门派简介*/, SectExtra /*门派风格理念*/, SectVow /*入派誓言*/, SectStory /*门派主线故事*/;
        public string LocationText;   // NPC 当前所在地(区域·地块名),供对话/查熟人/AI事件用
        // 最新本体把年龄拆为身龄(PhysiologicalAge，外观/社交呈现)与命龄(ActualAge，实际生存年数)。
        // Age 保留为兼容入口，但语义固定为身龄；新代码应优先使用两个明确字段。
        public int PhysiologicalAge, ActualAge, GradeCode;
        public int Charm = -1; // CharacterDisplayData.Charm：含本体加成的实时魅力；负值为未知
        public int Age { get => PhysiologicalAge; set => PhysiologicalAge = value; }
        public sbyte GameAgeGroup = -1;   // 本体 AgeGroup.GetAgeGroup(CurrAge)：0婴儿、1儿童、2成人
        public sbyte ConsummateLevel = -1;   // 武学精纯 0..18(杀/绑硬门槛:行凶者不得低于对方，相等可成;-1=未读到)
        public bool HasRope;                 // 随身是否备有真绳子(可绑缚擒人)——预载给提示词,免先口头答应绑人再被 no_rope 打回
        public bool HasPoison;               // 随身是否备有毒药(可下毒)
        public sbyte[] Personalities = new sbyte[7];   // 七元赋性 0..100
        public List<string> Features = new List<string>();   // 人物特性/秉性(勇敢/仗义/嗜杀…),NPC 自知,注入对话
        public short[] CurMainAttr, MaxMainAttr;        // 主属性 6 维
        // 关系/好感
        public ushort RelationFlag; public string Relation;
        public short Favor; public string FavorLevel;
        // 当前状态
        public bool CompletelyInfected;   // 相枢已完全感染
        public short QiDisorderChange;    // 气机紊乱(心魔)每月变化,正=恶化
        public sbyte Happiness;           // 当前心情；提示词按实时值使用，不固化进长期画像
        public string FameText;           // 当前江湖名誉/侠名（FameType 的本体权威显示名）
        public short Health, LeftMaxHealth;
        // 伤势必须单独从 GetCharacterInjuryDisplayData 权威读取；CharacterDisplayData
        // 只有气血，没有各部位伤势。-1 表示未可靠读取，不能当成“无伤”。
        public int InjuryMarkCount = -1;
        public bool InjuryStateLoaded;
        public bool IsDead;
        public bool IsCaptive;   // #19 被太吾擒下、关在据点牢中(ExternalRelationType.CapturedInSettlementPrison=32);在押=物理在场,对话走当面而非千里传音
        public int Silver;       // 当前银钱(ResourceType.Money=6)，供赠银执行前置与提示事实使用
        // 记忆经历
        public List<(int date, string type, string text)> LifeRecords = new List<(int, string, string)>();
        public List<(int date, string type, string text)> Secrets = new List<(int, string, string)>();
        // 可吐露给太吾的秘闻(带 id,排除广播态;供"对话套出秘闻"用)
        public List<SecretRef> ShareableSecrets = new List<SecretRef>();
        // 随身可赠之物(供 AI 决定送哪件;带稳定 ItemKey,对话层按名匹配后转移)
        public List<GiftableItem> GiftableItems = new List<GiftableItem>();
        // 换装前置事实必须区分“背包里可换上的装备”和“当前已经穿着的装备”。
        // GiftableItems 同时含资源、熟食和已穿戴物，不能直接拿来证明某件物品可执行 equip。
        public List<GiftableItem> EquipableInventoryItems = new List<GiftableItem>();
        public List<string> EquippedItemNames = new List<string>();
        public HashSet<string> EquippedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool InventoryLoaded, EquipmentLoaded, FoodLoaded, HoldingsLoaded;
        // 月度 Agent 按权威现场筛选工具时必须区分“确认没有”与“读取失败”。
        // 读取失败不能把空列表误当成没有技能/秘闻，从而错误隐藏可用动作。
        public bool SecretsLoaded, CombatSkillsLoaded, LifeSkillsLoaded;
        // 与本体 SetActivePage 同条件的可颠倒正逆练资格。Loaded=false 表示读取失败/未知，
        // 不能把空集合误当成确认没有；Loaded=true 且空才表示确实没有可颠倒功法。
        public bool FlipPracticeEligibilityLoaded;
        public HashSet<short> FlippableCombatSkillIds = new HashSet<short>();
        // NPC 闲暇修炼/读书只向模型展示尚未完成的权威候选。查询失败与确认无候选分开，
        // 执行前还会再次刷新，避免把已满进度的武学或书籍重复算作本月行动。
        public bool StudyProgressLoaded;
        public HashSet<short> IncompleteTrainingSkillIds = new HashSet<short>();
        public List<string> UnreadBookNames = new List<string>();
        // 与后端 use_item 共享本体 1.0.72 判定的实时自用消耗品；查询失败与确认无候选分开。
        public bool UsableItemsLoaded;
        public List<string> UsableItemNames = new List<string>();
        // 所习武学(可传授;供 AI 决定传哪门,带 templateId)
        public List<LearnableSkill> LearnableSkills = new List<LearnableSkill>();
        // 所习技艺(生活技能,可传授;同上,排除太吾已学)
        public List<LearnableSkill> LearnableLifeSkills = new List<LearnableSkill>();
        public bool IsMerchant;   // 是否商人(决定是否带 query_merchant_goods / trade 工具)
        public List<GiftableItem> MerchantGoods = new List<GiftableItem>();   // 真·行商货架(MerchantData.GoodsList);商人售货优先卖这个、非随身背包
        public int MerchantTemplateId = -1;   // 商人模板 id(供后端在货未生成时用 GenerateGoods 现生成)
        public List<string> Errors = new List<string>();
    }

    /// <summary>一条可分享的秘闻引用(带引擎 id,供让太吾知晓)。</summary>
    public sealed class SecretRef
    {
        public SecretInformationId Id;
        public string Text;
        public int SourceCharId;
        // 在讲述者完整可吐露清单中的稳定 1-based 序号。按接收者过滤后仍保留，
        // 避免把过滤列表重新编号而令后续 tell_secret 说成另一桩秘闻。
        public int DisplayIndex;
    }

    /// <summary>一件可赠物品(名字 + 引擎 ItemKey + 现有数量,供 AI 选送哪件、并据实告知数量)。</summary>
    public sealed class GiftableItem
    {
        public string Name;
        public ItemKey Key;
        public int Count;
        // 自主赠礼只能消耗未穿戴数量；同名物既在背包又在装备槽时仍可赠背包副本。
        public int NonEquippedCount;
        public int EquippedCount;
        // 仅商人货架使用；把只读查询看到的角色店铺/商队来源绑定到随后的成交。
        public int MerchantOwnerType = -1;
        public int MerchantOwnerId = -1;
    }

    /// <summary>一门 NPC 所习武学(名字 + templateId,供 AI 选传哪门)。</summary>
    public sealed class LearnableSkill { public string Name; public short TemplateId; }

    /// <summary>太吾自身随身物 + 所习武学/技艺(供"太吾送 NPC 物品 / 传功传艺",惰性取)。</summary>
    public sealed class TaiwuHoldings
    {
        public List<GiftableItem> Items = new List<GiftableItem>();
        public List<LearnableSkill> CombatSkills = new List<LearnableSkill>();
        public List<LearnableSkill> LifeSkills = new List<LearnableSkill>();
        // 太吾的资源储备(名称→数量:食材/木料/金石/玉石/布料/药材),供 query_taiwu_items 据实告知 NPC
        public List<KeyValuePair<string, int>> Resources = new List<KeyValuePair<string, int>>();
    }

    public static class NpcSnapshotReader
    {
        const float TIMEOUT = 10f;

        /// <summary>
        /// One-RPC display/status base for short-lived event queries. Unlike Fetch(lite:true),
        /// this deliberately does not read Qi disorder, life records, secrets, Taiwu data or
        /// directional Taiwu relations. Requested sections are loaded separately by the caller.
        /// </summary>
        public static IEnumerator FetchDisplayOnly(int npcId, Action<NpcSnapshot> done,
            Func<bool> stillCurrent = null)
        {
            var s = new NpcSnapshot { NpcId = npcId };
            if (npcId < 0 || !StillCurrent(stillCurrent))
            {
                done?.Invoke(StillCurrent(stillCurrent) ? s : null);
                yield break;
            }
            CharacterDisplayData dd = null;
            bool displayDone = false;
            CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, npcId,
                (offset, pool) =>
                {
                    try { Serializer.Deserialize(pool, offset, ref dd); }
                    catch (Exception e) { s.Errors.Add("display: " + e.GetType().Name); }
                    finally { displayDone = true; }
                });
            yield return WaitDone(() => displayDone, stillCurrent);
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            if (dd == null) { s.Errors.Add("display null"); done?.Invoke(s); yield break; }

            s.DisplayLoaded = true;
            // 永久副本的实体模板必须是本体合法智能人物；特殊人设匹配仍应认原剧情人物。
            // 这里只替换“身份模板”字段，年龄、属性、位置、装备等实时事实继续取新实体。
            s.CharacterTemplateId = ResolveIdentityTemplateId(npcId, dd.TemplateId);
            s.CreatingType = dd.CreatingType;
            s.Gender = dd.Gender == 1 ? "男" : dd.Gender == 0 ? "女" : "?";
            s.PhysiologicalAge = dd.PhysiologicalAge;
            s.ActualAge = dd.ActualAge;
            s.Charm = dd.Charm;
#pragma warning disable CS0618
            s.GameAgeGroup = GameData.Domains.Character.AgeGroup.GetAgeGroup(dd.CurrAge);
#pragma warning restore CS0618
            s.ConsummateLevel = dd.ConsummateLevel;
            s.Behavior = BehaviorName(dd.BehaviorType);
            s.Happiness = dd.Happiness;
            s.FameText = FameText(dd);
            s.Health = dd.Health;
            s.LeftMaxHealth = dd.LeftMaxHealth;
            s.CompletelyInfected = dd.CompletelyInfected;
            s.IsDead = dd.AliveState == 1 || dd.AliveState == 2;
            s.LocationText = ResolveLocationText(dd.Location)
                ?? "去向不明（游戏当前未提供有效地点）";
            ushort relation = dd.RelationToTaiwu == ushort.MaxValue
                ? (ushort)0 : dd.RelationToTaiwu;
            s.RelationFlag = (ushort)(relation & ~16384);
            s.Relation = RelationName(relation, false, null);
            s.Favor = dd.FavorabilityToTaiwu == short.MinValue
                ? (short)0 : dd.FavorabilityToTaiwu;
            s.FavorLevel = FavorLevelName(s.Favor);
            try
            {
                if (dd.FeatureIds != null)
                    foreach (var featureId in dd.FeatureIds)
                    {
                        if (s.Features.Count >= 12) break;
                        var feature = Config.CharacterFeature.Instance[featureId];
                        if (feature == null || feature.Hidden) continue;
                        string name = Clean(feature.Name);
                        if (!string.IsNullOrWhiteSpace(name)
                            && !s.Features.Contains(name)) s.Features.Add(name);
                    }
            }
            catch { }
            s.Name = "NPC#" + dd.CharacterId;
            try { s.Name = NameCenter.GetMonasticTitleOrDisplayName(dd, false) ?? s.Name; }
            catch { }
            done?.Invoke(s);
        }

        public static IEnumerator Fetch(int npcId, Action<NpcSnapshot> done, bool lite = false,
            Func<bool> stillCurrent = null)
        {
            var s = new NpcSnapshot { NpcId = npcId };
            var bgd = SingletonObject.getInstance<BasicGameData>();
            s.TaiwuId = (bgd != null) ? bgd.TaiwuCharId : -1;
            s.CurrentDate = (bgd != null) ? bgd.CurrDate : 0;
            if (npcId < 0) { s.Errors.Add("invalid npcId"); done?.Invoke(s); yield break; }
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }

            // ===== 1) 画像底座:一次 GetCharacterDisplayData 全拿 =====
            // 本体的 RelationToTaiwu 只有“NＰＣ→太吾”一个方向；恋人必须是双方都带
            // Adored(16384)。关系方向查询与画像 RPC 同时发起，不增加关键路径等待。
            MonthlyActionPreflight taiwuRelation = null;
            bool taiwuRelationDone = s.TaiwuId <= 0 || s.TaiwuId == npcId;
            if (!taiwuRelationDone)
                JianghuYouling.Effects.EffectHandler.QueryMonthlyActionPreflight(npcId, s.TaiwuId,
                    value => { taiwuRelation = value; taiwuRelationDone = true; });
            bool personaTraitsDone = false;
            JianghuYouling.Effects.EffectHandler.QueryPersonaTraits(npcId, (ok, bisexual) =>
            {
                s.SexualOrientationLoaded = ok;
                s.SexualOrientation = ok
                    ? (bisexual ? "兼有同性与异性取向" : "异性向") : null;
                personaTraitsDone = true;
            });
            CharacterDisplayData dd = null; bool ddDone = false;
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, npcId, (offset, pool) =>
            {
                try { Serializer.Deserialize(pool, offset, ref dd); }
                catch (Exception e) { s.Errors.Add("display: " + e.GetType().Name); }
                finally { ddDone = true; }
            });
            yield return WaitDone(() => ddDone, stillCurrent);
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            if (dd == null) { s.Errors.Add("display null"); done?.Invoke(s); yield break; }
            s.DisplayLoaded = true;

            // 与 FetchDisplayOnly 保持一致：只让人设目录识别原固定模板，不能把原模板的
            // 缺失/特殊字段重新灌进合法实体快照。
            s.CharacterTemplateId = ResolveIdentityTemplateId(npcId, dd.TemplateId);
            s.CreatingType = dd.CreatingType;
            s.Gender = dd.Gender == 1 ? "男" : dd.Gender == 0 ? "女" : "?";
            s.PhysiologicalAge = dd.PhysiologicalAge;
            s.ActualAge = dd.ActualAge;
            s.Charm = dd.Charm;
#pragma warning disable CS0618 // 本体 Character.GetAgeGroup() 明确使用 CurrAge；这里是启动 Agent 前的同语义二次校验。
            s.GameAgeGroup = GameData.Domains.Character.AgeGroup.GetAgeGroup(dd.CurrAge);
#pragma warning restore CS0618
            s.ConsummateLevel = dd.ConsummateLevel;   // 武学精纯,已在画像里反序列化好,直接取;供杀/绑前自判能否得手
            s.Behavior = BehaviorName(dd.BehaviorType);
            s.Happiness = dd.Happiness;
            s.FameText = FameText(dd);
            s.Health = dd.Health; s.LeftMaxHealth = dd.LeftMaxHealth;
            s.CompletelyInfected = dd.CompletelyInfected;
            s.IsDead = dd.AliveState == 1 || dd.AliveState == 2;
            s.IsCaptive = false;   // 权威判定改走后端 captive_state(见下,非 lite 才查):bit32 只表"某门派牢中"、不证是太吾,且漏掉绳缚俘虏(太吾 Capture 的产物)
            s.LocationText = ResolveLocationText(dd.Location) ?? "去向不明（游戏当前未提供有效地点）";   // 每轮都显式注入；取不到也不静默省略
            // 游戏对"无特殊关系"返回 ushort.MaxValue(65535=RelationType.Invalid,全位为1);归一化为 0,
            // 否则下游位掩码(含 Arbiter 关系闸)会把它误读成"同时是父母/子女/夫妻/师徒/恋人/仇敌"。
            ushort rawRelationToTaiwu = (dd.RelationToTaiwu == ushort.MaxValue) ? (ushort)0 : dd.RelationToTaiwu;
            s.RelationFlag = rawRelationToTaiwu;
            // 单向爱慕绝不能先按“恋人”写进默认上下文或 Arbiter 状态；到 Fetch 结束时
            // 若并行的双向查询已返回，再用权威方向覆盖。
            s.RelationFlag = (ushort)(s.RelationFlag & ~16384);
            s.Relation = RelationName(rawRelationToTaiwu,
                (rawRelationToTaiwu & 16384) != 0 ? (bool?)true : false, null);
            // 游戏对"从未与太吾打过交道"的人,GetCharacterDisplayData 把 FavorabilityToTaiwu 置为 short.MinValue(-32768)哨兵;
            // 真实好感钳在 [-30000, 上限],绝不到 -32768。若把哨兵直接喂 FavorLevelName 会被误判成"血仇"——
            // 避免把素昧平生的路人误读成血仇或亲近关系。归一化为 0(陌路/素未谋面),与关系 65535 哨兵同理。
            short favRaw = dd.FavorabilityToTaiwu == short.MinValue ? (short)0 : dd.FavorabilityToTaiwu;
            s.Favor = favRaw;
            s.FavorLevel = FavorLevelName(favRaw);
            for (int i = 0; i < 7; i++) s.Personalities[i] = dd.Personalities[i];
            // 人物特性(勇敢/仗义/嗜杀…):NPC 自知其秉性,据实注入。跳过隐藏特性,上限若干条防过长。
            try
            {
                if (dd.FeatureIds != null)
                    foreach (var fid in dd.FeatureIds)
                    {
                        if (s.Features.Count >= 12) break;
                        try
                        {
                            var fi = Config.CharacterFeature.Instance[fid];
                            if (fi == null || fi.Hidden) continue;
                            string fn = Clean(fi.Name);
                            if (!string.IsNullOrWhiteSpace(fn) && !s.Features.Contains(fn)) s.Features.Add(fn);
                        }
                        catch { }
                    }
            }
            catch { }
            s.Name = "NPC#" + dd.CharacterId;
            try { s.Name = NameCenter.GetMonasticTitleOrDisplayName(dd, false) ?? s.Name; } catch { }
            s.GradeCode = dd.OrgInfo.Grade; s.GradeLevel = GradeName(dd.OrgInfo.Grade);
            s.OrgFullTitle = "无门派"; s.GradeName = "";
            if (dd.OrgInfo.OrgTemplateId > 0)
            {
                // OrgInfo.ToString()/GetOrgMemberConfig() 内部按 Grade 索引门派成员配置,Grade 越界/配置缺失会抛异常;
                // 包 try/catch 回退,避免门派渲染失败拖垮整个 NPC 快照(属性/记忆/秘闻/赠物/武学全丢)。
                try
                {
                    s.OrgFullTitle = dd.OrgInfo.ToString();
                    var mc = dd.OrgInfo.GetOrgMemberConfig();
                    s.GradeName = mc?.GradeName ?? "";
                }
                catch (Exception e) { s.OrgFullTitle = "无门派"; s.GradeName = ""; s.Errors.Add("org: " + e.GetType().Name); }
            }
            // 门派设定/故事/背景:直读 Config.Organization 静态表(前端可直接访问,无需 RPC)
            s.OrgTemplateId = dd.OrgInfo.OrgTemplateId;
            if (dd.OrgInfo.OrgTemplateId > 0)
            {
                try
                {
                    var org = Config.Organization.Instance[dd.OrgInfo.OrgTemplateId];
                    if (org != null)
                    {
                        s.SectName = Clean(org.Name); s.IsSect = org.IsSect;
                        s.SectDesc = Clean(org.Desc); s.SectExtra = Clean(org.OrganizationExtraDesc); s.SectVow = Clean(org.VowSpecialHint);
                        if (org.IsSect && org.SectMainStory != null) s.SectStory = Clean(org.SectMainStory.UnlockStoryDesc);
                    }
                }
                catch (Exception e) { s.Errors.Add("sect: " + e.GetType().Name); }
            }

            // 死者/已移除角色守卫:下面 GetCharacterAttributeDisplayData / GetChangeOfQiDisorder 等用的是游戏内【不容错】的
            // _objects 查找(GetElement_Objects),对已不在角色域的人会抛 KeyNotFound、直接搞崩后端进程(整局数据进程 Disconnect)。
            // 死者(GetCharacterDisplayData 仍容错返回其底座)就此打住:只回姓名/画像/已死标记,跳过所有实时取数。
            if (s.IsDead)
            {
                if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
                yield return FetchTaiwuAndTime(s, stillCurrent);   // 太吾侧信息无碍,仍补上,供"与已故之人"的只读/回顾场景
                if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
                float deadRelationDeadline = Time.unscaledTime
                    + JianghuYouling.Effects.EffectHandler.ReadOnlyQueryWaitSeconds;
                while (!taiwuRelationDone && Time.unscaledTime < deadRelationDeadline
                    && StillCurrent(stillCurrent)) yield return null;
                while (!personaTraitsDone && Time.unscaledTime < deadRelationDeadline
                    && StillCurrent(stillCurrent)) yield return null;
                if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
                ApplyDirectionalAdoration(s, rawRelationToTaiwu, taiwuRelationDone ? taiwuRelation : null);
                done?.Invoke(s); yield break;
            }

            // ===== 2) 主属性(底座不带,单独读) =====  lite 模式跳过主属性,省一次 RPC
            if (!lite)
            {
                CharacterAttributeDisplayData attr = null; bool attrDone = false;
                if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
                CharacterDomainMethod.AsyncCall.GetCharacterAttributeDisplayData(null, npcId, (offset, pool) =>
                {
                    try { Serializer.Deserialize(pool, offset, ref attr); }
                    catch (Exception e) { s.Errors.Add("attr: " + e.GetType().Name); }
                    finally { attrDone = true; }
                });
                yield return WaitDone(() => attrDone, stillCurrent);
                if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
                if (attr != null)
                {
                    s.CurMainAttr = new short[6]; s.MaxMainAttr = new short[6];
                    for (int i = 0; i < 6; i++) { s.CurMainAttr[i] = attr.CurMainAttributes[i]; s.MaxMainAttr[i] = attr.MaxMainAttributes[i]; }
                }
            }

            // ===== 3) 气机紊乱(心魔)每月变化,返回 short =====
            short qi = 0; bool qiDone = false;
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            CharacterDomainMethod.AsyncCall.GetChangeOfQiDisorder(null, npcId, (offset, pool) =>
            {
                try { Serializer.Deserialize(pool, offset, ref qi); }
                catch (Exception e) { s.Errors.Add("qi: " + e.GetType().Name); }
                finally { qiDone = true; }
            });
            yield return WaitDone(() => qiDone, stillCurrent);
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            s.QiDisorderChange = qi;

            // ===== 4) 记忆经历:一生记录 + 秘闻(画像初遇蒸馏要用,留作每轮预取)=====
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            yield return FetchLifeRecords(npcId, s, stillCurrent);
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            yield return FetchSecrets(npcId, s.CurrentDate, s, stillCurrent);
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            // 可赠之物 / 可传武学技艺仍作为独立权威切片读取，不参与工具 schema 的增删；
            // 商人现场标志在完整快照中读取，唯一用于加入商人专属工具。
            if (!lite)
            {
                yield return FetchMerchant(npcId, s, stillCurrent);   // 仅判定 IsMerchant(决定工具清单);lite 轻量快照不需要,省一次 RPC
                if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            }
            if (!lite)
            {
                bool cap = false, capDone = false;
                if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
                JianghuYouling.Effects.EffectHandler.QueryCaptiveByTaiwu(npcId, b => { cap = b; capDone = true; });
                yield return WaitDone(() => capDone, stillCurrent);
                if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
                s.IsCaptive = cap;
            }   // 是否太吾阶下囚(对话注入囹圄语境)
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            yield return FetchTaiwuAndTime(s, stillCurrent);
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }

            float relationDeadline = Time.unscaledTime
                + JianghuYouling.Effects.EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!taiwuRelationDone && Time.unscaledTime < relationDeadline
                && StillCurrent(stillCurrent)) yield return null;
            while (!personaTraitsDone && Time.unscaledTime < relationDeadline
                && StillCurrent(stillCurrent)) yield return null;
            if (!StillCurrent(stillCurrent)) { done?.Invoke(null); yield break; }
            ApplyDirectionalAdoration(s, rawRelationToTaiwu, taiwuRelationDone ? taiwuRelation : null);
            done?.Invoke(s);
        }

        // 太吾本人信息（包括性别）+ 当下时令。每次 Agent 调用前都从 CharacterDisplayData
        // 实时只读，不依赖人物画像、人设或旧聊天，单聊/群聊/NPC 主动消息共用此快照链路。
        // CurrentDate 是"月计数"(每过月+1,非天数):年=cd/12+1,月内=cd%12(0..11)。
        static IEnumerator FetchTaiwuAndTime(NpcSnapshot s, Func<bool> stillCurrent = null)
        {
            int cd = s.CurrentDate < 0 ? 0 : s.CurrentDate;
            int year = cd / 12 + 1, m = cd % 12;
            // 季节须与游戏 Season 配置一致(0基月份):春={1,2,3} 夏={4,5,6} 秋={7,8,9} 冬={10,11,0}。
            // 不可写成 m<3春/m<6夏/m<9秋,那样 0/3/6/9 四个月边界全错(冬误作春、春误作夏…)。
            string season = (m == 0 || m >= 10) ? "冬" : m <= 3 ? "春" : m <= 6 ? "夏" : "秋";
            s.WorldTimeText = "第" + year + "年 " + season + "(" + (m + 1) + "月)";

            if (s.TaiwuId <= 0) yield break;
            CharacterDisplayData tdd = null; bool tDone = false;
            if (!StillCurrent(stillCurrent)) yield break;
            CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, s.TaiwuId, (offset, pool) =>
            {
                try { Serializer.Deserialize(pool, offset, ref tdd); }
                catch (Exception e) { s.Errors.Add("taiwu: " + e.GetType().Name); }
                finally { tDone = true; }
            });
            yield return WaitDone(() => tDone, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            if (tdd == null) yield break;

            string tName = "太吾"; try { tName = NameCenter.GetMonasticTitleOrDisplayName(tdd, true) ?? "太吾"; } catch { }
            string tGender = tdd.Gender == 1 ? "男" : tdd.Gender == 0 ? "女" : "?";
            s.TaiwuName = tName;
            s.TaiwuGender = tGender;
            s.TaiwuLocationText = ResolveLocationText(tdd.Location)
                ?? "去向不明（游戏当前未提供有效地点）";
            string tOrg = ""; try { if (tdd.OrgInfo.OrgTemplateId > 0) tOrg = dd_OrgString(tdd); } catch { }
            string tFame = ""; try { if (tdd.FameType >= 0 || tdd.FameType == -2) tFame = CommonUtils.GetFameString(tdd.FameType) ?? ""; } catch { }

            var sb = new System.Text.StringBuilder();
            sb.Append(tName).Append(" · ").Append(tGender)
              .Append(" · 身龄").Append(tdd.PhysiologicalAge).Append("岁")
              .Append(" · 命龄").Append(tdd.ActualAge).Append("岁")
              .Append(" · 立场").Append(BehaviorName(tdd.BehaviorType));
            if (!string.IsNullOrWhiteSpace(tOrg)) sb.Append(" · ").Append(tOrg);
            if (!string.IsNullOrWhiteSpace(tFame)) sb.Append(" · 侠名【").Append(tFame).Append("】");
            sb.Append(" · 当前心情值").Append((int)tdd.Happiness);
            sb.Append(" · 当前魅力值:").Append(CharacterCharmText.Format(tdd.Charm));
            s.TaiwuInfoText = sb.ToString();
        }

        // 取门派头衔(Grade 越界会抛,故 try/catch 由调用方包)
        static string dd_OrgString(CharacterDisplayData d) => d.OrgInfo.ToString();

        private static short ResolveIdentityTemplateId(int npcId, short actualTemplateId)
        {
            try
            {
                BasicGameData bgd = SingletonObject.getInstance<BasicGameData>();
                int taiwuId = bgd == null ? -1 : bgd.TaiwuCharId;
                if (taiwuId > 0 && CharacterProxyIdentityService.TryGetDisplayTemplate(
                    taiwuId, npcId, out short originalTemplateId))
                    return originalTemplateId;
            }
            catch { }
            return actualTemplateId;
        }

        static IEnumerator FetchLifeRecords(int npcId, NpcSnapshot s, Func<bool> stillCurrent = null)
        {
            TransferableLifeRecordData all = null; int start = 0;
            for (; ; )
            {
                TransferableLifeRecordData page = null; bool pDone = false;
                if (!StillCurrent(stillCurrent)) yield break;
                LifeRecordDomainMethod.AsyncCall.GetReversedRecord(null, npcId, start, 10000, false, (offset, pool) =>
                {
                    try { page = new TransferableLifeRecordData(); Serializer.Deserialize(pool, offset, ref page); }
                    catch (Exception e) { s.Errors.Add("life: " + e.GetType().Name); }
                    finally { pDone = true; }
                });
                yield return WaitDone(() => pDone, stillCurrent);
                if (!StillCurrent(stillCurrent)) yield break;
                if (page == null) break;
                int cnt = page.LifeRecordCount;
                if (all == null) all = page; else all.TransferData(page);
                if (cnt < 10000) break;
                int next = all.LifeRecordCount; if (next <= start) break; start = next;
                if (!StillCurrent(stillCurrent)) yield break;
            }
            if (all?.Record == null) yield break;
            foreach (var r in all.Record)
            {
                if (r == null || r.RecordType < 0) continue;
                var rr = new RenderedRecordData(); rr.SetData(r, all);
                string text = Clean(rr.Main); if (string.IsNullOrWhiteSpace(text)) continue;
                string type = ""; try { type = Config.LifeRecord.Instance[r.RecordType]?.Name ?? ""; } catch { }
                s.LifeRecords.Add((r.Date, Clean(type), text));
            }
            s.LifeRecords.Sort((a, b) => a.date.CompareTo(b.date));
        }

        /// <summary>读某角色【近期】(date≥sinceDate)生平大事的文本列表。供过月连载判定太吾是否与班底有"经历交集"。</summary>
        public static IEnumerator FetchRecentLifeRecordTexts(int charId, int sinceDate, Action<List<string>> done)
        {
            var outp = new List<string>();
            if (charId <= 0) { done?.Invoke(outp); yield break; }
            var s = new NpcSnapshot { NpcId = charId };
            yield return FetchLifeRecords(charId, s);
            if (s.LifeRecords != null)
                foreach (var lr in s.LifeRecords)
                    if (lr.date >= sinceDate && !string.IsNullOrEmpty(lr.text)) outp.Add(lr.text);
            done?.Invoke(outp);
        }

        static IEnumerator FetchSecrets(int npcId, int curDate, NpcSnapshot s, Func<bool> stillCurrent = null)
        {
            s.SecretsLoaded = false;
            SecretInformationDisplayPackage pkg = null; bool pDone = false;
            if (!StillCurrent(stillCurrent)) yield break;
            InformationDomainMethod.AsyncCall.GetSecretInformationDisplayPackageFromCharacter(null, npcId, (offset, pool) =>
            {
                try { pkg = new SecretInformationDisplayPackage(); Serializer.Deserialize(pool, offset, ref pkg); }
                catch (Exception e) { s.Errors.Add("secret: " + e.GetType().Name); }
                finally { pDone = true; }
            });
            yield return WaitDone(() => pDone, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            s.SecretsLoaded = pkg != null;
            if (pkg?.SecretInformationDisplayDataList == null) yield break;
            foreach (var d in pkg.SecretInformationDisplayDataList)
            {
                if (d == null) continue;
                string type = ""; try { type = SecretInformation.Instance[d.SecretInformationTemplateId]?.Name ?? ""; } catch { }
                string text;
                try
                {
                    text = InformationUtils.MakeSecretInformationDescription(new SecretSortAndFilterData
                    {
                        Data = d,
                        Characters = new Dictionary<int, CharacterDisplayData>(pkg.CharacterData),
                        Date = curDate
                    });
                }
                catch { continue; }
                text = Clean(text).Trim('“', '”', '"', ' ');
                if (string.IsNullOrWhiteSpace(text)) continue;
                s.Secrets.Add((d.OccurenceDate, Clean(type), text));
                // 非广播态秘闻方可被太吾接收(广播态引擎会拒);记 id 供"套出秘闻"
                if (!d.IsInBroadcast)
                    s.ShareableSecrets.Add(new SecretRef
                    {
                        Id = d.SecretInformationId,
                        Text = text,
                        SourceCharId = d.SourceCharacterId,
                        DisplayIndex = s.ShareableSecrets.Count + 1,
                    });
            }
            s.Secrets.Sort((a, b) => a.date.CompareTo(b.date));
        }

        /// <summary>读某角色(如太吾)可分享的秘闻(非广播态),供"太吾把秘闻讲给 NPC"。复用 FetchSecrets。</summary>
        public static IEnumerator FetchShareableSecrets(int charId, int curDate, Action<List<SecretRef>> onResult)
        {
            var tmp = new NpcSnapshot();
            yield return FetchSecrets(charId, curDate, tmp);
            onResult?.Invoke(tmp.ShareableSecrets);
        }

        /// <summary>
        /// 读取讲述者确知、且对指定接收者当前仍可传播的秘闻。保持原始秘闻顺序与引用，
        /// 供单聊、群聊、千里传音和两类过月 Agent 共用同一项“先查对象再吐露”技能。
        /// 只读预检并发发出，避免逐条串行 RPC 把一轮查询拖成几十次等待。
        /// </summary>
        public static IEnumerator FetchDisclosableSecrets(int sourceCharId, int targetCharId, int curDate,
            Action<List<SecretRef>, string> onResult, Func<bool> stillCurrent = null)
        {
            var eligible = new List<SecretRef>();
            if (sourceCharId <= 0 || targetCharId <= 0 || sourceCharId == targetCharId
                || !StillCurrent(stillCurrent))
            {
                onResult?.Invoke(eligible, "秘闻讲述者或接收者无效");
                yield break;
            }

            List<SecretRef> all = null;
            yield return FetchShareableSecrets(sourceCharId, curDate, value => all = value);
            if (!StillCurrent(stillCurrent)) yield break;
            if (all == null || all.Count == 0)
            {
                onResult?.Invoke(eligible, "讲述者当前没有未公开且确知的秘闻");
                yield break;
            }

            int count = all.Count;
            // 与项目其它批量权威读取保持同一并发上限，避免秘闻很多时瞬间向
            // GameData 后端灌入数十个 RPC；仍会分批扫描完整清单，不截断候选。
            const int maxConcurrency = 8;
            var completed = new int[count];
            var allowed = new bool[count];
            var reasons = new string[count];
            int completedCount = 0;
            int next = 0;
            int inflight = 0;
            float deadline = Time.unscaledTime + 8f;
            while ((next < count || System.Threading.Volatile.Read(ref completedCount) < count)
                && Time.unscaledTime < deadline && StillCurrent(stillCurrent))
            {
                while (next < count && System.Threading.Volatile.Read(ref inflight) < maxConcurrency)
                {
                    int slot = next++;
                    SecretRef secret = all[slot];
                    if (secret == null)
                    {
                        System.Threading.Interlocked.Exchange(ref completed[slot], 1);
                        System.Threading.Interlocked.Increment(ref completedCount);
                        continue;
                    }
                    System.Threading.Interlocked.Increment(ref inflight);
                    EffectHandler.QueryCanDiscloseSecret(secret.Id, sourceCharId, targetCharId,
                        (ok, reason) =>
                        {
                            allowed[slot] = ok;
                            reasons[slot] = reason;
                            if (System.Threading.Interlocked.Exchange(ref completed[slot], 1) == 0)
                            {
                                System.Threading.Interlocked.Increment(ref completedCount);
                                System.Threading.Interlocked.Decrement(ref inflight);
                            }
                        });
                }
                if (System.Threading.Volatile.Read(ref completedCount) < count)
                    yield return null;
            }
            if (!StillCurrent(stillCurrent)) yield break;

            string firstReason = null;
            for (int i = 0; i < count; i++)
            {
                if (System.Threading.Volatile.Read(ref completed[i]) != 0 && allowed[i] && all[i] != null)
                    eligible.Add(all[i]);
                else if (firstReason == null && !string.IsNullOrWhiteSpace(reasons[i])) firstReason = reasons[i];
            }
            string diagnostic = eligible.Count > 0 ? null
                : System.Threading.Volatile.Read(ref completedCount) < count ? "秘闻接收资格预查超时"
                : (firstReason ?? "接收者已经知情，或这些秘闻已经公开");
            onResult?.Invoke(eligible, diagnostic);
        }

        /// <summary>按需读取角色当前内外伤标记总数。回调/反序列化失败时保留未知态，绝不把未知写成无伤。</summary>
        public static IEnumerator FetchInjuryState(int charId, NpcSnapshot s,
            Func<bool> stillCurrent = null)
        {
            if (s == null) yield break;
            s.InjuryStateLoaded = false;
            s.InjuryMarkCount = -1;
            if (charId <= 0 || !StillCurrent(stillCurrent)) yield break;
            CharacterInjuryDisplayData data = null;
            bool done = false;
            CharacterDomainMethod.AsyncCall.GetCharacterInjuryDisplayData(null, charId, (offset, pool) =>
            {
                try { Serializer.Deserialize(pool, offset, ref data); }
                catch (Exception e) { s.Errors.Add("injuries: " + e.GetType().Name); data = null; }
                finally { done = true; }
            });
            yield return WaitDone(() => done, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            s.InjuryStateLoaded = data != null;
            if (data != null) s.InjuryMarkCount = data.Injuries.GetSum();
        }

        // 读 NPC 背包,聚成"可赠之物"候选(名字去重,带稳定 ItemKey;供 AI 选送哪件)
        public static IEnumerator FetchGiftables(int npcId, NpcSnapshot s,
            Func<bool> stillCurrent = null)   // 对话预载或动作 JIT 复核均复用本权威读取
        {
            if (s == null) yield break;
            s.GiftableItems.Clear();
            s.EquipableInventoryItems.Clear();
            s.EquippedItemNames.Clear();
            s.EquippedParts.Clear();
            s.InventoryLoaded = false;
            s.EquipmentLoaded = false;
            s.FoodLoaded = false;
            s.HoldingsLoaded = false;
            s.HasRope = false;
            s.HasPoison = false;
            s.Silver = 0;
            CharacterItemsDisplayData pkg = null; bool done = false, foodLoaded = false;
            if (!StillCurrent(stillCurrent)) yield break;
            CharacterDomainMethod.AsyncCall.GetCharacterItemsDisplayData(null, npcId, (offset, pool) =>
            {
                try { Serializer.Deserialize(pool, offset, ref pkg); }
                catch (Exception e) { s.Errors.Add("items: " + e.GetType().Name); }
                finally { done = true; }
            });
            yield return WaitDone(() => done, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            // Callback completion alone does not prove authoritative data was decoded.
            // A deserialize failure leaves pkg null and must disable negative preflight.
            s.InventoryLoaded = pkg != null;
            // 与后端 EnumerateNpcHoldings 保持同一口径：背包、资源、熟食、已装备物
            // 中的同名持有物合并数量，并保留首个真实 ItemKey。月度预查若另造一套
            // “按来源去重”规则，会低估数量或把同一物名重复展示给模型。
            var byName = new Dictionary<string, GiftableItem>(StringComparer.Ordinal);
            var equipableByName = new Dictionary<string, GiftableItem>(StringComparer.Ordinal);
            var items = pkg?.InventoryItems;
            if (items != null)
                foreach (var it in items)
                {
                    if (it == null || it.Amount <= 0) continue;
                    var key = it.RealKey;
                    // 顺带探测随身有无绳索/毒药(与后端 capture/poison 判定同源),供提示词预载——绑/毒"调用前自知",无则模型自会改口而非先答应再被打回。
                    if (!s.HasRope && key.ItemType == (sbyte)12 && key.TemplateId >= 82 && key.TemplateId <= 90) s.HasRope = true;
                    if (!s.HasPoison) { try { if (ItemTemplateHelper.GetMedicineItemPoisonType(key.ItemType, key.TemplateId) >= 0) s.HasPoison = true; } catch { } }
                    string name; try { name = Clean(ItemTemplateHelper.GetName(key.ItemType, key.TemplateId)); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    GiftableItem holding;
                    if (byName.TryGetValue(name, out holding))
                    {
                        holding.Count += it.Amount;
                        holding.NonEquippedCount += it.Amount;
                    }
                    else
                    {
                        holding = new GiftableItem
                        {
                            Name = name, Key = key, Count = it.Amount,
                            NonEquippedCount = it.Amount
                        };
                        byName[name] = holding;
                        s.GiftableItems.Add(holding);
                    }
                    if (CanEquip(key))
                    {
                        GiftableItem equipable;
                        if (equipableByName.TryGetValue(name, out equipable)) equipable.Count += it.Amount;
                        else
                        {
                            equipable = new GiftableItem { Name = name, Key = key, Count = it.Amount };
                            equipableByName[name] = equipable;
                            s.EquipableInventoryItems.Add(equipable);
                        }
                    }
                }

            // 资源(食材0/木料1/金石2/玉石3/布料4/药材5——存于 ResourceInts,与 InventoryItems 分开存,持有页都算"持有")也纳入可赠。
            // 编码同游戏显示行 ItemKey(12,0,资源类型,0);银钱6走 type=silver、威望7不赠,故只取 0..5。名字去重,与 InventoryItems 重叠也无碍。
            if (pkg != null)
            {
                try { s.Silver = pkg.Resources[6]; } catch { s.Silver = 0; }
                for (sbyte rt = 0; rt <= 5; rt++)
                {
                    int amt; try { amt = pkg.Resources[rt]; } catch { break; }
                    if (amt <= 0) continue;
                    string name; try { name = Clean(Config.ResourceType.Instance[(sbyte)rt].Name); } catch { continue; }   // 资源用规范名(食材/布料…),而非 Misc.Instance[rt](错表→AI 认不出、按名送不出)
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    GiftableItem holding;
                    if (byName.TryGetValue(name, out holding))
                    {
                        holding.Count += amt;
                        holding.NonEquippedCount += amt;
                    }
                    else
                    {
                        holding = new GiftableItem
                        {
                            Name = name,
                            Key = new ItemKey((sbyte)12, (byte)0, (short)rt, 0),
                            Count = amt,
                            NonEquippedCount = amt
                        };
                        byName[name] = holding;
                        s.GiftableItems.Add(holding);
                    }
                }
            }

            // 再纳入"身上佩带之物"(已装备的兵器/衣甲/佩饰)——玩家最想要的往往正是 NPC 随身佩带之物;
            // 后端转移前会先为其卸下。这样 AI 答应"把佩剑赠你"也能真正落地(此前装备物结构性不在可赠清单 = 最大根因)。
            {
                List<ItemKey> eq = null; bool done2 = false;
                if (!StillCurrent(stillCurrent)) yield break;
                CharacterDomainMethod.AsyncCall.GetEquipmentKeys(null, npcId, (offset, pool) =>
                {
                    try { Serializer.Deserialize(pool, offset, ref eq); }
                    catch (Exception e) { s.Errors.Add("equip: " + e.GetType().Name); }
                    finally { done2 = true; }
                });
                yield return WaitDone(() => done2, stillCurrent);
                if (!StillCurrent(stillCurrent)) yield break;
                s.EquipmentLoaded = eq != null;
                if (eq != null)
                    for (int slot = 0; slot < eq.Count; slot++)
                    {
                        var key = eq[slot];
                        if (!key.IsValid()) continue;
                        string name; try { name = Clean(ItemTemplateHelper.GetName(key.ItemType, key.TemplateId)); } catch { continue; }
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!s.EquippedItemNames.Contains(name)) s.EquippedItemNames.Add(name);
                        string part = EquipmentPart(slot);
                        if (!string.IsNullOrWhiteSpace(part)) s.EquippedParts.Add(part);
                        GiftableItem holding;
                        if (byName.TryGetValue(name, out holding))
                        {
                            holding.Count++;
                            holding.EquippedCount++;
                        }
                        else
                        {
                            holding = new GiftableItem
                            {
                                Name = name, Key = key, Count = 1, EquippedCount = 1
                            };
                            byName[name] = holding;
                            s.GiftableItems.Add(holding);
                        }
                    }
            }

            // 再纳入"随身熟食"(荷包蛋等存于 EatingItems,GetCharacterItemsDisplayData 不含 → 后端 char_food 补读)。
            // 玩家常想送/要的正是这类;此前不在可赠清单 = query_npc_items 查不到、赠不出的根因。
            {
                bool fdone = false, foodOk = false; List<ItemKey> food = null;
                if (!StillCurrent(stillCurrent)) yield break;
                JianghuYouling.Effects.EffectHandler.QueryCharFood(npcId,
                    (ok, fk) => { foodOk = ok; food = fk; fdone = true; });
                yield return WaitDone(() => fdone, stillCurrent);
                if (!StillCurrent(stillCurrent)) yield break;
                foodLoaded = fdone && foodOk;
                s.FoodLoaded = foodLoaded;
                if (food != null)
                    foreach (var key in food)
                    {
                        string name; try { name = Clean(ItemTemplateHelper.GetName(key.ItemType, key.TemplateId)); } catch { continue; }
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        GiftableItem holding;
                        if (byName.TryGetValue(name, out holding))
                        {
                            holding.Count++;
                            holding.NonEquippedCount++;
                        }
                        else
                        {
                            holding = new GiftableItem
                            {
                                Name = name, Key = key, Count = 1, NonEquippedCount = 1
                            };
                            byName[name] = holding;
                            s.GiftableItems.Add(holding);
                        }
                    }
            }
            s.HoldingsLoaded = s.InventoryLoaded && s.EquipmentLoaded && foodLoaded;
        }

        private static string EquipmentPart(int slot)
        {
            // 与后端 SlotsForPart 保持同源，避免模型看到“有武器”便尝试卸下一个
            // 实际不在可卸槽位里的备用兵器。GetEquipmentKeys 权威返回固定 17 槽顺序。
            if (slot == 0 || slot == 1 || slot == 2) return "weapon";
            if (slot == 3 || slot == 5 || slot == 6 || slot == 7) return "armor";
            if (slot == 8 || slot == 9 || slot == 10 || slot == 14 || slot == 15 || slot == 16)
                return "accessory";
            if (slot == 11 || slot == 12 || slot == 13) return "carrier";
            if (slot == 4) return "clothing";
            return null;
        }

        // 太吾 build 24185552 权威规则：能否穿戴不能只按 ItemType 0..N 判断。
        // 兵器子类 17 不能进兵器槽，特殊佩饰只进囊袋 14..16，代步则进 11..13；
        // 与后端一样逐槽调用 IsItemMeetSlot，避免月度预查显示“可换上”而真实执行失败。
        private static bool CanEquip(ItemKey key)
        {
            for (sbyte slot = 0; slot < 17; slot++)
            {
                try { if (EquipmentSlotHelper.IsItemMeetSlot(slot, key)) return true; }
                catch { }
            }
            return false;
        }

        // 读 NPC 所习武学,聚成候选(名字去重 + templateId;供 query_npc_skills 展示 / write_book 默写 / teach 传授判名)。
        // #14 不再排除太吾已学的武学 —— NPC 传授给太吾后仍应"会"该功,不该从其武学清单里凭空消失(让人觉得它把功夫忘了)。
        // 安全性已另有兜底:NPC→太吾 现走"默写成册"(后端 writebook 校验 already_has 书);NPC→NPC 直传(后端 TeachSkill 全程 try/catch),
        // 故无需再靠"排除太吾已会"来规避 LearnCombatSkill 对已学武学抛异常的旧卡死路径。
        public static IEnumerator FetchSkills(int npcId, NpcSnapshot s,
            Func<bool> stillCurrent = null,
            bool includeFlipEligibility = true)   // 受教者查重只读所学，不额外查询其正逆练资格
        {
            s.CombatSkillsLoaded = false;
            s.FlipPracticeEligibilityLoaded = false;
            s.FlippableCombatSkillIds.Clear();
            s.LearnableSkills.Clear();
            List<short> ids = null; bool done = false;
            if (!StillCurrent(stillCurrent)) yield break;
            GameData.Domains.CombatSkill.CombatSkillDomainMethod.AsyncCall.GetLearnedCombatSkillByType(null, npcId, (sbyte)(-1), (offset, pool) =>
            {
                try { Serializer.Deserialize(pool, offset, ref ids); }
                catch (Exception e) { s.Errors.Add("skills: " + e.GetType().Name); }
                finally { done = true; }
            });
            yield return WaitDone(() => done, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            s.CombatSkillsLoaded = ids != null;
            if (ids == null) yield break;
            var seen = new HashSet<string>();
            foreach (var id in ids)
            {
                string name; try { name = Clean(Config.CombatSkill.Instance[id].Name); } catch { continue; }
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                // 带上武学品阶(九品~一品),供查询据实显示;teach 仍按名(含品阶/不含)宽松匹配
                string gname = ""; try { gname = GradeName((sbyte)Config.CombatSkill.Instance[id].Grade); } catch { }
                string disp = string.IsNullOrEmpty(gname) ? name : (name + "(" + gname + ")");
                s.LearnableSkills.Add(new LearnableSkill { Name = disp, TemplateId = id });
                // 不截断:列全所习武学(查询/传授要看全),天然受技能总数 + 工具结果 4000 字兜底约束
            }

            if (!includeFlipEligibility) yield break;

            // 可颠倒资格涉及阅读态 + 突破激活态，前端公开快照不含这些字段；按最新版
            // CombatSkillDomain.SetActivePage 的权威校验在后端一次性只读计算，执行前即可避开 no_reverse_page。
            if (ids.Count == 0)
            {
                s.FlipPracticeEligibilityLoaded = true;
                yield break;
            }
            bool eligibilityDone = false, eligibilityOk = false;
            HashSet<short> eligible = null;
            string eligibilityError = null;
            EffectHandler.QueryFlippablePracticeSkills(npcId, (ok, value, error) =>
            {
                eligibilityOk = ok;
                eligible = value;
                eligibilityError = error;
                eligibilityDone = true;
            });
            yield return WaitDone(() => eligibilityDone, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            if (eligibilityDone && eligibilityOk && eligible != null)
            {
                s.FlippableCombatSkillIds.UnionWith(eligible);
                s.FlipPracticeEligibilityLoaded = true;
            }
            else if (!string.IsNullOrWhiteSpace(eligibilityError))
                s.Errors.Add("flip eligibility: " + eligibilityError);
        }

        // 读 NPC 所习技艺(生活技能),聚成候选(名=种类名+品阶,带 templateId)。
        // #14 同武学:不再排除太吾已学的技艺,NPC 传授后仍"会",不从其技艺清单消失。
        public static IEnumerator FetchLifeSkills(int npcId, NpcSnapshot s,
            Func<bool> stillCurrent = null)   // 惰性:同上(技艺)
        {
            s.LifeSkillsLoaded = false;
            s.LearnableLifeSkills.Clear();
            CharacterMenuLifeSkillDisplayData data = null; bool done = false;
            if (!StillCurrent(stillCurrent)) yield break;
            CharacterDomainMethod.AsyncCall.GetCharacterMenuLifeSkillDisplayData(null, npcId, (offset, pool) =>
            { try { Serializer.Deserialize(pool, offset, ref data); } catch (Exception e) { s.Errors.Add("life: " + e.GetType().Name); } finally { done = true; } });
            yield return WaitDone(() => done, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            s.LifeSkillsLoaded = data != null;
            if (data?.LearnedLifeSkills == null) yield break;
            var seen = new HashSet<string>();
            foreach (var ls in data.LearnedLifeSkills)
            {
                string name; try { name = LifeSkillName(ls.SkillTemplateId); } catch { continue; }
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                s.LearnableLifeSkills.Add(new LearnableSkill { Name = name, TemplateId = ls.SkillTemplateId });
            }
        }

        public static IEnumerator FetchStudyProgress(int npcId, NpcSnapshot s,
            Func<bool> stillCurrent = null)
        {
            s.StudyProgressLoaded = false;
            s.IncompleteTrainingSkillIds.Clear();
            s.UnreadBookNames.Clear();
            if (!StillCurrent(stillCurrent)) yield break;
            bool done = false, ok = false;
            HashSet<short> skills = null;
            List<string> books = null;
            string error = null;
            EffectHandler.QueryNpcStudyProgress(npcId, (success, incompleteSkills,
                unreadBooks, message) =>
            {
                ok = success;
                skills = incompleteSkills;
                books = unreadBooks;
                error = message;
                done = true;
            });
            yield return WaitDone(() => done, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            if (done && ok && skills != null && books != null)
            {
                s.IncompleteTrainingSkillIds.UnionWith(skills);
                s.UnreadBookNames.AddRange(books);
                s.StudyProgressLoaded = true;
            }
            else if (!string.IsNullOrWhiteSpace(error))
                s.Errors.Add("study progress: " + error);
        }

        public static IEnumerator FetchUsableItems(int npcId, NpcSnapshot s,
            Func<bool> stillCurrent = null)
        {
            s.UsableItemsLoaded = false;
            s.UsableItemNames.Clear();
            if (!StillCurrent(stillCurrent)) yield break;
            bool done = false, ok = false;
            List<string> names = null;
            string error = null;
            EffectHandler.QueryNpcUsableItems(npcId, (success, usable, message) =>
            {
                ok = success; names = usable; error = message; done = true;
            });
            yield return WaitDone(() => done, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            if (done && ok && names != null)
            {
                s.UsableItemNames.AddRange(names);
                s.UsableItemsLoaded = true;
            }
            else if (!string.IsNullOrWhiteSpace(error))
                s.Errors.Add("usable items: " + error);
        }

        // 技艺书真名(如"金针伐脉功");取不到再用"种类名+品阶"兜底。
        static string LifeSkillName(short tpl)
        {
            try { var nm = Clean(Config.LifeSkill.Instance[tpl].Name); if (!string.IsNullOrWhiteSpace(nm)) return nm; } catch { }
            for (sbyte ty = 0; ty < 16; ty++)
            {
                try
                {
                    var item = Config.LifeSkillType.Instance[ty];
                    var list = item?.SkillList;
                    if (list == null) continue;
                    for (int g = 0; g < list.Length; g++)
                        if (list[g] == tpl) return Clean(item.Name) + GradeName((sbyte)g);
                }
                catch { }
            }
            return null;
        }

        // ===== 太吾自身的随身物 + 所习武学/技艺(供"太吾送 NPC 物品 / 传功传艺"按需读取;非每轮必拉,故单列、惰性取)=====
        // 与 NPC 版不同:这里要太吾"自己拥有的",不做任何"已学排除";造诣继承在后端读太吾真实进度。
        public static IEnumerator FetchTaiwuHoldings(int taiwuId, Action<TaiwuHoldings> done)
        {
            var h = new TaiwuHoldings();
            if (taiwuId <= 0) { done?.Invoke(h); yield break; }

            // 1) 随身可赠之物(背包 + 佩带)
            CharacterItemsDisplayData pkg = null; bool d1 = false;
            CharacterDomainMethod.AsyncCall.GetCharacterItemsDisplayData(null, taiwuId, (offset, pool) =>
            { try { Serializer.Deserialize(pool, offset, ref pkg); } catch { } finally { d1 = true; } });
            yield return WaitDone(() => d1);
            var seen = new HashSet<string>();
            if (pkg?.InventoryItems != null)
                foreach (var it in pkg.InventoryItems)
                {
                    if (it == null || it.Amount <= 0) continue;
                    var key = it.RealKey;
                    string name; try { name = Clean(ItemTemplateHelper.GetName(key.ItemType, key.TemplateId)); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    h.Items.Add(new GiftableItem
                    {
                        Name = name,
                        Key = key,
                        Count = it.Amount,
                        NonEquippedCount = it.Amount,
                    });
                }

            // 1a2) 资源(食材/木料/金石/玉石/布料/药材,存于 ResourceInts、与背包分开):太吾持有页同样含,纳入可赠/可送清单(银钱走 silver、威望不赠)
            if (pkg != null)
                for (sbyte rt = 0; rt <= 5; rt++)
                {
                    int amt; try { amt = pkg.Resources[rt]; } catch { break; }
                    if (amt <= 0) continue;
                    string name; try { name = Clean(Config.ResourceType.Instance[(sbyte)rt].Name); } catch { continue; }   // 资源用规范名(食材/布料…),而非 Misc.Instance[rt](错表→AI 认不出、按名送不出)
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    h.Items.Add(new GiftableItem
                    {
                        Name = name,
                        Key = new ItemKey((sbyte)12, (byte)0, (short)rt, 0),
                        Count = amt,
                        NonEquippedCount = amt,
                    });
                    h.Resources.Add(new KeyValuePair<string, int>(name, amt));   // 记下数量,供据实告知
                }

            // 1b) 随身熟食(荷包蛋等存于 EatingItems,不在背包显示数据里 → 经后端补读)
            bool fdone = false; List<GameData.Domains.Item.ItemKey> food = null;
            JianghuYouling.Effects.EffectHandler.QueryCharFood(taiwuId, (ok, fk) => { food = ok ? fk : null; fdone = true; });
            yield return WaitDone(() => fdone);
            if (food != null)
                foreach (var key in food)
                {
                    string name; try { name = Clean(ItemTemplateHelper.GetName(key.ItemType, key.TemplateId)); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    h.Items.Add(new GiftableItem
                    {
                        Name = name,
                        Key = key,
                        Count = 1,
                        NonEquippedCount = 1,
                    });
                }

            // 1c) 太吾当前武具/装备页穿戴物。玩家常会说"我把佩剑/衣甲给你",这些不在背包 InventoryItems 里。
            List<ItemKey> eq = null; bool edone = false;
            CharacterDomainMethod.AsyncCall.GetEquipmentKeys(null, taiwuId, (offset, pool) =>
            { try { Serializer.Deserialize(pool, offset, ref eq); } catch { } finally { edone = true; } });
            yield return WaitDone(() => edone);
            if (eq != null)
                foreach (var key in eq)
                {
                    if (!key.IsValid()) continue;
                    string name; try { name = Clean(ItemTemplateHelper.GetName(key.ItemType, key.TemplateId)); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    h.Items.Add(new GiftableItem
                    {
                        Name = name,
                        Key = key,
                        Count = 1,
                        EquippedCount = 1,
                    });
                }

            // 2) 所习武学/技艺。动作前置以最新版后端 Character 的完整已学集合为权威；
            // 前端菜单接口仅在旧后端/瞬时 RPC 不可得时兜底，避免“太吾明明会却查不到”。
            List<short> ids = null, lifeIds = null; bool d2 = false;
            string skillQueryError = null;
            JianghuYouling.Effects.EffectHandler.QueryTaiwuLearnedSkills(taiwuId,
                (combat, life, error) => { ids = combat; lifeIds = life; skillQueryError = error; d2 = true; });
            yield return WaitDone(() => d2);
            if (ids == null)
            {
                if (!string.IsNullOrWhiteSpace(skillQueryError))
                    Debug.LogWarning("[江湖有灵] 太吾权威功法查询回退前端显示接口:" + skillQueryError);
                d2 = false;
                GameData.Domains.CombatSkill.CombatSkillDomainMethod.AsyncCall.GetLearnedCombatSkillByType(null, taiwuId, (sbyte)(-1), (offset, pool) =>
                { try { Serializer.Deserialize(pool, offset, ref ids); } catch { } finally { d2 = true; } });
                yield return WaitDone(() => d2);
            }
            if (ids != null)
            {
                var cseen = new HashSet<string>();
                foreach (var id in ids)
                {
                    string name; try { name = Clean(Config.CombatSkill.Instance[id].Name); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(name) || !cseen.Add(name)) continue;
                    string gn = ""; try { gn = GradeName((sbyte)Config.CombatSkill.Instance[id].Grade); } catch { }
                    h.CombatSkills.Add(new LearnableSkill { Name = string.IsNullOrEmpty(gn) ? name : (name + "(" + gn + ")"), TemplateId = id });
                    // 不截断:太吾后期所习武学常远超 60,查询时须列全(否则要传的功法可能不在表里);天然受技能总数 + 工具结果 4000 字兜底约束
                }
            }

            // 3) 所习技艺(太吾会的生活技能,可传给 NPC)
            if (lifeIds != null)
            {
                var lseen = new HashSet<string>();
                foreach (short lifeId in lifeIds)
                {
                    string name; try { name = LifeSkillName(lifeId); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(name) || !lseen.Add(name)) continue;
                    h.LifeSkills.Add(new LearnableSkill { Name = name, TemplateId = lifeId });
                }
            }
            else
            {
                CharacterMenuLifeSkillDisplayData life = null; bool d3 = false;
                CharacterDomainMethod.AsyncCall.GetCharacterMenuLifeSkillDisplayData(null, taiwuId, (offset, pool) =>
                { try { Serializer.Deserialize(pool, offset, ref life); } catch { } finally { d3 = true; } });
                yield return WaitDone(() => d3);
                if (life?.LearnedLifeSkills != null)
                {
                    var lseen = new HashSet<string>();
                    foreach (var ls in life.LearnedLifeSkills)
                    {
                        string name; try { name = LifeSkillName(ls.SkillTemplateId); } catch { continue; }
                        if (string.IsNullOrWhiteSpace(name) || !lseen.Add(name)) continue;
                        h.LifeSkills.Add(new LearnableSkill { Name = name, TemplateId = ls.SkillTemplateId });
                    }
                }
            }

            done?.Invoke(h);
        }

        /// <summary>取太吾当前小队/同行成员的 charId 列表(含太吾本人;调用方需自行剔除太吾与失效者)。前端直读 GetGroupSet。</summary>
        public static void FetchGroupMembers(int taiwuId, Action<List<int>> onResult)
        {
            if (taiwuId <= 0) { onResult?.Invoke(new List<int>()); return; }
            try
            {
                CharacterDomainMethod.AsyncCall.GetGroupSet(null, taiwuId, (offset, pool) =>
                {
                    List<int> ids = null;
                    try { Serializer.Deserialize(pool, offset, ref ids); } catch { }
                    onResult?.Invoke(CharacterProxyIdentityService.NormalizeKnownIds(
                        taiwuId, ids ?? new List<int>(), includeTaiwu: true));
                });
            }
            catch { onResult?.Invoke(new List<int>()); }
        }

        // 判定是否商人(GetMerchantTemplateId>=0)。决定对话是否带 query_merchant_goods / trade 工具。
        // 仅判定是否商人(决定工具清单);货架本身惰性,见 FetchMerchantGoods。
        static IEnumerator FetchMerchant(int npcId, NpcSnapshot s, Func<bool> stillCurrent = null)
        {
            sbyte tpl = -1; bool done = false;
            if (!StillCurrent(stillCurrent)) yield break;
            GameData.Domains.Merchant.MerchantDomainMethod.AsyncCall.GetMerchantTemplateId(null, npcId, (offset, pool) =>
            { try { Serializer.Deserialize(pool, offset, ref tpl); } catch (Exception e) { s.Errors.Add("merchant: " + e.GetType().Name); } finally { done = true; } });
            yield return WaitDone(() => done, stillCurrent);
            if (!StillCurrent(stillCurrent)) yield break;
            s.IsMerchant = tpl >= 0;
            s.MerchantTemplateId = tpl;
        }

        // 取某角色当前所在区域 id(无效=-1)。供"AI 月度事件"算远近。一次 GetCharacterDisplayData。
        public static IEnumerator FetchCharArea(int charId, Action<short> done)
        {
            if (charId <= 0) { done?.Invoke(-1); yield break; }
            short backendArea = -1; bool backendOk = false; bool backendDone = false;
            JianghuYouling.Effects.EffectHandler.QueryCharLocation(charId, (area, block, ok) => { backendArea = area; backendOk = ok; backendDone = true; });
            float bdl = Time.unscaledTime + 2.5f;
            while (!backendDone && Time.unscaledTime < bdl) yield return null;
            if (backendDone && backendOk && backendArea >= 0) { done?.Invoke(backendArea); yield break; }

            CharacterDisplayData dd = null; bool d = false;
            CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, charId, (offset, pool) =>
            { try { Serializer.Deserialize(pool, offset, ref dd); } catch { } finally { d = true; } });
            yield return WaitDone(() => d);
            done?.Invoke(dd != null && dd.Location.IsValid() ? dd.Location.AreaId : (short)-1);
        }

        // JHYL_MONTHLY_FRONTEND_CURRENT_AREA_FIRST: 过月事件地点优先取前端世界地图当前位置,后端角色坐标只作兜底。
        public static bool TryGetCurrentWorldArea(out short area, out short block)
        {
            short templateId;
            string areaName;
            return TryGetCurrentWorldArea(out area, out block, out templateId, out areaName);
        }

        public static bool TryGetCurrentWorldArea(out short area, out short block, out short templateId, out string areaName)
        {
            area = -1;
            block = -1;
            templateId = -1;
            areaName = null;
            try
            {
                var wm = SingletonObject.getInstance<WorldMapModel>();
                if (wm == null) return false;
                area = wm.CurrentAreaId;
                block = wm.CurrentBlockId;
                try { templateId = wm.GetCurrentAreaTemplateId(); } catch { }
                try { areaName = Clean(wm.GetAreaName(area)); } catch { }
                if (area < 0) return false;
                return true;
            }
            catch { return false; }
        }

        // 查熟人:取某角色的简识(姓名/性别/年岁/立场/门派 + 当前所在地)。一次 GetCharacterDisplayData。
        public static IEnumerator FetchPersonBrief(int charId, Action<string> done)
        {
            if (charId <= 0) { done?.Invoke(null); yield break; }
            CharacterDisplayData dd = null; bool d = false;
            CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, charId, (offset, pool) =>
            { try { Serializer.Deserialize(pool, offset, ref dd); } catch { } finally { d = true; } });
            yield return WaitDone(() => d);
            if (dd == null) { done?.Invoke(null); yield break; }
            var sb = new System.Text.StringBuilder();
            string nm = "NPC#" + charId; try { nm = NameCenter.GetMonasticTitleOrDisplayName(dd, false) ?? nm; } catch { }
            sb.Append(nm).Append(":").Append(dd.Gender == 1 ? "男" : dd.Gender == 0 ? "女" : "?")
              .Append("性,身龄").Append(dd.PhysiologicalAge).Append("岁,命龄").Append(dd.ActualAge).Append("岁");
            string beh = BehaviorName(dd.BehaviorType); if (!string.IsNullOrEmpty(beh)) sb.Append(",").Append(beh).Append("立场");
            sb.Append(",当前心情值").Append((int)dd.Happiness);
            sb.Append(",当前魅力值:").Append(CharacterCharmText.Format(dd.Charm));
            string fame = FameText(dd);
            if (!string.IsNullOrWhiteSpace(fame)) sb.Append(",江湖名誉/侠名:").Append(fame);
            if (dd.AliveState == 1 || dd.AliveState == 2) sb.Append(",已故");
            // 身份:门派+职务(无门派则记"白身·江湖散人")——修"打听人物没身份"
            string org = null; try { if (dd.OrgInfo.OrgTemplateId > 0) org = Clean(dd.OrgInfo.ToString()); } catch { }
            sb.Append(",身份:").Append(!string.IsNullOrWhiteSpace(org) ? org : "无门派的江湖散人");
            // 性情特性:几条显性特性勾勒其为人(无门派者尤其靠这个立住"身份")
            try
            {
                if (dd.FeatureIds != null)
                {
                    var feats = new List<string>();
                    foreach (var fid in dd.FeatureIds)
                    {
                        if (feats.Count >= 4) break;
                        try { var fi = Config.CharacterFeature.Instance[fid]; if (fi != null && !fi.Hidden) { var fn = Clean(fi.Name); if (!string.IsNullOrWhiteSpace(fn) && !feats.Contains(fn)) feats.Add(fn); } } catch { }
                    }
                    if (feats.Count > 0) sb.Append(",性情:").Append(string.Join("、", feats));
                }
            }
            catch { }
            sb.Append(",武学精纯").Append((int)dd.ConsummateLevel).Append("/18");   // 供杀/绑前比对:行凶者不低于此值即可，相等可成
            string loc = ResolveLocationText(dd.Location);
            sb.Append(";此刻在").Append(string.IsNullOrWhiteSpace(loc) ? "去向不明" : loc).Append("。");
            done?.Invoke(sb.ToString());
        }

        private static string FameText(CharacterDisplayData data)
        {
            try
            {
                if (data != null && (data.FameType >= 0 || data.FameType == -2))
                    return Clean(CommonUtils.GetFameString(data.FameType));
            }
            catch { }
            return null;
        }

        // 把角色 Location 解析成可读地名(区域·地块);取不到返回 null。用游戏自带 WorldMapModel(与人物面板"所在"同源)。
        internal static string ResolveLocationText(GameData.Domains.Map.Location loc)
        {
            try
            {
                if (!loc.IsValid()) return null;
                var wm = SingletonObject.getInstance<WorldMapModel>();
                if (wm == null) return null;
                string area = Clean(wm.GetAreaName(loc.AreaId));
                string block = null; try { block = Clean(wm.GetBlockName(loc)); } catch { }
                if (!string.IsNullOrWhiteSpace(block) && block != area) return string.IsNullOrWhiteSpace(area) ? block : (area + "·" + block);
                return string.IsNullOrWhiteSpace(area) ? null : area;
            }
            catch { return null; }
        }

        /// <summary>门派名 → OrgTemplateId(模糊匹配 Config.Organization 名);认不出回 0。供 query_org_members。</summary>
        public static int ResolveOrgId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            name = name.Trim();
            try
            {
                foreach (var o in Config.Organization.Instance)
                    if (o != null && !string.IsNullOrEmpty(o.Name) && (o.Name == name || o.Name.Contains(name) || name.Contains(o.Name)))
                        return o.TemplateId;
            }
            catch { }
            return 0;
        }

        // 惰性:商人真·货架(固定店铺=角色货架;行商=按地块找商队货架,后端 merchant_goods 处理)。query_merchant_goods/trade 首次用到才拉。
        public static IEnumerator FetchMerchantGoods(int npcId, NpcSnapshot s)
        {
            if (s == null || !s.IsMerchant) yield break;
            bool gdone = false; JianghuYouling.Effects.MerchantGoodsQueryResult query = null;
            JianghuYouling.Effects.EffectHandler.QueryMerchantGoodsWithSource(npcId, s.MerchantTemplateId,
                value => { query = value; gdone = true; });
            yield return WaitDone(() => gdone);
            List<KeyValuePair<ItemKey, int>> goods = query?.Goods;
            if (goods != null)
            {
                var seen = new HashSet<string>();
                foreach (var kv in goods)
                {
                    if (s.MerchantGoods.Count >= 80) break;
                    var key = kv.Key;
                    string name; try { name = Clean(ItemTemplateHelper.GetName(key.ItemType, key.TemplateId)); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    s.MerchantGoods.Add(new GiftableItem { Name = name, Key = key, Count = kv.Value,
                        MerchantOwnerType = query?.OwnerType ?? -1, MerchantOwnerId = query?.OwnerId ?? -1 });
                }
            }
            Debug.Log("[江湖有灵] 商人货架 npc=" + npcId + " 现货种类=" + s.MerchantGoods.Count + "(0=未刷新/无存货,对话层不会回退背包)");
        }

        static bool StillCurrent(Func<bool> stillCurrent)
        {
            if (stillCurrent == null) return true;
            try { return stillCurrent(); }
            catch { return false; }
        }

        static IEnumerator WaitDone(Func<bool> ready, Func<bool> stillCurrent = null)
        {
            float dl = Time.realtimeSinceStartup + TIMEOUT;
            return new WaitUntil(() => ready() || !StillCurrent(stillCurrent) || Time.realtimeSinceStartup >= dl);
        }

        static string Clean(string v) => string.IsNullOrWhiteSpace(v) ? "" :
            System.Text.RegularExpressions.Regex.Replace(v, "<[^>]+>", "").Trim();

        static string BehaviorName(sbyte b)
        {
            switch (b) { case 0: return "刚正"; case 1: return "仁善"; case 2: return "中庸"; case 3: return "叛逆"; case 4: return "唯我"; default: return "未知"; }
        }
        static string GradeName(sbyte g)
        {
            switch (g) { case 0: return "九品"; case 1: return "八品"; case 2: return "七品"; case 3: return "六品"; case 4: return "五品"; case 5: return "四品"; case 6: return "三品"; case 7: return "二品"; case 8: return "一品"; default: return ""; }
        }
        static string FavorLevelName(short f) =>
            f <= -26000 ? "血仇" : f <= -22000 ? "痛恨" : f <= -18000 ? "憎恨" : f <= -14000 ? "仇视" : f <= -10000 ? "敌视" : f <= -6000 ? "鄙视" :
            f <= 6000 ? "陌路" : f <= 10000 ? "冷淡" : f <= 14000 ? "融洽" : f <= 18000 ? "热忱" : f <= 22000 ? "喜爱" : f <= 26000 ? "亲密" : "不渝";
        static void ApplyDirectionalAdoration(NpcSnapshot s, ushort rawRelation, MonthlyActionPreflight pair)
        {
            if (s == null) return;
            bool npcToTaiwu = pair != null ? pair.ActorAdoresTarget : (rawRelation & 16384) != 0;
            bool taiwuToNpc = pair != null && pair.TargetAdoresActor;
            s.NpcAdoresTaiwu = npcToTaiwu;
            s.TaiwuAdoresNpc = taiwuToNpc;
            s.MutualLoverWithTaiwu = npcToTaiwu && taiwuToNpc;
            s.RelationFlag = (ushort)(rawRelation & ~16384);
            if (s.MutualLoverWithTaiwu) s.RelationFlag |= 16384;
            s.Relation = RelationName(rawRelation, npcToTaiwu, pair != null ? (bool?)taiwuToNpc : null);
        }

        static string RelationName(ushort f, bool? npcAdoresTaiwu = null, bool? taiwuAdoresNpc = null)
        {
            if (f == ushort.MaxValue) f = 0;   // 65535=RelationType.Invalid 哨兵,非"全部关系"
            if (f == 0 && npcAdoresTaiwu != true && taiwuAdoresNpc != true) return "无特殊关系";
            var l = new List<string>();
            if ((f & 64) > 0) l.Add("义父母");
            if ((f & (1 | 8)) > 0) l.Add("父母");
            if ((f & 128) > 0) l.Add("义子女");
            if ((f & (2 | 16)) > 0) l.Add("子女");
            if ((f & (4 | 32 | 256)) > 0) l.Add("兄弟姐妹");
            if ((f & 512) > 0) l.Add("义兄弟");
            if ((f & 1024) > 0) l.Add("夫妻");
            if ((f & (2048 | 4096)) > 0) l.Add("师徒");
            if ((f & 8192) > 0) l.Add("挚友");
            bool npcAdore = npcAdoresTaiwu ?? ((f & 16384) > 0);
            bool taiwuAdore = taiwuAdoresNpc == true;
            if (npcAdore && taiwuAdore) l.Add("恋人（两情相悦）");
            else if (npcAdore) l.Add("爱慕太吾（单向）");
            else if (taiwuAdore) l.Add("被太吾爱慕（单向）");
            if ((f & 32768) > 0) l.Add("仇敌");
            return l.Count > 0 ? string.Join("/", l) : "无特殊关系";
        }
    }
}
