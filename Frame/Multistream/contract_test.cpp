#include "protocol.h"
#include "frame_pairer.h"
#include "datagrams.h"
#include <iostream>
#define CHECK(x) do{if(!(x))throw std::runtime_error(#x);}while(false)
template<class F> void rejects(F f){bool caught=false;try{f();}catch(const std::exception&){caught=true;}CHECK(caught);}
int main(){try{
 split::Session session;session.validate();split::Group header;header.bytes={1,1,0,0};header.validate(session,0);
 rejects([&]{auto s=session;s.streams=3;s.validate();});
 rejects([&]{auto s=session;s.frames=1000000;s.validate();});
 rejects([&]{auto h=header;h.bytes[0]=split::max_payload+1;h.validate(session,0);});
 rejects([&]{auto h=header;h.pts_us=1;h.validate(session,0);});
 rejects([&]{auto h=header;h.bytes[2]=1;h.validate(session,0);});
 // A translated, 90-degree yawed camera must map its own position to the
 // origin, and its forward ray to camera -Z. A pose used as a view fails both.
 split::Transform camera{0,0,1,2, 0,1,0,3, -1,0,0,4};
 auto view=split::view_from_pose(camera.data());auto identity=split::compose(view.data(),camera.data());
 for(unsigned r=0;r<3;++r)for(unsigned c=0;c<4;++c)CHECK(std::abs(identity[r*4+c]-(r==c?1.f:0.f))<.0001f);
 float point[3]{1,3,4},camera_point[3]{};
 for(unsigned r=0;r<3;++r){camera_point[r]=view[r*4+3];for(unsigned k=0;k<3;++k)camera_point[r]+=view[r*4+k]*point[k];}
 CHECK(std::abs(camera_point[0])<.0001f&&std::abs(camera_point[1])<.0001f&&std::abs(camera_point[2]+1)<.0001f);
 split::GameFrame game;std::copy(camera.begin(),camera.end(),game.pose);
 for(unsigned eye=0;eye<2;++eye){game.projection[eye*4]=-1;game.projection[eye*4+1]=1;game.projection[eye*4+2]=-1;game.projection[eye*4+3]=1;}
 game.validate();rejects([&]{auto bad=game;bad.pose[0]=3;bad.validate();});
 rejects([&]{auto bad=game;bad.projection[0]=2;bad.validate();});
 // Recover arbitrary single-packet loss per block, including the short tail,
 // with packet reordering and duplicates. Two missing data packets stay incomplete.
 std::vector<uint8_t> source(split::fragment_size*(split::data_fragments*2+3)+31);
 for(size_t i=0;i<source.size();++i)source[i]=uint8_t(i*37);
 std::vector<std::vector<uint8_t>> packets;
 split::fragment(0,source,[&](const void* data,size_t size){auto* bytes=static_cast<const uint8_t*>(data);packets.emplace_back(bytes,bytes+size);});
 for(unsigned missing=0;missing<split::data_fragments;++missing){split::Reassembly r;
  for(auto it=packets.rbegin();it!=packets.rend();++it){split::Datagram h;memcpy(&h,it->data(),sizeof(h));if(h.index==missing)continue;r.accept(it->data(),it->size());r.accept(it->data(),it->size());}
  auto recovered=r.take(0);CHECK(recovered&&*recovered==source);CHECK(r.recovered>=2);CHECK(r.duplicates>0);
 }
 split::Reassembly unrecoverable;
 for(auto& packet:packets){split::Datagram h;memcpy(&h,packet.data(),sizeof(h));if(h.index<2)continue;unrecoverable.accept(packet.data(),packet.size());}
 CHECK(!unrecoverable.take(0));
 rejects([&]{split::Reassembly r;auto invalid=packets.front();split::Datagram h;memcpy(&h,invalid.data(),sizeof(h));h.total=0;memcpy(invalid.data(),&h,sizeof(h));r.accept(invalid.data(),invalid.size());});
 auto image=[](uint64_t id){split::Image value;value.id=id;value.nv12={uint8_t(id)};value.decoded=split::Time::now();return value;};
 split::Pairer pair(2,2);
 CHECK(!pair.submit(0,image(0)));CHECK(!pair.submit(0,image(0)));CHECK(pair.duplicate==1);
 CHECK(!pair.submit(1,image(1))); // Adjacent timestamps must never pair.
 auto zero=pair.submit(1,image(0));CHECK(zero&&zero->id==0);
 auto one=pair.submit(0,image(1));CHECK(one&&one->images[1].id==1);
 CHECK(!pair.submit(1,image(0)));CHECK(pair.late==1);
 CHECK(!pair.submit(0,image(2)));CHECK(!pair.submit(0,image(3)));CHECK(!pair.submit(0,image(4)));
 CHECK(pair.incomplete==1);CHECK(!pair.submit(1,image(2)));CHECK(pair.late==2);
 auto four=pair.submit(1,image(4));CHECK(four&&four->id==4);CHECK(pair.incomplete==2);
 split::Pairer quad(4);for(unsigned i=0;i<3;++i)CHECK(!quad.submit(i,image(42)));
 auto all=quad.submit(3,image(42));CHECK(all&&all->id==42);CHECK(quad.matched==1);
 CHECK(!quad.submit(0,image(43)));quad.finish();CHECK(quad.incomplete==1);
 // A decoder surface is retained until the complete set is consumed/discarded.
 unsigned released=0;
 {
  split::Pairer leases(2,1);
  auto a=image(1);a.lease=std::make_shared<split::Lease>([&]{++released;});
  CHECK(!leases.submit(0,std::move(a)));CHECK(released==0);
  auto group=leases.submit(1,image(1));CHECK(group);CHECK(released==0);
  group.reset();CHECK(released==1);
  auto b=image(2);b.lease=std::make_shared<split::Lease>([&]{++released;});
  CHECK(!leases.submit(0,std::move(b)));CHECK(!leases.submit(0,image(3)));CHECK(released==2);
 }
 {
  split::Pairer leases(2);
  auto missing=image(5);missing.decoded-=std::chrono::milliseconds(100);
  missing.lease=std::make_shared<split::Lease>([&]{++released;});
  CHECK(!leases.submit(0,std::move(missing)));leases.expire(split::Time::now()-std::chrono::milliseconds(50));
  CHECK(released==3);CHECK(leases.incomplete==1);
  CHECK(!leases.submit(1,image(5)));CHECK(leases.late==1);
  CHECK(!leases.submit(0,image(6)));CHECK(leases.submit(1,image(6)));
 }
 std::cout<<"{\"protocol_guards\":true,\"timestamp_pairing\":true,\"bounded_queues\":true,\"four_streams\":true}\n";
 return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
