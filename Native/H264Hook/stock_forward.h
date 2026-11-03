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
// The options the driver's own encoder is running with, for the monitor: rate control and buffer as last applied, the preset it was created with
// (0 when the encoder was created before the options were switched on). Returns 0 when no option has been applied.
int StockAppliedOptions(uint32_t* cbr,uint32_t* preset,uint32_t* vbv);
// The multipass mode in hevc-options.txt (0 off, 1 quarter resolution first pass, 2 full resolution first pass): HEVC and H.264 both use it.
int StockMultiPass(void);
// The picture size the driver's encoder was initialized with (0 x 0 before the first initialize).
void StockEncodeSize(uint32_t* width,uint32_t* height);
// The average and peak rate (bits per second) the driver last asked its encoder for (0 before the first request).
void StockRequestedRate(uint32_t* average,uint32_t* peak);
#ifdef __cplusplus
}
#endif
