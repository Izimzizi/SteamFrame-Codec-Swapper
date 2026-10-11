# Driver and NVENC compatibility evidence

The driver was decompiled with IDA Professional headless on local copies. The layout
and ABI verification used read-only files; the later live acceptance check is
described below. Detailed decompilation,
database and byte-comparison proof stay in the diagnostic artifacts, outside the
redistributed application.

Reference `driver_vrlink.dll` SHA-256 values (each 13,927,576 bytes):

- Original: `5f6f063f1ece088491f572db8feb2f2433a5621d0adf7013de77b91604d146b5`
- Cap-patched: `82fd637f1d4b844ca349d2a98867cd0ba8f969a14de47c2a3192b875fb62425d`
- Later Valve-signed original: `d415ba30fab129b4737ae19a3c71d7628c27e450dd8bf0002a1d4dd98ed58a05`

These identify the analyzed reference files; the product does not require a listed
whole-file hash. The later original has 1,956 changed bytes in 59 runs elsewhere,
while executable bytes at RVAs `9FE4A0–C315EC` (2,306,380 bytes) match the first
original. Read-only adaptive inspection accepts all three reference files.

Full functions at RVAs `A214E0–A21AD9` (output extraction),
`A21B70–A220C5` (NVENC initialization), `A23FF0–A245D4` (start/submit hook),
and `A24720–A25319` (picture setup) are byte-identical in all three reference variants.
The map function at `A1F170–A20370` also matches.
Their bytes also match the analyzed IDA database. The start hook remains at
`A23FF0`; `A24720` is the separate picture-setup function.

The cap patch's file offset `A21B8D` is RVA `A2278D`, because `.text` has
a `C00` raw-file/virtual-address difference. That preset-copy call is outside
initialization `A21B70–A220C5` (raw file range `A20F70–A214C5`). Similarly,
the reconfiguration level patch at RVA `A23A44` precedes start `A23FF0`.
The constants, tail-call cave and PE header-size patch are outside these four
functions as well. Thus the whole-function byte-equality claims above include
no expected patch deltas; they do not compare raw offsets as if they were RVAs.

The initialization function resolves `NvEncodeAPICreateInstance` from
`nvEncodeAPI64.dll`, initializes function-list version `0x7102000B`, and passes
the table at encoder offset `39520`. The table's encode pointer is at offset
`136` (encoder `39656`), and lock pointer at offset `144` (encoder `39664`).
These are exactly the API 11.1 header layout used by the helper. At runtime the
quality path additionally checks the driver's table version and both stored pointers
against the actual entry points it detoured before accessing private encoder fields.
Telemetry learns sessions independently from the public EncodePicture structure,
after verifying its exact version and the caller's executable-module ownership.

`GetEncodedPacket` at RVA `A214E0` writes lock-structure version `0x7101000B`
at RVA `A2157D`, then calls encoder offset `39664` at RVA `A215B2`, passing
the session handle from encoder offset `520`. The wrapper zero-initializes
`1544` bytes, matching `sizeof(NV_ENC_LOCK_BITSTREAM)` in the bundled header.
The successful result has:

| Field | Byte offset | Driver use |
| --- | ---: | --- |
| `outputBitstream` | 8 | Ring output-buffer handle |
| `frameIdx` | 24 | Copied into the returned frame identifier |
| `bitstreamSizeInBytes` | 36 | Actual encoded packet size |
| `outputTimeStamp` | 40 | NVENC output timestamp metadata |
| `bitstreamBufferPtr` | 56 | Driver copies encoded payload; helper never reads it |

The header compiles to API version `0x0100000B`, lock version `0x7101000B`,
function-list version `0x7102000B`, and the offsets above. Static assertions
enforce these values in the helper.

At startup the helper parses a bounded x64 PE and requires the NVENC loader markers.
The loaded module's executable sections are validated with `VirtualQuery`, including
exact allocation ownership. Only return addresses inside those sections authorize
telemetry session learning. Three NVENC functions are hooked for telemetry;
the private Start function is hooked only if all five quality fingerprints match.
`InspectDriver` reads a file without loading it or installing hooks. Its success
means potential telemetry support; live ABI checks determine actual support.

