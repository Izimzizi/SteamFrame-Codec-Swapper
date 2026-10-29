#pragma once
#include <stdint.h>
// Forwards calls to the driver's own SDK 11.1 encoder API (the stock HEVC path SteamVR asked for). Plain C types only so
// the SDK 12.1 bridge can use it without seeing the SDK 11.1 headers. Every function returns an NVENCSTATUS (0 = success).
#ifdef __cplusplus
extern "C" {
#endif
int StockSetTable(const void* sdk11_table,uint32_t bytes,void* factory);
int StockReady(void);
int StockOpen(const void* open_params,void** encoder);
int StockInitialize(void* encoder,void* init_params);
int StockReconfigure(void* encoder,void* reconfigure_params);
int StockRegister(void* encoder,const void* register_params,void** resource);
int StockUnregister(void* encoder,void* resource);
int StockMap(void* encoder,const void* map_params,void* resource,void** mapped,uint32_t* format);
int StockUnmap(void* encoder,void* mapped);
int StockCreateOutput(void* encoder,const void* create_params,void** output);
int StockDestroyOutput(void* encoder,void* output);
int StockEncode(void* encoder,const void* picture,void* input,void* output,uint32_t extra_flags);
int StockLock(void* encoder,void* lock_params,void* output);
int StockUnlock(void* encoder,void* output);
int StockEvent(void* encoder,const void* event_params,int registration);
int StockDestroy(void* encoder);
#ifdef __cplusplus
}
#endif
