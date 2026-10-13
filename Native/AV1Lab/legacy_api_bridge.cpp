#define AV1_BACKEND_BUILD
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
uint32_t requested_codec=1;
bool valid_budget(uint32_t average,uint32_t peak){
 const uint32_t average_limit=requested_codec==2?500000000u:200000000u;
 const uint32_t peak_limit=requested_codec==2?500000000u:230000000u;
 return average>=1000000&&average<=average_limit&&peak>=average&&peak<=peak_limit;
}
GUID codec_guid(){return requested_codec==2?NV_ENC_CODEC_H264_GUID:NV_ENC_CODEC_AV1_GUID;}
Av1BridgeStats stats{sizeof(Av1BridgeStats),1};
struct State {
 uint32_t width=0,height=0;
 std::unordered_map<void*,std::vector<int8_t>> maps;
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
 // codec-specific initializer separately validates the actual H.264/AV1
 // configuration. AV1 does not expose HEVC's constrained-slice cap.
 const auto result=api.nvEncGetEncodeCaps(encoder,codec,&copy,output);
 if(result==NV_ENC_SUCCESS&&output&&(copy.capsToQuery==NV_ENC_CAPS_SUPPORT_10BIT_ENCODE||
    copy.capsToQuery==NV_ENC_CAPS_NUM_MAX_BFRAMES||copy.capsToQuery==NV_ENC_CAPS_SUPPORT_LOOKAHEAD))*output=0;
 // SteamVR initializes at the advertised maximum before choosing the packed
 // stream layout. Bound that allocation to the exact paired decoder trial.
 if(result==NV_ENC_SUCCESS&&output&&copy.capsToQuery==NV_ENC_CAPS_WIDTH_MAX)*output=std::min(*output,requested_codec==2?1024:1536);
 if(result==NV_ENC_SUCCESS&&output&&copy.capsToQuery==NV_ENC_CAPS_HEIGHT_MAX)*output=std::min(*output,requested_codec==2?4096:6144);
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

bool configure(void* encoder,const NV_ENC_INITIALIZE_PARAMS& source,NV_ENC_INITIALIZE_PARAMS& init,NV_ENC_CONFIG& config){
 if(source.version!=old_init||!source.encodeConfig||source.encodeConfig->version!=old_config||
    !source.frameRateDen||uint64_t(source.frameRateNum)!=uint64_t(source.frameRateDen)*requested_fps||
    source.encodeWidth<128||source.encodeHeight<128||source.encodeWidth>(requested_codec==2?1024u:1536u)||source.encodeHeight>(requested_codec==2?4096u:6144u))return false;
 NV_ENC_PRESET_CONFIG baseline{};baseline.version=NV_ENC_PRESET_CONFIG_VER;baseline.presetCfg.version=NV_ENC_CONFIG_VER;
 if(api.nvEncGetEncodePresetConfigEx(encoder,codec_guid(),NV_ENC_PRESET_P2_GUID,
    NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,&baseline)!=NV_ENC_SUCCESS)return false;
 init=source;config=baseline.presetCfg;
 init.version=NV_ENC_INITIALIZE_PARAMS_VER;config.version=NV_ENC_CONFIG_VER;
 init.encodeGUID=codec_guid();init.presetGUID=NV_ENC_PRESET_P2_GUID;
 init.tuningInfo=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;init.enablePTD=1;init.enableMEOnlyMode=0;
 init.encodeConfig=&config;config.profileGUID=requested_codec==2?NV_ENC_H264_PROFILE_HIGH_GUID:NV_ENC_AV1_PROFILE_MAIN_GUID;
 config.frameIntervalP=1;config.gopLength=requested_fps;
 auto& rc=config.rcParams;rc.version=NV_ENC_RC_PARAMS_VER;rc.rateControlMode=NV_ENC_PARAMS_RC_VBR;
 rc.averageBitRate=requested_fixed?requested_average:std::min(source.encodeConfig->rcParams.averageBitRate,requested_average);
 if(rc.averageBitRate<1000000)rc.averageBitRate=requested_average;
 rc.maxBitRate=requested_fixed?requested_peak:std::min(std::max(source.encodeConfig->rcParams.maxBitRate,rc.averageBitRate),requested_peak);
 rc.vbvBufferSize=rc.averageBitRate/requested_fps;rc.vbvInitialDelay=0;
 rc.enableLookahead=0;rc.lookaheadDepth=0;rc.enableAQ=0;rc.enableTemporalAQ=0;
 rc.enableMinQP=0;rc.enableMaxQP=0;rc.enableInitialRCQP=0;
 rc.multiPass=NV_ENC_MULTI_PASS_DISABLED;rc.zeroReorderDelay=1;rc.qpMapMode=NV_ENC_QP_MAP_DELTA;
 if(requested_codec==2){
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
 }else{
  auto& av1=config.encodeCodecConfig.av1Config;
  av1.level=17;av1.tier=NV_ENC_TIER_AV1_1;av1.idrPeriod=requested_fps;
  av1.repeatSeqHdr=1;av1.enableBitstreamPadding=0;av1.pixelBitDepthMinus8=av1.inputPixelBitDepthMinus8=0;
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
 Av1LegacyPicture view{sizeof(Av1LegacyPicture),1};
 if(Av1ReadLegacyPicture(source,3344,&view))return NV_ENC_ERR_INVALID_VERSION;
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
  const unsigned block=requested_codec==2?16:64;
  const unsigned columns=(view.width+block-1)/block,rows=(view.height+block-1)/block;
  auto& map=found->second.maps[view.output];map.resize(columns*rows);
  for(unsigned y=0;y<rows;++y)for(unsigned x=0;x<columns;++x){
   if(requested_codec==2){
    map[y*columns+x]=static_cast<int8_t>(std::clamp(int(view.hevc_delta_map[(y/2)*width+x/2]),-51,51));
   }else{
    int minimum=31;
    for(unsigned yy=y*2;yy<std::min(y*2+2,height);++yy)for(unsigned xx=x*2;xx<std::min(x*2+2,width);++xx)
     minimum=std::min(minimum,int(view.hevc_delta_map[yy*width+xx]));
    map[y*columns+x]=static_cast<int8_t>(std::clamp(minimum*4,-128,124));
   }
  }
  // Retain the timing-marker block at high quality across the larger AV1 SB.
  map[(rows-1)*columns]=requested_codec==2?-51:-128;
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

extern "C" int AV1_CALL Av1CreateLegacyFunctionTable(void* raw,uint32_t bytes,uint32_t average,uint32_t peak){
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

extern "C" int AV1_CALL Av1ConfigureLegacyBudget(uint32_t average,uint32_t peak,uint32_t fixed){
 std::lock_guard<std::mutex> lock(mutex);
 if(!valid_budget(average,peak)||fixed>1)return -1;
 if(!sessions.empty())return -2;
 requested_average=average;requested_peak=peak;requested_fixed=fixed!=0;return 0;
}

extern "C" int AV1_CALL Av1ConfigureLegacyRate(uint32_t fps){
 if(fps!=90&&fps!=120)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 if(!sessions.empty())return -2;
 requested_fps=fps;return 0;
}

extern "C" int AV1_CALL Av1ConfigureLegacyCodec(uint32_t codec){
 if(codec!=1&&codec!=2)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 if(!sessions.empty())return -2;
 requested_codec=codec;return 0;
}
extern "C" int AV1_CALL Av1GetLegacyIdentity(Av1TrialIdentity* output){
 if(!output||output->size!=sizeof(Av1TrialIdentity)||output->version!=1)return -1;
 std::lock_guard<std::mutex> lock(mutex);
 *output={sizeof(Av1TrialIdentity),1,requested_codec,requested_fps,requested_average,requested_peak};return 0;
}

extern "C" int AV1_CALL Av1GetLegacyBridgeStats(Av1BridgeStats* output){
 if(!output||output->size!=sizeof(Av1BridgeStats)||output->version!=1)return -1;
 std::lock_guard<std::mutex> lock(mutex);*output=stats;return 0;
}
