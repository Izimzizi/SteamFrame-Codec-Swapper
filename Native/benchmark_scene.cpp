// Original benchmark scene, submitted through SteamVR's public compositor API.
// The parent owns all settings. This process changes no SteamVR settings.
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <avrt.h>
#include <atomic>
#include <chrono>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include "openvr.h"
using Microsoft::WRL::ComPtr;
static std::atomic<bool> stop{false};
static std::atomic<ULONGLONG> heartbeat{0};
// Keep the submitting thread in Windows' bounded multimedia scheduling class.
// Unlike process-wide high/realtime priority, this is scoped to the owned scene.
struct FrameScheduling {
 HANDLE task=nullptr;
 FrameScheduling(){DWORD index=0;task=AvSetMmThreadCharacteristicsW(L"Games",&index);
  if(task)AvSetMmThreadPriority(task,AVRT_PRIORITY_HIGH);}
 ~FrameScheduling(){if(task)AvRevertMmThreadCharacteristics(task);}
};
static void check(HRESULT h) { if (FAILED(h)) throw std::runtime_error("D3D initialization failed"); }
static const char* shader = R"(
cbuffer View : register(b0) { float4 eye; float4 right; float4 up; float4 back; float4 frustum; float4 clock; };
struct Vertex { float4 pos:SV_POSITION; float2 uv:TEXCOORD; };
Vertex vs(uint i:SV_VertexID) { Vertex v; v.uv=float2((i<<1)&2,i&2); v.pos=float4(v.uv*float2(2,-2)+float2(-1,1),0,1); return v; }
uint hash(uint x) { x^=x>>16; x*=0x7feb352d; x^=x>>15; x*=0x846ca68b; return x^(x>>16); }
float4 ps(Vertex v):SV_TARGET {
 float3 ray=normalize(right.xyz*lerp(frustum.x,frustum.y,v.uv.x)-up.xyz*lerp(frustum.z,frustum.w,v.uv.y)-back.xyz);
 // World-fixed room, correct per-eye projection and tracked head pose.
 float t=100; float2 surface=0; float shade=.8;
 if(abs(ray.z)>.0001) { float a=((ray.z<0?-5:5)-eye.z)/ray.z; if(a>0&&a<t){t=a;surface=(eye+float4(ray*t,0)).xy;shade=.9;} }
 if(abs(ray.x)>.0001) { float a=((ray.x<0?-5:5)-eye.x)/ray.x; if(a>0&&a<t){t=a;surface=(eye+float4(ray*t,0)).zy;shade=.75;} }
 if(abs(ray.y)>.0001) { float a=((ray.y<0?-.1:4)-eye.y)/ray.y; if(a>0&&a<t){t=a;surface=(eye+float4(ray*t,0)).xz;shade=.6;} }
 // Repeating 12-second workload: mixed fine texture, larger tiles and moving edges.
 float phase=fmod(clock.x,12); float scale=phase<4?100:(phase<8?200:320);
 float2 p=(surface+float2(clock.x*.05,0))*scale;
 int2 tile=(int2)floor(p); uint h=hash(asuint(tile.x)+asuint(tile.y)*8191);
 float3 grain=float3(h&255,(h>>8)&255,(h>>16)&255)/255;
 float lines=step(.04,frac(surface.x*2))*step(.04,frac(surface.y*2));
 float3 color=(.18+.42*grain)*(.6+.4*lines)*shade;
 // A stable horizon and panel borders give a clear visual tracking reference.
 color=lerp(color,float3(.12,.45,.55),1-step(.025,abs(frac(surface.y)-.5)));
 return float4(color,1);
})";
int wmain(int argc, wchar_t** argv) {
 if(argc<2 || argc>3) return 2;
 if(std::wstring(argv[1])==L"--self-test") {
  ComPtr<ID3DBlob> code,errors;
  if(FAILED(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&code,&errors)))return 10;
  code.Reset();errors.Reset();
  if(FAILED(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&code,&errors)))return 11;
  std::cout<<"Benchmark shaders compiled successfully"<<std::endl;return 0;
 }
 if(argc!=3 || (std::wstring(argv[2])!=L"--unattended" && std::wstring(argv[2])!=L"--tracked"))return 2;
 const bool unattended=std::wstring(argv[2])==L"--unattended";
 HMODULE loader=LoadLibraryExW(argv[1],nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
 if(!loader) return 3;
 using Init=uint32_t(__cdecl*)(vr::EVRInitError*,vr::EVRApplicationType,const char*);
 using Get=void*(__cdecl*)(const char*,vr::EVRInitError*);
 using Shut=void(__cdecl*)();
 auto init=(Init)GetProcAddress(loader,"VR_InitInternal2"); auto get=(Get)GetProcAddress(loader,"VR_GetGenericInterface");
 auto shut=(Shut)GetProcAddress(loader,"VR_ShutdownInternal");
 if(!init||!get||!shut) { FreeLibrary(loader); return 4; }
 bool initialized=false; int result=0;
 try {
  vr::EVRInitError e=vr::VRInitError_None; init(&e,vr::VRApplication_Scene,nullptr);
  if(e) throw std::runtime_error("OpenVR scene initialization failed"); initialized=true;
  auto sys=(vr::IVRSystem*)get(vr::IVRSystem_Version,&e);
  auto comp=(vr::IVRCompositor*)get(vr::IVRCompositor_Version,&e);
  if(e||!sys||!comp) throw std::runtime_error("OpenVR scene interfaces unavailable");
  int32_t index=0; sys->GetDXGIOutputInfo(&index);
  ComPtr<IDXGIFactory1> factory; check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
  ComPtr<IDXGIAdapter1> adapter; check(factory->EnumAdapters1(index,&adapter));
  ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
  check(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
  uint32_t w,h; sys->GetRecommendedRenderTargetSize(&w,&h);
  if(w<256||h<256||w>8192||h>8192) throw std::runtime_error("Unsupported render dimensions");
  D3D11_TEXTURE2D_DESC td{}; td.Width=w; td.Height=h; td.MipLevels=td.ArraySize=1;
  td.Format=DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count=1; td.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> eyes[2]; ComPtr<ID3D11RenderTargetView> rt[2];
  for(int i=0;i<2;i++){check(device->CreateTexture2D(&td,nullptr,&eyes[i]));check(device->CreateRenderTargetView(eyes[i].Get(),nullptr,&rt[i]));}
  ComPtr<ID3DBlob> vs,ps,errors; check(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&vs,&errors));
  check(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&ps,&errors));
  ComPtr<ID3D11VertexShader> v; ComPtr<ID3D11PixelShader> p;
  check(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&v));
  check(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&p));
  D3D11_BUFFER_DESC bd{}; bd.ByteWidth=96; bd.Usage=D3D11_USAGE_DEFAULT; bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
  ComPtr<ID3D11Buffer> cb; check(device->CreateBuffer(&bd,nullptr,&cb));
  D3D11_VIEWPORT viewport{0,0,(float)w,(float)h,0,1}; context->RSSetViewports(1,&viewport);
  context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST); context->VSSetShader(v.Get(),nullptr,0); context->PSSetShader(p.Get(),nullptr,0);
  ID3D11Buffer* buffer=cb.Get(); context->PSSetConstantBuffers(0,1,&buffer);
  heartbeat=GetTickCount64();
  std::thread([]{std::string line;while(std::getline(std::cin,line)){if(line=="stop")break;if(line=="heartbeat")heartbeat=GetTickCount64();}stop=true;}).detach();
  // Also bounds a blocked WaitGetPoses if the controlling app crashes.
  std::thread([]{while(!stop){Sleep(500);if(GetTickCount64()-heartbeat>12000)ExitProcess(0);}}).detach();
  const auto start=std::chrono::steady_clock::now();
  FrameScheduling scheduling;
  double last=0,reported=-1,previousWork=0,longest=0,longWait=0,longWork=0; unsigned long long frames=0,invalid=0,submitErrors=0,longIntervals=0;
  while(!stop && GetTickCount64()-heartbeat<10000) {
   double t=std::chrono::duration<double>(std::chrono::steady_clock::now()-start).count(); if(t>=7200)break;
   vr::VREvent_t event{}; while(sys->PollNextEvent(&event,sizeof(event))) if(event.eventType==vr::VREvent_Quit)stop=true;
   if(stop) break;
   vr::TrackedDevicePose_t poses[vr::k_unMaxTrackedDeviceCount]{};
   const auto waitStart=std::chrono::steady_clock::now();
   if(comp->WaitGetPoses(poses,vr::k_unMaxTrackedDeviceCount,nullptr,0)!=vr::VRCompositorError_None)++submitErrors;
   t=std::chrono::duration<double>(std::chrono::steady_clock::now()-start).count();
   const float fps=sys->GetFloatTrackedDeviceProperty(vr::k_unTrackedDeviceIndex_Hmd,vr::Prop_DisplayFrequency_Float);
   if(last>0 && t-last>1.75/(fps>0?fps:90)){++longIntervals;
    if((t-last)*1000>longest){longest=(t-last)*1000;longWork=previousWork;longWait=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-waitStart).count();}}
   last=t;
   const bool tracked=poses[0].bPoseIsValid && poses[0].bDeviceIsConnected;
   if(!tracked) ++invalid;
   auto pose=poses[0].mDeviceToAbsoluteTracking;
   if(!tracked){pose={};pose.m[0][0]=pose.m[1][1]=pose.m[2][2]=1;pose.m[1][3]=1.6f;}
   auto camera=pose;
   // In unattended mode camera motion is part of the rendered world, while the
   // submitted physical pose remains explicit for the compositor's reprojection.
   if(unattended){float angle=.25f*std::sin((float)t*.35f);float c=std::cos(angle),s=std::sin(angle);
    for(int row=0;row<3;row++){camera.m[row][0]=pose.m[row][0]*c-pose.m[row][2]*s;camera.m[row][2]=pose.m[row][0]*s+pose.m[row][2]*c;}}
   for(int i=0;(tracked || unattended) && i<2;i++) {
    auto eyePose=sys->GetEyeToHeadTransform((vr::EVREye)i); float view[24]{};
    for(int row=0;row<3;row++) {
     view[row]=camera.m[row][3]; for(int k=0;k<3;k++)view[row]+=camera.m[row][k]*eyePose.m[k][3];
     for(int col=0;col<3;col++)for(int k=0;k<3;k++)view[4+col*4+row]+=camera.m[row][k]*eyePose.m[k][col];
    }
    sys->GetProjectionRaw((vr::EVREye)i,&view[16],&view[17],&view[18],&view[19]);view[20]=(float)t;
    context->UpdateSubresource(cb.Get(),0,nullptr,view,0,0);
    ID3D11RenderTargetView* target=rt[i].Get();context->OMSetRenderTargets(1,&target,nullptr);context->Draw(3,0);
    vr::VRTextureWithPose_t texture{};texture.handle=eyes[i].Get();texture.eType=vr::TextureType_DirectX;
    texture.eColorSpace=vr::ColorSpace_Gamma;texture.mDeviceToAbsoluteTracking=pose;
    if(comp->Submit((vr::EVREye)i,&texture,nullptr,vr::Submit_TextureWithPose)!=vr::VRCompositorError_None)++submitErrors;
   }
   if(tracked || unattended){context->Flush();comp->PostPresentHandoff();++frames;}
   if(t-reported>=.5) {reported=t;std::cout<<"{\"Seconds\":"<<t<<",\"Frames\":"<<frames<<",\"InvalidPoses\":"<<invalid
    <<",\"SubmitErrors\":"<<submitErrors<<",\"LongIntervals\":"<<longIntervals<<",\"LongestIntervalMs\":"<<longest<<",\"LongWaitMs\":"<<longWait<<",\"LongWorkMs\":"<<longWork<<",\"Fps\":"<<(int)std::lround(fps)<<",\"Tracked\":"<<(tracked?"true":"false")<<",\"Unattended\":"<<(unattended?"true":"false")<<"}"<<std::endl;}
   previousWork=(std::chrono::duration<double>(std::chrono::steady_clock::now()-start).count()-t)*1000;
  }
 } catch(const std::exception& error) {std::cerr<<error.what()<<std::endl;result=1;}
 if(initialized)shut();FreeLibrary(loader);return result;
}
