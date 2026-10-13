#pragma once
#include <array>
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <map>
#include <memory>
#include <functional>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <vector>

namespace split {
using Time=std::chrono::steady_clock;
struct Surface {int fd=-1;unsigned stride=0,uv_offset=0;};
struct Lease {
 std::function<void()> release;
 explicit Lease(std::function<void()> callback):release(std::move(callback)){}
 ~Lease(){release();}
};
struct Image {
 uint64_t id=0; std::vector<uint8_t> nv12; Time::time_point decoded;
 Surface surface;std::shared_ptr<Lease> lease;
};
struct Matched { uint64_t id=0; std::array<Image,4> images; double skew_ms=0; };
// Bounded ownership: never combine timestamps; retain surfaces until their set
// is consumed or discarded. Expiration releases missing partners. Drops count.
class Pairer {
 struct Pending { std::array<std::optional<Image>,4> images; unsigned mask=0; };
 unsigned streams_,capacity_; uint64_t floor_=0;
 std::map<uint64_t,Pending> pending_;
 std::mutex mutex_;
public:
 uint64_t matched=0,incomplete=0,late=0,duplicate=0;
 Pairer(unsigned streams,unsigned capacity=3):streams_(streams),capacity_(capacity){
  if((streams!=2&&streams!=4)||!capacity||capacity>8)throw std::invalid_argument("Invalid pairing bounds");
 }
 std::optional<Matched> submit(unsigned stream,Image image){
  std::lock_guard<std::mutex> guard(mutex_);
  if(stream>=streams_)throw std::invalid_argument("Invalid stream identity");
  if(image.id<floor_){++late;return {};}
  auto& entry=pending_[image.id];
  if(entry.mask&(1U<<stream)){++duplicate;return {};}
  entry.mask|=1U<<stream;entry.images[stream]=std::move(image);
  std::optional<Matched> result;
  if(entry.mask==(1U<<streams_)-1){
   Matched group;group.id=entry.images[0]->id;
   auto earliest=entry.images[0]->decoded,latest=earliest;
   for(unsigned i=0;i<streams_;++i){
    group.images[i]=std::move(*entry.images[i]);
    earliest=std::min(earliest,group.images[i].decoded);latest=std::max(latest,group.images[i].decoded);
   }
   group.skew_ms=std::chrono::duration<double,std::milli>(latest-earliest).count();
   floor_=group.id+1;++matched;result=std::move(group);
   for(auto it=pending_.begin();it!=pending_.end()&&it->first<floor_;){
    if(it->first+1!=floor_)++incomplete;
    it=pending_.erase(it);
   }
  }
  while(pending_.size()>capacity_){floor_=pending_.begin()->first+1;++incomplete;pending_.erase(pending_.begin());}
  return result;
 }
 void finish(){std::lock_guard<std::mutex> guard(mutex_);incomplete+=pending_.size();pending_.clear();}
 void expire(Time::time_point before){
  std::lock_guard<std::mutex> guard(mutex_);
  while(!pending_.empty()){
   auto entry=pending_.begin();auto oldest=Time::time_point::max();
   for(const auto& image:entry->second.images)if(image)oldest=std::min(oldest,image->decoded);
   if(oldest>=before)break;
   floor_=std::max(floor_,entry->first+1);++incomplete;pending_.erase(entry);
  }
 }
};
}
