// Fake synchronous NVENC APIs only. Never creates a GPU session or opens SteamVR.
#include "../encoder_probe.cpp"
NVENCSTATUS policyResult = NV_ENC_SUCCESS;
NV_ENC_INITIALIZE_PARAMS seenParams{};
NV_ENC_CONFIG seenConfig{};
void* seenPointer{};
NV_ENC_CONFIG* seenConfigPointer{};
NVENCSTATUS NVENCAPI fakeInitialize(void*, NV_ENC_INITIALIZE_PARAMS* params) {
    seenPointer = params;
    if (params && params->encodeConfig) { seenParams = *params; seenConfig = *params->encodeConfig; seenConfigPointer = params->encodeConfig; }
    return policyResult;
}
NVENCSTATUS NVENCAPI fakeReconfigure(void* handle, NV_ENC_RECONFIGURE_PARAMS* params) {
    auto result = fakeInitialize(handle, params ? &params->reInitEncodeParams : nullptr);
    seenPointer = params; return result;
}
NVENCSTATUS NVENCAPI fakePolicyCaps(void*, GUID, NV_ENC_CAPS_PARAM*, int* value) { *value = 7; return NV_ENC_SUCCESS; }
int main() {
    originalInitialize = fakeInitialize; originalReconfigure = fakeReconfigure;
    getCaps = fakePolicyCaps;
    driverImage.base = reinterpret_cast<uintptr_t>(&main); driverImage.size = 65536;
    driverImage.code.push_back({0, std::vector<unsigned char>(65536)});
    installed.store(true);
    NV_ENC_CONFIG config{}; config.version = NV_ENC_CONFIG_VER;
    config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_VBR; config.rcParams.multiPass = NV_ENC_MULTI_PASS_DISABLED;
    config.rcParams.averageBitRate = 200000000; config.rcParams.maxBitRate = 200000000;
    config.rcParams.vbvBufferSize = 1666667; config.rcParams.vbvInitialDelay = 1666667;
    config.gopLength = 120; config.frameIntervalP = 1; config.rcParams.lookaheadDepth = 0;
    config.encodeCodecConfig.hevcConfig.enableFillerDataInsertion = 1;
    config.encodeCodecConfig.hevcConfig.outputPictureTimingSEI = 1;
    NV_ENC_INITIALIZE_PARAMS params{}; params.version = NV_ENC_INITIALIZE_PARAMS_VER;
    params.encodeConfig = &config; params.encodeGUID = NV_ENC_CODEC_HEVC_GUID; params.presetGUID = NV_ENC_PRESET_P2_GUID;
    params.encodeWidth = 1536; params.encodeHeight = 3072; params.frameRateNum = 120; params.frameRateDen = 1;
    auto originalParams = params; auto originalConfig = config;
    auto handle = reinterpret_cast<void*>(123);
    hookInitialize(handle, &params);
    if (seenPointer != &params || !stats.policyObserved || stats.appliedPolicy || sessions[handle].customPolicy) return 1;
    if (SetEncodingPolicy(reinterpret_cast<void*>(10)) || encodingPolicy != 10) return 2; // CBR + quarter pass.
    hookInitialize(handle, &params);
    if (seenPointer == &params || seenConfigPointer == &config || stats.appliedPolicy != 10 || !sessions[handle].customPolicy) return 3;
    auto expected = config;
    expected.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    expected.rcParams.multiPass = NV_ENC_TWO_PASS_QUARTER_RESOLUTION;
    expected.encodeCodecConfig.hevcConfig.enableFillerDataInsertion = 0;
    expected.encodeCodecConfig.hevcConfig.outputPictureTimingSEI = 0;
    if (memcmp(&seenConfig, &expected, sizeof(expected)) || memcmp(&params, &originalParams, sizeof(params)) ||
        memcmp(&config, &originalConfig, sizeof(config)) || stats.filler || stats.frameRateNum != 120 || stats.averageBitrate != 200000000) return 4;
    NV_ENC_RECONFIGURE_PARAMS reconfigure{}; reconfigure.version = NV_ENC_RECONFIGURE_PARAMS_VER;
    reconfigure.reInitEncodeParams = params; reconfigure.resetEncoder = 0; reconfigure.forceIDR = 0;
    auto originalReconfigureParams = reconfigure;
    hookReconfigure(handle, &reconfigure);
    if (seenPointer == &reconfigure || memcmp(&reconfigure, &originalReconfigureParams, sizeof(reconfigure)) ||
        memcmp(&seenConfig, &expected, sizeof(expected))) return 5;
    // A rejected configuration never becomes an 'applied' readback.
    SetEncodingPolicy(reinterpret_cast<void*>(5)); policyResult = NV_ENC_ERR_GENERIC;
    hookReconfigure(handle, &reconfigure);
    if (stats.appliedPolicy != 10 || stats.policyErrors != 1) return 6;
    policyResult = NV_ENC_SUCCESS;
    if (StopProbe(nullptr) != 1014 || !installed || encodingPolicy != 0) return 7;
    hookReconfigure(handle, &reconfigure);
    if (seenPointer != &reconfigure || stats.appliedPolicy || sessions[handle].customPolicy ||
        memcmp(&seenConfig, &config, sizeof(config))) return 8;
    // Exact ABI versions and a verified caller are required. Unknown calls pass through unchanged.
    SetEncodingPolicy(reinterpret_cast<void*>(10)); config.version = 0;
    hookInitialize(handle, &params);
    if (seenPointer != &params || stats.rejectedPolicyCalls != 1 || stats.appliedPolicy) return 9;
    config.version = NV_ENC_CONFIG_VER; reconfigure.version = 0;
    hookReconfigure(handle, &reconfigure);
    if (seenPointer != &reconfigure || stats.rejectedPolicyCalls != 2) return 10;
    driverImage.code.clear();
    { std::lock_guard<std::mutex> outer(controlMutex); hookInitialize(handle, &params); hookReconfigure(handle, &reconfigure); }
    hookInitialize(handle, &params);
    if (seenPointer != &params || stats.rejectedPolicyCalls != 2 || stats.appliedPolicy) return 11;
    for (unsigned invalid : {3u, 7u, 12u, 16u}) if (SetEncodingPolicy(reinterpret_cast<void*>(static_cast<uintptr_t>(invalid))) != 1013) return 12;
    // No caller-owned structure or unrequested parameter changed in this test.
    printf("PASS: public API policy copy, exact ABI/caller guards, no-padding CBR, preserved frame/GOP/VBV, failed-call telemetry, restoration-before-stop.\n");
    return 0;
}
