// Isolated fake public APIs: no GPU, SteamVR, video payload or injection.
#include "../encoder_probe.cpp"
NV_ENC_CONFIG observed{}, fixture{};
NV_ENC_INITIALIZE_PARAMS observedParams{};
unsigned calls{}; bool rejectCq{}, rejectVbv{};
NVENCSTATUS NVENCAPI fakeInitializeAdvanced(void*, NV_ENC_INITIALIZE_PARAMS* params) {
    ++calls; observed = *params->encodeConfig; observedParams = *params;
    return rejectCq && observed.rcParams.targetQuality == 27 ? NV_ENC_ERR_UNSUPPORTED_PARAM : NV_ENC_SUCCESS;
}
NVENCSTATUS NVENCAPI fakeReconfigureAdvanced(void* handle, NV_ENC_RECONFIGURE_PARAMS* params) { return fakeInitializeAdvanced(handle, &params->reInitEncodeParams); }
NVENCSTATUS NVENCAPI fakeCaps(void*, GUID, NV_ENC_CAPS_PARAM* parameter, int* value) {
    *value = rejectVbv && parameter->capsToQuery == NV_ENC_CAPS_SUPPORT_CUSTOM_VBV_BUF_SIZE ? 0
        : parameter->capsToQuery == NV_ENC_CAPS_SUPPORTED_RATECONTROL_MODES ? 7 : 1; return NV_ENC_SUCCESS;
}
NVENCSTATUS NVENCAPI fakePreset(void*, GUID, GUID preset, NV_ENC_TUNING_INFO, NV_ENC_PRESET_CONFIG* output) {
    output->presetCfg = fixture;
    output->presetCfg.rcParams.constQP.qpInterP = IsEqualGUID(preset, NV_ENC_PRESET_P4_GUID) ? 14 : 10;
    return NV_ENC_SUCCESS;
}
NVENCSTATUS NVENCAPI fakeDestroyAdvanced(void*) { return NV_ENC_SUCCESS; }
int main() {
    originalInitialize = fakeInitializeAdvanced; originalReconfigure = fakeReconfigureAdvanced;
    originalPreset = fakePreset; getCaps = fakeCaps; originalDestroy = fakeDestroyAdvanced;
    driverImage.base = reinterpret_cast<uintptr_t>(&main); driverImage.size = 65536;
    driverImage.code.push_back({0, std::vector<unsigned char>(65536)}); installed.store(true);
    fixture.version = NV_ENC_CONFIG_VER; fixture.profileGUID = NV_ENC_HEVC_PROFILE_MAIN10_GUID;
    fixture.gopLength = 120; fixture.frameIntervalP = 1;
    fixture.rcParams.rateControlMode = NV_ENC_PARAMS_RC_VBR; fixture.rcParams.averageBitRate = 200000000;
    fixture.rcParams.maxBitRate = 230000000; fixture.rcParams.vbvBufferSize = 2000000; fixture.rcParams.qpMapMode = NV_ENC_QP_MAP_DELTA;
    NV_ENC_CONFIG config = fixture;
    NV_ENC_INITIALIZE_PARAMS params{}; params.version = NV_ENC_INITIALIZE_PARAMS_VER; params.encodeConfig = &config;
    params.encodeGUID = NV_ENC_CODEC_HEVC_GUID; params.presetGUID = NV_ENC_PRESET_P2_GUID;
    params.frameRateNum = 120; params.frameRateDen = 1; params.encodeWidth = 1536; params.encodeHeight = 6144;
    params.tuningInfo = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
    const auto originalParams = params; const auto originalConfig = config; auto handle = reinterpret_cast<void*>(123);
    hookInitialize(handle, &params);
    AdvancedEncoding value{}; value.flags = AdvancedCq | AdvancedMaxQp | AdvancedPeak | AdvancedVbv | AdvancedDelay;
    value.cq256 = 26 * 256 + 64; value.maxQp = 40; value.peakBitrate = 225000000; value.vbvBits = 3000000; value.vbvDelayBits = 1000000;
    if (SetAdvancedEncoding(&value)) return 1;
    NV_ENC_RECONFIGURE_PARAMS reconfigure{}; reconfigure.version = NV_ENC_RECONFIGURE_PARAMS_VER; reconfigure.reInitEncodeParams = params;
    const auto originalReconfigureParams = reconfigure;
    if (hookReconfigure(handle, &reconfigure) || observed.rcParams.targetQuality != 26 || observed.rcParams.targetQualityLSB != 64 ||
        !observed.rcParams.enableMaxQP || observed.rcParams.maxQP.qpIntra != 40 || observed.rcParams.maxBitRate != 225000000 ||
        observed.rcParams.vbvBufferSize != 3000000 || observed.rcParams.vbvInitialDelay != 1000000 || stats.appliedAdvancedFlags != value.flags) return 2;
    if (memcmp(&params, &originalParams, sizeof(params)) || memcmp(&config, &originalConfig, sizeof(config)) ||
        memcmp(&reconfigure, &originalReconfigureParams, sizeof(reconfigure)) || observed.gopLength != 120 ||
        observedParams.frameRateNum != 120 || observed.rcParams.qpMapMode != NV_ENC_QP_MAP_DELTA) return 3;
    value.cq256 = 27 * 256; rejectCq = true; SetAdvancedEncoding(&value); const auto before = calls;
    if (hookReconfigure(handle, &reconfigure) || calls != before + 2 || stats.rejectedAdvancedFlags != value.flags ||
        stats.appliedAdvancedFlags || stats.lastAdvancedError != NV_ENC_ERR_UNSUPPORTED_PARAM || memcmp(&observed, &config, sizeof(config))) return 4;
    hookReconfigure(handle, &reconfigure); if (calls != before + 3) return 5;
    rejectCq = false;
    auto invalid = value; invalid.flags = 1024; if (SetAdvancedEncoding(&invalid) != 1015) return 6;
    invalid = value; invalid.vbvDelayBits = invalid.vbvBits + 1; if (SetAdvancedEncoding(&invalid) != 1015) return 7;
    invalid = value; invalid.cq256 = 51 * 256 + 1; if (SetAdvancedEncoding(&invalid) != 1015) return 8;
    value = {}; value.flags = AdvancedChroma | AdvancedSpatialAq | AdvancedTemporalAq | AdvancedPreset;
    value.cbQpOffset = -2; value.crQpOffset = -3; value.spatialAq = 1; value.aqStrength = 5; value.temporalAq = 1; value.preset = 4;
    SetAdvancedEncoding(&value); hookReconfigure(handle, &reconfigure);
    if (stats.appliedAdvancedFlags || stats.pendingAdvancedFlags != value.flags || observed.rcParams.enableAQ) return 9;
    hookDestroy(handle);
    NV_ENC_PRESET_CONFIG preset{}; preset.version = NV_ENC_PRESET_CONFIG_VER; preset.presetCfg.version = NV_ENC_CONFIG_VER;
    if (hookPreset(handle, params.encodeGUID, params.presetGUID, NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY, &preset)) return 10;
    config.rcParams.constQP.qpInterP = preset.presetCfg.rcParams.constQP.qpInterP;
    if (config.rcParams.constQP.qpInterP != 10) return 11;
    if (hookInitialize(handle, &params) || observed.rcParams.constQP.qpInterP != 14 ||
        !IsEqualGUID(observedParams.presetGUID, NV_ENC_PRESET_P4_GUID) || stats.appliedAdvancedFlags != value.flags || stats.pendingAdvancedFlags ||
        !observed.rcParams.enableAQ || !observed.rcParams.enableTemporalAQ || observed.rcParams.aqStrength != 5 ||
        observed.rcParams.cbQPIndexOffset != -2 || observed.rcParams.crQPIndexOffset != -3) return 12;
    hookReconfigure(handle, &reconfigure);
    if (observed.rcParams.cbQPIndexOffset != -2 || !observed.rcParams.enableAQ || !IsEqualGUID(observedParams.presetGUID, NV_ENC_PRESET_P4_GUID)) return 13;
    auto nextPreset = value; nextPreset.preset = 3; SetAdvancedEncoding(&nextPreset);
    hookPreset(handle, params.encodeGUID, params.presetGUID, params.tuningInfo, &preset);
    hookReconfigure(handle, &reconfigure);
    if (!IsEqualGUID(observedParams.presetGUID, NV_ENC_PRESET_P4_GUID) || observed.rcParams.constQP.qpInterP != 14) return 22;
    AdvancedEncoding restore{}; SetAdvancedEncoding(&restore); hookReconfigure(handle, &reconfigure);
    if (stats.pendingAdvancedFlags != value.flags || stats.appliedAdvancedFlags != value.flags || StopProbe(nullptr) != 1014 || !installed) return 14;
    hookDestroy(handle); hookInitialize(handle, &params);
    if (stats.appliedAdvancedFlags || stats.pendingAdvancedFlags || sessions[handle].customPolicy) return 15;
    value = {}; value.flags = AdvancedPeak; value.peakBitrate = 150000000; auto copy = config;
    if (!ApplyAdvancedEncoding(value, copy) || copy.rcParams.averageBitRate != 150000000 || copy.rcParams.maxBitRate != 150000000 ||
        config.rcParams.averageBitRate != 200000000) return 16;
    value = {}; value.flags = AdvancedCq; copy.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    if (ApplyAdvancedEncoding(value, copy)) return 17;
    value = {}; value.flags = AdvancedMaxQp; value.maxQp = 12; copy = config; copy.rcParams.enableMinQP = 1; copy.rcParams.minQP.qpIntra = 20;
    if (ApplyAdvancedEncoding(value, copy)) return 18;
    SetAdvancedEncoding(&value); config.version = 0; hookInitialize(handle, &params);
    if (observed.version || stats.rejectedPolicyCalls != 1) return 19;
    driverImage.code.clear();
    { std::lock_guard<std::mutex> outer(controlMutex); hookInitialize(handle, &params); hookReconfigure(handle, &reconfigure); }
    if (!stats.reconfigureCalls) return 20;
    // Whole fields preserve Valve's changed scalar instead of creating a byte-wise hybrid.
    auto baseline = fixture, desired = fixture, merged = fixture;
    baseline.rcParams.constQP.qpInterP = 0x100; desired.rcParams.constQP.qpInterP = 0x201; merged.rcParams.constQP.qpInterP = 0x120;
    desired.frameFieldMode = NV_ENC_PARAMS_FRAME_FIELD_MODE_FIELD; desired.rcParams.enableLookahead = 1;
    const auto preserved = merged; MergeAdvancedPreset(baseline, desired, merged);
    if (memcmp(&merged, &preserved, sizeof(merged))) return 21;
    driverImage.code.push_back({0, std::vector<unsigned char>(65536)}); config.version = NV_ENC_CONFIG_VER;
    auto other = reinterpret_cast<void*>(789); rejectVbv = true;
    value = {}; value.flags = AdvancedCq | AdvancedVbv; value.cq256 = 30 * 256; value.vbvBits = 3000000; SetAdvancedEncoding(&value);
    if (hookInitialize(other, &params) || stats.rejectedAdvancedFlags != AdvancedVbv || stats.appliedAdvancedFlags != AdvancedCq ||
        observed.rcParams.targetQuality != 30 || observed.rcParams.vbvBufferSize != config.rcParams.vbvBufferSize) return 23;
    // Conflicts are dropped per field: CQ cannot apply under CBR, but max QP still does.
    rejectVbv = false; auto third = reinterpret_cast<void*>(456);
    value = {}; value.flags = AdvancedCq | AdvancedMaxQp; value.cq256 = 30 * 256; value.maxQp = 38; SetAdvancedEncoding(&value);
    SetEncodingPolicy(reinterpret_cast<void*>(2));
    if (hookInitialize(third, &params) || stats.rejectedAdvancedFlags != AdvancedCq || stats.appliedAdvancedFlags != AdvancedMaxQp ||
        observed.rcParams.rateControlMode != NV_ENC_PARAMS_RC_CBR || !observed.rcParams.enableMaxQP || observed.rcParams.maxQP.qpIntra != 38) return 24;
    SetEncodingPolicy(nullptr);
    // A VBV-only request clamps Valve's larger initial delay instead of rejecting every field.
    copy = config; copy.rcParams.vbvInitialDelay = 2000000; value = {}; value.flags = AdvancedVbv; value.vbvBits = 1000000;
    if (!ApplyAdvancedEncoding(value, copy) || copy.rcParams.vbvBufferSize != 1000000 || copy.rcParams.vbvInitialDelay != 1000000) return 25;
    value = {}; value.flags = AdvancedDelay; value.vbvDelayBits = 3000000; copy = config;
    if (AdvancedConflicts(value, copy) != AdvancedDelay || ApplyAdvancedEncoding(value, copy)) return 26;
    printf("PASS: advanced ABI/ranges, copied fields, rejection fallback/blocking, consistent presets, reconnect/restoration, min-QP/RC guards and telemetry.\n");
    return 0;
}
