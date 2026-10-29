#include "engine.hpp"
#include "connection_policy.hpp"
#include "pftp_request.pb.h"
#include "services.hpp"
#include "native_sdk.pb.h"
#include <windows.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Devices.Bluetooth.GenericAttributeProfile.h>
#include <winrt/Windows.Devices.Bluetooth.h>
#include <winrt/Windows.Devices.Enumeration.h>
#include <winrt/Windows.Devices.Radios.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Storage.Streams.h>
namespace polar {
using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Devices::Bluetooth;
using namespace Windows::Devices::Bluetooth::Advertisement;
using namespace Windows::Devices::Bluetooth::GenericAttributeProfile;
using namespace Windows::Devices::Enumeration;
using namespace Windows::Storage::Streams;
static const guid dis_service{L"0000180a-0000-1000-8000-00805f9b34fb"};
static const guid pmd_service{L"fb005c80-02e7-f387-1cad-8acd2d8df0c8"},
    pmd_cp{L"fb005c81-02e7-f387-1cad-8acd2d8df0c8"},
    pmd_data{L"fb005c82-02e7-f387-1cad-8acd2d8df0c8"};
static const guid hr_service{L"0000180d-0000-1000-8000-00805f9b34fb"},
    hr_data{L"00002a37-0000-1000-8000-00805f9b34fb"};
static const guid hts_service{L"00001809-0000-1000-8000-00805f9b34fb"},
    hts_data{L"00002a1c-0000-1000-8000-00805f9b34fb"};
static const guid bas_service{L"0000180f-0000-1000-8000-00805f9b34fb"},
    bas_level{L"00002a19-0000-1000-8000-00805f9b34fb"},
    bas_status{L"00002bed-0000-1000-8000-00805f9b34fb"};
static const guid ftp_service{L"0000feee-0000-1000-8000-00805f9b34fb"},
    ftp_mtu{L"fb005c51-02e7-f387-1cad-8acd2d8df0c8"},
    ftp_d2h{L"fb005c52-02e7-f387-1cad-8acd2d8df0c8"},
    ftp_h2d{L"fb005c53-02e7-f387-1cad-8acd2d8df0c8"};
static const guid pf_service{L"6217ff4b-fb31-1140-ad5a-a45545d7ecf3"},
    pf_cp{L"6217ff4d-91bb-91d0-7e2a-7cd3bda8a1f3"};
static guid uuid(const uint8_t *b) {
    guid v;
    std::memcpy(&v, b, 16);
    return v;
}
static bytes from_buffer(IBuffer const &b) {
    bytes v(b.Length());
    if (!v.empty())
        DataReader::FromBuffer(b).ReadBytes(v);
    return v;
}
static IBuffer to_buffer(view v) {
    DataWriter w;
    w.WriteBytes(v);
    return w.DetachBuffer();
}
template <class Op> auto await(command &c, Op op, uint32_t timeout = 30000) {
    auto done = std::make_shared<bool>(false);
    auto weak = c.weak_from_this();
    op.Completed([weak, done](auto const &, auto) {
        if (auto q = weak.lock()) {
            std::lock_guard l(q->async_gate);
            *done = true;
            q->wake.notify_all();
        }
    });
    {
        std::unique_lock l(c.async_gate);
        bool woke =
            c.wake.wait_for(l, std::chrono::milliseconds(c.c.timeout_ms ? c.c.timeout_ms : timeout),
                            [&] { return *done || c.cancel->load() || c.disconnected; });
        if (!woke || c.cancel->load() || c.disconnected) {
            const auto code = c.cancel->load() ? POLAR_CANCELLED
                              : c.disconnected ? POLAR_DISCONNECTED
                                               : POLAR_TIMEOUT;
            l.unlock(); // Cancel may synchronously deliver Completed.
            op.Cancel();
            throw failure(code, "Windows BLE operation cancelled, disconnected or timed out");
        }
    }
    return op.GetResults();
}
template <class R> void checked(R const &r, uint32_t stage) {
    if (r.Status() == GattCommunicationStatus::Success)
        return;
    failure f(POLAR_OS_ERROR, "GATT status " + std::to_string(int(r.Status())));
    f.stage = stage;
    if (r.ProtocolError())
        f.att = r.ProtocolError().Value();
    throw f;
}
struct connected {
    uint64_t id = 0;
    std::atomic_bool stale = false, initialized = false;
    std::atomic_bool auto_reconnect = false;
    std::atomic_bool link_seen = false;
    std::atomic_uint32_t phase = POLAR_CONNECTION_CONNECTING;
    polar_connect_options options{sizeof(polar_connect_options), POLAR_DEFAULT_FEATURES, 0, 0};
    uint32_t requested_features = 0, ready_features = 0, unavailable_features = 0,
             failed_features = 0, pairing_status = UINT32_MAX, authentication_attempts = 0;
    bool authentication_failed = false;
    std::optional<failure> authentication_error;
    BluetoothLEDevice device{nullptr};
    GattSession session{nullptr};
    event_token disconnected{};
    event_token pdu_changed{}, parameters_changed{}, phy_changed{};
    bool has_parameter_events = false;
    BluetoothLEPreferredConnectionParametersRequest parameter_request{nullptr};
    std::map<guid, GattDeviceService> services;
    std::map<std::pair<guid, guid>, GattCharacteristic> chars;
    std::map<guid, std::pair<GattCharacteristic, event_token>> subscriptions;
    std::mutex gate;
    std::weak_ptr<command> active;
    std::chrono::steady_clock::time_point negotiation_changed = std::chrono::steady_clock::now();
    std::map<uint32_t, uint64_t> streams;
    std::weak_ptr<command> pending;
    std::weak_ptr<command> notification_waiter;
    guid pending_characteristic{};
    bool control_invalid = false;
    bool telemetry_streaming = false;
    int telemetry_sequence = -1;
    sdk::DeviceSnapshot snapshot;
    std::optional<pftp_assembler> notification;
    void close() noexcept {
        auto cleanup = [](auto action) noexcept {
            try {
                action();
            } catch (...) {
            }
        };
        stale = true;
        for (auto &[key, s] : subscriptions)
            cleanup([&] { s.first.ValueChanged(s.second); });
        subscriptions.clear();
        if (device)
            cleanup([&] { device.ConnectionStatusChanged(disconnected); });
        if (has_parameter_events && device) {
            cleanup([&] { device.ConnectionParametersChanged(parameters_changed); });
            cleanup([&] { device.ConnectionPhyChanged(phy_changed); });
        }
        if (session)
            cleanup([&] { session.MaxPduSizeChanged(pdu_changed); });
        if (parameter_request) {
            cleanup([&] { parameter_request.Close(); });
            parameter_request = nullptr;
        }
        chars.clear();
        for (auto &[key, s] : services)
            cleanup([&] { s.Close(); });
        services.clear();
        if (session) {
            cleanup([&] { session.MaintainConnection(false); });
            cleanup([&] { session.Close(); });
            session = nullptr;
        }
        if (device) {
            cleanup([&] { device.Close(); });
            device = nullptr;
        }
    }
};
struct active_request {
    std::shared_ptr<connected> device;
    explicit active_request(std::shared_ptr<connected> d, command &c) : device(std::move(d)) {
        std::lock_guard l(device->gate);
        device->active = c.weak_from_this();
    }
    ~active_request() {
        std::lock_guard l(device->gate);
        device->active.reset();
    }
};
class windows_backend final : public backend {
    std::weak_ptr<context> owner;
    BluetoothLEAdvertisementWatcher watcher{nullptr};
    event_token received{}, stopped{};
    uint64_t broadcast_stream = 0;
    std::vector<std::pair<Windows::Devices::Radios::Radio, event_token>> radio_handlers;
    std::shared_ptr<std::atomic_bool> radio_active;
    void radio_stop() {
        if (radio_active)
            *radio_active = false;
        for (auto &[radio, token] : radio_handlers)
            radio.StateChanged(token);
        radio_handlers.clear();
        radio_active.reset();
    }
    std::map<uint64_t, std::shared_ptr<connected>> devices;
    static void alive(command &c, const std::shared_ptr<connected> &d) {
        if (c.cancel->load())
            throw failure(POLAR_CANCELLED, "Connection request cancelled");
        if (d->stale)
            throw failure(POLAR_DISCONNECTED, "Connection session ended");
    }
    static void delay(command &c, const std::shared_ptr<connected> &d, uint32_t ms) {
        alive(c, d);
        std::unique_lock l(c.async_gate);
        c.wake.wait_for(l, std::chrono::milliseconds(ms),
                        [&] { return c.cancel->load() || c.disconnected || d->stale; });
        l.unlock();
        alive(c, d);
    }
    void state(command &c, const std::shared_ptr<connected> &d, uint32_t phase, int error = 0) {
        d->phase = phase;
        if (auto root = owner.lock()) {
            uint32_t pdu = 0;
            try {
                if (d->session)
                    pdu = d->session.MaxPduSize();
            } catch (...) {
            }
            polar_connection_state status{phase,
                                          d->requested_features,
                                          d->ready_features,
                                          d->unavailable_features,
                                          d->failed_features,
                                          pdu,
                                          d->pairing_status,
                                          d->authentication_attempts};
            event e{};
            e.e.type = POLAR_EVENT_CONNECTION_STATE;
            e.e.request = c.id;
            e.e.device = d->id;
            e.e.flags = phase;
            e.e.result = error;
            auto b = reinterpret_cast<const uint8_t *>(&status);
            e.data.assign(b, b + sizeof(status));
            root->push(std::move(e));
        }
    }
    static void changed(const std::weak_ptr<connected> &weak) noexcept {
        if (auto d = weak.lock(); d && !d->stale) {
            std::shared_ptr<command> request;
            {
                std::lock_guard l(d->gate);
                d->negotiation_changed = std::chrono::steady_clock::now();
                request = d->active.lock();
            }
            if (request)
                request->wake.notify_all();
        }
    }
    void negotiate(command &c, const std::shared_ptr<connected> &d) {
        state(c, d, POLAR_CONNECTION_NEGOTIATING);
        const auto began = std::chrono::steady_clock::now();
        const auto limit = began + std::chrono::milliseconds(6000);
        // Windows owns MTU and PHY negotiation. Observe its events; do not send
        // Android HCI commands. Device6 is optional on the Windows 10 baseline.
        if (d->device.try_as<IBluetoothLEDevice6>()) {
            try {
                d->parameter_request = d->device.RequestPreferredConnectionParameters(
                    BluetoothLEPreferredConnectionParameters::Balanced());
            } catch (const hresult_error &e) {
                event diagnostic{};
                diagnostic.e.type = POLAR_EVENT_DIAGNOSTIC;
                diagnostic.e.request = c.id;
                diagnostic.e.device = d->id;
                diagnostic.e.hresult = e.code().value;
                diagnostic.e.stage = 50; // Optional connection parameter preference.
                if (auto root = owner.lock())
                    root->push(std::move(diagnostic));
            }
        }
        std::unique_lock lock(c.async_gate);
        for (;;) {
            alive(c, d);
            std::chrono::steady_clock::time_point last;
            {
                std::lock_guard l(d->gate);
                last = std::max(began, d->negotiation_changed);
            }
            const auto until = std::min(limit, last + std::chrono::milliseconds(1200));
            if (std::chrono::steady_clock::now() >= until)
                break;
            c.wake.wait_until(lock, until);
        }
    }
    void authenticate(command &c, const std::shared_ptr<connected> &d, uint32_t attempt,
                      bool explicit_bond) {
        d->authentication_attempts = attempt;
        state(c, d, POLAR_CONNECTION_AUTHENTICATING);
        delay(c, d, 500);
        // WinRT can negotiate encryption as part of the protected ATT operation.
        // First retry that operation with EncryptionRequired; request an explicit
        // bond only if the device still rejects security on the next attempt.
        if (attempt == 1 && !explicit_bond)
            return;
        // BluetoothLEDevice's DeviceInformation is a snapshot; pairing may have
        // changed while Windows was discovering services. Fetch a fresh object.
        auto information =
            await(c, DeviceInformation::CreateFromIdAsync(d->device.DeviceInformation().Id()));
        auto pair = information.Pairing();
        if (!pair.IsPaired()) {
            auto custom = pair.Custom();
            auto weak = c.weak_from_this();
            std::weak_ptr<connected> device = d;
            auto token = custom.PairingRequested([weak, device](auto const &, auto const &args) {
                auto request = weak.lock();
                auto live = device.lock();
                if (request && live && !request->cancel->load() && !live->stale &&
                    args.PairingKind() == DevicePairingKinds::ConfirmOnly)
                    args.Accept();
            });
            try {
                auto r = await(c, custom.PairAsync(DevicePairingKinds::ConfirmOnly,
                                                   DevicePairingProtectionLevel::Encryption));
                d->pairing_status = uint32_t(r.Status());
                if (r.Status() != DevicePairingResultStatus::Paired &&
                    r.Status() != DevicePairingResultStatus::AlreadyPaired)
                    throw failure(POLAR_AUTHENTICATION_FAILED,
                                  "Windows authentication failed; pairing status " +
                                      std::to_string(d->pairing_status));
                custom.PairingRequested(token);
            } catch (...) {
                custom.PairingRequested(token);
                throw;
            }
        } else
            d->pairing_status = uint32_t(DevicePairingResultStatus::AlreadyPaired);
        // Keep the same GATT session and the original ATT operation alive.
        delay(c, d, 500);
    }
    template <class Perform>
    auto secure(command &c, const std::shared_ptr<connected> &d,
                const GattCharacteristic &characteristic, uint32_t stage, Perform perform) {
        auto previous = d->phase.load();
        uint32_t last_att = 0;
        try {
            auto r = authenticated_operation(
                [&] {
                    const auto budget =
                        std::min<uint32_t>(12000, c.c.timeout_ms ? c.c.timeout_ms : 12000);
                    const auto deadline =
                        std::chrono::steady_clock::now() + std::chrono::milliseconds(budget);
                    return settle_security(
                        [&] {
                            alive(c, d);
                            return await(c, perform());
                        },
                        [&](auto const &r) {
                            const auto att =
                                r.ProtocolError() ? uint32_t(r.ProtocolError().Value()) : 0;
                            // Windows can return Unreachable while its link encryption
                            // is still negotiating. Keep the session alive for reads and
                            // CCCD writes, whose repeated execution is idempotent. Never
                            // replay an arbitrary write with an uncertain outcome.
                            return stage != 5 && d->phase == POLAR_CONNECTION_AUTHENTICATING &&
                                   d->authentication_attempts == 1 &&
                                   (att == 5 || att == 15 ||
                                    r.Status() == GattCommunicationStatus::Unreachable);
                        },
                        [&] { delay(c, d, 500); },
                        [&] { return std::chrono::steady_clock::now() < deadline; });
                },
                [&](auto const &r) {
                    const auto att = r.ProtocolError() ? uint32_t(r.ProtocolError().Value()) : 0;
                    if (att)
                        last_att = att;
                    // WinRT may report Unreachable when automatic encryption
                    // cannot start, while this GATT session is still alive.
                    return security_retry_needed(
                        att, stage != 5 && r.Status() == GattCommunicationStatus::Unreachable,
                        d->phase == POLAR_CONNECTION_AUTHENTICATING ? d->authentication_attempts
                                                                    : 0,
                        last_att);
                },
                [&](uint32_t attempt) {
                    if (d->authentication_failed)
                        throw failure(POLAR_AUTHENTICATION_FAILED,
                                      "Authentication recovery exhausted for this session");
                    authenticate(c, d, attempt, stage == 5);
                    characteristic.ProtectionLevel(GattProtectionLevel::EncryptionRequired);
                },
                [&] { alive(c, d); }, d->options.authentication == POLAR_AUTHENTICATE_WHEN_NEEDED);
            checked(r, stage);
            if (d->phase == POLAR_CONNECTION_AUTHENTICATING)
                state(c, d, previous);
            return r;
        } catch (const failure &error) {
            auto f = error;
            if (!f.stage)
                f.stage = stage;
            if (f.code != POLAR_CANCELLED && (f.code == POLAR_AUTHENTICATION_FAILED ||
                                              d->phase == POLAR_CONNECTION_AUTHENTICATING)) {
                f.code = POLAR_AUTHENTICATION_FAILED;
                f.att = last_att;
                f.stage = stage;
                d->authentication_failed = true;
                d->authentication_error = f;
                d->auto_reconnect = false;
                state(c, d, POLAR_CONNECTION_FAILED, f.code);
            }
            throw f;
        } catch (const hresult_error &error) {
            if (d->phase != POLAR_CONNECTION_AUTHENTICATING) {
                failure f(POLAR_OS_ERROR, to_string(error.message()));
                f.hresult = error.code().value;
                f.stage = stage;
                throw f;
            }
            d->authentication_failed = true;
            d->auto_reconnect = false;
            failure f(POLAR_AUTHENTICATION_FAILED,
                      "Windows pairing: " + to_string(error.message()));
            f.hresult = error.code().value;
            f.att = last_att;
            f.stage = stage;
            d->authentication_error = f;
            state(c, d, POLAR_CONNECTION_FAILED, f.code);
            throw f;
        }
    }
    std::shared_ptr<connected> get(uint64_t id) {
        auto i = devices.find(id);
        if (i == devices.end() || i->second->stale)
            throw failure(POLAR_INVALID_HANDLE, "Stale device; reconnect for a new handle");
        return i->second;
    }
    GattCharacteristic characteristic(command &c, std::shared_ptr<connected> const &d, guid service,
                                      guid ch) {
        auto key = std::make_pair(service, ch);
        if (auto i = d->chars.find(key); i != d->chars.end())
            return i->second;
        auto s = d->services.find(service);
        if (s == d->services.end())
            throw failure(POLAR_NOT_SUPPORTED, "GATT service absent");
        auto r =
            await(c, s->second.GetCharacteristicsForUuidAsync(ch, BluetoothCacheMode::Uncached));
        checked(r, 3);
        if (!r.Characteristics().Size())
            throw failure(POLAR_NOT_SUPPORTED, "GATT characteristic absent");
        auto result = r.Characteristics().GetAt(0);
        d->chars.emplace(key, result);
        return result;
    }
    bytes read(command &c, std::shared_ptr<connected> const &d, guid service, guid ch) {
        auto a = characteristic(c, d, service, ch);
        auto r = secure(c, d, a, 4, [&] { return a.ReadValueAsync(BluetoothCacheMode::Uncached); });
        return from_buffer(r.Value());
    }
    void write(command &c, std::shared_ptr<connected> const &d, guid service, guid ch, view data,
               bool response) {
        auto a = characteristic(c, d, service, ch);
        if (data.size() > mtu(d))
            throw failure(POLAR_INVALID_ARGUMENT, "Write exceeds negotiated ATT payload");
        auto option =
            response ? GattWriteOption::WriteWithResponse : GattWriteOption::WriteWithoutResponse;
        secure(c, d, a, 5, [&] { return a.WriteValueWithResultAsync(to_buffer(data), option); });
    }
    uint32_t mtu(std::shared_ptr<connected> const &d) {
        return d->session ? std::min<uint32_t>(512, d->session.MaxPduSize() - 3) : 20;
    }
    void subscribe(command &c, std::shared_ptr<connected> const &d, guid service, guid ch,
                   bool enabled = true) {
        auto existing = d->subscriptions.find(ch);
        if (enabled && existing != d->subscriptions.end())
            return;
        auto a = characteristic(c, d, service, ch);
        auto mode = GattClientCharacteristicConfigurationDescriptorValue::None;
        if (enabled)
            mode = (a.CharacteristicProperties() & GattCharacteristicProperties::Notify) !=
                           GattCharacteristicProperties::None
                       ? GattClientCharacteristicConfigurationDescriptorValue::Notify
                       : GattClientCharacteristicConfigurationDescriptorValue::Indicate;
        if (enabled) {
            std::weak_ptr<connected> weak = d;
            auto ctx = owner;
            auto token = a.ValueChanged(
                [weak, ctx, ch](auto const &, GattValueChangedEventArgs const &arg) noexcept {
                    try {
                        auto dev = weak.lock();
                        auto root = ctx.lock();
                        if (!dev || !root || dev->stale || root->stopping)
                            return;
                        uint64_t received_at = clock_ns();
                        auto buf = arg.CharacteristicValue();
                        if (ch == pmd_data || ch == hr_data || ch == hts_data || ch == bas_level ||
                            ch == bas_status) {
                            uint32_t len = buf.Length();
                            if (len > max_packet)
                                throw failure(POLAR_PROTOCOL_ERROR,
                                              "Notification exceeds ingress packet capacity");
                            std::array<uint8_t, max_packet> storage;
                            DataReader::FromBuffer(buf).ReadBytes(
                                array_view<uint8_t>(storage.data(), storage.data() + len));
                            uint32_t type = ch == hr_data      ? POLAR_HR
                                            : ch == hts_data   ? POLAR_THERMOMETER
                                            : ch == bas_level  ? POLAR_BATTERY_LEVEL
                                            : ch == bas_status ? POLAR_BATTERY_STATUS
                                                               : (len ? storage[0] & 63 : 0);
                            uint64_t stream = 0;
                            {
                                std::lock_guard l(dev->gate);
                                auto s = dev->streams.find(type);
                                if (s != dev->streams.end())
                                    stream = s->second;
                            }
                            if (stream)
                                root->receive(stream, {storage.data(), len}, received_at);
                            return;
                        }
                        auto b = from_buffer(buf);
                        std::shared_ptr<command> pending;
                        {
                            std::lock_guard l(dev->gate);
                            if (dev->pending_characteristic == ch)
                                pending = dev->pending.lock();
                        }
                        if (pending) {
                            std::lock_guard l(pending->async_gate);
                            if (pending->responses.size() < 65536)
                                pending->responses.push_back(std::move(b));
                            else
                                pending->disconnected = true;
                            pending->wake.notify_all();
                            return;
                        }
                        event e{};
                        e.e.device = dev->id;
                        e.e.type = POLAR_EVENT_NOTIFICATION;
                        if (ch == guid{L"54220005-f6a5-4007-a371-722f4ebd8436"}) {
                            std::lock_guard l(dev->gate);
                            if (!dev->telemetry_streaming)
                                return;
                            require(!b.empty(), "Empty telemetry chunk");
                            int sequence = b[0] & 31;
                            if (sequence == dev->telemetry_sequence)
                                return;
                            e.e.flags =
                                dev->telemetry_sequence < 0
                                    ? 0
                                    : uint32_t((sequence - dev->telemetry_sequence - 1 + 32) % 32);
                            e.e.stage = 41;
                            dev->telemetry_sequence = sequence;
                        }
                        if (ch == pmd_cp && !b.empty() && b[0] == 1) {
                            e.e.type = POLAR_EVENT_MEASUREMENT_STOPPED;
                            for (size_t i = 1; i < b.size(); i++) {
                                std::lock_guard l(dev->gate);
                                auto s = dev->streams.find(b[i] & 63);
                                if (s != dev->streams.end()) {
                                    auto stream = root->find_stream(s->second);
                                    std::lock_guard sl(stream->gate);
                                    stream->stopped = true;
                                }
                            }
                        }
                        if (ch == ftp_d2h) {
                            std::shared_ptr<command> waiter;
                            {
                                std::lock_guard l(dev->gate);
                                if (!dev->notification)
                                    dev->notification.emplace();
                                if (!dev->notification->add(b))
                                    return;
                                b = std::move(dev->notification->data);
                                dev->notification.reset();
                                waiter = dev->notification_waiter.lock();
                            }
                            if (waiter) {
                                std::lock_guard l(waiter->async_gate);
                                if (waiter->notifications.size() >= 64)
                                    waiter->disconnected = true;
                                else
                                    waiter->notifications.push_back(b);
                                waiter->wake.notify_all();
                            }
                            e.e.stage = 35;
                            if (!b.empty())
                                e.e.flags = b[0];
                            if (!b.empty() && b[0] == 18) {
                                polar_transform t{};
                                t.struct_size = sizeof(t);
                                t.operation = POLAR_CODEC_REST_EVENTS;
                                t.data = b.data() + 1;
                                t.data_length = uint32_t(b.size() - 1);
                                event typed{};
                                typed.e.type = POLAR_EVENT_REST;
                                typed.e.device = dev->id;
                                typed.e.stage = 35;
                                typed.data = transform(t);
                                root->push(std::move(typed));
                            }
                        }
                        e.data = std::move(b);
                        root->push(std::move(e));
                    } catch (const failure &f) {
                        if (auto root = ctx.lock()) {
                            event e{};
                            e.e.type = POLAR_EVENT_ERROR;
                            e.e.result = f.code;
                            root->push(std::move(e));
                        }
                    } catch (...) {
                    }
                });
            d->subscriptions.emplace(ch, std::make_pair(a, token));
        }
        try {
            secure(c, d, a, 6, [&] {
                return a.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(mode);
            });
        } catch (...) {
            if (enabled) {
                auto i = d->subscriptions.find(ch);
                if (i != d->subscriptions.end()) {
                    a.ValueChanged(i->second.second);
                    d->subscriptions.erase(i);
                }
            }
            throw;
        }
        if (!enabled && existing != d->subscriptions.end()) {
            a.ValueChanged(existing->second.second);
            d->subscriptions.erase(existing);
        }
    }
    bytes next_response(command &c) {
        std::unique_lock l(c.async_gate);
        bool ok = c.wake.wait_for(
            l, std::chrono::milliseconds(c.c.timeout_ms ? c.c.timeout_ms : 30000),
            [&] { return c.cancel->load() || c.disconnected || !c.responses.empty(); });
        if (c.cancel->load())
            throw failure(POLAR_CANCELLED, "Control request cancelled; reconnect");
        if (c.disconnected)
            throw failure(POLAR_DISCONNECTED, "Disconnected during control request");
        if (!ok)
            throw failure(POLAR_TIMEOUT, "Control response timed out; reconnect");
        auto b = std::move(c.responses.front());
        c.responses.pop_front();
        return b;
    }
    bytes transact(command &c, std::shared_ptr<connected> const &d, guid service, guid ch,
                   view request, int protocol) {
        if (d->control_invalid)
            throw failure(POLAR_DISCONNECTED, "Interrupted transaction: reconnect required");
        subscribe(c, d, service, ch);
        if (protocol == 1)
            subscribe(c, d, service, ftp_d2h);
        {
            std::lock_guard l(d->gate);
            d->pending = c.shared_from_this();
            d->pending_characteristic = ch;
        }
        struct reset {
            std::shared_ptr<connected> d;
            ~reset() {
                std::lock_guard l(d->gate);
                d->pending.reset();
            }
        } cleanup{d};
        try {
            if (protocol == 1) {
                auto progress = [&](uint64_t done, uint64_t total, uint32_t direction) {
                    if (auto root = owner.lock()) {
                        auto &path = c.transfer_path.empty() ? c.path : c.transfer_path;
                        event event{};
                        event.e.type = POLAR_EVENT_PROGRESS;
                        event.e.device = d->id;
                        event.e.request = c.id;
                        le(event.data, done, 8);
                        le(event.data, total, 8);
                        le(event.data, path.size(), 4);
                        le(event.data, direction, 4);
                        event.data.insert(event.data.end(), path.begin(), path.end());
                        root->push(std::move(event));
                    }
                };
                auto fragments = pftp_fragment(request, mtu(d));
                uint64_t sent = 0;
                for (size_t i = 0; i < fragments.size(); i++) {
                    write(c, d, service, ch, fragments[i],
                          (i + 1) % 5 == 0 || i + 1 == fragments.size());
                    sent += fragments[i].size() - 1;
                    if ((i + 1) % 32 == 0 || i + 1 == fragments.size())
                        progress(sent, request.size(), 1);
                    std::lock_guard l(c.async_gate);
                    if (i + 1 < fragments.size() && !c.responses.empty()) {
                        pftp_assembler a;
                        a.add(c.responses.front());
                        throw failure(POLAR_PROTOCOL_ERROR, "PFTP write interrupted");
                    }
                }
                pftp_assembler response;
                size_t reported = 0;
                while (!response.add(next_response(c))) {
                    if (response.data.size() >= reported + 16384) {
                        reported = response.data.size();
                        progress(reported, 0, 0);
                    }
                }
                progress(response.data.size(), response.data.size(), 0);
                return response.data;
            }
            write(c, d, service, ch, request, true);
            bytes out;
            std::optional<uint8_t> type;
            for (;;) {
                auto b = next_response(c);
                bounds(b, 0, protocol == 2 ? 3 : 4);
                require(b[0] == 0xf0 && b[1] == request[0], "Control command mismatch");
                if (protocol == 2) {
                    if (b[2] != 1) {
                        failure f(POLAR_DEVICE_ERROR, "PFCS command rejected");
                        f.att = b[2];
                        throw f;
                    }
                    return bytes(b.begin() + 3, b.end());
                }
                if (type)
                    require(*type == b[2], "PMD response changed type");
                if (request[0] >= 1 && request[0] <= 4 && request.size() > 1)
                    require((b[2] & 63) == (request[1] & 63), "PMD response measurement mismatch");
                type = b[2];
                if (b[3]) {
                    failure f(POLAR_DEVICE_ERROR, "PMD command rejected");
                    f.att = b[3];
                    throw f;
                }
                if (b.size() > 5)
                    out.insert(out.end(), b.begin() + 5, b.end());
                require(out.size() <= 65536, "PMD response too large");
                if (b.size() <= 4 || b[4] == 0)
                    return out;
            }
        } catch (const failure &f) {
            if (f.code != POLAR_DEVICE_ERROR)
                d->control_invalid = true;
            throw;
        } catch (...) {
            d->control_invalid = true;
            throw;
        }
    }
    void scan_stop() {
        if (broadcast_stream) {
            if (auto root = owner.lock())
                try {
                    auto s = root->find_stream(broadcast_stream);
                    std::lock_guard l(s->gate);
                    s->stopped = true;
                    s->ready.notify_all();
                } catch (const failure &) {
                }
            broadcast_stream = 0;
        }
        if (watcher) {
            watcher.Received(received);
            watcher.Stopped(stopped);
            watcher.Stop();
            watcher = nullptr;
        }
    }

