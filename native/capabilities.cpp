#include "services.hpp"
#include "capability_config.hpp"
#include <google/protobuf/struct.pb.h>
#include <google/protobuf/util/json_util.h>
#include <cctype>
namespace polar {
device_profile profile_for_device(std::string name) {
    static const auto config = [] {
        google::protobuf::Struct value;
        require(google::protobuf::util::JsonStringToMessage(polar_capability_config, &value).ok(),
                "Device capability config");
        return value;
    }();
    for (auto &c : name)
        c = char(std::tolower(static_cast<unsigned char>(c)));
    if (name.starts_with("polar "))
        name.erase(0, 6);
    auto suffix = name.rfind(' ');
    if (suffix != std::string::npos && name.size() - suffix == 9 &&
        std::all_of(name.begin() + suffix + 1, name.end(),
                    [](unsigned char c) { return std::isxdigit(c) != 0; }))
        name.resize(suffix);
    const auto &defaults = config.fields().at("defaults").struct_value().fields();
    const auto &devices = config.fields().at("devices").struct_value().fields();
    const auto found = devices.find(name);
    auto field = [&](const char *key) -> const google::protobuf::Value & {
        if (found != devices.end()) {
            const auto &fields = found->second.struct_value().fields();
            auto at = fields.find(key);
            if (at != fields.end())
                return at->second;
        }
        return defaults.at(key);
    };
    return {field("fileSystemType").string_value() == "H10_FILE_SYSTEM",
            field("recordingSupported").bool_value(), field("firmwareUpdateSupported").bool_value(),
            field("activityDataSupported").bool_value(), field("isDeviceSensor").bool_value()};
}
} // namespace polar
