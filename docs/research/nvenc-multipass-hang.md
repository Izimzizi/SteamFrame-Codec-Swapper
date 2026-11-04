# NVENC hang on a live multipass change (GPU error 153)

Status: root cause found and worked around in the hook (commit fd27987). Verified with the native stress test; the fixed build also survived the 10-minute tray stress test on a real VR session.

## Symptom

While streaming Default (HEVC), changing the encoder options live from the tray sometimes ended in:

- Windows System log: `nvlddmkm` event 153, "Error occurred on GPUID: 100".
- The stream stops, then SteamVR crashes or the video never comes back.
- Steam Link driver log (`driver_vrlink.txt`): `[Fatal] Perf: Frame N not in expected state`, then `Got error during nvEncEncodePicture EOS with NVENCSTATUS. 4`.
- After the fault every NVENC call returns 8 (`NV_ENC_ERR_INVALID_PARAM`).

It was reported as "multipass 2 crashes", always after some live changes, sometimes after changing the preset a few times first.

## What the evidence showed

1. `nvEncReconfigureEncoder` and `nvEncEncodePicture` return success. The hang appears at the bitstream lock of that frame, which never returns.
2. The fault is always milliseconds after a reconfigure that changed an option. Rate control, preset and VBV changes were never the one that hung.
3. The hook's flight recorder (`%LOCALAPPDATA%\CodecSwapper\flight.log`, last 300 encoder calls, written on every options change and on failures) showed no difference in the arguments of the hanging call: no reset, no IDR, ordinary bitrate and VBV values.
4. The tray's stress test (Benchmark window, "Debug: stress test") that only toggled multipass between 2 and 0/1 ran 204 changes without a fault. The same test with the sequence 0 -> 1 -> 2 (the order the smooth benchmark uses) failed on the 9th change, on the 1 -> 2 step.

## Reproduction without VR

`Native/H264Hook/encoder_stress_gpu_test.cpp` runs the driver's HEVC encoder at the streamed size through the hook, reconfigures before every frame like SteamVR, and changes options through the options file. Walking multipass 0 -> 1 -> 2 hangs about 3 seconds in, on one thread or two, with or without other GPU load.

Environment variables choose the sequence: `STRESS_MP_START` (multipass the encoder is created with), `STRESS_MP_SEQ` (digits, one per change, repeated), `STRESS_MP_TOGGLE`. With a 1.5 s interval each result was repeatable:

| Sequence | Result |
|---|---|
| 0 -> 1 -> 2 | hang at the 1 -> 2 step |
| created at 1, then 2 | hang |
| 2 -> 0 -> 1 -> 2 | hang |
| 2 -> 1 -> 2 (repeated) | fine |
| 0 -> 2, 2 -> 0 | fine |
| 1 -> 0 -> 2 | fine |
| 0 <-> 1 | fine |

Changing faster than the hook reads the options file (about 500 ms) skips the 1 -> 2 step and does not trigger it. A forced reset and keyframe on the multipass step did not help.

Rule that fits all runs: a reconfigure from multipass 1 (quarter-resolution first pass) straight to 2 (full resolution) hangs, unless the 1 was itself entered from 2. Why NVENC does this is not known; the reconfigure is accepted and the failure surfaces later at the lock.

## What was tried first, and why it was wrong

- **Pin multipass at creation** (commit 22602fc, reverted): the encoder would keep the multipass it was created with and a change would wait for the next SteamVR start. That hid the problem by removing live multipass changes, and multipass 0 <-> 1 changes work fine, so it was reverted. The user asked for the real cause.
- **No reset on a changed option** (earlier): removed `resetEncoder`/`forceIDR` after a rate-control reset together with multipass 2 faulted. This was a different suspect, not the cause.
- Early tests with a single bare encoder and 1,200 random changes did not fault, because they never walked 0 -> 1 -> 2.

## Tools that found it (kept in the repo)

- **Flight recorder** in `stock_forward.cpp`: ring of 300 calls (init, reconfigure with what the driver asked and what was applied, encode, lock, destroy), headings name the option that moved ("multipass 1->2 (change 9 since the encoder was created, ...)").
- **Native stress test** `encoder_stress_gpu_test.exe` with separate encode and lock threads, frames in flight, other GPU load, new content per frame and SteamVR-like request dips. Exit code 2 (call failed) or 3 (hang).
- **Tray stress test** (a temporary debug button, removed again after the fix; restore it from commits eb227ea and 7a2e8fe): 10 minutes, a change every 2 s, every 3rd change steps multipass 0 -> 1 -> 2 -> 0; stops on lost video, encoder errors, GPU error 153 or a driver `[Fatal]`; saves its log and the flight log in the Benchmarks folder.

## Fix

In `apply_options` (`Native/H264Hook/stock_forward.cpp`): when the options ask for multipass 2 and the encoder is currently at 1, the reconfigure of that call uses multipass 0, and the next call (SteamVR reconfigures before every frame) applies 2. Every other multipass step is applied directly. The setting stays live and takes effect one frame later on that single step.

Verified natively: the sequences that hung (0 -> 1 -> 2, 2 -> 0 -> 1 -> 2, created at 1 then 2) survive, and a 120 s random run with the multipass walk (79 changes, 3,882 frames, GPU load 70%) finished without a fault.

## Still open: live multipass is only partly safe

The 1 -> 2 fix removes the GPU hang, not every problem with live multipass changes:

- A benchmark sweep that applied multipass live (0 -> 1) failed the multipass 1 step with 71 stream-reset requests (0 per second at first, then 6 and 65 about 12 seconds after the change). The same step was fine when the sweep restarted the stream for each multipass change, so the sweep restarts the stream again (`SweepApply` in `TrayApp.Benchmark.cs`). The tray's own option changes (menu, HUD) still apply multipass live.
- Idea not yet tested: a 1 entered from 0 may be what leaves the encoder in a bad state (every observed hang and storm had a 1 that was entered from 0 or created at 1; 2 -> 1 -> 2 was fine), so 0 -> 1 could go through 2 like 1 -> 2 goes through 0. A first try of that was dropped because the native test could not show reset storms.
- The native stress test with no option changes at all (`STRESS_MP_START=2 STRESS_MP_SEQ=2`, a change every 6 s that rewrites the same values) sometimes stalls for 8 seconds at multipass 1 or 2 and sometimes runs a full minute, with no GPU error in the Windows log. Not understood; the test's stall is not necessarily the same fault.

## Other open points


- Whether newer drivers or GPUs behave differently. The rule above was found on the development PC's driver; if the flight log of a future fault shows another multipass step, extend the rule and add the sequence to the table.
