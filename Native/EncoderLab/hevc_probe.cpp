// Standalone NVENC experiment. No hooks, sockets, SteamVR writes or headset access.
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include "nvEncodeAPI11.h"
using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;

struct Options {
    std::string mode = "cbr", scene = "flat", multipass = "single", csv, output;
    uint32_t width = 2048, height = 8192, fps = 90, frames = 300, mbps = 400, stepMbps = 0, stepAt = 150;
    bool pace = false, resetAtStep = false;
};
Options arguments(int argc, char** argv) {
    Options o;
    for (int i = 1; i < argc; ++i) {
        const std::string key = argv[i];
        if (key == "--pace") { o.pace = true; continue; }
        if (key == "--reset-at-step") { o.resetAtStep = true; continue; }
        if (i + 1 == argc) throw std::invalid_argument("Missing argument value");
        const std::string value = argv[++i];
        if (key == "--mode") o.mode = value;
        else if (key == "--scene") o.scene = value;
        else if (key == "--multipass") o.multipass = value;
        else if (key == "--csv") o.csv = value;
        else if (key == "--output") o.output = value;
        else {
            if (value.empty() || value.find_first_not_of("0123456789") != std::string::npos)
                throw std::invalid_argument("Expected an unsigned integer");
            const auto n = std::stoull(value);
            if (n > UINT32_MAX) throw std::invalid_argument("Integer out of range");
            if (key == "--width") o.width = static_cast<uint32_t>(n);
            else if (key == "--height") o.height = static_cast<uint32_t>(n);
            else if (key == "--fps") o.fps = static_cast<uint32_t>(n);
            else if (key == "--frames") o.frames = static_cast<uint32_t>(n);
            else if (key == "--mbps") o.mbps = static_cast<uint32_t>(n);
            else if (key == "--step-mbps") o.stepMbps = static_cast<uint32_t>(n);
            else if (key == "--step-at") o.stepAt = static_cast<uint32_t>(n);
            else throw std::invalid_argument("Unknown argument: " + key);
        }
    }
    if ((o.mode != "vbr" && o.mode != "cbr" && o.mode != "padded") ||
        (o.scene != "flat" && o.scene != "alternating") ||
        (o.multipass != "single" && o.multipass != "quarter" && o.multipass != "full") ||
        o.width < 64 || o.width > 8192 || o.width % 2 || o.height < 64 || o.height > 8192 || o.height % 2 ||
        uint64_t(o.width) * o.height > 2048ULL * 8192 || o.fps < 30 || o.fps > 120 ||
        o.frames < 60 || o.frames > 1800 || o.mbps < 25 || o.mbps > 500 ||
        (o.resetAtStep && !o.stepMbps) ||
        (o.stepMbps && (o.stepMbps < 25 || o.stepMbps > 500 || o.stepAt == 0 || o.stepAt >= o.frames)))
        throw std::invalid_argument("Invalid mode, scene, dimensions, cadence or rate");
    return o;
}

struct Encoder {
    HMODULE library = nullptr;
    NV_ENCODE_API_FUNCTION_LIST api{};
    void* session = nullptr;
    NV_ENC_OUTPUT_PTR output = nullptr;
    std::array<NV_ENC_REGISTERED_PTR, 2> registered{};
    NV_ENC_INPUT_PTR mapped = nullptr;
    bool locked = false;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    std::array<ComPtr<ID3D11Texture2D>, 2> textures;
    void check(NVENCSTATUS status, const char* operation) {
        if (status != NV_ENC_SUCCESS) throw std::runtime_error(std::string(operation) + ": NVENC " + std::to_string(status));
    }
    int capability(NV_ENC_CAPS cap) {
        NV_ENC_CAPS_PARAM p{}; p.version = NV_ENC_CAPS_PARAM_VER; p.capsToQuery = cap;
        int value{}; check(api.nvEncGetEncodeCaps(session, NV_ENC_CODEC_HEVC_GUID, &p, &value), "capability"); return value;
    }
    ~Encoder() {
        if (session) {
            if (locked) api.nvEncUnlockBitstream(session, output);
            if (mapped) api.nvEncUnmapInputResource(session, mapped);
            if (output) api.nvEncDestroyBitstreamBuffer(session, output);
            for (auto resource : registered) if (resource) api.nvEncUnregisterResource(session, resource);
            api.nvEncDestroyEncoder(session);
        }
        if (library) FreeLibrary(library);
    }
};
void hr(HRESULT result) { if (FAILED(result)) throw std::runtime_error("D3D11 error " + std::to_string(result)); }

