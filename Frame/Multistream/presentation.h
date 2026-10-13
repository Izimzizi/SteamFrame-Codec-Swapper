#pragma once
#define GL_GLEXT_PROTOTYPES
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <openvr.h>
#include <libdrm/drm_fourcc.h>
#include <fstream>
#include <string>
#include "protocol.h"
#include "frame_pairer.h"
namespace split {
class Presentation {
 EGLDisplay display_=EGL_NO_DISPLAY;EGLContext context_=EGL_NO_CONTEXT;EGLSurface surface_=EGL_NO_SURFACE;
 GLuint program_=0,vao_=0,fbo_=0,rgba_=0,y_=0,uv_=0;
 std::array<GLuint,3> output_textures_{},output_fbos_{};unsigned output_index_=0;
 Session session_;unsigned width_,height_;bool vr_initialized_=false;
 vr::VROverlayHandle_t overlay_=vr::k_ulOverlayHandleInvalid;
 std::array<vr::VROverlayHandle_t,2> projection_overlays_{};
 struct Imported {GLuint y=0,uv=0;EGLImageKHR y_image=EGL_NO_IMAGE_KHR,uv_image=EGL_NO_IMAGE_KHR;};
 std::map<uint64_t,Imported> imported_;
 PFNEGLCREATEIMAGEKHRPROC create_image_=nullptr;
 PFNEGLDESTROYIMAGEKHRPROC destroy_image_=nullptr;
 using BindImage=void(*)(GLenum,void*);
 BindImage bind_image_=nullptr;
 Imported& import(const Surface& surface,unsigned stream){
  const uint64_t key=(uint64_t(stream)<<32)|unsigned(surface.fd);
  auto found=imported_.find(key);if(found!=imported_.end())return found->second;
  if(!create_image_||!destroy_image_||!bind_image_)throw std::runtime_error("DMA-buffer texture import unavailable");
  auto& images=imported_[key];
  auto plane=[&](bool chroma,GLuint& texture,EGLImageKHR& image){
   EGLint attributes[]={EGL_WIDTH,EGLint(session_.width/(chroma?2:1)),EGL_HEIGHT,EGLint(session_.height/(chroma?2:1)),
    EGL_LINUX_DRM_FOURCC_EXT,EGLint(chroma?DRM_FORMAT_GR88:DRM_FORMAT_R8),EGL_DMA_BUF_PLANE0_FD_EXT,surface.fd,
    EGL_DMA_BUF_PLANE0_OFFSET_EXT,EGLint(chroma?surface.uv_offset:0),EGL_DMA_BUF_PLANE0_PITCH_EXT,EGLint(surface.stride),EGL_NONE};
   image=create_image_(display_,EGL_NO_CONTEXT,EGL_LINUX_DMA_BUF_EXT,nullptr,attributes);
   if(image==EGL_NO_IMAGE_KHR)throw std::runtime_error("DMA-buffer plane import failed: "+std::to_string(eglGetError()));
   glGenTextures(1,&texture);glBindTexture(GL_TEXTURE_2D,texture);bind_image_(GL_TEXTURE_2D,image);
   glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);
   glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_S,GL_CLAMP_TO_EDGE);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_T,GL_CLAMP_TO_EDGE);
   if(glGetError()!=GL_NO_ERROR)throw std::runtime_error("DMA-buffer texture binding failed");
  };
  plane(false,images.y,images.y_image);plane(true,images.uv,images.uv_image);return images;
 }
 static GLuint shader(GLenum type,const char* source){
  GLuint result=glCreateShader(type);glShaderSource(result,1,&source,nullptr);glCompileShader(result);
  GLint valid=0;glGetShaderiv(result,GL_COMPILE_STATUS,&valid);
  if(!valid){char log[2048]{};glGetShaderInfoLog(result,sizeof(log),nullptr,log);glDeleteShader(result);throw std::runtime_error(log);}return result;
 }
 static void overlay_ok(vr::EVROverlayError code){if(code!=vr::VROverlayError_None)throw std::runtime_error("Overlay error "+std::to_string(code));}
