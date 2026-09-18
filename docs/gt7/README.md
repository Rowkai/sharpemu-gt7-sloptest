<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Gran Turismo 7 (PPSA01317) on SharpEmu

Title: `Gran Turismo® 7`, `PPSA01317`, v`01.680.000`,
`G:/Games/GT7/PPSA01317-app/eboot.bin`. Branch `gt7-boot`.
Host: Windows 11, Ryzen 9 9950X3D, RTX 5080, 32 GB.

This file is the **current** state: status, live blockers, next steps, how to
run, tools and traps. [boot-investigation.md](boot-investigation.md) is the
chronological evidence log (symptom, evidence, fix, ruled out) — its early
sections contain conclusions that were later corrected; the index at its top
says which. Read [CLAUDE.md](../../CLAUDE.md) first: fix the emulator, not the
game.

**Goal.** Repeatably reach GT7's main menu, then load and finish an offline race
with advancing frames, input and audio, through emulator-general fixes only. No
title-id checks, forced guest flags, skipped instructions or fake results.

## Status — 2026-09-14

| Area | State | Evidence |
| --- | --- | --- |
| Boot | Reaches the PSN sign-in dialog ("Failed / You can't use network features … PS™N / OK") in every recent boot; an occasional boot stalls early at ~0.2 fps | §6be, §6bf |
| Presentation | Guest frames presented, 24–30 fps on the dialog, 60 fps after OK | §6az, §6be |
| Text | **Not drawn on any screen.** Glyphs are rasterised correctly into guest memory, but no draw binds them | §6bf |
| PS Studio logo movie | Opens (`size=57730081`), decodes, audio plays, and its draws now execute and reach the screen — but the picture is **flat green**. The sampled planes are correct on the GPU; which shader path writes the green is unresolved | §6bh, §6bi, §6bk–§6bn |
| `SHARPEMU_GUEST_ARGS=skip_op` | Skips the movie. With the §6bo–§6br fixes, keyboard input drives the display-calibration wizard **all the way through**; the next screen is a carousel of album-art tiles (images render, text still does not). The race menu is reachable and selecting the first race starts the load. The load deadlocked because SharpEmu granted an invalid direct-memory allocation; that is fixed. **2026-09-15:** with shared direct-memory backing ([Appendix G](boot-investigation.md#appendix-g-direct-memory-allocation-and-mapping-contract)) the race load streams to completion (`race37`: 8,869 shared maps, all 669 partial unmaps, no memory faults), then a physics job crashes — see blocker E | §6br, §6bs, [Appendix F](boot-investigation.md#appendix-f-gt7-race-load-session-record-2026-09-15) |
| Stability | The §6bg `ObjectDisposedException` was a runner-disposal race, fixed; none in `rax-fix1` | §6bh |
| Tests | 1,154 passed, 0 failed (2026-09-15, shared direct backing) | [Appendix G](boot-investigation.md#appendix-g-direct-memory-allocation-and-mapping-contract) |

Nothing on this branch after `58272cd` is committed.

Picking this up cold? Start with [Appendix H](boot-investigation.md#appendix-h-handover-gt7-2026-09-16): branch state, the two
shader fixes landed that day, blocker F's fault chain and ruled-out list, and
the tooling traps that have cost runs.

## Live blockers

### F. Job state corrupted after the race load, moving sites (2026-09-15)

- With shared direct backing and the fiber-fs fix, the race load streams to
  completion every time (`race43`, `race44`: ~9,000 shared maps, all 669 partial
  unmaps), then a job-side thread crashes at a **different site each run**:
  `race43` GPUex runs a deferred callback node with `fn=0x58` (`0x8005FE9C4`, in the
  destructor `sub_8005FE930`); `race44` Job#5 calls an allocator whose pointer at
  `[ctx+0x28]` holds float bits (`0x8007826A1` → thunk `0x8031B7C70`), last import
  `sceFiberSwitch`.
- **Ruled out:** false aliasing (all 9,035 maps in `race44` lie inside one live
  allocation; the crash struct has no alias); lazy commit (0 faults); static TLS
  overlap; a missing CPU-id import; AGC zero-buffer warnings (baseline in
  `race30`); fiber Run/Switch/ReturnToThread disagreement (no failing results in
  any run).
- **Ruled out — fiber tracking (`race45`, `race46`):** with guest-thread-tagged
  fiber tracing and an independent per-thread transition record
  (`SHARPEMU_LOG_FIBER=getself`), `sceFiberGetSelf` never answers "not in a fiber"
  while the caller is executing a fiber (0 mismatches, 0 wrong-fiber across
  170,000+ checks). Fibers run on Job#0–5, Updat and five ADTsk threads; the
  PERMISSION flood is threads asking from their root context.
- **`race64` (2026-09-16), a fresh site with the same last import:** Job#0 takes
  an AV reading `0xCC` at `0x802F8C74D` — a null structure pointer, not a stale
  one — with `rdi=0xA50000`, `rsi=0xA4`, `rdx=0xA5` (a counter pair one apart)
  and `last_import=tn3VlD0hG60` (`scePthreadMutexUnlock`), matching `race52`,
  `race53` and `race54`. Four runs now crash on a different instruction with the
  same preceding import, so the search should be on what that unlock releases
  rather than on any one crash site. The run also carried the §6by shader
  changes, which are pixel/vertex SPIR-V only and cannot reach guest job state.
- **Open:** what corrupts job state after the load. `race45` stalled on the
  carousel instead of crashing (no thread snapshot). Adding
  `SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1` (`race46`) produced a host-side crash on
  `.NET TP Gate`, so per-thread state needs another way to be captured.

### E. Physics job reads a zero type word after the race load (2026-09-15)

**Root cause found and fixed; `race43` streamed the load without this crash:** SharpEmu's fiber continuations
carried the suspending thread's fs base, so a fiber migrated between Job workers
ran on another worker's TLS and GT7's own TLS job-state re-attach wrote into the
wrong thread. Fibers now keep the resuming thread's fs (ShadPS4 never switches fs
in its fiber context). Details and evidence chain in
[Appendix F](boot-investigation.md#blocker-e-root-cause-fibers-carried-the-suspending-threads-fs).
The notes below record how it was narrowed down.

- `race37`: after the load streams completely, Job#5 faults at `0x8002CDC4B`, a
  switch dispatch: `edx = word[rbx+0x18A] - 0x1700` with the word **0**, so the
  jump-table index wraps (`rdx=0xFFFFE900`). `rbx=0x10017C0D80`.
- Same context as `race28` on the old private backing: Job thread, last import
  `scePthreadMutexUnlock` returning to `0x80034F561`, `0x7FFDFD…` pointers in
  the frame. So this predates shared backing — and **aliasing was not `race28`'s
  cause**, contrary to the 2026-09-15 morning reading.
- Ruled out as a backing bug: the struct's mapping (`0x1001600000`, phys
  `0xACC00000`) is a recycled range, but its previous mapping at `0xF41BE00000`
  was detached by the covering release (line 8235/8236) long before the range
  was re-allocated and cleared (line 25448).
- **Next:** find who writes `[rbx+0x18A]` — a `SHARPEMU_WATCH_GUEST_WRITE` deref
  spec from a stable root, or decompile the caller around `0x80034F561` for where
  the struct is initialised. Check whether hardware zeroes recycled direct memory;
  zero-on-reuse is still an unverified reading.

### A. Text is rasterised but never drawn (§6bf)

- `sceFontRenderCharGlyphImageHorizontal` fills correct glyph bitmaps
  (e.g. slot `0xEC06856000`); the glyph-cache entry has a valid Gen5 texture
  header. **No draw or compute dispatch ever binds a glyph texture.**
- Text widget class: vtable `0x8058081D8` (the only vtable holding the layout
  method). Slots: `+0x40 sub_80323F210`, `+0x48 sub_80323F3C0`,
  `+0x50 sub_80323F410` (render → glyph submit `sub_8032420D0`),
  `+0x58 sub_80323F550`, `+0x68 sub_80323A400`, `+0x70 sub_800084A30` (layout).
- Exec probes on eight entries: **only layout (`+0x70`) runs**. The render slot
  has no direct callers. Live widget `+0x3B8/+0x3C0` (texture array / vertex
  buffer) are null, counts 0.
- The inline `+0x40/+0x50` dispatch inside `sub_8004223D0` is special-case
  wrap/truncation work, not the normal label draw. In its decompile `param_1` is
  `long*`: `param_1 + 0x25` is byte offset **0x128**.
- **Next:** find the per-frame UI traversal that should call `vtable+0x50`.
  Start from the live callers of the text update (`0x800352490`, `0x80035E730`,
  `0x800421E10`) and the path that draws the panel/button in the same frame;
  compare a drawn panel with a label under the same parent (list membership,
  visibility, clipping, dirty state). Candidates reading the glyph list at
  `+0x248..+0x258` are in `artifacts/gt7-opus-20260913b/glist-decomp.log`,
  unread. Find the first branch that omits the label and trace its input back to
  an HLE result.

### B. The PS Studio movie draws green: its samples return zero (§6bk–§6bn)

The open/size failure is **fixed**: SharpEmu never copied a guest callback's RAX
back into the callback context, so GT7's import-free size callback read as 0
(§6bh). In `rax-fix1` GT7's callbacks give `size=57730081`, the file is read, the
3840×2160 NV12 decoder starts, video frames and audio are delivered, and the
audio is audible.

**Command path traced (§6bi):** GT7 `memcpy`s each frame into its own 3-slot
ring (`player+0x418`, base `0xF41D228000`), binds Y+UV from it, draws the movie
into a 3840x2160 target, and a second draw composites that into a 1920x2160
surface. Until §6bk none of it reached the screen (every playback dump was
pixel-identical to the sign-in dialog) because the draw threw before command
recording — §6bj.

**Image creation fixed (§6bj, §6bk, 2026-09-14):** the movie draw threw
`DivideByZeroException` inside `vkCreateImage` because its Y/UV planes are
`R8Uscaled`/`R8G8Uscaled` and this host reports no image features for any
scaled format. Scaled textures are now stored widened to a float format that
holds the unnormalised value exactly (`R8Uscaled` → `R16Sfloat`, `R8G8Uscaled`
→ `R16G16Sfloat`, and so on), and every texture's format is checked for
`SAMPLED_IMAGE` support before creation. No exception and no failed draw in
four captures, and **the movie's output now reaches the screen** — in §6bi
every playback dump was pixel-identical to the sign-in dialog.

**Still wrong:** the screen is flat green `(0,95,0)` for the whole movie, with
the UI still composited on top. The movie's own 3840x2160 draw writes that green
— its render targets are the guest's display buffers and read back green
(`G` mean ~380/1023) — so the composite and flip are not at fault (§6bl).

**Established by GPU readback (§6bl):** the sampled plane images hold the right
values (UV exactly `128.0`, later slots `110…171`), so the widening, staging and
copy are correct — though that proves the image content, not that the draw
sampled *that* image through the right view, descriptor and coordinates.

**The right shader path runs, and its samples come back zero (§6bn).**
`0x1258E49600` has a 10-bit BT.2020+PQ block at `0x0064` and an 8-bit BT.709
block at `0x1200`; PC markers written into the export channels
(`SHARPEMU_MARK_PIXEL_PCS`, `SHARPEMU_CAPTURE_PIXEL_VGPR_POINTS`) show the
**8-bit block executes** (marker 1016/1023 on 99.2% of the target) and the
10-bit block does not (2/1023). On that block the texture coordinate is a clean
0…1 gradient (mean 508/1023) while **luma, U and V all sample ~0** (1.2–2.1 of
1023) — and zeros through that block's arithmetic reproduce the green. Guest
bit depth is not implicated; leave AvPlayer alone.

**Next:** the failure is now confined to **the resource the draw binds for `s0`
and `s8`** — content, coordinates, path selection, composite and flip are all
ruled out by measurement. Dump the movie draw's descriptor set and compare each
slot against the plane addresses; §6bi's `agc.movie_bind_group` also carries a
`65534x65536` and a null `1x1` entry, so a slot mismatch would sample empty
exactly like this. Retracted along the way: §6bk's "zero refreshes" (the trace
was disabled) and sampler-border diagnosis; §6bl's "expects 10-bit", its P010
suggestion and its staleness inference (the movie runs 8.558 s, not the capture
length); §6bm's "flattened SPIR-V" reading (the translator is a program-counter
dispatcher) and the SDWA-to-VCC lead (`s106` *is* VCC). The §6bi `writer=1693`
sample predates playback and is not evidence of a movie overwrite.

### C. Host crash: disposed wait handle in a guest runner thread — fixed (§6bh)

Confirmed by reading: the reaper disposed a runner while holding
`_guestThreadGate`, which the exiting runner needed to finish, so the 500 ms join
timed out and the thread then waited on a destroyed handle. Runners now wait on a
monitor (nothing to dispose), stops are idempotent and ordered with scheduling,
continuation waiters are never abandoned, and the reaper skips runners still
`ExecutorActive`. Test: `GuestRunnerDisposalTests` (isolated process).

### D. Progression past calibration

**Fixed, one real cause (§6bo):** short key presses never reached the guest.
`SdlHostWindow.PumpEvents` drains every queued SDL event in one loop before each
frame, so a key-down and key-up landing in the same drain were added and removed
from `PressedKeys` back to back and the guest — which samples the pad once a
frame — saw nothing. Measured before the fix: a 150 ms tap left `buttons=0x0000`
while a 2 s hold gave `0x4000`. A released key is now held visible for 100 ms
from release (`HostWindowInput`), cleared on focus loss; a 120 ms tap registers.
Tests: `HostWindowInputLatchTests`.

Also fixed on this path: `libSceSigninDialog` did not exist and
`sceNpHasSignedUp` was unresolved, so GT7's sign-in step got `NOT_FOUND` (§6bo).

Not a bug, and retracted: the brightness page "ignoring" input. Those drives ran
with `SHARPEMU_GUEST_IMAGE_DUMP_CONTINUOUS=1`, which dumps every frame (~1 fps),
or lost window focus between keys. Cross does advance the wizard's pages; whether
the slider page accepts left/right is being re-tested under a clean harness.
`SHARPEMU_GUEST_ARGS='offline skip_op'` (two args) stalls before the first
frame — compare guest argv layout with the single-arg case before blaming
anything else.

**Race load (§6bs, [Appendix D](boot-investigation.md#appendix-d-gt7-race-load-hang-handover-2026-09-14-opus)) — partly fixed, still
open.** Selecting the first race deadlocked: a file worker held `0x806DFAB08`
while waiting on an event that nothing signaled. Cause: SharpEmu granted
`sceKernelAllocateDirectMemory(len=0x180800002, alignment=0x6007FFE32)`, which
hardware rejects because neither is a multiple of 16 KiB, and it took 6152 MiB.
The 5.06 GiB streaming arena requested later was refused and kept base 0, its
sub-allocated offsets were used as pointers, `sceKernelVirtualQuery` answered
NOT_FOUND, and the read request was dropped. Allocate now validates length and
alignment, and the pool reports 13,376 MiB (KytyPS5's model). With that the arena
allocates and the query misses are gone, but the title then sits on the carousel
with a spinner (`Job#2` busy under `0xE418577F88`; `Strea` flat at 30,611
imports). The malformed allocation itself comes from an unresolved
`sceHmd2ReprojectionQueryDisplayBufferSizeAlign`: GT7 uses its `rax:rdx` return
unchecked as size and alignment. `race14` hit the original deadlock again
because its streaming-arena `MAP_FIXED` failed: Windows had put the emulator
main thread's host stack inside that guest range. SharpEmu does not keep host
memory out of PS5 user space. Separately, GT7 maps the same physical memory at two
addresses and SharpEmu backs each with private pages — a real bug, but the
earlier "proven in `race14`" claim is withdrawn (that run never reached the
aliasing phase and the pairs read were stale). Only the first race in that menu
is available on a fresh save.

Session record for the 2026-09-15 work (what moved, what was corrected, what is
still open): [Appendix F](boot-investigation.md#appendix-f-gt7-race-load-session-record-2026-09-15).

## Next steps, in order

1. **Blocker D, race load (§6bs, §6bt, §6bu, §6bv).** Both upstream defects are
   fixed and verified in a run: the reprojection size queries (§6bt — the 32 MiB
   allocation at `0x800FEE9CB` now succeeds) and the AMPR gather/scatter command
   (§6bu — 2,572 full-size streaming reads in `race22`, where every earlier run
   transferred none). The load now advances past streaming and **crashes**:
   null descriptor at `0x806973F20`, faulting at `0x800A06D15` on `Job#0`,
   identically in `race19` and `race22`. §6bv/§6bw: that crash **stops happening**
   once the carousel walk selects the first tile properly (the driver's 90 ms
   taps were dropped at ~19 fps). With the right tile, `race28` gets further and
   dies in car/physics setup instead — `mov rcx,[rcx+0x11D8]` with `rcx = 0xEE`,
   a pointer field read as garbage out of a direct-memory mapping. That is the
   signature of the aliasing bug, and §6bx now **evidences** that bug for the race
   load rather than leaving it hypothetical: `map_direct_result` (new trace —
   `map_direct` only logged the address *of* the out-pointer, which is why this
   was invisible) shows **30 physical ranges mapped at more than one guest VA** in
   `race30`, each backed by private host pages. `munmap_partial` is also a hot
   path now (669 calls carving the streaming arena 2 MiB at a time), so the
   sparse-residency model is real. The fix is shared backing for direct memory:
   the host primitives exist and pass (`SharedBackingSectionTests`), but
   `PhysicalVirtualMemory` does not use them. One gap remains — a single run with
   both direct-memory logging *and* the crash, to tie the faulting `rax` to the
   alias list.
   Still open behind that: reserve PS5 user space at
   startup so host allocations cannot block guest fixed mappings (the cause of
   `race14`'s deadlock).

   Driving to the load is now scripted and frame-driven rather than timed:
   `artifacts/gt7-opus-20260914/drive-race2.ps1` waits for the guest's first
   presents, walks the calibration wizard with a held stick plus the
   right-hand confirm button, and detects the carousel from a 12x7 frame
   signature (`sig.py`). The old `drive-race.ps1` pressed during boot and
   desynced the dialog stack, which is why several earlier runs never left the
   wizard.
   Independently, direct memory mapped at two virtual addresses does not share
   bytes; whether that causes the stall is not shown. Fix: shared section backing
   with placeholder views, host-level tests first — see
   [Appendix D](boot-investigation.md#fix-design-shared-backing-for-direct-memory-not-started).
2. **Blocker B** — scaled texture creation is fixed (§6bk); the correct shader
   block runs but its samples return zero (§6bn), so audit the descriptors the
   movie draw binds for `s0`/`s8`.
3. **Blocker A** — the per-frame UI traversal.
4. (Blocker C fixed in §6bh.)
5. **Housekeeping before any commit:** revert the temporary AvPlayer diagnostic;
   implement `sceKernelAprGetFileSize` properly (see below); add tests for the
   `sceKernelVirtualQuery` host-mapping fallback, for thread stack/TLS region
   reuse and for the direct-memory length/alignment validation; decide whether `SHARPEMU_AUTO_PAD` and
   `SHARPEMU_TRACE_TEXTURE_BIND_ADDRESS` stay; run the full suite.
6. Only once a scene is correct and repeatable: progression to the menu, then
   performance work (profile first).

## Uncommitted emulator changes on this branch

| Change | Where | Test |
| --- | --- | --- |
| PM4 COUNT `0x3FFF` wraps to a 1-dword packet (every graphics submission was being discarded); `sceAgcCbNop` bound | `Agc/AgcExports.cs` | `AgcPaddingNopPacketTests` |
| `V_XAD_U32` (VOP3 `0x345`) in SPIR-V and MSL | `ShaderCompiler*/…Alu.cs`, `Gen5ShaderTranslator.cs` | `Gen5XadSpirvTests` |
| Guest thread stack/TLS windows no longer overlap; exited threads' regions and host threads are reclaimed | `Core/Cpu/Native/DirectExecutionBackend.cs` | **none** (boot counts only) |
| IME keyboard lifecycle (`KeyboardOpen` event, `sceImeKeyboardClose` export) | `Ime/ImeExports.cs` | `ImeKeyboardTests` |
| libSceFont rasterises real glyphs (OpenType/CFF, Type 2 charstrings) | `Font/OpenTypeFont.cs`, `Font/FontExports.cs` | `FontExportsTests` |
| AvPlayer honours `SceAvPlayerFileReplacement` when the host cannot resolve a path | `AvPlayer/AvPlayerExports.cs` | `AvPlayerFileReplacementTests` |
| Four-argument guest callbacks | `HLE/GuestThreadExecution.cs`, `DirectExecutionBackend.cs` | via the above |
| Guest runners wait on a monitor, not disposable handles; reaper skips `ExecutorActive` and never joins under the gate (§6bh) | `Core/Cpu/Native/DirectExecutionBackend.cs` | `GuestRunnerDisposalTests` |
| Guest callbacks and continuations return the guest's full 64-bit RAX (16-byte host-RSP slot, return stub saves RAX across `TlsGetValue`) (§6bh) | `Core/Cpu/Native/DirectExecutionBackend.cs` | `Gen5NativeReturnSmokeTests.ImportFreeGuestCallback_ReturnsFull64BitRax` |
| Shared isolated-process test helper | `tests/…/Cpu/IsolatedTestWorker.cs` | used by the two above |
| **Diagnostic, revert or keep:** movie/texture bind tracing — `agc.video_buffer_bind`, `agc.texture_bind_summary`, `agc.large_texture_bind` (all under `SHARPEMU_TRACE_AVPLAYER_IMAGES=1`), and `SHARPEMU_TRACE_MOVIE_TEXTURE=<hex>` (binding group + forced draw trace) (§6bi) | `Agc/AgcExports.cs` | — |
| Scaled (USCALED/SSCALED) textures stored widened to float, one shared upload-preparation path, and a sampled-image capability check before every `vkCreateImage` (§6bk) | `VideoOut/VulkanVideoPresenter.cs` | `VulkanScaledTextureFormatTests` |
| AGC completion interrupts queued on their own guest queue (they all landed on `host.default`, so could fire before the work they report) (§6br) | `Agc/AgcExports.cs` (`NotifySubmittedDcbCompleted`) | existing `AgcSubmitCompletionEventTests`; queue attribution checked at runtime only |
| SNORM texture formats `(5,1)`, `(10,1)`, `(12,1)` mapped (they fell to RGBA8, so GT7's `Rgba16Snorm` compute dispatch was refused as a mismatch); mirrored in Metal (§6bp) | `VideoOut/VulkanVideoPresenter.cs`, `Gpu/Metal/MetalGuestFormats.cs` | `VulkanScaledTextureFormatTests` |
| Host key taps no longer lost: a key released between two SDL event drains stays visible for 100 ms (§6bo) | `Pad/HostWindowInput.cs` | `HostWindowInputLatchTests` |
| `SHARPEMU_LOG_PAD=1`: pad state on change, plus guest read counts | `Pad/PadExports.cs` | — |
| `SHARPEMU_LOG_AGC_LABELS=1`: label-producer trace without the full packet trace (§6bq) | `Agc/AgcExports.cs` | — |
| Stall snapshot skips exited guest threads (count line instead) and lists up to 1024; a blocked `pthread_mutex_lock` names its owner and waiter count (§6bs) | `Core/Cpu/Native/DirectExecutionBackend.cs`, `Kernel/KernelPthreadCompatExports.cs` | — |
| Direct-memory allocate (`sceKernelAllocateDirectMemory`, `sceKernelAllocateMainDirectMemory`) returns EINVAL for a length or non-zero alignment that is not a multiple of 16 KiB, as KytyPS5 does — GT7's invalid 6152 MiB call had starved its streaming arena (§6bs) | `Kernel/KernelMemoryCompatExports.cs` | **none** (runtime: `race12`) |
| Direct-memory pool reports 13,376 MiB (13,824 MiB total − 448 MiB flexible, KytyPS5's model) instead of 16,384 MiB (§6bs) | `Kernel/KernelMemoryCompatExports.cs` | **none** |
| Diagnostics: under `SHARPEMU_LOG_DIRECT_MEMORY=1`, `direct_memory_exhausted`, `release_direct`, `checked_release_direct` and `virtual_query_miss` (+ guest stack); `SHARPEMU_LOG_PTHREAD_COND_SITE=<hex,…>`; `SHARPEMU_LOG_PTHREAD_COND_WAKE=1`; exec-watch hits log `rax`–`rdx` (§6bs) | `Kernel/KernelMemoryCompatExports.cs`, `Kernel/KernelPthreadCompatExports.cs`, `Core/Cpu/Native/DirectExecutionBackend.WriteWatch.cs` | — |
| `libSceSigninDialog` (Initialize/Open/GetStatus/UpdateStatus/GetResult/Close/Terminate; finishes as `USER_CANCELED`) and `sceNpHasSignedUp` (false offline) — GT7 called both and got NOT_FOUND | `CommonDialog/SigninDialogExports.cs`, `Np/NpManagerExports.cs` | `MissingGt7ExportsTests` |
| `sceKernelVirtualQuery` answers for backend-mapped memory (guest stacks) | `Kernel/KernelMemoryCompatExports.cs` | **none** |
| Exports: `sceAgcAcbRewind`, `sceAgcAsyncRewindPatchSetRewindState`, `sceHmd2GetDeviceInformation`, `sceShareGetRunningStatus`, `sceNetInetNtop`, `sceNetRecv`/`sceNetRecvfrom`, `sceDeviceServiceQueryDeviceInfo_`, `sceDeviceServiceGetEventState`, `sceFontRenderSurfaceSetScissor` | `Agc`, `Hmd2`, `Share`, `Network`, `DeviceService/`, `Font` | `MissingGt7ExportsTests` and per-library tests |
| Diagnostics: `SHARPEMU_BREAK_GUEST_EXEC` (+ argument decode), `SHARPEMU_LOG_FONT`, `SHARPEMU_TRACE_TEXTURE_BIND_ADDRESS`, `SHARPEMU_AUTO_PAD`, lazy-commit decline log | various | — |
| **TEMP, revert:** `SHARPEMU_AVPLAYER_HOLD_BEFORE_CLOSE_MS` holds before GT7's `close` and re-calls `size` | `AvPlayer/AvPlayerExports.cs` (`TryReadGuestSourceThroughCallbacks`) | — |

Known contract bug, not on GT7's path: `sceKernelAprGetFileSize` returns success
without writing the size. Its signature is `(uint32_t id, uint64_t* size)`
(KytyPS5 `libAmpr.cpp`); GT7 does not import it (census).

## Running

```powershell
dotnet build SharpEmu.slnx -c Release      # a running emulator locks the DLLs
dotnet test  SharpEmu.slnx -c Release      # dotnet test alone does not refresh win-x64
& artifacts/gt7-opus-20260913b/tools/boot.ps1 -Name <run> -Seconds 150 -ExtraEnv @{ SHARPEMU_LOG_IO='1' }
```

`boot.ps1` clears `SHARPEMU_*`, sets `SHARPEMU_LOG_VIDEOOUT_FPS=1`, writes
`artifacts/gt7-opus-20260913b/<run>.{out,err}.log`, stops only the processes it
started, and prints a presented-fps summary. **One emulator at a time.**

- The launcher spawns a mitigated **child**; guest memory lives in the one with
  the large working set.
- To dismiss the sign-in dialog unattended:
  `SHARPEMU_AUTO_PAD='30:cross,36:cross,42:cross,48:cross,54:cross,60:cross'`
  (`button[+button]`, seconds from process start, held 0.4 s).
- Swapchain images: `SHARPEMU_TRACE_GUEST_IMAGES=present`,
  `SHARPEMU_GUEST_IMAGE_DUMP_DIR=<dir>`, `SHARPEMU_SWAPCHAIN_DUMP_EVERY=600`
  (`.bgra`). These dumps exist only on the guest-image blit path and stop when
  a title stops presenting through the guest-image blit. The desktop capture
  `artifacts/gt7-opus-20260914/capwin.ps1` is **not** reliable: it can return a
  stale frame (§6bp). A silent `[LOADER][PERF]` line means no flips and no
  presents at all.
- Driving menus: `artifacts/gt7-opus-20260914/sendkeys.ps1 -Keys cross,right,...`
  sends real key presses to the focused window (re-focusing before each key,
  extended flag on arrows). Do **not** combine with
  `SHARPEMU_GUEST_IMAGE_DUMP_CONTINUOUS`, which dumps every frame (~1 fps).
- Healthy boot: `presented_fps > 0` within ~15 PERF intervals. Stalled:
  `submitted_fps≈0.7 presented_fps=0.0`.

GT7-relevant diagnostics (the general table is in CLAUDE.md):

| Variable | Use |
| --- | --- |
| `SHARPEMU_LOG_IO` | path opens, `apr_resolve*`, `apr_get_file_stat` |
| `SHARPEMU_LOG_AMPR` | APR command buffers and reads |
| `SHARPEMU_LOG_AGC_LABELS` | one `agc.label_producer` line per scheduled label write (queue, submission, dst, action); with `agc.wait_suspended` it shows cross-queue wait cycles. Keeps ~22 fps, where full `SHARPEMU_LOG_AGC` drops GT7 to ~1.8 fps and changes the ordering being studied |
| `SHARPEMU_LOG_PAD` | pad button/focus changes and guest read counts |
| `SHARPEMU_LOG_FONT` | font handles, parse result, per-glyph render |
| `SHARPEMU_TRACE_TEXTURE_BIND_ADDRESS=<hex>` | texture binds in the MiB above the address |
| `SHARPEMU_BREAK_GUEST_EXEC=<hex,...>` | one-shot int3 probe on **any** thread (main included), logs registers and stack |
| `SHARPEMU_LOG_IMPORT_FILTER=<substr>` | matching imports with arguments (positive signal only) |
| `SHARPEMU_DUMP_GUEST_MEMORY` | specs `0xADDR`, `reg±off`, `[ptr]+off` (one `±` per bracket level) |
| `SHARPEMU_GUEST_ARGS` | up to two guest argv entries (`skip_op`, `offline`) |

## Tools

| Tool | Use |
| --- | --- |
| `artifacts/gt7-codex-20260912/read-vm.py <pid> dump\|vm\|string <hex>` | read-only qword dumps, Adhoc VM frames, strings |
| `artifacts/gt7-opus-20260913b/tools/vfsstate.py <pid> <fileObject>` | GT7 VFS object → stream → device state (blocker B) |
| `artifacts/gt7-opus-20260913b/tools/peek.py`, `scanglyph.py` | glyph bitmap preview / find copies |
| `artifacts/gt7-opus-20260913/scan-strings.py <pid> <lo> <hi> <needle>…` | committed-memory string scan (script-native names) |
| `artifacts/gt7-investigation/tools/` (`memscan`, `adcdump`) | memory scan/dump; Adhoc script disassembly |
| `scripts/aerolib_catalog.py lookup <nid>` | name a NID offline |
| `artifacts/kyty` | KytyPS5 at the pinned release (below) |

Ghidra, headless, no GUI:

```powershell
$env:JAVA_TOOL_OPTIONS='-Duser.home=G:/Games/GT7/SharpEmu-Source/sharpemu-gt7/artifacts/ghidra-home'
& C:\Tools\ghidra_12.1.3_PUBLIC\support\analyzeHeadless.bat artifacts/gt7-ghidra/project gt7 `
  -process gt7_text.bin -noanalysis -readOnly -scriptPath scripts/ghidra -postScript DecompAt.java 0xADDR
```

Saved decompiles: `artifacts/gt7-opus-20260913b/*-decomp.log`. Vtables and
function-pointer tables are relocated — read them from a live process.

Recovered boot scripts: `artifacts/gt7-codex-20260912/*.ad.diss`,
`artifacts/gt7-investigation/adc-disasm/`. GT7's data files are plaintext except
`gt.idx`; `/scripts/gt7/main.adc` is `contents/D/4/PM1EF` (`ADCH015`).

## KytyPS5 as a reference

Latest release, confirmed via `/releases/latest` on 2026-09-13:
**`KytyPS5-2026-09-12-d3d7bd3`** (commit `d3d7bd33f8eb4996cf198c430bb2e4fa4bf518eb`),
checked out at `artifacts/kyty` (ignored). The API's release list is not in
publish order — use `/releases/latest`. Compare against this pin, never moving
`main`; open PRs (477, 500, 506) are not released code and their fps reports are
unverified.

Kyty is another implementation, not a hardware specification: validate a
semantic change against the guest's own use. Useful files: `src/libs/avPlayer.cpp`
(`AvPlayerInitDataEx`, `FileStreamer`), `src/libs/libAmpr.cpp` (APR resolve,
stat, size, error returns). Kyty has no GT7 notes. Reusing its code needs its
licence and attribution.

Other references: shadPS4 (PS4; AvPlayer at `artifacts/shadps4`), prosper (PS5
libkernel/AGC second opinion), GTAdhocToolchain (Adhoc disassembler). GTToolsSharp
cannot open this title's volume.

## Traps

- **An unresolved import returns `ORBIS_GEN2_ERROR_NOT_FOUND`**, not 0; a title
  branching on it silently skips a whole subsystem.
- **Instruments:** `SHARPEMU_WATCH_GUEST_EXEC`/`_WRITE` cannot see the title's
  main thread — use `SHARPEMU_BREAK_GUEST_EXEC`. One-shot probes show only the
  first hit; a silent probe deep inside a function says nothing about the
  function. Run a positive control in the same run.
- **Do not probe `getRenderContext` (`0x801DFA610`)** — it has crashed boots.
- **Read state before teardown.** A read after a callback's `close` (or any
  cleanup) reports the torn-down object; this produced the wrong "no stream"
  conclusion for the movie.
- **Heap addresses move between boots**, and deref-spec watches resolve once,
  early. Re-derive pointers live.
- **Grep limits hide results.** A truncated search once hid an existing export
  and led to a duplicate implementation.
- **Function boundaries:** prefer `call rel32` targets; confirm with a decompile
  that the code you expect is inside. Hand disassembly misses enclosing
  conditions.
- **Progress signals:** log volume, state transitions, task counts and
  `submitted_fps` all move while nothing is visible. Use `presented_fps` and a
  swapchain dump.
- **Traces name things their own way** (`videoout.submit_flip`, not
  `SubmitFlip`); check the emitted string before concluding "never called".
- **Do not attach a Windows debugger** to SharpEmu; it destroys the guest.
- **Do not poke render-context or login state** to force progress.

## Retracted or superseded conclusions

Recorded in the log so they are not repeated: the RaceMusicManager blocker
(§6h, corrected in §6l), the video-out flag chain (§6p/§6q/§6s, corrected in
§6r/§6t/§6y), "sequence never started" (§6af; §6ao/§6ap, corrected in
Appendix A), "GT7 never renders" (§6au, retracted), "metrics-only font use"
(§6bc, corrected in §6bf), the `initContext` exit hypothesis (§6bd, corrected in
§6be), and "the movie's VFS open returns no stream" (§6bf, corrected in §6bg).
