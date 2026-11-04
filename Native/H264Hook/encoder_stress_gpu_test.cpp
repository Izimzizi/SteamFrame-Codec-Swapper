#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <atomic>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <deque>
#include <iostream>
#include <mutex>
#include <queue>
#include <random>
#include <stdexcept>
#include <string>
#include <thread>
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
//                               [threads=1] [load=0] [content=1] [dips=1]
//   threads=1  encode on one thread and lock the bitstream on another, with three frames in flight (SteamVR does: its encode and lock calls come from different threads)
//   load=N     N percent of the time another GPU client keeps the 3D engine busy (the compositor and the desktop share the GPU with the encoder)
//   content=1  new picture content for every frame (0: every sixth frame)
//   dips=1     the requests SteamVR makes alternate as the flight recorder showed: a low 30 Mbps request now and then, 350/351 Mbps, VBV of 5.4 to 5.8 million
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

// Another GPU client: big clears and copies on a device of its own for load_percent of every 20 ms.
static void gpu_load(int load_percent){
 ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
 if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)))return;
 D3D11_TEXTURE2D_DESC desc{};desc.Width=desc.Height=6144;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.SampleDesc.Count=1;
 desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_RENDER_TARGET;
 ComPtr<ID3D11Texture2D> a,b;if(FAILED(device->CreateTexture2D(&desc,nullptr,&a))||FAILED(device->CreateTexture2D(&desc,nullptr,&b)))return;
 ComPtr<ID3D11RenderTargetView> view;if(FAILED(device->CreateRenderTargetView(a.Get(),nullptr,&view)))return;
 D3D11_QUERY_DESC query_desc{D3D11_QUERY_EVENT,0};ComPtr<ID3D11Query> query;device->CreateQuery(&query_desc,&query);
 unsigned n=0;
 while(!finished){
  const ULONGLONG begin=GetTickCount64();
  while(GetTickCount64()-begin<ULONGLONG(20*load_percent/100)&&!finished){
   const float color[4]{float(n%7)/7,float(n%5)/5,float(n%3)/3,1};context->ClearRenderTargetView(view.Get(),color);context->CopyResource(b.Get(),a.Get());++n;
   context->End(query.Get());context->Flush();BOOL done=FALSE;while(context->GetData(query.Get(),&done,sizeof(done),0)!=S_OK||!done){if(finished)return;Sleep(0);}
  }
  Sleep(DWORD(20-20*load_percent/100)+1);
 }
}

struct Slot{ComPtr<ID3D11Texture2D> texture;NV_ENC_REGISTERED_PTR registered=nullptr;NV_ENC_OUTPUT_PTR output=nullptr;NV_ENC_INPUT_PTR mapped=nullptr;};

