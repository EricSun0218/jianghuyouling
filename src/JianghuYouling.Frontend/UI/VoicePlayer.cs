using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Web;

namespace JianghuYouling
{
    /// <summary>
    /// 把 NPC/灵儿回话合成为语音并顺序播放。每次 Speak 都拥有独立 generation 和 CancellationToken；
    /// 后发请求会立即取消前一请求，旧请求即使乱序完成也不能写缓存、替换 clip 或覆盖 UI 状态。
    /// </summary>
    public static class VoicePlayer
    {
        const long MaxCacheBytes = 48L * 1024 * 1024;
        const float MaxCacheSeconds = 15f * 60f;
        const int MaxCacheEntries = 96;
        const float SeedTts2GameRelativeVolumeBoost = 1.0f;
        internal const string NoDialogueNotice = "本条没有可朗读的说话内容（当前选择：仅说话）";

        sealed class CacheEntry
        {
            public AudioClip Clip;
            public long Bytes;
            public float Seconds;
            public LinkedListNode<string> Node;
        }

        sealed class SpeechChunkWork
        {
            public int Index;
            public string Text;
            public string CacheKey;
            public AudioClip Clip;
            public bool RetainedInCache;
            public CancellationTokenSource RequestCancellation;
            public System.Threading.Tasks.Task<MiniMaxAudioResult> RequestTask;
        }

        static AudioSource _src;
        static bool _busy;
        static long _generation;
        static CancellationTokenSource _activeCancellation;
        static object _activeOwner;
        static Action _activeCanceled;
        static readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        static readonly LinkedList<string> _lru = new LinkedList<string>();
        static long _cacheBytes;
        static float _cacheSeconds;
        static int _fileSeq;

        public static bool IsBusy => _busy;
        public static bool IsOwnedBy(object owner)
            => owner != null && ReferenceEquals(_activeOwner, owner);

        static AudioSource EnsureSource()
        {
            if (_src != null) return _src;
            var go = new GameObject("JHYL_VoicePlayer");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _src = go.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
            _src.volume = 1f;
            _src.priority = 0;
            _src.ignoreListenerPause = true;
            _src.ignoreListenerVolume = true;
            _src.bypassListenerEffects = true;
            _src.bypassReverbZones = true;
            try { if (UnityEngine.Object.FindObjectOfType<AudioListener>() == null) go.AddComponent<AudioListener>(); }
            catch { }
            return _src;
        }

        /// <summary>立即停止播放、取消网络/载入，并使所有旧 coroutine 过期。</summary>
        public static void Stop()
        {
            unchecked { _generation++; }
            CancelActive();
        }

        /// <summary>Only stop the request owned by this exact tab/round token.</summary>
        public static void Stop(object owner)
        {
            if (owner == null || !ReferenceEquals(_activeOwner, owner)) return;
            unchecked { _generation++; }
            CancelActive();
        }

        /// <summary>A successful provider-configuration change invalidates account-bound audio immediately.</summary>
        public static void InvalidateCacheForConfigurationChange()
        {
            Stop();
            try { if (_src != null) _src.clip = null; } catch { }
            var keys = new List<string>(_cache.Keys);
            foreach (string key in keys) RemoveCacheEntry(key, true);
            _lru.Clear();
            _cacheBytes = 0;
            _cacheSeconds = 0f;
        }

        /// <summary>
        /// Audio and cache keys contain dialogue from the current save.  A world boundary must
        /// release both decoded clips and those text-bearing keys instead of retaining them in a
        /// DontDestroyOnLoad singleton.
        /// </summary>
        public static void ResetForWorldExit() => InvalidateCacheForConfigurationChange();

        public static void Shutdown()
        {
            InvalidateCacheForConfigurationChange();
            if (_src != null)
            {
                try { UnityEngine.Object.Destroy(_src.gameObject); } catch { }
                _src = null;
            }
        }

        public static void Speak(int npcId, bool isAssistant, string text, Action<string> onState = null, Action onDone = null)
            => Speak(null, npcId, isAssistant, text, onState, onDone);

