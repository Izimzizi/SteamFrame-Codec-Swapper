// SPDX-License-Identifier: GPL-2.0-only
/* Copyright (C) 2017 Linaro Ltd. (public Venus lifecycle reference) */
/* Frame board integration. Public Venus firmware lifecycle is the reference;
 * register offsets/order are verified against this Frame's stock iris.ko.
 * This selects the existing DT video-firmware path, not a firmware replacement.
 */
#include <linux/err.h>
#include <linux/io.h>
#include <linux/iommu.h>
#include <linux/of.h>
#include <linux/of_device.h>
#include <linux/platform_device.h>
#include "iris_core.h"
#include "frame_firmware_no_tz.h"

#define FRAME_CPU_RESET 0xc1000
#define FRAME_CPA_START 0xc1020
#define FRAME_CPA_END 0xc1024
#define FRAME_FW_START 0xc1028
#define FRAME_FW_END 0xc102c
#define FRAME_NONPIX_START 0xc1030
#define FRAME_NONPIX_END 0xc1034
#define FRAME_CPU_CLOCK_CONFIG 0xb0088

bool frame_fw_uses_no_tz(struct iris_core *core)
{
	struct device_node *node = of_get_child_by_name(core->dev->of_node, "video-firmware");
	bool found = node != NULL;
	of_node_put(node);
	return found;
}

int frame_fw_no_tz_init(struct iris_core *core)
{
	struct platform_device_info info = {0};
	struct platform_device *pdev;
	struct iommu_domain *domain;
	struct device_node *node;
	int ret;

	if (core->frame_fw_device)
		return 0;
	if (!of_device_is_compatible(core->dev->of_node, "qcom,sm8650-iris"))
		return -ENODEV;
	node = of_get_child_by_name(core->dev->of_node, "video-firmware");
	if (!node)
		return -ENODEV;
	info.fwnode = &node->fwnode;
	info.parent = core->dev;
	info.name = node->name;
	info.dma_mask = DMA_BIT_MASK(32);
	pdev = platform_device_register_full(&info);
	if (IS_ERR(pdev)) {
		ret = PTR_ERR(pdev);
		goto put_node;
	}
	pdev->dev.of_node = node;
	ret = of_dma_configure(&pdev->dev, node, true);
	if (ret)
		goto unregister;
	domain = iommu_paging_domain_alloc(&pdev->dev);
	if (IS_ERR_OR_NULL(domain)) {
		ret = domain ? PTR_ERR(domain) : -ENOMEM;
		goto unregister;
	}
	ret = iommu_attach_device(domain, &pdev->dev);
	if (ret) {
		iommu_domain_free(domain);
		goto unregister;
	}
	core->frame_fw_device = &pdev->dev;
	core->frame_fw_domain = domain;
	of_node_put(node);
	return 0;
unregister:
	platform_device_unregister(pdev);
put_node:
	of_node_put(node);
	return ret;
}

void frame_fw_no_tz_deinit(struct iris_core *core)
{
	if (!core->frame_fw_device)
		return;
	if (core->frame_fw_mapped) {
		iommu_unmap(core->frame_fw_domain, 0, core->frame_fw_mapped);
		core->frame_fw_mapped = 0;
	}
	iommu_detach_device(core->frame_fw_domain, core->frame_fw_device);
	iommu_domain_free(core->frame_fw_domain);
	core->frame_fw_domain = NULL;
	platform_device_unregister(to_platform_device(core->frame_fw_device));
	core->frame_fw_device = NULL;
}

static void frame_fw_reset_cpu(struct iris_core *core)
{
	u32 size = core->frame_fw_mapped;
	writel(0, core->reg_base + FRAME_FW_START);
	writel(size, core->reg_base + FRAME_FW_END);
	writel(0, core->reg_base + FRAME_CPA_START);
	writel(size, core->reg_base + FRAME_CPA_END);
	writel(size, core->reg_base + FRAME_NONPIX_START);
	writel(size, core->reg_base + FRAME_NONPIX_END);
	writel(0, core->reg_base + FRAME_CPU_RESET);
	writel(0, core->reg_base + FRAME_CPU_CLOCK_CONFIG);
}

int frame_fw_no_tz_boot(struct iris_core *core, phys_addr_t physical, size_t size)
{
	int ret;
	if (!core->frame_fw_device || !size || size > U32_MAX || core->frame_fw_mapped)
		return -EINVAL;
	ret = iommu_map(core->frame_fw_domain, 0, physical, size,
			IOMMU_READ | IOMMU_WRITE | IOMMU_PRIV, GFP_KERNEL);
	if (ret)
		return ret;
	core->frame_fw_mapped = size;
	frame_fw_reset_cpu(core);
	dev_info(core->dev, "Frame video-firmware path mapped and reset\n");
	return 0;
}

int frame_fw_no_tz_unload(struct iris_core *core)
{
	size_t unmapped;
	if (!core->frame_fw_device)
		return 0;
	writel(readl(core->reg_base + FRAME_CPU_RESET) | 1,
	       core->reg_base + FRAME_CPU_RESET);
	if (core->frame_fw_mapped) {
		unmapped = iommu_unmap(core->frame_fw_domain, 0, core->frame_fw_mapped);
		if (unmapped != core->frame_fw_mapped) {
			dev_err(core->dev, "Frame firmware unmap incomplete\n");
			return -EIO;
		}
		core->frame_fw_mapped = 0;
	}
	frame_fw_no_tz_deinit(core);
	return 0;
}

int frame_fw_no_tz_state(struct iris_core *core, bool resume)
{
	if (!core->frame_fw_mapped)
		return -EINVAL;
	if (resume)
		frame_fw_reset_cpu(core);
	else
		writel(1, core->reg_base + FRAME_CPU_RESET);
	return 0;
}
