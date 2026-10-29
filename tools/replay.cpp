#include <algorithm>
#include <chrono>
#include <fstream>
#include <iostream>
#include <polar/polar_sdk.hpp>
#include <thread>
#include <windows.h>
#include <psapi.h>
static uint64_t process_cpu_ns() {
    FILETIME created, exit, kernel, user;
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exit, &kernel, &user))
        return 0;
    auto value = [](FILETIME v) { return (uint64_t(v.dwHighDateTime) << 32) | v.dwLowDateTime; };
    return (value(kernel) + value(user)) * 100;
}
static void put(std::vector<uint8_t> &b, size_t o, uint64_t v, size_t n) {
    for (size_t i = 0; i < n; i++)
        b[o + i] = uint8_t(v >> (8 * i));
}
int main(int argc, char **argv) {
    try {
        double seconds = argc > 1 ? std::stod(argv[1]) : 10;
        int load = argc > 2 ? std::stoi(argv[2]) : 1;
        std::string output = argc > 3 ? argv[3] : "replay-result.json";
        if (seconds <= 0 || seconds > 18000 || load < 1 || load > 4)
            throw std::invalid_argument("Duration 0..18000 seconds, load 1..4 required");
        auto cpu_begin = process_cpu_ns();
        polar::sdk::context ctx(POLAR_LOSSLESS, 8192);
        polar_command c{};
        c.operation = POLAR_OP_REPLAY_OPEN;
        c.measurement = POLAR_ACC;
        auto request = ctx.submit(c);
        polar_stream stream = 0;
        std::vector<uint8_t> event_data(4096);
        while (!stream) {
            polar_event e{};
            e.struct_size = sizeof(e);
            uint32_t size;
            auto r = polar_sdk_poll_event(ctx.native_handle(), &e, event_data.data(),
                                          uint32_t(event_data.size()), &size);
            if (r == 0 && e.request == request) {
                polar::sdk::check(e.result);
                stream = e.stream;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        std::vector<polar_sample> buffer(8192);
        std::vector<uint8_t> frame(10 + 6 * 8);
        frame[0] = 2;
        frame[9] = 1;
        for (size_t i = 0; i < 8; i++) {
            put(frame, 10 + 6 * i, 100 + i, 2);
            put(frame, 12 + 6 * i, 200 + i, 2);
            put(frame, 14 + 6 * i, 1000, 2);
        }
        std::vector<double> processing, ready, latency;
        size_t reserve = size_t(seconds * 120 + 2);
        processing.reserve(reserve);
        ready.reserve(reserve * size_t(load) * 8);
        latency.reserve(ready.capacity());
        uint64_t timestamp = 700000000000000000ull, produced = 0, consumed = 0;
        std::atomic_bool done = false;
        std::atomic_int failure = 0;
        std::thread producer([&] {
            auto tick = std::chrono::steady_clock::now();
            while (!done) {
                for (int i = 0; i < load; i++) {
                    timestamp += 153846154;
                    put(frame, 1, timestamp, 8);
                    auto r = polar_sdk_replay_frame(ctx.native_handle(), stream, frame.data(),
                                                    uint32_t(frame.size()), polar_sdk_clock_ns());
                    if (r < 0) {
                        failure = r;
                        done = true;
                        break;
                    }
                    produced += 8;
                }
                tick += std::chrono::microseconds(153846);
                std::this_thread::sleep_until(tick);
            }
        });
        HANDLE timer = CreateWaitableTimerExW(
            nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (!timer)
            throw std::runtime_error("High resolution waitable timer unavailable");
        auto start = std::chrono::steady_clock::now(), tick = start;
        while (std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count() <
                   seconds &&
               !done) {
            auto before = polar_sdk_clock_ns();
            uint32_t n = 0;
            auto r = polar_sdk_read_samples(ctx.native_handle(), stream, buffer.data(),
                                            uint32_t(buffer.size()), &n);
            auto after = polar_sdk_clock_ns();
            if (r < 0) {
                failure = r;
                break;
            }
            processing.push_back((after - before) / 1e6);
            for (uint32_t i = 0; i < n; i++) {
                ready.push_back((buffer[i].ready_ns - buffer[i].received_ns) / 1e6);
                latency.push_back((after - buffer[i].received_ns) / 1e6);
            }
            consumed += n;
            tick += std::chrono::nanoseconds(8333333);
            auto remain = std::chrono::duration_cast<std::chrono::nanoseconds>(
                              tick - std::chrono::steady_clock::now())
                              .count();
            if (remain > 0) {
                LARGE_INTEGER due;
                due.QuadPart = -std::max<int64_t>(1, remain / 100);
                SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE);
                WaitForSingleObject(timer, INFINITE);
            }
        }
        CloseHandle(timer);
        done = true;
        producer.join();
        for (int i = 0; i < 100; i++) {
            uint32_t n = 0;
            polar_sdk_read_samples(ctx.native_handle(), stream, buffer.data(),
                                   uint32_t(buffer.size()), &n);
            consumed += n;
            if (consumed == produced)
                break;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        auto percentile = [](std::vector<double> &v, double p) {
            if (v.empty())
                return 0.;
            std::sort(v.begin(), v.end());
            return v[std::min(v.size() - 1, size_t(p * (v.size() - 1)))];
        };
        polar_stream_info info{};
        info.struct_size = sizeof(info);
        polar_sdk_stream_info(ctx.native_handle(), stream, &info);
        polar_stream_metrics metrics{};
        metrics.struct_size = sizeof(metrics);
        polar::sdk::check(polar_sdk_get_stream_metrics(ctx.native_handle(), stream, &metrics));
        PROCESS_MEMORY_COUNTERS_EX memory{};
        memory.cb = sizeof(memory);
        GetProcessMemoryInfo(GetCurrentProcess(),
                             reinterpret_cast<PROCESS_MEMORY_COUNTERS *>(&memory), sizeof(memory));
        std::ofstream out(output);
        out << "{\n\"mode\":\"native_replay_120hz_consumer\",\n\"seconds\":" << seconds
            << ",\n\"load\":" << load << ",\n\"produced\":" << produced
            << ",\n\"consumed\":" << consumed << ",\n\"skipped\":" << info.skipped_samples
            << ",\n\"ingress_dropped\":" << info.ingress_dropped << ",\n\"error\":" << failure
            << ",\n\"read_p50_ms\":" << percentile(processing, .5)
            << ",\n\"read_p95_ms\":" << percentile(processing, .95)
            << ",\n\"read_p99_ms\":" << percentile(processing, .99)
            << ",\n\"process_cpu_seconds\":" << (process_cpu_ns() - cpu_begin) / 1e9
            << ",\n\"peak_working_set_bytes\":" << memory.PeakWorkingSetSize
            << ",\n\"queue_high_watermark\":" << info.high_watermark
            << ",\n\"ingress_bytes\":" << metrics.ingress_bytes
            << ",\n\"decoder_handoff_bytes\":" << metrics.decoder_bytes
            << ",\n\"sample_queue_copy_bytes\":" << metrics.sample_queue_bytes
            << ",\n\"consumer_copy_bytes\":" << metrics.consumer_bytes
            << ",\n\"native_ready_p50_ms\":" << percentile(ready, .5)
            << ",\n\"native_ready_p95_ms\":" << percentile(ready, .95)
            << ",\n\"native_ready_p99_ms\":" << percentile(ready, .99)
            << ",\n\"consume_p50_ms\":" << percentile(latency, .5)
            << ",\n\"consume_p95_ms\":" << percentile(latency, .95)
            << ",\n\"consume_p99_ms\":" << percentile(latency, .99) << "\n}\n";
        std::cout << "samples=" << consumed << "/" << produced << " result=" << output << '\n';
        return failure || produced != consumed ? 1 : 0;
    } catch (const std::exception &e) {
        std::cerr << e.what() << '\n';
        return 2;
    }
}
