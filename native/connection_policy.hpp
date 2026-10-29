#pragma once
#include "common.hpp"
namespace polar {
inline polar_connect_options connection_options(view data) {
    polar_connect_options options{sizeof(options), POLAR_DEFAULT_FEATURES,
                                  POLAR_AUTHENTICATE_WHEN_NEEDED, 0};
    if (!data.empty()) {
        if (data.size() != sizeof(options))
            throw failure(POLAR_INVALID_ARGUMENT, "CONNECT options size");
        std::memcpy(&options, data.data(), sizeof(options));
        if (options.struct_size != sizeof(options) || options.reserved ||
            (options.feature_mask & ~0x3fffffu) ||
            options.authentication > POLAR_AUTHENTICATION_DISABLED)
            throw failure(POLAR_INVALID_ARGUMENT, "Invalid CONNECT options");
    }
    return options;
}
inline polar_connect_options automatic_connection_options(view data) {
    if (data.empty())
        return connection_options({});
    if (data.size() != 8 && data.size() != 8 + sizeof(polar_connect_options))
        throw failure(POLAR_INVALID_ARGUMENT, "AUTO_CONNECT options size");
    return connection_options(data.subspan(8));
}
inline bool security_retry_needed(uint32_t att, bool unreachable, uint32_t attempts,
                                  uint32_t trigger) {
    return att == 5 || att == 15 ||
           (unreachable && attempts == 1 && (trigger == 5 || trigger == 15));
}
// Only idempotent GATT operations may use this bounded security-settlement wait.
template <class Perform, class Retry, class Pause, class WithinBudget>
auto settle_security(Perform perform, Retry retry, Pause pause, WithinBudget within_budget) {
    auto result = perform();
    while (retry(result) && within_budget()) {
        pause();
        if (!within_budget())
            break;
        result = perform();
    }
    return result;
}
/* The same failed ATT operation is retried only after authentication completes.
   This control-path policy is shared by reads, writes and CCCD writes. The
   transport supplies cancellation/session checks before every attempt. */
template <class Perform, class NeedsSecurity, class Authenticate, class Guard>
auto authenticated_operation(Perform perform, NeedsSecurity needs_security,
                             Authenticate authenticate, Guard guard, bool allowed) {
    guard();
    auto result = perform();
    for (uint32_t attempt = 1; needs_security(result); ++attempt) {
        guard();
        if (!allowed || attempt > 2)
            throw failure(
                POLAR_AUTHENTICATION_FAILED,
                allowed
                    ? "Protected operation failed after authentication; pairing recovery required"
                    : "Protected operation requires authentication (disabled for this connection)");
        authenticate(attempt);
        guard();
        result = perform();
    }
    guard();
    return result;
}
inline uint32_t reconnect_delay_ms(uint32_t failed_attempts) {
    return 2000u << std::min(failed_attempts ? failed_attempts - 1 : 0u, 3u);
}
} // namespace polar
