using MobMate.Core.Tts;
using Xunit;

namespace MobMate.Core.Tests;

public sealed class TtsWavOutputStoreTests
{
    [Fact]
    public void WindowsSapiConfig_RoundTripPreservesWavOutputEnabled()
    {
        using var service = new WindowsSapiTtsService();

        service.SetConfig(new TtsConfig { WavOutputEnabled = true });

        Assert.True(service.GetConfig().WavOutputEnabled);
    }

    [Fact]
    public async Task SaveWavAsync_UsesAtomicFinalNameAndKeepsCombinedMaximum()
    {
        var root = Path.Combine(Path.GetTempPath(), "mobmate-wav-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            for (var i = 0; i < 5; i++)
                await File.WriteAllBytesAsync(Path.Combine(root, $"old-mobfy-{i:D2}.wav"), [1, 2, 3]);

            var store = new TtsWavOutputStore(root);
            for (var i = 0; i < 100; i++)
                await store.SaveWavAsync("voicevox", [82, 73, 70, 70]);

            Assert.Equal(TtsWavOutputStore.MaximumFileCount, Directory.GetFiles(root, "*.wav").Length);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            Assert.Empty(Directory.GetFiles(root, "old-mobfy-*.wav"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
