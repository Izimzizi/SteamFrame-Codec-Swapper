// Throwaway probe: does SteamVR load a deviceless external driver, and does it do so before driver_vrlink brings up its encoder?
// It reports no devices, does nothing else, and writes a timeline to %LOCALAPPDATA%\SteamFrameTray\driver-probe.log.
#define NOMINMAX
#include <windows.h>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>

namespace {
std::atomic<bool> stopping{false};
std::thread watcher;
LARGE_INTEGER frequency{},origin{};
FILE* log_file=nullptr;

double seconds(){LARGE_INTEGER now;QueryPerformanceCounter(&now);return double(now.QuadPart-origin.QuadPart)/double(frequency.QuadPart);}
void write(const char* format,...){
 if(!log_file)return;
 SYSTEMTIME time;GetLocalTime(&time);
 fprintf(log_file,"%02d:%02d:%02d.%03d +%.3fs pid %lu ",time.wHour,time.wMinute,time.wSecond,time.wMilliseconds,seconds(),GetCurrentProcessId());
 va_list arguments;va_start(arguments,format);vfprintf(log_file,format,arguments);va_end(arguments);
 fputc('\n',log_file);fflush(log_file);
}
void open_log(){
 if(log_file)return;
 char base[MAX_PATH]{};
 if(!GetEnvironmentVariableA("LOCALAPPDATA",base,MAX_PATH))return;
 const std::string directory=std::string(base)+"\\SteamFrameTray";CreateDirectoryA(directory.c_str(),nullptr);
 log_file=fopen((directory+"\\driver-probe.log").c_str(),"a");
 QueryPerformanceFrequency(&frequency);QueryPerformanceCounter(&origin);
}
bool loaded(const wchar_t* name){return GetModuleHandleW(name)!=nullptr;}

// Notes when the modules that matter appear in vrserver, relative to this driver's load.
void watch(){
 const wchar_t* names[]={L"driver_vrlink.dll",L"nvEncodeAPI64.dll",L"driver_frame_hmd.dll",L"vrcompositor.exe"};
 bool seen[4]{};
 for(int i=0;i<4;++i)seen[i]=loaded(names[i]);
 while(!stopping.load()){
  for(int i=0;i<4;++i)if(!seen[i]&&loaded(names[i])){seen[i]=true;write("module appeared: %ls",names[i]);}
  Sleep(2);
 }
}

struct Provider{
 virtual int Init(void*){
  open_log();
  char exe[MAX_PATH]{};GetModuleFileNameA(nullptr,exe,MAX_PATH);
  write("Init in %s; already loaded: driver_vrlink=%d nvEncodeAPI64=%d",exe,int(loaded(L"driver_vrlink.dll")),int(loaded(L"nvEncodeAPI64.dll")));
  watcher=std::thread(watch);
  return 0;
 }
 virtual void Cleanup(){write("Cleanup");stopping=true;if(watcher.joinable())watcher.join();}
 virtual const char* const* GetInterfaceVersions(){static const char* const none[]={nullptr};return none;}
 virtual void RunFrame(){static bool first=true;if(first){first=false;write("first RunFrame");}}
 virtual bool ShouldBlockStandbyMode(){return false;}
 virtual void EnterStandby(){}
 virtual void LeaveStandby(){}
};
Provider provider;
}

extern "C" __declspec(dllexport) void* HmdDriverFactory(const char* name,int* code){
 open_log();
 write("HmdDriverFactory(%s)",name?name:"null");
 if(name&&!strcmp(name,"IServerTrackedDeviceProvider_004"))return &provider;
 if(code)*code=105; // VRInitError_Init_InterfaceNotFound
 return nullptr;
}

BOOL WINAPI DllMain(HINSTANCE,DWORD reason,LPVOID){
 if(reason==DLL_PROCESS_ATTACH){open_log();write("DLL_PROCESS_ATTACH; driver_vrlink loaded=%d",int(loaded(L"driver_vrlink.dll")));}
 return TRUE;
}
