using Microsoft.Extensions.Logging;

namespace MobMate.Core.Tts;

public sealed class TtsWavOutputStore
{
    public const int MaximumFileCount = 100;

    private static long _sequence;
    private readonly string _outputDirectory;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TtsWavOutputStore(string outputDirectory, ILogger? logger = null)
    {
        _outputDirectory = outputDirectory;
        _logger = logger;
    }

    public string OutputDirectory => _outputDirectory;

    public async Task SaveWavAsync(string source, byte[] wavBytes)
    {
        if (wavBytes.Length == 0) return;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            var safeSource = new string((source ?? "tts").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (string.IsNullOrEmpty(safeSource)) safeSource = "tts";
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var sequence = Interlocked.Increment(ref _sequence);
            var finalPath = Path.Combine(_outputDirectory, $"{stamp}_{sequence:D6}_tts_{safeSource}.wav");
            temporaryPath = finalPath + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, wavBytes).ConfigureAwait(false);
            File.Move(temporaryPath, finalPath);
            temporaryPath = null;
            CleanupOldestWavs();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TTS] WAV output save failed: source={Source} directory={Directory}", source, _outputDirectory);
        }
        finally
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
            _writeLock.Release();
        }
    }

    private void CleanupOldestWavs()
    {
        var files = new DirectoryInfo(_outputDirectory)
            .EnumerateFiles("*.wav", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        for (var i = 0; i < files.Length - MaximumFileCount; i++)
        {
            try { files[i].Delete(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "[TTS] WAV output cleanup failed: path={Path}", files[i].FullName); }
        }
    }
}
