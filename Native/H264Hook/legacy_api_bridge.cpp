#define HOOK_BUILD
#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include "nvEncodeAPI12.h"
#include "legacy_api_bridge.h"
#include "legacy_picture.h"
#include "stock_forward.h"

namespace {
constexpr uint32_t old_common=0x7101000b,old_table=0x7102000b;
constexpr uint32_t old_init=0xf105000b,old_config=0xf107000b,old_preset=0xf104000b;
constexpr uint32_t old_reconfigure=0xf101000b,old_register=0x7103000b,old_map=0x7104000b;
static_assert(sizeof(NV_ENCODE_API_FUNCTION_LIST)==2552);
static_assert(sizeof(NV_ENC_INITIALIZE_PARAMS)==1808);
static_assert(sizeof(NV_ENC_CONFIG)==3584);
static_assert(sizeof(NV_ENC_REGISTER_RESOURCE)==1536);
static_assert(sizeof(NV_ENC_MAP_INPUT_RESOURCE)==1544);
static_assert(sizeof(NV_ENC_CREATE_BITSTREAM_BUFFER)==776);
static_assert(sizeof(NV_ENC_EVENT_PARAMS)==1544);
static_assert(sizeof(NV_ENC_SEQUENCE_PARAM_PAYLOAD)==1544);
NV_ENCODE_API_FUNCTION_LIST api{};
using Factory=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
Factory factory=nullptr;
HMODULE module=nullptr;
std::mutex mutex;
uint32_t requested_average=100000000,requested_peak=115000000;
bool requested_fixed=false;
uint32_t requested_fps=120;
uint32_t requested_rate_control=0,requested_preset=2,requested_vbv_frames=1;
// Encoder options queued while a session is open; the next session picks them up.
bool pending_encoder=false;
uint32_t pending_rate_control=0,pending_preset=2,pending_vbv_frames=1;
bool valid_budget(uint32_t average,uint32_t peak){
 return average>=1000000&&average<=500000000u&&peak>=average&&peak<=500000000u;
}
GUID preset_guid(){
 switch(requested_preset){
  case 1:return NV_ENC_PRESET_P1_GUID;case 3:return NV_ENC_PRESET_P3_GUID;case 4:return NV_ENC_PRESET_P4_GUID;
  case 5:return NV_ENC_PRESET_P5_GUID;case 6:return NV_ENC_PRESET_P6_GUID;case 7:return NV_ENC_PRESET_P7_GUID;
  default:return NV_ENC_PRESET_P2_GUID;
 }
}
HookBridgeStats stats{sizeof(HookBridgeStats),1};
// SteamVR sees one set of buffer handles. Each wraps the handle in the H.264 session and the one in the stock HEVC session
// (when the driver's own API is available), so the codec can change while SteamVR keeps running.
struct VResource{void* h264=nullptr;void* stock=nullptr;};
struct VOutput{void* h264=nullptr;void* stock=nullptr;int last=0;};
struct VMapped{void* real=nullptr;int on=0;uint32_t format=0;};
int active_mode=0; // 0 H.264, 1 stock HEVC
HookSwitchInfo switch_info{sizeof(HookSwitchInfo),1};
struct State {
 void* stock=nullptr;int last_encoded=-1;
 std::unordered_set<VResource*> resources;std::unordered_set<VOutput*> outputs;std::unordered_set<VMapped*> mapped;
 uint32_t width=0,height=0;
 std::unordered_map<void*,std::vector<int8_t>> maps;
 // The host reconfigures before every frame; the codec preset is fixed per session.
 bool baseline_ready=false;
 NV_ENC_CONFIG baseline{};
};
std::unordered_map<void*,State> sessions;

template<class T> bool upgrade(const T* source,T& copy,uint32_t old,uint32_t modern){
 if(!source||source->version!=old)return false;
 copy=*source;copy.version=modern;return true;
}

NVENCSTATUS NVENCAPI open(NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS* source,void** output){
 NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS copy{};
 if(!output||!upgrade(source,copy,old_common,NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER))return NV_ENC_ERR_INVALID_VERSION;
 if(copy.deviceType!=NV_ENC_DEVICE_TYPE_DIRECTX)return NV_ENC_ERR_UNSUPPORTED_DEVICE;
 copy.apiVersion=NVENCAPI_VERSION;
 // The driver validates an open against the API version of the process's latest factory call; a stock SDK 11.1 session
 // opened beside this one makes that 11.1, so ask for 12.1 again first.
 if(factory){NV_ENCODE_API_FUNCTION_LIST fresh{};fresh.version=NV_ENCODE_API_FUNCTION_LIST_VER;factory(&fresh);}
 const auto status=api.nvEncOpenEncodeSessionEx(&copy,output);
 {std::lock_guard<std::mutex> lock(mutex);stats.last_status=status;
  if(status==NV_ENC_SUCCESS){
   if(pending_encoder){requested_rate_control=pending_rate_control;requested_preset=pending_preset;requested_vbv_frames=pending_vbv_frames;pending_encoder=false;}
   State state{};
   if(StockReady()){const auto stock_status=StockOpen(source,&state.stock);if(stock_status){state.stock=nullptr;++switch_info.stock_errors;switch_info.last_stock_status=uint32_t(stock_status);}}
   sessions.emplace(*output,std::move(state));++stats.opens;
  }else ++stats.errors;}
 return status;
}
NVENCSTATUS NVENCAPI caps(void* encoder,GUID codec,NV_ENC_CAPS_PARAM* source,int* output){
 NV_ENC_CAPS_PARAM copy{};
 if(!upgrade(source,copy,old_common,NV_ENC_CAPS_PARAM_VER))return NV_ENC_ERR_INVALID_VERSION;
 // SteamVR still calls this SDK-11 table as a HEVC encoder and requires its
 // constrained-slice capability. Preserve that legacy capability view; our
 // codec-specific initializer separately validates the actual H.264
 // configuration.
 const auto result=api.nvEncGetEncodeCaps(encoder,codec,&copy,output);
 if(result==NV_ENC_SUCCESS&&output&&(copy.capsToQuery==NV_ENC_CAPS_SUPPORT_10BIT_ENCODE||
    copy.capsToQuery==NV_ENC_CAPS_NUM_MAX_BFRAMES||copy.capsToQuery==NV_ENC_CAPS_SUPPORT_LOOKAHEAD))*output=0;
 // SteamVR initializes at the advertised maximum before choosing the packed
 // stream layout. Bound that allocation to the paired H.264 decoder.
 if(result==NV_ENC_SUCCESS&&output&&copy.capsToQuery==NV_ENC_CAPS_WIDTH_MAX)*output=std::min(*output,1024);
 if(result==NV_ENC_SUCCESS&&output&&copy.capsToQuery==NV_ENC_CAPS_HEIGHT_MAX)*output=std::min(*output,4096);
 return result;
}
NVENCSTATUS preset_common(void* encoder,GUID codec,GUID preset,NV_ENC_TUNING_INFO tuning,NV_ENC_PRESET_CONFIG* source,bool extended){
 if(!source||source->version!=old_preset||source->presetCfg.version!=old_config)return NV_ENC_ERR_INVALID_VERSION;
 NV_ENC_PRESET_CONFIG copy{};copy.version=NV_ENC_PRESET_CONFIG_VER;copy.presetCfg.version=NV_ENC_CONFIG_VER;
 auto status=extended?api.nvEncGetEncodePresetConfigEx(encoder,codec,preset,tuning,&copy):
                      api.nvEncGetEncodePresetConfig(encoder,codec,preset,&copy);
 if(status==NV_ENC_SUCCESS){copy.version=old_preset;copy.presetCfg.version=old_config;
  copy.presetCfg.rcParams.version=old_common;*source=copy;}
 return status;
}
NVENCSTATUS NVENCAPI preset_ex(void* e,GUID c,GUID p,NV_ENC_TUNING_INFO t,NV_ENC_PRESET_CONFIG* s){return preset_common(e,c,p,t,s,true);}
NVENCSTATUS NVENCAPI preset(void* e,GUID c,GUID p,NV_ENC_PRESET_CONFIG* s){return preset_common(e,c,p,NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,s,false);}

bool session_baseline(void* encoder,NV_ENC_CONFIG& output){
 {std::lock_guard<std::mutex> lock(mutex);auto found=sessions.find(encoder);
  if(found!=sessions.end()&&found->second.baseline_ready){output=found->second.baseline;return true;}}
 NV_ENC_PRESET_CONFIG baseline{};baseline.version=NV_ENC_PRESET_CONFIG_VER;baseline.presetCfg.version=NV_ENC_CONFIG_VER;
 if(api.nvEncGetEncodePresetConfigEx(encoder,NV_ENC_CODEC_H264_GUID,preset_guid(),
    NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,&baseline)!=NV_ENC_SUCCESS)return false;
 output=baseline.presetCfg;
 std::lock_guard<std::mutex> lock(mutex);auto found=sessions.find(encoder);
 if(found!=sessions.end()){found->second.baseline=output;found->second.baseline_ready=true;}
 return true;
}
bool configure(void* encoder,const NV_ENC_INITIALIZE_PARAMS& source,NV_ENC_INITIALIZE_PARAMS& init,NV_ENC_CONFIG& config){
 if(source.version!=old_init||!source.encodeConfig||source.encodeConfig->version!=old_config||
    !source.frameRateDen||uint64_t(source.frameRateNum)!=uint64_t(source.frameRateDen)*requested_fps||
    source.encodeWidth<128||source.encodeHeight<128||source.encodeWidth>1024u||source.encodeHeight>4096u)return false;
 NV_ENC_CONFIG baseline{};
 if(!session_baseline(encoder,baseline))return false;
 init=source;config=baseline;
 init.version=NV_ENC_INITIALIZE_PARAMS_VER;config.version=NV_ENC_CONFIG_VER;
 init.encodeGUID=NV_ENC_CODEC_H264_GUID;init.presetGUID=preset_guid();
 init.tuningInfo=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;init.enablePTD=1;init.enableMEOnlyMode=0;
 init.encodeConfig=&config;config.profileGUID=NV_ENC_H264_PROFILE_HIGH_GUID;
 config.frameIntervalP=1;config.gopLength=requested_fps;
 auto& rc=config.rcParams;rc.version=NV_ENC_RC_PARAMS_VER;rc.rateControlMode=requested_rate_control?NV_ENC_PARAMS_RC_CBR:NV_ENC_PARAMS_RC_VBR;
 rc.averageBitRate=requested_fixed?requested_average:std::min(source.encodeConfig->rcParams.averageBitRate,requested_average);
 if(rc.averageBitRate<1000000)rc.averageBitRate=requested_average;
 rc.maxBitRate=requested_fixed?requested_peak:std::min(std::max(source.encodeConfig->rcParams.maxBitRate,rc.averageBitRate),requested_peak);
 // Constant bitrate: the peak is the average.
 if(requested_rate_control)rc.maxBitRate=rc.averageBitRate;
 rc.vbvBufferSize=rc.averageBitRate/requested_fps*requested_vbv_frames;rc.vbvInitialDelay=0;
 rc.enableLookahead=0;rc.lookaheadDepth=0;rc.enableAQ=0;rc.enableTemporalAQ=0;
 rc.enableMinQP=0;rc.enableMaxQP=0;rc.enableInitialRCQP=0;
 rc.multiPass=NV_ENC_MULTI_PASS_DISABLED;rc.zeroReorderDelay=1;rc.qpMapMode=NV_ENC_QP_MAP_DELTA;
 {
  auto& h264=config.encodeCodecConfig.h264Config;
  h264.level=requested_average>200000000?NV_ENC_LEVEL_AUTOSELECT:NV_ENC_LEVEL_H264_52;
  h264.idrPeriod=requested_fps;h264.chromaFormatIDC=1;
  // Advertise the actual small DPB. Without VUI bitstream restrictions, the
  // Frame driver derives 18 capture buffers from level 6.1; VRLink has only
  // 16 backing surfaces and repeatedly fails QBUF at index 16.
  h264.maxNumRefFrames=3;h264.h264VUIParameters.bitstreamRestrictionFlag=1;
  // At high bitrates CABAC entropy decoding stalls before pixel throughput
  // is exhausted. CAVLC passed the paired 400-Mbps/90-Hz hardware test.
  if(requested_average>200000000)h264.entropyCodingMode=NV_ENC_H264_ENTROPY_CODING_MODE_CAVLC;
  h264.repeatSPSPPS=1;h264.disableSPSPPS=0;h264.outputAUD=1;h264.enableFillerDataInsertion=0;
  h264.sliceMode=3;h264.sliceModeData=1;
 }
 return true;
}
NVENCSTATUS NVENCAPI initialize(void* encoder,NV_ENC_INITIALIZE_PARAMS* source){
 if(!source)return NV_ENC_ERR_INVALID_PTR;
 {std::lock_guard<std::mutex> lock(mutex);++stats.initialize_attempts;
  stats.width=source->encodeWidth;stats.height=source->encodeHeight;
  stats.source_fps_num=source->frameRateNum;stats.source_fps_den=source->frameRateDen;
  stats.source_async=source->enableEncodeAsync;}
 NV_ENC_INITIALIZE_PARAMS copy{};NV_ENC_CONFIG config{};
 if(!configure(encoder,*source,copy,config)){
  std::lock_guard<std::mutex> lock(mutex);stats.initialize_status=NV_ENC_ERR_INVALID_PARAM;
  strcpy_s(stats.initialize_error,"Source layout/rate/dimensions or paired-codec preset rejected");
  return NV_ENC_ERR_INVALID_PARAM;
 }
 const auto status=api.nvEncInitializeEncoder(encoder,&copy);
 {std::lock_guard<std::mutex> lock(mutex);stats.last_status=status;
  stats.initialize_status=status;stats.initialize_error[0]=0;
  if(status!=NV_ENC_SUCCESS&&api.nvEncGetLastErrorString){const auto* error=api.nvEncGetLastErrorString(encoder);
   if(error)strncpy_s(stats.initialize_error,error,_TRUNCATE);}
  if(status==NV_ENC_SUCCESS){auto found=sessions.find(encoder);if(found==sessions.end())return NV_ENC_ERR_INVALID_ENCODERDEVICE;
   found->second.width=copy.encodeWidth;found->second.height=copy.encodeHeight;
   if(found->second.stock&&StockInitialize(found->second.stock,source)){
    // The stock session cannot take SteamVR's own parameters: this session streams H.264 only.
    ++switch_info.stock_errors;StockDestroy(found->second.stock);found->second.stock=nullptr;
   }
   ++stats.initializes;stats.width=copy.encodeWidth;stats.height=copy.encodeHeight;
   stats.average_bps=config.rcParams.averageBitRate;stats.peak_bps=config.rcParams.maxBitRate;}else ++stats.errors;}
 return status;
}
NVENCSTATUS reconfigure_h264(void* encoder,NV_ENC_RECONFIGURE_PARAMS* source){
 NV_ENC_RECONFIGURE_PARAMS copy{};NV_ENC_CONFIG config{};
 auto reject=[](NVENCSTATUS status){std::lock_guard<std::mutex> lock(mutex);
  ++stats.errors;stats.last_status=status;return status;};
 if(!upgrade(source,copy,old_reconfigure,NV_ENC_RECONFIGURE_PARAMS_VER))return reject(NV_ENC_ERR_INVALID_VERSION);
 // The host puts its measured throughput in rate updates. It can fall below
 // display cadence or briefly exceed it while draining buffered frames. Keep
 // the session's selected nominal rate rather than rejecting those estimates.
 auto requested=source->reInitEncodeParams;
 bool valid=false;
 {std::lock_guard<std::mutex> lock(mutex);auto found=sessions.find(encoder);
  valid=found!=sessions.end()&&found->second.width&&requested.frameRateDen&&
     uint64_t(requested.frameRateNum)>=uint64_t(requested.frameRateDen)&&
     uint64_t(requested.frameRateNum)<=uint64_t(requested.frameRateDen)*1000;}
 if(!valid)return reject(NV_ENC_ERR_INVALID_PARAM);
 requested.frameRateNum=requested_fps;requested.frameRateDen=1;
 // Encoder options queued while this session runs are adopted here: the host reconfigures before every frame, so the
 // new rate control, VBV and preset reach the running encoder without a new session.
 bool adopted=false;uint32_t old_rate_control=0,old_preset=0,old_vbv=0;
 {std::lock_guard<std::mutex> lock(mutex);
  if(pending_encoder){
   old_rate_control=requested_rate_control;old_preset=requested_preset;old_vbv=requested_vbv_frames;
   requested_rate_control=pending_rate_control;requested_preset=pending_preset;requested_vbv_frames=pending_vbv_frames;pending_encoder=false;adopted=true;
   auto found=sessions.find(encoder);if(found!=sessions.end()&&old_preset!=requested_preset)found->second.baseline_ready=false;
  }}
 if(!configure(encoder,requested,copy.reInitEncodeParams,config))return reject(NV_ENC_ERR_INVALID_PARAM);
 auto status=api.nvEncReconfigureEncoder(encoder,&copy);
 if(adopted&&status!=NV_ENC_SUCCESS){
  // The encoder refused the new options (a preset cannot always change in place): keep running on the old ones.
  {std::lock_guard<std::mutex> lock(mutex);requested_rate_control=old_rate_control;requested_preset=old_preset;requested_vbv_frames=old_vbv;
   auto found=sessions.find(encoder);if(found!=sessions.end())found->second.baseline_ready=false;}
  copy=NV_ENC_RECONFIGURE_PARAMS{};config=NV_ENC_CONFIG{};
  if(!upgrade(source,copy,old_reconfigure,NV_ENC_RECONFIGURE_PARAMS_VER)||!configure(encoder,requested,copy.reInitEncodeParams,config))return reject(NV_ENC_ERR_INVALID_PARAM);
  status=api.nvEncReconfigureEncoder(encoder,&copy);
 }
 {std::lock_guard<std::mutex> lock(mutex);++stats.reconfigures;stats.last_status=status;
  if(status!=NV_ENC_SUCCESS)++stats.errors;
  else {stats.width=copy.reInitEncodeParams.encodeWidth;stats.height=copy.reInitEncodeParams.encodeHeight;
   stats.average_bps=config.rcParams.averageBitRate;stats.peak_bps=config.rcParams.maxBitRate;}}
 return status;
}
NVENCSTATUS NVENCAPI reconfigure(void* encoder,NV_ENC_RECONFIGURE_PARAMS* source){
 const auto h264=reconfigure_h264(encoder,source);
 void* stock=nullptr;int mode=0;
 {std::lock_guard<std::mutex> lock(mutex);auto found=sessions.find(encoder);if(found!=sessions.end())stock=found->second.stock;mode=active_mode;}
 if(!stock)return h264;
 // The stock encoder follows SteamVR's own reconfigure requests unchanged, so a switch finds it already at the right size and rate.
 const auto result=StockReconfigure(stock,source);
 if(result){std::lock_guard<std::mutex> lock(mutex);++switch_info.stock_errors;}
 return mode==1?static_cast<NVENCSTATUS>(result):h264;
}
State* find_session(void* encoder){auto found=sessions.find(encoder);return found==sessions.end()?nullptr:&found->second;}
NVENCSTATUS NVENCAPI create_output(void* encoder,NV_ENC_CREATE_BITSTREAM_BUFFER* source){
 NV_ENC_CREATE_BITSTREAM_BUFFER copy{};
 if(!upgrade(source,copy,old_common,NV_ENC_CREATE_BITSTREAM_BUFFER_VER))return NV_ENC_ERR_INVALID_VERSION;
 std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);if(!session)return NV_ENC_ERR_INVALID_ENCODERDEVICE;
 const auto status=api.nvEncCreateBitstreamBuffer(encoder,&copy);
 if(status!=NV_ENC_SUCCESS)return status;
 auto* output=new VOutput{copy.bitstreamBuffer,nullptr,0};
 if(session->stock&&StockCreateOutput(session->stock,source,&output->stock)){output->stock=nullptr;++switch_info.stock_errors;}
 session->outputs.insert(output);source->bitstreamBuffer=output;
 return status;
}
NVENCSTATUS NVENCAPI destroy_output(void* encoder,NV_ENC_OUTPUT_PTR handle){
 std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);if(!session)return NV_ENC_ERR_INVALID_ENCODERDEVICE;
 auto* output=static_cast<VOutput*>(handle);if(!session->outputs.erase(output))return NV_ENC_ERR_INVALID_PTR;
 if(output->stock)StockDestroyOutput(session->stock,output->stock);
 const auto status=api.nvEncDestroyBitstreamBuffer(encoder,output->h264);
 session->maps.erase(output);delete output;return status;
}
NVENCSTATUS NVENCAPI register_resource(void* encoder,NV_ENC_REGISTER_RESOURCE* source){
 NV_ENC_REGISTER_RESOURCE copy{};
 if(!upgrade(source,copy,old_register,NV_ENC_REGISTER_RESOURCE_VER))return NV_ENC_ERR_INVALID_VERSION;
 std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);if(!session)return NV_ENC_ERR_INVALID_ENCODERDEVICE;
 const auto status=api.nvEncRegisterResource(encoder,&copy);
 if(status!=NV_ENC_SUCCESS)return status;
 auto* resource=new VResource{copy.registeredResource,nullptr};
 if(session->stock&&StockRegister(session->stock,source,&resource->stock)){resource->stock=nullptr;++switch_info.stock_errors;}
 session->resources.insert(resource);source->registeredResource=resource;
 return status;
}
NVENCSTATUS NVENCAPI unregister_resource(void* encoder,NV_ENC_REGISTERED_PTR handle){
 std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);if(!session)return NV_ENC_ERR_INVALID_ENCODERDEVICE;
 auto* resource=static_cast<VResource*>(handle);if(!session->resources.erase(resource))return NV_ENC_ERR_INVALID_PTR;
 if(resource->stock)StockUnregister(session->stock,resource->stock);
 const auto status=api.nvEncUnregisterResource(encoder,resource->h264);
 delete resource;return status;
}
NVENCSTATUS NVENCAPI map_resource(void* encoder,NV_ENC_MAP_INPUT_RESOURCE* source){
 NV_ENC_MAP_INPUT_RESOURCE copy{};
 if(!upgrade(source,copy,old_map,NV_ENC_MAP_INPUT_RESOURCE_VER))return NV_ENC_ERR_INVALID_VERSION;
 std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);if(!session)return NV_ENC_ERR_INVALID_ENCODERDEVICE;
 auto* resource=static_cast<VResource*>(source->registeredResource);if(!session->resources.count(resource))return NV_ENC_ERR_INVALID_PTR;
 // The codec is chosen here, per frame: the mapped handle remembers which session it belongs to.
 const int on=(active_mode==1&&session->stock&&resource->stock)?1:0;
 void* real=nullptr;uint32_t format=0;NVENCSTATUS status;
 if(on)status=static_cast<NVENCSTATUS>(StockMap(session->stock,source,resource->stock,&real,&format));
 else{copy.registeredResource=resource->h264;status=api.nvEncMapInputResource(encoder,&copy);real=copy.mappedResource;format=copy.mappedBufferFmt;}
 if(status!=NV_ENC_SUCCESS)return status;
 auto* mapped=new VMapped{real,on,format};session->mapped.insert(mapped);
 source->mappedResource=mapped;source->mappedBufferFmt=static_cast<NV_ENC_BUFFER_FORMAT>(format);
 return status;
}
NVENCSTATUS NVENCAPI unmap_resource(void* encoder,NV_ENC_INPUT_PTR handle){
 std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);if(!session)return NV_ENC_ERR_INVALID_ENCODERDEVICE;
 auto* mapped=static_cast<VMapped*>(handle);if(!session->mapped.erase(mapped))return NV_ENC_ERR_INVALID_PTR;
 const auto status=mapped->on?static_cast<NVENCSTATUS>(StockUnmap(session->stock,mapped->real)):api.nvEncUnmapInputResource(encoder,mapped->real);
 delete mapped;return status;
}
NVENCSTATUS event_common(void* encoder,NV_ENC_EVENT_PARAMS* source,bool registration){
 NV_ENC_EVENT_PARAMS copy{};
 if(!upgrade(source,copy,old_common,NV_ENC_EVENT_PARAMS_VER))return NV_ENC_ERR_INVALID_VERSION;
 const auto status=registration?api.nvEncRegisterAsyncEvent(encoder,&copy):api.nvEncUnregisterAsyncEvent(encoder,&copy);
 std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);
 // The completion event is a plain handle: both sessions may be given it, and only the one that encodes the frame signals it.
 if(session&&session->stock){const auto stock_status=StockEvent(session->stock,source,registration?1:0);
  // The driver refuses to register the same event with a second session, yet signals it for the frames that session encodes
  // (the mode switch test waits on it), so this is recorded but is not an error.
  if(stock_status)switch_info.last_stock_status=uint32_t(stock_status)+(registration?1000u:2000u);}
 return status;
}
NVENCSTATUS NVENCAPI register_event(void* e,NV_ENC_EVENT_PARAMS* p){return event_common(e,p,true);}
NVENCSTATUS NVENCAPI unregister_event(void* e,NV_ENC_EVENT_PARAMS* p){return event_common(e,p,false);}
NVENCSTATUS NVENCAPI sequence(void* encoder,NV_ENC_SEQUENCE_PARAM_PAYLOAD* source){
 NV_ENC_SEQUENCE_PARAM_PAYLOAD copy{};
 if(!upgrade(source,copy,old_common,NV_ENC_SEQUENCE_PARAM_PAYLOAD_VER))return NV_ENC_ERR_INVALID_VERSION;
 return api.nvEncGetSequenceParams(encoder,&copy);
}
NVENCSTATUS NVENCAPI encode(void* encoder,NV_ENC_PIC_PARAMS* source){
 HookLegacyPicture view{sizeof(HookLegacyPicture),1};
 if(HookReadLegacyPicture(source,3344,&view))return NV_ENC_ERR_INVALID_VERSION;
 std::lock_guard<std::mutex> lock(mutex);
 auto* session=find_session(encoder);if(!session)return NV_ENC_ERR_INVALID_ENCODERDEVICE;
 auto* mapped=static_cast<VMapped*>(view.input);auto* output=static_cast<VOutput*>(view.output);
 const bool eos=(view.flags&NV_ENC_PIC_FLAG_EOS)!=0;
 // An end-of-stream picture carries no input.
 if(!eos&&!session->mapped.count(mapped))return NV_ENC_ERR_INVALID_PTR;
 if(!session->outputs.count(output))return NV_ENC_ERR_INVALID_PTR;
 const int on=!eos?mapped->on:(session->last_encoded==1?1:0);
 void* real_output=on?output->stock:output->h264;if(!real_output)return NV_ENC_ERR_INVALID_PARAM;
 // The first picture a session encodes after a switch starts a fresh sequence: its decoder has seen nothing of this stream.
 uint32_t extra_flags=0;
 if(!eos){if(session->last_encoded<0)session->last_encoded=on;
  else if(session->last_encoded!=on){extra_flags=NV_ENC_PIC_FLAG_FORCEIDR|NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;session->last_encoded=on;}}
 output->last=on;
 NVENCSTATUS status;
 if(on){
  status=static_cast<NVENCSTATUS>(StockEncode(session->stock,source,eos?nullptr:mapped->real,real_output,extra_flags));++switch_info.stock_encodes;++stats.encodes;
  if(status!=NV_ENC_SUCCESS&&status!=NV_ENC_ERR_NEED_MORE_INPUT){++stats.errors;++switch_info.stock_errors;}
  return status;
 }
 NV_ENC_PIC_PARAMS copy{};copy.version=NV_ENC_PIC_PARAMS_VER;
 copy.inputWidth=view.width;copy.inputHeight=view.height;copy.inputPitch=view.pitch;
 copy.encodePicFlags=view.flags|extra_flags;copy.frameIdx=view.frame_index;copy.inputTimeStamp=view.timestamp;copy.inputDuration=view.duration;
 copy.inputBuffer=eos?nullptr:mapped->real;copy.outputBitstream=real_output;copy.completionEvent=view.completion_event;
 copy.bufferFmt=static_cast<NV_ENC_BUFFER_FORMAT>(view.buffer_format);copy.pictureStruct=static_cast<NV_ENC_PIC_STRUCT>(view.picture_structure);
 if(view.hevc_delta_map&&!eos){
  const unsigned width=(view.width+31)/32,height=(view.height+31)/32;
  if(!width||!height||view.hevc_map_entries!=width*height)return NV_ENC_ERR_INVALID_PARAM;
  const unsigned block=16;
  const unsigned columns=(view.width+block-1)/block,rows=(view.height+block-1)/block;
  auto& map=session->maps[output];map.resize(columns*rows);
  for(unsigned y=0;y<rows;++y)for(unsigned x=0;x<columns;++x)
   map[y*columns+x]=static_cast<int8_t>(std::clamp(int(view.hevc_delta_map[(y/2)*width+x/2]),-51,51));
  // Retain the HEVC 32x32 timing tile at high quality: every H.264 macroblock it covers.
  for(unsigned y=2*(height-1);y<rows;++y)for(unsigned x=0;x<std::min(2u,columns);++x)map[y*columns+x]=-51;
  copy.qpDeltaMap=map.data();copy.qpDeltaMapSize=static_cast<uint32_t>(map.size());
 }
 status=api.nvEncEncodePicture(encoder,&copy);++stats.encodes;stats.last_status=status;
 if(status!=NV_ENC_SUCCESS&&status!=NV_ENC_ERR_NEED_MORE_INPUT)++stats.errors;
 return status;
}
NVENCSTATUS NVENCAPI lock_output(void* encoder,NV_ENC_LOCK_BITSTREAM* source){
 if(!source||source->version!=old_common)return NV_ENC_ERR_INVALID_VERSION;
 VOutput* output=nullptr;void* stock=nullptr;
 {std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);if(!session)return NV_ENC_ERR_INVALID_ENCODERDEVICE;
  output=static_cast<VOutput*>(source->outputBitstream);if(!session->outputs.count(output))return NV_ENC_ERR_INVALID_PTR;stock=session->stock;}
 if(output->last==1&&stock&&output->stock){
  // Stock HEVC: the driver's own structure, filled in place.
  const auto result=static_cast<NVENCSTATUS>(StockLock(stock,source,output->stock));
  std::lock_guard<std::mutex> lock(mutex);
  if(result==NV_ENC_SUCCESS){++switch_info.stock_locks;++stats.locks;stats.bytes+=source->bitstreamSizeInBytes;}else{++stats.errors;++switch_info.stock_errors;}
  return result;
 }
 // Only the audited common request/result prefix is accessed in the legacy
 // object; never copy/write the 1552-byte modern struct into its 1544 bytes.
 NV_ENC_LOCK_BITSTREAM copy{};copy.version=NV_ENC_LOCK_BITSTREAM_VER;
 copy.doNotWait=source->doNotWait;copy.outputBitstream=output->h264;copy.sliceOffsets=source->sliceOffsets;
 const auto status=api.nvEncLockBitstream(encoder,&copy);
 if(status==NV_ENC_SUCCESS){
  source->frameIdx=copy.frameIdx;source->hwEncodeStatus=copy.hwEncodeStatus;
  source->numSlices=copy.numSlices;source->bitstreamSizeInBytes=copy.bitstreamSizeInBytes;
  source->outputTimeStamp=copy.outputTimeStamp;source->outputDuration=copy.outputDuration;
  source->bitstreamBufferPtr=copy.bitstreamBufferPtr;source->pictureType=copy.pictureType;
  if(copy.pictureType==NV_ENC_PIC_TYPE_I)source->pictureType=NV_ENC_PIC_TYPE_IDR;
  source->pictureStruct=copy.pictureStruct;source->frameAvgQP=copy.frameAvgQP;source->frameSatd=copy.frameSatd;
 }
 {std::lock_guard<std::mutex> lock(mutex);stats.last_status=status;
  if(status==NV_ENC_SUCCESS){++stats.locks;stats.bytes+=copy.bitstreamSizeInBytes;}else ++stats.errors;}
 return status;
}
NVENCSTATUS NVENCAPI unlock_output(void* encoder,NV_ENC_OUTPUT_PTR handle){
 std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);if(!session)return NV_ENC_ERR_INVALID_ENCODERDEVICE;
 auto* output=static_cast<VOutput*>(handle);if(!session->outputs.count(output))return NV_ENC_ERR_INVALID_PTR;
 if(output->last==1&&session->stock&&output->stock)return static_cast<NVENCSTATUS>(StockUnlock(session->stock,output->stock));
 return api.nvEncUnlockBitstream(encoder,output->h264);
}
NVENCSTATUS NVENCAPI destroy(void* encoder){
 void* stock=nullptr;
 {std::lock_guard<std::mutex> lock(mutex);auto* session=find_session(encoder);if(session){
  stock=session->stock;
  for(auto* item:session->resources)delete item;
  for(auto* item:session->outputs)delete item;
  for(auto* item:session->mapped)delete item;
  session->resources.clear();session->outputs.clear();session->mapped.clear();}}
 if(stock)StockDestroy(stock);
 const auto status=api.nvEncDestroyEncoder(encoder);
 if(status==NV_ENC_SUCCESS){std::lock_guard<std::mutex> lock(mutex);sessions.erase(encoder);}
 return status;
}
}

