// Stateful V4L2 hardware AV1/H.264/HEVC test. No software decoder or codec fallback.
#include <linux/videodev2.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <arpa/inet.h>
#include <fcntl.h>
#include <poll.h>
#include <unistd.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#ifndef V4L2_PIX_FMT_AV1
#define V4L2_PIX_FMT_AV1 v4l2_fourcc('A','V','0','1')
#endif
#ifndef V4L2_PIX_FMT_QC08C
#define V4L2_PIX_FMT_QC08C v4l2_fourcc('Q','0','8','C')
#endif
#ifndef V4L2_PIX_FMT_QC10C
#define V4L2_PIX_FMT_QC10C v4l2_fourcc('Q','1','0','C')
#endif
using Clock=std::chrono::steady_clock;
void xio(int fd,unsigned long op,void* data,const char* label) {
 int result;do{result=ioctl(fd,op,data);}while(result<0&&errno==EINTR);
 if(result<0)throw std::runtime_error(std::string(label)+": "+strerror(errno));
}
struct Frame {std::vector<uint8_t> bytes;uint64_t id;};
struct Clip {
 int source=-1,listener=-1;
 bool live=false;
 uint16_t width=0,height=0;
 uint32_t fps=120,fourcc=V4L2_PIX_FMT_AV1;
 unsigned port=50120;
 const char* codec() const {return fourcc==V4L2_PIX_FMT_H264?"H.264":fourcc==V4L2_PIX_FMT_HEVC?"HEVC":"AV1";}
 std::vector<Frame> frames;
 std::atomic<unsigned> ready{0},consumed{0};
 std::atomic<bool> stopped{false},failed{false};
 std::thread reader;
 void exact(void* destination,size_t size){
  auto* next=static_cast<uint8_t*>(destination);
  while(size){
   const auto result=read(source,next,size);
   if(result<0&&errno==EINTR)continue;
   if(result<=0)throw std::runtime_error("Compressed input ended or timed out");
   next+=result;size-=static_cast<size_t>(result);
  }
 }
 Frame record(unsigned index){
  Frame frame{};uint32_t size=0;exact(&size,4);exact(&frame.id,8);
  if(!size||size>16000000||frame.id!=index)throw std::runtime_error("Invalid IVF frame/timestamp");
  frame.bytes.resize(size);exact(frame.bytes.data(),size);return frame;
 }
 void cleanup(){
  stopped=true;
  if(live&&source>=0)shutdown(source,SHUT_RDWR);
  if(reader.joinable())reader.join();
  if(source>=0)close(source);
  if(listener>=0)close(listener);
  source=listener=-1;
 }
 explicit Clip(const char* path,int input_fd=-1,unsigned max_pending=32){
  try{
   const std::string source_path=path;
   live=input_fd>=0||source_path=="tcp"||source_path.rfind("tcp:",0)==0;
   if(source_path.rfind("tcp:",0)==0){
    const auto suffix=source_path.substr(4);
    if(suffix!="50120"&&suffix!="50121"&&suffix!="50122"&&suffix!="50123")
     throw std::runtime_error("Only the four paired lab ports are supported");
    port=std::stoul(suffix);
   }
   if(input_fd>=0){
    source=dup(input_fd);
    if(source<0)throw std::runtime_error("Cannot duplicate supplied input socket");
   }else if(live){
    listener=socket(AF_INET,SOCK_STREAM|SOCK_CLOEXEC,0);
    if(listener<0)throw std::runtime_error("Cannot create dongle listener");
    int reuse=1;setsockopt(listener,SOL_SOCKET,SO_REUSEADDR,&reuse,sizeof(reuse));
    sockaddr_in local{};local.sin_family=AF_INET;local.sin_port=htons(port);
    inet_pton(AF_INET,"10.35.78.1",&local.sin_addr);
    if(bind(listener,reinterpret_cast<sockaddr*>(&local),sizeof(local))||listen(listener,1))
     throw std::runtime_error("Cannot bind Frame dongle address");
    std::cerr<<"{\"dongle_listener_ready\":true}\n"<<std::flush;
    pollfd waiting{listener,POLLIN,0};
    if(poll(&waiting,1,30000)<=0)throw std::runtime_error("Dongle source did not connect in 30 seconds");
    sockaddr_in peer{};socklen_t length=sizeof(peer);
    source=accept4(listener,reinterpret_cast<sockaddr*>(&peer),&length,SOCK_CLOEXEC);
    in_addr permitted{};inet_pton(AF_INET,"10.35.78.21",&permitted);
    if(source<0||peer.sin_addr.s_addr!=permitted.s_addr)throw std::runtime_error("Unexpected dongle sender");
    int receive_buffer=4*1024*1024;setsockopt(source,SOL_SOCKET,SO_RCVBUF,&receive_buffer,sizeof(receive_buffer));
    timeval timeout{3,0};setsockopt(source,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof(timeout));
   }else{
    source=open(path,O_RDONLY|O_CLOEXEC);if(source<0)throw std::runtime_error("Cannot open clip");
   }
   std::array<uint8_t,32> header{};exact(header.data(),header.size());
   if(memcmp(header.data(),"DKIF",4)||header[4]||header[5]||header[6]!=32||header[7])
    throw std::runtime_error("Expected a version 0, 32-byte lab clip header");
   if(!memcmp(header.data()+8,"H264",4))fourcc=V4L2_PIX_FMT_H264;
   else if(!memcmp(header.data()+8,"HEVC",4))fourcc=V4L2_PIX_FMT_HEVC;
   else if(memcmp(header.data()+8,"AV01",4))throw std::runtime_error("Expected AV01, lab H264 or lab HEVC payloads");
   uint32_t rate=0,scale=0,count=0;
   memcpy(&width,header.data()+12,2);memcpy(&height,header.data()+14,2);
   memcpy(&rate,header.data()+16,4);memcpy(&scale,header.data()+20,4);memcpy(&count,header.data()+24,4);
   if(!width||!height||width>8192||height>8192||(width|height)&1)throw std::runtime_error("Invalid even dimensions");
   if(!scale||(uint64_t(rate)!=uint64_t(scale)*90&&uint64_t(rate)!=uint64_t(scale)*120))
    throw std::runtime_error("This trial requires 90 or 120 fps");
   fps=rate/scale;
   if(count>72000||(live&&count<120))throw std::runtime_error("Invalid declared frame count");
   if(live){
    frames.resize(count);
    reader=std::thread([this,count,max_pending]{
     try{
      for(unsigned i=0;i<count&&!stopped;++i){
       while(i>=consumed.load()+max_pending&&!stopped)std::this_thread::sleep_for(std::chrono::milliseconds(1));
       if(stopped)break;
       frames[i]=record(i);ready.store(i+1);
      }
     }catch(...){failed=true;}
    });
    const auto start=Clock::now();
    while(!ready&&!failed&&Clock::now()-start<std::chrono::seconds(5))std::this_thread::sleep_for(std::chrono::milliseconds(1));
    if(!ready)throw std::runtime_error("First dongle frame unavailable");
   }else{
    while(true){
     uint8_t byte=0;const auto n=read(source,&byte,1);
     if(n==0)break;
     if(n<0)throw std::runtime_error("Cannot read clip");
     if(lseek(source,-1,SEEK_CUR)<0)throw std::runtime_error("Use tcp for live input");
     if(frames.size()>=72000)throw std::runtime_error("Too many frames");
     frames.push_back(record(static_cast<unsigned>(frames.size())));
    }
    if(frames.empty()||(count&&count!=frames.size()))throw std::runtime_error("IVF frame count mismatch");
    ready=static_cast<unsigned>(frames.size());
   }
  }catch(...){cleanup();throw;}
 }
 Clip(const Clip&)=delete;
 ~Clip(){cleanup();}
};
struct Queue {
 int fd;v4l2_buf_type type;unsigned planes,fps;bool streaming=false;
 struct Buffer{std::vector<void*> data;std::vector<unsigned> length;};
 std::vector<Buffer> buffers;
 std::vector<bool> queued;
 Queue(int device,v4l2_buf_type t,unsigned n,unsigned rate=120):fd(device),type(t),planes(n),fps(rate){
  if(!planes||planes>VIDEO_MAX_PLANES)throw std::runtime_error("Invalid plane count");
 }
 void allocate(unsigned count) {
  v4l2_requestbuffers req{};req.type=type;req.memory=V4L2_MEMORY_MMAP;req.count=count;
  xio(fd,VIDIOC_REQBUFS,&req,"REQBUFS");
  if(!req.count||req.count>64)throw std::runtime_error("Invalid buffer count");
  buffers.resize(req.count);queued.resize(req.count,false);
  for(unsigned i=0;i<req.count;++i) {
   v4l2_plane p[VIDEO_MAX_PLANES]{};v4l2_buffer b{};
   b.type=type;b.memory=V4L2_MEMORY_MMAP;b.index=i;b.length=planes;b.m.planes=p;
   xio(fd,VIDIOC_QUERYBUF,&b,"QUERYBUF");
   for(unsigned j=0;j<planes;++j){void* ptr=mmap(nullptr,p[j].length,PROT_READ|PROT_WRITE,MAP_SHARED,fd,p[j].m.mem_offset);
    if(ptr==MAP_FAILED)throw std::runtime_error("mmap failed");
    buffers[i].data.push_back(ptr);buffers[i].length.push_back(p[j].length);}
  }
 }
 void queue(unsigned index,const Frame* frame=nullptr) {
  if(queued.at(index))throw std::runtime_error("Buffer is already queued");
  v4l2_plane p[VIDEO_MAX_PLANES]{};v4l2_buffer b{};
  b.type=type;b.memory=V4L2_MEMORY_MMAP;b.index=index;b.length=planes;b.m.planes=p;
  for(unsigned j=0;j<planes;++j)p[j].length=buffers.at(index).length.at(j);
  if(frame) {
   if(frame->bytes.size()>p[0].length)throw std::runtime_error("Encoded frame exceeds input buffer");
   memcpy(buffers[index].data[0],frame->bytes.data(),frame->bytes.size());p[0].bytesused=frame->bytes.size();
   b.flags=V4L2_BUF_FLAG_TIMESTAMP_COPY;b.timestamp.tv_sec=frame->id/fps;b.timestamp.tv_usec=(frame->id%fps)*1000000/fps;
  }
  xio(fd,VIDIOC_QBUF,&b,"QBUF");
  queued[index]=true;
 }
 void on(){xio(fd,VIDIOC_STREAMON,&type,"STREAMON");streaming=true;}
 bool dequeue(v4l2_buffer& b,v4l2_plane* p) {
  memset(&b,0,sizeof(b));memset(p,0,sizeof(v4l2_plane)*VIDEO_MAX_PLANES);
  b.type=type;b.memory=V4L2_MEMORY_MMAP;b.length=planes;b.m.planes=p;
  int result;do{result=ioctl(fd,VIDIOC_DQBUF,&b);}while(result<0&&errno==EINTR);
  if(result<0&&errno==EAGAIN)return false;
  if(result<0)throw std::runtime_error(std::string("DQBUF: ")+strerror(errno));
  if(b.index>=buffers.size()||!queued[b.index])throw std::runtime_error("Invalid dequeued buffer identity");
  queued[b.index]=false;
  return true;
 }
 ~Queue(){if(streaming)ioctl(fd,VIDIOC_STREAMOFF,&type);
  for(auto& b:buffers)for(unsigned j=0;j<b.data.size();++j)munmap(b.data[j],b.length[j]);}
};