public:
 explicit Presentation(Session session,bool visible):session_(session),width_(session.width*session.streams),height_(session.height){
  display_=eglGetPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA,EGL_DEFAULT_DISPLAY,nullptr);
  if(display_==EGL_NO_DISPLAY||!eglInitialize(display_,nullptr,nullptr)||!eglBindAPI(EGL_OPENGL_API))throw std::runtime_error("EGL OpenGL unavailable");
  EGLint attributes[]={EGL_SURFACE_TYPE,EGL_PBUFFER_BIT,EGL_RENDERABLE_TYPE,EGL_OPENGL_BIT,EGL_RED_SIZE,8,EGL_GREEN_SIZE,8,EGL_BLUE_SIZE,8,EGL_NONE};
  EGLConfig config{};EGLint count=0;if(!eglChooseConfig(display_,attributes,&config,1,&count)||count!=1)throw std::runtime_error("EGL config unavailable");
  EGLint context_attributes[]={EGL_CONTEXT_MAJOR_VERSION,3,EGL_CONTEXT_MINOR_VERSION,3,EGL_NONE};
  context_=eglCreateContext(display_,config,EGL_NO_CONTEXT,context_attributes);
  EGLint size[]={EGL_WIDTH,1,EGL_HEIGHT,1,EGL_NONE};surface_=eglCreatePbufferSurface(display_,config,size);
  if(context_==EGL_NO_CONTEXT||surface_==EGL_NO_SURFACE||!eglMakeCurrent(display_,surface_,surface_,context_))throw std::runtime_error("EGL context failed");
  create_image_=reinterpret_cast<PFNEGLCREATEIMAGEKHRPROC>(eglGetProcAddress("eglCreateImageKHR"));
  destroy_image_=reinterpret_cast<PFNEGLDESTROYIMAGEKHRPROC>(eglGetProcAddress("eglDestroyImageKHR"));
  bind_image_=reinterpret_cast<BindImage>(eglGetProcAddress("glEGLImageTargetTexture2DOES"));
  const char* renderer=reinterpret_cast<const char*>(glGetString(GL_RENDERER));
  if(!renderer||std::string(renderer).find("llvmpipe")!=std::string::npos)throw std::runtime_error("Hardware graphics renderer required");
  const char* vs=R"(#version 330 core
out vec2 uv;void main(){uv=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(uv*2-1,0,1);})";
  const char* fs=R"(#version 330 core