int main(int argc,char** argv){
 const int seconds=argc>1?atoi(argv[1]):120;const unsigned seed=argc>2?unsigned(atoi(argv[2])):1;const int interval_ms=argc>3?atoi(argv[3]):1500;
 const int mp2_every=argc>4?atoi(argv[4]):4;const int mbps=argc>5?atoi(argv[5]):350;
 const uint32_t width=argc>6?uint32_t(atoi(argv[6])):1344,height=argc>7?uint32_t(atoi(argv[7])):5376,fps=argc>8?uint32_t(atoi(argv[8])):90;
 const bool use_threads=argc>9?atoi(argv[9])!=0:true;const int load_percent=argc>10?atoi(argv[10]):0;
 const bool content_every_frame=argc>11?atoi(argv[11])!=0:true;const bool dips=argc>12?atoi(argv[12])!=0:true;
 InitializeCriticalSection(&history_lock);
 char temp[MAX_PATH]{};GetTempPathA(MAX_PATH,temp);
 const std::string folder=std::string(temp)+"encoder-stress-"+std::to_string(GetCurrentProcessId());
 CreateDirectoryA(folder.c_str(),nullptr);CreateDirectoryA((folder+"\\CodecSwapper").c_str(),nullptr);
 SetEnvironmentVariableA("LOCALAPPDATA",folder.c_str());   // the hook reads and writes its files under LOCALAPPDATA: keep the user's own apart
 options_path=folder+"\\CodecSwapper\\hevc-options.txt";
 std::cout<<"seed "<<seed<<", "<<seconds<<" s, a change every "<<interval_ms<<" ms, multipass 2 every "<<mp2_every<<". changes, "<<width<<"x"<<height<<" at "<<fps<<" fps, "<<mbps<<" Mbps, "
  <<(use_threads?"encode and lock on separate threads":"one thread")<<", other GPU load "<<load_percent<<"%, "<<(content_every_frame?"new content every frame":"content every 6th frame")
  <<(dips?", SteamVR-like requests":"")<<"\nhook files: "<<folder<<"\\CodecSwapper\n";
 NV_ENCODE_API_FUNCTION_LIST api{};api.version=NV_ENCODE_API_FUNCTION_LIST_VER;void* encoder=nullptr;
 constexpr int SlotCount=3;Slot slots[SlotCount];
 int frames=0;
 std::mutex lock;std::condition_variable wake;std::queue<int> to_lock,free_slots;std::string worker_error;bool stop_worker=false;std::atomic<int> locked_frames{0};
 std::thread locker,loader;
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
  for(int i=0;i<SlotCount;++i){
   if(FAILED(device->CreateTexture2D(&desc,nullptr,&slots[i].texture)))throw std::runtime_error("Texture failed");
   NV_ENC_REGISTER_RESOURCE resource{};resource.version=NV_ENC_REGISTER_RESOURCE_VER;resource.resourceType=NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
   resource.resourceToRegister=slots[i].texture.Get();resource.width=width;resource.height=height;resource.bufferFormat=NV_ENC_BUFFER_FORMAT_NV12;resource.bufferUsage=NV_ENC_INPUT_IMAGE;
   check(api.nvEncRegisterResource(encoder,&resource),"register");slots[i].registered=resource.registeredResource;
   NV_ENC_CREATE_BITSTREAM_BUFFER buffer{};buffer.version=NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
   check(api.nvEncCreateBitstreamBuffer(encoder,&buffer),"output");slots[i].output=buffer.bitstreamBuffer;
   free_slots.push(i);
  }
  std::vector<uint8_t> pixels(size_t(width)*height*3/2,128);
  auto paint=[&](Slot& slot,unsigned shift){for(size_t i=0;i<size_t(width)*height;++i)pixels[i]=uint8_t(16+((i%width)*3+(i/width)*5+shift*7)%220);
   context->UpdateSubresource(slot.texture.Get(),0,nullptr,pixels.data(),width,0);context->Flush();};
  for(auto& slot:slots)paint(slot,0);

  // Taking a finished frame's bitstream: its own thread when threads=1, as in SteamVR, else straight after the encode call.
  auto finish_frame=[&](int index){
   Slot& slot=slots[index];
   NV_ENC_LOCK_BITSTREAM locked{};locked.version=NV_ENC_LOCK_BITSTREAM_VER;locked.outputBitstream=slot.output;
   check(api.nvEncLockBitstream(encoder,&locked),"lock");
   if(!locked.bitstreamSizeInBytes)throw std::runtime_error("empty bitstream");
   check(api.nvEncUnlockBitstream(encoder,slot.output),"unlock");check(api.nvEncUnmapInputResource(encoder,slot.mapped),"unmap");
   ++locked_frames;last_progress=GetTickCount64();
  };
  if(use_threads){
   locker=std::thread([&]{
    for(;;){
     int index;
     {std::unique_lock<std::mutex> guard(lock);wake.wait(guard,[&]{return stop_worker||!to_lock.empty();});if(to_lock.empty())return;index=to_lock.front();to_lock.pop();}
     try{finish_frame(index);}catch(const std::exception& error){std::lock_guard<std::mutex> guard(lock);worker_error=error.what();wake.notify_all();return;}
     {std::lock_guard<std::mutex> guard(lock);free_slots.push(index);}wake.notify_all();
    }
   });
  }
  if(load_percent>0)loader=std::thread(gpu_load,load_percent);

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
    char line[240];snprintf(line,sizeof(line),"[%6.1f s] change %3d (%d since multipass 2, frame %d): %s -> %s",at,changes,since_mp2,frames,describe(before).c_str(),describe(state).c_str());
    std::cout<<line<<std::endl;remember(line);write_options(state,mbps);
   }
   // A free frame slot: with one thread the previous frame is already finished.
   int index;
   {
    std::unique_lock<std::mutex> guard(lock);
    wake.wait(guard,[&]{return !free_slots.empty()||!worker_error.empty();});
    if(!worker_error.empty())throw std::runtime_error(worker_error);
    index=free_slots.front();free_slots.pop();
   }
   Slot& slot=slots[index];
   // SteamVR reconfigures before every frame; most of its requests carry the full rate (350 and 351 Mbps alternate), some a low one, with a VBV of its own.
   NV_ENC_CONFIG request=config;
   if(dips){
    request.rcParams.averageBitRate=(frames/200)%2?351000000u:350000000u;request.rcParams.maxBitRate=uint32_t(double(request.rcParams.averageBitRate)*1.15);
    request.rcParams.vbvBufferSize=5400000u+uint32_t(random()%400000u);
    if(frames%11==5){request.rcParams.averageBitRate=30000000;request.rcParams.maxBitRate=80000000;request.rcParams.vbvBufferSize=19501;}
   }else if(frames%11==5){request.rcParams.averageBitRate=30000000;request.rcParams.maxBitRate=80000000;}
   NV_ENC_RECONFIGURE_PARAMS change{};change.version=NV_ENC_RECONFIGURE_PARAMS_VER;change.reInitEncodeParams=init;change.reInitEncodeParams.encodeConfig=&request;
   check(api.nvEncReconfigureEncoder(encoder,&change),"reconfigure");
   if(content_every_frame||frames%6==0)paint(slot,unsigned(frames));
   NV_ENC_MAP_INPUT_RESOURCE mapped{};mapped.version=NV_ENC_MAP_INPUT_RESOURCE_VER;mapped.registeredResource=slot.registered;
   check(api.nvEncMapInputResource(encoder,&mapped),"map");slot.mapped=mapped.mappedResource;
   NV_ENC_PIC_PARAMS picture{};picture.version=NV_ENC_PIC_PARAMS_VER;picture.inputWidth=width;picture.inputHeight=height;picture.inputBuffer=mapped.mappedResource;picture.bufferFmt=mapped.mappedBufferFmt;
   picture.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;picture.outputBitstream=slot.output;picture.frameIdx=uint32_t(frames);picture.inputTimeStamp=uint64_t(frames)*1000000/fps;
   if(!frames)picture.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR|NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
   check(api.nvEncEncodePicture(encoder,&picture),"encode");
   ++frames;
   if(use_threads){{std::lock_guard<std::mutex> guard(lock);to_lock.push(index);}wake.notify_all();}
   else{finish_frame(index);std::lock_guard<std::mutex> guard(lock);free_slots.push(index);}
  }
  finished=true;
  {std::lock_guard<std::mutex> guard(lock);stop_worker=true;}wake.notify_all();
  if(locker.joinable())locker.join();
  if(loader.joinable())loader.join();
  NV_ENC_PIC_PARAMS eos{};eos.version=NV_ENC_PIC_PARAMS_VER;eos.encodePicFlags=NV_ENC_PIC_FLAG_EOS;api.nvEncEncodePicture(encoder,&eos);
  for(auto& slot:slots){api.nvEncDestroyBitstreamBuffer(encoder,slot.output);api.nvEncUnregisterResource(encoder,slot.registered);}
  api.nvEncDestroyEncoder(encoder);
  std::cout<<"{\"survived\":true,\"changes\":"<<changes<<",\"frames\":"<<frames<<",\"locked\":"<<locked_frames.load()<<",\"seed\":"<<seed<<"}\n";
  return 0;
 }catch(const std::exception& error){
  finished=true;
  std::cerr<<"FAIL after change #"<<change_number.load()<<", frame "<<frames<<": "<<error.what()<<"\nRecent changes:\n";print_history();
  std::cerr<<"The hook's flight recorder is in "<<folder<<"\\CodecSwapper\\flight.log\n";std::cerr.flush();
  TerminateProcess(GetCurrentProcess(),2);   // a hung driver may not let threads or the encoder be torn down
  return 2;
 }
}
