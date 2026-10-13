#pragma once
#include "nvEncodeAPI11.h"

// Public API 11.1 only. This helper does not modify presets, GOP, lookahead,
// reference frames, frame rate, VBV, or a caller-owned parameter structure.
inline bool EncodingPolicyValid(unsigned policy) {
    return !(policy & ~0xfu) && (policy & 3u) <= 2u;
}
inline bool EncodingParametersValid(const NV_ENC_INITIALIZE_PARAMS* params) {
    return params && params->version == NV_ENC_INITIALIZE_PARAMS_VER && params->encodeConfig &&
        params->encodeConfig->version == NV_ENC_CONFIG_VER &&
        (IsEqualGUID(params->encodeGUID, NV_ENC_CODEC_HEVC_GUID) || IsEqualGUID(params->encodeGUID, NV_ENC_CODEC_H264_GUID));
}
inline void ApplyEncodingPolicy(unsigned policy, const NV_ENC_INITIALIZE_PARAMS& params, NV_ENC_CONFIG& config) {
    switch (policy & 3u) {
    case 1: config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_VBR; break;
    case 2:
        config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
        if (IsEqualGUID(params.encodeGUID, NV_ENC_CODEC_HEVC_GUID)) {
            config.encodeCodecConfig.hevcConfig.enableFillerDataInsertion = 0;
            // Picture-timing SEI can independently request HRD padding.
            config.encodeCodecConfig.hevcConfig.outputPictureTimingSEI = 0;
        } else {
            config.encodeCodecConfig.h264Config.enableFillerDataInsertion = 0;
            config.encodeCodecConfig.h264Config.outputPictureTimingSEI = 0;
        }
        break;
    }
    switch ((policy >> 2) & 3u) {
    case 1: config.rcParams.multiPass = NV_ENC_MULTI_PASS_DISABLED; break;
    case 2: config.rcParams.multiPass = NV_ENC_TWO_PASS_QUARTER_RESOLUTION; break;
    case 3: config.rcParams.multiPass = NV_ENC_TWO_PASS_FULL_RESOLUTION; break;
    }
}