// Iris linear NV12: luma scanlines align to 32, stride to 128; remove
// hardware padding before comparing against a tightly packed golden frame.
std::vector<uint8_t> visible_nv12(const uint8_t* data,size_t bytes,
 const v4l2_pix_format_mplane& format,const v4l2_rect& crop) {
 const size_t stride=format.plane_fmt[0].bytesperline;
 const size_t rows=(size_t(format.height)+31)&~size_t(31);
 if(!stride||format.num_planes!=1||crop.left<0||crop.top<0||
    !crop.width||!crop.height||(crop.left|crop.top|crop.width|crop.height)&1||
    uint64_t(crop.left)+crop.width>format.width||uint64_t(crop.top)+crop.height>format.height||
    uint64_t(crop.left)+crop.width>stride)throw std::runtime_error("Invalid NV12 crop/layout");
 const size_t uv_start=stride*rows;
 const size_t required=uv_start+(size_t(crop.top)/2+crop.height/2-1)*stride+crop.left+crop.width;
 if(required>bytes)throw std::runtime_error("Truncated NV12 capture");
 std::vector<uint8_t> result(size_t(crop.width)*crop.height*3/2);
 size_t offset=0;
 for(unsigned y=0;y<crop.height;++y){
  memcpy(result.data()+offset,data+(size_t(crop.top)+y)*stride+crop.left,crop.width);offset+=crop.width;
 }
 for(unsigned y=0;y<crop.height/2;++y){
  memcpy(result.data()+offset,data+uv_start+(size_t(crop.top)/2+y)*stride+crop.left,crop.width);offset+=crop.width;
 }
 return result;
}

