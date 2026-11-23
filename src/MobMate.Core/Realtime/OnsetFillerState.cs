using System.Security.Cryptography;

namespace MobMate.Core.Realtime;

// Bounded, session-local selection and invalidation only. No timers, synthesis,
// playback, persistence, or native calls may run while this gate is held.
internal sealed class OnsetFillerState
{
    private readonly object _gate = new();
    private readonly Func<int, int> _random;
    private bool _enabled;
    private string _configuration = "";
    private long _configurationGeneration;
    private long _playbackGeneration;
    private ulong _utterance;
    private FillerClip[] _clips = [];
    private string? _lastStarted;

    internal OnsetFillerState(Func<int, int>? random = null) => _random = random ?? Random.Shared.Next;

    internal FillerPreparation Configure(bool enabled, string configuration)
    {
        lock (_gate)
        {
            if (_enabled != enabled || _configuration != configuration)
            {
                _enabled = enabled;
                _configuration = configuration;
                _configurationGeneration++;
                _playbackGeneration++;
                _clips = [];
            }
            return new(_configurationGeneration, _configuration);
        }
    }

    internal bool Publish(FillerPreparation preparation, IEnumerable<FillerClip> clips)
    {
        // Materialize outside the gate. A caller may still be finishing an old
        // synthesis; publishing must not arm an already consumed utterance.
        var prepared = clips.DistinctBy(c => c.Id).Take(5).ToArray();
        lock (_gate)
        {
            if (!_enabled || preparation != new FillerPreparation(_configurationGeneration, _configuration))
                return false;
            _clips = prepared;
            return true;
        }
    }

    internal FillerAttempt? BeginUtterance(ulong sequence, bool permitted)
    {
        var onset = AcceptUtterance(sequence, permitted, out _);
        return onset == null ? null : Choose(onset);
    }

    // Native callback boundary: capture only the already prepared pool and
    // consume/invalidate. Random selection runs after the old output has drained,
    // so a delayed real-start ACK still participates in previous-WAV exclusion.
    internal FillerOnset? AcceptUtterance(ulong sequence, bool permitted, out bool consumed)
    {
        lock (_gate)
        {
            consumed = false;
            if (sequence == 0 || sequence <= _utterance) return null;
            consumed = true;
            _utterance = sequence; // OFF/unready/denied is consumed too.
            var generation = ++_playbackGeneration;
            if (!_enabled || !permitted || _clips.Length == 0) return null;
            return new(generation, _configurationGeneration, sequence, _configuration, _clips);
        }
    }

    internal FillerAttempt? Choose(FillerOnset onset)
    {
        lock (_gate)
        {
            if (!_enabled || onset.PlaybackGeneration != _playbackGeneration ||
                onset.ConfigurationGeneration != _configurationGeneration || onset.Sequence != _utterance || onset.Configuration != _configuration) return null;
            var candidates = onset.Clips.Where(c => c.Id != _lastStarted).ToArray();
            if (candidates.Length == 0) return null;
            return new(onset.PlaybackGeneration, _configurationGeneration, onset.Sequence,
                _configuration, candidates[_random(candidates.Length)]);
        }
    }

    internal bool IsCurrent(FillerAttempt attempt)
    {
        lock (_gate) return IsCurrentUnsafe(attempt);
    }

    // Only an acknowledgement of actual output may update the exclusion. The
    // ACK can arrive after a concurrent OFF/body/config invalidation: the sound
    // still started and must remain the last actual sound, even in a new session.
    internal bool RecordActualStart(FillerAttempt attempt)
    {
        lock (_gate)
        {
            if (attempt.Acknowledged) return false;
            attempt.Acknowledged = true;
            _lastStarted = attempt.Clip.Id;
            return true;
        }
    }

    // Native sequence numbers belong to a handle, not to the process. The
    // caller must also discard queued notifications from the previous handle.
    internal void BeginSession()
    {
        lock (_gate)
        {
            _enabled = false;
            _utterance = 0;
            _configurationGeneration++;
            _playbackGeneration++;
            _clips = [];
        }
    }

    internal void BodyReady()
    {
        lock (_gate) _playbackGeneration++;
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            _enabled = false;
            _configurationGeneration++;
            _playbackGeneration++;
            _clips = [];
        }
    }

    private bool IsCurrentUnsafe(FillerAttempt attempt) => _enabled &&
        attempt.PlaybackGeneration == _playbackGeneration &&
        attempt.ConfigurationGeneration == _configurationGeneration &&
        attempt.Sequence == _utterance && attempt.Configuration == _configuration;
}

internal readonly record struct FillerPreparation(long Generation, string Configuration);
internal sealed record FillerOnset(long PlaybackGeneration, long ConfigurationGeneration,
    ulong Sequence, string Configuration, FillerClip[] Clips);
internal sealed record FillerAttempt(long PlaybackGeneration, long ConfigurationGeneration,
    ulong Sequence, string Configuration, FillerClip Clip)
{
    internal bool Acknowledged { get; set; }
}

internal sealed class FillerClip
{
    internal string Id { get; }
    internal ReadOnlyMemory<byte> Wav { get; }

    internal FillerClip(byte[] wav)
    {
        // The synthesis adapter validates PCM/WAV and voice/language first.
        // Own the bytes, so callers cannot silently replace an identified clip.
        var owned = wav.ToArray();
        Id = Convert.ToHexString(SHA256.HashData(owned));
        Wav = owned;
    }
}