    void initialize(command &c, const std::shared_ptr<connected> &d, event &result, bool dis_only,
                    uint32_t selected = UINT32_MAX) {
        struct probe {
            uint32_t state = 0;
            int result = 0, hresult = 0;
            uint32_t att = 0;
        };
        std::array<probe, 8> probes{};
        auto &snapshot = d->snapshot;
        snapshot.set_address(d->device.BluetoothAddress());
        snapshot.set_name(to_string(d->device.Name()));
        snapshot.clear_services();
        for (auto &[uuid, service] : d->services)
            snapshot.add_services(&uuid, 16);
        uint32_t requested = selected != UINT32_MAX ? selected
                             : dis_only             ? 2u
                                                    : (c.c.flags ? c.c.flags : 0x3fffffu);
        require((requested & ~0x3fffffu) == 0, "Unknown requested feature bits");
        const std::array<uint32_t, 22> dependencies{1,  2,  4,  8,  24, 16, 16, 16, 8,  16, 32,
                                                    24, 16, 16, 16, 16, 16, 16, 16, 64, 16, 128};
        uint32_t needed = 0;
        for (size_t i = 0; i < dependencies.size(); i++)
            if (requested & (1u << i))
                needed |= dependencies[i];
        auto run = [&](size_t index, guid service, auto action) {
            if (!(needed & (1u << index)))
                return;
            auto &p = probes[index];
            if (!d->services.contains(service)) {
                p.state = 2;
                return;
            }
            try {
                if (d->authentication_failed)
                    throw failure(POLAR_AUTHENTICATION_FAILED,
                                  "Authentication failed for this session");
                alive(c, d);
                if (auto root = owner.lock()) {
                    static const char *names[] = {
                        "Preparing HR notifications",
                        "Reading device information",
                        "Preparing battery notifications",
                        "Preparing PMD control/data notifications and features",
                        "Preparing PSFTP",
                        "Preparing thermometer",
                        "Preparing PFCS",
                        "Preparing telemetry"};
                    event progress{};
                    progress.e.type = POLAR_EVENT_DIAGNOSTIC;
                    progress.e.request = c.id;
                    progress.e.device = d->id;
                    progress.e.stage = uint32_t(60 + index);
                    const auto *name = names[index];
                    progress.data.assign(name, name + std::strlen(name));
                    root->push(std::move(progress));
                }
                action();
                p.state = 1;
            } catch (const failure &f) {
                if (f.code == POLAR_CANCELLED || f.code == POLAR_DISCONNECTED)
                    throw;
                p.state = f.code == POLAR_NOT_SUPPORTED ? 2 : 3;
                p.result = f.code;
                p.hresult = f.hresult;
                p.att = f.att;
            } catch (const hresult_error &e) {
                p.state = 3;
                p.result = POLAR_OS_ERROR;
                p.hresult = e.code().value;
            }
        };
        run(0, hr_service, [&] { subscribe(c, d, hr_service, hr_data); });
        run(1, dis_service, [&] {
            snapshot.clear_attributes();
            auto chars = await(
                c,
                d->services.at(dis_service).GetCharacteristicsAsync(BluetoothCacheMode::Uncached));
            checked(chars, 3);
            for (auto ch : chars.Characteristics()) {
                d->chars.emplace(std::make_pair(dis_service, ch.Uuid()), ch);
                if ((ch.CharacteristicProperties() & GattCharacteristicProperties::Read) !=
                    GattCharacteristicProperties::None) {
                    auto b = read(c, d, dis_service, ch.Uuid());
                    auto field = snapshot.add_attributes();
                    auto id = ch.Uuid();
                    field->set_uuid(&id, 16);
                    field->set_value(b.data(), b.size());
                    field->set_binary(id == guid{L"00002a23-0000-1000-8000-00805f9b34fb"} ||
                                      id == guid{L"00002a50-0000-1000-8000-00805f9b34fb"} ||
                                      id == guid{L"00002a2a-0000-1000-8000-00805f9b34fb"});
                }
            }
        });
        run(2, bas_service, [&] {
            auto chars = await(
                c,
                d->services.at(bas_service).GetCharacteristicsAsync(BluetoothCacheMode::Uncached));
            checked(chars, 3);
            bool supported = false;
            for (auto ch : chars.Characteristics()) {
                d->chars.emplace(std::make_pair(bas_service, ch.Uuid()), ch);
                if (ch.Uuid() == bas_level || ch.Uuid() == bas_status) {
                    supported = true;
                    auto properties = ch.CharacteristicProperties();
                    if ((properties & GattCharacteristicProperties::Read) !=
                        GattCharacteristicProperties::None)
                        read(c, d, bas_service, ch.Uuid());
                    if ((properties & (GattCharacteristicProperties::Notify |
                                       GattCharacteristicProperties::Indicate)) !=
                        GattCharacteristicProperties::None)
                        subscribe(c, d, bas_service, ch.Uuid());
                }
            }
            if (!supported)
                throw failure(POLAR_NOT_SUPPORTED, "Battery characteristics absent");
        });
        run(3, pmd_service, [&] {
            subscribe(c, d, pmd_service, pmd_cp);
            subscribe(c, d, pmd_service, pmd_data);
            auto b = read(c, d, pmd_service, pmd_cp);
            require(b.size() >= 3, "PMD feature bytes");
            snapshot.set_pmd_features(b.data(), b.size());
        });
        run(4, ftp_service, [&] {
            subscribe(c, d, ftp_service, ftp_mtu);
            subscribe(c, d, ftp_service, ftp_d2h);
            characteristic(c, d, ftp_service, ftp_h2d);
        });
        run(5, hts_service, [&] { characteristic(c, d, hts_service, hts_data); });
        run(6, pf_service, [&] {
            auto b = read(c, d, pf_service, guid{L"6217ff4c-c8ec-b1fb-1380-3ad986708e2d"});
            subscribe(c, d, pf_service, pf_cp);
            snapshot.set_pfcs_features(b.data(), b.size());
        });
        run(7, guid{L"54220000-f6a5-4007-a371-722f4ebd8436"}, [&] {
            characteristic(c, d, guid{L"54220000-f6a5-4007-a371-722f4ebd8436"},
                           guid{L"54220005-f6a5-4007-a371-722f4ebd8436"});
        });
        auto profile = profile_for_device(snapshot.name());
        auto bit = [&](size_t type) {
            auto &b = snapshot.pmd_features();
            return b.size() > 1 + type / 8 && (uint8_t(b[1 + type / 8]) & (1u << (type % 8))) != 0;
        };
        for (size_t i = 0; i < dependencies.size(); i++)
            if (requested & (1u << i)) {
                sdk::FeatureReadiness *f = nullptr;
                for (auto &old : *snapshot.mutable_features())
                    if (old.feature() == i)
                        f = &old;
                if (!f)
                    f = snapshot.add_features();
                f->Clear();
                f->set_feature(uint32_t(i));
                f->set_state(1);
                for (size_t base = 0; base < 8; base++)
                    if (dependencies[i] & (1u << base)) {
                        auto p = probes[base];
                        if (p.state != 1) {
                            f->set_state(p.state);
                            f->set_result(p.result);
                            f->set_hresult(p.hresult);
                            f->set_att_error(p.att);
                            break;
                        }
                    }
                if (f->state() == 1) {
                    bool supported = i == 4                                     ? bit(13)
                                     : i == 8 || i == 11                        ? bit(9)
                                     : i == 5                                   ? profile.recording
                                     : i == 6                                   ? profile.h10
                                     : i == 12                                  ? profile.firmware
                                     : i == 13 || i == 14 || i == 15 || i == 17 ? profile.activity
                                     : i == 20                                  ? !profile.sensor
                                                                                : true;
                    if (!supported)
                        f->set_state(2);
                }
            }
        snapshot.clear_hr_measurements();
        if (d->services.contains(hr_service))
            snapshot.add_hr_measurements(POLAR_HR);
        if (d->services.contains(hts_service))
            snapshot.add_hr_measurements(POLAR_THERMOMETER);
        result.data = message_bytes(snapshot);
        if (auto context = owner.lock()) {
            event e{};
            e.e.device = d->id;
            e.e.request = c.id;
            e.e.type = dis_only ? POLAR_EVENT_DEVICE_INFO : POLAR_EVENT_FEATURES;
            e.data = result.data;
            context->push(std::move(e));
        }
    }