Each quality fingerprint combines a 64-byte masked prefix with SHA-256 of the
complete normalized function. The generator used instruction-boundary disassembly
to mask direct relative calls, external relative tail jumps and RIP-relative
memory displacements, plus actual PE DIR64 relocations. Internal branches, opcodes
and object-field offsets are preserved. It does not scan raw opcode bytes to decide
what to mask. Every function must match exactly once across executable sections;
the matching Start address is derived from its location. Moved code and changed
relative addresses can match automatically. Changed private layouts or ambiguous
matches do not authorize quality adjustment. The status exposes `quality_supported`
and `rejected_api_calls`; SetQuality returns `1012` for unsupported activation.
RIP-relative displacement width is explicitly four bytes, including `66`-prefixed
COMISD instructions for which the installed disassembler reports an incorrect
two-byte width. Their instruction boundaries and disp32 encoding were checked
against the saved IDA assembly.

Normalized SHA-256 fingerprints:

| Reference function RVA | Length | Normalized SHA-256 |
| --- | ---: | --- |
| `A23FF0` | 1508 | `21055b587636a8a1b3d461d7018eec78210074b65c77cde29d281104c048d310` |
| `A214E0` | 1529 | `cc0fa6f42179375ac91b7e7da3229e1f5bb13f45d277c84a07de5ecb30d0d4c2` |
| `A21B70` | 1365 | `ab9db12529b030be32d2dce4d05394dff08ac4245ea3df3fefee2fd10fa3dd2e` |
| `A24720` | 3065 | `1d636c29b52313fdfe2f9fdc99e664b083308e5317b2428c2ceb77df459f5a7b` |
| `A1F170` | 4608 | `1ea4abfded3198e0b6ae3a52d15493ad99c97d5791d9ab56ea9bd179f757967e` |

Picture setup assigns `inputFrameIdx` from the submitted descriptor. The start
function then copies that frame identifier into `inputTimeStamp`, replacing the
earlier clock value. Because the output identifiers are related, deduplication
does not assume that the pair is always unique. Instead the helper records each
observed Valve submission's output-buffer handle and a local submission generation.
Only its first successful lock adds the reported encoded bytes. A new submission
to a reused buffer is counted even if caller frame identifiers repeat. A repeated
lock without a new submission is ignored. Destroyed sessions discard their buffer
ledger; Start resets the counters and snapshot generation. Frames already pending
before the helper starts are excluded until a submission has been observed.
An unexpected submission error invalidates its pending generation, so a failed
reuse cannot cause a stale previous output to be counted a second time.

Four isolated native checks pass: quality-map preservation, absent-driver startup
guard, metadata-only counter behavior (unrelated/unlearned sessions, repeated
locks, repeated frame IDs after a new submission, lock errors and session cleanup),
and adaptive discovery. Discovery checks relocated call addresses and moved code,
rejects changed object-field offsets beyond the short prefix and duplicate matches,
and validates executable caller bounds and malformed PE rejection.
The counter test uses an invalid payload pointer to verify that no payload read is
needed. Wrong public API versions are rejected and counted. The isolated C# harness
also passes x64 exports, malformed helper rejection, offset-range guards and read-only
idle polling; it checks absent-server Start only when SteamVR is absent.

The coordinated live acceptance check used the later unpatched reference driver and
helper SHA-256 `d45480bc2602a0934534fe8dc07c39fd92121a14a1d0203d66e0168d9fff2d85`.
Start returned zero and discovered quality support. Eight one-second samples measured
approximately 132–158 Mbps of HEVC output at 120 encoded frames per second, with zero
encoding errors and zero rejected API calls. Stop returned zero. The smoke harness
did not change bitrate or quality settings. This confirms actual output counting for
that running scene; it does not establish a minimum bitrate or universal future ABI
compatibility. Other encoder output sizes are unavailable here.

The initial remote-load error `126` was a cache visibility problem. A local file
handle's final path proved that Codex redirected logical LocalAppData into its
package's `LocalCache/Local`; the unpackaged SteamVR process could not see the logical
path. Loading the same helper from an app-adjacent cache visible to both processes
resolved the error. Steam overlay loader hooks were left intact. Detailed path and
loader evidence remain in the diagnostic artifacts and are not bundled.
