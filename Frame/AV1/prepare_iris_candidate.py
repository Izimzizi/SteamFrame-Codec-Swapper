"""Create a guarded out-of-tree compile candidate; never installs or loads it.

This is an upstream driver port, NOT the matching Valve source tree. The known
kernel ABI is provided by the exact extracted headers. Board/HFI behavior still
requires comparison with Valve sources and a recovery path before any load.
"""
import argparse,difflib,json,pathlib,re

p=argparse.ArgumentParser()
p.add_argument('upstream',type=pathlib.Path)
p.add_argument('destination',type=pathlib.Path)
p.add_argument('--release',default='6.18.0-ge66bc2ca6f8c',help='Exact kernel release the module will refuse to probe without')
a=p.parse_args()
if not re.fullmatch(r'[0-9A-Za-z.+_-]{1,64}',a.release):raise ValueError('Invalid kernel release')
metadata=json.loads((a.upstream/'provenance.json').read_text())
required={'iris_hfi_gen2_defines.h':'HFI_CODEC_DECODE_AV1','iris_hfi_gen2_command.c':'NUM_COMV_AV1','iris_vpu_buffer.c':'av1','iris_platform_gen2.c':'V4L2_PIX_FMT_AV1'}
for file,token in required.items():
 if token not in (a.upstream/file).read_text():raise RuntimeError('Source does not contain the complete AV1 path: '+file)
a.destination.mkdir(parents=True,exist_ok=True)
compat=pathlib.Path(__file__).with_name('frame_av1_compat.h')
(a.destination/compat.name).write_text(compat.read_text(),newline='\n')
for name in ('frame_firmware_no_tz.c','frame_firmware_no_tz.h'):
 (a.destination/name).write_text(pathlib.Path(__file__).with_name(name).read_text(),newline='\n')