    void prepare(command &c, const std::shared_ptr<connected> &d, uint32_t features, event &result,
                 bool strict = true) {
        alive(c, d);
        d->requested_features |= features;
        state(c, d, POLAR_CONNECTION_INITIALIZING);
        initialize(c, d, result, false, features);
        d->ready_features = d->unavailable_features = d->failed_features = 0;
        std::optional<failure> failed;
        for (const auto &f : d->snapshot.features()) {
            const auto bit = 1u << f.feature();
            if (f.state() == 1)
                d->ready_features |= bit;
            else if (f.state() == 2)
                d->unavailable_features |= bit;
            else if (f.state() == 3) {
                d->failed_features |= bit;
                if ((features & bit) && !failed) {
                    failed.emplace(f.result() < 0 ? f.result() : POLAR_OS_ERROR,
                                   "Feature initialization failed: " + std::to_string(f.feature()));
                    failed->att = f.att_error();
                    failed->hresult = f.hresult();
                    failed->stage = 51;
                }
            }
        }
        // Preserve the specific authentication failure even if disconnect came
        // during negotiation. A stale handle must never be announced as ready.
        if (failed && strict) {
            if (failed->code == POLAR_AUTHENTICATION_FAILED && d->authentication_error)
                failed = d->authentication_error;
            state(c, d, POLAR_CONNECTION_FAILED, failed->code);
            throw *failed;
        }
        alive(c, d);
        state(c, d, failed ? POLAR_CONNECTION_FAILED : POLAR_CONNECTION_READY,
              failed ? failed->code : POLAR_OK);
    }
    void require_feature(command &c, const std::shared_ptr<connected> &d, uint32_t feature) {
        const auto mask = 1u << feature;
        if (!(d->ready_features & mask)) {
            event prepared{};
            prepare(c, d, mask, prepared);
        }
        if (!(d->ready_features & mask))
            throw failure(POLAR_NOT_SUPPORTED, "Requested stream feature is unavailable");
    }

