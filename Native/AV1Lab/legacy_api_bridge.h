#pragma once
#include "av1_backend.h"
#ifdef __cplusplus
extern "C" {
#endif
// Isolated SDK 11.1 -> 12.1 function table. Not an installed/injected hook.
// Fixed paired trial: HEVC-facing queries, AV1 Main 8-bit/P2/120-fps output.
AV1_API int AV1_CALL Av1CreateLegacyFunctionTable(void* sdk11_table, uint32_t bytes,
                                                uint32_t average_bps, uint32_t peak_bps);
// Explicit lab target; immutable while any encoder session is open. No filler.
AV1_API int AV1_CALL Av1ConfigureLegacyBudget(uint32_t average_bps, uint32_t peak_bps,
                                            uint32_t fixed_target);
AV1_API int AV1_CALL Av1ConfigureLegacyRate(uint32_t fps);
AV1_API int AV1_CALL Av1ConfigureLegacyCodec(uint32_t codec); // 1 AV1, 2 H.264
typedef struct Av1TrialConfig {
 uint32_t size,version,average_bps,peak_bps,fixed_target,fps,codec;
} Av1TrialConfig;
typedef struct Av1TrialIdentity { uint32_t size,version,codec,fps,average_bps,peak_bps; } Av1TrialIdentity;
AV1_API int AV1_CALL Av1GetLegacyIdentity(Av1TrialIdentity*);
typedef struct Av1BridgeStats {
 uint32_t size,version;
 uint64_t tables,opens,initializes,encodes,locks,bytes,errors,reconfigures;
 uint32_t width,height,average_bps,peak_bps,last_status;
 uint32_t initialize_attempts,initialize_status,source_fps_num,source_fps_den,source_async;
 char initialize_error[192];
} Av1BridgeStats;
AV1_API int AV1_CALL Av1GetLegacyBridgeStats(Av1BridgeStats*);
#ifdef __cplusplus
}
#endif
