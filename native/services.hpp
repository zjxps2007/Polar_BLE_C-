#pragma once
#include "common.hpp"
#include <google/protobuf/message.h>
#include <polar/polar_services.h>
namespace polar {
std::unique_ptr<google::protobuf::Message> message_parse(const std::string &, view);
bytes message_bytes(const google::protobuf::Message &);
bytes transform(const polar_transform &);
struct recording {
    polar_offline_info info{};
    std::vector<polar_sample> samples;
};
recording decode_recording(view, uint32_t, view, uint64_t previous = 0,
                           uint32_t derived_methods = 0);
struct archive_file {
    std::string path;
    bytes data;
};
std::vector<archive_file> parse_zip(view);
bytes http_request(const std::string &, view body = {}, size_t limit = 128 * 1024 * 1024,
                   const std::function<void()> &check_cancel = {});
bytes kvtx_get(view, uint32_t key = 1064434511);
bytes kvtx_set(view, uint32_t key = 1064434511);
struct service_transport {
    std::function<bytes(const std::string &)> read;
    std::function<void(const std::string &, view)> write;
    std::function<void(const std::string &)> remove;
    std::function<bytes(uint16_t, view)> query;
    std::function<void(uint8_t, view)> notify;
    std::function<void()> check_cancel;
    std::function<void()> reconnect;
    std::function<bytes(view)> feature;
    std::function<bytes(view)> pmd;
    std::function<bytes(const std::string &, view, size_t)> http;
    std::function<void()> observe_notifications;
    std::function<bytes(uint8_t)> wait_notification;
    std::function<void(const std::string &, view)> persist;
};
bytes run_service(uint32_t, view, service_transport &);
bytes run_control_service(uint32_t, view, service_transport &);
struct device_profile {
    bool h10 = false, recording = false, firmware = true, activity = false, sensor = false;
};
device_profile profile_for_device(std::string name);
} // namespace polar
