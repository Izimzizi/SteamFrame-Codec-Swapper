#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <mmsystem.h>
#include <avrt.h>
#include <algorithm>
#include <chrono>
#include <fstream>
#include <iostream>
#include <memory>
#include <string>
#include <thread>
#include <vector>
#include <deque>
#include <mutex>
#include <condition_variable>
#include "nvEncodeAPI12.h"
#include "../../Frame/Multistream/protocol.h"
#include "../../Frame/Multistream/datagrams.h"
using Microsoft::WRL::ComPtr;
using Clock=std::chrono::steady_clock;
struct MultimediaThread {
 HANDLE task=nullptr;
 MultimediaThread(){DWORD index=0;task=AvSetMmThreadCharacteristicsW(L"Games",&index);
  if(task)AvSetMmThreadPriority(task,AVRT_PRIORITY_HIGH);
  else std::cerr<<"MMCSS Games registration unavailable: "<<GetLastError()<<'\n';}
 ~MultimediaThread(){if(task)AvRevertMmThreadCharacteristics(task);}
};
void check(NVENCSTATUS result,const char* name){if(result)throw std::runtime_error(std::string(name)+": "+std::to_string(result));}
void ok(HRESULT result){if(FAILED(result))throw std::runtime_error("D3D11 operation failed");}
#include "game_capture.h"
struct Connection {
 SOCKET socket=INVALID_SOCKET,udp=INVALID_SOCKET;bool segmented=false;
 Connection(){WSADATA data{};if(WSAStartup(MAKEWORD(2,2),&data))throw std::runtime_error("Winsock unavailable");}
 void connect(bool datagrams){
  socket=::socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);if(socket==INVALID_SOCKET)throw std::runtime_error("Socket failed");
  sockaddr_in local{};local.sin_family=AF_INET;inet_pton(AF_INET,"10.35.78.21",&local.sin_addr);
  if(bind(socket,reinterpret_cast<sockaddr*>(&local),sizeof(local)))throw std::runtime_error("Paired dongle source unavailable");
  int yes=1,size=4*1024*1024;DWORD timeout=5000;
  setsockopt(socket,IPPROTO_TCP,TCP_NODELAY,reinterpret_cast<char*>(&yes),sizeof(yes));
  setsockopt(socket,SOL_SOCKET,SO_SNDBUF,reinterpret_cast<char*>(&size),sizeof(size));
  setsockopt(socket,SOL_SOCKET,SO_SNDTIMEO,reinterpret_cast<char*>(&timeout),sizeof(timeout));
  setsockopt(socket,SOL_SOCKET,SO_RCVTIMEO,reinterpret_cast<char*>(&timeout),sizeof(timeout));
  sockaddr_in peer{};peer.sin_family=AF_INET;peer.sin_port=htons(split::port);inet_pton(AF_INET,"10.35.78.1",&peer.sin_addr);
  if(::connect(socket,reinterpret_cast<sockaddr*>(&peer),sizeof(peer)))throw std::runtime_error("Prototype receiver unavailable");
  if(datagrams){udp=::socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);if(udp==INVALID_SOCKET)throw std::runtime_error("Datagram socket failed");
   local.sin_port=htons(split::udp_port);peer.sin_port=htons(split::udp_port);
   if(bind(udp,reinterpret_cast<sockaddr*>(&local),sizeof(local))||::connect(udp,reinterpret_cast<sockaddr*>(&peer),sizeof(peer)))throw std::runtime_error("Paired datagram path unavailable");
   setsockopt(udp,SOL_SOCKET,SO_SNDBUF,reinterpret_cast<char*>(&size),sizeof(size));
   setsockopt(udp,SOL_SOCKET,SO_SNDTIMEO,reinterpret_cast<char*>(&timeout),sizeof(timeout));
   segmented=WSASetUdpSendMessageSize(udp,sizeof(split::Datagram)+split::fragment_size)==0;}
 }
 void write(const void* source,size_t bytes){
  auto* next=static_cast<const char*>(source);
  while(bytes){int count=send(socket,next,int(std::min(bytes,size_t(65536))),0);
   if(count<=0)throw std::runtime_error("Bounded dongle write failed");next+=count;bytes-=count;}
 }
 void ready(){char value=0;if(recv(socket,&value,1,MSG_WAITALL)!=1||value!='R')throw std::runtime_error("Decoder readiness barrier failed");}
 void packet(const std::vector<uint8_t>& data){
  if(udp==INVALID_SOCKET){write(data.data(),data.size());return;}
  split::Group group;memcpy(&group,data.data(),sizeof(group));const auto began=Clock::now();size_t wire_bytes=0;double maximum_call_ms=0;
  const size_t count=(data.size()+split::fragment_size-1)/split::fragment_size;
  const size_t expected=(count+(count+split::data_fragments-1)/split::data_fragments)*(sizeof(split::Datagram)+split::fragment_size);
  std::vector<uint8_t> batch;batch.reserve(32*(sizeof(split::Datagram)+split::fragment_size));
  auto flush=[&]{if(batch.empty())return;
   // Spread each frame across most of the 11.1 ms period. Sending it as an
   // 8 ms burst overran the link during larger Home frames.
   const auto due=began+std::chrono::nanoseconds(wire_bytes*10500000/expected);
   while(Clock::now()<due)YieldProcessor();
   const auto call_began=Clock::now();
   if(send(udp,reinterpret_cast<const char*>(batch.data()),int(batch.size()),0)!=int(batch.size()))throw std::runtime_error("Datagram send failed: "+std::to_string(WSAGetLastError()));
   maximum_call_ms=std::max(maximum_call_ms,std::chrono::duration<double,std::milli>(Clock::now()-call_began).count());
   wire_bytes+=batch.size();batch.clear();};
  split::fragment(group.id,data,[&](const void* packet,size_t length){
   auto* bytes=static_cast<const uint8_t*>(packet);batch.insert(batch.end(),bytes,bytes+length);
   if(!segmented||batch.size()>=32*(sizeof(split::Datagram)+split::fragment_size))flush();
  });flush();
  const double elapsed=std::chrono::duration<double,std::milli>(Clock::now()-began).count();
  if(group.id<3||elapsed>20)std::cerr<<"{\"udp_frame\":"<<group.id<<",\"bytes\":"<<data.size()<<",\"wire_bytes\":"<<wire_bytes<<",\"segmented\":"<<(segmented?"true":"false")<<",\"send_ms\":"<<elapsed<<",\"maximum_send_call_ms\":"<<maximum_call_ms<<"}\n";
 }
 ~Connection(){if(udp!=INVALID_SOCKET)closesocket(udp);if(socket!=INVALID_SOCKET){shutdown(socket,SD_SEND);closesocket(socket);}WSACleanup();}
};
// Keep the socket off the render thread. The queue is bounded, and never
// drops predictive access units: an overloaded link fails instead of silently
// corrupting the reference chain or building seconds of latency.
class SendQueue {
 Connection& connection_;std::mutex mutex_;std::condition_variable cv_;
 std::deque<std::vector<uint8_t>> queue_;bool closing_=false,finished_=false;
 std::exception_ptr error_;std::thread worker_;
public:
 std::vector<double> durations;
 explicit SendQueue(Connection& connection):connection_(connection),worker_([this]{
  MultimediaThread scheduling;
  try{while(true){std::vector<uint8_t> packet;
   {std::unique_lock<std::mutex> lock(mutex_);cv_.wait(lock,[&]{return closing_||!queue_.empty();});
    if(queue_.empty())break;packet=std::move(queue_.front());queue_.pop_front();cv_.notify_all();}
   auto start=Clock::now();connection_.packet(packet);
   durations.push_back(std::chrono::duration<double,std::milli>(Clock::now()-start).count());
  }}catch(...){std::lock_guard<std::mutex> lock(mutex_);error_=std::current_exception();closing_=true;cv_.notify_all();}
 }){}
 void push(std::vector<uint8_t> packet){
  std::unique_lock<std::mutex> lock(mutex_);
  if(!cv_.wait_for(lock,std::chrono::milliseconds(50),[&]{return queue_.size()<4||closing_;}))
   throw std::runtime_error("Network cannot sustain the bounded game stream (queued="+std::to_string(queue_.size())+")");
  if(error_)std::rethrow_exception(error_);
  if(closing_)throw std::runtime_error("Network sender stopped");
  queue_.push_back(std::move(packet));cv_.notify_all();
 }
 void finish(){
  {std::lock_guard<std::mutex> lock(mutex_);closing_=true;cv_.notify_all();}
  worker_.join();finished_=true;if(error_)std::rethrow_exception(error_);
 }
 ~SendQueue(){if(!finished_){
  {std::lock_guard<std::mutex> lock(mutex_);closing_=true;queue_.clear();cv_.notify_all();}
  shutdown(connection_.socket,SD_BOTH);if(worker_.joinable())worker_.join();
  if(!durations.empty())std::cerr<<"{\"sent_after_bootstrap\":"<<durations.size()<<",\"maximum_send_ms\":"<<*std::max_element(durations.begin(),durations.end())<<"}\n";
 }}
};
struct Encoder {
 NV_ENCODE_API_FUNCTION_LIST* api;void* session=nullptr;NV_ENC_OUTPUT_PTR output=nullptr;
 NV_ENC_REGISTERED_PTR registered=nullptr;NV_ENC_INPUT_PTR mapped=nullptr;
 HANDLE event=nullptr;bool event_registered=false;
 ComPtr<ID3D11Texture2D> texture;ComPtr<ID3D11RenderTargetView> target;
 NV_ENC_LOCK_BITSTREAM locked{};
 std::vector<int8_t> quality;
 explicit Encoder(NV_ENCODE_API_FUNCTION_LIST& table):api(&table){}
 void initialize(ID3D11Device* device,const split::Session& config,unsigned mbps,bool cabac){
  NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};open.version=NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
  open.device=device;open.deviceType=NV_ENC_DEVICE_TYPE_DIRECTX;open.apiVersion=NVENCAPI_VERSION;
  check(api->nvEncOpenEncodeSessionEx(&open,&session),"open H.264 encoder");
  NV_ENC_PRESET_CONFIG preset{};preset.version=NV_ENC_PRESET_CONFIG_VER;preset.presetCfg.version=NV_ENC_CONFIG_VER;
  check(api->nvEncGetEncodePresetConfigEx(session,NV_ENC_CODEC_H264_GUID,NV_ENC_PRESET_P2_GUID,NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,&preset),"P2 ULL preset");
  auto c=preset.presetCfg;c.profileGUID=NV_ENC_H264_PROFILE_HIGH_GUID;c.gopLength=config.fps;c.frameIntervalP=1;
  auto& rc=c.rcParams;rc.rateControlMode=NV_ENC_PARAMS_RC_CBR;rc.averageBitRate=mbps*1000000/config.streams;rc.maxBitRate=rc.averageBitRate;
  rc.vbvBufferSize=rc.averageBitRate/config.fps;rc.vbvInitialDelay=0;rc.enableLookahead=0;rc.lookaheadDepth=0;
  rc.enableAQ=0;rc.enableTemporalAQ=0;rc.zeroReorderDelay=1;rc.multiPass=NV_ENC_MULTI_PASS_DISABLED;rc.qpMapMode=config.game()?NV_ENC_QP_MAP_DELTA:NV_ENC_QP_MAP_DISABLED;
  auto& h=c.encodeCodecConfig.h264Config;h.idrPeriod=config.fps;h.repeatSPSPPS=1;h.outputAUD=1;h.maxNumRefFrames=3;
  h.h264VUIParameters.bitstreamRestrictionFlag=1;h.h264VUIParameters.videoSignalTypePresentFlag=1;
  h.h264VUIParameters.colourDescriptionPresentFlag=1;h.h264VUIParameters.colourPrimaries=NV_ENC_VUI_COLOR_PRIMARIES_BT709;
  h.h264VUIParameters.transferCharacteristics=NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;h.h264VUIParameters.colourMatrix=NV_ENC_VUI_MATRIX_COEFFS_BT709;
  h.h264VUIParameters.videoFullRangeFlag=0;
  h.entropyCodingMode=cabac?NV_ENC_H264_ENTROPY_CODING_MODE_CABAC:NV_ENC_H264_ENTROPY_CODING_MODE_CAVLC;
  h.enableFillerDataInsertion=0;h.sliceMode=3;h.sliceModeData=1;
  NV_ENC_INITIALIZE_PARAMS init{};init.version=NV_ENC_INITIALIZE_PARAMS_VER;init.encodeGUID=NV_ENC_CODEC_H264_GUID;
  init.presetGUID=NV_ENC_PRESET_P2_GUID;init.encodeWidth=init.maxEncodeWidth=init.darWidth=config.width;
  init.encodeHeight=init.maxEncodeHeight=init.darHeight=config.height;init.frameRateNum=config.fps;init.frameRateDen=1;
  init.enablePTD=1;init.enableEncodeAsync=1;init.encodeConfig=&c;init.tuningInfo=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
  check(api->nvEncInitializeEncoder(session,&init),"initialize H.264 encoder");
  D3D11_TEXTURE2D_DESC desc{};desc.Width=config.width;desc.Height=config.height;desc.MipLevels=desc.ArraySize=1;
  desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_RENDER_TARGET;
  ok(device->CreateTexture2D(&desc,nullptr,&texture));ok(device->CreateRenderTargetView(texture.Get(),nullptr,&target));
  NV_ENC_REGISTER_RESOURCE resource{};resource.version=NV_ENC_REGISTER_RESOURCE_VER;resource.resourceType=NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
  resource.resourceToRegister=texture.Get();resource.width=config.width;resource.height=config.height;
  resource.bufferFormat=NV_ENC_BUFFER_FORMAT_ARGB;resource.bufferUsage=NV_ENC_INPUT_IMAGE;
  check(api->nvEncRegisterResource(session,&resource),"register texture");registered=resource.registeredResource;
  NV_ENC_CREATE_BITSTREAM_BUFFER buffer{};buffer.version=NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
  check(api->nvEncCreateBitstreamBuffer(session,&buffer),"bitstream buffer");output=buffer.bitstreamBuffer;
  event=CreateEventW(nullptr,FALSE,FALSE,nullptr);if(!event)throw std::runtime_error("Completion event failed");
  NV_ENC_EVENT_PARAMS registration{};registration.version=NV_ENC_EVENT_PARAMS_VER;registration.completionEvent=event;
  check(api->nvEncRegisterAsyncEvent(session,&registration),"completion event");event_registered=true;
 }
 void submit(const split::Session& s,uint64_t id,const split::GameFrame* game=nullptr,unsigned stream=0){
  NV_ENC_MAP_INPUT_RESOURCE input{};input.version=NV_ENC_MAP_INPUT_RESOURCE_VER;input.registeredResource=registered;
  check(api->nvEncMapInputResource(session,&input),"map input");mapped=input.mappedResource;
  NV_ENC_PIC_PARAMS p{};p.version=NV_ENC_PIC_PARAMS_VER;p.inputWidth=s.width;p.inputHeight=s.height;p.inputBuffer=mapped;
  p.bufferFmt=input.mappedBufferFmt;p.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;p.outputBitstream=output;p.completionEvent=event;
  p.frameIdx=uint32_t(id);p.inputTimeStamp=id*1000000/s.fps;
  if(game){
   const unsigned columns=(s.width+15)/16,rows=(s.height+15)/16,parts=s.streams/2,eye=stream/parts;
   quality.resize(columns*rows);
   const float gx=.5f+.5f*game->gaze[eye*2],gy=.5f-.5f*game->gaze[eye*2+1];
   for(unsigned y=0;y<rows;++y)for(unsigned x=0;x<columns;++x){
    float u=(float(stream%parts)+(x+.5f)/columns)/parts,v=(y+.5f)/rows;
    float radius=std::sqrt((u-gx)*(u-gx)+(v-gy)*(v-gy));
    // Wide 70%-of-eye-diameter plateau with a soft transition. Invalid gaze
    // uses uniform quality, never a stale/small fixed center.
    quality[y*columns+x]=game->gaze_valid?int8_t(std::lround(4*std::clamp((radius-.35f)/.20f,0.f,1.f))):0;
   }
   p.qpDeltaMap=quality.data();p.qpDeltaMapSize=uint32_t(quality.size());
  }
  if(id%s.fps==0)p.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR|NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
  check(api->nvEncEncodePicture(session,&p),"encode");
 }
 void lock(uint64_t pts){
  if(WaitForSingleObject(event,2000)!=WAIT_OBJECT_0)throw std::runtime_error("Encoder completion timeout");
  locked={};locked.version=NV_ENC_LOCK_BITSTREAM_VER;locked.outputBitstream=output;
  check(api->nvEncLockBitstream(session,&locked),"lock");
  if(locked.outputTimeStamp!=pts||!locked.bitstreamSizeInBytes||locked.bitstreamSizeInBytes>split::max_payload)
   throw std::runtime_error("Encoder identity/size failure");
 }
 void release(){check(api->nvEncUnlockBitstream(session,output),"unlock");locked.bitstreamBufferPtr=nullptr;
  check(api->nvEncUnmapInputResource(session,mapped),"unmap");mapped=nullptr;}
 ~Encoder(){if(session){
  if(locked.bitstreamBufferPtr)api->nvEncUnlockBitstream(session,output);
  if(mapped)api->nvEncUnmapInputResource(session,mapped);
  if(event_registered){NV_ENC_EVENT_PARAMS p{};p.version=NV_ENC_EVENT_PARAMS_VER;p.completionEvent=event;api->nvEncUnregisterAsyncEvent(session,&p);}
  if(output)api->nvEncDestroyBitstreamBuffer(session,output);
  if(registered)api->nvEncUnregisterResource(session,registered);
  api->nvEncDestroyEncoder(session);
 }if(event)CloseHandle(event);}
};
const char* shader=R"(
cbuffer Settings:register(b0){uint frame;uint stream;uint width;uint height;uint streams;uint game;uint sourceSrgb;uint pad;}
Texture2D sourceEye:register(t0);SamplerState sampleEye:register(s0);
struct V{float4 pos:SV_Position;};
V vs(uint i:SV_VertexID){V o;float2 uv=float2((i<<1)&2,i&2);o.pos=float4(uv*float2(2,-2)+float2(-1,1),0,1);return o;}
uint hash(uint x){x^=x>>16;x*=0x7feb352d;x^=x>>15;x*=0x846ca68b;return x^(x>>16);}
float4 ps(V v):SV_Target{
 uint x=uint(v.pos.x),y=uint(v.pos.y);uint seed=hash(x+y*width+frame*974711+stream*130007);
 if(game){float2 uv=float2((float(stream%(streams/2))+(x+.5)/width)/(streams/2),(y+.5)/height);
  float3 c=sourceEye.SampleLevel(sampleEye,uv,0).rgb;
  if(sourceSrgb)c=lerp(12.92*c,1.055*pow(max(c,0),1/2.4)-.055,step(.0031308,c));
  return float4(c,1);}
 float3 color=float3(seed&255,(seed>>8)&255,(seed>>16)&255)/255.;
 // A distinct stream strip and moving marker make region order observable.
 if(y<24)color=stream==0?float3(1,0,0):stream==1?float3(0,1,0):stream==2?float3(0,0,1):float3(1,1,0);
 if(abs(int(x)-int((frame*11)%width))<8)color=1;
 return float4(color,1);
})";
void wait_until(Clock::time_point due){
 while(Clock::now()+std::chrono::microseconds(500)<due)std::this_thread::sleep_until(due-std::chrono::microseconds(500));
 while(Clock::now()<due)YieldProcessor();
}
struct Timer {Timer(){timeBeginPeriod(1);}~Timer(){timeEndPeriod(1);}};
int main(int argc,char** argv){try{
 if(argc<4||argc>7)throw std::runtime_error("Use sender STREAMS AGGREGATE_MBPS SECONDS [cabac] [game] [udp]");
 split::Session s;s.streams=std::stoul(argv[1]);unsigned mbps=std::stoul(argv[2]),seconds=std::stoul(argv[3]);
 bool cabac=false,game=false,udp=false;for(int i=4;i<argc;++i){if(std::string(argv[i])=="cabac")cabac=true;else if(std::string(argv[i])=="game")game=true;else if(std::string(argv[i])=="udp")udp=true;else throw std::runtime_error("Unknown sender option");}
 s.width=s.streams==4?1024:2048;s.height=game?2048:1024;s.reserved=(game?1:0)|(udp?2:0);s.frames=seconds*s.fps;s.validate();
 if(mbps<25||mbps>600||seconds<10||seconds>60)throw std::runtime_error("Use 25..600 Mbps and 10..60 seconds");
 ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
 std::unique_ptr<GameCapture> capture;if(game){capture=std::make_unique<GameCapture>();capture->device(device,context);}
 else ok(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
 HMODULE module=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
 if(!module)throw std::runtime_error("System NVENC unavailable");
 auto create=reinterpret_cast<NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*)>(GetProcAddress(module,"NvEncodeAPICreateInstance"));
 auto version=reinterpret_cast<NVENCSTATUS(NVENCAPI*)(uint32_t*)>(GetProcAddress(module,"NvEncodeAPIGetMaxSupportedVersion"));
 if(!create||!version)throw std::runtime_error("NVENC API missing");uint32_t supported=0;check(version(&supported),"NVENC version");
 if(supported<((NVENCAPI_MAJOR_VERSION<<4)|NVENCAPI_MINOR_VERSION))throw std::runtime_error("NVENC 12.1 required");
 NV_ENCODE_API_FUNCTION_LIST api{};api.version=NV_ENCODE_API_FUNCTION_LIST_VER;check(create(&api),"API table");
 std::vector<std::unique_ptr<Encoder>> encoders;
 for(unsigned i=0;i<s.streams;++i){auto e=std::make_unique<Encoder>(api);e->initialize(device.Get(),s,mbps,cabac);encoders.push_back(std::move(e));}
 ComPtr<ID3DBlob> vs,ps,error;ok(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&vs,&error));
 ok(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"ps","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&ps,&error));
 ComPtr<ID3D11VertexShader> vertex;ComPtr<ID3D11PixelShader> pixel;
 ok(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));ok(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
 D3D11_BUFFER_DESC bd{};bd.ByteWidth=32;bd.Usage=D3D11_USAGE_DEFAULT;bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
 ComPtr<ID3D11Buffer> constants;ok(device->CreateBuffer(&bd,nullptr,&constants));auto* cb=constants.Get();
 context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(pixel.Get(),nullptr,0);context->PSSetConstantBuffers(0,1,&cb);
 D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;
 ComPtr<ID3D11SamplerState> sampler;ok(device->CreateSamplerState(&sd,&sampler));auto* sample=sampler.Get();context->PSSetSamplers(0,1,&sample);
 context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);D3D11_VIEWPORT viewport{0,0,float(s.width),float(s.height),0,1};context->RSSetViewports(1,&viewport);
 Connection connection;connection.connect(udp);connection.write(&s,sizeof(s));SendQueue sends(connection);
 uint64_t bytes=0,late=0;std::vector<double> encode_ms,send_ms;Timer timer;MultimediaThread scheduling;auto start=Clock::now();
 for(uint64_t id=0;id<s.frames;++id){
  if(id>1&&!game)wait_until(start+std::chrono::nanoseconds((id-1)*1000000000/s.fps));
  split::GameFrame metadata;if(capture)metadata=capture->frame();
  const auto begin=Clock::now();
  for(unsigned i=0;i<s.streams;++i){uint32_t srgb=0;if(capture){auto* source=capture->eye(i,s.streams);D3D11_SHADER_RESOURCE_VIEW_DESC d{};source->GetDesc(&d);srgb=d.Format==DXGI_FORMAT_R8G8B8A8_UNORM_SRGB||d.Format==DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;context->PSSetShaderResources(0,1,&source);}
   uint32_t settings[]={uint32_t(id),i,s.width,s.height,s.streams,game?1u:0u,srgb,0};
   context->UpdateSubresource(constants.Get(),0,nullptr,settings,0,0);auto* target=encoders[i]->target.Get();
   context->OMSetRenderTargets(1,&target,nullptr);context->Draw(3,0);context->OMSetRenderTargets(0,nullptr,nullptr);}
  context->Flush();
  for(unsigned i=0;i<s.streams;++i)encoders[i]->submit(s,id,game?&metadata:nullptr,i);
  split::Group group;group.id=id;group.pts_us=id*1000000/s.fps;
  for(unsigned i=0;i<s.streams;++i){encoders[i]->lock(group.pts_us);group.bytes[i]=encoders[i]->locked.bitstreamSizeInBytes;}
  const auto encoded=Clock::now();size_t packet_size=sizeof(group)+(game?sizeof(metadata):0);
  for(unsigned i=0;i<s.streams;++i)packet_size+=group.bytes[i];
  std::vector<uint8_t> packet(packet_size);size_t offset=0;
  auto append=[&](const void* data,size_t length){memcpy(packet.data()+offset,data,length);offset+=length;};
  append(&group,sizeof(group));if(game)append(&metadata,sizeof(metadata));
  for(unsigned i=0;i<s.streams;++i){auto& e=*encoders[i];append(e.locked.bitstreamBufferPtr,group.bytes[i]);bytes+=group.bytes[i];
   if(id==0){std::ofstream first("stream-"+std::to_string(i)+"-first.h264",std::ios::binary);first.write(static_cast<char*>(e.locked.bitstreamBufferPtr),group.bytes[i]);}
   e.release();}
  if(id==0){connection.packet(packet);connection.ready();start=Clock::now();}
  else sends.push(std::move(packet));
  const auto sent=Clock::now();
  if(id){encode_ms.push_back(std::chrono::duration<double,std::milli>(encoded-begin).count());
   if(sent>start+std::chrono::nanoseconds(id*1000000000/s.fps))++late;}
 }
 sends.finish();send_ms=std::move(sends.durations);
 const double elapsed=std::chrono::duration<double>(Clock::now()-start).count();
 std::sort(encode_ms.begin(),encode_ms.end());std::sort(send_ms.begin(),send_ms.end());
 std::cout<<"{\"realtime_encoder\":true,\"synthetic_source\":"<<(game?"false":"true")<<",\"steamvr_capture\":"<<(game?"true":"false")<<",\"streams\":"<<s.streams<<",\"fps\":"<<s.fps
  <<",\"width_per_stream\":"<<s.width<<",\"height_per_stream\":"<<s.height<<",\"frames\":"<<s.frames<<",\"target_mbps\":"<<mbps
  <<",\"entropy\":\""<<(cabac?"CABAC":"CAVLC")<<"\",\"encoded_bytes\":"<<bytes<<",\"coded_mbps_nominal\":"<<bytes*8./seconds/1e6
  <<",\"elapsed_seconds\":"<<elapsed<<",\"deadline_misses\":"<<late<<",\"encode_p99_ms\":"<<encode_ms[encode_ms.size()*99/100]
  <<",\"send_p99_ms\":"<<send_ms[send_ms.size()*99/100]<<",\"udp_segmented\":"<<(connection.segmented?"true":"false")<<",\"padding\":false,\"capture_repeated_frames\":"<<(capture?capture->repeats:0)<<",\"capture_skipped_frames\":"<<(capture?capture->skipped:0)<<",\"gaze_valid_frames\":"<<(capture?capture->gaze_frames:0)<<"}\n";
 return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
