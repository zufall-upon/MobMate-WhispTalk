extern alias SteamCore;
using System.Runtime.InteropServices;
using System.Text.Json;
using SteamCore::MobMate.Core.Realtime;
using Xunit;

namespace MobMate.Steam.Tests;

public sealed class RealtimeFinalOutputTests
{
    private static RealtimePollResult Poll(string text, string language = "ja") => new()
    {
        State = "completed", Final = text, SelectedText = text, SelectedEngine = "whisper",
        DecisionReason = "existing", Language = language,
        FinalPcm16Le = new byte[3200], FinalPcmSampleRate = 16000
    };

    [Theory]
    [InlineData("haas_ja")]
    [InlineData("parakeet_ja")]
    [InlineData("qwen3_asr_ja")]
    [InlineData("nemotron_3_5")]
    public void LiveAndReplayParserPreserveCjkFormattingPcmAndCandidateMetadata(string engine)
    {
        var json = JsonSerializer.Serialize(new
        {
            state = "completed", final = "日 本 語", selected_text = "日 本 語", selected_engine = engine,
            candidates = new[] { new { engine, text = "日 本 語", success = true, wallMs = 17, confidence_score = (double?)null } },
            finalPcm16Base64 = Convert.ToBase64String(new byte[3200]), finalPcmSampleRate = 16000
        });
        var poll = RealtimeSessionController.ParseNativePoll(json);
        Assert.Equal("日本語", poll.SelectedText);
        Assert.Equal("日本語", poll.Final);
        Assert.Equal(3200, poll.FinalPcm16Le.Length);
        Assert.Equal("日 本 語", poll.AddonCandidate!.Value.GetProperty("text").GetString());
        Assert.Equal(engine, poll.AddonCandidate.Value.GetProperty("engine").GetString());
        Assert.Equal(engine, poll.SelectedEngine);
        Assert.Equal(17, poll.AddonCandidate.Value.GetProperty("wallMs").GetInt32());
        Assert.Equal(JsonValueKind.Null, poll.AddonCandidate.Value.GetProperty("confidence_score").ValueKind);
        var latin = RealtimeSessionController.ParseNativePoll("{\"final\":\"two words\",\"selected_text\":\"two words\"}");
        Assert.Equal("two words", latin.SelectedText);
    }

    [Fact]
    public void SharedOutputUsesTheExistingSenseVoiceThenPhoneticOrder()
    {
        var calls = new List<string>();
        var input = Poll("あ");
        var output = RealtimeFinalOutputProcessor.Process(input, "joint_auto", "ja",
            (poll, lang) => { calls.Add("sense_voice:" + lang); Assert.Same(input, poll); return "補完"; },
            (poll, text) => { calls.Add("phonetic:" + text); return new RealtimeFinalOverride("最終", "phoneme_ctc", "phonetic_assist_filler"); });
        Assert.Equal(new[] { "sense_voice:ja", "phonetic:補完" }, calls);
        Assert.Equal("最終", output.Text);
        Assert.Equal("phoneme_ctc", output.Engine);
        Assert.Equal("phonetic_assist_filler", output.DecisionReason);
        Assert.Equal("補完", output.SenseVoiceText);
        Assert.Equal("あ", input.SelectedText);
    }

    [Fact]
    public void ExistingNativeResultsAndNonJapaneseNeighborsDoNotGainSenseVoice()
    {
        string? Never(RealtimePollResult _, string __) => throw new Exception("unexpected SenseVoice");
        var native = RealtimeFinalOutputProcessor.Process(Poll("既存"), "joint_auto", "ja", Never, null);
        Assert.Equal("既存", native.Text);
        Assert.Equal("whisper", native.Engine);
        Assert.Equal("existing", native.DecisionReason);
        var english = RealtimeFinalOutputProcessor.Process(Poll("a", "en"), "joint_auto", "en", Never, null);
        Assert.Equal("a", english.Text);
        var chinese = RealtimeFinalOutputProcessor.Process(Poll("字", "zh"), "joint_auto", "zh", Never, null);
        Assert.Equal("字", chinese.Text);
        var haas = RealtimeFinalOutputProcessor.Process(Poll("あ"), "haas_ja", "ja", Never, null);
        Assert.Equal("あ", haas.Text);
        var regionalChinese = RealtimeFinalOutputProcessor.Process(Poll("字", "zh-TW"), "joint_auto", "zh-TW",
            (_, _) => "補完", null);
        Assert.Equal("補完", regionalChinese.Text);
    }

    [Fact]
    public void EmptyGateResultCannotBeReplacedByManagedRecognizers()
    {
        var result = RealtimeFinalOutputProcessor.Process(Poll(""), "joint_auto", "ja",
            (_, _) => throw new Exception("unexpected SenseVoice"),
            (_, _) => throw new Exception("unexpected phonetic"));
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void ReplayCallbackUsesRowContextAndBoundsUtf8WithoutLeakingExceptions()
    {
        var json = Marshal.StringToCoTaskMemUTF8("{\"state\":\"completed\",\"final\":\"日 本\",\"selected_text\":\"日 本\",\"selected_engine\":\"whisper\"}");
        var lang = Marshal.StringToCoTaskMemUTF8("ja");
        var scene = Marshal.StringToCoTaskMemUTF8("talk_clean");
        var text = Marshal.AllocHGlobal(64);
        var engine = Marshal.AllocHGlobal(64);
        var reason = Marshal.AllocHGlobal(64);
        try
        {
            var callback = RealtimeReplayFinalizer.Create((poll, language, effectiveScene) =>
            {
                Assert.Equal("日本", poll.SelectedText);
                Assert.Equal("ja", language);
                Assert.Equal("talk_clean", effectiveScene);
                return new RealtimeFinalOutput("仕上げ", "phoneme_ctc", "phonetic_assist_filler", null, "仕上げ");
            });
            Assert.Equal(0, callback(json, lang, scene, text, 64, engine, 64, reason, 64));
            Assert.Equal("仕上げ", Marshal.PtrToStringUTF8(text));
            Assert.Equal("phoneme_ctc", Marshal.PtrToStringUTF8(engine));
            Assert.NotEqual(0, callback(json, lang, scene, text, 4, engine, 64, reason, 64));
            var throwing = RealtimeReplayFinalizer.Create((_, _, _) => throw new IOException("fixture failure"));
            Assert.NotEqual(0, throwing(json, lang, scene, text, 64, engine, 64, reason, 64));
        }
        finally
        {
            Marshal.FreeCoTaskMem(json); Marshal.FreeCoTaskMem(lang); Marshal.FreeCoTaskMem(scene);
            Marshal.FreeHGlobal(text); Marshal.FreeHGlobal(engine); Marshal.FreeHGlobal(reason);
        }
    }
}
