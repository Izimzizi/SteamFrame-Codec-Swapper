// SPDX-License-Identifier: GPL-2.0-only
// One-shot recovery of the exact unbound child left by an earlier lab module.
#include <linux/module.h>
#include <linux/utsname.h>
#include <linux/platform_device.h>
#include <linux/of.h>
#include <linux/of_reserved_mem.h>
#include <linux/iommu.h>
#include <linux/sizes.h>
static bool armed;
module_param(armed,bool,0400);
static int __init recover(void)
{
 struct device *video,*firmware;
 struct device_node *memory;
 struct reserved_mem *reserved;
 struct iommu_domain *domain;
 phys_addr_t physical;
 int ret=-EPERM;
 if(!armed||strcmp(init_utsname()->release,"6.18.0-ge66bc2ca6f8c"))return -EPERM;
 video=bus_find_device_by_name(&platform_bus_type,NULL,"aa00000.video-codec");
 if(!video)return -ENODEV;
 if(video->driver||!of_device_is_compatible(video->of_node,"qcom,sm8650-iris"))goto video_out;
 firmware=bus_find_device_by_name(&platform_bus_type,NULL,"video-firmware.0");
 if(!firmware){ret=-ENODEV;goto video_out;}
 if(firmware->parent!=video||firmware->driver||!firmware->of_node||
    strcmp(firmware->of_node->name,"video-firmware"))goto firmware_out;
 memory=of_parse_phandle(video->of_node,"memory-region",0);
 reserved=memory?of_reserved_mem_lookup(memory):NULL;
 of_node_put(memory);
 if(!reserved||reserved->size!=SZ_8M)goto firmware_out;
 domain=iommu_get_domain_for_dev(firmware);
 if(!domain||domain->type!=IOMMU_DOMAIN_UNMANAGED)goto firmware_out;
 physical=iommu_iova_to_phys(domain,0);
 if(physical&&physical!=reserved->base)goto firmware_out;
 iommu_unmap(domain,0,reserved->size);
 iommu_detach_device(domain,firmware);
 iommu_domain_free(domain);
 platform_device_unregister(to_platform_device(firmware));
 pr_info("Frame lab orphan video-firmware child released; no active driver was changed\n");
 ret=0;
firmware_out:
 put_device(firmware);
video_out:
 put_device(video);
 return ret;
}
static void __exit done(void){}
module_init(recover);module_exit(done);
MODULE_LICENSE("GPL");
MODULE_DESCRIPTION("Guarded one-shot Frame AV1 lab orphan firmware recovery");
