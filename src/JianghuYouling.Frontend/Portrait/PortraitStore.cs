using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using JianghuYouling.Core.Prompt;
using JianghuYouling.Core.Persistence;

namespace JianghuYouling
{
    /// <summary>
    /// 人物画像:被 AI 扮演的人设锚点。每游戏月至多刷新一次(按 NpcSnapshot.CurrentDate 判月,当月多次开窗复用)。
    /// 蒸馏由 PortraitService(LLM)完成;LLM 不可用/失败时回退到 BuildSimple 的底座拼接画像。
    /// 本类只管:底座→profile 映射、底座简易画像、按月缓存读写。世界目录已隔离存档，
    /// 因而画像按 npcId 落盘；传剑换代后沿用稳定画像，新的当代太吾关系由每轮快照提供。
    /// </summary>
    public static class PortraitStore
    {
        public const int CurrentSchemaVersion = 3;
        private const int MaxFileBytes = 2 * 1024 * 1024;
        private static readonly object Sync = new object();
        private static readonly string[] QiYuan = { "冷静", "聪颖", "热情", "勇壮", "坚毅", "福缘", "合道" };

        private static string PathFor(NpcSnapshot s)
            => CanonicalPath(s.NpcId);

        private static string CanonicalPath(int npcId)
            => Path.Combine(JianghuYoulingPaths.Portraits, "Portrait_" + npcId + ".json");

        private static JObject ReadRecoverable(string path, bool preserveCorrupt = true, int expectedTaiwu = 0, int expectedNpc = 0)
        {
            lock (Sync)
            {
                return TryReadRecoverable(path, expectedTaiwu, expectedNpc, out JObject value, out _) ? value : null;
            }
        }

        private static bool TryReadRecoverable(string path, int expectedTaiwu, int expectedNpc,
            out JObject value, out bool anyCandidate)
        {
            value = null;
            anyCandidate = false;
            if (string.IsNullOrEmpty(path)) return false;
            bool Validator(string json) => IsValidDocument(json, expectedTaiwu, expectedNpc);
            if (!DurableFileStore.TryReadRecoverableText(path, MaxFileBytes, Validator,
                out string raw, out anyCandidate, out _, preserveInvalidCandidates: true)) return false;
            value = JObject.Parse(raw);
            return true;
        }

