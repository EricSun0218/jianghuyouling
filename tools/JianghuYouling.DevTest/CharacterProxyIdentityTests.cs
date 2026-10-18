using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persona;
using JianghuYouling.Effects;

namespace JianghuYouling.DevTest
{
    internal static class CharacterProxyIdentityTests
    {
        private const int Taiwu = 7451, Source = 15427, OtherSource = 15428, OldProxy = 19522;

        internal static void Run()
        {
            ReusedIdCannotRouteOrPaint();
            RecreatedProxyDoesNotConsumeReusedIdContent();
            DifferentSourcesDoNotShareValidation();
            CurrentDestinationCannotConsumeAnotherOwnersContent();
            OrphanAndReusedSourceContentStayIsolated();
            TaiwuAndConflictingOwnersAreRejected();
            ReadFailureCannotFallBackToDisk();
            ConfirmedMissingAnchorRetainsSkinAndCurrentLifeState();
            ConfirmedOwnerSurvivesStaleHistoricalAliases();
            HistoricalAliasesCannotAuthorizeAnUnrecordedOwner();
            HistoricalAliasesCannotAuthorizeContentMigration();
            LateReadCannotCrossReload();
            LateCreationCannotConsumeNewWaiters();
            PrewarmIsBoundedAndReadOnly();
            Console.WriteLine("[PASS] Proxy authority: historical aliases, reused ids, owner collisions, Taiwu guards, read failures, reload races and bounded prewarm");
        }

        private static CharacterProxyIdentity Plain(int id, bool prepare = false)
            => new CharacterProxyIdentity { RequestedId = id, OriginalId = id,
                ResolvedId = id, RequiresPreparation = prepare };

        private static CharacterProxyIdentity Proxy(int requested, int source, int proxy, short template = 55)
            => new CharacterProxyIdentity { RequestedId = requested, OriginalId = source,
                ResolvedId = proxy, IsProxy = true, DisplayTemplateId = template };

