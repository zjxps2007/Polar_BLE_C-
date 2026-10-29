#pragma once
#include "common.hpp"
namespace polar {
struct command : std::enable_shared_from_this<command> {
    polar_command c{};
    polar_request id = 0;
    uint64_t reconnect_from = 0;
    std::chrono::steady_clock::time_point ready_at{};
    bytes data;
    std::string path;
    std::string transfer_path;
    std::shared_ptr<std::atomic_bool> cancel = std::make_shared<std::atomic_bool>(false);
    std::mutex async_gate;
    std::condition_variable wake;
    std::deque<bytes> responses;
    std::deque<bytes> notifications;
    bool disconnected = false;
    void abort() {
        std::lock_guard l(async_gate);
        cancel->store(true);
        wake.notify_all();
    }
};
struct event {
    polar_event e{};
    bytes data;
};
struct packet {
    uint64_t stream = 0, received = 0;
    uint32_t length = 0;
    std::array<uint8_t, max_packet> data;
};
struct stream {
    uint64_t id = 0, device = 0;
    uint32_t measurement = 0, policy = 0;
    polar_decode_options options{sizeof(polar_decode_options), 0, 0, 1, 0, 0};
    std::mutex gate;
    std::vector<polar_sample> samples;
    size_t head = 0, count = 0;
    uint64_t produced = 0, consumed = 0, skipped = 0, dropped = 0, high = 0;
    uint32_t pending_frames = 0;
    polar_stream_metrics metrics{sizeof(polar_stream_metrics), 0, 0, 0, 0, 0, 0};
    std::array<uint64_t, 128> timestamps{};
    int32_t error = 0;
    bool stopped = false, initializing = false;
    std::condition_variable ready;
};
class backend {
  public:
    virtual ~backend() = default;
    virtual void execute(command &, event &) = 0;
    virtual void shutdown() = 0;
    virtual void abandon(uint64_t) = 0;
};
class context : public std::enable_shared_from_this<context> {
  public:
    polar_config config{};
    std::mutex gate;
    std::condition_variable work, incoming;
    std::deque<std::shared_ptr<command>> commands;
    std::map<uint64_t, std::shared_ptr<command>> requests;
    std::deque<event> events;
    std::map<uint64_t, std::shared_ptr<stream>> streams;
    std::vector<packet> ingress;
    size_t ingress_head = 0, ingress_count = 0;
    size_t pending_completions = 0;
    bool event_overflow = false;
    std::atomic_bool stopping = false;
    std::thread control_thread, decoder_thread;
    std::vector<std::thread> replay_threads;
    std::unique_ptr<backend> ble;
    explicit context(polar_config c) : config(c), ingress(c.ingress_capacity) {}
    ~context();
    void start();
    void stop();
    void push(event e, bool completion = false);
    std::shared_ptr<stream> new_stream(uint64_t device, uint32_t measurement, view settings);
    std::shared_ptr<stream> find_stream(uint64_t);
    void invalidate_device(uint64_t, polar_result);
    polar_result receive(uint64_t, view, uint64_t);
    void control();
    void decoder();
    void reconnect(uint64_t previous_device, uint64_t address);
};
uint64_t next_handle();
std::unique_ptr<backend> make_windows_backend(std::weak_ptr<context>);
} // namespace polar