in vec2 uv;out vec4 color;uniform sampler2D luma;uniform sampler2D chroma;
void main(){float y=(texture(luma,uv).r-16./255.)*(255./219.);vec2 c=(texture(chroma,uv).rg-128./255.)*(255./224.);
color=vec4(y+1.5748*c.y,y-.187324*c.x-.468124*c.y,y+1.8556*c.x,1);})";
  GLuint v=shader(GL_VERTEX_SHADER,vs),f=shader(GL_FRAGMENT_SHADER,fs);program_=glCreateProgram();
  glAttachShader(program_,v);glAttachShader(program_,f);glLinkProgram(program_);glDeleteShader(v);glDeleteShader(f);
  GLint valid=0;glGetProgramiv(program_,GL_LINK_STATUS,&valid);if(!valid)throw std::runtime_error("NV12 shader link failed");
  glGenVertexArrays(1,&vao_);glBindVertexArray(vao_);glUseProgram(program_);
  glUniform1i(glGetUniformLocation(program_,"luma"),0);glUniform1i(glGetUniformLocation(program_,"chroma"),1);
  auto texture=[](GLuint& id,GLint format,unsigned w,unsigned h,GLenum channels){
   glGenTextures(1,&id);glBindTexture(GL_TEXTURE_2D,id);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);
   glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_S,GL_CLAMP_TO_EDGE);
   glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_T,GL_CLAMP_TO_EDGE);glTexImage2D(GL_TEXTURE_2D,0,format,w,h,0,channels,GL_UNSIGNED_BYTE,nullptr);
  };
  texture(y_,GL_R8,session.width,session.height,GL_RED);texture(uv_,GL_RG8,session.width/2,session.height/2,GL_RG);
  glGenFramebuffers(3,output_fbos_.data());
  for(unsigned i=0;i<3;++i){texture(output_textures_[i],GL_RGBA8,width_,height_,GL_RGBA);
   glBindFramebuffer(GL_FRAMEBUFFER,output_fbos_[i]);glFramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,output_textures_[i],0);
   if(glCheckFramebufferStatus(GL_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE)throw std::runtime_error("Composition framebuffer incomplete");}
  if(visible){
   vr::EVRInitError error=vr::VRInitError_None;vr::VR_Init(&error,vr::VRApplication_Overlay);
   if(error!=vr::VRInitError_None)throw std::runtime_error("OpenVR initialization error "+std::to_string(error));
   vr_initialized_=true;
   if(session.game()){
    for(unsigned eye=0;eye<2;++eye){auto& handle=projection_overlays_[eye];
     overlay_ok(vr::VROverlay()->CreateOverlay(("steamframe.multistream.game."+std::to_string(eye)).c_str(),"SteamVR multistream game",&handle));
     overlay_ok(vr::VROverlay()->SetOverlaySortOrder(handle,100));
     vr::VRTextureBounds_t bounds{eye*.5f,1,(eye+1)*.5f,0};overlay_ok(vr::VROverlay()->SetOverlayTextureBounds(handle,&bounds));
    }
   }else{
   overlay_ok(vr::VROverlay()->CreateOverlay("steamframe.multistream.prototype","H.264 multistream prototype",&overlay_));
   overlay_ok(vr::VROverlay()->SetOverlayWidthInMeters(overlay_,3.0f));
   vr::HmdMatrix34_t transform={{{1,0,0,0},{0,1,0,0},{0,0,1,-2}}};
   overlay_ok(vr::VROverlay()->SetOverlayTransformTrackedDeviceRelative(overlay_,vr::k_unTrackedDeviceIndex_Hmd,&transform));
   overlay_ok(vr::VROverlay()->SetOverlayFlag(overlay_,vr::VROverlayFlags_SideBySide_Parallel,true));
   vr::VRTextureBounds_t bounds{0,1,1,0};overlay_ok(vr::VROverlay()->SetOverlayTextureBounds(overlay_,&bounds));
   overlay_ok(vr::VROverlay()->ShowOverlay(overlay_));
   }
  }
 }
 void render(const Matched& group){
  // Never draw into the currently displayed overlay texture. At least two
  // other complete submissions separate reuse of an output surface.
  output_index_=(output_index_+1)%output_textures_.size();
  fbo_=output_fbos_[output_index_];rgba_=output_textures_[output_index_];
  glBindFramebuffer(GL_FRAMEBUFFER,fbo_);glUseProgram(program_);glBindVertexArray(vao_);
  for(unsigned i=0;i<session_.streams;++i){
   const auto& image=group.images[i];const size_t plane=size_t(session_.width)*session_.height;
   if(image.id!=group.id)throw std::runtime_error("Mismatched presentation image");
   glViewport(i*session_.width,0,session_.width,session_.height);
   if(image.surface.fd>=0){
    auto& textures=import(image.surface,i);glActiveTexture(GL_TEXTURE0);glBindTexture(GL_TEXTURE_2D,textures.y);
    glActiveTexture(GL_TEXTURE1);glBindTexture(GL_TEXTURE_2D,textures.uv);
   }else{
    if(image.nv12.size()!=plane*3/2)throw std::runtime_error("Invalid NV12 presentation size");
    glActiveTexture(GL_TEXTURE0);glBindTexture(GL_TEXTURE_2D,y_);
    glTexSubImage2D(GL_TEXTURE_2D,0,0,0,session_.width,session_.height,GL_RED,GL_UNSIGNED_BYTE,image.nv12.data());
    glActiveTexture(GL_TEXTURE1);glBindTexture(GL_TEXTURE_2D,uv_);
    glTexSubImage2D(GL_TEXTURE_2D,0,0,0,session_.width/2,session_.height/2,GL_RG,GL_UNSIGNED_BYTE,image.nv12.data()+plane);
   }
   glDrawArrays(GL_TRIANGLES,0,3);
  }
  glFinish();if(glGetError()!=GL_NO_ERROR)throw std::runtime_error("GPU composition failed");
 }
 void present(const GameFrame* game=nullptr){
  if(game&&vr_initialized_){
   game->validate();vr::Texture_t texture{reinterpret_cast<void*>(uintptr_t(rgba_)),vr::TextureType_OpenGL,vr::ColorSpace_Gamma};
   for(unsigned eye=0;eye<2;++eye){
    vr::HmdMatrix34_t view{};
    auto eye_to_head=vr::VRSystem()->GetEyeToHeadTransform(static_cast<vr::EVREye>(eye));
    const auto eye_pose=compose(game->pose,&eye_to_head.m[0][0]);
    // Projection overlays consume tracking-origin-to-eye (the view matrix),
    // unlike absolute quad overlays, which consume an object pose. Passing
    // the eye pose here reverses the apparent response to head rotation.
    const auto world_to_eye=view_from_pose(eye_pose.data());memcpy(view.m,world_to_eye.data(),sizeof(view.m));
    const auto* p=game->projection+eye*4;vr::VROverlayProjection_t projection{p[0],p[1],p[2],p[3]};
    auto handle=projection_overlays_[eye];
    overlay_ok(vr::VROverlay()->SetOverlayTransformProjection(handle,static_cast<vr::ETrackingUniverseOrigin>(game->origin),&view,&projection,static_cast<vr::EVREye>(eye)));
    overlay_ok(vr::VROverlay()->SetOverlayTexture(handle,&texture));overlay_ok(vr::VROverlay()->ShowOverlay(handle));
   }
  }
  if(overlay_!=vr::k_ulOverlayHandleInvalid){vr::Texture_t texture{reinterpret_cast<void*>(uintptr_t(rgba_)),vr::TextureType_OpenGL,vr::ColorSpace_Gamma};overlay_ok(vr::VROverlay()->SetOverlayTexture(overlay_,&texture));}
 }
 void capture(const std::string& path){
  std::vector<uint8_t> pixels(size_t(width_)*height_*4);glReadPixels(0,0,width_,height_,GL_RGBA,GL_UNSIGNED_BYTE,pixels.data());
  if(glGetError()!=GL_NO_ERROR)throw std::runtime_error("GPU capture failed");
  std::ofstream file(path,std::ios::binary);file<<"P6\n"<<width_<<' '<<height_<<"\n255\n";
  for(size_t i=0;i<pixels.size();i+=4)file.write(reinterpret_cast<char*>(pixels.data()+i),3);
  if(!file)throw std::runtime_error("Cannot save GPU capture");
 }
 ~Presentation(){
  for(auto handle:projection_overlays_)if(handle)vr::VROverlay()->DestroyOverlay(handle);
  if(overlay_!=vr::k_ulOverlayHandleInvalid)vr::VROverlay()->DestroyOverlay(overlay_);
  if(vr_initialized_)vr::VR_Shutdown();
  if(context_!=EGL_NO_CONTEXT){
   for(auto& entry:imported_){auto& v=entry.second;glDeleteTextures(1,&v.y);glDeleteTextures(1,&v.uv);
    if(v.y_image!=EGL_NO_IMAGE_KHR)destroy_image_(display_,v.y_image);
    if(v.uv_image!=EGL_NO_IMAGE_KHR)destroy_image_(display_,v.uv_image);}
   glDeleteFramebuffers(3,output_fbos_.data());glDeleteTextures(3,output_textures_.data());glDeleteTextures(1,&y_);glDeleteTextures(1,&uv_);glDeleteProgram(program_);glDeleteVertexArrays(1,&vao_);eglMakeCurrent(display_,EGL_NO_SURFACE,EGL_NO_SURFACE,EGL_NO_CONTEXT);eglDestroyContext(display_,context_);}
  if(surface_!=EGL_NO_SURFACE)eglDestroySurface(display_,surface_);
  if(display_!=EGL_NO_DISPLAY)eglTerminate(display_);
 }
};
}
