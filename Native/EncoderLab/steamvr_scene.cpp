// Bounded synthetic moving-detail source through SteamVR's ordinary compositor.
// No encoder hooks, bitrate changes, driver modifications, or network traffic here.
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <string>
#include "openvr.h"
using Microsoft::WRL::ComPtr;
static void check(HRESULT h) { if (FAILED(h)) throw std::runtime_error("D3D failure: " + std::to_string(h)); }
static const char* shader = R"(
cbuffer Clock : register(b0) { float time; float width; float height; float detail; };
struct Vertex { float4 pos:SV_POSITION; float2 uv:TEXCOORD; };
Vertex vs(uint i:SV_VertexID) { Vertex v; v.uv=float2((i<<1)&2,i&2); v.pos=float4(v.uv*float2(2,-2)+float2(-1,1),0,1); return v; }
uint hash(uint x) { x ^= x>>16; x*=0x7feb352d; x^=x>>15; x*=0x846ca68b; return x^(x>>16); }
float4 ps(Vertex v):SV_TARGET {
 float2 p=v.uv*float2(width,height)+float2(time*113,time*67);
 uint2 tile=(uint2)floor(p/max(detail,1));
 uint h=hash(tile.x+tile.y*8191);
 float3 grain=float3(h&255,(h>>8)&255,(h>>16)&255)/255;
 // Spatial texture translates smoothly; no full-screen brightness flashes.
 float mortar=step(.08,frac(p.x/96))*step(.08,frac(p.y/96));
 return float4((.15+.65*grain)*(.55+.45*mortar),1);
})";
int main(int argc,char** argv) {
 int seconds=120; float detail=2;
 if (argc>1) seconds=std::stoi(argv[1]); if (argc>2) detail=std::stof(argv[2]);
 if(seconds<1||seconds>900||detail<1||detail>32) return 2;
 HMODULE loader=LoadLibraryExW(L"C:\\Program Files (x86)\\Steam\\steamapps\\common\\SteamVR\\bin\\win64\\openvr_api.dll",nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
 if(!loader) return 3;
 using Init=uint32_t(__cdecl*)(vr::EVRInitError*,vr::EVRApplicationType,const char*);
 using Get=void*(__cdecl*)(const char*,vr::EVRInitError*);
 using Shut=void(__cdecl*)();
 auto init=(Init)GetProcAddress(loader,"VR_InitInternal2");auto get=(Get)GetProcAddress(loader,"VR_GetGenericInterface");auto shut=(Shut)GetProcAddress(loader,"VR_ShutdownInternal");
 vr::IVRSettings* settings=nullptr; bool oldPause=true; float oldTimeout=5; bool restore=false;
 int result=0;
 try {
  vr::EVRInitError e=vr::VRInitError_None;init(&e,vr::VRApplication_Scene,nullptr); if(e) throw std::runtime_error("OpenVR init "+std::to_string(e));
  auto sys=(vr::IVRSystem*)get(vr::IVRSystem_Version,&e);
  auto comp=(vr::IVRCompositor*)get(vr::IVRCompositor_Version,&e);
  settings=(vr::IVRSettings*)get(vr::IVRSettings_Version,&e);
  if(e||!sys||!comp||!settings) throw std::runtime_error("OpenVR interfaces unavailable");
  vr::EVRSettingsError se=vr::VRSettingsError_None;
  oldPause=settings->GetBool("power","pauseCompositorOnStandby",&se); if(se) throw std::runtime_error("Read standby");
  oldTimeout=settings->GetFloat("power","turnOffScreensTimeout",&se); if(se) throw std::runtime_error("Read timeout");
  restore=true;settings->SetBool("power","pauseCompositorOnStandby",false);settings->SetFloat("power","turnOffScreensTimeout",3600);
  std::cout<<"{\"event\":\"power-baseline\",\"pause\":"<<oldPause<<",\"timeout\":"<<oldTimeout<<"}"<<std::endl;
  int32_t index=0;sys->GetDXGIOutputInfo(&index);
  ComPtr<IDXGIFactory1> factory;check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));ComPtr<IDXGIAdapter1> adapter;check(factory->EnumAdapters1(index,&adapter));
  ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;check(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
  uint32_t w,h;sys->GetRecommendedRenderTargetSize(&w,&h);
  D3D11_TEXTURE2D_DESC td{};td.Width=w;td.Height=h;td.MipLevels=td.ArraySize=1;td.Format=DXGI_FORMAT_R8G8B8A8_UNORM;td.SampleDesc.Count=1;td.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> eyes[2];ComPtr<ID3D11RenderTargetView> rt[2];
  for(int i=0;i<2;i++){check(device->CreateTexture2D(&td,nullptr,&eyes[i]));check(device->CreateRenderTargetView(eyes[i].Get(),nullptr,&rt[i]));}
  ComPtr<ID3DBlob> vs,ps,errors;check(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&vs,&errors));check(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&ps,&errors));
  ComPtr<ID3D11VertexShader> v;ComPtr<ID3D11PixelShader> p;check(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&v));check(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&p));
  D3D11_BUFFER_DESC bd{};bd.ByteWidth=16;bd.Usage=D3D11_USAGE_DEFAULT;bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;ComPtr<ID3D11Buffer> cb;check(device->CreateBuffer(&bd,nullptr,&cb));
  D3D11_VIEWPORT viewport{0,0,(float)w,(float)h,0,1};context->RSSetViewports(1,&viewport);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(v.Get(),nullptr,0);context->PSSetShader(p.Get(),nullptr,0);ID3D11Buffer* buffer=cb.Get();context->PSSetConstantBuffers(0,1,&buffer);
  comp->FadeGrid(0,false);
  auto start=std::chrono::steady_clock::now();unsigned frames=0,invalid=0,submitErrors=0;int reported=-1;
  while(true){
   float t=std::chrono::duration<float>(std::chrono::steady_clock::now()-start).count();if(t>=seconds)break;
   vr::VREvent_t event{};bool quit=false;while(sys->PollNextEvent(&event,sizeof(event))) if(event.eventType==vr::VREvent_Quit)quit=true;if(quit)break;
   vr::TrackedDevicePose_t poses[vr::k_unMaxTrackedDeviceCount]{};comp->WaitGetPoses(poses,vr::k_unMaxTrackedDeviceCount,nullptr,0);if(!poses[0].bPoseIsValid)++invalid;
   float clock[4]={t,(float)w,(float)h,detail};context->UpdateSubresource(cb.Get(),0,nullptr,clock,0,0);
   for(int i=0;i<2;i++){ID3D11RenderTargetView* target=rt[i].Get();context->OMSetRenderTargets(1,&target,nullptr);context->Draw(3,0);vr::Texture_t texture{eyes[i].Get(),vr::TextureType_DirectX,vr::ColorSpace_Gamma};if(comp->Submit((vr::EVREye)i,&texture)!=vr::VRCompositorError_None)++submitErrors;}
   context->Flush();comp->PostPresentHandoff();++frames;
   if(int(t)/5!=reported){reported=int(t)/5;std::cout<<"{\"seconds\":"<<t<<",\"frames\":"<<frames<<",\"invalid_poses\":"<<invalid<<",\"submit_errors\":"<<submitErrors<<"}"<<std::endl;}
  }
 } catch(const std::exception& e){std::cerr<<e.what()<<std::endl;result=1;}
 if(restore&&settings){if(!settings->GetBool("power","pauseCompositorOnStandby"))settings->SetBool("power","pauseCompositorOnStandby",oldPause);if(settings->GetFloat("power","turnOffScreensTimeout")==3600)settings->SetFloat("power","turnOffScreensTimeout",oldTimeout);std::cout<<"{\"event\":\"power-restored\"}"<<std::endl;}
 if(shut)shut();FreeLibrary(loader);return result;
}
