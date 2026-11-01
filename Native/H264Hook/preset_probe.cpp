// Prints what the NVENC presets P1..P7 change in the encode configuration on this GPU, for H.264 and HEVC and both low-latency
// tunings. The preset GUID given to nvEncInitializeEncoder is overridden by an explicit encode configuration, so a preset only
// has an effect on a stream whose configuration carries these fields.
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include "nvEncodeAPI12.h"

int main(){
 using Microsoft::WRL::ComPtr;
 try{
  auto library=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
  if(!library)throw std::runtime_error("NVENC unavailable");
  using Create=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
  auto create=reinterpret_cast<Create>(GetProcAddress(library,"NvEncodeAPICreateInstance"));
  NV_ENCODE_API_FUNCTION_LIST api{};api.version=NV_ENCODE_API_FUNCTION_LIST_VER;
  if(!create||create(&api))throw std::runtime_error("NVENC API rejected");
  ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
  if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)))throw std::runtime_error("D3D11 unavailable");
  NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};open.version=NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
  open.device=device.Get();open.deviceType=NV_ENC_DEVICE_TYPE_DIRECTX;open.apiVersion=NVENCAPI_VERSION;
  void* encoder=nullptr;if(api.nvEncOpenEncodeSessionEx(&open,&encoder))throw std::runtime_error("Session rejected");
  const GUID presets[]={NV_ENC_PRESET_P1_GUID,NV_ENC_PRESET_P2_GUID,NV_ENC_PRESET_P3_GUID,NV_ENC_PRESET_P4_GUID,NV_ENC_PRESET_P5_GUID,NV_ENC_PRESET_P6_GUID,NV_ENC_PRESET_P7_GUID};
  const struct{const char* name;GUID codec;}codecs[]={{"H264",NV_ENC_CODEC_H264_GUID},{"HEVC",NV_ENC_CODEC_HEVC_GUID}};
  const struct{const char* name;NV_ENC_TUNING_INFO value;}tunings[]={{"ultra-low-latency",NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY},{"low-latency",NV_ENC_TUNING_INFO_LOW_LATENCY},{"high-quality",NV_ENC_TUNING_INFO_HIGH_QUALITY}};
  for(const auto& codec:codecs)for(const auto& tuning:tunings){
   printf("== %s, %s\n",codec.name,tuning.name);
   NV_ENC_CONFIG first{};
   for(int index=0;index<7;++index){
    NV_ENC_PRESET_CONFIG preset{};preset.version=NV_ENC_PRESET_CONFIG_VER;preset.presetCfg.version=NV_ENC_CONFIG_VER;
    const auto status=api.nvEncGetEncodePresetConfigEx(encoder,codec.codec,presets[index],tuning.value,&preset);
    if(status){printf("P%d: status %d\n",index+1,status);continue;}
    const auto& c=preset.presetCfg;const auto& r=c.rcParams;
    printf("P%d: multiPass=%d lookahead=%d depth=%d aq=%d temporalAq=%d aqStrength=%d rc=%d gop=%u frameIntervalP=%d zeroReorder=%d nonRefP=%d adaptiveQuant=%d\n",index+1,
     int(r.multiPass),int(r.enableLookahead),int(r.lookaheadDepth),int(r.enableAQ),int(r.enableTemporalAQ),int(r.aqStrength),int(r.rateControlMode),
     c.gopLength,c.frameIntervalP,int(r.zeroReorderDelay),int(r.enableNonRefP),int(r.enableAQ));
    if(index==0)first=c;
    else{
     const auto* a=reinterpret_cast<const unsigned char*>(&first);const auto* b=reinterpret_cast<const unsigned char*>(&c);
     printf("    words that differ from P1:");int shown=0;
     for(size_t at=0;at+4<=sizeof(c);at+=4)if(memcmp(a+at,b+at,4)){if(shown++<24)printf(" +%zu",at);}
     printf("%s (%d total)\n",shown>24?" ...":"",shown);
    }
   }
  }
  api.nvEncDestroyEncoder(encoder);return 0;
 }catch(const std::exception& e){fprintf(stderr,"%s\n",e.what());return 1;}
}
