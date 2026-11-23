#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <utility>

namespace mobmate {

// One output, including legacy PCM. Backend setup/teardown and waits run outside
// the gate. Only the short non-blocking publish/start operation uses commit().
class PcmPlaybackOwner {
public:
    struct Lease {
        const uint64_t id;
        const bool filler;
        std::atomic<bool> cancelled{false};
        Lease(uint64_t value, bool identified) : id(value), filler(identified) {}
    };
    using Ticket = std::shared_ptr<Lease>;

    std::pair<Ticket, int> begin(uint64_t id, bool filler) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (filler) {
            if (id == 0) return {nullptr, -2};
            if (id <= seen_through_ || id <= cancelled_through_) return {nullptr, -6};
            seen_through_ = id; // Rejected/busy calls cannot reuse their identity.
        }
        if (active_) return {nullptr, -1};
        active_ = std::make_shared<Lease>(id, filler);
        return {active_, 0};
    }

    template<class Publish>
    int commit(const Ticket& ticket, Publish&& publish) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (active_ != ticket || ticket->cancelled.load()) return -6;
        return std::forward<Publish>(publish)();
    }

    // Called only after the backend stopped, released endpoints and completed
    // ALL cleanup. A late finish cannot clear a different output's owner.
    void finish(const Ticket& ticket) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (active_ == ticket) active_.reset();
        cv_.notify_all();
    }

    int cancel_filler_and_wait(uint64_t id) {
        if (id == 0) return -2;
        std::unique_lock<std::mutex> lock(mutex_);
        if (id > cancelled_through_) cancelled_through_ = id;
        const auto target = active_;
        if (!target || !target->filler || target->id != id) return 0;
        target->cancelled.store(true);
        cv_.notify_all();
        cv_.wait(lock, [&] { return active_ != target; });
        return 0;
    }

    int stop_legacy_and_wait() {
        std::unique_lock<std::mutex> lock(mutex_);
        const auto target = active_;
        if (!target) return -1;
        target->cancelled.store(true);
        cv_.notify_all();
        cv_.wait(lock, [&] { return active_ != target; });
        return 0;
    }

    template<class Rep, class Period>
    bool wait_cancelled(const Ticket& ticket, std::chrono::duration<Rep, Period> duration) {
        std::unique_lock<std::mutex> lock(mutex_);
        return cv_.wait_for(lock, duration, [&] { return ticket->cancelled.load(); });
    }

private:
    std::mutex mutex_;
    std::condition_variable cv_;
    Ticket active_;
    uint64_t seen_through_ = 0;
    uint64_t cancelled_through_ = 0;
};

} // namespace mobmate
