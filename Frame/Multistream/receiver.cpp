// Standalone prototype. It does not replace Valve's VRLink decoder or protocol.
#define FRAME_DECODE_EMBEDDED
#include "../AV1/hardware_decode.cpp"
#include "protocol.h"
#include "datagrams.h"
#include "frame_pairer.h"
#include "presentation.h"
#include <condition_variable>
#include <csignal>
#include <sstream>
#include <memory>
#include <deque>

namespace {
void read_exact(int fd,void* destination,size_t size){
 auto* next=static_cast<uint8_t*>(destination);
 while(size){auto n=recv(fd,next,size,0);if(n<0&&errno==EINTR)continue;
  if(n<=0)throw std::runtime_error("Input closed or timed out");
  next+=n;size-=size_t(n);}
}
void write_exact(int fd,const void* source,size_t size){
 auto* next=static_cast<const uint8_t*>(source);
 while(size){auto n=send(fd,next,size,MSG_NOSIGNAL);if(n<0&&errno==EINTR)continue;
  if(n<=0)throw std::runtime_error("Decoder pipe closed or timed out");
  next+=n;size-=size_t(n);}
}
struct Socket {
 int fd=-1;Socket()=default;Socket(const Socket&)=delete;~Socket(){if(fd>=0)close(fd);}
 void timeout(){timeval timeout{5,0};setsockopt(fd,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof(timeout));setsockopt(fd,SOL_SOCKET,SO_SNDTIMEO,&timeout,sizeof(timeout));}
};
struct Workers {
 std::array<std::array<int,2>,4> sockets{};std::vector<std::thread> threads;std::thread reader;
 std::atomic<bool> stop{false};int network=-1;
 Workers(){for(auto& pair:sockets)pair={-1,-1};}
 ~Workers(){
  stop=true;if(network>=0)shutdown(network,SHUT_RDWR);
  for(auto& pair:sockets)for(int fd:pair)if(fd>=0)shutdown(fd,SHUT_RDWR);
  if(reader.joinable())reader.join();
  for(auto& t:threads)if(t.joinable())t.join();
  for(auto& pair:sockets)for(int fd:pair)if(fd>=0)close(fd);
 }
};
double p99(std::vector<double> values){if(values.empty())return 0;std::sort(values.begin(),values.end());return values[values.size()*99/100];}
}
int main(int argc,char** argv){try{
 bool visible=false,zero_copy=true;
 for(int i=1;i<argc;++i){std::string option=argv[i];if(option=="--overlay")visible=true;
  else if(option=="--cpu-upload")zero_copy=false;else throw std::runtime_error("Use receiver [--overlay] [--cpu-upload]");}
 Socket listener;listener.fd=socket(AF_INET,SOCK_STREAM|SOCK_CLOEXEC,0);if(listener.fd<0)throw std::runtime_error("Socket failed");
 int yes=1,buffer=4*1024*1024;setsockopt(listener.fd,SOL_SOCKET,SO_REUSEADDR,&yes,sizeof(yes));
 // Window scaling is negotiated during the handshake. Setting this only on
 // the accepted socket leaves a small advertised window on higher RTT links.
 setsockopt(listener.fd,SOL_SOCKET,SO_RCVBUF,&buffer,sizeof(buffer));
 sockaddr_in address{};address.sin_family=AF_INET;address.sin_port=htons(split::port);inet_pton(AF_INET,"10.35.78.1",&address.sin_addr);
 if(bind(listener.fd,reinterpret_cast<sockaddr*>(&address),sizeof(address))||listen(listener.fd,1))throw std::runtime_error("Paired prototype port unavailable");
 Socket datagrams;datagrams.fd=socket(AF_INET,SOCK_DGRAM|SOCK_CLOEXEC,0);if(datagrams.fd<0)throw std::runtime_error("Datagram socket failed");
 address.sin_port=htons(split::udp_port);setsockopt(datagrams.fd,SOL_SOCKET,SO_RCVBUF,&buffer,sizeof(buffer));datagrams.timeout();
 if(bind(datagrams.fd,reinterpret_cast<sockaddr*>(&address),sizeof(address)))throw std::runtime_error("Datagram port unavailable");
 std::cerr<<"{\"prototype_listening\":true,\"port\":"<<split::port<<"}\n"<<std::flush;
 pollfd wait{listener.fd,POLLIN,0};if(poll(&wait,1,30000)<=0)throw std::runtime_error("No sender within 30 seconds");
 Socket connection;sockaddr_in peer{};socklen_t peer_size=sizeof(peer);connection.fd=accept4(listener.fd,reinterpret_cast<sockaddr*>(&peer),&peer_size,SOCK_CLOEXEC);
 in_addr allowed{};inet_pton(AF_INET,"10.35.78.21",&allowed);
 if(connection.fd<0||peer.sin_addr.s_addr!=allowed.s_addr)throw std::runtime_error("Unexpected peer");
 connection.timeout();setsockopt(connection.fd,SOL_SOCKET,SO_RCVBUF,&buffer,sizeof(buffer));
 split::Session session;read_exact(connection.fd,&session,sizeof(session));session.validate();
 split::Presentation presentation(session,visible);split::Pairer pairer(session.streams,6);
 std::mutex mutex;std::condition_variable cv;std::deque<split::Matched> pending;
 std::map<uint64_t,split::GameFrame> game_frames;
 std::array<std::vector<uint8_t>,4> first_pixels;
 std::array<std::ostringstream,4> reports;std::array<int,4> codes{};codes.fill(-1);
 unsigned ready=0,done=0;uint64_t presentation_drops=0,bytes=0;std::string reader_error;
 bool network_done=false;std::vector<double> skew,composition_ms,interval_ms,lateness_ms;
 // Declared last: cancellation joins workers before destroying any callback state.
 split::Reassembly assembly;Workers workers;workers.network=connection.fd;
 for(unsigned index=0;index<session.streams;++index){
  auto& sockets=workers.sockets[index];if(socketpair(AF_UNIX,SOCK_STREAM|SOCK_CLOEXEC,0,sockets.data()))throw std::runtime_error("Decoder socketpair failed");
  timeval timeout{5,0};for(int fd:sockets){setsockopt(fd,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof(timeout));setsockopt(fd,SOL_SOCKET,SO_SNDTIMEO,&timeout,sizeof(timeout));}
  workers.threads.emplace_back([&,index]{
   std::array<Socket,64> exported;
   struct Releases {std::mutex mutex;std::vector<unsigned> indices;};
   auto releases=std::make_shared<Releases>();
   DecodeHooks hooks;hooks.input_fd=workers.sockets[index][1];
   hooks.retained=[&](unsigned){return zero_copy;};
   hooks.released=[releases]{std::lock_guard<std::mutex> guard(releases->mutex);std::vector<unsigned> result;result.swap(releases->indices);return result;};
   hooks.ready=[&]{std::lock_guard<std::mutex> guard(mutex);++ready;cv.notify_all();};
   hooks.frame=[&,index](uint64_t id,const uint8_t* pixels,size_t size,const v4l2_pix_format_mplane& format,const v4l2_rect& crop,int device,unsigned buffer_index,unsigned offset){
    if(id==0)first_pixels[index]=visible_nv12(pixels,size,format,crop);
    split::Image image;image.id=id;image.decoded=Clock::now();
    if(zero_copy){
     if(offset||crop.left||crop.top||format.pixelformat!=V4L2_PIX_FMT_NV12||format.num_planes!=1)
      throw std::runtime_error("Unsupported DMA-buffer crop/layout");
     if(exported.at(buffer_index).fd<0){v4l2_exportbuffer export_buffer{};export_buffer.type=V4L2_BUF_TYPE_VIDEO_CAPTURE_MPLANE;
      export_buffer.index=buffer_index;export_buffer.flags=O_CLOEXEC|O_RDONLY;
      xio(device,VIDIOC_EXPBUF,&export_buffer,"export capture DMA buffer");exported[buffer_index].fd=export_buffer.fd;}
     image.surface={exported[buffer_index].fd,format.plane_fmt[0].bytesperline,((format.height+31)&~31U)*format.plane_fmt[0].bytesperline};
     image.lease=std::make_shared<split::Lease>([releases,buffer_index]{std::lock_guard<std::mutex> guard(releases->mutex);releases->indices.push_back(buffer_index);});
    }else image.nv12=visible_nv12(pixels,size,format,crop);
    auto group=pairer.submit(index,std::move(image));
    if(group){std::lock_guard<std::mutex> guard(mutex);
     if(pending.size()==6){pending.pop_front();++presentation_drops;}
     skew.push_back(group->skew_ms);pending.push_back(std::move(*group));cv.notify_all();}
    // Keep decoder ownership until GPU completion (or deliberate whole-set
    // discard). No queue buffer is recycled while the renderer samples it.
    // Release is polled on the decoder thread; GPU work never stalls input
    // submission or dequeue of other completed surfaces.
   };
   char name[]="decoder",device[]="/dev/video22",input[]="embedded";char* args[]={name,device,input};
   int result=run_decoder(3,args,&hooks,reports[index]);
   {std::lock_guard<std::mutex> guard(mutex);codes[index]=result;++done;cv.notify_all();}
   shutdown(workers.sockets[index][1],SHUT_RDWR);
  });
 }
 workers.reader=std::thread([&]{try{
  const auto header=split::clip_header(session);for(unsigned i=0;i<session.streams;++i)write_exact(workers.sockets[i][0],header.data(),header.size());
  for(uint64_t id=0;id<session.frames&&!workers.stop;++id){
   std::vector<uint8_t> frame;size_t cursor=0;
   if(session.datagrams()){
    const auto began=Clock::now();std::optional<std::vector<uint8_t>> complete;
    while(!(complete=assembly.take(id))){
     if(workers.stop||Clock::now()-began>std::chrono::seconds(3))throw std::runtime_error("Unrecoverable datagram frame loss");
     std::array<uint8_t,1500> packet{};sockaddr_in source{};socklen_t length=sizeof(source);
     const auto received=recvfrom(datagrams.fd,packet.data(),packet.size(),0,reinterpret_cast<sockaddr*>(&source),&length);
     if(received<0&&errno==EINTR)continue;
     if(received<=0)throw std::runtime_error("Datagram stream timed out");
     if(source.sin_addr.s_addr!=allowed.s_addr||source.sin_port!=htons(split::udp_port))continue;
     assembly.accept(packet.data(),size_t(received));
    }
    frame=std::move(*complete);
   }
   auto read=[&](void* destination,size_t bytes){if(!session.datagrams()){read_exact(connection.fd,destination,bytes);return;}
    if(bytes>frame.size()-cursor)throw std::runtime_error("Truncated datagram frame");
    memcpy(destination,frame.data()+cursor,bytes);cursor+=bytes;};
   split::Group group;read(&group,sizeof(group));group.validate(session,id);
   if(session.game()){split::GameFrame metadata;read(&metadata,sizeof(metadata));metadata.validate();
    std::lock_guard<std::mutex> guard(mutex);game_frames.emplace(id,metadata);
    while(game_frames.size()>64)game_frames.erase(game_frames.begin());}
   for(unsigned i=0;i<session.streams;++i){
    std::vector<uint8_t> payload(group.bytes[i]);read(payload.data(),payload.size());
    write_exact(workers.sockets[i][0],&group.bytes[i],4);write_exact(workers.sockets[i][0],&id,8);write_exact(workers.sockets[i][0],payload.data(),payload.size());bytes+=payload.size();
   }
   if(session.datagrams()&&cursor!=frame.size())throw std::runtime_error("Trailing datagram frame bytes");
   if(id==0){std::unique_lock<std::mutex> guard(mutex);
    if(!cv.wait_for(guard,std::chrono::seconds(5),[&]{return ready==session.streams||done||workers.stop;})||ready!=session.streams)
     throw std::runtime_error("All-decoder readiness barrier failed");
    const char signal='R';write_exact(connection.fd,&signal,1);
   }
  }
 }catch(const std::exception& e){std::lock_guard<std::mutex> guard(mutex);reader_error=e.what();}
  for(unsigned i=0;i<session.streams;++i)shutdown(workers.sockets[i][0],SHUT_WR);
  {std::lock_guard<std::mutex> guard(mutex);network_done=true;cv.notify_all();}
 });
 uint64_t rendered=0,first_id=0,last_id=0,source_epoch=0,clock_resets=0;auto first_time=Clock::now(),last_time=first_time,epoch=first_time;
 while(true){
  std::optional<split::Matched> group;
  split::GameFrame metadata;
  {std::unique_lock<std::mutex> guard(mutex);
   cv.wait_for(guard,std::chrono::milliseconds(10),[&]{return !pending.empty()||done==session.streams||!reader_error.empty();});
   if(!reader_error.empty())throw std::runtime_error(reader_error);
   if(!pending.empty()){group=std::move(pending.front());pending.pop_front();}else if(done==session.streams)break;
   if(group&&session.game()){auto found=game_frames.find(group->id);if(found==game_frames.end())throw std::runtime_error("Missing game pose identity");metadata=found->second;game_frames.erase(game_frames.begin(),std::next(found));}
  }
  // Expire missing partners even when each decoder holds only one live GPU
  // surface. Otherwise both could wait forever on different frame identities.
  pairer.expire(Clock::now()-std::chrono::milliseconds(50));
  if(Clock::now()-last_time>std::chrono::seconds(7))throw std::runtime_error("Presentation stalled");
  // Frame zero initializes every hardware decoder before the sender's ready
  // barrier. Its capture predates initialization and is deliberately not shown.
  if(group&&group->id==0)continue;
  if(group){auto before=Clock::now();const uint64_t source_id=session.game()?metadata.frame_index:group->id;
   if(!rendered){epoch=before+std::chrono::nanoseconds(3000000000/session.fps);source_epoch=source_id;}
   presentation.render(*group);auto composed=Clock::now();
   // GPU completion releases decoder surfaces before the playout wait.
   for(auto& image:group->images)image.lease.reset();
   // Use compositor source identity in game mode. Missing a capture must not
   // turn every following frame into an increasingly late deadline.
   if(source_id<source_epoch)throw std::runtime_error("Source clock went backwards");
   auto due=epoch+std::chrono::nanoseconds((source_id-source_epoch)*1000000000/session.fps);
   if(composed>due+std::chrono::nanoseconds(4000000000/session.fps)){
    ++clock_resets;epoch=composed;source_epoch=source_id;due=epoch;
   }
   std::this_thread::sleep_until(due);presentation.present(session.game()?&metadata:nullptr);auto after=Clock::now();
   lateness_ms.push_back(std::max(0.,std::chrono::duration<double,std::milli>(after-due).count()));
   composition_ms.push_back(std::chrono::duration<double,std::milli>(composed-before).count());
   if(rendered){if(group->id<=last_id)throw std::runtime_error("Presentation went backwards");interval_ms.push_back(std::chrono::duration<double,std::milli>(after-last_time).count());}
   else {first_time=after;first_id=group->id;}
   last_time=after;last_id=group->id;++rendered;
  }
 }
 if(workers.reader.joinable())workers.reader.join();
 for(auto& thread:workers.threads)thread.join();
 pairer.finish();
 if(!network_done||!reader_error.empty())throw std::runtime_error("Input incomplete: "+reader_error);
 for(unsigned i=0;i<session.streams;++i){std::ofstream file("decoded-"+std::to_string(i)+"-first.nv12",std::ios::binary);
  file.write(reinterpret_cast<const char*>(first_pixels[i].data()),first_pixels[i].size());if(!file)throw std::runtime_error("Cannot save first decoded image");}
 if(rendered)presentation.capture("composed-last.ppm");
 const double elapsed=std::chrono::duration<double>(last_time-first_time).count();
 bool functional=pairer.matched==session.frames&&!pairer.incomplete&&!pairer.duplicate&&!pairer.late&&rendered>0;
 for(unsigned i=0;i<session.streams;++i)functional=functional&&codes[i]==0;
 std::cout<<"{\"streams\":"<<session.streams<<",\"hardware_decode\":true,\"gpu_composition\":true,\"decoder_dmabuf_import\":"<<(zero_copy?"true":"false")<<",\"overlay_submitted\":"<<(visible?"true":"false")
  <<",\"udp_fec\":"<<(session.datagrams()?"true":"false")<<",\"recovered_packets\":"<<assembly.recovered
  <<",\"steamvr_game_stream\":"<<(session.game()?"true":"false")<<",\"matched\":"<<pairer.matched<<",\"incomplete\":"<<pairer.incomplete<<",\"late\":"<<pairer.late<<",\"duplicates\":"<<pairer.duplicate
  <<",\"rendered\":"<<rendered<<",\"presentation_queue_drops\":"<<presentation_drops<<",\"first_id\":"<<first_id<<",\"last_id\":"<<last_id
  <<",\"elapsed_seconds\":"<<elapsed<<",\"rendered_fps\":"<<(elapsed>0?(rendered-1)/elapsed:0)<<",\"received_video_bytes\":"<<bytes
  <<",\"pair_skew_p99_ms\":"<<p99(skew)<<",\"composition_p99_ms\":"<<p99(composition_ms)<<",\"presentation_interval_p99_ms\":"<<p99(interval_ms)
  <<",\"presentation_lateness_p99_ms\":"<<p99(lateness_ms)<<",\"playout_delay_frames\":3,\"clock_resets\":"<<clock_resets
  <<",\"functional_pass\":"<<(functional?"true":"false")<<",\"decoders\":[";
 for(unsigned i=0;i<session.streams;++i){if(i)std::cout<<',';std::cout<<"{\"exit_code\":"<<codes[i]<<",\"stats\":"<<(reports[i].str().empty()?"null":reports[i].str())<<'}';}
 std::cout<<"]}\n";return functional?0:3;
}catch(const std::exception& e){std::cerr<<"Prototype failed: "<<e.what()<<'\n';return 1;}}
