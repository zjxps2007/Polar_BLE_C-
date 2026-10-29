// Polar protocol framing and native codecs. See LICENSES for upstream notices.
#include "common.hpp"
#include <windows.h>
#include <bcrypt.h>
#include <zlib.h>
namespace polar {
bool pftp_assembler::add(view p) {
    require(!complete && !p.empty(), "Invalid RFC76 packet/state");
    uint8_t status = (p[0] >> 1) & 3;
    require((p[0] >> 4) == sequence && (p[0] & 1) == uint8_t(started),
            "RFC76 lost/out-of-sequence packet");
    require(status != 2, "Reserved RFC76 status");
    started = true;
    sequence = (sequence + 1) & 15;
    if (status == 0) {
        require(p.size() == 3, "RFC76 error length");
        auto error = u(p, 1, 2);
        if (error) {
            failure f(POLAR_DEVICE_ERROR, "Polar PFTP error " + std::to_string(error));
            f.att = uint32_t(error);
            throw f;
        }
        complete = true;
        return true;
    }
    require(data.size() + p.size() - 1 <= limit, "RFC76 response exceeds limit");
    data.insert(data.end(), p.begin() + 1, p.end());
    complete = status == 1;
    return complete;
}
std::vector<bytes> pftp_fragment(view b, size_t mtu) {
    require(mtu >= 2 && mtu <= 512, "Invalid ATT payload");
    std::vector<bytes> out;
    size_t o = 0;
    uint8_t seq = 0;
    do {
        size_t len = std::min(mtu - 1, b.size() - o);
        bool last = o + len == b.size();
        bytes p{uint8_t((seq << 4) | (o ? 1 : 0) | (last ? 2 : 6))};
        p.insert(p.end(), b.begin() + o, b.begin() + o + len);
        out.push_back(std::move(p));
        o += len;
        seq = (seq + 1) & 15;
    } while (o < b.size());
    return out;
}
bytes pftp_request(view op, view data) {
    require(op.size() <= 32767, "PFTP operation too large");
    bytes b;
    le(b, op.size(), 2);
    b.insert(b.end(), op.begin(), op.end());
    b.insert(b.end(), data.begin(), data.end());
    return b;
}
bytes pftp_query(uint16_t id, view p) {
    require(id <= 32767, "PFTP query id");
    bytes b;
    le(b, id | 0x8000, 2);
    b.insert(b.end(), p.begin(), p.end());
    return b;
}
bytes decrypt(uint32_t strategy, view key, view ciphertext) {
    require(strategy <= 3, "Unknown security strategy");
    if (!strategy) {
        require(key.empty(), "Unexpected plaintext key");
        return copy(ciphertext);
    }
    if (strategy == 1) {
        require(!key.empty(), "Missing XOR key");
        bytes b = copy(ciphertext);
        for (auto &v : b)
            v ^= key[0];
        return b;
    }
    require(key.size() == (strategy == 2 ? 16u : 32u) && ciphertext.size() % 16 == 0,
            "Invalid AES key/block size");
    struct provider {
        BCRYPT_ALG_HANDLE h = nullptr;
        ~provider() {
            if (h)
                BCryptCloseAlgorithmProvider(h, 0);
        }
    } a;
    struct key_handle {
        BCRYPT_KEY_HANDLE h = nullptr;
        ~key_handle() {
            if (h)
                BCryptDestroyKey(h);
        }
    } k;
    auto check = [](NTSTATUS s) {
        if (s < 0) {
            failure f(POLAR_OS_ERROR, "Windows CNG failure");
            f.hresult = s;
            throw f;
        }
    };
    check(BCryptOpenAlgorithmProvider(&a.h, BCRYPT_AES_ALGORITHM, nullptr, 0));
    check(BCryptSetProperty(a.h, BCRYPT_CHAINING_MODE,
                            reinterpret_cast<PUCHAR>(const_cast<wchar_t *>(BCRYPT_CHAIN_MODE_ECB)),
                            sizeof(BCRYPT_CHAIN_MODE_ECB), 0));
    check(BCryptGenerateSymmetricKey(a.h, &k.h, nullptr, 0, const_cast<PUCHAR>(key.data()),
                                     ULONG(key.size()), 0));
    bytes out(ciphertext.size());
    ULONG written = 0;
    if (!ciphertext.empty())
        check(BCryptDecrypt(k.h, const_cast<PUCHAR>(ciphertext.data()), ULONG(ciphertext.size()),
                            nullptr, nullptr, 0, out.data(), ULONG(out.size()), &written, 0));
    out.resize(written);
    return out;
}
bytes gzip_decode(view b, size_t limit) {
    z_stream z{};
    require(b.size() <= UINT_MAX, "Compressed input too large");
    require(inflateInit2(&z, 15 + 32) == Z_OK, "zlib initialize");
    struct close {
        z_stream *s;
        ~close() {
            inflateEnd(s);
        }
    } guard{&z};
    z.next_in = const_cast<Bytef *>(b.data());
    z.avail_in = uInt(b.size());
    bytes out;
    std::array<uint8_t, 16384> block{};
    for (;;) {
        z.next_out = block.data();
        z.avail_out = uInt(block.size());
        int r = inflate(&z, Z_NO_FLUSH);
        size_t len = block.size() - z.avail_out;
        require(out.size() + len <= limit, "Decompressed data exceeds limit");
        out.insert(out.end(), block.begin(), block.begin() + len);
        if (r == Z_STREAM_END) {
            require(z.avail_in == 0, "Trailing compressed data");
            break;
        }
        require(r == Z_OK && len > 0, "Invalid/truncated gzip");
    }
    return out;
}
bytes gzip_encode(view b) {
    z_stream z{};
    require(b.size() <= UINT_MAX, "Gzip input too large");
    require(deflateInit2(&z, Z_DEFAULT_COMPRESSION, Z_DEFLATED, 15 + 16, 8, Z_DEFAULT_STRATEGY) ==
                Z_OK,
            "zlib initialize");
    struct close {
        z_stream *s;
        ~close() {
            deflateEnd(s);
        }
    } guard{&z};
    z.next_in = const_cast<Bytef *>(b.data());
    z.avail_in = uInt(b.size());
    bytes out;
    std::array<uint8_t, 16384> block{};
    for (;;) {
        z.next_out = block.data();
        z.avail_out = uInt(block.size());
        int r = deflate(&z, Z_FINISH);
        out.insert(out.end(), block.begin(), block.end() - z.avail_out);
        if (r == Z_STREAM_END)
            break;
        require(r == Z_OK, "Gzip encode failure");
    }
    return out;
}
} // namespace polar
