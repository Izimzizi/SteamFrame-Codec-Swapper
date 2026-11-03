#define HOOK_BUILD
#include <windows.h>
#include <cstring>
#include <cstdio>
#include <cstdarg>
#include <string>
#include "nvEncodeAPI11.h"
#include "stock_forward.h"

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
//   enabled=1  rate_control=vbr|cbr  preset=1..7  vbv=1..16  multipass=0|1|2 (off, quarter resolution first pass, full resolution first pass;
//   read when the encoder is created, not changed afterwards)
// The file is checked about twice a second, so a change reaches the encoder SteamVR reconfigures before its next frame. Nothing is
// touched unless enabled=1; the driver's own parameters are copied, never edited in place.
namespace {
struct HevcOptions{bool enabled=false;bool cbr=false;int preset=2;int vbv=2;int multipass=0;};
HevcOptions options;ULONGLONG options_checked=0;
// The multipass mode the encoder was created with: it cannot change in place (the encoder allocates for it when it is created), so reconfigures keep it.
int initialized_multipass=0;
int applied_signature=-1;bool signature_changed=false;int reconfigure_calls=0;
int applied_preset=0;bool applied_cbr=false;int applied_vbv=0;bool applied_any=false;
// Diagnosis of the live HEVC route: what SteamVR's reconfigures carry and what was done with them (hevc-hook.log in the tray's data folder).
void trace(const char* format,...){
 char base[MAX_PATH]{};if(!GetEnvironmentVariableA("LOCALAPPDATA",base,MAX_PATH))return;
 const std::string path=std::string(base)+"\\CodecSwapper\\hevc-hook.log";
 WIN32_FILE_ATTRIBUTE_DATA info{};if(GetFileAttributesExA(path.c_str(),GetFileExInfoStandard,&info)&&info.nFileSizeLow>100000)DeleteFileA(path.c_str());
 FILE* file=nullptr;if(fopen_s(&file,path.c_str(),"a")||!file)return;
 SYSTEMTIME now{};GetLocalTime(&now);fprintf(file,"%02d:%02d:%02d.%03d ",now.wHour,now.wMinute,now.wSecond,now.wMilliseconds);
 va_list args;va_start(args,format);vfprintf(file,format,args);va_end(args);fputc('\n',file);fclose(file);
}
void load_options(){
 const ULONGLONG now=GetTickCount64();if(options_checked&&now-options_checked<500)return;options_checked=now;
 options=HevcOptions{};char base[MAX_PATH]{};if(!GetEnvironmentVariableA("LOCALAPPDATA",base,MAX_PATH))return;
 FILE* file=nullptr;const std::string path=std::string(base)+"\\CodecSwapper\\hevc-options.txt";
 if(fopen_s(&file,path.c_str(),"r")||!file)return;
 char line[96];
 while(fgets(line,sizeof(line),file)){
  int number=0;char word[16]{};
  if(sscanf_s(line,"enabled=%d",&number)==1)options.enabled=number==1;
  else if(sscanf_s(line,"rate_control=%15s",word,(unsigned)sizeof(word))==1)options.cbr=strcmp(word,"cbr")==0;
  else if(sscanf_s(line,"preset=%d",&number)==1&&number>=1&&number<=7)options.preset=number;
  else if(sscanf_s(line,"vbv=%d",&number)==1&&number>=1&&number<=16)options.vbv=number;
  else if(sscanf_s(line,"multipass=%d",&number)==1&&number>=0&&number<=2)options.multipass=number;
 }
 fclose(file);
}
GUID preset_guid(int preset){
 switch(preset){case 1:return NV_ENC_PRESET_P1_GUID;case 3:return NV_ENC_PRESET_P3_GUID;case 4:return NV_ENC_PRESET_P4_GUID;
  case 5:return NV_ENC_PRESET_P5_GUID;case 6:return NV_ENC_PRESET_P6_GUID;case 7:return NV_ENC_PRESET_P7_GUID;default:return NV_ENC_PRESET_P2_GUID;}
}
// Applies the options to a copy of the driver's init parameters and its encode configuration; returns whether it changed anything.
bool apply_options(const NV_ENC_INITIALIZE_PARAMS& source,NV_ENC_INITIALIZE_PARAMS& init,NV_ENC_CONFIG& config,bool initial){
 load_options();if(!options.enabled||!source.encodeConfig)return false;
 init=source;config=*source.encodeConfig;init.encodeConfig=&config;
 auto& rc=config.rcParams;
 rc.rateControlMode=options.cbr?NV_ENC_PARAMS_RC_CBR:NV_ENC_PARAMS_RC_VBR;
 if(options.cbr&&rc.averageBitRate)rc.maxBitRate=rc.averageBitRate;
 const uint32_t fps=init.frameRateDen?init.frameRateNum/init.frameRateDen:90;
 if(rc.averageBitRate&&fps){rc.vbvBufferSize=rc.averageBitRate/fps*static_cast<uint32_t>(options.vbv);rc.vbvInitialDelay=0;}
 // SteamVR reconfigures before every frame with its own preset, and the encoder follows the preset it is given each time, so the chosen
 // one is named on every call, not only when the encoder is created.
 init.presetGUID=preset_guid(options.preset);
 if(initial)initialized_multipass=options.multipass;
 rc.multiPass=static_cast<NV_ENC_MULTI_PASS>(initialized_multipass);
 // A changed rate control, buffer or preset needs the encoder to restart its rate control: the caller asks for a reset on that one reconfigure.
 const int signature=(options.cbr?1000:0)+options.vbv+options.preset*10000;
 applied_any=true;applied_cbr=options.cbr;applied_vbv=options.vbv;applied_preset=options.preset;
 signature_changed=applied_signature!=-1&&applied_signature!=signature;applied_signature=signature;
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
 reconfigure_calls=0;
 if(original&&apply_options(*original,patched,config,true)){
  trace("initialize: preset P%d asked of the encoder (driver asked for tuning %d, rc=%d)",applied_preset,static_cast<int>(original->tuningInfo),static_cast<int>(config.rcParams.rateControlMode));
  return status(table.nvEncInitializeEncoder(encoder,&patched));
 }
 initialized_multipass=original&&original->encodeConfig?static_cast<int>(original->encodeConfig->rcParams.multiPass):0;
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
  NV_ENC_RECONFIGURE_PARAMS copy=*original;copy.reInitEncodeParams=patched;
  if(signature_changed){copy.resetEncoder=1;copy.forceIDR=1;}
  const auto result=table.nvEncReconfigureEncoder(encoder,&copy);
  if(signature_changed||reconfigure_calls<5){
   trace("reconfigure: preset P%d, driver rc=%d vbv=%u avg=%u -> rc=%d vbv=%u, reset=%d, result=%d",applied_preset,static_cast<int>(original->reInitEncodeParams.encodeConfig->rcParams.rateControlMode),
    original->reInitEncodeParams.encodeConfig->rcParams.vbvBufferSize,original->reInitEncodeParams.encodeConfig->rcParams.averageBitRate,
    static_cast<int>(config.rcParams.rateControlMode),config.rcParams.vbvBufferSize,signature_changed?1:0,static_cast<int>(result));
  }
  ++reconfigure_calls;
  return status(result);
 }
 if(reconfigure_calls<5){trace("reconfigure: left alone (options off or no encode configuration: config=%d)",original&&original->reInitEncodeParams.encodeConfig?1:0);++reconfigure_calls;}
 return status(table.nvEncReconfigureEncoder(encoder,original));
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
 return status(table.nvEncEncodePicture(encoder,&copy));
}
int StockLock(void* encoder,void* params,void* output){
 if(!ready||!params)return NV_ENC_ERR_INVALID_PTR;
 auto* caller=static_cast<NV_ENC_LOCK_BITSTREAM*>(params);
 auto copy=*caller;copy.outputBitstream=output;
 const auto result=table.nvEncLockBitstream(encoder,&copy);
 if(result==NV_ENC_SUCCESS){const auto virtual_output=caller->outputBitstream;*caller=copy;caller->outputBitstream=virtual_output;}
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
int StockDestroy(void* encoder){if(ready)trace("encoder session destroyed");return ready?status(table.nvEncDestroyEncoder(encoder)):NV_ENC_ERR_INVALID_PTR;}
}
