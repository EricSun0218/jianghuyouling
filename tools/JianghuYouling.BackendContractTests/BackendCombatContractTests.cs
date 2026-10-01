using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JianghuYouling.BackendContractTests
{
    internal static class BackendCombatContractTests
    {
        private static int Main()
        {
            try
            {
                string root = FindRepositoryRoot();
                string rpc = Read(root, "src", "Shared", "RpcConst.cs");
                string backend = Read(root, "src", "JianghuYouling.Backend", "BackendPluginMain.cs");
                string effects = Read(root, "src", "JianghuYouling.Frontend", "Effects", "EffectHandler.cs");
                string operationRpc = Read(root, "src", "JianghuYouling.Frontend", "Rpc", "OperationRpcClient.cs");
                string talk = Read(root, "src", "JianghuYouling.Frontend", "Talk", "TalkOrchestrator.cs");
                string monthlyEvent = Read(root, "src", "JianghuYouling.Frontend", "Game", "MonthlyEventGenerator.cs");
                string group = Read(root, "src", "JianghuYouling.Frontend", "Talk", "GroupChatOrchestrator.cs");
                string chatWindow = Read(root, "src", "JianghuYouling.Frontend", "UI", "ChatWindow.cs");
                string imagePromptWindow = Read(root, "src", "JianghuYouling.Frontend", "UI",
                    "ImagePromptWindow.cs");
                string safeChatInput = Read(root, "src", "JianghuYouling.Frontend", "UI",
                    "SafeChatInputField.cs");
                string nativeGrooming = Read(root, "src", "JianghuYouling.Frontend", "UI",
                    "NativeGroomingInteraction.cs");
                string worldLifecycle = Read(root, "src", "JianghuYouling.Frontend", "Game", "WorldLifecycle.cs");
                string toolRegistry = Read(root, "src", "JianghuYouling.Core", "Tools", "ToolRegistry.cs");
                string reactionPolicy = Read(root, "src", "JianghuYouling.Core", "Influence",
                    "ConversationReactionPolicy.cs");
                string projectionPolicy = Read(root, "src", "JianghuYouling.Core", "Memory",
                    "GroupMemoryProjectionPolicy.cs");
                string groupCompressor = Read(root, "src", "JianghuYouling.Core", "Memory",
                    "GroupContextCompressor.cs");
                string startupRecoveryPolicy = Read(root, "src", "JianghuYouling.Core", "Persistence",
                    "StartupRecoveryPolicy.cs");
                string snapshotReader = Read(root, "src", "JianghuYouling.Frontend", "Game", "NpcSnapshotReader.cs");
                string proxyIdentity = Read(root, "src", "JianghuYouling.Frontend", "Game",
                    "CharacterProxyIdentityService.cs");
                string talkEntryHost = Read(root, "src", "JianghuYouling.Frontend", "UI",
                    "TalkEntryHost.cs");
                string nativeInteraction = Read(root, "src", "JianghuYouling.Frontend", "Hooks",
                    "NativeInteractionCapture.cs");
                string proxyBeastClassification = Read(root, "src", "JianghuYouling.Frontend", "Hooks",
                    "CharacterProxyBeastClassificationPatch.cs");
                string proxyAvatar = Read(root, "src", "JianghuYouling.Frontend", "Hooks",
                    "CharacterProxyAvatarPatch.cs");
                string chatHistoryEntry = Read(root, "src", "JianghuYouling.Frontend", "UI",
                    "ChatHistoryEntryHost.cs");
                string backendProject = Read(root, "src", "JianghuYouling.Backend",
                    "JianghuYouling.Backend.csproj");
                string portraitStore = Read(root, "src", "JianghuYouling.Frontend", "Portrait",
                    "PortraitStore.cs");
                string talkOrchestrator = Read(root, "src", "JianghuYouling.Frontend", "Talk",
                    "TalkOrchestrator.cs");
                string workshopUpdateChecker = Read(root, "src", "JianghuYouling.Frontend", "Game",
                    "WorkshopUpdateChecker.cs");
                string monthlyEventGenerator = Read(root, "src", "JianghuYouling.Frontend", "Game",
                    "MonthlyEventGenerator.cs");
                string groupJournal = Read(root, "src", "JianghuYouling.Core", "Memory",
                    "GroupExchangeJournal.cs");
                string promptBuilder = Read(root, "src", "JianghuYouling.Core", "Prompt", "TalkPromptBuilder.cs");
                string companion = Read(root, "src", "JianghuYouling.Frontend", "Game", "CompanionMonthlyActions.cs");
                string itemsSkill = Read(root, "src", "JianghuYouling.Core", "ActionSkills",
                    "items_exchange", "SKILL.md");
                string authority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Combat", "CombatDomain.cs");
                string eventCombatAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.TaiwuEvent.EventHelper", "EventHelper.cs");
                string eventDomainAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.TaiwuEvent", "TaiwuEventDomain.cs");
                string combatResultAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Frontend",
                    "Assembly-CSharp", "Game.Views.Combat", "ViewCombatResult.cs");
                string deathCombatCompletionAuthority = Read(root, ".decompiled", "taiwu_eventlibs_b25581398",
                    "Taiwu_EventPackage_CharacterInteraction_Oppose", "ConchShip.EventConfig.Taiwu",
                    "TaiwuEvent_f84d711751d44eab98e7c8f70d00547f.cs");
                string characterAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Character", "CharacterDomain.cs");
                string taiwuAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Taiwu", "TaiwuDomain.cs");
                string extraAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Extra", "ExtraDomain.cs");
                string basicDataAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Frontend",
                    "Assembly-CSharp", "BasicGameData.cs");
                string displayDataAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Frontend",
                    "GameData.Shared", "GameData.Domains.Character.Display", "CharacterDisplayData.cs");
                string merchantAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Merchant", "MerchantDomain.cs");
                string characterObjectAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Character", "Character.cs");
                string eventWindowCharacterAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993",
                    "Frontend", "Assembly-CSharp", "Game.Components.EventWindow", "EventWindowCharacter.cs");
                string mapTipAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993",
                    "Frontend", "Assembly-CSharp", "Game.Views.MouseTips", "MainPanel.cs");
                string monthTipAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993",
                    "Frontend", "Assembly-CSharp", "Game.Views.MouseTips", "MouseTipMonthNotify.cs");
                string characterMenuInfoAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993",
                    "Frontend", "Assembly-CSharp", "Game.Views.CharacterMenu", "ViewCharacterMenuInfo.cs");
                string commonUtilsAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Frontend",
                    "Assembly-CSharp", "CommonUtils.cs");
                string travelTargetAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData.Shared", "GameData.Domains.Character.Ai", "NpcTravelTarget.cs");
                string appointmentAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData.ActionPlanning.ActionImpl", "GameData.ActionPlanning.ActionImpl", "AppointmentAction.cs");
                string protectAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData.ActionPlanning.ActionImpl", "GameData.ActionPlanning.ActionImpl", "ProtectFriendOrFamilyAction.cs");
                string rescueAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData.ActionPlanning.ActionImpl", "GameData.ActionPlanning.ActionImpl", "RescueFriendOrFamilyAction.cs");
                string revengeAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData.ActionPlanning.ActionImpl", "GameData.ActionPlanning.ActionImpl", "TakeRevengeAction.cs");
                string huntTaiwuAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData.ActionPlanning.ActionImpl", "GameData.ActionPlanning.ActionImpl", "HuntTaiwuAction.cs");
                string giveItemAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Character.Ai.GeneralAction.BehaviorAction", "GiveItemAction.cs");
                string stealItemAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Character.Ai.GeneralAction.WealthDemand", "StealItemAction.cs");
                string requestItemAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Character.Ai.GeneralAction.WealthDemand", "RequestItemAction.cs");
                string robItemAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Character.Ai.GeneralAction.WealthDemand", "RobItemAction.cs");
                string scamItemAuthority = Read(root, ".decompiled", "taiwu_decomp_b25596993", "Backend",
                    "GameData", "GameData.Domains.Character.Ai.GeneralAction.WealthDemand", "ScamItemAction.cs");

                string gmCore = Slice(backend,
                    "private static SerializableModData GmCore(",
                    "private static bool IsGmMutationOp(");
                string physicalPairs = Slice(backend,
                    "private static bool TryGetPhysicalInteractionPair(",
                    "private static SerializableModData Kill(");
                string killMutation = Slice(backend,
                    "private static SerializableModData Kill(",
                    "private static SerializableModData Capture(");
                string captureMutation = Slice(backend,
                    "private static SerializableModData Capture(",
                    "private static bool TryValidateCombatInteractionAnchor(");
                RequireContains(gmCore, "TryGetPhysicalInteractionPair(op, p, out int physicalActorId",
                    "all GM physical mutations enter the shared live-location gate");
                RequireContains(gmCore, "if (!SameValidLocation(physicalActor, physicalTarget))",
                    "all GM physical mutations fail closed when participants are apart");
                foreach (string physicalOp in new[]
                {
                    "givesilver", "giveitem", "teachlife", "writebook",
                    "taiwu_give_item", "taiwu_teach", "barter", "event_teach",
                    "event_gift", "spend_night", "steal", "poison", "heal", "trade",
                })
                    RequireContains(physicalPairs, "case \"" + physicalOp + "\":",
                        physicalOp + " is covered by the shared live-location gate");
                RequireContains(killMutation, "if (!SameValidLocation(npc, target))",
                    "kill revalidates live co-location in its dedicated backend endpoint");
                RequireContains(captureMutation, "if (!SameValidLocation(npc, target))",
                    "capture revalidates live co-location in its dedicated backend endpoint");
                RequireContains(backend, "result.Set(\"same_valid_location\", sameValidLocation);",
                    "monthly preflight returns the authoritative live co-location verdict");
                RequireContains(effects, "resp.Get(\"same_valid_location\", out value.SameValidLocation);",
                    "frontend monthly preflight parses the authoritative co-location verdict");
                RequireContains(monthlyEvent,
                    "RequiresCoLocatedMonthlyAction(tool) && !state.SameValidLocation",
                    "monthly event dispatcher rejects remote physical actions before mutation");

                string relateNpcMutation = Slice(backend,
                    "case \"relate_npc\":",
                    "case \"addfeature\":");
                string taiwuRelationMutation = Slice(backend,
                    "private static SerializableModData ExecuteRelation(",
                    "// 过月给 NPC 注入行动目标");
                RequireContains(characterObjectAuthority,
                    "public static void ApplyAddRelation_Adore(DataContext context, Character selfChar, Character targetChar, sbyte charBehaviorType, bool targetLovesBack",
                    "1.0.72 exposes directional adoration with an explicit reciprocal-love flag");
                RequireContains(characterObjectAuthority,
                    "AllowAddingFriendRelation(id2, id)",
                    "1.0.72 native friendship writer validates target before initiator");
                RequireContains(characterObjectAuthority,
                    "AllowAddingSwornBrotherOrSisterRelation(id2, id)",
                    "1.0.72 native sworn writer validates target before initiator");
                RequireContains(relateNpcMutation,
                    "AllowAddingFriendRelation(bId, aId)",
                    "third-party friendship preflight follows the native target/initiator direction");
                RequireContains(relateNpcMutation,
                    "AllowAddingSwornBrotherOrSisterRelation(bId, aId)",
                    "third-party sworn preflight follows the native target/initiator direction");
                RequireContains(taiwuRelationMutation,
                    "AllowAddingFriendRelation(relationTargetId, relationHolderId)",
                    "Taiwu relationship preflight follows the native target/holder direction");
                RequireContains(taiwuRelationMutation,
                    "AllowAddingSwornBrotherOrSisterRelation(relationTargetId, relationHolderId)",
                    "Taiwu sworn preflight follows the native target/holder direction");
                RequireContains(relateNpcMutation,
                    "TryApplyDirectedAdoration(context, ca, cb, aT, bT)",
                    "third-party NPC relationship actions only write the initiating NPC's adoration");
                RequireContains(taiwuRelationMutation,
                    "TryApplyDirectedAdoration(context, npc, taiwu,",
                    "NPC-to-Taiwu relationship actions only write the initiating NPC's adoration");
                RequireContains(backend,
                    "Character.ApplyAddRelation_Adore(context, self, target, self.GetBehaviorType(), false,",
                    "directed adoration never grants reciprocal love on the target's behalf");
                Require(!backend.Contains("TryApplyMutualLoverRelation", StringComparison.Ordinal),
                    "no NPC action may synthesize mutual love by writing both directions");
                RequireContains(toolRegistry,
                    "lover=你单方面爱慕太吾",
                    "chat tools explain that an NPC only controls its own adoration");
                RequireContains(companion,
                    "lover 只写入你对目标的单向爱慕",
                    "companion-monthly tools preserve directional adoration semantics");
                RequireContains(monthlyEvent,
                    "lover 只写入行动者→目标的单向爱慕",
                    "jianghu event tools preserve directional adoration semantics");
                RequireContains(effects,
                    "onDone?.Invoke(ok, msg ?? (ok ? \"关系已建立\" : \"未能缔结\"))",
                    "third-party relationship callers receive the authoritative landed relation state");
                RequireContains(talk,
                    "case \"lover\": okZh = \"向太吾表达爱慕\"",
                    "conversation results no longer predeclare mutual love");

                string leaveMutation = Slice(backend,
                    "private static SerializableModData Leave(",
                    "// 入队:让 NPC 真正加入太吾队伍");
                string joinTeamMutation = Slice(backend,
                    "private static SerializableModData JoinTeam(",
                    "private static bool PrepareJoinEquipmentPlan(");
                string joinEquipmentRecovery = Slice(backend,
                    "private static bool PrepareJoinEquipmentPlan(",
                    "// 男媒女约:");
                string taiwuJoinAuthority = Slice(taiwuAuthority,
                    "public void JoinGroup(DataContext context, int charId, bool showNotification = true)",
                    "public void LeaveGroup(DataContext context, int charId, bool bringWards = true");
                RequireBefore(taiwuJoinAuthority,
                    "DomainManager.Taiwu.ApplyGroupCharEquipmentPlan(context, charId);",
                    "GameData.Domains.Character.Character element_Objects = DomainManager.Character.GetElement_Objects(charId);",
                    "1.0.72 restores a saved teammate equipment plan before any join-group mutation");
                RequireContains(joinTeamMutation, "npc.GetKidnapperId() >= 0",
                    "join-team rejects kidnapped characters before calling the native mutation");
                RequireContains(joinTeamMutation, "DomainManager.Organization.GetPrisonerSect(npcId) >= 0",
                    "join-team rejects imprisoned characters before calling the native mutation");
                RequireBefore(joinTeamMutation,
                    "PrepareJoinEquipmentPlan(context, npcId, out prepareError)",
                    "DomainManager.Taiwu.JoinGroup(context, npcId);",
                    "join-team resolves the fallible saved equipment plan before the native group mutation");
                RequireContains(joinEquipmentRecovery,
                    "DomainManager.Taiwu.ApplyGroupCharEquipmentPlan(context, npcId);",
                    "join-team preserves native equipment restoration when the saved plan is valid");
                RequireBefore(joinEquipmentRecovery,
                    "DomainManager.Taiwu.AddManualChangeEquipGroupChar(context, npcId);",
                    "DomainManager.Taiwu.RemoveManualChangeEquipGroupChar(context, npcId, keepRecord: false);",
                    "a broken saved equipment plan is discarded through native public record APIs");
                RequireContains(joinTeamMutation,
                    "joined = DomainManager.Taiwu.IsInGroup(npcId);",
                    "a native join tail exception is reconciled against the authoritative group post-state");
                RequireContains(leaveMutation,
                    "stillFollowing = DomainManager.Taiwu.IsCharacterFollowedByTaiwu(npcId);",
                    "a native leave tail exception is reconciled against the authoritative follow post-state");
                RequireContains(leaveMutation,
                    "stillInGroup = DomainManager.Taiwu.IsInGroup(npcId);",
                    "a native leave tail exception is reconciled against the authoritative group post-state");

                RequireContains(rpc, "EnsureCharacterProxyMethod = \"JYL_EnsureCharacterProxy\"",
                    "fixed-template identity resolution has a dedicated backend mutation");
                RequireContains(backend,
                    "AddMutationMethod(RpcConst.EnsureCharacterProxyMethod, EnsureCharacterProxy);",
                    "fixed-template identity resolution is registered as a mutation");
                string proxyMutation = Slice(backend,
                    "private static SerializableModData EnsureCharacterProxy(",
                    "private static SerializableModData CharacterProxyResult(");
                string nativeProxySourceResolution = Slice(backend,
                    "private static bool TryResolveNativeCharacterSource(",
                    "private static SerializableModData EnsureCharacterProxy(");
                RequireContains(characterAuthority,
                    "public Character CreateTemporaryCopyOfCharacter(DataContext context, Character srcCharacter)",
                    "1.0.72 exposes the authoritative temporary-character copy path");
                RequireContains(characterAuthority, "character.OfflineSetCopySource(id);",
                    "native temporary copies retain their authoritative source character id");
                RequireContains(characterObjectAuthority, "public int GetSrcCharId()",
                    "1.0.72 exposes the authoritative source id on every character instance");
                RequireContains(characterAuthority, "public bool IsTemporaryEnemy(int charId)",
                    "1.0.72 exposes the authoritative transient-event-enemy classification");
                RequireContains(characterAuthority,
                    "Both sides must be intelligent characters",
                    "native relation creation throws and terminates the backend on an invalid character pair");
                string invalidRelationGuard = Slice(backend,
                    "internal static class InvalidGeneralRelationCrashGuardPatch",
                    "internal static class CharacterProxyAnimalDowngradeGuardPatch");
                RequireContains(backend,
                    "nameof(CharacterDomain.TryCreateGeneralRelation)",
                    "invalid native relation creation is guarded at the authoritative boundary");
                RequireContains(invalidRelationGuard,
                    "selfChar.GetCreatingType() == 1 && relatedChar.GetCreatingType() == 1",
                    "normal intelligent-character relation creation remains on the native path");
                RequireContains(invalidRelationGuard, "HarmonyPriority(Priority.First)",
                    "the fatal relation guard runs before third-party relation prefixes");
                RequireContains(invalidRelationGuard, "return false;",
                    "invalid pairs are rejected without allowing the native fatal assertion to run");
                RequireContains(nativeProxySourceResolution, "current.GetSrcCharId()",
                    "proxy identity follows the native temporary-copy source relation");
                RequireContains(nativeProxySourceResolution, "var visited = new HashSet<int>();",
                    "native copy-source traversal fails closed on cyclic source data");
                Require(!nativeProxySourceResolution.Contains("GetTemplateId", StringComparison.Ordinal),
                    "temporary character identity is never guessed from a shared template id");
                RequireContains(proxyMutation,
                    "TryResolveNativeCharacterSource(requestedId, out int canonicalSourceId,",
                    "unmapped conversation targets are canonicalized before proxy lookup or creation");
                RequireContains(proxyMutation, "if (canonicalSourceId == taiwuId)",
                    "temporary projections of Taiwu can never create a Jianghu Youling proxy");
                RequireContains(proxyMutation,
                    "DomainManager.Character.IsTemporaryEnemy(requestedId)",
                    "transient event enemies are rejected before any durable proxy mapping is created");
                RequireContains(proxyMutation,
                    "source.GetTemplateId() == Config.Character.DefKey.CrossArchiveFuyuHilt",
                    "the cross-archive Fuyu hilt map marker can never become a conversation proxy");
                RequireContains(proxyMutation,
                    "source.GetTemplateId() == Config.Character.DefKey.OverwrittenTaiwu",
                    "the fixed map-only Taiwu portrait entity can never become a conversation proxy");
                RequireBefore(proxyMutation,
                    "TryGetPersistedCharacterProxy(canonicalSourceId, out int existingProxy)",
                    "CreateLegalCharacterProxy(context, source, out string createError)",
                    "all temporary copies of one source reuse its existing durable proxy");
                RequireContains(backend,
                    "DomainManager.Character.CreateIntelligentCharacter(context, ref info)",
                    "fixed-template conversations create a legal native intelligent character");
                RequireContains(backend,
                    "Config.Character.Instance[templateId].CreatingType == 1",
                    "proxy templates are postcondition-checked as native intelligent templates");
                RequireContains(backend,
                    "FinalizeNewLegalProxyData(context, source, proxy, out string finalizeError)",
                    "new legal proxies must finish their safe data initialization before identity mapping");
                RequireContains(backend,
                    "proxy.RecreateMainAttributes(context, 0, 8);",
                    "new proxies rebuild missing fixed-template main attributes through the native creator");
                RequireContains(backend,
                    "proxy.RecreateLifeSkillQualifications(context, 0, 8);",
                    "new proxies rebuild missing fixed-template life qualifications through the native creator");
                RequireContains(backend,
                    "proxy.RecreateCombatSkillQualifications(context, 0, 8);",
                    "new proxies rebuild missing fixed-template combat qualifications through the native creator");
                RequireContains(backend,
                    "safeName.Type = (sbyte)(safeName.Type & ~FullNameType.NoNameInfant);",
                    "fixed-template names cannot accidentally lock proxy life records as unnamed infants");
                RequireContains(backend,
                    "safeName = new FullName(singleNameId, -1, -1);",
                    "single-name fixed characters cannot inherit a random native surname");
                RequireContains(backend,
                    "feature == null || !feature.IsNormal() || feature.Hidden",
                    "new proxies copy only ordinary permanent features, never story or boss lifecycle traits");
                RequireContains(backend,
                    "proxy.SetMainAttributeInterest(mainInterest, context);",
                    "new proxies retain valid fixed-character attribute interests");
                RequireContains(backend,
                    "proxy.SetConsummateLevel((sbyte)Math.Min(sourceLevel, proxyMaxLevel), context);",
                    "source attainment level is preserved without exceeding the legal proxy's native cap");
                RequireContains(backend,
                    "DomainManager.CombatSkill.RemoveAllCombatSkills(proxy.GetId());",
                    "source combat skills replace the native creator's unrelated random skill set");
                RequireContains(backend,
                    "GameData.Serializer.Serializer.CreateCopy(sourceSkill)",
                    "new proxies retain source combat-skill page and direction state through native copies");
                RequireContains(backend,
                    "sourcePanels.Length == proxyPanels.Length",
                    "malformed fixed-character attainment panels cannot replace the legal native shape");
                RequireContains(backend,
                    "effect.ClassName.IndexOf(\"Neigong.Boss\"",
                    "boss inner skills are identified through the authoritative special-effect class");
                RequireContains(backend,
                    "skill.SetRevoked(true, context);",
                    "boss inner skills cannot activate their fixed-character runtime effects on a legal proxy");
                RequireBefore(backend,
                    "FinalizeNewLegalProxyData(context, source, proxy, out string finalizeError)",
                    "PersistProxyIdentity(context, canonicalSourceId, proxyId, 0",
                    "new proxy data is validated before its durable identity is published");
                RequireContains(backend,
                    "Location location = taiwu == null ? Location.Invalid : taiwu.GetValidLocation();",
                    "legal proxies avoid copying transient special-character map locations");
                Require(!backend.Contains("if (!location.IsValid()) location = source.GetLocation();"),
                    "legal proxies never fall back to a fixed character's special map location");
                RequireContains(backend,
                    "TryPickLegalProxyIdentity(context, gender, physiologicalAge,",
                    "legal proxies randomize a native-valid ordinary identity");
                RequireContains(backend,
                    "member.RestrictPrincipalAmount",
                    "proxy identity selection excludes limited principal offices");
                RequireContains(backend,
                    "organization.IsSect || !organization.IsCivilian",
                    "proxy identity selection excludes every sect identity");
                RequireContains(backend,
                    "!OrganizationDomain.MeetGenderRestriction(orgId, gender)",
                    "proxy identity selection honors native organization gender restrictions");
                RequireContains(backend,
                    "physiologicalAge < member.IdentityActiveAge",
                    "proxy identity selection honors native identity activation ages");
                RequireContains(backend,
                    "proxy.SetBaseMorality((short)(context.Random.Next(1001) - 500), context);",
                    "fixed-character morality is replaced by an independent legal random value");
                RequireContains(backend,
                    "feature.PersonalityCalm != 0 || feature.PersonalityClever != 0",
                    "source features cannot overwrite the native-random proxy personality");
                Require(!backend.Contains("new OrganizationInfo(0, 8, principal: true, settlementId), templateId"),
                    "new proxies no longer receive one fixed no-organization identity");
                Require(!backend.Contains("DomainManager.Character.DeepCopy(context, source, false)"),
                    "fixed-template proxies must not duplicate a fixed TemplateId into the save");
                RequireContains(backend, "PersistProxyIdentity(context, canonicalSourceId, proxyId, 0",
                    "the original-to-copy identity is persisted in the game save");
                RequireContains(backend, "CharacterProxyReverseKey(proxyId)",
                    "the copy-to-original identity is persisted for idempotent routing");
                RequireContains(characterAuthority,
                    "public Character DeepCopy(DataContext context, Character character, bool connectWithSrc = false)",
                    "1.0.72 exposes the authoritative character deep-copy path");
                RequireContains(characterAuthority, "character.OfflineSetCopySource(charId);",
                    "the native unpack path supports an independent copy source identity");
                RequireContains(backend, "LegacyCharacterProxyLoadGuardPatch",
                    "old DeepCopy saves are neutralized before native fixed-cache initialization");
                string legacyProxyLoadGuard = Slice(backend,
                    "internal static class LegacyCharacterProxyLoadGuardPatch",
                    "internal static class NativeInteractionMutationAudit");
                RequireContains(legacyProxyLoadGuard,
                    "BackendPluginMain.TryGetCharacterProxySource(pair.Key, out _)",
                    "legacy proxy load handling requires an exact durable Jianghu Youling identity");
                Require(!legacyProxyLoadGuard.Contains("canonicalIds", StringComparison.Ordinal)
                    && !legacyProxyLoadGuard.Contains("bool duplicate", StringComparison.Ordinal),
                    "native same-template animals and enemies are never guessed to be legacy proxies");
                RequireContains(backend,
                    "CompletePendingLegacyMigration(context, existingCanonicalSourceId, replacement,",
                    "partial legacy migrations resume through team and fixed-cache cleanup");
                RequireContains(proxyAvatar, "CharacterProxyIdentityService.TryGetDisplayTemplate",
                    "legal proxy entities render through their original fixed visual template");
                RequireContains(proxyAvatar, "ref short characterTemplateId",
                    "fixed visuals are substituted at the avatar argument boundary");
                RequireContains(proxyAvatar,
                    "CharacterProxyAvatarRenderScope.ReplaceCellTemplate(__result)",
                    "general list avatars replace only the render-only cell result");
                RequireContains(proxyAvatar,
                    "CharacterProxyAvatarRenderScope.Enter(data?.CharacterId ?? 0)",
                    "direct avatar entry points establish a character-scoped render alias");
                RequireContains(proxyAvatar, "typeof(CardItem), \"ShowCharacterAvatar\"",
                    "character-bound inventory cards render through the scoped proxy identity");
                RequireContains(proxyAvatar, "typeof(MouseTipMonthNotify), \"_dataList\"",
                    "monthly-notification avatar cells are normalized before their first render");
                RequireContains(mapTipAuthority,
                    "private void SetAvatar(CharacterDisplayDataForMapBlock data, bool isTaiwu)",
                    "current native map tooltip has the shared avatar-only entry point");
                RequireContains(mapTipAuthority, "SetAvatar(data, isTaiwu);",
                    "normal and tower map tooltips share the scoped avatar entry point");
                RequireContains(proxyAvatar, "typeof(MainPanel), \"SetAvatar\"",
                    "map tooltip patch avoids native Set overload ambiguity");
                RequireContains(proxyAvatar,
                    "new[] { typeof(CharacterDisplayDataForMapBlock), typeof(bool) }",
                    "map tooltip patch pins the exact shared avatar signature");
                RequireContains(monthTipAuthority, "public override void Refresh()",
                    "native monthly tooltip exposes a parameterless refresh");
                RequireContains(proxyAvatar, "nameof(MouseTipMonthNotify.Refresh), new Type[] { }",
                    "monthly avatar patch pins the parameterless refresh");
                RequireContains(characterMenuInfoAuthority, "private CButton talkButton;",
                    "native character menu provides the current conversation anchor");
                RequireContains(chatHistoryEntry, "GetField(\"talkButton\", NP)",
                    "character entry layout uses the current native talk field");
                Require(!chatHistoryEntry.Contains("GetField(\"interactionButton\""),
                    "character entry no longer queries a removed native field");
                RequireContains(characterObjectAuthority,
                    "CValuePercentBonus attainmentBonus = default(CValuePercentBonus)",
                    "current healing signature requires native combat math types");
                RequireContains(backendProject, "<Reference Include=\"GameData.Combat.Math\">",
                    "backend resolves the native combat math dependency at build time");
                Require(!proxyAvatar.Contains("displayData.TemplateId = displayTemplateId"),
                    "avatar rendering must not mutate shared character display data");
                Require(!proxyAvatar.Contains("data.CharacterTemplateId = displayTemplateId"),
                    "avatar rendering must not mutate shared list input data");
                Require(!proxyAvatar.Contains("TemplateIdField.SetValue"),
                    "avatar rendering must not mutate the shared monitor template field");
                RequireContains(snapshotReader,
                    "s.CharacterTemplateId = ResolveIdentityTemplateId(npcId, dd.TemplateId);",
                    "legal proxies still select the original fixed character persona catalog entry");
                RequireContains(backend,
                    "CharacterProxyAnimalDowngradeGuardPatch",
                    "legacy fixed-template proxies guard the native hunter-animal downgrade path");
                RequireContains(backend,
                    "!BackendPluginMain.IsCharacterProxy(character.GetId())",
                    "the hunter-animal compatibility guard is scoped to Jianghu Youling proxies");
                RequireContains(proxyBeastClassification,
                    "nameof(CharacterMonitorModel.IsTaiwuBeastTeammate)",
                    "the frontend corrects the native beast classification for fixed-template proxies");
                RequireContains(proxyBeastClassification,
                    "nameof(ViewCharacterMenu.IsTaiwuBeastTeammate)",
                    "the character menu leave button also uses normal teammate routing for fixed-template proxies");
                RequireContains(proxyBeastClassification,
                    "CharacterProxyIdentityService.IsKnownProxy(taiwuId, charId)",
                    "the frontend beast correction is scoped to durable Jianghu Youling proxy identities");
                RequireContains(proxyBeastClassification, "\"GetCanViewSocial\"",
                    "legal proxies retain the native social page despite their fixed visual alias");
                RequireContains(proxyBeastClassification, "\"GetCanViewRelation\"",
                    "legal proxies retain the native relation page despite their fixed visual alias");
                RequireContains(proxyBeastClassification, "\"GetCanViewLifeRecord\"",
                    "legal proxies retain the native life-record page despite their fixed visual alias");
                RequireContains(snapshotReader,
                    "s.TaiwuGender = tGender;",
                    "every conversation snapshot records Taiwu gender from live display data");
                RequireContains(promptBuilder,
                    "玩家/太吾=\" + taiwuName + \"(#\" + npc.TaiwuId + \",\" + taiwuGender",
                    "the live Taiwu gender is injected into the authoritative conversation identity ledger");
                RequireContains(group,
                    "RefreshIdentityContext = latest => BuildIdentityContext(sp, latest)",
                    "persistent group tabs rebuild Taiwu gender from each member's live turn snapshot");
                RequireContains(workshopUpdateChecker,
                    "var info = ModManager.GetModInfo(modId);",
                    "workshop update checks resolve the installed mod root through the native mod registry");
                RequireContains(workshopUpdateChecker,
                    "if (TryReadLoadedModIdentity(out version, out fileId, out source)) return true;",
                    "workshop update checks prefer the already validated loaded mod identity");
                RequireContains(workshopUpdateChecker,
                    "JianghuYouling.Frontend.dll",
                    "local mods with native temporary ids are identified by their loaded frontend plugin");
                RequireContains(workshopUpdateChecker,
                    "if (string.IsNullOrWhiteSpace(assemblyPath)) return null;",
                    "byte-loaded Unity assemblies cannot throw while falling back to Assembly.Location");
                RequireContains(chatWindow,
                    "ImagePromptWindow.Show(r, initialPrompt, _font,",
                    "image generation opens an editable prompt confirmation before capture or network access");
                RequireContains(chatWindow,
                    "GenerateRoundImageCo(r, lifecycleVersion, requestVersion,",
                    "only the player-confirmed image prompt enters the generation coroutine");
                RequireContains(chatWindow,
                    "string prompt = SecretRedactor.Redact(confirmedPrompt, secrets);",
                    "the confirmed image prompt is redacted immediately before provider submission");
                RequireContains(imagePromptWindow,
                    "以下内容将作为提示词发送给生图服务。请检查并自行修改；关闭窗口不会发送。",
                    "the image prompt dialog explains its send boundary to the player");
                RequireContains(imagePromptWindow,
                    "ImageGenerationClient.MaxPromptChars + 1",
                    "the editable image prompt uses the same provider request character bound");
                RequireContains(safeChatInput,
                    "TmpWarningsDisabledField.SetValue(tmpSettings, true)",
                    "Jianghu Youling input redraws suppress per-frame missing-glyph stack spam");
                string rollbackReset = Slice(monthlyEventGenerator,
                    "if (stale.StartDate >= currentDate)",
                    "int retainedChapterCount = 0;");
                RequireContains(rollbackReset,
                    "Revision = stale.Revision,",
                    "whole-saga rollback reset advances from the committed revision instead of being rejected as stale");
                RequireContains(characterAuthority, "list.AddRange(specialGroup);",
                    "native GetGroupSet merges special-group copies into the visible party roster");
                string delayedProxyMessage = Slice(chatWindow,
                    "void BeginProxyPreparationForMessage(",
                    "void FinishProxyPreparationUi()");
                RequireContains(delayedProxyMessage,
                    "CharacterProxyIdentityService.EnsureForConversation(_taiwuId, sourceNpcId",
                    "single-chat identity creation starts only from the first actual player message");
                RequireContains(proxyIdentity, "int backendNpcId = npcId;",
                    "first-open proxy validation starts from the character actually selected in the current game state");
                Require(!proxyIdentity.Contains("int backendNpcId = known;", StringComparison.Ordinal),
                    "stale frontend proxy routing never replaces backend save authority during first-open validation");
                RequireContains(proxyIdentity,
                    "(ok, originalId, resolvedId, created, isProxy, migrationFromId,",
                    "the frontend preserves backend proxy classification for timestamp persistence");
                RequireContains(proxyIdentity,
                    "known > 0 && known != npcId && known != resolvedId",
                    "a stale local proxy identity is migrated when backend authority returns a different live proxy");
                RequireContains(proxyIdentity,
                    "!Register(taiwuId, known, resolvedId, displayTemplateId,",
                    "stale local proxy routes are durably flattened to the newly authoritative identity");
                RequireContains(proxyIdentity,
                    "CopyDates = new Dictionary<int, int>(_copyDates)",
                    "fixed-template proxy routes durably record their game-month creation time");
                RequireContains(proxyIdentity,
                    "private const int UntimestampedSchemaVersion = 1;",
                    "existing fixed-template visual mappings survive the timestamp schema upgrade");
                RequireContains(proxyIdentity,
                    "int copyDate = isProxy && created ? CurrentWorldDate() : -1;",
                    "only a backend-confirmed new proxy records the current month as its creation time");
                RequireContains(proxyIdentity,
                    "if (!WorldLifecycle.HasWorldDate) return -1;",
                    "copy timestamp recording waits for the authoritative world date");
                Require(!proxyIdentity.Contains("PruneFutureCopies", StringComparison.Ordinal)
                    && !proxyIdentity.Contains("DeleteFutureIdentityContent", StringComparison.Ordinal),
                    "save rollback never deletes proxy identities or their external content");
                RequireContains(proxyIdentity,
                    "int stableOriginalId = originalId > 0 ? originalId : npcId;",
                    "frontend proxy routing persists the backend-canonical source identity");
                RequireContains(proxyIdentity,
                    "RegisterVolatileAlias(npcId, resolvedId);",
                    "one-scene temporary actor ids are routed in memory without polluting durable identity state");
                string nativeRecordingEnable = Slice(talkEntryHost,
                    "static IEnumerator EnableNativeInteractionRecording(",
                    "static int ResolveRecordingNpcId(");
                RequireContains(talkEntryHost,
                    "EwChar selectedCharacter = EwReflect.Right(w);",
                    "interaction recording captures the exact native target component before asynchronous work");
                string resolveDisplayedNpc = Slice(talkEntryHost,
                    "public static int ResolveNpcId(EwChar rc)",
                    "public static int TaiwuId()");
                RequireContains(eventWindowCharacterAuthority, "_curCharacterId = -1;",
                    "native event actor rendering clears the real displayed-character identity");
                RequireContains(eventWindowCharacterAuthority,
                    "_curCharacterId = characterDisplayData.CharacterId;",
                    "native normal-character rendering alone publishes a real displayed-character identity");
                Require(!resolveDisplayedNpc.Contains("DisplayingEventData", StringComparison.Ordinal),
                    "AI controls never recover a hidden target id while an actor, illusion, or narrative portrait is displayed");
                string nativeInteractionContext = Slice(nativeInteraction,
                    "private static bool TryContext(",
                    "private static SelectionAttempt LatestAttempt(");
                RequireContains(nativeInteractionContext,
                    "int displayedNpcId = EwReflect.ResolveNpcId(EwReflect.Right(window));",
                    "native interaction capture binds to the actual normal character shown on the right");
                RequireContains(nativeInteractionContext,
                    "displayedNpcId == data.TargetCharacter.CharacterId",
                    "native interaction capture rejects event actors and stale hidden targets");
                Require(Regex.Matches(nativeRecordingEnable,
                        Regex.Escape("EwReflect.IsCurrentTarget(w, selectedCharacter, selectedNpcId)"))
                        .Count >= 3,
                    "interaction recording revalidates the exact target before and after asynchronous proxy work");
                string singleOpen = Slice(chatWindow,
                    "public static void Open(int npcId, int taiwuId, string npcName, TMP_FontAsset font)",
                    "public static void OpenRemote(int npcId, int taiwuId, string npcName, TMP_FontAsset font)");
                RequireContains(singleOpen,
                    "CharacterProxyIdentityService.ResolveKnown(taiwuId, npcId)",
                    "single-chat opening only follows an already-established proxy route");
                Require(!singleOpen.Contains("EnsureForConversation", StringComparison.Ordinal),
                    "single-chat opening never creates a fixed-template proxy before the player speaks");
                string remoteOpen = Slice(chatWindow,
                    "public static void OpenRemote(int npcId, int taiwuId, string npcName, TMP_FontAsset font)",
                    "private static void OpenResolved(int npcId, int taiwuId, string npcName");
                RequireContains(remoteOpen,
                    "CharacterProxyIdentityService.ResolveKnown(taiwuId, npcId)",
                    "remote opening only follows an already-established proxy route");
                Require(!remoteOpen.Contains("EnsureForConversation", StringComparison.Ordinal),
                    "remote opening never creates a fixed-template proxy before the player speaks");
                string soulOpen = Slice(chatWindow,
                    "private static bool TryOpenSoulConversation(",
                    "public static void Open(int npcId, int taiwuId, string npcName, TMP_FontAsset font)");
                RequireContains(soulOpen,
                    "ArchivedCharacterStatusStore.IsDead(taiwuId, npcId)",
                    "soul-entry bypass is gated by durable authoritative death state");
                RequireContains(soulOpen,
                    "OpenResolved(knownDead ? knownId : npcId, taiwuId, npcName, font, false);",
                    "soul entries normalize direct and remote clicks to the non-physical conversation mode");
                string resolvedOpen = Slice(chatWindow,
                    "private static void OpenResolved(int npcId, int taiwuId, string npcName",
                    "public static void OpenAssistant(TMP_FontAsset font = null)");
                RequireContains(resolvedOpen, "tab.RefreshCharacterArchiveStatus();",
                    "reused live tabs refresh into soul mode after authoritative death is persisted");
                string groupOpen = Slice(chatWindow,
                    "static void OpenGroupCore(List<KeyValuePair<int, string>> roster",
                    "static void OpenGroupCoreResolved(");
                RequireContains(groupOpen, "CharacterProxyIdentityService.ResolveKnown",
                    "group-chat opening only follows already-established proxy routes");
                Require(!groupOpen.Contains("EnsureRoster", StringComparison.Ordinal),
                    "opening a group never creates a special-character proxy without a message");
                RequireContains(chatWindow,
                    "请先在单聊中发送消息或开启互动记录，再将其加入群聊",
                    "unresolved special characters are blocked from group membership without hidden copying");
                RequireBefore(proxyIdentity,
                    "&& !PrepareIdentityContent(taiwuId, migrationSource, resolvedId))",
                    "else if (error == null && !Register(taiwuId, stableOriginalId, resolvedId,",
                    "local routing is published only after conversation/persona/memory copies are durable");
                RequireBefore(proxyIdentity,
                    "else if (error == null && !Register(taiwuId, stableOriginalId, resolvedId,",
                    "FinalizeIdentityMigration(taiwuId, migrationSource);",
                    "source identity content is cleaned only after local routing is durable");
                string prepareProxyIdentity = Slice(proxyIdentity,
                    "private static bool PrepareIdentityContent(",
                    "private static bool ReconcileIdentityState(");
                Require(!prepareProxyIdentity.Contains("DeleteFile(", StringComparison.Ordinal)
                        && !prepareProxyIdentity.Contains("\"\", \"replace\"", StringComparison.Ordinal),
                    "identity preparation never destroys source persona or memory");
                string prepareConversationIdentity = Slice(talkOrchestrator,
                    "internal static bool MigrateConversationIdentity(",
                    "internal static bool FinalizeConversationIdentityMigration(");
                Require(!prepareConversationIdentity.Contains("PurgeConversationStorage(",
                        StringComparison.Ordinal),
                    "conversation preparation keeps the source transcript until routing is durable");
                string preparePortraitIdentity = Slice(portraitStore,
                    "public static bool ReplaceIdentity(",
                    "public static int GetConsolidatedMemCount(");
                Require(!preparePortraitIdentity.Contains("Delete(taiwuId, oldNpcId)",
                        StringComparison.Ordinal),
                    "portrait preparation keeps the source portrait until routing is durable");
                RequireContains(proxyIdentity,
                    "GroupChatOrchestrator.MigrateMemberIdentity(taiwuId, oldNpcId, newNpcId)",
                    "existing group transcripts migrate to the copy identity");
                RequireContains(group, "journal.ReplaceNpcIdentity(oldNpcId, newNpcId)",
                    "group transaction journals migrate alongside group transcripts");
                RequireContains(group, "archiveIndex <= document.ArchivedSegmentCount",
                    "archived group transcript segments migrate alongside the active document");
                RequireContains(groupJournal, "public bool ReplaceNpcIdentity(int oldNpcId, int newNpcId)",
                    "pending group exchange transactions support durable identity migration");
                RequireContains(nativeInteraction,
                    "npcId = CharacterProxyIdentityService.ResolveKnown(taiwuId, displayedNpcId);",
                    "native interaction records follow the established copy identity");
                RequireContains(backend,
                    "&& !TryGetCharacterProxySource(cid, out _)",
                    "an explicitly established fixed-animal copy may enter monthly/story candidates");
                RequireContains(backend,
                    "private const string CharacterProxyEverJoinedPrefix = \"character_proxy_ever_joined_v1_\";",
                    "fixed-template copies persist whether they have ever joined Taiwu");
                RequireContains(backend,
                    "if (IsCharacterProxyAwaitingFirstJoin(charId)) return true;",
                    "a new fixed-template copy without a reliable address remains face-to-face before first join");
                RequireContains(backend,
                    "return proxyId > 0 && IsCharacterProxyAwaitingFirstJoin(proxyId);",
                    "the pre-join face-to-face state also reaches the final physical-action gate");
                RequireContains(backend,
                    "&& SameValidLocation(ac, tc);",
                    "monthly preflight shares the same pre-join physical-scene authority");
                RequireContains(joinTeamMutation,
                    "MarkCharacterProxyEverJoined(context, npcId",
                    "successful join records the permanent contact-mode transition");
                RequireBefore(leaveMutation,
                    "MarkCharacterProxyEverJoined(context, npcId",
                    "DomainManager.Taiwu.LeaveGroup(context, npcId)",
                    "upgraded copies record prior team membership before leaving");
                RequireContains(chatWindow, "const string NpcDialogueColorTag = \"#F5F2EAFF\";",
                    "NPC spoken dialogue uses a full-opacity paper-white accent");
                RequireContains(chatWindow, "const string NpcNarrationColorTag = \"#D8C58CD2\";",
                    "NPC narration uses a quieter translucent warm-gold accent");
                RequireContains(chatWindow,
                    "output.Append(\"</color><color=\").Append(NpcDialogueColorTag).Append('>');",
                    "NPC spoken dialogue changes color without changing font weight");
                Require(chatWindow.IndexOf("if (_assistantMode || text.Length == 0) return text;", StringComparison.Ordinal) < 0,
                    "assistant replies share the normal NPC narration and dialogue color formatter");
                Require(chatWindow.IndexOf("<b><color=", StringComparison.Ordinal) < 0,
                    "NPC spoken dialogue does not use bold rich-text tags");

                RequireContains(characterAuthority,
                    "public void TransferInventoryItem(DataContext context, Character srcChar, Character destChar, ItemKey itemKey, int amount, EItemAutoOperationSource source = EItemAutoOperationSource.Other, bool ignoreLocked = false)",
                    "1.0.72 inventory transfer signature");
                RequireBefore(characterAuthority,
                    "DomainManager.Extra.RecordTaiwuGiftItem(context, id2, itemKey, amount);",
                    "srcChar.RemoveInventoryItem(context, itemKey, amount, deleteItem: false);",
                    "1.0.72 records Taiwu gift metadata before moving inventory");
                RequireBefore(characterAuthority,
                    "srcChar.RemoveInventoryItem(context, itemKey, amount, deleteItem: false);",
                    "destChar.AddInventoryItem(context, itemKey, amount, offLine: false, source);",
                    "authoritative transfer removes the old owner before assigning the new owner");
                RequireContains(extraAuthority,
                    "public void SetTaiwuGiftItemAmount(DataContext context, int targetCharId, ItemKey itemKey, int amount)",
                    "1.0.72 exposes an authoritative single-entry Taiwu gift-ledger restoration API");
                RequireContains(characterObjectAuthority,
                    "source > EItemAutoOperationSource.Invalid",
                    "Invalid source disables Taiwu automatic inventory redirection");
                string inventoryTransfer = Slice(backend,
                    "private static void TransferInventoryChecked(",
                    "private static int InventoryCount(");
                foreach (string nativeAction in new[]
                {
                    giveItemAuthority, stealItemAuthority, requestItemAuthority, robItemAuthority, scamItemAuthority
                })
                {
                    RequireContains(nativeAction, "ignoreLocked: true",
                        "1.0.72 intentional item actions bypass the Taiwu inventory lock");
                }
                RequireContains(inventoryTransfer,
                    "DomainManager.Character.TransferInventoryItem(context, src, dst, key, amount,",
                    "inventory transfer uses the 1.0.72 authoritative owner-ordering and lock semantics");
                RequireBefore(inventoryTransfer,
                    "if (!InventoryOwnerMatches(key, src.GetId()))",
                    "DomainManager.Character.TransferInventoryItem(context, src, dst, key, amount,",
                    "non-stackable item ownership is verified before native transfer");
                RequireContains(inventoryTransfer, "EItemAutoOperationSource.Invalid, ignoreLocked: true);",
                    "inventory transfer disables Taiwu automatic inventory redirection");
                Require(!inventoryTransfer.Contains("DomainManager.Taiwu.IsItemLocked(key)", StringComparison.Ordinal),
                    "intentional item actions must not reintroduce the obsolete Taiwu lock precondition");
                RequireContains(inventoryTransfer,
                    "if (srcAfter == srcBefore - amount && dstAfter == dstBefore + amount)",
                    "inventory transfer trusts an exact completed post-state after a native tail exception");
                RequireContains(inventoryTransfer,
                    "if (TryEnsureInventoryOwner(key, dst.GetId()))",
                    "a completed native tail exception is accepted only after destination ownership is verified or repaired");
                RequireBefore(inventoryTransfer,
                    "TryRestoreInventoryPair(context, src, dst, key, srcBefore,",
                    "物品数量已转移但唯一物品持有人及库存补偿未能确认",
                    "a completed-count owner failure attempts exact inventory compensation before becoming unknown");
                RequireBefore(inventoryTransfer,
                    "物品已恢复，但失败赠礼的太吾赠礼登记未能恢复",
                    "物品持有人后验失败，已恢复双方原库存",
                    "tail-exception owner compensation is retryable only after gift metadata also rolls back");
                RequireContains(inventoryTransfer,
                    "TryRestoreInventoryPair(context, src, dst, key, srcBefore, dstBefore,",
                    "partial inventory writes enter verified compensation");
                RequireContains(inventoryTransfer, "restoreSourceInventoryOwner: true",
                    "partial inventory compensation also restores unique-item ownership");
                RequireContains(inventoryTransfer,
                    "bool keepGiftLedger = taiwuSource && intent == TransferIntent.Gift",
                    "only a successfully landed real gift may preserve native Taiwu gift metadata");
                RequireContains(inventoryTransfer,
                    "TryRestoreTaiwuGiftItemAmount(context, dst.GetId(), key,",
                    "barter, theft, trade, loot, compensation and failed gifts restore Taiwu gift metadata");
                RequireBefore(inventoryTransfer,
                    "!TryGetTaiwuGiftItemAmount(dst.GetId(), key, out giftAmountBefore)",
                    "DomainManager.Character.TransferInventoryItem(context, src, dst, key, amount,",
                    "a failed gift-ledger snapshot aborts before native transfer instead of erasing unknown prior metadata");
                RequireBefore(inventoryTransfer,
                    "TryRestoreTaiwuGiftItemAmount(context, dst.GetId(), key,",
                    "if (transferError != null)",
                    "gift metadata is restored before classifying a native transfer exception");
                RequireContains(inventoryTransfer,
                    "if (taiwuSource && intent == TransferIntent.Gift",
                    "a failed unique-owner postcondition also rolls back a real gift's native metadata");
                RequireBefore(inventoryTransfer,
                    "物品已恢复，但失败赠礼的太吾赠礼登记未能恢复",
                    "物品转移未完整落地，已恢复双方原库存",
                    "a compensated owner failure is retryable only after gift metadata is restored");
                RequireContains(inventoryTransfer,
                    "throw new InvalidOperationException(\"物品转移部分落地且补偿恢复失败\", transferError);",
                    "unrecovered partial inventory writes remain indeterminate and stop the caller");
                RequireBefore(characterAuthority,
                    "srcChar.ChangeResource(context, resourceType, -amount);",
                    "destChar.ChangeResource(context, resourceType, amount);",
                    "1.0.72 resource transfer is a two-character non-transactional write");
                RequireBefore(characterObjectAuthority,
                    "_resources.Items[resourceType] = num;",
                    "ResourceInvokeGuidingTrigger(context, resourceType);",
                    "Taiwu resource guiding trigger runs after the authoritative balance write");
                string resourceTransfer = Slice(backend,
                    "private static void TransferResourceChecked(",
                    "private static void TransferInventoryChecked(");
                RequireContains(resourceTransfer,
                    "if (srcAfter == srcBefore - amount && dstAfter == dstBefore + amount) return;",
                    "resource transfer trusts exact post-state even when a native tail trigger throws");
                RequireContains(resourceTransfer,
                    "dst.SpecifyResource(context, resourceType, dstBefore);",
                    "partial resource transfer compensates destination without retriggering guiding events");
                RequireContains(resourceTransfer,
                    "src.SpecifyResource(context, resourceType, srcBefore);",
                    "partial resource transfer compensates source");
                RequireContains(resourceTransfer,
                    "if (dstBefore > NativeResourceCap - amount)",
                    "resource transfer rejects native destination cap truncation before writing");
                RequireContains(resourceTransfer,
                    "throw new TransferPreconditionException(\"资源转移未完整落地，已恢复双方原余额\");",
                    "verified resource compensation becomes a known zero-write failure");

                string eatingTransfer = Slice(backend,
                    "private static void TransferEatingItemToInventory(",
                    "private static void RestoreEatingOrigin(");
                RequireBefore(eatingTransfer,
                    "thing.EatingDuration = duration;",
                    "src.SetEatingItems(ref updated, context);",
                    "eating rollback captures the original duration before the first write");
                RequireBefore(eatingTransfer,
                    "src.SetEatingItems(ref updated, context);",
                    "DomainManager.Item.RemoveOwner(thing.Key, ItemOwnerType.CharacterEatingItem, srcId);",
                    "eating transfer clears the source slot before releasing its item owner");
                RequireBefore(eatingTransfer,
                    "DomainManager.Item.RemoveOwner(thing.Key, ItemOwnerType.CharacterEatingItem, srcId);",
                    "dst.AddInventoryItem(context, thing.Key, 1, offLine: false, source: EItemAutoOperationSource.Invalid)",
                    "eating transfer releases the old owner before assigning the destination inventory owner");
                RequireContains(eatingTransfer,
                    "TryRestoreEatingOrigin(context, srcId, src, dst,",
                    "every eating-stage exception enters verified compensation");
                RequireContains(eatingTransfer,
                    "DomainManager.Extra.RecordTaiwuGiftItem(context, dst.GetId(),",
                    "a real Taiwu gift from an eating slot receives the same native gift metadata as an inventory gift");
                RequireBefore(eatingTransfer,
                    "!TryGetTaiwuGiftItemAmount(dst.GetId(), thing.Key,",
                    "updated.Clear(thing.EatingSlot);",
                    "eating gifts snapshot native metadata before their first write");
                RequireContains(eatingTransfer,
                    "TryRestoreTaiwuGiftItemAmount(context, dst.GetId(), thing.Key,",
                    "failed eating gifts compensate their native gift metadata");
                string transferThing = Slice(backend,
                    "private static TransferReceipt ApplyTransferThing(",
                    "private static void TransferResourceChecked(");
                RequireContains(transferThing, "Exception unequipError = null;",
                    "equipment transfer observes native tail exceptions");
                RequireContains(transferThing,
                    "bool slotCleared = equipmentAfter != null",
                    "equipment transfer verifies the authoritative slot after native unequip");
                RequireContains(transferThing,
                    "TryRestoreEquippedTransferOrigin(context, srcId, src, dst,",
                    "equipment transfer compensates every incomplete stage");
                string equippedTransferFailure = Slice(transferThing,
                    "catch (Exception transferError)",
                    "TransferInventoryChecked(context, src, dst, thing.Key, thing.Amount, intent);");
                RequireBefore(equippedTransferFailure,
                    "if (!(transferError is TransferPreconditionException))",
                    "TryRestoreEquippedTransferOrigin(context, srcId, src, dst,",
                    "equipment compensation runs only after inventory proves a zero-write/restored failure");

                RequireContains(characterObjectAuthority,
                    "public int GetItemAlertFactor(ItemKey itemKey, int amount)",
                    "1.0.72 authoritative item-value alert factor");
                RequireContains(characterObjectAuthority,
                    "long value2 = (long)(80 + value / 75) * (long)(50 + 50 * amount) / 100;",
                    "1.0.72 high-value alert calculation is overflow-safe");
                RequireContains(characterObjectAuthority,
                    "public unsafe sbyte GetStealActionPhase(IRandomSource random, Character targetChar, int alertFactor",
                    "1.0.72 native three-phase theft check");
                string steal = Slice(backend, "case \"steal\":", "case \"taiwu_teach\":");
                RequireContains(steal, "victim.GetItemAlertFactor(thing.Key, thing.Amount)",
                    "theft uses authoritative item value and requested amount");
                RequireContains(steal, "victim.GetResourceAlertFactor(thing.ResourceType)",
                    "resource theft uses authoritative resource alert factor");
                RequireContains(steal, "thief.GetStealActionPhase(context.Random, calculationTarget, alertFactor, false)",
                    "theft outcome is decided by the native three-phase check");
                RequireContains(steal, "TransferIntent.Steal",
                    "theft cannot pollute Taiwu's native gift ledger");
                RequireContains(backend, "long product = (long)p1 * p2 * p3;",
                    "combined theft probability cannot overflow on high-value items");
                Require(!steal.Contains("55 + (thiefDex - victimDex) / 8", StringComparison.Ordinal),
                    "legacy dex-only theft probability must not return");

                RequireContains(rpc, "public const string StartCombatMethod = \"JYL_StartCombat\";",
                    "shared StartCombat RPC constant");
                RequireContains(backend, "JHYL_REGISTERED_RPC_METHOD_COUNT_21",
                    "registered RPC inventory marker");
                Require(Count(backend, "AddMutationMethod(RpcConst.StartCombatMethod, StartCombat);") == 1,
                    "StartCombat must be registered exactly once through AddMutationMethod");

                string journal = Slice(backend,
                    "private static SerializableModData ExecuteJournaled(",
                    "private static SerializableModData QueryOperation(");
                RequireBefore(journal, "TryValidateOperationBinding(",
                    "result = handler != null ? handler(context, parameter) : null;",
                    "operation binding must precede StartCombat handler dispatch");

                string combat = Slice(backend,
                    "private static SerializableModData StartCombat(",
                    "public override void Dispose()");
                RequireContains(combat, "combatConfig < 0 || combatConfig > 2",
                    "combat config whitelist");
                RequireContains(combat, "targetId == taiwuId", "self-combat rejection");
                RequireContains(combat, "TryGetElement_Objects(taiwuId, out taiwu)",
                    "live Taiwu lookup");
                RequireContains(combat, "TryGetElement_Objects(targetId, out target)",
                    "live target lookup");
                RequireContains(combat, "target.GetKidnapperId() >= 0", "kidnapped target rejection");
                RequireContains(combat, "DomainManager.Organization.GetPrisonerSect(targetId) >= 0",
                    "imprisoned target rejection");
                RequireContains(combat, "IsAtTaiwuScene(targetId, taiwuId, target, taiwu)",
                    "authoritative same-scene check");
                RequireBefore(combat, "IsAtTaiwuScene(targetId, taiwuId, target, taiwu)",
                    "TryValidateCombatInteractionAnchor(targetId, out finishInteractionAfterDispatch",
                    "scene validation must precede native interaction-anchor validation");
                RequireContains(combat, "DialogueDeathCombatCompleteEventGuid",
                    "deathmatch must name the native no-guard completion event");
                RequireContains(combat, "DomainManager.TaiwuEvent.GetEvent(DialogueDeathCombatCompleteEventGuid)",
                    "deathmatch completion event availability preflight");
                string deathDispatch = Slice(combat, "// Match b24185552's native no-guard deathmatch lifecycle.",
                    "catch (Exception e)");
                RequireContains(deathDispatch,
                    "new global::GameData.Domains.TaiwuEvent.EventArgBox()",
                    "deathmatch owns a clean native event argument box");
                RequireBefore(deathDispatch, "combatArgs.Set(\"CharacterId\", targetId);",
                    "EventHelper.EventHelper.StartCombat(",
                    "deathmatch target must be bound before native event combat starts");
                RequireBefore(deathDispatch, "EventHelper.EventHelper.StartCombat(",
                    "DomainManager.TaiwuEvent.ToEvent(string.Empty);",
                    "CombatOver listener must be registered before the old interaction exits");
                RequireContains(deathDispatch,
                    "targetId, (short)2, DialogueDeathCombatCompleteEventGuid, combatArgs, true);",
                    "deathmatch must use the native one-on-one completion lifecycle");
                Require(Count(combat,
                    "DomainManager.Combat.GmCmd_FightCharacter(context, targetId, (short)combatConfig);") == 1,
                    "raw GM combat entry is retained only for non-lethal play/beat modes");
                RequireContains(combat, "Indeterminate(\"start_combat_indeterminate\"",
                    "post-dispatch ambiguity must be non-retryable unknown");

                RequireContains(startupRecoveryPolicy, "public const int MaxAttempts = 3;",
                    "single-chat startup recovery has three-attempt bound");
                RequireContains(startupRecoveryPolicy, "completedAttempts <= 1 ? 0.5f : 1.5f",
                    "single-chat startup recovery has bounded 0.5/1.5 second backoff");
                string conversationRecovery = Slice(talk,
                    "private static IEnumerator RecoverWorldPendingConversationsCoroutine(",
                    "private static bool TryCreateConversationRecoveryEnumerator(");
                RequireContains(conversationRecovery, "StartupRecoveryPolicy.ShouldRetry(retryPending, completedAttempts)",
                    "single-chat startup recovery retries transient scan/read failures");
                RequireContains(conversationRecovery, "finally { ResetScheduledConversationRecovery(worldId, generation); }",
                    "single-chat startup recovery releases scheduling gate on every exit");

                string effectStart = Slice(effects,
                    "public static void StartCombat(",
                    "/// <summary>令 NPC 认可太吾");
                RequireContains(effectStart, "config < 0 || config > 2", "frontend config whitelist");
                RequireContains(effectStart, "CallBoolRpc(RpcConst.StartCombatMethod",
                    "EffectHandler journaled combat dispatch");
                RequireContains(effectStart, "onDone, stableOperationId", "stable operation id propagation");
                RequireContains(operationRpc,
                    "string.Equals(method, RpcConst.StartCombatMethod, StringComparison.Ordinal)",
                    "structured receipt operation-kind mapping");
                string prepareEnvelope = Slice(operationRpc,
                    "public static bool RegisterGroupPhysicalEnvelope(",
                    "public static bool RegisterOperationIdentityExpectation(");
                RequireContains(prepareEnvelope, "DiscardPreparedOperation(operationId);",
                    "invalid physical-envelope preparation cleans all transient state");
                RequireContains(prepareEnvelope, "GroupPhysicalEnvelopes.Remove(operationId);",
                    "re-preparation cannot inherit a stale physical envelope");
                string prepareIdentity = Slice(operationRpc,
                    "public static bool RegisterOperationIdentityExpectation(",
                    "public static void DiscardPreparedOperation(");
                RequireContains(prepareIdentity, "DiscardPreparedOperation(operationId);",
                    "invalid identity preparation cleans all transient state");
                RequireBefore(prepareIdentity, "GroupPhysicalEnvelopes.Remove(operationId);",
                    "OperationIdentityExpectations[operationId] =",
                    "identity re-preparation clears an earlier physical envelope");
                string operationCall = Slice(operationRpc,
                    "public static bool Call(",
                    "public static bool AcknowledgeExistingReceipt(string operationId");
                int operationCallDiscards = Count(operationCall, "DiscardPreparedOperation(operationId);");
                Require(operationCallDiscards == 6,
                    "OperationRpcClient.Call pre-dispatch rejection contract drift: expected exactly 6 "
                    + "DiscardPreparedOperation(operationId) releases, found " + operationCallDiscards
                    + "; every new pre-dispatch rejection branch must call DiscardPreparedOperation(operationId) "
                    + "before returning, and this exact count must be updated in the same change");
                int operationCallRejections = Count(operationCall, "return false;");
                Require(operationCallRejections == 7,
                    "OperationRpcClient.Call rejection-branch contract drift: expected exactly 7 "
                    + "`return false;` sites (6 with DiscardPreparedOperation + coroutine_host_missing, whose "
                    + "prepared state is already consumed before that point), found " + operationCallRejections
                    + "; adding any early rejection must pair it with DiscardPreparedOperation(operationId) "
                    + "(or document the consumed-state exemption) and update both counts in the same change");

                string merchantGoods = Slice(backend,
                    "case \"merchant_goods\":", "case \"taiwu_money\":");
                RequireContains(merchantGoods,
                    "ResolveMerchantStoreForQuery(context, npcId, out ot, out oid)",
                    "query_merchant_goods must use native shop-open initialization and preserve its source");
                string merchantResolver = Slice(backend,
                    "private static GameData.Domains.Merchant.MerchantData ResolveExistingMerchantStore(",
                    "private static void CollectMerchantGoods(");
                RequireContains(merchantResolver, "DomainManager.Merchant.TryGetMerchantData(npcId, out md)",
                    "existing merchant lookup authority accessor");
                RequireContains(merchantResolver, "TryGetSameBlockCaravan(npcId",
                    "merchant query must prefer the same-block same-type caravan shelf");
                RequireContains(merchantResolver, "DomainManager.Merchant.GetCaravanMerchantData(context, caravanId)",
                    "merchant query must refresh the matched native caravan shelf");
                RequireContains(merchantResolver, "DomainManager.Merchant.GetMerchantData(context, npcId)",
                    "merchant query must initialize an unopened normal merchant shelf");
                Require(merchantResolver.IndexOf("DomainManager.Merchant.GetMerchantInfoCaravanDataList(", StringComparison.Ordinal) < 0,
                    "merchant source matching must not create unrelated caravan display metadata");
                string trade = Slice(backend, "case \"trade\":", "case \"merchant_goods\":");
                RequireContains(trade, "ResolveExistingMerchantStore(npcId, out ot, out oid)",
                    "trade must consume only a previously existing readable store");
                Require(trade.IndexOf("DomainManager.Merchant.GetMerchantData(", StringComparison.Ordinal) < 0
                    && trade.IndexOf("DomainManager.Merchant.GetCaravanMerchantData(", StringComparison.Ordinal) < 0,
                    "failed trade must not generate or refresh hidden stock");

                string merchantFavor = Slice(backend,
                    "case \"merchantfavor\":", "case \"captive_state\":");
                RequireContains(merchantFavor, "int target = Math.Max(0, Math.Min(100, before + delta));",
                    "merchant-favor point target clamp");
                RequireContains(merchantFavor, "GlobalConfig.Instance.MerchantFavorabilityMoneyRequirements",
                    "merchant-favor exact segment-consumption inverse mapping");
                Require(merchantFavor.IndexOf("DomainManager.Merchant.GetCumulativeMoney(target",
                    StringComparison.Ordinal) < 0,
                    "intermediate favor targets must not use the non-invertible prefix-sum GetCumulativeMoney interpolation");
                RequireContains(merchantFavor, "DomainManager.Merchant.GetCumulativeMoney(100)",
                    "full-favor achievement threshold authority");
                RequireContains(merchantFavor, "AchievementManager.RequestSetStat",
                    "full-favor achievement hook parity");
                RequireContains(merchantFavor, "int[] updated = (int[])current.Clone();",
                    "merchant-favor domain array copy-before-write");
                RequireContains(merchantFavor, "DomainManager.Merchant.SetMerchantFavorability(updated, context);",
                    "merchant-favor authoritative persisted setter");
                RequireContains(merchantFavor, "if (after != target)",
                    "merchant-favor exact postcondition");
                Require(merchantFavor.IndexOf("ChangeMerchantCumulativeMoney(context, mtype, delta)",
                    StringComparison.Ordinal) < 0,
                    "UI favor points must never be passed directly as cumulative-money delta");
                RequireContains(merchantAuthority,
                    "public int GetCumulativeMoney(int favorability)",
                    "b24185552 merchant favor-to-cumulative mapping signature");
                RequireContains(merchantAuthority,
                    "public void SetMerchantFavorability(int[] value, DataContext context)",
                    "b24185552 persisted merchant favor setter signature");

                RequireContains(characterObjectAuthority, "if (skillTemplateId >= 0)",
                    "b24185552 skill-template zero-valid authority sentinel");
                string teachLife = Slice(backend, "case \"teachlife\":", "case \"writebook\":");
                RequireContains(teachLife, "learnerId <= 0 || tpl < 0",
                    "life-skill teaching must allow zero-based template id");
                string taiwuTeach = Slice(backend, "case \"taiwu_teach\":", "case \"flip_practice\":");
                RequireContains(taiwuTeach, "if (tpl < 0)",
                    "Taiwu teaching must allow zero-based template id");
                string teachCombat = Slice(backend,
                    "private static SerializableModData TeachSkill(",
                    "private static SerializableModData ExecuteRelation(");
                RequireContains(teachCombat, "learnerId <= 0 || tpl < 0",
                    "combat-skill teaching must allow zero-based template id");
                int effectsTemplateZeroGuards = Count(effects, "taiwuId <= 0 || templateId < 0");
                Require(effectsTemplateZeroGuards == 3,
                    "frontend NPC-to-target skill dispatch template-zero guard drift: expected exactly 3 "
                    + "'taiwuId <= 0 || templateId < 0' guards (ApplyTeachSkillId/ApplyTeachLifeSkill/ApplyWriteBook), "
                    + "found " + effectsTemplateZeroGuards + "; a new NPC skill dispatch must keep 'templateId < 0' "
                    + "(template id zero stays valid) and this exact count must be updated in the same change");
                RequireContains(effects, "taiwuId <= 0 || npcId <= 0 || templateId < 0",
                    "frontend Taiwu-to-NPC skill dispatch must allow template zero");
                RequireContains(teachLife, "tpl = -1", "missing life-skill template sentinel");
                RequireContains(taiwuTeach, "tpl = -1", "missing Taiwu-teach template sentinel");

                string addGoal = Slice(backend,
                    "private static SerializableModData AddGoal(",
                    "private static SerializableModData FilterDead(");
                foreach (string goalTemplate in new[] { "case 254:", "case 262:", "case 263:", "case 271:", "case 274:" })
                    RequireContains(addGoal, goalTemplate, "explicit Mod goal whitelist " + goalTemplate);
                RequireContains(addGoal, "default: return Fail(\"unsupported_goal\"",
                    "unknown game goal templates must be rejected");
                RequireContains(addGoal, "return AddNpcTravelTarget(context, p, npcId, travelMode);",
                    "generic travel must branch before CharacterGoal template validation");
                RequireContains(addGoal, "new NpcTravelTarget(location, duration)",
                    "fixed-place travel uses the authoritative bounded travel target");
                RequireContains(addGoal, "new NpcTravelTarget(targetCharId, duration)",
                    "person-seeking travel uses the authoritative dynamic character target");
                RequireContains(addGoal,
                    "if (p.Get(\"max_duration_months\", out requestedDuration)) duration = requestedDuration;",
                    "missing duration retains the bounded backend default");
                RequireContains(addGoal, "npc.GetCreatingType() != 1",
                    "travel rejects character types that the monthly movement loop never processes");
                RequireContains(addGoal, "CharacterDomain.IsLockMovementChar(npcId)",
                    "travel rejects story-locked movement before reporting success");
                RequireContains(addGoal, "updated.Insert(0, requested);",
                    "new Mod travel takes priority without clearing unrelated native targets");
                RequireContains(addGoal, "CharacterGoalData supersededGoal = npc.GetGoal(254);",
                    "a new generic trip snapshots goal 254 even when its registry half is absent");
                RequireContains(addGoal, "bool hadSupersededAppointmentRegistration =",
                    "a new generic trip snapshots the appointment registry independently");
                RequireContains(addGoal, "supersededGoal.ContextArgs[1]",
                    "goal rollback destination comes from the authoritative goal arguments");
                RequireBefore(addGoal, "if (supersededAppointmentGoal) npc.RemoveGoal(context, 254);",
                    "npc.SetNpcTravelTargets(updated, context);",
                    "an appointment goal orphan is removed before the replacement trip is committed");
                RequireBefore(addGoal, "if (hadSupersededAppointmentRegistration)",
                    "npc.SetNpcTravelTargets(updated, context);",
                    "an appointment registry orphan is removed before the replacement trip is committed");
                RequireContains(addGoal, "先前约定已由本次新行程取代",
                    "travel receipt discloses replacement of a prior appointment");
                RequireContains(addGoal,
                    "npc.SetNpcTravelTargets(new List<NpcTravelTarget>(priorTravelTargets), context);",
                    "failed trip replacement restores the exact previous travel-target list");
                RequireContains(addGoal, "RestoreAppointmentComponents(context, npc, npcId,",
                    "failed trip replacement restores goal and registry snapshots independently");
                RequireContains(addGoal,
                    "goals.Insert(Math.Max(0, Math.Min(goalIndex, goals.Count)), goalSnapshot);",
                    "goal rollback restores the exact CharacterGoalData at its original position");
                RequireContains(addGoal, "goalSnapshot.CurrentAction = goalCurrentAction;",
                    "goal rollback reverses native RemoveGoal action interruption");
                RequireContains(addGoal,
                    "npc.ActionPlanningData.InterruptedActions = interruptedActionsWasNull",
                    "goal rollback restores the exact interrupted-action collection state");
                RequireContains(addGoal, "npc.SetActionPlanningModified(context);",
                    "exact goal-object rollback is persisted through the native modification path");
                RequireContains(addGoal, "done.Set(\"action_state\", \"scheduled\");",
                    "travel receipt reports scheduled rather than completed state");
                RequireContains(backend, "case \"travel_state\"",
                    "backend exposes current trip and appointment state as a read-only query");
                RequireContains(backend,
                    "c.GetGoal(254, (PlanningContextArg)taiwuId,",
                    "travel state rejects orphaned or mismatched appointment halves");
                RequireContains(backend, "if (hasStaleAppointmentGoal)",
                    "a stale goal is reported before lower-priority ordinary travel targets");
                RequireContains(backend, "stateResult.Set(\"kind\", \"stale_appointment\");",
                    "an orphan or mismatched goal is never presented as a valid appointment");
                RequireContains(backend, "foreach (NpcTravelTarget candidate in travelTargets)",
                    "travel state scans targets in the same order as native monthly movement");
                RequireContains(backend, "if (!hasExecutableTravelTarget)",
                    "travel state reports no trip when every native target resolves invalid");
                RequireContains(backend,
                    "ResolveExecutableTravelTargetLocation(",
                    "travel state applies the native executable-destination resolver");
                RequireContains(backend,
                    "ProfessionSkillHandle.IsLocationForbiddenByBeggarSkill(candidate)",
                    "travel state rejects forbidden fallback blocks");
                RequireContains(backend, "center.GetManhattanDistance(aCoordinate)",
                    "beggar-skill fallback is ordered by native Manhattan distance");
                RequireContains(backend, "旧行程不得继续当作仍在执行",
                    "no-active-trip state explicitly invalidates stale conversation promises");
                RequireContains(addGoal, "appointment_requires_fixed_location",
                    "appointment goal requires an explicit fixed location");
                Require(Count(addGoal, "dest_char_id") == 0,
                    "appointment path must never snapshot another person's location");
                RequireBefore(addGoal, "TryGetElement_Appointments(npcId, out priorAppointment)",
                    "DomainManager.Taiwu.RemoveAppointment(context, npcId);",
                    "old appointment registry must be snapshotted before replacement removes it");
                RequireContains(addGoal,
                    "npc.AddGoal(context, 254, (PlanningContextArg)arg0, (PlanningContextArg)priorAppointment);",
                    "failed appointment replacement restores the old goal as well as its registry");
                RequireContains(addGoal, "TryGetElement_Objects(arg0, out target)",
                    "goal target authoritative live-object lookup");
                RequireContains(addGoal, "targetFavor < 10000",
                    "appointment/protect/rescue authoritative friendship gate");
                RequireContains(addGoal, "target.GetKidnapperId() == npcId",
                    "rescue-self-kidnapper rejection");
                RequireContains(addGoal, "DomainManager.World.GetWorldFunctionsStatus(4)",
                    "HuntTaiwu world-function validity gate");
                RequireContains(addGoal, "GetRelatedCharIds(npcId, 32768)",
                    "revenge target authoritative enmity relation gate");
                RequireContains(addGoal,
                    "npc.GetGoal(templateId, (PlanningContextArg)arg0, (PlanningContextArg)goalLocation)",
                    "appointment exact target/location postcondition");
                RequireContains(addGoal, "npc.GetGoal(templateId, (PlanningContextArg)arg0)",
                    "character-goal exact target postcondition");
                RequireContains(appointmentAuthority, "FavorabilityType.GetFavorabilityType", "appointment favor authority");
                RequireContains(protectAuthority, "FavorabilityType.GetFavorabilityType", "protect favor authority");
                RequireContains(rescueAuthority, "kidnapperId == selfChar.GetId()", "rescue kidnapper authority");
                RequireContains(revengeAuthority, "IsCharacterAlive(actionData.TargetCharId)", "revenge live-target authority");
                RequireContains(huntTaiwuAuthority, "GetWorldFunctionsStatus(4)", "HuntTaiwu world gate authority");
                RequireContains(travelTargetAuthority, "public NpcTravelTarget(Location targetLocation, int maxDuration)",
                    "b24185552 fixed travel target constructor authority");
                RequireContains(travelTargetAuthority, "public NpcTravelTarget(int targetCharId, int maxDuration)",
                    "b24185552 dynamic travel target constructor authority");
                RequireContains(travelTargetAuthority, "public int RemainingMonth;",
                    "b24185552 bounded travel lifetime authority");
                RequireContains(characterObjectAuthority, "private bool TravelToTargets(DataContext context)",
                    "b24185552 monthly travel execution authority");
                RequireContains(characterObjectAuthority,
                    "realTargetLocation = GetValidAndNotForbiddenByBeggarSkillLocation(realTargetLocation);",
                    "b24185552 resolves beggar-skill forbidden destinations before validity checks");
                RequireBefore(characterObjectAuthority,
                    "realTargetLocation = GetValidAndNotForbiddenByBeggarSkillLocation(realTargetLocation);",
                    "if (!realTargetLocation.IsValid())",
                    "b24185552 skips invalid travel targets only after forbidden-block resolution");
                RequireContains(characterObjectAuthority, "_npcTravelTargets.RemoveAt(i);",
                    "b24185552 travel target is removed on arrival");
                RequireContains(characterObjectAuthority, "public void UpdateTravelTargetRemainingMonths(DataContext context)",
                    "b24185552 travel targets expire by month");

                string genericGoto = Slice(effects, "public static void ApplyGotoPlace(",
                    "public static void ApplyAppointmentWithTaiwu(");
                RequireContains(genericGoto, "p.Set(\"travel_mode\", \"fixed\");",
                    "generic fixed travel frontend dispatch");
                RequireContains(genericGoto, "p.Set(\"travel_mode\", \"char\");",
                    "generic dynamic travel frontend dispatch");
                Require(Count(genericGoto, "template_id\", 254") == 0,
                    "generic travel must not manufacture appointment goal 254");
                RequireContains(effects, "public static void ApplyAppointmentWithTaiwu(",
                    "explicit fixed Taiwu appointment has a separate frontend path");
                RequireContains(effects, "public static void QueryTravelState(",
                    "frontend can read authoritative current trip state");
                RequireBefore(talk, "yield return NpcSnapshotReader.Fetch(npcId, s => snap = s);",
                    "EffectHandler.QueryTravelState(npcId,",
                    "death is resolved before any trip-state RPC");
                RequireContains(talk, "if (snap.IsDead)",
                    "dead NPC conversation has a dedicated soul branch");
                string deathAndTravelBranch = Slice(talk, "if (snap.IsDead)",
                    "if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(_turnWorldGeneration))");
                Match liveBranchStart = Regex.Match(deathAndTravelBranch, @"\belse\s*\{");
                Require(liveBranchStart.Success,
                    "live NPC branch after the dedicated soul branch missing");
                string deadNpcBranch = deathAndTravelBranch.Substring(0, liveBranchStart.Index);
                string liveNpcBranch = deathAndTravelBranch.Substring(liveBranchStart.Index);
                Require(Count(deadNpcBranch, "EffectHandler.QueryTravelState(npcId,") == 0,
                    "dead NPC branch must not dispatch a trip-state RPC");
                RequireContains(liveNpcBranch, "if (snap.PhysiologicalAge < 3)",
                    "live NPC branch keeps the infant conversation gate");
                RequireContains(liveNpcBranch, "EffectHandler.QueryTravelState(npcId,",
                    "trip-state RPC remains confined to the live NPC branch");
                RequireContains(talk, "不能仅凭旧对话断言某趟行程仍在执行",
                    "trip-state timeout fails closed against stale promises");
                RequireContains(talk, "if (purpose == \"赴约\")",
                    "chat distinguishes explicit appointment semantics");
                RequireContains(talk, "赴约须说定一个固定地点",
                    "chat rejects moving Taiwu location as a fixed appointment");
                RequireContains(monthlyEvent, "Anm(\"a\") + \"因\" + Anm(\"b\") + \"引发此行",
                    "monthly event travel keeps person b in the player-facing causal result");
                RequireContains(monthlyEvent, "if (r[0] == 1) Project(rn, 0);",
                    "monthly event travel stores targetless movement projection evidence");
                Require(Count(monthlyEvent, "if (r[0] == 1) Project(rn);") == 0,
                    "monthly event travel must not bind person b as movement target");

                string recognize = Slice(backend,
                    "private static SerializableModData Recognize(",
                    "private static SerializableModData ChangeMorality(");
                RequireContains(recognize, "TryGetElement_SectCharacters(npcId, out sectCharacter)",
                    "recognize exact writable sect entity precondition");
                RequireContains(recognize, "sectCharacter.GetApprovedTaiwu()",
                    "recognize idempotent precheck");
                RequireContains(recognize, "verified.GetApprovedTaiwu()",
                    "recognize authoritative postcondition");

                string release = Slice(backend,
                    "private static SerializableModData Release(",
                    "private static SerializableModData ResolveChar(");
                RequireContains(release, "target.GetKidnapperId() != npcId",
                    "release exact kidnapper precondition");
                RequireBefore(release, "target.GetKidnapperId() != npcId",
                    "DomainManager.Character.RemoveKidnappedCharacter(context, targetId, npcId, false);",
                    "release relationship check before write");
                RequireContains(release, "target.GetKidnapperId() >= 0",
                    "release authoritative postcondition");

                string resolveChar = Slice(backend,
                    "private static SerializableModData ResolveChar(",
                    "private static void SetHostilityEvidence(");
                Require(Count(backend, "TryExtractExplicitCharacterId(text, out explicitId)") >= 3,
                    "all relation/block/global person resolver paths accept stable #character identifiers");
                RequireContains(backend, "if (found > 0 && found != parsed)",
                    "explicit-id parsing rejects multiple different character ids as ambiguous");
                RequireContains(backend, "charId = -2;",
                    "explicit-id ambiguity is preserved instead of falling back to a guessed name");
                RequireContains(backend, "if (!ids.Contains(explicitId)) return -1;",
                    "stable identifiers remain scoped to the authorized candidate set");
                RequireContains(backend, "if (!hasExplicitId && scanned >= CAP) break;",
                    "exact same-block character identifiers are not truncated by the fuzzy-name scan cap");
                string globalResolver = Slice(backend,
                    "private static int ResolveGlobalByName(",
                    "private static int ResolveInRelations(");
                RequireBefore(globalResolver,
                    "DomainManager.Character.TryGetElement_Objects(explicitId, out exact)",
                    "DomainManager.Character.GmCmd_GetAllCharacterName()",
                    "global explicit character identifiers use direct authoritative lookup before name enumeration");
                RequireContains(backend,
                    "FilterResolvableCharacterIds(b.set.GetCollection(), speakerId, taiwuId)",
                    "relationship buckets discard stale, infant, animal and non-interactable ids before resolution");
                RequireContains(backend,
                    "GetRelatedCharacters(RelationOwnerId(speakerId))",
                    "mapped characters resolve mentees through the native relation owner");
                string matchByName = Slice(backend,
                    "private static int MatchByName(",
                    "private static bool TryExtractExplicitCharacterId(");
                RequireContains(matchByName, "var literalIds = new HashSet<int>();",
                    "literal longest-name matching tracks distinct character ids");
                RequireContains(matchByName, "if (literalIds.Count > 1) return -2;",
                    "literal same-name characters fail closed as ambiguous");
                RequireContains(matchByName, "var exactIds = new HashSet<int>();",
                    "normalized exact-name matching tracks distinct character ids");
                RequireContains(matchByName, "if (exactIds.Count > 1) return -2;",
                    "normalized same-name characters require an explicit stable id");
                string relationBucket = Slice(backend,
                    "private static void RelBucket(",
                    "private static int RelationOwnerId(");
                RequireContains(backend,
                    "private static string ListedCharacterName(int charId)",
                    "query rosters have a common stable character reference formatter");
                Require(Count(relationBucket, "ListedCharacterName(id)") >= 2,
                    "relationship and adored rosters expose stable ids for every listed character");
                RequireContains(backend, "name + \"(#\" + charId + \")\"",
                    "same-name relationship characters remain distinguishable by explicit ids");
                RequireContains(backend,
                    "var teamSet = FilterResolvableCharacterIds(team, speakerId, taiwuId);",
                    "team-name resolution uses the same live intelligent-character filter");
                RequireContains(backend, "int taiwuId = 0, allowHostile = 1;",
                    "same-block roster defaults to complete presence including enemies");
                RequireContains(effects, "d.Set(\"allow_hostile\", includeHostile ? 1 : 0);",
                    "frontend explicitly preserves both complete and friendly-only roster modes");

                string eventGift = Slice(backend, "case \"event_gift\":", "case \"char_names\":");
                RequireBefore(eventGift, "catch (TransferPreconditionException ge)", "catch (Exception ge)",
                    "known zero-write monthly gift preconditions before unknown-state catch");
                RequireContains(eventGift, "ItemTemplateHelper.IsTransferable(k.ItemType, k.TemplateId)",
                    "monthly automatic gifts skip native non-transferable inventory entries");
                RequireContains(eventGift, "ItemTemplateHelper.IsSpecial(k.ItemType, k.TemplateId)",
                    "monthly automatic gifts skip special and quest-like inventory entries");

                string monthlyPoison = Slice(backend, "case \"poison\":", "case \"heal\":");
                RequireContains(monthlyPoison, "IsActorRestrained(actor, actorId)",
                    "poison rejects a restrained actor independently of the optional physical envelope");
                RequireContains(monthlyPoison, "p.Get(\"monthly_only_purity\", out monthlyOnlyPurity);",
                    "monthly poison receives the explicit purity-only execution mode");
                RequireContains(monthlyPoison, "if (actorLv < targetLv)",
                    "monthly poison rejects only lower consummate strength");
                RequireBefore(monthlyPoison, "if (monthlyOnlyPurity == 1)",
                    "ItemKey poisonKey = default(ItemKey)",
                    "monthly poison bypasses normal inventory poison lookup");
                RequireContains(monthlyPoison, "ref PoisonInts poisoned = ref target.GetPoisoned();",
                    "monthly poison uses the authoritative direct poison state write");
                RequireContains(monthlyPoison, "MonthlyPoisonPolicy.TryGetNextValue(poisonBefore, out int requestedPoisonAfter)",
                    "monthly poison rejects an already saturated poison instead of reporting a no-op success");
                RequireContains(monthlyPoison, "ref PoisonInts confirmedPoisoned = ref target.GetPoisoned();",
                    "monthly poison re-reads authoritative state after the mutation");
                RequireContains(monthlyPoison, "if (poisonAfter <= poisonBefore)",
                    "monthly poison never reports success unless the selected poison actually increased");
                RequireContains(monthlyPoison, "ParsePoisonType(requestedPoisonType)",
                    "monthly poison accepts the model-selected poison type");
                RequireContains(monthlyPoison, "monthlyDone.Set(\"poison_name\", monthlyPoisonName);",
                    "monthly poison receipt exposes the exact applied poison type");
                RequireContains(monthlyPoison, "% 6",
                    "monthly poison has a deterministic six-type fallback");
                RequireContains(effects, "d.Set(\"poison_type\", poisonType.Trim());",
                    "frontend forwards the selected poison type to the backend");
                RequireContains(monthlyEvent,
                    "ToolDef.Sel(\"所下毒型\", \"烈毒\", \"郁毒\", \"寒毒\", \"赤毒\", \"腐毒\", \"幻毒\")",
                    "monthly event model must explicitly choose one of the six poison types");
                RequireContains(companion,
                    "ToolDef.Sel(\"所下毒型\", \"烈毒\", \"郁毒\", \"寒毒\", \"赤毒\", \"腐毒\", \"幻毒\")",
                    "companion monthly model must explicitly choose one of the six poison types");

                string kill = Slice(backend,
                    "private static SerializableModData Kill(",
                    "private static SerializableModData Capture(");
                RequireContains(kill, "IsActorRestrained(npc, npcId)",
                    "kill rejects a restrained actor independently of the optional physical envelope");
                RequireContains(kill, "ItemTemplateHelper.GetBaseValue(candidate.ItemType, candidate.TemplateId)",
                    "kill selects the target inventory item with highest base value");
                RequireContains(kill, "!ItemTemplateHelper.IsTransferable(candidate.ItemType, candidate.TemplateId)",
                    "kill never force-transfers an item that the latest game marks non-transferable");
                RequireContains(kill, "ItemTemplateHelper.GetBaseValue(candidate.ItemType, candidate.TemplateId) == 0",
                    "kill excludes zero-value templates just like the latest native inheritable-item query");
                RequireContains(kill, "ItemTemplateHelper.GetIcon(candidate.ItemType, candidate.TemplateId) == null",
                    "kill excludes templates without a valid inventory icon");
                RequireContains(kill, "ItemTemplateHelper.IsSpecial(candidate.ItemType, candidate.TemplateId)",
                    "kill excludes special and quest-like items");
                RequireBefore(kill,
                    "TransferInventoryChecked(context, target, npc, lootKey, 1,",
                    "DomainManager.Character.CombatResultHandle_KillEnemy(context, npc, target, false);",
                    "kill transfers one loot item before native death and inheritance consume the inventory");
                RequireContains(kill, "TransferIntent.Loot",
                    "kill loot cannot be mistaken for a Taiwu gift");
                RequireContains(kill, "TransferIntent.Compensation",
                    "kill compensates the pre-transferred loot if the target remains alive");
                RequireContains(kill, "DomainManager.Character.IsCharacterAlive(targetId)",
                    "kill verifies the authoritative death postcondition before reporting success");
                RequireContains(kill, "done.Set(\"loot_name\", lootName);",
                    "kill receipt exposes the exact loot display name");

                string capture = Slice(backend,
                    "private static SerializableModData Capture(",
                    "private static bool TryValidateCombatInteractionAnchor(");
                RequireContains(capture, "IsActorRestrained(npc, npcId)",
                    "capture rejects a restrained actor independently of the optional physical envelope");
                RequireContains(capture, "p.Get(\"monthly_only_purity\", out monthlyOnlyPurity);",
                    "monthly capture receives the explicit purity-only execution mode");
                RequireBefore(capture, "if (monthlyOnlyPurity == 1)", "if (!hasRope)",
                    "monthly capture bypasses the normal rope precondition");
                RequireContains(capture,
                    "DomainManager.Character.CombatResultHandle_KidnapEnemy(context, npc, target, false);",
                    "monthly capture uses the latest native kidnapping lifecycle");

                RequireContains(effects, "public static void ApplyMonthlyPoison(",
                    "frontend exposes explicit monthly poison path");
                RequireContains(effects, "public static void ApplyMonthlyKill(",
                    "frontend exposes explicit monthly kill path");
                RequireContains(effects, "public static void ApplyMonthlyCapture(",
                    "frontend exposes explicit monthly capture path");
                RequireContains(monthlyEvent, "EffectHandler.ApplyMonthlyPoison",
                    "monthly event poison uses purity-only backend mode");
                RequireContains(monthlyEvent, "EffectHandler.ApplyMonthlyKill",
                    "monthly event kill uses purity-only backend mode");
                RequireContains(monthlyEvent, "EffectHandler.ApplyMonthlyCapture",
                    "monthly event capture uses purity-only backend mode");
                RequireContains(companion, "EffectHandler.ApplyMonthlyPoison",
                    "companion monthly poison uses purity-only backend mode");
                RequireContains(companion, "EffectHandler.ApplyMonthlyKillDetailed",
                    "companion monthly kill reads the structured mutation receipt");
                RequireContains(companion, "lootName[0] = loot;",
                    "companion monthly kill projects the exact structured loot name");

                string tradeTransaction = Slice(backend,
                    "case \"trade\":", "case \"merchant_goods\":");
                Require(tradeTransaction.IndexOf("twMoney < price", StringComparison.Ordinal) < 0,
                    "partial trade must not reject against the unadjusted whole-order price");
                RequireBefore(tradeTransaction,
                    "if (pay > 0 && npcMoney > NativeResourceCap - pay)",
                    "smd.GetGoodsList(gi2).OfflineRemove(realKey, gave);",
                    "trade preflights merchant money capacity before moving shelf goods");
                string tradePayment = Slice(backend,
                    "private static int CalculateTradePayment(",
                    "private static bool TryRestoreResourcePair(");
                RequireContains(tradePayment,
                    "(long)requestedPrice * deliveredAmount + requestedAmount - 1L",
                    "partial trade payment rounds up and cannot truncate a positive price to zero");
                RequireContains(tradePayment, "pay > buyerMoney",
                    "trade checks affordability only after calculating the actual delivered payment");
                RequireContains(tradeTransaction, "catch (TransferPreconditionException paymentError)",
                    "trade distinguishes a known no-write payment failure");
                RequireContains(tradeTransaction, "TryRestoreMerchantGoodsTrade(context, twC, tradedMerchantData,",
                    "trade compensates shelf goods when payment does not happen");
                RequireContains(tradeTransaction, "TryRestoreResourcePair(context, npcC, twC, tradedResourceType,",
                    "trade compensates character resources when payment does not happen");
                RequireContains(tradeTransaction, "TryRestoreInventoryPair(context, npcC, twC, tradedKey,",
                    "trade compensates character inventory when payment does not happen");
                RequireBefore(tradeTransaction,
                    "if (!IsSupportedCustomMerchantShelfItem(k)) continue;",
                    "smd.GetGoodsList(gi2).OfflineRemove(realKey, gave);",
                    "trade rejects 1.0.72 extra-goods entities before any shelf mutation");
                RequireContains(backend, "private static bool TryRestoreMerchantGoodsTrade(",
                    "trade shelf compensation has an exact verified helper");
                RequireContains(backend, "&& ItemOwnerMatches(key, expectedOwner, ownerId);",
                    "trade shelf compensation verifies the restored unique-item owner");
                RequireContains(backend,
                    "!ModificationStateHelper.IsActive(item.GetModificationState(), 8)",
                    "custom merchant shelf filters the 1.0.72 extra-goods registry bit");
                Require(Count(backend, "if (!IsSupportedCustomMerchantShelfItem(k)) continue;") == 2,
                    "query and trade must share the exact same supported shelf-item filter");

                string barterTransaction = Slice(backend,
                    "case \"barter\":", "case \"steal\":");
                RequireBefore(barterTransaction,
                    "ValidateTransferThingPreconditions(aId, ca, cb, ta);",
                    "firstReceipt = ApplyTransferThing(context, aId, ca, cb, ta,",
                    "barter validates the first side before any mutation");
                RequireBefore(barterTransaction,
                    "ValidateTransferThingPreconditions(bId, cb, ca, tb);",
                    "firstReceipt = ApplyTransferThing(context, aId, ca, cb, ta,",
                    "barter validates the second side before any mutation");
                Require(Count(barterTransaction, "TransferIntent.Barter") == 2,
                    "both barter legs suppress native gift metadata");
                RequireBefore(barterTransaction,
                    "if (!(be is TransferPreconditionException))",
                    "try { RollbackTransferThing(context, firstReceipt); }",
                    "barter stops writing when the second leg has an indeterminate partial state");
                RequireContains(backend,
                    "thing.Amount, TransferIntent.Compensation);",
                    "barter compensation also suppresses reverse gift metadata");

                string payload = Slice(backend,
                    "private static bool ValidateGroupPhysicalPayload(",
                    "private static bool PositiveInt(");
                string[] physicalOperationKinds =
                {
                    "gm:givesilver", "gm:giveitem", "gm:barter", "gm:steal",
                    "RpcConst.TeachSkillMethod", "gm:teachlife", "gm:writebook",
                    "RpcConst.ExecuteRelationMethod", "gm:spend_night", "gm:dissolve",
                    "gm:relate_npc", "gm:enmity", "RpcConst.MatchmakeMethod",
                    "RpcConst.KillMethod", "RpcConst.CaptureMethod", "gm:poison", "gm:heal",
                    "gm:trade", "gm:taiwu_give_item", "gm:taiwu_teach", "gm:addfeature",
                    "gm:flip_practice", "gm:equip",
                    "gm:takeoff", "gm:merchantfavor", "RpcConst.StartCombatMethod",
                };
                RequirePhysicalOperationCaseSet(payload, physicalOperationKinds);
                string backendJournaledGm = Slice(backend,
                    "private static bool IsGmMutationOp(", "private static short ClampShort(");
                string frontendJournaledGm = Slice(effects,
                    "private static bool IsJournaledGmMutation(", "/// <summary>改 NPC 心情");
                foreach (string kind in physicalOperationKinds)
                {
                    if (kind.StartsWith("gm:", StringComparison.Ordinal))
                    {
                        string gmCase = "case \"" + kind.Substring(3) + "\":";
                        RequireContains(backendJournaledGm, gmCase,
                            kind + " backend journaled producer");
                        RequireContains(frontendJournaledGm, gmCase,
                            kind + " frontend structured-receipt producer");
                    }
                    else
                    {
                        Require(Count(backend, "AddMutationMethod(" + kind + ",") == 1,
                            kind + " must be registered exactly once as a journaled mutation");
                    }
                }
                string addFeature = Slice(payload, "case \"gm:addfeature\":", "case \"gm:flip_practice\":");
                RequireContains(addFeature, "PositiveInt(parameter, \"npc\", out first)",
                    "addfeature actor binding");
                RequireContains(addFeature, "second = boundTaiwuId;", "addfeature Taiwu scene anchor");
                RequireContains(addFeature, "expectedActor = first;", "addfeature self-action actor");

                string addFeatureMutation = Slice(backend,
                    "case \"addfeature\":", "case \"merchantfavor\":");
                RequireContains(characterAuthority,
                    "element.AddFeature(context, templateId, removeMutexFeature: true);",
                    "1.0.72 add-feature GM entry replaces an occupied mutex group");
                RequireContains(characterObjectAuthority,
                    "if (removeLowerOnly && characterFeatureItem2.Level >= characterFeatureItem.Level)",
                    "1.0.72 native lower-feature protection is opt-in");
                RequireContains(addFeatureMutation,
                    "var ownedByMutexGroup = new Dictionary<short, Config.CharacterFeatureItem>();",
                    "addfeature indexes the NPC's authoritative mutex groups");
                RequireContains(addFeatureMutation,
                    "if (ownedInGroup.Level >= f.Level) continue;",
                    "addfeature cannot downgrade or laterally replace an existing positive feature");
                RequireContains(addFeatureMutation,
                    "f.Hidden || f.BelongAdventure || f.Duration != 0 || f.MutexGroupId < 0",
                    "addfeature only chooses visible permanent safely grouped features");
                RequireContains(addFeatureMutation,
                    "!f.IsAllowedForOrganization(orgTemplateId)",
                    "addfeature respects the latest organization-bound feature rule");
                RequireBefore(addFeatureMutation,
                    "DomainManager.Character.GmCmd_AddFeature(context, npcId, chosen.TemplateId);",
                    "var after = c.GetFeatureIds();",
                    "addfeature reads authoritative state after the native write");
                RequireContains(addFeatureMutation,
                    "after == null || !after.Contains(chosen.TemplateId)",
                    "addfeature must not report success unless the feature actually exists");
                RequireContains(addFeatureMutation,
                    "feature_postcondition_indeterminate",
                    "addfeature records an honest unknown receipt after an unverifiable write");

                string readBookMutation = Slice(gmCore,
                    "case \"npc_read_book\":", "case \"writebook\":");
                RequireContains(readBookMutation,
                    "CombatSkillStateHelper.GetPageInternalIndex(pageTypes, page)",
                    "NPC combat-book reading binds every logical book page to its internal page index");
                RequireContains(readBookMutation,
                    "DomainManager.CombatSkill.SetCombatSkillReadingState(",
                    "NPC combat-book reading uses the current authoritative reading-state API");
                RequireContains(readBookMutation,
                    "TryActivateCombatSkillBookPageWhenSetReadingState(",
                    "NPC combat-book reading preserves native page activation");
                RequireContains(readBookMutation,
                    "ch.ReadLifeSkillPage(context, learnedIndex, page);",
                    "NPC life-book reading uses the current authoritative life-skill page API");
                RequireContains(characterObjectAuthority,
                    "public void ReadLifeSkillPage(DataContext context, int learnedSkillIndex, byte pageId)",
                    "b24769549 authoritative life-skill reading signature");
                RequireContains(characterObjectAuthority,
                    "CombatSkillStateHelper.GetPageInternalIndex(",
                    "b24769549 authoritative combat-book page mapping");

                string useItemMutation = Slice(gmCore,
                    "case \"use_item\":", "case \"poison\":");
                RequireContains(commonUtilsAuthority, "public static bool CanItemEat(sbyte itemType, short itemTemplateId)",
                    "b24769549 exposes the authoritative player-facing usable-item boundary");
                foreach (string itemType in new[] { "case 7:", "case 8:", "case 9:", "case 12:" })
                    RequireContains(backend, itemType, "NPC usable-item category " + itemType);
                RequireContains(backend,
                    "高品药材的直接服用是太吾专属职业能力，NPC 不能使用",
                    "NPC use-item filter excludes Taiwu-only raw-material consumption");
                RequireBefore(useItemMutation,
                    "TryResolveNpcUsableInventoryItem(actor, requested,",
                    "DomainManager.Character.AddEatingItem(context, charId, realKey, null);",
                    "use-item completes all live preconditions before invoking native effects");
                RequireContains(backend,
                    "if (exact) exactSeen = true; else fuzzySeen = true;",
                    "usable-item mutation resolves duplicate names inside the live usable set");
                RequireContains(backend,
                    "key.ItemType, key.TemplateId) || key.GetConfig().IsEat()",
                    "Tianjie talisman preflight reserves the generated medicine eating slot");
                RequireContains(useItemMutation,
                    "DomainManager.Extra.EatTianJieFuLu(context, charId, realKey, consumeAmount);",
                    "Tianjie talisman uses the native conversion endpoint");
                RequireContains(useItemMutation,
                    "afterCount != beforeCount - consumeAmount",
                    "use-item success requires an exact one-use inventory postcondition");
                RequireContains(backend,
                    "case \"query_npc_usable_items\":",
                    "usable-item planning has an authoritative backend query");
                Require(Count(backend, "TryCanNpcUseItemNow(") >= 3,
                    "query and mutation must share the same final NPC usable-item predicate");
                RequireContains(talk, "case \"use_item\":",
                    "conversation execution supports NPC self-use");
                RequireContains(companion, "case \"use_item\":",
                    "companion monthly execution supports NPC self-use");
                RequireContains(monthlyEvent, "case \"event_use_item\":",
                    "jianghu event execution supports NPC self-use");
                Require(!itemsSkill.Contains("use_item"),
                    "disabled self-use is absent from action skill planning surfaces");

                string flip = Slice(payload, "case \"gm:flip_practice\":", "default:");
                RequireContains(flip, "first = envelopeActorId;", "flip actor binding");
                RequireContains(flip, "if (second == envelopeActorId) second = boundTaiwuId;",
                    "self flip maps to actor plus Taiwu");

                string equipmentAndMerchant = Slice(payload,
                    "case \"gm:equip\":", "case RpcConst.StartCombatMethod:");
                RequireContains(equipmentAndMerchant, "case \"gm:takeoff\":", "takeoff physical kind");
                RequireContains(equipmentAndMerchant, "case \"gm:merchantfavor\":", "merchant favor physical kind");
                RequireSelfActionBinding(equipmentAndMerchant, "npc", "equipment/merchant favor");
                string startPhysical = Slice(payload, "case RpcConst.StartCombatMethod:", "default:");
                RequireSelfActionBinding(startPhysical, "target_id", "start combat");

                string guard = Slice(talk, "private IEnumerator BuildGroupDispatchGuard(",
                    "// 动作回执三态:");
                string irreversibleGuard = Slice(guard,
                    "case \"kill\":", "case \"heal\":");
                RequireContains(irreversibleGuard, "case \"capture\":",
                    "capture must share the final irreversible-action authorization gate");
                RequireContains(irreversibleGuard, "case \"poison\":",
                    "poison must share the final irreversible-action authorization gate");
                RequireContains(irreversibleGuard,
                    "HighRiskActionAuthorization.IsAuthorized(toolName, targetIsTaiwu",
                    "durable mutation dispatch must enforce objective hostile target invariants");
                Require(!irreversibleGuard.Contains("_currentPlayerInput", StringComparison.Ordinal)
                    && !irreversibleGuard.Contains("QueryHostility", StringComparison.Ordinal),
                    "hostile action motivation must come from the scene skill rather than player keyword or enemy gates");
                RequireBefore(irreversibleGuard, "HighRiskActionAuthorization.IsAuthorized(toolName, targetIsTaiwu",
                    "physical(npc); physical(first); guard.Footprint.AddLife(first);",
                    "objective target invariants must pass before the physical mutation footprint is admitted");
                RequireContains(guard, "physical(npc); physical(first == npc ? taiwu : first);",
                    "self flip actor plus Taiwu frontend envelope");
                RequireContains(Slice(guard, "case \"change_equipment\":", "case \"add_feature\":"),
                    "physical(npc); physical(taiwu);", "equipment actor plus Taiwu frontend envelope");
                Require(!guard.Contains("case \"change_appearance\":", StringComparison.Ordinal),
                    "native grooming proposal must not enter the durable group mutation guard");
                RequireContains(Slice(guard, "case \"change_caravan_favor\":", "case \"start_combat\":"),
                    "physical(npc); physical(taiwu);", "merchant favor actor plus Taiwu frontend envelope");
                RequireContains(Slice(guard, "case \"start_combat\":", "default:"),
                    "physical(npc); physical(taiwu);", "combat actor plus Taiwu frontend envelope");
                RequireContains(effects, "public static void QueryTaiwuScenePresence",
                    "frontend exposes the unified authoritative Taiwu-scene query");
                RequireContains(effects, "CallGm(\"taiwu_scene_presence\"",
                    "frontend Taiwu-scene query reaches the backend authority");
                RequireContains(guard,
                    "EffectHandler.QueryTaiwuScenePresence(taiwu, physicalEndpoints",
                    "group physical guard uses the unified authoritative Taiwu-scene query");
                Require(!guard.Contains("NpcSnapshotReader.FetchGroupMembers(taiwu, ids =>",
                        StringComparison.Ordinal),
                    "group physical guard must not rebuild presence from a separate companion query");
                Require(!guard.Contains("EffectHandler.QuerySameBlockChars(taiwu, ids =>",
                        StringComparison.Ordinal),
                    "group physical guard must not rebuild presence from a separate block query");
                RequireContains(backend, "case \"taiwu_scene_presence\":",
                    "backend exposes the unified Taiwu-scene query");
                string sceneQuery = Slice(backend,
                    "case \"taiwu_scene_presence\":", "case \"actor_block_chars\":");
                RequireContains(sceneQuery, "requested.Count == 0 || requested.Count > 16",
                    "Taiwu-scene query has a strict endpoint bound");
                RequireContains(sceneQuery, "IsAtTaiwuScene(cid, taiwuId, endpoint, taiwu)",
                    "Taiwu-scene query delegates every endpoint to the shared authority");
                string blockSceneQuery = Slice(backend,
                    "case \"block_chars\":", "case \"taiwu_scene_presence\":");
                RequireContains(blockSceneQuery, "JHYL_BLOCK_CHARS_ALIVE_LOCATION_SCAN",
                    "Taiwu scene roster documents the post-update CharacterSet fallback");
                RequireContains(blockSceneQuery,
                    "AddAllCharactersAtTaiwuScene(taiwuId, tw, sceneCandidates)",
                    "Taiwu scene roster supplements MapBlockData with final-presence authority");
                string sceneAuthority = Slice(backend,
                    "private static bool IsAtTaiwuScene(", "private static bool IsAtActorScene(");
                RequireContains(sceneAuthority,
                    "DomainManager.Organization.GetPrisonerSect(charId) == (sbyte)16",
                    "Taiwu-village prisoners count as physically present to Taiwu");
                RequireContains(sceneAuthority,
                    "DomainManager.Taiwu.GetTaiwuVillageLocation()",
                    "Taiwu-village prisoner presence resolves the authoritative village location");
                RequireContains(sceneAuthority,
                    "taiwuLocation.Equals(villageLocation)",
                    "a village prisoner is only present while Taiwu is actually at the village");
                RequireContains(sceneAuthority, "TaiwuScenePresencePolicy.IsPresent",
                    "Taiwu-scene authority delegates its truth table to the executable policy");
                string poisonTransaction = Slice(backend,
                    "case \"poison\":", "case \"heal\":");
                RequireContains(poisonTransaction, "if (kv.Value > 0 && pt >= 0)",
                    "normal poison selection excludes zero-count inventory keys");
                RequireContains(poisonTransaction, "if (normalPoisonBefore >= 25000)",
                    "normal poison fails before item consumption at the native poison clamp");
                RequireContains(poisonTransaction, "poisonApplied <= normalPoisonBefore",
                    "normal poison verifies that the native poison write had an effect");
                RequireContains(poisonTransaction,
                    "poisonItemAfter == poisonItemBefore - 1",
                    "normal poison verifies exact one-item consumption");
                RequireContains(poisonTransaction,
                    "RemoveInventoryItem(context, poisonKey, 1, deleteItem: true)",
                    "normal poison deletes consumed unique medicine entities");
                RequireContains(poisonTransaction,
                    "if (inventoryConsumptionComplete && !poisonPureStackable",
                    "normal poison distinguishes stackable counts from unique-entity deletion");
                RequireContains(poisonTransaction,
                    "DomainManager.Item.RemoveItem(context, poisonKey)",
                    "normal poison completes a unique-entity deletion interrupted after inventory removal");
                RequireContains(poisonTransaction,
                    "&& (poisonPureStackable || !poisonEntityStillExists)",
                    "normal poison verifies that a consumed unique medicine entity no longer exists");
                RequireContains(poisonTransaction, "TryRestorePoisonValue(context, target, poisonType",
                    "normal poison compensates a confirmed zero-write item-consumption failure");
                RequireContains(poisonTransaction, "poison_partial_commit",
                    "normal poison reports ambiguous partial state as indeterminate");
                string writeBookTransaction = Slice(backend,
                    "case \"writebook\":", "case \"taiwu_fame\":");
                RequireContains(writeBookTransaction,
                    "TryInventoryCount(taiwuC, bookKey, out int bookInventoryBefore)",
                    "write-book snapshots the recipient inventory before delivery");
                RequireContains(writeBookTransaction,
                    "bookInventoryAfter == bookInventoryBefore + 1",
                    "write-book verifies exact inventory delivery after native tail exceptions");
                RequireContains(writeBookTransaction,
                    "TryEnsureInventoryOwner(bookKey, dstId)",
                    "write-book verifies or repairs the unique book owner");
                RequireContains(writeBookTransaction,
                    "TryDiscardCreatedInventoryItem(context, taiwuC, bookKey",
                    "write-book compensates incomplete inventory or owner writes");
                RequireContains(writeBookTransaction,
                    "return Fail(\"book_delivery_failed\"",
                    "write-book reports a retryable failure only after exact compensation");
                RequireContains(writeBookTransaction,
                    "return Indeterminate(\"book_delivery_indeterminate\"",
                    "write-book preserves unknown classification when compensation cannot be proven");
                string actorSceneQuery = Slice(backend,
                    "case \"actor_block_chars\":", "case \"char_location\":");
                RequireContains(actorSceneQuery,
                    "DomainManager.Character.GetKidnappedCharacters(actorId).GetCollection()",
                    "actor-scene query includes captives carried outside MapBlockData");
                RequireContains(actorSceneQuery,
                    "captured.GetKidnapperId() == actorId",
                    "actor-scene captive discovery revalidates the authoritative captor");
                RequireContains(actorSceneQuery, "JHYL_ACTOR_BLOCK_CHARS_ALIVE_LOCATION_SCAN",
                    "actor scene roster documents the post-update CharacterSet fallback");
                RequireContains(actorSceneQuery, "AddAllCharactersAtActorScene(actorId, ids)",
                    "actor scene roster supplements MapBlockData with final-presence authority");
                string actorSceneAuthority = Slice(backend,
                    "private static bool IsAtActorScene(", "private static bool ValidateGroupPhysicalPayload(");
                RequireContains(actorSceneAuthority, "endpoint.GetKidnapperId() == actorId",
                    "actor-scene final guard treats the actor's own captive as present");
                string blockResolver = Slice(backend,
                    "private static int ResolveInBlocks(", "private static int ResolveGlobalByName(");
                RequireContains(blockResolver, "JHYL_RESOLVE_BLOCKS_ALIVE_LOCATION_SCAN",
                    "local name resolution documents its authoritative alive-location fallback");
                RequireContains(blockResolver,
                    "AddAllCharactersAtActorScene(speakerId, sceneCandidates)",
                    "local name resolution sees people physically present with the speaker");
                RequireContains(blockResolver,
                    "AddAllCharactersAtTaiwuScene(taiwuId, taiwu, sceneCandidates)",
                    "local name resolution sees people physically present with Taiwu");
                string sceneCollectors = Slice(backend,
                    "private static void AddAllCharactersAtTaiwuScene(",
                    "private static bool ValidateGroupPhysicalPayload(");
                RequireContains(sceneCollectors, "GmCmd_GetAllCharacterName()",
                    "scene roster fallback enumerates current live engine characters");
                RequireContains(sceneCollectors, "IsAtTaiwuScene(cid, taiwuId, person, taiwu)",
                    "Taiwu roster and final action guard share one scene truth");
                RequireContains(sceneCollectors, "IsAtActorScene(cid, actorId, person)",
                    "actor roster and final action guard share one scene truth");

                string semanticAgentLoop = Slice(talk,
                    "var situationTools =", "bool pendingFailedActionProse = false;");
                if (semanticAgentLoop.Contains("LooksLikeTaiwuGiftOffer(intentInput)")
                    || semanticAgentLoop.Contains("RouteIntent(")
                    || semanticAgentLoop.Contains("bool actionReq ="))
                    throw new InvalidOperationException(
                        "conversation actions must not be enabled or forced by player keyword routing");
                RequireContains(toolRegistry, "query_taiwu_items",
                    "Taiwu gift inventory lookup remains visible in the stable tool schema");
                RequireContains(toolRegistry, "taiwu_give_item",
                    "Taiwu gift mutation remains visible in the stable tool schema");
                Require(!talk.Contains("TryInferPromisedActionTool(", StringComparison.Ordinal)
                    && !talk.Contains("LooksLikeFakeToolSuccess(", StringComparison.Ordinal)
                    && !talk.Contains("JHYL_PROMISE_GATE", StringComparison.Ordinal),
                    "plain assistant prose is displayed without keyword auditing or hidden repair rounds");
                string queryPersonTool = Slice(talk,
                    "case \"query_person\":", "case \"query_current_block\":");
                RequireContains(queryPersonTool,
                    "EffectHandler.ResolveChar(npc, pn, true, true,",
                    "read-only person search accepts a globally unique strict full name");
                RequireContains(queryPersonTool, "实时现场核验",
                    "person search reports whether a physical action can reach the target now");
                Require(!talk.Contains("JHYL_DIRECT_PHYSICAL_SCENE_PREFETCH", StringComparison.Ordinal)
                    && !talk.Contains("JHYL_SCENE_DISCOVERY_PREFETCH", StringComparison.Ordinal)
                    && !talk.Contains("RequiresLiveSceneDiscovery(", StringComparison.Ordinal)
                    && !talk.Contains("LiveSceneDiscoveryActions", StringComparison.Ordinal),
                    "conversation runtime no longer guesses a physical workflow from player wording or preloads a hidden roster");
                RequireContains(toolRegistry,
                    "太吾让你从现场自行挑选时，先主动调用 query_current_block",
                    "the model receives the semantic procedure for discovering a live kill target");
                RequireContains(toolRegistry,
                    "太吾让你从现场自行挑选时，先主动调用 query_current_block，再结合性情",
                    "the model receives the semantic procedure for discovering a live capture target");
                RequireContains(toolRegistry,
                    "要从现场自行选人时先主动调用 query_current_block",
                    "the model receives the semantic procedure for discovering a live poison target");
                RequireContains(toolRegistry,
                    "无需预先存在仇怨",
                    "pre-existing enmity is not a kill or capture eligibility requirement");
                Require(!toolRegistry.Contains("不要随机杀人", StringComparison.Ordinal)
                    && !toolRegistry.Contains("不得随机抓人", StringComparison.Ordinal),
                    "obsolete blanket random-target prohibitions are removed");
                string localSceneRoster = Slice(talk,
                    "List<int> ids = null; List<int> companions = null;",
                    "case \"query_area_people\":");
                RequireContains(localSceneRoster, "id != taiwu && id != npc",
                    "the speaking NPC is excluded from its own selectable scene roster");

                string chatScrollDown = Slice(chatWindow,
                    "void ScrollDown()", "System.Collections.IEnumerator ScrollDownNextFrame()");
                Require(!chatScrollDown.Contains("verticalNormalizedPosition", StringComparison.Ordinal),
                    "streaming chat scroll must not force a global Canvas update through ScrollRect");
                RequireContains(chatScrollDown, "_content.anchoredPosition = position;",
                    "streaming chat scroll updates only the local top-anchored content");
                RequireContains(chatScrollDown, "_content.rect.height - viewport.rect.height",
                    "streaming chat bottom position uses the actual local viewport");
                string successionLifecycle = Slice(worldLifecycle,
                    "public static void MarkTaiwuIdentityChanged(", "public static void MarkWorldDateReady()");
                Require(!successionLifecycle.Contains("+ _generation + \")\")", StringComparison.Ordinal),
                    "Taiwu succession lifecycle log has no unmatched trailing parenthesis");
                string travelValidation = Slice(backend,
                    "private static bool TryGetNpcTravelEligibility(",
                    "private static SerializableModData FilterDead(");
                RequireContains(travelValidation,
                    "DomainManager.Organization.GetPrisonerSect(npcId) >= 0",
                    "imprisoned NPCs cannot accept independent travel");
                RequireContains(travelValidation,
                    "TryGetNpcTravelEligibility(npcId, npc,",
                    "every independent travel request reuses the authoritative eligibility gate");
                string captiveCapture = Slice(backend,
                    "private static SerializableModData Capture(", "private static bool TryValidateCombatInteractionAnchor(");
                RequireContains(captiveCapture,
                    "DomainManager.Organization.GetPrisonerSect(targetId) >= 0",
                    "capture cannot re-capture an imprisoned target");
                string restrainedGoal = Slice(backend,
                    "private static SerializableModData AddGoal(",
                    "private static SerializableModData AddNpcTravelTarget(");
                RequireContains(restrainedGoal, "npc.GetKidnapperId() >= 0",
                    "all exposed goal templates reject kidnapped actors");
                RequireContains(restrainedGoal,
                    "DomainManager.Organization.GetPrisonerSect(npcId) >= 0",
                    "all exposed goal templates reject imprisoned actors");
                RequireContains(effects, "CallGm(\"equip\"", "equip operation-kind producer");
                RequireContains(effects, "CallGm(\"takeoff\"", "takeoff operation-kind producer");
                RequireContains(effects, "CallGm(\"merchantfavor\"", "merchant-favor operation-kind producer");
                Require(!effects.Contains("ApplyMakeover", StringComparison.Ordinal)
                    && !effects.Contains("ApplyHairStyle", StringComparison.Ordinal)
                    && !effects.Contains("ApplyChangeAppearance", StringComparison.Ordinal),
                    "legacy direct appearance frontend methods must be deleted");
                Require(!rpc.Contains("MakeoverMethod", StringComparison.Ordinal)
                    && !rpc.Contains("HairStyleMethod", StringComparison.Ordinal)
                    && !rpc.Contains("ChangeAppearanceMethod", StringComparison.Ordinal),
                    "legacy direct appearance RPC constants must be deleted");
                Require(!backend.Contains("private static SerializableModData Makeover(", StringComparison.Ordinal)
                    && !backend.Contains("private static SerializableModData HairStyle(", StringComparison.Ordinal)
                    && !backend.Contains("private static SerializableModData ChangeAppearance(", StringComparison.Ordinal),
                    "legacy direct appearance backend handlers must be deleted");
                RequireContains(nativeGrooming,
                    "UIElement.CharacterShave.SetOnInitArgs(args)",
                    "grooming initializes the base-game CharacterShave view");
                RequireContains(nativeGrooming, "UIManager.Instance.MaskUI(UIElement.CharacterShave)",
                    "grooming opens the base-game CharacterShave view");
                Require(!nativeGrooming.Contains("JumpToInteractionEventOption", StringComparison.Ordinal),
                    "grooming bypasses the original interaction prerequisite chain");
                RequireContains(talk, "CanOpenGrooming = !npcInitiated && !Remote && GroupCtx == null",
                    "grooming is limited to player-initiated local single chat");
                RequireContains(toolRegistry,
                    "!string.Equals(toolName, \"change_appearance\", StringComparison.Ordinal)",
                    "autonomous monthly surfaces reject appearance changes");

                string reconcile = Slice(companion,
                    "private static IEnumerator ReconcileCompanionMutationJournal(",
                    "private static bool IsOperationNotFound(");
                RequireBefore(reconcile, "yield return RevalidateCompanionRecoveryRoster(entry, generation,",
                    "EffectHandler.PrepareOperationIdentity(entry.WorldId, entry.TaiwuId, entry.OperationId)",
                    "current companion roster must be checked before recovery dispatch claim");
                RequireContains(reconcile, "CancelCompanionRecoveryForRosterChange(path, entry.MutationKey,",
                    "membership change terminal cancellation");
                RequireBefore(reconcile,
                    "EffectHandler.ObserveOperationOutcome(entry.OperationId,",
                    "yield return RedispatchCompanionEnvelope(entry.ToolName",
                    "recovery registers the dispatch observer before replay");
                RequireBefore(reconcile,
                    "bool recoveryWasDispatched = EffectHandler.WasOperationDispatched(entry.OperationId);",
                    "CommitCompanionRecoveryDispatchAttempt(path, entry.MutationKey)",
                    "recovery consumes its one-dispatch budget only after real RPC dispatch");
                RequireContains(reconcile, "if (!recoveryWasDispatched)",
                    "pre-dispatch recovery rejection has a separate terminal/retryable path");
                string rosterCheck = Slice(companion,
                    "private static IEnumerator RevalidateCompanionRecoveryRoster(",
                    "private static bool CancelCompanionRecoveryForRosterChange(");
                RequireContains(rosterCheck, "EffectHandler.QueryNonBabyCompanionGroup(entry.TaiwuId",
                    "authoritative current non-baby group query");
                RequireContains(rosterCheck, "!group.Contains(entry.NpcId)",
                    "current actor companion check");
                RequireContains(rosterCheck,
                    "yield return RevalidateMonthlyRecoveryTarget(entry.TargetId, generation, onDone);",
                    "non-physical recovery revalidates the exact target through the authoritative eligibility query");
                Require(!rosterCheck.Contains(
                        "EffectHandler.QueryCharNames(new List<int> { entry.TargetId }",
                        StringComparison.Ordinal),
                    "a display name is not sufficient recovery authorization");
                RequireContains(rosterCheck,
                    "EffectHandler.QueryTaiwuScenePresence(entry.TaiwuId, endpoints",
                    "physical recovery uses the unified authoritative Taiwu-scene query");
                RequireContains(rosterCheck, "var present = new HashSet<int>();",
                    "physical recovery accepts only ids returned by the scene authority");
                RequireContains(rosterCheck, "foreach (int endpointId in endpoints)",
                    "all original endpoints enumerated");
                RequireContains(rosterCheck, "!present.Contains(endpointId)",
                    "physical targets must remain in the authoritative Taiwu scene");
                string rosterCancel = Slice(companion,
                    "private static bool CancelCompanionRecoveryForRosterChange(",
                    "private static bool CheckpointCompanionRecoveryDispatch(");
                RequireContains(rosterCancel, "found.Code = \"RECOVERY_GROUP_MEMBERSHIP_CHANGED\";",
                    "membership change cancellation code");
                RequireContains(rosterCancel, "found.Receipt = null;",
                    "stale unknown receipt cleared on local cancellation");
                RequireContains(rosterCancel, "found.BackendReceiptStored = false;",
                    "local cancellation must not become ACK-eligible");

                string publishOutcome = Slice(effects,
                    "private static void PublishOperationOutcome(",
                    "/// <summary>");
                RequireContains(publishOutcome, "observer.Callback = null;",
                    "terminal publication consumes the callback but retains dispatch evidence");
                Require(!publishOutcome.Contains("OperationOutcomeObservers.Remove(operationId)",
                        StringComparison.Ordinal),
                    "terminal publication must retain dispatch evidence until explicit Forget");
                string externalGateWait = Slice(companion,
                    "bool externalGateValid = true;",
                    "yield return RefreshActorPreflightState(");
                RequireContains(externalGateWait, "heartbeat?.Invoke();",
                    "external conflict-gate waiting reports scheduler liveness");
                RequireContains(externalGateWait, "ExternalExecutionGateMaxWaitSeconds",
                    "external conflict-gate waiting has an independent absolute bound");

                RequireContains(authority,
                    "public void GmCmd_FightCharacter(DataContext context, int charId, short combatConfig)",
                    "b24185552 authoritative combat signature");
                RequireContains(authority,
                    "CombatEntry(context, new List<int> { charId }, combatConfig);",
                    "b24185552 authoritative combat behavior");
                RequireContains(eventCombatAuthority,
                    "Domain.SetListenerWithActionName(combatCompleteEvent, argBox, \"CombatOver\");",
                    "b24185552 event combat registers CombatOver before entry");
                RequireBefore(eventCombatAuthority,
                    "Domain.SetListenerWithActionName(combatCompleteEvent, argBox, \"CombatOver\");",
                    "DomainManager.Combat.CombatEntry(mainThreadDataContext, enemyTeam, combatConfigId);",
                    "b24185552 listener registration precedes native combat entry");
                RequireContains(eventDomainAuthority,
                    "ShowingEvent = TaiwuEvent.Empty;",
                    "b24185552 ToEvent empty clears the native interaction anchor");
                RequireBefore(combatResultAuthority,
                    "CombatDomainMethod.Call.SelectGetItem(",
                    "TaiwuEventDomainMethod.Call.TriggerListener(\"CombatOver\", value: true);",
                    "result confirmation settles loot before CombatOver completion");
                RequireBefore(deathCombatCompletionAuthority,
                    "EventHelper.ToEvent(string.Empty);",
                    "EventHelper.HandleCombatResultKillEnemy(character, character2, true);",
                    "native no-guard deathmatch exits the event before removing the loser");

                string worldRecovery = Slice(group,
                    "private static void RecoverWorldTransaction(",
                    "private IEnumerator RefreshCompanionRoster(");
                RequireBefore(worldRecovery, "recovered.ReplayPreparedGroupExchanges();",
                    "recovered.ReplayPendingMemoryRefresh();",
                    "world-start group recovery must settle prepared exchanges before cleanup projection");
                RequireBefore(worldRecovery, "recovered.ReplayPendingMemoryRefresh();",
                    "recovered.ConsolidateMemoryOnly();",
                    "world-start group recovery must consume cleanup journal before rebuilding projection");
                RequireContains(group, "int roundDate = CurrentDate();",
                    "group round live date capture");
                RequireContains(group, "private const int CurrentGroupDocumentVersion = 8;",
                    "durable per-exchange group projection queue plus archive segment schema");
                RequireBefore(group, "SaveTranscript(contentChanged: true, changedExchangeId:",
                    "_exchangeJournal.Complete(exchangeId, attemptId, npcId)",
                    "projection pending must persist before final child journal deletion");
                RequireContains(group,
                    "TryCreateRecoveryCandidateEnumerator(directory, \"Group_*.json*\"",
                    "world-start transcript projection scan independent of child journal entries");
                RequireContains(group, "if (!allSaved || !TryCommitMemoryProjection(",
                    "projection completion after every member save");
                RequireContains(group, "GroupContextCompressor.BuildMessages",
                    "group exchange is compressed once into shared personal context");
                RequireContains(group, "【群聊摘要｜参与者：",
                    "successful group compression enters personal context");
                RequireContains(group, "【群聊实录（压缩失败，保留完整原文）｜参与者：",
                    "failed group compression preserves the complete original transcript");
                Require(group.IndexOf("GroupContextDistiller", StringComparison.Ordinal) < 0,
                    "group compression must not request per-NPC structured memory output");
                Require(!groupCompressor.Contains("NpcMemoryStore", StringComparison.Ordinal)
                    && !groupCompressor.Contains("MemoryFlush", StringComparison.Ordinal),
                    "group compression must not extract or write long-term memory");
                RequireContains(projectionPolicy,
                    "capturedTranscriptRevision == authoritativeTranscriptRevision",
                    "projection revision compare-and-commit policy");
                RequireContains(projectionPolicy, "&& !cleanupTransactionPending",
                    "projection cleanup exclusion policy");
                Require(group.IndexOf("sp.Snap != null ? sp.Snap.CurrentDate : CurrentDate()",
                    StringComparison.Ordinal) < 0, "group receipts must not reuse tab-open snapshot date");
                string liveDate = Slice(group, "private int CurrentDate()", "private void MergePendingMemoryRefresh(");
                RequireContains(liveDate, "SingletonObject.getInstance<BasicGameData>()",
                    "group current date live BasicGameData lookup");
                RequireContains(liveDate, "return basic.CurrDate;",
                    "group current date authoritative field");

                RequireContains(group, "yield return RefreshCompanionRoster(taiwuId);",
                    "group setup refreshes authoritative companion labels without rejecting remote members");
                RequireContains(group, "_authoritativeCompanionIds.Contains(member.Id)",
                    "group members are classified from the refreshed authoritative companion roster");
                RequireContains(talk, "yield return RecalculateRemoteFromAuthoritativePresence(snap, ct);",
                    "each group child independently revalidates physical presence before exposing tools");
                RequireContains(group, "CompanionNames = new List<string>(companionNames)",
                    "authoritative companion identity passed to each dialogue agent");
                RequireContains(group, "OrdinaryChannelNames = new List<string>(ordinaryChannelNames)",
                    "ordinary group members remain distinct from same-tile companions");

                RequireContains(snapshotReader, "s.CurrentDate = (bgd != null) ? bgd.CurrDate : 0;",
                    "fresh per-turn game date snapshot");
                RequireContains(snapshotReader,
                    "s.LocationText = ResolveLocationText(dd.Location) ?? \"去向不明（游戏当前未提供有效地点）\";",
                    "fresh per-turn location snapshot with explicit unknown fallback");
                RequireContains(promptBuilder, "【本轮默认时空上下文 · 每次交谈都必须知道】",
                    "mandatory per-turn spacetime context marker");
                RequireContains(promptBuilder, "你当前所在地点:", "NPC current-location prompt field");
                RequireContains(promptBuilder, "此刻:", "game-time prompt field");
                RequireContains(promptBuilder, "你与太吾的当前关系:",
                    "NPC-Taiwu relation is mandatory per-turn default context");
                RequireContains(promptBuilder, "你对太吾的当前好感:",
                    "NPC-Taiwu favor is mandatory per-turn default context");

                RequireContains(toolRegistry, "(\"initiator\", Sel(\"发起者\", \"taiwu\", \"npc\"), true)",
                    "player/NPC combat initiator schema");
                RequireContains(talk, "_pendingCombat = new PendingCombatRequest",
                    "dialogue combat proposal queue");
                RequireContains(chatWindow, "var combat = orch.TakePendingCombat();",
                    "combat proposal UI handoff");
                RequireContains(chatWindow,
                    "EffectHandler.StartCombat(request.NpcId, request.CombatConfig, (ok, message) =>",
                    "native combat dispatch from confirmed offer");
                RequireContains(chatWindow, "}, request.OperationId);",
                    "combat dispatch stable operation identity");

                RequireContains(toolRegistry, "(\"alertness_shift\", Int(",
                    "per-turn reaction alertness schema");
                RequireContains(reactionPolicy, "return -sat * 10;",
                    "satisfaction-linked default alertness policy");
                RequireContains(reactionPolicy, "OrdinaryConversationAlertnessCap = 1000",
                    "ordinary conversation alertness trust-boundary cap");
                string reaction = Slice(backend, "case \"reaction\":", "case \"giveitem\":");
                RequireContains(reaction, "alertnessDelta = Math.Max(-1000, Math.Min(1000, alertnessDelta));",
                    "backend reaction alertness defense-in-depth cap");
                RequireContains(reaction, "reactionNpc.GetCreatingType() != 1",
                    "reaction alertness eligibility guard");
                RequireContains(reaction,
                    "DomainManager.Character.ChangeAlertness(context, npcId, alertnessDelta);",
                    "reaction applies authoritative alertness mutation");

                RequireContains(characterAuthority,
                    "public void ChangeAlertness(DataContext context, int charId, int delta)",
                    "b24185552 authoritative alertness signature");
                RequireContains(characterAuthority, "if (element_Objects.GetCreatingType() == 1)",
                    "b24185552 authoritative alertness eligibility");
                RequireContains(basicDataAuthority, "public int CurrDate;",
                    "b24185552 authoritative current-date field");
                RequireContains(displayDataAuthority, "public short TemplateId;",
                    "b24185552 authoritative template-id field");
                RequireContains(displayDataAuthority, "public byte CreatingType;",
                    "b24185552 authoritative creating-type field");
                RequireContains(displayDataAuthority, "public Location Location;",
                    "b24185552 authoritative character-location field");
                RequireContains(displayDataAuthority, "public short Charm;",
                    "current native character display data exposes charm");
                RequireContains(characterAuthority, "Charm = character.GetAttraction(),",
                    "display charm comes from the calculated native attraction, not the base template");
                RequireContains(characterAuthority, "Charm = -1,",
                    "native unknown character data uses a negative charm sentinel");
                Require(Regex.Matches(snapshotReader, Regex.Escape("s.Charm = dd.Charm;")).Count == 2,
                    "full and display-only snapshots both preserve native charm without extra RPC");
                RequireContains(portraitStore, "Charm = s.Charm,",
                    "current charm reaches the shared NPC prompt profile");
                RequireContains(snapshotReader, "CharacterCharmText.Format(tdd.Charm)",
                    "the player has an independent charm value in NPC context");
                RequireContains(snapshotReader, "CharacterCharmText.Format(dd.Charm)",
                    "brief character context exposes the same native charm");
                RequireContains(companion, "CharacterCharmText.Format(snap.Charm)",
                    "monthly companion context exposes current charm");
                RequireContains(monthlyEventGenerator, "CharacterCharmText.Format(snap.Charm)",
                    "monthly event character status exposes current charm");
                RequireContains(Read(root, "src", "JianghuYouling.Frontend", "Talk", "AssistantOrchestrator.cs"),
                    "CharacterCharmText.Format(s.Charm)", "assistant player context exposes current charm");

                Console.WriteLine("[PASS] dialogue recovery/context/reaction and StartCombat authority contracts");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FAIL] " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static string FindRepositoryRoot()
        {
            foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(Path.GetFullPath(start));
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "src", "Shared", "RpcConst.cs")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            throw new DirectoryNotFoundException("repository root not found");
        }

        private static string Read(string root, params string[] parts)
        {
            string path = root;
            int start = 0;
            if (parts.Length > 0 && string.Equals(parts[0], ".decompiled", StringComparison.OrdinalIgnoreCase))
            {
                string configured = Environment.GetEnvironmentVariable("JHYL_DECOMPILED_ROOT");
                if (!string.IsNullOrWhiteSpace(configured)) path = Path.GetFullPath(configured);
                else path = Path.Combine(root, ".decompiled");
                start = 1;
            }
            for (int i = start; i < parts.Length; i++) path = Path.Combine(path, parts[i]);
            if (!File.Exists(path)) throw new FileNotFoundException("required contract source missing", path);
            return File.ReadAllText(path);
        }

        private static string Slice(string text, string start, string end)
        {
            int from = text.IndexOf(start, StringComparison.Ordinal);
            int to = from < 0 ? -1 : text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            if (from < 0 || to <= from)
                throw new InvalidOperationException("contract block not found: " + start);
            return text.Substring(from, to - from);
        }

        private static int Count(string text, string needle)
        {
            int count = 0, offset = 0;
            while ((offset = text.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += needle.Length;
            }
            return count;
        }

        private static void RequirePhysicalOperationCaseSet(string payload, IEnumerable<string> expected)
        {
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(payload,
                "case\\s+(RpcConst\\.[A-Za-z0-9_]+|\\\"gm:[^\\\"]+\\\")\\s*:"))
            {
                string label = match.Groups[1].Value;
                if (label.Length >= 2 && label[0] == '\"') label = label.Substring(1, label.Length - 2);
                Require(actual.Add(label), "duplicate physical payload case: " + label);
            }
            var wanted = new HashSet<string>(expected, StringComparer.Ordinal);
            string missing = string.Join(",", wanted.Where(x => !actual.Contains(x)).OrderBy(x => x));
            string unexpected = string.Join(",", actual.Where(x => !wanted.Contains(x)).OrderBy(x => x));
            Require(actual.SetEquals(wanted), "physical operation kind contract drift; missing=["
                + missing + "] unexpected=[" + unexpected + "]");
        }

        private static void RequireSelfActionBinding(string block, string actorField, string label)
        {
            RequireContains(block, "PositiveInt(parameter, \"" + actorField + "\", out first)",
                label + " payload actor field");
            RequireContains(block, "second = boundTaiwuId;", label + " Taiwu scene anchor");
            RequireContains(block, "expectedActor = first;", label + " envelope actor binding");
        }

        private static void RequireContains(string text, string needle, string label)
            => Require(text.IndexOf(needle, StringComparison.Ordinal) >= 0, label + " missing");

        private static void RequireBefore(string text, string first, string second, string label)
        {
            int a = text.IndexOf(first, StringComparison.Ordinal);
            int b = text.IndexOf(second, StringComparison.Ordinal);
            Require(a >= 0 && b > a, label);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
