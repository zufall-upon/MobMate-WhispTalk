namespace MobMate.Core.Audio;

/// <summary>
/// Conservative hearing pre-ASR cleanup based on VoiceBench clarity_poc17_gate_only_no_peak_norm.
/// Keeps speech samples unchanged; only mutes low-level frames below -42 dBFS.
/// No spectral subtraction, boost, compression, or peak normalization.
/// </summary>
public static class HearingNoiseGate
{
    private const int DefaultSampleRate = 16000;
    private const int FrameMs = 20;
    private const double GateDb = -42.0;
    private static readonly double GateRms = 32768.0 * Math.Pow(10.0, GateDb / 20.0);

    public static byte[] ApplyPoc17(byte[] pcm16le, int sampleRate = DefaultSampleRate)
    {
        if (pcm16le.Length < 2 || sampleRate <= 0) return pcm16le;

        var sampleCount = pcm16le.Length / 2;
        var frameSamples = Math.Max(1, sampleRate * FrameMs / 1000);
        var output = new byte[pcm16le.Length];
        Buffer.BlockCopy(pcm16le, 0, output, 0, pcm16le.Length);

        for (var frameStart = 0; frameStart < sampleCount; frameStart += frameSamples)
        {
            var frameEnd = Math.Min(sampleCount, frameStart + frameSamples);
            if (CalcFrameRms16Le(pcm16le, frameStart, frameEnd) >= GateRms) continue;

            Array.Clear(output, frameStart * 2, (frameEnd - frameStart) * 2);
        }

        return output;
    }

    private static double CalcFrameRms16Le(byte[] pcm16le, int startSample, int endSample)
    {
        var count = endSample - startSample;
        if (count <= 0) return 0.0;

        var sum = 0.0;
        for (var i = startSample; i < endSample; i++)
        {
            var sample = BitConverter.ToInt16(pcm16le, i * 2);
            sum += (double)sample * sample;
        }
        return Math.Sqrt(sum / count);
    }
}
