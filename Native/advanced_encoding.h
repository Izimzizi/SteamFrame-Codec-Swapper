#pragma once
#include "nvEncodeAPI11.h"
#include <cstdint>
#include <cstring>

enum AdvancedFlag : uint32_t {
    AdvancedCq = 1, AdvancedMaxQp = 2, AdvancedPeak = 4, AdvancedVbv = 8,
    AdvancedDelay = 16, AdvancedChroma = 32, AdvancedSpatialAq = 64,
    AdvancedTemporalAq = 128, AdvancedPreset = 256
};
constexpr uint32_t AdvancedAll = 511;
constexpr uint32_t AdvancedInitialization = AdvancedChroma | AdvancedSpatialAq | AdvancedTemporalAq | AdvancedPreset;
// Explicit little-endian x64 protocol. Do not marshal C++ bools or SDK structs.
struct AdvancedEncoding {
    uint32_t size = 64, version = 1, flags{}, cq256{}, maxQp{}, peakBitrate{}, vbvBits{}, vbvDelayBits{};
    int32_t cbQpOffset{}, crQpOffset{};
    uint32_t spatialAq{}, aqStrength{}, temporalAq{}, preset{}, reserved0{}, reserved1{};
};
static_assert(sizeof(AdvancedEncoding) == 64);
inline bool AdvancedEncodingValid(const AdvancedEncoding& value) {
    return value.size == 64 && value.version == 1 && !(value.flags & ~AdvancedAll) && !value.reserved0 && !value.reserved1 &&
        value.cq256 <= 51 * 256 && value.maxQp <= 51 &&
        (!(value.flags & AdvancedPeak) || (value.peakBitrate >= 25000000 && value.peakBitrate <= 500000000)) &&
        (!(value.flags & AdvancedVbv) || (value.vbvBits >= 1000 && value.vbvBits <= 64000000)) &&
        value.vbvDelayBits <= 64000000 && value.cbQpOffset >= -12 && value.cbQpOffset <= 12 &&
        value.crQpOffset >= -12 && value.crQpOffset <= 12 && value.spatialAq <= 1 && value.aqStrength <= 15 &&
        value.temporalAq <= 1 && value.preset <= 7 && (!(value.flags & AdvancedPreset) || value.preset != 0) &&
        (!(value.flags & AdvancedDelay) || !(value.flags & AdvancedVbv) || value.vbvDelayBits <= value.vbvBits);
}
inline GUID AdvancedPresetGuid(uint32_t value) {
    switch (value) {
    case 1: return NV_ENC_PRESET_P1_GUID; case 2: return NV_ENC_PRESET_P2_GUID;
    case 3: return NV_ENC_PRESET_P3_GUID; case 4: return NV_ENC_PRESET_P4_GUID;
    case 5: return NV_ENC_PRESET_P5_GUID; case 6: return NV_ENC_PRESET_P6_GUID;
    default: return NV_ENC_PRESET_P7_GUID;
    }
}
inline const char* PresetName(const GUID& value) {
    for (uint32_t index = 1; index <= 7; ++index) {
        const char* names[] = {"Valve", "P1", "P2", "P3", "P4", "P5", "P6", "P7"};
        if (IsEqualGUID(value, AdvancedPresetGuid(index))) return names[index];
    }
    return "Other";
}
// Fields that cannot apply to this configuration. Callers drop these individually so
// one conflict does not discard unrelated requested fields.
inline uint32_t AdvancedConflicts(const AdvancedEncoding& value, const NV_ENC_CONFIG& config) {
    const auto& rc = config.rcParams;
    uint32_t conflicts{};
    if ((value.flags & AdvancedCq) && rc.rateControlMode != NV_ENC_PARAMS_RC_VBR) conflicts |= AdvancedCq;
    if ((value.flags & AdvancedPeak) && rc.rateControlMode != NV_ENC_PARAMS_RC_VBR) conflicts |= AdvancedPeak;
    if ((value.flags & AdvancedMaxQp) && rc.enableMinQP &&
        (value.maxQp < rc.minQP.qpIntra || value.maxQp < rc.minQP.qpInterP || value.maxQp < rc.minQP.qpInterB)) conflicts |= AdvancedMaxQp;
    // An explicit delay larger than Valve's unchanged buffer cannot apply. (Both explicit
    // values are already validated together; a VBV-only request clamps Valve's delay.)
    if ((value.flags & AdvancedDelay) && !(value.flags & AdvancedVbv) && rc.vbvBufferSize && value.vbvDelayBits > rc.vbvBufferSize)
        conflicts |= AdvancedDelay;
    return conflicts;
}
inline bool ApplyAdvancedEncoding(const AdvancedEncoding& value, NV_ENC_CONFIG& config) {
    auto& rc = config.rcParams;
    if (AdvancedConflicts(value, config)) return false;
    const auto size = value.flags & AdvancedVbv ? value.vbvBits : rc.vbvBufferSize;
    const auto delay = value.flags & AdvancedDelay ? value.vbvDelayBits : rc.vbvInitialDelay;
    if ((value.flags & AdvancedDelay) && size && delay > size) return false;
    // A smaller requested buffer must not be paired with Valve's larger initial delay.
    if ((value.flags & AdvancedVbv) && !(value.flags & AdvancedDelay) && delay > size) rc.vbvInitialDelay = size;
    if (value.flags & AdvancedCq) { rc.targetQuality = static_cast<uint8_t>(value.cq256 >> 8); rc.targetQualityLSB = static_cast<uint8_t>(value.cq256); }
    if (value.flags & AdvancedMaxQp) { rc.enableMaxQP = 1; rc.maxQP = {value.maxQp, value.maxQp, value.maxQp}; }
    if (value.flags & AdvancedPeak) {
        rc.maxBitRate = value.peakBitrate;
        // Valve's per-frame budget can exceed its nominal UI target. An explicit
        // ceiling must bound both copied values rather than reject valid peaks.
        if (rc.averageBitRate > value.peakBitrate) rc.averageBitRate = value.peakBitrate;
    }
    if (value.flags & AdvancedVbv) rc.vbvBufferSize = value.vbvBits;
    if (value.flags & AdvancedDelay) rc.vbvInitialDelay = value.vbvDelayBits;
    if (value.flags & AdvancedChroma) { rc.cbQPIndexOffset = static_cast<int8_t>(value.cbQpOffset); rc.crQPIndexOffset = static_cast<int8_t>(value.crQpOffset); }
    if (value.flags & AdvancedSpatialAq) { rc.enableAQ = value.spatialAq; rc.aqStrength = value.aqStrength; }
    if (value.flags & AdvancedTemporalAq) rc.enableTemporalAQ = value.temporalAq;
    return true;
}
inline bool AdvancedInitializationEqual(const AdvancedEncoding& a, const AdvancedEncoding& b) {
    const auto flags = a.flags & AdvancedInitialization;
    if (flags != (b.flags & AdvancedInitialization)) return false;
    return (!(flags & AdvancedChroma) || (a.cbQpOffset == b.cbQpOffset && a.crQpOffset == b.crQpOffset)) &&
        (!(flags & AdvancedSpatialAq) || (a.spatialAq == b.spatialAq && a.aqStrength == b.aqStrength)) &&
        (!(flags & AdvancedTemporalAq) || a.temporalAq == b.temporalAq) &&
        (!(flags & AdvancedPreset) || a.preset == b.preset);
}
inline AdvancedEncoding AdvancedForReconfigure(const AdvancedEncoding& request, const AdvancedEncoding& initialized) {
    auto effective = request;
    effective.flags = (request.flags & ~AdvancedInitialization) | (initialized.flags & AdvancedInitialization);
    effective.cbQpOffset = initialized.cbQpOffset; effective.crQpOffset = initialized.crQpOffset;
    effective.spatialAq = initialized.spatialAq; effective.aqStrength = initialized.aqStrength;
    effective.temporalAq = initialized.temporalAq; effective.preset = initialized.preset;
    return effective;
}
// Only whole, explicitly allowed quality fields may follow a preset. All codec,
// GOP, layout, resource, bitfield and latency policy stays exactly as Valve supplied it.
// The same fetched preset GUID also selects NVENC's non-exposed hardware choices.
inline void MergeAdvancedPreset(const NV_ENC_CONFIG& baseline, const NV_ENC_CONFIG& desired, NV_ENC_CONFIG& config) {
    if (config.mvPrecision == baseline.mvPrecision) config.mvPrecision = desired.mvPrecision;
    if (memcmp(&config.rcParams.constQP, &baseline.rcParams.constQP, sizeof(NV_ENC_QP)) == 0)
        config.rcParams.constQP = desired.rcParams.constQP;
    if (memcmp(&config.rcParams.initialRCQP, &baseline.rcParams.initialRCQP, sizeof(NV_ENC_QP)) == 0)
        config.rcParams.initialRCQP = desired.rcParams.initialRCQP;
}
