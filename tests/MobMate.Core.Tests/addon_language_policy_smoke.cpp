#include "mobmate_native_core/addon_language_policy.h"
#include <cassert>
#include <initializer_list>

int main() {
    using mobmate::addon_joint_language_supported;
    for (const auto engine : {"haas_ja", "parakeet_ja", "qwen3_asr_ja", "nemotron_3_5"}) {
        assert(addon_joint_language_supported(engine, "ja", false));
        assert(addon_joint_language_supported(engine, "ja", true));
        assert(!addon_joint_language_supported(engine, "zh", true));
    }
    assert(!addon_joint_language_supported("haas_ja", "en", true));
    assert(!addon_joint_language_supported("parakeet_ja", "en", true));
    assert(!addon_joint_language_supported("qwen3_asr_ja", "en", false));
    assert(!addon_joint_language_supported("qwen3_asr_ja", "en", true)); // old JA consent stays JA in mixed replay
    assert(addon_joint_language_supported("qwen3_asr_ja", "en", true, "en"));
    assert(!addon_joint_language_supported("qwen3_asr_ja", "ko", true));
    assert(!addon_joint_language_supported("nemotron_3_5", "ko", false));
    assert(addon_joint_language_supported("nemotron_3_5", "en", true, "en"));
    assert(addon_joint_language_supported("nemotron_3_5", "ko", true, "ko"));
    assert(!addon_joint_language_supported("nemotron_3_5", "ko", true, "en"));
    assert(!addon_joint_language_supported("unknown", "ja", true));
}