        public static void Speak(object owner, int npcId, bool isAssistant, string text,
            Action<string> onState = null, Action onDone = null)
        {
            var host = TalkEntryHost.Instance;
            if (host == null) { onState?.Invoke("宿主缺失，无法播放"); onDone?.Invoke(); return; }

            CancelActive();
            long generation;
            unchecked { generation = ++_generation; }
            var cancellation = new CancellationTokenSource();
            _activeCancellation = cancellation;
            _activeOwner = owner;
            _activeCanceled = onDone;
            _busy = true;
            host.StartCoroutine(SpeakCo(generation, cancellation, npcId, isAssistant, text, onState, onDone));
        }

        static void CancelActive()
        {
            var old = _activeCancellation;
            var canceled = _activeCanceled;
            _activeCancellation = null;
            _activeOwner = null;
            _activeCanceled = null;
            if (old != null)
            {
                try { old.Cancel(); } catch { }
            }
            try { if (_src != null) _src.Stop(); } catch { }
            _busy = false;
            try { canceled?.Invoke(); } catch { }
        }

        static bool IsCurrent(long generation, CancellationTokenSource cancellation)
        {
            return generation == _generation && ReferenceEquals(_activeCancellation, cancellation) &&
                   cancellation != null && !cancellation.IsCancellationRequested;
        }

