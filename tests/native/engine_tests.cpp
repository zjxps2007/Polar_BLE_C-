#include "engine.hpp"
#include "connection_policy.hpp"
#include <polar/polar_sdk.hpp>
#include <iostream>
#include <thread>
namespace {
std::weak_ptr<polar::context> current;
std::atomic_int attempts{0}, abandoned{0};
std::atomic_bool late{false};
std::atomic_bool auth_failure{false};
std::atomic_uint32_t observed_features{0};
void check(bool ok, const char *why) {
    if (!ok)
        throw std::runtime_error(why);
}
void connection_policy_tests() {
    static_assert(sizeof(polar_connect_options) == 16 && sizeof(polar_connection_state) == 32);
    int time = 0, probes = 0;
    auto settled = polar::settle_security([&] { return ++probes < 3 ? 5 : 0; },
                                          [](int result) { return result == 5; }, [&] { ++time; },
                                          [&] { return time < 10; });
    check(settled == 0 && probes == 3 && time == 2,
          "Wait for delayed Windows encryption before completing preparation");
    time = probes = 0;
    settled = polar::settle_security(
        [&] {
            ++probes;
            return 5;
        },
        [](int) { return true; }, [&] { ++time; }, [&] { return time < 3; });
    check(settled == 5 && probes == 3 && time == 3, "Security settlement must be bounded");
    probes = 0;
    try {
        polar::settle_security(
            [&] {
                ++probes;
                return 5;
            },
            [](int) { return true; }, [] { throw polar::failure(POLAR_CANCELLED, "cancel"); },
            [] { return true; });
        check(false, "Cancelled settlement completed");
    } catch (const polar::failure &e) {
        check(e.code == POLAR_CANCELLED && probes == 1,
              "Cancelled settlement must not repeat an ATT operation");
    }
    auto options = polar::connection_options({});
    check(options.feature_mask == 7 && options.authentication == 0,
          "Default initialization policy");
    options.feature_mask = 15;
    auto wire = polar::view(reinterpret_cast<const uint8_t *>(&options), sizeof(options));
    polar::bytes automatic(8, 0);
    check(polar::automatic_connection_options(automatic).feature_mask == 7,
          "Legacy auto-connect scan options must not become CONNECT options");
    automatic.insert(automatic.end(), wire.begin(), wire.end());
    check(polar::automatic_connection_options(automatic).feature_mask == 15,
          "Auto-connect forwards appended initialization options");
    check(polar::connection_options(wire).feature_mask == 15, "Requested features preserved");
    auto expect = [](auto action, int code) {
        try {
            action();
        } catch (const polar::failure &e) {
            check(e.code == code, "Unexpected connection failure code");
            return;
        }
        throw std::runtime_error("Expected connection policy rejection");
    };
    expect([&] { polar::connection_options(wire.first(15)); }, POLAR_INVALID_ARGUMENT);
    options.reserved = 1;
    expect([&] { polar::connection_options(wire); }, POLAR_INVALID_ARGUMENT);
    options.reserved = 0;
    options.authentication = 2;
    expect([&] { polar::connection_options(wire); }, POLAR_INVALID_ARGUMENT);
    options.authentication = 0;
    options.feature_mask = 0x80000000u;
    expect([&] { polar::connection_options(wire); }, POLAR_INVALID_ARGUMENT);
    auto protected_result = [](int result) { return result == 5 || result == 15; };
    check(!polar::security_retry_needed(0, true, 0, 0),
          "Unrelated unreachable must not trigger pairing");
    check(polar::security_retry_needed(0, true, 1, 5),
          "Failed automatic encryption may request explicit bonding");
    check(!polar::security_retry_needed(0, true, 2, 5),
          "Failed explicit authentication must terminate");
    check(!polar::security_retry_needed(0, false, 1, 5),
          "Successful protected operation must not pair again");
    auto guard = [] {};
    int calls = 0, authentications = 0;
    check(polar::authenticated_operation(
              [&] {
                  ++calls;
                  return 0;
              },
              protected_result, [&](uint32_t) { ++authentications; }, guard, true) == 0 &&
              calls == 1 && authentications == 0,
          "Public GATT operation must not pre-pair");
    for (int att : {5, 15}) {
        std::vector<int> trace;
        polar::authenticated_operation(
            [&] {
                trace.push_back(1);
                return trace.size() == 1 ? att : 0;
            },
            protected_result,
            [&](uint32_t attempt) {
                check(attempt == 1, "Authentication attempt");
                trace.push_back(2);
            },
            guard, true);
        check(trace == std::vector<int>({1, 2, 1}), "Retry original ATT only after authentication");
    }
    calls = authentications = 0;
    expect(
        [&] {
            polar::authenticated_operation(
                [&] {
                    ++calls;
                    return 5;
                },
                protected_result, [&](uint32_t) { ++authentications; }, guard, true);
        },
        POLAR_AUTHENTICATION_FAILED);
    check(calls == 3 && authentications == 2, "Security retries must be bounded");
    calls = authentications = 0;
    expect(
        [&] {
            polar::authenticated_operation(
                [&] {
                    ++calls;
                    return 15;
                },
                protected_result, [&](uint32_t) { ++authentications; }, guard, false);
        },
        POLAR_AUTHENTICATION_FAILED);
    check(calls == 1 && authentications == 0, "Disabled authentication must not pair");
    for (int stopped : {POLAR_CANCELLED, POLAR_DISCONNECTED}) {
        bool live = true;
        calls = 0;
        expect(
            [&] {
                polar::authenticated_operation(
                    [&] {
                        ++calls;
                        return 5;
                    },
                    protected_result, [&](uint32_t) { live = false; },
                    [&] {
                        if (!live)
                            throw polar::failure(stopped, "stale");
                    },
                    true);
            },
            stopped);
        check(calls == 1,
              "Late authentication completion must not retry a cancelled or stale session");
    }
    check(polar::reconnect_delay_ms(1) == 2000 && polar::reconnect_delay_ms(2) == 4000 &&
              polar::reconnect_delay_ms(3) == 8000 && polar::reconnect_delay_ms(40) == 16000,
          "Reconnect backoff must be bounded");
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
            observed_features = polar::connection_options(c.data).feature_mask;
            if (auth_failure)
                throw polar::failure(POLAR_AUTHENTICATION_FAILED, "Injected key failure");
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
        connection_policy_tests();
        {
            polar::sdk::context app;
            auth_failure = true;
            attempts = 0;
            polar_connect_options options{sizeof(options), 15, 0, 0};
            current.lock()->reconnect(
                123, 42, {reinterpret_cast<const uint8_t *>(&options), sizeof(options)});
            auto result = wait_event(app.native_handle(), POLAR_EVENT_COMPLETE);
            check(result.result == POLAR_AUTHENTICATION_FAILED && attempts == 1 &&
                      observed_features == 15,
                  "Terminal authentication failure must complete without reconnect retry; preserve "
                  "options");
            auth_failure = false;
        }
        {
            polar::sdk::context app;
            auto core = current.lock();
            attempts = 0;
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
            current.lock()->reconnect(123, 42);
            auto reconnect = wait_event(app.native_handle(), POLAR_EVENT_RECONNECTING);
            polar_command unpair{};
            unpair.operation = POLAR_OP_UNPAIR;
            unpair.address = 42;
            auto request = app.submit(unpair);
            check(wait_event(app.native_handle(), POLAR_EVENT_COMPLETE, reconnect.request).result ==
                      POLAR_CANCELLED,
                  "Explicit unpair must cancel target reconnect");
            check(wait_event(app.native_handle(), POLAR_EVENT_COMPLETE, request).result == 0,
                  "Explicit unpair completion missing");
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
        std::cout << "PASS connection policy: options, protected retry ordering/budget, "
                     "cancellation, terminal authentication, explicit unpair\n";
        std::cout << "PASS engine: stale callback rejection, retry fairness, cancellation, late "
                     "completion cleanup, 20 reconnect shutdown cycles\n";
        return 0;
    } catch (const std::exception &e) {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
