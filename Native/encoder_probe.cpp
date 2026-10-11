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
using StartFn = void(__fastcall*)(uintptr_t);
StartFn originalStart{};
PNVENCENCODEPICTURE originalEncode{};
PNVENCLOCKBITSTREAM originalLock{};
PNVENCDESTROYENCODER originalDestroy{};
std::array<void*, 4> targets{};
std::atomic<uint32_t> mode{};
std::atomic<bool> installed{};
std::atomic<bool> qualitySupported{};
inspection::Image driverImage;
HMODULE retainedDriver{}, retainedNvenc{};
bool initialized{};
std::mutex controlMutex, statsMutex;
thread_local uintptr_t currentEncoder{};
thread_local std::array<int8_t, 65536> mapCopy{};
struct BufferStamp { uint64_t submitted{}, counted{}; };
struct Session { std::unordered_map<void*, BufferStamp> buffers; };
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
    const char* codec = "NVENC";
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
    if (!config) return "NVENC";
    const auto& profile = config->profileGUID;
    if (IsEqualGUID(profile, NV_ENC_HEVC_PROFILE_MAIN_GUID) || IsEqualGUID(profile, NV_ENC_HEVC_PROFILE_MAIN10_GUID) || IsEqualGUID(profile, NV_ENC_HEVC_PROFILE_FREXT_GUID)) return "HEVC";
    if (IsEqualGUID(profile, NV_ENC_H264_PROFILE_BASELINE_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_MAIN_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_HIGH_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_HIGH_444_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_STEREO_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_PROGRESSIVE_HIGH_GUID) || IsEqualGUID(profile, NV_ENC_H264_PROFILE_CONSTRAINED_HIGH_GUID)) return "H.264";
    return "NVENC";
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
                buffers[picture->outputBitstream].submitted = submission = ++stats.submission;
        }
        stats.codec = codecName(config);
    }
    const bool modified = valid && (settings & 0x10000);
    if (modified) picture->qpDeltaMap = mapCopy.data();
    const auto result = originalEncode(handle, picture);
    picture->qpDeltaMap = native;
    const bool error = result != NV_ENC_SUCCESS && result != NV_ENC_ERR_NEED_MORE_INPUT;
    if (error) mode.store(0);
    {
        std::lock_guard<std::mutex> lock(statsMutex);
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
        ++stats.encodedFrames;
        stats.lastFrameBytes = output->bitstreamSizeInBytes;
        if (output->frameAvgQP <= 63) {
            ++stats.quantizerSamples;
            stats.quantizerSum += output->frameAvgQP;
            stats.lastFrameQp = output->frameAvgQP;
        }
        stats.lastEncodedTime = qpcSeconds();
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
    if (!initialized) {
        if (!retainedDriver && !GetModuleHandleExW(0, L"driver_vrlink.dll", &retainedDriver)) return 1001;
        wchar_t path[32768]{};
        if (!GetModuleFileNameW(retainedDriver, path, 32768) || InspectDriver(path) || !inspection::moduleImage(retainedDriver, driverImage)) return 1001;
        const auto layout = inspection::qualityLayout(driverImage, inspection::qualityPatterns, std::size(inspection::qualityPatterns));
        qualitySupported.store(layout.supported);
        if (!retainedNvenc && !GetModuleHandleExW(0, L"nvEncodeAPI64.dll", &retainedNvenc)) return 1002;
        using CreateFn = NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
        const auto create = reinterpret_cast<CreateFn>(GetProcAddress(retainedNvenc, "NvEncodeAPICreateInstance"));
        NV_ENCODE_API_FUNCTION_LIST functions{}; functions.version = NV_ENCODE_API_FUNCTION_LIST_VER;
        if (!create || create(&functions) != NV_ENC_SUCCESS || !functions.nvEncEncodePicture || !functions.nvEncLockBitstream || !functions.nvEncDestroyEncoder) return 1003;
        targets = {layout.supported ? reinterpret_cast<unsigned char*>(retainedDriver) + layout.startRva : nullptr,
            reinterpret_cast<void*>(functions.nvEncEncodePicture), reinterpret_cast<void*>(functions.nvEncLockBitstream), reinterpret_cast<void*>(functions.nvEncDestroyEncoder)};
        const auto init = MH_Initialize();
        if (init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED) return 1004;
        void* detours[] = {reinterpret_cast<void*>(&hookStart), reinterpret_cast<void*>(&hookEncode), reinterpret_cast<void*>(&hookLock), reinterpret_cast<void*>(&hookDestroy)};
        void** originals[] = {reinterpret_cast<void**>(&originalStart), reinterpret_cast<void**>(&originalEncode), reinterpret_cast<void**>(&originalLock), reinterpret_cast<void**>(&originalDestroy)};
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
    mode.store(0); installed.store(true);
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
extern "C" __declspec(dllexport) DWORD WINAPI WriteStatus(void* parameter) {
    if (!parameter) return 1009;
    const auto path = static_cast<const wchar_t*>(parameter);
    const auto length = wcsnlen_s(path, 32768);
    if (!length || length >= 32768) return 1009;
    const std::filesystem::path destination(path), temporary(destination.wstring() + L".writing");
    Stats copy{};
    { std::lock_guard<std::mutex> lock(statsMutex); copy = stats; }
    const auto settings = mode.load(); const auto now = qpcSeconds();
    FILE* file{};
    if (_wfopen_s(&file, temporary.c_str(), L"wb")) return 1009;
    const bool active = installed.load() && copy.lastEncodedTime > 0 && now - copy.lastEncodedTime < 2.0;
    const int written = fprintf(file,
        "{\"protocol_version\":1,\"process_id\":%lu,\"process_creation_time\":%llu,\"installed\":%s,\"enabled\":%s,\"active\":%s,\"center_offset\":%d,\"peripheral_offset\":%d,\"encoded_bytes\":%llu,\"encoded_frames\":%llu,\"timestamp_seconds\":%.9f,\"last_encoded_timestamp_seconds\":%.9f,\"generation\":\"%lu-%llu-%llu\",\"encode_errors\":%llu,\"lock_errors\":%llu,\"frames\":%llu,\"valid_maps\":%llu,\"modified_frames\":%llu,\"rejected_maps\":%llu,\"last_nvenc_status\":%u,\"codec\":\"%s\",\"quality_supported\":%s,\"rejected_api_calls\":%llu,"
        "\"last_map_timestamp_seconds\":%.9f,\"last_map_enabled\":%s,\"last_map_center_offset\":%d,\"last_map_peripheral_offset\":%d,\"last_map_size\":%u,\"last_map_peripheral_tiles\":%u,\"last_map_peripheral_changed_tiles\":%u,\"last_map_peripheral_saturated_tiles\":%u,\"last_map_peripheral_clamped_tiles\":%u,\"last_map_peripheral_min_delta\":%d,\"last_map_peripheral_max_delta\":%d,\"last_map_protected_tiles\":%u,\"last_map_protected_mismatches\":%u,\"last_map_nvenc_status\":%u,"
        "\"last_map_center_tiles\":%u,\"last_map_center_changed_tiles\":%u,\"last_map_center_clamped_tiles\":%u,\"last_map_center_min_delta\":%d,\"last_map_center_max_delta\":%d,\"quantizer_samples\":%llu,\"quantizer_sum\":%llu,\"last_frame_qp\":%u,\"last_frame_bytes\":%u}\n",
        GetCurrentProcessId(), creationTime(), installed.load() ? "true" : "false", settings & 0x10000 ? "true" : "false", active ? "true" : "false",
        static_cast<int8_t>(settings & 255), static_cast<int8_t>((settings >> 8) & 255), copy.encodedBytes, copy.encodedFrames, now, copy.lastEncodedTime,
        GetCurrentProcessId(), creationTime(), copy.generation, copy.encodeErrors, copy.lockErrors, copy.frames, copy.validMaps, copy.modifiedFrames, copy.rejectedMaps, copy.lastStatus, copy.codec,
        qualitySupported.load() ? "true" : "false", copy.rejectedApiCalls,
        copy.lastMapTime, copy.lastMap.enabled ? "true" : "false", copy.lastMap.centerOffset, copy.lastMap.peripheralOffset,
        copy.lastMap.size, copy.lastMap.peripheralTiles, copy.lastMap.peripheralChangedTiles, copy.lastMap.peripheralSaturatedTiles,
        copy.lastMap.peripheralClampedTiles, copy.lastMap.peripheralMinDelta, copy.lastMap.peripheralMaxDelta,
        copy.lastMap.protectedTiles, copy.lastMap.protectedMismatches, copy.lastMapStatus,
        copy.lastMap.centerTiles, copy.lastMap.centerChangedTiles, copy.lastMap.centerClampedTiles,
        copy.lastMap.centerMinDelta, copy.lastMap.centerMaxDelta, copy.quantizerSamples, copy.quantizerSum, copy.lastFrameQp, copy.lastFrameBytes);
    const bool good = written > 0 && fflush(file) == 0;
    const bool closed = fclose(file) == 0;
    if (!good || !closed || !MoveFileExW(temporary.c_str(), destination.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { DeleteFileW(temporary.c_str()); return 1009; }
    return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI StopProbe(void*) {
    std::lock_guard<std::mutex> control(controlMutex);
    mode.store(0);
    if (!installed.load()) return 0;
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