        static IEnumerator SpeakCo(long generation, CancellationTokenSource cancellation, int npcId, bool isAssistant,
            string text, Action<string> onState, Action onDone)
        {
            bool doneFired = false;
            Action fireDone = () =>
            {
                if (doneFired) return;
                doneFired = true;
                if (ReferenceEquals(_activeCancellation, cancellation)) _activeCanceled = null;
                try { onDone?.Invoke(); } catch { }
            };

            try
            {
                if (!TtsSettings.TryLoad(out var st))
                {
                    onState?.Invoke("语音参数文件损坏且无法从备份恢复");
                    yield break;
                }
                text = TtsProviderUtil.PrepareSpeechText(text, st.DialogueOnly);
                if (st.DialogueOnly && !TtsProviderUtil.ContainsReadableSpeech(text))
                {
                    onState?.Invoke(NoDialogueNotice);
                    yield break;
                }
                if (string.IsNullOrWhiteSpace(text)) yield break;

                var (provider, baseUrl, apiKey, model) = TtsConfig.Resolve();
                long cacheRevision = TtsConfig.CacheRevision;
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    Debug.LogWarning("[江湖有灵] 配音：未配置独立 TTS 接口");
                    onState?.Invoke("未配置语音接口；请在语音设置中填写独立接口和密钥（不会复用主模型密钥）");
                    yield break;
                }

                int gender = 1;
                int age = 30;
                string feats = "";
                string behavior = "";
                if (!isAssistant && npcId >= 0)
                {
                    NpcSnapshot snap = null;
                    yield return NpcSnapshotReader.Fetch(npcId, s => snap = s);
                    if (!IsCurrent(generation, cancellation)) yield break;
                    if (snap != null)
                    {
                        gender = string.IsNullOrEmpty(snap.Gender) ? 1 : (snap.Gender.Contains("女") ? 0 : 1);
                        age = snap.PhysiologicalAge;
                        feats = snap.Features != null ? string.Join(",", snap.Features.ToArray()) : "";
                        behavior = snap.Behavior ?? "";
                    }
                }

                string voice = !string.IsNullOrWhiteSpace(st.Voice)
                    ? st.Voice
                    : TtsConfig.PickVoice(provider, isAssistant, gender, age, feats, behavior);
                string performanceProfile = TtsConfig.PerformanceProfile(provider, isAssistant,
                    gender, age, feats, behavior);
                float speed = st.Speed;
                string emotion = st.Emotion;
                string useModel = !string.IsNullOrWhiteSpace(st.Model) ? st.Model : model;
                bool seedTts2 = provider == TtsProviderUtil.ProviderVolcengineSeedAudio
                    && baseUrl.IndexOf("/api/v3/tts/unidirectional", StringComparison.OrdinalIgnoreCase) >= 0;
                if (seedTts2)
                {
                    if (string.Equals(useModel, "seed-audio-1.0", StringComparison.OrdinalIgnoreCase))
                        useModel = "seed-tts-2.0";
                    if (string.IsNullOrWhiteSpace(st.Voice)
                        || !voice.StartsWith("zh_", StringComparison.OrdinalIgnoreCase))
                        voice = TtsConfig.PickVoice(provider, isAssistant, gender, age, feats, behavior);
                }
                float synthesisVolume = seedTts2
                    ? Mathf.Clamp(st.Vol + SeedTts2GameRelativeVolumeBoost, 0.5f, 2f)
                    : st.Vol;
                if (!TtsProviderUtil.TryPlanSpeech(provider, text, out IReadOnlyList<string> chunks,
                        out string planError))
                {
                    Debug.LogWarning("[江湖有灵] 配音：拒绝超预算请求 — " + (planError ?? "语音请求超出限制"));
                    onState?.Invoke(planError ?? "语音请求超出限制");
                    yield break;
                }
                var synthesisBudget = new TtsProviderUtil.TtsSynthesisBudget();

                Debug.Log("[江湖有灵] 配音：开始 provider=" + SafeLogValue(provider, apiKey) +
                          " model=" + SafeLogValue(useModel, apiKey) +
                          " base=" + SafeLogValue(SafeEndpointForLog(baseUrl), apiKey) +
                          " voice=" + SafeLogValue(voice, apiKey) +
                          " speed=" + speed.ToString("0.00") + " 语气=" + SafeLogValue(st.Dynamic ? "逐段动态" : (emotion ?? "auto"), apiKey) +
                          " 响度=" + synthesisVolume.ToString("0.00") +
                          " 文长=" + text.Length + " 分段=" + chunks.Count);

                if (chunks.Count > 1)
                {
                    yield return SpeakParallelCo(generation, cancellation, provider, baseUrl, apiKey,
                        cacheRevision, useModel, voice, performanceProfile, speed, emotion,
                        synthesisVolume, st.Vol, st.Pitch, st.Dynamic, st.DialogueOnly, seedTts2, chunks, onState);
                    if (IsCurrent(generation, cancellation))
                        Debug.Log("[江湖有灵] 配音：全部播放结束");
                    yield break;
                }

                for (int index = 0; index < chunks.Count; index++)
                {
                    if (!IsCurrent(generation, cancellation)) yield break;
                    string chunk = chunks[index];
                    float chunkSpeed = speed;
                    string chunkEmotion = emotion;
                    if (st.Dynamic)
                    {
                        var prosody = TtsConfig.Prosody(chunk);
                        chunkSpeed = Mathf.Clamp(st.Speed * prosody.speed, 0.5f, 2f);
                        chunkEmotion = string.IsNullOrEmpty(st.Emotion) ? prosody.emotion : st.Emotion;
                    }
                    string chunkPerformanceProfile = performanceProfile +
                        (st.DialogueOnly || TtsProviderUtil.IsDialogueSpeechChunk(chunk)
                            ? "；本段是人物对白，像当面说话一样自然"
                            : "；本段是旁白叙述，讲述自然克制，不使用播音腔");
                    string cacheKey = BuildCacheKey(provider, baseUrl, apiKey, cacheRevision, useModel, voice,
                        chunkPerformanceProfile, chunkSpeed, chunkEmotion, synthesisVolume, st.Pitch, chunk);
                    AudioClip clip = TryGetCached(cacheKey);
                    bool retainedInCache = clip != null;
                    if (clip == null)
                    {
                        if (!synthesisBudget.TryGetNextRequestTimeout(out int requestTimeoutMs))
                        {
                            ReportSynthesisDeadline(onState);
                            yield break;
                        }
                        if (!doneFired)
                            onState?.Invoke(chunks.Count > 1 ? "配音中…（" + (index + 1) + "/" + chunks.Count + "）" : "配音中…");
                        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                        requestCancellation.CancelAfter(requestTimeoutMs);
                        var requestWatch = System.Diagnostics.Stopwatch.StartNew();
                        System.Threading.Tasks.Task<MiniMaxAudioResult> task = null;
                        try
                        {
                            task = provider == TtsProviderUtil.ProviderVolcengineSeedAudio
                                ? (seedTts2
                                    ? MiniMaxClient.VolcengineSeedTts2Async(baseUrl, apiKey,
                                        string.IsNullOrWhiteSpace(useModel) ? "seed-tts-2.0" : useModel,
                                        chunk, voice, chunkPerformanceProfile, chunkSpeed, chunkEmotion, synthesisVolume,
                                        requestCancellation.Token)
                                    : MiniMaxClient.VolcengineSeedAudioAsync(baseUrl, apiKey,
                                        string.IsNullOrWhiteSpace(useModel) ? "seed-audio-1.0" : useModel,
                                        chunk, voice, chunkSpeed, chunkEmotion, synthesisVolume, st.Pitch,
                                        requestCancellation.Token))
                                : (provider == TtsProviderUtil.ProviderOpenAi
                                    ? MiniMaxClient.OpenAiSpeechAsync(baseUrl, apiKey, useModel, voice, chunk, "mp3", chunkSpeed, requestCancellation.Token)
                                    : (provider == TtsProviderUtil.ProviderDashScopeQwen
                                        ? MiniMaxClient.DashScopeQwenSpeechAsync(baseUrl, apiKey,
                                            string.IsNullOrWhiteSpace(useModel) ? "qwen3-tts-flash" : useModel, voice, chunk, requestCancellation.Token)
                                        : MiniMaxClient.TextToSpeechAsync(baseUrl, apiKey, chunk, voice,
                                            string.IsNullOrWhiteSpace(useModel) ? "speech-02-turbo" : useModel,
                                            chunkSpeed, chunkEmotion, st.Vol, st.Pitch, requestCancellation.Token)));

                            while (!task.IsCompleted)
                            {
                                if (!IsCurrent(generation, cancellation)) yield break;
                                if (requestCancellation.IsCancellationRequested && !cancellation.IsCancellationRequested)
                                {
                                    ReportSynthesisDeadline(onState);
                                    yield break;
                                }
                                yield return null;
                            }
                            if (!IsCurrent(generation, cancellation)) yield break;
                            if (requestCancellation.IsCancellationRequested && !cancellation.IsCancellationRequested)
                            {
                                ReportSynthesisDeadline(onState);
                                yield break;
                            }

                            MiniMaxAudioResult result;
                            try { result = task.Result; }
                            catch (Exception e) { result = new MiniMaxAudioResult { Ok = false, Error = FormatTaskException(e) }; }
                            if (result == null || !result.Ok || result.Mp3 == null)
                            {
                                string error = result != null ? result.Error : "空响应";
                                if (seedTts2 && IsNoReadableTextError(error))
                                {
                                    Debug.LogWarning("[江湖有灵] 配音：跳过服务端判定不可朗读的第 " +
                                                     (index + 1) + "/" + chunks.Count + " 段，字符=" + chunk.Length);
                                    continue;
                                }
                                Debug.LogWarning("[江湖有灵] 配音：第 " + (index + 1) + "/" + chunks.Count +
                                                 " 段合成失败，字符=" + chunk.Length + " — " + error);
                                onState?.Invoke("配音失败：" + error);
                                yield break;
                            }

                            AudioClip loaded = null;
                            string loadError = null;
                            yield return LoadClipCo(generation, cancellation, result.Mp3, result.Format,
                                (c, e) => { loaded = c; loadError = e; });
                            if (!IsCurrent(generation, cancellation)) yield break;
                            if (loaded == null)
                            {
                                onState?.Invoke("音频载入失败：" + (loadError ?? "未知错误"));
                                yield break;
                            }
                            clip = loaded;
                            retainedInCache = AddToCache(cacheKey, clip, result.Mp3.Length);
                        }
                        finally
                        {
                            requestWatch.Stop();
                            synthesisBudget.Charge(requestWatch.ElapsedMilliseconds);
                            if (task != null && !task.IsCompleted)
                            {
                                var cleanup = requestCancellation;
                                task.ContinueWith(t =>
                                {
                                    var ignored = t.Exception;
                                    try { cleanup.Dispose(); } catch { }
                                }, CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                                    System.Threading.Tasks.TaskScheduler.Default);
                            }
                            else
                            {
                                try { requestCancellation.Dispose(); } catch { }
                            }
                        }
                    }
                    else
                    {
                        Debug.Log("[江湖有灵] 配音：命中缓存 " + (index + 1) + "/" + chunks.Count);
                    }

                    if (!IsCurrent(generation, cancellation)) yield break;
                    try
                    {
                        var source = EnsureSource();
                        source.clip = clip;
                        source.Play();
                        if (!doneFired)
                        {
                            onState?.Invoke(null);
                            fireDone();
                        }
                        Debug.Log("[江湖有灵] 配音：播放 " + (index + 1) + "/" + chunks.Count +
                                  "，时长≈" + clip.length.ToString("0.0") + "s");
                        while (source.isPlaying)
                        {
                            if (!IsCurrent(generation, cancellation)) yield break;
                            yield return null;
                        }
                    }
                    finally
                    {
                        if (!retainedInCache && clip != null)
                        {
                            try { UnityEngine.Object.Destroy(clip); } catch { }
                        }
                    }
                }
                if (IsCurrent(generation, cancellation)) Debug.Log("[江湖有灵] 配音：全部播放结束");
            }
            finally
            {
                bool current = IsCurrent(generation, cancellation);
                if (current) fireDone();
                if (current)
                {
                    _activeCancellation = null;
                    _activeOwner = null;
                    _activeCanceled = null;
                    _busy = false;
                }
                try { cancellation?.Dispose(); } catch { }
            }
        }

