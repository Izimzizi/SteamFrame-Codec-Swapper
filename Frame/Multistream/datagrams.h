#pragma once
#include <array>
#include <map>
#include <optional>
#include <vector>
#include <cstring>
#include <stdexcept>
#include "protocol.h"
namespace split {
constexpr unsigned udp_port=50125,fragment_size=1400,data_fragments=16;
constexpr uint32_t datagram_magic=0x31554446;
constexpr uint32_t max_group_bytes=sizeof(Group)+sizeof(GameFrame)+4*max_payload;
struct Datagram {
 uint64_t id=0;
 uint32_t magic=datagram_magic,total=0;
 uint16_t block=0,index=0,bytes=0,flags=0;
};
static_assert(sizeof(Datagram)==24);
struct FragmentBlock {
 std::array<std::array<uint8_t,fragment_size>,data_fragments+1> data{};
 uint32_t mask=0;
};
// Sixteen data packets plus XOR parity recover one missing packet per block.
// Duplicate/reordered packets are harmless. Incomplete frame groups never
// reach H.264, so a missing predictive reference cannot silently corrupt it.
class Reassembly {
 struct Frame {uint32_t total=0;std::map<unsigned,FragmentBlock> blocks;};
 std::map<uint64_t,Frame> frames_;uint64_t floor_=0;
public:
 uint64_t recovered=0,duplicates=0,late=0;
 void accept(const void* packet,size_t size){
  if(size<sizeof(Datagram))throw std::runtime_error("Short datagram header");
  Datagram h;memcpy(&h,packet,sizeof(h));
  if(h.magic!=datagram_magic||h.flags||h.total<sizeof(Group)||h.total>max_group_bytes||h.index>data_fragments||
     !h.bytes||h.bytes>fragment_size||(size!=sizeof(h)+h.bytes&&size!=sizeof(h)+fragment_size))
   throw std::runtime_error("Invalid datagram bounds");
  if(h.id<floor_){++late;return;}
  if(h.id-floor_>=8)throw std::runtime_error("Datagram frame window exceeded");
  const size_t block_offset=size_t(h.block)*data_fragments*fragment_size;
  if(block_offset>=h.total)throw std::runtime_error("Datagram block outside frame");
  const auto count=std::min<size_t>(data_fragments,(h.total-block_offset+fragment_size-1)/fragment_size);
  if(h.index<data_fragments&&(h.index>=count||h.bytes!=std::min<size_t>(fragment_size,h.total-block_offset-h.index*fragment_size)))
   throw std::runtime_error("Invalid data fragment length");
  if(h.index==data_fragments&&h.bytes!=fragment_size)throw std::runtime_error("Invalid parity fragment length");
  auto& frame=frames_[h.id];if(frame.total&&frame.total!=h.total)throw std::runtime_error("Datagram frame length changed");
  frame.total=h.total;auto& b=frame.blocks[h.block];
  if(b.mask&(1<<h.index)){++duplicates;return;}
  memcpy(b.data[h.index].data(),static_cast<const uint8_t*>(packet)+sizeof(h),h.bytes);b.mask|=1<<h.index;
 }
 std::optional<std::vector<uint8_t>> take(uint64_t id){
  if(id!=floor_)throw std::runtime_error("Datagram frame order changed");
  auto found=frames_.find(id);if(found==frames_.end())return {};
  auto& f=found->second;const auto blocks=(f.total+data_fragments*fragment_size-1)/(data_fragments*fragment_size);
  if(f.blocks.size()!=blocks)return {};
  for(unsigned n=0;n<blocks;++n){auto& b=f.blocks.at(n);
   const unsigned count=std::min<unsigned>(data_fragments,(f.total-n*data_fragments*fragment_size+fragment_size-1)/fragment_size);
   unsigned missing=0,index=0;
   for(unsigned i=0;i<count;++i)if(!(b.mask&(1<<i))){++missing;index=i;}
   if(missing>1||(missing&&!(b.mask&(1<<data_fragments))))return {};
   if(missing){b.data[index]=b.data[data_fragments];for(unsigned i=0;i<count;++i)if(i!=index)
     for(unsigned j=0;j<fragment_size;++j)b.data[index][j]^=b.data[i][j];
    b.mask|=1<<index;++recovered;}
  }
  std::vector<uint8_t> result(f.total);
  for(unsigned n=0;n<blocks;++n)for(unsigned i=0;i<data_fragments;++i){size_t offset=(n*data_fragments+i)*fragment_size;
   if(offset<result.size())memcpy(result.data()+offset,f.blocks.at(n).data[i].data(),std::min<size_t>(fragment_size,result.size()-offset));}
  frames_.erase(found);++floor_;return result;
 }
};
template<class Send> void fragment(uint64_t id,const std::vector<uint8_t>& frame,Send send){
 if(frame.size()<sizeof(Group)||frame.size()>max_group_bytes)throw std::runtime_error("Invalid fragment source");
 const size_t blocks=(frame.size()+data_fragments*fragment_size-1)/(data_fragments*fragment_size);
 std::vector<std::array<uint8_t,fragment_size>> parity(blocks);
 std::array<uint8_t,sizeof(Datagram)+fragment_size> packet{};
 // Interleave blocks so a short burst loss removes one packet from several
 // independent recovery blocks rather than all data in a single block.
 for(unsigned i=0;i<data_fragments+1;++i)for(size_t block=0;block<blocks;++block){
  const size_t offset=(block*data_fragments+i)*fragment_size;
  Datagram h;h.id=id;h.total=uint32_t(frame.size());h.block=uint16_t(block);h.index=uint16_t(i);
  if(i<data_fragments){if(offset>=frame.size())continue;h.bytes=uint16_t(std::min<size_t>(fragment_size,frame.size()-offset));
   memcpy(packet.data()+sizeof(h),frame.data()+offset,h.bytes);
   std::fill(packet.begin()+sizeof(h)+h.bytes,packet.end(),uint8_t(0));
   for(unsigned j=0;j<h.bytes;++j)parity[block][j]^=frame[offset+j];
  }else{h.bytes=fragment_size;memcpy(packet.data()+sizeof(h),parity[block].data(),fragment_size);}
  memcpy(packet.data(),&h,sizeof(h));send(packet.data(),packet.size());
 }
}
}
