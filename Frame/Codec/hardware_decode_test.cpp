// Host-only checks for hardware padding/crop handling; no device is opened.
#define main hardware_decode_entry
#include "hardware_decode.cpp"
#undef main

int main() {
 v4l2_pix_format_mplane format{};
 format.width=128;format.height=32;format.num_planes=1;
 format.plane_fmt[0].bytesperline=128;
 std::vector<uint8_t> padded(6144,0xee);
 for(unsigned y=0;y<4;++y)for(unsigned x=0;x<4;++x)padded[y*128+x]=uint8_t(y*4+x+1);
 for(unsigned y=0;y<2;++y)for(unsigned x=0;x<4;++x)padded[4096+y*128+x]=uint8_t(y*4+x+17);
 const v4l2_rect whole{0,0,4,4},crop{2,2,2,2};
 std::vector<uint8_t> expected(24);for(unsigned i=0;i<24;++i)expected[i]=uint8_t(i+1);
 if(visible_nv12(padded.data(),padded.size(),format,whole)!=expected)return 1;
 if(visible_nv12(padded.data(),padded.size(),format,crop)!=std::vector<uint8_t>({11,12,15,16,23,24}))return 2;
 try{visible_nv12(padded.data(),4096,format,whole);return 3;}catch(const std::runtime_error&){}
 try{visible_nv12(padded.data(),padded.size(),format,{1,0,4,4});return 4;}catch(const std::runtime_error&){}
 try{visible_nv12(padded.data(),padded.size(),format,{126,0,4,4});return 5;}catch(const std::runtime_error&){}
 // The lab H264 framing uses complete access units and frame-index timestamps.
 // AV01 keeps its existing framing; neither format may silently fall back.
 const auto path=std::string("/tmp/frame-codec-header-test-")+std::to_string(getpid());
 auto write_clip=[&](const char* codec,uint32_t fps,uint64_t stamp){
  std::array<uint8_t,32> header{};memcpy(header.data(),"DKIF",4);header[6]=32;
  memcpy(header.data()+8,codec,4);uint16_t width=128,height=256;
  uint32_t scale=1,count=1,size=1;
  memcpy(header.data()+12,&width,2);memcpy(header.data()+14,&height,2);
  memcpy(header.data()+16,&fps,4);memcpy(header.data()+20,&scale,4);memcpy(header.data()+24,&count,4);
  std::ofstream file(path,std::ios::binary);file.write(reinterpret_cast<char*>(header.data()),32);
  file.write(reinterpret_cast<char*>(&size),4);file.write(reinterpret_cast<char*>(&stamp),8);file.put(0);
 };
 write_clip("H264",90,0);
 {Clip clip(path.c_str());if(clip.fps!=90||clip.fourcc!=V4L2_PIX_FMT_H264||clip.frames.size()!=1){unlink(path.c_str());return 6;}}
 write_clip("AV01",120,0);
 {Clip clip(path.c_str());if(clip.fps!=120||clip.fourcc!=V4L2_PIX_FMT_AV1){unlink(path.c_str());return 7;}}
 write_clip("HEVC",90,0);
 {Clip clip(path.c_str());if(clip.fps!=90||clip.fourcc!=V4L2_PIX_FMT_HEVC){unlink(path.c_str());return 8;}}
 write_clip("FAKE",90,0);
 try{Clip clip(path.c_str());unlink(path.c_str());return 11;}catch(const std::runtime_error&){}
 write_clip("H264",91,0);
 try{Clip clip(path.c_str());unlink(path.c_str());return 9;}catch(const std::runtime_error&){}
 write_clip("H264",90,1);
 try{Clip clip(path.c_str());unlink(path.c_str());return 10;}catch(const std::runtime_error&){}
 unlink(path.c_str());
 std::cout<<"{\"device_opened\":false,\"padding_removed\":true,\"crop_verified\":true,\"truncation_rejected\":true,\"codec_rate_timestamp_guards\":true}\n";
}
