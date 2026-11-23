#include "mobmate_native_core/pcm_playback_owner.h"
#include <future>
#include <iostream>
#include <stdexcept>

using mobmate::PcmPlaybackOwner;
using namespace std::chrono_literals;
static void require(bool value, const char* name) {
    if (!value) throw std::runtime_error(name);
}

int main() {
    try {
        PcmPlaybackOwner owner;
        require(owner.cancel_filler_and_wait(1) == 0, "cancel before native entry");
        require(owner.begin(1, true).second == -6, "late cancelled play rejected");
        auto [starting, status] = owner.begin(2, true);
        require(status == 0, "Starting owns output before backend setup");
        std::promise<void> cancelling;
        auto cancelled = std::async(std::launch::async, [&] {
            cancelling.set_value();
            return owner.cancel_filler_and_wait(2);
        });
        struct FinishOnExit {
            PcmPlaybackOwner& owner;
            PcmPlaybackOwner::Ticket ticket;
            ~FinishOnExit() { owner.finish(ticket); }
        } starting_cleanup{owner, starting};
        cancelling.get_future().wait();
        require(owner.wait_cancelled(starting, 2s), "Starting cancellation observed");
        bool published = false;
        require(owner.commit(starting, [&] { published = true; return 0; }) == -6 && !published,
            "cancelled Starting never publishes PCM");
        require(cancelled.wait_for(0s) != std::future_status::ready, "cancel waits for real backend cleanup");
        require(owner.begin(0, false).second == -1, "body cannot overlap old cleanup");
        owner.finish(starting);
        require(cancelled.get() == 0, "cancel completes after cleanup");
        auto [body, body_status] = owner.begin(0, false);
        require(body_status == 0, "body handoff after real cleanup");
        owner.finish(starting); // Deliberately delayed obsolete completion.
        require(owner.begin(3, true).second == -1, "old completion cannot release new body");
        require(owner.cancel_filler_and_wait(3) == 0 && !body->cancelled.load(), "target cancel never stops body");
        owner.finish(body);
        require(owner.begin(3, true).second == -6, "busy/cancelled ID is never reused");

        auto [playing, playing_status] = owner.begin(4, true);
        require(playing_status == 0, "new utterance has a new ID");
        int starts = 0;
        require(owner.commit(playing, [&] { ++starts; return 0; }) == 0 && starts == 1,
            "actual backend publish acknowledgement");
        auto stopping = std::async(std::launch::async, [&] { return owner.stop_legacy_and_wait(); });
        FinishOnExit playing_cleanup{owner, playing};
        require(owner.wait_cancelled(playing, 2s), "global Stop also drains identified output");
        require(owner.commit(playing, [&] { ++starts; return 0; }) == -6 && starts == 1,
            "Stop prevents later PCM publication");
        owner.finish(playing);
        require(stopping.get() == 0, "Stop finishes after backend exit");
        auto [other, other_status] = owner.begin(0, false);
        require(other_status == 0, "legacy output keeps existing entry point");
        require(owner.cancel_filler_and_wait(4) == 0 && !other->cancelled.load(), "old Cancel cannot target manual PCM");
        owner.finish(other);
        require(owner.stop_legacy_and_wait() == -1, "idle legacy Stop contract");
        std::cout << "PCM owner barriers passed: pre-entry, Starting, handoff, obsolete completion, Stop, legacy isolation; no devices.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
