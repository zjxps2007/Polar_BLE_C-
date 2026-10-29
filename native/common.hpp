#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <polar/polar_sdk.h>
#include <span>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
namespace polar {
using bytes = std::vector<uint8_t>;
using view = std::span<const uint8_t>;
struct failure : std::runtime_error {
    polar_result code;
    int32_t hresult = 0;
    uint32_t att = 0, stage = 0;
    failure(polar_result c, const std::string &s) : std::runtime_error(s), code(c) {}
};
inline void require(bool b, const char *s) {
    if (!b)
        throw failure(POLAR_PROTOCOL_ERROR, s);
}
inline void bounds(view b, size_t o, size_t n) {
    require(o <= b.size() && n <= b.size() - o, "Truncated binary payload");
}
inline uint64_t u(view b, size_t o, size_t n) {
    bounds(b, o, n);
    require(n <= 8, "Integer width");
    uint64_t v = 0;
    for (size_t i = 0; i < n; i++)
        v |= uint64_t(b[o + i]) << (8 * i);
    return v;
}
inline int64_t si(view b, size_t o, size_t n) {
    auto v = u(b, o, n);
    if (n < 8 && (v & (uint64_t(1) << (n * 8 - 1))))
        v |= ~uint64_t(0) << (n * 8);
    return std::bit_cast<int64_t>(v);
}
inline float f32(view b, size_t o) {
    return std::bit_cast<float>(uint32_t(u(b, o, 4)));
}
inline double f64(view b, size_t o) {
    return std::bit_cast<double>(u(b, o, 8));
}
inline void le(bytes &b, uint64_t v, size_t n) {
    for (size_t i = 0; i < n; i++)
        b.push_back(uint8_t(v >> (8 * i)));
}
inline uint64_t clock_ns() {
    return uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(
                        std::chrono::steady_clock::now().time_since_epoch())
                        .count());
}
inline bytes copy(view b) {
    return bytes(b.begin(), b.end());
}
constexpr size_t max_packet = 4096;
constexpr size_t max_samples = 4096;
struct setting {
    uint8_t kind;
    std::vector<uint32_t> values;
};
std::vector<setting> parse_settings(view);
bytes select_settings(view, bool maximum);
uint32_t get_setting(view, uint8_t, uint32_t fallback = 0);
size_t decode(view, const polar_decode_options &, std::span<polar_sample>);
size_t decode_hr(view, uint64_t, std::span<polar_sample>);
size_t decode_standard(uint32_t, view, uint64_t, std::span<polar_sample>);
uint64_t sample_time(uint64_t previous, uint64_t last, uint32_t count, uint32_t rate,
                     uint32_t index, bool floor = false);
bytes decrypt(uint32_t strategy, view key, view ciphertext);
bytes gzip_decode(view, size_t limit = 64 * 1024 * 1024);
bytes gzip_encode(view);
struct pftp_assembler {
    bytes data;
    uint8_t sequence = 0;
    bool started = false, complete = false;
    size_t limit = 64 * 1024 * 1024;
    bool add(view packet);
};
std::vector<bytes> pftp_fragment(view, size_t mtu);
bytes pftp_request(view operation, view data = {});
bytes pftp_query(uint16_t, view = {});
} // namespace polar