        /// <summary>取持久长期记忆画像(随对话演进,不按月失效)。无则返回 null。</summary>
        public static string GetPortrait(NpcSnapshot s)
        {
            if (s == null) return null;
            lock (Sync) try
            {
                var o = ReadForNpc(s.TaiwuId, s.NpcId);
                if (!IsCurrentSchema(o)) return null;
                var t = o?["portrait"]?.ToString();
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            catch { }
            return null;
        }

        public static string GetPortrait(int taiwuId, int npcId)
        {
            try
            {
                JObject o = ReadForNpc(taiwuId, npcId);
                if (!IsCurrentSchema(o)) return null;
                string t = o?["portrait"]?.ToString();
                return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
            }
            catch { return null; }
        }

        /// <summary>
        /// 固定模板人物建立永久副本后，把已有 AI 人物画像原样转交给副本。画像内容不重新生成，
        /// 只改持久文档中的人物 id。这里只提交目标，不清理源画像；身份映射提交后再清理，
        /// 防止后续迁移失败令原人物画像暂时变成空白。
        /// </summary>
        public static bool ReplaceIdentity(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (taiwuId <= 0 || oldNpcId < 0 || newNpcId < 0) return false;
            if (oldNpcId == newNpcId) return true;
            lock (Sync)
            {
                try
                {
                    string oldPath = CanonicalPath(oldNpcId);
                    string newPath = CanonicalPath(newNpcId);
                    if (TryReadRecoverable(newPath, 0, newNpcId,
                        out JObject existing, out bool targetArtifacts) && existing != null)
                    {
                        // 画像跨太吾换代沿用；迁移时顺手把旧太吾 id 重新绑定到当代太吾。
                        existing["taiwu"] = taiwuId;
                        existing["npc"] = newNpcId;
                        return DurableFileStore.TryWriteTextAtomic(newPath, existing.ToString(),
                            MaxFileBytes, raw => IsValidDocument(raw, taiwuId, newNpcId));
                    }
                    if (targetArtifacts) return false;

                    if (!TryReadRecoverable(oldPath, 0, oldNpcId,
                        out JObject source, out bool sourceArtifacts))
                        return !sourceArtifacts;
                    source["taiwu"] = taiwuId;
                    source["npc"] = newNpcId;
                    source["revision"] = unchecked((source["revision"]?.Value<long>() ?? 0) + 1);
                    source["writtenUtcTicks"] = DateTime.UtcNow.Ticks;
                    string json = source.ToString();
                    if (!DurableFileStore.TryWriteTextAtomic(newPath, json, MaxFileBytes,
                        raw => IsValidDocument(raw, taiwuId, newNpcId))) return false;
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>读上次"固化进画像"的对话记忆条数(用于判断有多少新记忆待固化)。无则 0。</summary>
        public static int GetConsolidatedMemCount(NpcSnapshot s)
        {
            if (s == null) return 0;
            try
            {
                return ReadForNpc(s.TaiwuId, s.NpcId)?["memCount"]?.Value<int>() ?? 0;
            }
            catch { }
            return 0;
        }

        public static string GetMemoryFingerprint(NpcSnapshot s)
        {
            if (s == null) return null;
            try
            {
                return ReadForNpc(s.TaiwuId, s.NpcId)?["memoryFingerprint"]?.ToString();
            }
            catch { return null; }
        }

        public static string GetSourceFingerprint(NpcSnapshot s)
        {
            if (s == null) return null;
            try { return ReadForNpc(s.TaiwuId, s.NpcId)?["sourceFingerprint"]?.ToString(); }
            catch { return null; }
        }

        private static bool IsCurrentSchema(JObject value)
        {
            try { return value != null && (value["schemaVersion"]?.Value<int>() ?? 1) >= CurrentSchemaVersion; }
            catch { return false; }
        }

        /// <summary>旧短画像或 simple 兜底在 LLM 可用后需自动升级为当前人物圣经。</summary>
        public static bool NeedsUpgrade(NpcSnapshot s, string currentSourceFingerprint = null)
        {
            if (s == null) return false;
            try
            {
                var o = ReadForNpc(s.TaiwuId, s.NpcId);
                if (o == null) return true;
                int version = o["schemaVersion"]?.Value<int>() ?? 1;
                string source = o["source"]?.ToString() ?? "simple";
                string portrait = o["portrait"]?.ToString();
                string savedSourceFingerprint = o["sourceFingerprint"]?.ToString();
                return version < CurrentSchemaVersion
                    || string.Equals(source, "simple", StringComparison.OrdinalIgnoreCase)
                    || !PortraitDistiller.IsDurablePersonaLayer(portrait)
                    || (!string.IsNullOrEmpty(currentSourceFingerprint)
                        && !string.Equals(savedSourceFingerprint, currentSourceFingerprint, StringComparison.Ordinal));
            }
            catch { return true; }
        }

        /// <summary>落盘画像(source: "llm"蒸馏/"llm-update"对话固化/"simple"兜底;memCount=固化时的对话记忆条数)。仅在原子提交并语义读回成功时返回 true。</summary>
        public static bool Save(NpcSnapshot s, string portrait, string source, int memCount, string memoryFingerprint = null, string sourceFingerprint = null)
        {
            if (s == null || string.IsNullOrWhiteSpace(portrait)) return false;
            try
            {
                bool complete = !string.Equals(source, "simple", StringComparison.OrdinalIgnoreCase)
                    && PortraitDistiller.IsDurablePersonaLayer(portrait);
                string path = PathFor(s);
                long previousRevision = 0;
                JObject previous = ReadForNpc(s.TaiwuId, s.NpcId);
                bool any = HasAnyArtifacts(path);
                if (previous != null)
                    previousRevision = previous?["revision"]?.Value<long>() ?? 0;
                else if (any) throw new InvalidDataException("画像候选全部损坏，拒绝覆盖恢复现场");
                var o = new JObject
                {
                    ["date"] = s.CurrentDate,
                    ["taiwu"] = s.TaiwuId,
                    ["npc"] = s.NpcId,
                    ["portrait"] = portrait,
                    ["source"] = source ?? "simple",
                    ["memCount"] = memCount,
                    // simple 只表示内容仍待 LLM 升级，不表示它可以沿用旧版字段契约。
                    // 同样写当前 schema，后续靠 source=simple 继续触发后台升级。
                    ["schemaVersion"] = CurrentSchemaVersion,
                    ["memoryFingerprint"] = memoryFingerprint,
                    ["sourceFingerprint"] = sourceFingerprint,
                    ["revision"] = unchecked(previousRevision + 1),
                    ["writtenUtcTicks"] = DateTime.UtcNow.Ticks,
                };
                string json = o.ToString();
                if (!DurableFileStore.TryWriteTextAtomic(path, json, MaxFileBytes,
                    raw => IsValidDocument(raw, s.TaiwuId, s.NpcId)))
                    throw new IOException("画像耐久提交或语义读回失败");
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[江湖有灵] 画像存盘失败: " + e.GetType().Name);
                return false;
            }
        }

        /// <summary>同步取画像:有持久画像则用之,否则用底座简易画像并落盘。供无 LLM 场景兜底。</summary>
        public static string GetOrBuild(NpcSnapshot s)
        {
            if (s == null) return "";
            var p = GetPortrait(s);
            if (p != null) return p;
            string simple = BuildSimple(s);
            Save(s, simple, "simple", 0);
            return simple;
        }

        /// <summary>删除某 npc 的全部画像恢复文件。返回目标是否已确认不存在（原本就没有也算成功）。</summary>
        public static bool Delete(int taiwuId, int npcId)
        {
            bool ok = true;
            try
            {
                string path = CanonicalPath(npcId);
                string parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    foreach (var candidate in Directory.GetFiles(parent, Path.GetFileName(path) + ".corrupt-*"))
                        try { if (File.Exists(candidate)) File.Delete(candidate); if (File.Exists(candidate)) ok = false; }
                        catch { ok = false; }
                    // Legacy storage may have only a recoverable .bak/.tmp left. Enumerating
                    // only the base .json would let that old Taiwu-scoped portrait resurrect
                    // after the player explicitly resets this NPC's portrait.
                    var legacyBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string artifact in Directory.GetFiles(parent,
                        "Portrait_*_" + npcId + ".json*"))
                    {
                        int jsonEnd = artifact.LastIndexOf(".json",
                            StringComparison.OrdinalIgnoreCase);
                        if (jsonEnd < 0) continue;
                        string legacy = artifact.Substring(0, jsonEnd + ".json".Length);
                        if (!string.Equals(legacy, path, StringComparison.OrdinalIgnoreCase))
                            legacyBases.Add(legacy);
                    }
                    foreach (string legacy in legacyBases)
                    {
                        ok = DurableFileStore.TryDeleteAllArtifacts(legacy) && ok;
                        foreach (string corrupt in Directory.GetFiles(parent,
                            Path.GetFileName(legacy) + ".corrupt-*"))
                            try
                            {
                                if (File.Exists(corrupt)) File.Delete(corrupt);
                                if (File.Exists(corrupt)) ok = false;
                            }
                            catch { ok = false; }
                    }
                }
                return DurableFileStore.TryDeleteAllArtifacts(path) && ok;
            }
            catch { ok = false; }
            return false;
        }

        private static JObject ReadForNpc(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId < 0) return null;
            lock (Sync)
            {
                string canonical = CanonicalPath(npcId);
                if (TryReadRecoverable(canonical, taiwuId, npcId, out JObject current,
                    out bool canonicalArtifacts))
                    return current;
                // A canonical file written by the former Taiwu is still this NPC's portrait.
                // Rebind its identity field before normal validation and future saves.
                if (canonicalArtifacts
                    && TryReadRecoverable(canonical, 0, npcId, out JObject priorCanonical, out _))
                    return RebindAndPersist(canonical, priorCanonical, taiwuId, npcId);
                if (canonicalArtifacts) return null;

                string legacy = FindBestLegacyPath(taiwuId, npcId);
                if (legacy == null
                    || !TryReadRecoverable(legacy, 0, npcId, out JObject prior, out _))
                    return null;
                return RebindAndPersist(canonical, prior, taiwuId, npcId);
            }
        }

        private static JObject RebindAndPersist(string path, JObject source, int taiwuId, int npcId)
        {
            if (source == null) return null;
            JObject rebound = (JObject)source.DeepClone();
            rebound["taiwu"] = taiwuId;
            rebound["npc"] = npcId;
            rebound["writtenUtcTicks"] = DateTime.UtcNow.Ticks;
            string json = rebound.ToString();
            // 即使迁移落盘因瞬时文件占用失败，本次也继续使用已经验证过的旧画像，
            // 不能把可恢复的传剑前画像表现成“人设重置”。后续读取会再次尝试迁移。
            DurableFileStore.TryWriteTextAtomic(path, json, MaxFileBytes,
                raw => IsValidDocument(raw, taiwuId, npcId));
            return rebound;
        }

        private static string FindBestLegacyPath(int taiwuId, int npcId)
        {
            string directory = JianghuYoulingPaths.Portraits;
            if (!Directory.Exists(directory)) return null;
            string exact = Path.Combine(directory, "Portrait_" + taiwuId + "_" + npcId + ".json");
            if (HasAnyArtifacts(exact)) return exact;
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string artifact in Directory.GetFiles(directory,
                "Portrait_*_" + npcId + ".json*"))
            {
                string path = artifact.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                    || artifact.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    ? artifact.Substring(0, artifact.Length - 4) : artifact;
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || !candidates.Add(path)) continue;
                string file = Path.GetFileNameWithoutExtension(path);
                string suffix = "_" + npcId;
                if (string.IsNullOrEmpty(file)
                    || !file.StartsWith("Portrait_", StringComparison.Ordinal)
                    || !file.EndsWith(suffix, StringComparison.Ordinal)) continue;
                string owner = file.Substring("Portrait_".Length,
                    file.Length - "Portrait_".Length - suffix.Length);
                if (!int.TryParse(owner, out int ownerId) || ownerId <= 0) continue;
                DateTime time = LatestArtifactWriteTime(path);
                if (best == null || time > bestTime) { best = path; bestTime = time; }
            }
            return best;
        }

