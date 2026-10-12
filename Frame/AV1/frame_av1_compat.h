/* SPDX-License-Identifier: GPL-2.0-only */
#ifndef STEAM_FRAME_AV1_COMPAT_H
#define STEAM_FRAME_AV1_COMPAT_H
#include <linux/videodev2.h>
#define FRAME_AV1_LAB_DECODE_FPS 120
/* Stateful AV1 FourCC, upstream Linux 7.0. Absent in the Frame 6.18 ABI. */
#ifndef V4L2_PIX_FMT_AV1
#define V4L2_PIX_FMT_AV1 v4l2_fourcc('A', 'V', '0', '1')
#endif
#endif
