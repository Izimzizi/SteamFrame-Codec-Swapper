# Corresponding source for the experimental Frame decoder

This archive contains the actual transformed source used for the bundled
`steam-frame-av1-lab.ko`, not the full Valve kernel tree. The module is
GPL-2.0-only; see `COPYING`. Its output fingerprint is:

```
c83ea764512488af7e9c3a357d78c21a4f32d4d193c39de01bb075226d440339
```

`provenance.json` records the public Linux Iris source commit
`028ef9c96e96197026887c0f092424679298aae8`, original file hashes and the Linux 6.18
V4L2 mem-to-mem dependency. `frame-port.patch` records the changes to those source
files; the additional Frame integration source/header files are included directly.
The Frame kernel commit is `e66bc2ca6f8ca6baa5260c69c9b79dc01a6238b9`.
Matching Valve repository source requires separate access and is not represented
as part of this public upstream port.

## Build with the matching kernel ABI

Compile on the Frame (aarch64), using its existing development toolchain and the
exact kernel headers package:

```
linux-618-deckard-headers-6.18.0+ge66bc2ca6f8c-1-aarch64.pkg.tar.zst
SHA256: 1be8af9b51c6d8ca33d03228d3c1045a423e2f8cd81989edc1641b38441f8810
```

Obtain that package from the Frame's configured Valve package repository; access
depends on the device's configured repository. Extract it into a separate working
directory. Keep its `.config`, `Module.symvers`, generated headers and build scripts
together. Do not overwrite the installed kernel or headers.

After extracting this source archive into a separate directory, run the following
on the Frame, replacing the two directory paths with your extracted directories:

```sh
make -C /path/to/extracted-headers/usr/lib/modules/6.18.0-ge66bc2ca6f8c/build \
  M=/absolute/path/to/decoder-source -j2 CONFIG_DEBUG_INFO_BTF_MODULES= modules
modinfo /absolute/path/to/decoder-source/steam-frame-av1-lab.ko
```

This builds the out-of-tree module; it does not load or install it. The makefile
includes `v4l2-mem2mem.c` because the tested Frame kernel lacks that module, and
forces the compatibility header. The module refuses to probe by default, has no
automatic modalias and requires the exact kernel release and SM8650 Iris binding.
Its explicit armed mode is handled by the separately authenticated supervisor.
It uses the Frame's existing Qualcomm firmware, which is not in this archive.

The `tools` directory includes source fetch/transformation scripts and the
additional Frame integration source/header inputs they use. These are optional
for reviewing or regenerating the port; the transformed sources at the archive
root can be built directly with the make command above. Use your own paired SSH
connection if transferring sources to the Frame.

A rebuild can differ in binary fingerprint because of compiler, paths or build
metadata. The release helper intentionally accepts only the validated pinned
candidate. Updating that fingerprint requires separate decoder acceptance and
rollback testing; successful compilation alone is insufficient.
