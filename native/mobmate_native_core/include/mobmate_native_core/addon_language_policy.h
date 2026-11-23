#pragma once
#include <string_view>

namespace mobmate {
inline bool addon_language_supported(std::string_view engine, std::string_view language) {
    return (engine == "haas_ja" || engine == "parakeet_ja") ? language == "ja" :
        engine == "qwen3_asr_ja" ? (language == "ja" || language == "en") :
        engine == "nemotron_3_5" && (language == "ja" || language == "en" || language == "ko");
}
// JA keeps the legacy callback contract. Additional languages require the V2
// engine/language context; this policy does not bypass any existing input gate.
inline bool addon_joint_language_supported(std::string_view engine, std::string_view language, bool context_callback,
                                          std::string_view consent_language = "ja") {
    return language == consent_language && addon_language_supported(engine, language) && (language == "ja" || context_callback);
}
}
