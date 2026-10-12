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
 std::cout<<"{\"device_opened\":false,\"padding_removed\":true,\"crop_verified\":true,\"truncation_rejected\":true}\n";
}
