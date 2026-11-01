#define HOOK_BUILD
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include "nvEncodeAPI11.h"
#include "stock_forward.h"

// Drives StockInitialize/StockReconfigure against a fake driver table: the options file must change what the driver is given
// (a copy) and never the caller's own structures, and with the file off or absent nothing may change.
static NV_ENC_INITIALIZE_PARAMS seen_init{};static NV_ENC_CONFIG seen_config{};static bool seen_has_config=false;static int calls=0;
static NVENCSTATUS NVENCAPI fake_initialize(void*,NV_ENC_INITIALIZE_PARAMS* p){seen_init=*p;seen_has_config=p->encodeConfig!=nullptr;if(p->encodeConfig)seen_config=*p->encodeConfig;calls++;return NV_ENC_SUCCESS;}
static uint32_t seen_reset=0;
static NVENCSTATUS NVENCAPI fake_reconfigure(void*,NV_ENC_RECONFIGURE_PARAMS* p){seen_reset=p->resetEncoder;seen_init=p->reInitEncodeParams;seen_has_config=p->reInitEncodeParams.encodeConfig!=nullptr;if(seen_has_config)seen_config=*p->reInitEncodeParams.encodeConfig;calls++;return NV_ENC_SUCCESS;}
static NVENCSTATUS NVENCAPI nothing(){return NV_ENC_SUCCESS;}

static int failures=0;
#define CHECK(condition) do{if(!(condition)){printf("FAIL line %d: %s\n",__LINE__,#condition);failures++;}}while(0)

static void write_options(const char* text){
 char base[MAX_PATH]{};GetEnvironmentVariableA("LOCALAPPDATA",base,MAX_PATH);
 const std::string directory=std::string(base)+"\\CodecSwapper";CreateDirectoryA(directory.c_str(),nullptr);
 FILE* file=nullptr;fopen_s(&file,(directory+"\\hevc-options.txt").c_str(),"w");if(file){fputs(text,file);fclose(file);}
}

