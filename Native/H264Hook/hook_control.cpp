#define NOMINMAX
#include <windows.h>
#include <psapi.h>
#include <algorithm>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include "legacy_api_bridge.h"

struct Handle{HANDLE value=nullptr;~Handle(){if(value&&value!=INVALID_HANDLE_VALUE)CloseHandle(value);}};
struct Remote{
 HANDLE process;void* address=nullptr;bool owned=true;
 Remote(HANDLE p,size_t size):process(p){address=VirtualAllocEx(p,nullptr,size,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);if(!address)throw std::runtime_error("Remote allocation failed");}
 ~Remote(){if(owned&&address)VirtualFreeEx(process,address,0,MEM_RELEASE);}
};
struct Module{HMODULE base;std::wstring path;DWORD size;};
std::string quoted(const char* value){
 std::string result="\"";
 for(;*value;++value){const auto c=static_cast<unsigned char>(*value);
  if(c=='\\'||c=='\"')result+='\\';
  if(c>=32&&c<127)result+=static_cast<char>(c);else result+=' ';}
 return result+'\"';
}
std::vector<Module> modules(HANDLE process){
 HMODULE handles[1024];DWORD needed=0;
 if(!EnumProcessModulesEx(process,handles,sizeof(handles),&needed,LIST_MODULES_64BIT)||needed>sizeof(handles))throw std::runtime_error("Module inventory failed");
 std::vector<Module> result;
 for(unsigned i=0;i<needed/sizeof(HMODULE);++i){wchar_t path[32768];MODULEINFO info{};
  if(GetModuleFileNameExW(process,handles[i],path,32768)&&GetModuleInformation(process,handles[i],&info,sizeof(info)))result.push_back({handles[i],path,info.SizeOfImage});}
 return result;
}
DWORD call(HANDLE process,void* address,Remote* argument){
 Handle thread{CreateRemoteThread(process,nullptr,0,reinterpret_cast<LPTHREAD_START_ROUTINE>(address),argument?argument->address:nullptr,0,nullptr)};
 if(!thread.value)throw std::runtime_error("Remote call could not start");
 if(WaitForSingleObject(thread.value,10000)!=WAIT_OBJECT_0){if(argument)argument->owned=false;throw std::runtime_error("Remote call still pending; leave module loaded and restart SteamVR before retry");}
 DWORD result=0;if(!GetExitCodeThread(thread.value,&result))throw std::runtime_error("Remote result unavailable");return result;
}
void write(HANDLE process,Remote& buffer,const void* data,size_t size){
 SIZE_T count=0;if(!WriteProcessMemory(process,buffer.address,data,size,&count)||count!=size)throw std::runtime_error("Remote write failed");
 std::vector<uint8_t> readback(size);if(!ReadProcessMemory(process,buffer.address,readback.data(),size,&count)||count!=size||memcmp(data,readback.data(),size))throw std::runtime_error("Remote argument verification failed");
}
int wmain(int argc,wchar_t** argv){
 try{
  if(argc!=4&&argc!=7&&argc!=8&&argc!=12)throw std::runtime_error("Use hook_control PID DLL install|configure|stats|identity|remove [average_bps peak_bps fixed_target [fps [codec rate_control preset vbv_frames]]]");
  const auto pid=std::stoul(argv[1]);const auto path=std::filesystem::canonical(argv[2]).wstring();const std::wstring action=argv[3];
  if(action!=L"install"&&action!=L"configure"&&action!=L"stats"&&action!=L"remove"&&action!=L"identity")throw std::runtime_error("Unknown trial action");
  HookTrialConfig config{sizeof(config),5,100000000,115000000,0,120,0,2,1};
  if(argc>=7){
   if(action!=L"install"&&action!=L"configure")throw std::runtime_error("Budget arguments require install or configure");
   auto number=[](const wchar_t* input){size_t count=0;const auto value=std::stoull(input,&count);if(count!=wcslen(input)||value>UINT32_MAX)throw std::runtime_error("Invalid budget argument");return static_cast<uint32_t>(value);};
   config.average_bps=number(argv[4]);config.peak_bps=number(argv[5]);config.fixed_target=number(argv[6]);
   if(argc>=8)config.fps=number(argv[7]);
   if(argc==12){
    // The codec argument is kept for the supervisor's command line; H.264 (2) is the only codec this hook produces.
    if(number(argv[8])!=2)throw std::runtime_error("Only H.264 (codec 2) is supported");
    const std::wstring mode=argv[9];
    if(mode!=L"vbr"&&mode!=L"cbr")throw std::runtime_error("Rate control is vbr or cbr");
    config.rate_control=mode==L"cbr"?1:0;config.preset=number(argv[10]);config.vbv_frames=number(argv[11]);
   }
  }
  if(config.average_bps<1000000||config.average_bps>500000000u||config.peak_bps<config.average_bps||config.peak_bps>500000000u||config.fixed_target>1||(config.fps!=72&&config.fps!=80&&config.fps!=90&&config.fps!=120)||config.preset<1||config.preset>7||config.vbv_frames<1||config.vbv_frames>16)throw std::runtime_error("Budget/rate/preset outside bounds (H.264 up to 500 Mbps, presets P1-P7)");
  Handle process{OpenProcess(PROCESS_CREATE_THREAD|PROCESS_VM_OPERATION|PROCESS_VM_READ|PROCESS_VM_WRITE|PROCESS_QUERY_INFORMATION,FALSE,pid)};
  if(!process.value)throw std::runtime_error("Cannot open SteamVR server");
  wchar_t image[32768];DWORD length=32768;USHORT machine=0,native=0;
  if(!QueryFullProcessImageNameW(process.value,0,image,&length)||_wcsicmp(std::filesystem::path(image).filename().c_str(),L"vrserver.exe")||
     !IsWow64Process2(process.value,&machine,&native)||machine||native!=IMAGE_FILE_MACHINE_AMD64)throw std::runtime_error("Target is not x64 vrserver.exe");
  auto inventory=modules(process.value);HMODULE remote=nullptr;
  for(const auto& m:inventory)if(!_wcsicmp(m.path.c_str(),path.c_str()))remote=m.base;
  if(!remote){
   if(action!=L"install")throw std::runtime_error("Trial module is not loaded");
   auto loader=GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"LoadLibraryW");HMODULE owner=nullptr;
   if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(loader),&owner))throw std::runtime_error("Windows loader owner unavailable");
   wchar_t owner_path[32768];GetModuleFileNameW(owner,owner_path,32768);void* remote_loader=nullptr;
   for(const auto& m:inventory)if(!_wcsicmp(std::filesystem::path(m.path).filename().c_str(),std::filesystem::path(owner_path).filename().c_str()))
    remote_loader=reinterpret_cast<uint8_t*>(m.base)+(reinterpret_cast<uint8_t*>(loader)-reinterpret_cast<uint8_t*>(owner));
   if(!remote_loader)throw std::runtime_error("Remote Windows loader unavailable");
   Remote argument(process.value,(path.size()+1)*sizeof(wchar_t));write(process.value,argument,path.c_str(),(path.size()+1)*sizeof(wchar_t));call(process.value,remote_loader,&argument);
   inventory=modules(process.value);for(const auto& m:inventory)if(!_wcsicmp(m.path.c_str(),path.c_str()))remote=m.base;
   if(!remote)throw std::runtime_error("Trial DLL did not load");
  }
  const auto local=LoadLibraryExW(path.c_str(),nullptr,DONT_RESOLVE_DLL_REFERENCES);if(!local)throw std::runtime_error("Trial exports unavailable");
  const char* name=action==L"install"?"HookInstallTrial":action==L"configure"?"HookReconfigure":action==L"stats"?"HookReadTrialStats":action==L"identity"?"HookReadTrialIdentity":"HookRemoveTrial";
  const auto symbol=GetProcAddress(local,name);if(!symbol)throw std::runtime_error("Required trial export missing");
  auto address=reinterpret_cast<uint8_t*>(remote)+(reinterpret_cast<uint8_t*>(symbol)-reinterpret_cast<uint8_t*>(local));
  DWORD result=0;
  if(action==L"identity"){
   HookTrialIdentity identity{sizeof(identity),1};Remote argument(process.value,sizeof(identity));write(process.value,argument,&identity,sizeof(identity));
   result=call(process.value,address,&argument);SIZE_T read=0;
   if(result||!ReadProcessMemory(process.value,argument.address,&identity,sizeof(identity),&read)||read!=sizeof(identity))throw std::runtime_error("Codec identity unavailable");
   std::cout<<"{\"codec\":\""<<(identity.codec==2?"H.264":"Unknown")<<"\",\"fps\":"<<identity.fps<<",\"average_bps\":"<<identity.average_bps<<",\"peak_bps\":"<<identity.peak_bps<<",\"rate_control\":\""<<(identity.rate_control?"cbr":"vbr")<<"\",\"preset\":"<<identity.preset<<",\"vbv_frames\":"<<identity.vbv_frames<<"}\n";
  }else if(action==L"stats"){
   HookBridgeStats stats{sizeof(stats),1};Remote argument(process.value,sizeof(stats));write(process.value,argument,&stats,sizeof(stats));
   result=call(process.value,address,&argument);SIZE_T read=0;
   if(result||!ReadProcessMemory(process.value,argument.address,&stats,sizeof(stats),&read)||read!=sizeof(stats))throw std::runtime_error("Trial stats unavailable");
   std::cout<<"{\"pid\":"<<pid<<",\"tables\":"<<stats.tables<<",\"opens\":"<<stats.opens<<",\"initializes\":"<<stats.initializes
    <<",\"encodes\":"<<stats.encodes<<",\"locks\":"<<stats.locks<<",\"bytes\":"<<stats.bytes<<",\"errors\":"<<stats.errors
    <<",\"reconfigures\":"<<stats.reconfigures<<",\"width\":"<<stats.width<<",\"height\":"<<stats.height
    <<",\"average_bps\":"<<stats.average_bps<<",\"peak_bps\":"<<stats.peak_bps<<",\"last_status\":"<<stats.last_status
    <<",\"initialize_attempts\":"<<stats.initialize_attempts<<",\"initialize_status\":"<<stats.initialize_status
    <<",\"source_fps_num\":"<<stats.source_fps_num<<",\"source_fps_den\":"<<stats.source_fps_den<<",\"source_async\":"<<stats.source_async
    <<",\"initialize_error\":"<<quoted(stats.initialize_error)<<"}\n";
  }else{Remote argument(process.value,sizeof(config));const bool configured=action==L"install"||action==L"configure";if(configured)write(process.value,argument,&config,sizeof(config));result=call(process.value,address,configured?&argument:nullptr);std::cout<<"{\"pid\":"<<pid<<",\"action\":\""<<(action==L"install"?"install":action==L"configure"?"configure":"remove")<<"\",\"code\":"<<result<<"}\n";}
  FreeLibrary(local);return result?1:0;
 }catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}
}
