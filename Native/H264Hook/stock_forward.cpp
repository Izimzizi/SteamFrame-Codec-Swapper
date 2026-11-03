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
//   enabled=1  rate_control=vbr|cbr  preset=1..7  vbv=1..16
// The file is checked about twice a second, so a change reaches the encoder SteamVR reconfigures before its next frame. Nothing is
// touched unless enabled=1; the driver's own parameters are copied, never edited in place.
namespace {
struct HevcOptions{bool enabled=false;bool cbr=false;int preset=2;int vbv=2;};
HevcOptions options;ULONGLONG options_checked=0;
int applied_signature=-1;bool signature_changed=false;int reconfigure_calls=0;
int applied_preset=0;bool applied_cbr=false;int applied_vbv=0;bool applied_any=false;uint32_t applied_vbv_bits=0;
// Diagnosis of the live HEVC route: what SteamVR's reconfigures carry and what was done with them (hevc-hook.log in the tray's data folder).
void trace(const char* format,...){
 char base[MAX_PATH]{};if(!GetEnvironmentVariableA("LOCALAPPDATA",base,MAX_PATH))return;
 const std::string path=std::string(base)+"\\CodecSwapper\\hevc-hook.log";
 WIN32_FILE_ATTRIBUTE_DATA info{};if(GetFileAttributesExA(path.c_str(),GetFileExInfoStandard,&info)&&info.nFileSizeLow>1000000)MoveFileExA(path.c_str(),(path+".1").c_str(),MOVEFILE_REPLACE_EXISTING);
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
 }
 fclose(file);
}
GUID preset_guid(int preset){
 switch(preset){case 1:return NV_ENC_PRESET_P1_GUID;case 3:return NV_ENC_PRESET_P3_GUID;case 4:return NV_ENC_PRESET_P4_GUID;
  case 5:return NV_ENC_PRESET_P5_GUID;case 6:return NV_ENC_PRESET_P6_GUID;case 7:return NV_ENC_PRESET_P7_GUID;default:return NV_ENC_PRESET_P2_GUID;}
}
// Applies the options to a copy of the driver's init parameters and its encode configuration; returns whether it changed anything.
bool apply_options(const NV_ENC_INITIALIZE_PARAMS& source,NV_ENC_INITIALIZE_PARAMS& init,NV_ENC_CONFIG& config,bool){
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
 // A changed rate control, buffer or preset needs the encoder to restart its rate control: the caller asks for a reset on that one reconfigure.
 applied_vbv_bits=config.rcParams.vbvBufferSize;
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
// Diagnosis of the driver's own HEVC encoder, written the same way for every driver build so two builds can be compared line for line:
//  - "driver config": everything the driver sets when it creates or reconfigures the encoder (logged when it changes);
//  - "output": once a second, what the encoder produced against what it was asked for;
//  - "detail": once a second, how the driver behaves (reconfigures, resets, forced keyframes, asked rate range) and what the encoder did
//    (keyframes, average QP, how long an encode took from submit to result).
static char rate_control_text[400]={};
static char driver_config_text[1400]={};
struct Window{
 ULONGLONG start=0;uint64_t bytes=0,frames=0;uint32_t largest=0;
 uint32_t reconfigures=0,resets=0,force_idr=0,burst=0,avg_min=0xffffffffu,avg_max=0;
 uint32_t encodes=0,flagged_idr=0,idr=0,intra=0,predicted=0;uint64_t qp_sum=0;uint32_t qp_frames=0;
 double ms_sum=0,ms_max=0;uint32_t ms_n=0;
};
static Window win;
struct Stamp{uint64_t timestamp=0;LONGLONG ticks=0;};
static Stamp stamps[256];
static LONGLONG ticks_per_second(){static LONGLONG value=0;if(!value){LARGE_INTEGER f{};QueryPerformanceFrequency(&f);value=f.QuadPart;}return value;}
static LONGLONG ticks_now(){LARGE_INTEGER n{};QueryPerformanceCounter(&n);return n.QuadPart;}
static void note_encode(const NV_ENC_PIC_PARAMS& picture){
 ++win.encodes;if(picture.encodePicFlags&NV_ENC_PIC_FLAG_FORCEIDR)++win.flagged_idr;
 auto& slot=stamps[picture.inputTimeStamp%256];slot.timestamp=picture.inputTimeStamp;slot.ticks=ticks_now();
}
static void note_output(const NV_ENC_LOCK_BITSTREAM& lock){
 const uint32_t bytes=lock.bitstreamSizeInBytes;
 const ULONGLONG now=GetTickCount64();if(!win.start)win.start=now;
 win.bytes+=bytes;++win.frames;if(bytes>win.largest)win.largest=bytes;
 if(lock.pictureType==NV_ENC_PIC_TYPE_IDR)++win.idr;else if(lock.pictureType==NV_ENC_PIC_TYPE_I)++win.intra;else ++win.predicted;
 if(lock.frameAvgQP){win.qp_sum+=lock.frameAvgQP;++win.qp_frames;}
 const auto& slot=stamps[lock.outputTimeStamp%256];
 if(slot.ticks&&slot.timestamp==lock.outputTimeStamp){
  const double ms=(ticks_now()-slot.ticks)*1000.0/ticks_per_second();win.ms_sum+=ms;++win.ms_n;if(ms>win.ms_max)win.ms_max=ms;
 }
 if(now-win.start<1000)return;
 const double seconds=(now-win.start)/1000.0;
 trace("output: %llu frames, %.1f Mbps, frame avg %.0f KB / largest %.0f KB; asked avg=%.1f Mbps peak=%.1f Mbps, buffer %u bits (%.1f KB); driver rate control: %s",
  static_cast<unsigned long long>(win.frames),win.bytes*8/seconds/1e6,win.bytes/1024.0/win.frames,win.largest/1024.0,
  requested_average/1e6,requested_peak/1e6,applied_vbv_bits,applied_vbv_bits/8192.0,rate_control_text);
 trace("detail: reconfigures %u (reset %u, forceIDR %u, peak over 2x avg %u), asked avg %.0f..%.0f Mbps; encodes %u (keyframe flag %u); keyframes IDR %u / I %u / other %u; avg QP %.1f; encode wait avg %.1f ms max %.1f ms",
  win.reconfigures,win.resets,win.force_idr,win.burst,win.avg_min==0xffffffffu?0.0:win.avg_min/1e6,win.avg_max/1e6,win.encodes,win.flagged_idr,
  win.idr,win.intra,win.predicted,win.qp_frames?static_cast<double>(win.qp_sum)/win.qp_frames:0.0,win.ms_n?win.ms_sum/win.ms_n:0.0,win.ms_max);
 const auto keep=win.start;(void)keep;
 win=Window{};win.start=now;
}
static void note_reconfigure(const NV_ENC_RECONFIGURE_PARAMS& params){
 ++win.reconfigures;if(params.resetEncoder)++win.resets;if(params.forceIDR)++win.force_idr;
 const auto* config=params.reInitEncodeParams.encodeConfig;
 if(config&&config->rcParams.averageBitRate){
  const auto average=config->rcParams.averageBitRate;
  if(average<win.avg_min)win.avg_min=average;if(average>win.avg_max)win.avg_max=average;
  if(config->rcParams.maxBitRate>2ull*average)++win.burst;
 }
}
static const char* preset_name(const GUID& guid){
 static char text[16];
 for(int p=1;p<=7;++p){const GUID known=preset_guid(p);if(!memcmp(&guid,&known,sizeof(GUID))){snprintf(text,sizeof text,"P%d",p);return text;}}
 snprintf(text,sizeof text,"%08lx",static_cast<unsigned long>(guid.Data1));return text;
}
// Everything the driver sets for the encoder except the bitrate-dependent numbers (those change every frame); logged when it changes.
static void note_driver_config(const NV_ENC_INITIALIZE_PARAMS& init,const char* where){
 const auto* config=init.encodeConfig;
 if(!config)return;
 const auto& rc=config->rcParams;const auto& h=config->encodeCodecConfig.hevcConfig;
 snprintf(rate_control_text,sizeof rate_control_text,"mode=%d minQP=%d/%u,%u,%u maxQP=%d/%u,%u,%u targetQuality=%u.%u multiPass=%d aq=%u(%u) temporalAq=%u lookahead=%u(%u) nonRefP=%u strictGop=%u zeroReorder=%u initialQP=%d",
  static_cast<int>(rc.rateControlMode),rc.enableMinQP?1:0,rc.minQP.qpInterP,rc.minQP.qpInterB,rc.minQP.qpIntra,rc.enableMaxQP?1:0,rc.maxQP.qpInterP,rc.maxQP.qpInterB,rc.maxQP.qpIntra,
  rc.targetQuality,rc.targetQualityLSB,static_cast<int>(rc.multiPass),rc.enableAQ?1:0,rc.aqStrength,rc.enableTemporalAQ?1:0,rc.enableLookahead?1:0,rc.lookaheadDepth,
  rc.enableNonRefP?1:0,rc.strictGOPTarget?1:0,rc.zeroReorderDelay?1:0,rc.enableInitialRCQP?1:0);
 char text[1400];
 snprintf(text,sizeof text,
  "preset=%s tuning=%d codec=%08lx size=%ux%u max=%ux%u fps=%u/%u async=%u ptd=%u format=%d | profile=%08lx gop=%u bframes+1=%u fieldMode=%d mv=%d | %s | hevc level=%u tier=%u cu=%d..%d idr=%u refsInDpb=%u refL0=%d refL1=%d bRef=%d slice=%u/%u repeatSpsPps=%u aud=%u timingSei=%u bufferingSei=%u filler=%u intraRefresh=%u chroma=%u depth=%u temporalLayers=%u",
  preset_name(init.presetGUID),static_cast<int>(init.tuningInfo),static_cast<unsigned long>(init.encodeGUID.Data1),init.encodeWidth,init.encodeHeight,init.maxEncodeWidth,init.maxEncodeHeight,
  init.frameRateNum,init.frameRateDen,init.enableEncodeAsync,init.enablePTD,static_cast<int>(init.bufferFormat),
  static_cast<unsigned long>(config->profileGUID.Data1),config->gopLength,config->frameIntervalP,static_cast<int>(config->frameFieldMode),static_cast<int>(config->mvPrecision),rate_control_text,
  h.level,h.tier,static_cast<int>(h.minCUSize),static_cast<int>(h.maxCUSize),h.idrPeriod,h.maxNumRefFramesInDPB,static_cast<int>(h.numRefL0),static_cast<int>(h.numRefL1),static_cast<int>(h.useBFramesAsRef),
  h.sliceMode,h.sliceModeData,h.repeatSPSPPS,h.outputAUD,h.outputPictureTimingSEI,h.outputBufferingPeriodSEI,h.enableFillerDataInsertion,h.enableIntraRefresh,h.chromaFormatIDC,h.pixelBitDepthMinus8,h.maxTemporalLayersMinus1);
 if(strcmp(driver_config_text,text)){trace("%s: driver config: %s",where,text);memcpy(driver_config_text,text,sizeof driver_config_text);}
}
// The driver's request: the monitor shows it, and the driver's configuration is logged whenever it changes. The request itself moves about
// every frame, so the log reports it only through the once-a-second lines.
static void note_request(const NV_ENC_INITIALIZE_PARAMS* params,const char* where){
 if(!params||!params->encodeConfig||!params->encodeConfig->rcParams.averageBitRate)return;
 note_driver_config(*params,where);
 const auto* config=params->encodeConfig;
 requested_average=config->rcParams.averageBitRate;requested_peak=config->rcParams.maxBitRate;
}
// Which driver build this is: the file the process loaded.
static void note_driver_module(){
 static bool done=false;if(done)return;done=true;
 const HMODULE module=GetModuleHandleA("driver_vrlink.dll");
 char path[MAX_PATH]{};if(!module||!GetModuleFileNameA(module,path,MAX_PATH)){trace("driver module: driver_vrlink.dll not found in this process");return;}
 WIN32_FILE_ATTRIBUTE_DATA info{};GetFileAttributesExA(path,GetFileExInfoStandard,&info);
 SYSTEMTIME written{};FileTimeToSystemTime(&info.ftLastWriteTime,&written);
 trace("driver module: %s, %lu bytes, written %04d-%02d-%02d %02d:%02d",path,static_cast<unsigned long>(info.nFileSizeLow),written.wYear,written.wMonth,written.wDay,written.wHour,written.wMinute);
}
int StockInitialize(void* encoder,void* init){
 if(!ready)return NV_ENC_ERR_INVALID_PTR;
 auto* original=static_cast<NV_ENC_INITIALIZE_PARAMS*>(init);
 if(original){initialized_width=original->encodeWidth;initialized_height=original->encodeHeight;note_driver_module();driver_config_text[0]=0;note_request(original,"initialize");trace("initialize: picture %ux%u (max %ux%u)",initialized_width,initialized_height,original->maxEncodeWidth,original->maxEncodeHeight);}
 NV_ENC_INITIALIZE_PARAMS patched{};NV_ENC_CONFIG config{};
 reconfigure_calls=0;
 if(original&&apply_options(*original,patched,config,true)){
  trace("initialize: preset P%d asked of the encoder (driver asked for tuning %d, rc=%d)",applied_preset,static_cast<int>(original->tuningInfo),static_cast<int>(config.rcParams.rateControlMode));
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
 if(original){note_reconfigure(*original);note_request(&original->reInitEncodeParams,"reconfigure");}
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
 note_encode(copy);
 return status(table.nvEncEncodePicture(encoder,&copy));
}
int StockLock(void* encoder,void* params,void* output){
 if(!ready||!params)return NV_ENC_ERR_INVALID_PTR;
 auto* caller=static_cast<NV_ENC_LOCK_BITSTREAM*>(params);
 auto copy=*caller;copy.outputBitstream=output;
 copy.getRCStats=1; // asks the encoder to fill in the frame's average QP, which the log reports
 const auto result=table.nvEncLockBitstream(encoder,&copy);
 if(result==NV_ENC_SUCCESS){const auto virtual_output=caller->outputBitstream;*caller=copy;caller->outputBitstream=virtual_output;note_output(copy);}
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
int StockDestroy(void* encoder){if(ready)trace("encoder session destroyed");return ready?status(table.nvEncDestroyEncoder(encoder)):NV_ENC_ERR_INVALID_PTR;}
}
