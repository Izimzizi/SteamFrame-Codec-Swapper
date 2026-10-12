#include <windows.h>
#include <intrin.h>
#include <bcrypt.h>
#include <atomic>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <filesystem>
#include <iterator>
#include <mutex>
#include <unordered_map>
#include <vector>
#include "MinHook.h"
#include "nvEncodeAPI11.h"
#include "qp_map.h"
#include "driver_patterns.h"
#include "encoding_policy.h"
#include "advanced_encoding.h"

// Public NVENC ABI for telemetry; strict discovered private layout for optional quality.
// No hooks, file access or NVENC calls in DllMain.
static_assert(sizeof(void*) == 8);
static_assert(offsetof(NV_ENC_PIC_PARAMS, qpDeltaMap) == 1696);
static_assert(offsetof(NV_ENC_PIC_PARAMS, qpDeltaMapSize) == 1704);
static_assert(offsetof(NV_ENC_CONFIG, rcParams) + offsetof(NV_ENC_RC_PARAMS, qpMapMode) == 136);
static_assert(offsetof(NV_ENC_LOCK_BITSTREAM, bitstreamSizeInBytes) == 36);
static_assert(NV_ENC_LOCK_BITSTREAM_VER == 0x7101000b);
static_assert(NV_ENCODE_API_FUNCTION_LIST_VER == 0x7102000b);
static_assert(offsetof(NV_ENC_LOCK_BITSTREAM, frameIdx) == 24);
static_assert(offsetof(NV_ENC_LOCK_BITSTREAM, outputTimeStamp) == 40);
static_assert(sizeof(NV_ENC_LOCK_BITSTREAM) == 1544);
static_assert(offsetof(NV_ENCODE_API_FUNCTION_LIST, nvEncEncodePicture) == 136);
static_assert(offsetof(NV_ENCODE_API_FUNCTION_LIST, nvEncLockBitstream) == 144);
static_assert(NV_ENC_CONFIG_VER == 0xf107000b);
static_assert(NV_ENC_INITIALIZE_PARAMS_VER == 0xf105000b);
static_assert(NV_ENC_RECONFIGURE_PARAMS_VER == 0xf101000b);
using StartFn = void(__fastcall*)(uintptr_t);
StartFn originalStart{};
PNVENCENCODEPICTURE originalEncode{};
PNVENCLOCKBITSTREAM originalLock{};
PNVENCDESTROYENCODER originalDestroy{};
PNVENCINITIALIZEENCODER originalInitialize{};
PNVENCRECONFIGUREENCODER originalReconfigure{};
PNVENCGETENCODEPRESETCONFIGEX originalPreset{};
PNVENCGETENCODECAPS getCaps{};
std::array<void*, 7> targets{};
std::atomic<uint32_t> mode{};
std::atomic<uint32_t> encodingPolicy{};
std::atomic<bool> installed{};
std::atomic<bool> qualitySupported{};
inspection::Image driverImage;
HMODULE retainedDriver{}, retainedNvenc{};
bool initialized{};
std::mutex controlMutex, statsMutex;
thread_local uintptr_t currentEncoder{};
thread_local std::array<int8_t, 65536> mapCopy{};
AdvancedEncoding advancedRequest{};
uint32_t advancedRejected{};
struct BufferStamp { uint64_t submitted{}, counted{}; double submittedAt{}; };
struct Session {
    std::unordered_map<void*, BufferStamp> buffers;
    bool customPolicy{}, capsQueried{}, dynamicBitrate{}, dynamicRc{}, customVbv{}, temporalAq{};
    uint32_t supportedRc{};
    AdvancedEncoding initializedAdvanced{};
    uint32_t queriedPreset{};
    NV_ENC_CONFIG baselinePreset{}, desiredPreset{};
    GUID queryCodec{}, queryOriginalPreset{}, initializedCodec{};
    NV_ENC_TUNING_INFO queryTuning{};
    NV_ENC_CONFIG initializedBaselinePreset{}, initializedDesiredPreset{};
    const char* codec = "Unknown";
    const char* codecSource = "";
};
std::unordered_map<void*, Session> sessions;
struct Stats {
    uint64_t frames{}, encodeErrors{}, validMaps{}, modifiedFrames{}, rejectedMaps{};
    uint64_t encodedBytes{}, encodedFrames{}, lockErrors{}, generation{}, submission{}, rejectedApiCalls{};
    uint64_t quantizerSamples{}, quantizerSum{};
    uint32_t lastFrameQp{}, lastFrameBytes{};
    uint32_t lastStatus{};
    double lastEncodedTime{};
    QualityMapMetadata lastMap{};
    double lastMapTime{};
    uint32_t lastMapStatus{};
    uint64_t policyCalls{}, policyErrors{}, rejectedPolicyCalls{};
    bool policyObserved{};
    uint32_t appliedPolicy{}, rateControl{}, multiPass{}, averageBitrate{}, maxBitrate{}, vbvSize{}, vbvDelay{};
    uint32_t encodeWidth{}, encodeHeight{}, frameRateNum{}, frameRateDen{};
    uint32_t bitDepth = 8;
    bool filler{};
    const char* preset = "Unknown";
    const char* codec = "Unknown";
    const char* codecSource = "";
    uint32_t appliedAdvancedFlags{}, pendingAdvancedFlags{}, rejectedAdvancedFlags{}, lastAdvancedError{};
    NV_ENC_RC_PARAMS effectiveRc{};
    bool capsQueried{}, dynamicBitrate{}, customVbv{}, temporalAq{};
    uint64_t reconfigureCalls{}, reconfigureTotalUs{}, reconfigureMaxUs{}, reconfigureOver2Ms{};
    uint64_t encodeCalls{}, encodeTotalUs{}, encodeMaxUs{}, encodeOver2Ms{};
    uint64_t completionCalls{}, completionTotalUs{}, completionMaxUs{}, completionOver2Ms{};
} stats;

