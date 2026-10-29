#include <chrono>
#include <iomanip>
#include <iostream>
#include <map>
#include <polar/polar_sdk.hpp>
#include <thread>
int main(int argc, char **argv) {
    try {
        std::cout << std::unitbuf;
        polar::sdk::context ctx;
        std::string mode = argc > 1 ? argv[1] : "scan";
        uint64_t address = argc > 2 ? std::stoull(argv[2], nullptr, 16) : 0;
        int seconds = argc > 3 ? std::stoi(argv[3]) : 10;
        if (seconds < 1)
            throw std::runtime_error("Duration must be positive");
        const bool hr = mode == "hr" || mode == "hr-acc" || mode == "hr-noauth";
        const bool acc = mode == "acc" || mode == "hr-acc";
        polar_connect_options options{sizeof(options), POLAR_DEFAULT_FEATURES | (acc ? 8u : 0u),
                                      mode == "hr-noauth" ? 1u : 0u, 0};
        if (argc > 4)
            options.feature_mask = uint32_t(std::stoul(argv[4], nullptr, 0));
        polar_command c{};
        c.operation = mode == "scan"                             ? POLAR_OP_SCAN_START
                      : mode == "radio" || mode == "radio-watch" ? POLAR_OP_RADIO
                      : mode == "pair-state"                     ? POLAR_OP_PAIR_STATE
                      : mode == "unpair"                         ? POLAR_OP_UNPAIR
                                                                 : POLAR_OP_CONNECT;
        c.address = address;
        c.timeout_ms = 30000;
        if (c.operation == POLAR_OP_CONNECT) {
            c.data = reinterpret_cast<const uint8_t *>(&options);
            c.data_length = sizeof(options);
        }
        if (mode == "cached" || mode == "radio-watch")
            c.flags = 1;
        const auto request = ctx.submit(c);
        auto began = std::chrono::steady_clock::now();
        auto deadline = began + std::chrono::seconds(seconds + 90);
        auto elapsed = [&] {
            return std::chrono::duration<double>(std::chrono::steady_clock::now() - began).count();
        };
        std::vector<uint8_t> bytes(65536);
        std::vector<polar_sample> samples(4096);
        std::map<polar_request, uint32_t> starts;
        struct capture {
            uint32_t type;
            uint64_t count = 0;
            double last = 0, max_gap = 0;
        };
        std::map<polar_stream, capture> streams;
        bool complete = false;
        int result = 0;
        while (std::chrono::steady_clock::now() < deadline) {
            polar_event e{};
            e.struct_size = sizeof(e);
            uint32_t n = 0;
            auto code = polar_sdk_poll_event(ctx.native_handle(), &e, bytes.data(),
                                             uint32_t(bytes.size()), &n);
            if (code == POLAR_BUFFER_TOO_SMALL) {
                bytes.resize(n);
                continue;
            }
            if (code < 0)
                throw std::runtime_error("Event poll failed: " + std::to_string(code));
            if (code == POLAR_OK) {
                std::cout << "t=" << elapsed() << " event=" << e.type << " request=" << e.request
                          << " device=" << e.device << " stream=" << e.stream
                          << " result=" << e.result << " hresult=0x" << std::hex
                          << uint32_t(e.hresult) << std::dec << " att=" << e.att_error
                          << " stage=" << e.stage << " elapsed_ms=" << e.elapsed_ns / 1000000.
                          << " bytes=" << n << '\n';
                if (e.type == POLAR_EVENT_CONNECTION_STATE && n == sizeof(polar_connection_state)) {
                    polar_connection_state s{};
                    std::memcpy(&s, bytes.data(), sizeof(s));
                    const char *names[] = {"idle",        "connecting",   "discovering",
                                           "negotiating", "initializing", "authenticating",
                                           "ready",       "failed"};
                    std::cout << "phase=" << (s.state < 8 ? names[s.state] : "unknown")
                              << " requested=" << s.requested_features
                              << " ready=" << s.ready_features
                              << " unavailable=" << s.unavailable_features
                              << " failed=" << s.failed_features << " pdu=" << s.max_pdu_size
                              << " pairing=" << s.pairing_status
                              << " auth_attempts=" << s.authentication_attempts << '\n';
                }
                if (e.type == POLAR_EVENT_DIAGNOSTIC && n) {
                    std::cout.write(reinterpret_cast<char *>(bytes.data()), n);
                    std::cout << '\n';
                }
                if (e.result < 0 &&
                    (e.type == POLAR_EVENT_COMPLETE || e.type == POLAR_EVENT_ERROR ||
                     e.type == POLAR_EVENT_DISCONNECTED)) {
                    if (e.type == POLAR_EVENT_COMPLETE && n)
                        std::cout.write(reinterpret_cast<char *>(bytes.data()), n);
                    std::cout << '\n';
                    result = 2;
                    break;
                }
                if (e.type == POLAR_EVENT_ADVERTISEMENT && n == sizeof(polar_advertisement)) {
                    polar_advertisement a{};
                    std::memcpy(&a, bytes.data(), sizeof(a));
                    std::cout << "advertisement " << std::hex << a.address << std::dec
                              << " rssi=" << a.rssi << " name=" << a.name << '\n';
                }
                if (e.type == POLAR_EVENT_COMPLETE && e.request == request) {
                    complete = true;
                    if (mode == "pair-state" && n == 12) {
                        uint32_t status[3]{};
                        std::memcpy(status, bytes.data(), sizeof(status));
                        std::cout << "paired=" << status[0] << " can_pair=" << status[1]
                                  << " protection=" << status[2] << '\n';
                    }
                    if (hr || acc) {
                        if (hr)
                            starts.emplace(ctx.start(e.device, POLAR_HR), POLAR_HR);
                        if (acc)
                            starts.emplace(ctx.start(e.device, POLAR_ACC), POLAR_ACC);
                        complete = false;
                    } else if (mode != "scan" && mode != "radio-watch")
                        break;
                    else
                        deadline = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
                } else if (e.type == POLAR_EVENT_COMPLETE && starts.contains(e.request)) {
                    streams.emplace(e.stream, capture{starts.at(e.request)});
                    starts.erase(e.request);
                    if (starts.empty()) {
                        complete = true;
                        deadline = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
                    }
                }
            }
            for (auto &[id, capture] : streams) {
                uint32_t count = 0;
                auto r = polar_sdk_read_samples(ctx.native_handle(), id, samples.data(),
                                                uint32_t(samples.size()), &count);
                if (r < 0) {
                    std::cout << "read_error=" << r << '\n';
                    result = 2;
                    break;
                }
                if (count) {
                    auto now = elapsed();
                    if (capture.count)
                        capture.max_gap = std::max(capture.max_gap, now - capture.last);
                    capture.last = now;
                    capture.count += count;
                    std::cout << "t=" << now << " measurement=" << capture.type
                              << " samples=" << count << " first_ns=" << samples[0].timestamp_ns
                              << " value=" << samples[0].values[0] << '\n';
                }
            }
            if (result)
                break;
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        std::cout << "complete=" << complete << " elapsed_seconds=" << elapsed() << '\n';
        if (!complete && !result)
            result = 3;
        for (auto &[id, capture] : streams) {
            std::cout << "measurement=" << capture.type << " received_samples=" << capture.count
                      << " last_sample_age_seconds=" << elapsed() - capture.last
                      << " max_gap_seconds=" << capture.max_gap << '\n';
            if (!result &&
                (capture.count < 3 || elapsed() - capture.last > 5 || capture.max_gap > 5))
                result = 3;
        }
        if ((hr || acc) && streams.size() != size_t(hr) + size_t(acc) && !result)
            result = 3;
        return result;
    } catch (const std::exception &e) {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
