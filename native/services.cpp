#include "services.hpp"
#include "native_sdk.pb.h"
#include "pftp_notification.pb.h"
#include <cctype>
#include <google/protobuf/dynamic_message.h>
#include <google/protobuf/struct.pb.h>
#include <google/protobuf/util/json_util.h>
#include <mz.h>
#include <mz_strm.h>
#include <mz_zip.h>
#include <mz_zip_rw.h>
#include <set>
#include <windows.h>
#include <winhttp.h>
namespace polar {
std::unique_ptr<google::protobuf::Message> message_parse(const std::string &schema, view b) {
    auto d = google::protobuf::DescriptorPool::generated_pool()->FindMessageTypeByName(schema);
    require(d != nullptr, "Unknown protobuf schema");
    auto proto = google::protobuf::MessageFactory::generated_factory()->GetPrototype(d);
    require(proto != nullptr, "Schema prototype absent");
    std::unique_ptr<google::protobuf::Message> m(proto->New());
    require(m->ParsePartialFromArray(b.data(), int(b.size())), "Malformed protobuf");
    return m;
}
bytes message_bytes(const google::protobuf::Message &m) {
    auto s = m.SerializePartialAsString();
    return bytes(s.begin(), s.end());
}
bytes kvtx_get(view b, uint32_t key) {
    std::map<uint32_t, bytes> values;
    size_t at = 0;
    while (at < b.size()) {
        auto cmd = b[at++];
        if (cmd == 5)
            continue;
        uint32_t target = uint32_t(u(b, at, 4));
        at += 4;
        bool applicable = true;
        if (cmd >= 6 && cmd <= 8) {
            bounds(b, at, 1);
            size_t n = b[at++];
            bounds(b, at, n);
            at += n;
            applicable = n == 0;
        }
        if (cmd == 0 || cmd == 1 || cmd == 6 || cmd == 7) {
            size_t n = size_t(u(b, at, 4));
            at += 4;
            bounds(b, at, n);
            if (applicable) {
                auto &v = values[target];
                if (cmd == 0 || cmd == 6)
                    v.clear();
                require(v.size() + n <= 64 * 1024 * 1024, "KVTX value limit");
                v.insert(v.end(), b.begin() + at, b.begin() + at + n);
            }
            at += n;
        } else if (cmd == 2 || cmd == 8) {
            if (applicable)
                values.erase(target);
        } else if (cmd == 3 || cmd == 4) {
            uint32_t other = uint32_t(u(b, at, 4));
            at += 4;
            require(target != key && other != key, "COPY/MOVE target unsupported");
        } else
            require(false, "Unknown KVTX instruction");
    }
    auto it = values.find(key);
    return it == values.end() ? bytes{} : it->second;
}
bytes kvtx_set(view b, uint32_t key) {
    bytes out{0};
    le(out, key, 4);
    le(out, b.size(), 4);
    out.insert(out.end(), b.begin(), b.end());
    out.push_back(5);
    return out;
}
// Canonical six-field watchface representation: u16 time/layout/background,
// u8 font, u8 reserved, u32 accent, u32 count, then count int32 complication IDs.
static bytes watch_decode(view b) {
    size_t table = size_t(u(b, 0, 4));
    int64_t vt = int64_t(table) - int32_t(u(b, table, 4));
    require(vt >= 0, "Invalid watchface vtable");
    size_t vtable = size_t(vt), vs = size_t(u(b, vtable, 2)), os = size_t(u(b, vtable + 2, 2));
    require(vs >= 4 && vs % 2 == 0 && os >= 4, "Watchface sizes");
    bounds(b, vtable, vs);
    bounds(b, table, os);
    auto field = [&](size_t i, size_t n) -> size_t {
        if (4 + i * 2 >= vs)
            return 0;
        size_t off = size_t(u(b, vtable + 4 + i * 2, 2));
        if (!off)
            return 0;
        require(off >= 4 && off + n <= os, "Watchface field range");
        return table + off;
    };
    bytes result;
    for (size_t i = 0; i < 3; i++) {
        auto at = field(i, 2);
        le(result, at ? u(b, at, 2) : 0, 2);
    }
    auto font = field(5, 1);
    le(result, font ? u(b, font, 1) : 0, 1);
    le(result, 0, 1);
    auto color = field(3, 4);
    le(result, color ? u(b, color, 4) : 0, 4);
    auto v = field(4, 4);
    size_t pos = v ? v + size_t(u(b, v, 4)) : 0, n = pos ? size_t(u(b, pos, 4)) : 0;
    require(n <= 1000, "Too many watchface complications");
    le(result, n, 4);
    if (n) {
        bounds(b, pos + 4, n * 4);
        result.insert(result.end(), b.begin() + pos + 4, b.begin() + pos + 4 + n * 4);
    }
    return result;
}
static bytes watch_encode(view b) {
    bounds(b, 0, 16);
    size_t n = size_t(u(b, 12, 4));
    require(n <= 1000 && b.size() == 16 + n * 4, "Watchface input length");
    bytes out(44 + n * 4);
    auto put = [&](size_t o, uint64_t v, size_t count) {
        for (size_t i = 0; i < count; i++)
            out[o + i] = uint8_t(v >> (i * 8));
    };
    put(0, 20, 4);
    put(4, 16, 2);
    put(6, 20, 2);
    uint16_t offsets[] = {4, 6, 8, 12, 16, 10};
    for (size_t i = 0; i < 6; i++)
        put(8 + i * 2, offsets[i], 2);
    put(20, 16, 4);
    for (size_t i = 0; i < 3; i++)
        put(24 + 2 * i, u(b, 2 * i, 2), 2);
    out[30] = b[6];
    put(32, u(b, 8, 4), 4);
    put(36, 4, 4);
    put(40, n, 4);
    std::copy(b.begin() + 16, b.end(), out.begin() + 44);
    return out;
}
recording decode_recording(view file, uint32_t strategy, view key, uint64_t previous,
                           uint32_t derived_methods) {
    bounds(file, 0, 1);
    uint32_t header = file[0];
    require(header <= 3, "Offline security strategy");
    if (header >= 2)
        require(strategy == header, "Offline AES strategy mismatch");
    if (header)
        require(strategy != 0, "Offline key required");
    size_t metadata_len = header >= 2 ? (file.size() - 1) / 16 * 16 : file.size() - 1;
    auto meta =
        header ? decrypt(strategy, key, file.subspan(1, metadata_len)) : copy(file.subspan(1));
    view b(meta);
    bounds(b, 0, 37);
    require(u(b, 0, 4) == 0x3d7c4c2b, "Offline signature/key mismatch");
    recording result;
    result.info.struct_size = sizeof(result.info);
    result.info.version = uint32_t(u(b, 4, 4));
    result.info.header_free = uint32_t(u(b, 8, 4));
    result.info.firmware_hash = uint32_t(u(b, 12, 4));
    std::memcpy(result.info.start_time_utc, b.data() + 16, 19);
    auto date = std::string(result.info.start_time_utc, 19);
    require(date[4] == '-' && date[7] == '-' && (date[10] == ' ' || date[10] == 'T') &&
                date[13] == ':' && date[16] == ':',
            "Offline date");
    auto number = [&](size_t offset, size_t n) {
        int value = 0;
        for (size_t i = 0; i < n; i++) {
            char c = date[offset + i];
            require(c >= '0' && c <= '9', "Invalid offline date digit");
            value = value * 10 + c - '0';
        }
        return value;
    };
    require(
        (std::chrono::year{number(0, 4)} / unsigned(number(5, 2)) / unsigned(number(8, 2))).ok() &&
            number(11, 2) < 24 && number(14, 2) < 60 && number(17, 2) < 60,
        "Invalid offline calendar date");
    size_t at = 36, len = b[at++];
    bounds(b, at, len);
    auto settings = b.subspan(at, len);
    at += len;
    result.info.sample_rate = get_setting(settings, 0, 0);
    polar_decode_options options{
        sizeof(options),
        result.info.sample_rate,
        previous,
        std::bit_cast<float>(get_setting(settings, 5, std::bit_cast<uint32_t>(1.f))),
        derived_methods,
        0};
    for (auto &s : parse_settings(settings))
        if (s.kind == 7)
            for (auto id : s.values) {
                require(id < 16, "Derived method");
                options.derived_methods |= 1u << id;
            }
    bounds(b, at, 1);
    len = b[at++];
    bytes payload_key = copy(key);
    uint32_t payload_strategy = strategy;
    if (len) {
        bounds(b, at, len);
        payload_strategy = b[at];
        if (!payload_strategy)
            payload_key.clear();
        else if (payload_strategy == 1) {
            require(len >= 2 && !key.empty() && b[at + 1] < key.size(), "Offline XOR index");
            payload_key = {key[b[at + 1]]};
        }
        at += len;
    }
    if (header >= 2)
        at += 16 - at % 16;
    if (at == b.size() && file.size() == at + 1)
        return result;
    size_t packet = size_t(u(b, at, 2));
    at += 2;
    if (header >= 2)
        at += 16 - at % 16;
    bounds(file, 1 + at, 0);
    auto payload = decrypt(payload_strategy, payload_key, file.subspan(1 + at));
    view data(payload);
    at = 0;
    auto scratch = std::make_unique<polar_sample[]>(max_samples);
    while (at < data.size()) {
        if (!packet) {
            require(std::all_of(data.begin() + at, data.end(), [](auto x) { return x == 0; }),
                    "Data after offline terminator");
            break;
        }
        require(packet >= 10, "Offline packet length");
        bounds(data, at, packet);
        auto frame = data.subspan(at, packet);
        size_t count = decode(frame, options, {scratch.get(), max_samples});
        require(result.samples.size() + count <= 10000000, "Offline sample limit");
        result.samples.insert(result.samples.end(), scratch.get(), scratch.get() + count);
        options.previous_timestamp = u(frame, 1, 8);
        at += packet;
        if (at == data.size())
            break;
        packet = size_t(u(data, at, 2));
        at += 2;
    }
    result.info.sample_count = result.samples.size();
    result.info.last_timestamp_ns = options.previous_timestamp;
    return result;
}
std::vector<archive_file> parse_zip(view input) {
    require(input.size() <= 128 * 1024 * 1024, "Firmware ZIP too large");
    void *reader = mz_zip_reader_create();
    require(reader != nullptr, "ZIP allocation");
    struct close {
        void *&p;
        ~close() {
            mz_zip_reader_delete(&p);
        }
    } cleanup{reader};
    require(mz_zip_reader_open_buffer(reader, const_cast<uint8_t *>(input.data()),
                                      int32_t(input.size()), 0) == MZ_OK,
            "Invalid ZIP");
    std::set<std::string> seen;
    std::vector<archive_file> out;
    uint64_t total = 0;
    int entries = 0;
    auto status = mz_zip_reader_goto_first_entry(reader);
    while (status == MZ_OK) {
        require(++entries <= 128, "ZIP entry limit");
        mz_zip_file *info = nullptr;
        require(mz_zip_reader_entry_get_info(reader, &info) == MZ_OK && info, "ZIP entry info");
        std::string name = info->filename;
        std::string lower = name;
        std::transform(lower.begin(), lower.end(), lower.begin(),
                       [](unsigned char c) { return char(std::tolower(c)); });
        if (!name.ends_with('/') && lower != "readme.txt") {
            require(!name.empty() && name[0] != '/' && name.find('\\') == std::string::npos &&
                        name.find(':') == std::string::npos && seen.insert(lower).second,
                    "Unsafe/duplicate ZIP path");
            size_t pos = 0;
            while (pos <= name.size()) {
                size_t end = name.find('/', pos);
                if (end == std::string::npos)
                    end = name.size();
                auto part = name.substr(pos, end - pos);
                require(!part.empty() && part != "." && part != "..", "Unsafe ZIP segment");
                pos = end + 1;
            }
            require(info->uncompressed_size >= 0 &&
                        uint64_t(info->uncompressed_size) <= 256 * 1024 * 1024 - total,
                    "ZIP expansion limit");
            total += info->uncompressed_size;
            archive_file f{"/" + name, bytes(size_t(info->uncompressed_size))};
            require(mz_zip_reader_entry_save_buffer(reader, f.data.data(),
                                                    int32_t(f.data.size())) == MZ_OK,
                    "ZIP entry read/CRC failure");
            out.push_back(std::move(f));
        }
        status = mz_zip_reader_goto_next_entry(reader);
    }
    require(status == MZ_END_OF_LIST && !out.empty(), "Empty/corrupt ZIP");
    std::stable_sort(out.begin(), out.end(), [](auto &a, auto &b) {
        auto is = [](std::string s) {
            std::transform(s.begin(), s.end(), s.begin(),
                           [](unsigned char c) { return char(std::toupper(c)); });
            return s.ends_with("SYSUPDAT.IMG");
        };
        return is(a.path) < is(b.path);
    });
    return out;
}
static std::wstring wide(const std::string &s) {
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), int(s.size()), nullptr, 0);
    require(n > 0, "Invalid UTF-8 URL");
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), int(s.size()), w.data(), n);
    return w;
}
bytes http_request(const std::string &url, view body, size_t limit,
                   const std::function<void()> &check_cancel) {
    if (check_cancel)
        check_cancel();
    auto address = wide(url);
    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    parts.dwHostNameLength = DWORD(-1);
    parts.dwUrlPathLength = DWORD(-1);
    parts.dwExtraInfoLength = DWORD(-1);
    require(WinHttpCrackUrl(address.c_str(), DWORD(address.size()), 0, &parts) != FALSE &&
                parts.nScheme == INTERNET_SCHEME_HTTPS,
            "HTTPS URL required");
    struct handle {
        HINTERNET h = nullptr;
        ~handle() {
            if (h)
                WinHttpCloseHandle(h);
        }
    } session{WinHttpOpen(L"PolarNative/0.2.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                          WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0)};
    auto checked = [](bool ok) {
        if (!ok) {
            failure f(POLAR_OS_ERROR, "WinHTTP request failed");
            f.hresult = int32_t(HRESULT_FROM_WIN32(GetLastError()));
            throw f;
        }
    };
    checked(session.h != nullptr);
    WinHttpSetTimeouts(session.h, 5000, 5000, 5000, 5000);
    std::wstring host(parts.lpszHostName, parts.dwHostNameLength),
        path(parts.lpszUrlPath, parts.dwUrlPathLength);
    if (parts.dwExtraInfoLength)
        path.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);
    handle connect{WinHttpConnect(session.h, host.c_str(), parts.nPort, 0)};
    checked(connect.h != nullptr);
    handle request{WinHttpOpenRequest(connect.h, body.empty() ? L"GET" : L"POST", path.c_str(),
                                      nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
                                      WINHTTP_FLAG_SECURE)};
    checked(request.h != nullptr);
    DWORD redirect = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
    checked(WinHttpSetOption(request.h, WINHTTP_OPTION_REDIRECT_POLICY, &redirect,
                             sizeof(redirect)) != FALSE);
    checked(WinHttpSendRequest(
                request.h, L"Accept: application/json\r\nContent-Type: application/json\r\n",
                DWORD(-1),
                body.empty() ? WINHTTP_NO_REQUEST_DATA : const_cast<uint8_t *>(body.data()),
                DWORD(body.size()), DWORD(body.size()), 0) != FALSE);
    checked(WinHttpReceiveResponse(request.h, nullptr) != FALSE);
    if (check_cancel)
        check_cancel();
    DWORD status = 0, size = sizeof(status);
    checked(WinHttpQueryHeaders(request.h, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                                WINHTTP_HEADER_NAME_BY_INDEX, &status, &size,
                                WINHTTP_NO_HEADER_INDEX) != FALSE);
    require(status >= 200 && status < 300, "HTTP error status");
    bytes out;
    std::array<uint8_t, 16384> scratch{};
    DWORD got;
    do {
        if (check_cancel)
            check_cancel();
        checked(WinHttpReadData(request.h, scratch.data(), DWORD(scratch.size()), &got) != FALSE);
        require(out.size() + got <= limit, "HTTP response limit");
        out.insert(out.end(), scratch.begin(), scratch.begin() + got);
    } while (got);
    return out;
}
bytes transform(const polar_transform &t) {
    view b(t.data, t.data_length), p(t.parameters, t.parameters_length);
    switch (t.operation) {
    case POLAR_CODEC_GZIP_DECODE:
        return gzip_decode(b);
    case POLAR_CODEC_GZIP_ENCODE:
        return gzip_encode(b);
    case POLAR_CODEC_AES128:
        return decrypt(2, p, b);
    case POLAR_CODEC_AES256:
        return decrypt(3, p, b);
    case POLAR_CODEC_XOR:
        return decrypt(1, p, b);
    case POLAR_CODEC_PROTO_ROUNDTRIP:
        return message_bytes(*message_parse(std::string(t.schema, t.schema_length), b));
    case POLAR_CODEC_PROTO_MERGE: {
        auto base = message_parse(std::string(t.schema, t.schema_length), b),
             patch = message_parse(std::string(t.schema, t.schema_length), p);
        base->MergeFrom(*patch);
        return message_bytes(*base);
    }
    case POLAR_CODEC_WATCHFACE_GET: {
        auto v = kvtx_get(b);
        return v.empty() ? bytes(16) : watch_decode(v);
    }
    case POLAR_CODEC_WATCHFACE_SET:
        return kvtx_set(watch_encode(b));
    case POLAR_CODEC_ZIP_LIST: {
        bytes out;
        auto files = parse_zip(b);
        le(out, files.size(), 4);
        for (auto &f : files) {
            le(out, f.path.size(), 4);
            out.insert(out.end(), f.path.begin(), f.path.end());
            le(out, f.data.size(), 8);
            out.insert(out.end(), f.data.begin(), f.data.end());
        }
        return out;
    }
    case POLAR_CODEC_REST_EVENTS: {
        protocol::PbPftpDHRestApiEvent notification;
        require(notification.ParsePartialFromArray(b.data(), int(b.size())),
                "Invalid REST notification");
        sdk::RestEvents result;
        for (const auto &item : notification.event()) {
            bytes json(item.begin(), item.end());
            if (!notification.uncompressed())
                json = gzip_decode(json, 1024 * 1024);
            google::protobuf::Struct object;
            require(google::protobuf::util::JsonStringToMessage(
                        std::string(json.begin(), json.end()), &object)
                        .ok(),
                    "Invalid REST JSON object");
            auto out = result.add_events();
            out->set_json(json.data(), json.size());
            auto state = object.fields().find("sleep_recording_state");
            if (state != object.fields().end()) {
                uint32_t status = 0;
                if (state->second.kind_case() == google::protobuf::Value::kStructValue) {
                    auto &fields = state->second.struct_value().fields();
                    auto enabled = fields.find("enabled");
                    if (enabled != fields.end() &&
                        enabled->second.kind_case() == google::protobuf::Value::kBoolValue)
                        status = enabled->second.bool_value() ? 2 : 1;
                }
                out->set_sleep_status(status);
            }
        }
        return message_bytes(result);
    }
    case POLAR_CODEC_JSON_VALIDATE: {
        google::protobuf::Struct value;
        auto s =
            google::protobuf::util::JsonStringToMessage(std::string(b.begin(), b.end()), &value);
        require(s.ok(), "Invalid JSON object");
        return copy(b);
    }
    default:
        throw failure(POLAR_NOT_SUPPORTED, "Unknown codec");
    }
}
} // namespace polar
