#pragma once
#include "av1_backend.h"

// A field-by-field snapshot of the SDK 11.1 caller, independent of SDK layout.
// QP maps are NOT codec translated by this function.
typedef struct Av1LegacyPicture {
    uint32_t size,version,width,height,pitch,flags,frame_index,buffer_format,picture_structure;
    uint64_t timestamp,duration;
    void* input;
    void* output;
    void* completion_event;
    const int8_t* hevc_delta_map;
    uint32_t hevc_map_entries;
} Av1LegacyPicture;
#ifdef __cplusplus
extern "C" {
#endif
AV1_API int AV1_CALL Av1ReadLegacyPicture(const void* sdk11_picture,uint32_t bytes,Av1LegacyPicture*);
#ifdef __cplusplus
}
#endif
