#define HOOK_BUILD
#include <windows.h>
#include <cstring>
#include <cstdio>
#include <cstdarg>
#include <string>
#include <mutex>
#include "nvEncodeAPI11.h"
#include "stock_forward.h"
#include "data_folder.h"

namespace {
NV_ENCODE_API_FUNCTION_LIST table{};
using Factory=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
Factory factory=nullptr;
bool ready=false;
int status(NVENCSTATUS value){return static_cast<int>(value);}
}

extern "C" {
int StockSetTable(const void* raw,uint32_t bytes,void* create){
 if(!raw||bytes!=sizeof(table))return NV_ENC_ERR_INVALID_PARAM;
 memcpy(&table,raw,sizeof(table));factory=reinterpret_cast<Factory>(create);
 ready=table.nvEncOpenEncodeSessionEx&&table.nvEncInitializeEncoder&&table.nvEncReconfigureEncoder&&table.nvEncEncodePicture&&
  table.nvEncLockBitstream&&table.nvEncRegisterResource&&table.nvEncMapInputResource&&table.nvEncCreateBitstreamBuffer&&table.nvEncDestroyEncoder;
 return ready?0:NV_ENC_ERR_INVALID_PARAM;
}
int StockReady(void){return ready?1:0;}
int StockOpen(const void* open_params,void** encoder){
 if(!ready||!open_params||!encoder)return NV_ENC_ERR_INVALID_PTR;
 auto copy=*static_cast<const NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS*>(open_params);
 // The driver checks an open against the API version of the most recent factory call in the process, so ask for
 // this table's version again right before opening a session of that version.
 if(factory){NV_ENCODE_API_FUNCTION_LIST fresh{};fresh.version=table.version;factory(&fresh);}
 return status(table.nvEncOpenEncodeSessionEx(&copy,encoder));
}
// Optional rate-control overrides for the driver's own (HEVC) encoder, read from hevc-options.txt in the tray's data folder:
//   enabled=1  rate_control=vbr|cbr  preset=1..7  vbv=1..16  multipass=0|1|2 (off, quarter resolution first pass, full resolution first pass)
// The file is checked about twice a second, so a change reaches the encoder SteamVR reconfigures before its next frame. Nothing is
// touched unless enabled=1; the driver's own parameters are copied, never edited in place.
namespace {
struct HevcOptions{bool enabled=false;bool cbr=false;int preset=2;int vbv=2;int multipass=0;bool hold=true;int target=0;};
HevcOptions options;ULONGLONG options_checked=0;
int applied_signature=-1;bool signature_changed=false;int reconfigure_calls=0;
int applied_preset=0;bool applied_cbr=false;int applied_vbv=0;bool applied_any=false;
// Which option a live change moved, for the flight recorder: "preset 2->4, multipass 1->2 (change 12 since the encoder was created, 3 since multipass was last 2)".
int applied_multipass=0,live_changes=0,changes_since_multipass2=0;char change_text[200]{};
// Diagnosis of the live HEVC route: what SteamVR's reconfigures carry and what was done with them (hevc-hook.log in the tray's data folder).
void trace(const char* format,...){
 const std::wstring path=hook_data_path(L"hevc-hook.log");if(path.empty())return;
 WIN32_FILE_ATTRIBUTE_DATA info{};if(GetFileAttributesExW(path.c_str(),GetFileExInfoStandard,&info)&&info.nFileSizeLow>100000)DeleteFileW(path.c_str());
 FILE* file=nullptr;if(_wfopen_s(&file,path.c_str(),L"a")||!file)return;
 SYSTEMTIME now{};GetLocalTime(&now);fprintf(file,"%02d:%02d:%02d.%03d ",now.wHour,now.wMinute,now.wSecond,now.wMilliseconds);
 va_list args;va_start(args,format);vfprintf(file,format,args);va_end(args);fputc('\n',file);fclose(file);
}
// Flight recorder for the live HEVC route (flight.log in the tray's data folder). The GPU has faulted (nvlddmkm 153) a few milliseconds after some
// reconfigures; this keeps the last calls to the encoder in memory (every reconfigure with what the driver asked and what was applied, every encode and
// lock with its result and timing) and writes them out when something that could matter happens: a change of the applied options, or any call that
// does not return success. The calls that follow are written as they happen. Times are microseconds from the first call, with the calling thread.
struct Flight{LONGLONG us;DWORD thread;char kind;int result;unsigned v[8];};
constexpr int FlightSize=300,FlightAfter=120;
Flight flight_ring[FlightSize];int flight_head=0,flight_count=0,flight_after=0;LONGLONG flight_zero=0;std::mutex flight_lock;
LONGLONG flight_clock(){static LARGE_INTEGER frequency{};LARGE_INTEGER now{};if(!frequency.QuadPart)QueryPerformanceFrequency(&frequency);QueryPerformanceCounter(&now);return now.QuadPart*1000000/frequency.QuadPart;}
void flight_write(const char* heading,const Flight* entries,int count){
 const std::wstring path=hook_data_path(L"flight.log");if(path.empty())return;
 WIN32_FILE_ATTRIBUTE_DATA info{};if(GetFileAttributesExW(path.c_str(),GetFileExInfoStandard,&info)&&info.nFileSizeLow>3000000)MoveFileExW(path.c_str(),(path+L".1").c_str(),MOVEFILE_REPLACE_EXISTING);
 FILE* file=nullptr;if(_wfopen_s(&file,path.c_str(),L"a")||!file)return;
 SYSTEMTIME now{};GetLocalTime(&now);
 if(heading)fprintf(file,"==== %02d:%02d:%02d.%03d %s\n",now.wHour,now.wMinute,now.wSecond,now.wMilliseconds,heading);
 for(int i=0;i<count;i++){
  const Flight& f=entries[i];
  fprintf(file,"%10lld t%lu %c r=%d",static_cast<long long>(f.us),static_cast<unsigned long>(f.thread),f.kind,f.result);
  for(int k=0;k<8;k++)fprintf(file," %u",f.v[k]);
  fputc('\n',file);
 }
 fclose(file);
}
// kind: R reconfigure (v: driver avg, max, vbv, rc|multipass<<4|lookahead<<8|aq<<12|preset<<16 | applied avg, max, vbv, rc|multipass<<4|preset<<16|driver reset<<20|driver IDR<<21|changed<<22),
// r reconfigure left alone, E encode (v: flags, picture type), L lock (v: bytes, picture type), I initialize, D destroy.
void flight_note(char kind,int result,const unsigned (&v)[8],const char* why,bool change=false){
 std::lock_guard<std::mutex> guard(flight_lock);
 if(!flight_zero)flight_zero=flight_clock();
 Flight entry{flight_clock()-flight_zero,GetCurrentThreadId(),kind,result,{}};memcpy(entry.v,v,sizeof(entry.v));
 flight_ring[flight_head]=entry;flight_head=(flight_head+1)%FlightSize;if(flight_count<FlightSize)++flight_count;
 if(why&&!(change&&flight_after>0)){
  Flight ordered[FlightSize];const int first=(flight_head-flight_count+FlightSize)%FlightSize;
  for(int i=0;i<flight_count;i++)ordered[i]=flight_ring[(first+i)%FlightSize];
  flight_write(why,ordered,flight_count);flight_after=FlightAfter;
 }else if(flight_after>0){flight_write(why,&entry,1);--flight_after;}
}
unsigned preset_number(const GUID& guid){
 const GUID* all[]={&NV_ENC_PRESET_P1_GUID,&NV_ENC_PRESET_P2_GUID,&NV_ENC_PRESET_P3_GUID,&NV_ENC_PRESET_P4_GUID,&NV_ENC_PRESET_P5_GUID,&NV_ENC_PRESET_P6_GUID,&NV_ENC_PRESET_P7_GUID};
 for(int i=0;i<7;i++)if(memcmp(&guid,all[i],sizeof(GUID))==0)return static_cast<unsigned>(i+1);
 return 0;
}
void load_options(){
 const ULONGLONG now=GetTickCount64();if(options_checked&&now-options_checked<500)return;options_checked=now;
 options=HevcOptions{};const std::wstring path=hook_data_path(L"hevc-options.txt");if(path.empty())return;
 FILE* file=nullptr;
 if(_wfopen_s(&file,path.c_str(),L"r")||!file)return;
 char line[96];
 while(fgets(line,sizeof(line),file)){
  int number=0;char word[16]{};
  if(sscanf_s(line,"enabled=%d",&number)==1)options.enabled=number==1;
  else if(sscanf_s(line,"rate_control=%15s",word,(unsigned)sizeof(word))==1)options.cbr=strcmp(word,"cbr")==0;
  else if(sscanf_s(line,"preset=%d",&number)==1&&number>=1&&number<=7)options.preset=number;
  else if(sscanf_s(line,"vbv=%d",&number)==1&&number>=1&&number<=16)options.vbv=number;
  else if(sscanf_s(line,"multipass=%d",&number)==1&&number>=0&&number<=2)options.multipass=number;
  else if(sscanf_s(line,"hold_target=%d",&number)==1)options.hold=number!=0;
  else if(sscanf_s(line,"target=%d",&number)==1&&number>=1&&number<=1000)options.target=number;
 }
 fclose(file);
}
GUID preset_guid(int preset){
 switch(preset){case 1:return NV_ENC_PRESET_P1_GUID;case 3:return NV_ENC_PRESET_P3_GUID;case 4:return NV_ENC_PRESET_P4_GUID;
  case 5:return NV_ENC_PRESET_P5_GUID;case 6:return NV_ENC_PRESET_P6_GUID;case 7:return NV_ENC_PRESET_P7_GUID;default:return NV_ENC_PRESET_P2_GUID;}
}
// SteamVR's driver asks for a different average bitrate on almost every frame (its own estimate of what each frame's link can carry, with some frames at 30 Mbps),
// so the encoder averages far below the user's target on a busy scene. The highest average asked for in the last one to two seconds is the target the user set;
// holding the encoder to it keeps the average at the target, and a lowered target is followed within two seconds.
struct Asked{uint32_t average=0,peak=0;};
Asked held_request(uint32_t average,uint32_t peak){
 static ULONGLONG window_start=0;static Asked current,previous;
 const ULONGLONG now=GetTickCount64();
 if(!window_start||now-window_start>=1000){previous=(window_start&&now-window_start<2000)?current:Asked{};current=Asked{};window_start=now;}
 if(average>current.average)current={average,peak};
 // The peak that goes with the average is the one the driver itself asked for alongside it, so it is always a pair the encoder has accepted.
 return current.average>=previous.average?current:previous;
}
// Applies the options to a copy of the driver's init parameters and its encode configuration; returns whether it changed anything.
bool apply_options(const NV_ENC_INITIALIZE_PARAMS& source,NV_ENC_INITIALIZE_PARAMS& init,NV_ENC_CONFIG& config,bool){
 load_options();if(!options.enabled||!source.encodeConfig)return false;
 init=source;config=*source.encodeConfig;init.encodeConfig=&config;
 auto& rc=config.rcParams;
 if(options.hold&&rc.averageBitRate){
  if(options.target>0){
   // The tray names the user's target: the driver's own request can stay below it for a whole session at a large picture. The peak keeps the 1.15 ratio the driver uses at high rates.
   rc.averageBitRate=static_cast<uint32_t>(options.target)*1000000u;rc.maxBitRate=static_cast<uint32_t>(static_cast<double>(rc.averageBitRate)*1.15+0.5);
  }else{const Asked held=held_request(rc.averageBitRate,rc.maxBitRate);if(held.average>rc.averageBitRate){rc.averageBitRate=held.average;rc.maxBitRate=held.peak;}}
 }
 rc.rateControlMode=options.cbr?NV_ENC_PARAMS_RC_CBR:NV_ENC_PARAMS_RC_VBR;
 if(options.cbr&&rc.averageBitRate)rc.maxBitRate=rc.averageBitRate;
 const uint32_t fps=init.frameRateDen?init.frameRateNum/init.frameRateDen:90;
 if(rc.averageBitRate&&fps){rc.vbvBufferSize=rc.averageBitRate/fps*static_cast<uint32_t>(options.vbv);rc.vbvInitialDelay=0;}
 // SteamVR reconfigures before every frame with its own preset, and the encoder follows the preset it is given each time, so the chosen
 // one is named on every call, not only when the encoder is created.
 init.presetGUID=preset_guid(options.preset);
 // The encoder hangs when a reconfigure goes from multipass 1 (quarter resolution) straight to 2 (full resolution), unless the 1 was itself entered from 2: found with
 // encoder_stress_gpu_test (0->1->2 and an encoder created with 1 then 2 hang the lock; 0->2, 2->1->2 and 1->0->2 do not). So that step goes through 0 for one call.
 int effective_multipass=options.multipass;
 if(applied_signature!=-1&&applied_multipass==1&&options.multipass==2)effective_multipass=0;
 rc.multiPass=static_cast<NV_ENC_MULTI_PASS>(effective_multipass);
 // A changed rate control, buffer or preset is followed by a plain reconfigure; the encoder is not reset (a reset with multipass 2 faulted the GPU).
 const int signature=(options.cbr?1000:0)+options.vbv+options.preset*10000+options.multipass*100000;
 signature_changed=applied_signature!=-1&&applied_signature!=signature;
 if(signature_changed){
  ++live_changes;if(options.multipass==2||applied_multipass==2)changes_since_multipass2=0;else ++changes_since_multipass2;
  int used=0;change_text[0]=0;
  if(applied_cbr!=options.cbr)used+=snprintf(change_text+used,sizeof(change_text)-used,"%srate control %s->%s",used?", ":"",applied_cbr?"cbr":"vbr",options.cbr?"cbr":"vbr");
  if(applied_preset!=options.preset)used+=snprintf(change_text+used,sizeof(change_text)-used,"%spreset P%d->P%d",used?", ":"",applied_preset,options.preset);
  if(applied_vbv!=options.vbv)used+=snprintf(change_text+used,sizeof(change_text)-used,"%svbv %d->%d",used?", ":"",applied_vbv,options.vbv);
  if(applied_multipass!=options.multipass)used+=snprintf(change_text+used,sizeof(change_text)-used,"%smultipass %d->%d",used?", ":"",applied_multipass,options.multipass);
  snprintf(change_text+used,sizeof(change_text)-used," (change %d since the encoder was created, %d since multipass was last 2)",live_changes,changes_since_multipass2);
  trace("options changed: %s",change_text);
 }
 applied_any=true;applied_cbr=options.cbr;applied_vbv=options.vbv;applied_preset=options.preset;applied_multipass=effective_multipass;applied_signature=signature;
 return true;
}
}
static uint32_t initialized_width=0,initialized_height=0;
void StockEncodeSize(uint32_t* width,uint32_t* height){*width=initialized_width;*height=initialized_height;}
// What the driver's encoder was last asked for (average and peak bits per second): the driver's own request, as the monitor shows it.
static uint32_t requested_average=0,requested_peak=0;
void StockRequestedRate(uint32_t* average,uint32_t* peak){*average=requested_average;*peak=requested_peak;}
// Notes the driver's request, which the monitor shows (it changes about every frame, so it is not written to the log).
static void note_request(const NV_ENC_CONFIG* config){
 if(!config||!config->rcParams.averageBitRate)return;
 requested_average=config->rcParams.averageBitRate;requested_peak=config->rcParams.maxBitRate;
}
int StockInitialize(void* encoder,void* init){
 if(!ready)return NV_ENC_ERR_INVALID_PTR;
 auto* original=static_cast<NV_ENC_INITIALIZE_PARAMS*>(init);
 if(original){initialized_width=original->encodeWidth;initialized_height=original->encodeHeight;note_request(original->encodeConfig);trace("initialize: picture %ux%u (max %ux%u)",initialized_width,initialized_height,original->maxEncodeWidth,original->maxEncodeHeight);}
 NV_ENC_INITIALIZE_PARAMS patched{};NV_ENC_CONFIG config{};
 reconfigure_calls=0;live_changes=0;changes_since_multipass2=0;applied_signature=-1;
 if(original&&apply_options(*original,patched,config,true)){
  trace("initialize: preset P%d asked of the encoder (driver asked for tuning %d, rc=%d), multipass %d",applied_preset,static_cast<int>(original->tuningInfo),static_cast<int>(config.rcParams.rateControlMode),options.multipass);
  { const unsigned v[8]{original->encodeWidth,original->encodeHeight,static_cast<unsigned>(options.multipass),static_cast<unsigned>(applied_preset),0,0,0,0};flight_note('I',0,v,"initialize"); }
  return status(table.nvEncInitializeEncoder(encoder,&patched));
 }
 trace("initialize: left alone (options off or no encode configuration: config=%d)",original&&original->encodeConfig?1:0);
 return status(table.nvEncInitializeEncoder(encoder,original));
}
int StockReconfigure(void* encoder,void* params){
 if(!ready)return NV_ENC_ERR_INVALID_PTR;
 auto* original=static_cast<NV_ENC_RECONFIGURE_PARAMS*>(params);
 if(original&&original->reInitEncodeParams.encodeWidth){
  if(original->reInitEncodeParams.encodeWidth!=initialized_width||original->reInitEncodeParams.encodeHeight!=initialized_height)
   trace("reconfigure: picture %ux%u -> %ux%u",initialized_width,initialized_height,original->reInitEncodeParams.encodeWidth,original->reInitEncodeParams.encodeHeight);
  initialized_width=original->reInitEncodeParams.encodeWidth;initialized_height=original->reInitEncodeParams.encodeHeight;
 }
 if(original)note_request(original->reInitEncodeParams.encodeConfig);
 NV_ENC_INITIALIZE_PARAMS patched{};NV_ENC_CONFIG config{};
 if(original&&apply_options(original->reInitEncodeParams,patched,config,false)){
  requested_average=config.rcParams.averageBitRate;requested_peak=config.rcParams.maxBitRate;   // the monitor shows what the encoder is really held to
  NV_ENC_RECONFIGURE_PARAMS copy=*original;copy.reInitEncodeParams=patched;
  // No resetEncoder/forceIDR on a changed option: a rate-control reset together with full-resolution two-pass (multipass 2) faulted the GPU (nvlddmkm event 153) and crashed SteamVR.
  const auto result=table.nvEncReconfigureEncoder(encoder,&copy);
  {
   const auto* drv=original->reInitEncodeParams.encodeConfig;
   const unsigned v[8]{drv?drv->rcParams.averageBitRate:0,drv?drv->rcParams.maxBitRate:0,drv?drv->rcParams.vbvBufferSize:0,
    drv?static_cast<unsigned>(drv->rcParams.rateControlMode)|static_cast<unsigned>(drv->rcParams.multiPass)<<4|(drv->rcParams.enableLookahead?1u:0u)<<8|(drv->rcParams.enableAQ?1u:0u)<<12|preset_number(original->reInitEncodeParams.presetGUID)<<16:0,
    config.rcParams.averageBitRate,config.rcParams.maxBitRate,config.rcParams.vbvBufferSize,
    static_cast<unsigned>(config.rcParams.rateControlMode)|static_cast<unsigned>(config.rcParams.multiPass)<<4|static_cast<unsigned>(applied_preset)<<16|(original->resetEncoder?1u:0u)<<20|(original->forceIDR?1u:0u)<<21|(signature_changed?1u:0u)<<22};
   flight_note('R',status(result),v,result!=NV_ENC_SUCCESS?"reconfigure failed":(signature_changed?change_text:nullptr),result==NV_ENC_SUCCESS&&signature_changed);
  }
  if(signature_changed||reconfigure_calls<5){
   trace("reconfigure: preset P%d, driver rc=%d vbv=%u avg=%u -> rc=%d vbv=%u, reset=%d, result=%d",applied_preset,static_cast<int>(original->reInitEncodeParams.encodeConfig->rcParams.rateControlMode),
    original->reInitEncodeParams.encodeConfig->rcParams.vbvBufferSize,original->reInitEncodeParams.encodeConfig->rcParams.averageBitRate,
    static_cast<int>(config.rcParams.rateControlMode),config.rcParams.vbvBufferSize,signature_changed?1:0,static_cast<int>(result));
  }
  if(signature_changed||reconfigure_calls<5){
   // What else the driver asked for. The hook keeps all of it, so a limit here (a quality target or QP bounds) explains a bitrate that stops short of the target.
   const auto& d=original->reInitEncodeParams.encodeConfig->rcParams;
   trace("reconfigure: driver also asked avg=%u max=%u targetQuality=%u minQP=%d(%u/%u/%u) maxQP=%d(%u/%u/%u) aq=%d/%u lookahead=%d multipass=%d tuning=%d",d.averageBitRate,d.maxBitRate,
    static_cast<unsigned>(d.targetQuality),d.enableMinQP,d.minQP.qpIntra,d.minQP.qpInterP,d.minQP.qpInterB,d.enableMaxQP,d.maxQP.qpIntra,d.maxQP.qpInterP,d.maxQP.qpInterB,
    d.enableAQ,d.aqStrength,d.enableLookahead,static_cast<int>(d.multiPass),static_cast<int>(original->reInitEncodeParams.tuningInfo));
  }
  ++reconfigure_calls;
  return status(result);
 }
 if(reconfigure_calls<5){trace("reconfigure: left alone (options off or no encode configuration: config=%d)",original&&original->reInitEncodeParams.encodeConfig?1:0);++reconfigure_calls;}
 const auto left=table.nvEncReconfigureEncoder(encoder,original);
 { const unsigned v[8]{};flight_note('r',status(left),v,left!=NV_ENC_SUCCESS?"reconfigure (left alone) failed":nullptr); }
 return status(left);
}
int StockRegister(void* encoder,const void* params,void** resource){
 if(!ready||!params||!resource)return NV_ENC_ERR_INVALID_PTR;
 auto copy=*static_cast<const NV_ENC_REGISTER_RESOURCE*>(params);
 const auto result=table.nvEncRegisterResource(encoder,&copy);
 if(result==NV_ENC_SUCCESS)*resource=copy.registeredResource;
 return status(result);
}
int StockUnregister(void* encoder,void* resource){return ready?status(table.nvEncUnregisterResource(encoder,resource)):NV_ENC_ERR_INVALID_PTR;}
int StockMap(void* encoder,const void* params,void* resource,void** mapped,uint32_t* format){
 if(!ready||!params||!mapped||!format)return NV_ENC_ERR_INVALID_PTR;
 auto copy=*static_cast<const NV_ENC_MAP_INPUT_RESOURCE*>(params);copy.registeredResource=resource;
 const auto result=table.nvEncMapInputResource(encoder,&copy);
 if(result==NV_ENC_SUCCESS){*mapped=copy.mappedResource;*format=copy.mappedBufferFmt;}
 return status(result);
}
int StockUnmap(void* encoder,void* mapped){return ready?status(table.nvEncUnmapInputResource(encoder,mapped)):NV_ENC_ERR_INVALID_PTR;}
int StockCreateOutput(void* encoder,const void* params,void** output){
 if(!ready||!params||!output)return NV_ENC_ERR_INVALID_PTR;
 auto copy=*static_cast<const NV_ENC_CREATE_BITSTREAM_BUFFER*>(params);
 const auto result=table.nvEncCreateBitstreamBuffer(encoder,&copy);
 if(result==NV_ENC_SUCCESS)*output=copy.bitstreamBuffer;
 return status(result);
}
int StockDestroyOutput(void* encoder,void* output){return ready?status(table.nvEncDestroyBitstreamBuffer(encoder,output)):NV_ENC_ERR_INVALID_PTR;}
int StockEncode(void* encoder,const void* picture,void* input,void* output,uint32_t extra_flags){
 if(!ready||!picture)return NV_ENC_ERR_INVALID_PTR;
 // The caller's picture is read-only here: submit a copy that names this session's own input and output.
 auto copy=*static_cast<const NV_ENC_PIC_PARAMS*>(picture);
 copy.inputBuffer=input;copy.outputBitstream=output;copy.encodePicFlags|=extra_flags;
 const auto result=table.nvEncEncodePicture(encoder,&copy);
 { const unsigned v[8]{static_cast<unsigned>(copy.encodePicFlags),static_cast<unsigned>(copy.pictureType),0,0,0,0,0,0};flight_note('E',status(result),v,result!=NV_ENC_SUCCESS?"encode failed":nullptr); }
 return status(result);
}
int StockLock(void* encoder,void* params,void* output){
 if(!ready||!params)return NV_ENC_ERR_INVALID_PTR;
 auto* caller=static_cast<NV_ENC_LOCK_BITSTREAM*>(params);
 auto copy=*caller;copy.outputBitstream=output;
 const auto result=table.nvEncLockBitstream(encoder,&copy);
 if(result==NV_ENC_SUCCESS){const auto virtual_output=caller->outputBitstream;*caller=copy;caller->outputBitstream=virtual_output;}
 { const unsigned v[8]{result==NV_ENC_SUCCESS?static_cast<unsigned>(copy.bitstreamSizeInBytes):0,result==NV_ENC_SUCCESS?static_cast<unsigned>(copy.pictureType):0,0,0,0,0,0,0};flight_note('L',status(result),v,result!=NV_ENC_SUCCESS?"lock failed":nullptr); }
 return status(result);
}
int StockUnlock(void* encoder,void* output){return ready?status(table.nvEncUnlockBitstream(encoder,output)):NV_ENC_ERR_INVALID_PTR;}
int StockEvent(void* encoder,const void* params,int registration){
 if(!ready||!params)return NV_ENC_ERR_INVALID_PTR;
 auto copy=*static_cast<const NV_ENC_EVENT_PARAMS*>(params);
 return status(registration?table.nvEncRegisterAsyncEvent(encoder,&copy):table.nvEncUnregisterAsyncEvent(encoder,&copy));
}
int StockAppliedOptions(uint32_t* cbr,uint32_t* preset,uint32_t* vbv){
 if(!applied_any)return 0;
 *cbr=applied_cbr?1:0;*preset=static_cast<uint32_t>(applied_preset);*vbv=static_cast<uint32_t>(applied_vbv);return 1;
}
int StockMultiPass(void){load_options();return options.multipass;}
int StockDestroy(void* encoder){if(ready){trace("encoder session destroyed");const unsigned v[8]{};flight_note('D',0,v,"encoder session destroyed");}return ready?status(table.nvEncDestroyEncoder(encoder)):NV_ENC_ERR_INVALID_PTR;}
}
