#include "services.hpp"
#include "native_sdk.pb.h"
namespace polar {
static view binary(const std::string &s) {
    return {reinterpret_cast<const uint8_t *>(s.data()), s.size()};
}
static int setting_width(uint32_t kind) {
    switch (kind) {
    case 0:
    case 1:
    case 2:
    case 9:
        return 2;
    case 3:
    case 5:
    case 10:
    case 11:
        return 4;
    case 4:
    case 7:
    case 8:
    case 12:
        return 1;
    default:
        throw failure(POLAR_INVALID_ARGUMENT, "Unknown PMD setting type");
    }
}
template <class Settings>
static bytes encode(const Settings &settings, const sdk::ServiceRequest &r, bool secret) {
    bytes out;
    for (const auto &s : settings) {
        int w = setting_width(s.type());
        require(s.values_size() > 0 && s.values_size() <= 255, "PMD setting count");
        out.push_back(uint8_t(s.type()));
        out.push_back(uint8_t(s.values_size()));
        for (auto value : s.values()) {
            require(w == 4 || value < (1u << (w * 8)), "PMD setting width overflow");
            le(out, value, w);
        }
    }
    if (secret && r.has_key_strategy()) {
        auto strategy = r.key_strategy();
        auto size = r.key().size();
        require(strategy <= 3 && (strategy != 0 || size == 0) && (strategy != 1 || size > 0) &&
                    (strategy != 2 || size == 16) && (strategy != 3 || size == 32),
                "PMD key length/strategy");
        out.insert(out.end(), {6, 1, uint8_t(strategy)});
        out.insert(out.end(), r.key().begin(), r.key().end());
    }
    return out;
}
template <class Destination> static void settings_to(view b, Destination &dest) {
    for (auto &source : parse_settings(b)) {
        auto d = dest.Add();
        d->set_type(source.kind);
        for (auto v : source.values)
            d->add_values(v);
    }
}
static sdk::PmdResponse trigger_response(view data) {
    require(!data.empty() && data[0] <= 2, "Invalid PMD trigger mode");
    sdk::PmdResponse result;
    result.set_wire(data.data(), data.size());
    result.set_trigger_mode(data[0]);
    size_t at = 1;
    while (at < data.size()) {
        bounds(data, at, 2);
        auto enabled = data[at++];
        auto type = data[at++] & 63;
        require(enabled <= 1, "Invalid trigger enabled flag");
        auto d = result.add_triggers();
        d->set_measurement(type);
        d->set_enabled(enabled != 0);
        if (enabled) {
            bounds(data, at, 1);
            size_t n = data[at++];
            bounds(data, at, n);
            settings_to(data.subspan(at, n), *d->mutable_settings());
            at += n;
        }
    }
    return result;
}
bytes run_control_service(uint32_t kind, view input, service_transport &io) {
    sdk::ServiceRequest r;
    require(r.ParsePartialFromArray(input.data(), int(input.size())), "Invalid control request");
    sdk::ServiceResult result;
    if (kind == POLAR_SERVICE_FEATURE) {
        require(bool(io.feature), "Feature transport missing");
        auto opcode = r.flags();
        if (opcode == 0) {
            auto b = io.feature({});
            require(b.size() >= 2, "Feature bitmask");
            result.set_unsigned_value(u(b, 0, 2));
            return message_bytes(result);
        }
        require(opcode >= 1 && opcode <= 15 && opcode != 13, "Unknown feature command");
        bool configure = opcode == 1 || opcode == 3 || opcode == 5 || opcode == 7 || opcode == 8 ||
                         opcode == 10 || opcode == 14;
        if (opcode == 8 || opcode == 9 || opcode == 14 || opcode == 15) {
            auto b = io.feature({});
            require(b.size() >= 2, "Feature mask");
            uint32_t mask = opcode <= 9 ? 128 : 2048;
            if (!(u(b, 0, 2) & mask))
                throw failure(POLAR_NOT_SUPPORTED, "PFCS feature absent");
        }
        bytes command{uint8_t(opcode)};
        if (configure) {
            require(r.has_enabled(), "Feature enabled value required");
            command.push_back(r.enabled() ? 1 : 0);
        }
        auto b = io.feature(command);
        result.set_data(b.data(), b.size());
        if (!configure) {
            require(b.size() == 1, "Invalid feature query response");
            if (opcode == 12)
                result.set_unsigned_value(b[0]);
            else {
                require(b[0] <= 1, "Invalid feature Boolean response");
                result.set_boolean_value(b[0] != 0);
            }
        }
        return message_bytes(result);
    }
    require(bool(io.pmd), "PMD transport missing");
    auto opcode = r.flags();
    require(opcode >= 1 && opcode <= 12, "PMD opcode 1..12 required");
    require(r.value() <= 63, "PMD measurement/group ID range");
    auto send_trigger = [&](uint32_t type, bool enabled, const auto &settings) {
        require(type <= 63, "Trigger measurement range");
        bytes command{9, uint8_t(enabled), uint8_t(type)};
        if (enabled) {
            auto b = encode(settings, r, true);
            require(b.size() <= 255, "Trigger settings too long");
            command.push_back(uint8_t(b.size()));
            command.insert(command.end(), b.begin(), b.end());
        }
        io.pmd(command);
    };
    if (opcode == 12) {
        require(r.value() <= 2, "Trigger mode range");
        bytes command{8, uint8_t(r.value())};
        io.pmd(command);
        if (r.value() != 0) {
            auto current = trigger_response(io.pmd(bytes{7}));
            for (const auto &item : current.triggers()) {
                const sdk::PmdTrigger *chosen = nullptr;
                for (const auto &t : r.triggers())
                    if (t.measurement() == item.measurement())
                        chosen = &t;
                if (chosen)
                    send_trigger(item.measurement(), chosen->enabled(), chosen->settings());
                else
                    send_trigger(item.measurement(), false, r.pmd_settings());
            }
        }
        return message_bytes(result);
    }
    if (opcode == 9) {
        send_trigger(uint32_t(r.value()), r.enabled(), r.pmd_settings());
        return message_bytes(result);
    }
    bytes command{uint8_t(opcode)};
    if (opcode == 11)
        command = {4, uint8_t(POLAR_DERIVED | 128)};
    else if (opcode == 1 || opcode == 2 || opcode == 3 || opcode == 4) {
        command.push_back(uint8_t(r.value() | ((opcode != 3 && r.offline()) ? 128 : 0)));
        if (opcode == 2) {
            auto settings = encode(r.pmd_settings(), r, true);
            command.insert(command.end(), settings.begin(), settings.end());
        }
    } else if (opcode == 8) {
        require(r.value() <= 2, "Trigger mode range");
        command.push_back(uint8_t(r.value()));
    } else if (opcode == 10)
        command.push_back(uint8_t(r.value()));
    auto wire = io.pmd(command);
    sdk::PmdResponse response;
    response.set_wire(wire.data(), wire.size());
    if (opcode == 1 || opcode == 2 || opcode == 4 || opcode == 10 || opcode == 11)
        settings_to(wire, *response.mutable_settings());
    else if (opcode == 5) {
        for (auto v : wire) {
            auto s = response.add_statuses();
            s->set_measurement(v & 63);
            s->set_state(v >> 6);
        }
    } else if (opcode == 6) {
        require(wire.size() == 1 && wire[0] <= 1, "Invalid SDK mode");
        response.set_sdk_mode(wire[0] != 0);
        result.set_boolean_value(wire[0] != 0);
    } else if (opcode == 7)
        response = trigger_response(wire);
    result.set_data(response.SerializePartialAsString());
    result.set_schema("polar.sdk.PmdResponse");
    return message_bytes(result);
}
} // namespace polar
