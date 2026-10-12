#pragma once
#include <stdint.h>

// Experimental, isolated backend. Not loaded by the production SteamVR helper.
#ifdef _WIN32
#define AV1_CALL __cdecl
#ifdef AV1_BACKEND_BUILD
#define AV1_API __declspec(dllexport)
#else
#define AV1_API __declspec(dllimport)
#endif
#else
#define AV1_CALL
#define AV1_API
#endif
#ifdef __cplusplus
extern "C" {
#endif

typedef struct Av1Session Av1Session;
typedef struct Av1Config {
    uint32_t size, version;
    uint32_t width, height, fps, bit_depth;
    uint32_t average_bps, peak_bps, keyframe_interval;
    uint32_t enable_delta_map;
    uint32_t input_bit_depth;
    uint32_t level_index; // 0..17, or 24 = NVENC AUTOSELECT (inspect emitted header).
    uint32_t tile_columns, tile_rows; // Version 2: 0=auto; 1,2,4,8=uniform tiles.
} Av1Config;

typedef struct Av1Packet {
    uint32_t size, version;
    const uint8_t* data;       // Owned by session, valid until its next operation.
    uint32_t bytes, keyframe;
    uint64_t frame_id, timestamp_us;
    double encode_ms;
} Av1Packet;

typedef struct Av1Stats {
    uint32_t size, version;
    uint64_t submitted, completed, encoded_bytes, errors;
    uint32_t width, height, fps, bit_depth, average_bps, peak_bps, map_entries;
} Av1Stats;

// Synchronous, no B frames, lookahead, AQ or padding. 120 fps is required.
// Device/texture must be an ID3D11Device/ID3D11Texture2D on the same adapter.
// Input: 8-bit BGRA/NV12 or 10-bit P010, one mip/array layer/sample.
// The session retains up to 16 input textures until destruction.
AV1_API int AV1_CALL Av1Create(void* d3d_device, const Av1Config*, Av1Session**);
AV1_API int AV1_CALL Av1Encode(Av1Session*, void* texture, uint64_t frame_id,
    uint64_t timestamp_us, const int8_t* delta_map, uint32_t map_entries,
    uint32_t force_keyframe, Av1Packet*);
AV1_API int AV1_CALL Av1SetRate(Av1Session*, uint32_t average_bps, uint32_t peak_bps);
AV1_API int AV1_CALL Av1GetStats(Av1Session*, Av1Stats*);
AV1_API const char* AV1_CALL Av1LastError(void); // Current calling thread.
AV1_API void AV1_CALL Av1Destroy(Av1Session*);

#ifdef __cplusplus
}
#endif