struct FrameClock {
    HANDLE timer = nullptr;
    explicit FrameClock(bool enabled) {
        if (enabled) {
            timer = CreateWaitableTimerExW(nullptr, nullptr, 0x2, TIMER_MODIFY_STATE | SYNCHRONIZE);
            if (!timer) throw std::runtime_error("High-resolution waitable timer unavailable");
        }
    }
    ~FrameClock() { if (timer) CloseHandle(timer); }
    void wait(Clock::time_point due) {
        if (!timer) return;
        // Keep the final spin short. Ordinary sleep_until rounded to coarse Windows
        // timer ticks in this lab, giving 25 ms gaps despite a 90 fps average.
        const auto tail = std::chrono::microseconds(150);
        auto remaining = due - Clock::now();
        if (remaining > tail) {
            LARGE_INTEGER delay{};
            delay.QuadPart = -std::max<int64_t>(1, std::chrono::duration_cast<std::chrono::nanoseconds>(remaining - tail).count() / 100);
            if (!SetWaitableTimerEx(timer, &delay, 0, nullptr, nullptr, nullptr, 0) || WaitForSingleObject(timer, INFINITE) != WAIT_OBJECT_0)
                throw std::runtime_error("Frame timer failed");
        }
        while (Clock::now() < due) YieldProcessor();
    }
};

// HEVC Annex B filler-data NAL (type 38), including its start code and NAL header.
// Excludes VPS/SPS/PPS/SEI from filler: non-filler bytes are not a quality score.
uint64_t fillerBytes(const uint8_t* data, size_t size) {
    size_t start = size, prefix = 0; uint64_t filler = 0;
    const auto finish = [&](size_t end) {
        if (start < size && end >= start + prefix + 2 && ((data[start + prefix] >> 1) & 63) == 38)
            filler += end - start;
    };
    for (size_t i = 0; i + 3 <= size;) {
        size_t next = 0;
        if (data[i] == 0 && data[i + 1] == 0) {
            if (i + 4 <= size && data[i + 2] == 0 && data[i + 3] == 1) next = 4;
            else if (data[i + 2] == 1) next = 3;
        }
        if (next) { finish(i); start = i; prefix = next; i += next; }
        else ++i;
    }
    if (start == size) throw std::runtime_error("Encoder did not return Annex B");
    finish(size); return filler;
}

