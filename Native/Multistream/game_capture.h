#pragma once
#include "openvr.h"
#include <dxgi.h>
#include <cmath>
#include <filesystem>
// SteamVR's public undistorted eye textures are captured on its graphics
// adapter. Valve's existing connection keeps input, tracking and audio alive.
class GameCapture {
 HMODULE loader_=nullptr;using Shut=void(__cdecl*)();Shut shutdown_=nullptr;
 vr::IVRSystem* system_=nullptr;vr::IVRCompositor* compositor_=nullptr;vr::IVROverlay* overlays_=nullptr;
 std::array<ID3D11ShaderResourceView*,2> eyes_{};
public:
 uint64_t repeats=0,captured=0,gaze_frames=0,skipped=0;uint32_t previous=0;
 GameCapture(){
  wchar_t path[32768]{};DWORD length=GetEnvironmentVariableW(L"STEAMVR_RUNTIME",path,32768);
  std::filesystem::path root=length&&length<32768?path:L"C:\\Program Files (x86)\\Steam\\steamapps\\common\\SteamVR";
  loader_=LoadLibraryExW((root/L"bin/win64/openvr_api.dll").c_str(),nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
  if(!loader_)throw std::runtime_error("Installed SteamVR loader unavailable; set STEAMVR_RUNTIME");
  using Init=uint32_t(__cdecl*)(vr::EVRInitError*,vr::EVRApplicationType,const char*);
  using Get=void*(__cdecl*)(const char*,vr::EVRInitError*);
  auto init=reinterpret_cast<Init>(GetProcAddress(loader_,"VR_InitInternal2"));
  auto get=reinterpret_cast<Get>(GetProcAddress(loader_,"VR_GetGenericInterface"));
  shutdown_=reinterpret_cast<Shut>(GetProcAddress(loader_,"VR_ShutdownInternal"));
  if(!init||!get||!shutdown_)throw std::runtime_error("SteamVR exports unavailable");
  vr::EVRInitError error=vr::VRInitError_None;init(&error,vr::VRApplication_Overlay,nullptr);
  if(error)throw std::runtime_error("SteamVR capture initialization: "+std::to_string(error));
  system_=static_cast<vr::IVRSystem*>(get(vr::IVRSystem_Version,&error));
  compositor_=static_cast<vr::IVRCompositor*>(get(vr::IVRCompositor_Version,&error));
  overlays_=static_cast<vr::IVROverlay*>(get(vr::IVROverlay_Version,&error));
  if(error||!system_||!compositor_||!overlays_)throw std::runtime_error("SteamVR capture interfaces unavailable");
 }
 void device(ComPtr<ID3D11Device>& device,ComPtr<ID3D11DeviceContext>& context){
  int32_t index=-1;system_->GetDXGIOutputInfo(&index);
  ComPtr<IDXGIFactory1> factory;ok(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
  ComPtr<IDXGIAdapter1> adapter;ok(factory->EnumAdapters1(index,&adapter));
  ok(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
  for(unsigned i=0;i<2;++i){auto error=compositor_->GetMirrorTextureD3D11(static_cast<vr::EVREye>(i),device.Get(),reinterpret_cast<void**>(&eyes_[i]));
   if(error||!eyes_[i])throw std::runtime_error("SteamVR eye texture unavailable: "+std::to_string(error));}
 }
 ID3D11ShaderResourceView* eye(unsigned stream,unsigned streams)const{return eyes_.at(stream/(streams/2));}
 split::GameFrame frame(){
  auto result=overlays_->WaitFrameSync(100);
  if(result)throw std::runtime_error("SteamVR capture frame sync: "+std::to_string(result));
  vr::Compositor_FrameTiming timing{};timing.m_nSize=sizeof(timing);
  const bool timing_ok=compositor_->GetFrameTiming(&timing);
  if(!timing_ok||!timing.m_HmdPose.bPoseIsValid){
   vr::TrackedDevicePose_t current{};system_->GetDeviceToAbsoluteTrackingPose(compositor_->GetTrackingSpace(),0,&current,1);
   std::cerr<<"capture timing="<<timing_ok<<" frame="<<timing.m_nFrameIndex<<" render_pose="<<timing.m_HmdPose.bPoseIsValid<<" live_pose="<<current.bPoseIsValid<<" tracking="<<current.eTrackingResult<<" connected="<<current.bDeviceIsConnected<<'\n';
   throw std::runtime_error("No valid SteamVR rendered pose");
  }
  split::GameFrame info;info.frame_index=timing.m_nFrameIndex;info.origin=compositor_->GetTrackingSpace();
  memcpy(info.pose,timing.m_HmdPose.mDeviceToAbsoluteTracking.m,sizeof(info.pose));
  for(unsigned i=0;i<2;++i){auto* p=info.projection+i*4;system_->GetProjectionRaw(static_cast<vr::EVREye>(i),p,p+1,p+2,p+3);}
  vr::HmdVector2_t left{},right{};info.gaze_valid=system_->GetEyeTrackedFoveationCenter(&left,&right);
  if(info.gaze_valid){info.gaze[0]=left.v[0];info.gaze[1]=left.v[1];info.gaze[2]=right.v[0];info.gaze[3]=right.v[1];++gaze_frames;}
  info.validate();if(captured>1){if(previous==info.frame_index)++repeats;
   else if(info.frame_index>previous+1)skipped+=info.frame_index-previous-1;}
  previous=info.frame_index;++captured;return info;
 }
 ~GameCapture(){if(compositor_)for(auto* eye:eyes_)if(eye)compositor_->ReleaseMirrorTextureD3D11(eye);if(shutdown_)shutdown_();if(loader_)FreeLibrary(loader_);}
};
