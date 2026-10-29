#include "services.hpp"
#include "user_id.pb.h"
#include <cmath>
#include <fstream>
#include <iostream>
#include <polar/polar_sdk.hpp>
#include <random>
#include <thread>
using namespace polar;
static int checks = 0;
static void test(bool ok, const char *message) {
    ++checks;
    if (!ok)
        throw std::runtime_error(message);
}
template <class F> static void rejects(F &&f) {
    bool bad = false;
    try {
        f();
    } catch (...) {
        bad = true;
    }
    test(bad, "Expected rejection");
}
static bytes file(const std::string &path) {
    std::ifstream f(path, std::ios::binary);
    test(bool(f), "Fixture file missing");
    return bytes(std::istreambuf_iterator<char>(f), {});
}
static bytes hex(std::string s) {
    bytes b;
    for (size_t i = 0; i < s.size();) {
        if (s[i] == ' ') {
            i++;
            continue;
        }
        b.push_back(uint8_t(std::stoul(s.substr(i, 2), nullptr, 16)));
        i += 2;
    }
    return b;
}
static void golden() {
    auto b = file("tests/native/fixtures/baseline-pmd.bin");
    size_t at = 0, frames = 0;
    auto scratch = std::make_unique<polar_sample[]>(max_samples);
    while (at < b.size()) {
        size_t n = size_t(u(b, at, 4));
        at += 4;
        auto frame = view(b).subspan(at, n);
        at += n;
        polar_decode_options o{sizeof(o), uint32_t(u(b, at, 4)), 0, 1, 0, 0};
        at += 4;
        o.factor = f32(b, at);
        at += 4;
        o.previous_timestamp = u(b, at, 8);
        at += 8;
        o.derived_methods = uint32_t(u(b, at, 4));
        at += 4;
        size_t count = size_t(u(b, at, 4));
        at += 4;
        size_t actual = decode(frame, o, {scratch.get(), max_samples});
        test(actual == count, "Golden sample count");
        for (size_t i = 0; i < count; i++) {
            auto time = u(b, at, 8);
            at += 8;
            size_t channels = size_t(u(b, at, 4));
            at += 4;
            if (scratch[i].timestamp_ns != time)
                std::cerr << "time mismatch frame " << frames << " sample " << i << " actual "
                          << scratch[i].timestamp_ns << " expected " << time << '\n';
            test(scratch[i].timestamp_ns == time, "Golden exact uint64 timestamp");
            test(scratch[i].channel_count == channels, "Golden channels");
            for (size_t c = 0; c < channels; c++) {
                double expected = f64(b, at);
                at += 8;
                test(scratch[i].values[c] == expected ||
                         (std::isnan(scratch[i].values[c]) && std::isnan(expected)),
                     "Golden measurement");
            }
        }
        ++frames;
    }
    std::cout << "golden_frames=" << frames << '\n';
}
static void framing() {
    for (size_t mtu : {2, 20, 229, 512})
        for (size_t n : {0, 1, 20, 511, 1024}) {
            bytes b(n);
            for (size_t i = 0; i < n; i++)
                b[i] = uint8_t(i);
            pftp_assembler a;
            for (auto &p : pftp_fragment(b, mtu))
                a.add(p);
            test(a.complete && a.data == b, "PFTP fragmentation");
        }
    rejects([] {
        pftp_assembler a;
        a.add(hex("12 01"));
    });
    rejects([] {
        pftp_assembler a;
        a.add(hex("00 67 00"));
    });
    test(pftp_query(5) == hex("05 80"), "PFTP query");
    test(pftp_request(hex("12"), hex("34")) == hex("01 00 12 34"), "PFTP request");
}
static void codecs() {
    auto key = hex("00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E FF");
    test(decrypt(2, key, key) == hex("60 08 6B DA 00 DB 42 62 34 60 27 43 71 A7 53 68"),
         "AES128 upstream golden");
    auto doubled = key;
    doubled.insert(doubled.end(), key.begin(), key.end());
    test(decrypt(3, doubled, key) == hex("C8 0D 56 BB 97 7A 42 5F 5A A1 CD FC 24 A2 78 12"),
         "AES256 upstream golden");
    rejects([&] { decrypt(2, key, hex("00")); });
    bytes data(102400, 42);
    test(gzip_decode(gzip_encode(data)) == data, "Gzip roundtrip");
    rejects([&] { gzip_decode(gzip_encode(data), 100); });
    auto record_key = key;
    record_key.back() = 15;
    for (auto path :
         {"tests/native/fixtures/PmdTests-0.bin", "tests/native/fixtures/PmdTests-1.bin"}) {
        auto b = file(path);
        auto record = decode_recording(b, 2, record_key);
        test(!record.samples.empty(), "Encrypted upstream offline recording");
    }
    bytes cfg(16);
    cfg[0] = 3;
    cfg[6] = 2;
    polar_transform t{sizeof(t), POLAR_CODEC_WATCHFACE_SET, cfg.data(), uint32_t(cfg.size()), 0};
    auto encoded = transform(t);
    t.operation = POLAR_CODEC_WATCHFACE_GET;
    t.data = encoded.data();
    t.data_length = uint32_t(encoded.size());
    test(transform(t) == cfg, "Watchface FlatBuffer/KVTX");
    rejects([] { kvtx_get(hex("00 01")); });
    data::PbUserIdentifier user;
    user.set_master_identifier(UINT64_MAX);
    auto raw = message_bytes(user);
    raw.insert(raw.end(), {0xf8, 0x07, 0x7b});
    auto m = message_parse("data.PbUserIdentifier", raw);
    test(message_bytes(*m) == raw, "Protobuf unknown field preservation");
    polar_message id;
    test(polar_sdk_message_parse("data.PbUserIdentifier", 21, raw.data(), uint32_t(raw.size()),
                                 &id) == 0,
         "C ABI message parse");
    polar_field v{};
    v.struct_size = sizeof(v);
    uint32_t n;
    test(polar_sdk_message_field(id, "master_identifier", 17, 0, &v, nullptr, 0, &n) == 0 &&
             v.present && v.unsigned_value == UINT64_MAX,
         "C ABI uint64 precision");
    test(polar_sdk_message_destroy(id) == 0, "Message dispose");
    test(polar_sdk_message_destroy(id) == POLAR_INVALID_HANDLE, "Stale message rejected");
}
static polar_stream open(polar::sdk::context &c) {
    polar_command cmd{};
    cmd.operation = POLAR_OP_REPLAY_OPEN;
    cmd.measurement = POLAR_ACC;
    auto req = c.submit(cmd);
    for (int i = 0; i < 5000; i++) {
        polar_event e{};
        e.struct_size = sizeof(e);
        uint8_t buffer[1024];
        uint32_t n;
        auto r = polar_sdk_poll_event(c.native_handle(), &e, buffer, sizeof(buffer), &n);
        if (r == 0 && e.request == req) {
            test(e.result == 0, "Open replay");
            return e.stream;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    throw std::runtime_error("Replay open timeout");
}
static void boundary_tests() {
    test(polar_sdk_abi_version() == 2, "ABI version");
    test(polar_sdk_destroy(UINT64_MAX) == POLAR_INVALID_HANDLE, "Stale context");
    for (uint32_t policy : {POLAR_LATEST, POLAR_LOSSLESS}) {
        polar::sdk::context c(policy, 2);
        auto s = open(c);
        auto frame = hex(
            "02 00 94 35 77 00 00 00 00 01 01 00 02 00 03 00 04 00 05 00 06 00 07 00 08 00 09 00");
        test(polar_sdk_replay_frame(c.native_handle(), s, frame.data(), uint32_t(frame.size()),
                                    0) == 0,
             "Queue replay frame");
        polar_stream_info info{};
        info.struct_size = sizeof(info);
        for (int i = 0; i < 500; i++) {
            polar_sdk_stream_info(c.native_handle(), s, &info);
            if (info.produced_samples || info.error)
                break;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        if (policy == POLAR_LATEST)
            test(info.skipped_samples == 1 && info.queued_samples == 2,
                 "Latest policy reports skips");
        else
            test(info.error == POLAR_QUEUE_OVERFLOW && info.state == 1 && info.queued_samples == 0,
                 "Lossless overflow faults atomically");
        polar_sample out[2];
        uint32_t n;
        polar_sdk_read_samples(c.native_handle(), s, out, 2, &n);
        test(n == (policy == 0 ? 2u : 0u), "Batch copy");
    }
    polar_decode_options o{sizeof(o), 52, 0, 1, 0, 0};
    auto frame = hex("02 00 94 35 77 00 00 00 00 01 01 00 02 00 03 00");
    uint32_t n;
    test(polar_sdk_decode_frame(frame.data(), uint32_t(frame.size()), &o, nullptr, 0, &n) ==
                 POLAR_BUFFER_TOO_SMALL &&
             n == 1,
         "Decode capacity negotiation");
}
static void mutate() {
    std::mt19937 rng(43);
    auto samples = std::make_unique<polar_sample[]>(max_samples);
    polar_decode_options o{sizeof(o), 52, 0, 1, 0, 0};
    auto base = hex("02 00 94 35 77 00 00 00 00 81 01 00 02 00 03 00 04 01 F1 08");
    for (int i = 0; i < 20000; i++) {
        auto b = base;
        int mutations = 1 + int(rng() % 5);
        for (int j = 0; j < mutations; j++)
            b[rng() % b.size()] = uint8_t(rng());
        if (i % 3 == 0)
            b.resize(rng() % b.size());
        try {
            decode(b, o, {samples.get(), max_samples});
        } catch (const failure &) {
        }
    }
    std::cout << "mutated_inputs=20000\n";
}
void service_tests();
void lifetime_tests();
int main() {
    try {
        golden();
        framing();
        codecs();
        boundary_tests();
        service_tests();
        lifetime_tests();
        mutate();
        std::cout << "PASS checks=" << checks << " (assertion count is not legacy parity)\n";
        return 0;
    } catch (const std::exception &e) {
        std::cerr << "FAIL after " << checks << " checks: " << e.what() << '\n';
        return 1;
    }
}
