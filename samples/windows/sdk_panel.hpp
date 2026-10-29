#pragma once
#include <array>
#include <condition_variable>
#include <cstdio>
#include <deque>
#include <fstream>
#include <filesystem>
#include <set>
#include <map>
#include <mutex>
#include <polar/polar_sdk.hpp>
#include <thread>
// Uses only installed public SDK headers and exports.
struct CsvWriter {
    std::mutex mutex;
    std::condition_variable wake;
    std::vector<polar_sample> ring;
    size_t head = 0, count = 0;
    bool stopping = false, failed = false;
    std::thread thread;
    bool start(const char *path) {
        stop();
        auto f = std::make_shared<std::ofstream>(std::filesystem::u8path(path));
        if (!*f)
            return false;
        *f << "timestamp_ns,received_ns,sequence,measurement,x,y,z\n";
        ring.resize(32768);
        stopping = false;
        failed = false;
        head = count = 0;
        thread = std::thread([this, f] {
            std::vector<polar_sample> batch(1024);
            for (;;) {
                size_t n;
                {
                    std::unique_lock l(mutex);
                    wake.wait(l, [&] { return stopping || count; });
                    if (!count && stopping)
                        break;
                    n = std::min(count, batch.size());
                    for (size_t i = 0; i < n; i++)
                        batch[i] = ring[(head + i) % ring.size()];
                    head = (head + n) % ring.size();
                    count -= n;
                }
                for (size_t i = 0; i < n; i++) {
                    auto &s = batch[i];
                    *f << s.timestamp_ns << ',' << s.received_ns << ',' << s.sequence << ','
                       << s.measurement << ',' << s.values[0] << ',' << s.values[1] << ','
                       << s.values[2] << '\n';
                }
                if (!*f) {
                    std::lock_guard l(mutex);
                    failed = true;
                    stopping = true;
                    break;
                }
            }
            f->flush();
            if (!*f) {
                std::lock_guard l(mutex);
                failed = true;
            }
        });
        return true;
    }
    bool push(const polar_sample *p, size_t n) {
        std::lock_guard l(mutex);
        if (!thread.joinable() || stopping)
            return false;
        if (count + n > ring.size()) {
            failed = true;
            stopping = true;
            wake.notify_one();
            return false;
        }
        for (size_t i = 0; i < n; i++)
            ring[(head + count + i) % ring.size()] = p[i];
        count += n;
        wake.notify_one();
        return true;
    }
    void stop() {
        {
            std::lock_guard l(mutex);
            stopping = true;
        }
        wake.notify_one();
        if (thread.joinable())
            thread.join();
    }
    ~CsvWriter() {
        stop();
    }
};
inline void DrawPolarPanel() {
    struct State {
        polar::sdk::context ctx{POLAR_LOSSLESS, 8192};
        std::map<uint64_t, polar_advertisement> devices;
        polar_device device = 0;
        polar_stream stream = 0;
        std::set<polar_stream> streams;
        std::map<polar_stream, polar_device> stream_devices;
        std::map<polar_request, uint32_t> requests;
        std::vector<uint8_t> payload = std::vector<uint8_t>(65536);
        std::vector<polar_sample> samples = std::vector<polar_sample>(8192);
        std::array<float, 1024> graph{};
        size_t cursor = 0;
        uint64_t total = 0;
        std::deque<std::string> log;
        std::string settings;
        polar_request settings_request = 0;
        char csv[260] = "polar-capture.csv";
        bool recording = false;
        CsvWriter writer;
    };
    static State state;
    auto &s = state;
    auto log = [&](std::string text) {
        if (s.log.size() == 80)
            s.log.pop_front();
        s.log.push_back(std::move(text));
    };
    for (int i = 0; i < 128; i++) {
        polar_event e{};
        e.struct_size = sizeof(e);
        uint32_t n;
        auto r = polar_sdk_poll_event(s.ctx.native_handle(), &e, s.payload.data(),
                                      uint32_t(s.payload.size()), &n);
        if (r == POLAR_EMPTY)
            break;
        if (r == POLAR_BUFFER_TOO_SMALL) {
            s.payload.resize(n);
            continue;
        }
        if (r < 0) {
            log("Poll failed: " + std::to_string(r));
            break;
        }
        if (e.type == POLAR_EVENT_ADVERTISEMENT && n == sizeof(polar_advertisement)) {
            polar_advertisement a;
            std::memcpy(&a, s.payload.data(), sizeof(a));
            s.devices[a.address] = a;
        } else {
            char text[256];
            std::snprintf(
                text, sizeof(text),
                "event %u request %llu result %d HRESULT 0x%08x ATT %u stage %u (%.2f ms)", e.type,
                e.request, e.result, uint32_t(e.hresult), e.att_error, e.stage, e.elapsed_ns / 1e6);
            log(text);
            if (e.result < 0 && n)
                log(std::string(s.payload.begin(), s.payload.begin() + n));
            auto pending = s.requests.find(e.request);
            uint32_t operation = pending == s.requests.end() ? 0 : pending->second;
            if (e.type == POLAR_EVENT_COMPLETE)
                s.requests.erase(e.request);
            if (e.type == POLAR_EVENT_RECONNECTED && e.result == 0)
                s.device = e.device;
            if (e.type == POLAR_EVENT_COMPLETE && e.result == 0) {
                if (e.device && operation == POLAR_OP_CONNECT)
                    s.device = e.device;
                if (e.stream &&
                    (operation == POLAR_OP_STREAM_START || operation == POLAR_OP_REPLAY_OPEN)) {
                    s.stream = e.stream;
                    s.streams.insert(e.stream);
                    s.stream_devices[e.stream] = e.device;
                }
                if (operation == POLAR_OP_DISCONNECT) {
                    s.device = 0;
                }
                if (n) {
                    s.settings.clear();
                    for (uint32_t k = 0; k < n && k < 1024; k++) {
                        char h[4];
                        std::snprintf(h, sizeof(h), "%02X ", s.payload[k]);
                        s.settings += h;
                    }
                    if (e.request == s.settings_request)
                        try {
                            polar::sdk::message service("polar.sdk.ServiceResult",
                                                        {s.payload.data(), n});
                            auto data = service.field("data").second;
                            polar::sdk::message pmd("polar.sdk.PmdResponse", data);
                            auto count = pmd.field("settings").first.count;
                            s.settings.clear();
                            for (uint32_t k = 0; k < count; k++) {
                                auto wire = pmd.field("settings", k).second;
                                polar::sdk::message setting("polar.sdk.PmdSetting", wire);
                                auto kind = setting.field("type").first.unsigned_value;
                                const char *label = kind == 0   ? "Sample rate (Hz)"
                                                    : kind == 1 ? "Resolution (bits)"
                                                    : kind == 2 ? "Range"
                                                    : kind == 4 ? "Channels"
                                                                : "Setting";
                                s.settings +=
                                    std::string(label) + " [" + std::to_string(kind) + "]: ";
                                auto values = setting.field("values").first.count;
                                for (uint32_t index = 0; index < values; index++)
                                    s.settings +=
                                        std::to_string(
                                            setting.field("values", index).first.unsigned_value) +
                                        " ";
                                s.settings += '\n';
                            }
                        } catch (const std::exception &error) {
                            log(error.what());
                        }
                }
            }
            if (e.type == POLAR_EVENT_DISCONNECTED) {
                s.device = 0;
            }
        }
    }
    for (auto it = s.streams.begin(); it != s.streams.end();) {
        auto current = *it;
        uint32_t n = 0;
        auto r = polar_sdk_read_samples(s.ctx.native_handle(), current, s.samples.data(),
                                        uint32_t(s.samples.size()), &n);
        for (uint32_t i = 0; i < n; i++) {
            if (current == s.stream)
                s.graph[s.cursor++ % s.graph.size()] = float(s.samples[i].values[0]);
        }
        s.total += n;
        if (s.recording && n && !s.writer.push(s.samples.data(), n)) {
            log("CSV queue overflow or disk error: recording stopped");
            s.writer.stop();
            s.recording = false;
        }
        if (r < 0) {
            log("Stream stopped: " + std::to_string(r));
        }
        polar_stream_info info{};
        info.struct_size = sizeof(info);
        auto status = polar_sdk_stream_info(s.ctx.native_handle(), current, &info);
        if (r < 0 || status < 0 || (info.state && !info.queued_samples && !info.pending_frames)) {
            polar_sdk_release_stream(s.ctx.native_handle(), current);
            it = s.streams.erase(it);
            s.stream_devices.erase(current);
            if (s.stream == current)
                s.stream = s.streams.empty() ? 0 : *s.streams.begin();
        } else
            ++it;
    }
    auto action = [&](polar_command c) {
        try {
            auto id = s.ctx.submit(c);
            s.requests[id] = c.operation;
        } catch (const std::exception &e) {
            log(e.what());
        }
    };
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(ImGui::GetIO().DisplaySize);
    ImGui::Begin("Polar Native SDK", nullptr,
                 ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove |
                     ImGuiWindowFlags_NoSavedSettings);
    ImGui::Text("POLAR / NATIVE SDK 0.2 PREVIEW");
    ImGui::TextDisabled("Windows x64 | C++20 | ABI v2 | %0.1f FPS", ImGui::GetIO().Framerate);
    ImGui::Separator();
    if (ImGui::Button("Scan")) {
        polar_command c{};
        c.operation = POLAR_OP_SCAN_START;
        action(c);
    }
    ImGui::SameLine();
    if (ImGui::Button("Stop scan")) {
        polar_command c{};
        c.operation = POLAR_OP_SCAN_STOP;
        action(c);
    }
    ImGui::SameLine();
    if (ImGui::Button("Radio state")) {
        polar_command c{};
        c.operation = POLAR_OP_RADIO;
        action(c);
    }
    for (auto &[address, a] : s.devices) {
        ImGui::PushID(int(address));
        char name[192];
        std::snprintf(name, sizeof(name), "%s | %012llX | %d dBm", a.name, address, a.rssi);
        if (ImGui::Selectable(name)) {
            polar_command c{};
            c.operation = POLAR_OP_CONNECT;
            c.address = address;
            c.flags = 2; // Native reconnect policy; new streams still require explicit start.
            action(c);
        }
        ImGui::PopID();
    }
    ImGui::Separator();
    ImGui::Text("Device handle: %llu | Stream: %llu | Samples: %llu", s.device, s.stream, s.total);
    ImGui::BeginDisabled(!s.streams.empty());
    if (ImGui::Button("Replay ACC")) {
        polar_command c{};
        c.operation = POLAR_OP_REPLAY_OPEN;
        c.measurement = POLAR_ACC;
        c.flags = 257;
        action(c);
    }
    ImGui::EndDisabled();
    ImGui::SameLine();
    ImGui::BeginDisabled(!s.stream);
    if (ImGui::Button("Stop selected")) {
        polar_command c{};
        c.device = s.stream_devices[s.stream];
        c.operation = c.device ? POLAR_OP_STREAM_STOP : POLAR_OP_REPLAY_CLOSE;
        c.stream = s.stream;
        action(c);
    }
    ImGui::EndDisabled();
    ImGui::BeginDisabled(!s.device);
    if (ImGui::Button("ACC settings")) {
        try {
            polar::sdk::message request("polar.sdk.ServiceRequest");
            request.uint32("flags", 1).uint64("value", POLAR_ACC);
            s.settings_request = request.submit(s.ctx, s.device, POLAR_SERVICE_PMD);
            s.requests[s.settings_request] = POLAR_OP_SERVICE;
        } catch (const std::exception &e) {
            log(e.what());
        }
    }
    ImGui::SameLine();
    if (ImGui::Button("Start HR")) {
        polar_command c{};
        c.operation = POLAR_OP_STREAM_START;
        c.device = s.device;
        c.measurement = POLAR_HR;
        action(c);
    }
    ImGui::SameLine();
    if (ImGui::Button("Start ACC")) {
        polar_command c{};
        c.operation = POLAR_OP_STREAM_START;
        c.device = s.device;
        c.measurement = POLAR_ACC;
        action(c);
    }
    ImGui::SameLine();
    if (ImGui::Button("Stop stream")) {
        polar_command c{};
        c.operation = POLAR_OP_STREAM_STOP;
        c.device = s.device;
        c.stream = s.stream;
        action(c);
    }
    ImGui::SameLine();
    if (ImGui::Button("Disconnect")) {
        polar_command c{};
        c.operation = POLAR_OP_DISCONNECT;
        c.device = s.device;
        action(c);
    }
    ImGui::EndDisabled();
    if (!s.settings.empty()) {
        ImGui::TextWrapped("Last response: %s", s.settings.c_str());
    }
    ImGui::PlotLines("Channel 0", s.graph.data(), int(s.graph.size()),
                     int(s.cursor % s.graph.size()), nullptr, FLT_MAX, FLT_MAX, ImVec2(-1, 170));
    ImGui::InputText("CSV path", s.csv, sizeof(s.csv));
    if (ImGui::Button(s.recording ? "Finish CSV" : "Record CSV")) {
        if (s.recording) {
            s.writer.stop();
            {
                std::lock_guard l(s.writer.mutex);
                if (s.writer.failed)
                    log("CSV flush/write failed");
            }
            s.recording = false;
        } else {
            s.recording = s.writer.start(s.csv);
            if (!s.recording)
                log("Cannot open CSV output");
        }
    }
    if (s.stream) {
        polar_stream_info info{};
        info.struct_size = sizeof(info);
        polar_sdk_stream_info(s.ctx.native_handle(), s.stream, &info);
        ImGui::Text("Queue %llu | Peak %llu | Skipped %llu | Ingress lost %llu",
                    info.queued_samples, info.high_watermark, info.skipped_samples,
                    info.ingress_dropped);
    }
    ImGui::Separator();
    ImGui::Text("Diagnostics");
    ImGui::BeginChild("diagnostics", ImVec2(0, 0), ImGuiChildFlags_Borders);
    for (auto &line : s.log)
        ImGui::TextUnformatted(line.c_str());
    ImGui::EndChild();
    ImGui::End();
}
