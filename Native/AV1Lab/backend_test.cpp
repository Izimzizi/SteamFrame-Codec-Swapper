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
#include <cstring>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include "av1_backend.h"
using Microsoft::WRL::ComPtr;
using Clock=std::chrono::steady_clock;
struct MultimediaThread {
 HANDLE handle=nullptr;
 MultimediaThread(){DWORD index=0;handle=AvSetMmThreadCharacteristicsW(L"Games",&index);
  if(handle)AvSetMmThreadPriority(handle,AVRT_PRIORITY_HIGH);}
 ~MultimediaThread(){if(handle)AvRevertMmThreadCharacteristics(handle);}
};
void wait_frame(Clock::time_point due){
 // Sleep for the coarse portion, then avoid Windows' final sleep rounding.
 const auto spin=std::chrono::microseconds(500);
 while(Clock::now()+spin<due)std::this_thread::sleep_until(due-spin);
 while(Clock::now()<due)YieldProcessor();
}
void ok(HRESULT status){if(FAILED(status))throw std::runtime_error("D3D11 failure");}
void av(int status){if(status)throw std::runtime_error(Av1LastError());}
const char* shader=R"(
cbuffer Settings:register(b0){float phase;float width;float height;float unused;}
struct V{float4 pos:SV_Position;float2 uv:TEXCOORD0;};
V vs(uint i:SV_VertexID){V o;o.uv=float2((i<<1)&2,i&2);o.pos=float4(o.uv*float2(2,-2)+float2(-1,1),0,1);return o;}
float4 ps(V v):SV_Target{float2 p=v.uv*float2(width,height)+float2(phase*90,phase*45);float gray=.5+.23*sin(p.x*.65)+.23*sin(p.y*.4);return float4(gray,gray*.9,gray*.8,1);}
)";
struct Sender {
    SOCKET socket=INVALID_SOCKET;
    Sender(bool enable) {
        if(!enable)return;
        WSADATA ws{};if(WSAStartup(MAKEWORD(2,2),&ws))throw std::runtime_error("WSAStartup failed");
        socket=::socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);
        if(socket==INVALID_SOCKET)throw std::runtime_error("socket failed");
        sockaddr_in local{};local.sin_family=AF_INET;inet_pton(AF_INET,"10.35.78.21",&local.sin_addr);
        if(bind(socket,reinterpret_cast<sockaddr*>(&local),sizeof(local)))throw std::runtime_error("Dongle source IP unavailable");
        DWORD timeout=2000;setsockopt(socket,SOL_SOCKET,SO_SNDTIMEO,reinterpret_cast<char*>(&timeout),sizeof(timeout));
        int no_delay=1;setsockopt(socket,IPPROTO_TCP,TCP_NODELAY,reinterpret_cast<char*>(&no_delay),sizeof(no_delay));
        sockaddr_in peer{};peer.sin_family=AF_INET;peer.sin_port=htons(50120);inet_pton(AF_INET,"10.35.78.1",&peer.sin_addr);
        if(connect(socket,reinterpret_cast<sockaddr*>(&peer),sizeof(peer)))throw std::runtime_error("Frame lab receiver unavailable");
    }
    void write(const void* data,size_t bytes) {
        if(socket==INVALID_SOCKET)return;
        auto* p=static_cast<const char*>(data);
        while(bytes){int sent=send(socket,p,static_cast<int>((std::min)(bytes,size_t(1048576))),0);
            if(sent<=0)throw std::runtime_error("Dongle transfer failed");p+=sent;bytes-=sent;}
    }
    ~Sender(){if(socket!=INVALID_SOCKET){shutdown(socket,SD_SEND);closesocket(socket);WSACleanup();}}
};
int main(int argc,char** argv) {
    Av1Session* encoder=nullptr;
    try {
        const unsigned width=argc>1?std::stoul(argv[1]):1536,height=argc>2?std::stoul(argv[2]):6144;
        const unsigned frames=argc>3?std::stoul(argv[3]):600;
        const bool send=argc>4 && std::string(argv[4])=="dongle";
        const unsigned bit_depth=argc>5?std::stoul(argv[5]):10;
        const unsigned requested_mbps=argc>6?std::stoul(argv[6]):200;
        if(requested_mbps<1||requested_mbps>417)throw std::runtime_error("Trial average must be 1..417 Mbps");
        const unsigned average=requested_mbps*1000000;
        const unsigned peak=static_cast<unsigned>(uint64_t(average)*115/100);
        const unsigned level=argc>7?std::stoul(argv[7]):(width*height>3840*2160?17u:24u);
        const unsigned tile_columns=argc>8?std::stoul(argv[8]):0,tile_rows=argc>9?std::stoul(argv[9]):0;
        if(frames<120||frames>72000)throw std::runtime_error("Use 120..72000 frames");
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        ok(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
        Av1Config config{sizeof(Av1Config),2,width,height,120,bit_depth,average,peak,120,1,8,level,tile_columns,tile_rows};
        Av1Config invalid=config;invalid.fps=90;Av1Session* rejected=nullptr;
        if(Av1Create(device.Get(),&invalid,&rejected)==0||rejected)throw std::runtime_error("120-fps guard failed");
        av(Av1Create(device.Get(),&config,&encoder));
        if(Av1SetRate(encoder,200000000,100000000)==0)throw std::runtime_error("Invalid peak accepted");
        D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.ArraySize=desc.MipLevels=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;
        desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> texture;ComPtr<ID3D11RenderTargetView> target;
        ok(device->CreateTexture2D(&desc,nullptr,&texture));ok(device->CreateRenderTargetView(texture.Get(),nullptr,&target));
        ComPtr<ID3DBlob> vs,ps,error;ok(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&vs,&error));
        ok(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"ps","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&ps,&error));
        ComPtr<ID3D11VertexShader> vertex;ComPtr<ID3D11PixelShader> pixel;
        ok(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
        ok(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
        D3D11_BUFFER_DESC bd{};bd.ByteWidth=16;bd.Usage=D3D11_USAGE_DEFAULT;bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        ComPtr<ID3D11Buffer> constants;ok(device->CreateBuffer(&bd,nullptr,&constants));
        ID3D11Buffer* cb=constants.Get();context->PSSetConstantBuffers(0,1,&cb);context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(pixel.Get(),nullptr,0);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&viewport);
        std::vector<int8_t> map(((width+63)/64)*((height+63)/64),8);
        for(unsigned y=0;y<(height+63)/64;++y)for(unsigned x=0;x<(width+63)/64;++x)
            if(x>=(width+63)/64/3&&x<(width+63)/64*2/3)map[y*((width+63)/64)+x]=-8;
        Av1Packet packet{sizeof(Av1Packet),1};
        if(Av1Encode(encoder,texture.Get(),0,0,map.data(),static_cast<uint32_t>(map.size()-1),0,&packet)==0)
            throw std::runtime_error("Map size guard failed");
        Sender sender(send);std::ofstream file("backend-test.ivf",std::ios::binary);
        char header[32]={'D','K','I','F',0,0,32,0,'A','V','0','1'};
        const uint16_t w=static_cast<uint16_t>(width),h=static_cast<uint16_t>(height);const uint32_t rate=120,scale=1;
        memcpy(header+12,&w,2);memcpy(header+14,&h,2);memcpy(header+16,&rate,4);memcpy(header+20,&scale,4);memcpy(header+24,&frames,4);
        file.write(header,32);sender.write(header,32);
        // Warm the graphics shader/target before the measured encode cadence.
        float warm[]={0,float(width),float(height),0};context->UpdateSubresource(constants.Get(),0,nullptr,warm,0,0);
        auto* warm_target=target.Get();context->OMSetRenderTargets(1,&warm_target,nullptr);context->Draw(3,0);
        context->OMSetRenderTargets(0,nullptr,nullptr);
        D3D11_QUERY_DESC query_desc{D3D11_QUERY_EVENT,0};ComPtr<ID3D11Query> warmed;
        ok(device->CreateQuery(&query_desc,&warmed));context->End(warmed.Get());context->Flush();
        const auto warm_start=Clock::now();
        while(context->GetData(warmed.Get(),nullptr,0,0)==S_FALSE){
            if(Clock::now()-warm_start>std::chrono::seconds(1))throw std::runtime_error("GPU warmup timed out");
            std::this_thread::sleep_for(std::chrono::microseconds(100));
        }
        uint64_t misses=0,keyframes=0,steady_misses=0;std::vector<unsigned> missed_ids;
        std::vector<double> latency;latency.reserve(frames);
        MultimediaThread multimedia;
        timeBeginPeriod(1);const auto start=Clock::now();
        for(unsigned i=0;i<frames;++i) {
            wait_frame(start+std::chrono::nanoseconds(uint64_t(i)*1000000000/120));
            const auto began=Clock::now();
            if(i==frames/2)av(Av1SetRate(encoder,average*3/4,peak*3/4));
            float values[]={i/120.f,float(width),float(height),0};context->UpdateSubresource(constants.Get(),0,nullptr,values,0,0);
            auto* rt=target.Get();context->OMSetRenderTargets(1,&rt,nullptr);context->Draw(3,0);context->OMSetRenderTargets(0,nullptr,nullptr);context->Flush();
            av(Av1Encode(encoder,texture.Get(),i,uint64_t(i)*1000000/120,map.data(),static_cast<uint32_t>(map.size()),i==frames/3,&packet));
            if(packet.frame_id!=i || packet.timestamp_us!=uint64_t(i)*1000000/120)throw std::runtime_error("Frame identity mismatch");
            keyframes+=packet.keyframe;
            file.write(reinterpret_cast<const char*>(&packet.bytes),4);file.write(reinterpret_cast<const char*>(&packet.frame_id),8);file.write(reinterpret_cast<const char*>(packet.data),packet.bytes);
            sender.write(&packet.bytes,4);sender.write(&packet.frame_id,8);sender.write(packet.data,packet.bytes);
            const auto end=Clock::now();latency.push_back(std::chrono::duration<double,std::milli>(end-began).count());
            if(end>start+std::chrono::nanoseconds(uint64_t(i+1)*1000000000/120)){
                ++misses;if(i)++steady_misses;if(missed_ids.size()<16)missed_ids.push_back(i);
            }
        }
        timeEndPeriod(1);file.close();
        Av1Stats stats{sizeof(Av1Stats),1};av(Av1GetStats(encoder,&stats));
        if(stats.completed!=frames || stats.submitted!=frames || stats.errors)throw std::runtime_error("Frame accounting failed");
        std::sort(latency.begin(),latency.end());
        std::cout<<"{\"codec\":\"AV1\",\"width\":"<<width<<",\"height\":"<<height<<",\"fps\":120,\"bit_depth\":"<<bit_depth<<",\"completed\":"<<stats.completed
            <<",\"level_index\":"<<level<<",\"initial_average_bps\":"<<average
            <<",\"tile_columns\":"<<tile_columns<<",\"tile_rows\":"<<tile_rows
            <<",\"errors\":"<<stats.errors<<",\"keyframes\":"<<keyframes<<",\"deadline_misses\":"<<misses<<",\"p99_ms\":"<<latency[frames*99/100]
            <<",\"max_ms\":"<<latency.back()<<",\"encoded_bytes\":"<<stats.encoded_bytes<<",\"rate_reconfigure\":true,\"dongle_send\":"<<(send?"true":"false")
            <<",\"steady_deadline_misses\":"<<steady_misses<<",\"first_frame_ms\":"<<latency.front()<<",\"missed_frame_ids\":[";
        for(size_t i=0;i<missed_ids.size();++i){if(i)std::cout<<",";std::cout<<missed_ids[i];}
        std::cout<<"],\"steamvr_integration\":false}"<<std::endl;
        Av1Destroy(encoder);return 0;
    } catch(const std::exception& error) {std::cerr<<error.what()<<std::endl;Av1Destroy(encoder);return 1;}
}
