/* SPDX-License-Identifier: GPL-2.0-only */
#ifndef FRAME_FIRMWARE_NO_TZ_H
#define FRAME_FIRMWARE_NO_TZ_H
#include <linux/types.h>
struct iris_core;
bool frame_fw_uses_no_tz(struct iris_core *core);
int frame_fw_no_tz_init(struct iris_core *core);
void frame_fw_no_tz_deinit(struct iris_core *core);
int frame_fw_no_tz_boot(struct iris_core *core, phys_addr_t physical, size_t size);
int frame_fw_no_tz_unload(struct iris_core *core);
int frame_fw_no_tz_state(struct iris_core *core, bool resume);
#endif
