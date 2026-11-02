#define HOOK_BUILD
#include <windows.h>
#include <intrin.h>
#include <cstdio>
#include <cstring>
#include <string>
#include "nvEncodeAPI11.h"
#include "legacy_api_bridge.h"
#include "MinHook.h"

static HMODULE driver=nullptr,nvenc=nullptr;
static void* target=nullptr;
using Create=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
static Create original=nullptr;
static bool installed=false;
// True when SteamVR loaded this DLL as an external driver at start-up (as opposed to the supervisor injecting it).
static bool resident=false;
static HookTrialConfig budget{sizeof(HookTrialConfig),5,100000000,115000000,0,120,0,2,1};

static NVENCSTATUS NVENCAPI intercept(NV_ENCODE_API_FUNCTION_LIST* table){
 HMODULE caller=nullptr;
 GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                   reinterpret_cast<LPCWSTR>(_ReturnAddress()),&caller);
 if(caller==driver&&table&&table->version==NV_ENCODE_API_FUNCTION_LIST_VER){
  // The driver's own SDK 11.1 encoder, which the bridge opens beside the H.264 one so the codec can change while SteamVR runs.
  NV_ENCODE_API_FUNCTION_LIST stock{};stock.version=table->version;
  if(original(&stock)==NV_ENC_SUCCESS)HookProvideStockTable(&stock,sizeof(stock),reinterpret_cast<void*>(original));
  const auto status=HookCreateLegacyFunctionTable(table,sizeof(*table),budget.average_bps,budget.peak_bps);
  return status==0?NV_ENC_SUCCESS:NV_ENC_ERR_INVALID_VERSION;
 }
 return original(table);
}

