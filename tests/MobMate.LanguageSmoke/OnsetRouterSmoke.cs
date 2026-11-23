using MobMate.Core.Native;
using MobMate.Core.Realtime;
using MobMate.Core.Tts;
using MobMate.Core.VoiceConversion;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class OnsetRouterSmoke
{
    internal static async Task RunPiperPlusModel(Action<string, bool> check, string executable, string modelDirectory, string? mossModelDirectory = null)
    {
        var directory = Path.GetFullPath(modelDirectory);
        var properties = Path.Combine(directory, "mobmate-model.properties");
        var originalProperties = await File.ReadAllTextAsync(properties);
        var home = Path.Combine(Path.GetDirectoryName(directory)!, "test-home");
        Directory.CreateDirectory(home);
        var environment = new Dictionary<string, string?>
        {
            ["PIPER_PLUS_OFFLINE_MODE"] = "1",
            ["PIPER_PLUS_AUTO_DOWNLOAD_DICT"] = "0",
            ["PIPER_PLUS_DISABLE_CACHE"] = "1",
            ["USERPROFILE"] = home, ["HOME"] = home, ["APPDATA"] = home, ["LOCALAPPDATA"] = home,
            ["OPENJTALK_DICTIONARY_PATH"] = null, ["DOTNETG2P_NAIST_JDIC_PATH"] = null,
            ["NAIST_JDIC_PATH"] = null, ["CMUDICT_PATH"] = null,
            ["PINYIN_SINGLE_PATH"] = null, ["PINYIN_PHRASES_PATH"] = null
        };
        var previous = environment.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var item in environment) Environment.SetEnvironmentVariable(item.Key, item.Value);
            using var mossHost = mossModelDirectory == null ? null : new MossTtsService();
            if (mossHost != null && mossModelDirectory != null)
            {
                mossHost.SetConfig(Path.GetFullPath(mossModelDirectory), "Ava", 160);
                check("MOSS host runtime is loaded before PiperPlus child synthesis",
                    await mossHost.WarmUpAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            }
            using var service = new PiperPlusTtsService(Path.GetFullPath(executable),
                Path.Combine(directory, "css10-ja-6lang-fp16.onnx"));
            service.SetConfig(reflectEmotion: false, configPath: Path.Combine(directory, "config.json"));
            foreach (var (language, text) in new[] { ("en", "This is a test."), ("zh", "你好。") })
            {
                // The scratch copy selects one phonemizer at a time; the installed
                // model and its multilingual metadata remain untouched.
                await File.WriteAllTextAsync(properties, $"language={language}\ntext_mode_language_tag={language}\n");
                var result = await service.SynthesizeAsync(text).WaitAsync(TimeSpan.FromSeconds(45));
                check("PiperPlus real body " + language, result.Success && result.WavBytes is { Length: > 172 } wav &&
                    TtsPlaybackService.CanPrepareFillerWav(wav) && wav.Skip(44).Any(value => value != 0));
            }
            using var missingModel = new PiperPlusTtsService(Path.GetFullPath(executable), Path.Combine(directory, "missing-model.onnx"));
            var missing = await missingModel.SynthesizeAsync("This is a test.").WaitAsync(TimeSpan.FromSeconds(5));
            check("PiperPlus missing model reports failure without PCM", !missing.Success && missing.WavBytes == null &&
                missing.Error?.Contains("model not installed", StringComparison.OrdinalIgnoreCase) == true);

            using var cancel = new CancellationTokenSource();
            var running = service.SynthesizeAsync("This is a cancellation test.", cancel.Token);
            cancel.Cancel();
            var cancelled = false;
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { cancelled = true; }
            check("PiperPlus real synthesis cancellation is propagated", cancelled);
        }
        finally
        {
            await File.WriteAllTextAsync(properties, originalProperties);
            foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
        // No audio device is opened, and the returned PCM stays in memory.
    }

    internal static async Task RunMossModel(string modelDirectory)
    {
        using var factory = MobMate.Core.AppLogging.CreateLoggerFactory("debug");
        var logger = factory.CreateLogger("OnsetMossSmoke");
        using var service = new MossTtsService();
        service.SetConfig(modelDirectory, "Ava", 160);
        var cases = new[]
        {
            ("ja", new[] { "えっと、", "あの、", "ああ", "うーん、", "ええと、", "これはテストです。" }),
            ("en", new[] { "Um,", "Well,", "Hmm,", "Let me see,", "Uh,", "This is a test." }),
            ("zh", new[] { "嗯，", "那个，", "我想想，", "怎么说呢，", "这个，", "这是一个测试。" }),
            ("es", new[] { "Eh,", "Mmm,", "A ver,", "Bueno,", "Este,", "Esta es una prueba." }),
            ("fr", new[] { "Euh,", "Hmm,", "Alors,", "Voyons,", "Eh bien,", "Ceci est un test." }),
            ("de", new[] { "Ähm,", "Hm,", "Also,", "Mal sehen,", "Nun,", "Dies ist ein Test." })
        };
        var failures = new List<string>();
        foreach (var (language, texts) in cases)
        foreach (var text in texts)
        {
            var result = await service.SynthesizeAsync(text);
            var valid = result.Success && result.WavBytes != null &&
                TtsPlaybackService.CanPrepareFillerWav(result.WavBytes) && result.WavBytes.Skip(44).Any(b => b != 0);
            logger.LogInformation("MOSS voice=Ava language={Language} text={Text} valid={Valid} frames={Frames} bytes={Bytes} elapsed={Elapsed} error={Error}",
                language, text, valid, result.FrameCount, result.WavBytes?.Length ?? 0, result.Elapsed, result.Error);
            if (!valid) failures.Add($"{language} {text}: {result.Error ?? "invalid or silent WAV"}");
        }
        if (failures.Count != 0) throw new InvalidOperationException("MOSS real synthesis failures: " + string.Join("; ", failures));

        var body = new MossTtsService();
        using var playback = new TtsPlaybackService(new Backend());
        await using var router = new TtsEngineRouter(new NullPrimaryTtsService(), playback, mossService: body);
        var config = new TtsConfig { Engine = "moss", MossModelId = "moss-tts-nano-100m-onnx-int8",
            MossModelDir = modelDirectory, MossVoiceId = "Ava", MossMaxNewFrames = 160, OutputDeviceId = "fixture-virtual-output" };
        int Prepared() => JsonSerializer.SerializeToElement(router.GetOnsetStatus()).GetProperty("prepared").GetInt32();
        foreach (var (language, voiceId) in new[] { ("es", "Ava"), ("fr", "Bella"), ("de", "Ava") })
        {
            config.MossVoiceId = voiceId;
            router.SetConfig(config);
            router.PrepareOnset(language);
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(45));
            if (Prepared() != 5) throw new InvalidOperationException($"MOSS router preparation failed for {language}/{voiceId}: {JsonSerializer.Serialize(router.GetOnsetStatus())}");
            logger.LogInformation("MOSS router prepared voice={Voice} language={Language} count={Count}; pronunciation and perceived voice unverified",
                voiceId, language, Prepared());
        }

        // Generated signal only; no recording, output device, or persisted PCM.
        var reference = new byte[32000];
        for (var i = 0; i < reference.Length / 2; i++)
        {
            var value = (short)(5000 * Math.Sin(2 * Math.PI * 220 * i / 16000));
            reference[2 * i] = (byte)value; reference[2 * i + 1] = (byte)(value >> 8);
        }
        var referenceTask = router.SetMossReferencePcmAsync(reference, 16000);
        if (Prepared() != 0) throw new InvalidOperationException("MOSS reference mutation did not invalidate prepared clips");
        if (!await referenceTask) throw new InvalidOperationException("MOSS synthetic reference encoding failed");
        router.PrepareOnset("de");
        await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(45));
        if (Prepared() != 0) throw new InvalidOperationException("MOSS custom reference allowed built-in filler voice");
        config.MossVoiceId = "Bella";
        router.SetConfig(config);
        router.PrepareOnset("de");
        await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(45));
        if (Prepared() != 5) throw new InvalidOperationException("MOSS did not resume after built-in voice change cleared reference");
        logger.LogInformation("MOSS reference mutation invalidated preparation; built-in preparation resumed after voice change");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.OnsetSynthesizeForTest = async (_, _, _) => { entered.TrySetResult(); await release.Task; return OnsetPlaybackSmoke.Wav(); };
        try
        {
            router.StopOnset(); router.SetConfig(config); router.PrepareOnset("de");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = router.OnsetPreparationCompletion;
            var result = await router.SpeakAsync("This is a test.", "moss", null, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            if (!result.Success || pending.IsCompleted) throw new InvalidOperationException("MOSS body waited for pending filler preparation");
            logger.LogInformation("MOSS body synthesized through the mock output while filler preparation was still blocked");
        }
        finally { release.TrySetResult(); }
        await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        if (Prepared() != 0) throw new InvalidOperationException("Cancelled MOSS preparation published after body synthesis");
    }

    internal static async Task RunInstalledModel(Action<string, bool> check, string modelDirectory, string nativeDirectory)
    {
        NativeLibrary.Load(Path.Combine(nativeDirectory, "onnxruntime.dll"));
        var handle = NativeLibrary.Load(Path.Combine(nativeDirectory, "sherpa-onnx-c-api.dll"));
        NativeLibrary.SetDllImportResolver(typeof(SherpaOnnx.OfflineRecognizer).Assembly,
            (name, _, _) => name == "sherpa-onnx-c-api" ? handle : IntPtr.Zero);
        var kokoro = new KokoroTtsService();
        using var playback = new TtsPlaybackService(new Backend());
        await using var router = new TtsEngineRouter(new NullPrimaryTtsService(), playback, kokoroService: kokoro);
        var config = new TtsConfig { Engine = "kokoro", KokoroModelId = "kokoro-int8-multi-lang-v1_1",
            KokoroModelDir = modelDirectory, KokoroSpeakerId = 3, KokoroSpeed = 1,
            OutputDeviceId = "fixture-virtual-output", MonitorVolumePercent = 0 };
        foreach (var (language, speaker) in new[] { ("en", 3), ("zh", 45) })
        {
            config.KokoroSpeakerId = speaker;
            router.SetConfig(config);
            check("installed model initialized: " + language, kokoro.IsModelInstalled);
            router.ConfigureOnset(true, language, () => true, true);
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(30));
            check("installed model dynamically prepares selected voice: " + language,
                JsonSerializer.SerializeToElement(router.GetOnsetStatus()).GetProperty("prepared").GetInt32() == 5);
        }
        // No onset notification is sent, so this check cannot open an audio device.
    }

    internal static async Task Run(Action<string, bool> check, string directory)
    {
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "fixture-runtime-not-executed.exe");
        var model = Path.Combine(directory, "fixture-model-not-executed.onnx");
        await File.WriteAllTextAsync(exe, "not executable");
        await File.WriteAllTextAsync(model, "not a model");
        await File.WriteAllTextAsync(model + ".json", "{\"phoneme_type\":\"espeak\",\"language\":{\"code\":\"en_US\"}}");
        var kokoroDirectory = Path.Combine(directory, "kokoro-int8-multi-lang-v1_1");
        Directory.CreateDirectory(kokoroDirectory);
        await File.WriteAllTextAsync(Path.Combine(kokoroDirectory, "mobmate-model.properties"),
            "id=kokoro-int8-multi-lang-v1_1\nspeakers=3:af_heart,4:af_sarah,45:zf_xiaobei,46:zm_yunjian\n");
        var englishKokoroDirectory = Path.Combine(directory, "kokoro-int8-en-v0_19");
        Directory.CreateDirectory(englishKokoroDirectory);
        // These IDs/names are public choices in rich_tts_catalog.json, including
        // the short blend name at SID 0 (not a missing or unknown voice).
        await File.WriteAllTextAsync(Path.Combine(englishKokoroDirectory, "mobmate-model.properties"),
            "id=kokoro-int8-en-v0_19\nspeakers=0:af,1:af_bella\n");
        var mossDirectory = Path.Combine(directory, "moss-fixture");
        Directory.CreateDirectory(mossDirectory);
        await File.WriteAllTextAsync(Path.Combine(mossDirectory, "browser_poc_manifest.json"),
            "{\"model_files\":{\"codec_meta\":\"codec.json\"},\"builtin_voices\":[{\"voice\":\"Ava\",\"prompt_audio_codes\":[[1]]},{\"voice\":\"Bella\",\"prompt_audio_codes\":[[2]]}]}");
        await File.WriteAllTextAsync(Path.Combine(mossDirectory, "tts_browser_onnx_meta.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(mossDirectory, "tokenizer.model"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(mossDirectory, "codec.json"), "{}");

        var spoken = new ConcurrentQueue<string>();
        var synthesisEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyReady = new TaskCompletionSource<PiperResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var piper = new PiperPlusTtsService(exe, model, async (text, cancellation) =>
        {
            spoken.Enqueue(text);
            if (text == "BODY") { synthesisEntered.TrySetResult(); return await bodyReady.Task.WaitAsync(cancellation); }
            return PiperResult.Ok(OnsetPlaybackSmoke.Wav());
        });
        check("existing monolingual Piper metadata remains readable", piper.SupportsFillerLanguage("en") && !piper.SupportsFillerLanguage("ja"));
        var piperProperties = Path.Combine(directory, "mobmate-model.properties");
        await File.WriteAllTextAsync(piperProperties, "text_mode_language_tag=ja-en,zh_ko es\n");
        check("Piper text-mode tag respects mixed delimiters",
            new[] { "ja", "en", "zh", "ko", "es" }.All(piper.SupportsFillerLanguage) && !piper.SupportsFillerLanguage("fr"));
        File.Delete(piperProperties);
        var backend = new Backend();
        using var playback = new TtsPlaybackService(backend);
        await using var router = new TtsEngineRouter(new NullPrimaryTtsService(), playback,
            voicevoxService: new VoicevoxTtsService("http://127.0.0.1:50021", 3), piperService: piper,
            kokoroService: new KokoroTtsService(), mossService: new MossTtsService());
        var calls = new ConcurrentQueue<(string Engine, string Voice, int Sid, float Speed, string Text, string MossVoice, int MossFrames)>();
        Task<byte[]?> Synthesize(TtsConfig snapshot, string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls.Enqueue((snapshot.Engine, snapshot.VoicevoxSpeakerId, snapshot.KokoroSpeakerId, snapshot.KokoroSpeed, text, snapshot.MossVoiceId, snapshot.MossMaxNewFrames));
            var wav = OnsetPlaybackSmoke.Wav();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            wav[44] = hash[0]; wav[45] = hash[1];
            return Task.FromResult<byte[]?>(wav);
        }
        router.OnsetSynthesizeForTest = Synthesize;
        var config = new TtsConfig { Engine = "voicevox", VoicevoxApi = "http://127.0.0.1:50021", VoicevoxSpeakerId = "26",
            KokoroModelId = "kokoro-int8-multi-lang-v1_1", KokoroModelDir = kokoroDirectory, KokoroSpeakerId = 3, KokoroSpeed = 1,
            PiperPlusExePath = exe, PiperPlusModelPath = model, OutputDeviceId = "fixture-virtual-output",
            OutputDeviceName = "Virtual Cable", MonitorVolumePercent = 0 };
        int Prepared() => JsonSerializer.SerializeToElement(router.GetOnsetStatus()).GetProperty("prepared").GetInt32();
        string Reason() => JsonSerializer.SerializeToElement(router.GetOnsetStatus()).GetProperty("reason").GetString()!;
        bool Enabled() => JsonSerializer.SerializeToElement(router.GetOnsetStatus()).GetProperty("enabled").GetBoolean();
        async Task<int> Configure(string language, bool permitted = true)
        {
            router.StopOnset();
            router.SetConfig(config);
            router.ConfigureOnset(true, language, () => permitted, true);
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            return Prepared();
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            const string speakers = "[{\"styles\":[{\"id\":26,\"type\":\"talk\"},{\"id\":4,\"type\":\"sing\"}]}]";
            check("VOICEVOX metadata accepts the unique selected talk style only",
                TtsEngineRouter.OnsetMatchesVoicevoxMetadata(speakers, 26) &&
                !TtsEngineRouter.OnsetMatchesVoicevoxMetadata(speakers, 4) &&
                !TtsEngineRouter.OnsetMatchesVoicevoxMetadata(speakers, 3) &&
                !TtsEngineRouter.OnsetMatchesVoicevoxMetadata("not-json", 26) &&
                !TtsEngineRouter.OnsetMatchesVoicevoxMetadata("[" + speakers[1..^1] + "," + speakers[1..^1] + "]", 26));

            router.SetConfig(config);
            router.PrepareOnset("ja");
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            check("VOICEVOX 26 prewarms five distinct clips while playback is OFF", Prepared() == 5 && !Enabled() && calls.Count == 5 && calls.All(c => c.Engine == "voicevox" && c.Voice == "26"));
            check("VOICEVOX 26 prewarms the five Japanese phrases", calls.Select(c => c.Text).SequenceEqual(new[] { "えっと、", "あの、", "ああ", "うーん、", "ええと、" }));
            var beforeStart = calls.Count;
            var handler = router.ConfigureOnset(true, "ja", () => true, true)!;
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            check("Start republishes prewarm without resynthesis", Enabled() && Prepared() == 5 && calls.Count == beforeStart);
            handler(1);
            var first = await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check("onset reaches real router output ownership without a final", playback.IsPlaying && !router.IsSpeakingForRealtimeBody && router.GetStatus().IsSpeaking);
            var speaking = router.SpeakAsync("BODY", "piper_plus", null, interrupt: false, CancellationToken.None);
            await synthesisEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check("body Piper synthesis starts without waiting for filler end", backend.Cancels == 0 && playback.IsPlaying);
            bodyReady.TrySetResult(PiperResult.Ok(OnsetPlaybackSmoke.Wav()));
            check("body Piper ready interrupts filler", (await speaking.WaitAsync(TimeSpan.FromSeconds(5))).Success && backend.Cancels == 1 && backend.Bodies == 1);
            check("body PCM shape and samples are preserved", backend.BodyRate == 16000 && backend.BodyChannels == 1 && backend.BodySamples!.SequenceEqual(new short[] { 1, 0 }));
            backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler(2);
            var second = await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check("router excludes immediately started WAV", first != second);
            var cancels = backend.Cancels;
            handler(2);
            check("duplicate onset is consumed only once", backend.Cancels == cancels);
            router.ConfigureOnset(false, "ja", () => true);
            check("active OFF drains filler and clears pool", !playback.IsPlaying && Prepared() == 0);
            var countAtOff = calls.Count;
            handler(3);
            check("OFF obsolete onset cannot synthesize", calls.Count == countAtOff);

            config.VoicevoxSpeakerId = "4";
            check("a different selected VOICEVOX style is dynamically prepared", await Configure("ja") == 5 && calls.TakeLast(5).All(c => c.Voice == "4"));
            check("denied permission still allows silent prewarm", await Configure("ja", false) == 5);
            backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = router.ConfigureOnset(true, "ja", () => false)!;
            handler(1);
            await Task.WhenAny(backend.Started.Task, Task.Delay(100));
            check("denied permission never starts filler output", !backend.Started.Task.IsCompleted && !playback.IsPlaying);
            config.MonitorVolumePercent = 1;
            check("monitor-on configuration may prepare without enabling output", await Configure("ja") == 5);
            backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = router.ConfigureOnset(true, "ja", () => true)!;
            handler(1);
            await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check("monitor-on reaches the same identified playback backend", playback.IsPlaying && backend.FillerMonitor == 1);
            router.StopOnset();
            config.MonitorVolumePercent = 0;
            check("VOICEVOX is not used for English", await Configure("en") == 0);
            foreach (var api in new[] { "https://localhost:50021", "http://localhost:0", "http://user@localhost:50021", "http://localhost:50021/proxy", "http://localhost:50021?redirect=1", "http://localhost:50021#fragment" })
            {
                config.VoicevoxApi = api;
                var before = calls.Count;
                check("unsupported VOICEVOX URL is silent: " + api, await Configure("ja") == 0 && calls.Count == before);
            }
            foreach (var api in new[] { "http://example.invalid:50021", "http://localhost.example.invalid:50021", "not-a-url" })
            {
                config.VoicevoxApi = api;
                var rejected = false;
                try { await Configure("ja"); } catch (ArgumentException) { rejected = true; }
                check("existing VOICEVOX config rejects non-local URL: " + api, rejected);
            }
            config.VoicevoxApi = "http://127.0.0.1:50021";
            foreach (var engine in new[] { "piper", "piper_plus" })
            {
                config.Engine = engine;
                var before = calls.Count;
                check(engine + " filler is excluded", await Configure("en") == 0 && calls.Count == before);
            }
            config.Engine = "moss";
            var beforeMoss = calls.Count;
            check("uninstalled MOSS remains silent even for an existing phrase language",
                await Configure("ja") == 0 && await Configure("fr") == 0 && calls.Count == beforeMoss);

            var voicevoxCancellationPrimary = new RecordingPrimary { Available = true };
            using (var voicevoxCancellationPlayback = new TtsPlaybackService(new Backend()))
            await using (var voicevoxCancellationRouter = new TtsEngineRouter(voicevoxCancellationPrimary, voicevoxCancellationPlayback,
                voicevoxService: new VoicevoxTtsService("http://127.0.0.1:1", 26)))
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var observedCancellation = false;
                try { await voicevoxCancellationRouter.SpeakAsync("BODY", "voicevox", null, false, cancelled.Token); }
                catch (OperationCanceledException) { observedCancellation = true; }
                check("cancelled VOICEVOX body does not fall back to SAPI",
                    observedCancellation && voicevoxCancellationPrimary.SpeakCalls == 0);
            }

            var kokoroCancellationPrimary = new RecordingPrimary { Available = true };
            using (var kokoroCancellationPlayback = new TtsPlaybackService(new Backend()))
            {
                var kokoro = new KokoroTtsService();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(KokoroTtsService).GetField("_modelInstalled", flags)!.SetValue(kokoro, true);
                var previousSynthesis = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                typeof(KokoroTtsService).GetField("_currentTask", flags)!.SetValue(kokoro, previousSynthesis.Task);
                await using var kokoroCancellationRouter = new TtsEngineRouter(kokoroCancellationPrimary, kokoroCancellationPlayback,
                    kokoroService: kokoro);
                using var cancelled = new CancellationTokenSource();
                var running = kokoroCancellationRouter.SpeakAsync("BODY", "kokoro", null, false, cancelled.Token);
                cancelled.Cancel();
                previousSynthesis.TrySetResult();
                var observedCancellation = false;
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { observedCancellation = true; }
                check("cancelled Kokoro body does not fall back to SAPI",
                    observedCancellation && kokoroCancellationPrimary.SpeakCalls == 0);
                await kokoroCancellationRouter.SpeakAsync("BODY", "kokoro", null, false, CancellationToken.None);
                check("non-cancelled Kokoro failure retains existing body fallback",
                    kokoroCancellationPrimary.SpeakCalls == 1);
            }

            var cancellationPrimary = new RecordingPrimary { Available = true };
            using (var cancellationPlayback = new TtsPlaybackService(new Backend()))
            await using (var cancellationRouter = new TtsEngineRouter(cancellationPrimary, cancellationPlayback, mossService: new MossTtsService()))
            using (var cancelled = new CancellationTokenSource())
            {
                cancellationRouter.SetConfig(new TtsConfig { Engine = "moss" });
                cancelled.Cancel();
                var observedCancellation = false;
                try { await cancellationRouter.SpeakAsync("BODY", "moss", null, false, cancelled.Token); }
                catch (OperationCanceledException) { observedCancellation = true; }
                check("cancelled MOSS body does not fall back to SAPI", observedCancellation && cancellationPrimary.SpeakCalls == 0);
                await cancellationRouter.SpeakAsync("BODY", "moss", null, false, CancellationToken.None);
                check("non-cancelled MOSS failure retains existing body fallback", cancellationPrimary.SpeakCalls == 1);
            }

            config.MossModelId = "moss-tts-nano-100m-onnx-int8";
            config.MossModelDir = mossDirectory; config.MossVoiceId = "Ava"; config.MossMaxNewFrames = 100;
            foreach (var language in new[] { "ja", "en", "zh", "es", "fr", "de", "it", "hu", "ko", "ru", "fa", "ar", "pl", "pt", "cs", "da", "sv", "el", "tr" })
                check("MOSS selected voice prepares declared language: " + language, await Configure(language) == 5 &&
                    calls.TakeLast(5).All(c => c.Engine == "moss" && c.MossVoice == "Ava" && c.MossFrames == 100));
            config.MossVoiceId = "Bella"; config.MossMaxNewFrames = 120;
            check("MOSS voice and frame changes replace the prepared generation", await Configure("fr") == 5 &&
                calls.TakeLast(5).All(c => c.MossVoice == "Bella" && c.MossFrames == 120));
            router.StopOnset(); router.SetConfig(config);
            config.MossVoiceId = "Ava"; config.MossMaxNewFrames = 140;
            router.PrepareOnset("de");
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            check("MOSS uses the applied voice and frames, not later caller mutation", Prepared() == 5 &&
                calls.TakeLast(5).All(c => c.MossVoice == "Bella" && c.MossFrames == 120));
            config.MossVoiceId = "Bella"; config.MossMaxNewFrames = 120;
            var beforeUnknown = calls.Count;
            config.MossVoiceId = "unknown";
            check("unknown MOSS voice has no default fallback", await Configure("fr") == 0 && calls.Count == beforeUnknown);
            config.MossVoiceId = "";
            check("empty MOSS voice has no default fallback", await Configure("fr") == 0 && calls.Count == beforeUnknown);
            config.MossVoiceId = "Ava";
            check("unknown MOSS language is silent", await Configure("xx") == 0 && calls.Count == beforeUnknown);
            config.MossModelId = "unknown";
            check("unknown MOSS model is silent", await Configure("fr") == 0 && calls.Count == beforeUnknown);
            config.MossModelId = "moss-tts-nano-100m-onnx-int8";
            var mossEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mossRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                config.MossVoiceId = "Ava";
                router.OnsetSynthesizeForTest = async (snapshot, text, token) =>
                {
                    if (snapshot.MossVoiceId == "Ava") { mossEntered.TrySetResult(); await mossRelease.Task; }
                    return await Synthesize(snapshot, text, CancellationToken.None);
                };
                router.StopOnset(); router.SetConfig(config); router.PrepareOnset("es");
                await mossEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var previous = router.OnsetPreparationCompletion;
                config.MossVoiceId = "Bella";
                router.SetConfig(config); router.PrepareOnset("fr");
                var next = router.OnsetPreparationCompletion;
                mossRelease.TrySetResult();
                await previous.WaitAsync(TimeSpan.FromSeconds(5));
                await next.WaitAsync(TimeSpan.FromSeconds(5));
                check("late MOSS voice generation cannot replace the new language and voice", Prepared() == 5 &&
                    calls.TakeLast(5).All(c => c.MossVoice == "Bella"));
            }
            finally { mossRelease.TrySetResult(); router.OnsetSynthesizeForTest = Synthesize; }
            config.MossVoiceId = "Ava";
            check("MOSS prepares before reference mutation", await Configure("fr") == 5);
            var refTask = router.SetMossReferencePcmAsync(new byte[3200], 16000);
            check("MOSS reference mutation immediately invalidates prepared built-in voice", Prepared() == 0);
            await refTask;

            using (var absentPlayback = new TtsPlaybackService(new Backend()))
            await using (var absentRouter = new TtsEngineRouter(new NullPrimaryTtsService(), absentPlayback))
            {
                var absentCalls = 0;
                absentRouter.OnsetSynthesizeForTest = (_, _, _) =>
                {
                    absentCalls++;
                    return Task.FromResult<byte[]?>(OnsetPlaybackSmoke.Wav());
                };
                config.Engine = "kokoro";
                absentRouter.SetConfig(config);
                absentRouter.PrepareOnset("en");
                await absentRouter.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
                var absentStatus = JsonSerializer.SerializeToElement(absentRouter.GetOnsetStatus());
                check("missing Kokoro body service cannot prepare a different voice", absentCalls == 0 &&
                    absentStatus.GetProperty("prepared").GetInt32() == 0 &&
                    absentStatus.GetProperty("reason").GetString() == "unsupported-voice-language-or-route");
            }

            config.Engine = "kokoro"; config.KokoroSpeakerId = 4; config.KokoroSpeed = 1.2f;
            router.OnsetSynthesizeForTest = null;
            var beforeUninitialized = calls.Count;
            check("uninitialized Kokoro body cannot prepare filler", await Configure("en") == 0 &&
                calls.Count == beforeUninitialized && Reason() == "unsupported-voice-language-or-route");
            router.OnsetSynthesizeForTest = Synthesize;
            check("different English Kokoro SID and speed are used", await Configure("en") == 5 && calls.TakeLast(5).All(c => c.Sid == 4 && c.Speed == 1.2f));
            config.KokoroSpeakerId = 46; config.KokoroSpeed = 0.8f;
            check("different Chinese Kokoro SID and speed are used", await Configure("zh") == 5 && calls.TakeLast(5).All(c => c.Sid == 46 && c.Speed == 0.8f));
            check("Chinese voice is not used for English", await Configure("en") == 0);
            check("unknown language is silent", await Configure("auto") == 0);
            config.KokoroSpeakerId = 4;
            router.StopOnset(); router.SetConfig(config);
            config.KokoroSpeakerId = 46; config.KokoroSpeed = 1.5f;
            router.PrepareOnset("en");
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            check("preparation uses captured SID and speed, not later caller mutation", Prepared() == 5 && calls.TakeLast(5).All(c => c.Sid == 4 && c.Speed == 0.8f));
            router.SetConfig(config); router.RefreshOnset(() => true, true);
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            check("applied voice change invalidates previous pool", Prepared() == 0);
            check("Linux NullPrimary public config remains unchanged", router.GetConfig().Engine == "windows_sapi");
            check("explicit normal capture selection is required", OnsetCapturePolicy.IsExplicitInput("{\"ok\":true,\"devices\":[{\"id\":\"mic\",\"name\":\"USB Microphone\"}]}", "mic") &&
                !OnsetCapturePolicy.IsExplicitInput("{\"ok\":true,\"devices\":[{\"id\":\"mic\",\"name\":\"USB Microphone\"}]}", ""));

            config.Engine = "voicevox"; config.VoicevoxSpeakerId = "26";
            router.StopOnset(); router.SetConfig(config);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            router.OnsetSynthesizeForTest = async (snapshot, text, token) =>
            {
                if (snapshot.VoicevoxSpeakerId == "26") { entered.TrySetResult(); await release.Task; }
                return await Synthesize(snapshot, text, CancellationToken.None);
            };
            handler = router.ConfigureOnset(true, "ja", () => true, true)!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldPreparation = router.OnsetPreparationCompletion;
            var callsBeforeUnready = calls.Count;
            handler(1);
            var bodyDuringPreparation = router.SpeakAsync("BODY", "piper_plus", null, interrupt: false, CancellationToken.None);
            check("body does not wait for pending non-cancellable preparation", (await bodyDuringPreparation.WaitAsync(TimeSpan.FromSeconds(5))).Success && backend.Bodies == 2);
            check("unready VAD does not initiate synthesis", calls.Count == callsBeforeUnready && Prepared() == 0);
            config.VoicevoxSpeakerId = "4";
            router.SetConfig(config); router.PrepareOnset("ja");
            var newPreparation = router.OnsetPreparationCompletion;
            check("new generation waits for the old non-cancellable synthesis", !newPreparation.IsCompleted && calls.Count == callsBeforeUnready);
            release.TrySetResult();
            await oldPreparation.WaitAsync(TimeSpan.FromSeconds(5));
            await newPreparation.WaitAsync(TimeSpan.FromSeconds(5));
            check("cancelled late result cannot replace the new voice pool", Prepared() == 5 && calls.TakeLast(5).All(c => c.Voice == "4") && calls.Count == callsBeforeUnready + 6);
            router.OnsetSynthesizeForTest = (_, _, _) => throw new IOException("fixture synthesis failure");
            router.StopOnset(); router.SetConfig(config); router.PrepareOnset("ja");
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            check("synthesis failure remains unprepared without fallback", Prepared() == 0 && Reason() == "preparation-failed");
            router.OnsetSynthesizeForTest = Synthesize;
            router.PrepareOnset("ja");
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            check("a later prepare retries a failed generation", Prepared() == 5);

            config.Engine = "kokoro";
            config.KokoroModelId = "kokoro-int8-en-v0_19";
            config.KokoroModelDir = englishKokoroDirectory;
            foreach (var sid in new[] { 0, 1 })
            {
                config.KokoroSpeakerId = sid;
                router.StopOnset(); router.SetConfig(config); router.PrepareOnset("en");
                await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
                check("catalog English Kokoro voice silently prewarms while OFF: " + sid,
                    Prepared() == 5 && !Enabled() && calls.TakeLast(5).All(c => c.Engine == "kokoro" && c.Sid == sid));
                var callsBeforeEnable = calls.Count;
                handler = router.ConfigureOnset(true, "en", () => true, true)!;
                backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                handler(1);
                await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var playing = playback.IsPlaying;
                var callsAfterEnable = calls.Count;
                var onsetStatus = JsonSerializer.SerializeToElement(router.GetOnsetStatus());
                var detail = $" sid={sid} fillerId={backend.FillerId} mainActive={backend.MainActive}" +
                    $" playing={playing} callsBefore={callsBeforeEnable} callsAfter={callsAfterEnable}" +
                    $" status={onsetStatus}";
                check("catalog English Kokoro prewarm reaches actual start:" + detail,
                    onsetStatus.GetProperty("playbackReason").GetString() == "started");
                check("catalog English Kokoro prewarm remains playing:" + detail, playing);
                check("catalog English Kokoro prewarm avoids onset synthesis:" + detail,
                    callsAfterEnable == callsBeforeEnable);
                router.StopOnset();
            }
            config.KokoroSpeakerId = 0;
            var callsBeforeMismatch = calls.Count;
            check("catalog short English blend is not used for Chinese", await Configure("zh") == 0 && calls.Count == callsBeforeMismatch);
            config.KokoroSpeakerId = 999;
            check("unknown English Kokoro SID remains silent", await Configure("en") == 0 && calls.Count == callsBeforeMismatch);

            // Same id/name schema as the normal capture enumeration, not the
            // separate hearing/render-loopback path. Names are not permissions.
            var captures = new[] {
                new { id = "sonar", name = "SteelSeries Sonar - Microphone" },
                new { id = "cable", name = "CABLE Output (VB-Audio Virtual Cable)" },
                new { id = "usb", name = "USB Microphone" }
            };
            var captureJson = JsonSerializer.Serialize(new { ok = true, devices = captures });
            check("unknown, unselected and failed capture enumeration stay closed",
                !OnsetCapturePolicy.IsExplicitInput(captureJson, "missing") && !OnsetCapturePolicy.IsExplicitInput(captureJson, "") &&
                !OnsetCapturePolicy.IsExplicitInput("not-json", "sonar") &&
                !OnsetCapturePolicy.IsExplicitInput("{\"ok\":false,\"devices\":[]}", "sonar"));
            config.Engine = "voicevox"; config.VoicevoxSpeakerId = "26";
            config.OutputDeviceId = "virtual-A"; config.OutputDeviceName = "Virtual Cable A";
            router.StopOnset(); router.SetConfig(config); router.PrepareOnset("ja");
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            handler = router.ConfigureOnset(true, "ja", () => OnsetCapturePolicy.IsExplicitInput(captureJson, "missing"), true)!;
            var blocked = JsonSerializer.SerializeToElement(router.GetOnsetStatus("input-required"));
            check("prepared five does not imply a valid capture selection", blocked.GetProperty("prepared").GetInt32() == 5 &&
                blocked.GetProperty("reason").GetString() == "ready" && blocked.GetProperty("playbackReason").GetString() == "input-required");
            backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler(1);
            await Task.WhenAny(backend.Started.Task, Task.Delay(100));
            check("unknown capture cannot play prepared clips", !backend.Started.Task.IsCompleted && !playback.IsPlaying);
            foreach (var capture in captures)
            {
                router.StopOnset(); router.SetConfig(config); router.PrepareOnset("ja");
                await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
                handler = router.ConfigureOnset(true, "ja", () => OnsetCapturePolicy.IsExplicitInput(captureJson, capture.id), true)!;
                var beforeOnset = calls.Count;
                backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                handler(1);
                await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check("normal selected capture reaches playback ACK without onset synthesis: " + capture.id,
                    playback.IsPlaying && calls.Count == beforeOnset &&
                    JsonSerializer.SerializeToElement(router.GetOnsetStatus()).GetProperty("playbackReason").GetString() == "started");
                check("normal capture body takes priority: " + capture.id,
                    (await router.SpeakAsync("BODY", "piper_plus", null, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Success && !playback.IsPlaying);
            }
            using (router.BeginOnsetRouteChange())
                check("native output change closes playback and discards the previous pool", router.OnsetRouteChanging && Prepared() == 0 && !playback.IsPlaying);
            check("native output change releases its gate", !router.OnsetRouteChanging);
            var oldHandler = handler;
            router.StopOnset(); router.SetConfig(config); router.PrepareOnset("ja");
            await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            router.ConfigureOnset(true, "ja", () => OnsetCapturePolicy.IsExplicitInput(captureJson, "sonar"), true);
            backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            oldHandler(3);
            await Task.WhenAny(backend.Started.Task, Task.Delay(100));
            check("old session onset cannot play a newly prepared Sonar pool", !backend.Started.Task.IsCompleted && !playback.IsPlaying);
            await MonitorRouting(check, exe, model);
            await MobfyFiller(check);
            // Explicit, private, device-free opt-in after managed regressions.
            // No runtime/model download, settings access or output PCM storage.
            var realRoot = Environment.GetEnvironmentVariable("MOBMATE_MOBFY_FILLER_REAL_ROOT");
            if (!string.IsNullOrWhiteSpace(realRoot))
                await MobfyRealPreparation(check, directory, Path.GetFullPath(realRoot));
        }
        finally { release.TrySetResult(); bodyReady.TrySetResult(PiperResult.Fail("fixture cleanup")); router.StopOnset(); }
    }

    private static async Task MonitorRouting(Action<string, bool> check, string exe, string model)
    {
        var backend = new Backend();
        using var playback = new TtsPlaybackService(backend);
        var primary = new RecordingPrimary();
        var piper = new PiperPlusTtsService(exe, model, (text, _) => Task.FromResult(PiperResult.Ok(OnsetPlaybackSmoke.Wav())));
        await using var router = new TtsEngineRouter(primary, playback,
            voicevoxService: new VoicevoxTtsService("http://127.0.0.1:50021", 26), piperService: piper);
        var calls = 0;
        router.OnsetSynthesizeForTest = (_, text, _) =>
        {
            Interlocked.Increment(ref calls);
            var wav = OnsetPlaybackSmoke.Wav();
            SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 2).CopyTo(wav.AsSpan(44));
            return Task.FromResult<byte[]?>(wav);
        };
        var config = new TtsConfig { Engine = "voicevox", VoicevoxApi = "http://127.0.0.1:50021", VoicevoxSpeakerId = "26",
            PiperPlusExePath = exe, PiperPlusModelPath = model,
            OutputDeviceId = "fixture-output", OutputDeviceName = "fixture-output", MonitorVolumePercent = 0 };
        router.SetConfig(config);
        router.PrepareOnset("ja");
        await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        check("prepare alone neither plays nor starts a body", calls == 5 && !playback.IsPlaying && backend.Bodies == 0);
        var handler = router.ConfigureOnset(true, "ja", () => true, true)!;
        handler(1);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check("monitor zero uses selected output without monitor", primary.GetConfig().OutputDeviceId == "fixture-output" && backend.FillerMonitor == 0 && backend.MainActive && !backend.MonitorActive);
        check("monitor zero hands off to body on the same backend", (await router.SpeakAsync("body", "piper_plus", null, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Success &&
            backend.BodyMonitor == 0 && !backend.MainActive && !backend.MonitorActive);

        backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler(2);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var beforeChange = calls;
        config.MonitorVolumePercent = 40;
        using (router.BeginOnsetRouteChange(config))
        {
            check("monitor change drains the prior owner before applying settings", !backend.MainActive && !backend.MonitorActive);
            router.SetConfig(config);
        }
        router.RefreshOnset(() => true, true);
        check("monitor-only change preserves the five prepared clips", calls == beforeChange &&
            JsonSerializer.SerializeToElement(router.GetOnsetStatus()).GetProperty("prepared").GetInt32() == 5);
        backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler(3);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check("positive monitor uses selected output and matching volume", primary.GetConfig().OutputDeviceId == "fixture-output" &&
            backend.FillerMonitor == 40 && backend.MainActive && backend.MonitorActive);
        check("positive monitor body handoff drains both outputs and retains body volume", (await router.SpeakAsync("body", "piper_plus", null, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Success &&
            backend.BodyMonitor == 40 && !backend.MainActive && !backend.MonitorActive && !router.IsSpeakingForRealtimeBody);
        backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler(4);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var previousId = backend.FillerId;
        var previousCancels = backend.Cancels;
        backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler(5);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check("new utterance replaces both old outputs under a new owner", backend.Cancels == previousCancels + 1 &&
            backend.FillerId != previousId && backend.MainActive && backend.MonitorActive);
        config.OutputDeviceId = "fixture-new-output"; config.OutputDeviceName = "fixture-new-output";
        using (router.BeginOnsetRouteChange(config))
        {
            check("output generation change drains both old outputs", !backend.MainActive && !backend.MonitorActive);
            router.SetConfig(config);
        }
        check("output generation change invalidates the old pool", JsonSerializer.SerializeToElement(router.GetOnsetStatus()).GetProperty("prepared").GetInt32() == 0);
        router.PrepareOnset("ja");
        await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        backend.BlockEntry = true;
        backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler(6);
        check("old output reservation reaches the entry barrier", backend.Entry.Wait(TimeSpan.FromSeconds(5)));
        config.MonitorVolumePercent = 60;
        using (router.BeginOnsetRouteChange(config)) router.SetConfig(config);
        backend.AllowEntry.Set();
        check("stale reservation cannot start after monitor generation changes", backend.Rejected.Wait(TimeSpan.FromSeconds(5)) &&
            !backend.Started.Task.IsCompleted && !backend.MainActive && !backend.MonitorActive);
        backend.BlockEntry = false;
        router.RefreshOnset(() => true, true);
        backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler(7);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check("new onset uses the new monitor generation", backend.FillerMonitor == 60 && backend.MonitorActive);
        router.StopOnset();
        check("Stop drains both outputs under the filler owner", !backend.MainActive && !backend.MonitorActive && !playback.IsPlaying);

        // A monitor update while preparation is in flight must not restart it.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        router.OnsetSynthesizeForTest = async (_, text, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            var wav = OnsetPlaybackSmoke.Wav();
            SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 2).CopyTo(wav.AsSpan(44));
            return wav;
        };
        try
        {
            router.PrepareOnset("ja");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var preparation = router.OnsetPreparationCompletion;
            config.MonitorVolumePercent = 70;
            using (router.BeginOnsetRouteChange(config)) router.SetConfig(config);
            release.TrySetResult();
            await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            check("monitor-only change preserves in-flight preparation without playback", calls == 5 && !playback.IsPlaying &&
                JsonSerializer.SerializeToElement(router.GetOnsetStatus()).GetProperty("prepared").GetInt32() == 5);
        }
        finally { release.TrySetResult(); }
    }

    private const string MobfyOptions = "{\"engine\":\"rvc_onnx\",\"modelPath\":\"fixture-rvc\",\"speakerId\":2,\"f0ShiftSemitones\":1,\"mode\":\"rvc_raw\",\"useGpu\":true}";
    private const string MobfyIdle = "{\"ok\":true,\"running\":true,\"conversionRuntimeAvailable\":true,\"onnx\":{\"loaded\":true},\"ringBufferFill\":0,\"vad\":{\"enabled\":true,\"speechActive\":false,\"laughAsrPending\":false},\"rtRvc\":{\"active\":true,\"asrGateActive\":false,\"asrGatePendingSamples\":0,\"postRingFill\":0}}";

    private static async Task MobfyFiller(Action<string, bool> check)
    {
        using var logging = LoggerFactory.Create(_ => { });
        var logger = logging.CreateLogger("MobfyOnsetSmoke");
        var gate = new object();
        using var playback = new TtsPlaybackService(new Backend());
        await using var router = new TtsEngineRouter(new NullPrimaryTtsService(), playback,
            voicevoxService: new VoicevoxTtsService("http://127.0.0.1:1", 26));
        var config = new TtsConfig { Engine = "voicevox", VoicevoxApi = "http://127.0.0.1:1", VoicevoxSpeakerId = "26" };
        router.OnsetSynthesizeForTest = (_, text, _) =>
        {
            var wav = OnsetPlaybackSmoke.Wav();
            SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 2).CopyTo(wav.AsSpan(44));
            return Task.FromResult<byte[]?>(wav);
        };
        router.SetConfig(config);
        router.PrepareOnset("ja");
        await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        check("Mobfy raw source snapshot is available while ASR playback is OFF",
            router.TryGetOnsetSourceSnapshot("ja", out var source) && source!.Wavs.Length == 5 &&
            !router.TryGetOnsetSourceSnapshot("en", out _));
        source!.Wavs[0][44] ^= 255;
        router.TryGetOnsetSourceSnapshot("ja", out var copied);
        check("Mobfy source snapshot owns a copy, not the fixed raw source pool", source.Wavs[0][44] != copied!.Wavs[0][44]);
        var sourceKey = copied.Key;
        var native = new MobfyNativeFixture(gate);
        IntPtr live = IntPtr.Zero;
        using var filler = new MobfyOnsetFiller(router, "fixture-not-executed", gate, () => live, logger)
        {
            CreateForTest = native.Create, LoadForTest = native.Load, ProcessForTest = native.Process,
            DestroyForTest = native.Destroy, ExchangeForTest = native.Exchange
        };
        int Prepared() => JsonSerializer.SerializeToElement(filler.GetStatus()).GetProperty("prepared").GetInt32();
        bool Enabled() => JsonSerializer.SerializeToElement(filler.GetStatus()).GetProperty("enabled").GetBoolean();
        await Task.Run(() => { lock (gate) filler.Configure(MobfyOptions, "ja", false); }).WaitAsync(TimeSpan.FromSeconds(5));
        await filler.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        check("Mobfy OFF prewarm uses one temporary context without capture or playback",
            Prepared() == 5 && !Enabled() && native.Creates == 1 && native.Loads == 1 && native.Processes == 5 &&
            native.Destroys == 1 && !playback.IsPlaying && native.NoSessionLock &&
            native.LastOptions!.Contains("\"wavOutputEnabled\":false") && native.LastOptions.Contains("\"useGpu\":true"));
        check("Mobfy mono PCM is resampled to 48k without modifying body WAV",
            MobfyOnsetFiller.DecodeSource(OnsetPlaybackSmoke.Wav()) is { Length: 6 } &&
            MobfyOnsetFiller.DecodeSource(new byte[12]) == null);
        var partialSample = OnsetPlaybackSmoke.Wav();
        BitConverter.GetBytes(3).CopyTo(partialSample, 40);
        check("Mobfy rejects a trailing partial PCM16 sample", MobfyOnsetFiller.DecodeSource(partialSample) == null);
        var stereo = OnsetPlaybackSmoke.Wav();
        BitConverter.GetBytes((short)2).CopyTo(stereo, 22);
        BitConverter.GetBytes(48000).CopyTo(stereo, 24);
        BitConverter.GetBytes((short)1000).CopyTo(stereo, 44);
        BitConverter.GetBytes((short)-500).CopyTo(stereo, 46);
        check("Mobfy stereo downmix averages channels before conversion", MobfyOnsetFiller.DecodeSource(stereo)!.SequenceEqual(new short[] { 250 }));

        router.StopOnset();
        check("ASR Stop clears raw PCM but leaves Mobfy source identity unchanged",
            !router.TryGetOnsetSourceSnapshot("ja", out _) && router.GetOnsetSourceKey("ja") == sourceKey && Prepared() == 5);
        var prewarmIds = filler.PreparationEvidence.Select(c => c.Id).ToArray();
        filler.ProcessForTest = (handle, input, output) =>
        {
            var result = native.Process(handle, input, output);
            output[0] ^= 1; // Simulate a newly loaded model at the same path.
            return result;
        };
        lock (gate)
        {
            filler.BeforeStart(MobfyOptions, "ja", false);
            live = (IntPtr)1;
            filler.Attach();
        }
        check("Mobfy BeforeStart retires identical-options prewarm and keeps explicit OFF",
            Prepared() == 0 && !Enabled() && native.LastUpdate is { ClipCount: 0, Enabled: 0 } && native.Creates == 1);
        router.PrepareOnset("ja");
        await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        filler.Configure(MobfyOptions, "ja", false);
        filler.ObserveStatus(MobfyIdle);
        await filler.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        check("Mobfy Start reconverts five clips at an unchanged model path without arming OFF",
            Prepared() == 5 && !Enabled() && native.LastUpdate is { ClipCount: 5, Enabled: 0 } &&
            native.Creates == 2 && native.Loads == 2 && native.Processes == 10 && native.Destroys == 2 &&
            !native.LastClipIds.Intersect(prewarmIds).Any() && !playback.IsPlaying && native.NoSessionLock);
        filler.ProcessForTest = native.Process;
        var firstClip = native.LastClipIds[0];
        native.AckClip = firstClip;
        filler.GetStatus();
        filler.Stop();
        check("Mobfy Stop retires native bank and active state", Prepared() == 0 && native.LastUpdate is { ClipCount: 0, Enabled: 0 });
        live = IntPtr.Zero;
        router.PrepareOnset("ja");
        await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        filler.Configure(MobfyOptions, "ja", true);
        await filler.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        check("Mobfy ready prewarm still requires an explicit session Start",
            JsonSerializer.SerializeToElement(filler.GetStatus()).GetProperty("playbackReason").GetString() == "start-required");
        lock (gate)
        {
            filler.BeforeStart(MobfyOptions, "ja", true);
            live = (IntPtr)2;
            filler.Attach();
        }
        check("Mobfy Attach passes only last-start seed until fresh conversion", Prepared() == 0 && native.LastUpdate is { ClipCount: 0, Enabled: 0 } &&
            native.LastUpdate.Value.PreviousClipId == firstClip);
        filler.Configure(MobfyOptions, "ja", false);
        filler.ObserveStatus(MobfyIdle.Replace("\"loaded\":true", "\"loaded\":false"));
        check("Mobfy not-yet-loaded first status cannot restore an older bank", Prepared() == 0 && native.LastUpdate is { ClipCount: 0, Enabled: 0 });
        filler.ObserveStatus(MobfyIdle);
        await filler.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        check("Mobfy OFF received during Start is read before the first possible arm", !Enabled() && native.LastUpdate is { ClipCount: 5, Enabled: 0 });
        filler.Configure(MobfyOptions, "ja", true);
        check("Mobfy loaded session may arm the current bank with latest ON intent", native.LastUpdate is { ClipCount: 5, Enabled: 1 });
        check("Mobfy later OFF/enable exchanges retain the previous actual-start seed before this handle starts",
            native.LastUpdate is { PreviousClipId: var previousStarted } && previousStarted == firstClip);
        var changedRoute = MobfyOptions[..^1] + ",\"outputDeviceId\":\"new-output\"}";
        filler.Configure(changedRoute, "ja", true);
        check("Mobfy retains same-voice PCM but cannot rearm the previous live output after route selection changes",
            Prepared() == 5 && native.LastUpdate is { ClipCount: 0, Enabled: 0 });
        filler.Configure(MobfyOptions, "ja", true);
        check("Mobfy current live route can reuse its matching prepared bank",
            native.LastUpdate is { ClipCount: 5, Enabled: 1 });
        var selected = MobfyOptions.Replace("fixture-rvc", "fixture-new-rvc");
        filler.ObserveStatus(MobfyIdle.Replace("\"speechActive\":false", "\"speechActive\":true"));
        var beforeBusy = native.Processes;
        filler.Configure(selected, "ja", true);
        filler.ObserveStatus("{}");
        filler.ObserveStatus(MobfyIdle.Replace("\"speechActive\":false", "\"speechActive\":true"));
        check("Mobfy unknown/speech status defers preparation and selection never publishes into old live voice",
            native.Processes == beforeBusy && native.LastUpdate is { ClipCount: 0, Enabled: 0 });
        filler.ObserveStatus(MobfyIdle);
        await filler.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        check("Mobfy existing idle status resumes one bounded preparation but keeps old live model silent",
            Prepared() == 5 && native.Processes == beforeBusy + 5 && native.LastUpdate is { ClipCount: 0, Enabled: 0 });
        var beforeSelectedStart = native.Processes;
        filler.BeforeStart(selected, "ja", true);
        live = (IntPtr)3;
        filler.Attach();
        check("Mobfy model-selection prewarm cannot carry over into Start", Prepared() == 0 && native.LastUpdate is { ClipCount: 0, Enabled: 0 });
        filler.ObserveStatus(MobfyIdle);
        await filler.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        check("Mobfy actual Start options freshly convert only the selected model",
            native.LastUpdate is { ClipCount: 5, Enabled: 1 } && native.Processes == beforeSelectedStart + 5);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockOnce = 0;
        filler.ProcessForTest = (handle, input, output) =>
        {
            if (Interlocked.Increment(ref blockOnce) == 1) { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
            return native.Process(handle, input, output);
        };
        filler.WaitingForIdleForTest = () => deferred.TrySetResult();
        filler.Configure(selected.Replace("\"speakerId\":2", "\"speakerId\":3"), "ja", true);
        filler.ObserveStatus(MobfyIdle);
        var old = filler.PreparationCompletion;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            filler.ObserveStatus(MobfyIdle.Replace("\"speechActive\":false", "\"speechActive\":true"));
            release.TrySetResult();
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check("Mobfy speech beginning during one native clip defers the next call", blockOnce == 1 && !old.IsCompleted);
            await Task.Run(() => { lock (gate) filler.BeforeStart(selected, "ja", false); }).WaitAsync(TimeSpan.FromSeconds(5));
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            check("Mobfy BeforeStart cancels old-generation publication and preserves OFF intent", Prepared() == 0 && !Enabled());
        }
        finally { release.TrySetResult(); }

        // A non-cancellable native inference owns its temporary context until
        // completion. Neither Stop nor BeforeStart may join or destroy it.
        filler.Stop();
        live = IntPtr.Zero;
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        filler.ProcessForTest = (handle, input, output) =>
        {
            entered.TrySetResult(); release.Task.GetAwaiter().GetResult();
            return native.Process(handle, input, output);
        };
        filler.Configure(MobfyOptions, "ja", true);
        old = filler.PreparationCompletion;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var destroys = native.Destroys;
            await Task.Run(() => { lock (gate) filler.BeforeStart(MobfyOptions, "ja", false); }).WaitAsync(TimeSpan.FromSeconds(5));
            check("Mobfy BeforeStart does not wait for or destroy in-flight native processing", !old.IsCompleted && native.Destroys == destroys);
            filler.Stop();
            release.TrySetResult();
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            check("Mobfy late native result after Stop cannot publish and is destroyed exactly once", Prepared() == 0 && native.Destroys == destroys + 1);
        }
        finally { release.TrySetResult(); }

        foreach (var kind in new[] { "passthrough", "silence", "failure" })
        {
            filler.ProcessForTest = (_, input, output) =>
            {
                if (kind != "silence") input.CopyTo(output, 0);
                return new() { Success = kind != "failure", RawJson = kind == "passthrough" ? "{\"vcApplied\":false}" : "{\"vcApplied\":true}" };
            };
            filler.Configure(MobfyOptions, "ja", false);
            await filler.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            check("Mobfy rejects converted bank without valid RVC output: " + kind, Prepared() == 0 && !Enabled());
            filler.Stop();
        }
        filler.ProcessForTest = native.Process;
        filler.Configure(MobfyOptions, "ja", false);
        await filler.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        config.VoicevoxSpeakerId = "4";
        router.SetConfig(config);
        check("Mobfy actual source voice change retires converted cache immediately", Prepared() == 0);
        filler.BeforeStart(MobfyOptions, "ja", false);
        check("Mobfy source mismatch cannot reuse previous voice bank", Prepared() == 0);
        MobfyAbi(check, gate);
        MobfyOnsetFiller.TryPreparationOptions(MobfyOptions, "ja", out _, out var key, out var configuration);
        var route = MobfyOptions[..^1] + ",\"inputDeviceId\":\"other\",\"monitorVolumePercent\":80,\"wavOutputEnabled\":true,\"wavOutputDirectory\":\"elsewhere\"}";
        MobfyOnsetFiller.TryPreparationOptions(route, "ja", out var safe, out var routeKey, out var changedConfiguration);
        check("Mobfy routing/storage invalidates playback configuration but not voice cache identity",
            key == routeKey && configuration != changedConfiguration && safe.Contains("\"wavOutputEnabled\":false"));
        MobfyOnsetFiller.TryPreparationOptions(MobfyOptions[..^1] + ",\"outputVolumePercent\":140,\"sampleRate\":0}", "ja", out _, out var defaultsKey, out _);
        check("Mobfy canonical identity normalizes omitted native voice defaults", key == defaultsKey);
        MobfyOnsetFiller.TryPreparationOptions(MobfyOptions.Replace("\"speakerId\":2", "\"speakerId\":3"), "ja", out _, out var otherSpeaker, out _);
        MobfyOnsetFiller.TryPreparationOptions(MobfyOptions.Replace("\"useGpu\":true", "\"useGpu\":false"), "ja", out _, out var otherBackend, out _);
        check("Mobfy canonical identity keeps speaker and hardware backend distinct", key != otherSpeaker && key != otherBackend);
        check("Mobfy invalid selection or non-48k session cannot publish",
            !MobfyOnsetFiller.TryPreparationOptions(MobfyOptions[..^1] + ",\"sampleRate\":16000}", "ja", out _, out _, out _) &&
            !MobfyOnsetFiller.TryPreparationOptions("{\"engine\":\"rvc_onnx\",\"engine\":\"passthrough\"}", "ja", out _, out _, out _));
        check("Mobfy invalid status is fail-closed and actual pending ASR blocks preparation",
            MobfyOnsetFiller.IsIdleStatus(MobfyIdle) && !MobfyOnsetFiller.IsIdleStatus("[]") &&
            !MobfyOnsetFiller.IsIdleStatus(MobfyIdle.Replace("\"laughAsrPending\":false", "\"laughAsrPending\":true")));
    }

    private static void MobfyAbi(Action<string, bool> check, object gate)
    {
        using var logging = LoggerFactory.Create(_ => { });
        var native = new MobfyNativeFixture(gate);
        var clips = Enumerable.Range(1, 5).Select(i => new NativeLibraryBridge.VcFillerPcm((ulong)i, new short[] { (short)i, -2 })).ToArray();
        check("Mobfy v1 x64 ABI is exactly clip24/update48/snapshot64", NativeLibraryBridge.VcFillerAbiSupported);
        lock (gate)
        {
            check("Mobfy pinned bank exposes only synchronous PCM and all five unique IDs",
                NativeLibraryBridge.TryVoiceConversionFillerExchange((IntPtr)10, new(1, true, clips), out _, native.Exchange) &&
                native.LastClipIds.SequenceEqual(new ulong[] { 1, 2, 3, 4, 5 }) &&
                native.LastPcmCopies[4].SequenceEqual(new short[] { 5, -2 }));
            check("Mobfy status-only ABI passes NULL update",
                NativeLibraryBridge.TryVoiceConversionFillerExchange((IntPtr)10, null, out _, native.Exchange) && native.StatusCalls > 0);
            check("Mobfy rejects invalid bank ID/sample count/generation before native",
                !NativeLibraryBridge.TryVoiceConversionFillerExchange((IntPtr)10, new(0, true, clips), out _, native.Exchange) &&
                !NativeLibraryBridge.TryVoiceConversionFillerExchange((IntPtr)10, new(2, true, [clips[0], clips[0]]), out _, native.Exchange) &&
                !NativeLibraryBridge.TryVoiceConversionFillerExchange((IntPtr)10, new(2, true, [new(0, [1])]), out _, native.Exchange) &&
                !NativeLibraryBridge.TryVoiceConversionFillerExchange((IntPtr)10, new(2, true, [new(1, new short[144001])]), out _, native.Exchange));
            int Missing(IntPtr h, IntPtr u, ref NativeLibraryBridge.VcFillerSnapshot s) => throw new EntryPointNotFoundException("fixture");
            check("Mobfy missing optional native entrypoint silently returns false",
                !NativeLibraryBridge.TryVoiceConversionFillerExchange((IntPtr)10, null, out _, Missing));
            int Old(IntPtr h, IntPtr u, ref NativeLibraryBridge.VcFillerSnapshot s) => -2;
            check("Mobfy native old-generation rejection cannot be treated as publication",
                !NativeLibraryBridge.TryVoiceConversionFillerExchange((IntPtr)10, new(1, true, clips), out _, Old));
        }
    }

    private static async Task MobfyRealPreparation(Action<string, bool> check, string scratch, string root)
    {
        var nativeExe = Environment.GetEnvironmentVariable("MOBMATE_MOBFY_FILLER_NATIVE_EXE");
        var runtime = Environment.GetEnvironmentVariable("MOBMATE_MOBFY_FILLER_ORT");
        if (string.IsNullOrWhiteSpace(nativeExe) || !File.Exists(nativeExe) ||
            string.IsNullOrWhiteSpace(runtime) || !File.Exists(runtime))
            throw new ArgumentException("Explicit existing native executable and RVC ONNX Runtime paths are required");
        // Keep the RVC runtime explicit; do not probe or substitute ASR/Piper
        // runtimes. No process environment or DLL placement is modified here.
        NativeLibrary.Load(Path.GetFullPath(runtime));
        NativeLibraryBridge.SetNativePath(Path.GetFullPath(nativeExe));
        var hashes = new[]
        {
            "3570EDB718A759E537D15867A848D37A1384A77521352B4F1EDB150A6AEC8E01",
            "FC5159E63C73DC3DBA5F878B1D7B6080F18A1C989E07975C47EE2C6E4EC05132",
            "16A40BEC8E4472DB42D915A11D93A251536EAC114AA522E5D0C172DAAC17997A",
            "D51A4DCB414B49C6FBCDA8AB349AF3052B739A2D9CEF7420D54F80F57DD6685A",
            "49D5729EF892C9B54D9D3BB39828313AFE58AA4133825079161364C60E36A108"
        };
        var sources = new byte[5][];
        for (var i = 0; i < sources.Length; ++i)
        {
            sources[i] = await File.ReadAllBytesAsync(Path.Combine(root, "src", "MobMate.Core", "Tts", "OnsetAssets",
                $"kokoro-en-3-af_heart-{i + 1:00}.wav"));
            check("Mobfy permitted non-personal Kokoro source hash: " + (i + 1),
                Convert.ToHexString(SHA256.HashData(sources[i])) == hashes[i]);
        }
        var modelDir = Path.Combine(root, "models", "voiceconv", "tsukuyomi-rvc");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modelDir, "manifest.json")));
        var files = manifest.RootElement.GetProperty("files");
        check("Mobfy real preparation uses the existing selected RVC manifest",
            manifest.RootElement.GetProperty("engine").GetString() == "rvc_onnx" &&
            File.Exists(Path.Combine(modelDir, files.GetProperty("model").GetString()!)) &&
            File.Exists(Path.Combine(modelDir, files.GetProperty("contentEncoder").GetString()!)));
        var selectedOptions = System.Text.Json.Nodes.JsonNode.Parse(MobfyOnsetSelection.Options(new MobMate.Core.UiSettingsDocument
        {
            Settings = new() { ["mobmate.vc.modelId"] = "tsukuyomi-rvc" }
        }))!.AsObject();
        selectedOptions["engine"] = "rvc_onnx";
        selectedOptions["modelPath"] = modelDir;
        selectedOptions["vcModelFile"] = files.GetProperty("model").GetString();
        selectedOptions["contentEncoderFile"] = files.GetProperty("contentEncoder").GetString();
        selectedOptions["f0ModelFile"] = files.GetProperty("f0Model").GetString();
        selectedOptions["onnxRuntimeDllDir"] = Path.GetDirectoryName(runtime);
        var seModelDir = Path.Combine(root, "models", "dtln");
        selectedOptions["seModelDir"] = Directory.Exists(seModelDir) ? seModelDir : "";
        var options = selectedOptions.ToJsonString();
        using var logging = MobMate.Core.AppLogging.CreateLoggerFactory("debug");
        var logger = logging.CreateLogger("MobfyRealPreparation");
        using var playback = new TtsPlaybackService(new Backend());
        await using var router = new TtsEngineRouter(new NullPrimaryTtsService(), playback, kokoroService: new KokoroTtsService());
        var sourceIndex = 0;
        router.OnsetSynthesizeForTest = (_, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<byte[]?>(sources[sourceIndex++].ToArray());
        };
        router.SetConfig(new TtsConfig
        {
            Engine = "kokoro", KokoroModelId = "kokoro-int8-multi-lang-v1_1",
            KokoroModelDir = Path.Combine(scratch, "kokoro-int8-multi-lang-v1_1"), KokoroSpeakerId = 3
        });
        router.PrepareOnset("en");
        await router.OnsetPreparationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        check("Mobfy real preparation consumes five copied selected-voice source WAVs",
            sourceIndex == 5 && router.TryGetOnsetSourceSnapshot("en", out var snapshot) && snapshot!.Wavs.Length == 5);
        var gate = new object();
        var liveHandle = IntPtr.Zero;
        using var filler = new MobfyOnsetFiller(router, nativeExe, gate, () => liveHandle, logger);
        filler.Configure(options, "en", false);
        await filler.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(60));
        var evidence = filler.PreparationEvidence;
        check("Mobfy real temporary create/load/process validates vcApplied, full PCM, nonzero and five distinct IDs",
            evidence.Length == 5 && evidence.Select(c => c.Id).Distinct().Count() == 5 &&
            evidence.All(c => c.Id != 0 && c.Samples is > 0 and <= 144000 && c.Nonzero) && !playback.IsPlaying);
        lock (gate)
        {
            try
            {
                filler.BeforeStart(options, "en", false);
                check("Mobfy real ABI creates a separate stopped handle without capture",
                    NativeLibraryBridge.VoiceConversionCreate(nativeExe, options, out liveHandle).Success);
                filler.Attach();
                check("Mobfy managed/native ABI owns five prepared clips without arming or playing",
                    NativeLibraryBridge.TryVoiceConversionFillerExchange(liveHandle, null, out var nativeBank) &&
                    nativeBank.PreparedCount == 5 && nativeBank.Active == 0 && nativeBank.StartedSerial == 0);
                filler.Stop();
                check("Mobfy real ABI Stop clears only the filler bank",
                    NativeLibraryBridge.TryVoiceConversionFillerExchange(liveHandle, null, out nativeBank) &&
                    nativeBank.PreparedCount == 0 && nativeBank.Active == 0);
            }
            finally
            {
                NativeLibraryBridge.VoiceConversionDestroy(liveHandle);
                liveHandle = IntPtr.Zero;
            }
        }
        logger.LogInformation("[VC filler] device-free real preparation mode=Mobfy-default backend=CPU source=Kokoro/en/3 rvc=tsukuyomi-rvc sampleRate=48000 evidence={Evidence}; no playback, no persisted converted assets, live GPU/resource contention unverified",
            JsonSerializer.Serialize(evidence.Select(c => new { c.Id, c.Samples, c.Nonzero })));
    }

    private sealed class MobfyNativeFixture(object gate)
    {
        internal int Creates, Loads, Processes, Destroys, StatusCalls;
        internal bool NoSessionLock = true;
        internal string? LastOptions;
        internal NativeLibraryBridge.VcFillerUpdate? LastUpdate;
        internal ulong[] LastClipIds = [];
        internal short[][] LastPcmCopies = [];
        internal ulong AckClip;
        private IntPtr _lastHandle;
        internal IntPtr Create(string options) { NoSessionLock &= !Monitor.IsEntered(gate); LastOptions = options; Creates++; return (IntPtr)(1000 + Creates); }
        internal bool Load(IntPtr handle) { NoSessionLock &= !Monitor.IsEntered(gate); Loads++; return true; }
        internal NativeLibraryResult Process(IntPtr handle, short[] input, short[] output)
        {
            NoSessionLock &= !Monitor.IsEntered(gate);
            Processes++;
            input.CopyTo(output, 0);
            return new() { Success = true, RawJson = "{\"vcApplied\":true}" };
        }
        internal void Destroy(IntPtr handle) { NoSessionLock &= !Monitor.IsEntered(gate); Destroys++; }
        internal int Exchange(IntPtr handle, IntPtr update, ref NativeLibraryBridge.VcFillerSnapshot snapshot)
        {
            if (!Monitor.IsEntered(gate)) throw new InvalidOperationException("Live exchange must own host session lock");
            if (_lastHandle != handle) { _lastHandle = handle; AckClip = 0; }
            if (update == IntPtr.Zero) StatusCalls++;
            else
            {
                var value = Marshal.PtrToStructure<NativeLibraryBridge.VcFillerUpdate>(update);
                LastUpdate = value;
                LastClipIds = new ulong[value.ClipCount];
                LastPcmCopies = new short[value.ClipCount][];
                for (var i = 0; i < value.ClipCount; ++i)
                {
                    var clip = Marshal.PtrToStructure<NativeLibraryBridge.VcFillerClip>(value.Clips + i * 24);
                    if (clip.NumSamples is < 1 or > 144000 || clip.Reserved != 0)
                        throw new InvalidOperationException("Invalid pinned clip");
                    LastClipIds[i] = clip.Id;
                    LastPcmCopies[i] = new short[clip.NumSamples];
                    Marshal.Copy(clip.Samples, LastPcmCopies[i], 0, (int)clip.NumSamples);
                }
                snapshot.Generation = value.Generation;
                snapshot.PreparedCount = value.ClipCount;
            }
            snapshot.Size = 64; snapshot.Version = 1;
            snapshot.StartedSerial = AckClip == 0 ? 0u : 1u;
            snapshot.StartedClipId = AckClip;
            return 0;
        }
    }

    private sealed class RecordingPrimary : IPrimaryTtsService
    {
        private TtsConfig _config = new();
        internal bool Available;
        internal int SpeakCalls;
        public bool IsAvailable => Available;
        public string EngineName => "fixture";
        public TtsResult SpeakAsync(string text, bool interrupt = true) { SpeakCalls++; return TtsResult.Fail("fixture"); }
        public TtsResult StopAsync() => TtsResult.Fail("fixture");
        public void SetConfig(TtsConfig config) => _config = JsonSerializer.Deserialize<TtsConfig>(JsonSerializer.Serialize(config))!;
        public TtsConfig GetConfig() => _config;
        public TtsStatus GetStatus() => new() { Available = false, IsSpeaking = false };
        public void Dispose() { }
    }

    private sealed class Backend : IPcmPlaybackBackend
    {
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _cancel = new(), _exited = new(true);
        internal readonly ManualResetEventSlim Entry = new(), AllowEntry = new(), Rejected = new();
        internal volatile bool BlockEntry;
        private ulong _next, _active, _cancelled;
        internal int Cancels, Bodies, BodyRate, BodyChannels, FillerMonitor, BodyMonitor;
        internal ulong FillerId;
        internal volatile bool MainActive, MonitorActive;
        internal short[]? BodySamples;
        internal TaskCompletionSource<string> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsFiller() => true;
        public ulong NextFillerId() { lock (_gate) return ++_next; }
        public NativeLibraryResult PlayFiller(ulong id, short[] pcm, int rate, int channels, int monitor, Action started)
        {
            if (BlockEntry)
            {
                Entry.Set();
                if (!AllowEntry.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("fixture entry did not release");
            }
            lock (_gate)
            {
                if (id <= _cancelled) { Rejected.Set(); return new() { Success = false }; }
                _active = id; _cancel.Reset(); _exited.Reset();
                FillerId = id; FillerMonitor = monitor; MainActive = true; MonitorActive = monitor > 0;
            }
            try { started(); Started.TrySetResult(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pcm.AsSpan())))); _cancel.Wait(TimeSpan.FromSeconds(5)); return new() { Success = true }; }
            finally { lock (_gate) { _active = 0; MainActive = false; MonitorActive = false; } _exited.Set(); }
        }
        public NativeLibraryResult CancelFiller(ulong id)
        {
            bool active;
            lock (_gate) { _cancelled = Math.Max(_cancelled, id); active = _active == id; if (active) { Cancels++; _cancel.Set(); } }
            if (active && !_exited.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("fixture output did not exit");
            return new() { Success = true };
        }
        public NativeLibraryResult Play(short[] samples, int rate, int channels, int monitor)
        { Bodies++; BodySamples = samples; BodyRate = rate; BodyChannels = channels; BodyMonitor = monitor; return new() { Success = true }; }
        public NativeLibraryResult Stop() => new() { Success = true };
    }
}
