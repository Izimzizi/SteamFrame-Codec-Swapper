#pragma once
#include <array>
#include <cmath>
#include <stdexcept>

namespace split {
using Transform=std::array<float,12>;
inline void rigid(const float* m){
 for(unsigned r=0;r<3;++r)for(unsigned c=0;c<3;++c){
  float dot=0;for(unsigned k=0;k<3;++k)dot+=m[r*4+k]*m[c*4+k];
  if(!std::isfinite(dot)||std::abs(dot-(r==c?1.f:0.f))>.01f)
   throw std::runtime_error("Capture rotation is not orthonormal");
 }
 const float determinant=m[0]*(m[5]*m[10]-m[6]*m[9])-m[1]*(m[4]*m[10]-m[6]*m[8])+m[2]*(m[4]*m[9]-m[5]*m[8]);
 if(determinant<.99f)throw std::runtime_error("Capture rotation changes handedness");
}
inline Transform compose(const float* a,const float* b){
 Transform result{};
 for(unsigned r=0;r<3;++r)for(unsigned c=0;c<4;++c){
  result[r*4+c]=c==3?a[r*4+3]:0;
  for(unsigned k=0;k<3;++k)result[r*4+c]+=a[r*4+k]*b[k*4+c];
 }
 return result;
}
inline Transform view_from_pose(const float* pose){
 rigid(pose);Transform result{};
 for(unsigned r=0;r<3;++r){
  for(unsigned c=0;c<3;++c)result[r*4+c]=pose[c*4+r];
  for(unsigned k=0;k<3;++k)result[r*4+3]-=result[r*4+k]*pose[k*4+3];
 }
 return result;
}
}
