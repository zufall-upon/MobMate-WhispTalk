#pragma once

#include "mobmate_native_core/native_api.h"
#include <algorithm>
#include <array>
#include <chrono>
#include <cstddef>
#include <vector>

namespace mobmate::vc {

// All runtime access is under the audio route mutex (session mutex when stopped).
// Banks are copied and retired by the exchange caller, never by audio callbacks.
struct VcOnsetFiller {
    struct Clip {
        uint64_t id = 0;
        std::vector<int16_t> samples;
    };
    using Bank = std::array<Clip, 5>;
    Bank bank;
    uint64_t generation = 0, utterance = 0;
    uint64_t started_serial = 0, started_generation = 0;
    uint64_t started_utterance = 0, started_clip_id = 0, previous_id = 0;
    uint32_t count = 0;
    bool enabled = false;
    size_t selected = 5, offset = 0;
    uint64_t random = static_cast<uint64_t>(
        std::chrono::high_resolution_clock::now().time_since_epoch().count()) | 1;

    static bool valid(const mmv2_vc_filler_update& u, uint32_t sample_rate) {
        if (u.size != sizeof(u) || u.version != 1 || !u.generation ||
            u.enabled > 1 || u.reserved || u.sample_rate != sample_rate ||
            u.clip_count > 5 || (u.clip_count && !u.clips)) return false;
        for (uint32_t i = 0; i < u.clip_count; ++i) {
            const auto& c = u.clips[i];
            if (!c.id || c.reserved || !c.samples || !c.num_samples ||
                c.num_samples > static_cast<uint64_t>(sample_rate) * 3) return false;
            for (uint32_t j = 0; j < i; ++j)
                if (c.id == u.clips[j].id) return false;
        }
        return true;
    }
    static Bank prepare(const mmv2_vc_filler_update& u) {
        Bank result;
        for (uint32_t i = 0; i < u.clip_count; ++i) {
            result[i].id = u.clips[i].id;
            result[i].samples.assign(u.clips[i].samples, u.clips[i].samples + u.clips[i].num_samples);
        }
        return result;
    }
    bool active() const { return selected < count; }
    void cancel() { selected = 5; offset = 0; }
    void invalidate() { cancel(); enabled = false; }
    void install(const mmv2_vc_filler_update& u, Bank& prepared) {
        cancel();
        bank.swap(prepared);
        generation = u.generation;
        enabled = u.enabled != 0;
        count = u.clip_count;
        if (!started_serial) previous_id = u.previous_clip_id;
    }
    // Called once per accepted VAD false->active edge, even when unavailable.
    // Publishing later cannot arm that already-consumed utterance.
    void onset(bool playable) {
        cancel();
        ++utterance;
        if (!playable || !enabled) return;
        size_t candidates[5];
        size_t n = 0;
        for (size_t i = 0; i < count; ++i)
            if (bank[i].id != previous_id) candidates[n++] = i;
        if (!n) return;
        random ^= random << 13;
        random ^= random >> 7;
        random ^= random << 17;
        selected = candidates[random % n];
    }
    size_t read(int16_t* dst, size_t frames) {
        if (!active() || !frames) return 0;
        const auto& clip = bank[selected];
        const size_t n = std::min(frames, clip.samples.size() - offset);
        if (!offset && n) {
            ++started_serial;
            started_generation = generation;
            started_utterance = utterance;
            started_clip_id = previous_id = clip.id;
        }
        std::copy_n(clip.samples.data() + offset, n, dst);
        offset += n;
        if (offset == clip.samples.size()) cancel();
        return n;
    }
    void snapshot(mmv2_vc_filler_snapshot& s) const {
        s = {sizeof(s), 1, generation, utterance, started_serial, started_generation,
             started_utterance, started_clip_id, count, active() ? 1u : 0u};
    }
};

} // namespace mobmate::vc