        private static DateTime LatestArtifactWriteTime(string path)
        {
            DateTime latest = DateTime.MinValue;
            foreach (string candidate in new[] { path, path + ".bak", path + ".tmp" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    DateTime time = File.GetLastWriteTimeUtc(candidate);
                    if (time > latest) latest = time;
                }
                catch { }
            }
            return latest;
        }

        private static bool HasAnyArtifacts(string path)
        {
            try { return File.Exists(path) || File.Exists(path + ".bak") || File.Exists(path + ".tmp"); }
            catch { return true; }
        }

        /// <summary>底座→prompt profile 映射(供蒸馏与对话共用,含七元赋性与当下状态文本)。</summary>
        public static NpcProfileForPrompt BuildProfile(NpcSnapshot s)
        {
            if (s == null) return new NpcProfileForPrompt();
            return new NpcProfileForPrompt
            {
                NpcId = s.NpcId, TaiwuId = s.TaiwuId,
                IsDead = s.IsDead,
                Name = s.Name, Gender = s.Gender,
                SexualOrientation = s.SexualOrientation,
                PhysiologicalAge = s.PhysiologicalAge, ActualAge = s.ActualAge,
                Charm = s.Charm,
                Behavior = s.Behavior,
                TaiwuName = s.TaiwuName, TaiwuGender = s.TaiwuGender,
                OrgTitle = s.OrgFullTitle, GradeName = s.GradeName,
                SectLore = SectLoreText(s),
                Relation = s.Relation, FavorLevel = s.FavorLevel,
                PersonalitiesText = PersonalitiesText(s.Personalities),
                FeaturesText = PersonaFeatureFilter.Join(s.Features),
                StatusText = StatusText(s),
                Happiness = s.Happiness,
                FameText = s.FameText,
                SpecialPersona = JianghuYouling.Core.Persona.SpecialPersonaCatalog.Resolve(s.CharacterTemplateId),
                GiftableItemsText = GiftablesText(s.GiftableItems),
                EquippedItemsText = s.EquippedItemNames == null || s.EquippedItemNames.Count == 0
                    ? null : string.Join("、", s.EquippedItemNames.ToArray()),
                LearnableSkillsText = SkillsText(s.LearnableSkills),
                LearnableLifeSkillsText = SkillsText(s.LearnableLifeSkills),
                TrainableSkillsText = TrainableSkillsText(s),
                UnreadBooksText = UnreadBooksText(s),
                UsableItemsText = UsableItemsText(s),
                TellableSecretsText = SecretsListText(s.ShareableSecrets),
                TaiwuInfoText = s.TaiwuInfoText, WorldTimeText = s.WorldTimeText, LocationText = s.LocationText,
                ConsummateLevel = s.ConsummateLevel, HasRope = s.HasRope, HasPoison = s.HasPoison,   // 攻击性硬条件预载(精纯/绳/毒)
            };
        }

