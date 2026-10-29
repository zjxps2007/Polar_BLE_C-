#include "common.hpp"
#include <windows.h>
#include <atomic>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <new>
static std::atomic_uint64_t allocated{0}, allocations{0};
static std::atomic_bool measuring{false};
void *operator new(std::size_t n) {
    if (auto p = std::malloc(n ? n : 1)) {
        if (measuring) {
            allocated += n;
            ++allocations;
        }
        return p;
    }
    throw std::bad_alloc();
}
void operator delete(void *p) noexcept {
    std::free(p);
}
void operator delete(void *p, std::size_t) noexcept {
    std::free(p);
}
void *operator new[](std::size_t n) {
    return ::operator new(n);
}
void operator delete[](void *p) noexcept {
    ::operator delete(p);
}
void operator delete[](void *p, std::size_t) noexcept {
    ::operator delete(p);
}
int main(int argc, char **argv) {
    using namespace polar;
    bytes frame(58);
    frame[0] = 2;
    frame[9] = 1;
    const uint64_t timestamp = 700000000000000000ull;
    for (int i = 0; i < 8; i++)
        frame[1 + i] = uint8_t(timestamp >> (i * 8));
    for (int i = 0; i < 8; i++) {
        frame[10 + i * 6] = uint8_t(100 + i);
        frame[12 + i * 6] = uint8_t(200 + i);
        frame[14 + i * 6] = 0xe8;
        frame[15 + i * 6] = 3;
    }
    polar_decode_options options{sizeof(options), 52, 0, 1, 0, 0};
    polar_sample samples[8];
    for (int i = 0; i < 10000; i++)
        decode(frame, options, samples);
    constexpr int count = 200000;
    std::vector<double> times(count);
    LARGE_INTEGER frequency, start, stop, before, after;
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&start);
    measuring = true;
    for (int i = 0; i < count; i++) {
        QueryPerformanceCounter(&before);
        auto n = decode(frame, options, samples);
        QueryPerformanceCounter(&after);
        times[i] = (after.QuadPart - before.QuadPart) * 1e9 / double(frequency.QuadPart);
        if (n != 8)
            return 1;
    }
    measuring = false;
    QueryPerformanceCounter(&stop);
    std::sort(times.begin(), times.end());
    std::ofstream out(argc > 1 ? argv[1] : "native-parser.json");
    out << "{\n\"scope\":\"PMD raw ACC decode only, eight samples, warm, no Unity or "
           "BLE\",\n\"iterations\":"
        << count << ",\n\"seconds\":" << double(stop.QuadPart - start.QuadPart) / frequency.QuadPart
        << ",\n\"ns_p50\":" << times[count / 2] << ",\n\"ns_p95\":" << times[int(count * .95)]
        << ",\n\"ns_p99\":" << times[int(count * .99)]
        << ",\n\"cpp_new_bytes_per_frame\":" << allocated / double(count)
        << ",\n\"cpp_new_calls_per_frame\":" << allocations / double(count) << "\n}\n";
    std::cout << "native parser p99=" << times[int(count * .99)] << "ns C++ new bytes=" << allocated
              << '\n';
}