double qpcSeconds() {
    LARGE_INTEGER value{}, frequency{};
    QueryPerformanceCounter(&value); QueryPerformanceFrequency(&frequency);
    return static_cast<double>(value.QuadPart) / frequency.QuadPart;
}
uint64_t creationTime() {
    FILETIME created{}, exited{}, kernel{}, user{};
    GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user);
    return (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
}
template<class T> T field(uintptr_t base, size_t offset) { return *reinterpret_cast<T*>(base + offset); }
const char* codecName(const NV_ENC_CONFIG* config) {
    if (!config) return "Unknown";
    const auto& profile = config->profileGUID;
    if (IsEqualGUID(profile, NV_ENC_HEVC_PROFILE_MAIN_GUID) || IsEqualGUID(profile, NV_ENC_HEVC_PROFILE_MAIN10_GUID) || IsEqualGUID(profile, NV_ENC_HEVC_PROFILE_FREXT_GUID)) return "HEVC";
    if (IsEqualGUID(profile, NV_ENC_H264_PROFILE_BASELINE_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_MAIN_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_HIGH_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_HIGH_444_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_STEREO_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_PROGRESSIVE_HIGH_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_CONSTRAINED_HIGH_GUID)) return "H.264";
    return "Unknown";
}
const char* codecGuidName(const GUID& codec) {
    if (IsEqualGUID(codec, NV_ENC_CODEC_HEVC_GUID)) return "HEVC";
    if (IsEqualGUID(codec, NV_ENC_CODEC_H264_GUID)) return "H.264";
    return "Unknown"; // The SDK 11 helper cannot identify an AV1 session.
}
void recordPolicy(void* handle, const NV_ENC_INITIALIZE_PARAMS& params, uint32_t policy, NVENCSTATUS result,
                  const AdvancedEncoding& advanced = {}, bool initialization = false) {
    std::lock_guard<std::mutex> lock(statsMutex);
    ++stats.policyCalls;
    if (result != NV_ENC_SUCCESS) { ++stats.policyErrors; return; }
    auto found = sessions.find(handle);
    if (found == sessions.end() && sessions.size() < 32) found = sessions.try_emplace(handle).first;
    if (found != sessions.end()) {
        found->second.codec = codecGuidName(params.encodeGUID);
        found->second.codecSource = "NVENC accepted codec GUID and encoded output";
        if (initialization) {
            found->second.initializedAdvanced = advanced; found->second.initializedAdvanced.flags &= AdvancedInitialization;
            found->second.initializedCodec = params.encodeGUID;
            if (advanced.flags & AdvancedPreset) {
                found->second.initializedBaselinePreset = found->second.baselinePreset;
                found->second.initializedDesiredPreset = found->second.desiredPreset;
            }
        }
        found->second.customPolicy = policy != 0 || advanced.flags != 0 || found->second.initializedAdvanced.flags != 0;
        stats.capsQueried = found->second.capsQueried; stats.dynamicBitrate = found->second.dynamicBitrate;
        stats.customVbv = found->second.customVbv; stats.temporalAq = found->second.temporalAq;
        const auto& initialPolicy = found->second.initializedAdvanced;
        stats.pendingAdvancedFlags = AdvancedInitializationEqual(advancedRequest, initialPolicy) ? 0
            : (advancedRequest.flags | initialPolicy.flags) & AdvancedInitialization;
    }
    const auto& config = *params.encodeConfig;
    stats.policyObserved = true; stats.appliedPolicy = policy;
    stats.rateControl = config.rcParams.rateControlMode; stats.multiPass = config.rcParams.multiPass;
    stats.averageBitrate = config.rcParams.averageBitRate; stats.maxBitrate = config.rcParams.maxBitRate;
    stats.vbvSize = config.rcParams.vbvBufferSize; stats.vbvDelay = config.rcParams.vbvInitialDelay;
    stats.encodeWidth = params.encodeWidth; stats.encodeHeight = params.encodeHeight;
    stats.frameRateNum = params.frameRateNum; stats.frameRateDen = params.frameRateDen;
    const bool hevc = IsEqualGUID(params.encodeGUID, NV_ENC_CODEC_HEVC_GUID);
    stats.bitDepth = hevc ? 8 + config.encodeCodecConfig.hevcConfig.pixelBitDepthMinus8 : 8;
    const bool cbr = config.rcParams.rateControlMode == NV_ENC_PARAMS_RC_CBR;
    stats.filler = hevc ? config.encodeCodecConfig.hevcConfig.enableFillerDataInsertion || (cbr && config.encodeCodecConfig.hevcConfig.outputPictureTimingSEI)
        : config.encodeCodecConfig.h264Config.enableFillerDataInsertion || (cbr && config.encodeCodecConfig.h264Config.outputPictureTimingSEI);
    stats.codec = codecGuidName(params.encodeGUID);
    stats.codecSource = "NVENC accepted codec GUID and encoded output";
    stats.preset = PresetName(params.presetGUID);
    stats.appliedAdvancedFlags = advanced.flags; stats.effectiveRc = config.rcParams;
}
bool canTrackPolicy(void* handle) {
    std::lock_guard<std::mutex> lock(statsMutex);
    if (sessions.size() < 32 || sessions.find(handle) != sessions.end()) return true;
    ++stats.rejectedPolicyCalls; return false;
}
void rejectAdvanced(uint32_t flags, NVENCSTATUS error) {
    advancedRejected |= flags;
    std::lock_guard<std::mutex> lock(statsMutex);
    stats.rejectedAdvancedFlags = advancedRejected; stats.lastAdvancedError = error;
}
void queryAdvancedCaps(void* handle, const GUID& codec) {
    { std::lock_guard<std::mutex> lock(statsMutex); if (sessions[handle].capsQueried) return; }
    const auto query = [&](NV_ENC_CAPS capability) {
        NV_ENC_CAPS_PARAM parameter{}; parameter.version = NV_ENC_CAPS_PARAM_VER; parameter.capsToQuery = capability;
        int value{};
        return getCaps && getCaps(handle, codec, &parameter, &value) == NV_ENC_SUCCESS ? value : 0;
    };
    const bool bitrate = query(NV_ENC_CAPS_SUPPORT_DYN_BITRATE_CHANGE);
    const bool vbv = query(NV_ENC_CAPS_SUPPORT_CUSTOM_VBV_BUF_SIZE);
    const bool temporal = query(NV_ENC_CAPS_SUPPORT_TEMPORAL_AQ);
    const bool dynamicRc = query(NV_ENC_CAPS_SUPPORT_DYN_RCMODE_CHANGE);
    const auto supportedRc = static_cast<uint32_t>(query(NV_ENC_CAPS_SUPPORTED_RATECONTROL_MODES));
    std::lock_guard<std::mutex> lock(statsMutex);
    auto& session = sessions[handle]; session.capsQueried = true; session.dynamicBitrate = bitrate;
    session.customVbv = vbv; session.temporalAq = temporal;
    session.dynamicRc = dynamicRc; session.supportedRc = supportedRc;
}
uint32_t validatedPolicy(void* handle, const NV_ENC_INITIALIZE_PARAMS& params, uint32_t policy, bool initialization) {
    if (!(policy & 3u)) return policy;
    queryAdvancedCaps(handle, params.encodeGUID);
    const auto desired = (policy & 3u) == 1 ? NV_ENC_PARAMS_RC_VBR : NV_ENC_PARAMS_RC_CBR;
    bool supported{};
    { std::lock_guard<std::mutex> lock(statsMutex); const auto& session = sessions[handle];
        supported = (session.supportedRc & (1u << desired)) && (initialization || session.dynamicRc || params.encodeConfig->rcParams.rateControlMode == desired);
        if (!supported) ++stats.rejectedPolicyCalls;
    }
    return supported ? policy : 0;
}
AdvancedEncoding prepareAdvanced(void* handle, bool initialization, NV_ENC_INITIALIZE_PARAMS& params, NV_ENC_CONFIG& config) {
    auto value = advancedRequest; value.flags &= ~advancedRejected;
    if (!canTrackPolicy(handle)) { value.flags = 0; return value; }
    queryAdvancedCaps(handle, params.encodeGUID);
    uint32_t unsupported{};
    {
        std::lock_guard<std::mutex> lock(statsMutex);
        const auto& session = sessions[handle];
        if (!initialization) value = AdvancedForReconfigure(value, session.initializedAdvanced);
        if ((value.flags & AdvancedPeak) && !initialization && !session.dynamicBitrate) unsupported |= AdvancedPeak;
        if ((value.flags & (AdvancedVbv | AdvancedDelay)) && !session.customVbv) unsupported |= value.flags & (AdvancedVbv | AdvancedDelay);
        if ((value.flags & AdvancedTemporalAq) && value.temporalAq && !session.temporalAq) unsupported |= AdvancedTemporalAq;
        // Capability rejection is per field. An unrelated unsupported VBV request
        // must not disable a working quality target or an initialized preset.
        value.flags &= ~unsupported;
        if (value.flags & AdvancedPreset) {
            const bool matching = initialization ? session.queriedPreset == value.preset &&
                IsEqualGUID(session.queryCodec, params.encodeGUID) && IsEqualGUID(session.queryOriginalPreset, params.presetGUID) &&
                session.queryTuning == params.tuningInfo : session.initializedAdvanced.preset == value.preset &&
                IsEqualGUID(session.initializedCodec, params.encodeGUID);
            if (matching) {
                MergeAdvancedPreset(initialization ? session.baselinePreset : session.initializedBaselinePreset,
                    initialization ? session.desiredPreset : session.initializedDesiredPreset, config);
                params.presetGUID = AdvancedPresetGuid(value.preset);
            } else value.flags &= ~AdvancedPreset; // No matching preset query; wait for a fresh session.
        }
    }
    if (unsupported) rejectAdvanced(unsupported, NV_ENC_ERR_UNSUPPORTED_PARAM);
    return value;
}
NVENCSTATUS NVENCAPI hookPreset(void* handle, GUID codec, GUID preset, NV_ENC_TUNING_INFO tuning, NV_ENC_PRESET_CONFIG* output) {
    const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress());
    if (!installed.load() || !inspection::executableCaller(driverImage, caller)) return originalPreset(handle, codec, preset, tuning, output);
    std::lock_guard<std::mutex> control(controlMutex);
    const auto result = originalPreset(handle, codec, preset, tuning, output);
    if (result != NV_ENC_SUCCESS || !output || output->version != NV_ENC_PRESET_CONFIG_VER ||
        output->presetCfg.version != NV_ENC_CONFIG_VER || !(advancedRequest.flags & AdvancedPreset) ||
        (advancedRejected & AdvancedPreset) || !canTrackPolicy(handle)) return result;
    // Leave Valve's returned preset untouched. Initialize merges a separately fetched
    // candidate into Valve's final config, which leaves an exact fallback available.
    auto candidate = *output;
    const auto alternate = originalPreset(handle, codec, AdvancedPresetGuid(advancedRequest.preset), tuning, &candidate);
    if (alternate != NV_ENC_SUCCESS) { rejectAdvanced(AdvancedPreset, alternate); return result; }
    std::lock_guard<std::mutex> lock(statsMutex);
    auto& session = sessions[handle]; session.queriedPreset = advancedRequest.preset;
    session.baselinePreset = output->presetCfg; session.desiredPreset = candidate.presetCfg;
    session.queryCodec = codec; session.queryOriginalPreset = preset; session.queryTuning = tuning;
    return result;
}
NVENCSTATUS NVENCAPI hookInitialize(void* handle, NV_ENC_INITIALIZE_PARAMS* params) {
    const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress());
    // Reject internal/unrelated calls before acquiring the gate. NVENC may call
    // another hooked public API while a verified reconfigure is in progress.
    if (!installed.load() || !inspection::executableCaller(driverImage, caller)) return originalInitialize(handle, params);
    std::lock_guard<std::mutex> control(controlMutex);
    if (!installed.load()) return originalInitialize(handle, params);
    if (!EncodingParametersValid(params)) {
        { std::lock_guard<std::mutex> lock(statsMutex); ++stats.rejectedPolicyCalls; }
        return originalInitialize(handle, params);
    }
    auto policy = encodingPolicy.load();
    if ((policy || advancedRequest.flags) && !canTrackPolicy(handle)) return originalInitialize(handle, params);
    policy = validatedPolicy(handle, *params, policy, true);
    auto copied = *params; auto config = *params->encodeConfig;
    ApplyEncodingPolicy(policy, copied, config); copied.encodeConfig = &config;
    auto advanced = prepareAdvanced(handle, true, copied, config);
    if (!ApplyAdvancedEncoding(advanced, config) || (!advanced.flags && (advancedRequest.flags & ~advancedRejected))) {
        rejectAdvanced(advancedRequest.flags, NV_ENC_ERR_UNSUPPORTED_PARAM); advanced.flags = 0;
        copied = *params; config = *params->encodeConfig; ApplyEncodingPolicy(policy, copied, config); copied.encodeConfig = &config;
    }
    auto result = originalInitialize(handle, policy || advanced.flags ? &copied : params);
    if (result != NV_ENC_SUCCESS && advanced.flags) {
        rejectAdvanced(advancedRequest.flags, result); advanced.flags = 0;
        result = originalInitialize(handle, params); // Exact untouched Valve fallback.
        recordPolicy(handle, *params, 0, result, advanced, true);
    } else recordPolicy(handle, policy || advanced.flags ? copied : *params, policy, result, advanced, true);
    return result;
}
NVENCSTATUS NVENCAPI hookReconfigure(void* handle, NV_ENC_RECONFIGURE_PARAMS* params) {
    const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress());
    if (!installed.load() || !inspection::executableCaller(driverImage, caller)) return originalReconfigure(handle, params);
    std::lock_guard<std::mutex> control(controlMutex);
    if (!installed.load()) return originalReconfigure(handle, params);
    if (!params || params->version != NV_ENC_RECONFIGURE_PARAMS_VER || !EncodingParametersValid(&params->reInitEncodeParams)) {
        { std::lock_guard<std::mutex> lock(statsMutex); ++stats.rejectedPolicyCalls; }
        return originalReconfigure(handle, params);
    }
    auto policy = encodingPolicy.load();
    if ((policy || advancedRequest.flags) && !canTrackPolicy(handle)) return originalReconfigure(handle, params);
    policy = validatedPolicy(handle, params->reInitEncodeParams, policy, false);
    auto copied = *params; auto config = *params->reInitEncodeParams.encodeConfig;
    ApplyEncodingPolicy(policy, copied.reInitEncodeParams, config); copied.reInitEncodeParams.encodeConfig = &config;
    auto advanced = prepareAdvanced(handle, false, copied.reInitEncodeParams, config);
    if (!ApplyAdvancedEncoding(advanced, config) || (!advanced.flags && (advancedRequest.flags & ~advancedRejected & ~AdvancedInitialization))) {
        rejectAdvanced(advancedRequest.flags & ~AdvancedInitialization, NV_ENC_ERR_UNSUPPORTED_PARAM);
        copied = *params; config = *params->reInitEncodeParams.encodeConfig;
        ApplyEncodingPolicy(policy, copied.reInitEncodeParams, config); copied.reInitEncodeParams.encodeConfig = &config;
        advanced = prepareAdvanced(handle, false, copied.reInitEncodeParams, config);
        ApplyAdvancedEncoding(advanced, config);
    }
    const auto begin = qpcSeconds();
    auto result = originalReconfigure(handle, policy || advanced.flags ? &copied : params);
    if (result != NV_ENC_SUCCESS && (advanced.flags & ~AdvancedInitialization)) {
        rejectAdvanced(advancedRequest.flags & ~AdvancedInitialization, result);
        copied = *params; config = *params->reInitEncodeParams.encodeConfig; copied.reInitEncodeParams.encodeConfig = &config;
        advanced = prepareAdvanced(handle, false, copied.reInitEncodeParams, config);
        ApplyAdvancedEncoding(advanced, config);
        result = originalReconfigure(handle, advanced.flags ? &copied : params);
        recordPolicy(handle, advanced.flags ? copied.reInitEncodeParams : params->reInitEncodeParams, 0, result, advanced);
    } else recordPolicy(handle, policy || advanced.flags ? copied.reInitEncodeParams : params->reInitEncodeParams, policy, result, advanced);
    const auto duration = static_cast<uint64_t>((qpcSeconds() - begin) * 1000000.0);
    { std::lock_guard<std::mutex> lock(statsMutex); ++stats.reconfigureCalls; stats.reconfigureTotalUs += duration;
        if (duration > stats.reconfigureMaxUs) stats.reconfigureMaxUs = duration; stats.reconfigureOver2Ms += duration > 2000; }
    return result;
}
void __fastcall hookStart(uintptr_t encoder) {
    const auto previous = currentEncoder;
    currentEncoder = encoder;
    originalStart(encoder);
    currentEncoder = previous;
}
NVENCSTATUS NVENCAPI hookEncode(void* handle, NV_ENC_PIC_PARAMS* picture) {
    const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress());
    const auto encoder = currentEncoder;
    if (!installed.load() || !picture || !inspection::executableCaller(driverImage, caller)) return originalEncode(handle, picture);
    if (picture->version != NV_ENC_PIC_PARAMS_VER) {
        { std::lock_guard<std::mutex> lock(statsMutex); ++stats.rejectedApiCalls; }
        mode.store(0);
        return originalEncode(handle, picture);
    }
    // Private fields are accessed only after every layout fingerprint has matched.
    const bool privateLayout = qualitySupported.load() && encoder && handle == field<void*>(encoder, 520) &&
        field<uint32_t>(encoder, 39520) == NV_ENCODE_API_FUNCTION_LIST_VER &&
        field<void*>(encoder, 39656) == targets[1] && field<void*>(encoder, 39664) == targets[2];
    const auto settings = mode.load(std::memory_order_acquire);
    const auto config = privateLayout ? field<NV_ENC_CONFIG*>(encoder, 624) : nullptr;
    const auto width = picture->inputWidth, height = picture->inputHeight, size = picture->qpDeltaMapSize;
    auto native = picture->qpDeltaMap;
    bool valid = privateLayout && config && config->rcParams.qpMapMode == NV_ENC_QP_MAP_DELTA && native &&
        width >= 32 && height >= 128 && width <= 8192 && height <= 16384 && width % 32 == 0 && height % 128 == 0 &&
        size == (width / 32) * (height / 32) && size <= mapCopy.size() && field<uint8_t>(encoder, 39441) == 2 &&
        field<int>(encoder, 20) == 0 && width == field<uint32_t>(encoder, 39504) && height == field<uint32_t>(encoder, 39500);
    QualityMapMetadata mapMetadata{};
    if (valid) valid = ApplyQualityMap(native, mapCopy.data(), size, width, static_cast<int8_t>(settings & 255), static_cast<int8_t>((settings >> 8) & 255), (settings & 0x10000) != 0, &mapMetadata);
    uint64_t submission = 0;
    {
        std::lock_guard<std::mutex> lock(statsMutex);
        // Learn only submissions whose real return address belongs to Valve's code.
        if (sessions.size() < 32 || sessions.find(handle) != sessions.end()) {
            auto& buffers = sessions.try_emplace(handle).first->second.buffers;
            if (picture->version == NV_ENC_PIC_PARAMS_VER && picture->outputBitstream &&
                (buffers.size() < 256 || buffers.find(picture->outputBitstream) != buffers.end()))
                { auto& stamp = buffers[picture->outputBitstream]; stamp.submitted = submission = ++stats.submission; stamp.submittedAt = qpcSeconds(); }
        }
        const auto found = sessions.find(handle);
        // A verified codec GUID takes precedence over automatic/generic profile GUIDs.
        // The profile is a fallback for a probe attached after encoder initialization.
        if (config && found != sessions.end() && !found->second.codecSource[0]) {
            found->second.codec = codecName(config);
            found->second.codecSource = "NVENC codec profile and encoded output";
        }
    }
    const bool modified = valid && (settings & 0x10000);
    if (modified) picture->qpDeltaMap = mapCopy.data();
    const auto begin = qpcSeconds();
    const auto result = originalEncode(handle, picture);
    const auto duration = static_cast<uint64_t>((qpcSeconds() - begin) * 1000000.0);
    picture->qpDeltaMap = native;
    const bool error = result != NV_ENC_SUCCESS && result != NV_ENC_ERR_NEED_MORE_INPUT;
    if (error) mode.store(0);
    {
        std::lock_guard<std::mutex> lock(statsMutex);
        ++stats.encodeCalls; stats.encodeTotalUs += duration;
        if (duration > stats.encodeMaxUs) stats.encodeMaxUs = duration; stats.encodeOver2Ms += duration > 2000;
        ++stats.frames; stats.encodeErrors += error; stats.validMaps += valid; stats.modifiedFrames += modified;
        stats.rejectedMaps += !valid; stats.lastStatus = result;
        if (valid) {
            stats.lastMap = mapMetadata;
            stats.lastMapTime = qpcSeconds();
            stats.lastMapStatus = result;
        }
        if (error && submission) {
            const auto session = sessions.find(handle);
            if (session != sessions.end()) {
                const auto buffer = session->second.buffers.find(picture->outputBitstream);
                if (buffer != session->second.buffers.end() && buffer->second.submitted == submission)
                    buffer->second.submitted = buffer->second.counted; // Reject an unaccepted output generation.
            }
        }
    }
    return result;
}
NVENCSTATUS NVENCAPI hookLock(void* handle, NV_ENC_LOCK_BITSTREAM* output) {
    const auto result = originalLock(handle, output);
    if (!installed.load() || !output) return result;
    std::lock_guard<std::mutex> lock(statsMutex);
    const auto session = sessions.find(handle);
    if (session == sessions.end()) return result;
    if (output->version != NV_ENC_LOCK_BITSTREAM_VER) { ++stats.rejectedApiCalls; mode.store(0); return result; }
    if (result != NV_ENC_SUCCESS) {
        if (result != NV_ENC_ERR_LOCK_BUSY && result != NV_ENC_ERR_NEED_MORE_INPUT) ++stats.lockErrors;
        return result;
    }
    if (!output->outputBitstream) return result;
    auto& buffers = session->second.buffers;
    const auto buffer = buffers.find(output->outputBitstream);
    if (buffer == buffers.end()) return result;
    auto& stamp = buffer->second;
    if (stamp.submitted && stamp.submitted != stamp.counted) {
        // Size metadata only. Never access bitstreamBufferPtr or copy video payload.
        stats.encodedBytes += output->bitstreamSizeInBytes;
        stats.codec = session->second.codec;
        stats.codecSource = session->second.codecSource;
        ++stats.encodedFrames;
        stats.lastFrameBytes = output->bitstreamSizeInBytes;
        if (output->frameAvgQP <= 63) {
            ++stats.quantizerSamples;
            stats.quantizerSum += output->frameAvgQP;
            stats.lastFrameQp = output->frameAvgQP;
        }
        stats.lastEncodedTime = qpcSeconds();
        if (stamp.submittedAt > 0) {
            const auto duration = static_cast<uint64_t>((stats.lastEncodedTime - stamp.submittedAt) * 1000000.0);
            ++stats.completionCalls; stats.completionTotalUs += duration;
            if (duration > stats.completionMaxUs) stats.completionMaxUs = duration; stats.completionOver2Ms += duration > 2000;
        }
        stamp.counted = stamp.submitted;
    }
    return result;
}
NVENCSTATUS NVENCAPI hookDestroy(void* handle) {
    const auto result = originalDestroy(handle);
    if (result == NV_ENC_SUCCESS) { std::lock_guard<std::mutex> lock(statsMutex); sessions.erase(handle); }
    return result;
}
extern "C" __declspec(dllexport) DWORD WINAPI GetProbeVersion(void*) { return 1; }
extern "C" __declspec(dllexport) DWORD WINAPI InspectDriver(void* parameter) {
    if (!parameter || wcsnlen_s(static_cast<const wchar_t*>(parameter), 32768) >= 32768) return 1001;
    inspection::Image image;
    return inspection::fileImage(static_cast<const wchar_t*>(parameter), image) ? 0 : 1001;
}
extern "C" __declspec(dllexport) DWORD WINAPI StartProbe(void*) {
    std::lock_guard<std::mutex> control(controlMutex);
    if (installed.load()) return 0;
    if (GetModuleHandleW(L"SteamFrameQuality.dll")) return 1011;
    if (GetModuleHandleW(L"SteamFrameAV1Backend.dll")) return 1016;
    if (!initialized) {
        if (!retainedDriver && !GetModuleHandleExW(0, L"driver_vrlink.dll", &retainedDriver)) return 1001;
        wchar_t path[32768]{};
        if (!GetModuleFileNameW(retainedDriver, path, 32768) || InspectDriver(path) || !inspection::moduleImage(retainedDriver, driverImage)) return 1001;
        const auto layout = inspection::qualityLayout(driverImage, inspection::qualityPatterns, std::size(inspection::qualityPatterns));
        qualitySupported.store(layout.supported);
        if (!retainedNvenc && !GetModuleHandleExW(0, L"nvEncodeAPI64.dll", &retainedNvenc)) {
            // Arm reconnect-only controls before the first encoder session exists.
            // The public function table creates no encode session or device context.
            // Restrict lookup to the installed system NVIDIA API, never app/CWD DLLs.
            retainedNvenc = LoadLibraryExW(L"nvEncodeAPI64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (!retainedNvenc) return 1002;
        }
        using CreateFn = NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
        const auto create = reinterpret_cast<CreateFn>(GetProcAddress(retainedNvenc, "NvEncodeAPICreateInstance"));
        NV_ENCODE_API_FUNCTION_LIST functions{}; functions.version = NV_ENCODE_API_FUNCTION_LIST_VER;
        if (!create || create(&functions) != NV_ENC_SUCCESS || !functions.nvEncEncodePicture || !functions.nvEncLockBitstream || !functions.nvEncDestroyEncoder || !functions.nvEncInitializeEncoder || !functions.nvEncReconfigureEncoder || !functions.nvEncGetEncodePresetConfigEx || !functions.nvEncGetEncodeCaps) return 1003;
        getCaps = functions.nvEncGetEncodeCaps;
        targets = {layout.supported ? reinterpret_cast<unsigned char*>(retainedDriver) + layout.startRva : nullptr,
            reinterpret_cast<void*>(functions.nvEncEncodePicture), reinterpret_cast<void*>(functions.nvEncLockBitstream), reinterpret_cast<void*>(functions.nvEncDestroyEncoder),
            reinterpret_cast<void*>(functions.nvEncInitializeEncoder), reinterpret_cast<void*>(functions.nvEncReconfigureEncoder), reinterpret_cast<void*>(functions.nvEncGetEncodePresetConfigEx)};
        const auto init = MH_Initialize();
        if (init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED) return 1004;
        void* detours[] = {reinterpret_cast<void*>(&hookStart), reinterpret_cast<void*>(&hookEncode), reinterpret_cast<void*>(&hookLock), reinterpret_cast<void*>(&hookDestroy), reinterpret_cast<void*>(&hookInitialize), reinterpret_cast<void*>(&hookReconfigure), reinterpret_cast<void*>(&hookPreset)};
        void** originals[] = {reinterpret_cast<void**>(&originalStart), reinterpret_cast<void**>(&originalEncode), reinterpret_cast<void**>(&originalLock), reinterpret_cast<void**>(&originalDestroy), reinterpret_cast<void**>(&originalInitialize), reinterpret_cast<void**>(&originalReconfigure), reinterpret_cast<void**>(&originalPreset)};
        for (size_t i = 0; i < targets.size(); ++i) {
            if (!targets[i]) continue;
            if (MH_CreateHook(targets[i], detours[i], originals[i]) != MH_OK) {
                for (size_t j = 0; j < i; ++j) if (targets[j]) MH_RemoveHook(targets[j]);
                return 1005;
            }
        }
        initialized = true;
    }
    {
        std::lock_guard<std::mutex> lock(statsMutex);
        const auto generation = stats.generation + 1;
        stats = {}; stats.generation = generation; sessions.clear();
    }
    mode.store(0); encodingPolicy.store(0); advancedRequest = {}; advancedRejected = 0; installed.store(true);
    for (auto target : targets) if (target) MH_QueueEnableHook(target);
    if (MH_ApplyQueued() != MH_OK) {
        installed.store(false);
        for (auto target : targets) if (target) MH_QueueDisableHook(target);
        MH_ApplyQueued();
        return 1007;
    }
    return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI SetQuality(void* parameter) {
    const auto settings = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(parameter));
    const int center = static_cast<int8_t>(settings & 255), periphery = static_cast<int8_t>((settings >> 8) & 255);
    if ((settings & ~0x1ffffu) || !QualityOffsetsValid(center, periphery) || !installed.load()) return 1008;
    if ((settings & 0x10000) && !qualitySupported.load()) return 1012;
    mode.store(settings, std::memory_order_release); return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI SetEncodingPolicy(void* parameter) {
    std::lock_guard<std::mutex> control(controlMutex);
    const auto policy = reinterpret_cast<uintptr_t>(parameter);
    if (policy > 15 || !EncodingPolicyValid(static_cast<uint32_t>(policy)) || !installed.load()) return 1013;
    encodingPolicy.store(static_cast<uint32_t>(policy));
    return 0; // Acceptance is reported only after the driver's next successful NVENC call.
}
extern "C" __declspec(dllexport) DWORD WINAPI SetAdvancedEncoding(void* parameter) {
    std::lock_guard<std::mutex> control(controlMutex);
    if (!parameter || !installed.load()) return 1015;
    const auto request = *static_cast<const AdvancedEncoding*>(parameter);
    if (!AdvancedEncodingValid(request)) return 1015;
    advancedRequest = request; advancedRejected = 0;
    std::lock_guard<std::mutex> lock(statsMutex);
    stats.rejectedAdvancedFlags = 0; stats.lastAdvancedError = 0;
    stats.pendingAdvancedFlags = 0;
    for (const auto& entry : sessions) if (!AdvancedInitializationEqual(request, entry.second.initializedAdvanced))
        stats.pendingAdvancedFlags |= (request.flags | entry.second.initializedAdvanced.flags) & AdvancedInitialization;
    if (sessions.empty()) stats.pendingAdvancedFlags = request.flags;
    return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI WriteStatus(void* parameter) {
    if (!parameter) return 1009;
    const auto path = static_cast<const wchar_t*>(parameter);
    const auto length = wcsnlen_s(path, 32768);
    if (!length || length >= 32768) return 1009;
    const std::filesystem::path destination(path), temporary(destination.wstring() + L".writing");
    Stats copy{};
    AdvancedEncoding request{};
    { std::lock_guard<std::mutex> control(controlMutex); request = advancedRequest;
        std::lock_guard<std::mutex> lock(statsMutex); copy = stats; }
    const auto settings = mode.load(); const auto now = qpcSeconds();
    FILE* file{};
    if (_wfopen_s(&file, temporary.c_str(), L"wb")) return 1009;
    const bool active = installed.load() && copy.lastEncodedTime > 0 && now - copy.lastEncodedTime < 2.0;
    const int written = fprintf(file,
        "{\"protocol_version\":1,\"process_id\":%lu,\"process_creation_time\":%llu,\"installed\":%s,\"enabled\":%s,\"active\":%s,\"center_offset\":%d,\"peripheral_offset\":%d,\"encoded_bytes\":%llu,\"encoded_frames\":%llu,\"timestamp_seconds\":%.9f,\"last_encoded_timestamp_seconds\":%.9f,\"generation\":\"%lu-%llu-%llu\",\"encode_errors\":%llu,\"lock_errors\":%llu,\"frames\":%llu,\"valid_maps\":%llu,\"modified_frames\":%llu,\"rejected_maps\":%llu,\"last_nvenc_status\":%u,\"codec\":\"%s\",\"quality_supported\":%s,\"rejected_api_calls\":%llu,"
        "\"last_map_timestamp_seconds\":%.9f,\"last_map_enabled\":%s,\"last_map_center_offset\":%d,\"last_map_peripheral_offset\":%d,\"last_map_size\":%u,\"last_map_peripheral_tiles\":%u,\"last_map_peripheral_changed_tiles\":%u,\"last_map_peripheral_saturated_tiles\":%u,\"last_map_peripheral_clamped_tiles\":%u,\"last_map_peripheral_min_delta\":%d,\"last_map_peripheral_max_delta\":%d,\"last_map_protected_tiles\":%u,\"last_map_protected_mismatches\":%u,\"last_map_nvenc_status\":%u,"
        "\"last_map_center_tiles\":%u,\"last_map_center_changed_tiles\":%u,\"last_map_center_clamped_tiles\":%u,\"last_map_center_min_delta\":%d,\"last_map_center_max_delta\":%d,\"quantizer_samples\":%llu,\"quantizer_sum\":%llu,\"last_frame_qp\":%u,\"last_frame_bytes\":%u,"
        "\"requested_encoding_policy\":%u,\"policy_observed\":%s,\"applied_encoding_policy\":%u,\"rate_control\":%u,\"multi_pass\":%u,\"average_bitrate\":%u,\"max_bitrate\":%u,\"vbv_size\":%u,\"vbv_delay\":%u,\"encode_width\":%u,\"encode_height\":%u,\"frame_rate_num\":%u,\"frame_rate_den\":%u,\"filler\":%s,\"preset\":\"%s\",\"policy_calls\":%llu,\"policy_errors\":%llu,\"rejected_policy_calls\":%llu,",
        GetCurrentProcessId(), creationTime(), installed.load() ? "true" : "false", settings & 0x10000 ? "true" : "false", active ? "true" : "false",
        static_cast<int8_t>(settings & 255), static_cast<int8_t>((settings >> 8) & 255), copy.encodedBytes, copy.encodedFrames, now, copy.lastEncodedTime,
        GetCurrentProcessId(), creationTime(), copy.generation, copy.encodeErrors, copy.lockErrors, copy.frames, copy.validMaps, copy.modifiedFrames, copy.rejectedMaps, copy.lastStatus, copy.codec,
        qualitySupported.load() ? "true" : "false", copy.rejectedApiCalls,
        copy.lastMapTime, copy.lastMap.enabled ? "true" : "false", copy.lastMap.centerOffset, copy.lastMap.peripheralOffset,
        copy.lastMap.size, copy.lastMap.peripheralTiles, copy.lastMap.peripheralChangedTiles, copy.lastMap.peripheralSaturatedTiles,
        copy.lastMap.peripheralClampedTiles, copy.lastMap.peripheralMinDelta, copy.lastMap.peripheralMaxDelta,
        copy.lastMap.protectedTiles, copy.lastMap.protectedMismatches, copy.lastMapStatus,
        copy.lastMap.centerTiles, copy.lastMap.centerChangedTiles, copy.lastMap.centerClampedTiles,
        copy.lastMap.centerMinDelta, copy.lastMap.centerMaxDelta, copy.quantizerSamples, copy.quantizerSum, copy.lastFrameQp, copy.lastFrameBytes,
        encodingPolicy.load(), copy.policyObserved ? "true" : "false", copy.appliedPolicy, copy.rateControl, copy.multiPass,
        copy.averageBitrate, copy.maxBitrate, copy.vbvSize, copy.vbvDelay, copy.encodeWidth, copy.encodeHeight,
        copy.frameRateNum, copy.frameRateDen, copy.filler ? "true" : "false", copy.preset, copy.policyCalls, copy.policyErrors, copy.rejectedPolicyCalls);
    const auto& rc = copy.effectiveRc;
    const int advancedWritten = fprintf(file,
        "\"codec_source\":\"%s\",\"bit_depth\":%u,\"advanced_supported\":true,\"requested_advanced\":{\"flags\":%u,\"cq256\":%u,\"max_qp\":%u,\"peak_bitrate\":%u,\"vbv_bits\":%u,\"vbv_delay_bits\":%u,\"cb_qp_offset\":%d,\"cr_qp_offset\":%d,\"spatial_aq\":%u,\"aq_strength\":%u,\"temporal_aq\":%u,\"preset\":%u},"
        "\"applied_advanced_flags\":%u,\"pending_advanced_flags\":%u,\"rejected_advanced_flags\":%u,\"last_advanced_error\":%u,"
        "\"effective\":{\"cq256\":%u,\"max_qp_enabled\":%s,\"max_qp_i\":%u,\"max_qp_p\":%u,\"max_qp_b\":%u,\"spatial_aq\":%s,\"aq_strength\":%u,\"temporal_aq\":%s,\"cb_qp_offset\":%d,\"cr_qp_offset\":%d},"
        "\"advanced_capabilities\":{\"queried\":%s,\"dynamic_bitrate\":%s,\"custom_vbv\":%s,\"temporal_aq\":%s},"
        "\"timing\":{\"reconfigure_calls\":%llu,\"reconfigure_total_us\":%llu,\"reconfigure_max_us\":%llu,\"reconfigure_over_2ms\":%llu,\"encode_calls\":%llu,\"encode_total_us\":%llu,\"encode_max_us\":%llu,\"encode_over_2ms\":%llu,\"completion_calls\":%llu,\"completion_total_us\":%llu,\"completion_max_us\":%llu,\"completion_over_2ms\":%llu}}\n",
        copy.codecSource, copy.bitDepth, request.flags, request.cq256, request.maxQp, request.peakBitrate, request.vbvBits, request.vbvDelayBits,
        request.cbQpOffset, request.crQpOffset, request.spatialAq, request.aqStrength, request.temporalAq, request.preset,
        copy.appliedAdvancedFlags, copy.pendingAdvancedFlags, copy.rejectedAdvancedFlags, copy.lastAdvancedError,
        (static_cast<uint32_t>(rc.targetQuality) << 8) | rc.targetQualityLSB, rc.enableMaxQP ? "true" : "false",
        rc.maxQP.qpIntra, rc.maxQP.qpInterP, rc.maxQP.qpInterB, rc.enableAQ ? "true" : "false", rc.aqStrength,
        rc.enableTemporalAQ ? "true" : "false", rc.cbQPIndexOffset, rc.crQPIndexOffset,
        copy.capsQueried ? "true" : "false", copy.dynamicBitrate ? "true" : "false", copy.customVbv ? "true" : "false", copy.temporalAq ? "true" : "false",
        copy.reconfigureCalls, copy.reconfigureTotalUs, copy.reconfigureMaxUs, copy.reconfigureOver2Ms,
        copy.encodeCalls, copy.encodeTotalUs, copy.encodeMaxUs, copy.encodeOver2Ms,
        copy.completionCalls, copy.completionTotalUs, copy.completionMaxUs, copy.completionOver2Ms);
    const bool good = written > 0 && advancedWritten > 0 && fflush(file) == 0;
    const bool closed = fclose(file) == 0;
    if (!good || !closed || !MoveFileExW(temporary.c_str(), destination.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { DeleteFileW(temporary.c_str()); return 1009; }
    return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI StopProbe(void*) {
    std::lock_guard<std::mutex> control(controlMutex);
    mode.store(0);
    if (!installed.load()) return 0;
    encodingPolicy.store(0);
    advancedRequest = {}; advancedRejected = 0;
    { std::lock_guard<std::mutex> lock(statsMutex); for (const auto& session : sessions) if (session.second.customPolicy) return 1014; }
    for (auto target : targets) if (target) MH_QueueDisableHook(target);
    if (MH_ApplyQueued() != MH_OK) return 1010;
    installed.store(false);
    // Keep this module and its trampolines loaded for in-flight callbacks until process exit.
    return 0;
}
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
