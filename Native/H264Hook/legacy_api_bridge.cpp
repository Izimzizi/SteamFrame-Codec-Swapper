#define HOOK_BUILD
#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>
#include "nvEncodeAPI12.h"
#include "legacy_api_bridge.h"
#include "legacy_picture.h"

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
HMODULE module=nullptr;
std::mutex mutex;
uint32_t requested_average=100000000,requested_peak=115000000;
bool requested_fixed=false;
uint32_t requested_fps=120;
uint32_t requested_rate_control=0,requested_preset=2,requested_vbv_frames=1;
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
struct State {
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
 const auto status=api.nvEncOpenEncodeSessionEx(&copy,output);
 {std::lock_guard<std::mutex> lock(mutex);stats.last_status=status;
  if(status==NV_ENC_SUCCESS){sessions.emplace(*output,State{});++stats.opens;}else ++stats.errors;}
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
   ++stats.initializes;stats.width=copy.encodeWidth;stats.height=copy.encodeHeight;
   stats.average_bps=config.rcParams.averageBitRate;stats.peak_bps=config.rcParams.maxBitRate;}else ++stats.errors;}
 return status;
}
NVENCSTATUS NVENCAPI reconfigure(void* encoder,NV_ENC_RECONFIGURE_PARAMS* source){
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
 if(!configure(encoder,requested,copy.reInitEncodeParams,config))return reject(NV_ENC_ERR_INVALID_PARAM);
 const auto status=api.nvEncReconfigureEncoder(encoder,&copy);
 {std::lock_guard<std::mutex> lock(mutex);++stats.reconfigures;stats.last_status=status;
  if(status!=NV_ENC_SUCCESS)++stats.errors;
  else {stats.width=copy.reInitEncodeParams.encodeWidth;stats.height=copy.reInitEncodeParams.encodeHeight;
   stats.average_bps=config.rcParams.averageBitRate;stats.peak_bps=config.rcParams.maxBitRate;}}
 return status;
}
NVENCSTATUS NVENCAPI create_output(void* encoder,NV_ENC_CREATE_BITSTREAM_BUFFER* source){
 NV_ENC_CREATE_BITSTREAM_BUFFER copy{};
 if(!upgrade(source,copy,old_common,NV_ENC_CREATE_BITSTREAM_BUFFER_VER))return NV_ENC_ERR_INVALID_VERSION;
 const auto status=api.nvEncCreateBitstreamBuffer(encoder,&copy);
 if(status==NV_ENC_SUCCESS)source->bitstreamBuffer=copy.bitstreamBuffer;
 return status;
}
NVENCSTATUS NVENCAPI register_resource(void* encoder,NV_ENC_REGISTER_RESOURCE* source){
 NV_ENC_REGISTER_RESOURCE copy{};
 if(!upgrade(source,copy,old_register,NV_ENC_REGISTER_RESOURCE_VER))return NV_ENC_ERR_INVALID_VERSION;
 const auto status=api.nvEncRegisterResource(encoder,&copy);
 if(status==NV_ENC_SUCCESS)source->registeredResource=copy.registeredResource;
 return status;
}
NVENCSTATUS NVENCAPI map_resource(void* encoder,NV_ENC_MAP_INPUT_RESOURCE* source){
 NV_ENC_MAP_INPUT_RESOURCE copy{};
 if(!upgrade(source,copy,old_map,NV_ENC_MAP_INPUT_RESOURCE_VER))return NV_ENC_ERR_INVALID_VERSION;
 const auto status=api.nvEncMapInputResource(encoder,&copy);
 if(status==NV_ENC_SUCCESS){source->mappedResource=copy.mappedResource;source->mappedBufferFmt=copy.mappedBufferFmt;}
 return status;
}
NVENCSTATUS event_common(void* encoder,NV_ENC_EVENT_PARAMS* source,bool registration){
 NV_ENC_EVENT_PARAMS copy{};
 if(!upgrade(source,copy,old_common,NV_ENC_EVENT_PARAMS_VER))return NV_ENC_ERR_INVALID_VERSION;
 return registration?api.nvEncRegisterAsyncEvent(encoder,&copy):api.nvEncUnregisterAsyncEvent(encoder,&copy);
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
 NV_ENC_PIC_PARAMS copy{};copy.version=NV_ENC_PIC_PARAMS_VER;
 copy.inputWidth=view.width;copy.inputHeight=view.height;copy.inputPitch=view.pitch;
 copy.encodePicFlags=view.flags;copy.frameIdx=view.frame_index;copy.inputTimeStamp=view.timestamp;copy.inputDuration=view.duration;
 copy.inputBuffer=view.input;copy.outputBitstream=view.output;copy.completionEvent=view.completion_event;
 copy.bufferFmt=static_cast<NV_ENC_BUFFER_FORMAT>(view.buffer_format);copy.pictureStruct=static_cast<NV_ENC_PIC_STRUCT>(view.picture_structure);
 std::lock_guard<std::mutex> lock(mutex);
 auto found=sessions.find(encoder);if(found==sessions.end())return NV_ENC_ERR_INVALID_ENCODERDEVICE;
 if(view.hevc_delta_map&&!(view.flags&NV_ENC_PIC_FLAG_EOS)){
  const unsigned width=(view.width+31)/32,height=(view.height+31)/32;
  if(!width||!height||view.hevc_map_entries!=width*height)return NV_ENC_ERR_INVALID_PARAM;
  const unsigned block=16;
  const unsigned columns=(view.width+block-1)/block,rows=(view.height+block-1)/block;
  auto& map=found->second.maps[view.output];map.resize(columns*rows);
  for(unsigned y=0;y<rows;++y)for(unsigned x=0;x<columns;++x)
   map[y*columns+x]=static_cast<int8_t>(std::clamp(int(view.hevc_delta_map[(y/2)*width+x/2]),-51,51));
  // Retain the HEVC 32x32 timing tile at high quality: every H.264 macroblock it covers.
  for(unsigned y=2*(height-1);y<rows;++y)for(unsigned x=0;x<std::min(2u,columns);++x)map[y*columns+x]=-51;
  copy.qpDeltaMap=map.data();copy.qpDeltaMapSize=static_cast<uint32_t>(map.size());
 }
 const auto status=api.nvEncEncodePicture(encoder,&copy);++stats.encodes;stats.last_status=status;
 if(status!=NV_ENC_SUCCESS&&status!=NV_ENC_ERR_NEED_MORE_INPUT)++stats.errors;
 return status;
}
NVENCSTATUS NVENCAPI lock_output(void* encoder,NV_ENC_LOCK_BITSTREAM* source){
 if(!source||source->version!=old_common)return NV_ENC_ERR_INVALID_VERSION;
 // Only the audited common request/result prefix is accessed in the legacy
 // object; never copy/write the 1552-byte modern struct into its 1544 bytes.
 NV_ENC_LOCK_BITSTREAM copy{};copy.version=NV_ENC_LOCK_BITSTREAM_VER;
 copy.doNotWait=source->doNotWait;copy.outputBitstream=source->outputBitstream;copy.sliceOffsets=source->sliceOffsets;
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
NVENCSTATUS NVENCAPI destroy(void* encoder){
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
  using Create=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
  auto create=reinterpret_cast<Create>(GetProcAddress(module,"NvEncodeAPICreateInstance"));
  api.version=NV_ENCODE_API_FUNCTION_LIST_VER;if(!create||create(&api)!=NV_ENC_SUCCESS)return -4;
 }
 requested_average=average;requested_peak=peak;
 auto table=api;table.version=old_table;
 table.nvEncOpenEncodeSessionEx=open;table.nvEncGetEncodeCaps=caps;
 table.nvEncGetEncodePresetConfig=preset;table.nvEncGetEncodePresetConfigEx=preset_ex;
 table.nvEncInitializeEncoder=initialize;table.nvEncReconfigureEncoder=reconfigure;
 table.nvEncCreateBitstreamBuffer=create_output;table.nvEncRegisterResource=register_resource;
 table.nvEncMapInputResource=map_resource;table.nvEncRegisterAsyncEvent=register_event;
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
extern "C" int HOOK_CALL HookGetLegacyIdentity(HookTrialIdentity* output){
 if(!output||output->size!=sizeof(HookTrialIdentity)||output->version!=1)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 // The identity layout is shared with the managed reader: codec 2 is H.264, the only codec this hook produces.
 *output={sizeof(HookTrialIdentity),1,2,requested_fps,requested_average,requested_peak,requested_rate_control,requested_preset,requested_vbv_frames};return 0;
}

extern "C" int HOOK_CALL HookGetLegacyBridgeStats(HookBridgeStats* output){
 if(!output||output->size!=sizeof(HookBridgeStats)||output->version!=1)return -1;
 std::lock_guard<std::mutex> lock(mutex);*output=stats;return 0;
}