extern "C" int HOOK_CALL HookCreateLegacyFunctionTable(void* raw,uint32_t bytes,uint32_t average,uint32_t peak){
 if(!raw||bytes!=2552||*static_cast<uint32_t*>(raw)!=old_table)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 if(!valid_budget(average,peak))return -1;
 if(!sessions.empty()&&(average!=requested_average||peak!=requested_peak))return -2;
 if(!module){
  module=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);if(!module)return -3;
  auto create=reinterpret_cast<Factory>(GetProcAddress(module,"NvEncodeAPICreateInstance"));
  api.version=NV_ENCODE_API_FUNCTION_LIST_VER;if(!create||create(&api)!=NV_ENC_SUCCESS)return -4;
  factory=create;
 }
 requested_average=average;requested_peak=peak;
 auto table=api;table.version=old_table;
 table.nvEncOpenEncodeSessionEx=open;table.nvEncGetEncodeCaps=caps;
 table.nvEncGetEncodePresetConfig=preset;table.nvEncGetEncodePresetConfigEx=preset_ex;
 table.nvEncInitializeEncoder=initialize;table.nvEncReconfigureEncoder=reconfigure;
 table.nvEncCreateBitstreamBuffer=create_output;table.nvEncDestroyBitstreamBuffer=destroy_output;
 table.nvEncRegisterResource=register_resource;table.nvEncUnregisterResource=unregister_resource;
 table.nvEncMapInputResource=map_resource;table.nvEncUnmapInputResource=unmap_resource;table.nvEncUnlockBitstream=unlock_output;
 table.nvEncRegisterAsyncEvent=register_event;
 table.nvEncUnregisterAsyncEvent=unregister_event;table.nvEncGetSequenceParams=sequence;
 table.nvEncEncodePicture=encode;table.nvEncLockBitstream=lock_output;table.nvEncDestroyEncoder=destroy;
 memcpy(raw,&table,sizeof(table));++stats.tables;return 0;
}

