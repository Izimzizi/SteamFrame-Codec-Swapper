#define HOOK_BUILD
#include <windows.h>
#include <intrin.h>
#include <cstring>
#include "nvEncodeAPI11.h"
#include "legacy_api_bridge.h"
#include "MinHook.h"

static HMODULE driver=nullptr,nvenc=nullptr;
static void* target=nullptr;
using Create=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
static Create original=nullptr;
static bool installed=false;
static HookTrialConfig budget{sizeof(HookTrialConfig),5,100000000,115000000,0,120,0,2,1};

static NVENCSTATUS NVENCAPI intercept(NV_ENCODE_API_FUNCTION_LIST* table){
 HMODULE caller=nullptr;
 GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                   reinterpret_cast<LPCWSTR>(_ReturnAddress()),&caller);
 if(caller==driver&&table&&table->version==NV_ENCODE_API_FUNCTION_LIST_VER){
  const auto status=HookCreateLegacyFunctionTable(table,sizeof(*table),budget.average_bps,budget.peak_bps);
  return status==0?NV_ENC_SUCCESS:NV_ENC_ERR_INVALID_VERSION;
 }
 return original(table);
}

extern "C" __declspec(dllexport) DWORD WINAPI HookInstallTrial(void* argument){
 const auto requested=argument?*static_cast<HookTrialConfig*>(argument):HookTrialConfig{sizeof(HookTrialConfig),5,100000000,115000000,0,120,0,2,1};
 if(requested.size!=sizeof(HookTrialConfig)||requested.version!=5)return 19;
 if(installed)return memcmp(&requested,&budget,sizeof(budget))?19:0;
 wchar_t name[MAX_PATH]{};GetModuleFileNameW(nullptr,name,MAX_PATH);
 const auto* base=wcsrchr(name,L'\\');base=base?base+1:name;
 if(_wcsicmp(base,L"vrserver.exe"))return 10;
 // The HEVC-only monitor detours the same API and cannot translate the modern API.
 if(GetModuleHandleW(L"SteamFrameEncoderProbe.dll")||GetModuleHandleW(L"SteamFrameQuality.dll"))return 18;
 driver=GetModuleHandleW(L"driver_vrlink.dll");if(!driver)return 11;
 if(HookConfigureLegacyEncoder(requested.rate_control,requested.preset,requested.vbv_frames))return 19;
 if(HookConfigureLegacyBudget(requested.average_bps,requested.peak_bps,requested.fixed_target))return 19;
 if(HookConfigureLegacyRate(requested.fps))return 19;
 budget=requested;
 // Populate the real modern API before detouring its factory (no recursion).
 NV_ENCODE_API_FUNCTION_LIST prepare{};prepare.version=NV_ENCODE_API_FUNCTION_LIST_VER;
 if(HookCreateLegacyFunctionTable(&prepare,sizeof(prepare),budget.average_bps,budget.peak_bps))return 12;
 nvenc=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);if(!nvenc)return 13;
 target=reinterpret_cast<void*>(GetProcAddress(nvenc,"NvEncodeAPICreateInstance"));if(!target)return 14;
 const auto initialized=MH_Initialize();if(initialized!=MH_OK&&initialized!=MH_ERROR_ALREADY_INITIALIZED)return 15;
 if(MH_CreateHook(target,reinterpret_cast<void*>(intercept),reinterpret_cast<void**>(&original))!=MH_OK)return 16;
 if(MH_EnableHook(target)!=MH_OK){MH_RemoveHook(target);return 17;}
 installed=true;return 0;
}
// Queues new encoder options on the resident hook; they apply when SteamVR next opens an encoder session.
extern "C" __declspec(dllexport) DWORD WINAPI HookReconfigure(void* argument){
 if(!installed)return 21;
 const auto requested=argument?*static_cast<HookTrialConfig*>(argument):budget;
 if(requested.size!=sizeof(HookTrialConfig)||requested.version!=5)return 19;
 if(HookQueueLegacyEncoder(requested.rate_control,requested.preset,requested.vbv_frames))return 19;
 budget.rate_control=requested.rate_control;budget.preset=requested.preset;budget.vbv_frames=requested.vbv_frames;
 return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI HookRemoveTrial(void*){
 if(!installed)return 0;
 HookBridgeStats state{sizeof(state),1};HookGetLegacyBridgeStats(&state);
 // Existing tables/sessions retain callbacks; keep this DLL loaded until exit.
 if(MH_DisableHook(target)!=MH_OK)return 20;
 installed=false;return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI HookReadTrialStats(void* output){
 return HookGetLegacyBridgeStats(static_cast<HookBridgeStats*>(output))?1:0;
}
extern "C" __declspec(dllexport) DWORD WINAPI HookReadTrialIdentity(void* output){
 return HookGetLegacyIdentity(static_cast<HookTrialIdentity*>(output))?1:0;
}