int main(int argc, char** argv) {
    try {
        if (argc == 2 && std::string(argv[1]) == "--help") {
            std::cout << "Standalone HEVC lab: --mode vbr|cbr|padded --scene flat|alternating --multipass single|quarter|full\n"
                "--mbps 25..500 --frames 60..1800 --fps 30..120 --width N --height N --pace\n"
                "--step-mbps N --step-at FRAME --reset-at-step --csv PATH --output PATH (first 60 frames)\n";
            return 0;
        }
        const auto o = arguments(argc, argv);
        Encoder e;
        e.library = LoadLibraryExW(L"nvEncodeAPI64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!e.library) throw std::runtime_error("NVENC DLL unavailable");
        using Create = NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
        const auto create = reinterpret_cast<Create>(GetProcAddress(e.library, "NvEncodeAPICreateInstance"));
        if (!create) throw std::runtime_error("NVENC factory unavailable");
        e.api.version = NV_ENCODE_API_FUNCTION_LIST_VER; e.check(create(&e.api), "factory");
        hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &e.device, nullptr, &e.context));
        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{}; open.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
        open.device = e.device.Get(); open.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX; open.apiVersion = NVENCAPI_VERSION;
        e.check(e.api.nvEncOpenEncodeSessionEx(&open, &e.session), "open");
        const int dynamicRc = e.capability(NV_ENC_CAPS_SUPPORT_DYN_RCMODE_CHANGE);
        const int dynamicBitrate = e.capability(NV_ENC_CAPS_SUPPORT_DYN_BITRATE_CHANGE);
        const int supportedRc = e.capability(NV_ENC_CAPS_SUPPORTED_RATECONTROL_MODES);
        NV_ENC_PRESET_CONFIG preset{}; preset.version = NV_ENC_PRESET_CONFIG_VER; preset.presetCfg.version = NV_ENC_CONFIG_VER;
        e.check(e.api.nvEncGetEncodePresetConfigEx(e.session, NV_ENC_CODEC_HEVC_GUID, NV_ENC_PRESET_P2_GUID,
            NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY, &preset), "preset");
        auto config = preset.presetCfg;
        config.profileGUID = NV_ENC_HEVC_PROFILE_MAIN10_GUID;
        config.gopLength = NVENC_INFINITE_GOPLENGTH; config.frameIntervalP = 1;
        auto& rc = config.rcParams;
        rc.rateControlMode = o.mode == "vbr" ? NV_ENC_PARAMS_RC_VBR : NV_ENC_PARAMS_RC_CBR;
        rc.averageBitRate = o.mbps * 1000000; rc.maxBitRate = rc.averageBitRate;
        rc.vbvBufferSize = rc.averageBitRate / o.fps; rc.vbvInitialDelay = rc.vbvBufferSize;
        rc.enableLookahead = 0; rc.lookaheadDepth = 0; rc.zeroReorderDelay = 1;
        rc.multiPass = o.multipass == "full" ? NV_ENC_TWO_PASS_FULL_RESOLUTION
            : o.multipass == "quarter" ? NV_ENC_TWO_PASS_QUARTER_RESOLUTION : NV_ENC_MULTI_PASS_DISABLED;
        auto& hevc = config.encodeCodecConfig.hevcConfig;
        hevc.pixelBitDepthMinus8 = 2; hevc.chromaFormatIDC = 1; hevc.idrPeriod = NVENC_INFINITE_GOPLENGTH;
        hevc.enableFillerDataInsertion = o.mode == "padded"; hevc.outputPictureTimingSEI = 0;
        NV_ENC_INITIALIZE_PARAMS init{}; init.version = NV_ENC_INITIALIZE_PARAMS_VER;
        init.encodeGUID = NV_ENC_CODEC_HEVC_GUID; init.presetGUID = NV_ENC_PRESET_P2_GUID;
        init.tuningInfo = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY; init.encodeConfig = &config; init.enablePTD = 1;
        init.encodeWidth = init.maxEncodeWidth = init.darWidth = o.width;
        init.encodeHeight = init.maxEncodeHeight = init.darHeight = o.height;
        init.frameRateNum = o.fps; init.frameRateDen = 1;
        e.check(e.api.nvEncInitializeEncoder(e.session, &init), "initialize");
        for (unsigned index = 0; index < e.textures.size(); ++index) {
            std::vector<uint16_t> pixels(size_t(o.width) * o.height * 3 / 2, uint16_t(512 << 6));
            if (o.scene == "alternating") {
                uint32_t state = 0x3A281517u + index;
                for (size_t p = 0; p < size_t(o.width) * o.height; ++p) {
                    state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                    pixels[p] = static_cast<uint16_t>((64 + state % 877) << 6);
                }
            }
            D3D11_TEXTURE2D_DESC desc{}; desc.Width = o.width; desc.Height = o.height;
            desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1; desc.Format = DXGI_FORMAT_P010;
            desc.Usage = D3D11_USAGE_DEFAULT;
            D3D11_SUBRESOURCE_DATA data{}; data.pSysMem = pixels.data(); data.SysMemPitch = o.width * 2;
            hr(e.device->CreateTexture2D(&desc, &data, &e.textures[index]));
            NV_ENC_REGISTER_RESOURCE resource{}; resource.version = NV_ENC_REGISTER_RESOURCE_VER;
            resource.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX; resource.resourceToRegister = e.textures[index].Get();
            resource.width = o.width; resource.height = o.height; resource.bufferFormat = NV_ENC_BUFFER_FORMAT_YUV420_10BIT;
            resource.bufferUsage = NV_ENC_INPUT_IMAGE;
            e.check(e.api.nvEncRegisterResource(e.session, &resource), "register"); e.registered[index] = resource.registeredResource;
        }
        NV_ENC_CREATE_BITSTREAM_BUFFER buffer{}; buffer.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
        e.check(e.api.nvEncCreateBitstreamBuffer(e.session, &buffer), "output"); e.output = buffer.bitstreamBuffer;
        std::ofstream csv, output;
        if (!o.csv.empty()) { csv.open(o.csv); if (!csv) throw std::runtime_error("Cannot open CSV"); csv << "frame,target_mbps,bytes,filler_bytes,encode_complete_ms,completion_seconds\n"; }
        if (!o.output.empty()) { output.open(o.output, std::ios::binary); if (!output) throw std::runtime_error("Cannot open bitstream"); }
        uint64_t total = 0, filler = 0; std::vector<double> timing;
        FrameClock frameClock(o.pace);
        const auto begin = Clock::now();
        for (uint32_t frame = 0; frame < o.frames; ++frame) {
            frameClock.wait(begin + std::chrono::microseconds(uint64_t(frame) * 1000000 / o.fps));
            if (o.stepMbps && frame == o.stepAt) {
                rc.averageBitRate = rc.maxBitRate = o.stepMbps * 1000000;
                rc.vbvBufferSize = rc.vbvInitialDelay = rc.averageBitRate / o.fps;
                NV_ENC_RECONFIGURE_PARAMS change{}; change.version = NV_ENC_RECONFIGURE_PARAMS_VER; change.reInitEncodeParams = init;
                change.resetEncoder = change.forceIDR = o.resetAtStep;
                e.check(e.api.nvEncReconfigureEncoder(e.session, &change), "rate step");
            }
            const auto start = Clock::now();
            NV_ENC_MAP_INPUT_RESOURCE mapped{}; mapped.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
            mapped.registeredResource = e.registered[o.scene == "flat" ? 0 : frame % 2];
            e.check(e.api.nvEncMapInputResource(e.session, &mapped), "map"); e.mapped = mapped.mappedResource;
            NV_ENC_PIC_PARAMS picture{}; picture.version = NV_ENC_PIC_PARAMS_VER;
            picture.inputWidth = o.width; picture.inputHeight = o.height; picture.inputBuffer = e.mapped;
            picture.bufferFmt = mapped.mappedBufferFmt; picture.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
            picture.outputBitstream = e.output; picture.frameIdx = frame; picture.inputTimeStamp = frame;
            if (frame == 0) picture.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
            e.check(e.api.nvEncEncodePicture(e.session, &picture), "encode");
            NV_ENC_LOCK_BITSTREAM lock{}; lock.version = NV_ENC_LOCK_BITSTREAM_VER; lock.outputBitstream = e.output;
            e.check(e.api.nvEncLockBitstream(e.session, &lock), "lock"); e.locked = true;
            const auto completed = Clock::now();
            if (lock.outputTimeStamp != frame) throw std::runtime_error("Output timestamp mismatch");
            const auto padding = fillerBytes(static_cast<const uint8_t*>(lock.bitstreamBufferPtr), lock.bitstreamSizeInBytes);
            const auto milliseconds = std::chrono::duration<double, std::milli>(completed - start).count();
            total += lock.bitstreamSizeInBytes; filler += padding; timing.push_back(milliseconds);
            if (csv) csv << frame << ',' << rc.averageBitRate / 1000000 << ',' << lock.bitstreamSizeInBytes << ',' << padding << ',' << milliseconds << ',' << std::chrono::duration<double>(completed - begin).count() << '\n';
            // Bound independent decoder-validation output to 60 frames.
            if (output && frame < 60) output.write(static_cast<const char*>(lock.bitstreamBufferPtr), lock.bitstreamSizeInBytes);
            e.check(e.api.nvEncUnlockBitstream(e.session, e.output), "unlock"); e.locked = false;
            e.check(e.api.nvEncUnmapInputResource(e.session, e.mapped), "unmap"); e.mapped = nullptr;
        }
        const auto seconds = std::chrono::duration<double>(Clock::now() - begin).count();
        if (csv.is_open()) csv.flush();
        if (output.is_open()) output.flush();
        if ((csv.is_open() && !csv) || (output.is_open() && !output)) throw std::runtime_error("Output write failed");
        std::sort(timing.begin(), timing.end());
        std::cout << "{\"lab_version\":2,\"codec\":\"HEVC10\",\"api\":\"11.1\",\"pacer\":\"" << (o.pace ? "high_resolution_timer" : "unpaced") << "\",\"mode\":\"" << o.mode << "\",\"scene\":\"" << o.scene
            << "\",\"multipass\":\"" << o.multipass << "\",\"width\":" << o.width << ",\"height\":" << o.height
            << ",\"target_mbps\":" << o.mbps << ",\"step_mbps\":" << o.stepMbps << ",\"frames\":" << o.frames << ",\"nominal_fps\":" << o.fps
            << ",\"paced\":" << (o.pace ? "true" : "false") << ",\"dynamic_rc\":" << dynamicRc << ",\"supported_rc_mask\":" << supportedRc
            << ",\"dynamic_bitrate\":" << dynamicBitrate << ",\"reset_at_step\":" << (o.resetAtStep ? "true" : "false")
            << ",\"accepted_rc_mode\":" << rc.rateControlMode << ",\"accepted_multipass\":" << rc.multiPass
            << ",\"accepted_average_bps\":" << rc.averageBitRate << ",\"accepted_maximum_bps\":" << rc.maxBitRate
            << ",\"vbv_bits\":" << rc.vbvBufferSize << ",\"min_qp_enabled\":" << rc.enableMinQP << ",\"max_qp_enabled\":" << rc.enableMaxQP
            << ",\"spatial_aq\":" << rc.enableAQ << ",\"temporal_aq\":" << rc.enableTemporalAQ
            << ",\"total_bytes\":" << total << ",\"filler_bytes\":" << filler << ",\"media_mbps\":" << total * 8.0 * o.fps / o.frames / 1e6
            << ",\"nonfiller_media_mbps\":" << (total - filler) * 8.0 * o.fps / o.frames / 1e6
            << ",\"wall_seconds\":" << seconds << ",\"wall_fps\":" << o.frames / seconds
            << ",\"encode_complete_p50_ms\":" << timing[timing.size() / 2] << ",\"encode_complete_p99_ms\":" << timing[(timing.size() - 1) * 99 / 100] << "}\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
