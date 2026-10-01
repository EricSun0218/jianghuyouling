using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using JianghuYouling.Core.Llm;
using static JianghuYouling.Core.Llm.ToolDef;

namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// 对话行动技能的渐进式目录。每项技能的元数据与正文都来自独立嵌入资源；本类只负责
    /// 发现、校验、现场过滤与模板渲染，绝不改变 ToolRegistry 构造出的当前工具权限。
    /// </summary>
    public static class ConversationSkillCatalog
    {
        public const string ConsultToolName = "consult_action_guide";

        private const string ResourcePrefix = "JianghuYouling.Core.ActionSkills.";
        private const string ResourceSuffix = ".SKILL.md";
        private const string CompanionMonthlyResourceSuffix = ".references.companion-monthly.md";
        private const string MonthlyEventResourceSuffix = ".references.monthly-event.md";

        private sealed class Skill
        {
            public string Id;
            public string Version;
            public int Order;
            public string Title;
            public string Summary;
            public string[] ActionTools;
            public string[] CompanionTools;
            public string[] EventTools;
            public string Availability;
            public string Instructions;
            public string CompanionInstructions;
            public string MonthlyEventInstructions;
            public string ResourceName;
            public string CompanionResourceName;
            public string MonthlyEventResourceName;
        }

        private static readonly Regex IdentifierPattern = new Regex(
            "^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

        private static readonly Regex VersionPattern = new Regex(
            "^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> AllowedMetadata = new HashSet<string>(
            new[]
            {
                "name", "version", "order", "title", "description", "action_tools",
                "companion_tools", "event_tools", "availability",
            },
            StringComparer.Ordinal);

        private static readonly Regex ConditionalBlockPattern = new Regex(
            @"\{\{#if\s+(?<name>[A-Za-z][A-Za-z0-9_]*)\s*\}\}(?<yes>.*?)(?:\{\{#else\}\}(?<no>.*?))?\{\{/if\}\}",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        private static readonly Skill[] All = LoadAll();

        public static ToolDef BuildConsultTool(ToolContext context)
        {
            ToolContext ctx = context ?? new ToolContext();
            Skill[] available = Available(ctx).ToArray();
            string directory = string.Join("；", available.Select(s => s.Id + "(v" + s.Version + ")=" + s.Summary));
            return Of(ConsultToolName,
                "按语义按需读取复杂行动指南。先理解玩家目标与上下文，再从目录选择最具体的一项；"
                + "不依赖固定口令，也不要因为玩家换了说法就漏掉行动。纯闲聊和单次事实查询无需调用；"
                + "准备复杂行动、需要多步前置或一次失败后调整方案时调用。目录："
                + directory + "。指南只说明方法，不会新增、删减或授权任何工具；读取后完整现场工具表仍然可用。",
                Obj(("skill", Sel("技能类别", available.Select(s => s.Id).ToArray()), true)));
        }

        public static bool TryLoad(string skillId, ToolContext context, out string instructions)
        {
            ToolContext ctx = context ?? new ToolContext();
            Skill skill = Available(ctx).FirstOrDefault(s => string.Equals(s.Id, skillId, StringComparison.Ordinal));
            if (skill == null)
            {
                instructions = null;
                return false;
            }

            instructions = "【即时聊天行动技能：" + skill.Title + "】\n"
                + "这是当前单聊/群聊的语义指南：根据太吾本轮表达、角色主动回应与实时聊天上下文判断是否行动；"
                + "不要套用同道过月或江湖事件的主动触发条件。\n"
                + "本技能只约束如何使用当前已经提供的工具，不新增能力、不覆盖现场限制，也不证明任何行动成功。\n"
                + RenderConditions(skill.Instructions, ctx);
            return true;
        }

        public static bool CoversAction(string toolName, ToolContext context)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            return Available(context ?? new ToolContext()).Any(s =>
                s.ActionTools != null && Array.IndexOf(s.ActionTools, toolName) >= 0);
        }

        public static bool TryLoadForAction(string toolName, ToolContext context,
            out string skillId, out string instructions)
        {
            ToolContext ctx = context ?? new ToolContext();
            Skill skill = Available(ctx).FirstOrDefault(s => s.ActionTools != null
                && Array.IndexOf(s.ActionTools, toolName ?? string.Empty) >= 0);
            if (skill == null)
            {
                skillId = null;
                instructions = null;
                return false;
            }
            skillId = skill.Id;
            return TryLoad(skill.Id, ctx, out instructions);
        }

        /// <summary>
        /// NPC 主动消息仍是一次普通聊天，不继承过月的行动数量、生命周期或工具名；但在
        /// 需要行动时，可以同时借用同类过月技能中更丰富的自主欲望与动机。按需读取而不
        /// 预塞整本技能，避免无动作的普通来信也增加大量 token。
        /// </summary>
        public static bool TryLoadForNpcInitiated(string skillId, ToolContext context,
            out string instructions)
        {
            ToolContext ctx = AsNpcInitiatedContext(context);
            Skill skill = Available(ctx).FirstOrDefault(s =>
                string.Equals(s.Id, skillId, StringComparison.Ordinal));
            if (skill == null)
            {
                instructions = null;
                return false;
            }

            if (!TryLoad(skill.Id, ctx, out string chatInstructions))
            {
                instructions = null;
                return false;
            }

            if (string.IsNullOrWhiteSpace(skill.CompanionInstructions))
            {
                instructions = chatInstructions;
                return true;
            }

            instructions = chatInstructions
                + "\n\n【NPC 主动联系时的自主行动参考】\n"
                + "这里只借用同类过月技能里关于人物欲望、性情和处境的动机例子；本轮仍是普通单聊。"
                + "没有最低行动数量，也不要求调用任何工具；若只是有话想说，直接给出正文即可。"
                + "只有行动确实符合人物与当前因果时才使用本轮真实工具，工具名、参数、现场限制和回执均以即时聊天指南及当前工具表为准。\n"
                + NormalizeCompanionGuidanceForNpcInitiated(skill.CompanionInstructions);
            return true;
        }

        public static bool TryLoadForNpcInitiatedAction(string toolName, ToolContext context,
            out string skillId, out string instructions)
        {
            ToolContext ctx = AsNpcInitiatedContext(context);
            Skill skill = Available(ctx).FirstOrDefault(s => s.ActionTools != null
                && Array.IndexOf(s.ActionTools, toolName ?? string.Empty) >= 0);
            if (skill == null)
            {
                skillId = null;
                instructions = null;
                return false;
            }
            skillId = skill.Id;
            return TryLoadForNpcInitiated(skill.Id, ctx, out instructions);
        }

        private static ToolContext AsNpcInitiatedContext(ToolContext context)
        {
            ToolContext source = context ?? new ToolContext();
            return new ToolContext
            {
                IsMerchant = source.IsMerchant,
                InSect = source.InSect,
                Remote = source.Remote,
                CanStartCombat = source.CanStartCombat,
                NpcInitiated = true,
            };
        }

        private static string NormalizeCompanionGuidanceForNpcInitiated(string source)
        {
            string normalized = (source ?? string.Empty)
                .Replace("\r\n", "\n").Replace('\r', '\n')
                .Replace("gift_item", "gift")
                .Replace("gift_silver", "gift(type=silver)")
                .Replace("send_message", "可见正文")
                .Replace("adjust_favor", "adjust_third_party_favor")
                .Replace("【可计划工具】", "【当前工具表】")
                .Replace("过月", "主动联系")
                .Replace("本月", "本轮");
            normalized = Regex.Replace(normalized, @"(?<![a-z0-9_])relate(?![a-z0-9_])",
                "relate_npc", RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"(?<![a-z0-9_])enmity(?![a-z0-9_])",
                "set_enmity", RegexOptions.CultureInvariant);
            var kept = new List<string>();
            foreach (string raw in normalized.Split('\n'))
            {
                // 这些是过月编排器的完成条件，不是行动语义；主动消息明确不继承。
                if (raw.Contains("三项实质行为") || raw.Contains("最低行动数")
                    || raw.Contains("虽至少成功一次") || raw.Contains("不计入三项"))
                    continue;
                kept.Add(raw);
            }
            return string.Join("\n", kept.ToArray()).Trim();
        }

        public static IReadOnlyList<string> AvailableSkillIds(ToolContext context)
            => Available(context ?? new ToolContext()).Select(s => s.Id).ToArray();

        /// <summary>
        /// 按同道本月已经由权威现场筛出的工具面组合技能参考。这里只拼提示词，不增删工具，
        /// 也不增加一次模型选择往返；no_action/remember 等跨技能生命周期规则仍由规划器负责。
        /// </summary>
        public static string BuildCompanionMonthlySkillBundle(IEnumerable<string> availableToolNames)
        {
            var available = new HashSet<string>(availableToolNames ?? Array.Empty<string>(), StringComparer.Ordinal);
            var sections = new List<string>();
            foreach (Skill skill in All)
            {
                string[] covered = skill.CompanionTools.Where(available.Contains).ToArray();
                if (covered.Length == 0) continue;
                if (string.IsNullOrWhiteSpace(skill.CompanionInstructions))
                    throw new InvalidOperationException("同道过月技能缺少参考正文: " + skill.Id);
                sections.Add("【同道主动行事技能：" + skill.Title + "｜当前覆盖："
                    + string.Join(",", covered) + "】\n" + skill.CompanionInstructions.Trim());
            }
            if (sections.Count == 0) return string.Empty;
            return "【按当前权威工具面装配的行动技能】\n"
                + "【场景：同道过月主动行事】这不是即时聊天：无需等待太吾当月提出命令，NPC 应依据人设、欲望、"
                + "心情、名望、关系、位置、记忆、本月处境与上一动作回执主动判断。相同能力在聊天中的应答条件"
                + "不能替代这里的月度触发条件；每项动作以本段对应技能写出的动机和因果为准。\n"
                + "以下技能只说明如何规划本月已经提供的动作，不会授权新工具。若技能提及的某个动作不在【可计划工具】中，就不能规划它。\n\n"
                + string.Join("\n\n", sections.ToArray());
        }

        public static bool CoversCompanionAction(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            return All.Any(s => Array.IndexOf(s.CompanionTools, toolName) >= 0);
        }

        /// <summary>
        /// 江湖事件与同道主动行事共享同一组独立行动技能，但使用各自的工具名与现场语义。
        /// 这里只按本轮稳定工具面拼装参考，不授予工具，也不增加一次分类模型调用。
        /// </summary>
        public static string BuildMonthlyEventSkillBundle(IEnumerable<string> availableToolNames)
        {
            var available = new HashSet<string>(availableToolNames ?? Array.Empty<string>(), StringComparer.Ordinal);
            var sections = new List<string>();
            foreach (Skill skill in All)
            {
                string[] covered = skill.EventTools.Where(available.Contains).ToArray();
                if (covered.Length == 0) continue;
                if (string.IsNullOrWhiteSpace(skill.MonthlyEventInstructions))
                    throw new InvalidOperationException("江湖事件技能缺少参考正文: " + skill.Id);
                sections.Add("【过月江湖事件技能：" + skill.Title + "｜当前覆盖："
                    + string.Join(",", covered) + "】\n" + skill.MonthlyEventInstructions.Trim());
            }
            if (sections.Count == 0) return string.Empty;
            return "【按当前权威工具面装配的江湖事件技能】\n"
                + "【场景：过月江湖事件】这不是聊天应答，也不是同道个人日程：应从本回人物欲望、性格、心情、"
                + "名望、关系、位置、未决线索和连续回执主动推进 NPC 与 NPC 的因果。相同能力在聊天或同道过月中的"
                + "触发条件不能直接搬来；每项事件以本段对应技能写出的事件动机为准。\n"
                + "以下技能规定查询、预检、行动和失败恢复顺序，不会授权新工具。执行层也会强制缺失的前置查询；预检拒绝不是已经发生的失败。\n\n"
                + string.Join("\n\n", sections.ToArray());
        }

        public static bool CoversMonthlyEventAction(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            return All.Any(s => Array.IndexOf(s.EventTools, toolName) >= 0);
        }

        /// <summary>供回归测试与诊断确认技能确实来自独立资源，而不是重新内嵌到代码。</summary>
        public static bool TryGetResourceInfo(string skillId, out string version, out string resourceName)
        {
            Skill skill = All.FirstOrDefault(s => string.Equals(s.Id, skillId, StringComparison.Ordinal));
            if (skill == null)
            {
                version = null;
                resourceName = null;
                return false;
            }
            version = skill.Version;
            resourceName = skill.ResourceName;
            return true;
        }

        private static IEnumerable<Skill> Available(ToolContext context)
            => All.Where(s => IsAvailable(s.Availability, context)
                && s.ActionTools.Any(tool => ToolRegistry.IsActionAvailable(tool, context)
                    && (!context.NpcInitiated || ToolRegistry.IsNpcInitiatedToolAllowed(tool))));

        private static bool IsAvailable(string availability, ToolContext context)
        {
            if (string.Equals(availability, "always", StringComparison.Ordinal)) return true;
            foreach (string rawCondition in availability.Split(','))
            {
                ParseAvailabilityCondition(rawCondition, availability, out string name, out bool expected);
                if (ContextFlag(name, context) != expected) return false;
            }
            return true;
        }

        private static void ValidateAvailability(string availability)
        {
            if (string.Equals(availability, "always", StringComparison.Ordinal)) return;
            foreach (string rawCondition in availability.Split(','))
            {
                ParseAvailabilityCondition(rawCondition, availability, out string name, out _);
                ContextFlag(name, new ToolContext());
            }
        }

        private static void ParseAvailabilityCondition(string rawCondition, string expression,
            out string name, out bool expected)
        {
            string condition = rawCondition.Trim();
            int separator = condition.IndexOf('=');
            if (separator <= 0 || !bool.TryParse(condition.Substring(separator + 1).Trim(), out expected))
                throw new InvalidOperationException("行动技能现场条件格式错误: " + expression);
            name = condition.Substring(0, separator).Trim();
        }

        private static bool ContextFlag(string name, ToolContext context)
        {
            switch (name)
            {
                case "remote": return context.Remote;
                case "merchant": return context.IsMerchant;
                case "sect": return context.InSect;
                case "combat": return context.CanStartCombat;
                default: throw new InvalidOperationException("行动技能使用未知现场条件: " + name);
            }
        }

        private static Skill[] LoadAll()
        {
            Assembly assembly = typeof(ConversationSkillCatalog).Assembly;
            string[] allResourceNames = assembly.GetManifestResourceNames();
            var allResourceSet = new HashSet<string>(allResourceNames, StringComparer.Ordinal);
            string[] resources = allResourceNames
                .Where(x => x.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                    && x.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            if (resources.Length == 0)
                throw new InvalidOperationException("没有找到嵌入的行动技能资源");

            var skills = new List<Skill>(resources.Length);
            foreach (string resourceName in resources)
            {
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) throw new InvalidOperationException("无法读取行动技能资源: " + resourceName);
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                    {
                        Skill skill = ParseSkill(reader.ReadToEnd(), resourceName);
                        string companionResourceName = resourceName.Substring(0,
                            resourceName.Length - ResourceSuffix.Length) + CompanionMonthlyResourceSuffix;
                        string monthlyEventResourceName = resourceName.Substring(0,
                            resourceName.Length - ResourceSuffix.Length) + MonthlyEventResourceSuffix;
                        bool hasCompanionReference = allResourceSet.Contains(companionResourceName);
                        bool hasMonthlyEventReference = allResourceSet.Contains(monthlyEventResourceName);
                        if (skill.CompanionTools.Length > 0 && !hasCompanionReference)
                            throw new InvalidOperationException("同道过月技能缺少独立参考资源: " + skill.Id);
                        if (skill.CompanionTools.Length == 0 && hasCompanionReference)
                            throw new InvalidOperationException("同道过月技能参考没有 companion_tools 映射: " + skill.Id);
                        if (hasCompanionReference)
                        {
                            skill.CompanionInstructions = ReadResourceText(assembly, companionResourceName).Trim();
                            skill.CompanionResourceName = companionResourceName;
                            if (skill.CompanionInstructions.Length == 0)
                                throw new InvalidOperationException("同道过月技能参考正文为空: " + skill.Id);
                        }
                        if (skill.EventTools.Length > 0 && !hasMonthlyEventReference)
                            throw new InvalidOperationException("江湖事件技能缺少独立参考资源: " + skill.Id);
                        if (skill.EventTools.Length == 0 && hasMonthlyEventReference)
                            throw new InvalidOperationException("江湖事件技能参考没有 event_tools 映射: " + skill.Id);
                        if (hasMonthlyEventReference)
                        {
                            skill.MonthlyEventInstructions = ReadResourceText(assembly, monthlyEventResourceName).Trim();
                            skill.MonthlyEventResourceName = monthlyEventResourceName;
                            if (skill.MonthlyEventInstructions.Length == 0)
                                throw new InvalidOperationException("江湖事件技能参考正文为空: " + skill.Id);
                        }
                        skills.Add(skill);
                    }
                }
            }

            string[] duplicateIds = skills.GroupBy(x => x.Id, StringComparer.Ordinal)
                .Where(g => g.Count() != 1).Select(g => g.Key).ToArray();
            if (duplicateIds.Length != 0)
                throw new InvalidOperationException("行动技能 id 重复: " + string.Join(",", duplicateIds));

            int[] duplicateOrders = skills.GroupBy(x => x.Order)
                .Where(g => g.Count() != 1).Select(g => g.Key).ToArray();
            if (duplicateOrders.Length != 0)
                throw new InvalidOperationException("行动技能 order 重复: " + string.Join(",", duplicateOrders));

            string[] duplicateTools = skills.SelectMany(x => x.ActionTools.Select(tool => new { x.Id, Tool = tool }))
                .GroupBy(x => x.Tool, StringComparer.Ordinal).Where(g => g.Count() != 1)
                .Select(g => g.Key).ToArray();
            if (duplicateTools.Length != 0)
                throw new InvalidOperationException("动作工具被多个行动技能重复声明: " + string.Join(",", duplicateTools));

            string[] duplicateCompanionTools = skills
                .SelectMany(x => x.CompanionTools.Select(tool => new { x.Id, Tool = tool }))
                .GroupBy(x => x.Tool, StringComparer.Ordinal).Where(g => g.Count() != 1)
                .Select(g => g.Key).ToArray();
            if (duplicateCompanionTools.Length != 0)
                throw new InvalidOperationException("同道动作被多个行动技能重复声明: "
                    + string.Join(",", duplicateCompanionTools));

            string[] duplicateEventTools = skills
                .SelectMany(x => x.EventTools.Select(tool => new { x.Id, Tool = tool }))
                .GroupBy(x => x.Tool, StringComparer.Ordinal).Where(g => g.Count() != 1)
                .Select(g => g.Key).ToArray();
            if (duplicateEventTools.Length != 0)
                throw new InvalidOperationException("江湖事件动作被多个行动技能重复声明: "
                    + string.Join(",", duplicateEventTools));

            return skills.OrderBy(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        }

        private static Skill ParseSkill(string source, string resourceName)
        {
            string normalized = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.Length != 0 && normalized[0] == '\uFEFF') normalized = normalized.Substring(1);
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
                throw new InvalidOperationException("行动技能缺少 frontmatter 起始标记: " + resourceName);
            int headerEnd = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (headerEnd < 0)
                throw new InvalidOperationException("行动技能缺少 frontmatter 结束标记: " + resourceName);

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string rawLine in normalized.Substring(4, headerEnd - 4).Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int separator = line.IndexOf(':');
                if (separator <= 0)
                    throw new InvalidOperationException("行动技能元数据格式错误: " + resourceName + " -> " + line);
                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (metadata.ContainsKey(key))
                    throw new InvalidOperationException("行动技能元数据字段重复: " + resourceName + " -> " + key);
                metadata.Add(key, value);
            }

            string id = Required(metadata, "name", resourceName);
            string version = Required(metadata, "version", resourceName);
            string orderText = Required(metadata, "order", resourceName);
            string title = Required(metadata, "title", resourceName);
            string summary = Required(metadata, "description", resourceName);
            string availability = Required(metadata, "availability", resourceName);
            string[] actionTools = Required(metadata, "action_tools", resourceName)
                .Split(',').Select(x => x.Trim()).Where(x => x.Length != 0).ToArray();
            string[] companionTools = metadata.TryGetValue("companion_tools", out string companionToolText)
                ? companionToolText.Split(',').Select(x => x.Trim()).Where(x => x.Length != 0).ToArray()
                : Array.Empty<string>();
            string[] eventTools = metadata.TryGetValue("event_tools", out string eventToolText)
                ? eventToolText.Split(',').Select(x => x.Trim()).Where(x => x.Length != 0).ToArray()
                : Array.Empty<string>();
            string instructions = normalized.Substring(headerEnd + 5).Trim();

            string[] unknownMetadata = metadata.Keys.Where(x => !AllowedMetadata.Contains(x)).ToArray();
            if (unknownMetadata.Length != 0)
                throw new InvalidOperationException("行动技能包含未知元数据: " + resourceName
                    + " -> " + string.Join(",", unknownMetadata));

            if (!IdentifierPattern.IsMatch(id))
                throw new InvalidOperationException("行动技能 id 非法: " + resourceName + " -> " + id);
            if (!VersionPattern.IsMatch(version))
                throw new InvalidOperationException("行动技能 version 非法: " + resourceName + " -> " + version);
            if (!int.TryParse(orderText, out int order) || order < 0)
                throw new InvalidOperationException("行动技能 order 非法: " + resourceName + " -> " + orderText);
            if (actionTools.Length == 0 || actionTools.Any(x => !IdentifierPattern.IsMatch(x)))
                throw new InvalidOperationException("行动技能 action_tools 非法: " + resourceName);
            if (actionTools.Distinct(StringComparer.Ordinal).Count() != actionTools.Length)
                throw new InvalidOperationException("行动技能 action_tools 重复: " + resourceName);
            if (companionTools.Any(x => !IdentifierPattern.IsMatch(x))
                || companionTools.Distinct(StringComparer.Ordinal).Count() != companionTools.Length)
                throw new InvalidOperationException("行动技能 companion_tools 非法或重复: " + resourceName);
            if (eventTools.Any(x => !IdentifierPattern.IsMatch(x))
                || eventTools.Distinct(StringComparer.Ordinal).Count() != eventTools.Length)
                throw new InvalidOperationException("行动技能 event_tools 非法或重复: " + resourceName);
            if (instructions.Length == 0)
                throw new InvalidOperationException("行动技能正文为空: " + resourceName);
            ValidateAvailability(availability);

            string expectedFolderToken = ".ActionSkills." + id + ".SKILL.md";
            if (!resourceName.EndsWith(expectedFolderToken, StringComparison.Ordinal))
                throw new InvalidOperationException("行动技能目录名必须与 id 一致: " + resourceName + " -> " + id);

            // 打包阶段同时验证真假两条模板分支，不能等玩家触发后才发现拼写或闭合错误。
            RenderConditions(instructions, new ToolContext());
            RenderConditions(instructions, new ToolContext
            {
                Remote = true,
                CanStartCombat = true,
                IsMerchant = true,
                InSect = true,
            });

            return new Skill
            {
                Id = id,
                Version = version,
                Order = order,
                Title = title,
                Summary = summary,
                ActionTools = actionTools,
                CompanionTools = companionTools,
                EventTools = eventTools,
                Availability = availability,
                Instructions = instructions,
                ResourceName = resourceName,
            };
        }

        private static string Required(IReadOnlyDictionary<string, string> metadata, string key, string resourceName)
        {
            if (!metadata.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("行动技能缺少元数据: " + resourceName + " -> " + key);
            return value;
        }

        private static string ReadResourceText(Assembly assembly, string resourceName)
        {
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new InvalidOperationException("无法读取行动技能资源: " + resourceName);
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                {
                    string value = reader.ReadToEnd();
                    if (value.Length != 0 && value[0] == '\uFEFF') value = value.Substring(1);
                    return value.Replace("\r\n", "\n").Replace('\r', '\n');
                }
            }
        }

        private static string RenderConditions(string template, ToolContext context)
        {
            string rendered = template ?? string.Empty;
            for (int guard = 0; guard < 16; guard++)
            {
                Match match = ConditionalBlockPattern.Match(rendered);
                if (!match.Success) break;
                bool condition;
                switch (match.Groups["name"].Value)
                {
                    case "Remote": condition = context.Remote; break;
                    case "CanStartCombat": condition = context.CanStartCombat; break;
                    case "IsMerchant": condition = context.IsMerchant; break;
                    case "InSect": condition = context.InSect; break;
                    default: throw new InvalidOperationException("行动技能使用未知模板条件: " + match.Groups["name"].Value);
                }
                string replacement = condition ? match.Groups["yes"].Value : match.Groups["no"].Value;
                rendered = rendered.Substring(0, match.Index) + replacement
                    + rendered.Substring(match.Index + match.Length);
            }
            if (rendered.IndexOf("{{#if", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("{{#else}}", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("{{/if}}", StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("行动技能条件模板不完整");
            return rendered.Trim();
        }
    }
}
