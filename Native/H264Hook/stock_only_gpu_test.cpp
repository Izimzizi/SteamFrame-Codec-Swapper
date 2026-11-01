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

static bool has_hevc_vps(const uint8_t* data,size_t size){
 for(size_t i=0;i+4<size;++i)if(data[i]==0&&data[i+1]==0&&data[i+2]==1&&((data[i+3]>>1)&0x3f)==32&&!(data[i+3]&0x81))return true;
 return false;
}

// Default (HEVC) at a picture the H.264 encoder cannot take (taller than 4096): SteamVR must be told the real sizes, the session must
// run on the driver's own encoder alone, and nothing may be asked of the H.264 encoder beside it.
int main(){
 const uint32_t fps=90,width=1344,height=5376;
 NV_ENCODE_API_FUNCTION_LIST api{};api.version=NV_ENCODE_API_FUNCTION_LIST_VER;void* encoder=nullptr;
 NV_ENC_OUTPUT_PTR output=nullptr;NV_ENC_REGISTERED_PTR registered=nullptr;
 try{
  HMODULE module=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);if(!module)throw std::runtime_error("nvEncodeAPI64.dll missing");
  using Create=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
  auto create=reinterpret_cast<Create>(GetProcAddress(module,"NvEncodeAPICreateInstance"));
  NV_ENCODE_API_FUNCTION_LIST stock{};stock.version=NV_ENCODE_API_FUNCTION_LIST_VER;
  if(!create||create(&stock)!=NV_ENC_SUCCESS)throw std::runtime_error("stock table failed");
  if(HookCreateLegacyFunctionTable(&api,sizeof(api),100000000,115000000))throw std::runtime_error("Legacy table creation failed");
  stock={};stock.version=NV_ENCODE_API_FUNCTION_LIST_VER;if(create(&stock)!=NV_ENC_SUCCESS)throw std::runtime_error("stock table failed");
  if(HookProvideStockTable(&stock,sizeof(stock),reinterpret_cast<void*>(create)))throw std::runtime_error("Stock table rejected");
  if(HookConfigureLegacyRate(fps)||HookConfigureLegacyBudget(100000000,115000000,0))throw std::runtime_error("Configuration failed");
  ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
  if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)))throw std::runtime_error("D3D device failed");
  NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};open.version=NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
  open.device=device.Get();open.deviceType=NV_ENC_DEVICE_TYPE_DIRECTX;open.apiVersion=NVENCAPI_VERSION;
  check(api.nvEncOpenEncodeSessionEx(&open,&encoder),"open");
  auto query=[&](NV_ENC_CAPS cap){NV_ENC_CAPS_PARAM p{};p.version=NV_ENC_CAPS_PARAM_VER;p.capsToQuery=cap;int value=0;
   check(api.nvEncGetEncodeCaps(encoder,NV_ENC_CODEC_HEVC_GUID,&p,&value),"caps");return value;};
  // H.264 mode: the sizes SteamVR is told are the H.264 encoder's.
  const int h264_height=query(NV_ENC_CAPS_HEIGHT_MAX),h264_width=query(NV_ENC_CAPS_WIDTH_MAX);
  if(h264_height>4096||h264_width>4096)throw std::runtime_error("H.264 mode advertised more than the H.264 encoder takes");
  if(HookSetLegacyMode(1))throw std::runtime_error("Stock mode refused");
  const int stock_height=query(NV_ENC_CAPS_HEIGHT_MAX);
  if(stock_height<int(height))throw std::runtime_error("Stock mode still limits the height to "+std::to_string(stock_height));
  NV_ENC_PRESET_CONFIG preset{};preset.version=NV_ENC_PRESET_CONFIG_VER;preset.presetCfg.version=NV_ENC_CONFIG_VER;
  check(api.nvEncGetEncodePresetConfigEx(encoder,NV_ENC_CODEC_HEVC_GUID,NV_ENC_PRESET_P2_GUID,NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,&preset),"legacy preset");
  NV_ENC_CONFIG config=preset.presetCfg;config.rcParams.averageBitRate=40000000;config.rcParams.maxBitRate=50000000;
  NV_ENC_INITIALIZE_PARAMS init{};init.version=NV_ENC_INITIALIZE_PARAMS_VER;init.encodeGUID=NV_ENC_CODEC_HEVC_GUID;init.presetGUID=NV_ENC_PRESET_P2_GUID;
  init.encodeWidth=init.maxEncodeWidth=init.darWidth=width;init.encodeHeight=init.maxEncodeHeight=init.darHeight=height;
  init.frameRateNum=fps;init.frameRateDen=1;init.enablePTD=1;init.encodeConfig=&config;init.tuningInfo=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
  const auto initialized=api.nvEncInitializeEncoder(encoder,&init);
  if(initialized!=NV_ENC_SUCCESS)throw std::runtime_error(std::string("stock-only initialize: ")+std::to_string(initialized));
  HookSwitchInfo info{sizeof(info),1};
  if(HookGetLegacySwitchInfo(&info)||info.stock_sessions!=1||info.stock_errors)throw std::runtime_error("The stock encoder did not take the tall picture");
  D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_NV12;
  desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> texture;if(FAILED(device->CreateTexture2D(&desc,nullptr,&texture)))throw std::runtime_error("Texture failed");
  std::vector<uint8_t> pixels(size_t(width)*height*3/2,128);for(size_t i=0;i<size_t(width)*height;++i)pixels[i]=uint8_t(16+(i%width)%220);
  context->UpdateSubresource(texture.Get(),0,nullptr,pixels.data(),width,0);context->Flush();
  NV_ENC_REGISTER_RESOURCE resource{};resource.version=NV_ENC_REGISTER_RESOURCE_VER;resource.resourceType=NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
  resource.resourceToRegister=texture.Get();resource.width=width;resource.height=height;resource.bufferFormat=NV_ENC_BUFFER_FORMAT_NV12;resource.bufferUsage=NV_ENC_INPUT_IMAGE;
  check(api.nvEncRegisterResource(encoder,&resource),"register");registered=resource.registeredResource;
  NV_ENC_CREATE_BITSTREAM_BUFFER buffer{};buffer.version=NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
  check(api.nvEncCreateBitstreamBuffer(encoder,&buffer),"output");output=buffer.bitstreamBuffer;
  const uint64_t frames=30;
  for(uint64_t i=0;i<frames;++i){
   NV_ENC_MAP_INPUT_RESOURCE mapped{};mapped.version=NV_ENC_MAP_INPUT_RESOURCE_VER;mapped.registeredResource=registered;
   check(api.nvEncMapInputResource(encoder,&mapped),"map");
   NV_ENC_PIC_PARAMS picture{};picture.version=NV_ENC_PIC_PARAMS_VER;
   picture.inputWidth=width;picture.inputHeight=height;picture.inputBuffer=mapped.mappedResource;picture.bufferFmt=mapped.mappedBufferFmt;
   picture.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;picture.outputBitstream=output;picture.frameIdx=uint32_t(i);picture.inputTimeStamp=i*1000000/fps;
   if(!i)picture.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR|NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
   check(api.nvEncEncodePicture(encoder,&picture),"encode");
   NV_ENC_LOCK_BITSTREAM locked{};locked.version=NV_ENC_LOCK_BITSTREAM_VER;locked.outputBitstream=output;
   check(api.nvEncLockBitstream(encoder,&locked),"lock");
   const auto* data=static_cast<const uint8_t*>(locked.bitstreamBufferPtr);
   if(!locked.bitstreamSizeInBytes)throw std::runtime_error("Empty bitstream at frame "+std::to_string(i));
   if(!i&&!has_hevc_vps(data,locked.bitstreamSizeInBytes))throw std::runtime_error("The first frame is not HEVC with parameter sets");
   check(api.nvEncUnlockBitstream(encoder,output),"unlock");check(api.nvEncUnmapInputResource(encoder,mapped.mappedResource),"unmap");
   if(i%10==5){NV_ENC_RECONFIGURE_PARAMS change{};change.version=NV_ENC_RECONFIGURE_PARAMS_VER;change.reInitEncodeParams=init;
    check(api.nvEncReconfigureEncoder(encoder,&change),"reconfigure");}
  }
  NV_ENC_PIC_PARAMS eos{};eos.version=NV_ENC_PIC_PARAMS_VER;eos.encodePicFlags=NV_ENC_PIC_FLAG_EOS;check(api.nvEncEncodePicture(encoder,&eos),"end of stream");
  HookBridgeStats stats{sizeof(stats),1};
  if(HookGetLegacySwitchInfo(&info)||info.stock_locks!=frames||info.stock_errors)throw std::runtime_error("Stock counters disagree: locks "+std::to_string(info.stock_locks)+", errors "+std::to_string(info.stock_errors));
  if(HookGetLegacyBridgeStats(&stats)||stats.errors)throw std::runtime_error("Errors were counted");
  check(api.nvEncDestroyBitstreamBuffer(encoder,output),"destroy output");output=nullptr;
  check(api.nvEncUnregisterResource(encoder,registered),"unregister");registered=nullptr;
  check(api.nvEncDestroyEncoder(encoder),"destroy");encoder=nullptr;
  if(HookGetLegacySwitchInfo(&info)||info.sessions)throw std::runtime_error("A session was left open");
  std::cout<<"{\"stock_only\":true,\"frames\":"<<frames<<",\"width\":"<<width<<",\"height\":"<<height<<",\"h264_caps_height\":"<<h264_height<<",\"stock_caps_height\":"<<stock_height<<"}\n";
  return 0;
 }catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 1;}
}
