#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include "nvEncodeAPI11.h"
#include "legacy_api_bridge.h"
using Microsoft::WRL::ComPtr;
void check(NVENCSTATUS n,const char* action){if(n!=NV_ENC_SUCCESS)throw std::runtime_error(std::string(action)+": "+std::to_string(n));}

// First NAL header byte of an Annex-B access unit that carries parameter sets, or 0 when there is none.
static int first_nal(const uint8_t* data,size_t size){
 for(size_t i=0;i+3<size;++i)if(data[i]==0&&data[i+1]==0&&(data[i+2]==1||(data[i+2]==0&&data[i+3]==1&&i+4<size))){
  const size_t at=i+(data[i+2]==1?3:4);return at<size?data[at]:0;}
 return 0;
}
static bool has_hevc_vps(const uint8_t* data,size_t size){
 for(size_t i=0;i+4<size;++i)if(data[i]==0&&data[i+1]==0&&data[i+2]==1&&((data[i+3]>>1)&0x3f)==32&&!(data[i+3]&0x81))return true;
 return false;
}
static bool has_h264_sps(const uint8_t* data,size_t size){
 for(size_t i=0;i+4<size;++i)if(data[i]==0&&data[i+1]==0&&data[i+2]==1&&(data[i+3]&0x1f)==7&&!(data[i+3]&0x80))return true;
 return false;
}

