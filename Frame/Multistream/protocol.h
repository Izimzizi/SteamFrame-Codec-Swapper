#pragma once
#include <array>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <stdexcept>
#include "projection_math.h"

// Private, little-endian lab protocol, not Virtual Desktop's wire protocol.
// One connection carries equal-size independent H.264 access units. Each group
// shares one frame ID/PTS. No padding bytes are included in the video byte count.
namespace split {
constexpr uint32_t magic=0x31534d46; // FMS1
constexpr uint32_t max_payload=4*1024*1024;
constexpr unsigned port=50124;
struct Session {
 uint32_t signature=magic,version=3,streams=2,width=2048,height=1024,fps=90,frames=900,reserved=0;
 bool game()const{return (reserved&1)!=0;}
 bool datagrams()const{return (reserved&2)!=0;}
 void validate() const {
  if(signature!=magic||version!=3||(streams!=2&&streams!=4)||width<128||height<128||
     width>4096||height>4096||(width|height)&1||uint64_t(width)*height*streams>16777216||
     (fps!=90&&fps!=120)||frames<fps*10||frames>fps*60||reserved>3)
   throw std::runtime_error("Unsupported bounded multistream session");
 }
};
static_assert(sizeof(Session)==32);
struct Group {
 uint64_t id=0,pts_us=0;
 std::array<uint32_t,4> bytes{};
 void validate(const Session& session,uint64_t expected) const {
  if(id!=expected||id>=session.frames||pts_us!=id*1000000/session.fps)
   throw std::runtime_error("Out-of-order or mismatched group timestamp");
  for(unsigned i=0;i<4;++i)
   if(i<session.streams?(!bytes[i]||bytes[i]>max_payload):bytes[i]!=0)
    throw std::runtime_error("Invalid access-unit length");
 }
};
static_assert(sizeof(Group)==32);
// Explicit wire fields avoid Windows/Linux OpenVR packing differences. The
// captured pose, projections and gaze belong to this frame identity.
struct GameFrame {
 uint32_t frame_index=0,origin=1,gaze_valid=0,reserved=0;
 float pose[12]{},projection[8]{},gaze[4]{};
 void validate()const{
  if(origin>2||gaze_valid>1||reserved)throw std::runtime_error("Invalid game metadata flags");
  for(float value:pose)if(!std::isfinite(value)||std::abs(value)>10000)throw std::runtime_error("Invalid capture pose");
  rigid(pose);
  for(float value:projection)if(!std::isfinite(value)||std::abs(value)>10)throw std::runtime_error("Invalid capture projection");
  for(unsigned eye=0;eye<2;++eye){const auto* p=projection+eye*4;
   if(p[0]>=p[1]||p[2]>=p[3])throw std::runtime_error("Empty capture projection");}
  for(float value:gaze)if(!std::isfinite(value)||std::abs(value)>2)throw std::runtime_error("Invalid gaze coordinate");
 }
};
static_assert(sizeof(GameFrame)==112);
inline std::array<uint8_t,32> clip_header(const Session& s){
 std::array<uint8_t,32> h{};std::memcpy(h.data(),"DKIF",4);h[6]=32;std::memcpy(h.data()+8,"H264",4);
 uint16_t w=uint16_t(s.width),v=uint16_t(s.height);uint32_t scale=1;
 std::memcpy(h.data()+12,&w,2);std::memcpy(h.data()+14,&v,2);std::memcpy(h.data()+16,&s.fps,4);
 std::memcpy(h.data()+20,&scale,4);std::memcpy(h.data()+24,&s.frames,4);return h;
}
}