extern "C" int HOOK_CALL HookConfigureLegacyBudget(uint32_t average,uint32_t peak,uint32_t fixed){
 std::lock_guard<std::mutex> lock(mutex);
 if(!valid_budget(average,peak)||fixed>1)return -1;
 if(!sessions.empty())return -2;
 requested_average=average;requested_peak=peak;requested_fixed=fixed!=0;return 0;
}

extern "C" int HOOK_CALL HookConfigureLegacyRate(uint32_t fps){
 if(fps!=72&&fps!=80&&fps!=90&&fps!=120)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 if(!sessions.empty())return -2;
 requested_fps=fps;return 0;
}

extern "C" int HOOK_CALL HookConfigureLegacyEncoder(uint32_t rate_control,uint32_t preset,uint32_t vbv_frames){
 if(rate_control>1||preset<1||preset>7||vbv_frames<1||vbv_frames>16)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 if(!sessions.empty())return -2;
 requested_rate_control=rate_control;requested_preset=preset;requested_vbv_frames=vbv_frames;return 0;
}
extern "C" int HOOK_CALL HookQueueLegacyEncoder(uint32_t rate_control,uint32_t preset,uint32_t vbv_frames){
 if(rate_control>1||preset<1||preset>7||vbv_frames<1||vbv_frames>16)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 if(sessions.empty()){requested_rate_control=rate_control;requested_preset=preset;requested_vbv_frames=vbv_frames;pending_encoder=false;return 0;}
 pending_rate_control=rate_control;pending_preset=preset;pending_vbv_frames=vbv_frames;pending_encoder=true;return 0;
}
extern "C" int HOOK_CALL HookGetLegacyIdentity(HookTrialIdentity* output){
 if(!output||output->size!=sizeof(HookTrialIdentity)||output->version!=1)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 // The identity layout is shared with the managed reader: codec 2 is H.264, the only codec this hook produces.
 *output={sizeof(HookTrialIdentity),1,active_mode==1?3u:2u,requested_fps,requested_average,requested_peak,requested_rate_control,requested_preset,requested_vbv_frames};return 0;
}

