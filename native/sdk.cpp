#include "engine.hpp"
#include "services.hpp"
#include <winrt/base.h>
#include <cmath>
namespace polar {
static std::atomic_uint64_t handles{1};
uint64_t next_handle() {
    auto id = handles.fetch_add(1);
    if (!id)
        std::terminate();
    return id;
}
static std::mutex registry_gate;
static std::map<uint64_t, std::shared_ptr<context>> registry;
static std::mutex message_gate;
static std::map<uint64_t, std::unique_ptr<google::protobuf::Message>> messages;
static polar_result copy_result(view b, uint8_t *out, uint32_t cap, uint32_t *needed) {
    if (!needed || (!out && cap))
        return POLAR_INVALID_ARGUMENT;
    *needed = uint32_t(b.size());
    if (cap < b.size())
        return POLAR_BUFFER_TOO_SMALL;
    if (!b.empty())
        std::memcpy(out, b.data(), b.size());
    return POLAR_OK;
}
static std::shared_ptr<context> find_context(uint64_t id) {
    std::lock_guard l(registry_gate);
    auto i = registry.find(id);
    if (i == registry.end())
        throw failure(POLAR_INVALID_HANDLE, "Invalid context");
    return i->second;
}
context::~context() {
    stop();
}
void context::start() {
    ble = make_windows_backend(weak_from_this());
    decoder_thread = std::thread([this] { decoder(); });
    control_thread = std::thread([this] { control(); });
}
void context::stop() {
    stopping = true;
    {
        std::lock_guard l(gate);
        for (auto &[id, c] : requests)
            c->abort();
        for (auto &[id, s] : streams) {
            std::lock_guard sl(s->gate);
            s->stopped = true;
            s->ready.notify_all();
        }
    }
    work.notify_all();
    incoming.notify_all();
    if (control_thread.joinable())
        control_thread.join();
    for (auto &thread : replay_threads)
        if (thread.joinable())
            thread.join();
    if (decoder_thread.joinable())
        decoder_thread.join();
}
void context::push(event e, bool completion) {
    std::lock_guard l(gate);
    if (stopping && !completion)
        return;
    e.e.struct_size = sizeof(polar_event);
    e.e.host_time_ns = clock_ns();
    e.e.data_length = uint32_t(e.data.size());
    if (e.e.type == POLAR_EVENT_PROGRESS) {
        for (auto &previous : events)
            if (previous.e.type == POLAR_EVENT_PROGRESS && previous.e.request == e.e.request) {
                previous = std::move(e);
                return;
            }
        if (events.size() + pending_completions >= config.event_capacity)
            return;
    }
    if (completion) {
        if (pending_completions)
            --pending_completions;
    } else if (events.size() + pending_completions >= config.event_capacity) {
        event_overflow = true;
        return;
    }
    events.push_back(std::move(e));
}
std::shared_ptr<stream> context::new_stream(uint64_t device, uint32_t measurement, view settings) {
    auto s = std::make_shared<stream>();
    s->id = next_handle();
    s->device = device;
    s->measurement = measurement;
    s->policy = config.overflow_policy;
    s->samples.resize(config.sample_capacity);
    s->options.sample_rate = get_setting(settings, 0, measurement == POLAR_HR ? 1 : 52);
    s->options.factor =
        std::bit_cast<float>(get_setting(settings, 5, std::bit_cast<uint32_t>(1.f)));
    for (auto &set : parse_settings(settings))
        if (set.kind == 7)
            for (auto id : set.values) {
                require(id < 16, "Derived method");
                s->options.derived_methods |= 1u << id;
            }
    std::lock_guard l(gate);
    require(streams.size() < 64, "Stream limit reached");
    streams.emplace(s->id, s);
    return s;
}
std::shared_ptr<stream> context::find_stream(uint64_t id) {
    std::lock_guard l(gate);
    auto i = streams.find(id);
    if (i == streams.end())
        throw failure(POLAR_INVALID_HANDLE, "Invalid stream");
    return i->second;
}
void context::invalidate_device(uint64_t id, polar_result error) {
    std::lock_guard l(gate);
    for (auto it = streams.begin(); it != streams.end();) {
        auto s = it->second;
        if (s->device == id) {
            std::lock_guard sl(s->gate);
            s->stopped = true;
            s->error = error;
            s->ready.notify_all();
            it = streams.erase(it);
        } else
            ++it;
    }
}
polar_result context::receive(uint64_t id, view b, uint64_t received) {
    if (b.size() > max_packet)
        return POLAR_BUFFER_TOO_SMALL;
    std::shared_ptr<stream> s;
    {
        std::lock_guard l(gate);
        if (stopping)
            return POLAR_CANCELLED;
        auto it = streams.find(id);
        if (it == streams.end())
            return POLAR_INVALID_HANDLE;
        s = it->second;
        {
            std::lock_guard sl(s->gate);
            if (s->stopped)
                return s->error ? s->error : POLAR_DISCONNECTED;
        }
        if (ingress_count == ingress.size() && config.overflow_policy == POLAR_LATEST) {
            auto old = streams.find(ingress[ingress_head].stream);
            if (old != streams.end()) {
                std::lock_guard sl(old->second->gate);
                ++old->second->dropped;
                --old->second->pending_frames;
                old->second->ready.notify_all();
                old->second->timestamps.fill(0);
            }
            ingress_head = (ingress_head + 1) % ingress.size();
            --ingress_count;
        }
        if (ingress_count < ingress.size()) {
            auto &p = ingress[(ingress_head + ingress_count) % ingress.size()];
            p.stream = id;
            p.received = received ? received : clock_ns();
            p.length = uint32_t(b.size());
            std::copy(b.begin(), b.end(), p.data.begin());
            ++ingress_count;
            {
                std::lock_guard sl(s->gate);
                ++s->pending_frames;
                ++s->metrics.ingress_frames;
                s->metrics.ingress_bytes += b.size();
            }
            incoming.notify_one();
            return POLAR_OK;
        }
    }
    {
        std::lock_guard l(s->gate);
        s->dropped++;
        s->error = POLAR_QUEUE_OVERFLOW;
        s->stopped = true;
    }
    event e{};
    e.e.type = POLAR_EVENT_ERROR;
    e.e.stream = id;
    e.e.result = POLAR_QUEUE_OVERFLOW;
    push(std::move(e));
    return POLAR_QUEUE_OVERFLOW;
}
void context::control() {
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    for (;;) {
        std::shared_ptr<command> c;
        {
            std::unique_lock l(gate);
            for (;;) {
                if (commands.empty()) {
                    if (stopping)
                        break;
                    work.wait(l);
                    continue;
                }
                auto now = std::chrono::steady_clock::now();
                auto ready = std::find_if(commands.begin(), commands.end(), [&](auto &q) {
                    return stopping || q->cancel->load() || q->ready_at <= now;
                });
                if (ready != commands.end()) {
                    c = *ready;
                    commands.erase(ready);
                    break;
                }
                auto earliest =
                    (*std::min_element(commands.begin(), commands.end(), [](auto &a, auto &b) {
                        return a->ready_at < b->ready_at;
                    }))->ready_at;
                work.wait_until(l, earliest);
            }
            if (!c)
                break;
        }
        event e{};
        e.e.request = c->id;
        e.e.type = POLAR_EVENT_COMPLETE;
        e.e.device = c->c.device;
        auto start = clock_ns();
        try {
            if (stopping || c->cancel->load())
                throw failure(POLAR_CANCELLED, "Cancelled");
            if (c->c.operation == POLAR_OP_REPLAY_OPEN) {
                const auto load = c->c.flags & 255u;
                const bool motion = (c->c.flags & 256u) != 0;
                require(!(c->c.flags & ~511u) && load <= 4 &&
                            (!load || c->c.measurement == POLAR_ACC),
                        "Automatic replay supports ACC load 1..4, optional motion bit8");
                auto s = new_stream(0, c->c.measurement, c->data);
                e.e.stream = s->id;
                if (load) {
                    replay_threads.emplace_back([this, s, load, motion] {
                        bytes frame(58);
                        frame[0] = 2;
                        frame[9] = 1;
                        for (size_t i = 0; i < 8; i++) {
                            frame[10 + i * 6] = uint8_t(100 + i);
                            frame[12 + i * 6] = uint8_t(200 + i);
                            frame[14 + i * 6] = 0xe8;
                            frame[15 + i * 6] = 3;
                        }
                        uint64_t time = 700000000000000000ull;
                        auto next = std::chrono::steady_clock::now();
                        while (!stopping) {
                            {
                                std::lock_guard l(s->gate);
                                if (s->stopped)
                                    break;
                            }
                            for (uint32_t j = 0; j < load; j++) {
                                time += 153846154;
                                for (int k = 0; k < 8; k++)
                                    frame[1 + k] = uint8_t(time >> (8 * k));
                                if (motion)
                                    for (int k = 0; k < 8; k++)
                                        for (int axis = 0; axis < 2; axis++) {
                                            auto value = int16_t(
                                                750 * std::sin(double(time % 10000000000ull) / 1e9 +
                                                               axis + k * .02));
                                            auto bits = uint16_t(value);
                                            frame[10 + k * 6 + axis * 2] = uint8_t(bits);
                                            frame[11 + k * 6 + axis * 2] = uint8_t(bits >> 8);
                                        }
                                if (receive(s->id, frame, clock_ns()) < 0)
                                    return;
                            }
                            next += std::chrono::microseconds(153846);
                            std::unique_lock l(gate);
                            work.wait_until(l, next, [&] { return bool(stopping); });
                        }
                    });
                }
            } else if (c->c.operation == POLAR_OP_REPLAY_CLOSE) {
                auto s = find_stream(c->c.stream);
                std::unique_lock l(s->gate);
                s->stopped = true;
                s->ready.wait(l, [&] { return s->pending_frames == 0 || stopping; });
                e.e.stream = s->id;
            } else
                ble->execute(*c, e);
            if (c->cancel->load()) {
                if (e.e.stream) {
                    auto s = find_stream(e.e.stream);
                    std::lock_guard sl(s->gate);
                    s->stopped = true;
                    s->error = POLAR_CANCELLED;
                    s->ready.notify_all();
                }
                if (e.e.device) {
                    invalidate_device(e.e.device, POLAR_CANCELLED);
                    ble->abandon(e.e.device);
                }
                if (e.e.stream) {
                    std::lock_guard l(gate);
                    streams.erase(e.e.stream);
                    e.e.stream = 0;
                }
                e.e.device = 0;
                throw failure(POLAR_CANCELLED, "Cancelled");
            }
        } catch (const failure &f) {
            e.e.result = f.code;
            e.e.hresult = f.hresult;
            e.e.att_error = f.att;
            e.e.stage = f.stage;
            auto p = f.what();
            e.data.assign(p, p + std::strlen(p));
        } catch (const winrt::hresult_error &f) {
            e.e.result = POLAR_OS_ERROR;
            e.e.hresult = f.code();
            auto s = winrt::to_string(f.message());
            e.data.assign(s.begin(), s.end());
        } catch (const std::exception &f) {
            e.e.result = POLAR_INTERNAL_ERROR;
            auto p = f.what();
            e.data.assign(p, p + std::strlen(p));
        } catch (...) {
            e.e.result = POLAR_INTERNAL_ERROR;
        }
        e.e.elapsed_ns = clock_ns() - start;
        if (c->reconnect_from && !stopping && !c->cancel->load() &&
            (e.e.result == POLAR_OS_ERROR || e.e.result == POLAR_TIMEOUT ||
             e.e.result == POLAR_DISCONNECTED)) {
            e.e.type = POLAR_EVENT_RECONNECTING;
            e.e.device = c->reconnect_from;
            push(std::move(e));
            std::lock_guard l(gate);
            c->ready_at = std::chrono::steady_clock::now() + std::chrono::seconds(2);
            commands.push_back(c);
            continue;
        }
        if (c->reconnect_from && e.e.result == POLAR_OK)
            e.e.type = POLAR_EVENT_RECONNECTED;
        {
            std::lock_guard l(gate);
            requests.erase(c->id);
        }
        push(std::move(e), true);
    }
    ble->shutdown();
    winrt::uninit_apartment();
}
void context::reconnect(uint64_t previous, uint64_t address) {
    auto c = std::make_shared<command>();
    c->id = next_handle();
    c->reconnect_from = previous;
    c->c.operation = POLAR_OP_CONNECT;
    c->c.address = address;
    c->c.flags = 2;
    c->c.timeout_ms = config.command_timeout_ms;
    {
        std::lock_guard l(gate);
        if (stopping)
            return;
        if (events.size() + pending_completions >= config.event_capacity - 1) {
            event_overflow = true;
            return;
        }
        requests.emplace(c->id, c);
        commands.push_back(c);
        ++pending_completions;
    }
    event e{};
    e.e.type = POLAR_EVENT_RECONNECTING;
    e.e.request = c->id;
    e.e.device = previous;
    push(std::move(e));
    work.notify_one();
}
void context::decoder() {
    auto scratch = std::make_unique<polar_sample[]>(max_samples);
    packet p{};
    for (;;) {
        std::shared_ptr<stream> s;
        {
            std::unique_lock l(gate);
            incoming.wait(l, [&] { return stopping || ingress_count; });
            if (stopping)
                break;
            const auto &entry = ingress[ingress_head];
            p.stream = entry.stream;
            p.received = entry.received;
            p.length = entry.length;
            std::memcpy(p.data.data(), entry.data.data(), entry.length);
            ingress_head = (ingress_head + 1) % ingress.size();
            --ingress_count;
            auto it = streams.find(p.stream);
            if (it == streams.end())
                continue;
            s = it->second;
        }
        polar_result error = 0;
        try {
            std::unique_lock l(s->gate);
            s->metrics.decoder_bytes += p.length;
            s->ready.wait(l, [&] { return !s->initializing || s->stopped || stopping; });
            if (s->error || stopping) {
                --s->pending_frames;
                s->ready.notify_all();
                continue;
            }
            auto opt = s->options;
            opt.received_ns = p.received;
            auto bytes = view(p.data.data(), p.length);
            uint64_t broadcast_address = 0;
            if (s->measurement == POLAR_HR_ADVERTISEMENT) {
                broadcast_address = u(bytes, 0, 8);
                bytes = bytes.subspan(8);
            }
            uint32_t fmt = s->measurement >= POLAR_HR ? 0 : (p.length > 9 ? p.data[9] & 127 : 0);
            opt.previous_timestamp = s->timestamps[fmt];
            size_t count = s->measurement >= POLAR_HR
                               ? decode_standard(s->measurement, bytes, p.received,
                                                 {scratch.get(), max_samples})
                               : decode(bytes, opt, {scratch.get(), max_samples});
            if (s->measurement == POLAR_HR_ADVERTISEMENT)
                for (size_t i = 0; i < count; i++)
                    scratch[i].identifier = broadcast_address;
            if (s->measurement < POLAR_HR)
                s->timestamps[fmt] = u(bytes, 1, 8);
            if (s->count + count > s->samples.size() && s->policy == POLAR_LOSSLESS) {
                s->stopped = true;
                s->error = POLAR_QUEUE_OVERFLOW;
                error = POLAR_QUEUE_OVERFLOW;
            } else {
                for (size_t i = 0; i < count; i++) {
                    auto &v = scratch[i];
                    v.sequence = ++s->produced;
                    if (s->count == s->samples.size()) {
                        s->head = (s->head + 1) % s->samples.size();
                        --s->count;
                        ++s->skipped;
                    }
                    s->samples[(s->head + s->count) % s->samples.size()] = v;
                    s->metrics.sample_queue_bytes += sizeof(polar_sample);
                    ++s->count;
                }
                s->high = std::max<uint64_t>(s->high, s->count);
                const auto ready = clock_ns();
                for (size_t i = 0; i < std::min(count, s->count); i++)
                    s->samples[(s->head + s->count - 1 - i) % s->samples.size()].ready_ns = ready;
            }
        } catch (const failure &f) {
            error = f.code;
        } catch (...) {
            error = POLAR_INTERNAL_ERROR;
        }
        {
            std::lock_guard l(s->gate);
            if (error) {
                s->error = error;
                s->stopped = true;
            }
            --s->pending_frames;
            s->ready.notify_all();
        }
        if (error) {
            event e{};
            e.e.type = POLAR_EVENT_ERROR;
            e.e.stream = s->id;
            e.e.device = s->device;
            e.e.result = error;
            push(std::move(e));
        }
    }
}
template <class F> polar_result boundary(F &&f) noexcept {
    try {
        return f();
    } catch (const failure &e) {
        return e.code;
    } catch (...) {
        return POLAR_INTERNAL_ERROR;
    }
}
} // namespace polar
using namespace polar;
extern "C" {
uint32_t POLAR_CALL polar_sdk_abi_version() {
    return POLAR_ABI_VERSION;
}
uint64_t POLAR_CALL polar_sdk_clock_ns() {
    return clock_ns();
}
polar_result POLAR_CALL polar_sdk_create(const polar_config *c, polar_context *out) {
    return boundary([&]() -> polar_result {
        if (!out || !c || c->struct_size != sizeof(*c) || c->abi_version != 2 ||
            c->overflow_policy > 1)
            return POLAR_INVALID_ARGUMENT;
        auto cfg = *c;
        if (!cfg.sample_capacity)
            cfg.sample_capacity = 4096;
        if (!cfg.ingress_capacity)
            cfg.ingress_capacity = 256;
        if (!cfg.event_capacity)
            cfg.event_capacity = 256;
        if (!cfg.command_timeout_ms)
            cfg.command_timeout_ms = 30000;
        if (cfg.sample_capacity > 1048576 || cfg.ingress_capacity > 65536 ||
            cfg.event_capacity < 8 || cfg.event_capacity > 65536)
            return POLAR_INVALID_ARGUMENT;
        auto ctx = std::make_shared<context>(cfg);
        ctx->start();
        auto id = next_handle();
        {
            std::lock_guard l(registry_gate);
            registry.emplace(id, ctx);
        }
        *out = id;
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_destroy(polar_context id) {
    return boundary([&]() -> polar_result {
        std::shared_ptr<context> c;
        {
            std::lock_guard l(registry_gate);
            auto i = registry.find(id);
            if (i == registry.end())
                return POLAR_INVALID_HANDLE;
            c = i->second;
            registry.erase(i);
        }
        c->stop();
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_submit(polar_context id, const polar_command *input,
                                         polar_request *out) {
    return boundary([&]() -> polar_result {
        if (!input || !out || input->struct_size != sizeof(*input) ||
            (!input->data && input->data_length) || (!input->path && input->path_length) ||
            input->path_length > 4096 || input->data_length > 64 * 1024 * 1024)
            return POLAR_INVALID_ARGUMENT;
        auto c = find_context(id);
        auto q = std::make_shared<command>();
        q->c = *input;
        if (!q->c.timeout_ms)
            q->c.timeout_ms = c->config.command_timeout_ms;
        q->c.data = nullptr;
        q->c.path = nullptr;
        if (input->data_length)
            q->data.assign(input->data, input->data + input->data_length);
        if (input->path_length)
            q->path.assign(input->path, input->path_length);
        q->id = next_handle();
        {
            std::lock_guard l(c->gate);
            if (c->stopping)
                return POLAR_CANCELLED;
            if (c->events.size() + c->pending_completions >= c->config.event_capacity - 1)
                return POLAR_BUSY;
            if (input->operation == POLAR_OP_DISCONNECT ||
                (input->operation == POLAR_OP_RECONNECT_POLICY && input->flags == 0))
                for (auto &[rid, request] : c->requests)
                    if (request->reconnect_from == input->device)
                        request->abort();
            c->requests.emplace(q->id, q);
            c->commands.push_back(q);
            ++c->pending_completions;
        }
        *out = q->id;
        c->work.notify_one();
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_cancel(polar_context id, polar_request req) {
    return boundary([&]() -> polar_result {
        auto c = find_context(id);
        std::shared_ptr<command> q;
        {
            std::lock_guard l(c->gate);
            auto i = c->requests.find(req);
            if (i == c->requests.end())
                return POLAR_INVALID_HANDLE;
            q = i->second;
        }
        q->abort();
        c->work.notify_all();
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_poll_event(polar_context id, polar_event *e, uint8_t *data,
                                             uint32_t cap, uint32_t *needed) {
    return boundary([&]() -> polar_result {
        if (!e || e->struct_size != sizeof(*e) || !needed || (!data && cap))
            return POLAR_INVALID_ARGUMENT;
        auto c = find_context(id);
        std::lock_guard l(c->gate);
        *needed = 0;
        if (c->events.empty()) {
            if (c->event_overflow) {
                c->event_overflow = false;
                *e = {};
                e->struct_size = sizeof(*e);
                e->type = POLAR_EVENT_ERROR;
                e->result = POLAR_QUEUE_OVERFLOW;
                return POLAR_OK;
            }
            return POLAR_EMPTY;
        }
        auto &next = c->events.front();
        *needed = uint32_t(next.data.size());
        if (cap < *needed)
            return POLAR_BUFFER_TOO_SMALL;
        *e = next.e;
        if (*needed)
            std::memcpy(data, next.data.data(), *needed);
        c->events.pop_front();
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_read_samples(polar_context id, polar_stream sid,
                                               polar_sample *out, uint32_t cap, uint32_t *written) {
    return boundary([&]() -> polar_result {
        if (!written || (!out && cap))
            return POLAR_INVALID_ARGUMENT;
        *written = 0;
        auto c = find_context(id);
        auto s = c->find_stream(sid);
        std::lock_guard l(s->gate);
        size_t n = std::min<size_t>(cap, s->count);
        size_t first = std::min(n, s->samples.size() - s->head);
        if (first)
            std::memcpy(out, &s->samples[s->head], first * sizeof(*out));
        if (n > first)
            std::memcpy(out + first, s->samples.data(), (n - first) * sizeof(*out));
        s->head = (s->head + n) % s->samples.size();
        s->count -= n;
        s->consumed += n;
        s->metrics.consumer_bytes += n * sizeof(polar_sample);
        *written = uint32_t(n);
        return n ? POLAR_OK : s->error ? s->error : POLAR_EMPTY;
    });
}
polar_result POLAR_CALL polar_sdk_stream_info(polar_context id, polar_stream sid,
                                              polar_stream_info *out) {
    return boundary([&]() -> polar_result {
        if (!out || out->struct_size != sizeof(*out))
            return POLAR_INVALID_ARGUMENT;
        auto s = find_context(id)->find_stream(sid);
        std::lock_guard l(s->gate);
        *out = {sizeof(*out),
                s->measurement,
                s->count,
                s->produced,
                s->consumed,
                s->skipped,
                s->dropped,
                s->high,
                s->stopped ? 1 : 0,
                s->error,
                s->options.sample_rate,
                s->pending_frames};
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_get_stream_metrics(polar_context id, polar_stream sid,
                                                     polar_stream_metrics *out) {
    return boundary([&]() -> polar_result {
        if (!out || out->struct_size != sizeof(*out))
            return POLAR_INVALID_ARGUMENT;
        auto s = find_context(id)->find_stream(sid);
        std::lock_guard l(s->gate);
        *out = s->metrics;
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_replay_frame(polar_context id, polar_stream sid, const uint8_t *b,
                                               uint32_t n, uint64_t received) {
    return boundary([&]() -> polar_result {
        if (!b || !n)
            return POLAR_INVALID_ARGUMENT;
        return find_context(id)->receive(sid, {b, n}, received);
    });
}
polar_result POLAR_CALL polar_sdk_release_stream(polar_context id, polar_stream sid) {
    return boundary([&]() -> polar_result {
        auto c = find_context(id);
        std::lock_guard l(c->gate);
        auto it = c->streams.find(sid);
        if (it == c->streams.end())
            return POLAR_INVALID_HANDLE;
        auto s = it->second;
        std::lock_guard sl(s->gate);
        if (!s->stopped || s->pending_frames)
            return POLAR_BUSY;
        c->streams.erase(it);
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_decode_frame(const uint8_t *b, uint32_t n,
                                               const polar_decode_options *o, polar_sample *out,
                                               uint32_t cap, uint32_t *written) {
    return boundary([&]() -> polar_result {
        if (!b || !o || o->struct_size != sizeof(*o) || !written || (!out && cap))
            return POLAR_INVALID_ARGUMENT;
        *written = 0;
        thread_local auto scratch = std::make_unique<polar_sample[]>(max_samples);
        size_t count = decode({b, n}, *o, {scratch.get(), max_samples});
        *written = uint32_t(count);
        if (cap < count)
            return POLAR_BUFFER_TOO_SMALL;
        std::memcpy(out, scratch.get(), count * sizeof(*out));
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_decode_notification(uint32_t type, const uint8_t *b, uint32_t n,
                                                      uint64_t received, polar_sample *out,
                                                      uint32_t cap, uint32_t *written) {
    return boundary([&]() -> polar_result {
        if (!b || !written || (!out && cap))
            return POLAR_INVALID_ARGUMENT;
        *written = 0;
        thread_local auto scratch = std::make_unique<polar_sample[]>(max_samples);
        auto count = decode_standard(type, {b, n}, received, {scratch.get(), max_samples});
        *written = uint32_t(count);
        if (cap < count)
            return POLAR_BUFFER_TOO_SMALL;
        if (count)
            std::memcpy(out, scratch.get(), count * sizeof(*out));
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_transform(const polar_transform *t, uint8_t *out, uint32_t cap,
                                            uint32_t *needed) {
    return boundary([&]() -> polar_result {
        if (!t || t->struct_size != sizeof(*t) || (!t->data && t->data_length) ||
            (!t->parameters && t->parameters_length) || (!t->schema && t->schema_length))
            return POLAR_INVALID_ARGUMENT;
        auto b = transform(*t);
        return copy_result(b, out, cap, needed);
    });
}
polar_result POLAR_CALL polar_sdk_decode_recording(const uint8_t *b, uint32_t n, uint32_t strategy,
                                                   const uint8_t *key, uint32_t key_n,
                                                   polar_offline_info *info, polar_sample *out,
                                                   uint32_t cap, uint32_t *needed) {
    return boundary([&]() -> polar_result {
        if (!b || (!key && key_n) || !info || info->struct_size != sizeof(*info) || !needed ||
            (!out && cap))
            return POLAR_INVALID_ARGUMENT;
        auto record = decode_recording({b, n}, strategy, {key, key_n});
        *info = record.info;
        *needed = uint32_t(record.samples.size());
        if (cap < *needed)
            return POLAR_BUFFER_TOO_SMALL;
        if (*needed)
            std::memcpy(out, record.samples.data(), *needed * sizeof(*out));
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_message_parse(const char *schema, uint32_t schema_n,
                                                const uint8_t *b, uint32_t n, polar_message *out) {
    return boundary([&]() -> polar_result {
        if (!schema || !schema_n || schema_n > 1024 || (!b && n) || !out)
            return POLAR_INVALID_ARGUMENT;
        auto m = message_parse(std::string(schema, schema_n), {b, n});
        std::lock_guard l(message_gate);
        if (messages.size() >= 16384)
            return POLAR_BUSY;
        auto id = next_handle();
        messages.emplace(id, std::move(m));
        *out = id;
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_decode_recordings(const polar_recording_part *parts,
                                                    uint32_t count,
                                                    const polar_recording_options *options,
                                                    polar_offline_info *info, polar_sample *out,
                                                    uint32_t capacity, uint32_t *needed) {
    return boundary([&]() -> polar_result {
        if (!parts || !count || count > 10000 || !options ||
            options->struct_size != sizeof(*options) || (!options->key && options->key_length) ||
            !info || info->struct_size != sizeof(*info) || !needed || (!out && capacity))
            return POLAR_INVALID_ARGUMENT;
        for (uint32_t i = 0; i < count; i++)
            if (!parts[i].data || !parts[i].length)
                return POLAR_INVALID_ARGUMENT;
        uint64_t previous = 0, total = 0;
        polar_offline_info first{};
        for (uint32_t i = 0; i < count; i++) {
            auto record = decode_recording({parts[i].data, parts[i].length}, options->strategy,
                                           {options->key, options->key_length}, previous,
                                           options->derived_methods);
            if (i == 0)
                first = record.info;
            else
                require(first.version == record.info.version &&
                            first.firmware_hash == record.info.firmware_hash,
                        "Split recording header/firmware mismatch");
            for (const auto &sample : record.samples)
                require(options->measurement == UINT32_MAX ||
                            sample.measurement == options->measurement ||
                            sample.measurement == POLAR_DERIVED,
                        "Recording measurement differs from filename");
            total += record.samples.size();
            require(total <= 10000000, "Combined recording sample limit");
            if (!record.samples.empty())
                previous = record.info.last_timestamp_ns;
        }
        first.sample_count = total;
        first.last_timestamp_ns = previous;
        *info = first;
        *needed = uint32_t(total);
        if (total > capacity)
            return POLAR_BUFFER_TOO_SMALL;
        previous = 0;
        size_t offset = 0;
        for (uint32_t i = 0; i < count; i++) {
            auto record = decode_recording({parts[i].data, parts[i].length}, options->strategy,
                                           {options->key, options->key_length}, previous,
                                           options->derived_methods);
            if (!record.samples.empty()) {
                std::memcpy(out + offset, record.samples.data(),
                            record.samples.size() * sizeof(*out));
                previous = record.info.last_timestamp_ns;
            }
            offset += record.samples.size();
        }
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_message_destroy(polar_message id) {
    return boundary([&]() -> polar_result {
        std::lock_guard l(message_gate);
        return messages.erase(id) ? POLAR_OK : POLAR_INVALID_HANDLE;
    });
}
polar_result POLAR_CALL polar_sdk_message_set(polar_message id, const char *path, uint32_t len,
                                              uint32_t index, const polar_field *v,
                                              const uint8_t *data, uint32_t n) {
    return boundary([&]() -> polar_result {
        if (!path || !len || len > 4096 || !v || v->struct_size != sizeof(*v) || (!data && n) ||
            n > 64 * 1024 * 1024)
            return POLAR_INVALID_ARGUMENT;
        std::lock_guard l(message_gate);
        auto it = messages.find(id);
        if (it == messages.end())
            return POLAR_INVALID_HANDLE;
        // Validate into a copy: an invalid nested path must not partially modify the message.
        std::unique_ptr<google::protobuf::Message> edited(it->second->New());
        edited->CopyFrom(*it->second);
        auto *m = edited.get();
        std::string p(path, len);
        size_t at = 0;
        for (;;) {
            auto end = p.find('.', at);
            auto f = m->GetDescriptor()->FindFieldByName(
                p.substr(at, end == std::string::npos ? end : end - at));
            if (!f)
                return POLAR_INVALID_ARGUMENT;
            auto r = m->GetReflection();
            if (end != std::string::npos) {
                if (f->is_repeated() ||
                    f->cpp_type() != google::protobuf::FieldDescriptor::CPPTYPE_MESSAGE)
                    return POLAR_INVALID_ARGUMENT;
                m = r->MutableMessage(m, f);
                at = end + 1;
                continue;
            }
            if (!v->present) {
                r->ClearField(m, f);
                break;
            }
            if (v->kind != uint32_t(f->cpp_type()))
                return POLAR_INVALID_ARGUMENT;
            bool repeated = f->is_repeated(), append = repeated && index == UINT32_MAX;
            if (repeated && !append && index >= uint32_t(r->FieldSize(*m, f)))
                return POLAR_INVALID_ARGUMENT;
            using F = google::protobuf::FieldDescriptor;
#define POLAR_SET(Type, value)                                                                     \
    do {                                                                                           \
        if (append)                                                                                \
            r->Add##Type(m, f, value);                                                             \
        else if (repeated)                                                                         \
            r->SetRepeated##Type(m, f, int(index), value);                                         \
        else                                                                                       \
            r->Set##Type(m, f, value);                                                             \
    } while (false)
            switch (f->cpp_type()) {
            case F::CPPTYPE_INT32:
                if (v->signed_value < INT32_MIN || v->signed_value > INT32_MAX)
                    return POLAR_INVALID_ARGUMENT;
                POLAR_SET(Int32, int32_t(v->signed_value));
                break;
            case F::CPPTYPE_INT64:
                POLAR_SET(Int64, v->signed_value);
                break;
            case F::CPPTYPE_UINT32:
                if (v->unsigned_value > UINT32_MAX)
                    return POLAR_INVALID_ARGUMENT;
                POLAR_SET(UInt32, uint32_t(v->unsigned_value));
                break;
            case F::CPPTYPE_UINT64:
                POLAR_SET(UInt64, v->unsigned_value);
                break;
            case F::CPPTYPE_BOOL:
                if (v->unsigned_value > 1)
                    return POLAR_INVALID_ARGUMENT;
                POLAR_SET(Bool, v->unsigned_value != 0);
                break;
            case F::CPPTYPE_FLOAT:
                POLAR_SET(Float, float(v->real_value));
                break;
            case F::CPPTYPE_DOUBLE:
                POLAR_SET(Double, v->real_value);
                break;
            case F::CPPTYPE_ENUM:
                if (v->signed_value < INT32_MIN || v->signed_value > INT32_MAX)
                    return POLAR_INVALID_ARGUMENT;
                POLAR_SET(EnumValue, int(v->signed_value));
                break;
            case F::CPPTYPE_STRING: {
                std::string value;
                if (n)
                    value.assign(reinterpret_cast<const char *>(data), n);
                POLAR_SET(String, value);
                break;
            }
            case F::CPPTYPE_MESSAGE: {
                auto *dest = append     ? r->AddMessage(m, f)
                             : repeated ? r->MutableRepeatedMessage(m, f, int(index))
                                        : r->MutableMessage(m, f);
                if (!dest->ParsePartialFromArray(data, int(n)))
                    return POLAR_PROTOCOL_ERROR;
                break;
            }
            }
#undef POLAR_SET
            break;
        }
        it->second.swap(edited);
        return POLAR_OK;
    });
}
polar_result POLAR_CALL polar_sdk_message_serialize(polar_message id, uint8_t *b, uint32_t n,
                                                    uint32_t *needed) {
    return boundary([&]() -> polar_result {
        std::lock_guard l(message_gate);
        auto it = messages.find(id);
        if (it == messages.end())
            return POLAR_INVALID_HANDLE;
        return copy_result(message_bytes(*it->second), b, n, needed);
    });
}
polar_result POLAR_CALL polar_sdk_message_field(polar_message id, const char *path, uint32_t len,
                                                uint32_t index, polar_field *value, uint8_t *out,
                                                uint32_t cap, uint32_t *needed) {
    return boundary([&]() -> polar_result {
        if (!path || !len || len > 4096 || !value || value->struct_size != sizeof(*value) ||
            !needed || (!out && cap))
            return POLAR_INVALID_ARGUMENT;
        std::lock_guard l(message_gate);
        auto it = messages.find(id);
        if (it == messages.end())
            return POLAR_INVALID_HANDLE;
        const auto *m = it->second.get();
        std::string p(path, len);
        size_t pos = 0;
        bool parent_present = true;
        for (;;) {
            auto end = p.find('.', pos);
            auto name = p.substr(pos, end == std::string::npos ? end : end - pos);
            auto f = m->GetDescriptor()->FindFieldByName(name);
            if (!f)
                return POLAR_INVALID_ARGUMENT;
            auto r = m->GetReflection();
            if (end != std::string::npos) {
                if (f->cpp_type() != google::protobuf::FieldDescriptor::CPPTYPE_MESSAGE ||
                    f->is_repeated())
                    return POLAR_INVALID_ARGUMENT;
                parent_present = parent_present && r->HasField(*m, f);
                m = &r->GetMessage(*m, f);
                pos = end + 1;
                continue;
            }
            *value = {};
            value->struct_size = sizeof(*value);
            value->kind = f->cpp_type();
            bool repeated = f->is_repeated();
            value->count = repeated ? uint32_t(r->FieldSize(*m, f)) : 1;
            value->present =
                parent_present && (repeated ? index < value->count : r->HasField(*m, f));
            *needed = 0;
            if (!value->present)
                return POLAR_OK;
            switch (f->cpp_type()) {
            case google::protobuf::FieldDescriptor::CPPTYPE_INT32:
                value->signed_value =
                    repeated ? r->GetRepeatedInt32(*m, f, index) : r->GetInt32(*m, f);
                break;
            case google::protobuf::FieldDescriptor::CPPTYPE_INT64:
                value->signed_value =
                    repeated ? r->GetRepeatedInt64(*m, f, index) : r->GetInt64(*m, f);
                break;
            case google::protobuf::FieldDescriptor::CPPTYPE_UINT32:
                value->unsigned_value =
                    repeated ? r->GetRepeatedUInt32(*m, f, index) : r->GetUInt32(*m, f);
                break;
            case google::protobuf::FieldDescriptor::CPPTYPE_UINT64:
                value->unsigned_value =
                    repeated ? r->GetRepeatedUInt64(*m, f, index) : r->GetUInt64(*m, f);
                break;
            case google::protobuf::FieldDescriptor::CPPTYPE_FLOAT:
                value->real_value =
                    repeated ? r->GetRepeatedFloat(*m, f, index) : r->GetFloat(*m, f);
                break;
            case google::protobuf::FieldDescriptor::CPPTYPE_DOUBLE:
                value->real_value =
                    repeated ? r->GetRepeatedDouble(*m, f, index) : r->GetDouble(*m, f);
                break;
            case google::protobuf::FieldDescriptor::CPPTYPE_BOOL:
                value->unsigned_value =
                    repeated ? r->GetRepeatedBool(*m, f, index) : r->GetBool(*m, f);
                break;
            case google::protobuf::FieldDescriptor::CPPTYPE_ENUM:
                value->signed_value =
                    repeated ? r->GetRepeatedEnumValue(*m, f, index) : r->GetEnumValue(*m, f);
                break;
            case google::protobuf::FieldDescriptor::CPPTYPE_STRING: {
                auto s = repeated ? r->GetRepeatedString(*m, f, index) : r->GetString(*m, f);
                value->data_length = uint32_t(s.size());
                return copy_result({reinterpret_cast<const uint8_t *>(s.data()), s.size()}, out,
                                   cap, needed);
            }
            case google::protobuf::FieldDescriptor::CPPTYPE_MESSAGE: {
                auto b = message_bytes(repeated ? r->GetRepeatedMessage(*m, f, index)
                                                : r->GetMessage(*m, f));
                value->data_length = uint32_t(b.size());
                return copy_result(b, out, cap, needed);
            }
            }
            return POLAR_OK;
        }
    });
}
}
