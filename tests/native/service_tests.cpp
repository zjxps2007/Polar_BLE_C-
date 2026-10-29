#include "act_samples.pb.h"
#include "dailysummary.pb.h"
#include "device.pb.h"
#include "native_sdk.pb.h"
#include "pftp_response.pb.h"
#include "pftp_notification.pb.h"
#include "services.hpp"
#include "user_devset.pb.h"
#include <fstream>
#include <iostream>
#include <set>
namespace {
using namespace polar;
void check(bool b, const char *m) {
    if (!b)
        throw std::runtime_error(m);
}
template <class F> void fails(F f) {
    bool rejected = false;
    try {
        f();
    } catch (const failure &) {
        rejected = true;
    }
    check(rejected, "Expected service rejection");
}
struct device_mock {
    std::map<std::string, bytes> files;
    std::vector<std::string> events;
    service_transport io;
    bool persisted = false, reset = false, updated = false, multi = true, fail_write = false;
    device_mock() {
        io.check_cancel = [] {};
        io.read = [this](const std::string &p) -> bytes {
            events.push_back("read:" + p);
            if (p.ends_with('/')) {
                protocol::PbPFtpDirectory directory;
                std::set<std::string> names;
                for (auto &[path, contents] : files)
                    if (path.starts_with(p)) {
                        auto name = path.substr(p.size());
                        auto slash = name.find('/');
                        if (slash != std::string::npos)
                            name.resize(slash + 1);
                        if (!name.empty() && names.insert(name).second) {
                            auto f = directory.add_entries();
                            f->set_name(name);
                            f->set_size(name.ends_with('/') ? 0 : contents.size());
                        }
                    }
                return message_bytes(directory);
            }
            auto it = files.find(p);
            if (it == files.end()) {
                failure f(POLAR_DEVICE_ERROR, "Missing mock file");
                f.att = 103;
                throw f;
            }
            return it->second;
        };
        io.write = [this](const std::string &p, view b) {
            events.push_back("write:" + p);
            if (p == "/config/boot.bin" && fail_write)
                throw failure(POLAR_OS_ERROR, "Injected firmware transfer failure");
            files[p] = copy(b);
            if (p == "/SYSUPDAT.IMG")
                updated = true;
        };
        io.remove = [this](const std::string &p) {
            events.push_back("remove:" + p);
            for (auto it = files.begin(); it != files.end();)
                if (it->first == p || it->first.starts_with(p + "/"))
                    it = files.erase(it);
                else
                    ++it;
        };
        io.query = [this](uint16_t id, view) {
            events.push_back("query:" + std::to_string(id));
            if (id == 5) {
                protocol::PbPFtpDiskSpaceResult p;
                p.set_fragment_size(4096);
                p.set_total_fragments(1ull << 40);
                p.set_free_fragments(1ull << 39);
                return message_bytes(p);
            }
            return bytes{};
        };
        io.notify = [this](uint8_t id, view) {
            events.push_back("notify:" + std::to_string(id));
            if (id == 2) {
                check(persisted, "Reset preceded persistent backup");
                reset = true;
            }
        };
        io.reconnect = [this] {
            events.push_back("reconnect");
            if (updated) {
                data::PbDeviceInfo info;
                info.mutable_device_version()->set_major(3);
                info.mutable_device_version()->set_minor(0);
                info.mutable_device_version()->set_patch(17);
                files["/DEVICE.BPB"] = message_bytes(info);
            }
        };
        io.persist = [this](const std::string &, view b) {
            events.push_back("persist");
            check(!b.empty(), "Empty recovery backup");
            persisted = true;
        };
        io.feature = [this](view b) -> bytes {
            if (b.empty())
                return {128, 0};
            if (b[0] == 9)
                return {uint8_t(multi)};
            check(b.size() == 2 && b[0] == 8, "Feature command");
            multi = b[1] != 0;
            events.push_back(multi ? "multi:on" : "multi:off");
            return {};
        };
    }
    sdk::ServiceResult call(uint32_t op, const sdk::ServiceRequest &r = {}) {
        auto b = run_service(op, message_bytes(r), io);
        sdk::ServiceResult out;
        check(out.ParseFromArray(b.data(), int(b.size())), "Service result protobuf");
        return out;
    }
};
} // namespace
void service_tests() {
    using namespace polar;
    device_mock d;
    {
        bool observing = false;
        d.io.observe_notifications = [&] { observing = true; };
        d.io.wait_notification = [&](uint8_t id) {
            check(observing && id == 18, "Sleep event setup order");
            protocol::PbPftpDHRestApiEvent p;
            p.set_uncompressed(true);
            p.add_event("{\"sleep_recording_state\":{\"enabled\":true}}");
            return message_bytes(p);
        };
        check(d.call(POLAR_SERVICE_SLEEP_STATUS).unsigned_value() == 2,
              "Native one-shot sleep status");
        data::PbDeviceInfo device;
        device.mutable_device_version()->set_major(3);
        device.mutable_device_version()->set_minor(0);
        device.mutable_device_version()->set_patch(16);
        device.set_hardware_code("mock");
        d.files["/DEVICE.BPB"] = message_bytes(device);
        d.io.http = [](const std::string &url, view, size_t) {
            check(url.starts_with("https://firmware-management.polar.com/"), "Firmware endpoint");
            std::string json =
                "{\"version\":\"3.0.17\",\"fileUrl\":\"https://example.invalid/"
                "test.zip\",\"hardwareVersions\":[{\"erpNumber\":\"mock\",\"mandatory\":false}]}";
            return bytes(json.begin(), json.end());
        };
        sdk::ServiceRequest r;
        r.set_client_id("native-mock-test");
        r.set_device_id("00000000");
        auto result = d.call(POLAR_SERVICE_FIRMWARE_CHECK, r);
        sdk::FirmwareInfo info;
        info.ParseFromString(result.data());
        check(info.update_available() && info.has_mandatory() && !info.mandatory(),
              "Firmware version/optional mandatory selection");
        device.mutable_device_version()->set_patch(18);
        d.files["/DEVICE.BPB"] = message_bytes(device);
        check(!d.call(POLAR_SERVICE_FIRMWARE_CHECK, r).boolean_value(),
              "Older firmware catalog offered downgrade");
    }
    {
        std::vector<bytes> sent;
        d.io.pmd = [&](view b) -> bytes {
            sent.push_back(copy(b));
            if (b[0] == 7)
                return {1, 0, 2, 0, 3};
            if (b[0] == 6)
                return {1};
            if (b[0] == 5)
                return {0xc2, 0x83};
            return {};
        };
        sdk::ServiceRequest control;
        control.set_flags(2);
        control.set_value(2);
        control.set_offline(true);
        auto setting = control.add_pmd_settings();
        setting->set_type(0);
        setting->add_values(52);
        control.set_key_strategy(2);
        control.set_key(std::string(16, 'x'));
        d.call(POLAR_SERVICE_PMD, control);
        check(sent.back().size() == 25 && sent.back()[0] == 2 && sent.back()[1] == 0x82 &&
                  sent.back()[6] == 6 && sent.back()[8] == 2,
              "Typed PMD offline/secret command");
        control.Clear();
        control.set_flags(12);
        control.set_value(1);
        auto trigger = control.add_triggers();
        trigger->set_measurement(2);
        trigger->set_enabled(true);
        setting = trigger->add_settings();
        setting->set_type(0);
        setting->add_values(52);
        d.call(POLAR_SERVICE_PMD, control);
        check(sent[sent.size() - 2] == bytes({9, 1, 2, 4, 0, 1, 52, 0}) &&
                  sent.back() == bytes({9, 0, 3}),
              "Typed trigger update disables omitted measurements");
        control.Clear();
        control.set_flags(6);
        check(d.call(POLAR_SERVICE_PMD, control).boolean_value(), "SDK mode decode");
        control.Clear();
        control.set_flags(9);
        check(d.call(POLAR_SERVICE_FEATURE, control).boolean_value(), "Feature getter");
        control.set_flags(8);
        control.set_enabled(false);
        d.call(POLAR_SERVICE_FEATURE, control);
        check(!d.multi, "Typed feature setter");
        d.multi = true;
    }
    data::PbUserDeviceSettings config;
    config.mutable_usb_connection_settings()->set_mode(data::PbUsbConnectionSettings::OFF);
    auto original = message_bytes(config);
    original.insert(original.end(), {0xf8, 0x07, 0x7b});
    d.files["/U/0/S/UDEVSET.BPB"] = original;
    sdk::ServiceRequest r;
    r.set_value(1);
    d.call(POLAR_SERVICE_USB, r);
    auto changed = message_parse("data.PbUserDeviceSettings", d.files["/U/0/S/UDEVSET.BPB"]);
    check(changed->GetReflection()->GetUnknownFields(*changed).field_count() == 1,
          "RMW discarded unknown field");
    data::PbUserDeviceSettings known;
    known.ParsePartialFromArray(d.files["/U/0/S/UDEVSET.BPB"].data(),
                                int(d.files["/U/0/S/UDEVSET.BPB"].size()));
    check(known.usb_connection_settings().mode() == data::PbUsbConnectionSettings::ON,
          "USB mode enum");
    r.set_value(1);
    d.call(POLAR_SERVICE_OHR, r);
    d.call(POLAR_SERVICE_TELEMETRY_ENABLE, r);
    r.clear_values();
    r.add_values(101);
    r.add_values(10);
    fails([&] { d.call(POLAR_SERVICE_AUTO_TRAINING, r); });
    r.set_values(0, 50);
    d.call(POLAR_SERVICE_AUTO_TRAINING, r);
    auto disk = d.call(POLAR_SERVICE_DISK);
    check(disk.unsigned_value() == (1ull << 52) && disk.unsigned_values(0) == (1ull << 51),
          "Disk uint64 arithmetic");
    data::PbActivitySamples activity;
    activity.add_steps_samples(5);
    activity.add_steps_samples(7);
    d.files["/U/0/20261029/ACT/001.BPB"] = message_bytes(activity);
    d.files["/U/0/20261029/ACT/HISTORY.TXT"] = {1};
    r.Clear();
    r.set_from_day("20261029");
    r.set_to_day("20261029");
    auto steps = d.call(POLAR_SERVICE_STEPS, r);
    check(steps.unsigned_values(0) == 12 && steps.files_size() == 1, "Activity step aggregation");
    r.set_from_day("20260230");
    fails([&] { d.call(POLAR_SERVICE_DAILY, r); });
    const std::string base = "/U/0/20261029/R/123456/";
    d.files[base + "ACC0.REC"] = {1};
    d.files[base + "ACC1.REC"] = {2};
    d.files[base + "HR.REC"] = {3};
    r.Clear();
    r.set_path(base + "ACC.REC");
    auto parts = d.call(POLAR_SERVICE_OFFLINE_GET, r);
    check(parts.files_size() == 2 && parts.files(0).path() == base + "ACC0.REC",
          "Split recording order");
    d.call(POLAR_SERVICE_OFFLINE_DELETE, r);
    check(d.files.contains(base + "HR.REC") && !d.files.contains(base + "ACC0.REC"),
          "Offline delete preserved sibling type");
    fails([&] { d.call(POLAR_SERVICE_OFFLINE_DELETE, r); });
    r.Clear();
    r.set_path("/U/0/20261029/E/123456/");
    d.files[r.path() + "TSESS.BPB"] = {};
    d.files["/U/0/20261029/E/223456/TSESS.BPB"] = {};
    d.call(POLAR_SERVICE_EXERCISE_DELETE, r);
    check(d.files.contains("/U/0/20261029/E/223456/TSESS.BPB"), "Training delete removed sibling");
    check(d.files.contains("/U/0/20261029/ACT/HISTORY.TXT"),
          "Training delete removed unrelated day data");
    fails([&] { d.call(POLAR_SERVICE_EXERCISE_DELETE, r); });
    d.files["/TRC1.BIN"] = {1};
    d.files["/TRC001.BIN"] = {1};
    d.files["/TRC1234.BIN"] = {1};
    d.files["/DBGTRC1.BIN"] = {1};
    d.call(POLAR_SERVICE_TELEMETRY_DELETE);
    check(!d.files.contains("/TRC1.BIN") && d.files.contains("/TRC1234.BIN") &&
              d.files.contains("/DBGTRC1.BIN"),
          "Telemetry delete scope");
    r.Clear();
    r.set_payload(std::string(16, '\0'));
    d.call(POLAR_SERVICE_WATCHFACE_SET, r);
    r.clear_values();
    r.add_values(5);
    r.add_values(7);
    d.call(POLAR_SERVICE_WATCHFACE_COMPLICATIONS, r);
    auto watch = d.call(POLAR_SERVICE_WATCHFACE_GET);
    check(watch.data().size() == 24 && uint8_t(watch.data()[12]) == 2,
          "Watchface complications RMW");
    r.Clear();
    r.set_path("/REST/SERVICE.API");
    r.set_payload("{invalid");
    fails([&] { d.call(POLAR_SERVICE_REST_PUT, r); });
    r.set_payload("{\"enabled\":false}");
    d.call(POLAR_SERVICE_REST_PUT, r);
    check(d.call(POLAR_SERVICE_REST_GET, r).data() == r.payload(), "REST object preservation");
    std::ifstream input("tests/native/fixtures/firmware-valid.zip", std::ios::binary);
    bytes zip(std::istreambuf_iterator<char>(input), {});
    auto files = parse_zip(zip);
    check(files.size() == 2 && files.back().path == "/SYSUPDAT.IMG",
          "Firmware SYSUPDAT must be last");
    for (bool fail : {false, true}) {
        device_mock firmware;
        firmware.fail_write = fail;
        firmware.files["/U/0/S/UDEVSET.BPB"] = original;
        r.Clear();
        r.set_package(zip.data(), zip.size());
        r.set_path("3.0.17");
        r.set_backup_path("mock-recovery.pb");
        if (fail)
            fails([&] { firmware.call(POLAR_SERVICE_FIRMWARE_UPDATE, r); });
        else
            firmware.call(POLAR_SERVICE_FIRMWARE_UPDATE, r);
        check(firmware.persisted && firmware.reset && firmware.multi,
              "Firmware recovery or multi-BLE restoration failed");
        if (!fail)
            check(firmware.files["/U/0/S/UDEVSET.BPB"] == original,
                  "Firmware restore lost unknown setting");
    }
    device_mock untouched;
    r.set_package("invalid");
    fails([&] { untouched.call(POLAR_SERVICE_FIRMWARE_UPDATE, r); });
    check(untouched.events.empty(), "Bad package caused device mutation");
    std::cout << "PASS native service mocks: settings, dates, deletion preservation, watchface, "
                 "REST, firmware success/failure recovery\n";
}