extern "C" int HOOK_CALL HookProvideStockTable(const void* sdk11_table,uint32_t bytes,void* factory){
 std::lock_guard<std::mutex> lock(mutex);return StockSetTable(sdk11_table,bytes,factory);
}
// 0 = H.264, 1 = the driver's own HEVC. Takes effect at the next frame SteamVR maps; refused unless every open session has a stock encoder.
extern "C" int HOOK_CALL HookSetLegacyMode(uint32_t mode){
 if(mode>1)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 if(mode==1){
  if(!StockReady())return -2;
  for(auto& entry:sessions)if(!entry.second.stock)return -3;
 }
 if(active_mode!=int(mode)){active_mode=int(mode);++switch_info.switches;}
 return 0;
}
extern "C" int HOOK_CALL HookGetLegacySwitchInfo(HookSwitchInfo* output){
 if(!output||output->size!=sizeof(HookSwitchInfo)||output->version!=1)return -1;
 std::lock_guard<std::mutex> lock(mutex);*output=switch_info;output->mode=uint32_t(active_mode);
 output->sessions=uint32_t(sessions.size());output->stock_sessions=0;
 for(auto& entry:sessions)if(entry.second.stock)++output->stock_sessions;
 return 0;
}

extern "C" int HOOK_CALL HookGetLegacyBridgeStats(HookBridgeStats* output){
 if(!output||output->size!=sizeof(HookBridgeStats)||output->version!=1)return -1;
 std::lock_guard<std::mutex> lock(mutex);*output=stats;return 0;
}