int main(){
 // The real file may belong to a running tray: keep it and put it back.
 char base[MAX_PATH]{};GetEnvironmentVariableA("LOCALAPPDATA",base,MAX_PATH);
 const std::string path=std::string(base)+"\\CodecSwapper\\hevc-options.txt",backup=path+".testbackup";
 MoveFileExA(path.c_str(),backup.c_str(),MOVEFILE_REPLACE_EXISTING);

 NV_ENCODE_API_FUNCTION_LIST list{};list.version=NV_ENCODE_API_FUNCTION_LIST_VER;
 list.nvEncOpenEncodeSessionEx=reinterpret_cast<decltype(list.nvEncOpenEncodeSessionEx)>(nothing);
 list.nvEncInitializeEncoder=fake_initialize;list.nvEncReconfigureEncoder=fake_reconfigure;
 list.nvEncEncodePicture=reinterpret_cast<decltype(list.nvEncEncodePicture)>(nothing);list.nvEncLockBitstream=reinterpret_cast<decltype(list.nvEncLockBitstream)>(nothing);
 list.nvEncRegisterResource=reinterpret_cast<decltype(list.nvEncRegisterResource)>(nothing);list.nvEncMapInputResource=reinterpret_cast<decltype(list.nvEncMapInputResource)>(nothing);
 list.nvEncCreateBitstreamBuffer=reinterpret_cast<decltype(list.nvEncCreateBitstreamBuffer)>(nothing);list.nvEncDestroyEncoder=reinterpret_cast<decltype(list.nvEncDestroyEncoder)>(nothing);
 CHECK(StockSetTable(&list,sizeof(list),nullptr)==0);

 NV_ENC_CONFIG config{};config.rcParams.rateControlMode=NV_ENC_PARAMS_RC_VBR;config.rcParams.averageBitRate=300000000;config.rcParams.maxBitRate=400000000;
 config.rcParams.vbvBufferSize=111;config.rcParams.vbvInitialDelay=222;
 NV_ENC_INITIALIZE_PARAMS init{};init.encodeConfig=&config;init.frameRateNum=90;init.frameRateDen=1;init.presetGUID=NV_ENC_PRESET_P4_GUID;

 // No file: the driver sees exactly what it asked for.
 Sleep(600);CHECK(StockInitialize(nullptr,&init)==0);
 CHECK(seen_init.encodeConfig==&config&&seen_config.rcParams.vbvBufferSize==111&&memcmp(&seen_init.presetGUID,&NV_ENC_PRESET_P4_GUID,sizeof(GUID))==0);

 // Switched on: CBR, P7, 4 frames of buffer, on a copy.
 write_options("enabled=1\nrate_control=cbr\npreset=7\nvbv=4\n");Sleep(600);
 CHECK(StockInitialize(nullptr,&init)==0);
 CHECK(seen_init.encodeConfig!=&config);
 CHECK(seen_config.rcParams.rateControlMode==NV_ENC_PARAMS_RC_CBR&&seen_config.rcParams.maxBitRate==300000000);
 CHECK(seen_config.rcParams.vbvBufferSize==300000000/90*4&&seen_config.rcParams.vbvInitialDelay==0);
 CHECK(memcmp(&seen_init.presetGUID,&NV_ENC_PRESET_P7_GUID,sizeof(GUID))==0);
 CHECK(config.rcParams.rateControlMode==NV_ENC_PARAMS_RC_VBR&&config.rcParams.vbvBufferSize==111&&config.rcParams.maxBitRate==400000000);
 CHECK(memcmp(&init.presetGUID,&NV_ENC_PRESET_P4_GUID,sizeof(GUID))==0);

 // SteamVR's reconfigure carries its own preset every frame: it is replaced by the chosen one, so the encoder does not drift back.
 NV_ENC_RECONFIGURE_PARAMS re{};re.reInitEncodeParams=init;
 CHECK(StockReconfigure(nullptr,&re)==0);
 CHECK(seen_config.rcParams.rateControlMode==NV_ENC_PARAMS_RC_CBR&&memcmp(&seen_init.presetGUID,&NV_ENC_PRESET_P7_GUID,sizeof(GUID))==0);
 CHECK(memcmp(&re.reInitEncodeParams.presetGUID,&NV_ENC_PRESET_P4_GUID,sizeof(GUID))==0);   // the caller's own structure is untouched
 CHECK(re.reInitEncodeParams.encodeConfig==&config&&config.rcParams.rateControlMode==NV_ENC_PARAMS_RC_VBR);

 // Changing only the preset restarts the encoder's rate control once, like a rate-control change does.
 write_options("enabled=1\nrate_control=cbr\npreset=3\nvbv=4\n");Sleep(600);
 CHECK(StockReconfigure(nullptr,&re)==0&&seen_reset==1&&memcmp(&seen_init.presetGUID,&NV_ENC_PRESET_P3_GUID,sizeof(GUID))==0);
 CHECK(StockReconfigure(nullptr,&re)==0&&seen_reset==0);
 write_options("enabled=1\nrate_control=cbr\npreset=7\nvbv=4\n");Sleep(600);
 CHECK(StockReconfigure(nullptr,&re)==0&&seen_reset==1);

 // The monitor reports what the encoder was given: CBR and 4 frames now, the preset now in force (P7).
 { uint32_t cbr=9,preset=9,vbv=9;CHECK(StockAppliedOptions(&cbr,&preset,&vbv)==1&&cbr==1&&preset==7&&vbv==4); }

 // VBR keeps the driver's own peak.
 write_options("enabled=1\nrate_control=vbr\npreset=2\nvbv=1\n");Sleep(600);
 CHECK(StockInitialize(nullptr,&init)==0);
 CHECK(seen_config.rcParams.rateControlMode==NV_ENC_PARAMS_RC_VBR&&seen_config.rcParams.maxBitRate==400000000&&seen_config.rcParams.vbvBufferSize==300000000/90);

 // Switched off again: untouched.
 write_options("enabled=0\nrate_control=cbr\n");Sleep(600);
 CHECK(StockInitialize(nullptr,&init)==0);
 CHECK(seen_init.encodeConfig==&config&&seen_config.rcParams.rateControlMode==NV_ENC_PARAMS_RC_VBR&&seen_config.rcParams.vbvBufferSize==111);

 // Without an encode configuration there is nothing to adjust.
 write_options("enabled=1\nrate_control=cbr\n");Sleep(600);
 NV_ENC_INITIALIZE_PARAMS bare{};CHECK(StockInitialize(nullptr,&bare)==0&&!seen_has_config);

 DeleteFileA(path.c_str());MoveFileExA(backup.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING);
 printf(failures?"stock options test FAILED (%d)\n":"stock options test passed (%d calls)\n",failures?failures:calls);
 return failures?1:0;
}
