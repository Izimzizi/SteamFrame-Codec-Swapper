#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <iostream>
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
  const GUID codecs[]={NV_ENC_CODEC_H264_GUID,NV_ENC_CODEC_HEVC_GUID,NV_ENC_CODEC_AV1_GUID};
  const char* names[]={"H.264","HEVC","AV1"};
  for(unsigned i=0;i<3;++i){
   std::cout<<"{\"codec\":\""<<names[i]<<"\"";
   const NV_ENC_CAPS caps[]={NV_ENC_CAPS_WIDTH_MAX,NV_ENC_CAPS_HEIGHT_MAX,NV_ENC_CAPS_LEVEL_MAX,NV_ENC_CAPS_SUPPORT_10BIT_ENCODE,NV_ENC_CAPS_SUPPORT_CONSTRAINED_ENCODING};
   const char* labels[]={"maximum_width","maximum_height","maximum_level","10bit","constrained_encoding"};
   for(unsigned j=0;j<5;++j){NV_ENC_CAPS_PARAM p{};p.version=NV_ENC_CAPS_PARAM_VER;p.capsToQuery=caps[j];int value=0;
    auto status=api.nvEncGetEncodeCaps(encoder,codecs[i],&p,&value);
    std::cout<<",\""<<labels[j]<<"\":"<<value<<",\""<<labels[j]<<"_status\":"<<status;}
   std::cout<<"}\n";
  }
  api.nvEncDestroyEncoder(encoder);return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}
}