        private static void ReusedIdCannotRouteOrPaint()
        {
            using (var fixture = new Fixture())
            {
                fixture.Bind(Source, OldProxy);
                var preserved = fixture.SeedContent(OldProxy);
                fixture.Answers[Source] = Plain(Source, true);
                fixture.Reload();
                Check(CharacterProxyIdentityService.ResolveKnown(Taiwu, Source) == Source,
                    "unverified disk route is not used");
                Check(!CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out _),
                    "unverified disk portrait is not used");
                Check(!CharacterProxyIdentityService.IsKnownProxy(Taiwu, OldProxy), "unverified ordinary child is not a proxy");
                CharacterProxyIdentityService.Prewarm(Taiwu);
                Check(!CharacterProxyIdentityService.IsIdentityReady(Taiwu, Source),
                    "fixed source still needs preparation after stale destination becomes ordinary");
                Check(!CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out _),
                    "confirmed ordinary child cannot receive old skin");
                string error = null;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, OldProxy, (_, failure) => error = failure);
                Check(!string.IsNullOrWhiteSpace(error), "reused id cannot open former owner's chat");
                Check(fixture.MutationCalls == 0, "prewarm and display never create characters");
                AssertUnchanged(preserved);
            }
        }

        private static void RecreatedProxyDoesNotConsumeReusedIdContent()
        {
            using (var fixture = new Fixture())
            {
                const int newProxy = 19523;
                fixture.Bind(Source, OldProxy);
                var preserved = fixture.SeedContent(OldProxy);
                fixture.Answers[Source] = Plain(Source, true);
                fixture.Reload();
                CharacterProxyIdentityService.Prewarm(Taiwu);
                fixture.Create = (id, done) =>
                {
                    Check(id == Source, "creation uses selected source, not stale proxy");
                    done(true, Source, newProxy, true, true, 0, 55, null);
                };
                int result = 0; string error = null;
                CharacterProxyIdentityService.EnsureForConversation(Taiwu, Source,
                    (id, failure) => { result = id; error = failure; });
                Check(error == null && result == newProxy, "new authoritative proxy accepted");
                Check(CharacterProxyIdentityService.ResolveKnown(Taiwu, Source) == newProxy,
                    "source adopts current backend target");
                Check(CharacterProxyIdentityService.ResolveKnown(Taiwu, OldProxy) == OldProxy,
                    "reused child is never aliased to the new proxy");
                Check(TalkOrchestrator.MigratedNpcIdsForTest.All(pair => pair.Key != OldProxy),
                    "old local target not used as migration source");
                Check(!TalkOrchestrator.FinalizedNpcIdsForTest.Contains(OldProxy),
                    "reused id content not finalized or deleted");
                AssertUnchanged(preserved);
            }
        }

        private static void DifferentSourcesDoNotShareValidation()
        {
            using (var fixture = new Fixture())
            {
                const int otherProxy = 19524;
                fixture.Bind(Source, OldProxy);
                fixture.Bind(OtherSource, OldProxy, 66);
                fixture.Reload();
                fixture.SetProxy(Source, OldProxy, 55);
                fixture.SetProxy(OtherSource, otherProxy, 66);
                CharacterProxyIdentityService.Prewarm(Taiwu);
                Check(CharacterProxyIdentityService.ResolveKnown(Taiwu, Source) == OldProxy
                    && CharacterProxyIdentityService.ResolveKnown(Taiwu, OtherSource) == otherProxy,
                    "two current backend identities remain distinct despite a shared old target");
                Check(CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out short first) && first == 55
                    && CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, otherProxy, out short second) && second == 66,
                    "portraits are taken from each current owner, not last disk writer");
                fixture.Create = (id, done) =>
                {
                    Check(id == OtherSource, "other source is independently checked");
                    done(true, OtherSource, otherProxy, false, true, 0, 66, null);
                };
                string error = null;
                CharacterProxyIdentityService.EnsureForConversation(Taiwu, OtherSource, (_, failure) => error = failure);
                Check(error == null && fixture.MutationCalls == 1, "target validation does not certify a different source");
                Check(!TalkOrchestrator.MigratedNpcIdsForTest.Any(pair => pair.Key == OldProxy),
                    "other source cannot consume first proxy's history");
            }
        }

        private static void CurrentDestinationCannotConsumeAnotherOwnersContent()
        {
            using (var fixture = new Fixture())
            {
                fixture.Bind(OtherSource, OldProxy, 66);
                var preserved = fixture.SeedContent(OldProxy);
                fixture.Reload();
                fixture.SetProxy(Source, OldProxy, 55);
                fixture.Answers[OtherSource] = Plain(OtherSource, true);
                CharacterProxyIdentityService.Prewarm(Taiwu);
                string displayError = null;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, Source, (_, error) => displayError = error);
                Check(!string.IsNullOrWhiteSpace(displayError), "new owner cannot open old owner's local content");
                fixture.Create = (id, done) => done(true, Source, OldProxy, true, true, 0, 55, null);
                string copyError = null;
                CharacterProxyIdentityService.EnsureForConversation(Taiwu, Source, (_, error) => copyError = error);
                Check(!string.IsNullOrWhiteSpace(copyError), "content collision blocks preparation before writes");
                Check(TalkOrchestrator.MigratedNpcIdsForTest.Count == 0, "collision performs no content migration");
                AssertUnchanged(preserved);
            }
        }

        private static void TaiwuAndConflictingOwnersAreRejected()
        {
            Check(!CharacterProxyIdentity.HasConsistentMapping(Source, OldProxy, OtherSource, OldProxy, Taiwu),
                "reverse owner mismatch rejected");
            Check(!CharacterProxyIdentity.HasConsistentMapping(Source, OldProxy, Source, 19525, Taiwu),
                "forward target mismatch rejected");
            Check(!CharacterProxyIdentity.HasConsistentMapping(Source, Taiwu, Source, Taiwu, Taiwu),
                "Taiwu cannot be an NPC proxy");
            Check(!Proxy(Taiwu, Taiwu, OldProxy).IsValidFor(Taiwu)
                && !Proxy(Taiwu, Source, OldProxy).IsValidFor(Taiwu),
                "Taiwu cannot be rerouted or painted as someone else's proxy either");
            using (var fixture = new Fixture())
            {
                fixture.Bind(Source, Taiwu);
                fixture.Reload();
                fixture.Answers[Source] = Proxy(Source, Source, Taiwu);
                string error = null;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, Source, (_, failure) => error = failure);
                Check(!string.IsNullOrWhiteSpace(error), "invalid Taiwu query gives a visible nonempty error");
                Check(CharacterProxyIdentityService.ResolveKnown(Taiwu, Source) == Source
                    && !CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, Taiwu, out _),
                    "malformed local or backend mapping cannot route to or repaint Taiwu");
                fixture.Create = (id, done) => done(true, Source, Taiwu, true, true, 0, 55, null);
                CharacterProxyIdentityService.EnsureForConversation(Taiwu, Source, (_, failure) => error = failure);
                Check(!string.IsNullOrWhiteSpace(error) && TalkOrchestrator.MigratedNpcIdsForTest.Count == 0,
                    "invalid creation result rejected before migration");
            }
            using (var fixture = new Fixture())
            {
                fixture.SetProxy(Source, OldProxy, 55);
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, Source, (_, error) => Check(error == null, "first owner"));
                fixture.Answers[OtherSource] = Proxy(OtherSource, OtherSource, OldProxy, 66);
                string error2 = null;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, OtherSource, (_, error) => error2 = error);
                Check(!string.IsNullOrWhiteSpace(error2), "second owner of same proxy rejected");
                Check(CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out short template) && template == 55,
                    "second owner cannot overwrite first portrait");
            }
        }

        private static void OrphanAndReusedSourceContentStayIsolated()
        {
            using (var fixture = new Fixture())
            {
                fixture.Bind(Source, OldProxy);
                fixture.Bind(Source, 19523); // OldProxy now has only its own historic skin/date.
                var preserved = fixture.SeedContent(OldProxy);
                fixture.Reload();
                fixture.SetProxy(OtherSource, OldProxy, 66);
                string error = null;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, OtherSource, (_, failure) => error = failure);
                Check(!string.IsNullOrWhiteSpace(error), "orphaned content binding is not proof of the new owner's identity");
                AssertUnchanged(preserved);
            }
            using (var fixture = new Fixture())
            {
                fixture.Bind(OtherSource, OldProxy);
                var preserved = fixture.SeedContent(OldProxy);
                fixture.Reload();
                fixture.Create = (id, done) => done(true, Source, 19529, true, true, OldProxy, 55, null);
                string error = null;
                CharacterProxyIdentityService.EnsureForConversation(Taiwu, Source, (_, failure) => error = failure);
                Check(!string.IsNullOrWhiteSpace(error) && TalkOrchestrator.MigratedNpcIdsForTest.Count == 0,
                    "a reused migration source cannot donate somebody else's content");
                AssertUnchanged(preserved);
            }
        }

        private static void ReadFailureCannotFallBackToDisk()
        {
            using (var fixture = new Fixture())
            {
                fixture.Bind(Source, OldProxy);
                fixture.Reload();
                EffectHandler.QueryCharacterProxyIdentitiesForTest = (ids, done) => done(null, "query failed");
                CharacterProxyIdentityService.Prewarm(Taiwu);
                Check(CharacterProxyIdentityService.ResolveKnown(Taiwu, Source) == Source
                    && !CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out _), "read failure stays closed");
                EffectHandler.QueryCharacterProxyIdentitiesForTest = (ids, done) => throw new InvalidOperationException("query fixture failure");
                string thrownError = null;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, Source, (_, failure) => thrownError = failure);
                Check(!string.IsNullOrWhiteSpace(thrownError), "dispatch exception releases waiters with visible error");
                EffectHandler.QueryCharacterProxyIdentitiesForTest = (ids, done) => done(new List<CharacterProxyIdentity>(), null);
                string error = null;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, Source, (_, failure) => error = failure);
                Check(!string.IsNullOrWhiteSpace(error), "incomplete query rejected");
                fixture.SetProxy(Source, OldProxy);
                fixture.InstallQuery();
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, Source, (_, failure) => error = failure);
                Check(error == null && CharacterProxyIdentityService.IsIdentityReady(Taiwu, Source),
                    "later successful read retries without creating a duplicate");
                Check(fixture.MutationCalls == 0, "read retry never uses mutation RPC");
            }
        }

        private static void LateReadCannotCrossReload()
        {
            using (var fixture = new Fixture())
            {
                var replies = new List<Action<List<CharacterProxyIdentity>, string>>();
                EffectHandler.QueryCharacterProxyIdentitiesForTest = (ids, done) => replies.Add(done);
                int oldCallbacks = 0, newCallbacks = 0;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, Source, (_, error) => oldCallbacks++);
                fixture.Reload();
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, Source, (_, error) => newCallbacks++);
                replies[0](new List<CharacterProxyIdentity> { Proxy(Source, Source, OldProxy) }, null);
                Check(oldCallbacks == 1 && newCallbacks == 0
                    && CharacterProxyIdentityService.ResolveKnown(Taiwu, Source) == Source,
                    "old same-world read neither publishes nor consumes new waiters");
                replies[1](new List<CharacterProxyIdentity> { Proxy(Source, Source, 19526) }, null);
                Check(newCallbacks == 1 && CharacterProxyIdentityService.ResolveKnown(Taiwu, Source) == 19526,
                    "new generation receives only its own read");
            }
        }

        private static void ConfirmedMissingAnchorRetainsSkinAndCurrentLifeState()
        {
            using (var fixture = new Fixture())
            {
                fixture.Bind(Source, OldProxy, 55);
                fixture.SetProxy(Source, OldProxy, -1); // Current save confirms ownership, source object has gone.
                fixture.Reload();
                Check(!CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out _),
                    "missing anchor cannot authorize a cached skin before validation");
                CharacterProxyIdentityService.Prewarm(Taiwu);
                Check(CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out short skin) && skin == 55,
                    "exact confirmed owner may retain its historical skin after story anchor leaves");
                Check(ArchivedCharacterStatusStore.ApplyAuthoritative(Taiwu, null, new[] { OldProxy }, out _),
                    "fixture prior dead status");
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, Source, (_, error) => Check(error == null, "alive proxy can open"));
                Check(!ArchivedCharacterStatusStore.IsDead(Taiwu, OldProxy),
                    "current backend life state overrides rollback-stale soul marker");
            }
        }

        private static void ConfirmedOwnerSurvivesStaleHistoricalAliases()
        {
            using (var fixture = new Fixture())
            {
                const int missingAlias = 15429, redirectedAlias = 15430, otherProxy = 19524;
                foreach (int id in new[] { Source, OtherSource, missingAlias, redirectedAlias })
                    fixture.Bind(id, OldProxy, 55);
                fixture.SetProxy(Source, OldProxy, -1); // The current owner survives without a live story anchor.
                fixture.Answers[OtherSource] = Plain(OtherSource, true);
                fixture.Answers[missingAlias] = new CharacterProxyIdentity
                    { RequestedId = missingAlias, Error = "人物已失效" };
                fixture.SetProxy(redirectedAlias, otherProxy, 66);
                foreach (int id in new[] { Source, OtherSource, missingAlias, redirectedAlias, OldProxy })
                    fixture.SeedContent(id);
                Directory.CreateDirectory(JianghuYoulingPaths.ChatLogs);
                string chat = Path.Combine(JianghuYoulingPaths.ChatLogs, "Chat_" + Taiwu + "_" + OldProxy + ".json");
                File.WriteAllText(chat, "{\"messages\":[{\"role\":\"user\",\"content\":\"existing proxy chat\"}]}");
                var preserved = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)
                    .ToDictionary(path => path, File.ReadAllBytes);
                fixture.Reload();

                Check(!CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out _),
                    "historic aliases alone still cannot authorize a cached portrait");
                // First open must not depend on prewarm having queried every historical alias.
                int opened = 0; string error = null;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, OldProxy,
                    (id, failure) => { opened = id; error = failure; });
                Check(error == null && opened == OldProxy, "confirmed recorded owner can open before alias prewarm");
                for (int reload = 0; reload < 2; reload++)
                {
                    if (reload > 0) fixture.Reload();
                    CharacterProxyIdentityService.Prewarm(Taiwu);
                    Check(CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out short skin) && skin == 55,
                        "missing, unrelated and redirected aliases do not hide the confirmed owner's cached skin");
                    CharacterProxyIdentityService.EnsureForDisplay(Taiwu, OldProxy,
                        (id, failure) => { opened = id; error = failure; });
                    Check(error == null && opened == OldProxy, "confirmed proxy stays openable after prewarm and reload");
                    Check(CharacterProxyIdentityService.IsIdentityReady(Taiwu, OldProxy),
                        "existing proxy is ready for sending despite stale historical aliases");
                    CharacterProxyIdentityService.EnsureForConversation(Taiwu, OldProxy,
                        (id, failure) => { opened = id; error = failure; });
                    Check(error == null && opened == OldProxy && fixture.MutationCalls == 0,
                        "sending to the existing proxy needs neither creation nor identity migration");
                    Check(CharacterProxyIdentityService.ResolveKnown(Taiwu, OtherSource) == OtherSource
                        && CharacterProxyIdentityService.ResolveKnown(Taiwu, missingAlias) == missingAlias
                        && CharacterProxyIdentityService.ResolveKnown(Taiwu, redirectedAlias) == otherProxy,
                        "historic aliases do not become live routes or merge independent identities");
                    Check(TalkOrchestrator.MigratedNpcIdsForTest.Count == 0
                        && TalkOrchestrator.FinalizedNpcIdsForTest.Count == 0,
                        "opening and sending never merge or remove historical source data");
                    AssertUnchanged(preserved);
                }
            }
        }

        private static void HistoricalAliasesCannotAuthorizeAnUnrecordedOwner()
        {
            using (var fixture = new Fixture())
            {
                fixture.Bind(OtherSource, OldProxy, 55);
                fixture.Bind(15429, OldProxy, 55);
                var preserved = fixture.SeedContent(OldProxy);
                fixture.SetProxy(Source, OldProxy, -1);
                fixture.Reload();
                string error = null;
                CharacterProxyIdentityService.EnsureForDisplay(Taiwu, OldProxy, (_, failure) => error = failure);
                Check(!string.IsNullOrWhiteSpace(error), "unrelated old aliases do not prove the current owner's content binding");
                Check(!CharacterProxyIdentityService.TryGetDisplayTemplate(Taiwu, OldProxy, out _),
                    "a new owner cannot borrow an unverified historical portrait");
                Check(fixture.MutationCalls == 0, "rejected display never recreates or repairs a proxy");
                AssertUnchanged(preserved);
            }
        }

        private static void HistoricalAliasesCannotAuthorizeContentMigration()
        {
            using (var fixture = new Fixture())
            {
                const int newProxy = 19523;
                fixture.Bind(Source, OldProxy, 55);
                fixture.Bind(OtherSource, OldProxy, 55);
                var preserved = fixture.SeedContent(OldProxy);
                fixture.Reload();
                fixture.Create = (id, done) => done(true, Source, newProxy, false, true, OldProxy, 55, null);
                string error = null;
                CharacterProxyIdentityService.EnsureForConversation(Taiwu, Source, (_, failure) => error = failure);
                Check(!string.IsNullOrWhiteSpace(error), "a shared historical target is still rejected as a migration donor");
                Check(TalkOrchestrator.MigratedNpcIdsForTest.Count == 0
                    && TalkOrchestrator.FinalizedNpcIdsForTest.Count == 0,
                    "retaining current content must not authorize migration or deletion of ambiguous old data");
                AssertUnchanged(preserved);
            }
        }

        private static void LateCreationCannotConsumeNewWaiters()
        {
            using (var fixture = new Fixture())
            {
                var replies = new List<Action<bool, int, int, bool, bool, int, short, string>>();
                fixture.Create = (id, done) => replies.Add(done);
                int oldCallbacks = 0, newCallbacks = 0;
                CharacterProxyIdentityService.EnsureForConversation(Taiwu, Source, (_, error) => oldCallbacks++);
                fixture.Reload();
                CharacterProxyIdentityService.EnsureForConversation(Taiwu, Source, (_, error) => newCallbacks++);
                replies[0](true, Source, OldProxy, true, true, 0, 55, null);
                Check(oldCallbacks == 1 && newCallbacks == 0 && TalkOrchestrator.MigratedNpcIdsForTest.Count == 0,
                    "old creation result cannot steal new generation mutation callbacks");
                replies[1](true, Source, 19527, true, true, 0, 55, null);
                Check(newCallbacks == 1 && CharacterProxyIdentityService.ResolveKnown(Taiwu, Source) == 19527,
                    "new generation creation remains completable");
            }
        }

        private static void PrewarmIsBoundedAndReadOnly()
        {
            using (var fixture = new Fixture())
            {
                for (int index = 0; index < 130; index++)
                {
                    fixture.Bind(20000 + index, 30000 + index);
                    fixture.SetProxy(20000 + index, 30000 + index);
                }
                fixture.Reload();
                var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)
                    .ToDictionary(path => path, File.ReadAllBytes);
                CharacterProxyIdentityService.Prewarm(Taiwu);
                Check(fixture.QueryCalls == 3 && fixture.MaxBatch <= CharacterProxyIdentity.MaxQueryBatch,
                    "prewarm serially batches the 260 persisted id hints");
                Check(fixture.MutationCalls == 0, "prewarm never prepares a proxy");
                AssertUnchanged(before);
            }
        }

        private static void AssertUnchanged(Dictionary<string, byte[]> files)
        {
            foreach (var pair in files)
                Check(File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value),
                    "preserved file: " + Path.GetFileName(pair.Key));
        }

        private static void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Proxy identity authority: " + label);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _previousRoot = JianghuYoulingPaths.Root;
            private readonly uint _previousWorld = JianghuYoulingPaths.CurrentWorldId;
            private readonly int _previousGeneration = WorldLifecycle.Generation;
            private readonly bool _previousDateReady = WorldLifecycle.HasWorldDate;
            private readonly BasicGameData _previousData = SingletonObject.DataForTest;
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "JHYL_ProxyAuthority_" + Guid.NewGuid().ToString("N"));
            internal readonly Dictionary<int, CharacterProxyIdentity> Answers = new Dictionary<int, CharacterProxyIdentity>();
            internal Action<int, Action<bool, int, int, bool, bool, int, short, string>> Create;
            internal int QueryCalls, MutationCalls, MaxBatch;

            internal Fixture()
            {
                CharacterProxyIdentityService.ResetForWorldExit();
                ArchivedCharacterStatusStore.ResetForWorldExit();
                Directory.CreateDirectory(Root);
                JianghuYoulingPaths.Root = Root;
                JianghuYoulingPaths.CurrentWorldId = 557117641;
                WorldLifecycle.Generation++;
                WorldLifecycle.HasWorldDate = true;
                SingletonObject.DataForTest = new BasicGameData { CurrDate = 178 };
                TalkOrchestrator.MigratedNpcIdsForTest.Clear();
                TalkOrchestrator.FinalizedNpcIdsForTest.Clear();
                InstallQuery();
                EffectHandler.EnsureCharacterProxyForTest = (id, done) =>
                {
                    MutationCalls++;
                    if (Create == null) throw new InvalidOperationException("Unexpected proxy mutation in read-only test");
                    Create(id, done);
                };
            }

            internal void InstallQuery()
            {
                EffectHandler.QueryCharacterProxyIdentitiesForTest = (ids, done) =>
                {
                    QueryCalls++;
                    MaxBatch = Math.Max(MaxBatch, ids.Count);
                    done(ids.Select(id => Answers.TryGetValue(id, out var answer) ? answer : Plain(id)).ToList(), null);
                };
            }

            internal void SetProxy(int source, int proxy, short template = 55)
            {
                Answers[source] = Proxy(source, source, proxy, template);
                Answers[proxy] = Proxy(proxy, source, proxy, template);
            }

            internal void Bind(int source, int proxy, short template = 55)
            {
                var register = typeof(CharacterProxyIdentityService).GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static);
                Check((bool)register.Invoke(null, new object[] { Taiwu, source, proxy, template, true, 178 }), "fixture binding");
            }

            internal Dictionary<string, byte[]> SeedContent(int id)
            {
                Check(PersonaStore.Save(JianghuYoulingPaths.Personas, Taiwu.ToString(), id.ToString(), "原人物人设", "replace"), "fixture persona");
                var memory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, Taiwu.ToString(), id.ToString());
                memory.Add(new MemoryEntry { Id = "original-memory", Content = "原人物记忆", WorldDate = 178, Importance = 7 });
                Check(memory.Save(), "fixture memory");
                return Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
                    .Where(path => !path.StartsWith(JianghuYoulingPaths.Intents + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(path => path, File.ReadAllBytes);
            }

            internal void Reload()
            {
                CharacterProxyIdentityService.ResetForWorldExit();
                ArchivedCharacterStatusStore.ResetForWorldExit();
                WorldLifecycle.Generation++;
            }

            public void Dispose()
            {
                CharacterProxyIdentityService.ResetForWorldExit();
                ArchivedCharacterStatusStore.ResetForWorldExit();
                EffectHandler.QueryCharacterProxyIdentitiesForTest = null;
                EffectHandler.EnsureCharacterProxyForTest = null;
                TalkOrchestrator.MigratedNpcIdsForTest.Clear();
                TalkOrchestrator.FinalizedNpcIdsForTest.Clear();
                JianghuYoulingPaths.Root = _previousRoot;
                JianghuYoulingPaths.CurrentWorldId = _previousWorld;
                WorldLifecycle.Generation = _previousGeneration;
                WorldLifecycle.HasWorldDate = _previousDateReady;
                SingletonObject.DataForTest = _previousData;
                if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Test cleanup is outside the temporary directory");
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
        }
    }
}
