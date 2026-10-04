using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Tools;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    internal static class ToolSurfaceContractTests
    {
        private static readonly string[] ExpectedConversationTools =
        {
            "recall_memory", "consult_action_guide", "query_npc_status", "query_npc_relationships", "query_npc_history",
            "query_npc_items", "query_person_items", "query_person", "query_health_status", "query_current_block",
            "query_area_people", "query_org_members", "query_npc_skills", "query_npc_build",
            "query_npc_secrets", "query_taiwu_build", "query_taiwu_items", "query_taiwu_skills",
            "query_world_progress", "query_lore", "query_place", "query_sect_lore",
            "query_merchant_goods", "query_taiwu_secrets", "record_reaction", "remember", "offer_commission", "adjust_mood", "adjust_fame",
            "gift", "barter", "steal", "trade", "taiwu_give_item", "teach", "write_book",
            "taiwu_teach", "taiwu_write_book", "flip_practice", "train_skill", "read_book",
            "set_relation",
            "dissolve_relation", "matchmake", "relate_npc", "set_enmity", "spend_night",
            "sect_support", "change_caravan_favor", "adjust_third_party_favor",
            "change_appearance", "change_equipment", "add_feature", "goto_place", "tell_secret",
            "taiwu_tell_secret", "heal", "detox", "regulate_breath", "poison", "kill", "capture", "start_combat"
        };

        private static readonly string[] RemotePhysicalTools =
        {
            "gift", "barter", "steal", "teach", "write_book", "heal", "detox", "regulate_breath", "spend_night",
            "change_equipment", "change_appearance", "trade", "taiwu_give_item", "taiwu_teach",
            "taiwu_write_book", "change_caravan_favor", "start_combat", "kill", "capture", "poison"
        };

        private static readonly string[] ExpectedAssistantTools =
        {
            "query_person", "query_lore", "remember", "analyze_logs", "export_diagnostic_logs",
            "set_difficulty", "set_reply_length", "set_ghostwrite_length",
            "set_taiwu_voice", "toggle_ai_event", "toggle_companion_monthly",
            "toggle_thinking", "toggle_stream", "set_assistant_name", "toggle_assistant_proactive",
            "set_proactive_frequency",
        };

        private static readonly string[] ExpectedCompanionMonthlyTools =
        {
            "no_action", "query_person", "query_relationship", "query_health_status", "send_message", "steal", "relate", "enmity", "add_feature", "heal", "detox", "regulate_breath",
            "dissolve_relation", "adjust_favor", "adjust_mood", "adjust_fame", "set_relation", "spend_night", "matchmake",
            "flip_practice", "train_skill", "read_book", "barter",
            "change_equipment", "write_book", "teach", "query_secret_recipient", "tell_secret", "sect_support", "gift_item",
            "gift_silver", "kill", "poison", "capture",
        };

        private static readonly string[] ExpectedMonthlyEventQueryTools =
        {
            "query_person", "event_query_person", "event_query_place",
        };

        private static readonly string[] ExpectedMonthlyEventActionTools =
        {
            "event_relate", "event_enmity", "event_teach", "event_gift", "event_gift_silver",
            "event_barter", "event_steal", "event_goto", "event_heal", "event_detox", "event_regulate_breath", "event_favor", "event_mood", "event_fame",
            "event_matchmake", "event_spend_night", "event_dissolve", "event_write_book",
            "event_secret", "event_equipment", "event_flip_practice",
            "event_feature", "event_capture", "event_poison", "event_kill", "event_taiwu_fame",
        };

        private static readonly string[] ExpectedBackendDurableConversationTools =
        {
            "record_reaction", "gift", "barter", "steal", "teach", "write_book",
            "set_relation", "spend_night", "dissolve_relation", "matchmake", "relate_npc",
            "set_enmity", "kill", "capture", "poison", "heal", "detox", "regulate_breath", "tell_secret",
            "taiwu_tell_secret", "trade", "sect_support",
            "change_equipment", "flip_practice", "train_skill", "read_book", "use_item", "adjust_mood", "adjust_fame",
            "adjust_third_party_favor", "add_feature",
            "goto_place", "change_caravan_favor", "taiwu_give_item", "taiwu_teach",
            "taiwu_write_book",
        };

        public static void Run()
        {
            Console.WriteLine("=== 全 Mod 稳定工具面精确契约自测（63）===");
            var maximal = ToolRegistry.BuildConversationTools(new ToolContext
            {
                IsMerchant = true,
                InSect = true,
                CanStartCombat = true,
                CanOpenGrooming = true,
                Remote = false,
            });
            RequireValidDefinitions("最大本地工具面", maximal);
            RequireExactNames("最大本地工具面", maximal, ExpectedConversationTools);
            ToolDef remember = maximal.First(x => x.Name == "remember");
            if (!remember.Description.Contains("不要求必须是重大事件")
                || !remember.Description.Contains("明确说“记住/别忘/以后照此处理”时必须调用")
                || !remember.Description.Contains("只在正文口头答应不算记住"))
                throw new InvalidOperationException("明确记住请求没有形成强制写入长期记忆契约");
            ToolDef commission = maximal.First(x => x.Name == "offer_commission");
            if (commission.Parameters?["properties"]?["reward_grade"] == null
                || commission.Parameters?["properties"]?["reward_name"] != null
                || !commission.Description.Contains("不得指定具体奖品")
                || !commission.Description.Contains("右侧【委】列表"))
                throw new InvalidOperationException("人物委托没有保持‘AI 只选品级、代码随机奖励’边界");
            ToolDef giftTool = maximal.First(x => x.Name == "gift");
            if (giftTool.Parameters?["properties"]?["allow_equipped"] == null
                || giftTool.Description.IndexOf("语义上明确要求", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("穿戴中装备赠礼没有改为模型语义授权参数");
            ToolDef mood = maximal.First(x => x.Name == "adjust_mood");
            ToolDef fame = maximal.First(x => x.Name == "adjust_fame");
            if (!mood.Description.Contains("包括你自己、第三方 NPC 或太吾")
                || mood.Parameters?["properties"]?["target"] == null
                || mood.Parameters?["properties"]?["delta"] == null
                || !fame.Description.Contains("包括你自己、第三方 NPC 或太吾")
                || fame.Parameters?["properties"]?["target"] == null
                || fame.Parameters?["properties"]?["delta"] == null)
                throw new InvalidOperationException("心情/名望工具没有覆盖任意 NPC 与太吾");

            // 群聊使用与单聊相同的完整现场能力；缺少唯一当前对手的原生约战与梳妆入口被剔除，
            // 真实副作用仍由父事务、动作仲裁和权威在场策略限制。
            var group = ConversationToolRouter.CreateGroup(new ToolContext
            {
                IsMerchant = true,
                InSect = true,
                CanStartCombat = true, // CreateGroup 必须强制关闭本地单聊专属约战。
                Remote = false,
            });
            RequireValidDefinitions("群聊运行时路由工具面", group.Tools);
            RequireExactNames("群聊运行时路由工具面", group.Tools,
                ExpectedConversationTools.Where(x => x != "start_combat"
                    && x != "change_appearance" && x != "offer_commission"));

            var roster = new HashSet<int> { 11, 12 };
            var authoritativePresence = new HashSet<int> { 11, 12 };
            if (!GroupPhysicalPresencePolicy.Allows(1, new[] { 11, 12, 1 }, roster,
                authoritativePresence, out _, out _))
                throw new InvalidOperationException("群聊运行时物理策略错误拒绝在场 roster 成员");
            authoritativePresence.Remove(12);
            if (GroupPhysicalPresencePolicy.Allows(1, new[] { 12 }, roster,
                authoritativePresence, out _, out _))
                throw new InvalidOperationException("群聊运行时物理策略放行缺席成员");

            var remote = ToolRegistry.BuildConversationTools(new ToolContext
            {
                IsMerchant = true,
                InSect = true,
                CanStartCombat = true,
                Remote = true,
            });
            RequireValidDefinitions("远程工具面", remote);
            var remoteNames = new HashSet<string>(remote.Select(x => x.Name), StringComparer.Ordinal);
            var leaked = RemotePhysicalTools.Where(remoteNames.Contains).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (leaked.Length != 0)
                throw new InvalidOperationException("远程工具面泄漏物理动作: " + string.Join(",", leaked));
            string remoteSchema = Newtonsoft.Json.JsonConvert.SerializeObject(remote);
            string[] advertisedBlocked = RemotePhysicalTools
                .Where(name => Regex.IsMatch(
                    remoteSchema,
                    "(?<![A-Za-z0-9_])" + Regex.Escape(name) + "(?![A-Za-z0-9_])",
                    RegexOptions.CultureInvariant))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (advertisedBlocked.Length != 0)
                throw new InvalidOperationException("远程查询说明/行动目录仍诱导不可用物理动作: "
                    + string.Join(",", advertisedBlocked));
            ToolDef remoteBlock = remote.FirstOrDefault(x => x.Name == "query_current_block");
            if (remoteBlock == null
                || !remoteBlock.Description.Contains("仇敌")
                || !remoteBlock.Description.Contains("#人物编号")
                || !remoteBlock.Description.Contains("分别列出你身边和太吾身边")
                || !remoteBlock.Description.Contains("当前千里传音边界"))
                throw new InvalidOperationException("远程现场名单丢失完整检索语义或实时物理边界");

            var routed = ConversationToolRouter.Create(
                new ToolContext { IsMerchant = true, InSect = true, CanStartCombat = true, CanOpenGrooming = true });
            RequireValidDefinitions("路由后工具面", routed.Tools);
            RequireExactNames("路由后完整工具面", routed.Tools, ExpectedConversationTools);
            var greeting = ConversationToolRouter.Create(
                new ToolContext { IsMerchant = true, InSect = true, CanStartCombat = true, CanOpenGrooming = true });
            if (!routed.Tools.Select(x => x.Name).SequenceEqual(greeting.Tools.Select(x => x.Name)))
                throw new InvalidOperationException("不同玩家措辞改变了稳定工具面");
            if (routed.Tools.Any(x => string.Equals(x.Name, "request_capability", StringComparison.Ordinal)))
                throw new InvalidOperationException("稳定工具面不应再包含 request_capability");

            RequireProgressiveSkillContracts();
            RequireSourceDispatchParity();
            RequireMonthlyAgentSourceContracts();
            RequireLlmConcurrencyContracts();

            Console.WriteLine("  [OK] 全 Mod 工具面精确契约通过");
        }

        private static void RequireLlmConcurrencyContracts()
        {
            if (LlmWorkloadPolicy.Classify("过月江湖事件 Agent") != LlmWorkloadKind.MonthlyAgent
                || LlmWorkloadPolicy.Classify("同道过月 Agent") != LlmWorkloadKind.MonthlyAgent)
                throw new InvalidOperationException("两类过月 Agent 没有进入可借满三槽的月度车道");
            foreach (string tag in new[] { "画像蒸馏", "长期记忆整理", "旧梗概", "对话整理", "灵儿·主动" })
                if (LlmWorkloadPolicy.Classify(tag) != LlmWorkloadKind.Background)
                    throw new InvalidOperationException("非月度后台工作流误离开两槽车道: " + tag);
            foreach (string tag in new[] { "单聊", "群聊", "代笔", "代笔·群聊", "对话召回" })
                if (LlmWorkloadPolicy.Classify(tag) != LlmWorkloadKind.Foreground)
                    throw new InvalidOperationException("玩家等待的前台工作流没有进入优先车道: " + tag);

            var gate = new LlmPriorityConcurrencyGate(3);
            var activeMonthly = new[]
            {
                gate.AcquireAsync(LlmWorkloadKind.MonthlyAgent, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                gate.AcquireAsync(LlmWorkloadKind.MonthlyAgent, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                gate.AcquireAsync(LlmWorkloadKind.MonthlyAgent, CancellationToken.None)
                    .GetAwaiter().GetResult(),
            };
            if (gate.InUse != 3)
                throw new InvalidOperationException("过月 Agent 不能借满三个全局 provider 槽");

            Task<LlmPriorityConcurrencyGate.Lease> queuedMonthly =
                gate.AcquireAsync(LlmWorkloadKind.MonthlyAgent, CancellationToken.None);
            Task<LlmPriorityConcurrencyGate.Lease> queuedForeground =
                gate.AcquireAsync(LlmWorkloadKind.Foreground, CancellationToken.None);
            if (queuedMonthly.IsCompleted || queuedForeground.IsCompleted)
                throw new InvalidOperationException("满载时并发闸没有正确排队");

            activeMonthly[0].Dispose();
            if (!queuedForeground.IsCompleted || queuedMonthly.IsCompleted)
                throw new InvalidOperationException("前台进入等待后，月度候补抢走了下一枚空槽");
            var foregroundLease = queuedForeground.GetAwaiter().GetResult();
            foregroundLease.Dispose();
            if (!queuedMonthly.IsCompleted)
                throw new InvalidOperationException("前台完成后月度候补没有继续运行");
            var fourthMonthly = queuedMonthly.GetAwaiter().GetResult();
            activeMonthly[1].Dispose();
            activeMonthly[2].Dispose();
            fourthMonthly.Dispose();
            if (gate.InUse != 0)
                throw new InvalidOperationException("全局并发租约完成后没有全部归还");

            var cancelGate = new LlmPriorityConcurrencyGate(1);
            var occupied = cancelGate.AcquireAsync(
                LlmWorkloadKind.MonthlyAgent, CancellationToken.None).GetAwaiter().GetResult();
            using (var canceled = new CancellationTokenSource())
            {
                Task<LlmPriorityConcurrencyGate.Lease> canceledMonthly =
                    cancelGate.AcquireAsync(LlmWorkloadKind.MonthlyAgent, canceled.Token);
                canceled.Cancel();
                try
                {
                    canceledMonthly.GetAwaiter().GetResult();
                    throw new InvalidOperationException("已取消的月度候补仍取得了并发租约");
                }
                catch (OperationCanceledException) { }
                if (cancelGate.WaitingRegular != 0)
                    throw new InvalidOperationException("取消后的月度候补仍滞留并发队列");
            }
            Task<LlmPriorityConcurrencyGate.Lease> afterCancel =
                cancelGate.AcquireAsync(LlmWorkloadKind.Foreground, CancellationToken.None);
            occupied.Dispose();
            if (!afterCancel.IsCompleted)
                throw new InvalidOperationException("取消候补泄漏槽位，阻塞后续前台请求");
            afterCancel.GetAwaiter().GetResult().Dispose();

            var fairnessGate = new LlmPriorityConcurrencyGate(1);
            var fairnessOccupied = fairnessGate.AcquireAsync(
                LlmWorkloadKind.MonthlyAgent, CancellationToken.None).GetAwaiter().GetResult();
            Task<LlmPriorityConcurrencyGate.Lease> fairnessMonthly =
                fairnessGate.AcquireAsync(LlmWorkloadKind.MonthlyAgent, CancellationToken.None);
            var fairnessForeground = Enumerable.Range(0, 4)
                .Select(_ => fairnessGate.AcquireAsync(
                    LlmWorkloadKind.Foreground, CancellationToken.None))
                .ToArray();
            fairnessOccupied.Dispose();
            for (int i = 0; i < 3; i++)
            {
                if (!fairnessForeground[i].IsCompleted || fairnessMonthly.IsCompleted)
                    throw new InvalidOperationException(
                        "前台优先突发额度没有按顺序让玩家请求先运行");
                fairnessForeground[i].GetAwaiter().GetResult().Dispose();
            }
            if (!fairnessMonthly.IsCompleted || fairnessForeground[3].IsCompleted)
                throw new InvalidOperationException(
                    "持续前台排队把月度候补永久饿死，或公平轮次顺序错误");
            fairnessMonthly.GetAwaiter().GetResult().Dispose();
            if (!fairnessForeground[3].IsCompleted)
                throw new InvalidOperationException("公平轮次结束后前台候补没有继续运行");
            fairnessForeground[3].GetAwaiter().GetResult().Dispose();
            if (fairnessGate.InUse != 0 || fairnessGate.WaitingRegular != 0
                || fairnessGate.WaitingForeground != 0)
                throw new InvalidOperationException("公平调度测试后仍有租约或候补泄漏");

            for (int i = 0; i < 64; i++)
            {
                var raceGate = new LlmPriorityConcurrencyGate(1);
                var active = raceGate.AcquireAsync(
                    LlmWorkloadKind.MonthlyAgent, CancellationToken.None).GetAwaiter().GetResult();
                using (var raceCancellation = new CancellationTokenSource())
                {
                    Task<LlmPriorityConcurrencyGate.Lease> racing =
                        raceGate.AcquireAsync(LlmWorkloadKind.MonthlyAgent,
                            raceCancellation.Token);
                    Task cancel = Task.Run(() => raceCancellation.Cancel());
                    Task release = Task.Run(() => active.Dispose());
                    Task.WaitAll(cancel, release);
                    try
                    {
                        racing.GetAwaiter().GetResult().Dispose();
                    }
                    catch (OperationCanceledException) { }
                }
                if (raceGate.InUse != 0 || raceGate.WaitingRegular != 0
                    || raceGate.WaitingForeground != 0)
                    throw new InvalidOperationException("取消与释放竞争导致并发租约或候补泄漏");
            }

            string client = ReadRepoFile("src", "JianghuYouling.Core", "Llm",
                "OpenAiCompatibleClient.cs");
            string coordinator = ReadRepoFile("src", "JianghuYouling.Core", "Llm",
                "LlmConcurrencyCoordinator.cs");
            if (Regex.Matches(client,
                    "RunWithWorkflowGateAsync\\(tag, ct, workload =>").Count != 3
                || client.Contains("IsBackgroundWorkflow")
                || !client.Contains("_backgroundGate = new System.Threading.SemaphoreSlim(2, 2)")
                || !client.Contains("new LlmPriorityConcurrencyGate(MaxConcurrentRequests)")
                || !client.Contains("new LlmPriorityConcurrencyGate(MaxOfficialDeepSeekV4ConcurrentRequests)")
                || !client.Contains("new LlmPriorityConcurrencyGate(1)")
                || !client.Contains("_capabilities.IsOfficialDeepSeek && _capabilities.IsDeepSeekV4")
                || !client.Contains("requestGate.AcquireAsync(workload, ct)")
                || Regex.Matches(client,
                    "finally \\{ requestLease\\.Dispose\\(\\); \\}").Count != 2
                || !coordinator.Contains("MaxConsecutiveForegroundGrants = 3")
                || !coordinator.Contains("regularNeedsFairTurn")
                || !coordinator.Contains("if (_foreground.Count > 0 && !regularNeedsFairTurn)")
                || !coordinator.Contains("waiter.Completion.TrySetCanceled()"))
                throw new InvalidOperationException(
                    "三种发送路径没有共用可取消的前台优先协调器，或并发上限发生漂移");
        }

        private static void RequireProgressiveSkillContracts()
        {
            var maximalContext = new ToolContext
            {
                IsMerchant = true,
                InSect = true,
                CanStartCombat = true,
            };
            var consult = ToolRegistry.BuildConversationTools(maximalContext)
                .FirstOrDefault(x => string.Equals(x?.Name, ConversationSkillCatalog.ConsultToolName,
                    StringComparison.Ordinal));
            if (consult == null) throw new InvalidOperationException("完整现场缺少渐进式行动指南入口");

            string[] skillIds = (consult.Parameters?["properties"]?["skill"]?["enum"] as Newtonsoft.Json.Linq.JArray)
                ?.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                ?? Array.Empty<string>();
            string[] requiredSkills =
            {
                "items_exchange", "teaching_books", "relationships", "danger_care_combat",
                "secrets", "travel_personal_change", "merchant_trade", "sect_affairs", "mood_fame"
            };
            foreach (string required in requiredSkills)
            {
                if (!skillIds.Contains(required, StringComparer.Ordinal))
                    throw new InvalidOperationException("完整现场行动指南目录缺失: " + required);
                if (!ConversationSkillCatalog.TryGetResourceInfo(required, out string version,
                        out string resourceName)
                    || string.IsNullOrWhiteSpace(version)
                    || !resourceName.EndsWith(".ActionSkills." + required + ".SKILL.md",
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("行动技能没有独立且可版本化的嵌入资源: " + required);
                if (!ConversationSkillCatalog.TryLoad(required, maximalContext, out string fullSkill)
                    || string.IsNullOrWhiteSpace(fullSkill)
                    || fullSkill.Contains("{{#if") || fullSkill.Contains("{{#else}}")
                    || fullSkill.Contains("{{/if}}"))
                    throw new InvalidOperationException("行动技能不能从独立资源完整渲染: " + required);
            }

            var guideCoveredActions = ExpectedConversationTools.Where(x =>
                x != ConversationSkillCatalog.ConsultToolName && x != "recall_memory"
                && x != "record_reaction" && x != "remember" && x != "offer_commission"
                && !x.StartsWith("query_", StringComparison.Ordinal));
            string[] uncovered = guideCoveredActions
                .Where(x => !ConversationSkillCatalog.CoversAction(x, maximalContext))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (uncovered.Length != 0)
                throw new InvalidOperationException("动作工具没有对应渐进式指南: " + string.Join(",", uncovered));

            if (!ConversationSkillCatalog.TryLoad("items_exchange", maximalContext, out string itemGuide)
                || string.IsNullOrWhiteSpace(itemGuide)
                || !itemGuide.Contains("query_person_items") || !itemGuide.Contains("太吾不能借一句话")
                || !itemGuide.Contains("玩家不必使用固定口令")
                || !itemGuide.Contains("不能用动作描写代替回执"))
                throw new InvalidOperationException("物品行动指南未保留交换/偷窃关键契约");
            if (!ConversationSkillCatalog.TryLoad("danger_care_combat", maximalContext, out string dangerGuide)
                || !dangerGuide.Contains("query_current_block") || !dangerGuide.Contains("start_combat"))
                throw new InvalidOperationException("危险行动指南未保留在场/原生战斗契约");
            var groupContext = new ToolContext { IsMerchant = true, InSect = true, CanStartCombat = false };
            if (!ConversationSkillCatalog.TryLoad("danger_care_combat", groupContext, out string groupDangerGuide)
                || !groupDangerGuide.Contains("当前场景没有 start_combat")
                || !groupDangerGuide.Contains("kill、capture、poison、heal、detox、regulate_breath 是否可用只看当前真实工具表")
                || groupDangerGuide.Contains("不得用杀伤工具")
                || groupDangerGuide.Contains("正文说完后由太吾确认进入"))
                throw new InvalidOperationException("行动技能的现场条件模板没有正确关闭群聊约战");
            if (!ConversationSkillCatalog.TryLoadForAction("barter", maximalContext,
                    out string recoverySkillId, out string recoveryGuide)
                || recoverySkillId != "items_exchange" || !recoveryGuide.Contains("原子交换"))
                throw new InvalidOperationException("明确失败不能按实际动作取得恢复指南");

            RequireSemanticSkillCatalogContracts(maximalContext);
            RequireComprehensiveActionContracts(maximalContext);

            var ordinary = new ToolContext();
            var ordinarySkills = new HashSet<string>(ConversationSkillCatalog.AvailableSkillIds(ordinary),
                StringComparer.Ordinal);
            if (ordinarySkills.Contains("merchant_trade") || ordinarySkills.Contains("sect_affairs"))
                throw new InvalidOperationException("普通 NPC 行动指南泄漏商人/门派专属流程");

            var remote = new ToolContext { Remote = true, IsMerchant = true, InSect = true, CanStartCombat = true };
            var remoteSkills = new HashSet<string>(ConversationSkillCatalog.AvailableSkillIds(remote),
                StringComparer.Ordinal);
            foreach (string expected in new[] { "relationships", "travel_personal_change",
                "secrets", "sect_affairs", "teaching_books", "danger_care_combat" })
                if (!remoteSkills.Contains(expected))
                    throw new InvalidOperationException("远程场景误删仍有可调用动作的行动技能: " + expected);
            foreach (string blocked in new[]
            {
                "merchant_trade"
            })
                if (remoteSkills.Contains(blocked))
                    throw new InvalidOperationException("远程场景仍提供全部动作均被移除的行动技能: " + blocked);
            if (!ConversationSkillCatalog.TryLoad("relationships", remote, out string remoteRelationGuide)
                || !remoteRelationGuide.Contains("当前是千里传音")
                || remoteRelationGuide.Contains("它不是玩家单方面命令"))
                throw new InvalidOperationException("行动技能的远程条件模板没有正确渲染");
            if (ConversationSkillCatalog.TryLoad("items_exchange", remote, out _)
                || ConversationSkillCatalog.TryLoad("merchant_trade", remote, out _))
                throw new InvalidOperationException("远程现场行动技能未按仍可用动作精确保留");
            if (!ConversationSkillCatalog.TryLoad("danger_care_combat", remote,
                    out string remoteDangerGuide)
                || !remoteDangerGuide.Contains("query_health_status")
                || !remoteDangerGuide.Contains("当前是千里传音"))
                throw new InvalidOperationException("远程危险技能没有保留 NPC 自查身体状态能力");
            if (ConversationSkillCatalog.TryLoad("not_a_skill", maximalContext, out _))
                throw new InvalidOperationException("未知行动指南没有失败关闭");
            if (ConversationSkillCatalog.TryLoadForAction("not_an_action", maximalContext, out _, out _))
                throw new InvalidOperationException("未知动作没有失败关闭指南映射");

            string[] companionSkillActions = ExpectedCompanionMonthlyTools
                .Where(x => x != "no_action"
                    && x != "remember"
                    && x != "query_person"
                    && x != "query_relationship"
                    && x != "query_health_status").ToArray();
            string[] uncoveredCompanionActions = companionSkillActions
                .Where(x => !ConversationSkillCatalog.CoversCompanionAction(x))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (uncoveredCompanionActions.Length != 0)
                throw new InvalidOperationException("同道主动行事动作没有对应技能: "
                    + string.Join(",", uncoveredCompanionActions));
            string companionBundle = ConversationSkillCatalog.BuildCompanionMonthlySkillBundle(
                companionSkillActions);
            if (string.IsNullOrWhiteSpace(companionBundle)
                || !companionBundle.Contains("同道主动行事技能：赠取、交换与物品处置")
                || !companionBundle.Contains("同道主动行事技能：危险行动、身体照料与约战")
                || !companionBundle.Contains("场景：同道过月主动行事")
                || !companionBundle.Contains("无需等待太吾当月提出命令")
                || !companionBundle.Contains("不能替代这里的月度触发条件")
                || companionBundle.Contains("query_current_block")
                || companionBundle.Contains("商人报价与成交"))
                throw new InvalidOperationException("同道主动行事没有按自身工具面装配专用技能参考");
            string healOnlyBundle = ConversationSkillCatalog.BuildCompanionMonthlySkillBundle(new[] { "heal" });
            if (!healOnlyBundle.Contains("危险行动、身体照料与约战")
                || healOnlyBundle.Contains("赠取、交换与物品处置"))
                throw new InvalidOperationException("同道主动行事技能没有按实际可用工具选择");
            if (!string.IsNullOrEmpty(ConversationSkillCatalog.BuildCompanionMonthlySkillBundle(
                new[] { "no_action", "remember", "not_a_tool" })))
                throw new InvalidOperationException("同道主动行事跨技能规则或未知工具误加载行动技能");

            string[] monthlyEventSkillActions = ExpectedMonthlyEventActionTools
                .Where(x => x != "event_taiwu_fame").ToArray();
            string[] uncoveredMonthlyEventActions = monthlyEventSkillActions
                .Where(x => !ConversationSkillCatalog.CoversMonthlyEventAction(x))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (uncoveredMonthlyEventActions.Length != 0)
                throw new InvalidOperationException("过月江湖事件动作没有对应技能: "
                    + string.Join(",", uncoveredMonthlyEventActions));
            string monthlyEventBundle = ConversationSkillCatalog.BuildMonthlyEventSkillBundle(
                monthlyEventSkillActions);
            if (string.IsNullOrWhiteSpace(monthlyEventBundle)
                || !monthlyEventBundle.Contains("过月江湖事件技能：赠取、交换与物品处置")
                || !monthlyEventBundle.Contains("过月江湖事件技能：危险行动、身体照料与约战")
                || !monthlyEventBundle.Contains("场景：过月江湖事件")
                || !monthlyEventBundle.Contains("不是聊天应答，也不是同道个人日程")
                || !monthlyEventBundle.Contains("触发条件不能直接搬来")
                || !monthlyEventBundle.Contains("预检拒绝不是已经发生的失败")
                || monthlyEventBundle.Contains("query_current_block")
                || monthlyEventBundle.Contains("商人报价与成交"))
                throw new InvalidOperationException("过月江湖事件没有按自身工具面装配查询/预检技能参考");
            string eventSecretOnly = ConversationSkillCatalog.BuildMonthlyEventSkillBundle(
                new[] { "event_secret" });
            if (!eventSecretOnly.Contains("秘闻的吐露与承接")
                || eventSecretOnly.Contains("赠取、交换与物品处置"))
                throw new InvalidOperationException("过月江湖事件技能没有按实际可用工具选择");
            if (!string.IsNullOrEmpty(ConversationSkillCatalog.BuildMonthlyEventSkillBundle(
                new[] { "event_query_person", "event_taiwu_fame", "not_a_tool" })))
                throw new InvalidOperationException("过月江湖事件查询/名望或未知工具误加载行动技能");
        }

        private static void RequireSemanticSkillCatalogContracts(ToolContext maximalContext)
        {
            ToolDef consult = ConversationSkillCatalog.BuildConsultTool(maximalContext);
            if (consult == null || !string.Equals(consult.Name, ConversationSkillCatalog.ConsultToolName,
                    StringComparison.Ordinal)
                || !consult.Description.Contains("按语义按需读取")
                || !consult.Description.Contains("不依赖固定口令")
                || !consult.Description.Contains("完整现场工具表仍然可用"))
                throw new InvalidOperationException("行动技能目录没有把语义选择、稳定工具面与指南边界写清");

            if (!ConversationSkillCatalog.TryLoad("items_exchange", maximalContext, out string chatGuide)
                || !chatGuide.Contains("即时聊天行动技能")
                || !chatGuide.Contains("不要套用同道过月或江湖事件的主动触发条件")
                || !chatGuide.Contains("即时聊天的语义触发"))
                throw new InvalidOperationException("聊天行动技能没有与两类过月触发语境明确隔离");

            if (!ConversationSkillCatalog.TryLoadForNpcInitiated("items_exchange", maximalContext,
                    out string proactiveGuide)
                || !proactiveGuide.Contains("NPC 主动联系时的自主行动参考")
                || !proactiveGuide.Contains("不要求调用任何工具")
                || !proactiveGuide.Contains("贪图贵重物")
                || proactiveGuide.Contains("三项实质行为")
                || proactiveGuide.Contains("gift_item"))
                throw new InvalidOperationException("NPC 主动消息没有按需借用自主行动动机，或误继承了过月完成条件/工具名");
            if (!ConversationSkillCatalog.TryLoadForNpcInitiatedAction("gift", maximalContext,
                    out string proactiveSkillId, out string proactiveGiftGuide)
                || proactiveSkillId != "items_exchange"
                || !proactiveGiftGuide.Contains("gift(type=silver)"))
                throw new InvalidOperationException("NPC 主动消息无法按普通单聊动作加载对应的主动行动技能");
            if (ConversationSkillCatalog.TryLoadForNpcInitiatedAction("trade", maximalContext,
                    out _, out _))
                throw new InvalidOperationException("NPC 主动消息按需 Skill 仍能加载需要太吾确认成交的动作");

            string[] proactiveAllowed = { "recall_memory", "query_person", "consult_action_guide", "gift", "kill", "remember", "offer_commission" };
            if (proactiveAllowed.Any(x => !ToolRegistry.IsNpcInitiatedToolAllowed(x)))
                throw new InvalidOperationException("NPC 主动消息错误裁掉了查询、技能、记忆或自主行动工具");
            string[] proactiveDenied = { "taiwu_give_item", "taiwu_teach", "taiwu_write_book", "taiwu_tell_secret", "trade", "matchmake", "spend_night", "record_reaction", "start_combat", "change_appearance", "use_item" };
            if (proactiveDenied.Any(ToolRegistry.IsNpcInitiatedToolAllowed))
                throw new InvalidOperationException("NPC 主动消息仍能替太吾行动、越过玩家同意或暴露停用工具");
            var proactiveContext = new ToolContext
            {
                IsMerchant = true,
                InSect = true,
                CanStartCombat = true,
                CanOpenGrooming = true,
                NpcInitiated = true,
            };
            List<ToolDef> proactiveTools = ToolRegistry.BuildConversationTools(proactiveContext);
            if (proactiveDenied.Any(name => proactiveTools.Any(x => x != null && x.Name == name)))
                throw new InvalidOperationException("NPC 主动消息生产工具表仍包含替太吾决定或需要玩家确认的动作");
            ToolDef proactiveRelation = proactiveTools.FirstOrDefault(x => x?.Name == "set_relation");
            string[] proactiveRelationActions = proactiveRelation?.Parameters?["properties"]?["action"]?["enum"]?
                .Values<string>().ToArray() ?? Array.Empty<string>();
            if (!proactiveRelationActions.SequenceEqual(new[] { "lover", "recognize" }))
                throw new InvalidOperationException("NPC 主动消息关系 schema 没有限定为单方面爱慕或归心");
            if (ConversationSkillCatalog.AvailableSkillIds(proactiveContext).Contains("merchant_trade",
                    StringComparer.Ordinal))
                throw new InvalidOperationException("NPC 主动消息仍暴露本轮无法成交的商人行动技能");

            string[] expected =
            {
                "items_exchange", "teaching_books", "relationships", "danger_care_combat",
                "secrets", "travel_personal_change", "mood_fame", "merchant_trade", "sect_affairs",
            };
            string[] ids = ConversationSkillCatalog.AvailableSkillIds(maximalContext).ToArray();
            if (!ids.SequenceEqual(expected))
                throw new InvalidOperationException("行动技能目录顺序或现场过滤异常: " + string.Join(",", ids));
            foreach (string id in expected)
            {
                if (!consult.Description.Contains(id + "(v")
                    || !ConversationSkillCatalog.TryLoad(id, maximalContext, out string guide)
                    || string.IsNullOrWhiteSpace(guide)
                    || !guide.Contains("只约束如何使用当前已经提供的工具"))
                    throw new InvalidOperationException("行动技能未完整暴露给模型按语义选择: " + id);
            }

            var remoteContext = new ToolContext
            {
                IsMerchant = true, InSect = true, CanStartCombat = false, Remote = true,
            };
            ToolDef remoteConsult = ConversationSkillCatalog.BuildConsultTool(remoteContext);
            if (!ConversationSkillCatalog.AvailableSkillIds(remoteContext).Contains("danger_care_combat",
                    StringComparer.Ordinal)
                || !remoteConsult.Description.Contains("danger_care_combat(v"))
                throw new InvalidOperationException("远程现场没有暴露仍可用于自查身体状态的危险指南");
        }

        private static void RequireComprehensiveActionContracts(ToolContext maximalContext)
        {
            var tools = ToolRegistry.BuildConversationTools(maximalContext)
                .Where(x => x != null).ToDictionary(x => x.Name, x => x,
                    StringComparer.Ordinal);
            var requiredHints = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["gift"] = "query_npc_items",
                ["barter"] = "query_person_items",
                ["teach"] = "query_person",
                ["write_book"] = "query_npc_skills",
                ["tell_secret"] = "query_npc_secrets",
                ["taiwu_give_item"] = "query_taiwu_items",
                ["taiwu_teach"] = "query_taiwu_skills",
                ["taiwu_write_book"] = "query_taiwu_skills",
                ["trade"] = "query_merchant_goods",
                ["sect_support"] = "query_sect_lore",
                ["change_equipment"] = "query_npc_items",
                ["goto_place"] = "query_place",
                ["kill"] = "query_current_block",
                ["capture"] = "query_current_block",
                ["poison"] = "query_current_block",
                ["heal"] = "query_current_block",
                ["detox"] = "query_current_block",
                ["regulate_breath"] = "query_current_block",
                ["adjust_third_party_favor"] = "query_person",
                ["set_relation"] = "query_npc_relationships",
                ["dissolve_relation"] = "query_npc_relationships",
                ["matchmake"] = "query_person",
                ["relate_npc"] = "query_person",
                ["set_enmity"] = "query_npc_relationships",
                ["flip_practice"] = "query_npc_skills",
                ["taiwu_tell_secret"] = "query_taiwu_secrets",
            };
            string[] missingHints = requiredHints
                .Where(pair => !tools.TryGetValue(pair.Key, out ToolDef tool)
                    || string.IsNullOrWhiteSpace(tool.Description)
                    || !tool.Description.Contains(pair.Value))
                .Select(pair => pair.Key + " -> " + pair.Value)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            if (missingHints.Length != 0)
                throw new InvalidOperationException("动作工具的常驻契约缺少必要查询提示: "
                    + string.Join(", ", missingHints));

            ToolDef currentBlock = tools["query_current_block"];
            if (currentBlock.Description.Length < 180
                || !currentBlock.Description.Contains("仇敌")
                || !currentBlock.Description.Contains("陌生人")
                || !currentBlock.Description.Contains("#人物编号")
                || !currentBlock.Description.Contains("不能把两个远隔现场混成一个"))
                throw new InvalidOperationException("现场人物查询被压成简略说明，无法完整覆盖敌对、同名与远程边界");

            ToolDef kill = tools["kill"];
            string killTarget = kill.Parameters?["properties"]?["target"]?["description"]?.ToString() ?? "";
            if (!kill.Description.Contains("query_person/query_npc_status")
                || !kill.Description.Contains("goto_place")
                || !kill.Description.Contains("价值最高的一件")
                || !killTarget.Contains("#人物编号"))
                throw new InvalidOperationException("杀人工具缺少稳定查询、寻人、战利品或唯一目标契约");
            if (!ConversationSkillCatalog.TryLoad("danger_care_combat", maximalContext,
                    out string dangerGuide)
                || !dangerGuide.Contains("仇怨、除恶、自保、利益")
                || !dangerGuide.Contains("先 query_current_block")
                || !dangerGuide.Contains("query_npc_status 与 query_person")
                || !dangerGuide.Contains("价值最高的一件"))
                throw new InvalidOperationException("杀人的详细语义触发与步骤没有留在行动技能中");
        }

        private static void RequireSourceDispatchParity()
        {
            string talk = ReadRepoFile("src", "JianghuYouling.Frontend", "Talk", "TalkOrchestrator.cs");
            string talkExecutor = Slice(talk,
                "private IEnumerator ExecuteTool(", "private IEnumerator RefreshTaiwuHoldings(");
            RequireCases("NPC/群聊正式执行器", talkExecutor, ExpectedConversationTools);
            if (!talk.Contains("JHYL_DEBUG_TOOL_REGISTRY_PARITY")
                || !talk.Contains("yield return ExecuteTool(toolName"))
                throw new InvalidOperationException("调试正式工具入口没有复用同一执行器");
            if (talk.IndexOf("PlayerExplicitlyOffersTaiwuWrittenBook", StringComparison.Ordinal) >= 0
                || talk.IndexOf("QueryHostility(npc, first", StringComparison.Ordinal) >= 0
                || talk.IndexOf("IsAuthorized(toolName, _currentPlayerInput", StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("聊天执行层仍按玩家关键词或预存仇敌关系替模型选择/授权动作");

            string hostileInvariant = ReadRepoFile("src", "JianghuYouling.Core", "Tools",
                "HighRiskActionAuthorization.cs");
            if (hostileInvariant.IndexOf("playerInput", StringComparison.Ordinal) >= 0
                || hostileInvariant.IndexOf("ActionCues", StringComparison.Ordinal) >= 0
                || hostileInvariant.IndexOf("hostility", StringComparison.OrdinalIgnoreCase) >= 0
                || hostileInvariant.IndexOf("targetIsTaiwu", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("危险行动硬边界仍混入本地意图触发，或丢失太吾目标不变量");

            string assistant = ReadRepoFile("src", "JianghuYouling.Frontend", "Talk", "AssistantOrchestrator.cs");
            string assistantDefinitions = Slice(assistant, "private static List<ToolDef> BuildTools()", "public static string Persona()");
            string assistantExecutor = Slice(assistant, "private IEnumerator ExecuteTool(", "private static string ProgressLabel(");
            RequireDefinitionsAndCases("灵儿", assistantDefinitions, assistantExecutor, ExpectedAssistantTools);
            RequireExactCases("灵儿执行器", assistantExecutor, ExpectedAssistantTools);

            string companion = ReadRepoFile("src", "JianghuYouling.Frontend", "Game", "CompanionMonthlyActions.cs");
            string companionDefinitions = Slice(companion,
                "private static List<ToolDef> BuildCompanionTools(", "private static string DescribeCompanionTools(");
            string companionExecutor = Slice(companion,
                "private static IEnumerator ExecuteCompanionTool(", "private static IEnumerator ExecuteBooleanCompanionMutation(");
            RequireDefinitionsAndCases("同道过月", companionDefinitions, companionExecutor,
                ExpectedCompanionMonthlyTools);
            // set_relation 在同一执行器内用嵌套 switch 映射允许的关系动作；精确 case
            // 契约必须把这些值也纳入，尤其不能出现自主离队的 leave。
            RequireExactCases("同道过月执行器", companionExecutor,
                ExpectedCompanionMonthlyTools.Concat(new[]
                {
                    // Legacy in-flight batches may still contain the previously exposed local
                    // remember step. Keep its executor recoverable without advertising it to
                    // new monthly agents; new outcome memory is committed automatically.
                    "remember", "use_item",
                    "befriend", "swear_sibling", "apprentice", "take_disciple",
                    "lover", "spouse", "recognize", "follow",
                }));

            string durableClassifier = Slice(talk,
                "private static bool IsBackendDurableMutationTool(", "private static string ResolveFailMsg(");
            RequireExactCases("NPC/群聊持久副作用分类器", durableClassifier,
                ExpectedBackendDurableConversationTools);
            string postReceiptClassifier = Slice(talk,
                "private static readonly System.Collections.Generic.HashSet<string> PostReceiptProseTools",
                "private static bool RequiresPostReceiptProse(");
            string[] visibleMutationTools = ExpectedBackendDurableConversationTools
                .Where(x => x != "record_reaction").ToArray();
            string[] missingPostReceipt = visibleMutationTools.Where(x =>
                    !postReceiptClassifier.Contains("\"" + x + "\""))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missingPostReceipt.Length != 0)
                throw new InvalidOperationException("可见动作没有强制在权威回执后生成正文: "
                    + string.Join(",", missingPostReceipt));
            string groupGuard = Slice(talk,
                "private IEnumerator BuildGroupDispatchGuard(", "private static string ActMsg(");
            RequireCases("NPC/群聊动作仲裁", groupGuard,
                ExpectedBackendDurableConversationTools.Concat(new[] { "start_combat" }));

            string effects = ReadRepoFile("src", "JianghuYouling.Frontend", "Effects", "EffectHandler.cs");
            string backend = ReadRepoFile("src", "JianghuYouling.Backend", "BackendPluginMain.cs");
            var gmOps = Regex.Matches(effects, "CallGm\\(\\\"([^\\\"]+)\\\"")
                .Cast<Match>().Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
            var missingBackend = gmOps.Where(x => !backend.Contains("case \"" + x + "\":"))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missingBackend.Length != 0)
                throw new InvalidOperationException("EffectHandler→后端 GM 路由缺口: " + string.Join(",", missingBackend));

            if (!backend.Contains("default: return new sbyte[0];")
                || !backend.Contains("for (sbyte slot = 0; slot < 17; slot++)")
                || !backend.Contains("case \"carrier\": return new sbyte[] { 11, 12, 13 };")
                || !effects.Contains("ApplyEquipTakeOff"))
                throw new InvalidOperationException("最新版装备槽/失败关闭契约不完整");
        }

        private static void RequireMonthlyAgentSourceContracts()
        {
            string monthly = ReadRepoFile("src", "JianghuYouling.Frontend", "Game",
                "MonthlyEventGenerator.cs");
            string definitions = Slice(monthly,
                "static List<ToolDef> BuildMonthlyEventAgentTools(",
                "static string BuildVerifiedHostilityContext(");
            string loop = Slice(monthly,
                "static IEnumerator RunMonthlyEventAgent(",
                "static bool TryAcceptMonthlyAgentNarrative(");
            string queryClassifier = Slice(monthly,
                "static bool IsMonthlyEventQueryTool(",
                "static IEnumerator ExecuteMonthlyEventQuery(");
            string queryExecutor = Slice(monthly,
                "static IEnumerator ExecuteMonthlyEventQuery(",
                "static IEnumerator ExecuteMonthlyAgentTool(");
            string actionExecutor = Slice(monthly,
                "static IEnumerator ExecuteEventTool(",
                "static bool IsUnconfirmedRpcMessage(");
            string companion = ReadRepoFile("src", "JianghuYouling.Frontend", "Game",
                "CompanionMonthlyActions.cs");
            string companionDefinitions = Slice(companion,
                "private static List<ToolDef> BuildCompanionTools(",
                "private static string DescribeCompanionTools(");
            string companionExecutor = Slice(companion,
                "private static IEnumerator ExecuteCompanionTool(",
                "private static IEnumerator ExecuteBooleanCompanionMutation(");

            string[] allEventTools = ExpectedMonthlyEventQueryTools
                .Concat(ExpectedMonthlyEventActionTools).ToArray();
            string[] missingDefinitions = allEventTools.Where(x =>
                    !definitions.Contains("ToolDef.Of(\"" + x + "\""))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missingDefinitions.Length != 0)
                throw new InvalidOperationException("过月江湖事件工具定义缺失: "
                    + string.Join(",", missingDefinitions));
            if (!Regex.IsMatch(definitions,
                    "ToolDef\\.Of\\(\"query_person\"[\\s\\S]{0,500}\\(\"name\", ToolDef\\.Str"))
                throw new InvalidOperationException("过月江湖事件通用人物查询没有与其他 Agent 统一使用 name 参数");
            RequireExactCases("过月江湖事件动作执行器", actionExecutor,
                ExpectedMonthlyEventActionTools.Concat(new[] { "event_use_item" }));

            string[] missingQueries = ExpectedMonthlyEventQueryTools.Where(x =>
                    !queryClassifier.Contains("\"" + x + "\""))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missingQueries.Length != 0)
                throw new InvalidOperationException("过月江湖事件查询分类器缺失: "
                    + string.Join(",", missingQueries));
            if (!queryExecutor.Contains("QueryPlaces(")
                || !queryExecutor.Contains("NpcSnapshotReader.FetchDisplayOnly(")
                || !queryExecutor.Contains("FetchGiftables(")
                || !queryExecutor.Contains("FetchInjuryState(")
                || !queryExecutor.Contains("FetchSkills(")
                || !queryExecutor.Contains("FetchLifeSkills(")
                || !queryExecutor.Contains("QueryNpcRelations("))
                throw new InvalidOperationException("过月江湖事件只读查询没有覆盖地点、健康伤势、物品、技能和关系权威切片");
            if (!loop.Contains("BuildMonthlyEventSkillBundle(monthlyEventToolNames)")
                || !loop.Contains("TryValidateMonthlyEventQueryDependencies(")
                || !loop.Contains("query_auto_hydrated_and_executing")
                || !loop.Contains("query_auto_hydrated_incomplete")
                || !loop.Contains("InvalidateMonthlyEventQueryState(call.Name")
                || !queryClassifier.Contains("static void InvalidateMonthlyEventQueryState(")
                || !queryClassifier.Contains("queryResultCache.Remove(key)")
                || !monthly.Contains("int characterId = roster[personIndex - 1].Key;")
                || !Regex.IsMatch(monthly,
                    @"person=""\s*\+\s*personIndex\s*\+\s*"",section=""\s*\+\s*section")
                || !monthly.Contains("QueryCanDiscloseSecret((SecretInformationId)secretId, actor.Key, target.Key"))
                throw new InvalidOperationException("过月江湖事件没有落实技能→查询→预检→执行→失效旧查询的统一循环");
            string[] queryDependencyMatrix =
            {
                "event_relate / event_enmity / event_favor / event_dissolve：a.relations + b.relations",
                "event_matchmake / event_spend_night：a.relations + b.relations + a.status + b.status",
                "event_gift / event_gift_silver / event_barter / event_equipment：a.items + b.items",
                "event_steal：b.items",
                "event_teach：a.skills + b.skills",
                "event_write_book / event_flip_practice：a.skills",
                "event_secret：a.secrets，并且 secret_target=b",
                "event_heal / event_detox / event_regulate_breath：a.status + b.status + a.relations + b.relations",
                "event_kill / event_poison / event_capture：a.status + b.status",
                "event_feature：a.status",
                "event_goto：a.status + event_query_place(keyword=确切目的地)",
            };
            string[] missingDependencyGuidance = queryDependencyMatrix
                .Where(x => !monthly.Contains(x)).ToArray();
            if (!monthly.Contains("BuildMonthlyEventQueryDependencyDirective()")
                || !loop.Contains("BuildMonthlyEventQueryDependencyDirective()")
                || !monthly.Contains("同一行动需要的多个只读切片可在同一轮并列查询")
                || !monthly.Contains("代码会自动一次性补齐该动作缺少的全部权威只读前置")
                || !monthly.Contains("全部可靠就同轮执行")
                || missingDependencyGuidance.Length != 0)
                throw new InvalidOperationException("过月江湖事件没有把本地真实前置查询矩阵和同轮批量只读策略交给 Agent: "
                    + string.Join(",", missingDependencyGuidance));
            int autoHydrateStart = loop.IndexOf(
                "bool requiresRecipientSecretSnapshot = string.Equals(call.Name,",
                StringComparison.Ordinal);
            int secretFreezeStart = autoHydrateStart < 0 ? -1
                : loop.IndexOf("if (string.Equals(call.Name, \"event_secret\"",
                    autoHydrateStart, StringComparison.Ordinal);
            string autoHydrateBranch = autoHydrateStart < 0 || secretFreezeStart <= autoHydrateStart
                ? string.Empty : loop.Substring(autoHydrateStart,
                    secretFreezeStart - autoHydrateStart);
            if (!monthly.Contains("private sealed class MonthlyEventPrerequisiteQuery")
                || !queryClassifier.Contains("static IEnumerator HydrateMonthlyEventQuery(")
                || !autoHydrateBranch.Contains(
                    "bool requiresRecipientSecretSnapshot = string.Equals(call.Name,")
                || !autoHydrateBranch.Contains(
                    "if (requiresRecipientSecretSnapshot")
                || !autoHydrateBranch.Contains("foreach (MonthlyEventPrerequisiteQuery prerequisite")
                || !autoHydrateBranch.Contains("yield return HydrateMonthlyEventQuery(")
                || !autoHydrateBranch.Contains("CoalesceMonthlyEventPrerequisites(prerequisiteQueries)")
                || Regex.Matches(autoHydrateBranch,
                    "TryValidateMonthlyEventQueryDependencies\\(").Count < 1
                || !autoHydrateBranch.Contains("query_auto_hydrated_and_executing")
                || !autoHydrateBranch.Contains("query_auto_hydrated_incomplete")
                || autoHydrateBranch.Contains("本轮动作没有派发，不能视为成功"))
                throw new InvalidOperationException(
                    "过月江湖事件缺前置动作没有在同一工具轮自动补齐并复验，或仍强迫 Agent 下一轮重复派发");
            if (!monthly.Contains("Direct actions already enter the authoritative unified preflight")
                || !monthly.Contains("yield return PreflightMonthlyAction(tool, args, roster[a - 1], roster[b - 1], date")
                || !monthly.Contains("yield return BuildEventDispatchEnvelope(tool, args, roster, a, b,"))
                throw new InvalidOperationException(
                    "过月直接动作仍在统一执行预检前重复读取公共查询矩阵，或缺少最终权威校验");
            if (!loop.Contains("var parallelQueryByCall =")
                || !loop.Contains("distinctParallelQueries.Count > 1")
                || !Regex.IsMatch(loop,
                    @"queryHost\.StartCoroutine\s*\(\s*RunMonthlyEventQueryWork\s*\(")
                || !loop.Contains("parallelQueryByCall.TryGetValue(call,")
                || !loop.Contains("messages.Add(LlmMessage.Tool(call.Id,")
                || !queryClassifier.Contains(
                    "private static IEnumerator RunMonthlyEventQueryWork("))
                throw new InvalidOperationException(
                    "过月同轮多人物只读查询仍然串行等待，或并发结果没有按原工具顺序回填");
            if (!queryClassifier.Contains("CancellationToken cancellationToken,")
                || !queryClassifier.Contains("int expectedGeneration, uint expectedWorldId")
                || !queryClassifier.Contains("WorldLifecycle.WorldId != expectedWorldId")
                || !queryClassifier.Contains("!WorldLifecycle.IsSameWorld(expectedGeneration)")
                || !queryClassifier.Contains("work.Error = \"world_or_request_cancelled\"")
                || !queryClassifier.Contains("finally")
                || !queryClassifier.Contains("work.Done = true"))
                throw new InvalidOperationException(
                    "过月并行查询没有绑定世界代次/取消令牌，退出存档后仍可能回写旧世界缓存");
            if (!queryExecutor.Contains("NpcSnapshotReader.FetchDisplayOnly(")
                || queryExecutor.Contains("NpcSnapshotReader.Fetch(characterId,")
                || !queryExecutor.Contains("if (Want(\"secrets\"))")
                || !queryExecutor.Contains("FetchDisclosableSecrets(")
                || !queryExecutor.Contains("FetchShareableSecrets("))
                throw new InvalidOperationException(
                    "过月人物轻量查询仍无条件读取生命经历/秘闻，或收件人秘闻查询未按需执行");
            int incompleteTrace = autoHydrateBranch.IndexOf(
                "\"query_auto_hydrated_incomplete\"", StringComparison.Ordinal);
            int incompleteStop = incompleteTrace < 0 ? -1
                : autoHydrateBranch.IndexOf("continue;", incompleteTrace,
                    StringComparison.Ordinal);
            int executeTrace = autoHydrateBranch.IndexOf(
                "\"query_auto_hydrated_and_executing\"", StringComparison.Ordinal);
            if (incompleteTrace < 0 || incompleteStop <= incompleteTrace
                || executeTrace <= incompleteStop)
                throw new InvalidOperationException(
                    "过月江湖事件自动前置复验失败后没有在真实动作派发前 fail-closed");
            if (!monthly.Contains("static List<MonthlyEventPrerequisiteQuery> CoalesceMonthlyEventPrerequisites(")
                || !monthly.Contains("grouped.Args[\"section\"] = string.Join(\",\", sections.ToArray())")
                || !monthly.Contains("foreach (string rawSection in section.Split(new[] { ',' }"))
                throw new InvalidOperationException(
                    "过月江湖事件同一人物的多栏目自动前置没有合并，仍会重复读取人物基础快照");
            if (!monthly.Contains("foreach (string section in result.SuccessfulSections)")
                || !monthly.Contains("snap == null || !snap.DisplayLoaded")
                || !monthly.Contains("query.SuccessfulSections.Add(\"status\")")
                || !monthly.Contains("query.SuccessfulSections.Add(\"items\")")
                || !monthly.Contains("query.SuccessfulSections.Add(\"skills\")")
                || !monthly.Contains("query.SuccessfulSections.Add(\"secrets\")")
                || !monthly.Contains("query.SuccessfulSections.Add(\"relations\")")
                || !monthly.Contains("if (relationDone && relationsLoaded)"))
                throw new InvalidOperationException("过月人物查询仍可能把超时、未加载或失败栏目登记成已查询证据");
            if (!monthly.Contains("query.CanonicalPlaces.Add(canonical)")
                || !monthly.Contains("queriedPlaces.Contains(canonical)")
                || !monthly.Contains("event_query_place(keyword="))
                throw new InvalidOperationException("过月地点查询证据未绑定 event_goto 的规范化具体地点");
            if (!monthly.Contains("case \"event_heal\":")
                || !monthly.Contains("!state.TargetNeedsHealing")
                || !monthly.Contains("Need(actor, a, \"relations\"); Need(target, b, \"relations\");"))
                throw new InvalidOperationException("过月危险行动没有完整查询双方关系/状态，或健康目标仍会派发疗伤");
            int holdingsRead = queryExecutor.IndexOf("FetchGiftables(characterId, snap)",
                StringComparison.Ordinal);
            int holdingsLineStart = holdingsRead < 0 ? -1
                : queryExecutor.LastIndexOf('\n', holdingsRead);
            string holdingsReadLine = holdingsLineStart < 0 || holdingsRead < 0 ? string.Empty
                : queryExecutor.Substring(holdingsLineStart, holdingsRead - holdingsLineStart);
            if (holdingsRead < 0 || holdingsReadLine.Contains("if (")
                && !holdingsReadLine.Contains("section == \"status\""))
                throw new InvalidOperationException("人物 status 查询会把未读取的毒药/绳索误报为没有");

            int queryBranch = loop.IndexOf("if (requestedQuery)",
                StringComparison.Ordinal);
            int mutationOrdinal = loop.IndexOf("actionOrdinal++;", StringComparison.Ordinal);
            if (queryBranch < 0 || mutationOrdinal < 0 || queryBranch >= mutationOrdinal)
                throw new InvalidOperationException("过月江湖事件查询必须先于真实动作计数分支");
            string queryBranchBody = loop.Substring(queryBranch, mutationOrdinal - queryBranch);
            if (!queryBranchBody.Contains("continue;"))
                throw new InvalidOperationException("过月江湖事件只读查询会误落入真实动作计数");

            if (loop.Contains("actionOrdinal >=") || loop.Contains("maxActions")
                || loop.Contains("MaxPlannedSteps")
                || monthly.Contains("Kind = \"max_actions_per_month\""))
                throw new InvalidOperationException("过月江湖事件仍存在每月真实动作额度");
            if (!monthly.Contains("MonthlyAgentMaxRounds = 24")
                || !loop.Contains("string toolChoice = \"auto\"")
                || !loop.Contains("accepted_without_narrative")
                || loop.Contains("Array.Empty<ToolDef>() : tools")
                || monthly.Contains("actionStageTools")
                || monthly.Contains("MonthlyAgentMaxConsecutiveQueryRounds")
                || monthly.Contains("query_budget_exhausted")
                || !monthly.Contains("bool queryOnlyRound = false")
                || !monthly.Contains("if (requestedQuery) queryOnlyRound = true")
                || !monthly.Contains("MonthlyAgentRequestBudget.TryAcquire"))
                throw new InvalidOperationException("过月江湖事件缺少请求/token 熔断、免正文生成收束或并列只读查询");
            if (!monthly.Contains("MonthlyEventMinimumActions = 3")
                || !monthly.Contains("MonthlyEventMinimumActionCategories = 2")
                || !monthly.Contains("CountsTowardsMonthlyEventActionMinimum")
                || !monthly.Contains("MonthlyEventActionCategory")
                || !monthly.Contains("MonthlyEventActionMinimumSatisfied")
                || !monthly.Contains("三项、两类只是最低线，不是完成条件")
                || !monthly.Contains("只有整条因果已经自然落定")
                || !monthly.Contains("JHYL_MONTHLY_CAUSAL_REVIEW_ONLY_AFTER_NO_TOOL_DRAFT")
                || !loop.Contains("completionState.MarkCausalReviewInstructionDelivered()")
                || !loop.Contains("minimumSatisfiedForReceipt")
                || !loop.Contains("JHYL_MONTHLY_CAUSAL_REVIEW_EMBEDDED"))
                throw new InvalidOperationException("过月江湖事件缺少至少三项、两类最低门或门后因果复核");

            if (!monthly.Contains("RequiresCoLocatedMonthlyAction(tool) && !state.SameValidLocation")
                || !monthly.Contains("state.ActorArea")
                || !monthly.Contains("state.ActorBlock")
                || !monthly.Contains("state.TargetArea")
                || !monthly.Contains("state.TargetBlock")
                || !monthly.Contains("case \"event_gift\":")
                || !monthly.Contains("case \"event_barter\":")
                || !monthly.Contains("case \"event_kill\":")
                || !monthly.Contains("case \"event_poison\":")
                || !monthly.Contains("case \"event_capture\":"))
                throw new InvalidOperationException(
                    "过月当面动作没有使用执行前实时位置快照 fail-closed");

            string sagaStore = ReadRepoFile("src", "JianghuYouling.Frontend",
                "Game", "EventSagaStore.cs");
            if (!monthly.Contains("DispatchEnvelopeBinding.CreateDigest(dispatchEnvelope)")
                || !monthly.Contains(
                    "DispatchEnvelopeBinding.CreateOperationId(")
                || !monthly.Contains("OperationBindingKey = operationBindingKey")
                || !monthly.Contains("DispatchEnvelopeDigest = dispatchEnvelopeDigest")
                || !monthly.Contains("boundEnvelope[\"_tool\"] = tool")
                || !monthly.Contains("EventDispatchEnvelopeBinding.CreateStableKey(")
                || !monthly.Contains("EventDispatchEnvelopeBinding.Matches(saga, journal)")
                || !sagaStore.Contains("public string OperationBindingKey;")
                || !sagaStore.Contains("public string DispatchEnvelopeDigest;")
                || !sagaStore.Contains("envelope[\"_tool\"]")
                || !sagaStore.Contains("outcome.ToolName")
                || !sagaStore.Contains("string expectedKey = CreateStableKey(")
                || !sagaStore.Contains("DispatchEnvelopeBinding.Matches(item.OperationBindingKey")
                || !companion.Contains("DispatchEnvelopeBinding.CreateDigest(envelope)")
                || !companion.Contains(
                    "DispatchEnvelopeBinding.CreateOperationId(")
                || !companion.Contains(
                    "DispatchEnvelopeBinding.Matches(\"companion|\" + entry.MutationKey"))
                throw new InvalidOperationException(
                    "月度 crash recovery 未把完整派发信封摘要与 operationId 交叉绑定");

            if (!monthly.Contains("recipientSecretSelections.Clear();")
                || !monthly.Contains("queriedFacts.Clear();")
                || !monthly.Contains("queryResultCache.Clear();")
                || !monthly.Contains("recipientId != authority.ActorId")
                || !monthly.Contains("recipientId != authority.TargetId")
                || !monthly.Contains("并不知道秘闻内容")
                || !monthly.Contains("BuildAuthoritativeChapterContinuity(saga, checkpoint)")
                || !monthly.Contains("旧版文学正文已隔离")
                || monthly.Contains("saga.Chapters.Add(checkpoint.StoryText)"))
                throw new InvalidOperationException(
                    "秘闻权限缓存/扇出或江湖事件连续性仍可能泄漏旧秘闻与未经验证的展示正文");

            if (!companion.Contains("if (normalized == \"heal\")")
                || !companion.Contains("EffectHandler.QueryHealPreflight(targetId")
                || !companion.Contains("if (!heal.TargetNeedsHealing)")
                || Regex.Matches(companion,
                    "PreflightCompanionPair\\(name, a, snap, target, Current").Count < 2)
                throw new InvalidOperationException("同道过月危险动作/疗伤仍有分支绕过统一人物对预查");

            string backend = ReadRepoFile("src", "JianghuYouling.Backend", "BackendPluginMain.cs");
            if (!backend.Contains("beforeInjuries = target.GetInjuries().GetSum()")
                || !backend.Contains("target.GetLeftMaxHealth()")
                || !backend.Contains("afterHealth == beforeHealth && afterInjuries == beforeInjuries")
                || !backend.Contains("case \"heal_preflight\"")
                || !backend.Contains("target_needs_healing"))
                throw new InvalidOperationException("疗伤后端没有按最新版健康/伤势权威状态预拒并后验确认真实变化");

            // A→B 赠物必须把事件/同道解析出的真实 B 一直传到后端 recipient；旧版曾在
            // 展示 A→B 成功时实际把物品送给太吾，造成玩家看到 B 未收到甚至误以为物品消失。
            if (!actionExecutor.Contains("ApplyGiveItemByName(aid, taiwuId, item, requested,")
                || !actionExecutor.Contains("bid, stableOperationId)")
                || !companionExecutor.Contains("target == snap.TaiwuId ? 0 : target, lease.OperationId")
                || !backend.Contains("int dstId = rcpt > 0 ? rcpt : taiwuId;")
                || !backend.Contains("Character src, dst;")
                || !backend.Contains("TryGetElement_Objects(dstId, out dst)"))
                throw new InvalidOperationException("第三方赠物目标未贯穿事件/同道→RPC→后端收件人链路");
            if (!backend.Contains("ApplyTransferThing(context, npcId, src, dst, new TransferThing")
                || !backend.Contains("TransferInventoryChecked(context, src, dst, thing.Key, thing.Amount, intent)")
                || !backend.Contains("bool fullyTransferred = srcAfter == srcBefore - amount")
                || !backend.Contains("&& dstAfter == dstBefore + amount;")
                || !backend.Contains("TryEnsureInventoryOwner(key, dst.GetId())")
                || !backend.Contains("TryRestoreInventoryPair(context, src, dst, key, srcBefore, dstBefore,"))
                throw new InvalidOperationException("赠物后端缺少收件人入包与赠送者扣除的双向后验确认");

            int unknownStop = loop.IndexOf("if (executed.Unknown)", StringComparison.Ordinal);
            if (!monthly.Contains("此动作明确未成，请按真实原因换方案，不能写成成功") || unknownStop < 0
                || !loop.Substring(unknownStop).Contains("yield break;"))
                throw new InvalidOperationException("过月江湖事件没有做到明确失败续跑、仅未知终态停止");

            if (!definitions.Contains("先预查技能并使用确切名称")
                || !definitions.Contains("先预查持有物并使用确切名称")
                || !definitions.Contains("先分别预查并填写双方确切物名")
                || !actionExecutor.Contains("ApplyTeachSkillId(")
                || !actionExecutor.Contains("ApplyWriteBook("))
                throw new InvalidOperationException("过月江湖事件未锁定确切技能/物品查询与执行契约");
            string eventEnvelope = Slice(monthly,
                "static IEnumerator BuildEventDispatchEnvelope(",
                "static IEnumerator ExecuteEventTool(");
            if (!eventEnvelope.Contains("FetchGiftables(")
                || !eventEnvelope.Contains("StringComparison.OrdinalIgnoreCase"))
                throw new InvalidOperationException("过月江湖事件物品动作没有按权威持有物精确匹配名称");

            string companionLoop = Slice(companion,
                "private static IEnumerator RunOne(",
                "private static bool IsCompanionQueryTool(");
            if (!companionLoop.Contains("本月不设工具动作次数额度")
                || !companion.Contains("MaxAgentRounds = 12")
                || !companion.Contains("MaxJournalStepsPerBatch = 64")
                || !companion.Contains("MonthlyAgentRequestBudget.TryAcquire")
                || !companion.Contains("JHYL_COMPANION_CAUSAL_REVIEW_ONLY_AFTER_NO_TOOL_DRAFT")
                || companion.Contains("ShouldInvokePlanner(")
                || companionLoop.Contains("executionOrdinal >=")
                || companion.Contains("MaxPlannedSteps"))
                throw new InvalidOperationException("同道主动行事仍存在业务动作额度或 journal 六步残留");
            if (!companionLoop.Contains("MonthlyAgentReasoningPolicy.Select(messages, round)")
                || !companionLoop.Contains(
                    "MonthlyAgentRoundRecovery.RecoverAsync(roundReasoning, turn")
                || !companionLoop.Contains("JHYL_COMPANION_LOW_REASONING_RECOVERY")
                || !companionLoop.Contains("同道过月 Agent 轻思考失败恢复")
                || !loop.Contains("MonthlyAgentReasoningPolicy.Select(messages, round)")
                || !loop.Contains(
                    "MonthlyAgentRoundRecovery.RecoverAsync(roundReasoning, turn")
                || !loop.Contains("JHYL_MONTHLY_EVENT_LOW_REASONING_RECOVERY")
                || !loop.Contains("过月江湖事件 Agent 轻思考失败恢复"))
                throw new InvalidOperationException(
                    "同道或江湖事件没有落实可靠回执轻承接、失败自动恢复完整思考的自适应策略");
            const string recoveryResultFlow =
                @"recovery\s*=\s*recoveryTask\.Result;.*?"
                + @"turn\s*=\s*recovery\.Result;.*?"
                + @"if\s*\(turn\s*==\s*null\s*\|\|\s*!turn\.Ok\)";
            if (!Regex.IsMatch(companionLoop, recoveryResultFlow,
                    RegexOptions.Singleline)
                || !Regex.IsMatch(loop, recoveryResultFlow,
                    RegexOptions.Singleline))
                throw new InvalidOperationException(
                    "同道或江湖事件没有把完整思考恢复结果接回失败判断前的生产控制流");
            string invalidation = Slice(monthly,
                "static void InvalidateMonthlyEventQueryState(",
                "static bool TryValidateMonthlyEventQueryDependencies(");
            if (!Regex.IsMatch(invalidation,
                    @"case\s+""event_kill"":.*?Add\(a,\s*""status"",\s*""relations"",\s*""items""\);"
                    + @".*?Add\(b,\s*""status"",\s*""relations"",\s*""items""\);",
                    RegexOptions.Singleline)
                || !Regex.IsMatch(invalidation,
                    @"case\s+""event_capture"":.*?Add\(b,\s*""status"",\s*""relations""\);",
                    RegexOptions.Singleline))
                throw new InvalidOperationException(
                    "江湖事件杀人战利品没有同时失效行动者与目标物品缓存，或误把擒拿扩成物品变化");
            if (companionDefinitions.Contains("ToolDef.Of(\"goto_place\"")
                || companionExecutor.Contains("case \"goto_place\":")
                || companionDefinitions.Contains("\"leave\""))
                throw new InvalidOperationException("同道主动行事泄漏赴约、单独移动或离队能力");
            foreach (string required in new[]
            {
                "send_message", "barter", "set_relation", "spend_night", "matchmake"
            })
                if (!companionDefinitions.Contains("ToolDef.Of(\"" + required + "\""))
                    throw new InvalidOperationException("同道主动行事缺少能力: " + required);
            if (!companionDefinitions.Contains("向任意具名人物当面说话或发起千里传音")
                || !companionDefinitions.Contains("任意在场人物当面以物换物")
                || !companion.Contains("你本月始终与太吾同行,不能离队"))
                throw new InvalidOperationException("同道第三方、远程传音、以物换物或不离队语义缺失");
            if (!loop.Contains("accepted_without_narrative")
                || loop.Contains("TryAcceptMonthlyAgentNarrative(turn.Content")
                || !loop.Contains("MonthlyPreMinimumProgressFuse(3)")
                || !loop.Contains("MonthlyPostMinimumProgressFuse(2)")
                || !monthly.Contains("Content = BuildEventOutcomeMemory(saga, checkpoint, recipientId)")
                || !companionLoop.Contains("authoritative_outcomes_only")
                || !companionLoop.Contains("finalNarrative = DeterministicStory(result, null)")
                || companionLoop.Contains("TryAcceptCompanionAgentNarrative(turn.Content")
                || !companionLoop.Contains("MonthlyPreMinimumProgressFuse(3)")
                || !companion.Contains("normalized.StartsWith(\"query_\", StringComparison.Ordinal)")
                || companionDefinitions.Contains("ToolDef.Of(\"remember\"")
                || !companion.Contains("CommitCompanionActorOutcomeMemory"))
                throw new InvalidOperationException(
                    "过月 Agent 仍承担正文生成，或真实工具结果没有自动进入人物记忆");
            if (!companionLoop.Contains("roundHasSuccessfulActionReceipt, roundHasNewAuthoritativeFacts")
                || !loop.Contains("roundHasSuccessfulActionReceipt, roundHasNewAuthoritativeFacts")
                || !loop.Contains("!queryCacheHit && queryResult != null && queryResult.Reliable")
                || !companionLoop.Contains("queryResultCache.TryGetValue(canonicalKey")
                || !companionLoop.Contains("不能返回空消息")
                || !loop.Contains("不能返回空消息"))
                throw new InvalidOperationException("过月新事实被错误计为空转，或结束提示仍要求空消息");
        }

        private static void RequireDefinitionsAndCases(string label, string definitions, string executor,
            IEnumerable<string> expected)
        {
            string[] missingDefinitions = expected.Where(x =>
                    !definitions.Contains("ToolDef.Of(\"" + x + "\""))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missingDefinitions.Length != 0)
                throw new InvalidOperationException(label + " 工具有执行器但无定义: "
                    + string.Join(",", missingDefinitions));
            RequireCases(label + "执行器", executor, expected);
        }

        private static void RequireCases(string label, string source, IEnumerable<string> expected)
        {
            string[] missing = expected.Where(x => !source.Contains("case \"" + x + "\":"))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missing.Length != 0)
                throw new InvalidOperationException(label + " 未覆盖已注册工具: " + string.Join(",", missing));
        }

        private static void RequireExactCases(string label, string source, IEnumerable<string> expected)
        {
            var actual = Regex.Matches(source, "case\\s+\\\"([a-z_]+)\\\"\\s*:")
                .Cast<Match>().Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
            var wanted = new HashSet<string>(expected, StringComparer.Ordinal);
            var actualSet = new HashSet<string>(actual, StringComparer.Ordinal);
            var missing = wanted.Except(actualSet).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var unexpected = actualSet.Except(wanted).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missing.Length != 0 || unexpected.Length != 0)
                throw new InvalidOperationException(label + " case 不精确; missing=" + string.Join(",", missing)
                    + "; unexpected=" + string.Join(",", unexpected));
        }

        private static string Slice(string source, string begin, string end)
        {
            int from = source.IndexOf(begin, StringComparison.Ordinal);
            int to = source.IndexOf(end, from < 0 ? 0 : from + begin.Length, StringComparison.Ordinal);
            if (from < 0 || to <= from) throw new InvalidOperationException("源码契约切片标记缺失: " + begin);
            return source.Substring(from, to - from);
        }

        private static string ReadRepoFile(params string[] parts)
        {
            foreach (string seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(seed);
                for (int depth = 0; dir != null && depth < 12; depth++, dir = dir.Parent)
                {
                    string path = parts.Aggregate(dir.FullName, (current, part) => Path.Combine(current, part));
                    if (File.Exists(path)) return File.ReadAllText(path, System.Text.Encoding.UTF8);
                }
            }
            throw new FileNotFoundException("找不到仓库源码", Path.Combine(parts));
        }

        private static void RequireValidDefinitions(string label, IList<ToolDef> tools)
        {
            if (!ToolArgumentsValidator.ValidateDefinitions(tools, out string error))
                throw new InvalidOperationException(label + " schema 无效: " + error);
        }

        private static void RequireExactNames(string label, IList<ToolDef> tools, IEnumerable<string> expected)
        {
            var actualNames = tools.Select(x => x?.Name ?? "").ToArray();
            var duplicateNames = actualNames.GroupBy(x => x, StringComparer.Ordinal)
                .Where(g => g.Count() != 1).Select(g => g.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (duplicateNames.Length != 0)
                throw new InvalidOperationException(label + " 存在重复/空工具名: " + string.Join(",", duplicateNames));

            var actual = new HashSet<string>(actualNames, StringComparer.Ordinal);
            var wanted = new HashSet<string>(expected, StringComparer.Ordinal);
            var missing = wanted.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var unexpected = actual.Except(wanted).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missing.Length != 0 || unexpected.Length != 0)
                throw new InvalidOperationException(label + " 不匹配; missing=" + string.Join(",", missing)
                    + "; unexpected=" + string.Join(",", unexpected));
        }
    }
}
