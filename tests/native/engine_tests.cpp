#include "engine.hpp"
#include <polar/polar_sdk.hpp>
#include <iostream>
#include <thread>
namespace {
std::weak_ptr<polar::context> current;
std::atomic_int attempts{0}, abandoned{0};
std::atomic_bool late{false};
void check(bool ok, const char *why) {
    if (!ok)
        throw std::runtime_error(why);
}
class fake_backend : public polar::backend {
  public:
    void shutdown() override {}
    void abandon(uint64_t) override {
        ++abandoned;
    }
    void execute(polar::command &c, polar::event &e) override {
        if (c.c.operation == POLAR_OP_CONNECT) {
            ++attempts;
            if (late) {
                std::unique_lock l(c.async_gate);
                c.wake.wait_for(l, std::chrono::seconds(5), [&] { return c.cancel->load(); });
                e.e.device = polar::next_handle();
                return; // Emulate a late successful WinRT completion.
            }
            throw polar::failure(POLAR_OS_ERROR, "Injected unreachable");
        }
    }
};
polar_event wait_event(polar_context c, uint32_t type, uint64_t request = 0) {
    auto until = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (std::chrono::steady_clock::now() < until) {
        polar_event e{};
        e.struct_size = sizeof(e);
        uint8_t bytes[512];
        uint32_t n = 0;
        auto r = polar_sdk_poll_event(c, &e, bytes, sizeof(bytes), &n);
        if (r == 0 && e.type == type && (!request || e.request == request))
            return e;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    throw std::runtime_error("Engine event deadline");
}
} // namespace
namespace polar {
std::unique_ptr<backend> make_windows_backend(std::weak_ptr<context> owner) {
    current = owner;
    return std::make_unique<fake_backend>();
}
} // namespace polar
int main() {
    try {
        {
            polar::sdk::context app;
            auto core = current.lock();
            auto old = core->new_stream(123, POLAR_ACC, {});
            core->invalidate_device(123, POLAR_DISCONNECTED);
            polar_stream_info info{};
            info.struct_size = sizeof(info);
            check(polar_sdk_stream_info(app.native_handle(), old->id, &info) ==
                      POLAR_INVALID_HANDLE,
                  "Disconnected stream not invalidated");
            check(core->receive(old->id, {}, 0) == POLAR_INVALID_HANDLE,
                  "Late old-session callback accepted");
            core->reconnect(123, 42);
            auto start = wait_event(app.native_handle(), POLAR_EVENT_RECONNECTING);
            while (attempts == 0)
                std::this_thread::yield();
            polar_command cmd{};
            cmd.operation = POLAR_OP_REPLAY_OPEN;
            cmd.measurement = POLAR_ACC;
            auto began = std::chrono::steady_clock::now();
            auto request = app.submit(cmd);
            check(wait_event(app.native_handle(), POLAR_EVENT_COMPLETE, request).result == 0,
                  "Independent command failed during retry");
            check(std::chrono::steady_clock::now() - began < std::chrono::seconds(1),
                  "Reconnect backoff blocked other commands");
            check(polar_sdk_cancel(app.native_handle(), start.request) == 0, "Cancel reconnect");
            auto result = wait_event(app.native_handle(), POLAR_EVENT_COMPLETE, start.request);
            check(result.result == POLAR_CANCELLED, "Reconnect cancellation result");
        }
        {
            polar::sdk::context app;
            late = true;
            attempts = 0;
            polar_command cmd{};
            cmd.operation = POLAR_OP_CONNECT;
            cmd.address = 42;
            auto request = app.submit(cmd);
            while (attempts == 0)
                std::this_thread::yield();
            polar_sdk_cancel(app.native_handle(), request);
            auto result = wait_event(app.native_handle(), POLAR_EVENT_COMPLETE, request);
            check(result.result == POLAR_CANCELLED && result.device == 0 && abandoned == 1,
                  "Late successful handle not abandoned");
        }
        late = false;
        for (int i = 0; i < 20; i++) {
            auto began = std::chrono::steady_clock::now();
            {
                polar::sdk::context app;
                current.lock()->reconnect(123, 42);
            }
            check(std::chrono::steady_clock::now() - began < std::chrono::seconds(1),
                  "Shutdown during reconnect blocked");
        }
        std::cout << "PASS engine: stale callback rejection, retry fairness, cancellation, late "
                     "completion cleanup, 20 reconnect shutdown cycles\n";
        return 0;
    } catch (const std::exception &e) {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
