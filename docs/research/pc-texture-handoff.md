# PC shared-texture handoff and upstream frame stalls

This is an original description of the installed Windows SteamVR path, traced
from local binaries and matched to the October 13, 2026 test logs. Proprietary
code and binaries remain in ignored private research artifacts. Addresses below
are preferred virtual addresses, before ASLR. A verified control path does not
establish which operation caused an individual live stall.

## Build scope

| Binary | SHA-256 |
| --- | --- |
| `driver_vrlink.dll` | `34265101d56235e2f87e5b0e2f3fc2340ed1513a9b6d26f03c4fe12bafcbbc03` |
| `vrcompositor.exe` | `fad65f2f90138448ee6c9fb6b7bdeae72877080641a987fa0c28a3e9a80d13b7` |
| `vrclient_x64.dll` | `8adb57bc4bcdb5f2bce968493ef329a74ba7ba1222a6f7ae9eaca223c7598714` |
| `vrserver.exe` | `fbba0776f31dead1a36fcaa3087ff58cefe7e145ba4fdb85dab673514460cc00` |

The selected compositor, client and server functions were compared byte for byte
with their installed files. The selected driver ownership, Present, PostPresent,
encoder submission and worker functions also match. The existing driver database
has two differing operand bytes at `0x180A2278E/F` in initialization function
`0x180A22570`; its later asynchronous-worker creation branch matches, but that
whole initialization function is not claimed byte-identical. Local provenance
reports preserve this distinction. The reviewed compositor log identifies
SteamVR 2.18.3.

## A mutex and two events do different jobs

