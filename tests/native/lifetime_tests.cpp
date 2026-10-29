#include "common.hpp"
#include <polar/polar_services.h>
#include <polar/polar_sdk.hpp>
#include <iostream>
#include <set>
#include <thread>
using namespace polar;
static void check(bool b, const char *m) {
    if (!b)
        throw std::runtime_error(m);
}
static polar_event completion(polar_context c, uint64_t request) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (std::chrono::steady_clock::now() < deadline) {
        polar_event e{};
        e.struct_size = sizeof(e);
        uint8_t buffer[4096];
        uint32_t n = 0;
        auto r = polar_sdk_poll_event(c, &e, buffer, sizeof(buffer), &n);
        if (r == 0 && e.request == request)
            return e;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    throw std::runtime_error("Native completion timeout");
}
void lifetime_tests() {
    {
        auto rec = [](uint64_t timestamp, uint32_t samples) {
            bytes b(45 + 10 + samples * 6);
            b[0] = 0;
            auto put = [&](size_t at, uint64_t value, size_t n) {
                for (size_t i = 0; i < n; i++)
                    b[at + i] = uint8_t(value >> (8 * i));
            };
            put(1, 0x3d7c4c2b, 4);
            put(5, 1, 4);
            put(13, 42, 4);
            std::memcpy(b.data() + 17, "2026-10-29T12:34:56", 19);
            b[37] = 4;
            b[38] = 0;
            b[39] = 1;
            b[40] = 52;
            b[42] = 0;
            put(43, 10 + samples * 6, 2);
            b[45] = 2;
            put(46, timestamp, 8);
            b[54] = 1;
            return b;
        };
        auto a = rec(2000000000, 1), b = rec(3000000000, 2);
        polar_recording_part parts[]{{a.data(), uint32_t(a.size()), 0},
                                     {b.data(), uint32_t(b.size()), 0}};
        polar_recording_options options{sizeof(options), 0, POLAR_ACC, 0, nullptr, 0, 0};
        polar_offline_info info{};
        info.struct_size = sizeof(info);
        uint32_t n = 0;
        check(polar_sdk_decode_recordings(parts, 2, &options, &info, nullptr, 0, &n) ==
                      POLAR_BUFFER_TOO_SMALL &&
                  n == 3,
              "Split REC sizing");
        polar_sample output[3];
        check(polar_sdk_decode_recordings(parts, 2, &options, &info, output, 3, &n) == 0 &&
                  output[1].timestamp_ns == 2500000000ull,
              "Split REC previous timestamp interpolation");
        b[5] = 2;
        output[0].identifier = 123;
        check(polar_sdk_decode_recordings(parts, 2, &options, &info, output, 3, &n) ==
                      POLAR_PROTOCOL_ERROR &&
                  output[0].identifier == 123,
              "Split REC header mismatch changed output");
    }
    polar::sdk::context ctx(POLAR_LOSSLESS, 256);
    for (int i = 0; i < 80; i++) {
        polar_command cmd{};
        cmd.operation = POLAR_OP_REPLAY_OPEN;
        cmd.measurement = POLAR_ACC;
        auto event = completion(ctx.native_handle(), ctx.submit(cmd));
        check(event.result == 0, "Replay open failed");
        auto stream = event.stream;
        check(polar_sdk_release_stream(ctx.native_handle(), stream) == POLAR_BUSY,
              "Active release must reject");
        bytes frame{2, 0, 148, 53, 119, 0, 0, 0, 0, 1, 1, 0, 2, 0, 3, 0};
        for (int j = 0; j < 100; j++) {
            uint64_t timestamp = 2000000000ull + uint64_t(j) * 20000000;
            for (int k = 0; k < 8; k++)
                frame[1 + k] = uint8_t(timestamp >> (8 * k));
            check(polar_sdk_replay_frame(ctx.native_handle(), stream, frame.data(),
                                         uint32_t(frame.size()), 0) == 0,
                  "Replay enqueue");
        }
        cmd.operation = POLAR_OP_REPLAY_CLOSE;
        cmd.stream = stream;
        event = completion(ctx.native_handle(), ctx.submit(cmd));
        check(event.result == 0, "Stop completion");
        polar_stream_info info{};
        info.struct_size = sizeof(info);
        check(polar_sdk_stream_info(ctx.native_handle(), stream, &info) == 0 &&
                  info.produced_samples == 100 && info.pending_frames == 0,
              "Stop lost accepted frames");
        polar_sample batch[100];
        uint32_t n = 0;
        check(polar_sdk_read_samples(ctx.native_handle(), stream, batch, 100, &n) == 0 && n == 100,
              "Stop drain");
        check(polar_sdk_release_stream(ctx.native_handle(), stream) == 0, "Release stopped stream");
        check(polar_sdk_read_samples(ctx.native_handle(), stream, batch, 1, &n) ==
                  POLAR_INVALID_HANDLE,
              "Stale stream read");
    }
    for (int i = 0; i < 40; i++) {
        polar::sdk::context c;
        polar_command cmd{};
        cmd.operation = POLAR_OP_REPLAY_OPEN;
        cmd.measurement = POLAR_ACC;
        cmd.flags = 1;
        auto request = c.submit(cmd);
        auto cancelled = polar_sdk_cancel(c.native_handle(), request);
        auto event = completion(c.native_handle(), request);
        check(event.result == 0 || event.result == POLAR_CANCELLED, "Cancel race result");
        check(cancelled == 0 || cancelled == POLAR_INVALID_HANDLE, "Cancel return");
        if (event.result == POLAR_CANCELLED)
            check(event.stream == 0, "Cancelled request exposed live stream");
    }
    polar_message message = 0;
    const char schema[] = "polar.sdk.ServiceRequest";
    check(polar_sdk_message_parse(schema, sizeof(schema) - 1, nullptr, 0, &message) == 0,
          "Empty protobuf");
    polar_field field{};
    field.struct_size = sizeof(field);
    field.present = 1;
    field.kind = 4;
    field.unsigned_value = UINT64_MAX;
    check(polar_sdk_message_set(message, "value", 5, 0, &field, nullptr, 0) == 0,
          "Message set uint64");
    field = {};
    field.struct_size = sizeof(field);
    uint32_t n = 0;
    check(polar_sdk_message_field(message, "value", 5, 0, &field, nullptr, 0, &n) == 0 &&
              field.unsigned_value == UINT64_MAX,
          "Message integer precision");
    field.kind = 3;
    field.unsigned_value = UINT64_MAX;
    check(polar_sdk_message_set(message, "flags", 5, 0, &field, nullptr, 0) ==
              POLAR_INVALID_ARGUMENT,
          "uint32 overflow accepted");
    field.present = 0;
    check(polar_sdk_message_set(message, "value", 5, 0, &field, nullptr, 0) == 0, "Message clear");
    check(polar_sdk_message_field(message, "value", 5, 0, &field, nullptr, 0, &n) == 0 &&
              !field.present,
          "Optional clear");
    polar_sdk_message_destroy(message);
    polar_sample sample[4];
    uint8_t hr[]{0x1f, 0x2c, 1, 7, 0, 0, 4, 0, 2};
    check(polar_sdk_decode_notification(POLAR_HR, hr, sizeof(hr), 42, sample, 4, &n) == 0 &&
              n == 2 && sample[0].values[0] == 300 && sample[0].values[2] == 1000 &&
              sample[0].integers[0] == 7,
          "HR flags energy RR");
    uint8_t battery[]{101};
    check(polar_sdk_decode_notification(POLAR_BATTERY_LEVEL, battery, 1, 0, sample, 4, &n) ==
              POLAR_PROTOCOL_ERROR,
          "Battery invalid range");
    uint8_t temperature[]{0, 0xff, 0xff, 0x7f, 0};
    check(polar_sdk_decode_notification(POLAR_THERMOMETER, temperature, 5, 0, sample, 4, &n) == 0 &&
              std::isnan(sample[0].values[0]),
          "IEEE thermometer NaN");
    std::cout << "PASS lifetime: 80 start/drain/release cycles, 40 cancellation/shutdown races, "
                 "message mutation, standard notifications\n";
}
