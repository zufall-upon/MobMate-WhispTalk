using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MobMate.Core.Asr;

// Explicit opt-in using already pinned private assets. Never downloads a model
// and never treats this manager test as normal capture/UI or Joint-value proof.
internal static class QwenRealSmoke
{
    public static async Task<int> Run(string output, string store)
    {
        var root = Path.GetFullPath("temp") + Path.DirectorySeparatorChar;
        output = Path.GetFullPath(output); store = Path.GetFullPath(store);
        if (!output.StartsWith(root, StringComparison.Ordinal) || !store.StartsWith(root, StringComparison.Ordinal) || Directory.Exists(output))
            throw new ArgumentException("New output and existing asset store must be under private temp/");
        var japaneseAudio = Environment.GetEnvironmentVariable("MOBMATE_QWEN_JA_WAV");
        var englishAudio = Environment.GetEnvironmentVariable("MOBMATE_QWEN_EN_WAV");
        if (string.IsNullOrWhiteSpace(japaneseAudio) || !File.Exists(japaneseAudio) ||
            string.IsNullOrWhiteSpace(englishAudio) || !File.Exists(englishAudio))
            throw new ArgumentException("Set MOBMATE_QWEN_JA_WAV and MOBMATE_QWEN_EN_WAV to the existing evaluation audio.");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var rows = new List<object>();
        var clock = Stopwatch.StartNew();
        string? error = null;
        var handler = new DenyNetwork();
        void Check(string name, bool value)
        {
            if (!value) throw new InvalidDataException(name);
            checks.Add(name);
        }
        try
        {
            if (!SpeechLinuxRuntime.Available) throw new PlatformNotSupportedException();
            using var http = new HttpClient(handler);
            var installer = new AddonPackageInstaller(http);
            var engine = AddonCatalog.Engines.Single(e => e.Id == AddonCatalog.QwenId);
            var model = engine.Models.Single();
            var directory = Directory.GetDirectories(Path.Combine(store, "packages")).Single();
            var package = new PreparedAddonPackage(directory, Path.Combine(directory, "runtime", "speech"), Path.Combine(directory, model.FileName));
            Check("only Qwen has Linux runtime", engine.Platform == "linux-x64" && AddonCatalog.Engines.Where(e => e.Id != engine.Id).All(e => e.Platform == "windows-x64"));
            await installer.VerifyAsync(store, engine, model, package);
            Check("private evaluation package passes canonical full verification", true);

            // Preserve the old evaluation tree; regenerate the same three files
            // through the extraction implementation actually used by installer.
            Directory.Move(Path.Combine(directory, "runtime"), Path.Combine(output, "evaluation-runtime"));
            Directory.CreateDirectory(Path.Combine(directory, "runtime"));
            await SpeechLinuxRuntime.ExtractAsync(Path.Combine(directory, "runtime.zip"), Path.Combine(directory, "runtime"), CancellationToken.None);
            await installer.VerifyAsync(store, engine, model, package);
            var reused = await installer.PrepareAsync(store, engine, model);
            Check("installer extraction and canonical reuse keep the single model", reused == package && Directory.GetDirectories(Path.Combine(store, "packages")).Length == 1 && handler.Calls == 0);
            Check("verified alias remains absent from disk", !File.Exists(Path.Combine(directory, "runtime", "libspeech.so")));

            var extra = Path.Combine(directory, "runtime", "libspeech.so");
            try
            {
                await File.WriteAllTextAsync(extra, "unverified alias");
                var rejected = false;
                try { await installer.VerifyAsync(store, engine, model, package); }
                catch (InvalidDataException) { rejected = true; }
                Check("disk alias or unexpected file is rejected", rejected);
            }
            finally { File.Delete(extra); }
            var preservedHeader = Path.Combine(output, "evaluation-runtime", "speech.h");
            var header = await File.ReadAllBytesAsync(preservedHeader);
            try
            {
                header[0] ^= 1;
                await File.WriteAllBytesAsync(preservedHeader, header);
                var rejected = false;
                try { await SpeechLinuxRuntime.VerifyExtractedAsync(Path.Combine(directory, "runtime.zip"), Path.Combine(output, "evaluation-runtime"), CancellationToken.None); }
                catch (InvalidDataException) { rejected = true; }
                Check("same-length extracted content tampering is rejected", rejected);
            }
            finally { header[0] ^= 1; await File.WriteAllBytesAsync(preservedHeader, header); }

            await using (var manager = new AddonLanguageManager(store, http))
            {
                await manager.LoadAsync();
                await manager.InstallAsync(engine.Id, model.Id);
                Check("install alone does not select, enable, consent or start", manager.Profiles.Count == 0 && !manager.Snapshot().Ready);
                var windows = AddonCatalog.Engines.Single(e => e.Id == AddonCatalog.HaasId);
                var rejected = false;
                try { await manager.InstallAsync(windows.Id, windows.Models[0].Id); }
                catch (PlatformNotSupportedException) { rejected = true; }
                Check("Windows-only install is blocked before network", rejected && handler.Calls == 0);
                await manager.ConfigureAsync("ja", engine.Id, model.Id);
                Check("new JA defaults OFF", !manager.Profiles["ja"].Enabled && !manager.Profiles["ja"].JointRequested);
                await manager.ApplyLanguageAsync("ja", probe: true);
                Check("real silence probe establishes Ready, not Enabled", manager.Snapshot().Ready && !manager.Snapshot().Enabled);
                await manager.SetConsentAsync("ja", true, true);
                await manager.ConfigureAsync("en", engine.Id, model.Id);
                Check("JA opt-ins never spread to new EN", !manager.Profiles["en"].Enabled && !manager.Profiles["en"].JointRequested);
                await manager.ApplyLanguageAsync("en", probe: true);
                Check("EN real context probe Ready", manager.Snapshot().Ready);
                await manager.SetConsentAsync("en", true, false);
                var saved = await File.ReadAllTextAsync(Path.Combine(store, "language-profiles.json"));
                var inputs = new[] {
                    (Language: "ja", File: japaneseAudio, Expected: "こんにちは"),
                    (Language: "en", File: englishAudio, Expected: "Open the map now."),
                    (Language: "ja", File: japaneseAudio, Expected: "こんにちは") };
                foreach (var input in inputs)
                {
                    var before = await Hash(input.File);
                    await manager.ApplyLanguageAsync(input.Language);
                    var state = manager.Describe();
                    var resolved = manager.ResolveRecognitionEngine("whisper", true);
                    Check("saved profile resolves " + input.Language + " at " + rows.Count, state.State.Ready && state.Resident is { Active: true } && state.Language == input.Language && resolved == (input.Language == "ja" ? "joint_auto" : engine.Id));
                    var (pcm, rate) = ReadPcm(input.File);
                    var existing = new AddonAsrCandidate("none", "", false, -1, "unavailable");
                    var result = await manager.RecognizeAsync(pcm, rate, existing, TimeSpan.FromMinutes(1), joint: input.Language == "ja");
                    Check("real saved-context transcript " + input.Language + " at " + rows.Count,
                        result.Candidate is { Succeeded: true } candidate && candidate.Engine == engine.Id && Normalize(candidate.Text) == Normalize(input.Expected));
                    Check("legacy WAV unchanged " + rows.Count, before == await Hash(input.File));
                    rows.Add(new { input.Language, input.File, input.Expected, resolved, state.Resident, result.Candidate, rate });
                }
                Check("applying languages never rewrites opt-ins", saved == await File.ReadAllTextAsync(Path.Combine(store, "language-profiles.json")));
                Check("quality and Joint value remain unmeasured", manager.Describe().Quality == "not_evaluated" && manager.Describe().JointValue == "not_evaluated");
                await manager.StopAsync();
                Check("manager Stop confirms worker removal", !manager.Snapshot().Ready && manager.Describe().Resident is null);
            }
            await using var reload = new AddonLanguageManager(store, http);
            await reload.LoadAsync();
            Check("reload restores saved JA/EN but never fakes Ready", reload.Profiles["ja"].JointRequested && !reload.Profiles["en"].JointRequested && !reload.Snapshot().Ready);
            Check("no download performed", handler.Calls == 0);
        }
        catch (Exception ex) { error = ex.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            passed = error is null, checks, rows, error, networkCalls = handler.Calls,
            elapsedSeconds = clock.Elapsed.TotalSeconds, scope = "Real Qwen installer/manager; not normal capture/UI or Joint value"
        }, new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }

    private static async Task<string> Hash(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
    private static string Normalize(string text) => new(text.Normalize().ToLowerInvariant().Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToArray());
    private static (byte[] Pcm, int Rate) ReadPcm(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException();
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException();
        var rate = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var name = new string(reader.ReadChars(4)); var size = reader.ReadInt32(); var start = reader.BaseStream.Position;
            if (size < 0 || size > reader.BaseStream.Length - start) throw new InvalidDataException();
            if (name == "fmt ")
            {
                if (reader.ReadInt16() != 1 || reader.ReadInt16() != 1) throw new InvalidDataException();
                rate = reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt16();
                if (reader.ReadInt16() != 16) throw new InvalidDataException();
            }
            else if (name == "data" && rate > 0) return (reader.ReadBytes(size), rate);
            reader.BaseStream.Position = start + size + (size & 1);
        }
        throw new InvalidDataException("No PCM16 data");
    }
    private sealed class DenyNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("This real fixture forbids downloads"); }
    }
}
