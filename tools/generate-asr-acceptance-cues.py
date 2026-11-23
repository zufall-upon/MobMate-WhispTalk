#!/usr/bin/env python3
"""Original two-tone MobMate acceptance cues (stdlib only).

Deterministic construction: fixed 48,000 Hz, mono, signed 16-bit PCM,
80 ms tone + 60 ms digital silence + 80 ms tone (10,560 samples total).
There is no random input, external audio, downloaded asset, or dependency.
Commit the delivered WAV files to ensure distribution uses fixed bytes.
"""

from __future__ import annotations

import hashlib
import json
import math
import struct
import wave
from pathlib import Path

RATE = 48000
TONE_SAMPLES = RATE * 80 // 1000        # 3840
GAP_SAMPLES = RATE * 60 // 1000         # 2880
ATTACK_SAMPLES = RATE * 8 // 1000       # 384
RELEASE_SAMPLES = RATE * 10 // 1000     # 480
TOTAL_SAMPLES = 2 * TONE_SAMPLES + GAP_SAMPLES


def envelope(index: int) -> float:
    """Raised-cosine 8-ms attack and 10-ms release, including zero endpoints."""
    attack = min(1.0, index / ATTACK_SAMPLES)
    release = min(1.0, (TONE_SAMPLES - 1 - index) / RELEASE_SAMPLES)
    return (0.5 - 0.5 * math.cos(math.pi * attack)) * (0.5 - 0.5 * math.cos(math.pi * release))


def tone(frequency_hz: int, variant: str) -> list[int]:
    result = []
    for n in range(TONE_SAMPLES):
        phase = 2.0 * math.pi * frequency_hz * n / RATE
        if variant == "A":
            # Rounded sine-like sound; a very subtle second harmonic.
            sample = 0.17 * (math.sin(phase) + 0.06 * math.sin(2.0 * phase + 0.45)) / 1.06
        elif variant == "B":
            # Distinct electronic character; no hard edges or square waves.
            sample = 0.18 * (
                math.sin(phase)
                + 0.24 * math.sin(2.0 * phase + 0.10)
                + 0.11 * math.sin(3.0 * phase + 0.30)
            ) / 1.35
        else:
            raise ValueError("Unknown variant")
        sample *= envelope(n)
        result.append(max(-32768, min(32767, int(round(32767.0 * sample)))))
    return result


def make(name: str, frequencies: tuple[int, int], variant: str) -> dict[str, object]:
    samples = tone(frequencies[0], variant) + [0] * GAP_SAMPLES + tone(frequencies[1], variant)
    assert len(samples) == TOTAL_SAMPLES
    assert samples[TONE_SAMPLES:TONE_SAMPLES + GAP_SAMPLES] == [0] * GAP_SAMPLES
    target = Path(__file__).resolve().parent / name
    with wave.open(str(target), "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(RATE)
        wf.writeframes(struct.pack("<" + "h" * len(samples), *samples))
    data = target.read_bytes()
    peak = max(abs(x) for x in samples) / 32768.0
    rms = math.sqrt(sum(x * x for x in samples) / len(samples)) / 32768.0
    return {
        "file": name,
        "variant": variant,
        "frequencies_hz": list(frequencies),
        "sample_rate_hz": RATE,
        "channels": 1,
        "sample_format": "signed PCM16 little-endian",
        "sample_width_bits": 16,
        "tone_samples_each": TONE_SAMPLES,
        "silence_samples": GAP_SAMPLES,
        "total_samples": len(samples),
        "duration_seconds": len(samples) / RATE,
        "fade_attack_ms": 8,
        "fade_release_ms": 10,
        "peak_linear_fs": round(peak, 8),
        "rms_linear_fs": round(rms, 8),
        "peak_dbfs": round(20 * math.log10(peak), 3),
        "rms_dbfs": round(20 * math.log10(rms), 3),
        "size_bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
    }


def main() -> None:
    results = [
        make("mobmate_asr_cue_A_soft.wav", (700, 850), "A"),
        make("mobmate_asr_cue_B_electronic.wav", (780, 1040), "B"),
    ]
    path = Path(__file__).resolve().parent / "cue_manifest.json"
    path.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