The direct-mode path uses DXGI keyed-mutex key **0** for both acquisition and
release, on both the compositor and driver devices. This supplies mutual
exclusion; it does not encode an alternating producer/consumer turn number. When
several devices wait on the same key, the API does not promise their wake order.
A successful acquisition must be established by `S_OK`, rather than merely a
nonnegative HRESULT. `WAIT_TIMEOUT` and `WAIT_ABANDONED` are nonnegative special
results; abandoned surfaces require recreation. These are API requirements from
[Microsoft's AcquireSync documentation](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgikeyedmutex-acquiresync).

The installed sequence is:

1. Compositor render thread acquires the sync texture, with key 0 and a **10 ms**
   timeout. It performs the frame's graphics work.
2. `CGraphicsDevice::DoDriverDirectPresent` (`vrcompositor!0x14003FF80`) copies
   small texture regions into the sync resource, flushes through its renderer,
   and calls the private direct interface's Present method. The resource is the
   app's current shared sync texture, or a compositor-created 1×1 fallback with
   `D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX` when no app texture is available.
3. Client bridge `vrclient!0x1800AC730` stores the shared handle and frame flags,
   clears `DriverDirect_Acquired` and `DriverDirect_Presented`, and signals
   `DriverDirect_NewFrame`. This is queued interprocess handoff, not the driver
   function running synchronously on the compositor thread.
4. Server `vrserver!0x140025360`, on `SubmitFrameThread`, consumes that event and
   shared state. It calls SubmitLayer for the available layers, then the driver's
   Present method with the shared handle.
5. Driver `0x1809ED800` serializes its Present processing with its own CPU mutex,
   calls `0x1809F5700` to acquire the DXGI mutex with key 0 and a **100 ms** timeout,
   and composes the layers into its internal stream texture. This includes D3D
   Map/Unmap and draw work. Its end-of-Present helper `0x1809F7CF0` clears render
   bindings/state; it does not release the keyed mutex.
6. After Present returns, the server signals `DriverDirect_Acquired`, then calls
   driver PostPresent (`0x1809ECD40`). PostPresent first calls `0x1809F7FB0`, which
   obtains the immediate context, calls D3D11 `Flush`, and releases key 0. It then
   runs frame admission, input-copy/encoder submission and driver frame pacing.
7. The server updates next swap indices and timing, then signals
   `DriverDirect_Presented`. The compositor's corresponding waits are 5 ms for
   Acquired (`vrclient!0x1800AC8A0`) and 100 ms for Presented
   (`vrclient!0x1800AC8B0`).

The compositor releases its own keyed-mutex reference after the rendering and
Present path and before its Acquired/Presented waits. The driver can already be
waiting for that release because the NewFrame signal precedes it. A slow
compositor operation in this interval can therefore hold up the driver before
encoding has begun. The Acquired event is emitted **after the whole driver
Present method returns**, not at the moment AcquireSync succeeds. Presented
means the server callback sequence ended; it does not mean the Frame displayed
that image.

The exact D3D11 slots were checked against the installed Windows SDK header:
Map 112, Unmap 120, GetData 232, CopySubresourceRegion 368, CopyResource 376,
ClearState 880 and Flush 888 bytes into the interface vtable. Flush submits
commands asynchronously; its return is not a measurement of completed GPU work.
See [Microsoft's Flush documentation](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-flush).

## Proven failure handling defect

Both active acquisition implementations explicitly recognize a nonzero failure,
log it, and then return without making the failure actionable by their caller:

| Stage | Acquisition | Retained state after failure | Later action |
| --- | --- | --- | --- |
| Driver | `0x1809F5700`, 100 ms | Keyed-mutex COM interface at graphics object +2936 remains nonnull. | Present continues graphics work. `0x1809F7FB0` flushes, then calls ReleaseSync solely because this pointer exists. |
| Compositor | `0x1400388E0`, 10 ms | Keyed-mutex interface at graphics object +147232 remains nonnull. | `0x140117C00` continues rendering. `0x140067C40` later calls ReleaseSync solely because the pointer exists. |

An interface reference is not proof that the device acquired ownership. The
observed later `0x887A0001` is `DXGI_ERROR_INVALID_CALL`; its presence is consistent
with this failed-acquire/unowned-release path. It is not a second independent
radio fault. The mapping of this code is in [Microsoft's DXGI error reference](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-error).

Correct handling needs an explicit acquired state, propagation of failure to the
protected frame operation, and release only when owned. Clearing the pointer
alone prevents the invalid release while still allowing unsafe protected work.
Suppressing the warning, increasing a timeout or removing mutex calls does not
supply that missing ownership. A failed frame must preserve the server's event
and swap bookkeeping without pretending that fresh pixels were produced; that
requires a coordinated caller fix rather than changing one branch to report
success. An abandoned surface additionally needs a real shared-resource reset.
No such live modification has been applied in this investigation.

A separate `CNewFrameHandler::AcquireSync` (`0x140092E70`) checks only whether the
HRESULT is negative, so it also misclassifies the positive timeout/abandoned
results. That is a different asynchronous compositor path. `0x140115C10` selects
the ordinary `0x140117C00` loop when the direct interface is present; the reviewed
logs explicitly identify driver direct mode. The separate path's GPU-fence
environment options are not established controls for this active Frame path.

## What the capture proves, and what still needs attribution

The reviewed session started at **21:56:49**, compositor PID **49816**. Its
Acquired-event warnings were present throughout otherwise roughly 90 Hz
streaming: the 30-second suppression counts were 2676 at 21:57:20, 2700 at
21:57:50 and 2689 at 21:58:20. These generic `WaitForAcquire timed out` messages
must not be counted as equivalent to an actual `AcquireSync FAILED`.

The same session logged driver AcquireSync timeout and invalid ReleaseSync at
21:58:05.515. The compositor later logged AcquireSync timeout at 21:58:48.745,
invalid ReleaseSync at 21:58:49.082, then two more failure pairs through
21:58:49.152. The independent queue trace places two producer gaps of about
100.74 and 347.89 ms before new NVENC work, with empty encode/FIFO queues around
those gaps. This establishes an upstream source-frame interruption coincident
with texture-handoff failures. It does not establish the first device or call
that held the resource too long.

In particular, the 337 ms from the first compositor failure log to its release
error includes everything performed after failed acquisition. It is not the
configured 10 ms acquire timeout, nor a measured 337 ms ReleaseSync call.

There are concrete stages to distinguish next:

- time in the driver/compositor AcquireSync call versus time in protected
  graphics work;
- CPU scheduling or a D3D Map/Flush call versus GPU work completing;
- delayed server PostPresent/pacing versus delayed compositor rendering;
- GPU timing-result collection versus an actual render-completion fence.

`CGraphicsDevice::WaitForPresent` (`0x140079990`) can call
`CGpuTiming::GetDeltas` (`0x14013A4D0`) before the outer compositor mutex release.
The latter can repeatedly call D3D GetData with flags 0 after `S_FALSE`, using a
shared one-second polling deadline for its disjoint/timestamp queries. A caller
selecting the preceding query generation does not disable that polling. This
is a possible additional wait while a resource is held, not a demonstrated
cause of the captured stall. No supported setting that disables this collection
in the active direct path has been recovered. `allowTimingStore` concerns stored
timing data, and is not evidence of such a control.

[GetData's result contract](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-getdata)
distinguishes ready data from `S_FALSE`. Changing its flag to DONOTFLUSH by itself
would not make an existing polling loop safe: commands may then remain unsubmitted,
as [Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_async_getdata_flag).
A future nonblocking timing-collection experiment would need a bounded deferred
result policy, explicit missing timing, preserved render fences and verified
query lifetimes. It should follow evidence that this stage is actually stalling.

## Existing asynchronous encoding control

`driver_vrlink.asyncStartEncode` is consumed at encoder initialization by
`0x180A21B70` into encoder byte +42936; a missing/error setting becomes false.
The asynchronous worker is created during encoder setup. In `0x180A24720`,
input `CopyResource` and frame-parameter preparation still run on the submission
thread. The flag only chooses between calling `0x180A23FF0` there and waking the
`0x180A245E0` StartEncodeDataThread, which runs that function for the next prepared
slot. It moves NVENC reconfigure/encode calls out of PostPresent, while preserving
the existing queue and serialization checks.

This is a suitable existing-control A/B candidate for reducing driver callback
occupancy. It does not move the earlier AcquireSync, composition, Flush or input
copy, and cannot be declared a fix for a stall located before those operations
finish. Changing the setting requires the initialization path to run; reading
the live byte and worker identity is preferable to assuming a settings write
changed an existing encoder. Keep its test separate from bitrate, receiver
window and repair changes, and compare real timeout/reset events and source-frame
spacing rather than only average Mbps.

The later endpoint-only normal-controller capture already had this byte set:
all 6000 host queue samples reported `async_start_encode=true`. Enabling it is
therefore not a new fix for that capture's failure. It remains an independent
setting whose exact initialization and scope matter when comparing other runs.

## Read-only current-scope observer

The ignored `artifacts/RepairTrial/texture-handoff-scope-observer.py` observes the
existing compositor watchdog rather than modifying it. The registration function
`0x140201710` installs a 72-byte record containing the thread name, TID, heartbeat,
timeout and current scope pointer. The current-scope getter/setter are
`0x140201EC0`/`0x1402021E0`; they traverse module TLS +16 to the wrapper/record and
read/write its +64 scope pointer. The registered-record vector is at RVA 0x375F70;
the module TLS index is at RVA 0x37A408.

The observer verifies the exact file hash, mapped getter/setter/registration
code, process creation time, the registered `Render` TID, the thread's own
creation time, and the TEB/TLS link back to that same record. It uses only query
and bounded memory-read APIs. It does not call the encoder helper, enable ETW,
attach a debugger, stop threads or alter settings. Scope strings are accepted
only from the module's read-only sections. It stops on process/thread/code or
identity changes, records partial results on failure, and closes its handles.
The default is a plan; an explicit PID, output path and `--run` are required.
The optional all-thread mode covers registered watchdog threads, not every
operating-system thread, and is limited to 10 Hz.

The parent-run 10-second smoke test on compositor PID 48700/TID 59860 passed all
guards and collected 1000 samples at 100 Hz. Mean memory-read time was 64.3 µs,
maximum 200.1 µs; total observer CPU was 0.359 seconds including discovery and
file validation. 916 samples named `Direct->WaitForPresent()`, four named
AcquireSync, five Flush, and none GetDeltas. This validates access and labels,
not attribution of the previous session's stall.

Repeated labels are **point samples, not entry/exit durations**. A 300 ms run of
identical WaitForPresent labels may cover many normal frames, because the thread
can leave and re-enter that scope between samples. Sampling also misses short
operations and can alias with refresh rate. Interpret it alongside frame IDs,
producer timestamps and fault logs. If those establish a producer gap, scope
samples inside that same gap can narrow the responsible stage. CPU scheduling
traces or paired existing ETW entry/exit events would be needed to separate
running, descheduled and GPU-blocked time within that stage.

Existing ETW strings already include Begin/End AcquireSync and ReleaseSync,
Flush, GPU timing and GPU-work wait scopes. `0x140009650` registers the normal
string provider `{8c8f13b1-60eb-4b6a-a433-de86104115ac}` (severity variants begin
8d/8e). No ETW session was enabled for this mapping. Twelve offline observer tests
cover record bounds, vector bounds, TLS traversal, ABI layout and conservative
sample-run summaries. Private extracts, provenance, selected logs and the smoke
capture remain ignored; this document contains only original prose and API links.

## Paired normal-controller capture: a different failure sequence

The next 150-second stream measurement used host PID 45244 and compositor PID
48700, with a 155-second, 100 Hz scope capture on Render TID 59860. It made no
encoder-helper calls inside the measurement window. Actual encoded output was
302.20 Mbps at 89.14 fps, and the receiver reported 129 presentation timeouts and
one dropped frame. This run was not a smoothness pass.

The saved compositor log delta was empty; the server/driver deltas contained no
DXGI acquisition or release failures. None of 15,500 point samples showed
GetDeltas. Mean scope-read time was 76.0 microseconds, maximum 1.661 milliseconds;
observer CPU was 0.875 seconds including setup. This does not rule out an
unsampled short operation, but the capture did not reproduce the earlier keyed
mutex failure.

The first retained receiver fault was a retransmission-list overflow for TX
42610 at receiver wall time 22:20:14.545591, followed by a dequeue EAGAIN and then
three CheckStuck warnings and a hard reset. There was no preceding backtrack
warning in that log delta. AFTM records TX 42610's complete-receive event at
receiver time 22:20:14.550914, **5.323 ms after the overflow**. Later completion
does not undo the fault, and without the branch/operand observer this log alone
cannot identify timed underflow, progress-path overflow or another request
geometry as its mechanism. Host and receiver wall clocks differ; their raw
clock values must not be directly ordered against each other.

On the host's own timeline, the first reset request is logged at 22:20:14.145.
All AFTM production-stamp gaps exceeding 40 ms follow that request: 320.994 ms
from TX 42614 to 42615, then three approximately 311.1 ms gaps, and 66.671 ms.
The largest such gap before the first reset was 33.333 ms. These AFTM stamps are
producer release near the end of PostPresent, not encoder-submit timestamps.
Repeated, agreeing encoder-slot prepared-clock observations independently show
321.137, 311.671, 310.936 and 311.120 ms gaps for TX 42614 through 42618. Their
absolute wall-clock alignment uses an external read anchor and is approximate.
During those long gaps the decoded ACK remains 42608, admission Q rises from 7
through 10, the holdoff flag is normally set, and sampled active encoder slots,
FEC pending count and sender FIFO are empty. Compositor samples predominantly
show the wait for the server's Presented event. They do not establish one
continuous wait per gap.

Together, these observations support **receiver fault followed by host admission
holdoff and reset recovery** for this capture. They do not support assigning its
large source gaps to GetDeltas or a failed texture mutex. The earlier texture
failure remains a separate reproduced problem requiring its own paired capture.
