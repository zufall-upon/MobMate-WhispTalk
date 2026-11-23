namespace MobMate.Core.Tts;

/// <summary>
/// 依存追加なしの自前 位相ボコーダ pitch-shift。
/// pitch-shift = phase-vocoder time-stretch(α=r) → linear resample(1/r)。r = 2^(semitones/12)。
/// MOSS など音素境界が取れない生成波形へ、自声 F0 カーブ(semitone 偏差列)を全体トレンドとして転写するために使う。
/// </summary>
internal static class PitchShifter
{
    private const int FftSize = 2048;
    private const int HopA = 512; // analysis hop (1/4 overlap)

    /// <summary>固定 semitone shift。</summary>
    public static float[] Shift(float[] x, double semitones)
    {
        if (x.Length == 0 || Math.Abs(semitones) < 1e-6) return (float[])x.Clone();
        double r = Math.Pow(2.0, semitones / 12.0);
        var stretched = Stretch(x, r);
        return Resample(stretched, x.Length);
    }

    /// <summary>
    /// time-varying shift。contourSemitones は時間軸の半音偏差列(任意長, 線形補間で全長へ伸張)。
    /// </summary>
    public static float[] ShiftVarying(float[] x, float[] contourSemitones, double extraSemitones = 0.0)
    {
        if (x.Length == 0) return (float[])x.Clone();
        if (contourSemitones.Length < 2) return Shift(x, extraSemitones);
        return StretchVarying(x, contourSemitones, extraSemitones);
    }

    private static float[] Stretch(float[] x, double alpha)
    {
        int n = FftSize;
        int hopA = HopA;
        int hopS = Math.Max(1, (int)Math.Round(hopA * alpha));
        var window = Hann(n);

        int frames = (x.Length - n) / hopA + 1;
        if (frames < 1) return (float[])x.Clone();

        int outLen = (frames - 1) * hopS + n;
        var outBuf = new double[outLen];
        var winSum = new double[outLen];

        var lastPhase = new double[n];
        var sumPhase = new double[n];
        var omega = new double[n];
        for (int k = 0; k < n; k++) omega[k] = 2.0 * Math.PI * k / n;

        var re = new double[n];
        var im = new double[n];

        for (int f = 0; f < frames; f++)
        {
            int start = f * hopA;
            for (int i = 0; i < n; i++) { re[i] = x[start + i] * window[i]; im[i] = 0.0; }
            Fft(re, im, false);

            for (int k = 0; k < n; k++)
            {
                double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                double phase = Math.Atan2(im[k], re[k]);
                if (f == 0)
                {
                    sumPhase[k] = phase;
                }
                else
                {
                    double dphi = phase - lastPhase[k] - omega[k] * hopA;
                    dphi = WrapPhase(dphi);
                    double trueFreq = omega[k] + dphi / hopA;
                    sumPhase[k] += trueFreq * hopS;
                }
                lastPhase[k] = phase;
                re[k] = mag * Math.Cos(sumPhase[k]);
                im[k] = mag * Math.Sin(sumPhase[k]);
            }

            Fft(re, im, true);
            int outStart = f * hopS;
            for (int i = 0; i < n; i++)
            {
                outBuf[outStart + i] += re[i] * window[i];
                winSum[outStart + i] += window[i] * window[i];
            }
        }

        var result = new float[outLen];
        for (int i = 0; i < outLen; i++)
            result[i] = (float)(winSum[i] > 1e-9 ? outBuf[i] / winSum[i] : 0.0);
        return result;
    }

    // 区間ごと固定 shift の overlap-add で time-varying を近似。
    private static float[] StretchVarying(float[] x, float[] contour, double extra)
    {
        int seg = 4096;
        int ov = 1024;
        var outBuf = new float[x.Length];
        var winSum = new float[x.Length];
        int pos = 0;
        while (pos < x.Length)
        {
            int len = Math.Min(seg, x.Length - pos);
            if (len < 64) break;
            double t = (pos + len * 0.5) / x.Length;
            double semis = SampleContour(contour, t) + extra;
            var slice = new float[len];
            Array.Copy(x, pos, slice, 0, len);
            var shifted = Shift(slice, semis);
            int m = Math.Min(shifted.Length, len);
            for (int i = 0; i < m; i++)
            {
                float w = TaperWeight(i, len, ov);
                outBuf[pos + i] += shifted[i] * w;
                winSum[pos + i] += w;
            }
            pos += seg - ov;
        }
        var result = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
            result[i] = winSum[i] > 1e-6f ? outBuf[i] / winSum[i] : 0f;
        return result;
    }

    private static float TaperWeight(int i, int len, int ov)
    {
        if (ov <= 0) return 1f;
        if (i < ov) return (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / ov));
        if (i > len - ov) return (float)(0.5 - 0.5 * Math.Cos(Math.PI * (len - i) / ov));
        return 1f;
    }

    private static double SampleContour(float[] c, double t)
    {
        if (c.Length == 0) return 0;
        if (c.Length == 1) return c[0];
        double p = Math.Clamp(t, 0, 1) * (c.Length - 1);
        int lo = (int)p;
        int hi = Math.Min(c.Length - 1, lo + 1);
        double fr = p - lo;
        return c[lo] + (c[hi] - c[lo]) * fr;
    }

    private static float[] Resample(float[] x, int targetLen)
    {
        if (x.Length == 0) return new float[targetLen];
        if (x.Length == targetLen) return (float[])x.Clone();
        var result = new float[targetLen];
        double scale = (double)(x.Length - 1) / Math.Max(1, targetLen - 1);
        for (int i = 0; i < targetLen; i++)
        {
            double pos = i * scale;
            int lo = (int)pos;
            int hi = Math.Min(x.Length - 1, lo + 1);
            double fr = pos - lo;
            result[i] = (float)(x[lo] + (x[hi] - x[lo]) * fr);
        }
        return result;
    }

    private static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
        return w;
    }

    private static double WrapPhase(double p)
    {
        p = (p + Math.PI) % (2.0 * Math.PI);
        if (p < 0) p += 2.0 * Math.PI;
        return p - Math.PI;
    }

    // iterative radix-2 FFT (n は 2 の冪)
    private static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2.0 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1.0, ci = 0.0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci;
                    double ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
        if (inverse)
            for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }
}
