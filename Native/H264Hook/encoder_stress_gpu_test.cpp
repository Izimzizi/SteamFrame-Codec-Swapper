#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <deque>
#include <iostream>
#include <random>
#include <stdexcept>
#include <string>
#include <vector>
#include "nvEncodeAPI11.h"
#include "legacy_api_bridge.h"
using Microsoft::WRL::ComPtr;

// Debug test for the GPU error (nvlddmkm 153) that follows live encoder changes: the driver's own HEVC encoder, run through the hook's stock path at the picture
// SteamVR streams, reconfigured before every frame as SteamVR does, while the options (rate control, preset, VBV, multipass) are changed at random through
// the same options file the tray writes. Every few changes multipass goes to 2 and back, the pattern that hung the encoder. The run stops at the first failed
// call or a frame that does not come back, and says which change preceded it. The hook's own files (flight.log, hevc-hook.log) go to a temporary folder.
//
//   encoder_stress_gpu_test.exe [seconds=120] [seed=1] [interval_ms=1500] [multipass2_every=4] [mbps=350] [width=1344] [height=5376] [fps=90]
// Exit codes: 0 all changes survived, 2 a call failed, 3 a frame did not come back.
static void check(NVENCSTATUS status,const char* action){if(status!=NV_ENC_SUCCESS)throw std::runtime_error(std::string(action)+": "+std::to_string(status));}

struct Setting{bool cbr=false;int preset=2,vbv=1,multipass=0;};
static std::string describe(const Setting& s){return std::string(s.cbr?"cbr":"vbr")+" P"+std::to_string(s.preset)+" vbv "+std::to_string(s.vbv)+" multipass "+std::to_string(s.multipass);}

static std::string options_path;
static void write_options(const Setting& s,int mbps){
 const std::string temporary=options_path+".tmp";
 FILE* file=nullptr;if(fopen_s(&file,temporary.c_str(),"w")||!file)return;
 fprintf(file,"enabled=1\nrate_control=%s\npreset=%d\nvbv=%d\nmultipass=%d\ntarget=%d\n",s.cbr?"cbr":"vbr",s.preset,s.vbv,s.multipass,mbps);fclose(file);
 MoveFileExA(temporary.c_str(),options_path.c_str(),MOVEFILE_REPLACE_EXISTING);
}

static std::atomic<ULONGLONG> last_progress{0};
static std::atomic<int> change_number{0};
static std::atomic<bool> finished{false};
static std::string current_text;
static std::deque<std::string> history;
static CRITICAL_SECTION history_lock;
static void remember(const std::string& line){EnterCriticalSection(&history_lock);history.push_back(line);if(history.size()>40)history.pop_front();LeaveCriticalSection(&history_lock);}
static void print_history(){EnterCriticalSection(&history_lock);for(const auto& line:history)std::cerr<<"  "<<line<<"\n";LeaveCriticalSection(&history_lock);}

static DWORD WINAPI watchdog(void*){
 while(!finished){
  Sleep(500);
  const ULONGLONG idle=GetTickCount64()-last_progress;
  if(last_progress&&idle>8000&&!finished){
   std::cerr<<"HANG: no frame came back for "<<idle/1000<<" s after change #"<<change_number.load()<<"\nRecent changes:\n";print_history();
   std::cerr.flush();TerminateProcess(GetCurrentProcess(),3);
  }
 }
 return 0;
}

