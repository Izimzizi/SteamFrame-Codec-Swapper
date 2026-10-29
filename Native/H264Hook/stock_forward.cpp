#define HOOK_BUILD
#include <windows.h>
#include <cstring>
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
int StockInitialize(void* encoder,void* init){return ready?status(table.nvEncInitializeEncoder(encoder,static_cast<NV_ENC_INITIALIZE_PARAMS*>(init))):NV_ENC_ERR_INVALID_PTR;}
int StockReconfigure(void* encoder,void* params){return ready?status(table.nvEncReconfigureEncoder(encoder,static_cast<NV_ENC_RECONFIGURE_PARAMS*>(params))):NV_ENC_ERR_INVALID_PTR;}
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
int StockDestroy(void* encoder){return ready?status(table.nvEncDestroyEncoder(encoder)):NV_ENC_ERR_INVALID_PTR;}
}