  public:
    explicit windows_backend(std::weak_ptr<context> p) : owner(std::move(p)) {}
    void shutdown() override {
        scan_stop();
        radio_stop();
        for (auto &[id, d] : devices)
            d->close();
        devices.clear();
    }
    void abandon(uint64_t id) override {
        if (auto it = devices.find(id); it != devices.end()) {
            it->second->close();
            devices.erase(it);
        }
    }
    void execute(command &c, event &result) override {
        auto root = owner.lock();
        if (!root)
            throw failure(POLAR_CANCELLED, "Context destroyed");
        auto op = c.c.operation;
        if (op == POLAR_OP_SCAN_START) {
            scan_stop();
            require(c.data.size() % 8 == 0 && c.data.size() <= 4096,
                    "Scan address filter must contain uint64 addresses");
            std::vector<uint64_t> filter;
            for (size_t i = 0; i < c.data.size(); i += 8)
                filter.push_back(u(c.data, i, 8));
            if (c.c.flags & 4) {
                auto s = root->new_stream(0, POLAR_HR_ADVERTISEMENT, {});
                broadcast_stream = s->id;
                result.e.stream = s->id;
            }
            auto stream_id = broadcast_stream;
            struct duplicate_state {
                std::mutex gate;
                std::map<uint64_t, bytes> last;
            };
            auto duplicates = std::make_shared<duplicate_state>();
            bool skip_duplicates = (c.c.flags & 2) != 0;
            watcher = BluetoothLEAdvertisementWatcher();
            watcher.ScanningMode(BluetoothLEScanningMode::Active);
            auto ctx = owner;
            bool all = (c.c.flags & 1) != 0;
            received = watcher.Received(
                [ctx, all, filter, stream_id, duplicates, skip_duplicates](
                    auto const &, BluetoothLEAdvertisementReceivedEventArgs const &args) noexcept {
                    try {
                        if (auto owner = ctx.lock()) {
                            if (!filter.empty() &&
                                std::find(filter.begin(), filter.end(), args.BluetoothAddress()) ==
                                    filter.end())
                                return;
                            auto adv = args.Advertisement();
                            auto name = to_string(adv.LocalName());
                            bool polar = name.starts_with("Polar");
                            for (auto md : adv.ManufacturerData())
                                if (md.CompanyId() == 0x6b)
                                    polar = true;
                            if (!all && !polar)
                                return;
                            polar_advertisement data{};
                            data.address = args.BluetoothAddress();
                            data.rssi = args.RawSignalStrengthInDBm();
                            data.connectable = args.IsConnectable();
                            std::memcpy(data.name, name.data(),
                                        std::min(name.size(), sizeof(data.name) - 1));
                            for (auto md : adv.ManufacturerData())
                                if (md.CompanyId() == 0x6b) {
                                    auto b = md.Data();
                                    data.manufacturer_length = std::min<uint32_t>(256, b.Length());
                                    DataReader::FromBuffer(b).ReadBytes(array_view<uint8_t>(
                                        data.manufacturer,
                                        data.manufacturer + data.manufacturer_length));
                                    break;
                                }
                            if (skip_duplicates) {
                                bytes signature(data.manufacturer,
                                                data.manufacturer + data.manufacturer_length);
                                std::lock_guard l(duplicates->gate);
                                auto it = duplicates->last.find(data.address);
                                if (it != duplicates->last.end() && it->second == signature)
                                    return;
                                if (duplicates->last.size() >= 1024)
                                    duplicates->last.erase(duplicates->last.begin());
                                duplicates->last[data.address] = std::move(signature);
                            }
                            if (stream_id && data.manufacturer_length >= 3) {
                                // Address is retained as the exact identifier of each broadcast
                                // sample.
                                bytes frame;
                                le(frame, data.address, 8);
                                frame.insert(frame.end(), data.manufacturer,
                                             data.manufacturer + data.manufacturer_length);
                                owner->receive(stream_id, frame, clock_ns());
                            }
                            event e{};
                            e.e.type = POLAR_EVENT_ADVERTISEMENT;
                            e.data.resize(sizeof(data));
                            std::memcpy(e.data.data(), &data, sizeof(data));
                            owner->push(std::move(e));
                        }
                    } catch (...) {
                    }
                });
            stopped = watcher.Stopped(
                [ctx](auto const &,
                      BluetoothLEAdvertisementWatcherStoppedEventArgs const &args) noexcept {
                    if (args.Error() != BluetoothError::Success)
                        if (auto root = ctx.lock()) {
                            event e{};
                            e.e.type = POLAR_EVENT_ERROR;
                            e.e.result = POLAR_OS_ERROR;
                            e.e.hresult = int(args.Error());
                            root->push(std::move(e));
                        }
                });
            watcher.Start();
            return;
        }
        if (op == POLAR_OP_SCAN_STOP) {
            scan_stop();
            return;
        }
        if (op == POLAR_OP_AUTO_CONNECT) {
            // Optional data: int32 minimum RSSI, uint32 settle milliseconds; path is a name prefix.
            int minimum = -70;
            uint32_t settle = 1500;
            auto options = automatic_connection_options(c.data);
            if (!c.data.empty()) {
                minimum = int32_t(u(c.data, 0, 4));
                settle = uint32_t(u(c.data, 4, 4));
            }
            require(minimum >= -127 && minimum <= 20 && settle <= 10000, "Auto-connect thresholds");
            struct candidate {
                uint64_t address = 0;
                int rssi = -128;
            };
            auto best = std::make_shared<candidate>();
            auto weak = c.weak_from_this();
            auto prefix = c.path;
            BluetoothLEAdvertisementWatcher search;
            search.ScanningMode(BluetoothLEScanningMode::Active);
            auto token = search.Received(
                [weak, best, prefix, minimum](
                    auto const &, BluetoothLEAdvertisementReceivedEventArgs const &args) noexcept {
                    try {
                        if (auto request = weak.lock()) {
                            auto adv = args.Advertisement();
                            auto name = to_string(adv.LocalName());
                            bool polar = name.starts_with("Polar");
                            for (auto item : adv.ManufacturerData())
                                if (item.CompanyId() == 0x6b)
                                    polar = true;
                            auto rssi = args.RawSignalStrengthInDBm();
                            if (!polar || !args.IsConnectable() || rssi < minimum ||
                                (!prefix.empty() && !name.starts_with(prefix)))
                                return;
                            std::lock_guard l(request->async_gate);
                            if (rssi > best->rssi) {
                                best->address = args.BluetoothAddress();
                                best->rssi = rssi;
                            }
                            request->wake.notify_all();
                        }
                    } catch (...) {
                    }
                });
            uint64_t address = 0;
            try {
                search.Start();
                std::unique_lock l(c.async_gate);
                if (!c.wake.wait_for(
                        l, std::chrono::milliseconds(c.c.timeout_ms ? c.c.timeout_ms : 30000),
                        [&] { return c.cancel->load() || best->address != 0; }))
                    throw failure(POLAR_TIMEOUT, "No matching connectable Polar advertisement");
                c.wake.wait_for(l, std::chrono::milliseconds(settle),
                                [&] { return c.cancel->load(); });
                if (c.cancel->load())
                    throw failure(POLAR_CANCELLED, "Auto-connect cancelled");
                address = best->address;
                l.unlock();
                search.Received(token);
                search.Stop();
            } catch (...) {
                search.Received(token);
                search.Stop();
                throw;
            }
            auto saved = c.c;
            auto saved_data = std::move(c.data);
            auto option_bytes = reinterpret_cast<const uint8_t *>(&options);
            c.data.assign(option_bytes, option_bytes + sizeof(options));
            c.c.operation = POLAR_OP_CONNECT;
            c.c.address = address;
            try {
                execute(c, result);
                c.c = saved;
                c.data = std::move(saved_data);
            } catch (...) {
                c.c = saved;
                c.data = std::move(saved_data);
                throw;
            }
            return;
        }
        if (op == POLAR_OP_RADIO) {
            using namespace Windows::Devices::Radios;
            require(c.c.flags <= 2, "Radio flags: 0 snapshot, 1 observe, 2 stop observation");
            if (c.c.flags)
                radio_stop();
            if (c.c.flags == 2)
                return;
            auto radios = await(c, Radio::GetRadiosAsync());
            if (c.c.flags == 1) {
                radio_active = std::make_shared<std::atomic_bool>(true);
                auto active = radio_active;
                auto weak = owner;
                for (auto radio : radios)
                    if (radio.Kind() == RadioKind::Bluetooth) {
                        auto token = radio.StateChanged(
                            [active, weak](Radio const &sender, auto const &) noexcept {
                                try {
                                    if (!active->load())
                                        return;
                                    if (auto context = weak.lock()) {
                                        event e{};
                                        e.e.type = POLAR_EVENT_RADIO;
                                        e.e.flags = uint32_t(sender.State());
                                        le(e.data, e.e.flags, 4);
                                        context->push(std::move(e));
                                    }
                                } catch (...) {
                                }
                            });
                        radio_handlers.emplace_back(radio, token);
                    }
            }
            uint32_t state = 4;
            for (auto radio : radios)
                if (radio.Kind() == RadioKind::Bluetooth) {
                    auto v = radio.State();
                    if (v == RadioState::On) {
                        state = 1;
                        break;
                    }
                    state = v == RadioState::Off ? 2 : v == RadioState::Disabled ? 3 : 0;
                }
            le(result.data, state, 4);
            return;
        }
        if (op == POLAR_OP_CONNECT || op == POLAR_OP_PAIR_STATE || op == POLAR_OP_PAIR ||
            op == POLAR_OP_UNPAIR) {
            if (op == POLAR_OP_CONNECT) {
                event e{};
                e.e.type = POLAR_EVENT_CONNECTING;
                e.e.request = c.id;
                le(e.data, c.c.address, 8);
                root->push(std::move(e));
            }
            for (auto it = devices.begin(); it != devices.end();)
                if (it->second->stale || (op == POLAR_OP_UNPAIR &&
                                          it->second->device.BluetoothAddress() == c.c.address)) {
                    if (op == POLAR_OP_UNPAIR)
                        root->invalidate_device(it->first, POLAR_DISCONNECTED);
                    it->second->close();
                    it = devices.erase(it);
                } else
                    ++it;
            require(c.c.address > 0 && c.c.address <= 0xffffffffffffull,
                    "Invalid Bluetooth address");
            auto d = std::make_shared<connected>();
            d->options = connection_options(c.data);
            d->id = next_handle();
            d->requested_features = d->options.feature_mask;
            if (op == POLAR_OP_CONNECT)
                state(c, d, POLAR_CONNECTION_CONNECTING);
            d->device = await(c, BluetoothLEDevice::FromBluetoothAddressAsync(c.c.address));
            if (!d->device)
                throw failure(POLAR_OS_ERROR, "BluetoothLEDevice unavailable");
            auto pair = d->device.DeviceInformation().Pairing();
            if (op == POLAR_OP_UNPAIR) {
                try {
                    auto r = await(c, pair.UnpairAsync());
                    le(result.data, uint32_t(r.Status()), 4);
                    if (r.Status() != DeviceUnpairingResultStatus::Unpaired &&
                        r.Status() != DeviceUnpairingResultStatus::AlreadyUnpaired)
                        throw failure(POLAR_OS_ERROR, "Windows unpair failed: " +
                                                          std::to_string(uint32_t(r.Status())));
                    d->device.Close();
                    return;
                } catch (...) {
                    d->close();
                    throw;
                }
            }
            if (op == POLAR_OP_PAIR_STATE) {
                auto current_information = await(
                    c, DeviceInformation::CreateFromIdAsync(d->device.DeviceInformation().Id()));
                le(result.data, current_information.Pairing().IsPaired(), 4);
                le(result.data, pair.CanPair(), 4);
                le(result.data, uint32_t(pair.ProtectionLevel()), 4);
                d->device.Close();
                return;
            }
            if (op == POLAR_OP_PAIR) {
                auto custom = pair.Custom();
                auto token = custom.PairingRequested(
                    [](auto const &, DevicePairingRequestedEventArgs const &args) {
                        if (args.PairingKind() == DevicePairingKinds::ConfirmOnly)
                            args.Accept();
                    });
                try {
                    auto r = await(c, custom.PairAsync(DevicePairingKinds::ConfirmOnly,
                                                       DevicePairingProtectionLevel::Encryption));
                    le(result.data, uint32_t(r.Status()), 4);
                    le(result.data, uint32_t(r.ProtectionLevelUsed()), 4);
                    custom.PairingRequested(token);
                    d->device.Close();
                    return;
                } catch (...) {
                    custom.PairingRequested(token);
                    d->device.Close();
                    throw;
                }
            }
            d->auto_reconnect = (c.c.flags & 2) != 0;
            d->link_seen = d->device.ConnectionStatus() == BluetoothConnectionStatus::Connected;
            active_request active(d, c);
            auto ctx = owner;
            std::weak_ptr<connected> weak = d;
            d->disconnected = d->device.ConnectionStatusChanged(
                [weak, ctx](BluetoothLEDevice const &sender, auto const &) noexcept {
                    try {
                        auto dev = weak.lock();
                        auto root = ctx.lock();
                        if (!dev || !root || dev->stale)
                            return;
                        if (sender.ConnectionStatus() == BluetoothConnectionStatus::Connected) {
                            dev->link_seen = true;
                            changed(weak);
                            return;
                        }
                        if (!dev->link_seen)
                            return;
                        if (dev->stale.exchange(true))
                            return;
                        const bool authenticating = dev->phase == POLAR_CONNECTION_AUTHENTICATING;
                        if (authenticating)
                            dev->auto_reconnect = false;
                        root->invalidate_device(dev->id, POLAR_DISCONNECTED);
                        std::shared_ptr<command> pending;
                        std::shared_ptr<command> operation;
                        {
                            std::lock_guard l(dev->gate);
                            pending = dev->pending.lock();
                            operation = dev->active.lock();
                        }
                        if (operation) {
                            std::lock_guard l(operation->async_gate);
                            operation->disconnected = true;
                            operation->wake.notify_all();
                        }
                        if (pending) {
                            std::lock_guard l(pending->async_gate);
                            pending->disconnected = true;
                            pending->wake.notify_all();
                        }
                        event e{};
                        e.e.type = POLAR_EVENT_DISCONNECTED;
                        {
                            std::shared_ptr<command> waiter;
                            {
                                std::lock_guard l(dev->gate);
                                waiter = dev->notification_waiter.lock();
                            }
                            if (waiter) {
                                std::lock_guard l(waiter->async_gate);
                                waiter->disconnected = true;
                                waiter->wake.notify_all();
                            }
                        }
                        e.e.device = dev->id;
                        e.e.result = POLAR_DISCONNECTED;
                        // The still-running CONNECT owns its failure completion.
                        if (dev->initialized)
                            root->push(std::move(e));
                        if (dev->initialized && dev->auto_reconnect) {
                            auto options = reinterpret_cast<const uint8_t *>(&dev->options);
                            root->reconnect(dev->id, sender.BluetoothAddress(),
                                            {options, sizeof(dev->options)});
                        }
                    } catch (...) {
                    }
                });
            try {
                d->session =
                    await(c, GattSession::FromDeviceIdAsync(d->device.BluetoothDeviceId()));
                if (d->session) {
                    d->pdu_changed = d->session.MaxPduSizeChanged(
                        [weak](auto const &, auto const &) { changed(weak); });
                    d->session.MaintainConnection(true);
                }
                if (d->device.try_as<IBluetoothLEDevice6>()) {
                    d->parameters_changed = d->device.ConnectionParametersChanged(
                        [weak](auto const &, auto const &) { changed(weak); });
                    d->phy_changed = d->device.ConnectionPhyChanged(
                        [weak](auto const &, auto const &) { changed(weak); });
                    d->has_parameter_events = true;
                }
                state(c, d, POLAR_CONNECTION_DISCOVERING);
                auto r = await(c, d->device.GetGattServicesAsync(
                                      c.c.flags & 1 ? BluetoothCacheMode::Cached
                                                    : BluetoothCacheMode::Uncached));
                checked(r, 2);
                for (auto s : r.Services())
                    d->services.emplace(s.Uuid(), s);
                d->snapshot.set_address(c.c.address);
                d->snapshot.set_name(to_string(d->device.Name()));
                for (auto &[id, service] : d->services)
                    d->snapshot.add_services(&id, 16);
                negotiate(c, d);
                event prepared{};
                prepare(c, d, d->options.feature_mask, prepared);
                alive(c, d);
                devices.emplace(d->id, d);
                d->initialized = true;
                result.e.device = d->id;
                le(result.data, mtu(d), 4);
                auto current_information = await(
                    c, DeviceInformation::CreateFromIdAsync(d->device.DeviceInformation().Id()));
                le(result.data, current_information.Pairing().IsPaired(), 4);
                for (auto &[id, s] : d->services) {
                    auto raw = reinterpret_cast<const uint8_t *>(&id);
                    result.data.insert(result.data.end(), raw, raw + 16);
                }
                alive(c, d);
                return;
            } catch (...) {
                devices.erase(d->id);
                if (d->phase != POLAR_CONNECTION_FAILED) {
                    int code = POLAR_INTERNAL_ERROR;
                    try {
                        throw;
                    } catch (const failure &f) {
                        code = f.code;
                    } catch (const hresult_error &) {
                        code = POLAR_OS_ERROR;
                    } catch (...) {
                    }
                    state(c, d, POLAR_CONNECTION_FAILED, code);
                }
                d->close();
                throw;
            }
        }
        if (op == POLAR_OP_DISCONNECT) {
            root->invalidate_device(c.c.device, POLAR_DISCONNECTED);
            abandon(c.c.device);
            return;
        }
        if (op == POLAR_OP_RECONNECT_POLICY) {
            require(c.c.flags <= 1, "Reconnect policy: 0 off, 1 on");
            auto it = devices.find(c.c.device);
            if (it == devices.end()) {
                if (c.c.flags == 0)
                    return;
                throw failure(POLAR_INVALID_HANDLE, "Device absent");
            }
            it->second->auto_reconnect = c.c.flags != 0;
            return;
        }
        auto d = get(c.c.device);
        active_request active(d, c);
        alive(c, d);
        if (op == POLAR_OP_INITIALIZE || op == POLAR_OP_DEVICE_INFO) {
            if (op == POLAR_OP_INITIALIZE)
                prepare(c, d, c.c.flags ? c.c.flags : 0x3fffffu, result, false);
            else
                initialize(c, d, result, true);
            return;
        }
        if (op == POLAR_OP_CAPABILITIES) {
            result.data = message_bytes(d->snapshot);
            return;
        }
        if (op == POLAR_OP_SERVICES) {
            for (auto &[id, s] : d->services) {
                auto b = reinterpret_cast<const uint8_t *>(&id);
                result.data.insert(result.data.end(), b, b + 16);
            }
            return;
        }
        if (op == POLAR_OP_READ) {
            result.data = read(c, d, uuid(c.c.service_uuid), uuid(c.c.characteristic_uuid));
            return;
        }
        if (op == POLAR_OP_WRITE) {
            write(c, d, uuid(c.c.service_uuid), uuid(c.c.characteristic_uuid), c.data,
                  (c.c.flags & 1) != 0);
            return;
        }
        if (op == POLAR_OP_SUBSCRIBE) {
            subscribe(c, d, uuid(c.c.service_uuid), uuid(c.c.characteristic_uuid),
                      (c.c.flags & 1) != 0);
            return;
        }
        if (op == POLAR_OP_PMD_FEATURES) {
            result.data = read(c, d, pmd_service, pmd_cp);
            return;
        }
        if (op == POLAR_OP_PMD_SETTINGS) {
            bytes b{uint8_t(c.c.flags & 2 ? 4 : 1),
                    uint8_t(c.c.measurement | (c.c.flags & 1 ? 0x80 : 0))};
            result.data = transact(c, d, pmd_service, pmd_cp, b, 0);
            return;
        }
        if (op == POLAR_OP_PMD_COMMAND) {
            require(!c.data.empty(), "Empty PMD command");
            result.data = transact(c, d, pmd_service, pmd_cp, c.data, 0);
            if (c.data.size() >= 2 && (c.data[0] == 2 || c.data[0] == 3) &&
                c.data[1] == POLAR_SDK_MODE) {
                std::lock_guard l(d->gate);
                for (auto it = d->streams.begin(); it != d->streams.end();) {
                    if (it->first < POLAR_HR) {
                        auto s = root->find_stream(it->second);
                        {
                            std::lock_guard sl(s->gate);
                            s->stopped = true;
                            s->ready.notify_all();
                        }
                        event e{};
                        e.e.type = POLAR_EVENT_MEASUREMENT_STOPPED;
                        e.e.stream = s->id;
                        e.e.device = d->id;
                        root->push(std::move(e));
                        it = d->streams.erase(it);
                    } else
                        ++it;
                }
            }
            return;
        }
        if (op == POLAR_OP_STREAM_START) {
            require(c.c.measurement <= POLAR_BATTERY_STATUS, "Unknown stream type");
            require_feature(c, d,
                            c.c.measurement < POLAR_HR             ? POLAR_FEATURE_STREAMING
                            : c.c.measurement == POLAR_HR          ? POLAR_FEATURE_HR
                            : c.c.measurement == POLAR_THERMOMETER ? POLAR_FEATURE_THERMOMETER
                                                                   : POLAR_FEATURE_BATTERY);
            {
                std::lock_guard l(d->gate);
                if (d->streams.contains(c.c.measurement))
                    throw failure(POLAR_BUSY, "Measurement already streaming");
            }
            bytes settings = c.data;
            if (settings.empty() && c.c.measurement < POLAR_HR && c.c.measurement != POLAR_PPI) {
                bytes query{1, uint8_t(c.c.measurement)};
                settings = select_settings(transact(c, d, pmd_service, pmd_cp, query, 0), true);
            }
            auto stream = root->new_stream(d->id, c.c.measurement, settings);
            {
                std::lock_guard sl(stream->gate);
                stream->initializing = true;
            }
            {
                std::lock_guard l(d->gate);
                d->streams[c.c.measurement] = stream->id;
            }
            try {
                if (c.c.measurement >= POLAR_HR) {
                    auto service = c.c.measurement == POLAR_HR            ? hr_service
                                   : c.c.measurement == POLAR_THERMOMETER ? hts_service
                                                                          : bas_service;
                    auto ch = c.c.measurement == POLAR_HR              ? hr_data
                              : c.c.measurement == POLAR_THERMOMETER   ? hts_data
                              : c.c.measurement == POLAR_BATTERY_LEVEL ? bas_level
                                                                       : bas_status;
                    auto characteristic_value = characteristic(c, d, service, ch);
                    auto properties = characteristic_value.CharacteristicProperties();
                    if ((properties & (GattCharacteristicProperties::Notify |
                                       GattCharacteristicProperties::Indicate)) !=
                        GattCharacteristicProperties::None)
                        subscribe(c, d, service, ch);
                    else if (c.c.measurement < POLAR_BATTERY_LEVEL)
                        throw failure(POLAR_NOT_SUPPORTED, "Measurement notifications unavailable");
                    if (c.c.measurement >= POLAR_BATTERY_LEVEL &&
                        (properties & GattCharacteristicProperties::Read) !=
                            GattCharacteristicProperties::None) {
                        auto initial = read(c, d, service, ch);
                        auto code = root->receive(stream->id, initial, clock_ns());
                        if (code < 0)
                            throw failure(code, "Battery initial receive failed");
                    }
                } else {
                    subscribe(c, d, pmd_service, pmd_cp);
                    subscribe(c, d, pmd_service, pmd_data);
                    bytes b{2, uint8_t(c.c.measurement)};
                    b.insert(b.end(), settings.begin(), settings.end());
                    auto response = transact(c, d, pmd_service, pmd_cp, b, 0);
                    if (!response.empty()) {
                        std::lock_guard l(stream->gate);
                        stream->options.factor = std::bit_cast<float>(get_setting(
                            response, 5, std::bit_cast<uint32_t>(stream->options.factor)));
                    }
                }
            } catch (...) {
                std::lock_guard l(d->gate);
                d->streams.erase(c.c.measurement);
                {
                    std::lock_guard sl(stream->gate);
                    stream->stopped = true;
                    stream->error = POLAR_DISCONNECTED;
                    stream->ready.notify_all();
                }
                {
                    std::lock_guard l(root->gate);
                    root->streams.erase(stream->id);
                }
                throw;
            }
            {
                std::lock_guard sl(stream->gate);
                stream->initializing = false;
                stream->ready.notify_all();
            }
            result.e.stream = stream->id;
            return;
        }
        if (op == POLAR_OP_STREAM_STOP) {
            auto stream = root->find_stream(c.c.stream);
            require(stream->device == d->id, "Stream belongs to another device");
            if (stream->measurement >= POLAR_HR) {
                auto service = stream->measurement == POLAR_HR            ? hr_service
                               : stream->measurement == POLAR_THERMOMETER ? hts_service
                                                                          : bas_service;
                auto ch = stream->measurement == POLAR_HR              ? hr_data
                          : stream->measurement == POLAR_THERMOMETER   ? hts_data
                          : stream->measurement == POLAR_BATTERY_LEVEL ? bas_level
                                                                       : bas_status;
                if (d->subscriptions.contains(ch))
                    subscribe(c, d, service, ch, false);
            } else {
                bytes b{3, uint8_t(stream->measurement)};
                result.data = transact(c, d, pmd_service, pmd_cp, b, 0);
            }
            {
                std::lock_guard l(d->gate);
                d->streams.erase(stream->measurement);
            }
            {
                std::unique_lock l(stream->gate);
                stream->stopped = true;
                stream->ready.wait(l,
                                   [&] { return stream->pending_frames == 0 || root->stopping; });
            }
            result.e.stream = stream->id;
            return;
        }
        if (op == POLAR_OP_PFCS) {
            require(!c.data.empty(), "Empty PFCS command");
            result.data = transact(c, d, pf_service, pf_cp, c.data, 2);
            return;
        }
        if (op == POLAR_OP_SERVICE) {
            auto file_command = [&](const std::string &path,
                                    protocol::PbPFtpOperation::Command action, view data) -> bytes {
                require(!path.empty() && path[0] == '/' && path.find('\0') == std::string::npos,
                        "Invalid device path");
                protocol::PbPFtpOperation p;
                c.transfer_path = path;
                p.set_path(path);
                p.set_command(action);
                auto op = message_bytes(p);
                return transact(c, d, ftp_service, ftp_mtu, pftp_request(op, data), 1);
            };
            service_transport io;
            io.read = [&](const std::string &p) {
                return file_command(p, protocol::PbPFtpOperation::GET, {});
            };
            io.write = [&](const std::string &p, view b) {
                file_command(p, protocol::PbPFtpOperation::PUT, b);
            };
            io.remove = [&](const std::string &p) {
                file_command(p, protocol::PbPFtpOperation::REMOVE, {});
            };
            io.query = [&](uint16_t id, view b) {
                return transact(c, d, ftp_service, ftp_mtu, pftp_query(id, b), 1);
            };
            io.notify = [&](uint8_t id, view b) {
                bytes message{id};
                message.insert(message.end(), b.begin(), b.end());
                for (auto &packet : pftp_fragment(message, mtu(d)))
                    write(c, d, ftp_service, ftp_h2d, packet, false);
            };
            io.check_cancel = [&] {
                if (c.cancel->load())
                    throw failure(POLAR_CANCELLED, "Service cancelled");
            };
            io.feature = [&](view b) {
                io.check_cancel();
                if (b.empty())
                    return read(c, d, pf_service, guid{L"6217ff4c-c8ec-b1fb-1380-3ad986708e2d"});
                return transact(c, d, pf_service, pf_cp, b, 2);
            };
            auto notification_deadline = std::chrono::steady_clock::now();
            io.observe_notifications = [&] {
                subscribe(c, d, ftp_service, ftp_d2h);
                {
                    std::lock_guard l(c.async_gate);
                    c.notifications.clear();
                }
                {
                    std::lock_guard l(d->gate);
                    d->notification_waiter = c.weak_from_this();
                }
                notification_deadline =
                    std::chrono::steady_clock::now() +
                    std::chrono::milliseconds(c.c.timeout_ms ? c.c.timeout_ms : 30000);
            };
            io.wait_notification = [&](uint8_t id) -> bytes {
                std::unique_lock l(c.async_gate);
                for (;;) {
                    if (!c.wake.wait_until(l, notification_deadline, [&] {
                            return c.cancel->load() || c.disconnected || !c.notifications.empty();
                        }))
                        throw failure(POLAR_TIMEOUT, "Notification wait timed out");
                    if (c.cancel->load())
                        throw failure(POLAR_CANCELLED, "Notification wait cancelled");
                    if (c.disconnected)
                        throw failure(POLAR_DISCONNECTED,
                                      "Notification channel disconnected/overflowed");
                    auto b = std::move(c.notifications.front());
                    c.notifications.pop_front();
                    if (!b.empty() && b[0] == id)
                        return bytes(b.begin() + 1, b.end());
                }
            };
            io.pmd = [&](view b) {
                auto saved = c.c;
                auto saved_data = std::move(c.data);
                c.c.operation = POLAR_OP_PMD_COMMAND;
                c.c.device = d->id;
                c.data = copy(b);
                event value;
                try {
                    execute(c, value);
                    c.c = saved;
                    c.data = std::move(saved_data);
                    return value.data;
                } catch (...) {
                    c.c = saved;
                    c.data = std::move(saved_data);
                    throw;
                }
            };
            io.reconnect = [&] {
                auto address = d->device.BluetoothAddress();
                root->invalidate_device(d->id, POLAR_DISCONNECTED);
                d->close();
                devices.erase(d->id);
                auto saved = c.c;
                c.c.operation = POLAR_OP_CONNECT;
                c.c.address = address;
                c.c.flags = 0;
                c.c.device = 0;
                try {
                    auto deadline = std::chrono::steady_clock::now() +
                                    std::chrono::milliseconds(std::max(30000u, saved.timeout_ms));
                    for (;;) {
                        {
                            std::unique_lock l(c.async_gate);
                            c.wake.wait_for(l, std::chrono::seconds(2),
                                            [&] { return c.cancel->load(); });
                        }
                        io.check_cancel();
                        event fresh;
                        try {
                            execute(c, fresh);
                            d = get(fresh.e.device);
                            break;
                        } catch (const failure &f) {
                            if ((f.code != POLAR_OS_ERROR && f.code != POLAR_TIMEOUT &&
                                 f.code != POLAR_DISCONNECTED) ||
                                std::chrono::steady_clock::now() >= deadline)
                                throw;
                        } catch (const hresult_error &) {
                            if (std::chrono::steady_clock::now() >= deadline)
                                throw;
                        }
                    }
                    c.c = saved;
                    c.c.device = d->id;
                } catch (...) {
                    c.c = saved;
                    throw;
                }
            };
            io.persist = [](const std::string &path, view b) {
                auto name = to_hstring(path);
                HANDLE file = CreateFileW(name.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                          FILE_ATTRIBUTE_NORMAL, nullptr);
                if (file == INVALID_HANDLE_VALUE)
                    throw failure(POLAR_OS_ERROR, "Cannot create new firmware recovery backup");
                DWORD written = 0;
                bool ok = WriteFile(file, b.data(), DWORD(b.size()), &written, nullptr) &&
                          written == b.size() && FlushFileBuffers(file);
                CloseHandle(file);
                require(ok, "Firmware recovery backup persistence failed");
            };
            try {
                result.data = run_service(c.c.flags, c.data, io);
            } catch (...) {
                std::lock_guard l(d->gate);
                d->notification_waiter.reset();
                throw;
            }
            {
                std::lock_guard l(d->gate);
                d->notification_waiter.reset();
            }
            result.e.device = d->id;
            return;
        }
        if (op == POLAR_OP_TELEMETRY) {
            guid service{L"54220000-f6a5-4007-a371-722f4ebd8436"},
                data{L"54220005-f6a5-4007-a371-722f4ebd8436"};
            if (c.c.flags == 0) {
                for (auto id : {L"54220001-f6a5-4007-a371-722f4ebd8436",
                                L"54220002-f6a5-4007-a371-722f4ebd8436",
                                L"54220003-f6a5-4007-a371-722f4ebd8436",
                                L"54220004-f6a5-4007-a371-722f4ebd8436"}) {
                    auto b = read(c, d, service, guid{id});
                    le(result.data, b.size(), 4);
                    result.data.insert(result.data.end(), b.begin(), b.end());
                }
            } else if (c.c.flags == 1) {
                subscribe(c, d, service, data);
                {
                    std::lock_guard l(d->gate);
                    d->telemetry_sequence = -1;
                    d->telemetry_streaming = true;
                }
                bytes b{1};
                try {
                    write(c, d, service, data, b, true);
                } catch (...) {
                    std::lock_guard l(d->gate);
                    d->telemetry_streaming = false;
                    throw;
                }
            } else if (c.c.flags == 2) {
                bytes b{0};
                write(c, d, service, data, b, true);
                {
                    std::lock_guard l(d->gate);
                    d->telemetry_streaming = false;
                }
                subscribe(c, d, service, data, false);
            } else
                throw failure(POLAR_INVALID_ARGUMENT, "Telemetry operation");
            return;
        }
        if (op >= POLAR_OP_FILE_GET && op <= POLAR_OP_NOTIFICATION) {
            if (op == POLAR_OP_NOTIFICATION) {
                bytes message{uint8_t(c.c.flags)};
                message.insert(message.end(), c.data.begin(), c.data.end());
                for (auto &p : pftp_fragment(message, mtu(d)))
                    write(c, d, ftp_service, ftp_h2d, p, false);
                return;
            }
            bytes message;
            if (op == POLAR_OP_QUERY)
                message = pftp_query(uint16_t(c.c.flags), c.data);
            else {
                require(!c.path.empty() && c.path[0] == '/' &&
                            c.path.find('\0') == std::string::npos,
                        "Absolute Polar file path required");
                protocol::PbPFtpOperation p;
                p.set_path(c.path);
                p.set_command(op == POLAR_OP_FILE_PUT
                                  ? (c.c.flags & 1 ? protocol::PbPFtpOperation::MERGE
                                                   : protocol::PbPFtpOperation::PUT)
                              : op == POLAR_OP_FILE_REMOVE ? protocol::PbPFtpOperation::REMOVE
                                                           : protocol::PbPFtpOperation::GET);
                auto wire = p.SerializeAsString();
                message =
                    pftp_request({reinterpret_cast<const uint8_t *>(wire.data()), wire.size()},
                                 op == POLAR_OP_FILE_PUT ? view(c.data) : view{});
            }
            result.data = transact(c, d, ftp_service, ftp_mtu, message, 1);
            return;
        }
        throw failure(POLAR_NOT_SUPPORTED, "Unknown SDK operation");
    }
};
std::unique_ptr<backend> make_windows_backend(std::weak_ptr<context> p) {
    return std::make_unique<windows_backend>(std::move(p));
}
} // namespace polar
