#pragma once
#include <cstdint>
#include <cstddef>

// Metadata only. One writer under statsMutex; external readers bracket a copy
// with Sequence and accept only the same nonzero even publication number.
struct alignas(8) ProbeTelemetry {
    int64_t sequence{};
    uint32_t version=1, size=144;
    uint64_t generation{}, configurationRevision{}, frames{}, bytes{};
    uint64_t encodeErrors{}, lockErrors{}, rejectedCalls{}, policyErrors{};
    double lastEncodedSeconds{};
    uint32_t width{}, height{}, bitDepth{}, rateControl{}, multiPass{};
    uint32_t qualityMode{}, requestedPolicy{}, appliedPolicy{}, flags{};
    uint32_t pendingAdvanced{}, rejectedAdvanced{}, appliedAdvanced{};
    uint64_t policyCalls{};
};
static_assert(sizeof(ProbeTelemetry)==144);
static_assert(offsetof(ProbeTelemetry,lastEncodedSeconds)==80);
static_assert(offsetof(ProbeTelemetry,flags)==120);