int main(int argc,char** argv){
 const int seconds=argc>1?atoi(argv[1]):120;const unsigned seed=argc>2?unsigned(atoi(argv[2])):1;const int interval_ms=argc>3?atoi(argv[3]):1500;
 const int mp2_every=argc>4?atoi(argv[4]):4;const int mbps=argc>5?atoi(argv[5]):350;
 const uint32_t width=argc>6?uint32_t(atoi(argv[6])):1344,height=argc>7?uint32_t(atoi(argv[7])):5376,fps=argc>8?uint32_t(atoi(argv[8])):90;
 InitializeCriticalSection(&history_lock);
 char temp[MAX_PATH]{};GetTempPathA(MAX_PATH,temp);
 const std::string folder=std::string(temp)+"encoder-stress-"+std::to_string(GetCurrentProcessId());
 CreateDirectoryA(folder.c_str(),nullptr);CreateDirectoryA((folder+"\\CodecSwapper").c_str(),nullptr);
 SetEnvironmentVariableA("LOCALAPPDATA",folder.c_str());   // the hook reads and writes its files under LOCALAPPDATA: keep the user's own apart
 options_path=folder+"\\CodecSwapper\\hevc-options.txt";
 std::cout<<"seed "<<seed<<", "<<seconds<<" s, a change every "<<interval_ms<<" ms, multipass 2 every "<<mp2_every<<". changes, "<<width<<"x"<<height<<" at "<<fps<<" fps, "<<mbps<<" Mbps\nhook files: "<<folder<<"\\CodecSwapper\n";
 NV_ENCODE_API_FUNCTION_LIST api{};api.version=NV_ENCODE_API_FUNCTION_LIST_VER;void* encoder=nullptr;
 NV_ENC_OUTPUT_PTR output=nullptr;NV_ENC_REGISTERED_PTR registered=nullptr;
 int frames=0;
 try{
  HMODULE module=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);if(!module)throw std::runtime_error("nvEncodeAPI64.dll missing");
  using Create=NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
  auto create=reinterpret_cast<Create>(GetProcAddress(module,"NvEncodeAPICreateInstance"));
  NV_ENCODE_API_FUNCTION_LIST stock{};stock.version=NV_ENCODE_API_FUNCTION_LIST_VER;
  if(!create||create(&stock)!=NV_ENC_SUCCESS)throw std::runtime_error("stock table failed");
  if(HookCreateLegacyFunctionTable(&api,sizeof(api),100000000,115000000))throw std::runtime_error("Legacy table creation failed");
  stock={};stock.version=NV_ENCODE_API_FUNCTION_LIST_VER;if(create(&stock)!=NV_ENC_SUCCESS)throw std::runtime_error("stock table failed");
  if(HookProvideStockTable(&stock,sizeof(stock),reinterpret_cast<void*>(create)))throw std::runtime_error("Stock table rejected");
  if(HookConfigureLegacyRate(fps)||HookConfigureLegacyBudget(100000000,115000000,0))throw std::runtime_error("Configuration failed");
  ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
  if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)))throw std::runtime_error("D3D device failed");
  NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};open.version=NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
  open.device=device.Get();open.deviceType=NV_ENC_DEVICE_TYPE_DIRECTX;open.apiVersion=NVENCAPI_VERSION;
  check(api.nvEncOpenEncodeSessionEx(&open,&encoder),"open");
  if(HookSetLegacyMode(1))throw std::runtime_error("Stock mode refused");

  Setting state;write_options(state,mbps);Sleep(700);   // the hook reads the file about twice a second
  NV_ENC_PRESET_CONFIG preset{};preset.version=NV_ENC_PRESET_CONFIG_VER;preset.presetCfg.version=NV_ENC_CONFIG_VER;
  check(api.nvEncGetEncodePresetConfigEx(encoder,NV_ENC_CODEC_HEVC_GUID,NV_ENC_PRESET_P2_GUID,NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,&preset),"preset");
  NV_ENC_CONFIG config=preset.presetCfg;config.rcParams.averageBitRate=uint32_t(mbps)*1000000u;config.rcParams.maxBitRate=uint32_t(double(config.rcParams.averageBitRate)*1.15);
  NV_ENC_INITIALIZE_PARAMS init{};init.version=NV_ENC_INITIALIZE_PARAMS_VER;init.encodeGUID=NV_ENC_CODEC_HEVC_GUID;init.presetGUID=NV_ENC_PRESET_P2_GUID;
  init.encodeWidth=init.maxEncodeWidth=init.darWidth=width;init.encodeHeight=init.maxEncodeHeight=init.darHeight=height;
  init.frameRateNum=fps;init.frameRateDen=1;init.enablePTD=1;init.encodeConfig=&config;init.tuningInfo=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
  check(api.nvEncInitializeEncoder(encoder,&init),"initialize");

  D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_NV12;
  desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> texture;if(FAILED(device->CreateTexture2D(&desc,nullptr,&texture)))throw std::runtime_error("Texture failed");
  std::vector<uint8_t> pixels(size_t(width)*height*3/2,128);
  auto paint=[&](unsigned shift){for(size_t i=0;i<size_t(width)*height;++i)pixels[i]=uint8_t(16+((i%width)*3+(i/width)*5+shift*7)%220);
   context->UpdateSubresource(texture.Get(),0,nullptr,pixels.data(),width,0);context->Flush();};
  paint(0);
  NV_ENC_REGISTER_RESOURCE resource{};resource.version=NV_ENC_REGISTER_RESOURCE_VER;resource.resourceType=NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
  resource.resourceToRegister=texture.Get();resource.width=width;resource.height=height;resource.bufferFormat=NV_ENC_BUFFER_FORMAT_NV12;resource.bufferUsage=NV_ENC_INPUT_IMAGE;
  check(api.nvEncRegisterResource(encoder,&resource),"register");registered=resource.registeredResource;
  NV_ENC_CREATE_BITSTREAM_BUFFER buffer{};buffer.version=NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
  check(api.nvEncCreateBitstreamBuffer(encoder,&buffer),"output");output=buffer.bitstreamBuffer;

  std::mt19937 random(seed);
  auto pick=[&](int low,int high){return int(random()%unsigned(high-low+1))+low;};
  LARGE_INTEGER frequency{},start{},now{};QueryPerformanceFrequency(&frequency);QueryPerformanceCounter(&start);
  auto seconds_since=[&](){QueryPerformanceCounter(&now);return double(now.QuadPart-start.QuadPart)/double(frequency.QuadPart);};
  const double frame_time=1.0/fps;double next_frame=0,next_change=double(interval_ms)/1000.0;
  last_progress=GetTickCount64();CreateThread(nullptr,0,watchdog,nullptr,0,nullptr);
  int changes=0,since_mp2=0;
  while(seconds_since()<seconds){
   const double at=seconds_since();
   if(at<next_frame){Sleep(0);continue;}
   next_frame+=frame_time;if(next_frame<at-0.25)next_frame=at;   // do not try to catch up on a long frame
   if(at>=next_change){
    next_change+=double(interval_ms)/1000.0;
    ++changes;++change_number;const Setting before=state;
    if(mp2_every>0&&changes%mp2_every==0){state.multipass=state.multipass==2?pick(0,1):2;since_mp2=0;}
    else{
     ++since_mp2;
     switch(pick(0,2)){
      case 0:{int value;do value=pick(1,7);while(value==state.preset);state.preset=value;break;}
      case 1:state.cbr=!state.cbr;break;
      default:{int value;do value=pick(1,8);while(value==state.vbv);state.vbv=value;}
     }
    }
    char line[220];snprintf(line,sizeof(line),"[%6.1f s] change %3d (%d since multipass 2, frame %d): %s -> %s",at,changes,since_mp2,frames,describe(before).c_str(),describe(state).c_str());
    std::cout<<line<<"\n";remember(line);write_options(state,mbps);
   }
   // SteamVR reconfigures before every frame; most of its requests carry the full rate, some a low one.
   NV_ENC_CONFIG request=config;if(frames%11==5){request.rcParams.averageBitRate=30000000;request.rcParams.maxBitRate=80000000;}
   NV_ENC_RECONFIGURE_PARAMS change{};change.version=NV_ENC_RECONFIGURE_PARAMS_VER;change.reInitEncodeParams=init;change.reInitEncodeParams.encodeConfig=&request;
   check(api.nvEncReconfigureEncoder(encoder,&change),"reconfigure");
   if(frames%6==0)paint(unsigned(frames/6));
   NV_ENC_MAP_INPUT_RESOURCE mapped{};mapped.version=NV_ENC_MAP_INPUT_RESOURCE_VER;mapped.registeredResource=registered;
   check(api.nvEncMapInputResource(encoder,&mapped),"map");
   NV_ENC_PIC_PARAMS picture{};picture.version=NV_ENC_PIC_PARAMS_VER;picture.inputWidth=width;picture.inputHeight=height;picture.inputBuffer=mapped.mappedResource;picture.bufferFmt=mapped.mappedBufferFmt;
   picture.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;picture.outputBitstream=output;picture.frameIdx=uint32_t(frames);picture.inputTimeStamp=uint64_t(frames)*1000000/fps;
   if(!frames)picture.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR|NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
   check(api.nvEncEncodePicture(encoder,&picture),"encode");
   NV_ENC_LOCK_BITSTREAM locked{};locked.version=NV_ENC_LOCK_BITSTREAM_VER;locked.outputBitstream=output;
   check(api.nvEncLockBitstream(encoder,&locked),"lock");
   if(!locked.bitstreamSizeInBytes)throw std::runtime_error("empty bitstream");
   check(api.nvEncUnlockBitstream(encoder,output),"unlock");check(api.nvEncUnmapInputResource(encoder,mapped.mappedResource),"unmap");
   ++frames;last_progress=GetTickCount64();
  }
  finished=true;
  NV_ENC_PIC_PARAMS eos{};eos.version=NV_ENC_PIC_PARAMS_VER;eos.encodePicFlags=NV_ENC_PIC_FLAG_EOS;api.nvEncEncodePicture(encoder,&eos);
  api.nvEncDestroyBitstreamBuffer(encoder,output);api.nvEncUnregisterResource(encoder,registered);api.nvEncDestroyEncoder(encoder);
  std::cout<<"{\"survived\":true,\"changes\":"<<changes<<",\"frames\":"<<frames<<",\"seed\":"<<seed<<"}\n";
  return 0;
 }catch(const std::exception& error){
  finished=true;
  std::cerr<<"FAIL after change #"<<change_number.load()<<", frame "<<frames<<": "<<error.what()<<"\nRecent changes:\n";print_history();
  std::cerr<<"The hook's flight recorder is in "<<folder<<"\\CodecSwapper\\flight.log\n";
  return 2;
 }
}