static DWORD install(const HookTrialConfig& requested,bool pin_rate){
 if(installed)return 0;
 wchar_t name[MAX_PATH]{};GetModuleFileNameW(nullptr,name,MAX_PATH);
 const auto* base=wcsrchr(name,L'\\');base=base?base+1:name;
 if(_wcsicmp(base,L"vrserver.exe"))return 10;
 // The HEVC-only monitor detours the same API and cannot translate the modern API.
 if(GetModuleHandleW(L"SteamFrameEncoderProbe.dll")||GetModuleHandleW(L"SteamFrameQuality.dll"))return 18;
 driver=GetModuleHandleW(L"driver_vrlink.dll");if(!driver)return 11;
 if(HookConfigureLegacyEncoder(requested.rate_control,requested.preset,requested.vbv_frames))return 19;
 if(HookConfigureLegacyBudget(requested.average_bps,requested.peak_bps,requested.fixed_target))return 19;
 // A resident driver cannot know the Frame's refresh rate yet: each session takes the rate SteamVR starts it with.
 if(pin_rate&&HookConfigureLegacyRate(requested.fps))return 19;
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

extern "C" __declspec(dllexport) DWORD WINAPI HookInstallTrial(void* argument){
 const auto requested=argument?*static_cast<HookTrialConfig*>(argument):HookTrialConfig{sizeof(HookTrialConfig),5,100000000,115000000,0,120,0,2,1};
 if(requested.size!=sizeof(HookTrialConfig)||requested.version!=5)return 19;
 if(installed){
  if(!resident)return memcmp(&requested,&budget,sizeof(budget))?19:0;
  // The driver is already in place with its own defaults: arming means H.264 from here on, with the requested encoder options.
  if(HookQueueLegacyEncoder(requested.rate_control,requested.preset,requested.vbv_frames))return 19;
  HookSetDefaultLegacyMode(0);
  budget.rate_control=requested.rate_control;budget.preset=requested.preset;budget.vbv_frames=requested.vbv_frames;
  return 0;
 }
 return install(requested,true);
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
// Chooses the codec of the frames SteamVR encodes next: argument is a HookSwitchInfo whose mode is 0 (H.264) or 1 (stock HEVC);
// it comes back filled in with the counters.
extern "C" __declspec(dllexport) DWORD WINAPI HookSwitchMode(void* argument){
 if(!installed)return 21;
 auto* info=static_cast<HookSwitchInfo*>(argument);
 if(!info||info->size!=sizeof(HookSwitchInfo)||info->version!=1)return 19;
 const auto status=HookSetLegacyMode(info->mode);
 if(HookGetLegacySwitchInfo(info))return 19;
 return status==0?0:static_cast<DWORD>(30-status);
}
extern "C" __declspec(dllexport) DWORD WINAPI HookReadSwitchInfo(void* output){
 return HookGetLegacySwitchInfo(static_cast<HookSwitchInfo*>(output))?1:0;
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

// ---- SteamVR driver entry -------------------------------------------------------------------------------------------
// Registered once as an external driver, this makes SteamVR load the DLL when vrserver starts, before the Frame connects and
// SteamVR first asks for an encoder. It reports no devices. The codec starts as stock HEVC; the supervisor (or nothing at all)
// decides when to switch.
static FILE* driver_log=nullptr;
static std::string data_folder(){
 char base[MAX_PATH]{};
 if(!GetEnvironmentVariableA("LOCALAPPDATA",base,MAX_PATH))return {};
 const std::string directory=std::string(base)+"\\CodecSwapper";CreateDirectoryA(directory.c_str(),nullptr);
 return directory;
}
static void driver_note(const char* text){
 if(!driver_log){
  const auto directory=data_folder();if(directory.empty())return;
  driver_log=fopen((directory+"\\driver.log").c_str(),"a");if(!driver_log)return;
 }
 SYSTEMTIME time;GetLocalTime(&time);
 fprintf(driver_log,"%02d:%02d:%02d.%03d pid %lu %s\n",time.wHour,time.wMinute,time.wSecond,time.wMilliseconds,GetCurrentProcessId(),text);
 fflush(driver_log);
}
// An optional lower H.264 size limit: a text file "<width>x<height>" (or just the width) in the tray's data folder.
static void read_h264_limit(){
 const auto directory=data_folder();if(directory.empty())return;
 FILE* file=fopen((directory+"\\h264-limit.txt").c_str(),"r");if(!file)return;
 unsigned width=0,height=0;const int count=fscanf(file,"%ux%u",&width,&height);fclose(file);
 if(count>=1&&HookConfigureH264Limit(width,count>=2?height:0)==0){
  char note[96];snprintf(note,sizeof(note),"H.264 size limit %ux%u from h264-limit.txt",width,count>=2?height:0);driver_note(note);
 }
}
// ---- Stream width, changed from inside vrserver ------------------------------------------------------------------------------
// H.264 can take a stream no wider than 1024 px, so while it is the codec the width is lowered and the user's own value is put
// back afterwards. The original is kept in a file so a crash of SteamVR or the tray cannot leave the lowered value behind.
static void* settings=nullptr;
// While H.264 is the codec the driver's stream width is never read wider than 1024: the settings object's GetInt32 is wrapped, so a
// wider value typed into SteamVR's settings is not acted on at all (no new layout, no glitch). The stored value is left as it is.
using GetInt32Function=int(*)(void*,const char*,const char*,int*);
static GetInt32Function original_get_int=nullptr;
static int hooked_get_int(void* self,const char* section,const char* key,int* error){
 const int value=original_get_int(self,section,key,error);
 if(section&&key&&!strcmp(section,"driver_vrlink")&&!strcmp(key,"streamFormatWidth")&&value>1024&&HookIsH264Selected())return 1024;
 return value;
}
static void wrap_get_int(){
 if(!settings||original_get_int)return;
 void** slot=&(*static_cast<void***>(settings))[6];
 DWORD old=0;if(!VirtualProtect(slot,sizeof(void*),PAGE_READWRITE,&old))return;
 original_get_int=reinterpret_cast<GetInt32Function>(*slot);*slot=reinterpret_cast<void*>(&hooked_get_int);
 VirtualProtect(slot,sizeof(void*),old,&old);
}
static const char* const vrlink="driver_vrlink";
static std::string restore_path(){const auto directory=data_folder();return directory.empty()?std::string():directory+"\\width-restore.txt";}
static int settings_get_int(const char* key,bool* ok){
 int error=0;auto* table=*static_cast<void***>(settings);
 const auto value=reinterpret_cast<int(*)(void*,const char*,const char*,int*)>(table[6])(settings,vrlink,key,&error);
 if(ok)*ok=error==0;return value;
}
static bool settings_get_bool(const char* key,bool* ok){
 int error=0;auto* table=*static_cast<void***>(settings);
 const auto value=reinterpret_cast<bool(*)(void*,const char*,const char*,int*)>(table[5])(settings,vrlink,key,&error);
 if(ok)*ok=error==0;return value;
}
static void settings_set_int(const char* key,int value){
 int error=0;auto* table=*static_cast<void***>(settings);
 reinterpret_cast<void(*)(void*,const char*,const char*,int,int*)>(table[2])(settings,vrlink,key,value,&error);
}
static void settings_set_bool(const char* key,bool value){
 int error=0;auto* table=*static_cast<void***>(settings);
 reinterpret_cast<void(*)(void*,const char*,const char*,bool,int*)>(table[1])(settings,vrlink,key,value,&error);
}
static bool read_restore(int& width,int& automatic){
 const auto path=restore_path();if(path.empty())return false;
 FILE* file=fopen(path.c_str(),"r");if(!file)return false;
 const int count=fscanf(file,"%d %d",&width,&automatic);fclose(file);return count==2;
}
static void restore_width(){
 int width=0,automatic=0;
 if(!settings||!read_restore(width,automatic))return;
 if(width>0)settings_set_int("streamFormatWidth",width);
 settings_set_bool("automaticStreamFormatWidth",automatic!=0);
 DeleteFileA(restore_path().c_str());
 char note[96];snprintf(note,sizeof(note),"stream width restored to %d (automatic %d)",width,automatic);driver_note(note);
}
// SteamVR asked for a wider stream while H.264 is the codec (the settings page allows it): put the width back to what H.264 takes.
// Run on its own thread, at most once a second, so the encoder thread never waits on the settings.
static DWORD WINAPI clamp_width_thread(void*){
 settings_set_bool("automaticStreamFormatWidth",false);
 settings_set_int("streamFormatWidth",1024);
 driver_note("stream width asked above 1024 while H.264 is the codec: set back to 1024");
 return 0;
}
static void clamp_width(){
 static ULONGLONG last=0;const auto now=GetTickCount64();
 if(!settings||(last&&now-last<1000))return;
 last=now;HANDLE thread=CreateThread(nullptr,0,clamp_width_thread,nullptr,0,nullptr);if(thread)CloseHandle(thread);
}
extern "C" __declspec(dllexport) DWORD WINAPI HookStreamWidth(void* argument){
 auto* info=static_cast<HookWidthInfo*>(argument);
 if(!info||info->size!=sizeof(HookWidthInfo)||info->version!=1)return 19;
 info->settings_ready=settings?1:0;
 if(!settings)return 40;
 int saved_width=0,saved_automatic=0;
 info->pending_restore=read_restore(saved_width,saved_automatic)?1:0;
 if(info->action==1){
  bool ok=false;
  if(!info->pending_restore){
   const int width=settings_get_int("streamFormatWidth",&ok);const bool known=ok;
   const bool automatic=settings_get_bool("automaticStreamFormatWidth",&ok);
   saved_width=known?width:0;saved_automatic=automatic?1:0;
   const auto path=restore_path();FILE* file=path.empty()?nullptr:fopen(path.c_str(),"w");
   if(!file)return 41;
   fprintf(file,"%d %d\n",saved_width,saved_automatic);fclose(file);info->pending_restore=1;
  }
  int target_width=info->width>0?info->width:1024;
  if(saved_width>0&&saved_width<target_width)target_width=saved_width;
  target_width&=~31;
  settings_set_bool("automaticStreamFormatWidth",false);
  settings_set_int("streamFormatWidth",target_width);
  char note[96];snprintf(note,sizeof(note),"stream width set to %d for H.264 (user value %d)",target_width,saved_width);driver_note(note);
 }else if(info->action==2)restore_width();
 info->pending_restore=read_restore(saved_width,saved_automatic)?1:0;
 info->original_width=saved_width;info->original_automatic=saved_automatic;
 bool ok=false;
 info->width=settings_get_int("streamFormatWidth",&ok);info->automatic=settings_get_bool("automaticStreamFormatWidth",&ok)?1:0;
 return 0;
}
namespace {
struct Provider{
 virtual int Init(void* context){
  read_h264_limit();
  HookSetOversizeHandler(clamp_width);
  if(context){
   int error=0;auto* table=*static_cast<void***>(context);
   settings=reinterpret_cast<void*(*)(void*,const char*,int*)>(table[0])(context,"IVRSettings_003",&error);
   if(error)settings=nullptr;
   wrap_get_int();
   driver_note(settings?"driver Init: settings interface available":"driver Init: settings interface unavailable");
   // A previous session may have ended while the width was lowered.
   restore_width();
  }
  const HookTrialConfig defaults{sizeof(HookTrialConfig),5,500000000,500000000,0,90,0,2,2};
  const auto code=install(defaults,false);
  if(!code){resident=true;HookSetDefaultLegacyMode(1);}
  char note[96];snprintf(note,sizeof(note),"driver Init: hook %s (code %lu), starting as stock HEVC",code?"not installed":"installed",code);driver_note(note);
  return 0; // never fail SteamVR's start over this
 }
 virtual void Cleanup(){restore_width();driver_note("driver Cleanup");}
 virtual const char* const* GetInterfaceVersions(){static const char* const none[]={nullptr};return none;}
 virtual void RunFrame(){}
 virtual bool ShouldBlockStandbyMode(){return false;}
 virtual void EnterStandby(){}
 virtual void LeaveStandby(){}
};
Provider provider;
}
extern "C" __declspec(dllexport) void* HmdDriverFactory(const char* name,int* code){
 if(name&&!strcmp(name,"IServerTrackedDeviceProvider_004"))return &provider;
 if(code)*code=105; // VRInitError_Init_InterfaceNotFound
 return nullptr;
}