        static IEnumerator SpeakParallelCo(long generation, CancellationTokenSource cancellation,
            string provider, string baseUrl, string apiKey, long cacheRevision, string model,
            string voice, string performanceProfile, float speed, string emotion,
            float synthesisVolume, float configuredVolume, int pitch, bool dynamic, bool dialogueOnly,
            bool seedTts2, IReadOnlyList<string> chunks, Action<string> onState)
        {
            var work = new List<SpeechChunkWork>(chunks.Count);
            var deadlineWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int pendingRequests = 0;
                for (int index = 0; index < chunks.Count; index++)
                {
                    if (!IsCurrent(generation, cancellation)) yield break;
                    string chunk = chunks[index];
                    float chunkSpeed = speed;
                    string chunkEmotion = emotion;
                    if (dynamic)
                    {
                        var prosody = TtsConfig.Prosody(chunk);
                        chunkSpeed = Mathf.Clamp(speed * prosody.speed, 0.5f, 2f);
                        chunkEmotion = string.IsNullOrEmpty(emotion) ? prosody.emotion : emotion;
                    }
                    string chunkPerformanceProfile = performanceProfile +
                        (dialogueOnly || TtsProviderUtil.IsDialogueSpeechChunk(chunk)
                            ? "；本段是人物对白，像当面说话一样自然"
                            : "；本段是旁白叙述，讲述自然克制，不使用播音腔");
                    string cacheKey = BuildCacheKey(provider, baseUrl, apiKey, cacheRevision, model,
                        voice, chunkPerformanceProfile, chunkSpeed, chunkEmotion, synthesisVolume,
                        pitch, chunk);
                    var item = new SpeechChunkWork
                    {
                        Index = index,
                        Text = chunk,
                        CacheKey = cacheKey,
                        Clip = TryGetCached(cacheKey),
                    };
                    item.RetainedInCache = item.Clip != null;
                    if (item.Clip != null)
                    {
                        Debug.Log("[江湖有灵] 配音：命中缓存 " + (index + 1) + "/" + chunks.Count);
                        work.Add(item);
                        continue;
                    }

                    item.RequestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    item.RequestCancellation.CancelAfter(TtsProviderUtil.SpeechSynthesisDeadlineMilliseconds);
                    item.RequestTask = provider == TtsProviderUtil.ProviderVolcengineSeedAudio
                        ? (seedTts2
                            ? MiniMaxClient.VolcengineSeedTts2Async(baseUrl, apiKey,
                                string.IsNullOrWhiteSpace(model) ? "seed-tts-2.0" : model,
                                chunk, voice, chunkPerformanceProfile, chunkSpeed, chunkEmotion,
                                synthesisVolume, item.RequestCancellation.Token)
                            : MiniMaxClient.VolcengineSeedAudioAsync(baseUrl, apiKey,
                                string.IsNullOrWhiteSpace(model) ? "seed-audio-1.0" : model,
                                chunk, voice, chunkSpeed, chunkEmotion, synthesisVolume, pitch,
                                item.RequestCancellation.Token))
                        : (provider == TtsProviderUtil.ProviderOpenAi
                            ? MiniMaxClient.OpenAiSpeechAsync(baseUrl, apiKey, model, voice, chunk,
                                "mp3", chunkSpeed, item.RequestCancellation.Token)
                            : (provider == TtsProviderUtil.ProviderDashScopeQwen
                                ? MiniMaxClient.DashScopeQwenSpeechAsync(baseUrl, apiKey,
                                    string.IsNullOrWhiteSpace(model) ? "qwen3-tts-flash" : model,
                                    voice, chunk, item.RequestCancellation.Token)
                                : MiniMaxClient.TextToSpeechAsync(baseUrl, apiKey, chunk, voice,
                                    string.IsNullOrWhiteSpace(model) ? "speech-02-turbo" : model,
                                    chunkSpeed, chunkEmotion, configuredVolume, pitch,
                                    item.RequestCancellation.Token)));
                    pendingRequests++;
                    work.Add(item);
                }

                if (pendingRequests > 0)
                {
                    onState?.Invoke("配音中…（并行合成 " + pendingRequests + " 段）");
                    bool allCompleted = false;
                    while (!allCompleted)
                    {
                        if (!IsCurrent(generation, cancellation)) yield break;
                        allCompleted = true;
                        for (int i = 0; i < work.Count; i++)
                        {
                            var task = work[i].RequestTask;
                            if (task != null && !task.IsCompleted)
                            {
                                allCompleted = false;
                                break;
                            }
                        }
                        if (!allCompleted && deadlineWatch.ElapsedMilliseconds >=
                            TtsProviderUtil.SpeechSynthesisDeadlineMilliseconds)
                        {
                            for (int i = 0; i < work.Count; i++)
                                try { work[i].RequestCancellation?.Cancel(); } catch { }
                            ReportSynthesisDeadline(onState);
                            yield break;
                        }
                        if (!allCompleted) yield return null;
                    }
                    Debug.Log("[江湖有灵] 配音：并行合成完成 请求=" + pendingRequests
                              + "，耗时≈" + deadlineWatch.Elapsed.TotalSeconds.ToString("0.0") + "s");
                }

                // All provider calls finish before decoding or playback. Decoding in source order
                // also guarantees that a faster later paragraph can never jump ahead.
                for (int index = 0; index < work.Count; index++)
                {
                    if (!IsCurrent(generation, cancellation)) yield break;
                    SpeechChunkWork item = work[index];
                    if (item.Clip != null) continue;
                    MiniMaxAudioResult result;
                    try { result = item.RequestTask.Result; }
                    catch (Exception e)
                    {
                        result = new MiniMaxAudioResult { Ok = false, Error = FormatTaskException(e) };
                    }
                    if (result == null || !result.Ok || result.Mp3 == null)
                    {
                        string error = result != null ? result.Error : "空响应";
                        if (seedTts2 && IsNoReadableTextError(error))
                        {
                            Debug.LogWarning("[江湖有灵] 配音：跳过服务端判定不可朗读的第 "
                                             + (item.Index + 1) + "/" + chunks.Count + " 段，字符="
                                             + item.Text.Length);
                            continue;
                        }
                        Debug.LogWarning("[江湖有灵] 配音：第 " + (item.Index + 1) + "/"
                                         + chunks.Count + " 段合成失败，字符=" + item.Text.Length
                                         + " — " + error);
                        onState?.Invoke("配音失败：" + error);
                        yield break;
                    }

                    AudioClip loaded = null;
                    string loadError = null;
                    yield return LoadClipCo(generation, cancellation, result.Mp3, result.Format,
                        (c, e) => { loaded = c; loadError = e; });
                    if (!IsCurrent(generation, cancellation)) yield break;
                    if (loaded == null)
                    {
                        onState?.Invoke("音频载入失败：" + (loadError ?? "未知错误"));
                        yield break;
                    }
                    item.Clip = loaded;
                    item.RetainedInCache = AddToCache(item.CacheKey, loaded, result.Mp3.Length);
                }

                Debug.Log("[江湖有灵] 配音：全部分段已就绪，开始按原顺序连续播放 分段="
                          + work.Count);
                bool started = false;
                for (int index = 0; index < work.Count; index++)
                {
                    if (!IsCurrent(generation, cancellation)) yield break;
                    SpeechChunkWork item = work[index];
                    if (item.Clip == null) continue;
                    var source = EnsureSource();
                    source.clip = item.Clip;
                    source.Play();
                    if (!started)
                    {
                        started = true;
                        onState?.Invoke(null);
                    }
                    Debug.Log("[江湖有灵] 配音：播放 " + (item.Index + 1) + "/" + work.Count
                              + "，时长≈" + item.Clip.length.ToString("0.0") + "s");
                    while (source.isPlaying)
                    {
                        if (!IsCurrent(generation, cancellation)) yield break;
                        yield return null;
                    }
                    if (!item.RetainedInCache && item.Clip != null)
                    {
                        try { UnityEngine.Object.Destroy(item.Clip); } catch { }
                        item.Clip = null;
                    }
                }
                if (!started) onState?.Invoke("没有可播放的语音片段");
            }
            finally
            {
                deadlineWatch.Stop();
                for (int i = 0; i < work.Count; i++)
                {
                    SpeechChunkWork item = work[i];
                    var task = item.RequestTask;
                    var requestCancellation = item.RequestCancellation;
                    if (requestCancellation != null)
                    {
                        if (task != null && !task.IsCompleted)
                        {
                            try { requestCancellation.Cancel(); } catch { }
                            task.ContinueWith(t =>
                            {
                                var ignored = t.Exception;
                                try { requestCancellation.Dispose(); } catch { }
                            }, CancellationToken.None,
                                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                                System.Threading.Tasks.TaskScheduler.Default);
                        }
                        else try { requestCancellation.Dispose(); } catch { }
                    }
                    if (!item.RetainedInCache && item.Clip != null)
                    {
                        try { UnityEngine.Object.Destroy(item.Clip); } catch { }
                        item.Clip = null;
                    }
                }
            }
        }

        static IEnumerator LoadClipCo(long generation, CancellationTokenSource cancellation, byte[] bytes, string format,
            Action<AudioClip, string> completed)
        {
            string fmt = (format ?? "").Trim().ToLowerInvariant();
            if (fmt != "mp3" && fmt != "wav" && fmt != "ogg")
            {
                completed?.Invoke(null, "不支持的音频格式");
                yield break;
            }

            string path;
            try
            {
                int seq = Interlocked.Increment(ref _fileSeq);
                path = Path.Combine(Application.temporaryCachePath,
                    "jhyl_voice_" + generation.ToString("x") + "_" + seq + "." + ExtForFormat(fmt));
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception e)
            {
                completed?.Invoke(null, "写入临时音频失败：" + e.GetType().Name);
                yield break;
            }

            try
            {
                string uri = "file://" + path.Replace("\\", "/");
                using (var request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioTypeForFormat(fmt)))
                {
                    var op = request.SendWebRequest();
                    while (!op.isDone)
                    {
                        if (!IsCurrent(generation, cancellation))
                        {
                            try { request.Abort(); } catch { }
                            yield break;
                        }
                        yield return null;
                    }
                    if (!IsCurrent(generation, cancellation)) yield break;
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        completed?.Invoke(null, request.error ?? "Unity 音频解码请求失败");
                        yield break;
                    }
                    AudioClip clip = null;
                    try { clip = DownloadHandlerAudioClip.GetContent(request); } catch { }
                    if (clip == null)
                    {
                        completed?.Invoke(null, "音频解码失败");
                        yield break;
                    }
                    try { clip.hideFlags = HideFlags.DontUnloadUnusedAsset; } catch { }
                    completed?.Invoke(clip, null);
                }
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        static string BuildCacheKey(string provider, string baseUrl, string apiKey, long configRevision,
            string model, string voice, string performanceProfile, float speed, string emotion,
            float volume, int pitch, string text)
        {
            return TtsProviderUtil.BuildTtsCacheScope(provider, baseUrl, apiKey, configRevision) + "|" + (model ?? "") + "|" +
                   (voice ?? "") + "|" + (performanceProfile ?? "") + "|" + speed.ToString("0.00") + "|" + (emotion ?? "") + "|" +
                   volume.ToString("0.0") + "|" + pitch + "|" + text;
        }

        static void ReportSynthesisDeadline(Action<string> onState)
        {
            string message = "语音合成总超时（" +
                (TtsProviderUtil.SpeechSynthesisDeadlineMilliseconds / 1000) + " 秒）";
            Debug.LogWarning("[江湖有灵] 配音：" + message);
            onState?.Invoke(message);
        }

        static AudioClip TryGetCached(string key)
        {
            if (!_cache.TryGetValue(key, out var entry)) return null;
            if (entry == null || entry.Clip == null)
            {
                RemoveCacheEntry(key, false);
                return null;
            }
            if (entry.Node != null)
            {
                _lru.Remove(entry.Node);
                _lru.AddLast(entry.Node);
            }
            return entry.Clip;
        }

        static bool AddToCache(string key, AudioClip clip, int encodedBytes)
        {
            if (clip == null || string.IsNullOrEmpty(key)) return false;
            long decodedBytes;
            try { decodedBytes = Math.Max(encodedBytes, (long)clip.samples * Math.Max(1, clip.channels) * sizeof(float)); }
            catch { decodedBytes = encodedBytes; }
            float seconds;
            try { seconds = Math.Max(0f, clip.length); } catch { seconds = 0f; }
            if (decodedBytes <= 0 || decodedBytes > MaxCacheBytes || seconds > MaxCacheSeconds) return false;

            RemoveCacheEntry(key, true);
            var node = _lru.AddLast(key);
            _cache[key] = new CacheEntry { Clip = clip, Bytes = decodedBytes, Seconds = seconds, Node = node };
            _cacheBytes += decodedBytes;
            _cacheSeconds += seconds;

            int guard = _cache.Count + 1;
            while ((_cacheBytes > MaxCacheBytes || _cacheSeconds > MaxCacheSeconds || _cache.Count > MaxCacheEntries) &&
                   _lru.First != null && guard-- > 0)
            {
                string oldest = _lru.First.Value;
                RemoveCacheEntry(oldest, true);
            }
            return _cache.TryGetValue(key, out var retained) && retained != null && retained.Clip == clip;
        }

        static void RemoveCacheEntry(string key, bool destroy)
        {
            if (!_cache.TryGetValue(key, out var entry)) return;
            _cache.Remove(key);
            if (entry?.Node != null) _lru.Remove(entry.Node);
            if (entry != null)
            {
                _cacheBytes = Math.Max(0, _cacheBytes - entry.Bytes);
                _cacheSeconds = Math.Max(0f, _cacheSeconds - entry.Seconds);
                if (destroy && entry.Clip != null)
                {
                    try { UnityEngine.Object.Destroy(entry.Clip); } catch { }
                }
            }
        }

        static string ExtForFormat(string fmt)
        {
            if (fmt == "wav") return "wav";
            if (fmt == "ogg") return "ogg";
            return "mp3";
        }

        static AudioType AudioTypeForFormat(string fmt)
        {
            if (fmt == "wav") return AudioType.WAV;
            if (fmt == "ogg") return AudioType.OGGVORBIS;
            return AudioType.MPEG;
        }

        static string SafeEndpointForLog(string baseUrl)
        {
            try
            {
                var u = new Uri((baseUrl ?? "").Trim());
                return u.Host + u.AbsolutePath.TrimEnd('/');
            }
            catch { return "[invalid endpoint]"; }
        }

        static string SafeLogValue(string value, string apiKey)
        {
            return SecretRedactor.Redact(value ?? "", apiKey);
        }

        static string FormatTaskException(Exception e)
        {
            if (e == null) return "异常";
            var root = (e as AggregateException)?.GetBaseException() ?? e.GetBaseException();
            string msg = e.GetType().Name;
            if (root != null && root != e) msg += " | inner=" + root.GetType().Name;
            return msg;
        }

        static bool IsNoReadableTextError(string error)
            => !string.IsNullOrWhiteSpace(error)
               && error.IndexOf("No readable text", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