        static string SecretsListText(List<SecretRef> secrets)
        {
            if (secrets == null || secrets.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < secrets.Count && i < 8; i++)
            {
                var sr = secrets[i];
                if (sr == null || string.IsNullOrWhiteSpace(sr.Text)) continue;
                string t = sr.Text.Length > 40 ? sr.Text.Substring(0, 40) + "…" : sr.Text;
                sb.Append(i + 1).Append(") ").Append(t).Append("  ");
            }
            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }

        static string SectLoreText(NpcSnapshot s)
        {
            if (s == null || s.OrgTemplateId <= 0) return null;
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(s.SectName)) sb.Append(s.SectName);
            if (!string.IsNullOrWhiteSpace(s.SectDesc)) sb.Append(sb.Length > 0 ? "：" : "").Append(s.SectDesc);
            if (!string.IsNullOrWhiteSpace(s.SectExtra)) sb.Append(" 门风理念：").Append(s.SectExtra);
            if (!string.IsNullOrWhiteSpace(s.SectVow)) sb.Append(" 入派誓约：").Append(s.SectVow);
            if (!string.IsNullOrWhiteSpace(s.SectStory)) sb.Append(" 门派渊源：").Append(s.SectStory);
            string text = sb.ToString();
            return text.Length <= 1200 ? text : text.Substring(0, 1200) + "…";
        }

        static string SkillsText(List<LearnableSkill> skills)
        {
            if (skills == null || skills.Count == 0) return null;
            var names = new List<string>();
            foreach (var k in skills) if (k != null && !string.IsNullOrWhiteSpace(k.Name)) names.Add(k.Name);
            return names.Count > 0 ? string.Join("、", names) : null;
        }

        static string TrainableSkillsText(NpcSnapshot s)
        {
            if (s == null || !s.StudyProgressLoaded) return null;
            var names = new List<string>();
            if (s.LearnableSkills != null && s.IncompleteTrainingSkillIds != null)
                foreach (var skill in s.LearnableSkills)
                    if (skill != null && !string.IsNullOrWhiteSpace(skill.Name)
                        && s.IncompleteTrainingSkillIds.Contains(skill.TemplateId))
                        names.Add(skill.Name);
            return names.Count > 0 ? string.Join("、", names) : "无";
        }

        static string UnreadBooksText(NpcSnapshot s)
        {
            if (s == null || !s.StudyProgressLoaded) return null;
            var names = new List<string>();
            if (s.UnreadBookNames != null)
                foreach (string name in s.UnreadBookNames)
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            return names.Count > 0 ? string.Join("、", names) : "无";
        }

        static string UsableItemsText(NpcSnapshot s)
        {
            if (s == null || !s.UsableItemsLoaded) return null;
            var names = new List<string>();
            if (s.UsableItemNames != null)
                foreach (string name in s.UsableItemNames)
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            return names.Count > 0 ? string.Join("、", names) : "无";
        }

        static string GiftablesText(List<GiftableItem> items)
        {
            if (items == null || items.Count == 0) return null;
            var names = new List<string>();
            // 多件标「名×N」,让模型据实报数量、正确选 type=item/resources;名字只填×前部分(数量走 amount 参数)
            foreach (var g in items)
                if (g != null && g.NonEquippedCount > 0 && !string.IsNullOrWhiteSpace(g.Name))
                    names.Add(g.NonEquippedCount > 1
                        ? (g.Name + "×" + g.NonEquippedCount) : g.Name);
            return names.Count > 0 ? string.Join("、", names) : null;
        }

        public static string StatusText(NpcSnapshot s)
        {
            if (s == null) return "";
            var parts = new List<string>();
            if (s.CompletelyInfected) parts.Add("已堕入相枢魔道、心性大变");
            else if (s.QiDisorderChange > 0) parts.Add("气机渐乱、心魔滋长");
            if (s.Happiness <= -20) parts.Add("心绪低落"); else if (s.Happiness >= 20) parts.Add("心境舒畅");
            if (s.Health <= -20) parts.Add("身染沉疴");
            // 地点是每轮实时态，已由 TalkPromptBuilder 的“默认时空上下文”单独注入；
            // 不再塞进 StatusText 重复耗 token，也避免其它画像用途把临时地点当人格证据。
            string status = string.Join("、", parts.ToArray());
            // 人物特性(秉性):NPC 自知自身禀赋特质,据此说话行事更贴人物(志向/性情已由立场+赋性体现)
            string personaFeatures = PersonaFeatureFilter.Join(s.Features);
            if (!string.IsNullOrWhiteSpace(personaFeatures))
                status = (status.Length > 0 ? status + "。" : "") + "你的秉性特质:" + personaFeatures;
            return status;
        }

        // 纯客观底座生成的八节人物圣经。它是首轮/无 LLM 时的确定性兜底：结构足够详细，
        // 但每句话都标清证据边界，不把地点、心情、伤病、资源或臆测经历固化成人设。
        public static string BuildSimple(NpcSnapshot s)
        {
            if (s == null) return "";
            string gender = string.IsNullOrWhiteSpace(s.Gender) ? "未载" : s.Gender.Trim();
            string sexualOrientation = string.IsNullOrWhiteSpace(s.SexualOrientation)
                ? "未可靠读取" : s.SexualOrientation.Trim();
            string personalities = PersonalitiesText(s.Personalities);
            string features = StableFeatureEvidence(s.Features);
            string lifeEvidence = LifeEvidence(s.LifeRecords, 8, 900);

            var sb = new StringBuilder();
            sb.Append("【身份与处境】\n")
              .Append("长期底座只保留较稳定的性别与性取向资料：").Append(gender).Append("性，性取向为“")
              .Append(sexualOrientation).Append("”。")
              .Append("姓名、身龄、命龄、当前立场、门派、身份头衔、品级、关系和好感都属于每轮由游戏代码重新读取的事实，不写入本画像，也不得从旧画像推断。")
              .Append("资料没有写明的籍贯、师承、人脉、财富与隐秘身份一概不补造；长期出处只采用下文有据可查的生平记录。")
              .Append("\n\n");

            sb.Append("【性格底色与内在矛盾】\n")
              .Append("七元赋性为“")
              .Append(string.IsNullOrWhiteSpace(personalities) ? "未取得可靠数值" : personalities).Append("”；稳定秉性特质为“")
              .Append(features).Append("”。这些字段可以约束反应的方向与强弱，却不能自动推出“城府深”“多疑隐忍”或任何具体心理创伤。")
              .Append(PersonalityContrast(s.Personalities))
              .Append("现有资料若没有直接呈现互相拉扯的欲求，就应把内在矛盾保留为未知，而不是为了戏剧性硬造两面人格。")
              .Append("\n\n");

            sb.Append("【价值排序与底线】\n")
              .Append("可确证的长期价值线索只来自稳定特质、七元赋性以及有据可查的经历。当前立场和所属势力由每轮代码快照提供，只能影响当轮判断，不能在这里固化为永久价值观。")
              .Append("若稳定证据不足，就不能替此人发明忠义、名利、复仇等固定排序。")
              .Append("凡资料未明确支持的禁忌、誓言和不可退让之事，都应在实际对话与真实经历出现后再形成；在此之前只按已有立场与特质谨慎判断。")
              .Append("\n\n");

            sb.Append("【欲望·恐惧·软肋】\n")
              .Append("底座没有单独记录此人的长期欲望、恐惧或软肋，故本节不把年龄、门派、好感、眼前物品、当前地点、临时心情和一时伤病擅自改写成执念。")
              .Append("若生平记录明确写到追求、失去、牵挂或畏惧，只能沿用原记录的事实措辞；若没有，就应表现为尚未向太吾显露，而非暗自补上一段悲惨往事。")
              .Append("新的愿望与顾虑必须由后续真实对话、长期记忆或游戏状态变化提供证据后，才可进入稳定画像。")
              .Append("\n\n");

            sb.Append("【待人接物与决断方式】\n")
              .Append("可用于长期决断倾向的依据是：赋性数值“")
              .Append(string.IsNullOrWhiteSpace(personalities) ? "未载" : personalities).Append("”、秉性特质“").Append(features).Append("”。")
              .Append("面对请求时，应让这些长期线索与每轮代码提供的当前立场、身份、关系、能力和工具回执共同决定是否行动；不能只因一句客套便许诺，也不能凭空设定其永远冷酷、永远热心或必定深谋远虑。")
              .Append("资料没有提供固定决策范式时，允许其朴直、迟疑或改变主意，但任何赠物、结缘、伤害等事实都必须以真实落地结果为准。")
              .Append("\n\n");

            sb.Append("【语言风格】\n")
              .Append("当前客观资料没有记录固定自称、对太吾的专属称呼、口头禅、句尾字、语速或雅俗偏好，因此不得编造一句永久口癖。")
              .Append("默认按代码在本轮提供的姓名、身份、年龄、门派环境、双方关系与当轮情绪自然选择礼数：陌生时称呼审慎，亲近后才可随关系变化；没有证据时句式长短适中，不强行文绉绉，也不强行市井粗俗。")
              .Append("无论采用何种口吻，都只说角色本人此刻会说的话，不写后台规则，不替旁人发言，也不把旁白动作冒充台词。")
              .Append("\n\n");

            sb.Append("【有据可查的塑形经历】\n")
              .Append(lifeEvidence)
              .Append("以上只按原始生平记录转述；记录未出现的具体人名、因果、师承、爱恨与动机均不补写。")
              .Append(s.Secrets != null && s.Secrets.Count > 0
                  ? "其资料另含所知秘闻，但“知道某事”不等于亲历某事，不能把秘闻自动写成塑形经历。"
                  : "当前也没有可借由秘闻旁证的新经历。")
              .Append("\n\n");

            sb.Append("【与太吾的关系底色】\n")
              .Append("当前关系标签与好感档位不属于长期画像，均由代码在每轮交谈前实时提供。")
              .Append("本节只允许吸收真实长期记忆中有据可查的共同经历、恩义、嫌隙、承诺与未竟之事；不能把一时好感数值或当前关系名称扩写成未记录的恋情、血仇或共同经历。")
              .Append("若尚无长期关系记忆，就保持未知，让本轮权威关系、聊天历史与真实行动回执决定称呼、信任、戒备和愿意付出的程度。")
              .Append("\n");

            return sb.ToString().Trim();
        }

        private static bool IsValidDocument(string json, int expectedTaiwu, int expectedNpc)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 12, out JToken root)
                || !(root is JObject value)
                || !DurableFileStore.HasOnlyProperties(value, "date", "taiwu", "npc", "portrait", "source",
                    "memCount", "schemaVersion", "memoryFingerprint", "sourceFingerprint", "revision",
                    "writtenUtcTicks")) return false;
            if (value["taiwu"]?.Type != JTokenType.Integer || value["npc"]?.Type != JTokenType.Integer
                || value["portrait"]?.Type != JTokenType.String) return false;
            try
            {
                if (expectedTaiwu > 0 && value["taiwu"].Value<int>() != expectedTaiwu) return false;
                if (expectedNpc > 0 && value["npc"].Value<int>() != expectedNpc) return false;
            }
            catch { return false; }
            string portrait = value.Value<string>("portrait");
            if (string.IsNullOrWhiteSpace(portrait) || portrait.Length > 1048576
                || !DurableFileStore.IsBoundedString(value["source"], 128)
                || !DurableFileStore.IsBoundedString(value["memoryFingerprint"], 256)
                || !DurableFileStore.IsBoundedString(value["sourceFingerprint"], 256)) return false;
            foreach (string field in new[] { "date", "memCount", "schemaVersion", "revision", "writtenUtcTicks" })
                if (value[field] != null && value[field].Type != JTokenType.Integer) return false;
            return true;
        }

        private static string StableFeatureEvidence(List<string> features)
        {
            List<string> personaFeatures = PersonaFeatureFilter.Filter(features);
            if (personaFeatures.Count == 0) return "未取得明确稳定特质";
            var clean = new List<string>();
            foreach (string raw in personaFeatures)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string value = ClipEvidence(raw.Trim(), 40);
                if (!clean.Contains(value)) clean.Add(value);
                if (clean.Count >= 16) break;
            }
            return clean.Count == 0 ? "未取得明确稳定特质" : string.Join("、", clean.ToArray());
        }

        private static string PersonalityContrast(sbyte[] values)
        {
            if (values == null || values.Length == 0) return "赋性数据不足，不能据此确认性格强弱。";
            var pairs = new List<KeyValuePair<string, int>>();
            for (int i = 0; i < QiYuan.Length && i < values.Length; i++)
                pairs.Add(new KeyValuePair<string, int>(QiYuan[i], values[i]));
            if (pairs.Count == 0) return "赋性数据不足，不能据此确认性格强弱。";
            pairs.Sort((a, b) => b.Value.CompareTo(a.Value));
            var high = pairs[0];
            var low = pairs[pairs.Count - 1];
            return "其中相对较高的是“" + high.Key + "(" + high.Value + ")”，相对较低的是“"
                + low.Key + "(" + low.Value + ")”；这只是同一角色各赋性的相对记录，不等同于具体经历或道德结论。";
        }

        private static string LifeEvidence(List<(int date, string type, string text)> records, int max, int maxChars)
        {
            if (records == null || records.Count == 0) return "当前没有可引用的生平记录。";
            var items = new List<string>();
            int from = Math.Max(0, records.Count - Math.Max(1, max));
            int used = 0;
            for (int i = from; i < records.Count; i++)
            {
                var record = records[i];
                if (string.IsNullOrWhiteSpace(record.text)) continue;
                string text = ClipEvidence(record.text.Trim(), 180);
                string prefix = (record.date > 0 ? TalkPromptBuilder.FormatWorldMonth(record.date) : "日期未载")
                    + (string.IsNullOrWhiteSpace(record.type) ? "" : ("·" + ClipEvidence(record.type.Trim(), 24)));
                string item = prefix + "：“" + text + "”";
                if (used + item.Length > maxChars && items.Count > 0) break;
                items.Add(item);
                used += item.Length;
            }
            return items.Count == 0 ? "当前没有内容完整、可直接引用的生平记录。"
                : "可直接核对的近期生平记录有：" + string.Join("；", items.ToArray()) + "。";
        }

        private static string ClipEvidence(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return clean.Length <= max ? clean : clean.Substring(0, max) + "…";
        }

        public static string PersonalitiesText(sbyte[] p)
        {
            if (p == null) return "";
            var pairs = new List<KeyValuePair<string, int>>();
            for (int i = 0; i < 7 && i < p.Length; i++) pairs.Add(new KeyValuePair<string, int>(QiYuan[i], p[i]));
            pairs.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < pairs.Count; i++) parts.Add(pairs[i].Key + "(" + pairs[i].Value + ")");
            return string.Join("、", parts);
        }
    }
}
