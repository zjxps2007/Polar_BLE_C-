#include "act_samples.pb.h"
#include "automatic_samples.pb.h"
#include "dailysummary.pb.h"
#include "device.pb.h"
#include "native_sdk.pb.h"
#include "pftp_notification.pb.h"
#include "pftp_request.pb.h"
#include "pftp_response.pb.h"
#include "services.hpp"
#include "user_devset.pb.h"
#include "user_id.pb.h"
#include "user_physdata.pb.h"
#include <filesystem>
#include <fstream>
#include <google/protobuf/struct.pb.h>
#include <google/protobuf/util/json_util.h>
#include <regex>
#include <set>
#include <sstream>
#include <charconv>
namespace polar {
using Message = google::protobuf::Message;
static view binary(const std::string &s) {
    return {reinterpret_cast<const uint8_t *>(s.data()), s.size()};
}
template <class T> static T parse(view b) {
    T m;
    require(m.ParsePartialFromArray(b.data(), int(b.size())), "Invalid service protobuf");
    return m;
}
static bool missing(const failure &f) {
    return f.code == POLAR_DEVICE_ERROR && f.att == 103;
}
static void valid_path(const std::string &p) {
    require(!p.empty() && p[0] == '/' && p.find('\0') == std::string::npos &&
                p.find("..") == std::string::npos,
            "Invalid device path");
}
static std::string directory(std::string path) {
    valid_path(path);
    if (!path.ends_with('/'))
        path += '/';
    return path;
}
static std::vector<sdk::StoredFile> list(service_transport &io, std::string path, bool recursive) {
    std::deque<std::string> pending{directory(path)};
    std::set<std::string> visited;
    std::vector<sdk::StoredFile> result;
    while (!pending.empty()) {
        io.check_cancel();
        auto dir = pending.front();
        pending.pop_front();
        if (!visited.insert(dir).second)
            continue;
        require(visited.size() <= 10000, "Directory traversal limit");
        auto files = parse<protocol::PbPFtpDirectory>(io.read(dir));
        for (auto &f : files.entries()) {
            auto name = f.name();
            require(!name.empty() && name[0] != '/' && name != "." && name != ".." &&
                        name.find("../") == std::string::npos &&
                        name.find('\\') == std::string::npos,
                    "Unsafe directory entry");
            sdk::StoredFile item;
            item.set_path(dir + name);
            item.set_size(f.size());
            result.push_back(item);
            require(result.size() <= 100000, "File enumeration limit");
            if (recursive && item.path().ends_with('/'))
                pending.push_back(item.path());
        }
    }
    return result;
}
static std::chrono::sys_days day(const std::string &s) {
    require(s.size() == 8 && std::all_of(s.begin(), s.end(),
                                         [](unsigned char c) { return std::isdigit(c) != 0; }),
            "Date must be YYYYMMDD");
    std::chrono::year_month_day value{std::chrono::year{std::stoi(s.substr(0, 4))},
                                      std::chrono::month{unsigned(std::stoi(s.substr(4, 2)))},
                                      std::chrono::day{unsigned(std::stoi(s.substr(6, 2)))}};
    require(value.ok(), "Invalid calendar date");
    return std::chrono::sys_days(value);
}
static std::vector<std::string> dates(const sdk::ServiceRequest &r) {
    auto from = day(r.from_day()), to = day(r.to_day());
    require(to >= from && (to - from).count() <= 3660, "Invalid date range");
    std::vector<std::string> out;
    for (auto d = from; d <= to; d += std::chrono::days(1)) {
        std::chrono::year_month_day ymd{d};
        char b[16];
        std::snprintf(b, sizeof(b), "%04d%02u%02u", int(ymd.year()), unsigned(ymd.month()),
                      unsigned(ymd.day()));
        out.emplace_back(b);
    }
    return out;
}
static void set_scalar(Message &root, std::string path, uint64_t value) {
    Message *m = &root;
    size_t at = 0;
    for (;;) {
        size_t end = path.find('.', at);
        auto field = m->GetDescriptor()->FindFieldByName(
            path.substr(at, end == std::string::npos ? end : end - at));
        require(field && !field->is_repeated(), "Unknown settings field");
        auto refl = m->GetReflection();
        if (end != std::string::npos) {
            require(field->cpp_type() == google::protobuf::FieldDescriptor::CPPTYPE_MESSAGE,
                    "Non-message settings parent");
            m = refl->MutableMessage(m, field);
            at = end + 1;
            continue;
        }
        switch (field->cpp_type()) {
        case google::protobuf::FieldDescriptor::CPPTYPE_BOOL:
            refl->SetBool(m, field, value != 0);
            return;
        case google::protobuf::FieldDescriptor::CPPTYPE_UINT32:
            require(value <= UINT32_MAX, "uint32 overflow");
            refl->SetUInt32(m, field, uint32_t(value));
            return;
        case google::protobuf::FieldDescriptor::CPPTYPE_UINT64:
            refl->SetUInt64(m, field, value);
            return;
        case google::protobuf::FieldDescriptor::CPPTYPE_ENUM:
            require(value <= INT32_MAX && field->enum_type()->FindValueByNumber(int(value)),
                    "Invalid settings enum");
            refl->SetEnumValue(m, field, int(value));
            return;
        default:
            throw failure(POLAR_NOT_SUPPORTED, "Unsupported scalar setting");
        }
    }
}
static void sync_start(service_transport &io) {
    io.query(13, {});
    io.notify(8, {});
    io.notify(0, {});
}
static void sync_stop(service_transport &io, bool success) {
    protocol::PbPFtpStopSyncParams p;
    p.set_completed(success);
    io.notify(1, message_bytes(p));
    io.notify(9, {});
}
static void set_time(service_transport &io, const sdk::ServiceRequest &r, bool h10) {
    require(r.has_unix_time_ms() && r.timezone_minutes() >= -840 && r.timezone_minutes() <= 840,
            "Explicit time/offset required");
    auto fill = [&](int offset, auto &message) {
        auto tp = std::chrono::sys_time<std::chrono::milliseconds>{std::chrono::milliseconds(
                      r.unix_time_ms())} +
                  std::chrono::minutes(offset);
        auto date = std::chrono::floor<std::chrono::days>(tp);
        std::chrono::year_month_day ymd{date};
        std::chrono::hh_mm_ss time{tp - date};
        auto d = message.mutable_date();
        d->set_year(unsigned(int(ymd.year())));
        d->set_month(unsigned(ymd.month()));
        d->set_day(unsigned(ymd.day()));
        auto t = message.mutable_time();
        t->set_hour(uint32_t(time.hours().count()));
        t->set_minute(uint32_t(time.minutes().count()));
        t->set_seconds(uint32_t(time.seconds().count()));
        t->set_millis(uint32_t(time.subseconds().count()));
    };
    if (!h10) {
        protocol::PbPFtpSetSystemTimeParams p;
        fill(0, p);
        p.set_trusted(true);
        try {
            io.query(1, message_bytes(p));
        } catch (const failure &e) {
            if (e.code != POLAR_DEVICE_ERROR || e.att != 201)
                throw;
        }
    }
    protocol::PbPFtpSetLocalTimeParams p;
    fill(r.timezone_minutes(), p);
    p.set_tz_offset(r.timezone_minutes());
    io.query(3, message_bytes(p));
}
static void add_file(sdk::ServiceResult &result, service_transport &io, const std::string &path,
                     const std::string &schema = "") {
    auto b = io.read(path);
    bytes original;
    if (!schema.empty() && b.size() > 2 && b[0] == 0x1f && b[1] == 0x8b) {
        original = b;
        b = gzip_decode(b);
    }
    if (!schema.empty())
        message_parse(schema, b);
    auto f = result.add_files();
    f->set_path(path);
    f->set_size(b.size());
    f->set_data(b.data(), b.size());
    if (!schema.empty())
        f->set_schema(schema);
    if (!original.empty())
        f->set_source_data(original.data(), original.size());
}
static std::vector<sdk::StoredFile> offline_list(service_transport &io) {
    std::vector<sdk::StoredFile> out;
    std::set<std::string> seen;
    try {
        auto b = io.read("/PMDFILES.TXT");
        std::istringstream input(std::string(b.begin(), b.end()));
        std::string line;
        while (std::getline(input, line)) {
            std::istringstream row(line);
            uint64_t size = 0;
            std::string path, size_text;
            if (row >> size_text >> path) {
                auto parsed =
                    std::from_chars(size_text.data(), size_text.data() + size_text.size(), size);
                if (path.ends_with(".REC"))
                    require(parsed.ec == std::errc{} &&
                                parsed.ptr == size_text.data() + size_text.size(),
                            "Invalid recording index size");
                if (size && path.ends_with(".REC")) {
                    valid_path(path);
                    require(std::regex_match(
                                path, std::regex("^/U/0/[0-9]{8}/R/[0-9]{6}/[A-Z]+[0-9]*[.]REC$")),
                            "Invalid recording index path");
                    day(path.substr(5, 8));
                    require(seen.insert(path).second, "Duplicate recording index entry");
                    sdk::StoredFile f;
                    f.set_path(path);
                    f.set_size(size);
                    out.push_back(f);
                }
            }
        }
        if (!out.empty())
            return out;
    } catch (const failure &e) {
        if (!missing(e) && !(e.code == POLAR_DEVICE_ERROR && e.att == 201))
            throw;
    }
    for (auto &d : list(io, "/U/0/", false))
        if (std::regex_match(d.path(), std::regex("^/U/0/[0-9]{8}/$"))) {
            try {
                for (auto &f : list(io, d.path() + "R/", true))
                    if (f.path().ends_with(".REC"))
                        out.push_back(f);
            } catch (const failure &e) {
                if (!missing(e))
                    throw;
            }
        }
    return out;
}
static std::vector<sdk::StoredFile> offline_parts(service_transport &io, const std::string &path) {
    std::smatch match;
    require(std::regex_match(path, match,
                             std::regex("^(/U/0/[0-9]{8}/R/[0-9]{6}/)([A-Z]+)[0-9]*[.]REC$")),
            "Invalid recording path");
    auto dir = match[1].str(), type = match[2].str();
    std::vector<std::pair<int, sdk::StoredFile>> indexed;
    for (auto &f : list(io, dir, false)) {
        std::smatch part;
        std::string name = f.path().substr(dir.size());
        if (std::regex_match(name, part, std::regex("^" + type + "([0-9]*)[.]REC$"))) {
            int id = part[1].str().empty() ? -1 : std::stoi(part[1]);
            indexed.emplace_back(id, f);
        }
    }
    std::sort(indexed.begin(), indexed.end(), [](auto &a, auto &b) { return a.first < b.first; });
    require(!indexed.empty(), "Recording no longer exists");
    std::vector<sdk::StoredFile> out;
    for (size_t i = 0; i < indexed.size(); i++) {
        require(indexed[i].first == (indexed.size() == 1 && indexed[0].first == -1 ? -1 : int(i)),
                "Missing/duplicate recording part");
        out.push_back(indexed[i].second);
    }
    return out;
}
bytes run_service(uint32_t kind, view input, service_transport &io) {
    if (kind == POLAR_SERVICE_PMD || kind == POLAR_SERVICE_FEATURE)
        return run_control_service(kind, input, io);
    auto r = parse<sdk::ServiceRequest>(input);
    sdk::ServiceResult out;
    bool h10 = (r.flags() & 1) != 0;
    std::string settings = h10 ? "/UDEVSET.BPB" : "/U/0/S/UDEVSET.BPB";
    auto return_data = [&](bytes b, const std::string &schema = "") {
        out.set_data(b.data(), b.size());
        if (!schema.empty()) {
            message_parse(schema, b);
            out.set_schema(schema);
        }
    };
    switch (kind) {
    case POLAR_SERVICE_LIST_FILES:
        for (auto &f : list(io, r.path().empty() ? "/" : r.path(), (r.flags() & 2) != 0))
            *out.add_files() = f;
        break;
    case POLAR_SERVICE_READ_MESSAGE:
        add_file(out, io, r.path(), r.schema());
        break;
    case POLAR_SERVICE_MERGE_MESSAGE: {
        auto base = message_parse(r.schema(), io.read(r.path())),
             patch = message_parse(r.schema(), binary(r.payload()));
        base->MergeFrom(*patch);
        io.write(r.path(), message_bytes(*base));
        break;
    }
    case POLAR_SERVICE_TIME_SET:
        set_time(io, r, h10);
        break;
    case POLAR_SERVICE_TIME_GET:
        return_data(io.query(4, {}), "protocol.PbPFtpSetLocalTimeParams");
        break;
    case POLAR_SERVICE_DISK: {
        auto p = parse<protocol::PbPFtpDiskSpaceResult>(io.query(5, {}));
        require(!p.fragment_size() || p.total_fragments() <= UINT64_MAX / p.fragment_size(),
                "Disk size overflow");
        require(!p.fragment_size() || p.free_fragments() <= UINT64_MAX / p.fragment_size(),
                "Disk free overflow");
        out.set_unsigned_value(p.total_fragments() * p.fragment_size());
        out.add_unsigned_values(p.free_fragments() * p.fragment_size());
        return_data(message_bytes(p), "protocol.PbPFtpDiskSpaceResult");
        break;
    }
    case POLAR_SERVICE_SYNC_START:
        sync_start(io);
        break;
    case POLAR_SERVICE_SYNC_STOP:
        sync_stop(io, r.value() != 0);
        break;
    case POLAR_SERVICE_RESET: {
        protocol::PbPFtpFactoryResetParams p;
        p.set_sleep((r.flags() & 1) != 0);
        p.set_do_factory_defaults((r.flags() & 2) != 0);
        p.set_hibernate((r.flags() & 4) != 0);
        p.set_ota_fwupdate((r.flags() & 8) != 0);
        io.notify(2, message_bytes(p));
        break;
    }
    case POLAR_SERVICE_SETTINGS_GET:
        add_file(out, io, settings, "data.PbUserDeviceSettings");
        break;
    case POLAR_SERVICE_SETTINGS_MERGE:
    case POLAR_SERVICE_LOCATION:
    case POLAR_SERVICE_USB:
    case POLAR_SERVICE_TELEMETRY_ENABLE:
    case POLAR_SERVICE_OHR:
    case POLAR_SERVICE_AUTO_TRAINING: {
        auto m = message_parse("data.PbUserDeviceSettings", io.read(settings));
        if (kind == POLAR_SERVICE_SETTINGS_MERGE)
            m->MergeFrom(*message_parse("data.PbUserDeviceSettings", binary(r.payload())));
        else if (kind == POLAR_SERVICE_LOCATION)
            set_scalar(*m, "general_settings.device_location", r.value());
        else if (kind == POLAR_SERVICE_USB)
            set_scalar(*m, "usb_connection_settings.mode", r.value() ? 2 : 1);
        else if (kind == POLAR_SERVICE_TELEMETRY_ENABLE)
            set_scalar(*m, "telemetry_settings.telemetry_enabled", r.value());
        else if (kind == POLAR_SERVICE_OHR)
            set_scalar(*m, "automatic_measurement_settings.automatic_ohr_measurement.state",
                       r.value() ? 1 : 0);
        else {
            require(r.values_size() == 2 && r.values(0) <= 100, "Training detection settings");
            set_scalar(*m,
                       "automatic_measurement_settings.automatic_training_detection_settings.state",
                       r.value() ? 1 : 0);
            set_scalar(
                *m,
                "automatic_measurement_settings.automatic_training_detection_settings.sensitivity",
                r.values(0));
            set_scalar(*m,
                       "automatic_measurement_settings.automatic_training_detection_settings."
                       "minimum_training_duration_seconds",
                       r.values(1));
        }
        io.write(settings, message_bytes(*m));
        break;
    }
    case POLAR_SERVICE_LED: {
        bytes b{uint8_t((r.flags() & 1) != 0), uint8_t((r.flags() & 2) != 0)};
        io.write("/LEDCFG.BIN", b);
        break;
    }
    case POLAR_SERVICE_PHYSICAL:
        add_file(out, io, "/U/0/S/PHYSDATA.BPB", "data.PbUserPhysData");
        break;
    case POLAR_SERVICE_FTU_STATUS:
        try {
            auto m = parse<data::PbUserIdentifier>(io.read("/U/0/USERID.BPB"));
            out.set_boolean_value(m.has_master_identifier());
        } catch (const failure &e) {
            if (!missing(e))
                throw;
            out.set_boolean_value(false);
        }
        break;
    case POLAR_SERVICE_FTU: {
        auto physical = message_parse("data.PbUserPhysData", binary(r.payload()));
        require(physical->IsInitialized(), "Incomplete physical configuration");
        auto &phys = static_cast<const data::PbUserPhysData &>(*physical);
        auto birth = phys.birthday().value();
        require((std::chrono::year{int(birth.year())} / birth.month() / birth.day()).ok(),
                "Invalid birthday");
        if (phys.has_height())
            require(phys.height().value() >= 90 && phys.height().value() <= 240,
                    "Height outside 90..240 cm");
        if (phys.has_weight())
            require(phys.weight().value() >= 15 && phys.weight().value() <= 300,
                    "Weight outside 15..300 kg");
        if (phys.has_maximum_heartrate())
            require(phys.maximum_heartrate().value() >= 100 &&
                        phys.maximum_heartrate().value() <= 240,
                    "Maximum heart rate range");
        if (phys.has_resting_heartrate())
            require(phys.resting_heartrate().value() >= 20 &&
                        phys.resting_heartrate().value() <= 120,
                    "Resting heart rate range");
        if (phys.has_vo2max())
            require(phys.vo2max().value() >= 10 && phys.vo2max().value() <= 95, "VO2 range");
        if (phys.has_training_background())
            require(phys.training_background().value() >= 10 &&
                        phys.training_background().value() <= 60 &&
                        phys.training_background().value() % 10 == 0,
                    "Training background range");
        if (phys.has_sleep_goal())
            require(phys.sleep_goal().sleep_goal_minutes() >= 300 &&
                        phys.sleep_goal().sleep_goal_minutes() <= 660,
                    "Sleep goal range");
        sync_start(io);
        bool completed = false;
        try {
            set_time(io, r, false);
            io.write("/U/0/S/PHYSDATA.BPB", message_bytes(*physical));
            data::PbUserIdentifier id;
            id.set_master_identifier(r.has_value() ? r.value() : UINT64_MAX);
            auto tp = std::chrono::sys_time<std::chrono::milliseconds>{
                std::chrono::milliseconds(r.unix_time_ms())};
            auto daypart = std::chrono::floor<std::chrono::days>(tp);
            std::chrono::year_month_day ymd{daypart};
            std::chrono::hh_mm_ss clock{tp - daypart};
            auto dt = id.mutable_user_id_last_modified();
            dt->set_trusted(true);
            dt->mutable_date()->set_year(int(ymd.year()));
            dt->mutable_date()->set_month(unsigned(ymd.month()));
            dt->mutable_date()->set_day(unsigned(ymd.day()));
            dt->mutable_time()->set_hour(uint32_t(clock.hours().count()));
            dt->mutable_time()->set_minute(uint32_t(clock.minutes().count()));
            dt->mutable_time()->set_seconds(uint32_t(clock.seconds().count()));
            dt->mutable_time()->set_millis(uint32_t(clock.subseconds().count()));
            io.write("/U/0/USERID.BPB", message_bytes(id));
            completed = true;
            sync_stop(io, true);
        } catch (...) {
            if (!completed)
                try {
                    sync_stop(io, false);
                } catch (...) {
                }
            throw;
        }
        break;
    }
    case POLAR_SERVICE_DAILY:
    case POLAR_SERVICE_ACTIVITY:
    case POLAR_SERVICE_NIGHTLY:
    case POLAR_SERVICE_SLEEP:
    case POLAR_SERVICE_SKIN_TEMP:
    case POLAR_SERVICE_SPO2:
    case POLAR_SERVICE_STEPS:
    case POLAR_SERVICE_DISTANCE:
    case POLAR_SERVICE_CALORIES: {
        for (auto &date : dates(r)) {
            io.check_cancel();
            std::string base = "/U/0/" + date + "/";
            try {
                if (kind == POLAR_SERVICE_ACTIVITY || kind == POLAR_SERVICE_STEPS) {
                    uint64_t sum = 0;
                    for (auto &f : list(io, base + "ACT/", false))
                        if (f.path().ends_with(".BPB")) {
                            auto b = io.read(f.path());
                            auto m = parse<data::PbActivitySamples>(b);
                            for (auto v : m.steps_samples()) {
                                require(sum <= UINT64_MAX - v, "Step count overflow");
                                sum += v;
                            }
                            auto file = out.add_files();
                            *file = f;
                            file->set_data(b.data(), b.size());
                            file->set_schema("data.PbActivitySamples");
                        }
                    if (kind == POLAR_SERVICE_STEPS)
                        out.add_unsigned_values(sum);
                } else if (kind == POLAR_SERVICE_SLEEP) {
                    add_file(out, io, base + "SLEEP/SLEEPRES.BPB", "data.PbSleepAnalysisResult");
                    try {
                        add_file(out, io, base + "NSTRESUL/NSTRCONT.BPB",
                                 "data.PbSleepSkinTemperatureResult");
                    } catch (const failure &e) {
                        if (!missing(e))
                            throw;
                    }
                } else if (kind == POLAR_SERVICE_NIGHTLY)
                    add_file(out, io, base + "NR/NR.BPB", "data.PbNightlyRecoveryStatus");
                else if (kind == POLAR_SERVICE_SKIN_TEMP)
                    add_file(out, io, base + "SKINTEMP/TEMPCONT.BPB",
                             "data.TemperatureMeasurementPeriod");
                else if (kind == POLAR_SERVICE_SPO2) {
                    for (auto &f : list(io, base + "SPO2TEST/", true))
                        if (f.path().ends_with("/SPO2TRES.BPB"))
                            add_file(out, io, f.path(), "data.PbSpo2TestResult");
                } else {
                    auto b = io.read(base + "DSUM/DSUM.BPB");
                    auto m = parse<data::PbDailySummary>(b);
                    auto f = out.add_files();
                    f->set_path(base + "DSUM/DSUM.BPB");
                    f->set_data(b.data(), b.size());
                    f->set_schema("data.PbDailySummary");
                    if (kind == POLAR_SERVICE_DISTANCE && m.has_activity_distance())
                        out.add_real_values(m.activity_distance());
                    if (kind == POLAR_SERVICE_CALORIES) {
                        require(r.value() <= 2, "Calorie type");
                        if (r.value() == 0 && m.has_activity_calories())
                            out.add_unsigned_values(m.activity_calories());
                        else if (r.value() == 1 && m.has_training_calories())
                            out.add_unsigned_values(m.training_calories());
                        else if (r.value() == 2 && m.has_bmr_calories())
                            out.add_unsigned_values(m.bmr_calories());
                    }
                }
            } catch (const failure &e) {
                if (!missing(e))
                    throw;
            }
        }
        break;
    }
    case POLAR_SERVICE_ACTIVE_TIME: {
        for (const auto &date : dates(r)) {
            try {
                auto path = "/U/0/" + date + "/DSUM/DSUM.BPB";
                auto m = parse<data::PbDailySummary>(io.read(path));
                if (m.has_activity_class_times()) {
                    auto f = out.add_files();
                    f->set_path(path);
                    f->set_schema("data.PbActivityClassTimes");
                    auto b = message_bytes(m.activity_class_times());
                    f->set_data(b.data(), b.size());
                }
            } catch (const failure &e) {
                if (!missing(e))
                    throw;
            }
        }
        break;
    }
    case POLAR_SERVICE_AUTOMATIC_PPI:
    case POLAR_SERVICE_AUTOMATIC: {
        sdk::PpiSessions decoded;
        auto start = day(r.from_day()), end = day(r.to_day());
        require(end >= start, "Invalid date range");
        try {
            for (auto &f : list(io, "/U/0/AUTOS/", false))
                if (std::regex_search(f.path(), std::regex("/AUTOS[0-9]{3}[.]BPB$"))) {
                    auto b = io.read(f.path());
                    auto m = parse<data::PbAutomaticSampleSessions>(b);
                    auto ymd =
                        std::chrono::year{int(m.day().year())} / m.day().month() / m.day().day();
                    require(ymd.ok(), "Automatic sample date");
                    auto d = std::chrono::sys_days(ymd);
                    if (d >= start && d <= end) {
                        auto dest = out.add_files();
                        *dest = f;
                        dest->set_data(b.data(), b.size());
                        dest->set_schema("data.PbAutomaticSampleSessions");
                        if (kind == POLAR_SERVICE_AUTOMATIC_PPI) {
                            for (const auto &session : m.ppi_samples()) {
                                require(session.has_ppi() && session.has_recording_time(),
                                        "PPI session missing time/samples");
                                const auto &values = session.ppi();
                                require(values.ppi_delta_size() ==
                                                values.ppi_error_estimate_delta_size() &&
                                            values.ppi_delta_size() == values.status_size(),
                                        "PPI array lengths differ");
                                auto dest = decoded.add_sessions();
                                char date[16];
                                std::snprintf(date, sizeof(date), "%04u%02u%02u", m.day().year(),
                                              m.day().month(), m.day().day());
                                dest->set_day(date);
                                dest->set_source(session.SerializePartialAsString());
                                int64_t interval = 0, error = 0;
                                for (int i = 0; i < values.ppi_delta_size(); i++) {
                                    interval += values.ppi_delta(i);
                                    error += values.ppi_error_estimate_delta(i);
                                    require(interval >= 0 && interval <= INT32_MAX && error >= 0 &&
                                                error <= INT32_MAX,
                                            "PPI delta overflow");
                                    auto p = dest->add_samples();
                                    p->set_interval_ms(int32_t(interval));
                                    p->set_error_ms(int32_t(error));
                                    p->set_status(values.status(i));
                                }
                            }
                        }
                    }
                }
        } catch (const failure &e) {
            if (!missing(e))
                throw;
        }
        if (kind == POLAR_SERVICE_AUTOMATIC_PPI) {
            out.set_data(decoded.SerializePartialAsString());
            out.set_schema("polar.sdk.PpiSessions");
        }
        break;
    }
    case POLAR_SERVICE_OFFLINE_LIST:
        for (auto &f : offline_list(io))
            *out.add_files() = f;
        break;
    case POLAR_SERVICE_OFFLINE_GET:
        for (auto &f : offline_parts(io, r.path()))
            add_file(out, io, f.path());
        break;
    case POLAR_SERVICE_OFFLINE_COUNT:
        try {
            out.set_unsigned_value(offline_parts(io, r.path()).size());
        } catch (const failure &f) {
            if (!missing(f))
                throw;
            out.set_unsigned_value(0);
        }
        break;
    case POLAR_SERVICE_OFFLINE_GROUPS: {
        std::map<std::string, std::vector<std::pair<uint32_t, sdk::StoredFile>>> groups;
        for (auto &file : offline_list(io)) {
            std::smatch match;
            require(
                std::regex_match(file.path(), match,
                                 std::regex("^(/U/0/[0-9]{8}/R/[0-9]{6}/)([A-Z]+)([0-9]*)[.]REC$")),
                "Invalid recording group path");
            uint32_t index = 0;
            auto part = match[3].str();
            if (!part.empty()) {
                auto p = std::from_chars(part.data(), part.data() + part.size(), index);
                require(p.ec == std::errc{} && p.ptr == part.data() + part.size(),
                        "Recording part index");
            }
            groups[match[1].str() + match[2].str() + ".REC"].emplace_back(index, file);
        }
        sdk::OfflineRecordingGroups result;
        for (auto &[path, parts] : groups) {
            std::sort(parts.begin(), parts.end(),
                      [](auto &a, auto &b) { return a.first < b.first; });
            auto group = result.add_recordings();
            group->set_path(path);
            group->set_measurement(
                path.substr(path.rfind('/') + 1, path.size() - path.rfind('/') - 5));
            uint64_t size = 0;
            for (auto &[index, file] : parts) {
                require(UINT64_MAX - size >= file.size(), "Recording group size overflow");
                size += file.size();
                *group->add_parts() = file;
            }
            group->set_size(size);
        }
        return_data(message_bytes(result), "polar.sdk.OfflineRecordingGroups");
        break;
    }
    case POLAR_SERVICE_OFFLINE_DELETE: {
        auto parts = offline_parts(io, r.path());
        for (auto &f : parts)
            io.remove(f.path());
        auto dir = r.path().substr(0, r.path().rfind('/') + 1);
        while (dir != "/U/0/" && dir.starts_with("/U/0/")) {
            if (!list(io, dir, false).empty())
                break;
            io.remove(dir.substr(0, dir.size() - 1));
            dir = dir.substr(0, dir.rfind('/', dir.size() - 2) + 1);
        }
        break;
    }
    case POLAR_SERVICE_DELETE_DATE_FOLDERS:
        for (const auto &date : dates(r)) {
            try {
                io.remove("/U/0/" + date);
            } catch (const failure &e) {
                if (!missing(e))
                    throw;
            }
        }
        break;
    case POLAR_SERVICE_DELETE_STORED: {
        // Schema selects a bounded category. path is deliberately not used for deletion.
        static const std::set<std::string> allowed{"ACT",      "DSUM",     "NR",       "SLEEP",
                                                   "SLEEPSCO", "SKINCONT", "SKINTEMP", "SPO2TEST",
                                                   "AUTOS",    "SDLOGS"};
        require(allowed.contains(r.schema()), "Unknown stored data category");
        auto until = day(r.to_day());
        std::vector<std::string> removed;
        auto remove_files = [&](const std::string &path) {
            try {
                for (const auto &f : list(io, path, true)) {
                    bool match = r.schema() == "SDLOGS"
                                     ? (f.path().ends_with(".SLG") || f.path().ends_with(".TXT"))
                                     : (f.path().ends_with(".BPB") &&
                                        f.path().find("HIST") == std::string::npos);
                    if (match) {
                        io.remove(f.path());
                        removed.push_back(f.path());
                    }
                }
            } catch (const failure &e) {
                if (!missing(e))
                    throw;
            }
        };
        if (r.schema() == "SDLOGS")
            remove_files("/SDLOGS/");
        else if (r.schema() == "AUTOS") {
            sdk::ServiceRequest query;
            query.set_from_day("00010101");
            query.set_to_day(r.to_day());
            auto result = parse<sdk::ServiceResult>(
                run_service(POLAR_SERVICE_AUTOMATIC, message_bytes(query), io));
            for (const auto &f : result.files())
                io.remove(f.path());
        } else {
            for (const auto &f : list(io, "/U/0/", false)) {
                if (!std::regex_match(f.path(), std::regex("^/U/0/[0-9]{8}/$")))
                    continue;
                std::chrono::sys_days date;
                try {
                    date = day(f.path().substr(5, 8));
                } catch (const failure &) {
                    continue;
                }
                if (date <= until)
                    remove_files(f.path() + r.schema() + "/");
            }
        }
        std::set<std::string> dirs;
        for (auto path : removed)
            while (path.starts_with("/U/0/")) {
                path = path.substr(0, path.rfind('/'));
                if (path == "/U/0" || path.empty())
                    break;
                dirs.insert(path);
            }
        std::vector<std::string> ordered(dirs.begin(), dirs.end());
        std::sort(ordered.begin(), ordered.end(),
                  [](auto &a, auto &b) { return a.size() > b.size(); });
        for (const auto &path : ordered)
            try {
                if (list(io, path, false).empty())
                    io.remove(path);
            } catch (const failure &e) {
                if (!missing(e))
                    throw;
            }
        break;
    }
    case POLAR_SERVICE_H10_RECORDING: {
        require(r.flags() <= 2, "H10 operation: 0 status, 1 start, 2 stop");
        if (r.flags() == 1) {
            require(!r.path().empty() && r.path().size() < 64 && r.value() <= 1 &&
                        r.values_size() == 1 && (r.values(0) == 1 || r.values(0) == 5),
                    "H10 identifier/type/interval");
            protocol::PbPFtpRequestStartRecordingParams p;
            p.set_sample_data_identifier(r.path());
            p.set_sample_type(static_cast<PbSampleType>(r.value() ? 16 : 1));
            p.mutable_recording_interval()->set_seconds(r.values(0));
            io.query(14, message_bytes(p));
        } else if (r.flags() == 2)
            io.query(15, {});
        else {
            auto b = io.query(16, {});
            message_parse("protocol.PbRequestRecordingStatusResult", b);
            out.set_data(b.data(), b.size());
            out.set_schema("protocol.PbRequestRecordingStatusResult");
        }
        break;
    }
    case POLAR_SERVICE_EXERCISE_CONTROL: {
        require(r.flags() >= 21 && r.flags() <= 26, "Exercise query ID 21..26 required");
        bytes input;
        std::string schema;
        if (r.flags() == 21 || r.flags() == 26) {
            require(r.value() != 0, "Sport identifier required");
            if (r.flags() == 21) {
                protocol::PbPFtpStartExerciseParams p;
                p.mutable_sport_identifier()->set_value(r.value());
                input = message_bytes(p);
            } else {
                protocol::PbPFtpStartDmExerciseParams p;
                p.mutable_sport_identifier()->set_value(r.value());
                input = message_bytes(p);
                schema = "protocol.PbPftpStartDmExerciseResult";
            }
        } else if (r.flags() == 22) {
            protocol::PbPFtpStopExerciseParams p;
            p.set_save(r.value() != 0);
            input = message_bytes(p);
        } else if (r.flags() == 24)
            schema = "protocol.PbPftpResumeExerciseResult";
        else if (r.flags() == 25)
            schema = "protocol.PbPftpGetExerciseStatusResult";
        auto b = io.query(uint16_t(r.flags()), input);
        if (!schema.empty() && !b.empty()) {
            auto m = message_parse(schema, b);
            if (r.flags() == 24) {
                auto f = m->GetDescriptor()->FindFieldByName("result");
                require(m->GetReflection()->GetEnumValue(*m, f) == 1, "Exercise resume rejected");
            }
            out.set_schema(schema);
        }
        out.set_data(b.data(), b.size());
        break;
    }
    case POLAR_SERVICE_OFFLINE_EXERCISE_SUPPORT: {
        auto m = parse<data::PbDeviceInfo>(io.read("/DEVICE.BPB"));
        out.set_boolean_value(std::find(m.capabilities().begin(), m.capabilities().end(),
                                        "dm_exercise") != m.capabilities().end());
        break;
    }
    case POLAR_SERVICE_EXERCISE_LIST: {
        std::vector<std::string> roots;
        if (r.has_from_day())
            for (auto &date : dates(r))
                roots.push_back("/U/0/" + date + "/E/");
        else
            roots.push_back(r.path().empty() ? "/" : r.path());
        for (auto &root : roots)
            try {
                for (auto &f : list(io, root, true))
                    if (f.path().ends_with("/TSESS.BPB") || f.path().ends_with("/SAMPLES.BPB"))
                        *out.add_files() = f;
            } catch (const failure &e) {
                if (!missing(e))
                    throw;
            }
        break;
    }
    case POLAR_SERVICE_EXERCISE_GET: {
        if (r.path().ends_with("/SAMPLES.BPB"))
            add_file(out, io, r.path(), "data.PbExerciseSamples");
        else {
            auto path = r.path().ends_with(".BPB") ? r.path().substr(0, r.path().rfind('/') + 1)
                                                   : directory(r.path());
            std::map<std::string, std::string> schemas{
                {"TSESS.BPB", "data.PbTrainingSession"},
                {"BASE.BPB", "data.PbExerciseBase"},
                {"SAMPLES.BPB", "data.PbExerciseSamples"},
                {"SAMPLES2.BPB", "data.PbExerciseSamples2"},
                {"ROUTE.BPB", "data.PbExerciseRouteSamples"},
                {"ROUTE2.BPB", "data.PbExerciseRouteSamples2"}};
            for (auto &f : list(io, path, true)) {
                auto name = f.path().substr(f.path().rfind('/') + 1);
                if (schemas.contains(name))
                    add_file(out, io, f.path(), schemas[name]);
                else
                    add_file(out, io, f.path());
            }
        }
        break;
    }
    case POLAR_SERVICE_EXERCISE_DELETE: {
        valid_path(r.path());
        if (r.path().ends_with("SAMPLES.BPB")) {
            io.remove(r.path());
            break;
        }
        auto path = directory(r.path());
        require(std::regex_match(path, std::regex("^/U/0/[0-9]{8}/E/[0-9]{6}/$")),
                "Invalid training session path");
        auto current = list(io, path, true);
        require(std::any_of(current.begin(), current.end(),
                            [](auto &f) { return f.path().ends_with("TSESS.BPB"); }),
                "Stale training reference");
        io.remove(path.substr(0, path.size() - 1));
        auto parent = path.substr(0, path.rfind('/', path.size() - 2) + 1);
        if (list(io, parent, false).empty())
            io.remove(parent.substr(0, parent.size() - 1));
        break;
    }
    case POLAR_SERVICE_LOG_GET:
        add_file(out, io, "/SDLOGS.BPB", "data.PbSensorDataLog");
        break;
    case POLAR_SERVICE_LOG_SET:
        message_parse("data.PbSensorDataLog", binary(r.payload()));
        io.write("/SDLOGS.BPB", binary(r.payload()));
        break;
    case POLAR_SERVICE_LOG_EXPORT: {
        for (auto path : {"/ERRORLOG.BPB", "/ERRORLO2.BPB", "/SYSLOG.TXT"})
            try {
                add_file(out, io, path);
            } catch (const failure &e) {
                if (!missing(e))
                    throw;
            }
        for (auto prefix : {"/TRC", "/DBGTRC"})
            for (int i = 1; i <= 10000; i++) {
                try {
                    add_file(out, io, std::string(prefix) + std::to_string(i) + ".BIN");
                } catch (const failure &e) {
                    if (!missing(e))
                        throw;
                    break;
                }
                require(i < 10000, "Log enumeration limit");
            }
        break;
    }
    case POLAR_SERVICE_TELEMETRY_DELETE:
        for (auto &f : list(io, "/", false))
            if (std::regex_match(f.path(), std::regex("^/TRC[0-9]{1,3}[.]BIN$")))
                io.remove(f.path());
        break;
    case POLAR_SERVICE_WATCHFACE_GET:
    case POLAR_SERVICE_WATCHFACE_SET:
    case POLAR_SERVICE_WATCHFACE_COMPLICATIONS: {
        bytes config;
        if (kind == POLAR_SERVICE_WATCHFACE_GET || kind == POLAR_SERVICE_WATCHFACE_COMPLICATIONS) {
            auto wire = io.read("/SYS/KVTX");
            polar_transform t{sizeof(t), POLAR_CODEC_WATCHFACE_GET, wire.data(),
                              uint32_t(wire.size()), 0};
            config = transform(t);
        } else
            config = copy(binary(r.payload()));
        if (kind == POLAR_SERVICE_WATCHFACE_COMPLICATIONS) {
            require(r.values_size() <= 1000, "Complication count");
            config.resize(12);
            le(config, r.values_size(), 4);
            for (auto v : r.values())
                le(config, v, 4);
        }
        if (kind == POLAR_SERVICE_WATCHFACE_GET)
            return_data(config);
        else {
            polar_transform t{sizeof(t), POLAR_CODEC_WATCHFACE_SET, config.data(),
                              uint32_t(config.size()), 0};
            io.write("/SYS/KVTX", transform(t));
        }
        break;
    }
    case POLAR_SERVICE_REST_GET: {
        auto b = io.read(r.path().empty() ? "/REST/SERVICE.API" : r.path());
        polar_transform t{sizeof(t), POLAR_CODEC_JSON_VALIDATE, b.data(), uint32_t(b.size()), 0};
        return_data(transform(t));
        break;
    }
    case POLAR_SERVICE_REST_PUT: {
        polar_transform t{sizeof(t), POLAR_CODEC_JSON_VALIDATE,
                          reinterpret_cast<const uint8_t *>(r.payload().data()),
                          uint32_t(r.payload().size()), 0};
        io.write(r.path(), transform(t));
        break;
    }
    case POLAR_SERVICE_SLEEP_SUBSCRIBE:
    case POLAR_SERVICE_SLEEP_STOP: {
        if (kind == POLAR_SERVICE_SLEEP_SUBSCRIBE && io.observe_notifications)
            io.observe_notifications();
        bytes b{'{', '}'};
        io.write(kind == POLAR_SERVICE_SLEEP_SUBSCRIBE
                     ? "/REST/SLEEP.API?cmd=subscribe&event=sleep_recording_state&details=[enabled]"
                     : "/REST/SLEEP.API?cmd=post&endpoint=stop_sleep_recording",
                 b);
        break;
    }
    case POLAR_SERVICE_SLEEP_STATUS: {
        require(bool(io.observe_notifications) && bool(io.wait_notification),
                "Notification wait transport missing");
        io.observe_notifications();
        const bytes empty{'{', '}'};
        io.write("/REST/SLEEP.API?cmd=subscribe&event=sleep_recording_state&details=[enabled]",
                 empty);
        for (;;) {
            auto data = io.wait_notification(18);
            polar_transform t{};
            t.struct_size = sizeof(t);
            t.operation = POLAR_CODEC_REST_EVENTS;
            t.data = data.data();
            t.data_length = uint32_t(data.size());
            auto events = parse<sdk::RestEvents>(transform(t));
            bool found = false;
            for (const auto &e : events.events())
                if (e.has_sleep_status()) {
                    out.set_unsigned_value(e.sleep_status());
                    out.set_boolean_value(e.sleep_status() == 2);
                    found = true;
                }
            if (found) {
                out.set_data(events.SerializePartialAsString());
                out.set_schema("polar.sdk.RestEvents");
                break;
            }
        }
        break;
    }
    case POLAR_SERVICE_FIRMWARE_CHECK: {
        require(!r.client_id().empty() &&
                    std::regex_match(r.device_id(), std::regex("^[0-9A-Fa-f]{8}$")),
                "Truthful client ID and Polar device ID required");
        auto device = parse<data::PbDeviceInfo>(io.read("/DEVICE.BPB"));
        require(device.has_device_version() && !device.hardware_code().empty(),
                "Firmware identity absent");
        auto &v = device.device_version();
        std::string version = std::to_string(v.major()) + "." + std::to_string(v.minor()) + "." +
                              std::to_string(v.patch());
        google::protobuf::Struct body;
        auto fields = body.mutable_fields();
        (*fields)["clientId"].set_string_value(r.client_id());
        (*fields)["uuid"].set_string_value("0e030000-0084-0000-0000-0000" + r.device_id());
        (*fields)["firmwareVersion"].set_string_value(version);
        (*fields)["hardwareCode"].set_string_value(device.hardware_code());
        std::string json;
        require(google::protobuf::util::MessageToJsonString(body, &json).ok(),
                "Firmware request encoding");
        auto response =
            io.http
                ? io.http("https://firmware-management.polar.com/api/v1/firmware-update/check",
                          binary(json), 1024 * 1024)
                : http_request("https://firmware-management.polar.com/api/v1/firmware-update/check",
                               binary(json), 1024 * 1024, io.check_cancel);
        sdk::FirmwareInfo info;
        info.set_update_available(false);
        if (!response.empty()) {
            google::protobuf::Struct value;
            require(google::protobuf::util::JsonStringToMessage(
                        std::string(response.begin(), response.end()), &value)
                        .ok(),
                    "Firmware catalog response");
            auto text = [&](const std::string &name) {
                auto i = value.fields().find(name);
                require(i != value.fields().end() &&
                            i->second.kind_case() == google::protobuf::Value::kStringValue &&
                            !i->second.string_value().empty(),
                        "Firmware catalog string missing");
                return i->second.string_value();
            };
            auto numbers = [](const std::string &s) {
                require(!s.empty() && s.size() <= 128, "Version size");
                std::vector<uint32_t> values;
                std::istringstream in(s);
                std::string part;
                while (std::getline(in, part, '.')) {
                    require(!part.empty() && part.size() <= 10 &&
                                std::all_of(part.begin(), part.end(),
                                            [](char c) { return c >= '0' && c <= '9'; }),
                            "Numeric firmware version required");
                    auto n = std::stoull(part);
                    require(n <= UINT32_MAX, "Version component overflow");
                    values.push_back(uint32_t(n));
                }
                require(s.back() != '.', "Empty version component");
                return values;
            };
            auto available = text("version");
            auto a = numbers(available), b = numbers(version);
            a.resize(std::max(a.size(), b.size()));
            b.resize(a.size());
            info.set_catalog_json(response.data(), response.size());
            if (a > b) {
                auto url = text("fileUrl");
                require(url.starts_with("https://"), "Firmware download must use HTTPS");
                info.set_update_available(true);
                info.set_version(available);
                info.set_file_url(url);
                auto hardware = value.fields().find("hardwareVersions");
                if (hardware != value.fields().end() &&
                    hardware->second.kind_case() == google::protobuf::Value::kListValue)
                    for (const auto &entry : hardware->second.list_value().values())
                        if (entry.kind_case() == google::protobuf::Value::kStructValue) {
                            const auto &fields = entry.struct_value().fields();
                            auto erp = fields.find("erpNumber"),
                                 mandatory = fields.find("mandatory");
                            if (erp != fields.end() &&
                                erp->second.string_value() == device.hardware_code() &&
                                mandatory != fields.end() &&
                                mandatory->second.kind_case() ==
                                    google::protobuf::Value::kBoolValue)
                                info.set_mandatory(mandatory->second.bool_value());
                        }
            }
        }
        out.set_boolean_value(info.update_available());
        out.set_data(info.SerializePartialAsString());
        out.set_schema("polar.sdk.FirmwareInfo");
        break;
    }
    case POLAR_SERVICE_FIRMWARE_DOWNLOAD: {
        auto b = r.package().empty()
                     ? (io.http ? io.http(r.path(), {}, 128 * 1024 * 1024)
                                : http_request(r.path(), {}, 128 * 1024 * 1024, io.check_cancel))
                     : copy(binary(r.package()));
        for (auto &f : parse_zip(b)) {
            auto dest = out.add_files();
            dest->set_path(f.path);
            dest->set_size(f.data.size());
            dest->set_data(f.data.data(), f.data.size());
        }
        break;
    }
    case POLAR_SERVICE_FIRMWARE_BACKUP: {
        std::set<std::string> paths{"/U/0/S/PHYSDATA.BPB", "/U/0/S/UDEVSET.BPB", "/U/0/S/PREFS.BPB",
                                    "/U/0/USERID.BPB"};
        try {
            auto b = io.read("/SYS/BACKUP.TXT");
            std::istringstream input(std::string(b.begin(), b.end()));
            std::string p;
            while (std::getline(input, p)) {
                if (!p.empty() && p.back() == '\r')
                    p.pop_back();
                auto at = p.find("/U/*/");
                if (at != std::string::npos)
                    p.replace(at, 5, "/U/0/");
                if (!p.empty())
                    paths.insert(p);
            }
        } catch (const failure &e) {
            if (!missing(e))
                throw;
        }
        for (auto &p : paths) {
            if (p.ends_with('/') && p.find('*') == std::string::npos) {
                try {
                    for (auto &f : list(io, p, true))
                        add_file(out, io, f.path());
                } catch (const failure &e) {
                    if (!missing(e))
                        throw;
                }
                continue;
            }
            if (p.find('*') != std::string::npos) {
                auto slash = p.rfind('/');
                auto parent = p.substr(0, slash + 1);
                auto pattern = p.substr(slash + 1);
                std::string regex;
                for (char c : pattern)
                    regex += c == '*' ? ".*" : c == '.' ? "[.]" : std::string(1, c);
                try {
                    for (auto &f : list(io, parent, false))
                        if (std::regex_match(f.path().substr(parent.size()), std::regex(regex)))
                            add_file(out, io, f.path());
                } catch (const failure &e) {
                    if (!missing(e))
                        throw;
                }
            } else
                try {
                    valid_path(p);
                    add_file(out, io, p);
                } catch (const failure &e) {
                    if (!missing(e))
                        throw;
                }
        }
        break;
    }
    case POLAR_SERVICE_FIRMWARE_RESTORE: {
        auto backup = parse<sdk::ServiceResult>(binary(r.payload()));
        require(backup.files_size() <= 10000, "Backup file limit");
        for (auto &f : backup.files()) {
            valid_path(f.path());
            require(f.has_data(), "Backup data absent");
        }
        std::set<std::string> directories;
        for (auto &f : backup.files()) {
            io.check_cancel();
            if (f.path().starts_with("/U/0/"))
                for (size_t at = 3; (at = f.path().find('/', at + 1)) != std::string::npos;) {
                    auto directory = f.path().substr(0, at + 1);
                    if (directories.insert(directory).second)
                        try {
                            io.write(directory, {});
                        } catch (const failure &e) {
                            if (e.code != POLAR_DEVICE_ERROR || e.att != 104)
                                throw;
                        }
                }
            io.write(f.path(), binary(f.data()));
        }
        break;
    }
    case POLAR_SERVICE_FIRMWARE_UPDATE: {
        // Validate the entire archive before changing any device state. r.path is the
        // expected numeric version, backup_path must be persisted before reset.
        require(!r.package().empty() && !r.backup_path().empty() &&
                    std::regex_match(r.path(), std::regex("^[0-9]+[.][0-9]+[.][0-9]+$")),
                "Firmware package, expected version and backup path required");
        require(bool(io.persist) && bool(io.reconnect) && bool(io.feature),
                "Firmware lifecycle transport missing");
        auto files = parse_zip(binary(r.package()));
        for (auto &f : files)
            require(!f.data.empty(), "Empty firmware image");
        bytes backup;
        bool restore_multi = false, sync = false, changed = false;
        std::exception_ptr primary;
        try {
            try {
                auto features = io.feature({});
                if (features.size() >= 2 && (u(features, 0, 2) & 128)) {
                    bytes query{9};
                    auto state = io.feature(query);
                    require(state.size() == 1 && state[0] <= 1, "Multi-BLE state");
                    if (state[0]) {
                        restore_multi = true;
                        bytes disable{8, 0};
                        io.feature(disable);
                    }
                }
            } catch (const failure &e) {
                if (e.code != POLAR_NOT_SUPPORTED)
                    throw;
            }
            sync_start(io);
            sync = true;
            backup = run_service(POLAR_SERVICE_FIRMWARE_BACKUP, {}, io);
            io.persist(r.backup_path(), backup);
            protocol::PbPFtpFactoryResetParams reset;
            reset.set_sleep(false);
            reset.set_do_factory_defaults(true);
            reset.set_hibernate(false);
            reset.set_ota_fwupdate(true);
            changed = true;
            io.notify(2, message_bytes(reset));
            sync = false;
            io.reconnect();
            sync_start(io);
            sync = true;
            for (size_t i = 0; i < files.size(); i++) {
                io.check_cancel();
                io.query(12, {});
                try {
                    io.write(files[i].path, files[i].data);
                } catch (const failure &e) {
                    if (e.code != POLAR_DEVICE_ERROR || e.att != 1)
                        throw;
                    sync = false;
                    if (i + 1 < files.size()) {
                        io.reconnect();
                        sync_start(io);
                        sync = true;
                    }
                }
            }
            sync = false;
            io.reconnect();
            sync_start(io);
            sync = true;
            sdk::ServiceRequest restore;
            restore.set_payload(backup.data(), backup.size());
            run_service(POLAR_SERVICE_FIRMWARE_RESTORE, message_bytes(restore), io);
            if (r.has_unix_time_ms())
                set_time(io, r, false);
            auto device = parse<data::PbDeviceInfo>(io.read("/DEVICE.BPB"));
            auto &v = device.device_version();
            std::string actual = std::to_string(v.major()) + "." + std::to_string(v.minor()) + "." +
                                 std::to_string(v.patch());
            require(actual == r.path(),
                    "Firmware version verification failed; recovery backup retained");
            sync_stop(io, true);
            sync = false;
            out.set_text(actual);
        } catch (...) {
            primary = std::current_exception();
        }
        bool cleanup_failed = false;
        if (sync)
            try {
                sync_stop(io, false);
            } catch (...) {
                cleanup_failed = true;
            }
        if (restore_multi)
            try {
                io.reconnect();
                bytes enable{8, 1};
                io.feature(enable);
            } catch (...) {
                cleanup_failed = true;
            }
        if (primary) {
            try {
                std::rethrow_exception(primary);
            } catch (const std::exception &e) {
                throw failure(
                    POLAR_DEVICE_ERROR,
                    std::string("Firmware update failed: ") + e.what() +
                        (changed ? "; persisted recovery backup retained" : "; reset not started") +
                        (cleanup_failed ? "; cleanup failed" : ""));
            }
        }
        if (cleanup_failed)
            throw failure(POLAR_DEVICE_ERROR, "Firmware cleanup failed; recovery backup retained");
        break;
    }
    default:
        throw failure(POLAR_NOT_SUPPORTED, "Unknown high-level service");
    }
    return message_bytes(out);
}
} // namespace polar
