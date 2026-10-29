#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include "nvEncodeAPI11.h"
#include "legacy_api_bridge.h"
using Microsoft::WRL::ComPtr;
using Create=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
void check(NVENCSTATUS n,const char* action){if(n!=NV_ENC_SUCCESS)throw std::runtime_error(std::string(action)+": "+std::to_string(n));}

static double now_us(){static LARGE_INTEGER f{};LARGE_INTEGER t;if(!f.QuadPart)QueryPerformanceFrequency(&f);QueryPerformanceCounter(&t);return double(t.QuadPart)*1e6/double(f.QuadPart);}
struct Series{
 std::vector<double> values;
 void add(double v){values.push_back(v);}
 double mean()const{double s=0;for(auto v:values)s+=v;return values.empty()?0:s/double(values.size());}
 double percentile(double p){if(values.empty())return 0;auto copy=values;std::sort(copy.begin(),copy.end());return copy[size_t(p*double(copy.size()-1))];}
};
static double video_memory_mb(IDXGIAdapter3* adapter){
 DXGI_QUERY_VIDEO_MEMORY_INFO info{};
 if(!adapter||FAILED(adapter->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_LOCAL,&info)))return -1;
 return double(info.CurrentUsage)/1048576.0;
}

// Times the calls SteamVR makes for every frame (reconfigure, map, encode, lock, unlock, unmap) through whichever table it is given.
// usage: bridge_bench driver|bridge|dual-h264|dual-stock [frames]
int main(int argc,char** argv){
 const std::string mode=argc>1?argv[1]:"bridge";
 const int frames=argc>2?std::stoi(argv[2]):3000,warmup=200;
 const uint32_t fps=90,width=1024,height=4096;
 NV_ENC_OUTPUT_PTR output=nullptr;NV_ENC_REGISTERED_PTR registered=nullptr;void* encoder=nullptr;NV_ENCODE_API_FUNCTION_LIST api{};
 try{
  HMODULE module=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);if(!module)throw std::runtime_error("nvEncodeAPI64.dll missing");
  auto create=reinterpret_cast<Create>(GetProcAddress(module,"NvEncodeAPICreateInstance"));
  NV_ENCODE_API_FUNCTION_LIST stock{};stock.version=NV_ENCODE_API_FUNCTION_LIST_VER;
  if(!create||create(&stock)!=NV_ENC_SUCCESS)throw std::runtime_error("stock table failed");
  if(mode=="driver")api=stock;
  else{
   api.version=NV_ENCODE_API_FUNCTION_LIST_VER;
   if(HookConfigureLegacyRate(fps)||HookConfigureLegacyBudget(100000000,115000000,0))throw std::runtime_error("Configuration failed");
   if(HookCreateLegacyFunctionTable(&api,sizeof(api),100000000,115000000))throw std::runtime_error("Legacy table creation failed");
#ifdef BENCH_STOCK
   if(mode=="dual-h264"||mode=="dual-stock"){
    create(&stock);
    if(HookProvideStockTable(&stock,sizeof(stock),reinterpret_cast<void*>(create)))throw std::runtime_error("Stock table rejected");
    if(HookSetLegacyMode(mode=="dual-stock"?1:0))throw std::runtime_error("Mode refused");
   }
#else
   if(mode!="bridge")throw std::runtime_error("This build has no stock encoder");
#endif
  }
  ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
  if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)))throw std::runtime_error("D3D device failed");
  ComPtr<IDXGIDevice> dxgi;ComPtr<IDXGIAdapter> adapter;ComPtr<IDXGIAdapter3> adapter3;
  device.As(&dxgi);if(dxgi)dxgi->GetAdapter(&adapter);if(adapter)adapter.As(&adapter3);
  D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_NV12;
  desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> texture;if(FAILED(device->CreateTexture2D(&desc,nullptr,&texture)))throw std::runtime_error("Texture failed");
  std::vector<uint8_t> pixels(width*height*3/2,128);for(unsigned i=0;i<width*height;++i)pixels[i]=uint8_t(16+((i%width)*7+(i/width))%220);
  context->UpdateSubresource(texture.Get(),0,nullptr,pixels.data(),width,0);context->Flush();
  const double baseline_mb=video_memory_mb(adapter3.Get());
  NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};open.version=NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
  open.device=device.Get();open.deviceType=NV_ENC_DEVICE_TYPE_DIRECTX;open.apiVersion=NVENCAPI_VERSION;
  check(api.nvEncOpenEncodeSessionEx(&open,&encoder),"open");
  NV_ENC_PRESET_CONFIG preset{};preset.version=NV_ENC_PRESET_CONFIG_VER;preset.presetCfg.version=NV_ENC_CONFIG_VER;
  check(api.nvEncGetEncodePresetConfigEx(encoder,NV_ENC_CODEC_HEVC_GUID,NV_ENC_PRESET_P2_GUID,NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,&preset),"legacy preset");
  NV_ENC_CONFIG config=preset.presetCfg;config.rcParams.averageBitRate=100000000;config.rcParams.maxBitRate=115000000;
  NV_ENC_INITIALIZE_PARAMS init{};init.version=NV_ENC_INITIALIZE_PARAMS_VER;init.encodeGUID=NV_ENC_CODEC_HEVC_GUID;init.presetGUID=NV_ENC_PRESET_P2_GUID;
  init.encodeWidth=init.maxEncodeWidth=init.darWidth=width;init.encodeHeight=init.maxEncodeHeight=init.darHeight=height;
  init.frameRateNum=fps;init.frameRateDen=1;init.enablePTD=1;init.encodeConfig=&config;init.tuningInfo=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
  const auto initialized=api.nvEncInitializeEncoder(encoder,&init);
  if(initialized!=NV_ENC_SUCCESS)throw std::runtime_error(std::string("legacy initialize: ")+std::to_string(initialized)+"; "+api.nvEncGetLastErrorString(encoder));
  NV_ENC_REGISTER_RESOURCE resource{};resource.version=NV_ENC_REGISTER_RESOURCE_VER;resource.resourceType=NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
  resource.resourceToRegister=texture.Get();resource.width=width;resource.height=height;resource.bufferFormat=NV_ENC_BUFFER_FORMAT_NV12;resource.bufferUsage=NV_ENC_INPUT_IMAGE;
  check(api.nvEncRegisterResource(encoder,&resource),"legacy register");registered=resource.registeredResource;
  NV_ENC_CREATE_BITSTREAM_BUFFER buffer{};buffer.version=NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
  check(api.nvEncCreateBitstreamBuffer(encoder,&buffer),"legacy output");output=buffer.bitstreamBuffer;
  const double ready_mb=video_memory_mb(adapter3.Get());
  std::vector<int8_t> map(((width+31)/32)*((height+31)/32),8);
  Series reconfigure,map_call,encode,lock,unlock,unmap,frame;uint64_t bytes=0;
  for(int i=0;i<frames+warmup;++i){
   const double frame_start=now_us();
   // SteamVR reconfigures its encoder before every frame.
   NV_ENC_RECONFIGURE_PARAMS change{};change.version=NV_ENC_RECONFIGURE_PARAMS_VER;change.reInitEncodeParams=init;
   double t=now_us();check(api.nvEncReconfigureEncoder(encoder,&change),"legacy reconfigure");const double d_reconfigure=now_us()-t;
   NV_ENC_MAP_INPUT_RESOURCE mapped{};mapped.version=NV_ENC_MAP_INPUT_RESOURCE_VER;mapped.registeredResource=registered;
   t=now_us();check(api.nvEncMapInputResource(encoder,&mapped),"legacy map");const double d_map=now_us()-t;
   NV_ENC_PIC_PARAMS picture{};picture.version=NV_ENC_PIC_PARAMS_VER;
   picture.inputWidth=width;picture.inputHeight=height;picture.inputBuffer=mapped.mappedResource;picture.bufferFmt=mapped.mappedBufferFmt;
   picture.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;picture.outputBitstream=output;picture.frameIdx=uint32_t(i);picture.inputTimeStamp=uint64_t(i)*1000000/fps;
   picture.qpDeltaMap=map.data();picture.qpDeltaMapSize=uint32_t(map.size());
   if(!i)picture.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR|NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
   t=now_us();check(api.nvEncEncodePicture(encoder,&picture),"legacy encode");const double d_encode=now_us()-t;
   NV_ENC_LOCK_BITSTREAM locked{};locked.version=NV_ENC_LOCK_BITSTREAM_VER;locked.outputBitstream=output;
   t=now_us();check(api.nvEncLockBitstream(encoder,&locked),"legacy lock");const double d_lock=now_us()-t;
   bytes+=locked.bitstreamSizeInBytes;
   t=now_us();check(api.nvEncUnlockBitstream(encoder,output),"legacy unlock");const double d_unlock=now_us()-t;
   t=now_us();check(api.nvEncUnmapInputResource(encoder,mapped.mappedResource),"legacy unmap");const double d_unmap=now_us()-t;
   if(i>=warmup){reconfigure.add(d_reconfigure);map_call.add(d_map);encode.add(d_encode);lock.add(d_lock);unlock.add(d_unlock);unmap.add(d_unmap);frame.add(now_us()-frame_start);}
  }
  check(api.nvEncDestroyBitstreamBuffer(encoder,output),"destroy output");output=nullptr;
  check(api.nvEncUnregisterResource(encoder,registered),"unregister");registered=nullptr;
  check(api.nvEncDestroyEncoder(encoder),"destroy");encoder=nullptr;
  auto row=[](const char* name,Series& s){std::cout<<"\""<<name<<"\":["<<s.mean()<<","<<s.percentile(.5)<<","<<s.percentile(.99)<<"]";};
  std::cout<<"{\"mode\":\""<<mode<<"\",\"frames\":"<<frames<<",\"avg_kbit_per_frame\":"<<(bytes*8/1000/uint64_t(frames+warmup))<<",\"vram_session_mb\":"<<(ready_mb-baseline_mb)<<",\"us_mean_p50_p99\":{";
  row("reconfigure",reconfigure);std::cout<<",";row("map",map_call);std::cout<<",";row("encode",encode);std::cout<<",";row("lock",lock);std::cout<<",";row("unlock",unlock);std::cout<<",";row("unmap",unmap);std::cout<<",";row("frame",frame);
  std::cout<<"}}\n";
  return 0;
 }catch(const std::exception& error){std::cerr<<error.what()<<"\n";
  if(encoder){if(output)api.nvEncDestroyBitstreamBuffer(encoder,output);if(registered)api.nvEncUnregisterResource(encoder,registered);api.nvEncDestroyEncoder(encoder);}
  return 1;}
}
