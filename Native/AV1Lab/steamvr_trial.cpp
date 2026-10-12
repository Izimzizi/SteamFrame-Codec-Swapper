#define AV1_BACKEND_BUILD
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
static Av1TrialConfig budget{sizeof(Av1TrialConfig),3,100000000,115000000,0,120,1};

static NVENCSTATUS NVENCAPI intercept(NV_ENCODE_API_FUNCTION_LIST* table){
 HMODULE caller=nullptr;
 GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                   reinterpret_cast<LPCWSTR>(_ReturnAddress()),&caller);
 if(caller==driver&&table&&table->version==NV_ENCODE_API_FUNCTION_LIST_VER){
  const auto status=Av1CreateLegacyFunctionTable(table,sizeof(*table),budget.average_bps,budget.peak_bps);
  return status==0?NV_ENC_SUCCESS:NV_ENC_ERR_INVALID_VERSION;
 }
 return original(table);
}

extern "C" __declspec(dllexport) DWORD WINAPI Av1InstallTrial(void* argument){
 const auto requested=argument?*static_cast<Av1TrialConfig*>(argument):Av1TrialConfig{sizeof(Av1TrialConfig),3,100000000,115000000,0,120,1};
 if(requested.size!=sizeof(Av1TrialConfig)||requested.version!=3)return 19;
 if(installed)return memcmp(&requested,&budget,sizeof(budget))?19:0;
 wchar_t name[MAX_PATH]{};GetModuleFileNameW(nullptr,name,MAX_PATH);
 const auto* base=wcsrchr(name,L'\\');base=base?base+1:name;
 if(_wcsicmp(base,L"vrserver.exe"))return 10;
 // The HEVC-only monitor detours the same API and cannot translate modern AV1.
 if(GetModuleHandleW(L"SteamFrameEncoderProbe.dll")||GetModuleHandleW(L"SteamFrameQuality.dll"))return 18;
 driver=GetModuleHandleW(L"driver_vrlink.dll");if(!driver)return 11;
 if(Av1ConfigureLegacyBudget(requested.average_bps,requested.peak_bps,requested.fixed_target))return 19;
 if(Av1ConfigureLegacyRate(requested.fps))return 19;
 if(Av1ConfigureLegacyCodec(requested.codec))return 19;
 budget=requested;
 // Populate the real modern API before detouring its factory (no recursion).
 NV_ENCODE_API_FUNCTION_LIST prepare{};prepare.version=NV_ENCODE_API_FUNCTION_LIST_VER;
 if(Av1CreateLegacyFunctionTable(&prepare,sizeof(prepare),budget.average_bps,budget.peak_bps))return 12;
 nvenc=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);if(!nvenc)return 13;
 target=reinterpret_cast<void*>(GetProcAddress(nvenc,"NvEncodeAPICreateInstance"));if(!target)return 14;
 const auto initialized=MH_Initialize();if(initialized!=MH_OK&&initialized!=MH_ERROR_ALREADY_INITIALIZED)return 15;
 if(MH_CreateHook(target,reinterpret_cast<void*>(intercept),reinterpret_cast<void**>(&original))!=MH_OK)return 16;
 if(MH_EnableHook(target)!=MH_OK){MH_RemoveHook(target);return 17;}
 installed=true;return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI Av1RemoveTrial(void*){
 if(!installed)return 0;
 Av1BridgeStats state{sizeof(state),1};Av1GetLegacyBridgeStats(&state);
 // Existing tables/sessions retain callbacks; keep this DLL loaded until exit.
 if(MH_DisableHook(target)!=MH_OK)return 20;
 installed=false;return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI Av1ReadTrialStats(void* output){
 return Av1GetLegacyBridgeStats(static_cast<Av1BridgeStats*>(output))?1:0;
}
extern "C" __declspec(dllexport) DWORD WINAPI Av1ReadTrialIdentity(void* output){
 return Av1GetLegacyIdentity(static_cast<Av1TrialIdentity*>(output))?1:0;
}
