#pragma once
#include "hook_api.h"

// A field-by-field snapshot of the SDK 11.1 caller, independent of SDK layout.
// QP maps are NOT codec translated by this function.
typedef struct HookLegacyPicture {
    uint32_t size,version,width,height,pitch,flags,frame_index,buffer_format,picture_structure;
    uint64_t timestamp,duration;
    void* input;
    void* output;
    void* completion_event;
    const int8_t* hevc_delta_map;
    uint32_t hevc_map_entries;
} HookLegacyPicture;
#ifdef __cplusplus
extern "C" {
#endif
HOOK_API int HOOK_CALL HookReadLegacyPicture(const void* sdk11_picture,uint32_t bytes,HookLegacyPicture*);
#ifdef __cplusplus
}
#endif