changes=[]
for source in a.upstream.iterdir():
 if source.suffix not in ('.c','.h') and source.name!='Makefile':continue
 before=source.read_text();after=before
 # The object-allocation shorthand was added after this kernel. Preserve its
 # allocation size and zeroing with the existing 6.18 API.
 after=re.sub(r'kzalloc_obj\((\*[A-Za-z_][A-Za-z_0-9]*(?:->[A-Za-z_0-9]+)?)\)',r'kzalloc(sizeof(\1), GFP_KERNEL)',after)
 after=re.sub(r'kzalloc_obj\((struct [A-Za-z_][A-Za-z_0-9]*)\)',r'kzalloc(sizeof(\1), GFP_KERNEL)',after)
 if source.name=='Makefile':
  after=after.replace('qcom-iris','steam-frame-av1-lab').replace('obj-$(CONFIG_VIDEO_QCOM_IRIS)','obj-m')
  if (a.upstream/'v4l2-mem2mem.c').exists():
   after+='\n# Baseline Frame kernel has CONFIG_V4L2_MEM2MEM_DEV disabled.\nsteam-frame-av1-lab-objs += v4l2-mem2mem.o\n'
  after+='\nccflags-y += -include $(src)/frame_av1_compat.h\n'
  after+='steam-frame-av1-lab-objs += frame_firmware_no_tz.o\n'
 if source.name=='iris_core.h':
  marker='struct iris_core {\n'
  if after.count(marker)!=1:raise RuntimeError('Core layout changed; review Frame firmware fields')
  after=after.replace(marker,marker+'\tstruct device *frame_fw_device;\n\tstruct iommu_domain *frame_fw_domain;\n\tsize_t frame_fw_mapped;\n')
 if source.name=='iris_vidc.c':
  # Valve's vrdevice_path helper matches this QUERYCAP card string.
  if '"Iris Decoder"' not in after:raise RuntimeError('Querycap changed; review Frame device discovery')
  after=after.replace('"Iris Decoder"','"iris_decoder"').replace('"Iris Encoder"','"iris_encoder"')
 if source.name=='iris_vdec.c':
  marker='\tinst->fw_min_count = MIN_BUFFERS;'
  if after.count(marker)!=1:raise RuntimeError('Decoder init changed; review frame-rate vote')
  after=after.replace(marker,marker+'\n\tinst->frame_rate = FRAME_AV1_LAB_DECODE_FPS;\n\tinst->operating_rate = FRAME_AV1_LAB_DECODE_FPS;')
  marker='\t\tf->flags = V4L2_FMT_FLAG_COMPRESSED | V4L2_FMT_FLAG_DYN_RESOLUTION;'
  if after.count(marker)!=1:raise RuntimeError('Decoder enumeration changed; review AV1 description')
  after=after.replace(marker,marker+'\n\t\tif (f->pixelformat == V4L2_PIX_FMT_AV1)\n\t\t\tstrscpy(f->description, "AV1 stateful", sizeof(f->description));')
 if source.name=='iris_power.c':
  marker='vote_data->fps = DEFAULT_FPS;'
  if after.count(marker)!=1:raise RuntimeError('Interconnect vote changed; review decoder rate')
  after=after.replace(marker,'vote_data->fps = inst->domain == DECODER ?\n\t\tmax(inst->frame_rate, inst->operating_rate) : DEFAULT_FPS;')
 if source.name=='iris_vpu_common.c':
  marker='u32 fps = DEFAULT_FPS;'
  if after.count(marker)!=1:raise RuntimeError('VPU clock vote changed; review decoder rate')
  after=after.replace(marker,'u32 fps = inst->domain == DECODER ?\n\t\tmax(inst->frame_rate, inst->operating_rate) : DEFAULT_FPS;')
 if source.name=='iris_firmware.c':
  after=after.replace('#include "iris_firmware.h"','#include "iris_firmware.h"\n#include "frame_firmware_no_tz.h"')
  call='ret = qcom_mdt_load(dev, firmware, fw_name,\n\t\t\t    pas_id, mem_virt, mem_phys, res_size, NULL);'
  replacement='''if (frame_fw_uses_no_tz(core)) {
\t\tret = qcom_mdt_load_no_init(dev, firmware, fw_name,
\t\t\t\t\t    mem_virt, mem_phys, res_size, NULL);
\t\tif (!ret)
\t\t\tret = frame_fw_no_tz_boot(core, mem_phys, res_size);
\t} else {
\t\tret = qcom_mdt_load(dev, firmware, fw_name,
\t\t\t\t    pas_id, mem_virt, mem_phys, res_size, NULL);
\t}'''
  if after.count(call)!=1:raise RuntimeError('Firmware loader changed; review Frame path')
  after=after.replace(call,replacement)
  call='\tret = iris_load_fw_to_memory(core, fwpath);'
  after=after.replace(call,'''\tif (frame_fw_uses_no_tz(core)) {
\t\tret = frame_fw_no_tz_init(core);
\t\tif (ret)
\t\t\treturn ret;
\t\tret = iris_load_fw_to_memory(core, fwpath);
\t\tif (ret)
\t\t\tframe_fw_no_tz_deinit(core);
\t\treturn ret;
\t}

'''+call)
  after=after.replace('\treturn qcom_scm_pas_shutdown(core->iris_platform_data->pas_id);',
   '\tif (frame_fw_uses_no_tz(core))\n\t\treturn frame_fw_no_tz_unload(core);\n\treturn qcom_scm_pas_shutdown(core->iris_platform_data->pas_id);')
  after=after.replace('\treturn qcom_scm_set_remote_state(resume, 0);',
   '\tif (frame_fw_uses_no_tz(core))\n\t\treturn frame_fw_no_tz_state(core, resume);\n\treturn qcom_scm_set_remote_state(resume, 0);')
 if source.name in ('iris_platform_vpu3x.c','iris_platform_sm8650.c','iris_platform_gen2.c'):
  # Preserve the Frame's existing image; do not substitute upstream firmware.
  after=after.replace('"qcom/vpu/vpu33_p4.mbn"','"qcom/vpu/vpu33_4v.mbn"')
 if source.name=='iris_probe.c':
  after=after.replace('#include "iris_core.h"','#include "iris_core.h"\n#include "frame_firmware_no_tz.h"')
  cleanup='\tiris_core_deinit(core);\n\n\tvideo_unregister_device(core->vdev_dec);'
  if after.count(cleanup)!=1:raise RuntimeError('Iris remove lifecycle changed; review firmware cleanup')
  after=after.replace(cleanup,'\tiris_core_deinit(core);\n\t/* Error states can skip the normal firmware unload. */\n\tframe_fw_no_tz_deinit(core);\n\n\tvideo_unregister_device(core->vdev_dec);')
  after=after.replace('#include <linux/module.h>','#include <linux/module.h>\n#include <linux/utsname.h>')
  marker='static int iris_probe(struct platform_device *pdev)'
  guards='''static bool arm_av1_lab;
module_param(arm_av1_lab, bool, 0400);
MODULE_PARM_DESC(arm_av1_lab, "Experimental decoder; disabled by default");

'''
  after=after.replace(marker,guards+marker)
  match='\tcore = devm_kzalloc(&pdev->dev, sizeof(*core), GFP_KERNEL);'
  gate='''\t/* Fail closed: no automatic driver replacement or unsupported boards. */
\tif (!arm_av1_lab || strcmp(init_utsname()->release, "RELEASE_PLACEHOLDER") ||
\t    !of_device_is_compatible(dev->of_node, "qcom,sm8650-iris"))
\t\treturn -EPERM;

'''
  if after.count(match)!=1:raise RuntimeError('Upstream probe changed; review guard insertion')
  after=after.replace(match,gate.replace('RELEASE_PLACEHOLDER',a.release)+match)
  after=after.replace('MODULE_DEVICE_TABLE(of, iris_dt_match);','/* No modalias: explicit lab loading only. */')
  after=after.replace('.name = "qcom-iris"','.name = "steam-frame-av1-lab"')
 if source.name=='iris_vpu_common.c':
  after=after.replace('dev_err(core->dev, "power on failed\\n");','dev_err(core->dev, "power on failed: %d\\n", ret);')
 if source.name=='iris_vdec.c':
  busy='\tif (vb2_is_busy(q))\n\t\treturn -EBUSY;'
  if after.count(busy)!=1:raise RuntimeError('Decoder format busy guard changed; review capture compatibility')
  after=after.replace(busy,'''\tif (vb2_is_busy(q)) {
\t\t/* Frame repeats S_FMT after STREAMOFF for an unchanged source-change
\t\t * event, retaining its imported DMA buffers. Accept only that no-op;
\t\t * genuine storage/layout changes still require REQBUFS(0).
\t\t */
\t\tif (inst->codec == V4L2_PIX_FMT_AV1 &&
\t\t    f->type == V4L2_BUF_TYPE_VIDEO_CAPTURE_MPLANE &&
\t\t    !vb2_is_streaming(q) &&
\t\t    f->fmt.pix_mp.width == inst->fmt_dst->fmt.pix_mp.width &&
\t\t    f->fmt.pix_mp.height == inst->fmt_dst->fmt.pix_mp.height &&
\t\t    f->fmt.pix_mp.pixelformat == inst->fmt_dst->fmt.pix_mp.pixelformat) {
\t\t\tmemcpy(f, inst->fmt_dst, sizeof(*f));
\t\t\treturn 0;
\t\t}
\t\treturn -EBUSY;
\t}''')
 (a.destination/source.name).write_text(after,newline='\n')
 if after!=before:changes.extend(difflib.unified_diff(before.splitlines(True),after.splitlines(True),'a/'+source.name,'b/'+source.name))
(a.destination/'frame-port.patch').write_text(''.join(changes),newline='\n')
(a.destination/'provenance.json').write_text(json.dumps({**metadata,'valve_kernel_commit':'e66bc2ca6f8ca6baa5260c69c9b79dc01a6238b9','matching_valve_source':False,'default_probe_enabled':False,'firmware':'qcom/vpu/vpu33_4v.mbn','frame_video_firmware_path':True,'decoder_power_vote_fps':120,'loaded':False},indent=2))
print(json.dumps({'files':len(list(a.destination.glob('*.c'))),'probe_default':'disabled','matching_valve_source':False}))
