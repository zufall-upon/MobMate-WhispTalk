using MobMate.Core;
using MobMate.Core.Native;
using MobMate.Core.Realtime;

var parsed = Args.Parse(args);

if (parsed.Command is null or "version")
{
    Console.WriteLine($"MobMate CLI v{MobMateCore.Version}");
    return;
}

if (parsed.Command == "config")
{
    try
    {
        var config = AppConfigLoader.Load(parsed.ConfigPath);
        Console.WriteLine($"NativeExePath   = {config.NativeExePath}");
        Console.WriteLine($"ManifestPath    = {config.DefaultManifestPath}");
        Console.WriteLine($"ModelPath       = {config.DefaultModelPath}");
        Console.WriteLine($"WhisperDllDir   = {config.DefaultWhisperDllDir}");
        Console.WriteLine($"Threads         = {config.DefaultThreads}");
    }
    catch (FormatException ex)
    {
        Console.Error.WriteLine($"Config error: {ex.Message}");
        Environment.Exit(1);
    }
    return;
}

if (parsed.Command == "native-version")
{
    var config = AppConfigLoader.Load(parsed.ConfigPath);
    RunNative(config, ["version"]);
    return;
}

if (parsed.Command == "native-dll-version")
{
    var config = AppConfigLoader.Load(parsed.ConfigPath);
    var result = NativeLibraryBridge.GetVersion(config.NativeExePath);
    if (result.Success)
    {
        Console.WriteLine($"Native DLL: {result.Message}");
    }
    else
    {
        Console.Error.WriteLine($"Native DLL error: {result.Message}");
        Environment.Exit(1);
    }
    return;
}

if (parsed.Command == "transcribe-dll")
{
    var config = AppConfigLoader.Load(parsed.ConfigPath);
    if (string.IsNullOrEmpty(parsed.InputPath))
    {
        Console.Error.WriteLine("transcribe-dll: --input <wav> is required");
        Environment.Exit(1);
    }
    var modelPath = parsed.ModelPath ?? config.DefaultModelPath;
    var dllDir = parsed.WhisperDllDir ?? config.DefaultWhisperDllDir;
    var result = NativeLibraryBridge.TranscribeWav(
        config.NativeExePath,
        parsed.InputPath,
        modelPath,
        dllDir,
        "ja",
        config.DefaultThreads);
    if (result.Success)
    {
        Console.WriteLine($"Transcript: {result.Transcript}");
        Console.WriteLine($"Latency: {result.LatencyMs}ms");
    }
    else
    {
        Console.Error.WriteLine($"Transcribe error ({result.ErrorStage}): {result.Message}");
        Environment.Exit(1);
    }
    return;
}

if (parsed.Command == "bench-native")
{
    var config = AppConfigLoader.Load(parsed.ConfigPath);
    var outPath = parsed.OutPath ?? "out/bench/results_csharp_wrapper.jsonl";
    var benchArgs = new[]
    {
        "bench",
        "--manifest", config.DefaultManifestPath,
        "--out", outPath,
        "--model", config.DefaultModelPath,
        "--whisper-dll-dir", config.DefaultWhisperDllDir,
        "--threads", config.DefaultThreads.ToString(),
    };
    Console.Error.WriteLine($"Running native benchmark...");
    RunNative(config, benchArgs);
    return;
}

