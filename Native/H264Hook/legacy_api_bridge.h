#pragma once
#include "hook_api.h"
#ifdef __cplusplus
extern "C" {
#endif
// Isolated SDK 11.1 -> 12.1 function table. Not an installed/injected hook.
// Paired H.264 output: HEVC-facing queries from SteamVR, H.264 High from NVENC (VBR or CBR, preset P1-P7).
HOOK_API int HOOK_CALL HookCreateLegacyFunctionTable(void* sdk11_table, uint32_t bytes,
                                                uint32_t average_bps, uint32_t peak_bps);
// Explicit lab target; immutable while any encoder session is open. No filler.
HOOK_API int HOOK_CALL HookConfigureLegacyBudget(uint32_t average_bps, uint32_t peak_bps,
                                            uint32_t fixed_target);
HOOK_API int HOOK_CALL HookConfigureLegacyRate(uint32_t fps);
// Experiment: rate control (0 VBR, 1 CBR), NVENC preset (1 = P1 ... 7 = P7) and VBV buffer size in frames of the
// average bitrate (1..16). Immutable while a session is open.
HOOK_API int HOOK_CALL HookConfigureLegacyEncoder(uint32_t rate_control, uint32_t preset, uint32_t vbv_frames);
// Same options as HookConfigureLegacyEncoder, but allowed while a session is open: they take effect when the next
// session opens (SteamVR rebuilds the encoder on a stream reset).
HOOK_API int HOOK_CALL HookQueueLegacyEncoder(uint32_t rate_control, uint32_t preset, uint32_t vbv_frames);
typedef struct HookTrialConfig {
 uint32_t size,version,average_bps,peak_bps,fixed_target,fps,rate_control,preset,vbv_frames;
} HookTrialConfig;
typedef struct HookTrialIdentity { uint32_t size,version,codec,fps,average_bps,peak_bps,rate_control,preset,vbv_frames; } HookTrialIdentity;
HOOK_API int HOOK_CALL HookGetLegacyIdentity(HookTrialIdentity*);
// Live codec switching: with the driver's own SDK 11.1 API provided, every session also opens a stock HEVC encoder beside
// the H.264 one, and the mode picks which of the two encodes each frame.
typedef struct HookSwitchInfo {
 uint32_t size,version,mode,sessions,stock_sessions;
 uint32_t last_stock_status;
 uint64_t switches,stock_encodes,stock_locks,stock_errors;
} HookSwitchInfo;
HOOK_API int HOOK_CALL HookProvideStockTable(const void* sdk11_table,uint32_t bytes,void* factory);
HOOK_API int HOOK_CALL HookSetLegacyMode(uint32_t mode);
HOOK_API int HOOK_CALL HookGetLegacySwitchInfo(HookSwitchInfo*);
typedef struct HookBridgeStats {
 uint32_t size,version;
 uint64_t tables,opens,initializes,encodes,locks,bytes,errors,reconfigures;
 uint32_t width,height,average_bps,peak_bps,last_status;
 uint32_t initialize_attempts,initialize_status,source_fps_num,source_fps_den,source_async;
 char initialize_error[192];
} HookBridgeStats;
HOOK_API int HOOK_CALL HookGetLegacyBridgeStats(HookBridgeStats*);
#ifdef __cplusplus
}
#endif
