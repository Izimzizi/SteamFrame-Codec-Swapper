#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <array>
#include <cstring>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include "nvEncodeAPI11.h"
#include "legacy_api_bridge.h"
using Microsoft::WRL::ComPtr;
void check(NVENCSTATUS n,const char* action){if(n!=NV_ENC_SUCCESS)throw std::runtime_error(std::string(action)+": "+std::to_string(n));}
int main(int argc,char** argv){
 const bool async=argc>1&&std::string(argv[1])=="async";
 const std::string budget=argc>2?argv[2]:"";
 const bool fixed=budget=="fixed100"||budget=="fixed200"||budget=="fixed400"||budget=="fixed500";
 const uint32_t target=budget=="fixed100"?100000000:budget=="fixed400"?400000000:budget=="fixed500"?500000000:200000000;
 const uint32_t fps=argc>3&&std::string(argv[3])=="90"?90:120;
 const bool h264=argc>4&&std::string(argv[4])=="h264";
 const uint32_t width=fixed?(h264?1024:1536):512,height=fixed?(h264?4096:6144):512;
 NV_ENCODE_API_FUNCTION_LIST api{};api.version=NV_ENCODE_API_FUNCTION_LIST_VER;void* encoder=nullptr;
 NV_ENC_OUTPUT_PTR output=nullptr;NV_ENC_REGISTERED_PTR registered=nullptr;HANDLE event=nullptr;bool event_registered=false;
 try{
  if(Av1ConfigureLegacyBudget(200000001,200000001,1)==0||Av1ConfigureLegacyBudget(200000000,199999999,1)==0||Av1ConfigureLegacyBudget(200000000,200000000,2)==0)throw std::runtime_error("Budget guard failed");
  const uint32_t average=fixed?target:100000000,peak=fixed?target:115000000;
  if(Av1ConfigureLegacyCodec(3)==0||Av1ConfigureLegacyCodec(h264?2:1))throw std::runtime_error("Codec guard failed");
  if(h264&&(Av1ConfigureLegacyBudget(500000000,500000000,1)||Av1ConfigureLegacyBudget(500000001,500000001,1)==0))throw std::runtime_error("H.264 budget guard failed");
  if(Av1ConfigureLegacyRate(91)==0||Av1ConfigureLegacyRate(fps))throw std::runtime_error("Rate guard failed");
  if(Av1ConfigureLegacyBudget(average,peak,fixed?1:0))throw std::runtime_error("Budget configuration failed");
  if(Av1CreateLegacyFunctionTable(&api,sizeof(api)-1,100000000,115000000)==0)throw std::runtime_error("Table size guard failed");
  if(Av1CreateLegacyFunctionTable(&api,sizeof(api),average,peak))throw std::runtime_error("Legacy table creation failed");
  ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
  if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)))throw std::runtime_error("D3D device failed");
  NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};open.version=NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
  open.device=device.Get();open.deviceType=NV_ENC_DEVICE_TYPE_DIRECTX;open.apiVersion=NVENCAPI_VERSION;
  const auto original_open=open;check(api.nvEncOpenEncodeSessionEx(&open,&encoder),"open");
  if(Av1ConfigureLegacyBudget(average,peak,fixed?1:0)==0)throw std::runtime_error("Active budget mutation accepted");
  if(Av1ConfigureLegacyRate(fps)==0)throw std::runtime_error("Active rate mutation accepted");
  if(Av1ConfigureLegacyCodec(h264?2:1)==0)throw std::runtime_error("Active codec mutation accepted");
  NV_ENC_CAPS_PARAM required{};required.version=NV_ENC_CAPS_PARAM_VER;required.capsToQuery=NV_ENC_CAPS_SUPPORT_CONSTRAINED_ENCODING;
  int constrained=0;check(api.nvEncGetEncodeCaps(encoder,NV_ENC_CODEC_HEVC_GUID,&required,&constrained),"legacy constrained capability");
  if(!constrained)throw std::runtime_error("SteamVR legacy constrained capability was lost");
  if(memcmp(&open,&original_open,sizeof(open)))throw std::runtime_error("Caller open parameters changed");
  NV_ENC_PRESET_CONFIG preset{};preset.version=NV_ENC_PRESET_CONFIG_VER;preset.presetCfg.version=NV_ENC_CONFIG_VER;
  check(api.nvEncGetEncodePresetConfigEx(encoder,NV_ENC_CODEC_HEVC_GUID,NV_ENC_PRESET_P2_GUID,NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,&preset),"legacy preset");
  NV_ENC_CONFIG config=preset.presetCfg;config.rcParams.averageBitRate=5000000;config.rcParams.maxBitRate=6000000;
  NV_ENC_INITIALIZE_PARAMS init{};init.version=NV_ENC_INITIALIZE_PARAMS_VER;init.encodeGUID=NV_ENC_CODEC_HEVC_GUID;init.presetGUID=NV_ENC_PRESET_P2_GUID;
  init.encodeWidth=init.maxEncodeWidth=init.darWidth=width;init.encodeHeight=init.maxEncodeHeight=init.darHeight=height;
  init.frameRateNum=fps;init.frameRateDen=1;init.enablePTD=1;init.encodeConfig=&config;init.tuningInfo=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
  init.enableEncodeAsync=async?1u:0u;
  const auto original_init=init;const auto original_config=config;
  const auto initialized=api.nvEncInitializeEncoder(encoder,&init);
  if(initialized!=NV_ENC_SUCCESS)throw std::runtime_error(std::string("legacy initialize: ")+std::to_string(initialized)+"; "+api.nvEncGetLastErrorString(encoder));
  Av1BridgeStats accepted{sizeof(accepted),1};
  if(Av1GetLegacyBridgeStats(&accepted)||accepted.average_bps!=(fixed?target:5000000u)||accepted.peak_bps!=(fixed?target:6000000u))throw std::runtime_error("Accepted budget differs");
  if(memcmp(&init,&original_init,sizeof(init))||memcmp(&config,&original_config,sizeof(config)))throw std::runtime_error("Caller init/config changed");
  if(async){
   event=CreateEventW(nullptr,FALSE,FALSE,nullptr);if(!event)throw std::runtime_error("Completion event failed");
   NV_ENC_EVENT_PARAMS registration{};registration.version=NV_ENC_EVENT_PARAMS_VER;registration.completionEvent=event;
   check(api.nvEncRegisterAsyncEvent(encoder,&registration),"legacy event registration");event_registered=true;
  }
  D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_NV12;
  desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> texture;if(FAILED(device->CreateTexture2D(&desc,nullptr,&texture)))throw std::runtime_error("Texture failed");
  std::vector<uint8_t> pixels(width*height*3/2,128);for(unsigned i=0;i<width*height;++i)pixels[i]=uint8_t(16+(i%width)%220);
  context->UpdateSubresource(texture.Get(),0,nullptr,pixels.data(),width,0);context->Flush();
  NV_ENC_REGISTER_RESOURCE resource{};resource.version=NV_ENC_REGISTER_RESOURCE_VER;resource.resourceType=NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
  resource.resourceToRegister=texture.Get();resource.width=width;resource.height=height;resource.bufferFormat=NV_ENC_BUFFER_FORMAT_NV12;resource.bufferUsage=NV_ENC_INPUT_IMAGE;
  check(api.nvEncRegisterResource(encoder,&resource),"legacy register");registered=resource.registeredResource;
  NV_ENC_CREATE_BITSTREAM_BUFFER create{};create.version=NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
  check(api.nvEncCreateBitstreamBuffer(encoder,&create),"legacy output");output=create.bitstreamBuffer;
  std::ofstream file(h264?"legacy-bridge.h264":"legacy-bridge.ivf",std::ios::binary);std::array<uint8_t,32> header{};
  memcpy(header.data(),"DKIF",4);header[6]=32;memcpy(header.data()+8,"AV01",4);
  uint16_t ivfWidth=static_cast<uint16_t>(width),ivfHeight=static_cast<uint16_t>(height);uint32_t rate=fps,scale=1,count=120;
  memcpy(header.data()+12,&ivfWidth,2);memcpy(header.data()+14,&ivfHeight,2);memcpy(header.data()+16,&rate,4);memcpy(header.data()+20,&scale,4);memcpy(header.data()+24,&count,4);if(!h264)file.write(reinterpret_cast<char*>(header.data()),32);
  std::vector<int8_t> map(((width+31)/32)*((height+31)/32),8);map[map.size()-(width+31)/32]=0;
  for(uint64_t i=0;i<120;++i){
   NV_ENC_MAP_INPUT_RESOURCE mapped{};mapped.version=NV_ENC_MAP_INPUT_RESOURCE_VER;mapped.registeredResource=registered;
   check(api.nvEncMapInputResource(encoder,&mapped),"legacy map");
   struct Guarded{std::array<uint8_t,32> before;NV_ENC_PIC_PARAMS p;std::array<uint8_t,32> after;} guarded{};
   guarded.before.fill(0xa7);guarded.after.fill(0xa7);auto& picture=guarded.p;picture.version=NV_ENC_PIC_PARAMS_VER;
   picture.inputWidth=width;picture.inputHeight=height;picture.inputBuffer=mapped.mappedResource;picture.bufferFmt=mapped.mappedBufferFmt;
   picture.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;picture.outputBitstream=output;picture.frameIdx=uint32_t(i);picture.inputTimeStamp=i*1000000/fps;
   picture.completionEvent=event;
   picture.qpDeltaMap=map.data();picture.qpDeltaMapSize=uint32_t(map.size());if(!i)picture.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR|NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
   const auto original=guarded;check(api.nvEncEncodePicture(encoder,&picture),"legacy encode");
   if(memcmp(&guarded,&original,sizeof(guarded)))throw std::runtime_error("Picture/canaries changed");
   if(async&&WaitForSingleObject(event,2000)!=WAIT_OBJECT_0)throw std::runtime_error("Async completion did not signal");
   struct Locked{NV_ENC_LOCK_BITSTREAM p;std::array<uint8_t,32> after;} locked{};locked.after.fill(0xb6);
   locked.p.version=NV_ENC_LOCK_BITSTREAM_VER;locked.p.outputBitstream=output;
   check(api.nvEncLockBitstream(encoder,&locked.p),"legacy lock");
   for(auto byte:locked.after)if(byte!=0xb6)throw std::runtime_error("Lock canary changed");
   if(locked.p.outputTimeStamp!=i*1000000/fps)throw std::runtime_error("Timestamp mismatch");
   if(!h264){file.write(reinterpret_cast<char*>(&locked.p.bitstreamSizeInBytes),4);file.write(reinterpret_cast<char*>(&i),8);}file.write(static_cast<char*>(locked.p.bitstreamBufferPtr),locked.p.bitstreamSizeInBytes);
   check(api.nvEncUnlockBitstream(encoder,output),"legacy unlock");check(api.nvEncUnmapInputResource(encoder,mapped.mappedResource),"legacy unmap");
   if(i==30||i==60||i==90){NV_ENC_RECONFIGURE_PARAMS change{};change.version=NV_ENC_RECONFIGURE_PARAMS_VER;change.reInitEncodeParams=init;
    change.reInitEncodeParams.frameRateNum=i==30?65u:i==60?300u:fps;
    const auto unchanged=change;
    check(api.nvEncReconfigureEncoder(encoder,&change),"legacy measured-throughput reconfigure");
    if(memcmp(&change,&unchanged,sizeof(change)))throw std::runtime_error("Caller reconfigure changed");
   }
  }
  file.close();check(api.nvEncDestroyBitstreamBuffer(encoder,output),"destroy output");output=nullptr;
  check(api.nvEncUnregisterResource(encoder,registered),"unregister");registered=nullptr;
  if(event_registered){NV_ENC_EVENT_PARAMS p{};p.version=NV_ENC_EVENT_PARAMS_VER;p.completionEvent=event;check(api.nvEncUnregisterAsyncEvent(encoder,&p),"legacy event unregister");event_registered=false;}
  if(event){CloseHandle(event);event=nullptr;}
  check(api.nvEncDestroyEncoder(encoder),"destroy");encoder=nullptr;
  Av1BridgeStats completed{sizeof(completed),1};
  if(Av1GetLegacyBridgeStats(&completed)||completed.errors||completed.reconfigures!=3||completed.source_fps_num!=fps||completed.source_fps_den!=1)
   throw std::runtime_error("Measured-throughput reconfigure did not preserve nominal cadence");
  std::cout<<"{\"caller_sdk\":\"11.1\",\"codec\":\""<<(h264?"H.264":"AV1")<<"\",\"frames\":120,\"fps\":"<<fps<<",\"width\":"<<width<<",\"height\":"<<height<<",\"caller_preserved\":true,\"pic_lock_canaries_preserved\":true,\"reconfigure\":true,\"measured_rate_updates\":[65,300],\"async\":"<<(async?"true":"false")<<",\"injected\":false}\n";
  return 0;
 }catch(const std::exception& error){std::cerr<<error.what()<<"\n";
  if(encoder){if(output)api.nvEncDestroyBitstreamBuffer(encoder,output);if(registered)api.nvEncUnregisterResource(encoder,registered);
   if(event_registered){NV_ENC_EVENT_PARAMS p{};p.version=NV_ENC_EVENT_PARAMS_VER;p.completionEvent=event;api.nvEncUnregisterAsyncEvent(encoder,&p);}api.nvEncDestroyEncoder(encoder);}
  if(event)CloseHandle(event);return 1;}
}
