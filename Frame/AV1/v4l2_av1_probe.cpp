// Separate decoder descriptor, no S_FMT, STREAMON, buffers or firmware writes.
#include <linux/videodev2.h>
#include <sys/ioctl.h>
#include <fcntl.h>
#include <unistd.h>
#include <cerrno>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#ifndef V4L2_PIX_FMT_AV1
#define V4L2_PIX_FMT_AV1 v4l2_fourcc('A','V','0','1')
#endif
std::string fourcc(unsigned value) {
    std::string s(4,' ');for(unsigned i=0;i<4;++i)s[i]=char(value>>(i*8));return s;
}
int main(int argc,char** argv) {
    const char* path=argc>1?argv[1]:"/dev/video22";
    int fd=open(path,O_RDWR|O_NONBLOCK|O_CLOEXEC);
    if(fd<0){std::cerr<<"Cannot open decoder: "<<strerror(errno)<<"\n";return 2;}
    v4l2_capability cap{};
    if(ioctl(fd,VIDIOC_QUERYCAP,&cap)){close(fd);return 2;}
    bool listed=false;
    std::cout<<"{\"driver\":\""<<cap.driver<<"\",\"formats\":[";
    for(unsigned i=0;;++i) {
        v4l2_fmtdesc format{};format.type=V4L2_BUF_TYPE_VIDEO_OUTPUT_MPLANE;format.index=i;
        if(ioctl(fd,VIDIOC_ENUM_FMT,&format))break;
        if(i)std::cout<<",";
        std::cout<<"\""<<fourcc(format.pixelformat)<<"\"";
        listed|=format.pixelformat==V4L2_PIX_FMT_AV1;
    }
    std::cout<<"],\"negotiation\":[";bool accepted=listed;unsigned index=0;
    for(auto size:std::vector<std::pair<unsigned,unsigned>>{{512,512},{1920,1080},{1536,6144},{3072,3072}}) {
        v4l2_format f{};f.type=V4L2_BUF_TYPE_VIDEO_OUTPUT_MPLANE;
        f.fmt.pix_mp.width=size.first;f.fmt.pix_mp.height=size.second;
        f.fmt.pix_mp.pixelformat=V4L2_PIX_FMT_AV1;f.fmt.pix_mp.field=V4L2_FIELD_NONE;
        int status=ioctl(fd,VIDIOC_TRY_FMT,&f);
        bool exact=status==0 && f.fmt.pix_mp.pixelformat==V4L2_PIX_FMT_AV1 &&
            f.fmt.pix_mp.width==size.first && f.fmt.pix_mp.height==size.second;
        accepted &= exact;
        if(index++)std::cout<<",";
        std::cout<<"{\"requested_width\":"<<size.first<<",\"requested_height\":"<<size.second
            <<",\"returned\":\""<<fourcc(f.fmt.pix_mp.pixelformat)<<"\",\"width\":"<<f.fmt.pix_mp.width
            <<",\"height\":"<<f.fmt.pix_mp.height<<",\"ioctl_status\":"<<status<<",\"accepted\":"<<(exact?"true":"false")<<"}";
    }
    close(fd);
    std::cout<<"],\"av1_negotiation_passed\":"<<(accepted?"true":"false")
        <<",\"hardware_decode_tested\":false,\"steamvr_integration\":false}\n";
    return accepted?0:3;
}
