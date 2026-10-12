#define AV1_BACKEND_BUILD
#include <windows.h>
#include <cstring>
#include "nvEncodeAPI11.h"
#include "legacy_picture.h"

static_assert(sizeof(NV_ENC_PIC_PARAMS)==3344,"Review legacy picture layout");
static_assert(offsetof(NV_ENC_PIC_PARAMS,qpDeltaMap)==1696,"Review legacy map offset");
extern "C" int AV1_CALL Av1ReadLegacyPicture(const void* raw,uint32_t bytes,Av1LegacyPicture* out) {
    if(!raw || bytes!=sizeof(NV_ENC_PIC_PARAMS) || !out ||
        out->size!=sizeof(Av1LegacyPicture) || out->version!=1)return -1;
    NV_ENC_PIC_PARAMS p{};memcpy(&p,raw,sizeof(p));
    if(p.version!=NV_ENC_PIC_PARAMS_VER)return -2;
    Av1LegacyPicture v{};v.size=sizeof(v);v.version=1;
    v.width=p.inputWidth;v.height=p.inputHeight;v.pitch=p.inputPitch;
    v.flags=p.encodePicFlags;v.frame_index=p.frameIdx;
    v.buffer_format=p.bufferFmt;v.picture_structure=p.pictureStruct;
    v.timestamp=p.inputTimeStamp;v.duration=p.inputDuration;
    v.input=p.inputBuffer;v.output=p.outputBitstream;v.completion_event=p.completionEvent;
    v.hevc_delta_map=p.qpDeltaMap;v.hevc_map_entries=p.qpDeltaMapSize;
    *out=v;return 0;
}