// Optional embedded consumer. The default callback is synchronous. An embedded
// GPU consumer may retain a surface and return its index through released().
struct DecodeHooks {
 int input_fd=-1;
 unsigned max_pending=3;
 std::function<void()> ready;
 std::function<void(uint64_t,const uint8_t*,size_t,const v4l2_pix_format_mplane&,const v4l2_rect&,int,unsigned,unsigned)> frame;
 std::function<bool(unsigned)> retained;
 std::function<std::vector<unsigned>()> released;
};
int run_decoder(int argc,char** argv,const DecodeHooks* hooks=nullptr,std::ostream& report=std::cout) {
 int fd=-1;
 struct CloseDevice {int& fd;~CloseDevice(){if(fd>=0)close(fd);}} close_device{fd};
 try {
  if(argc<3||argc>7)throw std::runtime_error("Use hardware_decode DEVICE CLIP [paced] [GOLDEN.nv12] [ubwc|ubwc10] [repeat=N]; or --inspect CLIP");
  bool paced=false,compressed=false,ten_bit=false;unsigned repeats=1;std::string golden_path;
  for(int i=3;i<argc;++i){
   const std::string option=argv[i];
   if(option=="paced")paced=true;
   else if(option=="ubwc")compressed=true;
   else if(option=="ubwc10"){compressed=true;ten_bit=true;}
   else if(option.rfind("repeat=",0)==0){
    const auto value=option.substr(7);size_t used=0;repeats=std::stoul(value,&used);
    if(used!=value.size()||!repeats||repeats>60)throw std::runtime_error("Use repeat=1..60");
   }
   else if(golden_path.empty())golden_path=option;
   else throw std::runtime_error("Unexpected option");
  }
  if(compressed&&!golden_path.empty())throw std::runtime_error("Golden pixel comparison requires linear NV12 capture");
  Clip clip(argv[2],hooks?hooks->input_fd:-1,hooks?hooks->max_pending:32);
  if(ten_bit&&clip.fourcc!=V4L2_PIX_FMT_HEVC)throw std::runtime_error("10-bit capture is only tested with HEVC");
  const auto width=clip.width,height=clip.height;const auto fps=clip.fps;auto& frames=clip.frames;
  if(repeats>1){
   if(clip.live||frames.size()*repeats>72000)throw std::runtime_error("Repetition requires a bounded local clip");
   const auto original=frames.size();frames.reserve(original*repeats);
   for(unsigned i=1;i<repeats;++i)for(size_t j=0;j<original;++j){
    Frame copy=frames[j];copy.id=frames.size();frames.push_back(std::move(copy));
   }
   clip.ready=static_cast<unsigned>(frames.size());
  }
  if(std::string(argv[1])=="--inspect"){
   std::cout<<"{\"hardware_test_run\":false,\"codec\":\""<<clip.codec()<<"\",\"fps\":"<<fps<<",\"width\":"<<width
    <<",\"height\":"<<height<<",\"frames\":"<<frames.size()<<"}\n";return 0;
  }
  std::vector<uint8_t> first_image,golden_image;
  if(!golden_path.empty()){
   std::ifstream golden(golden_path,std::ios::binary|std::ios::ate);
   const auto expected=size_t(width)*height*3/2;
   if(!golden||golden.tellg()!=static_cast<std::streamoff>(expected))throw std::runtime_error("Invalid golden frame size");
   golden_image.resize(expected);golden.seekg(0);
   golden.read(reinterpret_cast<char*>(golden_image.data()),static_cast<std::streamsize>(expected));
   if(!golden)throw std::runtime_error("Cannot read golden frame");
  }
  fd=open(argv[1],O_RDWR|O_NONBLOCK|O_CLOEXEC);if(fd<0)throw std::runtime_error(std::string("Decoder open failed: ")+strerror(errno));
  v4l2_capability caps{};xio(fd,VIDIOC_QUERYCAP,&caps,"QUERYCAP");
  const auto capabilities=(caps.capabilities&V4L2_CAP_DEVICE_CAPS)?caps.device_caps:caps.capabilities;
  if(!(capabilities&V4L2_CAP_VIDEO_M2M_MPLANE)||!(capabilities&V4L2_CAP_STREAMING))
   throw std::runtime_error("Streaming multi-planar mem-to-mem decoder required");
  v4l2_format out{};out.type=V4L2_BUF_TYPE_VIDEO_OUTPUT_MPLANE;out.fmt.pix_mp.width=width;out.fmt.pix_mp.height=height;
  out.fmt.pix_mp.pixelformat=clip.fourcc;out.fmt.pix_mp.field=V4L2_FIELD_NONE;
  if(clip.live)out.fmt.pix_mp.plane_fmt[0].sizeimage=16000000;
  else for(const auto& frame:frames)out.fmt.pix_mp.plane_fmt[0].sizeimage=std::max(out.fmt.pix_mp.plane_fmt[0].sizeimage,static_cast<uint32_t>(frame.bytes.size()));
  xio(fd,VIDIOC_S_FMT,&out,"compressed S_FMT");
  if(out.fmt.pix_mp.pixelformat!=clip.fourcc||out.fmt.pix_mp.width<width||out.fmt.pix_mp.height<height||
     out.fmt.pix_mp.width>((unsigned(width)+15)&~15U)||out.fmt.pix_mp.height>((unsigned(height)+15)&~15U))
   throw std::runtime_error("Hardware refused requested codec/dimensions; no fallback");
  v4l2_event_subscription sub{};sub.type=V4L2_EVENT_SOURCE_CHANGE;xio(fd,VIDIOC_SUBSCRIBE_EVENT,&sub,"source-change subscription");
  v4l2_format cap{};cap.type=V4L2_BUF_TYPE_VIDEO_CAPTURE_MPLANE;
  Queue input(fd,V4L2_BUF_TYPE_VIDEO_OUTPUT_MPLANE,out.fmt.pix_mp.num_planes,fps);
  input.allocate(8);input.queue(0,&frames[0]);input.on();
  if(clip.live){std::vector<uint8_t>().swap(frames[0].bytes);clip.consumed=1;}
  bool source_changed=false;const auto startup=Clock::now();
  while(std::chrono::duration<double>(Clock::now()-startup).count()<5) {
   pollfd poller{fd,POLLPRI|POLLIN|POLLOUT,0};poll(&poller,1,100);
   v4l2_event event{};
   while(ioctl(fd,VIDIOC_DQEVENT,&event)==0)
    if(event.type==V4L2_EVENT_SOURCE_CHANGE&&(event.u.src_change.changes&V4L2_EVENT_SRC_CH_RESOLUTION))source_changed=true;
   if(source_changed)break;
   std::this_thread::sleep_for(std::chrono::milliseconds(2));
  }
  if(!source_changed)throw std::runtime_error("No hardware source-change response within 5 seconds");
  xio(fd,VIDIOC_G_FMT,&cap,"capture G_FMT after header");
  v4l2_selection selection{};selection.type=V4L2_BUF_TYPE_VIDEO_CAPTURE;selection.target=V4L2_SEL_TGT_COMPOSE;
  xio(fd,VIDIOC_G_SELECTION,&selection,"visible capture selection");
  if(selection.r.width!=width||selection.r.height!=height)throw std::runtime_error("Decoded visible dimensions differ from clip");
  const auto requested_format=ten_bit?V4L2_PIX_FMT_QC10C:compressed?V4L2_PIX_FMT_QC08C:V4L2_PIX_FMT_NV12;
  if(cap.fmt.pix_mp.pixelformat!=requested_format){
   cap.fmt.pix_mp.pixelformat=requested_format;cap.fmt.pix_mp.field=V4L2_FIELD_NONE;
   cap.fmt.pix_mp.width=width;cap.fmt.pix_mp.height=height;
   xio(fd,VIDIOC_S_FMT,&cap,"capture S_FMT after header");
   xio(fd,VIDIOC_G_SELECTION,&selection,"visible capture selection after S_FMT");
  }
  if(cap.fmt.pix_mp.pixelformat!=requested_format||cap.fmt.pix_mp.num_planes!=1||
     selection.r.width!=width||selection.r.height!=height)
   throw std::runtime_error("Requested capture format/exact visible dimensions required; no format fallback");
  v4l2_control minimum{};minimum.id=V4L2_CID_MIN_BUFFERS_FOR_CAPTURE;
  const int minimum_status=ioctl(fd,VIDIOC_G_CTRL,&minimum);
  // The stock Frame H.264 driver omits this read-only control. Request 20
  // buffers and validate the count actually returned by REQBUFS instead.
  if(minimum_status<0){
   if((clip.fourcc!=V4L2_PIX_FMT_H264&&clip.fourcc!=V4L2_PIX_FMT_HEVC)||(errno!=EINVAL&&errno!=ENOTTY))
    throw std::runtime_error(std::string("minimum capture buffers: ")+strerror(errno));
   minimum.value=0;
  }
  if((minimum_status==0&&minimum.value<=0)||minimum.value>60)throw std::runtime_error("Invalid minimum capture count");
  const unsigned requested=std::max(20U,static_cast<unsigned>(minimum.value)+4);
  Queue capture(fd,V4L2_BUF_TYPE_VIDEO_CAPTURE_MPLANE,cap.fmt.pix_mp.num_planes);capture.allocate(requested);
  if(capture.buffers.size()<static_cast<unsigned>(minimum.value))throw std::runtime_error("Insufficient capture buffers");
  for(unsigned i=0;i<capture.buffers.size();++i)capture.queue(i);
  capture.on();
  if(hooks&&hooks->ready)hooks->ready();
  std::cerr<<"{\"hardware_ready\":true,\"codec\":\""<<clip.codec()<<"\",\"fps\":"<<fps
   <<",\"minimum_capture_buffers\":"<<minimum.value<<",\"capture_buffers\":"<<capture.buffers.size()<<"}\n"<<std::flush;
  std::vector<unsigned> free_input;for(unsigned i=1;i<input.buffers.size();++i)free_input.push_back(i);
  std::vector<Clock::time_point> submitted(frames.size());submitted[0]=startup;
  std::vector<double> latency;unsigned next=1,decoded=0,errors=0,zero=0,identity=0,returned=0,late=0;uint64_t last_stamp=0;
  bool draining=false,last=false,golden_checked=false,golden_match=false;
  const auto capture_file=clip.live?std::string("/tmp/steam-frame-av1-lab-20261011/hardware-live")+
   (std::string(argv[2])=="tcp"?"":"-"+std::to_string(clip.port)):std::string(argv[2]);
  std::vector<uint8_t> seen(frames.size(),0);std::ofstream raw;
  if(!hooks){raw.open(capture_file+(ten_bit?".qc10c":compressed?".qc08c":".nv12"),std::ios::binary);
   if(!raw)throw std::runtime_error("Cannot open first-frame capture file");}
  const auto began=Clock::now();auto progress=began;
  std::vector<bool> retained(capture.buffers.size(),false),terminal(capture.buffers.size(),false);unsigned held=0;
  while(!last||returned<frames.size()||held) {
   if(hooks&&hooks->released)for(unsigned index:hooks->released()){
    if(index>=retained.size()||!retained[index])throw std::runtime_error("Invalid released capture identity");
    retained[index]=false;--held;if(!terminal[index])capture.queue(index);progress=Clock::now();
   }
   if(clip.failed)throw std::runtime_error("Dongle input ended/invalid/timed out");
   if(std::chrono::duration<double>(Clock::now()-progress).count()>5)throw std::runtime_error("Hardware decoder stalled for 5 seconds");
   v4l2_buffer b{};v4l2_plane p[VIDEO_MAX_PLANES]{};
   while(input.dequeue(b,p)) {if(b.flags&V4L2_BUF_FLAG_ERROR)++errors;free_input.push_back(b.index);++returned;}
   while(!last&&capture.dequeue(b,p)) {
    if(b.flags&V4L2_BUF_FLAG_ERROR)++errors;
    if(!p[0].bytesused){if(!(b.flags&V4L2_BUF_FLAG_LAST))++zero;}
    else {
     uint64_t stamp=uint64_t(b.timestamp.tv_sec)*1000000+b.timestamp.tv_usec;
     unsigned id=static_cast<unsigned>((stamp*fps+500000)/1000000);
     if(id>=frames.size()||seen[id])++identity;
     else {seen[id]=1;latency.push_back(std::chrono::duration<double,std::milli>(Clock::now()-submitted[id]).count());}
     if(decoded&&stamp<last_stamp)++identity;
     last_stamp=stamp;
     if(decoded==0||(hooks&&hooks->frame)){
      if(p[0].data_offset>p[0].bytesused||p[0].bytesused>capture.buffers[b.index].length[0])
       throw std::runtime_error("Invalid capture payload size");
      const auto* data=static_cast<uint8_t*>(capture.buffers[b.index].data[0])+p[0].data_offset;
      if(hooks&&hooks->frame)hooks->frame(id,data,p[0].bytesused-p[0].data_offset,cap.fmt.pix_mp,selection.r,fd,b.index,p[0].data_offset);
      if(decoded==0){
       if(compressed)first_image.assign(data,data+p[0].bytesused-p[0].data_offset);
       else first_image=visible_nv12(data,p[0].bytesused-p[0].data_offset,cap.fmt.pix_mp,selection.r);
      }
     }
     ++decoded;progress=Clock::now();
    }
    terminal[b.index]=(b.flags&V4L2_BUF_FLAG_LAST)!=0;
    if(terminal[b.index]){if(!draining)throw std::runtime_error("Unexpected decoder LAST before drain");last=true;progress=Clock::now();}
    if(p[0].bytesused&&hooks&&hooks->retained&&hooks->retained(b.index)){retained[b.index]=true;++held;}
    else if(!terminal[b.index])capture.queue(b.index);
   }
   while(next<clip.ready.load()&&!free_input.empty()) {
    const auto due=began+std::chrono::nanoseconds(uint64_t(next)*1000000000/fps);
    if(paced&&Clock::now()<due)break;
    if(paced&&Clock::now()>due+std::chrono::nanoseconds(1000000000/fps))++late;
    unsigned index=free_input.back();free_input.pop_back();submitted[next]=Clock::now();input.queue(index,&frames[next]);
    if(clip.live)std::vector<uint8_t>().swap(frames[next].bytes);
    ++next;clip.consumed=next;
   }
   if(next==frames.size()&&!draining){
    v4l2_decoder_cmd command{};command.cmd=V4L2_DEC_CMD_STOP;
    xio(fd,VIDIOC_DECODER_CMD,&command,"decoder drain STOP");draining=true;
   }
   v4l2_event event{};
   while(ioctl(fd,VIDIOC_DQEVENT,&event)==0)
    if(event.type==V4L2_EVENT_SOURCE_CHANGE)throw std::runtime_error("Unexpected source change during fixed-clip decode");
   pollfd poller{fd,POLLIN|POLLOUT|POLLPRI,0};poll(&poller,1,2);
   std::this_thread::sleep_for(std::chrono::microseconds(200));
  }
  const double elapsed=std::chrono::duration<double>(Clock::now()-began).count();
  if(!hooks){raw.write(reinterpret_cast<const char*>(first_image.data()),static_cast<std::streamsize>(first_image.size()));
   if(!raw)throw std::runtime_error("Cannot save first-frame capture");
   raw.close();}
  std::sort(latency.begin(),latency.end());
  golden_checked=!golden_path.empty();golden_match=golden_checked&&first_image==golden_image;
  const auto missing=std::count(seen.begin(),seen.end(),uint8_t(0));
  const bool functional=decoded==frames.size()&&!missing&&!errors&&!zero&&!identity&&last&&(!golden_checked||golden_match);
  const bool pacing_pass=functional&&paced&&frames.size()>=fps*10&&!late&&elapsed<=double(frames.size())/fps+4.0/fps;
  report<<"{\"hardware\":true,\"codec\":\""<<clip.codec()<<"\",\"fps\":"<<fps<<",\"driver\":\""<<caps.driver<<"\",\"width\":"<<width<<",\"height\":"<<height
   <<",\"capture_width\":"<<cap.fmt.pix_mp.width<<",\"capture_height\":"<<cap.fmt.pix_mp.height<<",\"stride\":"<<cap.fmt.pix_mp.plane_fmt[0].bytesperline
   <<",\"capture_format\":\""<<(ten_bit?"QC10C":compressed?"QC08C":"NV12")<<"\",\"pixel_verification_pass\":"<<(golden_checked&&golden_match?"true":"false")
   <<",\"dongle_live_input\":"<<(clip.live?"true":"false")
   <<",\"frames\":"<<decoded<<",\"errors\":"<<errors<<",\"empty_buffers\":"<<zero<<",\"identity_errors\":"<<identity<<",\"paced\":"<<(paced?"true":"false")
   <<",\"missing_frames\":"<<missing<<",\"drain_complete\":"<<(last?"true":"false")<<",\"input_deadline_misses\":"<<late
   <<",\"golden_checked\":"<<(golden_checked?"true":"false")<<",\"golden_match\":"<<(golden_match?"true":"false")
   <<",\"minimum_capture_buffers\":"<<minimum.value<<",\"capture_buffers\":"<<capture.buffers.size()
   <<",\"functional_pass\":"<<(functional?"true":"false")<<",\"pacing_pass\":"<<(pacing_pass?"true":"false")
   <<",\"120fps_pass\":"<<(fps==120&&pacing_pass?"true":"false")
   <<",\"elapsed_seconds\":"<<elapsed<<",\"p99_ms\":"<<(latency.empty()?0:latency[latency.size()*99/100])<<"}\n";
  // Queues destruct/STREAMOFF before the device descriptor is closed.
  return functional?0:3;
 } catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}
}
#ifndef FRAME_DECODE_EMBEDDED
int main(int argc,char** argv){return run_decoder(argc,argv);}
#endif
