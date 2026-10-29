#pragma once
#include "polar_sdk.h"
#include "polar_services.h"
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>
namespace polar::sdk {
class error : public std::runtime_error {
  public:
    polar_result code;
    explicit error(polar_result c)
        : std::runtime_error("Polar SDK error " + std::to_string(c)), code(c) {}
};
inline void check(polar_result c) {
    if (c < 0)
        throw error(c);
}
class context {
    polar_context value_ = 0;

  public:
    explicit context(uint32_t policy = POLAR_LATEST, uint32_t capacity = 4096) {
        polar_config c{sizeof(c), POLAR_ABI_VERSION, capacity, 256, 256, policy, 30000, 0};
        check(polar_sdk_create(&c, &value_));
    }
    ~context() {
        if (value_)
            polar_sdk_destroy(value_);
    }
    context(const context &) = delete;
    context &operator=(const context &) = delete;
    context(context &&o) noexcept : value_(std::exchange(o.value_, 0)) {}
    polar_context native_handle() const noexcept {
        return value_;
    }
    polar_request submit(polar_command c) {
        c.struct_size = sizeof(c);
        polar_request id;
        check(polar_sdk_submit(value_, &c, &id));
        return id;
    }
    polar_request scan(bool all = false) {
        polar_command c{};
        c.operation = POLAR_OP_SCAN_START;
        c.flags = all;
        return submit(c);
    }
    polar_request connect(uint64_t address, bool reconnect = false,
                          uint32_t features = POLAR_DEFAULT_FEATURES, bool authenticate = true) {
        polar_command c{};
        c.operation = POLAR_OP_CONNECT;
        c.address = address;
        c.flags = reconnect ? 2 : 0;
        polar_connect_options options{
            sizeof(options), features,
            uint32_t(authenticate ? POLAR_AUTHENTICATE_WHEN_NEEDED : POLAR_AUTHENTICATION_DISABLED),
            0};
        c.data = reinterpret_cast<const uint8_t *>(&options);
        c.data_length = sizeof(options);
        return submit(c);
    }
    polar_request initialize(polar_device device, uint32_t features = 0) {
        polar_command c{};
        c.operation = POLAR_OP_INITIALIZE;
        c.device = device;
        c.flags = features;
        return submit(c);
    }
    polar_request capabilities(polar_device device) {
        polar_command c{};
        c.operation = POLAR_OP_CAPABILITIES;
        c.device = device;
        return submit(c);
    }
    polar_request unpair(uint64_t address) {
        polar_command c{};
        c.operation = POLAR_OP_UNPAIR;
        c.address = address;
        return submit(c);
    }
    polar_request device_info(polar_device device) {
        polar_command c{};
        c.operation = POLAR_OP_DEVICE_INFO;
        c.device = device;
        return submit(c);
    }
    polar_request observe_radio(bool enabled = true) {
        polar_command c{};
        c.operation = POLAR_OP_RADIO;
        c.flags = enabled ? 1 : 2;
        return submit(c);
    }
    polar_request hr_broadcasts(std::span<const uint64_t> addresses = {}, bool deduplicate = true) {
        polar_command c{};
        c.operation = POLAR_OP_SCAN_START;
        c.flags = 4 | (deduplicate ? 2 : 0);
        c.data = reinterpret_cast<const uint8_t *>(addresses.data());
        c.data_length = uint32_t(addresses.size_bytes());
        return submit(c);
    }
    polar_request start(polar_device d, uint32_t type, std::span<const uint8_t> settings = {}) {
        polar_command c{};
        c.operation = POLAR_OP_STREAM_START;
        c.device = d;
        c.measurement = type;
        c.data = settings.data();
        c.data_length = uint32_t(settings.size());
        return submit(c);
    }
    polar_request stop(polar_device d, polar_stream s) {
        polar_command c{};
        c.operation = POLAR_OP_STREAM_STOP;
        c.device = d;
        c.stream = s;
        return submit(c);
    }
    polar_request file(polar_device d, uint32_t operation, const std::string &path,
                       std::span<const uint8_t> data = {}) {
        polar_command c{};
        c.operation = operation;
        c.device = d;
        c.path = path.data();
        c.path_length = uint32_t(path.size());
        c.data = data.data();
        c.data_length = uint32_t(data.size());
        return submit(c);
    }
    polar_request query(polar_device d, uint32_t query, std::span<const uint8_t> data = {}) {
        polar_command c{};
        c.operation = POLAR_OP_QUERY;
        c.device = d;
        c.flags = query;
        c.data = data.data();
        c.data_length = uint32_t(data.size());
        return submit(c);
    }
    size_t read(polar_stream s, std::span<polar_sample> out) {
        uint32_t n = 0;
        check(polar_sdk_read_samples(value_, s, out.data(), uint32_t(out.size()), &n));
        return n;
    }
    void release(polar_stream s) {
        check(polar_sdk_release_stream(value_, s));
    }
    polar_request reconnect_policy(polar_device d, bool enabled) {
        polar_command c{};
        c.operation = POLAR_OP_RECONNECT_POLICY;
        c.device = d;
        c.flags = enabled;
        return submit(c);
    }
};
// Control-plane RAII. No protobuf library is required by the application.
class message {
    polar_message value_ = 0;

