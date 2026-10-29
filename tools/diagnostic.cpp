#include <chrono>
#include <iomanip>
#include <iostream>
#include <polar/polar_sdk.hpp>
#include <thread>
int main(int argc, char **argv) {
    try {
        polar::sdk::context ctx;
        std::string mode = argc > 1 ? argv[1] : "scan";
        uint64_t address = argc > 2 ? std::stoull(argv[2], nullptr, 16) : 0;
        int seconds = argc > 3 ? std::stoi(argv[3]) : 10;
        polar_command c{};
        c.operation = mode == "scan"                             ? POLAR_OP_SCAN_START
                      : mode == "radio" || mode == "radio-watch" ? POLAR_OP_RADIO
                      : mode == "pair-state"                     ? POLAR_OP_PAIR_STATE
                                                                 : POLAR_OP_CONNECT;
        c.address = address;
        c.timeout_ms = 15000;
        if (mode == "cached")
            c.flags = 1;
        if (mode == "radio-watch")
            c.flags = 1;
        auto request = ctx.submit(c);
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(seconds + 20);
        std::vector<uint8_t> bytes(65536);
        std::vector<polar_sample> samples(4096);
        polar_stream stream = 0;
        uint64_t received = 0;
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
            if (code == POLAR_OK) {
                std::cout << "event=" << e.type << " request=" << e.request
                          << " device=" << e.device << " stream=" << e.stream
                          << " result=" << e.result << " hresult=0x" << std::hex
                          << uint32_t(e.hresult) << std::dec << " att=" << e.att_error
                          << " stage=" << e.stage << " elapsed_ms=" << e.elapsed_ns / 1000000.
                          << " bytes=" << n << "\n";
                if (e.result < 0) {
                    std::cout.write(reinterpret_cast<char *>(bytes.data()), n);
                    std::cout << '\n';
                    result = 2;
                    break;
                }
                if (e.type == POLAR_EVENT_ADVERTISEMENT && n == sizeof(polar_advertisement)) {
                    auto *a = reinterpret_cast<polar_advertisement *>(bytes.data());
                    std::cout << "advertisement " << std::hex << a->address << std::dec
                              << " rssi=" << a->rssi << " name=" << a->name << "\n";
                }
                if (e.type == POLAR_EVENT_COMPLETE && e.request == request) {
                    complete = true;
                    if (mode == "hr" || mode == "acc") {
                        if (!stream && e.stream) {
                            stream = e.stream;
                            deadline =
                                std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
                        } else if (!e.stream && e.device) {
                            request = ctx.start(e.device, mode == "hr" ? POLAR_HR : POLAR_ACC);
                            complete = false;
                        }
                    } else if (mode != "scan" && mode != "radio-watch")
                        break;
                    else
                        deadline = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
                }
            }
            if (stream) {
                uint32_t count = 0;
                auto r = polar_sdk_read_samples(ctx.native_handle(), stream, samples.data(),
                                                uint32_t(samples.size()), &count);
                if (r < 0) {
                    result = 2;
                    break;
                }
                received += count;
                if (count)
                    std::cout << "samples=" << count << " first_ns=" << samples[0].timestamp_ns
                              << " value=" << samples[0].values[0] << "\n";
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        std::cout << "complete=" << complete << " received_samples=" << received << "\n";
        if ((mode == "hr" || mode == "acc") && !received)
            result = 3;
        return result;
    } catch (const std::exception &e) {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
