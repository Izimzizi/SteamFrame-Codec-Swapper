#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <cstring>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include "av1_backend.h"
using Microsoft::WRL::ComPtr;
void ok(HRESULT h){if(FAILED(h))throw std::runtime_error("D3D surface creation/upload failed");}
void av(int n){if(n)throw std::runtime_error(Av1LastError());}
int main(int argc,char** argv) {
 Av1Session* s=nullptr;
 try {
  const bool p010=argc>1 && !strcmp(argv[1],"p010");
  constexpr unsigned w=512,h=512;
  const unsigned frames=argc>4?static_cast<unsigned>(std::stoul(argv[4])):120;
  if(frames<120||frames>72000)throw std::runtime_error("Use 120..72000 frames");
  ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
  ok(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
  const uint32_t average=argc>3?static_cast<uint32_t>(std::stoul(argv[3]))*1000000:50000000;
  Av1Config config{sizeof(Av1Config),2,w,h,120,p010?10u:8u,average,average*6/5,120,0,p010?10u:8u,argc>2?static_cast<uint32_t>(std::stoul(argv[2])):24u,0,0};
  av(Av1Create(device.Get(),&config,&s));
  D3D11_TEXTURE2D_DESC desc{};desc.Width=w;desc.Height=h;desc.MipLevels=desc.ArraySize=1;
  desc.Format=p010?DXGI_FORMAT_P010:DXGI_FORMAT_NV12;desc.SampleDesc.Count=1;
  desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> texture;ok(device->CreateTexture2D(&desc,nullptr,&texture));
  std::vector<uint8_t> data(w*h*3/2*(p010?2:1));
  if(p010) {
   auto* samples=reinterpret_cast<uint16_t*>(data.data());
   for(unsigned i=0;i<w*h;++i)samples[i]=static_cast<uint16_t>((64+(i%w))<<6);
   for(unsigned i=w*h;i<w*h*3/2;++i)samples[i]=512<<6;
  } else {
   for(unsigned i=0;i<w*h;++i)data[i]=static_cast<uint8_t>(16+(i%w)%220);
   memset(data.data()+w*h,128,w*h/2);
  }
  std::ofstream file(p010?"surface-p010.ivf":"surface-nv12.ivf",std::ios::binary);
  char header[32]={'D','K','I','F',0,0,32,0,'A','V','0','1'};
  uint16_t width=w,height=h;uint32_t rate=120,scale=1;
  memcpy(header+12,&width,2);memcpy(header+14,&height,2);memcpy(header+16,&rate,4);memcpy(header+20,&scale,4);memcpy(header+24,&frames,4);file.write(header,32);
  for(uint64_t i=0;i<frames;++i) {
   context->UpdateSubresource(texture.Get(),0,nullptr,data.data(),w*(p010?2:1),0);context->Flush();
   Av1Packet packet{sizeof(Av1Packet),1};av(Av1Encode(s,texture.Get(),i,i*1000000/120,nullptr,0,0,&packet));
   file.write(reinterpret_cast<char*>(&packet.bytes),4);file.write(reinterpret_cast<char*>(&i),8);file.write(reinterpret_cast<const char*>(packet.data),packet.bytes);
  }
  Av1Stats stats{sizeof(Av1Stats),1};av(Av1GetStats(s,&stats));
  if(stats.completed!=frames||stats.errors)return 2;
  std::cout<<"{\"input\":\""<<(p010?"P010":"NV12")<<"\",\"codec\":\"AV1\",\"frames\":"<<stats.completed<<",\"errors\":0,\"paced\":false}\n";
  Av1Destroy(s);return 0;
 } catch(const std::exception& e){std::cerr<<e.what()<<"\n";Av1Destroy(s);return 1;}
}