  public:
    explicit message(std::string_view schema, std::span<const uint8_t> wire = {}) {
        check(polar_sdk_message_parse(schema.data(), uint32_t(schema.size()), wire.data(),
                                      uint32_t(wire.size()), &value_));
    }
    ~message() {
        if (value_)
            polar_sdk_message_destroy(value_);
    }
    message(const message &) = delete;
    message &operator=(const message &) = delete;
    message(message &&other) noexcept : value_(std::exchange(other.value_, 0)) {}
    message &set(std::string_view path, polar_field field, std::span<const uint8_t> data = {},
                 uint32_t index = 0) {
        field.struct_size = sizeof(field);
        check(polar_sdk_message_set(value_, path.data(), uint32_t(path.size()), index, &field,
                                    data.data(), uint32_t(data.size())));
        return *this;
    }
    message &uint64(std::string_view path, uint64_t value) {
        polar_field f{};
        f.kind = 4;
        f.present = 1;
        f.unsigned_value = value;
        return set(path, f);
    }
    message &uint32(std::string_view path, uint32_t value, uint32_t index = 0) {
        polar_field f{};
        f.kind = 3;
        f.present = 1;
        f.unsigned_value = value;
        return set(path, f, {}, index);
    }
    message &text(std::string_view path, std::string_view value) {
        polar_field f{};
        f.kind = 9;
        f.present = 1;
        return set(path, f, {reinterpret_cast<const uint8_t *>(value.data()), value.size()});
    }
    message &binary(std::string_view path, std::span<const uint8_t> value, bool nested = false,
                    uint32_t index = 0) {
        polar_field f{};
        f.kind = nested ? 10 : 9;
        f.present = 1;
        return set(path, f, value, index);
    }
    std::vector<uint8_t> serialize() const {
        uint32_t n = 0;
        check(polar_sdk_message_serialize(value_, nullptr, 0, &n));
        std::vector<uint8_t> b(n);
        check(polar_sdk_message_serialize(value_, b.data(), n, &n));
        return b;
    }
    std::pair<polar_field, std::vector<uint8_t>> field(std::string_view path,
                                                       uint32_t index = 0) const {
        polar_field f{};
        f.struct_size = sizeof(f);
        uint32_t n = 0;
        check(polar_sdk_message_field(value_, path.data(), uint32_t(path.size()), index, &f,
                                      nullptr, 0, &n));
        std::vector<uint8_t> b(n);
        if (n)
            check(polar_sdk_message_field(value_, path.data(), uint32_t(path.size()), index, &f,
                                          b.data(), n, &n));
        return {f, std::move(b)};
    }
    polar_request submit(context &c, polar_device device, uint32_t service) const {
        auto b = serialize();
        polar_command command{};
        command.operation = POLAR_OP_SERVICE;
        command.device = device;
        command.flags = service;
        command.data = b.data();
        command.data_length = uint32_t(b.size());
        return c.submit(command);
    }
};
} // namespace polar::sdk
