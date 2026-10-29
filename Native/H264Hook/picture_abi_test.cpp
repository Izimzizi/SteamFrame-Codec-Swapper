#include <windows.h>
#include <array>
#include <cstring>
#include <iostream>
#include "nvEncodeAPI11.h"
#include "legacy_picture.h"
int main() {
    struct Guarded {std::array<uint8_t,64> before;NV_ENC_PIC_PARAMS p;std::array<uint8_t,64> after;};
    Guarded g{};memset(&g,0xa7,sizeof(g));memset(&g.p,0,sizeof(g.p));
    int8_t map[4]={-8,8,-8,8};g.p.version=NV_ENC_PIC_PARAMS_VER;
    g.p.inputWidth=1536;g.p.inputHeight=6144;g.p.inputPitch=2048;g.p.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR;
    g.p.frameIdx=19;g.p.inputTimeStamp=123456789;g.p.inputDuration=8333;
    g.p.inputBuffer=reinterpret_cast<void*>(uintptr_t(0x12340000));g.p.outputBitstream=reinterpret_cast<void*>(uintptr_t(0x45670000));
    g.p.completionEvent=reinterpret_cast<void*>(uintptr_t(0x789a0000));
    g.p.bufferFmt=NV_ENC_BUFFER_FORMAT_NV12;g.p.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;
    g.p.qpDeltaMap=map;g.p.qpDeltaMapSize=4;
    Guarded original=g;HookLegacyPicture view{sizeof(HookLegacyPicture),1};
    if(HookReadLegacyPicture(&g.p,sizeof(g.p),&view) || memcmp(&g,&original,sizeof(g)) ||
        view.width!=1536 || view.height!=6144 || view.pitch!=2048 || view.frame_index!=19 ||
        view.flags!=NV_ENC_PIC_FLAG_FORCEIDR || view.timestamp!=123456789 || view.duration!=8333 ||
        view.input!=g.p.inputBuffer || view.output!=g.p.outputBitstream || view.completion_event!=g.p.completionEvent ||
        view.hevc_delta_map!=map || view.hevc_map_entries!=4)return 1;
    if(HookReadLegacyPicture(&g.p,sizeof(g.p)-1,&view)==0)return 2;
    g.p.version^=1;
    if(HookReadLegacyPicture(&g.p,sizeof(g.p),&view)==0)return 3;
    std::cout<<"{\"legacy_sdk\":\"11.1\",\"field_translation\":true,\"caller_preserved\":true,\"canaries_preserved\":true,\"unknown_version_rejected\":true,\"hevc_map_offset\":1696}\n";
}