if (parsed.Command == "realtime-test")
{
    var config = AppConfigLoader.Load(parsed.ConfigPath);
    try
    {
        // Build options JSON for ASR engine
        string optionsJson = "{}";
        if (!string.IsNullOrEmpty(parsed.WhisperDllDir) && !string.IsNullOrEmpty(parsed.ModelPath))
        {
            optionsJson = $"{{\"whisperDllDir\":\"{parsed.WhisperDllDir}\",\"modelPath\":\"{parsed.ModelPath}\"}}";
            Console.WriteLine($"Loading ASR engine: dll={parsed.WhisperDllDir}, model={parsed.ModelPath}");
        }

        using var session = RealtimeSessionController.Create(config.NativeExePath, optionsJson);

        Console.WriteLine("=== realtime-test ===");

        // Poll initial state
        var r1 = session.Poll();
        Console.WriteLine($"[1] create+poll: state={r1.State}, vad={r1.VadState}, samples={r1.CaptureSamples}, rms={r1.CaptureRms:F1}, error={r1.Error ?? "(none)"}");
        if (!string.IsNullOrEmpty(r1.RawJson))
        {
            Console.WriteLine($"    raw: {r1.RawJson}");
        }

        // Start
        RealtimePollResult r2;
        try
        {
            session.Start();
        }
        catch (Exception startEx)
        {
            r2 = session.Poll();
            Console.WriteLine($"[2] start failed: {startEx.Message}");
            Console.WriteLine($"    poll: state={r2.State}, error={r2.Error ?? "(none)"}");
            throw;
        }
        r2 = session.Poll();
        Console.WriteLine($"[2] start+poll: state={r2.State}, vad={r2.VadState}, samples={r2.CaptureSamples}, rms={r2.CaptureRms:F1}, error={r2.Error ?? "(none)"}");

        // Wait 2 seconds to collect some audio and test VAD
        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(500);
            r2 = session.Poll();
            Console.WriteLine($"[2b.{i}] {i*500}ms: state={r2.State}, vad={r2.VadState}, samples={r2.CaptureSamples}, rms={r2.CaptureRms:F1}, speechCount={r2.VadSpeechCount}");
        }

        // Stop (triggers ASR transcription if engine is loaded)
        Console.WriteLine("Stopping capture (ASR will run on captured PCM)...");
        session.Stop();
        var r3 = session.Poll();
        Console.WriteLine($"[3] stop+poll: state={r3.State}, vad={r3.VadState}, samples={r3.CaptureSamples}, rms={r3.CaptureRms:F1}, speechCount={r3.VadSpeechCount}");
        Console.WriteLine($"    final=\"{r3.Final}\", latency={r3.LatencyMs}ms, error={r3.Error ?? "(none)"}");
        if (!string.IsNullOrEmpty(r3.RawJson))
        {
            Console.WriteLine($"    raw: {r3.RawJson}");
        }

        Console.WriteLine("=== realtime-test OK ===");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"realtime-test failed: {ex.Message}");
        Environment.Exit(1);
    }
    return;
}

Console.Error.WriteLine($"Unknown command: {parsed.Command}");
Environment.Exit(1);

static void RunNative(AppConfig config, string[] subArgs)
{
    var result = NativeCommandRunner.Run(config.NativeExePath, subArgs);
    if (!string.IsNullOrEmpty(result.Stdout))
    {
        Console.Write(result.Stdout);
    }
    if (!string.IsNullOrEmpty(result.Stderr))
    {
        Console.Error.Write(result.Stderr);
    }
    Console.Error.WriteLine($"[exit {result.ExitCode}]");
    if (!result.Success)
    {
        Environment.Exit(result.ExitCode);
    }
}

sealed class Args
{
    public string? Command { get; set; }
    public string? ConfigPath { get; set; }
    public string? OutPath { get; set; }
    public string? InputPath { get; set; }
    public string? WhisperDllDir { get; set; }
    public string? ModelPath { get; set; }

    public static Args Parse(string[] input)
    {
        var result = new Args();
        for (var i = 0; i < input.Length; i++)
        {
            var a = input[i];
          if (a == "--config" && i + 1 < input.Length)
                {
                    result.ConfigPath = input[++i];
                }
                else if (a == "--input" && i + 1 < input.Length)
                {
                    result.InputPath = input[++i];
                }
                else if (a == "--out" && i + 1 < input.Length)
            {
                result.OutPath = input[++i];
            }
            else if (a == "--whisper-dll-dir" && i + 1 < input.Length)
            {
                result.WhisperDllDir = input[++i];
            }
            else if (a == "--model" && i + 1 < input.Length)
            {
                result.ModelPath = input[++i];
            }
            else if (!a.StartsWith("-"))
            {
                result.Command = a;
            }
        }
        return result;
    }
}