// Mode switching on a real encoder: SteamVR's handles stay the same, the codec behind them changes frame by frame.
// usage: mode_switch_gpu_test [async|sync] [90|120]
int main(int argc,char** argv){
 const bool async=argc>1&&std::string(argv[1])=="async";
 const uint32_t fps=argc>2&&std::string(argv[2])=="120"?120:90;
 const uint32_t width=512,height=512;
 NV_ENCODE_API_FUNCTION_LIST api{};api.version=NV_ENCODE_API_FUNCTION_LIST_VER;void* encoder=nullptr;
 NV_ENC_OUTPUT_PTR output=nullptr;NV_ENC_REGISTERED_PTR registered=nullptr;HANDLE event=nullptr;
 try{
  // The driver's own API, exactly what the hook is handed by the original factory.
  HMODULE module=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);if(!module)throw std::runtime_error("nvEncodeAPI64.dll missing");
  using Create=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
  auto create=reinterpret_cast<Create>(GetProcAddress(module,"NvEncodeAPICreateInstance"));
  NV_ENCODE_API_FUNCTION_LIST stock{};stock.version=NV_ENCODE_API_FUNCTION_LIST_VER;
  if(!create||create(&stock)!=NV_ENC_SUCCESS)throw std::runtime_error("stock table failed");
  if(HookSetLegacyMode(1)==0)throw std::runtime_error("Stock mode accepted without a stock table");
  if(HookProvideStockTable(&stock,sizeof(stock)-1,reinterpret_cast<void*>(create))==0)throw std::runtime_error("Short stock table accepted");
  if(HookCreateLegacyFunctionTable(&api,sizeof(api),100000000,115000000))throw std::runtime_error("Legacy table creation failed");
  // The driver's factory is asked for the 11.1 list after the 12.1 one exists, as in SteamVR.
  stock={};stock.version=NV_ENCODE_API_FUNCTION_LIST_VER;if(create(&stock)!=NV_ENC_SUCCESS)throw std::runtime_error("stock table failed");
  if(HookProvideStockTable(&stock,sizeof(stock),reinterpret_cast<void*>(create)))throw std::runtime_error("Stock table rejected");
  if(HookSetLegacyMode(2)==0)throw std::runtime_error("Invalid mode accepted");
  if(HookConfigureLegacyRate(fps)||HookConfigureLegacyBudget(100000000,115000000,0))throw std::runtime_error("Configuration failed");
  ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
  if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)))throw std::runtime_error("D3D device failed");
  NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};open.version=NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
  open.device=device.Get();open.deviceType=NV_ENC_DEVICE_TYPE_DIRECTX;open.apiVersion=NVENCAPI_VERSION;
  check(api.nvEncOpenEncodeSessionEx(&open,&encoder),"open");
  HookSwitchInfo info{sizeof(info),1};
  if(HookGetLegacySwitchInfo(&info)||info.sessions!=1||info.stock_sessions!=1)throw std::runtime_error("The stock session was not opened beside the H.264 one (status "+std::to_string(info.last_stock_status)+", errors "+std::to_string(info.stock_errors)+")");
  NV_ENC_PRESET_CONFIG preset{};preset.version=NV_ENC_PRESET_CONFIG_VER;preset.presetCfg.version=NV_ENC_CONFIG_VER;
  check(api.nvEncGetEncodePresetConfigEx(encoder,NV_ENC_CODEC_HEVC_GUID,NV_ENC_PRESET_P2_GUID,NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,&preset),"legacy preset");
  NV_ENC_CONFIG config=preset.presetCfg;config.rcParams.averageBitRate=20000000;config.rcParams.maxBitRate=25000000;
  NV_ENC_INITIALIZE_PARAMS init{};init.version=NV_ENC_INITIALIZE_PARAMS_VER;init.encodeGUID=NV_ENC_CODEC_HEVC_GUID;init.presetGUID=NV_ENC_PRESET_P2_GUID;
  init.encodeWidth=init.maxEncodeWidth=init.darWidth=width;init.encodeHeight=init.maxEncodeHeight=init.darHeight=height;
  init.frameRateNum=fps;init.frameRateDen=1;init.enablePTD=1;init.encodeConfig=&config;init.tuningInfo=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
  init.enableEncodeAsync=async?1u:0u;
  const auto initialized=api.nvEncInitializeEncoder(encoder,&init);
  if(initialized!=NV_ENC_SUCCESS)throw std::runtime_error(std::string("legacy initialize: ")+std::to_string(initialized)+"; "+api.nvEncGetLastErrorString(encoder));
  if(HookGetLegacySwitchInfo(&info)||info.stock_sessions!=1||info.stock_errors)throw std::runtime_error("The stock encoder could not take SteamVR's own parameters");
  if(async){
   event=CreateEventW(nullptr,FALSE,FALSE,nullptr);if(!event)throw std::runtime_error("Completion event failed");
   NV_ENC_EVENT_PARAMS registration{};registration.version=NV_ENC_EVENT_PARAMS_VER;registration.completionEvent=event;
   check(api.nvEncRegisterAsyncEvent(encoder,&registration),"legacy event registration");
  }
  D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_NV12;
  desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> texture;if(FAILED(device->CreateTexture2D(&desc,nullptr,&texture)))throw std::runtime_error("Texture failed");
  std::vector<uint8_t> pixels(width*height*3/2,128);for(unsigned i=0;i<width*height;++i)pixels[i]=uint8_t(16+(i%width)%220);
  context->UpdateSubresource(texture.Get(),0,nullptr,pixels.data(),width,0);context->Flush();
  NV_ENC_REGISTER_RESOURCE resource{};resource.version=NV_ENC_REGISTER_RESOURCE_VER;resource.resourceType=NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
  resource.resourceToRegister=texture.Get();resource.width=width;resource.height=height;resource.bufferFormat=NV_ENC_BUFFER_FORMAT_NV12;resource.bufferUsage=NV_ENC_INPUT_IMAGE;
  check(api.nvEncRegisterResource(encoder,&resource),"legacy register");registered=resource.registeredResource;
  NV_ENC_CREATE_BITSTREAM_BUFFER buffer{};buffer.version=NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
  check(api.nvEncCreateBitstreamBuffer(encoder,&buffer),"legacy output");output=buffer.bitstreamBuffer;
  std::vector<int8_t> map(((width+31)/32)*((height+31)/32),8);
  const uint64_t frames=150;uint64_t stock_expected=0;uint32_t switches=0;bool in_stock=false;
  for(uint64_t i=0;i<frames;++i){
   const bool want=(i>=40&&i<80)||i>=110;
   if(want!=in_stock){if(HookSetLegacyMode(want?1:0))throw std::runtime_error("Mode switch refused");in_stock=want;++switches;}
   if(in_stock)++stock_expected;
   NV_ENC_MAP_INPUT_RESOURCE mapped{};mapped.version=NV_ENC_MAP_INPUT_RESOURCE_VER;mapped.registeredResource=registered;
   check(api.nvEncMapInputResource(encoder,&mapped),"legacy map");
   NV_ENC_PIC_PARAMS picture{};picture.version=NV_ENC_PIC_PARAMS_VER;
   picture.inputWidth=width;picture.inputHeight=height;picture.inputBuffer=mapped.mappedResource;picture.bufferFmt=mapped.mappedBufferFmt;
   picture.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;picture.outputBitstream=output;picture.frameIdx=uint32_t(i);picture.inputTimeStamp=i*1000000/fps;
   picture.completionEvent=event;picture.qpDeltaMap=map.data();picture.qpDeltaMapSize=uint32_t(map.size());
   // SteamVR asks for a keyframe only at the start; the switch itself must provide the others.
   if(!i)picture.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR|NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
   const auto before=picture;check(api.nvEncEncodePicture(encoder,&picture),"legacy encode");
   if(memcmp(&before,&picture,sizeof(picture)))throw std::runtime_error("The caller's picture was modified");
   if(async&&WaitForSingleObject(event,2000)!=WAIT_OBJECT_0)throw std::runtime_error("Async completion did not signal at frame "+std::to_string(i));
   NV_ENC_LOCK_BITSTREAM locked{};locked.version=NV_ENC_LOCK_BITSTREAM_VER;locked.outputBitstream=output;
   check(api.nvEncLockBitstream(encoder,&locked),"legacy lock");
   if(locked.outputBitstream!=output)throw std::runtime_error("The lock replaced the caller's output handle");
   if(locked.outputTimeStamp!=i*1000000/fps)throw std::runtime_error("Timestamp mismatch at frame "+std::to_string(i));
   const auto* data=static_cast<const uint8_t*>(locked.bitstreamBufferPtr);const size_t size=locked.bitstreamSizeInBytes;
   if(!size||!first_nal(data,size))throw std::runtime_error("Empty or unframed bitstream at frame "+std::to_string(i));
   const bool switch_frame=i==0||i==40||i==80||i==110;
   if(switch_frame){
    // The first frame of each stretch carries its own parameter sets so the Frame's decoder can start on it.
    const bool good=want?has_hevc_vps(data,size):has_h264_sps(data,size);
    if(!good)throw std::runtime_error(std::string("Frame ")+std::to_string(i)+" is not a "+(want?"HEVC":"H.264")+" keyframe with parameter sets");
   }
   check(api.nvEncUnlockBitstream(encoder,output),"legacy unlock");check(api.nvEncUnmapInputResource(encoder,mapped.mappedResource),"legacy unmap");
   // SteamVR reconfigures before every frame; both encoders must keep following it through the switches.
   if(i%10==5){NV_ENC_RECONFIGURE_PARAMS change{};change.version=NV_ENC_RECONFIGURE_PARAMS_VER;change.reInitEncodeParams=init;
    check(api.nvEncReconfigureEncoder(encoder,&change),"legacy reconfigure");}
  }
  if(HookGetLegacySwitchInfo(&info)||info.stock_encodes!=stock_expected||info.stock_locks!=stock_expected||info.switches!=switches||info.stock_errors)
   throw std::runtime_error("Counters disagree: stock encodes "+std::to_string(info.stock_encodes)+" of "+std::to_string(stock_expected)+", errors "+std::to_string(info.stock_errors)+", last status "+std::to_string(info.last_stock_status));
  HookBridgeStats stats{sizeof(stats),1};
  if(HookGetLegacyBridgeStats(&stats)||stats.errors||stats.encodes!=frames)throw std::runtime_error("The H.264 side saw errors or the wrong frame count");
  check(api.nvEncDestroyBitstreamBuffer(encoder,output),"destroy output");output=nullptr;
  check(api.nvEncUnregisterResource(encoder,registered),"unregister");registered=nullptr;
  if(async){NV_ENC_EVENT_PARAMS p{};p.version=NV_ENC_EVENT_PARAMS_VER;p.completionEvent=event;check(api.nvEncUnregisterAsyncEvent(encoder,&p),"legacy event unregister");}
  if(event){CloseHandle(event);event=nullptr;}
  check(api.nvEncDestroyEncoder(encoder),"destroy");encoder=nullptr;
  if(HookGetLegacySwitchInfo(&info)||info.sessions)throw std::runtime_error("A session was left open");
  std::cout<<"{\"mode_switch\":true,\"frames\":"<<frames<<",\"stock_frames\":"<<stock_expected<<",\"switches\":"<<switches<<",\"fps\":"<<fps<<",\"async\":"<<(async?"true":"false")<<"}\n";
  return 0;
 }catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 1;}
}
