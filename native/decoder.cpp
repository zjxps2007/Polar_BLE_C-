// Protocol behavior ported from Polar BLE SDK and f73aca5. See LICENSES.
#include "common.hpp"
#include <cmath>
namespace polar {
uint64_t sample_time(uint64_t previous, uint64_t last, uint32_t count, uint32_t rate, uint32_t i,
                     bool floor) {
    require(count > 0 && i < count, "Invalid sample count");
    if (previous) {
        require(last > previous || (floor && last == previous), "Non-increasing device timestamp");
        const uint64_t d = last - previous, k = uint64_t(i) + 1, rem = (d % count) * k;
        uint64_t v = previous + (d / count) * k + rem / count, r = rem % count;
        if (!floor && (r > count / 2.0 || (2 * r == count && (v & 1))))
            ++v;
        return v;
    }
    require(rate > 0, "First frame requires sample rate");
    const uint64_t n = 1000000000ull * (count - 1 - i), q = n / rate, r = n % rate;
    require(last >= q + (r != 0), "Timestamp precedes device epoch");
    uint64_t v = last - q;
    if (r * 2 > rate || (r * 2 == rate && (v & 1)))
        --v;
    return v;
}
static int width(uint8_t t) {
    switch (t) {
    case 0:
    case 1:
    case 2:
    case 9:
        return 2;
    case 3:
    case 5:
    case 10:
    case 11:
        return 4;
    case 4:
    case 7:
    case 8:
    case 12:
        return 1;
    default:
        throw failure(POLAR_PROTOCOL_ERROR, "Unknown/security integer setting");
    }
}
std::vector<setting> parse_settings(view b) {
    std::vector<setting> out;
    size_t o = 0;
    while (o < b.size()) {
        bounds(b, o, 2);
        setting s{b[o++], {}};
        auto count = b[o++];
        auto w = width(s.kind);
        for (int j = 0; j < count; j++, o += w)
            s.values.push_back(uint32_t(u(b, o, w)));
        out.push_back(std::move(s));
    }
    return out;
}
bytes select_settings(view b, bool maximum) {
    bytes out;
    for (auto &s : parse_settings(b)) {
        if (s.values.empty() || s.kind == 5 || s.kind == 10)
            continue;
        out.push_back(s.kind);
        if (s.kind == 7) {
            out.push_back(uint8_t(s.values.size()));
            for (auto v : s.values) {
                require(v < 16, "Derived method id");
                out.push_back(uint8_t(v));
            }
        } else {
            out.push_back(1);
            le(out, maximum ? *std::max_element(s.values.begin(), s.values.end()) : s.values[0],
               width(s.kind));
        }
    }
    return out;
}
uint32_t get_setting(view b, uint8_t kind, uint32_t fallback) {
    for (auto &s : parse_settings(b))
        if (s.kind == kind && !s.values.empty())
            return s.values[0];
    return fallback;
}
static void metadata(polar_sample &s, view b) {
    require(b.size() <= sizeof(s.metadata), "Sample metadata exceeds ABI capacity");
    s.metadata_length = uint32_t(b.size());
    std::copy(b.begin(), b.end(), s.metadata);
}
static int32_t scale_integer(int32_t v, float f) {
    float r = v * f;
    require(std::isfinite(r) && double(r) >= INT32_MIN && double(r) <= INT32_MAX,
            "Scaled integer overflow");
    return int32_t(r);
}
size_t decode(view frame, const polar_decode_options &opt, std::span<polar_sample> out) {
    bounds(frame, 0, 10);
    const uint32_t type = frame[0] & 63, format = frame[9] & 127;
    bool compressed = (frame[9] & 128) != 0;
    uint64_t last = u(frame, 1, 8);
    view d = frame.subspan(10);
    require(!d.empty(), "Empty PMD frame");
    size_t n = 0;
    auto add = [&]() -> polar_sample & {
        require(n < out.size(), "Decoded frame exceeds configured capacity");
        auto &s = out[n++];
        s = {};
        s.timestamp_ns = last;
        s.received_ns = opt.received_ns;
        s.measurement = type;
        s.format = format;
        s.flags = compressed ? 0x80000000u : 0;
        return s;
    };
    auto fixed = [&](size_t w, auto map) {
        require(w > 0 && d.size() % w == 0, "Partial PMD sample");
        for (size_t o = 0; o < d.size(); o += w) {
            auto &s = add();
            s.timestamp_ns = sample_time(opt.previous_timestamp, last, uint32_t(d.size() / w),
                                         opt.sample_rate, uint32_t(o / w));
            map(s, o);
        }
    };
    auto delta = [&](uint32_t channels, uint32_t resolution, bool sign, auto map) {
        require(channels > 0 && channels <= 32, "Channel count");
        std::array<int32_t, 32> prev{};
        size_t w = (resolution + 7) / 8, o = w * channels;
        bounds(d, 0, o);
        for (uint32_t c = 0; c < channels; c++) {
            uint32_t v = uint32_t(u(d, c * w, w));
            if (sign && resolution < 32 && (v & (1u << (resolution - 1))))
                v |= ~0u << resolution;
            prev[c] = std::bit_cast<int32_t>(v);
        }
        map(add(), prev);
        while (o < d.size()) {
            bounds(d, o, 2);
            uint32_t bits = d[o++], count = d[o++];
            require(bits > 0 && bits <= 32 && count > 0, "Invalid delta block");
            size_t len = (size_t(bits) * count * channels + 7) / 8;
            bounds(d, o, len);
            size_t bit = 0;
            for (uint32_t j = 0; j < count; j++) {
                for (uint32_t c = 0; c < channels; c++) {
                    size_t byte = bit / 8;
                    uint32_t shift = uint32_t(bit % 8);
                    uint64_t window = 0;
                    size_t read = std::min<size_t>(5, len - byte);
                    for (size_t k = 0; k < read; k++)
                        window |= uint64_t(d[o + byte + k]) << (8 * k);
                    uint32_t v = uint32_t(window >> shift);
                    if (bits < 32) {
                        v &= (1u << bits) - 1;
                        if (v & (1u << (bits - 1)))
                            v |= ~0u << bits;
                    }
                    prev[c] = std::bit_cast<int32_t>(uint32_t(prev[c]) + v);
                    bit += bits;
                }
                map(add(), prev);
            }
            o += len;
        }
        for (uint32_t i = 0; i < n; i++)
            out[i].timestamp_ns =
                sample_time(opt.previous_timestamp, last, uint32_t(n), opt.sample_rate, i);
    };
    if (type == POLAR_DERIVED || (type == POLAR_ACC && opt.derived_methods)) {
        require(!compressed && format <= 2, "Unsupported derived format");
        size_t o = 0;
        uint32_t methods = opt.derived_methods;
        bool spec = type == POLAR_DERIVED;
        if (spec) {
            bounds(d, 0, 4);
            require(d[0] == 0 && (d[1] & 63) == 2, "Derived source");
            methods = uint32_t(u(d, 2, 2));
            o = 4;
        }
        require(methods && !(methods & ~1023u), "Unknown derived methods");
        uint32_t components = spec ? 4 : 3, channels = 0;
        for (uint32_t id = 0; id < 10; id++)
            if (methods & (1u << id))
                channels += id <= components ? 3 : 1;
        size_t w = format == 0 ? 2 : format == 1 ? 4 : 3, sw = w * channels;
        require(d.size() > o && (d.size() - o) % sw == 0, "Truncated derived frame");
        uint32_t count = uint32_t((d.size() - o) / sw);
        for (uint32_t i = 0; i < count; i++) {
            auto &s = add();
            s.channel_count = channels;
            s.integers[0] = methods;
            s.timestamp_ns = count == 1 || !opt.previous_timestamp
                                 ? last
                                 : sample_time(opt.previous_timestamp, last, count, 1, i, true);
            uint32_t c = 0;
            for (uint32_t id = 0; id < 10; id++)
                if (methods & (1u << id))
                    for (uint32_t k = 0; k < (id <= components ? 3u : 1u); k++, o += w)
                        s.values[c++] = id >= 4 ? double(u(d, o, w)) : double(si(d, o, w));
        }
    } else
        switch (type) {
        case POLAR_ECG:
            require(!compressed && format <= 3, "Unsupported ECG format");
            fixed(format == 3 ? 7 : 3, [&](auto &s, size_t o) {
                s.channel_count = format == 3 ? 2 : 1;
                if (format == 0)
                    s.values[0] = double(si(d, o, 3));
                else if (format == 1) {
                    s.values[0] = d[o] | ((d[o + 1] & 63) << 8);
                    s.integers[0] = d[o + 2] & 1;
                    s.integers[1] = (d[o + 2] >> 1) & 3;
                    s.integers[2] = (d[o + 2] >> 3) & 3;
                } else if (format == 2) {
                    s.values[0] = d[o] | (d[o + 1] << 8) | ((d[o + 2] & 3) << 16);
                    s.integers[0] = (d[o + 2] >> 2) & 7;
                    s.integers[1] = d[o + 2] >> 5;
                } else {
                    s.values[0] = double(si(d, o, 3));
                    s.values[1] = double(si(d, o + 3, 3));
                    s.integers[0] = d[o + 6];
                }
            });
            break;
        case POLAR_ACC:
            if (compressed) {
                require(format <= 1, "Unsupported ACC delta format");
                delta(3, 16, true, [&](auto &s, auto &v) {
                    s.channel_count = 3;
                    for (int c = 0; c < 3; c++)
                        s.values[c] =
                            scale_integer(v[c], opt.factor * (format == 0 ? 1000.f : 1.f));
                });
            } else {
                require(format <= 2, "Unsupported ACC raw format");
                fixed((format + 1) * 3, [&](auto &s, size_t o) {
                    s.channel_count = 3;
                    for (int c = 0; c < 3; c++)
                        s.values[c] = double(si(d, o + c * (format + 1), format + 1));
                });
            }
            break;
        case POLAR_GYRO:
            require(compressed && format <= 1, "Unsupported gyro format");
            delta(3, format == 0 ? 16 : 32, format == 0, [&](auto &s, auto &v) {
                s.channel_count = 3;
                for (int c = 0; c < 3; c++)
                    s.values[c] =
                        (format == 0 ? float(v[c]) : std::bit_cast<float>(v[c])) * opt.factor;
            });
            break;
        case POLAR_MAG:
            require(compressed && format <= 1, "Unsupported magnetometer format");
            delta(format == 0 ? 3 : 4, 16, true, [&](auto &s, auto &v) {
                s.channel_count = 3;
                for (int c = 0; c < 3; c++)
                    s.values[c] = v[c] * opt.factor / (format == 0 ? 1.f : 1000.f);
                s.integers[0] = format == 0 ? -1 : v[3];
            });
            break;
        case POLAR_TEMP:
        case POLAR_SKIN_TEMP:
        case POLAR_PRESSURE:
            require(format == 0, "Unsupported temperature/pressure format");
            if (compressed)
                delta(1, 32, false, [&](auto &s, auto &v) {
                    s.channel_count = 1;
                    s.values[0] = std::bit_cast<float>(v[0]) * opt.factor;
                });
            else
                fixed(4, [&](auto &s, size_t o) {
                    s.channel_count = 1;
                    s.values[0] = f32(d, o);
                });
            break;
        case POLAR_PPI: {
            require(!compressed && format == 0 && d.size() % 6 == 0, "Invalid PPI frame");
            uint64_t current = last;
            for (size_t o = 0; o < d.size(); o += 6) {
                auto &s = add();
                s.channel_count = 3;
                s.values[0] = d[o];
                s.values[1] = double(u(d, o + 1, 2));
                s.values[2] = double(u(d, o + 3, 2));
                s.flags = d[o + 5];
            }
            for (size_t i = n; i-- > 0;) {
                out[i].timestamp_ns = current;
                if (last && i > 0) {
                    uint64_t diff = uint64_t(out[i].values[1]) * 1000000;
                    require(current >= diff, "PPI timestamp underflow");
                    current -= diff;
                }
            }
            break;
        }
        case POLAR_OFFLINE_HR:
            require(!compressed && format <= 1, "Unsupported offline HR");
            {
                size_t w = format == 0 ? 1 : 3;
                require(d.size() % w == 0, "Partial offline HR");
                for (size_t o = 0; o < d.size(); o += w) {
                    auto &s = add();
                    s.timestamp_ns = 0;
                    s.channel_count = uint32_t(w);
                    for (size_t c = 0; c < w; c++)
                        s.values[c] = d[o + c];
                }
            }
            break;
        case POLAR_PPG:
            if (compressed) {
                uint32_t ch = format == 0    ? 4
                              : format == 7  ? 17
                              : format == 8  ? 25
                              : format == 10 ? 21
                              : format == 13 ? 3
                                             : 0;
                require(ch != 0, "Unsupported PPG delta");
                delta(ch, 24, true, [&](auto &s, auto &v) {
                    s.channel_count = format == 0 ? ch : ch - 1;
                    for (uint32_t c = 0; c < s.channel_count; c++)
                        s.values[c] = scale_integer(v[c], opt.factor);
                    if (format != 0)
                        s.integers[0] = v[ch - 1] & 0xffffff;
                });
            } else if (format == 0)
                fixed(12, [&](auto &s, size_t o) {
                    s.channel_count = 4;
                    for (int c = 0; c < 4; c++)
                        s.values[c] = double(si(d, o + c * 3, 3));
                });
            else if (format == 4 || format == 9 || format == 14) {
                size_t count = format == 14 ? 1 : 12;
                fixed(count * 3, [&](auto &s, size_t o) { metadata(s, d.subspan(o, count * 3)); });
            } else if (format == 5)
                fixed(4, [&](auto &s, size_t o) {
                    s.channel_count = 1;
                    s.values[0] = double(u(d, o, 4));
                });
            else if (format == 6)
                fixed(8, [&](auto &s, size_t o) { s.identifier = u(d, o, 8); });
            else
                throw failure(POLAR_NOT_SUPPORTED, "Unsupported PPG raw format");
            break;
        case POLAR_LOCATION:
            require(!compressed && format <= 3, "Unsupported location format");
            if (format == 0)
                fixed(51, [&](auto &s, size_t o) {
                    s.channel_count = 9;
                    s.values[0] = f64(d, o);
                    s.values[1] = f64(d, o + 8);
                    s.values[2] = u(d, o + 24, 4) / 10.;
                    for (int c = 0; c < 4; c++)
                        s.values[3 + c] = f32(d, o + 28 + 4 * c);
                    s.values[7] = u(d, o + 44, 2) / 100.;
                    s.values[8] = u(d, o + 46, 2) / 100.;
                    s.integers[0] = int64_t(u(d, o + 16, 2));
                    s.integers[1] = d[o + 18];
                    s.integers[2] = d[o + 19];
                    s.integers[3] = int64_t(u(d, o + 20, 4));
                    s.integers[4] = d[o + 48];
                    s.integers[5] = int8_t(d[o + 49]);
                    s.integers[6] = d[o + 50];
                });
            else if (format == 1)
                fixed(6, [&](auto &s, size_t o) {
                    s.channel_count = 3;
                    s.values[0] = u(d, o, 2) / 100.;
                    s.values[1] = double(si(d, o + 2, 2));
                    s.values[2] = d[o + 4];
                    s.integers[0] = d[o + 5];
                });
            else if (format == 2)
                fixed(41, [&](auto &s, size_t o) {
                    s.channel_count = 1;
                    s.values[0] = d[o + 40];
                    metadata(s, d.subspan(o, 40));
                });
            else {
                size_t o = 0;
                while (o < d.size()) {
                    bounds(d, o, 7);
                    auto &s = add();
                    s.integers[0] = int64_t(u(d, o, 4));
                    auto len = size_t(u(d, o + 4, 2));
                    s.integers[1] = d[o + 6];
                    o += 7;
                    bounds(d, o, len);
                    metadata(s, d.subspan(o, len));
                    o += len;
                }
            }
            break;
        default:
            throw failure(POLAR_NOT_SUPPORTED, "Unsupported PMD measurement");
        }
    const auto ready = clock_ns();
    for (size_t i = 0; i < n; i++)
        out[i].ready_ns = ready;
    return n;
}
size_t decode_hr(view b, uint64_t received, std::span<polar_sample> out) {
    bounds(b, 0, 2);
    uint32_t flags = b[0];
    size_t w = flags & 1 ? 2 : 1, o = 1 + w;
    auto hr = u(b, 1, w);
    int64_t energy = -1;
    if (flags & 8) {
        energy = int64_t(u(b, o, 2));
        o += 2;
    }
    require((flags & 16) || o == b.size(), "Unexpected HR tail");
    require((b.size() - o) % 2 == 0, "Truncated HR interval");
    size_t count = std::max<size_t>(1, (b.size() - o) / 2);
    require(count <= out.size(), "HR output capacity");
    for (size_t i = 0; i < count; i++) {
        auto &s = out[i];
        s = {};
        s.measurement = POLAR_HR;
        s.flags = flags;
        s.received_ns = received;
        s.ready_ns = clock_ns();
        s.values[0] = double(hr);
        s.channel_count = 1;
        s.integers[0] = energy;
        if (o < b.size()) {
            auto rr = u(b, o, 2);
            o += 2;
            s.values[1] = double(rr);
            s.values[2] = double((rr * 1000 + 512) / 1024);
            s.channel_count = 3;
        }
    }
    return count;
}
size_t decode_standard(uint32_t type, view b, uint64_t received, std::span<polar_sample> out) {
    if (type == POLAR_HR)
        return decode_hr(b, received, out);
    require(!out.empty(), "Output sample capacity");
    auto &s = out[0];
    s = {};
    s.measurement = type;
    s.received_ns = received;
    s.ready_ns = clock_ns();
    if (type == POLAR_BATTERY_LEVEL) {
        require(b.size() == 1 && b[0] <= 100, "Invalid battery level");
        s.channel_count = 1;
        s.values[0] = b[0];
    } else if (type == POLAR_BATTERY_STATUS) {
        bounds(b, 0, 2);
        s.flags = b[0];
        s.integers[0] = (b[1] >> 5) & 3;
        s.integers[1] = b[1] & 1;
        s.integers[2] = (b[1] >> 1) & 3;
        s.integers[3] = (b[1] >> 3) & 3;
        metadata(s, b);
    } else if (type == POLAR_HR_ADVERTISEMENT) {
        size_t at = 0;
        while (at < b.size() && (b[at] & 64)) {
            bounds(b, at, 2);
            size_t len = b[at + 1] + 2;
            bounds(b, at, len);
            at += len;
        }
        if (at == b.size())
            return 0;
        b = b.subspan(at);
        require(b.size() == 3 || b.size() == 4, "Invalid Polar HR advertisement");
        s.flags = b[0];
        s.integers[0] = (b[0] >> 2) & 7;
        s.integers[1] = b[1];
        s.channel_count = 2;
        s.values[0] = b[2];
        s.values[1] = b.size() == 4 ? b[3] : b[2];
    } else if (type == POLAR_THERMOMETER) {
        bounds(b, 0, 5);
        s.flags = b[0];
        auto raw = u(b, 1, 3);
        double v;
        switch (raw) {
        case 0x7ffffe:
            v = INFINITY;
            s.integers[7] = 1;
            break;
        case 0x800002:
            v = -INFINITY;
            s.integers[7] = 2;
            break;
        case 0x7fffff:
            v = NAN;
            s.integers[7] = 3;
            break;
        case 0x800000:
            v = NAN;
            s.integers[7] = 4;
            break;
        case 0x800001:
            v = NAN;
            s.integers[7] = 5;
            break;
        default:
            v = std::round(si(b, 1, 3) * std::pow(10., int8_t(b[4])) * 100) / 100;
        }
        s.channel_count = 2;
        s.values[0] = b[0] & 1 ? (v - 32) * 5 / 9 : v;
        s.values[1] = b[0] & 1 ? v : v * 9 / 5 + 32;
        size_t at = 5;
        if (b[0] & 2) {
            bounds(b, at, 7);
            s.integers[0] = int64_t(u(b, at, 2));
            for (int i = 1; i < 6; i++)
                s.integers[i] = b[at + 1 + i];
            if (s.integers[0] && s.integers[1] && s.integers[2]) {
                auto date = std::chrono::year{int(s.integers[0])} / unsigned(s.integers[1]) /
                            unsigned(s.integers[2]);
                require(date.ok() && s.integers[3] < 24 && s.integers[4] < 60 && s.integers[5] < 60,
                        "Invalid thermometer local date");
            }
            at += 7;
        }
        s.integers[6] = -1;
        if (b[0] & 4) {
            bounds(b, at, 1);
            s.integers[6] = b[at++];
        }
        require(at == b.size(), "Thermometer trailing bytes");
        metadata(s, b);
    } else
        throw failure(POLAR_NOT_SUPPORTED, "Unknown standard notification");
    return 1;
}
} // namespace polar
