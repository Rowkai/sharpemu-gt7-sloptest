<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# GT7 boot investigation

Chronological evidence behind each change on this branch, plus the techniques
used to get it. **Current status, blockers and next steps are in
[README.md](README.md)**; this log is history, and later sections correct
earlier ones rather than rewriting them.

Sections whose conclusions no longer hold:

| Section | Claim | Corrected by |
| --- | --- | --- |
| 6h–6k | boot blocks on `GTSound::RaceMusicManager` | 6l (object is `MENU::mUpdateContextPS4`), 6n |
| 6p, 6q, 6s | video-out flags gate the menu | 6r, 6t, 6y |
| 6y | `finishProject` is the only `+0x94` writer; "never executes" | 6ag, 6av |
| 6af | the System sequence's thread never runs | 6ag, 6ah |
| 6ao, 6ap | `waitLaunchSequence(nil)` returns at once; main blocks at `main_loop` line 58 | Appendix A (the scheduled callback is `boot`, not `main_loop`) |
| 6au | GT7 never renders | retracted in place |
| 6bc | GT7 uses libSceFont for metrics only | 6bf |
| 6bd | `initContext` exits before `startPage` | 6be (IME keyboard close wait) |
| 6bf (movie) | GT7's VFS open returns no stream | 6bg (the open succeeds; the read was after `close`) |
| 6bg (movie) | size 0: the open completes late, or the size call is mis-run/mis-read | 6bh (SharpEmu dropped the callback's RAX; the open was never late) |
| 6bh (movie picture) | "no draw binds the movie frames" (movie1 watched AvPlayer's buffers) | 6bi (GT7 copies each frame into its own ring; those copies *are* bound) |
| 6bi | movie/composite command traces establish rendering; last-writer mismatch suggests a movie overwrite | 6bj (movie draw throws during texture creation; cited writer sample predates playback) |
| 6bi (screen) | "none of it reaches the screen" - every playback dump identical to the dialog | 6bk (with scaled formats created, the movie's output does reach the flip) |
| 6bk | "vk.texture_refresh fires zero times"; "the composite samples zero - suspect the sampler border" | 6bl (the trace was disabled; GPU readback shows the sampled images hold the right values) |
| 6bl | the movie shader "expects 10-bit"; the 0x0060 branch skips the YUV block; 188 binds prove stale content | 6bm (the branch target at 0x1200 is an 8-bit 1/255 path; the movie is 8.558 s, not the capture length) |
| 6bm | "the SPIR-V is flattened, so both YUV paths execute"; SDWA-to-VCC as a lead | 6bn (the translator is a PC dispatcher; s106 is VCC. Markers show only the 8-bit block runs) |
| 6bo (drives 4-5) | the title stalls entering the brightness page | 6bo (SHARPEMU_GUEST_IMAGE_DUMP_CONTINUOUS dumped every frame, ~1 fps) |
| 6bo (draft) | after the first press GT7 switched present branch; `capwin.ps1` sees every branch | 6bp (flips and render work stopped; desktop capture returns stale frames) |

---

## 1. AMPR completions used the wrong kevent filter

**Symptom.** GT7 launched, loaded its modules, spawned ~24 threads, and then
sat there. No window content, no crash, no stall warning. Earlier logs showed
the process alive for over two minutes with nothing happening.

**What the logs showed.** With `SHARPEMU_LOG_EQUEUE=1` plus
`SHARPEMU_LOG_AMPR=1`, the boot sequence is:

```
equeue.create      handle=2
equeue.add_user    handle=2  registrations=1
equeue.wait-block  handle=2  thread='QWRKR'
ampr.read_file     id=0xB1D4630D size=0x2DC2B6 read=0x2DC2B6 result=0 path='...\contents\gt.idx'
equeue.add_ampr    handle=2  registrations=2
ampr.write_equeue  cmd=0xEC000536F0 arg0=0x0 arg1=0xEC000536B8
equeue.wake        handle=2  ident=0x0 filter=-16 data=0xEC000536B8
pthread_cond_wait-enter  cond=0x7FFFF01FF418 mutex=0x7FFFF01FF410 waiters=1
equeue.wait-resume handle=2  thread='QWRKR'  result=0
equeue.wait-block  handle=2  waiter=2
```

The read succeeded, the completion was queued, the worker woke, took the
event — and went straight back to waiting without acting on it. The main
thread stayed parked on the condvar at `0x7FFFF01FF418` (its own stack)
forever.

Thread snapshots confirmed the shape: everything blocked except `Netwk`,
which kept calling imports (2.6k, then 5.3k, then 10k, …). That is why the
stall watchdog never fired.

**The proof.** GT7's pump is at `0x8040A3DC7`, calling `sceKernelWaitEqueue`
through its PLT. Disassembling what follows:

```
0x8040A3DD9  call  sceKernelGetEventFilter
0x8040A3DE1  mov   r13d, eax
0x8040A3DE4  call  sceKernelGetEventId
0x8040A3DEC  mov   r12, rax
0x8040A3DEF  call  sceKernelGetEventData
0x8040A3DF4  cmp   r13d, -0x19            ; -25: AMPR completion
0x8040A3DF8  je    0x8040A3E17            ;   -> sceKernelDeleteAmprEvent, then handle
0x8040A3DFA  xor   r13d, 0xfffffff5       ; -11: user event
0x8040A3DFE  xor   r12d, 0xfafae0e0       ;      with this identifier
0x8040A3E09  or    r12d, r13d
0x8040A3E0C  je    0x8040A3E37            ;   -> sceKernelDeleteUserEvent
0x8040A3E0E  ...                          ; otherwise: discard and loop
```

`-11` already matches `KernelEventFilterUser`, and `0xfafae0e0` is exactly the
identifier GT7 passes to `sceKernelAddUserEvent`, so the neighbouring
constants corroborate the read. An event that is neither branch is silently
dropped — which is what `-16` was doing.

**Independent confirmation.** Before the disassembly landed, a temporary env
override on `KernelEventFilterAmpr` allowed sweeping the value. 25 s per run,
counting `ampr.read_file` and `equeue.wake`:

| filter | reads | wakes |
|---|---|---|
| -13, -15, **-16** (previous), -20, -23, -24, -26, -27, -30 | 1 | 1 |
| **-25** | **55** | **114** |

Only `-25` lets boot proceed, which matches the `cmp r13d, -0x19` exactly.

**Ruled out along the way.**

- *Not the unresolved imports near the hang.* `sceRudpInit` (`amuBfI-AQc4`),
  `sceAppContentTemporaryDataGetAvailableSpaceKb` (`SaKib2Ug0yI`) and
  `sceKernelMkdir` returning `ALREADY_EXISTS` all occur around the stall but
  execution continues past every one of them.
- *Not the udata plumbing, though that is also wrong.* GT7 registers its
  streaming context with
  `sceKernelAddAmprEvent(eq=2, ident=0, udata=0xEC000536B8)`, but
  `EnqueueEvent` ships the event's own `UserData` instead of the
  registration's. Instrumenting
  `sceAmprCommandBufferWriteKernelEventQueue_04_00` shows why that is empty:

  ```
  [DIAG] write_equeue args equeue=0x2 ident=0x0 rcx=0xEC000536B8 r8=0x0 r9=0x2DC2B6
  [DIAG] deliver kevent  ident=0x0 filter=-16 flags=0x20 data=0xEC000536B8 udata=0x0
  ```

  `r8` is 0 and `r9` still holds the previous read's byte count, so there is no
  fifth argument and the `userData` parameter read from `r8` is fiction. A fix
  that takes udata from the registration was written and tested and **changed
  nothing** — GT7 reads `data`, not `udata` — so it was dropped rather than
  shipped. Still worth doing as a separate correctness change.

**Left open.** `KernelEventFilterAmprSystem` is still `-17` and untested; GT7
does not call `sceKernelAddAmprSystemEvent`. If `-25` is right for AMPR then
`-17` is suspect too, but there is no evidence either way yet.

---

## 2. libSceUsbd was unimplemented, so the USB poll thread spun

**Symptom.** After the AMPR fix, one NID dominated everything: `+wU6CGuZcWk`
(`sceUsbdHandleEventsTimeout`), ~250,000 calls in 90 seconds, all unresolved,
each emitting a warning line. Boot-time stderr was 607 KB, nearly all of it
that one message.

**Cause.** An unresolved import returns immediately; the real call blocks for
the caller's timeout. GT7 runs USB polling on its own thread, so "return
immediately" turns the poll into a busy loop that burns a core and floods the
log — and the logging is slow enough to starve the rest of the guest.

**Fix.** `src/SharpEmu.Libs/Usbd/UsbdExports.cs`. SharpEmu does not pass host
USB devices to the guest, so the library models an empty bus: init succeeds,
`sceUsbdGetDeviceList` writes a null array and reports zero devices, and the
event pump waits out the caller's `timeval` before returning success.

The API mirrors libusb 1.0 name-for-name, with Sony dropping libusb's explicit
context parameter, so `sceUsbdHandleEventsTimeout` takes the `timeval` in
`rdi` and `sceUsbdGetDeviceList` takes the out-pointer in `rdi`. The observed
registers are consistent with that — both are stack addresses. The wait is
capped at 50 ms because a guest thread parked inside an HLE export cannot be
rescheduled; the caller polls, so it simply calls again.

**Result.** Spin gone, stderr 607 KB → 75 KB.

---

## 3. Two missing font calls broke graphics init

**Symptom.** With the USB spin gone, boot ended in an access violation. The
last guest calls before it were a font setup loop, run once per font:

```
sceFontOpenFontMemory      (KXUpebrFk1U)
sceFontBindRenderer        (3OdRkSjOcog)
sceFontSetScalePixel       (N1EBMeGhf7E)
sceFontSetEffectWeight     (v0phZwa4R5o)
sceFontSetEffectSlant      (TMtqoFQjjbA)
sceFontRebindRenderer      (Z2cdsqJH+5k)   <- unresolved
sceFontCloseFont           (vzHs3C8lWJk)
...
sceFontGetCharGlyphMetrics (L97d+3OgMlE)   <- unresolved, x2
```

then

```
Code at RIP: 48 8B 40 08   mov rax, [rax+8]
Code before: 48 8B 40 50   mov rax, [rax+0x50]
```

a pointer chain walked off a structure nothing had filled in.

**Fix.** `src/SharpEmu.Libs/Font/FontExports.cs`. `sceFontRebindRenderer`
swaps the renderer on an open font; nothing in the current pipeline is tied to
a renderer instance, so it acknowledges the swap exactly as
`sceFontBindRenderer` already did. `sceFontGetCharGlyphMetrics` reports the
same `SceFontGlyphMetrics` as the existing `sceFontGetRenderCharGlyphMetrics`
without requiring a bound renderer; the two now share one writer. The metric
values are still placeholders inherited from the existing render-variant
implementation — real glyph metrics are separate work.

**Result.** This is the change that unblocked graphics. Same 90 s budget:

| run | fixes applied | Vulkan ready | splash | threads | max import # |
|---|---|---|---|---|---|
| b1 | AMPR | no | no | 54 | 249,997 |
| b2 | AMPR + usbd | no | no | 55 | 246,987 |
| b3 | AMPR + usbd + font | **yes** | **yes** | 56 | **3,667,538** |

Run b3 reaches:

```
Vulkan device: NVIDIA GeForce RTX 5080 (DiscreteGpu)
Vulkan VideoOut ready: 1920x1080, format=B8G8R8A8Unorm
Vulkan VideoOut recreated swapchain: 1920x1080
Vulkan VideoOut presented splash: 3840x2160
Scheduled guest thread 'StBGM' ... priority=314
```

and streams 56 assets across `gt.idx`, `M\P\PZZ8P`, `J\Z\6H74F`, `G\W\6YTG3`
and `P\0\P8VS2` (a 22 MB read) before the fault described in the README.

---

## 4. The fault occurs while reporting an ADHOC load failure

> Superseded by section 5. The reporting path described here is real, but
> the cause it reaches for is not: see [section 5](#5-apr-file-ids-with-bit-31-set-were-abandoned-unread).

A fresh Release build reproduces the same splash and fault at `0x800264EC1`.
Stdout, immediately before the recent-import dump, contains:

```
ADHOC(cannot open "/scripts/gt7/main.adc".)
```

Static control-flow inspection places `0x8010D8CC0` in diagnostic construction
and reporting. It calls `0x801BD7E00`, which reaches `0x802928210`, then
`0x800264B40`, then the faulting collection walker. This makes the null access
a failure in the reporting path, not evidence that the faulting object is a
script container. Whether the script's absence is expected is still unknown.

The earlier interpretation that lookup necessarily succeeded was too strong:

- `0x800264CA0` accesses a singleton stored at `0x8068CF8E8`, constructed by
  `0x801097310`.
- On a lookup miss, `0x800E26C90` supplies an explicitly zeroed shared-pointer
  payload to the node constructor at `0x800E26E50`.
- That constructor creates a `0x48`-byte node with multiple vtable pointers
  and copies the empty payload into `+0x20`/`+0x28`. Runtime register windows
  match this layout, including null `+0x20` and insertion sequence `+0x40=1`.
- The accessor then assumes a populated collection and dereferences it.
  This supports missing prior registration; it does not identify the emulator
  behavior responsible, and is not evidence of memory-copy corruption.

Two tempting changes are unsupported by the evidence. A root-path alias would
not provide this script: no loose `main.adc` exists in the extracted title
tree, and the IO trace contains no matching host open. Archive membership has
not been established. Also, the main initializer is not skipped: eboot entry
`0x8010EE100` calls `0x800000010` (its `DT_INIT`) at `0x8010EE12D`, before
calling game main. Forcing that initializer again risks duplicate setup.

The next target is the registration callback for this key and its prerequisite.
Runtime references to `0x8068CF920` and `0x8068D0120` identify additional
registration metadata to inspect; their relationship to this key is not yet
established. No guest patch or speculative success-returning export was added.

**Diagnostics and verification.** The runtime reference scan exposed a slow
shared helper: `IcedDecoder.TryReadGuestBytes` made 15 separate memory reads
for every full instruction window. It now attempts one bulk read and retains
the byte-wise fallback for partially readable windows. Both memory-interface
overloads share the implementation. Tests verify one read for mapped windows,
partial/unmapped windows, and length clamping. A separate equeue regression
asserts the literal AMPR filter `-25`, with a polling timeout to avoid hanging
on failure. Release build: zero warnings/errors. Library suite: 860 passed
(854 before these tests). These are diagnostic and regression improvements;
the boot fault remains unresolved.

The post-change GT7 run completed the requested three-target reference scan
and pointer windows in about 43 seconds total (capture creation to final log
write), with 56 reads, 56 guest threads and the same splash/fault. The earlier
scan was interrupted while incomplete; this is not a controlled speedup ratio.

Local captures are under ignored `artifacts/gt7-investigation/`: `baseline`,
`io`, and `registry` runs, plus `refscan`/`bulk-refscan` diagnostics, each with
separate `.out.log` and `.err.log` streams. They are local evidence, not
committed game assets.

---

---

## 5. APR file ids with bit 31 set were abandoned unread

This supersedes the reading in section 4. The access violation there is real,
but it is the tail of an error-reporting path; the defect is upstream and has
nothing to do with the registry entry or with any missing import.

**Symptom.** Boot reached the splash, streamed 56 reads across 5 files, printed

```
ADHOC(cannot open "/scripts/gt7/main.adc".)[build 779e...];C:\pdi\src\gt7\maintenance\adhoc\src\h_adhoc.cpp:267;
```

and faulted at `0x800264EC1` while formatting that diagnostic.

**The script is present, and the emulator resolves it correctly.** Decoding the
crash dump's recent-import ring (stdout) gives the last calls before the error:

```
#3669640 snprintf                          rsi=0x104   builds a MAX_PATH buffer
#3669642 sceKernelAprResolveFilepathsToIds rsi=0x1     resolve one path
#3669647 .. #3669674  snprintf x5                      builds the error text
#3669690 write(fd=1)                                   emits it
```

Nothing runs between the resolve and the error on that thread. The snprintf
sizes identify the message field by field: 22 = `/scripts/gt7/main.adc` + NUL,
37 = `cannot open "..."`, 65 = the 64-char build hash, 49 = the `h_adhoc.cpp`
path, 4 = `267`. So that resolve is the ADHOC open.

With `SHARPEMU_LOG_IO=1` the resolve **succeeds**:

```
apr_resolve_ids path='/app0/contents/D/4/PM1EF' host='...\contents\D\4\PM1EF' index=0 count=1 id=0xF56F8C58
```

and `contents/D/4/PM1EF` is 313,813 bytes beginning `41 44 43 48 30 31 35 00`
= `ADCH015`, GT7's compiled-ADHOC magic. The file exists, `gt.idx` maps to it,
and SharpEmu hands back a valid id. GT7 never issues a read for it.

**The proof.** Correlating every resolved id in one boot against whether the
title ever read it:

| path | id | bit 31 | reads |
|---|---|---|---|
| `gt.idx` | `0xB1D4630D` | set | 1 |
| `J/Z/6H74F` | `0x2DA7C644` | clear | 3 |
| `G/W/6YTG3` | `0x43B8F218` | clear | 1 |
| `M/P/PZZ8P` | `0x1D49F606` | clear | 50 |
| `B/U/61CKX` | `0xBAB9D6F1` | set | **0** |
| `I/X/6K76L` | `0x8472AE74` | set | **0** |
| `E/D/6K6AB` | `0xBBD9E716` | set | **0** |
| `Z/T/PBKEG` | `0xB0493624` | set | **0** |
| `P/0/P8VS2` | `0x3908F872` | clear | 1 |
| `5/7/6VIOL` | `0xD505C9BF` | set | **0** |
| `D/4/PM1EF` | `0xF56F8C58` | set | **0** |

Every id with the sign bit clear was read. Every id with it set was not. This
is not a time cutoff: `B/U`, `I/X`, `E/D` and `Z/T` were all resolved *before*
`P/0/P8VS2`, which was read.

`gt.idx` is the single exception, and it is also the only file in a whole boot
that goes through `stat` and `sceKernelAprGetFileStat`, so its bootstrap path
handles the handle differently from the general asset path.

The guest side agrees. The APR resolve wrapper has two call sites,
`0x8040A4619` and `0x8040A47F0`; the extended `apr_resolve_ids` trace shows all
11 resolves come from the second, which turns the id into a handle:

```
0x8040A47E7  mov  dword ptr [rcx], 0xffffffff   ; 4th arg, pre-seeded
0x8040A47F0  call 0x801923650                   ; sceKernelAprResolveFilepathsToIds
0x8040A47F5  test eax, eax
0x8040A47F7  je   0x8040a4809
0x8040A4809  mov  eax, dword ptr [rbp - 0x14c]  ; the id
0x8040A480F  bts  rax, 0x3e                     ; handle = id | (1 << 62)
```

`mov eax, ...` zero-extends, so the 64-bit handle is positive. Somewhere
downstream GT7 keeps that handle — or the id — in a signed 32-bit slot, where
`0xF56F8C58` is negative and reads as failure.

**Fix.** `src/SharpEmu.Libs/Ampr/AmprFileRegistry.cs`. Every id the registry
publishes or looks up is masked to 31 bits (`MaskId`). The mask is applied on
registration, on lookup, on the v3 index-cache save and on its load, so a
stale cache written by an earlier build still resolves. The mask is
idempotent, so a title that ships precomputed ids lands on the same slot
whichever form it passes.

**Result.** Same host, 100-second run:

| | before | after |
|---|---|---|
| outcome | access violation `0x800264EC1` | no crash, still streaming at the cap |
| `ADHOC(cannot open ...)` | printed | absent |
| `ampr.read_file` | 56 | 13,287 |
| distinct files read | 5 | 156 |
| `apr_resolve_ids` | 11 | 79 |
| guest threads | 56 | 57 |
| max import number | 3,669,xxx | 8,804,316 |

**Cost, and what is not fixed.** Losing a bit costs collisions: this title
registers 372,393 aliases over 93,103 files, and the masked run indexes
372,380 distinct ids against 372,393 unmasked — 13 extra collisions on top of
the 19 the unmasked scheme already had. A collision silently serves the wrong
host file. Nothing has been observed to hit one, and the registry does not
detect it. Nor is it established that Sony's real ids are this FNV hash at
all; the hash is SharpEmu's own choice, and the only new constraint proven
here is that whatever it returns must be non-negative.

**Ruled out along the way.**

- *Not a missing import.* All 15 unresolved imports in a full boot were
  resolved to names by recomputing NIDs (see Techniques) and decoding their
  `NID#lib#module` tags: `sceRudpInit`,
  `sceAppContentTemporaryDataGetAvailableSpaceKb`, `sceKernelAddHRTimerEvent`,
  `sceHmd2Initialize`, `sceDeviceServiceInitialize`, `sceShareFeatureProhibit`,
  `sceShareSetScreenshotOverlayImage`, two in `libSceVideoOutVrrStatus`, and
  five in `logiWheel` — a Logitech-wheel PRX sitting in the title directory
  that SharpEmu never loads, since only `sce_module` is preloaded. Each is hit
  exactly once and none is on the script path. This closes the "work the
  remaining one-shot unresolved imports" item from section 4.
- *Not the archive layer, and not a missing file.* `gt.idx` is fully encrypted
  (entropy 8.000 bits/byte over its first megabyte, all 256 byte values
  present), so it cannot be inspected statically — but GT7 decrypts it and
  resolves 11 paths through it correctly, the script's included.
- *Not the APR export's argument handling.* GT7 passes a fourth argument in
  `rcx` that `sceKernelAprResolveFilepathsToIds` does not model. The open call
  site pre-seeds it with `0xFFFFFFFF` and never reads it back; only the other
  call site, an existence predicate at `0x8040A4619`, tests it, and a non-zero
  value there means success, which the untouched pre-seed already satisfies.
  Leaving it unwritten is not what broke the open.

---

---

## 6. Past the fault: boot blocks waiting for GTSound::RaceMusicManager

With section 5's id fix the access violation is gone and boot reaches the
splash, so this section starts where that one ends. Two more emulator defects
were found and fixed on the way, but neither is what holds the boot.

### 6a. `scePthreadCondSignalto` was unresolved

**Symptom.** Boot reached the splash and went silent. The last event in the
log, four lines before the silence, was

```
Scheduled guest thread 'DlyLd'  handle=0x000001DF51614910
Import#218629 unresolved: nid=o69RpYO-Mu0  rdi=0x806E1A308  rsi=0x000001DF51614910
```

`rsi` is exactly the handle of the `DlyLd` (delay-load) thread created on the
line above. GT7 creates the thread and then wakes it by name.
`scePthreadCondSignal` and `scePthreadCondBroadcast` were both implemented;
only the `to` variant was missing, so the call returned without signalling and
that thread never woke. It fires exactly once in a boot, which is why it never
appeared before — nothing ever got this far.

**Fix.** `src/SharpEmu.Libs/Kernel/KernelPthreadCompatExports.cs`. Routed to
the existing signal core with `broadcast: true`. Waking extra waiters is safe —
a condition variable's waiters must re-check their predicate on wake — and it
guarantees the named thread is among those woken, which targeting the waiter
queue by handle would not if the thread has not reached its wait yet.

**Result.** `DlyLd` goes from parked to ~9,800 imports. It does not change how
far boot gets.

### 6b. The vblank kevent ident did not match hardware

**Symptom.** None directly — this was found while chasing a wrong theory (see
below), and is a latent correctness bug rather than a boot blocker.

**What was wrong.** `VideoOutExports.cs` carried

```csharp
// Distinct internal ident for vblank events. Games interpret events through
// sceVideoOutGetEventId (mapped below), so the exact value is internal; only
// its distinctness from the flip ident matters for GetEventId/GetEventData.
private const ulong SceVideoOutInternalEventVblank = 0x40;
```

The premise is wrong in general: the ident travels to the guest as the raw
kevent ident, and a title may read it straight off the kevent rather than
translating. The real values are flip `0x6` and vblank `0x7`. Flip already
matched; vblank was `0x40`, a value hardware never produces.

**Fix.** vblank `0x40` -> `0x7`. `sceVideoOutGetEventId` still maps the pair to
the public ids (0 = flip, 1 = vblank), so titles that translate are unaffected.

**A wrong turn worth recording.** The theory that produced this was that GT7
read the raw ident with `sceKernelGetEventId` and discarded everything outside
`{0,1,8,0x10}`, which would have meant every vblank was thrown away. GT7's pump
does dispatch on exactly that set, and it does sit next to `sceKernelGetEventFilter`
calls, so the reading looked solid. It was wrong: resolving the PLT thunk's GOT
slot through `JMPREL` gives

```
GOT 0x59107D0 -> 23CPPI1tyBY = sceKernelGetEventFilter
GOT 0x59107E0 -> mJ7aghmgvfc = sceKernelGetEventId
GOT 0x5910838 -> U2JJtSqNKZI = sceVideoOutGetEventId     <- the one in the -13 branch
```

The `-13` branch calls the **VideoOut** translator, not the kernel one. The
`{0,1,8,0x10}` set is the *public* `OrbisVideoOutEventId` enum
(flip/vblank/setmode and one more), not raw idents, and the translation layer
was doing its job all along. **Resolve the thunk before reading a dispatch.**
Two neighbouring calls in the same basic block went to two different libraries.

### 6c. Where the boot actually stops

**Method.** `SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1` with
`SHARPEMU_PERIODIC_SNAPSHOT_SECONDS` gives a per-thread `imports=` counter
every N seconds. Diffing consecutive rounds separates threads that are making
progress from threads that have stopped, which a single snapshot cannot.

Every game-logic thread stops at the same round and never moves again, while
the periodic infrastructure keeps ticking forever:

| thread | imports | after the stall |
|---|---|---|
| `SceSndzAudioOutMain` | 1,009,438 | +12,000 per round |
| `TmBluetoothMainThread` | 142,329 | +1,680 per round |
| `Netwk` | 46,471 | +549 per round |
| `PGL Event` / `Vsync` | ~35,500 | +420 per round |
| `QWRKR` (AMPR worker) | 132,883 | **frozen** |
| `DlyLd` | 9,853 | **frozen** |
| `FWRKR` | 3,985 | **frozen** |
| `Rendr` | 2,368 | **frozen** |
| `Job#1..5` | 239-491 | **frozen**, all on `sceKernelWaitSema` |
| `Job#0` | **11** | **frozen, but state=Running** |

`Job#0` is the odd one: it is *running* guest code with a frozen import
counter. Everything else is parked behind it.

**The spin, and what it is not.** `Job#0`'s last import was
`sceKernelSignalSema`, and just past its return address sits a 1024-iteration
`pause` loop polling a 16-bit queue-slot sequence, falling through to
`_Thrd_yield` and starting over. That looks exactly like a consumer stuck
waiting for a producer to publish a slot, and it is the wrong answer. The RIP
sampler (`SHARPEMU_PROFILE_GUEST_RIP=1`) says so directly — `Job#0` is 100%
running while every other thread is 100% parked, and its samples land on

```
0x800135352 = 11.1%   0x80013536A = 1.5%   0x800135357 = 0.8%
0x800135384 = 0.5%    0x800135399         0x8001353E7
```

every one of which is in the *work-stealing scan* loop, not the sequence spin.
The scan walks six queues — three per-worker-group at
`[rbx+0x138c0] + group*0xcc0`, three global at `[rbx+0x40/0x48/0x50]` against
`[rbx+0x80/0x88/0x90]` — and then:

```
0x800135350  pause
0x800135352  <scan all six queues; any head != tail -> go claim it>
0x8001353DE  movzx eax, byte ptr [rbx+0x13998]   ; shutdown flag
0x8001353ED  cmp   byte ptr [r14+0x1a], 0        ; this worker's group index
0x8001353F2  je    0x800135350                   ; group 0 -> pause and rescan, forever
0x8001353F8  mov   eax, dword ptr [rbx+0x13900]  ; every other group -> sleep accounting
0x8001354AC  sceKernelWaitSema([rbx+0x13940])
```

`Job#0` is group 0, so **its busy-poll is by design**: GT7 keeps one worker
spinning to cut dispatch latency and lets the rest sleep on the semaphore.
That also explains the frozen import counter — the loop's only call is
`_Thrd_yield`, a **libc** export served by the loaded `libc.prx` rather than an
HLE stub, so it never increments the counter while burning a core.

So there is no stuck producer and no livelock here. The finding is simpler and
duller: **the job system is idle and healthy, and nothing ever submits work to
it.** A probe on the control word confirms consistent, unremarkable state:

```
[PROBE][JOB] rip=0x800135352 rbx=0x806E06980 ctrl@0x806E1A280=00050005 r14=0xE400BF9AC8
```

`0x00050005` is five in each 16-bit half with the sign bit clear, sampled
identically across the whole stall. All six queues are empty every time round.

**The blind spot, now closed.** Neither the guest-thread snapshot nor the
RIP sampler enumerates the title's main thread — it is not a registered guest
thread. Section 6d shows the stall snapshot had it all along.

**Ruled out.**

- *Not the emulator's condvar plumbing.* Across one boot: 17,353 cond waits,
  17,331 signals, 433 distinct condvars. Only **11** signals land on a condvar
  nobody ever waited on, which is ordinary signal-before-wait. 17 condvars are
  waited on and never signalled — that count matches the parked threads, so
  they are parked correctly and the guest simply never signals them.
- *Not native-worker starvation.* `NativeWorkerMaxConcurrent` defaults to **2**,
  and `Job#0` holds one of those slots forever without returning, which looked
  like it would halve the pool. Running with
  `SHARPEMU_NATIVE_WORKER_MAX_CONCURRENT=16` produces byte-identical results —
  13,287 reads, 57 threads, one splash. Not the cause.
- *Not the fibers.* 128 fibers are initialised and exactly 6 run, all
  transferring cleanly, and no fiber ever returns or yields again. The 158
  `sceFiberGetSelf` failures are correct and explicitly handled by the caller:

  ```
  0x8007055E3  call sceFiberGetSelf
  0x8007055EA  jne  0x8007055f2
  0x8007055F2  xor  eax, eax        ; "no fiber" -> index 0
  0x8007055F4  sub  rax, [rip+...]  ; bounds-checked table lookup, default 2
  ```

- *Not a stuck AMPR read.* `ampr.read_file`, `apr.submit` and `ampr.complete`
  are all exactly 13,287, and `equeue.add_ampr` / `delete_ampr` both 13,287.
  `QWRKR` blocks on equeue 2 a 13,288th time with nothing left to deliver; that
  is an idle pump, not a lost completion.
- *Not a missing display event.* Equeue 3 is alive, waking 3,305 times with
  filter `-13`; `PGL Event` is its pump and is still cycling after the stall.

**Why the queues are empty** is answered in 6d: the thread that would submit
work is itself blocked. `sceKernelTriggerUserEvent` is likewise never called in
a whole boot, so the eq2 user-event kick never fires — same cause, not a
separate one.

Two filters GT7's `PGL Event` pump handles are never produced by SharpEmu:
`-14` (graphics) is emitted by `AgcExports` but never fires because no GPU work
is ever submitted, and `-15` has no constant anywhere in the codebase. Neither
is obviously the gate, and guessing a filter id is precisely the mistake
section 1 documents, so they need a pump-side proof before anyone implements
them.

---

### 6d. The main thread, and what it is actually waiting for

> The backtrace and mechanics below are correct and still the basis for
> everything after. The *identification* of the awaited object as belonging to
> the PSN in-game-catalog path is superseded by 6h, which follows the object's
> construction chain to `GTSound::RaceMusicManager`. This section reached its
> answer from an assert string inside one stack frame; 6h has direct evidence.

The blind spot in 6c turned out to be closed already. `LogStallWatchdogSnapshot`
prints a `Stall snapshot:` line built from `_cpuContext`, and that context *is*
GT7's main guest thread — `rsp=0x7FFFF01FF648` puts it on the main guest stack.
It runs on every `SHARPEMU_PERIODIC_SNAPSHOT_SECONDS` tick, independently of
whether the watchdog's progress timer has expired, so the data was in every
capture already:

```
Stall snapshot:    rip=0x700000001070 rsp=0x7FFFF01FF648 rbx=0xEC0309D028
Stall import-stub: nid=WKAXJ4XBPQ4 -> libKernel:scePthreadCondWait
```

The `Live hardware context (main thread …)` block in the same report is a
different thread — the *emulator's* main thread, which sits in
`win32u.dll` with an INFINITE timeout because it owns the SDL window's message
pump. That is healthy, and mistaking it for the title's main thread wastes a
lot of time.

**Main is blocked on a one-shot event.** The wait helper is `0x80077C7E0`:

```
0x80077C7E0  push rbp / mov rbp,rsp ...        ; rdi = event, rsi = timeout
0x80077C7F1  call scePthreadMutexLock          ; mutex at event+0
0x80077C7F6  cmp  byte ptr [rbx+0x19], 1       ; already-signalled flag
0x80077C7FA  je   <return immediately>
0x80077C7FC  mov  byte ptr [rbx+0x18], 1       ; mark waiter present
0x80077C800  lea  rdi, [rbx+8]                 ; cond  = event+8
0x80077C804  mov  rsi, rbx                     ; mutex = event+0
0x80077C819  test r14, r14
0x80077C81C  jne  <timed variant>
0x80077C81E  call scePthreadCondWait           ; untimed
```

Layout: mutex@0, cond@+8, payload@+0x10, waiting@+0x18, signalled@+0x19. For
this run the event is at `0xEC0309D028`, so the condvar is `0xEC0309D030` —
which is one of the 17 condvars the cond census found waited-on and never
signalled. `r14` is zero, so **main took the untimed branch**: there is no
deadline and it will never give up on its own.

**The call path.** Adding a guest RBP frame walk to the stall snapshot (see
Techniques) gives main's backtrace, which no existing tool printed:

| frame | ret | function | identified by |
|---|---|---|---|
| #0 | `0x800F853FE` | `0x800F853A0` | — |
| #1 | `0x800F8533A` | `0x800F852F0` | — |
| #2 | `0x801EB6AB4` | `0x801EB69C0` | car-part name strings |
| #3 | `0x8000B58EE` | `0x8000B5780` | `EL_TESTCAR_PRG20` |
| #4 | `0x8000ABD9D` | `0x8000ABAF0` | **`Network::psn::in_game_catalog::ParentContainer`** |
| #5 | `0x8000AA6A1` | `0x8000A9E60` | — |
| #6-#7 | `0x80065E3D3`, `0x80065E110` | `0x80065DD50` | car / livery names |
| #8 | `0x800C767C2` | `0x800C763F0` | `_C04_AFTERGOLD` |
| #9-#12 | `0x800C7B382`, `0x800FDA54C`, `0x801BD7514`, `0x8010EE13F` | — | eboot entry |

Frames #9-#12 are the same addresses the section 4 crash chain recorded, which
confirms this is the same boot phase, now reached and blocked rather than
faulted. Frame #4's function references the mangled RTTI name
`N…Network3psn15in_game_catalog15ParentContainerE`, so the awaited future
belongs to GT7's **PSN in-game catalog** (store) path.

**Corroboration from the import ring.** The recent-import ring records the last
64 dispatches and previously only printed on a crash, which is no use for a
title that hangs. Wiring `DumpRecentImportTrace()` into the periodic stall
snapshot gives main's final calls directly:

```
#10747095 sceRtcGetCurrentNetworkTick   ret=0x800C7C49F      (x4)
#10747425 scePthreadMutexLock           rdi=0x806D70498 rsi=0xE415A24300
#10747443 scePthreadMutexLock           rdi=0x806D70498 rsi=0xE415A27290
   ... 8 more, one global mutex, a different heap object each time ...
#10747783 scePthreadMutexLock  ret=0x80077C7F6  rdi=0xEC0309D028   ; the event's mutex
#10747784 scePthreadCondWait   ret=0x80077C823  rdi=0xEC0309D030   ; the event's cond
```

The last two entries match the `Stall snapshot:` line exactly, from an
independent mechanism, which settles that the snapshot's context really is
main and really is live. The lead-up is a walk over a collection — one global
lock (`0x806D70498` / `0x806D70A88`) taken per element — followed by four
network-time queries, and then the wait. Consistent with assembling a catalog
request.

Two things to know about this dump. The header line prints
`managed={Environment.CurrentManagedThreadId}`, which is the *watchdog* thread
doing the dumping (48), not the thread whose calls follow; the per-entry
`managed=` field is the one that identifies the caller — here every entry is
`managed=2`, main. And the ring is genuinely frozen: two snapshots 25 seconds
apart print byte-identical contents, first and last dispatch index unchanged.
Earlier iterations of the same loop *did* complete — the ring shows several
prior create/wait/destroy cycles on stack-allocated condvars that were
signalled normally — so this is one specific await that never returns, not a
subsystem that never worked.

**Ruled out, with evidence.**

- *Not the reported sign-in state.* `sceNpGetState` writes `1` while
  `sceNpGetNpReachabilityState` writes `0`, which looked like an inconsistent
  pair worth sweeping. Running with the value forced to 0, 1 and 2 produces the
  identical stall (`[rsp]=0x80077C823`) every time. The state GT7 is told does
  not change whether it waits.
- *Not any of the title-facing settings.* `SHARPEMU_BTHID_UNAVAILABLE=1`,
  `SHARPEMU_WRITABLE_APP0=1` and both together: same stall address. BTHID does
  remove one Thrustmaster FFB thread (57 -> 56 guest threads), so it takes
  effect; it just is not this blocker.
- *Not a missing WebApi call that got logged.* `sceNpWebApi*` has **zero**
  implementations in the tree and `sceHttp*` has six, so any call would surface
  as an unresolved import — and none appears in a whole boot. GT7 never reaches
  the WebApi layer, so the wait is decided before the request is issued.
- *Not a timeout GT7 is still counting down.* `r14` is zero at the wait, so
  main takes the untimed `scePthreadCondWait` branch and has no deadline. A
  5.5-minute run confirms it: five periodic snapshots, every one reporting the
  same `[rsp]=0x80077C823`, one splash, no progress. Every earlier run was
  capped at 70 seconds, so this was worth eliminating explicitly.
- *`SHARPEMU_LOG_NP=1` is nearly silent here.* Only a handful of NpManager
  exports call `TraceNp`, so the only line a full boot produces is
  `np.entitlement.initialize`. The flag is not a general PSN call trace.

**The setter is identified; its caller is not.** Scanning for
`mov byte ptr [reg+0x19], 1` and then decoding each hit's *enclosing function*
(a window scan misses it — the flag and the signal sit in different basic
blocks) finds the exact counterpart of the wait helper at `0x8009BBCC0`:

```
0x8009BBCC7  mov  rbx, rdi                 ; the same event object
0x8009BBCCA  call scePthreadMutexLock      ; mutex at +0
0x8009BBCCF  cmp  byte ptr [rbx+0x18], 1   ; waiter parked?
0x8009BBCD5  mov  r14, qword ptr [rbx+0x10]; payload
0x8009BBCD9  lea  rdi, [rbx+8]             ; cond at +8
0x8009BBCE5  call scePthreadCondSignal
0x8009BBCFA  mov  byte ptr [rbx+0x19], 1   ; no waiter -> just mark signalled
```

It has 24 direct callers. None passes `[reg+0xa8]` — the field main's future
lives in — directly; the closest is `0x8009BBBB0`, which sits immediately
before it and is the generic value-setting wrapper, so the chain fans out into
game logic from there. A waiter *is* parked on main's event, so if
`0x8009BBCC0` were ever reached for that object main would wake immediately.
It simply never is.

That makes the remaining question narrow and runtime-shaped: which caller
should have run, and what stopped it. The event address is stable across runs
(`0xEC0309D028`, allocation is deterministic), so a targeted trace on that
object — or on `0x8009BBCC0` with its `rdi` — would answer it directly. Working
forward from frame #4 to find which request GT7 believes it issued is the other
half.

One thing that is now certain and was not before: this is **not** a job-queue
problem, a condvar-plumbing problem, or a scheduling problem. It is a single
guest future that the title expects the online-catalog path to complete, waited
on with no timeout.

### 6e. The request is never submitted

Three runtime probes close out 6d's open question without needing a debugger
server. Each uses an existing facility plus a couple of lines.

**`Event::set` runs once per boot, on a different object.**
`SHARPEMU_PROBE_IMPORT_RET_ADDRESS` fires when an import's return address
matches, and `Event::set`'s *first* instruction after its prologue is a call to
`scePthreadMutexLock` — an HLE import. So probing return address
`0x8009BBCCF` is a breakpoint on `Event::set` for free:

```
SHARPEMU_PROBE_IMPORT_RET_ADDRESS=0x00000008009BBCCF
-> import-return-address-probe nid=9UK1vLZQft4 rdi=0x000000E400BBB7A8 saved_ret=0x8010A5814
```

One hit in a whole boot, on event `0xE400BBB7A8`, from caller `0x8010A580F`.
Main's event is `0xEC0309D028`. Because `Event::set` builds a frame
(`push rbp; mov rbp,rsp`) before that call, `saved_ret` is the caller of
`Event::set`, which is exactly the identity that mattered.

**The job system is drained, not stuck.** The job object lives at a fixed guest
BSS address (`0x806E06980`), so its queues can be read straight out of the
stall snapshot. All six are empty at stall time:

```
JOBQ global[0] head=0   tail=0   pending=0
JOBQ global[1] head=0   tail=0   pending=0
JOBQ global[2] head=144 tail=144 pending=0     <- 144 jobs ran and completed
(no per-group queue had head != tail)
```

So "work was queued and nobody ran it" is wrong. 144 jobs were submitted and
all of them finished.

**Nothing but main ever touches the awaited object.** Logging every import
handed a given guest pointer (`SHARPEMU_WATCH_GUEST_OBJECT`) gives the whole
life of `0xEC0309D028` across a boot — three calls, all `managed=2`:

```
scePthreadMutexInit  ret=0x80077D6FF  rdi=0xEC0309D028   ; main constructs the event
scePthreadMutexLock  ret=0x80077C7F6  rdi=0xEC0309D028   ; main locks it to wait
scePthreadCondWait   ret=0x80077C823  rdi=0xEC0309D030   ; main waits
```

That is the whole story. No worker, no network thread, no job, no callback ever
receives this object. **The producer is not slow or blocked — it never gets the
future at all**, so the conclusion is that the submission step between creating
the promise and awaiting it does nothing.

The object is created by `0x800C71B60`, called from `0x800C71BB8` — the same
`0x800C7xxxx` subsystem as boot frames #8 and #9. Main then descends through
frames #7..#0 and waits on the future it just made, having never handed it to
anybody.

**Where to pick this up.** The question is now narrow: between `0x800C71BB8`
(construction) and the wait, which call was supposed to hand the task off, and
why is it a no-op? That is a forward trace over a bounded stretch of one
thread's execution, not a search. The remaining unknown is whether the hand-off
is a virtual call landing on a null/stub implementation, or a registration into
a container that a never-started subsystem would have drained.

A caution recorded for whoever continues: an attempt to name the task's class
by walking its vtable to typeinfo produced mangled names (`ADHOC::BuiltinAttributeT`,
an `St10_Func_impl` of `ADHOC::Reflection::make_method_template`) that do not fit
a promise type, and the name pointers landed mid-string. Either the vtable
identification or the string mapping is wrong there; it was dropped rather than
built on. The runtime probes above are solid — prefer them.

### 6f. What GT7 actually depends on

Rather than keep reverse-engineering GT7 internals, this asks the emulator-side
question: which of SharpEmu's exports does the title rely on, and are any of
them hollow? `SHARPEMU_LOG_IMPORT_CENSUS=1` records the distinct set of exports
called and dumps it with the periodic snapshot.

**206 distinct exports** in a boot up to the stall:

| library | exports called |
|---|---|
| `libKernel` | 79 |
| `libSceFont` | 14 |
| `libSceAudioOut2` | 12 |
| `libc` | 11 |
| `libSceVideoOut` | 10 |
| `libSceAgc` | 7 |
| `libScePad` | 6 |
| `libSceUserService` | 5 |
| everything else | 1-4 each |

**The network surface is five calls.** In an entire boot GT7 touches only
`sceNetCtlGetState`, `sceNetCtlRegisterCallback`, `sceNetInetPton`,
`sceKernelGetOpenPsId` and `sceRtcGetCurrentNetworkTick`. No `sceHttp*`, no
`sceNpWebApi*`, no commerce calls. So although the awaited future belongs to a
PSN in-game-catalog code path (6d), **GT7 never issues a network request** —
the catalog work it is waiting on is entirely local.

`sceNetCtlGetState` reports `0` (disconnected), which is what an offline
emulator should say, so the title is not being misled about connectivity.

**Almost nothing it depends on is a stub.** Cross-referencing the 206 called
exports against their implementations, only **13** have a body that merely
reports success and does nothing, and every one is a benign init/open:

```
sceAgcGetIsTrinityMode          sceAudioOutInit        sceMouseInit
sceAgcGetRegisterDefaults2      sceAudioOut2Initialize sceMouseOpen
sceAgcGetRegisterDefaults2Internal                     sceUsbdInit
sceSystemGestureInitializePrimitiveTouchRecognizer     sceUsbdFreeDeviceList
sceSystemGestureOpen            _init_env              atexit
```

None of them submits work or owns a completion, so "a hollow stub swallowed the
request" is ruled out as an explanation for the hang. Combined with 6e — the
awaited object is never handed to anything — the conclusion is that the missing
step is inside GT7's own logic reacting to emulator state, not a SharpEmu API
that silently did nothing.

That matters for how to continue: the next move is **not** implementing more
exports. It is finding which piece of emulator-visible state GT7 consulted
before deciding not to dispatch the task.

### 6g. Proving the future is never registered, and one more missing export

**A pointer sweep settles 6e independently.** `SHARPEMU_FIND_GUEST_POINTER=<hex>`
sweeps readable guest memory for an 8-byte value at snapshot time. Run against
the awaited event across eight bands — guest image and BSS, the HLE allocation
arena, both game heap bands, the AMPR streaming targets, the large-asset band
and both guest stack bands, 875 MiB actually readable:

```
pointer-sweep 0x000000EC0309D028 referenced from 0x00007FFFF01FF5F0
pointer-sweep 0x000000EC0309D028 referenced from 0x00007FFFF01FF618
pointer-sweep complete: 2 references, 875 MiB scanned
```

Two references, both on **main's own stack**. The pointer is in no heap
structure, no global, no queue, no other thread's stack. Together with 6e's
import watch — three imports touch the object, all from main — two independent
mechanisms agree: **GT7 creates the future, never registers it anywhere, and
waits on it.** It did not fail to dispatch because a worker was busy or a
subsystem was down; the dispatch never happened.

A first pass of this sweep covered only four bands and missed the heap regions
AMPR actually streams into, which would have made "never registered" a lucky
guess rather than a result. Widening it first was the difference between the
two. Same lesson as the object watch, which originally checked only `rdi`/`rsi`
when the ABI passes six integer arguments.

**`sceAppContentTemporaryDataGetAvailableSpaceKb` was missing.** GT7 calls
`sceAppContentTemporaryDataMount2` and then sizes that mount. SharpEmu
implements the download-data sibling but not the temporary-data one, so the
call was unresolved: NOT_FOUND, output buffer untouched. shadPS4 implements it
as a 1 GiB report, which is exactly what SharpEmu's existing sibling already
does, so the fix is the sibling's body with the temporary-data NID.

Fixed in `src/SharpEmu.Libs/AppContent/AppContentExports.cs`. Unresolved
imports drop 15 -> 14. **It does not move the stall** — same address, same
13,287 reads, same 57 threads — so it is a correctness fix rather than a
boot fix, recorded here so nobody re-investigates it.

**What this rules out.** The remaining explanations for the hang do not include
a busy worker, a down subsystem, a lost completion, a hollow stub, or a missing
export on the request path. GT7 evaluated something and took a branch that
skipped the hand-off while still waiting. Finding that branch needs a trace of
main's execution between `0x800C71BB8` (the event's construction) and the wait
— the two addresses are known, and the window is one thread.

### 6h. The blocker, named: `GTSound::RaceMusicManager`
> **Superseded by 6l and 6n.** The object is `MENU::mUpdateContextPS4`,
> not an audio manager, and the cause is the VideoOut refresh-rate ordinal.
> This section is kept for the method, not the conclusion.


Walking the object's construction chain backwards names the subsystem GT7 is
waiting for.

**The await is not part of the streaming loop.** Raising the recent-import ring
depth (`SHARPEMU_IMPORT_TRACE_DEPTH`, added for this) to 20,000 shows main's
last 330,000 dispatches are **915 complete cycles** of one pattern:

```
MutexattrInit / Settype / MutexInit / CondattrInit / CondInit   construct an event
scePthreadMutexLock(0xEC000536B0)
sceKernelVirtualQuery
sceKernelAddAmprEvent(eq=2)
MutexLock(ret=0x80077C7F6) + CondWait(ret=0x80077C823)          await the read
CondDestroy / MutexDestroy                                      tear down, repeat
```

That is the AMPR streaming loop and **it completes 915 times**. In a deeper
65,536-entry window the same wait helper is entered **3,501 times** and returns
every time. The mechanism is demonstrably healthy.

The final wait is a different shape entirely — no construct, no
`sceKernelVirtualQuery`, no `sceKernelAddAmprEvent`. Just four
`sceRtcGetCurrentNetworkTick` calls, a walk over a collection (one global mutex
taken per element), and then a wait on a **long-lived heap event** created much
earlier. So the streaming loop finished; main then awaited something else.

**Following the object back.** The event lives at `+0x28` of an object created
by a factory with exactly one caller at each step, so the chain is unambiguous:

```
0x800C716C0   allocate 0x170C0 bytes (94 KB), construct, return as shared_ptr
0x800C71730   constructor -> 0x800C71B60 constructs the Event at +0x28
0x800C73470   caller: creates it, then
0x800C734A0     lea rdi, [rbx + 0xa8]        <- stores it at owner+0xa8
```

`owner+0xa8` is exactly the field main dereferences before waiting
(`mov rdi, [r15+0xa8]`, section 6d frame #1). The constructor's vtable operand
resolves to a typeinfo name of

```
St14_Ref_count_objIN7GTSound16RaceMusicManagerEE
  =  std::_Ref_count_obj<GTSound::RaceMusicManager>
```

and its neighbours in the string table place the call site:

```
GTFramework::Organizer::Impl::initialize
GTFramework::PauseManager
GTFramework::GhostManager
GTSound::RaceMusicManager
```

**So: GT7's boot blocks inside `GTFramework::Organizer::Impl::initialize()`,
waiting for `GTSound::RaceMusicManager` to signal that it is ready.** That is
consistent with `StBGM` — the start-BGM thread — being parked on a condvar at
waiter id 62 with 4 imports, never woken, from the first moments of boot.

This supersedes the reading in 6d that the await belonged to the PSN
in-game-catalog path. That identification came from an assert string inside one
frame's function; the construction chain above is direct evidence and should be
preferred. `Organizer::Impl::initialize` constructs many managers in sequence,
which is why frames in the middle of the stack reference unrelated car, livery
and catalog strings — they are all just neighbours in the same initialiser.

**What this changes.** The question is no longer "which future, and who owns
it". It is now specific and bounded: **what does `GTSound::RaceMusicManager`
wait for before it signals ready, and which piece of that never happens under
SharpEmu?** Its constructor calls one helper (`0x800C719A0`) four times, which
is the natural next thing to read.

Worth noting what is *not* implicated: the audio exports GT7 uses are all
implemented (13 `libSceAudioOut2` entries in the census), and
`SceSndzAudioOutMain` running at ~12,000 imports per snapshot round is what a
real-time audio pump looks like, not necessarily a spin. Do not assume it is
stuck without evidence.

### 6i. Following the named subsystem into audio
> **Superseded by 6n.** This section follows a class name that the runtime
> RTTI in 6l disproved. The AudioOut2 observations in it were never shown to
> cause the stall.


6h names the blocker as `GTSound::RaceMusicManager`. This is what the audio
path actually does, and what is and is not wrong with it.

**The sound engine is refreshing state, not rendering.** With
`SHARPEMU_LOG_AUDIO_OUT2=1` over 45 seconds:

| call | count |
|---|---|
| `sceAudioOut2PortGetState` | 11,225 |
| `sceAudioOut2GetSpeakerInfo` | 3,748 |
| `sceAudioOut2ContextPush` | **8** |
| `context-submit` / `context-submit-skip` | 15 / 15 |

Three ports are polled ~3,750 times each. **That is not a spin** — an earlier
reading of this as a stuck loop was wrong. Disassembling the caller
(`0x803591E60` region, inside the SNDZ engine whose render thread entry is
`0x803590C20`) shows a periodic multi-port state refresh at roughly 80 Hz,
which is what a real-time audio engine does every callback. What is notable is
the other number: **eight pushes in 45 seconds.** The engine is alive and
refreshing, and has essentially nothing to render — consistent with the music
manager never becoming ready rather than with the audio stack being broken.

**An unpopulated field, unproven significance.** `sceAudioOut2PortGetState`
writes a 0x20-byte struct of which SharpEmu fills three fields:

```csharp
BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], PortStateOutputConnectedPrimary); // 1
state[0x02] = channels;                                                                   // 2
BinaryPrimitives.WriteInt16LittleEndian(state[0x04..], -1);                               // volume
// remaining 0x1A bytes stay zero
```

The guest reads more than that:

```
0x803591F14  movzx eax, byte  [state+0x02]   ; channels
0x803591F28  movzx ebx, word  [state+0x00]   ; output word...
0x803591F31    shr al, 6 / and al, 1         ;   ...specifically BIT 6 of it
0x803591F3D  mov   r15d, dword [state+0x08]  ; a field SharpEmu never writes
0x803591F64  test  r15b, 1                   ;   and then branches on its bit 0
0x803592A88  cmp   word  [state+0x04], 0     ; volume, compared against zero
```

So three emulator choices feed guest branches: bit 6 of the output word (0
here, because the value written is 1), the `+0x08` dword (0, never written),
and `volume = -1`, which the comment in the source describes as "N/A for main"
and which the guest compares against zero. **None of this is demonstrated to
cause the hang** — it is recorded because the fields are read and the values
are arbitrary, which is exactly the shape of the vblank-ident bug in 6b. No
public definition of the PS5 `SceAudioOut2PortState` layout was found to check
them against; shadPS4 is PS4-era and has no AudioOut2.

**An intermittent crash, seen once.** One run in four faulted instead of
hanging: an access violation in guest code at `0x8007C8827`, executing
`mov dword [rdx+r12], 0x46425A53` — writing the ASCII tags `SZBF`/`SZBH`, so a
pool or block header writer. Three immediately following runs with identical
settings did not reproduce it, so it is nondeterministic and not a regression
from anything in this branch. Recorded so that if it becomes frequent, it is
already half-identified.

### 6j. Milestone-1 checkpoint: the RaceMusicManager start method runs

**Question.** Does the virtual start target at `0x800F5F950` fail to run, or
does it return before handing work to another subsystem?

**Bounded capture.** The existing Release executable was used because a fresh
build is currently blocked by the sandbox denying access to
`C:\Users\nzjam\AppData\Local\Microsoft SDKs` while evaluating the source
generator project. The owned run was launched with:

```powershell
$bin = (Resolve-Path 'artifacts\bin\Release\net10.0\win-x64').Path
$out = (Resolve-Path 'artifacts\gt7-investigation').Path + '\startprobe.out.log'
$err = (Resolve-Path 'artifacts\gt7-investigation').Path + '\startprobe.err.log'
$env:SHARPEMU_PROBE_IMPORT_RET_ADDRESS = '0x800F5F981'
$env:SHARPEMU_PERIODIC_SNAPSHOT_SECONDS = '10'
$env:SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = '1'
$env:SHARPEMU_IMPORT_TRACE_DEPTH = '2048'
Start-Process -FilePath ($bin + '\SharpEmu.exe') `
  -ArgumentList '"G:\Games\GT7\PPSA01317-app\eboot.bin"' `
  -WorkingDirectory $bin -RedirectStandardOutput $out -RedirectStandardError $err
```

The process ran for 35 seconds before the owned parent and mitigated child
were stopped. `startprobe.err.log` records one hit at the requested return
address:

```text
import-return-address-probe nid=3pcAvmwKCvM ret=0x800F5F981
  rdi=0 rsi=0 saved_ret=0x800C733CB
```

The NID resolves to `sceSystemGestureInitializePrimitiveTouchRecognizer`.
The same capture shows `StBGM` still blocked at `pthread_cond_wait`, imports 4,
and the main wait still at `0xEC0309D028` / cond `0xEC0309D030` across repeated
periodic snapshots. This proves the virtual start target executes on main and
reaches its first import; it is not a missing virtual dispatch.

**Static trace of the bounded body.** Disassembly using the verified text
mapping (`file = guest - 0x800000000 + 0x21700`) shows initialization of four
sub-objects at `manager + 0x810`, `+0x1208`, `+0x1c00`, and `+0x25f8`. Each goes
through the same helper at `0x800F5F8D0`, which calls `_Mtx_lock` and
`_Mtx_unlock` through the loaded libc path. It then calls `0x800F5FCC0` four
times with indices 0..3. A return-address probe on `0x800F5FD0A` captured all
four calls:

```text
qpo-mEOwje0 = sceSystemGestureOpen
saved_ret=0x800F5F9AF, 0x800F5F9ED, 0x800F5FA2B, 0x800F5FA69
```

`0x800F5FCC0` stores each result in the manager's per-sub-object fields and
returns normally. The only later call in that helper that produces a repeated
error is `scePadGetControllerInformation` at return `0x800F5FD31`; its result
is `-2137915389` for a `0xFFFFFFFF` controller handle while filling the
per-index state. The call is reached four times, and the function's normal
path continues after it. No SharpEmu export was changed from this observation:
the pad error is not yet proven to be the readiness cause, and changing it
would be a speculative ABI decision.

**Conclusion.** The start method is entered, runs its four initialization
iterations, and reaches the normal post-call path. The current evidence does
not identify a shared emulator defect or a missing wake. The parked `StBGM`
worker remains a correlation only; no causal link to the manager event has
been demonstrated. The next bounded experiment should trace the first
post-initialization call/branch that decides whether to submit work, using the
manager address from the same stall snapshot. Do not force `Event::set`, alter
the pad error, or add a GT7-specific path without that evidence.

### 6k. Identity warning: the start body looks like gesture setup

The NIDs reached by `0x800F5F950` are `sceSystemGestureInitializePrimitiveTouchRecognizer`,
`sceSystemGestureOpen`, and `scePadGetControllerInformation`. That is an
input/gesture initialization sequence, not an obviously audio-specific one.
This conflicts with the earlier type-name inference that labeled the object at
`manager + 0x28` as `GTSound::RaceMusicManager`. The earlier report itself
warns that vtable/typeinfo walks produced unrelated class names and should not
be trusted without runtime corroboration.

**Observation.** The direct runtime facts are still solid: the object at
`0xEC0309D000` (event at `+0x28`) is the object dereferenced by the main wait,
and its virtual slot `+0x2A8` contains `0x800F5F950` in the captured run.
What is not solid is the class name attached to that object. Treating the
waited event as a RaceMusicManager event, and treating `StBGM` as its worker,
now requires a fresh runtime identity check. The next investigation step must
resolve the slot's owner and construction path from runtime values or a
reliable call relationship before proposing an audio or gesture export fix.

### 6l. Runtime RTTI recheck: the vtable identifies a MENU update context

**Question.** Does the vtable used for the `0xEC0309D028` wait belong to the
audio manager inferred in 6h, or can its owner be identified from live RTTI?

**Bounded captures.** Four existing-Release runs were used, each stopped after
one or two 10-second snapshots. The first used:

```powershell
$env:SHARPEMU_DUMP_GUEST_MEMORY = '0x8054E6260'
$env:SHARPEMU_PROBE_IMPORT_RET_ADDRESS = '0x800F5F981'
$env:SHARPEMU_PERIODIC_SNAPSHOT_SECONDS = '10'
$env:SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = '1'
```

and captured `artifacts/gt7-investigation/identity.err.log`. Its stall
snapshot had event `0xEC030A0028` (confirming heap allocation varies between
boots), while the static vtable neighborhood was:

```text
memdump 0x8054E6260: 8058B5BC0 0000000000000000
memdump 0x8054E6270: 8054E65A0 801EBACA0
```

Thus `0x8054E6278` is the vtable address observed in the earlier object dump.
Under the Itanium ABI address-point convention, `vtable[-2]` is the zero word
at `0x8054E6268` and `vtable[-1]` is `0x8054E65A0` at `0x8054E6270`. The
`0x8058B5BC0` word at `vtable[-3]` is adjacent data and must not be followed as
the typeinfo pointer. The same run again hit the first-import probe at
`0x800F5F981` and observed `StBGM` parked on waiter 60.

The second run dumped the actual `vtable[-1]` typeinfo payload:

```powershell
$env:SHARPEMU_DUMP_GUEST_MEMORY = '0x8054E65A0'
$env:SHARPEMU_PERIODIC_SNAPSHOT_SECONDS = '10'
```

Capture: `artifacts/gt7-investigation/rtti-actual.err.log`. It returned:

```text
memdump 0x8054E65A0: 80830D828 804EC1631
memdump 0x8054E65B0: 8054E6250 0000000000000000
```

Following the live name pointer in the final bounded run, captured as
`artifacts/gt7-investigation/rtti-actual-name.err.log`, returned:

```text
memdump 0x804EC1630: 31554E454D344E00 6574616470556D37
memdump 0x804EC1640: 50747865746E6F43 3031745300453453
```

Interpreting the little-endian bytes at `0x804EC1631` gives the mangled RTTI
name `N4MENU17mUpdateContextPS4E`, i.e. `MENU::mUpdateContextPS4`. The earlier
`7hObject` string came from the adjacent `vtable[-3]` pointer and is discarded.
This runtime type is unrelated to the earlier `GTSound::RaceMusicManager`
label. The event address and virtual call remain valid observations, but the
object is not established as an audio manager or as the owner of `StBGM`.

**Conclusion.** The RaceMusicManager/StBGM causal chain is superseded by the
runtime RTTI result. No audio, gesture, pad, event, or ABI change is justified.
The next trace must follow the actual `MENU::mUpdateContextPS4` receiver
construction and the caller-owned pointer load around
`0x800C73370`/`0x800C733C5`, then identify who owns the awaited event before
looking for a missing signal.

### 6m. Post-initialization call chain: scheduling is the next causal boundary

An independent static review of the verified `0x800F5F950` body found no
direct `Event::set` through its return at `0x800F5FC1E`. After the four gesture
iterations, the first new call is:

```text
0x800F5FAC9 -> 0x800D3E9B0
0x800F5FADE -> 0x800D53520
0x800F5FAF1 -> 0x8004D4050
0x800F5FB8F -> virtual [[r13] + 0x30]
0x800F5FB9C -> virtual [[r13] + 0x38]
0x800F5FBBF -> 0x80097E4D0
```

The caller-side sequence around the object creation is also more specific
than the old manager narrative:

```text
0x800C733D2  calls [[owner + 0x28] + 0x38]
0x800C733E0  stores context r14 at [[owner + 0x28] + 0x90]
0x800C733EB  touches global 0x8064E5270
```

These are static control-flow observations, not proof that any one target
signals the awaited event. The first bounded runtime scheduling probe should
target `0x800D3E9B0`, capture its import/return path, and correlate it with
the live event address from the same stall snapshot. Do not modify this path
until the probe identifies a shared emulator contract and demonstrates the
missing readiness transition.

### 6n. The blocker, named for real: the display never reports a refresh rate

Sections 6h and 6i named the awaited object `GTSound::RaceMusicManager` and
blamed audio. Section 6l retracted the name. This section retracts the causal
story with it. The object is `MENU::mUpdateContextPS4`, the wait is a
request/completion handshake, and the thing that never happens is upstream of
both: SharpEmu told GT7 its display runs at a refresh rate no PlayStation has.

**The chain, each link checked against a live capture or the eboot bytes.**

Main posts a request and blocks. `0x800F853A0` copies its two arguments into
the context, sets the request state, and waits on the context's event. The
bytes at that site decode exactly as documented, and the call target is the
one-shot wait helper:

```text
0x800F853EC  48 8D 7B 28           lea  rdi,[rbx+0x28]      ; the event
0x800F853F0  31 F6                 xor  esi,esi             ; no timeout
0x800F853F2  C7 43 48 01 00 00 00  mov  dword [rbx+0x48],1  ; state := requested
0x800F853F9  E8 E2 73 7F FF        call 0x80077C7E0         ; Event::wait
0x800F853FE                        <- guest-frame#0 in every stall snapshot
```

There is no submit call. `0x800F852F0` only takes a lock, loads the context
from `[singleton+0xA8]`, and calls the wrapper. The request is serviced by
whatever already ticks the object, not by anything main does.

The servicer is `0x80049C650`. Scanning the text for calls to the known
`Event::set` (`0x8009BBCC0`) returns 24 sites; exactly one of them clears a
`+0x48` state before signalling a `+0x28` event:

```text
0x80049C8B6  mov  dword ptr [rbx+0x48], 0
0x80049C8BD  lea  rdi, [rbx+0x28]
0x80049C8C1  call 0x8009BBCC0
```

That function takes the context from `[this+0x90]` and runs a `lock cmpxchg`
state machine on `+0x48`: `1 -> 2` enters the init path, `3 -> 4` runs an
update pass, `5 -> 6` completes and signals. It has no direct callers — it is
virtual slot `+0x58`.

`this` is the object a stall-time pointer sweep identifies, and runtime RTTI
names it. The global `0x8064E5270` holds `0xE408F1FD50`; that object's `+0x90`
is the context; its vtable is `0x8054E6010` and `vtable+0x58` holds
`0x80049C650`; `vtable[-1]` resolves through typeinfo to
`N4MENU14MenuGameObjectE`. The object carries the inline string `RootWindow` at
`+0xC8`. So main asks the menu's root window to service an update, and blocks
until it does.

**Why the root window stopped being ticked.** Three threads (`60Hz`, `GPOTL`,
`SNDZ`) park on the condition variable at `0x8069C4168` and are never woken. A
full `SHARPEMU_LOG_PTHREAD_CONDS` capture shows that condvar taking blocking
waits and receiving zero signals or broadcasts for a whole boot, while its
sibling at `0x8069C41B0` is broadcast about sixty times a second.

An exact rip-relative scan — not a byte-pattern scan — finds one broadcaster of
`0x8069C4168`: `0x801F95195`, inside the loop at `0x801F94CC0`. Resolving that
loop's imports through JMPREL names it completely. It is the VideoOut event
pump:

```text
sceKernelWaitEqueue / sceKernelGetEventFilter
  filter -13 (EVFILT_VIDEO_OUT) -> sceVideoOutGetEventId
      id 0  -> sceVideoOutGetFlipStatus
      id 1  -> sceVideoOutGetVblankStatus, broadcast 0x8069C41B0
      id 8  -> sceVideoOutGetOutputStatus  (see below)
  filter -15 (EVFILT_HRTIMER), filter -14 (EVFILT_GRAPHICS_CORE)
```

After each vblank broadcast the pump decides whether to also broadcast the
frame-sync condvar, and the decision reads one global:

```text
0x801F94DFA  mov rax, qword ptr [rip + 0x40669b7]   ; -> 0x805FFB7B8
0x801F94E04  je  0x801F95150                        ; rax == 0
0x801F94E0E  je  0x801F95150                        ; rax == 3
0x801F94E18  je  0x801F95127                        ; rax == 0xD
0x801F94E1E  jmp 0x801F95143                        ; otherwise: skip
```

That global is written from `sceVideoOutGetOutputStatus`:

```text
0x801F95006  call sceVideoOutGetOutputStatus
0x801F9500B  mov  rax, qword ptr [rbp - 0x58]       ; status + 0x08
0x801F95021  mov  qword ptr [rip + 0x4066790], rax  ; -> 0x805FFB7B8
```

At the stall it held `0x3C`. SharpEmu was writing `port.RefreshRate` — the
literal number 60 — into `status + 0x08`.

`{0, 3, 0xD}` are `SCE_VIDEO_OUT_REFRESH_RATE_UNKNOWN`, `_59_94HZ` and
`_119_88HZ`. The field is the refresh-rate **ordinal**, not hertz, which is
also why it is eight bytes wide: `_ANY` is `0xFFFFFFFFFFFFFFFF`. GT7 runs its
frame sync for an unknown display, a 60-class display or a 120-class display,
and 60 is none of them. A second call site corroborates the layout
independently: `0x800FF0B32` reads `status + 0x00`, compares it with 1, and
picks 1080 or 2160 — the resolution class SharpEmu already wrote correctly.

**The fix.** `sceVideoOutGetOutputStatus` now maps the host refresh rate to the
`SCE_VIDEO_OUT_REFRESH_RATE_*` ordinal. This is general: the raw hertz value is
one no hardware ever reports, so any title branching on that field took the
fall-through path. A host mode with no console ordinal (144 Hz) reports
`UNKNOWN`, which is what hardware does for a mode it is not driving.

**What it unblocked, and the two defects behind it.** With the ordinal correct
the pump broadcasts the frame-sync condvar and the engine starts:

| thread | imports before | imports after |
|---|---|---|
| `60Hz` | 2 | 42,030 |
| `GPOTL` | 2 | 41,953 |
| `Job#1`..`Job#5` | 4-147 | ~3.0-3.7M each |
| `FWRKR` (flash workers) | 2-3,974 | 486-6,885 |
| `RDisp` | 4 | 922 |
| boot imports in ~90 s | 10.8M | 191M |

Two further defects surfaced immediately on the newly reached path:

- `sceKernelAddHRTimerEvent` was unresolved, and GT7 calls it once per frame
  sync — 921 unresolved calls in one bounded run. Implemented together with
  `sceKernelDeleteHRTimerEvent`, as one-shot `EVFILT_HRTIMER` (-15) events.
- The pump ran long enough to reach GT7's socket code, which then reproducibly
  smashed its own stack. The faulting instruction is an `fd_set` build:
  `or qword ptr [rbp + r9*8 - 0xb0], r8`, with the bitmap 0x80 bytes (1024
  bits, `FD_SETSIZE`) and `r9 = fd >> 6`. `rdi` held `0x4002`, so `fd >> 6`
  was 256 and the write landed 0x640 bytes past the top of the guest stack.
  `NetExports` allocated descriptors from `_nextSocketId = 0x4000`. Socket
  descriptors are POSIX file descriptors and titles index an `fd_set` with
  them, so a descriptor at or past 1024 corrupts the caller rather than failing
  a call. They are now allocated lowest-free below `FD_SETSIZE`.

**Where the boot stops now.** The root window is ticked, the init path runs,
and `context+0x48` sits at **3** — initialised, awaiting the update pass —
instead of 1. The update pass does not finish because entries remain pending in
the array at `context+0x6a8` (count at `context+0x6d8` = 6); an entry counts as
pending while `[entry+0x366] == 1` and `[entry+0x95] == 0`, and while any is
pending the tick rewrites state 3 rather than advancing to 5. Runtime RTTI
names those entries `MENU::mRenderContextPS4`.

The menu is therefore now waiting on render contexts, which is consistent with
the long-standing "zero guest draws" observation. The next investigation is the
render path, not this handshake. Main still parks on the same event, but for a
different and later reason.

**Ruled out, so nobody repeats it.**

- The awaited object is not an audio manager, and `StBGM` is not its worker.
- The context is not unregistered. A stall-time sweep finds 14 references to
  it, including the root window's `+0x90` and the global `0x8064E5270`. The
  earlier "object never queued / no inter-thread hand-off" note was wrong.
- `scePadGetControllerInformation` returning `-2137915389` for handle
  `0xFFFFFFFF` is not causal; the gesture startup path continues past it.
- Do not probe `0x80049C678` with `SHARPEMU_PROBE_IMPORT_RET_ADDRESS`. The
  import behind it is `scePthreadSelf`, which is not served through the HLE
  import path for this title, so the probe never fires however often the
  function runs. `SHARPEMU_LOG_IMPORT_FILTER=scePthreadSelf` produces nothing
  for a whole boot, which is how that was established. A probe address is only
  meaningful once the import behind it is confirmed to dispatch through HLE.

Captures: `ctx1.*`, `pump1.*`, `pump2.*`, `conds1.*`, `gate1.*`, `self1.*`,
`fix1.*` through `fix5.*`, `pend1.*`, `pend2.*` under
`artifacts/gt7-investigation/`.

### 6o. Past the handshake: the GPU pipeline had three separate blocks

Section 6n left GT7 with a running engine and the menu waiting on six
`MENU::mRenderContextPS4` entries. Nothing rendered: a whole boot produced one
DCB submission and zero draws. Three defects were stacked behind that, each
hiding the next.

**1. `sceAgcCreateShader` rejected a shader stage this tree has no mapping for.**

167 calls per boot returned `INVALID_ARGUMENT`, all with header type 8. Types
0-7 are Cs/Ps/Gs/Hs plus the four fused halves; type 8 appears in no public
description of the AGC header, and the frangametv fork — which is further along
on GTA V and Astro Bot rendering — stops at 7 as well, so it is not a mapping
this project simply missed.

A runtime dump settled what the header is. It is structurally a normal AGC
shader header: magic `1234`, version `0x18`, the same 0x60-byte struct, with
specials/SH-registers/user-data packed contiguously after it and sizes that
agree exactly with the count field.

```text
type 8   +00=0x0000001834333231 +08=userdata +10=code +18=cx(null)
         +20=shregs +28=specials +30/38=semantics(null) +5A=type 8 +5C=count 8
         table = 0x0000,0x0001,0x0002,0x0005,0x0003,0x0004,0x0006,0x0007
type 0   (succeeds) table = 0x020C,0x020D,0x022A,0x022A,0x0212,0x0213,0x0228,0x0207
```

The type-0 entries are real SH register offsets (`0x20C` = `COMPUTE_PGM_LO`);
the type-8 entries are not, so the PGM_LO/HI search finds nothing.

The stage is still unidentified and this change does not claim otherwise. What
changed is the failure policy. The header has already been validated and
relocated and the code VA has already been published at `ShaderCodeOffset` by
the time the search runs, so a missing PGM pair leaves nothing to reject — the
program address reaches the GPU through the binder, which is exactly why the
tree already skipped the patch for two named cases. Rejecting is also worse
than incomplete: GT7 stamps a "created" flag into the header *before* calling
(`cmp byte [rdx],0x30` / `mov byte [rdx],0x31` at `0x8005D77A5`) and never
retries, so one rejection loses that shader for the whole run. The two
type-specific escapes are now one general rule.

**2. `sceAgcDriverGetEqEventType` was unresolved, so every GPU completion was
misfiled.**

GT7's VideoOut/AGC pump calls it on each `EVFILT_GRAPHICS_CORE` (-14) event:

```text
0x801B4C6D4  call sceAgcDriverGetEqEventType   ; unresolved -> sentinel
0x801B4C6D9  cmp al, 1
0x801B4C6DB  ja  0x801B4C6EA                   ; >1: decode as a compute queue
; caller 0x801F94ECC: test al,al / jne -> skip waking the GPU pipeline
```

The unresolved sentinel reads as `>1`, so every graphics completion was treated
as a compute-queue event and the GPU pipeline was never woken. The export
returns the AGC event id — the same id `sceAgcDriverAddEqEvent` registers.
GT7's own registrations confirm the id space and the decode: it registers id
`0x00` for graphics end-of-pipe, and `0x22`/`0x2A` elsewhere, which its
`id & 7` / `(id >> 3) - 4` arithmetic turns into (queue 2, pipe 0) and
(queue 2, pipe 1). SharpEmu already assigns exactly those ids.

**3. One end-of-pipe interrupt was delivered twice, on two channels.**

With the event classified, GT7 immediately faulted at `0x801F94F2B`:

```text
0x801F94EDB  mov r14, [0x80650C9C0]   ; take the handoff slot
             lock; [0x80650C9C0] = 0; broadcast; unlock
0x801F94F2B  mov rax, [r14 + 0x48]    ; r14 == 0 -> access violation
```

The global is a single-slot handoff: the producer publishes a GPU context, the
pump consumes it on the next interrupt and clears it. One publish, one
notification. SharpEmu delivered several, because a submission is announced on
two independent paths — `NotifySubmittedDcbCompleted`, which raises the
registered event once the whole submission reaches end-of-pipe, and a
per-packet call in both `RELEASE_MEM` handlers that raised one for every
interrupt-flagged packet. The single 187-dword boot DCB carries three of those
(`int=1` once, `int=3` twice), so GT7 received three notifications plus the
real completion and faulted on the second.

Routing the per-packet delivery to the raising queue's own ident was not
enough — the graphics queue's ident *is* 0, so its own fences still matched.
The packet-level channel is now removed: the event a title registers for is a
submission-completion notification, and intra-submission fences are not
submissions. `TriggerRegisteredEventsByFilter` keeps the new optional `ident`
argument, which is the correct routing for any future per-queue delivery.

**What the three together unblocked.**

| | before 6o | after 6o |
|---|---|---|
| DCB submissions per boot | 1 | 1,588 |
| `agc.dcb_draw_index_auto` | 0 | 16,872 |
| `Rendr` imports | 2,461 (frozen) | 552,849 and climbing |
| `GPUex` imports | 25 (frozen) | 303,816 and climbing |
| `Flipx` imports | 3 (frozen) | 47,233 and climbing |
| crash | access violation in the pump | none |

Audio is audible during the boot, which is independent confirmation that the
engine is running rather than merely spinning.

**Where the boot stops now.** Unchanged from 6n in shape, later in cause. Main
still parks on the same event; `context+0x48` is still 3, and the same six
`MENU::mRenderContextPS4` entries are still pending — an entry counts as
pending while `[entry+0x366] == 1` and `[entry+0x95] == 0`, and the tick
rewrites state 3 rather than advancing to 5 while any is.

What is now known about those entries, from a live dump:

- The per-entry action `0x80049ED80` runs a three-step pipeline whose index
  lives at `entry+0x80` and is **reset to 0 after each full pass**, so an
  observed 0 is a completed pass, not a stuck one. Both of its gates
  (`entry+0x94`, `[entry+0x88]+0x135`) read clear, so all three steps run every
  tick. The step functions are `0x80049F200`, `0x80049DD10`, `0x80049EE60`,
  taken from the table at `0x8054B03E0`.
- None of the three writes `entry+0x95`, so completion is signalled from
  outside this pipeline.
- No flip is ever submitted: neither `sceVideoOutSubmitFlip` nor
  `sceVideoOutSubmitEopFlip` is called in a whole boot, and only the splash is
  presented. GT7 has not reached its present loop because it is still inside
  `Organizer::initialize()`.

**Ruled out, so nobody repeats it.**

- A byte-store scan for writers of `entry+0x95`, and a wider scan for stores
  covering it at `entry+0x94`, are both useless here: the offsets are common
  across unrelated classes and the wider scan returns hundreds of hits. This
  needs a runtime answer, not a pattern scan.
- The shader failures were *not* what gated rendering. Fixing them changed
  neither the pending count nor the update state; rendering only started once
  the event classification and the duplicate interrupt were fixed.
- `sceNetRecv`/`sceNetRecvfrom` are unresolved and called thousands of times
  per boot by the network thread. They are a real gap but not on this path.

Captures: `shader1.*`, `shader2.*`, `shader3.*`, `shfix1.*`, `conds2.*`,
`state2.*`, `state3.*`, `rctx1.*`, `steps1.*`, `census1.*`, `agc1.*`,
`gpufix1.*` through `gpufix4.*`, `rel1.*`, `draws1.*`, `pend1.*`, `pend2.*`
under `artifacts/gt7-investigation/`.

### 6p. `entry+0x95` answered: it is a bit in a mask, gated by a global flag pair

**Question.** Section 6o left one open question: what sets `entry+0x95`, the byte
that makes a `MENU::mRenderContextPS4` entry stop counting as pending. A byte
-store scan for the offset had found nothing usable, and the three step
functions do not write it.

**Why the earlier scan failed.** `+0x95` is never written as a byte. It is the
*second byte of a 32-bit slot bitmask at `+0x94`*. The writer is
`0x8004A00F0`, which takes a slot index in `esi`:

```
0x8004A0125  mov  eax, esi            ; slot index
0x8004A0127  shlx esi, r12d, esi      ; esi = 1 << index
0x8004A012C  mov  rbx, [rdi+0x58]     ; slot array base
0x8004A0130  imul r13, rax, 0x2b0     ; per-slot stride
0x8004A0137  or   dword ptr [rdi+0x94], esi
```

So the two gates the per-entry action reads are two byte views of one dword:
`[entry+0x94]` tests bits 0-7, `[entry+0x95]` tests bits 8-15. Any scan for a
byte store to `+0x95`, however thorough, cannot find this. **When an offset has
no writer, check whether a wider store covers it before concluding the write
happens elsewhere.**

`0x8004A00F0` has seven call sites. The slot indices passed are 0, 3 and 9;
only **9** (`1 << 9 = 0x200`) lands in `+0x95`:

```
0x8004A8115  cmp byte ptr [rbp-0x1a30], 0
0x8004A811C  je  0x8004A3AB6          ; skips the whole loop
0x8004A812F  (loop head)
0x8004A8176  call 0x8008924F0
0x8004A8189  mov  esi, 9
0x8004A8191  call 0x8004A00F0         ; or [entry+0x94], 1<<9
0x8004A8196  inc  r14d
0x8004A8199  cmp  dword ptr [rbp-0x1a20], r14d
0x8004A81A0  jne  0x8004A812F
```

**The gate.** The `rbp-0x1a30` boolean is computed from two adjacent global
bytes:

```
0x8004A3862  mov al, byte ptr [rip+0x606a949]   ; A = *0x80650E1B1
0x8004A3886  xor al, 1                          ; al = !A
0x8004A3888  and al, byte ptr [rip+0x606a922]   ; B = *0x80650E1B0
0x8004A38B9  mov byte ptr [rbp-0x1a30], al      ; guard = (!A) & B
```

**Runtime confirmation.** A boot with
`SHARPEMU_DUMP_GUEST_MEMORY=0x80650E1B0:0x10,[[[0x8064E5270]+0x90]+0x6a8]+0x90:0x10,[[0x8064E5270]+0x90]+0x40:0x20`
gave the same result in all four snapshots:

```
memdump 0x000000080650E1B0: 0000000000000000 00007FFDCEFFFF18
memdump 0x000000E410160FA0: 0101000000000011 0000000000000001   (entry+0x90)
memdump 0x000000EC0309D040: 0000000000000001 000000E400000003   (context+0x40)
```

- `A = B = 0`, so the guard is `1 & 0 = 0` — **false**. The bit-9 loop never
  runs, so `entry+0x95` is never set.
- `entry+0x94 = 0` and `entry+0x95 = 0`, confirming the entry is still pending.
- `context+0x48 = 3`, reproducing the 6o blocker exactly.

**What the flag pair is.** `0x80650E1B0` / `0x80650E1B1` are read from **270
sites across the whole executable** and written from only two functions,
`0x800602130` and `0x801F944C0`. This is an engine-wide feature/state flag pair,
not a menu-local one, so whatever leaves it zero is likely to gate far more than
the menu.

**Next step.** Find why `0x800602130` / `0x801F944C0` never set it — probe those
two functions at runtime rather than reading the pair statically. Note the
static file bytes at that address decode as `08 00`, which contradicts the
runtime `00 00`: the segment mapping for `0x8065xxxxx` is **not** one of the
verified rows in the mapping table, so the static read there is unreliable and
must not be used as evidence.

**Ruled out / traps.**
- The `MENU::mRenderContextPS4` vtable at `0x8054E66F0` is **zero in the file**;
  it is populated by `R_X86_64_RELATIVE` relocations at load. Reading a vtable
  out of the eboot image directly yields all zeros and looks like a bad address.
  The relocation run that fills it is at file `0x6327800`+.
- Walking that vtable's `vtable[-1]` typeinfo resolved to
  `20GTDataModelingSystem21CarSpecificLabelKey_tE`, a mid-string hit. This
  repeats the warning from 6k/6l: **typeinfo walks in this binary are not
  trustworthy** and should not be used to name a class.
- `0x80319D6C0` writes a byte at `+0x95` and also references `0x6a8`, but it is
  an unrelated class: its `0x6a8` is a *vtable slot offset*, not the render
  -context array field. Offset co-occurrence alone is not identity.

**Unrelated gap found in the same capture.** NID `sDuhHGNhHvE` is unresolved and
was called **364 million times** in a 115-second boot (1.73M log lines). Its
arguments are a font-like object plus two small integers that are consecutive
ASCII codes (`0x53 0x61` = "Sa", `0x61 0x6E` = "an", ...), i.e. a per-character
-pair lookup, most likely kerning. Caller returns to `0x80042A396`. This is a
real missing export and a large CPU cost, but it is not on the menu path.

Captures: `probe95.*` under `artifacts/gt7-investigation/`.

### 6q. The menu chain, closed: video-out state never reports open

Follow-on from 6p. Three claims in the earlier handover turned out to be stale
or wrong, and correcting them changed where the work went.

**Correction 1 — GT7 does present.** The handover said neither
`sceVideoOutSubmitFlip` nor `sceVideoOutSubmitEopFlip` is called in a whole
boot. With `SHARPEMU_LOG_VIDEOOUT=1` the current tree shows **4,559
`videoout.submit_flip` calls in 95 s**, cycling buffer index 0/1/2 over three
3840x2160 buffers registered once — a healthy triple-buffered present loop at
roughly 48 fps. The screen is black because GT7 is presenting *empty* frames,
not because it never presents. Anything reasoning from "it never flips" is
reasoning from a stale fact.

**Correction 2 — the VR path is a red herring.** `0x801F944C0` is the
video-out bring-up (`sceHmd2ReprojectionEnableVrMode` -> `sceVideoOutOpen` ->
`sceVideoOutRegisterBuffers2` -> `sceVideoOutConfigureOutput` ->
`sceHmd2ReprojectionSetRenderConfig` -> `sceVideoOutAddVblankEvent`, then both
flags to 1). It is gated by `0x801F94440`, which ignores its user-id argument
and returns true only if a VR-enabled global is 1 *and* `sceHmd2Open` succeeds.
That global reads **0** at runtime, no `sceHmd2*` export appears in the import
census at all, and SharpEmu implements no HMD2 surface. So the VR bring-up is
correctly skipped, exactly as it would be on a PS5 with no headset. It is not
the missing step.

**Correction 3 — GPU wait suspension is not the cause.** `sceAgcAcbRewind` and
`sceAgcAsyncRewindPatchSetRewindState` are both unresolved and called 13,478
times each per boot, and `HandleSubmittedRewind` suspends a submitted DCB at an
`IT_REWIND` packet waiting for bit 31 that the missing export was supposed to
write — a genuinely suspicious pairing. Running with
`SHARPEMU_GPU_WAIT_MODE=force`, which disables suspension entirely, changed
nothing: `context+0x48` stayed 3 and all six entries stayed pending. **Ruled
out with no speculative code.** The two exports are still real gaps, just not
this one.

#### The chain, end to end

The pending loop in the context tick (`0x80049C650`) reads, verbatim:

```
0x80049C6AE  lock cmpxchg dword ptr [rbx+0x48], ecx   ; state 3 -> 4
0x80049C6B9  cmp byte ptr [rbx+0xa1], 1               ; loop gate
0x80049C6C6  movsxd r13, dword ptr [rbx+0x6d8]        ; count
0x80049C6DC  mov r15, qword ptr [rbx+r14*8+0x6a8]     ; array of POINTERS
0x80049C6E9  cmp byte ptr [r15+0x366], 1
0x80049C6FD  cmp byte ptr [r15+0x95], 0               ; pending iff both hold
```

A six-entry runtime dump confirms every field of that reading:

| entry | address | `+0x90` | `+0x94` | `+0x95` | `+0x96` | `+0x97` |
|---|---|---|---|---|---|---|
| 0 | `0xE410160F10` | `0x11` | 0 | **0** | 1 | 1 |
| 1 | `0xE4102090D0` | `0x11` | 0 | **0** | 1 | 1 |
| 2 | `0xE41020A000` | `0x22` | 0 | **0** | 1 | 1 |
| 3 | `0xE410219A90` | `0x44` | 0 | **0** | 1 | 1 |
| 4 | `0xE41021BAE0` | `0x88` | 0 | **0** | 1 | 1 |
| 5 | `0xE41021DC60` | `0x11` | 0 | **0** | 1 | 1 |

with `count = 6`, `context+0xa1 = 1` (the loop gate passes) and
`context+0x48 = 3`. The entries are **separate heap objects**, not a strided
array — the `0x2b0` stride in `0x8004A00F0` belongs to a member array reached
through `[rdi+0x58]`, not to these.

An exhaustive linear-disassembly sweep of the whole text — every instruction
whose destination memory operand covers byte `+0x95` at any width, decoded from
real function starts rather than guessed boundaries — finds **26 byte writes to
`+0x95` in the entire executable**, and in the menu's own translation unit
exactly one thing that can set it: the bitfield OR in `0x8004A00F0` from 6p.

That function's seven call sites pass slot indices **0, 3, 4, 5, 6, 9 and 11**.
Every one of those bits lives in `+0x94` (bits 0-7) or `+0x95` (bits 8-15), and
**every one of them reads clear at runtime**. The bits that *are* set in the
dword at `+0x94` are 16 and 24 — outside the range this function can produce.
So `0x8004A00F0` has never run for these entries, whichever way `+0x94..+0x97`
is carved up.

Its bit-9 site sits behind the 6p guard, and the guard's inputs close the loop:

```
guard = (!*0x80650E1B1) && *0x80650E1B0        ; both read 0 at runtime
```

`0x80650E1B0` ("video out open") is written in exactly two places: the VR
bring-up tail (skipped, correction 2) and

```
0x8006078F3  cmp  eax, 1
0x8006078F6  mov  byte ptr [A], 1
0x8006078FD  sete byte ptr [0x80650CA82]
0x800607904  sete byte ptr [B0]              ; B0 = (eax == 1)
0x80060790B  mov  dword ptr [r14+0x1b0], 0xffffffff
```

reached only from the display manager's state machine:

```
0x800606FA9  mov eax, dword ptr [r14+0x1b0]
0x800606FB0  cmp eax, -1
0x800606FB3  jne 0x8006078F3                  ; consume a completed request
0x800606FB9  mov eax, dword ptr [r14+0x1ac]   ; 0 -> close path, 1 -> open path
```

`[r14+0x1b0]` is an **async request-result slot**: this function only reads it
and resets it to `-1`, so it is filled by a completion elsewhere. It never
holds a result, so B0 is never set, so slot 9 is never marked, so `+0x95` stays
0 on all six entries, so the tick rewrites state 3 forever and main stays on
`scePthreadCondWait`.

Note the full requirement is **two** steps, not one: the block above sets
`A = 1` as well as `B0`, and `guard` needs `A = 0`. `A` is cleared only at
`0x8006071B5` (`mov byte ptr [A], al`), also inside the display manager. So a
correct boot must run the completion block *and then* the path that clears `A`.

**Located at runtime: the display manager is idle.** Probing
`SHARPEMU_PROBE_IMPORT_RET_ADDRESS=0x8006022A4` (the `sceKernelSignalSema`
return inside `0x800602130`) fires **6,162 times** with a stable guest
`rbp=0x7FFFB11FFF20` and `saved_ret=0x800600FB6`, confirming the manager runs
every frame and is called from `0x800600FB1`. The object is therefore
`[rbp-0x3c0]` = `[0x7FFFB11FFB60]`, which dumps to **`0xE400000D00`**. Its
state fields:

| field | value | effect on the state machine |
|---|---|---|
| `+0x198` | `-1` | not `> 0`, falls through |
| `+0x19C` | `0` | falls through |
| `+0x1A0` | `-1` | equal, falls through |
| `+0x1A4` | `-1` | equal, falls through |
| `+0x1B0` | `-1` | equal — **the `B0` block at `0x8006078F3` never runs** |
| `+0x1AC` | `-1` | neither `0` (close) nor `1` (open) |

`+0x1AC` is the **requested mode**, and `-1` means *no video-out request has
ever been made*. The machine is not stuck waiting on a completion; it is idle
because nothing ever asks it to open the display. The `0x800607939` open branch
is confirmed dead: a probe on `0x800607951` (the `sceUserServiceGetInitialUser`
return inside it) **never fires in a whole boot**, while that export is called
from elsewhere, so the branch itself is never taken.

**Next step.** Find what sets `[obj+0x1AC] = 1`. Inside `0x800602130` the field
is only ever reset to `-1` (`0x800607997`), and a whole-text sweep finds 100
writers of `+0x1ac` with none in this translation unit — so the requester
almost certainly holds a **sub-object pointer** and writes a different
displacement (e.g. holds `obj+0x100` and writes `[p+0xac]`). Two usable angles:
the object address `0xE400000D00` is reproducible across boots, so it can be
watched directly; and `sceUserServiceGetEvent` already delivers exactly one
login event (type 0, user `0x10000000`) and then `NoEvent`, so a missing login
notification is **not** the reason — that was checked and ruled out.

Guest heap and stack addresses are reproducible across boots on this build (the
six entry addresses and this `rbp` were identical in every run), which is what
makes the two-stage "probe for an address, then dump it next run" technique
work at all.

#### Confidence: what is proven, and what is not

Proven, from runtime dumps:

- The pending predicate, the six entries, `count = 6`, `context+0xa1 = 1` and
  `context+0x48 = 3`.
- `0x8004A00F0` has **never run** for these entries — all seven of its slot
  indices (0, 3, 4, 5, 6, 9, 11) read clear on all six.
- The guard inputs `0x80650E1B0` / `0x80650E1B1` are both 0.
- The display manager runs every frame and is idle, with `+0x1AC = -1`.
- The `0x800607939` open branch is never taken.

**Not proven — the load-bearing gap.** That `0x8004A00F0` is the thing that
sets `entry+0x95` *at all*. Its `rdi` was never confirmed to be a
`MENU::mRenderContextPS4`: the entries are separate heap objects while that
function indexes a `0x2b0`-stride array through `[rdi+0x58]`, and the bits
actually set in the entries' `+0x94` dword (16 and 24) are outside the range it
can produce. Checking the other direction does not help either — of the 26
functions in the whole executable that write a byte to `+0x95`, **none
references `+0x366`**, the entry's own distinguishing field, so none can be
tied to this class that way.

So the honest state is: the writer of `entry+0x95` is **still unidentified**.
The `B0` chain is the best available candidate and every link in it that could
be checked at runtime holds, but the first link is inference. Before investing
in "make the display manager request an open", confirm that link — a memory
write-watch on `entry+0x95` (the entry addresses are reproducible across boots)
would settle it in one run, and would also settle whether the setter is the
bitfield OR or something not in the byte-write list at all, such as a struct
copy.

A second reason for caution: video out demonstrably **is** open for the main
display (4,559 flips, correction 1), yet this manager is idle. That is
consistent with `0x800602130` being a *secondary/VR* display path whose flags
are not what the 2D menu waits on. If so, `B0` is a dead end and the real
setter is elsewhere.

#### Fixed here

`sceFontGetKerning` (NID `sDuhHGNhHvE`) was unresolved and called **364 million
times** in a 95-second boot — by far the hottest thing in the process. Both of
its call sites have the same shape:

```
0x80042A38A  lea  rcx, [rbp-0x48]        ; out buffer
0x80042A391  call sceFontGetKerning
0x80042A396  test eax, eax
0x80042A398  jne  0x80042A6BC            ; non-zero -> abandon the layout
0x80042A3BB  vmovss xmm0, dword ptr [rbp-0x40]        ; reads OUT+0x08
0x80042A3C0  vaddss xmm0, xmm0, dword ptr [rbp-0x48]  ; reads OUT+0x00
```

The unresolved import returned `ORBIS_GEN2_ERROR_NOT_FOUND`, so GT7 abandoned
the layout at every character pair. Implemented to report zero offsets and
success, which is what a font with no kerning entry for the pair does. Both call
sites read only `+0x00` and `+0x08`, which is what the implementation defines.
Effect: unresolved count 364M -> **0**, stderr for a
95 s boot 260 MB -> 18 MB. It did **not** move the menu state —
`context+0x48` stayed 3 — so it is a correct fix, not the blocker.

#### Also observed, not chased at this checkpoint

- **Superseded by §6w:** this capture appeared to show that no pad was ever
  opened. The "Controls:" announcement did not print in that observation, so
  every
  `scePadOpen`/`scePadOpenExt` failed. The two `scePadOpen` calls pass
  `(userId=0xFF, type=0x10)` and `(userId=0x10000000, type=2)`, and the 258
  `scePadOpenExt` calls pass `(userId=0, type=2)` — none of which SharpEmu
  accepts (it takes `type==0` non-extended, `type in 0..2` extended, and
  `userId == 0x10000000`). `type=2` at ~2.7/s is consistent with GT7 polling for
  a **racing wheel**, which correctly fails with no wheel attached; the
  `userId=0` and `type=0x10` forms are not explained and want real ABI research
  before anyone "fixes" them into success. Later bounded captures proved that
  the standard pad opens and carries Cross input; do not reuse this negative
  observation as the current diagnosis.
- `sceFiberGetSelf` returns "not on a fiber" 82,304 times. The implementation is
  correct and these come from non-fiber worker threads; not a gap.
- Still unresolved and worth separate work: `sceAgcAcbRewind`,
  `sceAgcAsyncRewindPatchSetRewindState` (13,478 each), `sceNetRecv` /
  `sceNetRecvfrom` (12,650 each), `sceShareGetRunningStatus` (6,327).

#### Tooling note

A NID reverse map makes all of this readable: hash all 154,458 names in
`scripts/ps5_names.txt` with the algorithm in `Ps5Nid.cs` and invert it. Import
names in the eboot are `NID#modid#libid` strings in the dynamic string table at
file `0x6073990`; `JMPREL` is at file `0x607FDC0` (`0x6378` bytes) and
**`.dynsym` is at file `0x6079178`**, which is not derivable from the section
notes above and was recovered by solving for the base that maps two known
`GOT -> symbol index` anchors onto their known NID strings. With those three
numbers a PLT thunk resolves to a real export name offline. Note there is a
**second PLT region around `0x800000A00`** besides the `0x8041xxxxx` one; a
resolver that only scans the latter silently misses calls.

Captures: `vout1.*`, `kern1.*`, `ent1.*`, `force1.*` under
`artifacts/gt7-investigation/`.

### 6r. The finalize chain, proven — and why 6p/6q had the wrong proximate cause

6p and 6q chased "what sets `entry+0x95`" through a guard on two video-out
globals. **That chain is real but it is not the proximate cause**, and the part
6q flagged as unproven is now settled — in a way that moves the blocker
somewhere else entirely. Read this section in preference to 6q's "next step".

#### The finalize chain, end to end

The per-entry action `0x80049ED80` ends its three-step pass like this:

```
0x80049EE0F  mov  dword ptr [rbx+0x80], ecx   ; step index += 1
0x80049EE15  cmp  edx, 2
0x80049EE18  jl   0x80049EDD8                 ; more steps to run
0x80049EE1A  mov  dword ptr [rbx+0x80], 0     ; pass complete, reset step
0x80049EE24  test al, al                      ; al = [entry+0x94], loaded at 0x80049EDEC
0x80049EE26  jne  0x80049EE2D
0x80049EE28  ret                              ; al == 0 -> nothing happens
0x80049EE2D  mov  rdi, rbx                    ; the ENTRY
0x80049EE30  mov  esi, 1
0x80049EE39  jmp  0x800D72020                 ; tail-call finalize(entry, 1)
```

`0x800D72020` is the context **finalize**, and it is unambiguous about its
argument:

```
0x800D72027  cmp byte ptr [rdi+0x95], 0    ; idempotent guard -> rdi IS the entry
0x800D72039..0x800D72165                   ; releases +0x1A8, +0x1C0, +0x200/+0x208,
                                           ; and atomically swaps out +0x2A8..+0x2F0
0x800D7216A  mov byte ptr [r14+0x95], bl   ; bl = esi = 1  ->  entry+0x95 = 1
```

So the trigger is **`entry+0x94 != 0`**, not anything about bit 9. When it is
set, the next completed pass finalizes the entry, `+0x95` becomes 1, the entry
stops being pending; when all six stop, the tick reaches `0x80049C88B`:

```
0x80049C88B  mov eax, 5
0x80049C890  jmp 0x80049C78F              ; context+0x48 = 5
0x80049C79C  lock cmpxchg [rbx+0x48], 6   ; 5 -> 6
0x80049C7A1  je  0x80049C895
0x80049C8B0  call qword ptr [rax+0x2c0]   ; vtable hook
0x80049C8B6  mov dword ptr [rbx+0x48], 0
0x80049C8BD  lea rdi, [rbx+0x28]
0x80049C8C1  call 0x8009BBCC0             ; SIGNAL — this is what main waits on
```

`context+0x28` is exactly the address in the stall snapshot (`rbx=0xEC0309D028`).
The wake path is therefore fully mapped and needs no further reverse
engineering.

Within the menu's own translation unit the only writer of `+0x94` is the
bitfield OR from 6p, `0x8004A0137` (`or dword ptr [rdi+0x94], 1 << index`) in
`0x8004A00F0`. Its seven call sites pass indices 0, 3, 4, 5, 6, 9 and 11 — and
indices 0..7 land in the byte at `+0x94`, which is what the trigger reads. So
6p identified the right function; it just weighted bit 9 (and therefore the
video-out guard) as the interesting one, when indices 0/3/4/5/6 reach the
trigger without any such guard. In particular `0x8004A8110` passes **3** and
sits *before* the `(!A) && B0` test, so it is not gated on video-out state at
all.

#### Who the six contexts are

Each entry's payload (`[entry+0x88]`) carries an inline name at `+0x08` with its
length at `+0x18`. 6n only ever saw `ContextM…`; all six now read cleanly:

| entry | payload | name | `+0x90` |
|---|---|---|---|
| 0 | `0xE410220020` | `ContextMain` | `0x11` |
| 1 | `0xE41026F480` | `Context1P` | `0x11` |
| 2 | `0xE4102B5E40` | `Context2P` | `0x22` |
| 3 | `0xE410304800` | `Context3P` | `0x44` |
| 4 | `0xE41034B400` | `Context4P` | `0x88` |
| 5 | `0xE410392080` | `ContextOverlay` | `0x11` |

These are GT7's **split-screen render contexts** — main, one per player slot,
plus an overlay — and `+0x90` is a player/viewport bitmask (1P..4P = bits 0-3
paired with 4-7). Nothing about them is exotic; they are simply never finished.

#### Where it actually stops

The pipeline is healthy, which is the point:

- `entry+0x80` (the step index) reads **0** on all six — a completed pass, not a
  stall (6p already warned that 0 means completed).
- Both per-tick gates pass: `entry+0x94 = 0` and `payload+0x135 = 0`.

So the three steps run to completion every tick and simply never reach the
finalize call, because nothing sets `+0x94`.

`0x8004A00F0`'s call sites live in `0x8004A1810`, `0x8004A0EA0` and
`0x800892790`. A return-address probe on `0x8004A3D30` — the `sceFiberGetSelf`
return inside `0x8004A1810`, an HLE-served import, so the 6n probe trap does not
apply — **fires zero times in a 70-second boot**. `0x8004A1810` never runs. Its
only two callers are `0x8004C0840` (call at `0x8004C2380`) and `0x8004CC1D0`
(call at `0x8004CE68D`); neither contains any SCE import, so neither can be
probed directly. Their callers in turn are `0x801F58B50` / `0x802F426D0` and
`0x802DEB220` / `0x802DED980`.

#### A wrong turn: "the job system is empty" (it is not)

The RIP sampler shows `Job#0` at 97% running with the single hottest guest
address `0x800135352`, inside a work-stealing loop at `0x800134410`:

```
0x800135350  pause
0x800135352  movsx rax, byte ptr [r14+0x1a]    ; worker index
0x80013535E  imul rdx, rax, 0xcc0              ; per-queue stride
0x800135365  mov  r15, qword ptr [rcx+rdx+0x40]   ; head
0x80013536F  mov  rcx, qword ptr [rcx+rdx+0x80]   ; tail
0x800135377  cmp  r15, rcx
0x80013537A  jne  0x800135762                 ; work available -> pop
```

whose only call is `_Thrd_yield`. It is tempting to read that as a starved job
system — `Running` with an LLE-served yield never ticks the import counter, the
exact case the thread-state notes warn about — and I did. **That reading is
wrong**, and the thread census disproves it in one glance: across four
snapshots `Job#0`'s import counter runs **1,323,910 → 4,143,257 → 6,975,242 →
9,269,744**, and `Job#1`..`Job#4` climb through the millions as well. The
workers are executing constantly; the sampler simply catches them in the steal
loop between jobs, which is where a busy pool spends its idle slices.

The same census shows the rest of the engine alive, not stalled:

| thread | state | imports across four snapshots |
|---|---|---|
| `Job#0` | Running | 1.3M → 4.1M → 7.0M → 9.3M |
| `Job#1` | Blocked `sceKernelWaitSema` | 1.0M → 2.8M → 4.6M → 6.3M |
| `GPUex` | Blocked `pthread_cond_wait` | 93k → 211k → 328k → 424k |
| `Flipx` | Running (`sceVideoOutSubmitFlip`) | 16k → 33k → 49k |
| `60Hz` | Blocked `pthread_cond_wait` | 12.6k → 25.2k → 37.9k → 48.2k |
| `FexpR` | Blocked `pthread_cond_wait` | **4, and frozen** |

So "91% of guest thread-time waiting" is an engine ticking over with little to
do, not a deadlock. Treat a single hot RIP as a lead, never as a conclusion —
the import counters are the cheap check that settles it, and they were one grep
away.

The one genuinely frozen thread in that table is **`FexpR`** (4 imports, parked
on `pthread_cond_wait` at `ret=0x80077C823`), which is worth a look on its own.

**Next step.** Work out why `0x8004A1810` never runs. The job queue array is
at `[rbx+0x138C0]` with a `0xcc0` stride per worker and head/tail at `+0x40` /
`+0x80` — dumping a few of those head/tail pairs at runtime will confirm every
queue is empty and give the queue object to trace back to its producer. That is
a more productive target than climbing the `0x8004A1810` call chain by hand,
and far more productive than the video-out flags of 6p/6q.

**Correction to 6p/6q — itself corrected by 6s.** The `(!A) && B0` guard does
gate only the bit-9 call site, and the finalize trigger is indeed the low byte
of `+0x94`. But the conclusion drawn from that here — that the display manager
is "not on the critical path" — is **wrong**. 6s shows flag `A`
(`0x80650E1B1`) gates the render path by a second, independent route
(`test byte [A],1 ; je` at `0x8004CCFCE`), so the idle display manager is
precisely the root cause. Read 6s.

#### Trap: do not attach a Windows debugger to SharpEmu

A hardware watchpoint on `entry+0x94` would settle the remaining question in one
run, and `ghidra-mcp` exposes exactly that (`debugger_watch_memory`, 4
watchpoints via the x86 debug registers). It does not work here.

The server is real and startable — it lives at `C:\Tools\ghidra-mcp`, listens on
`127.0.0.1:8099`, and needs `pybag` (the dbgeng binding), which
`uv run --with pybag python -m debugger` supplies without modifying the install.
`debugger_attach(<pid>)` then succeeds and reports 50 modules. But **attaching
destroys the guest session**: immediately afterwards the process is down to 5
threads, every guest thread is gone, and every guest-space read fails with
`0x8007001E` (ERROR_READ_FAULT) — guest text `0x800135350`, guest heap
`0xE410160F90`, and guest stacks `0x7FFFB11FFF00` alike — while host module
reads still work fine (`0x7FF9447F0000` returns a clean `MZ`). The stderr log
shows the emulator running normally right up to the attach (imports at
`#298,881,xxx`), so it is the attach that breaks it, not a pre-existing fault.

Two further notes for anyone retrying this: run the server **without** a
`timeout`, because when the dbgeng server dies it takes the debuggee with it
(that killed one emulator session outright), and always `debugger_detach`
before stopping the server.

A write-watch therefore has to come from inside the emulator if it is wanted at
all — the external-debugger route is closed.

Captures: `step1.*`, `names1.*`, `probe4.*`, `prof1.*` under
`artifacts/gt7-investigation/`.

### 6s. Root cause, verified end to end: flag A gates the menu render path

This supersedes 6r's closing judgement. 6r said the display manager was "not on
the critical path to the menu". **That was wrong** — it is the critical path,
and the link that proves it is a single `test` instruction.

#### The gate

`0x8004CC1D0` is the function whose call at `0x8004CE68D` invokes
`0x8004A1810`, the only path that reaches the context marker `0x8004A00F0`.
Enumerating every conditional branch in `0x8004CC1D0` that jumps *over* that
call, and keeping only those whose test reads a global, leaves exactly three:

| test | global | branch | runtime value | taken? |
|---|---|---|---|---|
| `test byte ptr [rip+0x60411e3], 1` @`0x8004CCFC7` | `0x80650E1B1` (**A**) | `je 0x8004CF08B` | **0** | **YES — skips the call** |
| `cmp qword ptr [rip+0x5442a5e], 0` @`0x8004CD5CA` | `0x805910030` | `jne 0x8004CF76E` | 0 | no |
| `cmp dword ptr [rip+0x64efb92], 2` @`0x8004CD8FB` | `0x8069BD494` | `je 0x8004CF78F` | 0 | no |

`test byte, 1` sets ZF when bit 0 is clear, so `A = 0` makes that `je` fire
every time. The other two globals pass. **Flag A is the only thing standing
between GT7 and its menu render path**, and it is the same `0x80650E1B1` that
6p found guarding the bit-9 call site — reached here by a completely different
route.

#### The full chain

Every link below was confirmed against a runtime dump or probe:

```
display manager 0xE400000D00 is idle
   +0x1AC = -1  (no video-out request was ever made)
   +0x1B0 = -1  (no completion to consume)
      |
      v  so the block at 0x8006078F3 never runs
A (0x80650E1B1) stays 0   and   B0 (0x80650E1B0) stays 0
      |
      v  test byte [A],1 ; je   at 0x8004CCFCE
0x8004CC1D0 skips its call to 0x8004A1810 at 0x8004CE68D
      |
      v  so 0x8004A00F0 is never reached
entry+0x94 stays 0 on all six contexts
      |
      v  so the action 0x80049ED80 never tail-calls finalize
0x800D72020 never runs -> entry+0x95 stays 0
      |
      v  so all six stay pending
the tick rewrites context+0x48 = 3, forever
      |
      v  so 0x80049C88B / state 5 / cmpxchg 5->6 is never reached
context+0x28 is never signalled
      |
      v
main stays parked in GTFramework::Organizer::Impl::initialize()
```

One root cause, eight links, no inference left in the middle.

#### Ruled out along the way

- **`sceVideoOutAddOutputModeEvent` is not it.** It is genuinely unresolved and
  unimplemented (so is `LibwuIonIBw#9#s`), and both sit in GT7's display event
  registration block at `0x800FF0CEF`:
  `sceKernelCreateEqueue` -> `sceKernelAddUserEvent` ->
  `sceVideoOutAddVblankEvent` -> `sceVideoOutAddFlipEvent` ->
  **`sceVideoOutAddOutputModeEvent`** -> **`LibwuIonIBw`**. It was a tempting
  theory that a title which cannot hear about output modes never requests one.
  But GT7's own pump for that equeue (`0x801F94CC0`) dispatches
  `sceVideoOutGetEventId` and handles **only id 0 (flip) and id 1 (vblank)** —
  `cmp eax,1 / jne` loops straight back for everything else. An output-mode
  event would be ignored even if delivered. Implementing it would have been a
  guess at both the raw ident and the public id, and it would have changed
  nothing. Letting the title's own dispatch answer the question is much cheaper
  than guessing an SCE enum.
- Incidentally that pump contains the `test rax,rax / cmp rax,3 / cmp rax,0xD`
  refresh-rate ordinal check that 6n's fix targets, which is a useful
  confirmation that the 6n diagnosis landed in the right code.

#### Where to go next

The question is now exactly one link wide: **what fills the display manager's
request block?**

`0x8006CEB30` is the producer. It bulk-copies a request structure from a stack
buffer into the manager object, field for field:

```
0x8006D1D7C  mov dword ptr [r13+0x1a4], edx
0x8006D1D8B  mov qword ptr [r13+0x19c], rdx
0x8006D1D99  mov dword ptr [r13+0x1b0], edx      ; the result slot
0x8006D1DA8  mov qword ptr [r13+0x1a8], rdx
```

and it is also the only function outside `0x800602130` that reads both `A` and
`B0` (`0x8006CF827`, `0x8006D18E2`, `0x8006D26F0`, `0x8006D32D3`,
`0x8006D32D9`). It has no SCE import, so it cannot be probed by return address;
the source structure at `[rsp+rcx+0xfa8…]` is built earlier in the same
function and is what needs reading. Either the copy never runs, or it is
copying `-1` because the source says "no request" — and distinguishing those two
is the next concrete step.

Do not look for a write-watch to settle it: 6r records that attaching a Windows
debugger destroys the guest session.

Captures: `gates1.*` under `artifacts/gt7-investigation/`.

### 6t. Ghidra decompilation: a missed gate, and two corrections to 6q/6s

Ghidra was finally brought to bear on this, and it immediately found something
hand-disassembly had missed for three sections running. Two earlier conclusions
have to be weakened as a result.

#### How to run it (the MCP route is not required)

The GhidraMCP plugin needs a live GUI instance on port 8089, which needs the
extension installed for the exact Ghidra build. None of that is necessary:
`analyzeHeadless` gives the same decompiler with no GUI and no plugin.

```
python: write text segment to gt7_text.bin   (file 0x21700 .. 0x41B725C)
analyzeHeadless <projdir> gt7b -import gt7_text.bin \
   -processor x86:LE:64:default -cspec gcc \
   -loader BinaryLoader -loader-baseAddr 0x800000000 \
   -noanalysis -scriptPath <dir> -postScript DecompAt.java 0x800602130
```

`-noanalysis` matters: auto-analysis of a 68 MB text section is hours of work
for nothing. `DecompAt.java` simply calls `createFunction` at each address given
and prints `DecompileResults.getDecompiledFunction().getC()`, so only the
handful of functions actually under investigation get decompiled. Re-runs use
`-process gt7_text.bin` instead of `-import`.

Two reliability notes. Ghidra prints
`WARNING: Removing unreachable block` when it cannot resolve an indirect jump
(a switch table) without analysis — if most blocks are pruned, the decompile of
that function is incomplete, not wrong, and the pruned addresses are listed.
And a wrong entry address produces exactly the same symptom, so check the
address really is a function entry first.

#### Method fix: getting real function boundaries

The prologue heuristic used throughout 6p-6s is a recurring source of error. It
attributed `0x8006D1D99` to "func 0x8006CEB30" when Ghidra shows that address is
not in that function at all, and it produced `0x800600CB0` (a small condvar
wait) as the owner of code at `0x800601039`.

A far better source of entry points: **every `call rel32` target in the text is
a real function entry**. Scanning for `E8` and collecting targets yields 77,949
of them; `bisect` then gives the enclosing entry for any address. Confirmed
against known-good entries (`0x8004CC1D0` recovered exactly). Where the nearest
call target is too far back — because a function is only reached indirectly
through a vtable — fall back to scanning for `CC CC` padding followed by a
prologue, which correctly identifies `0x800600D30` as an entry.

#### The gate that was missed

`0x800602130` decompiles to a **deferred-command applier**, and its entire body
is wrapped in one condition that never appeared in any hand disassembly:

```c
void sub_800602130(long *param_1, ...)
{
  if ((char)param_1[0x32] == '\x05') {        // param_1 is long* -> byte at +0x190
     if ((int)param_1[0x34] != -1) { ... *(+0x1A0) = -1; }      // +0x1A0
     if (*(int *)(+0x19c) != 0)    { ... *(+0x19c) = 0; }
     if (0 < (int)param_1[0x33])   { ... *(+0x194) = -1; }
     if (*(int *)(+0x1a4) != -1)   { ... *(+0x1a4) = -1; }
     if ((int)param_1[0x36] == -1) {          // +0x1B0: no completion pending
         ... request branch ...
         *(undefined4 *)((long)plStack_3c8 + 0x1ac) = 0xffffffff;
     } else {                                  // completion branch
         bRam000000080650ca82 = (int)param_1[0x36] == 1;
         bRam000000080650e1b1 = 1;             // <-- flag A = 1
         bRam000000080650e1b0 = bRam000000080650ca82;
         *(undefined4 *)(param_1 + 0x36) = 0xffffffff;
     }
  }
  ...
  *(undefined1 *)(param_1 + 0x32) = 0;        // state reset to 0
}
```

So `[obj+0x190]` must be **5** for any of it to run, and the state machine is:

```
state 3 --> 4   at 0x800601039  (in 0x800600D30)
state 4 --> 5   at 0x8005FF595  (in 0x8005FEF20, the Flipx thread,
                                 immediately after sceVideoOutSubmitFlip)
state 5         consumed by 0x800602130, which resets it to 0
```

`0x8005FF595` is the only site in the entire executable that writes 5 to
`+0x190`, and it is guarded by `cmp al, 4`.

#### Correction 1: the runtime -1 readings prove less than 6q/6s claimed

6q and 6s read `+0x1AC = -1` and `+0x1B0 = -1` from periodic dumps and
concluded "no video-out request has ever been made". **That inference is not
sound.** The decompilation shows `0x800602130` resets `+0x1AC` and `+0x1B0` to
`-1` and `+0x190` to `0` at the end of every pass, so those are also the
*resting* values. A periodic snapshot will read them whether or not requests are
flowing. The same applies to the `+0x190 = 0` reading: 0 is the post-reset
state, not evidence that 4 and 5 are never reached.

What does still hold, because these are never reset: **flag A
(`0x80650E1B1`), `B0` (`0x80650E1B0`) and `0x80650CA82` all read 0 for the whole
boot**, and all three are written together in the completion branch. So the
completion branch genuinely never runs — either state never reaches 5, or when
it does `+0x1B0` is always `-1`.

#### Correction 2: on a non-VR machine the request branch is a dead end

The request branch reads, decompiled:

```c
if (*(int *)(+0x1ac) == 0) {                       // close
    if ((bRam_80650ca82 == 0) && (bRam_80650e1b0 == 1)) { teardown; }
} else {
    if (*(int *)(+0x1ac) != 1) goto skip;          // open
    if (bRam_80650e1b0 == 0) {
        sceUserServiceGetInitialUser(&user);
        if (sub_801f94440(user) != 0) { sub_801f944c0(); }   // VR gate -> bring-up
    }
}
```

and Ghidra confirms the gate exactly:

```c
undefined8 sub_801f94440(undefined8 param_1)
{
  if (cRam0000000806511654 == '\x01') {            // VR-enabled global
    iRam000000080650d4c8 = sceHmd2Open(param_1,0,0,0);
    if (-1 < iRam000000080650d4c8) { sceVrTracker2RegisterDevice(0,handle); return 1; }
  }
  return 0;
}
```

`cRam_806511654` reads **0**, so on a machine with no headset the open request
does nothing at all and falls through to `*(+0x1ac) = -1`. **`B0` can therefore
only ever be set by the completion branch**, i.e. by something posting a result
into `+0x1B0`. That makes the completion path the whole ball game, not one of
two options.

There is a second write of flag A further down the same function:

```c
bVar40 = 1;
if (bRam000000080650e1b0 == 1) {
    if ((cRam00000008069bd491 == '\x01') && (cRam00000008069bd490 != '\0')) { iVar41 = 2; bVar40 = 0; }
    else { iVar41 = 1; }
}
if (iRam00000008069bd494 != iVar41) {
    cRam00000008069bd492 = '\x01';
    bRam000000080650e1b1 = bVar40;          // A
    iRam00000008069bd494 = iVar41;          // the mode global from 6s gate 3
}
```

which also keys off `B0 == 1`, and writes the very `0x8069BD494` mode global
that 6s measured as 0. So both A writes bottom out on `B0`.

#### Where this leaves the chain

6s's gate finding is unaffected and still the key result: `test byte [A],1 ; je`
at `0x8004CCFCE` skips the render call, A reads 0, and the other two globals on
that path pass. What has changed is the shape of the question below it. It is no
longer "why was no request made" but:

**what posts a result into `[obj+0x1B0]`, and does the state ever reach 5?**

Concrete next steps, in order of value:

1. Settle whether state reaches 5 at all. `0x8005FF595` (Flipx, one instruction)
   is the only writer of 5 and sits right after `sceVideoOutSubmitFlip`, which
   is known to run 4,559 times per boot. A probe on the
   `sceVideoOutSubmitFlip` return inside that function (`0x8005FF549`) fires on
   the right thread; comparing its hit count against whether `+0x190` is ever
   observed as 4 would settle it.
2. Decompile `0x800600D30` properly. It holds the 3 -> 4 transition and is the
   most likely home of the poster, but it is reached indirectly and contains a
   switch table, so Ghidra prunes it without analysis. Either run analysis over
   that one function's range, or disassemble the region in the script before
   decompiling.
3. Note `0x8006059A1`: `xor dword ptr [rcx+0x18f], 0x100` — this toggles bit 0
   of the state byte at `+0x190` (bit 8 of the dword based at `+0x18F`), inside
   `0x800602130` itself. `3 = 2|1` and `5 = 4|1`, so the low bit of the state
   looks like a flag the manager flips rather than part of a plain counter. Do
   not assume the states are a simple linear sequence before reading that code.

Captures: Ghidra project under the session scratchpad; decompiler output in
`dec_dm.txt`, `dec_6ceb30.txt`, `dec_state.txt`.

### 6u. Root cause found and fixed: an unresolved import returns an *error*, not zero

This is the section that closes 6s/6t's chain, and it turns on a detail of
SharpEmu's own import dispatch rather than anything in GT7.

#### The mechanism

`DirectExecutionBackend.Imports.cs` handles a missing export like this:

```csharp
dispatchResolved = false;
orbisGen2Result = OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
cpuContext[CpuRegister.Rax] = unchecked((ulong)(int)orbisGen2Result);
```

So an unresolved import returns **`ORBIS_GEN2_ERROR_NOT_FOUND`**. (The
`_unresolvedReturnStub` of `31 C0 C3` — `xor eax,eax; ret` — is a different
mechanism and is *not* what a title sees here. 6q assumed the stub's zero was
the observable return and reasoned from that; that assumption was wrong.)

That matters enormously for any title that branches on a result. GT7's HMD/
display init at `0x800FEE780` is exactly such a caller:

```
0x800FEE799  mov  edi, 0x12a
0x800FEE7A6  call sceSysmoduleLoadModule       ; result unchecked
0x800FEE7B6  vmovups [rdi], xmm0               ; zero a 16-byte param block
0x800FEE7BA  call sceHmd2Initialize
0x800FEE7BF  test eax, eax
0x800FEE7C1  jne  0x800FEEB37                  ; error -> skip the whole block
...
0x800FEEB25  call sceVrTracker2SetCoordinateSystem(1)
0x800FEEB2A  mov  byte ptr [rip+0x5522b23], 1  ; 0x806511654 = 1
```

`sceHmd2Initialize` was unresolved, so it returned an error, so GT7 skipped the
entire block and `0x806511654` stayed 0. That global is the one
`sub_801f94440` demands:

```c
undefined8 sub_801f94440(undefined8 param_1)
{
  if (cRam0000000806511654 == '\x01') {
    iRam000000080650d4c8 = sceHmd2Open(param_1,0,0,0);
    if (-1 < iRam000000080650d4c8) { sceVrTracker2RegisterDevice(0,handle); return 1; }
  }
  return 0;
}
```

which gates the video-out bring-up `0x801F944C0`, which sets `B0`
(`0x80650E1B0`) and flag `A` (`0x80650E1B1`), and `A` is what 6s proved gates
the menu render path at `0x8004CCFCE`.

#### The fix

`sceHmd2Initialize` (NID `c812oYs7Vsc`, `libSceHmd2`) implemented in a new
`src/SharpEmu.Libs/Hmd2/Hmd2Exports.cs`, returning success and touching no guest
memory — the parameter block is an input the caller has already filled in.

The behavioural claim is that **the HMD library initialises successfully whether
or not a headset is attached**, device presence being reported separately by
`sceHmd2GetDeviceInformation`. Evidence: the title loads the module with
`sceSysmoduleLoadModule(0x12A)` first and only then calls `Initialize`, and the
code immediately after queries the device and tolerates *that* failing
(`sceHmd2GetFieldOfViewWithoutHandle` failing merely skips some FOV maths and
continues). A library-init call that hard-failed on every non-VR console would
make this whole path dead on retail hardware, which cannot be right. This is a
**strong inference, not a documented fact** — it is not proven from an SDK
header — but it is now supported by the runtime result below.

#### Verified effect

Before: `sceHmd2Initialize` unresolved (1 call); nothing else in the HMD family
ever called; `0x806511654 = 0`.

After: `sceHmd2Initialize` resolved (0 unresolved), and GT7 now executes the
entire block it previously skipped — newly reaching
`sceHmd2GetDeviceInformation` (**15,969 calls**, polled per frame),
`sceHmd2GetFieldOfViewWithoutHandle`, `sceHmd2ReprojectionQueryBufferSizeAlign`,
`sceHmd2ReprojectionQueryDisplayBufferSizeAlign`, `sceHmd2ReprojectionInitialize`,
`sceVrTracker2QueryMemory`, `sceVrTracker2Initialize` and
`sceVrTracker2SetCoordinateSystem` — and **`0x806511654` now reads 1**
(`memdump 0x806511650: 0000000100000438`, byte 4 = 0x01).

Build clean, full suite green at **1,071 tests**.

It did **not** reach the menu. `A` and `B0` still read 0 and `context+0x48` is
still 3, because `sceHmd2Open` is never called: `sub_801f94440` runs only from
the display manager's request branch, which fires once, early — and on the run
observed, before the flag was set. The one-shot request does not repeat.

Careful with byte order when reading these dumps. The dump prints a little
-endian qword, so `0x0000000100000438` means byte `+4` is `0x01`, not byte
`+5`. Misreading that cost a wrong "the flag is still zero" conclusion for
several minutes.

#### Superseded input hypothesis

The title-screen screenshot and button-prompt cue are valid observations, but
the conclusion that GT7 has no usable pad was wrong. It came from treating the
first invalid-handle read as steady state and from missing a call through the
shared opener. Sections 6v-6w show the standard `type=0` path, handle 1, the
later `Controls:` announcement, and Cross reaching GT7's own pad-data buffer.
Input is not the current blocker. The wheel probe must still remain rejected
when no wheel is attached.

Captures: `hmd1.*`, `hmd2.*`, `st4.*` under `artifacts/gt7-investigation/`;
Ghidra output in `dec_vr.txt`, `dec_pad.txt`.

### 6v. Pad call sites closed; the display completion producer is still open

This follow-up used Ghidra 12.1.3 headless with the in-tree
`scripts/ghidra/DecompAt.java` and a full `call rel32` scan of the extracted
text. Import resolution identifies thunk `0x804193370` as `scePadOpen` (GOT
`0x805910F90`, relocation symbol `xk0AcarP3V4#b#c`). It has exactly two direct
callers in the whole text: `0x800FD9BC2` and `0x800FDA417`.

`0x800FD9BC2` is the already-known wheel probe. The other path is deliberate:

```text
0x800FDA0F0  scePadInit()
             sceSystemGestureInitializePrimitiveTouchRecognizer(0)
             sub_800FDA130(0x806E2B940, 0xFF, 0x10)
0x800FDA130  -> sub_800FDA350(object, userId, type)
0x800FDA350  -> scePadOpen(userId, type, 0, NULL)
             -> on success, scePadSetMotionSensorState(handle, 1)
```

So `0xFF/0x10` is not an incorrectly decompiled wrapper or shifted ABI: the
manager hardcodes those values and passes them unchanged to the real
four-argument import. However, counting only direct import-thunk callers hid a
real standard path. Valid enclosing function `0x800371180` contains:

```c
userId = entry->field_4c;
if (userId != 0xff && entry->field_48 == 1) {
    entry->field_48 = 0;
    sub_800FD9ED0(entry);
    sub_800FDA130(entry, userId, 0);  // -> scePadOpen(userId, 0, 0, NULL)
}
```

A bounded boot then resolved the gate. The manager list head is non-null and
contains an entry at `0x806E2FFB0`. Across all three snapshots its fields are
stable: `+0x48 = 0`, `+0x4C = 0x10000000`, `+0x50 = 0` and `+0x54 = 1`.
Thus the entry already represents the primary user's standard controller and
stores handle 1; the standard open is skipped specifically because reconnect
flag `+0x48` is clear, not because the user-id predicate fails.

`PadOpenCore` remains unchanged; accepting the wheel's `type=2` would still
fabricate hardware. The remaining input question is what is supposed to set
`entry+0x48` and why downstream code still polls `0xFFFFFFFF` instead of this
entry's stored handle 1. Static work should now follow writers of `+0x48` in
this manager and the consumers of `+0x54`; the already-observed import return
at `0x800FDA41C` only repeats the separate `0xFF/0x10` failure.

The display-side follow-up also corrects a false producer match. The alleged
function boundary `0x8006D1BDE` lands inside an instruction; `0x8006D1D99` is
part of the large `0x8006CEB30` body. Its destination is a freshly allocated
`0x1D4`-byte render object that is then queued, not the reproducible display
manager object at `0xE400000D00`. Its store to object `+0x1B0` therefore does
not identify the producer of the manager's completion slot.

What remains proven: `0x800600D30` writes manager state `+0x190 = 4` at
`0x800601039`; `0x800602130` consumes manager `+0x1B0` at `0x800606FA9`, sets
`A/B0` for a non-`-1` result at `0x8006078F3`, and resets the slot. The actual
producer is still unknown. `SHARPEMU_WATCH_WRITE` cannot settle it because it
observes managed/HLE writes, not native guest stores, and Windows dbgeng must
not be attached. This needs temporary native guest-store instrumentation or a
safe hardware watchpoint mechanism.

Verification on this checkpoint: Release tests pass **1,071/1,071**. The build
emits three `SHEM006` warnings (two VoiceQoS names and
`sceAgcAddPrimStateRegisters`), so older “zero warnings” wording is stale.

Captures: `ghidra-pad-20260912.log`, `ghidra-pad-followup-20260912.log`,
`ghidra-pad-callers-20260912.log`, and `padgate1.*` under
`artifacts/gt7-investigation/`.

### 6w. The pad path is live; the invalid-handle read is transient

The §6v conclusion still over-weighted one early failed import. Every fresh
boot first calls `scePadReadState` from return `0x800371220` with handle `-1`,
but then prints the `Controls:` announcement. Ghidra explains the ordering:
`0x800371180` reads `entry+0x54` before the later standard-open/reconnect pass.
The manager subsequently stores handle 1, and later reads succeed silently.

The reconnect producer is `0x800FDA2D0`: it stores the user id to `entry+0x4C`
at `0x800FDA2D9`, sets `entry+0x48=1` at `0x800FDA2DC`, and enqueues the entry.
It has no direct `call rel32` caller and is probably reached indirectly. The
consumer clears `+0x48` at `0x800371799`; cleanup `0x800FD9F20` stores
`+0x54=-1` at `0x800FD9F47`, and successful open stores the new handle at
`0x800FDA43B`. This closes the relevant manager-field protocol.

A bounded runtime probe settled whether those successful reads carry input.
It used the existing `SHARPEMU_AUTO_CROSS` diagnostic and dumped GT7's own
`ScePadData` buffer at `0x806E30040` once per second. Neutral samples are:

```text
8080808000000000 0000000000000000
```

During scheduled Cross pulses they become:

```text
8080808000004000 0000000000000000
```

The dump prints each eight-byte word most-significant digit first, so the
second value represents little-endian button word `0x00004000` followed by
four centred stick bytes (`0x80`). Later samples return to neutral. This proves
the complete host-input -> `scePadReadState` -> GT7 buffer path is operating;
the early invalid handle is initialization noise, not the title-screen block.

No pad behavior changed. In particular, the wheel probe remains rejected and
`PadOpenCore` was not relaxed. Priority returns to the menu/display completion
chain and the unknown producer of the display manager's `+0x1B0` slot.

Capture: `padread1.{out,err}.log` under `artifacts/gt7-investigation/`.

### 6x. Display writer sweep and the exact next experiment

A fresh whole-text sweep rejected every remaining raw `+0x1B0` match as a
producer for the live display manager. The only proven manager consumer is:

```text
0x800606FA9  mov  eax,[r14+0x1b0]
0x800606FB0  cmp  eax,-1
0x8006078F3  cmp  eax,1
0x8006078F6  mov  byte [0x80650e1b1],1   ; A is set for any completion
0x8006078FD  sete byte [0x80650ca82]
0x800607904  sete byte [0x80650e1b0]     ; B0 is result == 1
0x80060790B  mov  dword [r14+0x1b0],-1   ; consume/reset
```

The runtime object is reproducibly `0xE400000D00`, its first qword is
`0x806461930`, and the watched field is therefore `0xE400000EB0`. Rejected
offset matches include constructors or fields in unrelated graphics/resource
objects at `0x80056E315`, `0x8005DF411`, `0x80060B12E`, `0x800610CDF`,
`0x80061E7D3`, `0x800636DD3`, `0x80042F679` and `0x80043019D`.
`0x8006D1D99` is also unrelated, as §6v explains. An offset match alone is not
evidence of receiver identity.

The request-side fork is now precise. `+0x1AC == 1` reaches `0x800607939`,
gets the initial user and calls `sub_801f94440`, which calls `sceHmd2Open`; it
is the VR enable/open route. Current captures have no call to the
`sceHmd2Open` NID `f3kPeoTZnIE`, so the unresolved import has not been observed
failing on this path. The ordinary non-VR completion can instead post result 0:
the consumer still sets `A=1` while leaving `B0=0`. The producer of that result
is the remaining link.

The heavily polled unresolved `sceHmd2GetDeviceInformation` is not a justified
fix. The startup call at `0x800FEE7CE` ignores its result. The per-frame call at
`0x800602A32` also ignores the result and only compares the device-info global
at `0x80650D4D0` with its previous contents. Failure leaves it unchanged.
`sceHmd2GetFieldOfViewWithoutHandle` failure only skips FOV calculations.

Periodic dumps cannot catch this field because the consumer resets it, and the
existing `SHARPEMU_WATCH_WRITE` sees HLE writes only. `GuestImageWriteTracker`
is also unsuitable: it disarms an entire hot page after its first write. The
next bounded experiment is an **internal opt-in hardware write watch** on the
four-byte slot `0xE400000EB0`, applied to registered guest host threads and
logging guest RIP, thread and the post-store value from the vectored exception
handler. This does not require attaching a Windows debugger. No implementation
of that experiment has been added yet.

For exact assembly in the `-noanalysis` Ghidra project, the in-tree
`scripts/ghidra/ListingRange.java` accepts `0xSTART 0xBYTE_COUNT` and prints a
bounded listing. This complements `DecompAt.java` and avoids broad analysis of
the 68 MB text segment.

The same capture also fixes the resting state of the object, sampled 30 times
over one boot and identical every time:

```text
memdump 0x000000E400000E88: 0000000000000000 FFFFFFFF00000000   ; +0x190 = 0
memdump 0x000000E400000EA8: FFFFFFFF00000000 00000000FFFFFFFF   ; +0x1AC = -1, +0x1B0 = -1
memdump 0x000000080650E1B0: 0000000000000000 ...                ; B0 = 0, A = 0
```

That is the resting state the consumer leaves behind, so it proves only that no
completion pass ever finished with `A` set; it does not prove the body never
runs. `A` and `B0` are the two bytes that are never reset, and both are still
zero at the title screen, so no completion has been posted at all.

Capture: `displayobj1.{out,err}.log` under
`artifacts/gt7-investigation/`.

### 6y. The display-flag chase was a misread; the real wait is the menu update

**Correction first.** Sections 6s-6x treated `A = *0x80650E1B1` as the gate on
the menu render path. It is not. The full sequence at the gate is:

```text
8004ccfb1  CMP  byte [0x80650e1b0],1     ; B0
8004ccfc5  JNZ  0x8004ccfd4              ; B0 != 1 -> skip the A test entirely
8004ccfc7  TEST byte [0x80650e1b1],1     ; A, reached only when B0 == 1
8004ccfce  JZ   0x8004cf08b
8004ccfd4  TEST EAX,0x600                ; [[0x805ff9938]+0xd0]
```

With `B0 == 0` — the non-VR case — execution continues at `0x8004CCFD4` without
ever reading `A`. So `A` never being set is not a blocker, and neither is the
display manager's `+0x1B0` completion slot. Do not spend more time on
`sceHmd2Open`, the `+0x1B0` producer, or `sceVideoOutAddOutputModeEvent` on the
strength of that chain. The earlier reading took the `JNZ` as if it fell into
the `A` test rather than jumping past it.

**What main is actually waiting for.** `MENU::mUpdateContextPS4::update`
(`0x800F853A0`) copies its arguments to `ctx+0x78`/`+0x80`, sets `ctx+0x48 = 1`
and waits on the event at `ctx+0x28`. The consumer is `0x80049C650`, reached
through the owner's `+0x90`, and it is a four-state machine on `ctx+0x48`:

| state | action |
|---|---|
| 1 -> 2 | calls vtable `+0x2B8`, fills the request vector, then sets 3 |
| 3 -> 4 | walks the entry array at `ctx+0x6A8` (count at `ctx+0x6D8`) |
| 5 -> 6 | calls vtable `+0x2C0`, sets `ctx+0x48 = 0`, `Event::set(ctx+0x28)` |

The state-3 pass re-dispatches every entry with `+0x366 == 1 && +0x95 == 0` as a
job (`0x80049D4A0(0x801dfcea0, entry, 0)`) and then returns to state 3. Only a
pass where no entry qualifies advances to 5 and wakes main.

**Live state.** `ctx = 0xEC0309D000`, `ctx+0x48 = 3` in every snapshot, count 6,
and entry[0] has `+0x366 = 1`, `+0x95 = 0`; entries 1-5 have `+0x366 = 0`.
All six share vtable `0x8054E66F0`, whose typeinfo name reads
`N4MENU17mRenderContextPS4E` = `MENU::mRenderContextPS4`.

**Why entry[0] never finishes.** The job callback is `0x801DFCEA0` ->
`0x80049ED80`, which sets `+0x95` (via `0x800D72020`) **only when `+0x94 != 0`**.
A hardware write watch on `entry+0x94` recorded exactly one write in a whole
boot: the constructor's `mov [rbx+0x90], rcx` with rcx = 0 at `0x800D71DDE`.
Nothing ever sets it.

The only code that sets it is `0x801DA10E0`, which loops the same
`+0x6A8` array and writes `[entry+0x94] = 1` for each. It has **no `call rel32`
caller**; a guest-memory pointer sweep found its address in exactly one place,
a runtime dispatch record at `0xE400D98480`. An execute breakpoint on it over a
full boot never fired, so GT7 never issues that command. Its first act is to
read the global `0x806DA1358`, which is **0** in every snapshot, and fall back
to a lookup (`0x800553260`) that can also fail.

Open question for the next session: what issues the end-contexts command, and
whether `0x806DA1358` being null is cause or consequence.

**New tooling this required.** `SHARPEMU_WATCH_GUEST_WRITE=<addr>[:1|2|4|8]`
and `SHARPEMU_WATCH_GUEST_EXEC=<addr>` program a debug register on every
registered guest thread and report the guest RIP (and, for the execute form, the
return address and rdi/rsi). Both accept the pointer-deref spec form
(`[0xEC0309D6A8]+0x366`), resolved lazily, because **guest heap addresses move
between boots** — `ctx+0x6A8` held `0xE410160F10` in one run and
`0xE410158F10`/`0xE400C33CC0` in others. An absolute address captured from a
previous boot is how the first `+0x366` watch silently watched nothing.

The title's main thread is not a registered guest thread, so it is never armed;
an execute watch cannot prove "no thread ever ran this" for code main runs.

**`sceNetRecv` / `sceNetRecvfrom` were missing entirely.** `libSceNet` had
socket/bind/listen/accept but no receive path, so GT7's network thread took
`ORBIS_GEN2_ERROR_NOT_FOUND` 3,362 times per boot on each of them — a code no
socket can return, so its would-block path was unreachable. Both are implemented
over the existing host-socket registry, with `SCE_NET_MSG_DONTWAIT` answered
from `Socket.Poll` instead of parking a guest thread. Unresolved count is now 0;
it did not move the menu stall.

**Audio: the Sndz engine tears itself down, and always has.** `SceSndzRenderThread`
exits after 15 imports with `sceKernelDeleteEventFlag` at `0x803590F1E`, and
`SceSndzAudioOutMain` exits after ~203k imports. `0x803590C30` is the engine's
command dispatcher (codes `0x26003100` start, `0x26003200` stop, `0x260033xx`
teardown), so this is GT7 asking for the shutdown, not an HLE failure — no audio
export returns an error in the capture. The same exits appear in captures from
10:02, 11:06 and 12:xx, i.e. before any change in this session, so it is
long-standing rather than a regression. It explains a boot where only the first
cue plays.

Captures: `ctx1-ctx5`, `ww1-ww4`, `ex1`, `ptr1`, `net1` under
`artifacts/gt7-investigation/`.

### 6z. What the frangametv fork does and does not provide — ruled out

Cloned to the ignored `artifacts/frangametv-sharpemu/` (head `e206ae4`) and
compared export-by-export. It shares our upstream and layout, and its
`scripts/ps5_names.txt` is byte-identical, so the only real difference is
coverage.

| | ours | fork |
|---|---|---|
| distinct export NIDs | 1,181 | 2,227 |
| `PreferLle = true` declarations | libc-only heuristic | 815 |

Of GT7's **33 distinct unresolved imports**, the fork covers **6**:
`sceAgcAcbRewind`, `sceAgcAsyncRewindPatchSetRewindState`,
`sceSigninDialogInitialize`, `sceSigninDialogTerminate`, `sceRudpInit`,
`sceShareFeatureProhibit`. The two that matter by volume — the AGC rewind pair,
9,844 calls each per boot — are **`PreferLle` declarations, not
implementations**: they route the call into the title's real `libSceAgc.prx`.
GT7's `sce_module` ships only `libc.prx`, `libSceNpCppWebApi.prx` and
`libScePfs.prx`, so there is no AGC module to route to and the declaration
would resolve to nothing. That is consistent with the reported result that GT7
does not boot on the fork either.

The remaining 1,069-NID gap is mostly irrelevant here: 319 are LLE-only
declarations, and of the HLE ones 242 are `libSceNpCppWebApi` (a module GT7
ships and therefore runs LLE) plus a long tail of `libc` maths and locale
entry points GT7 also gets from its own `libc.prx`. Their `AgcExports.cs` HLEs
five exports we lack (`sceAgcDcbCopyData`, `sceAgcDcbWaitOnAddressGetSize`,
`sceAgcDcbSetShRegisterDirect`, `sceAgcDriverGetEqContextId`, `Ikfdt-rIqCE`) —
**GT7 calls none of them**.

The transferable idea is `PreferLle` itself: for a title that ships a module,
declaring an export LLE-preferred beats guessing an ABI. It does not help the
27 exports GT7 needs and neither tree implements, of which the highest-volume
are `sceHmd2GetDeviceInformation` (10,736 calls) and `sceShareGetRunningStatus`
(4,516). Do not re-run this comparison; re-check only if GT7's shipped module
list changes.

### 6aa. What shadPS4 provides — two real fixes, and a limit worth knowing

shadPS4 is a **PS4** emulator. GT7 shipped on both consoles; shadPS4 boots the
PS4 build (CUSA), while this work targets the PS5 build PPSA01317. The GPU
halves therefore do not transfer at all — PS4 is GNM, PS5 is AGC. What does
transfer is the shared SCE library ABI, since most of `libSceFont`,
`libSceNet`, `libkernel` and friends carry over between generations.

Checked their coverage of the exports GT7 still calls unresolved. Most of the
high-volume ones are **named but not implemented** — `sceHmd2GetDeviceInformation`
(10,736 calls/boot), `sceShareGetRunningStatus` (4,516),
`sceDeviceServiceQueryDeviceInfo_`, `sceDeviceServiceGetEventState` and
`sceVideoOutAddOutputModeEvent` appear only in `src/core/aerolib/aerolib.inl`,
which is the same public NID catalogue `scripts/aerolib_catalog.py` already
reads. Implemented and useful: `sceFontRenderSurfaceSetScissor`, `sceNetRecv`,
`sceNetShutdown`, `sceNetInetNtop`, `sceNpHasSignedUp`.

Two fixes came out of it, both re-implemented here rather than copied:

- **`SceFontKerning` is four floats, not three.** `offsetX`, `offsetY`,
  `positionX`, `positionY` — sixteen bytes. Our `sceFontGetKerning` defined only
  twelve, leaving the caller's fourth field holding whatever was on its stack.
  This closes the open review-debt item that flagged the size as unverified;
  GT7's two observed reads at `+0x00` and `+0x08` never covered it.
- **`sceFontRenderSurfaceSetScissor` implemented** (30 calls per GT7 boot,
  previously unresolved). It clips glyph rendering to a rectangle stored in the
  render surface at `+0x18..+0x24` — the same layout our own
  `sceFontRenderSurfaceInit` already wrote, which independently confirms it.
  The bounds are unsigned, so a rectangle starting left of or above the surface
  clips to zero rather than wrapping, and one that ends before the surface
  begins collapses to empty.

Neither moved the menu stall, which remains title-side dispatch. Both are
covered by regressions in `FontExportsTests`.

Clone kept at the ignored `artifacts/shadps4/`. Note the licence difference:
shadPS4 is GPL-2.0 and this project is GPL-2.0-or-later, so treat it as a
reference for ABI facts — struct layouts, error codes, clamping behaviour — and
write the implementation here, which is also what CONTRIBUTING's clean-room
rule asks for.

### 6ab. The four loudest unresolved imports, named and mostly fixed

**Symptom.** Ranking the unresolved-import warnings over a full boot gave four
NIDs accounting for essentially all of them:

| NID | calls | symbol |
|---|---|---|
| `bIi4YUfSRys` | 19,021 | `sceHmd2GetDeviceInformation` |
| `DwICrVxerkY` | 18,154 | `sceAgcAcbRewind` |
| `eWaWyFegzgQ` | 18,154 | `sceAgcAsyncRewindPatchSetRewindState` |
| `crFxyW3HdK0` | 8,645 | `sceShareGetRunningStatus` |

**How they were named.** The warning line carries only the NID. shadPS4's
`src/core/aerolib/aerolib.inl` is a flat NID-to-name table of ~94k entries and
named all four. Two independent confirmations followed: the fork under
`artifacts/frangametv-sharpemu` carries the same names for the two Agc NIDs,
and this repo's own `SHEM004` analyzer recomputes the NID from the export name
at build time — it rejected a deliberately wrong NID with "computed NID is
`bIi4YUfSRys`". None of the four tripped `SHEM006`, so all four names were
already in `ps5_names.txt`.

**Locating the call sites without a debugger.** Every `call rel32` target in
the text segment is a real function entry; a sorted list of them plus a bisect
gives the enclosing function for any return address. Building that list from
`artifacts/gt7_text.bin` yielded 77,949 targets, matching the count recorded
earlier for this binary — which also re-confirms the segment mapping. Each of
the four NIDs was then checked from two separate call sites, so no conclusion
rests on a single PLT thunk.

**The two Agc exports — fixed.** Disassembly from the enclosing entry shows the
pattern plainly:

```
8000CD421  call <sceAgcAcbRewind>        ; rewind(acb, 0, 0)
8000CD426  mov  [r12+0x10], rax          ; keep the returned packet pointer
...                                       ; three gates built this way
80060640D  call <sceAgcAsyncRewindPatchSetRewindState>   ; f([rbx+0x10], 1)
```

GT7 writes three CP rewind gates per frame, keeps their packet pointers, then
releases each one. That is exactly the mechanism this tree already implements
for the DCB — `sceAgcDcbRewind` writes `IT_REWIND` with the valid bit clear and
the submit parser suspends on it until `sceAgcRewindPatchSetRewindState` sets
bit 31. IT_REWIND is a CP packet, not a graphics-ring one, so the ACB encodes
it identically; both new exports forward to the DCB implementations, the way
`sceAgcAcbJump` already forwards to `DcbJump`. Until now every one of those
18k calls returned `ORBIS_GEN2_ERROR_NOT_FOUND` and the title stored
`0xFFFFFFFF80020002` where a packet pointer belonged.

The ACB entry point takes a third argument the DCB one does not. Every observed
call passes zero, so it is ignored rather than guessed at.

**`sceShareGetRunningStatus` — fixed, but neutral.** The caller computes
`ret == 0 && (status & 0x3C)`, so an error return and a cleared status word
reach the same result. Implemented anyway (write 0, return OK) because leaving
it unresolved has the caller mask bits out of its own uninitialised stack.

**`sceHmd2GetDeviceInformation` — implemented, after a wrong turn worth
recording.** The caller's branch is:

```
801F951C1  call <sceHmd2GetDeviceInformation>(&info)
801F951C6  test eax, eax
801F951C8  jne  801F951D0        ; error -> no-headset path
801F951CA  cmp  byte [rbp-0x4C], 0
801F951CE  jne  801F9520E        ; present -> headset path
```

The presence byte at +0x14 only earns its place if a *successful* call can
still mean "nothing attached" — if absence were reported through the return
code, that second test would be dead. So the export returns success with the
record cleared. The record's size is unknown from any header available here;
0x18 is bounded by the title's own layout (a caller reads +0x14, and the next
distinct object sits 0x1C after the record), so a short record cannot be
overrun.

**The wrong turn.** The first attempt at this concluded the opposite — that
implementing it *crashed* the boot — on the strength of a three-way comparison
with **one boot per arm**: baseline clean, Agc+Share clean, Agc+Share+Hmd2 died
on an access violation writing to a reserved, uncommitted region. That
reasoning was wrong, and the error is worth naming because it is easy to repeat:
the crashing run was the only one launched while a *second* emulator instance
was still live and an 11 GB `ReadProcessMemory` scan was sweeping it. Re-running
the same build three times on a quiet machine produced no access violation at
all, and the one-off has not reproduced since.

The measured effect is a straightforward improvement. Over matched 75-second
runs:

| build | unresolved imports | guest threads | access violations |
|---|---|---|---|
| Agc + Share | 7,264 / 8,553 | 148 | 0 |
| Agc + Share + Hmd2 | 241 / 239 | 148 | 0 |

**Rules this cost.** One sample is not a comparison when the thing being
measured is a crash — a fault that appears once and never again is a race or an
environment artefact until a repeat says otherwise. And benchmark runs need a
quiet machine: leaving an old session running is not free, because it competes
for exactly the physical memory the run under test is trying to commit.

**Also recorded:** the access violation was declined by the lazy-commit fault
handler (`TryHandleLazyCommittedPage`), which turns a fault on guest-owned
reserved memory into a fatal error when the address is not in a tracked region.
Whether that decline was correct is still unknown — it did not fire again — so
the handler now logs the decline under `SHARPEMU_LOG_LAZY_COMMIT=1` (fault
address, access type, whether a CPU context was active, region count, and the
VirtualQuery state). That way the next occurrence is diagnosable instead of
being another unexplained crash dump.

### 6ac. The volume keys rotated on PS5; the decrypted data is in guest RAM

The plan to read GT7's boot script (`main.adc`) by extracting the volume did not
survive contact. GTToolsSharp opens GT7 volumes with a fixed ChaCha20 key, and
that key is from the **PS4 1.00** build: `gt7unpack` on this title's `gt.idx`
reports "Failed to decrypt volume or volume is not a GT7 MPH volume".

The key is not recoverable from the binaries. The header's first plaintext word
is derivable — the CRC layer's first word depends only on the base CRC, so the
required plaintext, and therefore the required first keystream word, is a
constant — which turns key recovery into a 32-bit oracle over candidate key
positions. A scan of `eboot.bin`, `libSieRdNNabla.prx` and every other shipped
module, testing each offset as a key under five key/nonce layouts, found
nothing. The oracle itself was verified against a synthetic binary with a known
key first, so the negative result is trustworthy: the PS5 build does not store
the key as a contiguous literal.

**None of this was needed, and the premise was wrong.** See 6af: the volume's
*data files* are not encrypted at all. Only `gt.idx` is. The script opens
straight off the disk with no key.

The other useful finding is that guest virtual addresses are host virtual
addresses under native execution, so a plain `ReadProcessMemory`
scan of the running emulator reads guest memory directly — no emulator change,
no debugger attach. Scanning a live GT7 session that way found the string
`main.adc` resident at `0xE400E496BD`, along with other `.adc` paths: the
volume's path table is decrypted in RAM. `ADCH` (compiled Adhoc script) and
`[tQc` (MPH volume header) do not appear, so the scripts are either not resident
in raw form or not stored that way in this build.

Two traps worth recording. The guest lives in the **child** process — SharpEmu
relaunches itself as `--sharpemu-mitigated-child`, and the parent has only
~125 MB mapped, so scanning the process you launched finds nothing. And a
running emulator locks the build output; building the CLI to a separate
directory with `-o` avoids having to stop a live session, and a baseline boot
from that layout behaves identically, so it is safe to compare runs that way.

### 6ad. What is left unresolved, and the device-enumeration lead

With the four exports of 6ab in place, a 75-second run drops from ~7,900
unresolved imports to ~240, and the survivors are a short tail:

| NID | calls / 75 s | symbol |
|---|---|---|
| `9vA2aW+CHuA` | 70 | `sceNetInetNtop` |
| `UNMEa+5lrUA` | 69 | `sceDeviceServiceQueryDeviceInfo_` |
| `9ddRUOV8Q5A` | 68 | `sceDeviceServiceGetEventState` |
| `TSM6whtekok` | 6 | `sceNetShutdown` |
| `Oad3rvY-NJQ` | 3 | `sceNpHasSignedUp` |

`sceNetInetNtop` is implemented: standard BSD semantics, the inverse of the
`sceNetInetPton` already present. Worth noting the ABI trap it hides —
`inet_ntop` reports failure by returning **NULL**, not an error code, so the
usual `SetNetError(ctx, <error>, <errno>)` pattern used everywhere else in
`NetExports` is wrong here. It puts the error code in RAX, which the caller
reads as a valid pointer. The failure paths set errno and return 0 instead. A
test caught this, which is the argument for writing one even for a function
this small.

**The lead: the device poll never finds anything.** The two
`sceDeviceService*` NIDs are a matched pair inside one function
(`0x8005D4850`), called about once a second forever. The enumerating call is:

```
8005D4D7E  mov  edi, 0x7001          ; device class
8005D4D87  mov  r8d, 1
8005D4D8D  push 0x70                 ; record size
8005D4D8F  push r10                  ; out count
8005D4D91  call <sceDeviceServiceQueryDeviceInfo_>
8005D4D96  add  rsp, 0x10
8005D4D9A  test eax, eax
8005D4D9C  js   8005D4CC1            ; negative -> give up, keep polling
8005D4DA2  cmp  dword [rbp-0x5b0], 0
8005D4DA9  jg   8005D5D4B            ; count > 0 -> the device-found path
```

Unresolved, this returns `ORBIS_GEN2_ERROR_NOT_FOUND` — negative — so the `js`
is taken every time and the count is never even examined. The device-found path
at `0x8005D5D4B` has never executed in any run. That makes this the most
promising remaining unresolved import: it is not a no-op query, it gates a
branch that is currently unreachable.

What is *not* yet known, and must not be guessed: what device class `0x7001`
denotes, and the layout of the 0x70-byte record the title expects back.
Claiming "no devices" (success, count 0) is one option and is probably wrong —
it reaches the same fall-through the error already reaches, so it would change
nothing. The useful experiment is to determine what a PS5 reports for class
`0x7001`, since `libSceDeviceService` covers peripherals generally rather than
pads specifically, and GT7 idling at a "press any button" prompt while polling
a device enumerator once a second is a suggestive combination — but suggestive
is not evidence, and the pad path is separately known to be live (§6w).

Neither PS5 reference implementation carries `libSceDeviceService`, so the
record layout will have to come from the title's own use of the buffer at
`[rbp-0x3f0]` on the found path.

### 6ae. The device poll, implemented — and what it did not fix

Both halves of the `libSceDeviceService` pair from 6ad are now implemented. The
signature came from the only caller, which is the whole of GT7's use of the
library:

```
lea  r9,  [rbp-0x5b0]      ; outCount, pre-zeroed by the caller
lea  r10, [rbp-0x600]      ; outExtraCount, pre-zeroed
lea  rcx, [rbp-0x3f0]      ; record buffer
mov  edi, 0x7001           ; device class
mov  r8d, 1                ; room for one record
push 0x70                  ; record size
push r10
call sceDeviceServiceQueryDeviceInfo_
```

`sceDeviceServiceQueryDeviceInfo_` reports success with a count of zero and
writes no record; `sceDeviceServiceGetEventState` reports no pending events.
Neither invents a device, and neither needed the meaning of class `0x7001` or
the record layout — both still unknown, and both still off-limits to guesswork.

**The runtime confirms the reading exactly.** Over a 75-second run, with
`SHARPEMU_LOG_DEVICE_SERVICE=1`:

| | before | after |
|---|---|---|
| `query_device_info` calls | 69 (all failing) | **1** |
| `get_event_state` calls | 68 (all failing) | 61 (succeeding) |
| unresolved imports, whole run | 169 | **34** |

The single query is the point. Unresolved, `ORBIS_GEN2_ERROR_NOT_FOUND` happened
to have one of the two bits set that the caller tests with `test al, 3`, so GT7
re-enumerated once a second forever in response to an error it was reading as an
event mask. With the event state reporting nothing pending, it enumerates once,
learns there is nothing attached, and stops — which is what a console with no
peripheral of that class does. The trace also confirms the stack-argument
offsets independently: it prints `max=1 record_size=0x70`, the values the
caller pushed.

**What it did not do.** Guest thread count is unchanged at 148 and the boot
still idles at the same place. This was a correctness fix, not the menu
blocker — an important distinction, because "the loudest unresolved import"
has now been the wrong proxy for "the thing that is stuck" four times in a row
(§6q, §6u, §6ab, here). Frequency ranks candidates; only a branch that is
provably unreachable is evidence.

**Still deliberately unimplemented** in this library: nothing. The remaining 34
unresolved calls per run are a long tail of one- and two-call NIDs, none of
which has been shown to gate anything.

### 6af. The scripts were readable all along — and they name the blocker

**The volume is not encrypted; only its index is.** 6ac spent a scan of every
shipped module hunting a ChaCha20 key and concluded the data layer was closed.
That conclusion was wrong, and the disproof took one command:

```
$ head -c 8 contents/D/4/PM1EF
ADCH015
```

`/scripts/gt7/main.adc` is plaintext on disk. `gt.idx` is encrypted because it
is the *index*; the files it points at are not. The README had recorded
"magic `ADCH015`" for this exact file back in section 4 — the information needed
to skip the whole key hunt was already in our own notes.

**The disassembler handles version 15.** GTAdhocToolchain advertises bytecode
versions 5-12 for its *compiler*, which is what made this look out of reach, but
`AdhocFile.ReadFromFile` handles 13+ including the version-15 scrambler — which
is keyed by the file's own MD5, so it is self-contained. Only the Disasm library
is needed; the compiler's parser submodule does not build on Windows (long
paths) and is irrelevant. `artifacts/gt7-investigation/adc-disasm/` holds the
output and the small driver that produced it: 33 scripts, 1.5 MB of readable
bytecode, including `gt7/main_loop.adi`, `gt7/Application.adi` and
`gt7/system/AppSystem.adi`.

**`0x801DA10E0` is named `finishProject`.** The runtime dispatch record that
6y could not identify carries an inline name 0x18 bytes after the handler
pointer:

```
0xE400D98480  0x0000000801DA10E0      <- handler
0xE400D98498  "finishProject"
```

The scripts use that vocabulary throughout — `startProject`, `standbyProject`,
`returnProject`, `getTopProject`, `clearProjectLevel` — so the table is
script-facing, and `finishProject` is reached from script as `context.finish()`.

**The whole blocked chain, in source.** `gt7/main_loop.adi`:

```
main_loop(update_context):
    id = SequenceController.requestSequence("System::AppSystem")
    SequenceController.waitLaunchSequence("System", id)
    context = update_context.getRenderContext(0)
    SequenceUtil.standbyProject(context, "residentmenu", "ResidentMenuProject")
    top_project_name, params = SequenceUtil.getTopProject()
    SequenceUtil.startProject(context, top_project_name, nil, nil, params)
    SequenceController.clearSequence("Main")
    SequenceController.clearSequence("System")
    SequenceUtil.clearProjectLevel()
    context.clearPage()
    manager.unloadProject(system_window)
    context.finish()                     <- the finishProject command
```

`context.finish()` is the **last** statement, and the second statement is a
wait. `waitLaunchSequence` is a cooperative spin:

```
seq = sequence_list["System"]
if id == nil: id = seq.req_id
while seq.run_id != id:
    YIELD
```

and `run_id` is assigned in exactly one place, `sequence_loop`:

```
seq.now_sequence_type = seq.next_sequence_type
seq.run_id = seq.req_id          <- this is what releases the wait
type.entry_func(seq, args)       <- AppSystem.mainEntry, which then blocks
                                    on monitor.wait() by design, forever
```

`sequence_loop` runs on its own Adhoc thread, created in `SequenceController`'s
`start()`:

```
if thread == nil:
    thread = Thread(sequence_loop, self)
    thread.priority = 50
    thread.start()
```

So the C++ evidence and the script agree and close the loop from 6y:
`main_loop` never gets past `waitLaunchSequence`, so it never calls
`context.finish()`, so `entry+0x94` is never set, so `+0x95` is never set, so
the state-3 pass always finds a pending entry, so `ctx+0x48` never reaches 5,
so `Event::set(ctx+0x28)` never fires, so main stays parked in
`GTFramework::Organizer::Impl::initialize()`.

Note the ordering: `run_id` is set *before* `entry_func` is called, so
`mainEntry` blocking on `monitor.wait()` is normal and is **not** the problem.
The problem is strictly that `sequence_loop` never runs for the System
sequence.

**The next question, precisely stated.** Does the System sequence's Adhoc
thread get created and scheduled? Guest thread names give no answer on their
own — there is no "sequence" thread, but there are 67 generic `SceLibc_Thr`
threads that could be hosting VM threads. Resolve that first: find what
Adhoc's `Thread.start()` lowers to natively, then check whether that path
succeeds for this sequence. That is an emulator-side question with a
definite answer, unlike everything the import census had left.

### 6ag. `finishProject` is not the only way `+0x94` gets set — 6y was wrong

**Correction to 6y.** 6y states "the only code that sets [`entry+0x94`] is
`0x801DA10E0`", i.e. `finishProject`, and that conclusion produced a
circularity: main blocks inside `mUpdateContextPS4::update` waiting for the
contexts to finish, while the only thing that finishes them is the last
statement of a script function. GT7 does not deadlock on hardware, so something
in that chain had to be wrong. It was the "only writer" claim, which came from a
byte-pattern scan — and the real writers are unreachable to that method.

Decompiling the job callback `0x80049ED80` shows the normal path:

```c
if (obj[0x95] == 0) {
    if (obj[0x94] == 0 && *(char *)(owner + 0x135) == 0) {
        for (i = obj->phase; i < 3; i++) {
            fn = *(code **)(i * 0x10 + 0x8054B03E0);   // member-pointer table
            if ((ulong)fn & 1) fn = virtual-lookup;    // Itanium member pointer
            (*fn)(obj);
            if (obj[0x94] != 0 || owner[0x135] != 0) break;
        }
        obj->phase = 0;
    }
    if (obj[0x94] != 0 && obj[0x95] == 0) { ...finalize; sets +0x95... }
}
```

A **phase function** is what normally sets `+0x94`. They are called through a
member-pointer table, so there is no `call rel32` to find and no direct write to
`+0x94` in any statically-reachable function body — which is exactly why both
earlier scans missed them. The table at `0x8054B03E0` holds three non-virtual
entries:

| phase | function | what it drains |
|---|---|---|
| 0 | `0x80049F200` | the queue at `entry+0x1B0`, count `entry+0x1B8` |
| 1 | `0x80049DD10` | the list at `owner+0x210`, count `owner+0x218` |
| 2 | `0x80049EE60` | not yet read |

**Measured live, with the boot stalled** (entry[0] = `0xE410160F10`,
owner = `0xE410220020`):

| field | value | meaning |
|---|---|---|
| `owner+0x135` | 0 | the gate is open, so the phase loop *does* run every pass |
| `entry+0x1B8` | 0 | phase 0 has nothing to drain |
| `owner+0x218` | 0 | phase 1 has nothing to drain |
| `entry+0x80` | 0 | phase index resets to 0 every pass, as the code says |
| `entry+0x94`, `+0x95` | 0, 0 | never finished |
| `0x806DA1358` | 0 | still null, as 6y recorded |

So the phases are running and finding **no work**. The render context is open,
idle, and correctly so: nothing has been queued into either list. This matches
the observation that opened the 2026-09-12 session — the context "is not starved
by our GPU or job path, it is open and doing nothing".

**The question this leaves is much sharper than 6y's.** It is no longer "what
issues `finishProject`" but: *what should enqueue work into `owner+0x210` or
`entry+0x1B0`, and why has nothing done so?* The `finishProject` path remains a
real second route to `+0x94`, and the script analysis in 6af still stands on its
own, but it is not the only route and should not be treated as the blocker
until the enqueue side is understood.

Note also that `0x806DA1358` is read by phase 1 *and* by `finishProject`. It is
null in both. It is not the phase-1 gate (phase 1 breaks on the empty list
first), but a singleton that two independent paths expect and neither finds is
worth identifying on its own.

**Method note.** Three separate scans for writers of `+0x94` and `+0x366`
produced misleading answers: 13 "writers" of `+0x366` and 56 "writers" of
`+0x94`, nearly all unrelated classes that happen to share the offset, exactly
as the trap list warns. Anchoring on the vtable failed too, because
`MENU::mRenderContextPS4`'s vtable lives outside the extracted text segment.
What finally worked was reading the *code that consumes the field* and then
measuring the live object. Prefer that order: decompile the consumer, then
measure, and only then go looking for writers.

### 6ah. The script VM is running, and 6af's conclusion was wrong

**Correction to 6af.** 6af ended by inferring that "main is parked because the
System sequence's Adhoc thread never runs". That was an inference, not a
measurement, and it is **wrong**.

Two independent measurements disprove it. First, the script's own symbols are
resident in the VM's heap area with the boot stalled:

```
"ResidentMenuProject"  -> 0xE400D977A4   (4 hits)
"System::AppSystem"    -> 0xE401059900
"main_loop"            -> 0xE401055D94   (4 hits)
```

(The raw `ADCH015` magic is absent because the file is parsed into VM
structures rather than kept as a blob — which is also why the earlier "no ADCH
in memory" scan was misread as "no script loaded".)

Second, and more conclusively, a boot traced with `SHARPEMU_LOG_IO=1` and
`SHARPEMU_LOG_AMPR=1` performs **13,555 file reads, every one completing**,
across 230 distinct files. Classifying each by its on-disk magic:

| magic | files | what it is |
|---|---|---|
| `ADCH` | **10** | compiled Adhoc scripts — `main.adc` **plus nine more** |
| `MPRJ` | **6** | **menu projects** |
| `4bpg` | 11 | packed assets |
| `TXS5` | 5 | textures |
| `SNDZ` | 5 | sound banks |
| `MDL5` | 3 | models |

The menu projects load. Nine scripts beyond `main.adc` load. The content
pipeline is healthy and the VM is executing — so the boot is far further along
than "the script never runs", and any future work should start from that.

**What this does to the chain.** `standbyProject` and `startProject` come
*after* `waitLaunchSequence` in `main_loop`, and menu projects are demonstrably
being loaded, which is evidence that the System-sequence wait was passed rather
than hung. Combined with 6ag — the phases run every pass and find both queues
empty — the live question is unchanged in shape but better founded:

> Something should enqueue render work into `owner+0x210` or `entry+0x1B0`
> once a project is started. Menu projects load, yet neither queue ever
> receives anything.

**Where main actually is.** The stalled main thread's guest backtrace is:

```
0x8010ED9E0 (entry) -> ... -> 0x800FDA480 -> ... -> 0x801EB6330
   -> 0x800F853A0  MENU::mUpdateContextPS4::update -> scePthreadCondWait
```

so main is inside the update whose completion depends on the contexts
finishing. Whether the Adhoc VM drives that path from this same thread is not
yet established, and it matters: if it does, the design requires a phase to set
`+0x94`, because `context.finish()` cannot run until update returns.

**Method note, again.** Two scans this session tried to find the producer by
displacement (`+0x210`/`+0x218`, `+0x1B0`/`+0x1B8`) and returned 218 and 251
candidate functions — useless, for the third time. Offsets that small are
shared by half the codebase. The measurements that actually moved this forward
were all either a decompile of a consumer or a live read of a known object.

### 6ai. The request path, decompiled — where to resume

Following 6ah's question ("what should enqueue render work") through the
*consumers*, as the method note demands. This is the state machine main is
waiting on, read out of `0x80049C650`:

```c
// state 1 -> 2
if (ctx[0x48] == 1) {
    ctx[0x48] = 2;
    *(uint16*)(ctx+0xA0) = 0;
    (**(code **)(*ctx + 0x2B8))(ctx);      // PRODUCER: virtual, fills the request vector
    *(uint8*)(ctx+0xA0) = 1;
    if (*(long*)(ctx+0x78) != 0) {          // <-- GUARD: update()'s argument
        count = (ctx[0x90] - ctx[0x88]) >> 3;   // vector end - begin, 8-byte items
        base  = count ? ctx[0x88] : 0;
        sub_80052FC10(tmp, ctx+0x78, count, base);
        sub_80049B4D0(tmp2, tmp);           // refcounted handoff
        *(uint8*)(ctx+0xA1) = 1;            // <-- "has work" flag
    }
    ctx[0x48] = 3;
}
// state 3 -> 4
if (ctx[0x48] == 3) {
    ctx[0x48] = 4;
    if (*(char*)(ctx+0xA1) == 1) {          // only when the guard above passed
        if ((int)ctx[0x6D8] < 1) { ...dispatch one job, go to the state-5 path... }
        else { ...walk the 6 entries: the loop that never terminates... }
    }
}
```

Two fields decide everything here, and **both are worth measuring first next
session** because neither has been read yet:

| field | meaning | why it matters |
|---|---|---|
| `ctx+0x78` | the argument `mUpdateContextPS4::update` copied in | if 0, no request vector is built at all |
| `ctx+0xA1` | "has work" flag | the entry walk only runs when this is 1 |
| `ctx+0x88`, `ctx+0x90` | request vector begin/end | their difference is the request count |

We know the entry walk *does* run (that is the observed non-terminating loop),
which implies `+0xA1 == 1` and therefore `ctx+0x78 != 0` — so a request vector
**was** built. Measure `(ctx+0x90 - ctx+0x88) >> 3` to get how many requests it
holds. If that count is 0, the producer at vtable `+0x2B8` produced nothing and
that virtual is the next target. If it is non-zero, the requests exist but never
reach the per-entry queues, and the handoff is the target.

`ctx` is stable across boots at `0xEC0309D000`, so these are direct reads:

```
memscan <guest-pid> dump EC0309D078 8     # ctx+0x78
memscan <guest-pid> dump EC0309D088 16    # ctx+0x88 / +0x90 (vector begin/end)
memscan <guest-pid> dump EC0309D0A0 8     # ctx+0xA0 / +0xA1
```

**The handoff chain, as far as it is read.** `sub_80049B4D0` is only a
refcounted pointer copy, but it first calls `sub_80049E350(g_manager, req)`
where `g_manager = *0x806D70178` — the same singleton the job callback and the
consumer both touch. That function:

- calls the request's virtual `+0x270`, and returns immediately if it is false
  (a readiness/submit predicate);
- range-checks a priority at `req+0x28` against `[-100, 100]`;
- assigns a sequence number from `g_manager+0x128` into `req+0x20` when that
  slot is still 0.

So `+0x270` is a gate that can silently drop a request. **That predicate is the
single most promising thing to probe**: if it returns false under SharpEmu, the
requests are built and then discarded, which matches every symptom — queues
empty, phases idle, contexts never finished, and no error anywhere.

**Still unread:** phase 2 (`0x80049EE60`) decompiles incompletely under
`-noanalysis` because of an unresolved switch, and the producer virtual
`+0x2B8` has not been resolved to an address. Resolve `+0x2B8` by reading the
entry's vtable pointer live and indexing it, rather than by scanning.

### 6aj. The three reads, executed — one request exists and is never consumed

6ai's reads, taken live with the boot stalled (`ctx = 0xEC0309D000`, stable
across boots):

| field | value | reading |
|---|---|---|
| `ctx+0x48` | 3 | state 3, the non-terminating entry walk — as expected |
| `ctx+0x78` | `0xE415639110` | **non-zero**, so `update()` was given an argument and the guard passed |
| `ctx+0x88` | `0xE4158D7F80` | request vector begin |
| `ctx+0x90` | `0xE4158D7F88` | request vector end |
| `ctx+0xA0` / `+0xA1` | 1 / 1 | "has work" is set |

`(0xE4158D7F88 - 0xE4158D7F80) >> 3` = **exactly one request**.

This settles 6ai's branch: the producer is **not** the problem. A request was
built, the guard passed, and the has-work flag is set. The request is then never
consumed — the per-entry queues stay empty (6ag) and the contexts never finish.

The single vector element reads as `0xEC0309D000`, which is `ctx` itself. That
is either a self-reference (the context enqueueing itself for processing, which
would be idiomatic) or a sign that the element is not a plain request pointer.
**It was not resolved, and the next session should not assume the former.**

**Virtuals resolved from the live vtable**, which is the reliable way to do it
(`[ctx] = 0x8054E6278`):

| slot | address | role in the consumer |
|---|---|---|
| `+0x2B8` | `0x800D71B00` | the producer that fills the request vector |
| `+0x270` | `0x801EBB380` | the predicate `sub_80049E350` checks before submitting |

`0x800D71B00` sits immediately below the `MENU::mRenderContextPS4` constructor
at `0x800D71D20`, which is a good sign the `+0x2B8` resolution is right.

**A caveat that must not be lost.** `0x801EBB380` decompiles as a table lookup
over `0x806042FC0` with a `param_3 < 4` bound and what look like formatting
helpers — it does **not** read like a boolean readiness gate. So either the
`+0x270` call in `sub_80049E350` is made on a *different* object than `ctx`
(most likely: its `param_2` is a pointer-to-pointer whose target was never
identified), or the vtable slot arithmetic differs for that call site. Resolve
what `sub_80049E350`'s `param_2` actually points to **before** trusting
`0x801EBB380` as the predicate. Do it by reading the live object, not by
scanning.

**State of the question.** "What should enqueue the work" is now answered in
part: the work *is* enqueued — one request, flag set, guard passed. The open
question has moved one step downstream and is narrower than it has ever been:

> One request sits in `ctx`'s vector with the has-work flag set, the phases run
> every pass, and both per-entry queues stay empty. What consumes that request,
> and why does nothing transfer it to `entry+0x1B0` / `owner+0x210`?

### 6ak. Proving the gate by intervention — and a correction to 6y's watch

Rather than infer further, this round *intervened*: `memscan` gained a `poke`
mode (`WriteProcessMemory` into the live guest) purely as a diagnostic, and the
suspected gate conditions were forced while the boot was stalled.

**Experiment 1 — force `entry[0]+0x95 = 1`** (make the walk find nothing
pending). Result: the log jumped from 8.76 MB to **13.7 MB — about 5 MB of
new execution** — and then the process died with a null dereference.

That is decisive. Main *does* wake the instant no entry qualifies, exactly as
the state machine predicts, and GT7 has a large amount of boot left that it has
never been allowed to run. The render-context completion is the sole gate.

**Experiment 2 — force `entry[0]+0x94 = 1`** (let the job take its own finalize
path instead of skipping the work). Result: `ctx+0x48` advanced **3 -> 4**, the
first state movement ever observed, with no crash. Driving `+0x94` continuously
for 20 s holds it at 1 but `+0x95` is never set and the state parks at 4, which
means the consumer is blocked *inside* the walk rather than cycling.

**Where experiment 1's crash actually is.** `0x801BD8391`, in
`sub_801BD7E00`, is not an independent bug — it is a retry loop:

```
801BD8365  call <import>            ; poll
801BD836C  jne  done                ; success leaves the loop
801BD8382  cmp  rax, 0x4C4B40       ; 5,000,000 us timeout
801BD838A  mov  rdi, [rbp-0x308]    ; NULL, because the completion was faked
801BD8391  mov  rax, [rdi]          ; <-- fault
801BD83A4  mov  edi, 0x3E80         ; 16 ms
801BD83A9  call <sleep>             ; and retry
```

GT7 polls something for five seconds at 16 ms intervals after the update
returns. The null is an artefact of lying to it about completion, not a defect
to chase.

**Correction to 6y's key evidence.** 6y's claim that `entry+0x94` has exactly
one writer rested on a hardware write watch that "recorded exactly one write in
a whole boot". That watch is **unsound as used**: the deref spec
`[0xEC0309D6A8]+0x94` is resolved **once, early**, and GT7 *rebuilds the entry
array afterwards*. Measured directly this session: the watch resolved entry[0]
to `0xE400C33CC0`, while the entry[0] the stalled boot actually uses is
`0xE410160F10`. The watch spent the entire boot monitoring a discarded object.

Any conclusion of the form "nothing ever writes this field", drawn from a
deref-spec watch, has to be re-established. Either watch the absolute address
after reading it live, or re-resolve the spec at use time.

**Research checked (2026-09-12).** shadPS4 runs the **PS4** build of GT7 and its
compatibility tracker reports "boots, then crashes at the first boot logo" on
Windows, macOS and Linux alike. No public emulator gets past this point in any
build of this title, so there is no working reference implementation to diff
against — the analysis has to stand on its own.

**Not the cause: `0x806DA1358`.** A write watch on that absolute address across
a full boot recorded **no writes at all**, and it is null at the stall. But the
~4,000 sites that read it all guard it with a null check (the decompiles of
`sub_80049CE80`, phase 1, and `finishProject` each do), so it is an optional
singleton rather than the missing piece. Recorded so the next session does not
re-suspect it.

**Net position.** The gate is proven and the machinery downstream of it works;
what is still missing is the legitimate signal. Forcing `+0x95` skips real work
and crashes later; forcing `+0x94` moves the machine one state and then blocks
inside the walk. The honest summary is that GT7 is waiting for a render context
to complete work that never gets queued (6ag/6aj), and neither forced flag is a
substitute for finding the producer-to-queue transfer.

### 6al. The render-work pipeline, mapped end to end — nothing is ever staged

Following the consumers (not offsets) gave the whole pipeline on the owner
object (`owner = entry[0]+0x88`, stable at `0xE410220020`):

| field | role |
|---|---|
| `owner+0x270` / `+0x278` | **staging** list head / count — where a producer posts |
| `owner+0x288` / `+0x290` | **destination** list head / count — what gets processed |
| `owner+0x210` / `+0x218` | phase 1's list head / count |
| `owner+0x248` | the lock guarding the splice |

`sub_80049C9F0(owner)` is the **transfer**: under the lock, if the staging count
is non-zero it adds it to the destination count, zeroes the staging count, and
splices the list. `sub_80049CB00(owner)` calls that transfer and then processes
the destination list, but only `if (count > 0)`.

**Measured live on a stalled boot, all three counts are zero:**

```
owner+0x278 (staging)     = 0      head 0xE41020FC20  (valid, empty)
owner+0x290 (destination) = 0      head 0xE41020B950  (valid, empty)
owner+0x218 (phase 1)     = 0      head 0xE410205D40  (valid, empty)
```

The lists are properly constructed and permanently empty. A write watch on the
**absolute** address of the staging count (`0xE410220298`) across a full boot
recorded **no writes at all**.

**The producer does not stage.** `ctx` vtable `+0x2B8` = `sub_800D71B00`,
decompiled in full (136 lines), only *initialises* the context: it picks an
entry from a 27-element table at `0x806DFE990` using a global index at
`0x806E06884`, then clears two `0x228`-byte regions at `param+0xC4` and
`param+0x3A4` and fills defaults. It contains no reference to `+0x270`,
`+0x278`, `+0x288`, `+0x290`, `+0x210` or `+0x218`.

So the pipeline is intact and idle at every stage, and the thing that should
post work into `owner+0x270` has still not been identified. That single
unidentified producer is the whole remaining question.

**Callers, for the next session** (a `call rel32` scan on a function address is
sound, unlike an offset scan):

| function | direct callers |
|---|---|
| `sub_80049C9F0` (splice) | `0x80049C590` (x2), `0x80049CB00`, `0x800E9D4D0` |
| `sub_80049CE80` (drain) | `0x80049C590` (x2), `0x80049D7C0` |

None of those is a producer — they are all on the consuming side. The stager is
reached another way, and the most likely remaining candidate is the script VM
issuing a render command, since GT7's menu is script-driven and the VM is
confirmed running (6ah).

### 6am. The menu lifecycle, from a public reference — and the strongest lever yet

**Research that paid off.** Nenkai's [OpenAdhoc](https://github.com/Nenkai/OpenAdhoc)
is an open-source re-implementation of Gran Turismo's scripts, and it documents
the render-context lifecycle that this investigation had been reconstructing
blind. From `src/projects/gt4/boot/boot.ad`:

```
function onLoad(context)
{
    context.createRenderContext(1);
    var render_context = context.getRenderContext(0);
    ...
    render_context.startPage(LoadRoot);      // <-- content
}
```

A render context is created, fetched, and then **given a page**. A context with
no page started has nothing to render.

GT7 does the same thing. Its `gt7/menu/system/projects.swift` (recovered from
the script disassembly, function at `script3` E88C) is:

```
updateContext.createRenderContext(1);
rc = updateContext.getRenderContext(0);
project = System::OptionalCast(manager.getProjectByName(projectName));
if (project == nil) {
    DebugTool::putHere("gt7/menu/system/projects.swift", 431,
                       "getProjectByName failed " + projectName + " not found");
    return [false, rc, nil, nil];              // <-- context created, no page
}
if (!project.defined(pageName)) {
    DebugTool::putHere("gt7/menu/system/projects.swift", 436,
                       projectName + "." + pageName + " not defined");
    return [false, rc, nil, nil];              // <-- context created, no page
}
page = System::OptionalCast(project[pageName]);
...
```

**Both failure paths create a render context and return without starting a
page**, which is precisely the observed state: a context that exists, is marked
active, and has empty queues at every stage.

Two caveats before this is treated as the answer. `DebugTool::putHere` output
does not reach SharpEmu's log at all, so which path fired (if either) cannot be
read directly — and a guest-memory search finds the string constant
`"getProjectByName failed"` exactly **once**, with no concatenated runtime
instance, which leans against that path having fired. More importantly, **the
splash art itself is a page**, so page-starting demonstrably works at least
once; the stall is a context waiting to finish so a transition can proceed,
not necessarily a page that was never started.

**The strongest lever found so far.** Poking `entry[0]+0x366 = 0` on the stalled
boot (marking the context not-active, rather than falsely complete):

- `ctx+0x48` went **3 -> 1**, meaning a *full* cycle completed (3 -> 5 -> 6 -> 0),
  main woke, and main issued its next `update()`;
- the state machine then kept **cycling** (1, 1, 1, 4, ...) instead of pinning at 3;
- the log grew from 8.2 MB to **32 MB**, roughly 550 KB every 3 s;
- **no crash**, unlike the `+0x95` intervention.

That *looked* like a running menu loop. **It is not — corrected by direct
observation.** A window capture taken 20 s after the poke shows the **identical
splash frame**, with the overlay reading `FPS 0.0`, `DRAWS 0/S`,
`TIME 00:00:00`. The state machine cycles and the log grows, but **no frames are
drawn and the game clock never starts**.

So the `+0x366` poke moves internal state without producing any visible
progress, and the "healthy cycling" reading above overstated it. It remains
useful only because it makes the next layer of behaviour observable; it is not
a route past the splash. `+0x366` is also set during construction (measured at 1
within one second of process start), i.e. it is the normal state for the active
context, so zeroing it was always a fake.

**Take the screenshot.** `DRAWS 0/S` is the single most informative number in
this whole investigation and it costs one window capture: the GPU is drawing
*nothing*, so the splash on screen is a stale presented frame, not a live
render. Any future claim that an intervention "got further" must be checked
against the overlay before it is believed.

**What the next layer looks like.** With the boot cycling, the log is dominated
by failing imports:

| NID | symbol | calls | result |
|---|---|---|---|
| `p+zLIOg27zU` | `sceFiberGetSelf` | ~104,000 | `0x80590005` |
| `304ooNZxWDY` / `9wO9XrMsNhc` | `sceNetRecvfrom` / `sceNetRecv` | ~19,000 each | `0x80410123` |
| `WFIiSfXGUq8` | `scePadOpenExt` | 414 | error |

`sceNetRecv*` returning `0x80410123` is `EWOULDBLOCK` — correct for a
non-blocking socket with no data, not a bug. **`sceFiberGetSelf` was checked and
ruled out**: a fiber trace shows the system working normally (135 `fiber.init`,
98,716 `fiber.transfer`, 12,763 `fiber.return`), so those failures are calls
from threads legitimately not running a fiber. `scePadOpenExt` is unexamined.

These failures are **pre-existing**, not caused by the poke — they appear in
stall logs from before any intervention.

### 6an. Reading GT7's own diagnostics out of guest memory

**A new capability worth keeping.** GT7's script layer reports problems through
`DebugTool::putHere(file, line, message)`, and none of that output reaches
SharpEmu's log. But the messages are **built by string concatenation at
runtime**, so any that fired still exist as VM string objects in the guest heap
and can be read with `memscan`. This makes the script layer observable without
any emulator change.

Searching a stalled boot's guest heap for the message tails of the two failure
paths in `gt7/menu/system/projects.swift` gives a precise answer:

| search | guest-heap hits | meaning |
|---|---|---|
| `" not found"` | **0** | the `getProjectByName failed ... not found` path **never fires** — projects resolve |
| `" not defined"` | 3 | the page/member path fires, twice with content |

The two constructed messages, reconstructed byte-exactly:

```
ProjectBase root.BuddyRootEvent not defined
ProjectBase buddyEvent.StartMsgSequence not defined
```

Both name **buddy/PSN event handlers**, which a console with no PSN session may
legitimately lack, so these are probably benign rather than the blocker. A
sweep for `" failed"`, `"cannot "`, `" error"` and `"invalid "` in the guest
heap returned **nothing**. A later sweep with a higher hit cap found one more
that the first pass had missed — **`[bind] SelectSet: not found`**, a single UI
binding failure — so the true total is **three** messages for an entire boot,
all of them incidental. Treat guest-heap string counts as a floor, not a total,
and re-run them with a high cap before concluding a message is absent.

That is itself the important result: **GT7 is not reporting an error. It is
simply waiting.**

**Two more things ruled out this round.**

*The job does run.* An execute watch on the job callback `0x80049ED80` fired
**476 times** in one boot (job threads are registered, so the watch works there).
Dispatch and scheduling are not the problem — the job runs, finds nothing, and
returns.

*Phase 2 drains a third queue.* `sub_80049F530`, phase 2's first call, pops a
lock-free stack at **`entry+0x990`** until empty:

```c
while ((item = pop(entry + 0x990)) != 0) { append to list; }
```

Measured live, `entry+0x990` is a tagged pointer (`ptr << 16 | tag`) whose head
and tail both point at the same sentinel — **empty**. With this, *all four*
queues in the pipeline are confirmed permanently empty:

| queue | drained by | state |
|---|---|---|
| `entry+0x1B0` / `+0x1B8` | phase 0 | empty |
| `owner+0x210` / `+0x218` | phase 1 | empty |
| `entry+0x990` | phase 2 | empty |
| `owner+0x270` -> `+0x288` | the splice | empty, never staged |

**Next-layer imports, all checked and all correct** (so they are not worth
re-suspecting): `sceNetRecv`/`sceNetRecvfrom` return `EWOULDBLOCK`, which is
right for a non-blocking socket with no data; `sceFiberGetSelf`'s ~104k failures
are calls from threads legitimately not in a fiber, and a fiber trace shows the
system healthy (135 `fiber.init`, 98,716 `fiber.transfer`, 12,763
`fiber.return`); `scePadOpenExt`'s 414 failures are GT7 probing for a racing
wheel with `type=2`, which SharpEmu deliberately rejects.

### 6ao. `main_loop` recovered byte-exact — the blocking statement is line 58

**A file that was never disassembled.** `main.adc` (`contents/D/4/PM1EF`) had
not been run through `adcdump` in this workstream — the earlier bulk pass
covered the *other* nine scripts. Disassembling it yields 33 scripts including
**`gt7/main_loop.adi`** and `gt7/Application.adi`. That is why a search for
`main_loop` across 1,899 disassembled files returned nothing and briefly made
6af look unsupported.

`gt7/main_loop.adi`, recovered exactly:

```
function main_loop(update_context)
{
    id = SequenceController::requestSequence("System::AppSystem");        // line 57
    SequenceController::waitLaunchSequence("System", id);                 // line 58
    context = update_context.getRenderContext(0);                         // line 60
    SequenceUtil::standbyProject(context, "residentmenu", "ResidentMenuProject");
    [top_project_name, params] = SequenceUtil::getTopProject();
    SequenceUtil::startProject(context, top_project_name, nil, nil, params);
    SequenceController::clearSequence("Main");
    SequenceController::clearSequence("System");
    SequenceUtil::clearProjectLevel();
    context.clearPage();
    manager.unloadProject(system_window);
    context.finish();                                                     // line 83
}
```

**Line 58 is the blocker.** Nothing below it runs until the `System::AppSystem`
sequence launches — so no `startProject`, no page on the render context, no
render work, and the context can never complete. Every measurement in
6ag-6an follows from this one fact, including the four permanently empty queues
and the context that is active-but-idle.

**`waitLaunchSequence` waits on `run_id`:**

```
seq = sequence_list[target];                 // target = "System"
if (seq != nil) {
    if (id == nil) { id = seq.req_id; return; }
    if (seq.run_id != id) { ...yield and retry... }
}
```

and `run_id` is assigned in **exactly one place** in the whole of `main.adc`:
the function **`sequence_loop(seq)`**. Which is started as an Adhoc thread:

```
if (thread.is_alive == false) { terminate(); return; }
if (thread == nil) {
    thread = Thread(sequence_loop, self);
    thread.priority = 50;
    thread.start();
}
```

So the causal chain, end to end, is:

> `Thread(sequence_loop).start()` -> `sequence_loop` sets `seq.run_id` ->
> `waitLaunchSequence` returns -> `startProject` -> a page on the render context
> -> render work queued -> context completes -> main wakes.

**This vindicates 6af and retracts part of 6ah.** 6af said "the System
sequence's Adhoc thread never runs"; 6ah downgraded that on the grounds that the
VM is demonstrably running. Both facts are true and they are not in conflict:
the VM runs, loads ten scripts and six menu projects, *and* the
`System::AppSystem` sequence still never launches. 6ah's correction over-reached.

**Adhoc threads do exist natively.** Guest thread names include **`ADTsk`**
(Adhoc Task) — five of them. So `Thread.start()` is not a no-op. With the boot
stalled, all five are `state=Blocked` on `pthread_cond_wait` at the same site
(`ret=0x80077C823`, function `sub_80077C7E0`) with frozen import counts
(593, 365, 2138, 1096, 2083). `sub_80077C7E0` is a small event primitive
(`+0x18` waiting flag, `+0x19` signalled flag, `+0x08` condvar), i.e. a worker
pool parked waiting for work.

**The condvar layer is healthy — checked, not assumed.** A boot traced with
`SHARPEMU_LOG_PTHREAD_CONDS=1` does 333,071 `cond_wait`, 58,295 `cond_broadcast`
and 26,930 `cond_signal`. Exactly **one** condvar in the entire boot is waited on
and never signalled (`0xE410484798`, 1,455 waits from `ret=0x808179832` inside a
loaded PRX) — and all 1,455 are `timed=True`, i.e. a polling loop that times out
by design, not a lost wakeup. **There is no missed-signal bug.**

**The next question, precisely.** Five `ADTsk` workers are parked with no work;
`sequence_loop` should be queued onto one of them. Determine whether the Adhoc
`Thread` object for `sequence_loop` is created and enqueued at all — find the
Thread object in guest memory, or identify what `Thread.start()` lowers to
(a native dispatch-record command, resolvable by name the way `finishProject`
was in 6ak). That is the last link that has not been measured.

### 6ap. The Adhoc task pool measured — and the one discriminator left

Following 6ao's chain down to the native layer.

**The Adhoc thread pool is real and idle.** All five `ADTsk` threads share entry
`0x8008F3D80`, a thin trampoline that calls one function per slot:

```c
(**(code **)(slot + 0x20))(*(void **)(slot + 0x28));   // the thread body
```

Every slot holds the *same* body, `sub_8035F8D20`, with static descriptors at
`0x806D70180` + 0x10 stride — so it is a worker pool. That worker loop is:

```c
while (desc[8] == 0) {
    sub_80077C7E0(0x806D702A0, 0);      // wait on the shared event
    ...pop work from the lock-free stack at 0x806D70208...
}
```

**Measured live on a stalled boot** (both are static addresses, so no staleness):

| address | value | meaning |
|---|---|---|
| `0x806D70208` (task queue head) | `0x000806D70218002B` | tagged pointer -> `0x806D70218`, the sentinel: **queue empty**; tag `0x2B` = **43 prior operations**, so the queue has been used |
| `0x806D702A0+0x18` (waiting) | 1 | workers are parked |
| `0x806D702A0+0x19` (signalled) | 0 | nothing pending |

So the pool is correctly idle with nothing queued, and it *has* served 43 tasks
earlier in the boot. No Adhoc task exists for `sequence_loop`.

**`sequence_loop` cannot have run.** Its first five statements are:

```
type = seq.next_sequence_type;   // 118
args = seq.next_argument;        // 119
seq.clear();                     // 120
seq.now_sequence_type = type;    // 121
seq.run_id = seq.req_id;         // 122   <-- nothing blocks before this
```

`run_id` is assigned immediately, with no wait ahead of it. If the function had
executed at all, `waitLaunchSequence` would have returned. It has not.

**Where the account is still incomplete — state it, do not paper over it.**
`requestSequence` has two exits:

```
if (seq.now_sequence_type != nil && seq.now_sequence_type.l_req_func != nil) {
    seq.now_sequence_type.l_req_func();
    return;                      // no start(), no req_id bump -> returns nil
}
seq.start();                     // 209
return ++seq.req_id;             // 211
```

and `waitLaunchSequence` **returns immediately when `id` is nil**. So the two
observations — "`main_loop` is parked at line 58" and "no task was ever queued"
— can only both hold if `seq.start()` ran and its Adhoc thread never reached the
pool. If instead `requestSequence` took the `l_req_func` exit, `main_loop` would
*not* be blocked at line 58 and the parked thread is something else.

**The one discriminator, and it is a single measurement:** read `req_id` and
`run_id` on the `"System"` entry of `sequence_list`.

- `req_id > run_id` -> `seq.start()` ran and the Adhoc thread never reached the
  worker pool. That is an emulator-level defect in whatever `Thread.start()`
  lowers to, and the pool measurements above localise it precisely.
- `req_id == run_id` (or both unset) -> `requestSequence` took the `l_req_func`
  exit, `main_loop` is *not* blocked at line 58, and 6ao's chain needs revisiting
  from the top.

Locating that object is the outstanding work: `sequence_list` is an Adhoc map in
the VM heap, and no reliable way to walk VM objects from outside has been built
yet. A VM-object walker for `memscan` is probably the highest-value tool left to
write, since it would also answer the page/project questions in 6am directly.

### 6aq. `MGOM.start` is the native bridge — the first blocker is *upstream* of `main_loop`

The module top-level of `gt7/main_loop.ad` does not call `main_loop` directly:

```
while (true) {
    debugReload();
    MGOM.start(main_loop, manager.getUpdateContext());   // line 99
    debugUnload();
}
```

and `MGOM` is assigned in `script25` as
**`main::menu::MMenuGameObjectManager()`** — a native C++ class, the same family
as `MENU::mRenderContextPS4`. So `MGOM.start(fn, ctx)` is the **native bridge**
that owns the update context, drives `mUpdateContextPS4::update`, and invokes
the script `main_loop` as a callback.

**This reorders the whole account.** Main is blocked inside `update()`, which
`MGOM.start` calls *before* `main_loop` is ever entered. Everything in 6ao about
`main_loop` line 58 is therefore **downstream of the first blocker**, not the
first blocker itself. The ordering is:

1. `MGOM.start` -> `update()` -> waits for render contexts -> **blocked here**,
   before `main_loop` runs at all;
2. only past that does `main_loop` run and reach `waitLaunchSequence`.

**Demonstrated by intervention.** Poking `entry[0]+0x366 = 0` (releasing the
`update()` wait) makes the Adhoc task queue advance: tag
`0x806D70208` goes **`...002B` -> `...002D`**, i.e. **two tasks pushed and
consumed** that never ran otherwise. So releasing stage 1 genuinely lets the
script machinery proceed into sequence work — it is not merely cosmetic state
churn.

It still produces **no frames**: `DRAWS 0/S`, `FPS 0.0`, `TIME 00:00:00`, and the
consumer state parks at 4. So stage 2 remains blocked behind it.

**What the first blocker actually is, stated precisely:** a render context
(`entry[0]`) exists and is marked active (`+0x366 = 1`, set at construction
within one second of process start) with **no work in any of its four queues**,
at a point where `MGOM.start` expects `update()` to complete so it can invoke
`main_loop`. On hardware that first update must return — either the context is
not created/active yet at that moment, or it completes trivially. Determining
which is the next question, and it is a *C++/emulator* question about render
context construction during `Organizer::initialize`, **not** a script question.

That is a better-posed target than anything in 6ao-6ap, because it sits entirely
in native code that SharpEmu controls, and because releasing it is already known
to move the title forward.

### 6ar. Two different releases of stage 1 give the identical result

The consumer has a fast path that was never tested:

```c
if ((int)ctx[0x6D8] < 1) { ...dispatch one job, go to state 5... }   // completes
else                     { ...walk the entries... }                  // the stuck loop
```

`createRenderContext(1)` is called by the *script*, after `main_loop` starts, so
at `MGOM.start` time there arguably should be **zero** render contexts. There are
**six** (`ctx+0x6D8 = 6`). Poking that count to 0 takes the fast path.

**Both releases behave identically:**

| intervention | state | Adhoc task queue tag | frames |
|---|---|---|---|
| `entry[0]+0x366 = 0` | 4 -> 1 (full cycle) | `002B` -> `002D` (2 tasks) | `DRAWS 0/S` |
| `ctx+0x6D8 = 0` | 4 -> 1 (full cycle) | `002B` -> `002D` (2 tasks) | `DRAWS 0/S` |

Two structurally different ways of releasing the `update()` wait produce exactly
the same downstream behaviour: main wakes, issues its next update, **two** Adhoc
tasks run, and then everything settles again with no rendering. That
reproducibility is worth more than either result alone — it says stage 1 is a
genuine gate and that whatever blocks stage 2 is deterministic and independent of
how stage 1 was released.

**The two-stage picture, confirmed:**

1. **Stage 1** — `MGOM.start` -> `update()` waits on a render context that is
   active with empty queues. Releasable two ways; releasing it lets the script
   machinery run (task queue advances).
2. **Stage 2** — with stage 1 released, exactly two Adhoc tasks run and then the
   sequence still never launches, so no page is started and nothing draws.

**`DRAWS 0/S` at the splash is itself a finding.** The GPU is drawing *nothing*
while the title art is on screen, so that art is a stale presented frame from
earlier in the boot, not a live render. Any future work should treat "does
`DRAWS` become non-zero" as the success signal, not log volume, not state
transitions, and not Adhoc task counts — all three of those move without a
single frame being produced.

### 6as. Ruling out my own changes as the cause of `DRAWS 0/S`

The docs record ~48 fps and 16,872 draws per boot from *before* the 2026-09-12
export work, and the overlay now reads `DRAWS 0/S`. That is a regression shape,
and the obvious suspect was **my own `sceAgcAcbRewind`**: it writes an
`IT_REWIND` gate with the valid bit clear, and the submit parser *suspends* on
that packet until `sceAgcRewindPatchSetRewindState` opens it. A gate that is
never patched would stall the queue permanently.

Tested rather than argued: the two ACB rewind exports were compiled out and the
CLI rebuilt to a separate directory.

**Result: `DRAWS 0/S`, `FPS 0.0`, `TIME 00:00:00` — unchanged.** The no-draw
state is not caused by the rewind exports, and by extension not by this
session's Agc work. Exports restored; full suite green at 1,084.

This is worth recording for two reasons. First, it removes a plausible
self-inflicted explanation that would otherwise have to be re-suspected every
time someone looks at the render path. Second, it means the `DRAWS 0/S`
condition predates this session's changes, so the "16,872 draws" figure in the
older docs describes a *different* run state (the boot's early rendering phase)
rather than the steady state at the stall — the GPU goes idle before the stall
settles, and that has probably always been true.

### 6at. Stage 1 release is necessary but not sufficient — and not repeatable

If the menu loop needed *every* `update()` to complete, holding the gate open
would let it run continuously. Tested: `ctx+0x6D8` was poked to 0 every 200 ms
for **60 seconds**.

**Result: the Adhoc task queue tag moved `...002B` -> `...002D` and stopped** —
exactly the two tasks a *single* release produces. Sixty seconds of sustained
releasing adds nothing.

So the update loop does not cycle continuously once released. GT7 runs one pass,
executes two Adhoc tasks, and settles into stage 2, where no further releasing
helps. Combined with 6ar (two independent releases give identical results), the
conclusion is firm:

> Releasing stage 1 is **necessary but not sufficient**, its effect is **not
> cumulative**, and stage 2 blocks deterministically regardless of how or how
> often stage 1 is released.

This closes the intervention line. Poking render-context state cannot get past
the splash, and further variations on it are not worth trying — the three
distinct pokes attempted (`+0x95`, `+0x94`, `+0x366`, plus `ctx+0x6D8`) either
crash, move internal state only, or produce these same two tasks. **Any future
effort belongs on stage 2** (the `System::AppSystem` sequence never launching,
6ao-6ap), or on the native question of why a render context is active with empty
queues at `MGOM.start` time (6aq).

### 6au. RETRACTED — GT7 does render and flip; I grepped for the wrong string

**This section originally claimed GT7 never submits a frame. That was wrong.**
It is retained, corrected, because the mistake is instructive.

The claim rested on `grep -c SubmitFlip` over a boot log returning **0**. The
video-out trace does not use that spelling: it writes **`videoout.submit_flip`**,
lower-case and underscored. Re-run with `SHARPEMU_LOG_VIDEOOUT=1`:

```
videoout.submit_flip      4,917
  of which submitted=True 4,915
videoout.register_buffers     2   handle=1 count=3 3840x2160 pitch=3840
videoout.add_vblank_event     1
videoout.add_flip_event       1
```

Flip indices cycle evenly across the three buffers (1639 / 1638 / 1638), i.e. a
**continuously presenting triple-buffered 4K swapchain**. The README's "4,559
flips in 95 s" figure is corroborated, not contradicted.

So the earlier status was right: GT7 **does** reach and present its title screen,
and the artwork on screen is GT7's own output, not the `pic0.png` splash. (The
splash loader is real — `VulkanVideoPresenter` does call
`PngSplashLoader.TryLoad("pic0.png")` — but it is superseded once the guest
presents.)

**Why the overlay looked like proof.** `DRAWS 0/S` is draw calls *per second*,
and it is genuinely 0: GT7 re-presents an already-rendered static title frame
without issuing new draws. That is consistent with flipping at ~55/s. The
`0.0 MS` and `TIME 00:00:00` readings are overlay-counter quirks on this path,
not evidence about the guest — they were read as corroboration when they were
nothing of the sort.

**The lesson, which cost a full round.** Three "independent" measurements were
cited for the retracted claim, but they were not independent: all three came from
the same instrument (the perf overlay and a log grep) and none was cross-checked
against the subsystem's own trace. A single `SHARPEMU_LOG_VIDEOOUT=1` run would
have refuted it immediately. **Before concluding a subsystem never runs, enable
that subsystem's own trace and check the spelling of what it emits.**

The render-context analysis in 6ag-6at stands unchanged, and its framing — GT7
presents a title screen and then stalls waiting on a render context — is the
correct one.

### 6av. The execute watch is provably blind to main — which invalidates a 6y pillar

**Naming a script-visible native, reusably.** The dispatch-record layout found
for `finishProject` in 6ak generalises: the record holds the handler pointer and
the command name **0x18 bytes later**. Scanning a live guest for the name and
reading back `-0x18` gives the handler. Confirmed for a second command:

```
0xE400CF11E0   0x0000000801DB9BC0     <- handler
0xE400CF11F8   "startPage"            <- name, +0x18
```

So **`startPage` = `sub_801DB9BC0`**. `startProject` and `standbyProject` records
exist in the same region and can be resolved the same way.

**The test, and the control that saved it.** An execute watch on `0x801DB9BC0`
over a full boot fired **0 times** — apparently "no page is ever started". Before
believing that, a control: an execute watch on `mUpdateContextPS4::update`
(`0x800F853A0`), the function main is *demonstrably parked inside*.

**That also fired 0 times.** The watch cannot see the title's main thread, so a
zero count proves nothing about anything main executes. The `startPage` result is
**inconclusive**, not negative.

**This invalidates a load-bearing claim in 6y.** 6y states of `0x801DA10E0`
(`finishProject`): *"An execute breakpoint on it over a full boot never fired, so
GT7 never issues that command."* That inference uses exactly the instrument just
shown to be blind to main. **It does not support its conclusion.** Whether
`finishProject` is ever issued is once again unknown, and the same caveat applies
to every "never executes" claim in this log that rests on
`SHARPEMU_WATCH_GUEST_EXEC`.

The handbook already warned that the title's main thread is not a registered
guest thread and so is never armed. What is new here is the **demonstration**:
watching a function main is provably sitting inside yields zero hits. Run that
control before trusting any negative from this instrument — it costs one boot.

### 6aw. Instruments that can and cannot see the title's main thread

Consolidated, because three separate conclusions in this log were drawn from
instruments blind to the thread they were asked about.

| instrument | sees main? | evidence |
|---|---|---|
| `SHARPEMU_WATCH_GUEST_EXEC` | **No** | watching `mUpdateContextPS4::update`, which main is parked inside, gives 0 hits (6av) |
| `SHARPEMU_WATCH_GUEST_WRITE` | **No** | same arming path — only registered guest threads get debug registers |
| `SHARPEMU_WATCH_GUEST_OBJECT` | **Yes** | runs in the import dispatcher; 22 hits captured for `entry[0]` |
| `SHARPEMU_PROBE_IMPORT_RET_ADDRESS` | **Yes** | same dispatcher path |
| guest-memory read (`memscan`) | **n/a** | thread-independent; reads state, not execution |

So any question of the form "does main ever execute X" must be answered with an
**import-dispatcher** instrument or with state inspection, never with the
exec/write watches.

**Applied to the open question, and it failed.** To test whether `startPage`
(`sub_801DB9BC0`, named via 6av's dispatch-record trick) ever runs, it needs an
import called early in its body to probe. It has none — the first calls are
internal (`sub_8000AB980`) or virtual. `finishProject` is no better: its only
import thunks are `__stack_chk_fail`-style error paths that do not run on the
normal path.

`SHARPEMU_WATCH_GUEST_OBJECT` on `entry[0]` returned 22 hits, but all are
`scePthreadMutexLock` with the pointer merely left over in `rsi` — the render
context is never passed as a real argument to any import, which is consistent
with it being idle but adds nothing.

**So the question "is a page ever started" is currently unanswerable with the
instruments in the tree.** Closing that gap is a tooling job, and the options are
concrete:

1. Add a guest-execution probe that works on unregistered threads (the import
   dispatcher already sees every thread; a "break when guest RIP enters range X"
   hook belongs alongside `SHARPEMU_PROBE_IMPORT_RET_ADDRESS`).
2. Or build the VM-object walker of 6ap and read `sequence_list["System"]`'s
   `req_id`/`run_id` directly, which answers the same question from the state
   side.

Either is a better investment than more inference over the existing evidence.

### 6ax. A guest execution probe that sees every thread — and the answer it gave

6aw ended by saying the open question needed an instrument that does not exist.
It exists now.

**`SHARPEMU_BREAK_GUEST_EXEC=<hex>[,<hex>...]`** (new, in
`DirectExecutionBackend.Exceptions.cs`). The debug-register watches are armed
per thread and only on *registered* guest threads, which is why they are blind to
the title's main thread. The **vectored exception handler is process-wide**, so
an `int3` planted in guest code reports from any thread at all. Each address is
patched once with `0xCC`; on the first trap the original byte is restored and RIP
is rewound, so the site fires exactly once and the guest resumes unmodified —
enough to answer "does this ever run".

**It was validated before being believed**, which matters because 6av showed what
an unvalidated zero is worth. A positive control on `mUpdateContextPS4::update`
(`0x800F853A0`) — the function main is parked inside — reports:

```
guest-exec-break armed at 0x0000000800F853A0 (original byte 0x55)
guest-exec-break HIT  0x0000000800F853A0 tid=84568 managed=2 ...
```

and the boot then continues normally (13 MB of log). The `0x55` is `push rbp`,
confirming the patch landed on a real function entry.

**One bug found in the probe itself by that control.** The first version matched
`rip - 1`, on the usual assumption that `int3` reports RIP *after* the trapping
byte. On this handler the context reports RIP **at** the byte
(`Exception Address: 0x800F853A0`, `RIP: 0x800F853A0`), so nothing matched and
the trap fell through to the generic "Unexpected breakpoint in direct-bridge
mode" reporter. The probe now accepts both forms.

**The answer.** With the validated instrument, over a 110-second boot:

| probe | address | result |
|---|---|---|
| `startPage` | `0x801DB9BC0` | **never executes** |
| `finishProject` | `0x801DA10E0` | **never executes** |

Both armed successfully (original byte `0x55` in each case) and neither fired, on
**any** thread. So:

> **No page is ever started on the render context, and `finishProject` is never
> issued.**

This settles two things that had been open. It confirms the render context is
idle because nothing ever gave it content — the conclusion 6al/6aj reached from
the queue side, now established from the execution side. And it **restores 6y's
claim** that `finishProject` never runs, which 6av had correctly invalidated on
instrument grounds; the claim was right, the evidence for it was not, and it now
has valid evidence.

**Still open:** whether `main_loop` is blocked at line 58 or never entered. The
discriminator is `getRenderContext` (line 60): probe it and the question is
answered. Its dispatch record was not located this session — the region found by
searching for the name is the VM's interned **string table** (names packed
back-to-back with `pd`-tagged headers), not the record array that held
`finishProject` at `0xE400D98480` and `startPage` at `0xE400CF11E0`. Search the
record region for the name instead of the string table.

### 6ay. Resolving a native method by its registration site, and a probe limitation

**How to find a script-visible native that has no dispatch record.**
`getRenderContext` is not in the command-record array (searching the name lands
in the VM's interned string table). It is registered from native code instead,
and the registration site is findable:

1. Find the name string **inside the module image** (not the heap):
   `getRenderContext` -> `0x804CBFAD3`, `getRenderContextCount` -> `0x804B32371`.
2. Xref the text for rip-relative references to those addresses.
3. Both land in `sub_800F231A0`, which registers them in a uniform pattern:

```
lea rsi, [name]        ; 0x800F23455 -> "getRenderContextCount"
lea rdx, [handler]     ; 0x800F2345C -> 0x800EE06B0
mov rdi, rbx
call <register>
lea rsi, [name]        ; 0x800F2346B -> "getRenderContext"
lea rdx, [handler]     ; 0x800F23472 -> 0x801DFA610
```

giving **`getRenderContext` = `sub_801DFA610`** and
**`getRenderContextCount` = `sub_800EE06B0`**. Both are genuine code
(`55 48 89 E5` / a two-instruction accessor).

**A confirmation worth keeping.** `getRenderContextCount` disassembles to exactly:

```
mov eax, dword ptr [rdi + 0x6d8]
ret
```

So `ctx+0x6D8` *is* the render-context count as the script sees it — independent
confirmation of the field identified in 6ar, from the title's own accessor.

**The probe cannot be used on this address.** Arming
`SHARPEMU_BREAK_GUEST_EXEC=0x801DFA610` crashes the boot reproducibly (three
runs) with an access violation in a PRX at `0x8081847E4` targeting
`0xF406E00000`, **without the probe ever reporting a hit**. Arming the control
alone, or `startPage` + `finishProject`, is stable — so the fault is specific to
patching this function.

The likely mechanism is the nested-exception guard: `VectoredHandler` returns 0
immediately when `_vectoredHandlerDepth > 0`. `getRenderContext` is called far
more often and earlier than the other probed functions, so its `int3` can land
while another exception (a lazy-commit fault, say) is already being serviced, and
the breakpoint then goes unhandled. That is a real limitation of the one-shot
design, not evidence about the guest.

**If this probe is developed further**, handle the breakpoint *before* the depth
guard, or arm the patch only after a delay so early/hot call sites are missed
rather than trapped. Until then the question "is `main_loop` blocked at line 58
or never entered" remains open, but the two answers it depends on
(`startPage` and `finishProject` never run, 6ax) are now established.

### 6az. The whole graphics pipeline was lost to one PM4 header decode

Everything above this section reasoned about *why GT7 would not render*, with
`DRAWS 0/S` as the symptom and a stalled script or render context as the
suspect. The actual defect was in the emulator's PM4 parser, and it discarded
every graphics submission the title ever made.

**Symptom.** `SHARPEMU_TRACE_FRAME_PACKETS=1` on a boot produced nothing but
this, once per graphics submission:

```text
[FRAMEPKT] parse-failure queue=dcb.graphics submission=1006 offset=15
  address=0x000000100020013C header=0xFFFF1000 reason=length-16385-remaining-7
```

2,128 submissions of 22 dwords each, all failing at the same dword, and
`draws=0` in every `[LOADER][PERF] videoout` line after the host splash stopped
drawing. The `draws=108` seen in the first two intervals is the emulator's own
splash, not guest work.

**Evidence.** Dumping the failing buffer out of the live process decodes the
whole submission, and the seven dwords the parser was abandoning are the
important ones:

```text
dw0  C0051018   NOP/reg 0x06, 7 dwords
dw7  C0021048   NOP/reg 0x12 -> indirect cx registers
dw11 C002104C   NOP/reg 0x13 -> indirect uc registers
dw15 FFFF1000   NOP, COUNT 0x3FFF          <- parser stopped here
dw16 C0005900   IT_REWIND (0x59)
dw18 C0023F00   IT_INDIRECT_BUFFER (0x3F) -> 0x1000262300
```

The submission is a *preamble*. The frame — every draw, every render target,
the whole command stream — lives behind that `INDIRECT_BUFFER` chain, and the
parser never reached it.

**Cause.** A type-3 header's COUNT field is body-dwords-minus-one in 14 bits, so
the length the CP derives from it wraps: COUNT `0x3FFF` means a *zero*-dword
body, i.e. a one-dword packet. AGC uses that encoding as a no-payload padding
marker. `Pm4Length` read it literally as `0x3FFF + 2 = 16385` dwords, which
overruns any real submission, so `ParseSubmittedDcbCore` returned false and the
rest of the buffer was dropped.

Two independent sources agree on the wrap, neither of them a guess about
hardware:

- shadPS4 decodes the same field as `(count + 1) & 0x3fff` and advances
  `NumWords() + 1` dwords, giving exactly one dword at COUNT `0x3FFF`.
- This tree's own `sceAgcGetDataPacketPayloadAddress` already reports *no
  payload* for a header matching `(header & 0x3FFF_0000) == 0x3FFF_0000`. The
  parser and that export disagreed with each other.

**Fix.** `Pm4Length` now wraps the same way the CP does:

```csharp
(((((header >> 16) & 0x3FFFu) + 1u) & 0x3FFFu) + 1u)
```

Ordinary packets are unchanged (`count + 2`); COUNT `0x3FFF` becomes 1.
`sceAgcCbNop`'s upper bound moved from `0x4001` to `0x4000` dwords for the same
reason: one more would encode COUNT `0x3FFF` and our own encoder would emit a
packet our decoder must read as empty.

**Result, measured on the same title and the same run shape:**

| | before | after |
|---|---|---|
| `dcb.graphics` parse failures | every submission | **0** |
| `draws` per second | 0 | **~700-780** |
| `presented_fps` | 0.0 | **~27-29** |
| `vk.present_dropped` | 3,943, continuous | 755, **early boot only** |
| window contents | host splash art | **a guest-rendered dialog panel and button** |

`artifacts/gt7-opus-20260913/gt7-window.png` is the capture: the
sign-in-required `ConfirmDialog` that section 6ay's VM stack had already
identified, now actually drawn. GT7 presents guest frames.

**What this does not fix.** The dialog renders its panel and button but no
text, and a `{ENTER}` sent with `SendKeys` did not dismiss it — neither is
diagnosed yet, and the SendKeys result does not prove the pad path is broken,
only that this injection method did not reach SDL. Boot is still parked in
`EventLoop.enter` waiting for that dialog.

**Why the earlier rounds missed it.** §6as ruled out the ACB rewind exports as
the cause of `DRAWS 0/S` and concluded the condition predated that session —
correct, and it stopped there. `draws=0` was read as "the title is not
rendering" for several sessions, when the title was rendering all along and the
emulator was throwing the commands away 15 dwords in. The cheap check that
would have found it at any point is `SHARPEMU_TRACE_FRAME_PACKETS=1`, which
names the packet and the reason in one line.

### 6ba. Two thread-lifetime leaks, and why the §6az render could not be repeated

The §6az run presented frames for five minutes. **No run since has reproduced
it, including a rebuild of that exact source state.** What follows is what the
failed reproductions turned up — two real leaks, both fixed — and an honest
statement of what is still unexplained.

**The reproduction attempts all end the same way.** `submitted_fps` ~0.7,
`draws` 2-3 (the host splash), `presented_fps` 0.0, no `[FRAMEPKT]` lines at all
— the title never reaches graphics submission. The periodic stall report puts
the title's main thread in `scePthreadCondWait` with frames
`0x800F853FE -> 0x800F8533A -> 0x801EB6AB4 -> 0x8000B58EE`, i.e. the
update-context wait of §6aq: `MGOM.start` -> `update()` parked on a render
context. The condvar itself is healthy — `pthread_cond_broadcast` fires on it
2,493 times in one capture and the waiters resume each time — so this is a
predicate that never becomes true, not a lost wakeup.

Alongside it the title churns threads: 27 `SceLibc_Thr` per second, 13,464 in
one run, against **148 for the whole of the §6az run**. Live thread count stays
flat (~107) and 2,801 are already `Exited` in a single snapshot, so this is
churn, not a guest-side leak — a job system retrying while the boot is parked.

**Leak 1: the guest thread stack and TLS windows overlapped.** Both windows walk
*downwards* from their own base in `GuestThreadRegionSlots` (1024) steps. At a
16 MiB stride the stack window spans 16 GiB from `0x7FFF_E000_0000`, which
reaches past the TLS base at `0x7FFE_0000_0000` (480 slots down) and through the
bootstrap stub band below it. The two pools then competed for the same
addresses, and a title died at **exactly ~511 threads** with

```text
pthread_create: failed to schedule guest thread 'SceLibc_Thr': failed to map
guest thread region near 0x00007FFFE0000000
```

after which the guest's libc throws `std::system_error` out of
`_Pad::_Launch -> _Throw_C_error -> __cxa_throw` and the process dies. Separate
strides (4 MiB for the 2 MiB stacks, 1 MiB for the 0x30000-byte TLS blocks) give
each window its own band; the ceiling moved 511 -> 1023, confirming the
diagnosis.

**Leak 2: nothing was ever reclaimed.** `IVirtualMemory` has no unmap, so those
regions were leaked outright — 1023 threads was simply the new ceiling, and GT7
reached it. Regions from a *cleanly* exited thread are now recycled through a
free list and zeroed on reuse, so a new thread still sees what a fresh mapping
would give it. With that in, 10,108 threads ran with zero mapping failures.

**Leak 3: one host thread per guest thread, forever.** Each guest thread owns a
`GuestExecutionRunner` (and sometimes a `GuestContinuationRunner`), each holding
a dedicated host `Thread`, and both were only disposed at teardown. The
recycling fix removed the crash and so exposed this one: **12,795 host threads**
in a single process. They are now torn down at the next thread creation — not
inline at exit, because the exiting thread is running *on* its own execution
runner and disposing that from inside its own loop races the wait handle it is
about to re-enter. Host threads dropped 12,795 -> **150**.

**What is not the cause of the stall.** Each of these was tested, not argued:

| Suspect | Test | Result |
| --- | --- | --- |
| The `V_XAD_U32` decode (§6bb) | reverted, rebuilt, booted | still stalls |
| The thread stride / recycling fixes | reverted, rebuilt, booted | still stalls |
| Both together — i.e. the exact §6az source state | reverted, rebuilt, booted | **still stalls** |
| GT7's save data and the Vulkan pipeline cache | quarantined, booted | still stalls |
| The diagnostic env of the §6az run | replayed exactly | still stalls |
| SDL window hidden / not foregrounded | booted visible and foregrounded | still stalls |

So the §6az render is not something this session's later changes broke, and it
is not explained by any persistent state found so far. The most likely remaining
explanation is that the stage-1 render-context wait of §6aq is timing-dependent
and §6az won the race once. **Do not treat "GT7 renders" as a settled
capability on the strength of that single run** — it is one observation, with a
screenshot, that has not been reproduced.

The PM4 fix in §6az is still correct and still necessary: when the boot does get
past stage 1, the graphics submissions it then makes are parsed instead of
discarded. It is simply not sufficient on its own.

### 6bb. `V_XAD_U32` — the compute opcode GT7's tiling kernel needs

`SHARPEMU_LOG_AGC_SHADER=1` reported one compute shader failing translation
every boot:

```text
cs=0x805951300 groups=8x40x1 wave=64 local=8x8x1
gpu=False error=block=0x0: pc=0x1C4 Vop3Raw345: unsupported vector opcode
```

`Vop3Raw345` is the decoder's fallback name: VOP3 opcode `0x345`, which it had
no entry for, so the shader failed emission and the whole dispatch was dropped.

The opcode is **`V_XAD_U32`** — `D = (S0 ^ S1) + S2` — on the authority of
LLVM's GFX10 definitions (`VOP3_Real_gfx10<0x345>`), which also place
`V_LSHL_ADD_U32` at `0x346` and `V_ADD_LSHL_U32` at `0x347`, both of which this
tree already decoded at exactly those numbers. The surrounding instruction
stream corroborates it: `V_AND_OR_B32`, `V_ADD_LSHL_U32`, `0x345`, `V_OR3_B32`,
then a `BufferLoadFormatX`/`BufferStoreFormatXy` pair — bit-shuffling address
arithmetic, which is where an xor-add belongs.

Decoded in `Gen5ShaderTranslator`, lowered in both the SPIR-V and MSL backends,
covered by `Gen5XadSpirvTests` (one test asserts the module really contains an
`OpIAdd` consuming an `OpBitwiseXor`, not merely that it compiles). Reverting
the decode entry reproduces the exact `Vop3Raw345` error the title produced.

This did not by itself change what reaches the screen — see §6ba — but it
removes a shader the GPU was silently dropping every boot.

### 6bc. GT7 does not rasterise its glyphs through libSceFont

Worth recording because it redirects the "no text in the dialog" question.
`SHARPEMU_LOG_IMPORT_FILTER=Font` over a boot gives:

```text
33360 libSceFont:sceFontGetCharGlyphMetrics
  141 libSceFont:sceFontSetScalePixel / SetEffectWeight / SetEffectSlant / RebindRenderer
   48 libSceFont:sceFontOpenFontMemory
    1 each: MemoryInit, CreateLibraryWithEdition, CreateRendererWithEdition,
            SelectLibraryFt, SelectRendererFt, OpenFontSet, AttachDeviceCacheBuffer,
            SupportSystemFonts, SupportExternalFonts
```

Metrics, 33,360 times — and **not one call to any glyph *image* export**
(`sceFontRenderCharGlyphImageHorizontal`, `sceFontGenerateCharGlyph`), nor any
unresolved font NID (all 19 unresolved imports in that run resolve to Hmd2,
VrTracker2, Share, DeviceService, Rudp and VideoOut names). GT7 uses libSceFont
for layout only and draws the glyphs itself.

So blank text is a *rendering* question, not a font-HLE one, and
`FontExports.RenderCharGlyphImageHorizontal` being a stub that clears its output
is not what makes GT7's dialog empty. One genuine defect is visible here anyway:
`sceFontGetCharGlyphMetrics` returns the same fabricated 8x16 metrics for every
character, so any title laying text out with it gets uniform advance widths.

### 6bd. The stalled boot yields at `boot_entry` line 98, waiting for `StartBootProject`

§6ba established that every reproduction parks with the title's main thread in
`scePthreadCondWait`. That framing was incomplete: **main waiting there is also
true of the run that rendered** — it is the `terminate`/join path of §6ay, not a
fault. The difference is further in, in the Adhoc VM, and it is now located.

Reading the live VMs out of a stalled process (one-shot `SHARPEMU_BREAK_GUEST_EXEC`
probes to recover the objects, then `read-vm.py`):

| Probe | Fires? | rdi |
| --- | --- | --- |
| `0x801DFCEA0` render-context tick | yes | ContextMain `0xE410160F10` |
| `0x80049ED80` render-context worker | yes | ContextMain |
| `0x80049F200` / `0x80049DD10` / `0x80049EE60` worker steps | **all three** | ContextMain |
| `0x800F853A0` update wait | yes | update ctx `0xEC030A0000` |
| `0x80049C650` update consumer | yes | — |
| `0x80049E350` command queue | yes | — |
| `0x80049E590` command execute | yes | command `0xE416900780` |
| `0x80013F980` VM build | yes | — |

So the whole native chain — tick, worker, all three table steps, command build,
queue and execute — runs, once each at least. Nothing in §6aq's "stage 1" is
missing.

The command's VM (`command + 0x68` = `0xE416900F10`, the same address §6ay
recorded) carries the same outer stack as the good run
(`boot -> clearSequence -> finalize -> terminate`). The joined Main sequence VM
is where the runs diverge:

```text
good run (§6ay, main-sequence-stack.txt):
  boot_entry  caller_line=126  meta=E4141B4380  ip=E414557458
  -> notifyState -> onBootSequenceDone -> ... -> open -> enter   (at the dialog)

stalled run:
  boot_entry  caller_line=126  meta=E4141B4380  ip=E414557398   (top frame)
```

**Read `caller_line` correctly.** It is the line *in the caller* where a frame
was invoked (`clearSequence` caller_line 169 is a line inside `boot`), so
`boot_entry caller_line=126` is identical in both runs and says nothing about
where execution is inside `boot_entry`. An earlier draft of this section mapped
126 onto `boot_functions.ad` and concluded the boot was stuck in save-data
setup. **That was wrong** — it matched a line number from a different source
file — and is recorded here so nobody repeats it.

**The instruction pointer does locate it.** Both captures share the same
compiled metadata (`E4141B4380`), and an IP indexes the instruction-pointer
vector at 8 bytes per instruction (§6ay's `enter`: `0x50 / 8 = 10`). In the good
run `boot_entry` is suspended in the call to `notifyState`, which the recovered
listing puts at instruction **150** (source line 108), so its IP is instruction
151 and the vector begins at `E414557458 - 151*8 = E414556FA0`. The stalled IP is
then `(E414557398 - E414556FA0) / 8 = 127`: **instruction 127, immediately after
the `SET_STATE: YIELD` at instruction 126, source line 98** — the same
resting shape as `enter` in §6ay.

```text
 84  project = manager.loadProject(PROJECT_ROOT_DIR + "/" + boot_project + "/" + boot_project)
 85  AwaitTask(context, project, fn { WidgetBind.bindingProject(context, project) })
 92  context.pushEvent(menu::MScriptEvent(project, project.onLoad,
 93      [update_context, is_product, isNetworkAvailable(target), isNoLoad(target)]))
 96  sequence.registerFunction("StartBootProject", fn { s_wait_boot_project = true })
 98  do { yield } while (s_wait_boot_project == false)      <- instruction 127
102  sequence.registerFunction("FinishBootSequence", fn { s_to_finish = true })
106  boot_sequence_common(context, target, is_product)
108  ListenerManager.notifyState("BootSequenceDone", true)
```

So the stalled boot has loaded and bound the boot project, pushed its `onLoad`
event into the render context, registered `StartBootProject`, and is yielding
until something calls it. **Nothing does.** The caller has to be the boot
project's own script, reached through that `onLoad` event — which is dispatched
by the render context the probes above show running.

**Ruled out along the way**, each measured over a stalled boot: zero
libSceSaveData imports (`SHARPEMU_LOG_IMPORT_FILTER=SaveData`), zero save-data
file I/O (`SHARPEMU_LOG_IO=1`), and on-disk save data created at 22:22 the
previous day — present for the good run too, and quarantining it changes
nothing. These were gathered while chasing the wrong reading; they remain true.

**Who is supposed to call it.** The only recovered caller is
`gt7/menu/app/boot/events/TopRootWindowEvent.swift` (`adc_B_X_6YJ81/script883`
in the earlier Opus scratch dump): `onInitialize(renderContext)` ends with
`FunctionManager.call("StartBootProject")` at line 64, after three nil-checks
(`Ctrl()`, `ctrl.GetLMCtx()`, `Widget()`) and no other exit. `BootApp.Initialize`
(`app.swift`) binds that event to the `TopRootWindow` page, and `Boot.App.onLoad`
(`service.swift`) starts the page at line 151 with `SequenceUtil.startPage(rc,
page)` — an unconditional `getInstance().startPage(...)`. So the chain is: pushed
`onLoad` event -> `initContext(updateContext, "boot", "TopRootWindow")` ->
`BootApp.Initialize` -> `startPage` -> root window initialises -> `onInitialize`
-> `StartBootProject`. `boot_target()` resolves `boot_project` to `boot` for
every version branch.

**Measured: the page is never started.** One-shot probe on the `startPage`
native (`0x801DB9BC0`, resolved in §6ax) with command-execute `0x80049E590` as
the positive control, over 62 PERF intervals of a stalled boot: control HIT
(command `0xE416900780`), `startPage` **never executes**, no crash, 2,764
`SceLibc_Thr` churned. `Boot.App.onLoad` never reaches line 151, the root window
never initialises, and `StartBootProject` is never called at all.

**A race this rules out.** `boot_entry` pushes the `onLoad` event (line 92)
*before* registering `StartBootProject` (line 96), so a fast render context
could in principle call it before it exists. That needs `startPage` to run; it
does not. Supporting: the concatenated `/piece/raw/4k/boot/common/<lang>/siei.img`
path `onInitialize` builds at line 55 is absent from all guest heap ranges
(`D000000000-FF00000000` holds only the two string constants), and stalled boots
make zero graphics submissions although the page would draw. The
`IsGameRegionScec` handler (`0x801D51050`, registered at `0x800E7337C`) *does*
fire, but `gt7/ui/WidgetBind.adi` also calls it during `bindingProject`, so that
hit is not evidence for `onInitialize`.

**Measured: `onLoad` reaches `initContext`, the page never starts.** Handlers
resolved from the module image (§6ay procedure; mapping re-verified against
`0x800F23455 -> 0x804B32371`): `getProjectByName` string `0x804AFC8B1` ->
registration `0x800F47538` -> handler `0x801DA03D0`; `pushEvent` string
`0x804AC8654` -> `0x800CABC42` -> `0x801DBCB70`. `createRenderContext`
(`0x804AFCA47`, registered at `0x800F2343F`) is bound to the `ret` stub at
`0x800000160` in this build — a no-op, nothing to probe.

In **one** stalled boot with `startPage` `0x801DB9BC0`, `getProjectByName`
`0x801DA03D0` and control `0x80049E590` armed together: the control and
`getProjectByName` HIT on the same host thread, `startPage` never (73 PERF
intervals, no presentation, no crash). A separate boot's `pushEvent` hit carried
the stalled `boot_entry` frame (`0xE416902740`) in its stack window.

So `Boot.App.onLoad` runs and reaches `initContext`'s project lookup
(`projects.swift` line 429) but never reaches `startPage` (`service.swift` line
151). Between them lie `initContext`'s three failure returns —
`getProjectByName("boot")` nil, `project.defined("TopRootWindow")` false,
`project["TopRootWindow"]` nil, each returning `[false, rc, nil, nil]` so `onLoad`
logs "Boot.App initContext failed" and returns — then `BootApp.Initialize` and
`BootService.Initialize`. The page lookup is the natural suspect: it depends on
`boot_entry` line 85 `AwaitTask(... WidgetBind.bindingProject ...)` having bound
the project, a load-order dependency that could hold in one run and not the next.

Gates read live in the stalled process, all open: ContextMain `+0x96 = 1` (the
byte `pushEvent`'s handler requires before it queues the event to `+0x88`),
`+0x94 = +0x95 = 0`, `+0x366 = 1`.

Caveats. `initContext` is shared `Projects::ProjectBase` code referenced by ~100
app services; the hit is attributed to the boot app because it is the only app
whose `onLoad` is dispatched during boot. Its argument could not be decoded after
the fact — the object was already reused. And this boot's heap was shifted by
`-0x8000` (command `0xE4168F8780`), so re-derive addresses rather than reuse them.

**Trap-time argument decode: the lookup is for `BootProject`.**
`TryReportGuestExecBreak` now also dumps the object behind `[rcx]` — the Adhoc
argument array's first element — and decodes its MSVC-style string at `+0x28`,
at trap time, because script objects are recycled within a frame or two (the
post-hoc read above found only reused memory). In a stalled boot the
`getProjectByName` hit printed:

```text
guest-exec-break-arg0 window @0x000000E416B372D0:
  +0x28: 0x6A6F7250746F6F42   +0x30: 0x0000000000746365   +0x38: 0x000000000000000B
guest-exec-break-arg0 string: "BootProject"
```

`BootProject` is the `ProjectName` static of `gt7/menu/app/boot/service.swift`,
so this is the boot app's own `onLoad` -> `initContext`, which removes the
attribution caveat. The name matters: `boot_entry` loaded the project by *path*
(`PROJECT_ROOT_DIR/boot/boot`), while `initContext` looks it up by its registered
*name*. Whether that name is registered with the manager at the moment `onLoad`
runs is now the question.

**The lookup succeeds.** Inside the `getProjectByName` handler the lookup
(`0x801DA0482 call sub_800C72980`) is followed by `mov rsi,[rbp-0x60]` and
`0x801DA048E call set_return`, so a one-shot probe at `0x801DA048E` logs the
lookup result in `rsi` (the handler's first caller is `BootProject`'s
`initContext`, so the one-shot catches the right call). `sub_800C72980` looks
the name up in the manager's table at `manager+0xB8`, type-checks the hit, and
otherwise returns the global at `0x806DA1358` — which reads **0** in the live
process. The probe returned `rsi = 0xE416B3AD70`, a populated object (vtable
`0x8054AF870`). So `getProjectByName("BootProject")` finds the project;
`initContext`'s first failure exit is **not** taken. The stop lies after it:
`project.defined("TopRootWindow")`, `project["TopRootWindow"]`, or
`BootApp.Initialize` / `BootService.Initialize`.

**Object identities (Itanium RTTI: `vtable-8 -> typeinfo -> +8 -> name`).** The
lookup result is `N4MENU8mProjectE` (`MENU::mProject`), ContextMain is
`N4MENU17mRenderContextPS4E`, the callback command `N4MENU14mFunctionEventE`.
The project holds its name through `+0x28` (holder `+8` = `BootProject`), its
class and module at `+0x10` (`hClass`) and `+0x30` (`hModule`), and at `+0x98` an
embedded `MENU::NodeList<mActor*>`.

**An inconclusive check, recorded so it is not over-read.** That actor list's
fields at `+0x10/+0x18/+0x20` are null for `BootProject`, which would suggest no
`TopRootWindow` page — but they are null for **all six** `mProject` instances in
the process (`MProject`, `CursorProject`, `Project`, two unnamed, `BootProject`),
enumerated by scanning `E400000000-E420000000` for the vtable. The list layout is
unvalidated, so this is not evidence either way. A heap scan for `TopRootWindow`
likewise found only compiled identifiers (event class names, source paths).

**Next step.** Decide which of the remaining exits is taken:
`project.defined("TopRootWindow")` false, `project["TopRootWindow"]` nil, or a
failure inside `BootApp.Initialize` / `BootService.Initialize`. `defined` and
element lookup are generic natives called from everywhere, so a one-shot probe
on them catches someone else's call first — this needs a **re-armable** probe
(restore the byte, single-step with the trap flag, re-plant the `int3`, log the
first N hits with the new argument decode) or a native unique to the success
path.

**Provenance limit.** The stalled frame was captured once. The decode rests on
the two recorded captures sharing one metadata address; re-derive the Main VM
through the command object in a fresh boot rather than reusing `0xE416902960`,
which did not resolve in the following run.

## Techniques

### Disassembling `eboot.bin`

It is a PS5 fself, magic `54 14 F5 EE`, not a plain ELF, and **the inner ELF's
`p_offset` values are not the offsets inside this container**. The header
claims the executable segment is at `0x4000`; it is actually at `0x21700`.

Working mapping for GT7's executable segment:

```
file_offset = guest_va - 0x800000000 + 0x21700      (valid for va < 0x804195B5C)
```

**Verify any mapping before trusting it.** Take a return address the loader
already printed — the `ret=` field on an import trace — and check that the
five bytes ending just before it are a `call rel32` (`E8 xx xx xx xx`). With
the wrong offset every candidate fails; with the right one they all pass:

```
delta 0x4000    8010EE119 pre8=03 48 89 df ff 10 4c 89   -
delta 0x21700   8010EE119 pre8=8d 7f 08 e8 a7 37 0a 03   call rel32
```

This one check would have saved hours. A wrong offset yields disassembly that
decodes cleanly into plausible-looking x86 while being entirely fictional, and
it makes correct emulator diagnostics look broken — the `ret=` values in the
`equeue.*` and `ampr.*` traces were dismissed as stale for exactly this
reason, and they were right all along.

The data segments need their own treatment. The dynamic symbol, string and
relocation tables are at `guest_va - 0x12D7BC0`, recovered by scanning the
file for a run of `Elf64_Rela` entries whose `r_offset` lands in the PLTGOT
range with type `R_X86_64_JUMP_SLOT`; the run found at file `0x607FDC0` is
exactly `0x6378` bytes, matching the `JmpRelSize` the loader logs.

Note also that SharpEmu rewrites guest code in memory — it reports e.g.
"Patched 1766 TLS loads" — so file bytes and runtime bytes can differ at
patched sites. When a crash dump prints `Code at RIP`, trust that over the
file.

### Finding a call site for a NID

1. Read the dynamic symbol table; import names are `NID#modid#libid`, e.g.
   `bBfz7kMF2Ho#BL#H`.
2. Find the symbol's `JMPREL` entry to get its GOT slot.
3. Scan `.text` for a rip-relative reference to that slot. This finds the PLT
   thunk (`jmp qword ptr [rip+disp]`), not the caller.
4. Scan `.text` for `call rel32` targeting the thunk. Those are the real call
   sites.

Worked example — the equeue pump was found this way:

```
sceKernelWaitEqueue      GOT 0x59107C8  thunk 0x8041923E0  callers 0x8040A3DC7 (+4 more)
sceKernelAddAmprEvent    GOT 0x5911D78  thunk 0x804194F40  callers 0x8040A4965 (+2 more)
sceKernelGetEventFilter  GOT 0x59107D0  thunk 0x8041923F0
sceKernelGetEventId      GOT 0x59107E0  thunk 0x804192410
sceKernelGetEventData    GOT 0x5911D70  thunk 0x804194F30
```

### Reading the crash

The exception handler already dumps a 64-entry recent-import ring per thread,
plus an RBP frame chain and memory windows around the fault registers. The
ring goes to **stdout** (`Recent import calls for managed=…`), the rest to
stderr — capture both.

The frame chain is reliable. The `[symbol+offset]` annotations next to raw
values are not: they anchor to whatever export is nearest and produce
nonsense like `f7uOxY9mM1U+0xC0DEBEF9E7335C00`. Decoding the memory windows as
ASCII is worth a try but check it reproduces across runs — one run appeared to
show a `Saturn_Adapter` device string that turned out to be unrelated host
heap and was gone on the next run.

### Deciding whether a change helped

Boot progress is easier to measure than to eyeball. From one stderr capture:

```sh
grep -c 'ampr.read_file'            err.log   # assets actually streamed
grep -c 'Scheduled guest thread'    err.log   # threads created
grep -c 'Vulkan VideoOut ready'     err.log   # graphics up
grep -c 'presented splash'          err.log
grep -o 'Import#[0-9]*' err.log | sed 's/Import#//' | sort -n | tail -1
grep 'unresolved:' err.log | sed -E 's/.*nid=([^ ]+).*/\1/' | sort | uniq -c | sort -rn
```

A stuck boot pins `ampr.read_file` at 1. A working one climbs.

---

## Techniques, continued

### Resolving a NID to a name offline

`scripts/ps5_names.txt` plus the algorithm in
`src/SharpEmu.SourceGenerators/Ps5Nid.cs`: base64 of the byte-reversed first
eight bytes of `SHA1(name + suffix)`, `/` replaced by `-`, padding stripped,
where the suffix is the 16 bytes `51 8D 64 A6 35 DE D8 C1 E6 B0 39 B1 C3 E5 52
30`. Hashing all 154,458 names into a reverse map takes a second and turns a
crash ring or an unresolved-import list into function names without running
the emulator. Verify the map against a known pair before trusting it — an
8-byte suffix and a hand-rolled base64 alphabet each produce a table that
looks fine and resolves nothing.

### Naming the library behind an unknown NID

Import symbols are `NID#libraryId#moduleId`, and both ids are base64-ish
(`A`=0 ... `-`=63, most significant character first). The two tables live in
the dynamic section: tag `0x61000045` maps module ids to names, tag
`0x61000049` maps library ids. Parse them out of the `DT_` array the loader
already reports (`TryLoadTableBytes: trying location=0x7F72A40, size=0x14C0`)
and a NID with no catalog entry still names its owner — which is how
`7IA8m97gygo#BB#+` turned out to be `logiWheel`, a module the emulator never
loads, rather than a missing SCE export.

### Mapping data-segment addresses to file offsets

Section 4's `guest_va - 0x12D7BC0` is specific to segment 8. The segments sit
back to back in the container but not contiguously with the text segment, so
derive each delta from that one anchor and the segment sizes the loader
prints:

| segment | VAddr | file offset | mapping |
|---|---|---|---|
| 0 (text) | `0x800000000` | `0x21700` | `va - 0x800000000 = file - 0x21700` |
| 1 | `0x804198000` | `0x41CC958` | `va - 0x800000000 = file - 0x34958` |
| 3 | `0x8053B0000` | `0x53E1860` | `va - 0x800000000 = file - 0x31860` |
| 7 | `0x805918000` | `0x59476E8` | `va - 0x800000000 = file - 0x2F6E8` |
| 8 | `0x80734B550` | `0x6073990` | `va - 0x800000000 = file + 0x12D7BC0` |

Check any of these the way section 4 checks the text delta: take a `ret=` the
loader printed and confirm the five bytes ending just before it decode as
`call rel32`.

### Reading the crash ring as a message

The recent-import ring records `snprintf` calls with their buffer sizes. When
a title builds a diagnostic out of fixed pieces, those sizes spell the message
out: matching 22 / 37 / 65 / 49 / 4 against the strings in the eboot is what
proved the ADHOC failure and the APR resolve were the same event, rather than
two unrelated things that happened to sit near each other in the log.

---

## Techniques, part three

### Telling a spinning thread from a blocked one

`SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1` plus
`SHARPEMU_PERIODIC_SNAPSHOT_SECONDS=15` and a diff of `imports=` across rounds
answers "is anything still moving", which no single snapshot can. Three states
are worth separating:

- `state=Blocked`, counter frozen: parked in an HLE call, waiting on the guest.
- `state=Running`, counter climbing: healthy.
- `state=Running`, counter **frozen**: spinning in guest code. This is the one
  that matters, and it is invisible to the stall watchdog because some other
  thread is still calling imports.

A thread spinning through an LLE-redirected call (`_Thrd_yield`, and anything
else served by a loaded `.prx` rather than an HLE stub) keeps a frozen counter
while burning a core, so a flat counter never means "idle" on its own.

### Resolving a PLT thunk to a symbol

Do this before drawing conclusions from a dispatch — two calls in one basic
block can belong to two different libraries.

1. Disassemble the thunk: `jmp qword ptr [rip+disp]` gives the GOT slot.
2. Find the `JMPREL` entry whose `r_offset` is that slot; `r_info >> 32` is the
   dynamic symbol index.
3. Index the dynamic symbol table, read `st_name` out of the string table, and
   the result is `NID#libraryId#moduleId`.

`JMPREL` for GT7 is at guest `0x7357980` size `0x6378`, dynsym at `0x7350D38`,
strtab at `0x734B550`; all three live in segment 8, so the file offset is
`guest_va - 0x12D7BC0`.

### Disassembling from a real instruction boundary

x86 decoding started at an arbitrary address produces plausible nonsense.
`mov ebx, 0x13940` followed by `[rbx+0x13900]` — an absolute address of
`0x27240` for a 64-bit object pointer — was the tell that an alignment was
wrong. Find the enclosing function first: scan backwards for `CC CC` padding
followed by `55 48 89 E5` (`push rbp; mov rbp,rsp`) and decode forward from
there. Capstone silently emits nothing when the first byte is mid-instruction,
so an empty listing means a bad start address, not an empty range.

### Finding who writes a field

Scanning `.text` for `mov byte ptr [reg+disp32], imm8` finds only the stores
that use that exact form. A compiler is free to `lea` a sub-object first and
store at a small displacement, which the scan misses. One apparent hit for the
renderer's wake flag turned out to belong to `N7TinyWeb20HttpsServerProcessorE`
— a different class with the same field offset, identified by walking its
vtable to the typeinfo and reading the mangled name out of the string table.
Treat a single-hit byte scan as a lead, not an answer.

### Walking the guest stack from a stall

`LogStallWatchdogSnapshot` printed the blocking call but not who asked for it,
which names a symptom and hides the cause. Walking the guest RBP chain out of
`_cpuContext` — read `[rbp+8]` for the return address, `[rbp]` for the next
frame, stop when it fails to advance — turns "parked in scePthreadCondWait"
into a 13-frame call path. Pair each frame with the assert/RTTI strings its
function references (scan the function for `lea reg, [rip+disp]` and read the
target as ASCII) and the frames name their own subsystems. That is how a bare
address became "GT7 is waiting on the PSN in-game catalog".

Two traps in the existing report: the `Live hardware context (main thread N)`
block is the *emulator's* main thread, not the title's, and the title's main
context is the `Stall snapshot:` line instead. And the periodic snapshot fires
on `SHARPEMU_PERIODIC_SNAPSHOT_SECONDS` regardless of the watchdog's progress
timer, so it works even for a title like GT7 whose network thread keeps the
watchdog permanently satisfied.

### Do not sample the emulator's own threads

`TryCaptureHostThreadContext` suspends the target thread. That is safe for a
guest thread sitting in native guest code, and unsafe for a .NET thread: adding
the emulator's main thread to the RIP sampler's rotation deadlocked the process
at guest entry within seconds, because a suspended runtime thread can hold a
CLR lock the sampler then needs. The sampler enumerates registered guest
threads for a reason.

### A breakpoint on guest code without a debugger

Most interesting guest functions call an HLE import early — a lock, an alloc, a
clock read. `SHARPEMU_PROBE_IMPORT_RET_ADDRESS=<hex>` fires when an import's
return address matches, so picking the return address of that first internal
call turns any such function into a breakpoint, with the caller available too:
if the function set up a frame first, `saved_ret` (`[rbp+8]`) is *its* caller.
That is how "who calls Event::set, and on which object" was answered without
standing up the debugger server.

`SHARPEMU_WATCH_GUEST_OBJECT=<hex>` is the complementary tool: log every import
handed a given guest pointer in rdi/rsi. Three lines of output settled a
question — "does anything other than main ever touch this object" — that days
of static scanning had not.

Both cost nothing when unset, and both beat byte-pattern scanning, which
repeatedly produced plausible wrong answers in this investigation.

## 6be. IME keyboard lifecycle was the boot wait

The next fresh boot narrowed the wait further than the earlier `initContext`
hypothesis. `getProjectByName("BootProject")` returned a populated
`MENU::mProject`; the script then entered `_start_page` and stopped in
`OSKUtil.closeChatDialog`, whose native implementation calls
`sceImeKeyboardClose` and waits for the IME keyboard state machine. The
corresponding native entry points resolve to `sceImeKeyboardOpen` at
`0x804192CB0`, `sceImeKeyboardClose` at `0x804192CC0`, and `sceImeUpdate` at
`0x804191EB0`.

SharpEmu’s old IME implementation returned success from `KeyboardOpen` but
kept no session and made `ImeUpdate` a no-op. In addition,
`sceImeKeyboardClose` (`PMVehSlfZ94`) was not exported at all, so the guest’s
close call received `ORBIS_GEN2_ERROR_NOT_FOUND`. Both changed in the same fix
and were never tested separately.

The title’s own code identifies the open event as the load-bearing change. The
guest IME event handler `0x80415F170` takes event `0x100` and, when all five
resource ids (`event[3..7]`, bytes 12–28) are zero, clears `obj+0xD30`. The
closeOSK wait `sub_800F47D70` loops while `+0xD30 != 0`. The event layout the fix
writes (id at byte 0, user id at 8, resource ids at 12) matches what the handler
reads.

The wait is bounded, not infinite. `closeOSK` (`0x801DC28D0`) calls
`sub_800F47D00`, which polls `+0xF4 == 0` 300 times and then force-closes via
`sub_801EB9750`. It then calls `sub_800F47D70` with the retry callback
`sub_801EBA6C0` and a limit of 300. That callback either sleeps 1 ms or makes a
virtual call on a thread-local object at `fs:-0x48`. The stall is indefinite
only if that virtual call waits for frames that never arrive. That is likely,
but it was not verified. It also explains how the 11:42 run (§6ba) reached the
sign-in dialog without any IME fix, so the boot should be treated as
timing-dependent until repeat boots show otherwise. The observed guest frame was `_start_page` at
`0xE416B37D70`; after the lifecycle fix the VM reached
`CheckLoginStatus` and `openLoginRequiredDialog` and Vulkan began presenting
guest frames.

The general emulator fix is in `src/SharpEmu.Libs/Ime/ImeExports.cs`: model an
empty keyboard bus, retain the opened user session, deliver the standard
`KeyboardOpen` event (`0x100`) through the guest callback on `sceImeUpdate`,
return an empty resource list with `CONNECTION_FAILED`, and remove the session
on close. `SharpEmuRuntime` resets this state between boots, and
`ImeKeyboardTests` covers the lifecycle and callback retry behavior.

Release build succeeded and the full suite passed 1,089 tests. PERF intervals
with `presented_fps > 0`, from `artifacts/gt7-codex-20260913/*.err.log`:

| boot | IME fix | intervals with frames | max fps |
| --- | --- | --- | --- |
| `baseline` | no | 1 / 226 | 0.2 |
| `osk` | no | 1 / 294 | 0.2 |
| `ime-fixed` | yes | 122 / 137 | 29.8 |
| `ime-repeat` | yes | 361 / 375 | 30.0 |

Two fixed boots against two unfixed ones is strong but not conclusive, given the
earlier unreproducible good run. Both window captures are invalid:
`ime-fixed-window.png` is black below the title bar, which is probably a
PrintWindow capture that missed the Vulkan surface, and `ime-fixed-visible.png`
is entirely white.

Opus repeat boots (same source, Release, `SHARPEMU_LOG_VIDEOOUT_FPS=1`, 150 s
each, logs in `artifacts/gt7-opus-20260913b/`):

| boot | intervals with frames | max fps | note |
| --- | --- | --- | --- |
| `boot1` | 120 / 135 | 31 | |
| `boot2` | 127 / 141 | 61 | user pressed Enter mid-run |
| `boot3-dump` | 121 / 135 | 31.8 | swapchain dump every 600 frames |

Counting Codex's two runs, that is five of five fixed boots presenting frames,
against the unfixed runs that presented none. The boot no longer looks
timing-dependent at this sample size.

The swapchain dumps (`SHARPEMU_TRACE_GUEST_IMAGES=present`,
`SHARPEMU_GUEST_IMAGE_DUMP_DIR`, `SHARPEMU_SWAPCHAIN_DUMP_EVERY=600`) are the
first valid capture of the screen. Dump 1 is black. Dumps 2–6 are identical
(same sampled non-black count) and show the sign-in dialog panel with its
rounded button and reflection, but **no text or glyphs anywhere**. A user
screenshot of `boot1` matches. Pressing Enter plays a sound and presentation
rises to a steady 60 fps, so keyboard input reaches the title and the dialog
most likely advances, though the next screen was not captured.

Next blocker: missing text. GT7 made metrics-only libSceFont calls in §6bc, so
start from the text draw path (the glyph atlas texture, its upload and sampling,
and the text shader) rather than the font HLE.

## 6bf. Real glyph rasterisation, and why text is still invisible

**§6bc is wrong.** It concluded GT7 never calls a glyph *image* export, but that
census came from a boot that stalled before the dialog. On a boot that reaches
the sign-in dialog GT7 calls `sceFontRenderCharGlyphImageHorizontal` 30 times,
each preceded by `sceFontRenderSurfaceInit` (`SHARPEMU_LOG_FONT=1`, new).

### libSceFont now rasterises

Every font export was fabricated: `sceFontOpenFontMemory` discarded the font
bytes, every metrics call returned the same 8x16 box, and the glyph-image export
cleared its output. GT7's fonts are **OpenType with CFF outlines** (`OTTO`,
48 of them, 3 KiB - 4.7 MiB, five of them CID-keyed).

`src/SharpEmu.Libs/Font/OpenTypeFont.cs` (new) parses the table directory,
`cmap` (formats 4 and 12), `hmtx` and the CFF INDEX/DICT structures including
FDArray/FDSelect, interprets Type 2 charstrings, and rasterises coverage with
signed-area accumulation. `FontExports` keeps the parsed font per handle,
records the pixel size from `sceFontSetScalePixel`, answers both metrics exports
from the real outline, and writes coverage into the guest's
`SceFontRenderSurface` clipped to its scissor. Unknown handles and unmapped
character codes keep the old placeholder.

Verified: `FontExportsTests` renders a synthetic one-glyph font; an offline
harness rendered `F` from GT7's own 4.7 MiB fonts; and in a live boot the guest
glyph slot at `0xEC06856000` contains a correctly rasterised bold `F`
(64x36 surface, 256-byte stride).

### The text is drawn by GT7 but never reaches the GPU

With glyphs in guest memory the dialog is still blank. Measured, in one boot:

- No draw ever binds a glyph texture. A new
  `SHARPEMU_TRACE_TEXTURE_BIND_ADDRESS=<hex>` (logs binds whose address lies in
  the MiB above it, and which upload path each took) saw nothing for the glyph
  pool, and 72,455 `agc.texture_binding` lines during the dialog never mention
  it. `TryCreateGuestDrawTexture` has a single caller, so that covers every
  sampled bind. No compute dispatch reads it either.
- Every dialog draw is a 4-vertex quad/strip or a 30-index triangle list: the
  panel, the button, gradients and the UI layer RT. Nothing has a text-sized
  vertex count.
- GT7 *does* build the quads: one-shot exec breaks at `0x800086173` and
  `0x800086225` (inside `sub_800084a30`, the text layout virtual at vtable+0x70)
  both fire, and the glyph-cache entry is live in guest memory at
  `0xEC021FC020`: slot pointer, 27.1x35.1 padded size, 64x36 dims, LRU state 4,
  and a valid Gen5 texture header at +0x24 that decodes to the glyph address.

So the loss is between GT7's per-glyph quad list and a submitted draw. The next
step is the text object's other virtuals — `sub_8004223d0` dispatches +0x40,
+0x48 then +0x50/+0x58 after the layout call, vtable near `0x8058081D8`.

Ruled out: the write tracker (`SHARPEMU_GUEST_IMAGE_CPU_SYNC=1` changes
nothing), texture fallbacks (`agc.texture_fallback` never fires), and descriptor
decode (every logged binding decoded; the glyph header decodes correctly by
hand).

### What the dialog says, and what OK does

Glyph codes from `SHARPEMU_LOG_FONT` reconstruct the message without pixels:
title font `Failed`, body characters `Youcantsewrkfilyg PS™N.`, button `OK` —
the not-signed-in message `NP_YOU_CANNOT_ENTER_WITHOUT_SIGNING_IN` from
`openLoginRequiredDialog` (`gt7/network/LoginUtil.ad`).

The script path is `CheckLoginStatus` -> `makeSureLogin` -> dialog -> returns
`[false, false]`, and `TopRootWindowEvent` then always calls
`onProductBootSequenceDone(context, isOnline=false)`. **Pressing Cross does
advance the boot**: presentation goes to a steady 60 fps and GT7 starts the
PS Studio logo movie. The screen stops changing there.

### The movie: AvPlayer file replacement, and the open that fails

`sceAvPlayerAddSource("/movie/oped/psstudio.mp4")` failed because SharpEmu only
resolved host paths. GT7 supplies `SceAvPlayerFileReplacement` callbacks in its
init data (object `+0x30`, open `+0x38`, close `+0x40`, readOffset `+0x48`,
size `+0x50`), which SharpEmu ignored while already reading the allocator and
event slots from the same struct.

`AvPlayerExports` now streams an unresolvable source through those callbacks
into a temp file (`TryReadGuestSourceThroughCallbacks`, covered by
`AvPlayerFileReplacementTests`). `readOffset(object, buffer, position, length)`
needs four arguments, so `IGuestThreadScheduler` gained a four-argument
`TryCallGuestFunction` (rcx), implemented by `DirectExecutionBackend`.

It is still blocked, one layer deeper: GT7's `open` runs and its VFS resolves
the movie (`apr_resolve_ids` finds `/app0/contents/K/J/6F1CC`, a 57.7 MB
`ftyp mp42/avc1` file), but GT7's `size` callback returns 0, meaning its own
stream open failed. Polling the size for 500 ms does not help, so the open is
not merely asynchronous. `sub_801019A60` (open) hands the path to
`sub_801019AA0`, which opens the stream at `object+0x90` — exactly what
`sub_801078D80` (size) reads — so both agree on the object. **Corrected in
§6bg:** GT7's open succeeds and the stream holds the correct size; the "no
stream" reading came from memory inspected after SharpEmu had already called
GT7's `close`.

Fixed along the way: `sceKernelVirtualQuery` returned NOT_FOUND for any address
the kernel HLE did not map itself, including guest thread stacks mapped by the
CPU backend. It now falls back to the host mapping (guest addresses are
host-identical). GT7's asset reader calls it next to `apr_resolve_ids`. This did
not change the movie result.

### Progression: GT7's own app options get past the login dialog

SharpEmu forwards up to two guest argv entries through `SHARPEMU_GUEST_ARGS`
(`CpuDispatcher.InitializeProcessEntryFrame`). GT7's boot scripts read them as
`main.AppOpt`:

- `AppOpt.defined("offline")` makes `PlayerStatusControl::CheckLoginStatus`
  return `[false, false]` immediately (script11, line 1098).
- `AppOpt.defined("skip_op")` clears `playpsstudio`, skipping the PS Studio
  logo and opening movie (`TopRootWindowEvent`, line 135).

Measured: `SHARPEMU_GUEST_ARGS='skip_op'` plus repeated Cross presses walks the
boot **past the login dialog and past the movie blocker** into GT7's first-boot
display-calibration wizard. The glyph codes GT7 rasterises name the pages:
`Display Setting`, `Make sure ...`, `Select "Adjust Brightness"`, `Back` /
`Next`, then the slider page (`Select`, `Min`, `max`, `-10`). Frame dumps show
the new wizard screens (a content box with two buttons), not the dialog.

`SHARPEMU_GUEST_ARGS='offline skip_op'` (two arguments) stalls the boot before
the first frame - only pass one until that is understood.

The wizard then needs input Cross alone does not provide, so the main menu was
not reached unattended. `SHARPEMU_AUTO_CROSS` presses Cross only, so this
session added `SHARPEMU_AUTO_PAD="40:cross,44:right,48:cross"` (PadExports):
each entry holds the named buttons (cross, circle, square, triangle, up, down,
left, right, options, l1, r1, combined with `+`) for 0.4s at that second offset
from process start. Mixed Cross/Right/Down patterns still stop on the
brightness page, and how far a run gets varies, so the page is input-timing
sensitive; driving it by hand is currently the reliable way past it.

### Why text never appears: the widget is measured, never rendered

The text widget class (vtable `0x8058081D8`, confirmed live) has these slots:
`+0x40` `sub_80323F210` (build render node), `+0x48` `sub_80323F3C0`,
`+0x50` `sub_80323F410` (render: box, optional outline, then
`sub_8032420D0` which allocates the vertex buffer and emits the glyph draws),
`+0x58` `sub_80323F550`, `+0x68` `sub_80323A400`, `+0x70` `sub_800084A30`
(layout).

One-shot exec breaks on **eight** of those entries across two boots, each armed
at a real `push rbp`: only `+0x70` (layout) ever fires, while 30 glyphs are
rasterised. `sub_8004223D0` calls layout and returns; its dispatch to
`+0x40/+0x48/+0x50` sits behind flags that are clear at runtime
(`obj+0x25 & 0x10 = 0`, bit 13 of `obj+0xCC = 0x00030080` clear), and the local
that would otherwise enable it is set only by character-class tests.

Confirmed by reading the live object: `+0x3B8` (texture array) and `+0x3C0`
(vertex buffer) are **null** and both counts are 0, so the geometry the glyph
draws would use is never even allocated. The glyph cache entry itself is valid
(bitmap, metrics, Gen5 texture header), so the loss is entirely on GT7's side of
the draw submission.

Ruled out this session, with evidence: no draw or compute dispatch binds a glyph
texture (`SHARPEMU_TRACE_TEXTURE_BIND_ADDRESS` over the glyph pool, plus 72k
`agc.texture_binding` lines); no `agc.draw_reject` fires, so nothing is dropped
by the empty-SRT guard; no font, text or AGC import is unresolved; the 120x120
texture bound ~700 times per frame is a diagonal dither pattern, not a glyph
atlas.

Next: find what the engine calls per frame instead. The layout call comes from
`sub_8004223D0` (import-return probe on `0x800086B64` shows `saved_ret =
0x8004235F0`), so walk *its* callers to the per-frame widget draw, rather than
assuming this class renders itself.

## 6bg. KytyPS5 comparison, and the movie open that actually succeeds

### The KytyPS5 pin

`GET /repos/KytyPS5/KytyPS5/releases/latest` returns
`KytyPS5-2026-09-12-d3d7bd3` (commit `d3d7bd33f8eb4996cf198c430bb2e4fa4bf518eb`,
published 2026-09-12 14:14:55 UTC). The plain release list returned
`KytyPS5-2026-09-12-dff2b19` first, which is **not** the latest — that endpoint is
not in publish order. Checked out detached at `artifacts/kyty` (ignored).

### What Kyty does and does not explain

- **AvPlayer.** `AvPlayerInitDataEx` is `this_size`, the 5-pointer memory
  replacement, then the file replacement `{object, open, close, readOffset,
  size}` at `+0x30..+0x50` — the offsets SharpEmu already reads from GT7's
  `init_ex` data. The callback ABI matches SharpEmu's calls:
  `open(object, path)` (negative = failure), `size(object)` (0 = failure),
  `readOffset(object, buffer, position, length)`. Kyty's `add_source` opens the
  source **synchronously**, as does shadPS4 (`avplayer_state.cpp:146`) and
  SharpEmu. Kyty uses the callbacks whenever `open` is set; SharpEmu only when the
  host cannot resolve the path, which is irrelevant here because GT7's path does
  not resolve. Nothing in this comparison explains a size of 0.
- **APR.** Kyty implements `sceKernelAprGetFileSize` (`WvEu7yl3Ivg`) as
  `(uint32_t id, uint64_t* size)` and writes the size. SharpEmu's
  (`KernelAprCompatExports.cs`) returns success and writes nothing — a real
  contract bug, but GT7 imports `sceKernelAprGetFileStat` and
  `sceKernelAprResolveFilepathsToIds`, not this (import census,
  `artifacts/gt7-investigation/census1.err.log`), so it is not on the movie path.
  Kyty's `sceKernelAprResolveFilepathsToIds` takes a fourth argument
  `uint32_t* error_index`, written only on failure — the value SharpEmu logs as
  `arg4`. Kyty returns `-1` with errno for failed libkernel APR calls where
  SharpEmu returns ORBIS error codes. Both differences are failure-path only; the
  movie's resolve succeeds.

### GT7's movie open, decompiled

All from `DecompAt.java` (logs `vfsopen-`, `filecb2-`, `mgr60-`, `mount-`,
`device-`, `fb60-`, `size-decomp.log` in `artifacts/gt7-opus-20260913b/`).
`fileObject` is the AvPlayer file-replacement object.

| Function | Role |
| --- | --- |
| `sub_801019A60(fileObject, path)` | open callback; calls `sub_801019AA0(fileObject+0x50, path, …)`, returns void |
| `sub_801019AA0` | calls `sub_800777620(fileObject+0x90, path, 1, 0, 0, …)`; on success sizes a 4 MiB buffer |
| `sub_800777620(vfs, …)` | asks the filesystem manager singleton `0x806DFBF18` `vtbl+0x60` for a stream (null → `vfs+0x10 = 4`, return -1); stores it at `vfs+0x18`, sets `stream+0x68 = vfs`; if `stream+0x1DC == 1` → `vfs+0x10 = 1`; else calls device `[stream+0x48]->vtbl+0xB0`; success sets `vfs+0x31 = 1`, failure clears the stream |
| `sub_80057BBF0` (manager `+0x60`) | mount lookup `sub_80057BDD0` (prefix match over the manager's mount map, count 3), then tail-calls `device->vtbl+0x28(device, relpath, …)` |
| `sub_80409FD90` (device `+0xB0`, the `gt.idx` archive device) | if `stream+0x130 == 0`, fills the stat block `stream+0x188..+0x1D0` from `sub_80409FB60` synchronously |
| `sub_80409FB60` | FNV-1a hash of the path compared against the `gt.idx` entry; size from entry `+0x0C` and `+0x15` |
| `sub_801078D80(fileObject)` | size callback: `sub_800775770(fileObject+0x90, buf)`, returns `buf+8` |
| `sub_800775770` | checks `stream+0x68 == vfs` and `stream+0x1DC != 1`, then device `vtbl+0x108` |
| `sub_8040A0760` (device `+0x108`) | with `device+9 == 0`, copies the stat block into `buf`; `buf+8` is `stream+0x190` |

No import and no asynchronous hand-off sits on either path.

### Measured

- **`vfs1`** (no hold): read after AvPlayer gave up — `vfs+0x10 = 0`, stream 0.
  Invalid: `TryReadGuestSourceThroughCallbacks` calls GT7's `close` in its
  `finally`, so this is the torn-down object. (State 0 rather than 4 already hinted
  that the manager had returned a stream.)
- **`vfs2`**, with a temporary `SHARPEMU_AVPLAYER_HOLD_BEFORE_CLOSE_MS=60000`
  that sleeps before `close`; object `0xE41844C5E0`, read with
  `tools/vfsstate.py` during the hold:

  ```text
  vfs+0x18 stream        = 0xE41844C6C0
  stream+0x68 back-ptr   = 0xE41844C670  (= fileObject+0x90, matches)
  stream+0x1DC pending   = 0
  vfs+0x30               = 0x0101        (+0x31 = 1: open succeeded)
  stream+0x48 device     = 0xE400BBE890  (vtbl 0x805903880, name "gt.idx" at +0x30)
  stream+0x190 size      = 0x370E421     (57,730,081 bytes, the MP4's real size)
  ```

So during the hold the size callback would return 57.7 MB, yet SharpEmu's
`size` call, made immediately after `open` returned, got 0. Two explanations
remain: the open completed only after `TryCallGuestFunction` returned (a
nested-callback block/resume would do it), or the size call itself was mis-run or
its return mis-read.

### The retry experiment, and a host crash

`vfs3` extended the temporary diagnostic to re-call `size` after one second
(logging `retry_size=`), with `SHARPEMU_LOG_GUEST_THREADS=1` to show nested
callback blocks. It never reached AvPlayer (no `init_ex` line):

```text
Unhandled exception. System.ObjectDisposedException: Cannot access a disposed object.
Object name: 'Microsoft.Win32.SafeHandles.SafeWaitHandle'.
   at SharpEmu.Core.Cpu.Native.DirectExecutionBackend.GuestExecutionRunner.<>c__DisplayClass5_0.<.ctor>b__0()
```

This is the first occurrence in every captured log. The lambda is
`GuestExecutionRunner`'s `ThreadMain`, whose loop waits on `_workAvailable`.
`Dispose()` sets `_stopping`, signals, joins for **500 ms**, then disposes
`_workAvailable` whether or not the thread exited. A runner that is still busy
(slower under thread logging) then calls `WaitOne()` on a disposed handle.
Callers include the reaper added in §6ba. Plausible, **not yet verified**.

Ruled out for the movie: an asynchronous step inside GT7's VFS open (static,
above); the `gt.idx` lookup (it produced the right size); the APR resolve (it
succeeds); AvPlayer ABI or open-ordering differences against Kyty and shadPS4.

Captures: `artifacts/gt7-opus-20260913b/vfs1.*`, `vfs2.*`, `vfs3.*`.

## 6bh. Blockers B and C were two backend bugs; the movie now plays, unseen

Both findings came from Astra's review of §6bg; both fixes are emulator-general
(`Core/Cpu/Native/DirectExecutionBackend.cs`). Full suite after the fixes:
**1,094 passed, 0 failed** (Metal 60, ShaderCompiler 95, SourceGenerators 36,
Libs 903).

### C: the runner disposal race

Sequence, from the code (not reproduced by a failing test — the fix changed the
runner API, so the new test cannot compile against the old code):

1. `RunGuestThread` publishes `State = Exited` under `_guestThreadGate`, releases
   it, and still has its `finally` to run, which re-takes the gate to clear
   `ExecutorActive`.
2. Another thread creates a guest thread; `TryStartThread` holds the gate and
   runs `ReapExitedGuestThreadRunnersLocked`, which sees `Exited` and calls
   `Dispose()` on that runner.
3. `Dispose()` joins for 500 ms — the runner is blocked on the gate the reaper
   holds, so the join times out — then disposes `_workAvailable`.
4. The reaper releases the gate; the runner finishes and loops into
   `_workAvailable.WaitOne()` on a disposed handle → the §6bg
   `ObjectDisposedException`. Guest-thread logging only widens the window.
   `GuestContinuationRunner` had the same shape, plus a worse case: a stop that
   landed before its thread picked up work left the `Run()` caller waiting on
   `_workCompleted` forever.

Fix:

- Both runners wait on a monitor (`Monitor.Wait/Pulse` on a private gate)
  instead of `AutoResetEvent`s, so there is no handle to destroy under a live
  thread and a timed-out join leaves nothing to clean up.
- `RequestStop()` is idempotent and ordered against `Schedule`/`TryRun` by the
  same gate. `Dispose()` = `RequestStop()` + a bounded join.
- `GuestContinuationRunner.TryRun` always completes work it accepted, even if a
  stop arrives meanwhile; it returns false if the runner was already stopped, and
  `TryCallGuestContinuation` then runs the continuation on a temporary thread.
- The reaper skips threads that are `Exited` but still `ExecutorActive`, and only
  requests stops — no join under `_guestThreadGate`. `ClearGuestThreads` already
  disposed outside the gate and is unchanged.

Test: `Cpu/GuestRunnerDisposalTests` (isolated process via the new
`Cpu/IsolatedTestWorker`) holds a slice across `Dispose()` with gates, releases
it, and asserts the runner thread exits, a late `Schedule` does not run, the
continuation waiter completes, and a late `TryRun` returns false without hanging.
The reaper and `ClearGuestThreads` are not covered by a direct test.

### B: the size callback's result was never read

- `TryCallGuestFunction` returns `context[Rax]`, but `ExecuteGuestThreadEntry`
  never wrote the guest's RAX into that context on an ordinary return;
  `CallNativeEntry` only exposes a 32-bit `int`, used for a log string.
- GT7's size callback (`sub_801078D80`) makes no imports, so its fresh callback
  context kept `RAX = 0`. That is §6bg's second explanation ("mis-read"); the
  "open completes late" explanation is ruled out.
- Callbacks that do call imports presumably returned whatever the last import
  dispatch left in the context, which would hide the bug elsewhere (reading, not
  measured).
- Continuations leave guest code through the shared `_guestReturnStub`, which
  calls `TlsGetValue` before restoring the host stack and so clobbers RAX too.

Reproduced first: a synthetic import-free callback `mov rax,
0x123456789ABCDEF0; ret` through `TryCallGuestFunction`, with the propagation
lines disabled, returned `Actual: 0`.

Fix: every host-RSP slot is now 16 bytes (`[+0]` host RSP, `[+8]` guest RAX). The
entry stub stores RAX after the guest returns into it; the guest return stub
pushes RAX across `TlsGetValue` and stores it. `ExecuteGuestThreadEntry` and
`ExecuteGuestContinuationEntry` copy the slot into `context[Rax]` on `Returned`
only; `Blocked` and `ForcedExit` are untouched. A guest thread's `ExitValue` now
also carries its real return value.

Test: `Gen5NativeReturnSmokeTests.ImportFreeGuestCallback_ReturnsFull64BitRax`,
both the direct return and the return through `_guestReturnStub` (address read
by reflection).

### Movie retry: `rax-fix1`

`boot.ps1 -Seconds 170` with
`SHARPEMU_AUTO_PAD='30:cross,36:cross,42:cross,48:cross,54:cross,60:cross'`; no
hold diagnostic, no guest-thread logging, no added delay.

```text
file_replacement open=1416649903 size=57730081 object=0x000000E418434920 ...
source guest='/movie/oped/psstudio.mp4' host='…\avplayer\720AF098….mp4' 3840x2160 fps=59.940 duration_ms=8558 audio=True
decoder_started … 3840x2160 nv12
texture_buffer index=0 data=0x000000F41AE88000 size=12441600   (also 0xF41BA68000, 0xF41C648000)
video_frame … ts=0 / 33 / 83 / 117 / 150 / 200
audio_frame … ts=0 … 4757
```

- Size correct, the whole file read through GT7's callbacks, video frames and
  audio decoded. AvPlayer's trace prints only the first 32 lines then every
  300th, so six `video_frame` lines are not a frame count.
- No `ObjectDisposedException` or unhandled exception. Presented fps:
  `intervals=41 nonzero=33 max=61`.

### What is visible (user observation, same run)

From the user's screenshots and report during the run:

- 00:31 and 00:41: the sign-in dialog panels, still without text (blocker A).
- The movie's **audio is heard, but no video picture is seen**.
- 00:42: a monitor graphic with a solid **red rectangle** where a screen would be;
  the user can move around and select things, but nothing is labelled.

Not established: what the red rectangle is. By timing it appears ~6 s after the
Cross at 36 s, while the 8.6 s movie's audio is playing, so it could be the quad
the movie should be drawn on; it could equally be GT7's display-calibration
wizard (§6bf reached one with `skip_op`). A grep for a red SharpEmu fallback
texture found none (a lead only, not exhaustive).

### Open, not pursued in this session

1. **Movie picture.** Decoded NV12 frames land in the three `texture_buffer`
   addresses above; find whether any draw binds them
   (`SHARPEMU_TRACE_TEXTURE_BIND_ADDRESS` on a buffer address — the addresses move
   between boots) and what the red rectangle is.
2. **Text** (blocker A) unchanged.
3. **Housekeeping:** the TEMP `SHARPEMU_AVPLAYER_HOLD_BEFORE_CLOSE_MS` code in
   `AvPlayerExports.cs` is still present and must be reverted;
   `sceKernelAprGetFileSize` still writes nothing.

Ruled out: an asynchronous step in GT7's VFS open as the cause of size 0; raising
the join timeout or catching `ObjectDisposedException` (would leave the race).

Capture: `artifacts/gt7-opus-20260913b/rax-fix1.*`.

## 6bi. The movie pipeline, traced from decode to composite

Following Codex's revised plan: one controlled capture first, then the first
missing step. Captures `movie1`..`movie6` in `artifacts/gt7-opus-20260913b/`.
All diagnostics below are env-gated and **uncommitted**.

### Instrumentation added

`AvPlayerExports` already had `ShouldTraceVideoBufferAddress`/`...Range`
(registered frame-buffer ranges) with **no callers**. Hooked into
`AgcExports.TraceTextureBind`, which every draw *and* compute image binding
reaches through `TryCreateGuestDrawTexture`:

| Trace (env) | What it gives |
|---|---|
| `agc.video_buffer_bind` (`SHARPEMU_TRACE_AVPLAYER_IMAGES=1`) | any bind inside a registered AvPlayer buffer — all three buffers, both planes |
| `agc.texture_bind_summary` (same) | every 5000th bind: running totals — the positive control |
| `agc.large_texture_bind` (same) | first bind of each distinct texture ≥1920 wide, address-independent |
| `agc.movie_bind_group` (`SHARPEMU_TRACE_MOVIE_TEXTURE=<hex>`) | the complete image-binding group of any draw/dispatch binding within 64 MiB of that base |
| forced `agc.shader_draw` (same) | the existing draw trace, forced for those draws only |

### The chain, end to end

1. **AvPlayer delivers.** 132 `sceAvPlayerGetVideoDataEx` calls in a 75 s boot
   (`ret=0x8009E2668`), each returning a full NV12 frame; payload hashes differ
   per frame, `nonzero_bytes=12441600/12441600`.
2. **GT7 copies every frame into its own ring** — `sub_8009E2620`, decompiled.
   It reads width/height/pitch from the frame info (`+0x18/+0x1C/+0x3C`), stores
   them at `player+0x3CC/+0x3D4`, allocates a 3-slot ring at `player+0x418`
   (slot = `h*pitch*3/2 + 0x1000` = 12,445,696 bytes), `memcpy`s `info.pData`
   into the next slot, and publishes it at `player+0x410` (timestamp `+0x400`,
   flags `+0x3F0`). Ring base observed at `0xF41D228000` in three runs.
   `sub_8009E2590` is just `sceAvPlayerCurrentTime * 90` (90 kHz).
3. **Those copies are bound as textures**, one pair per ring slot:
   Y `3840x2160 fmt1/num2/tile0` at the slot base and UV `1920x1080 fmt3/num2`
   at slot+`0x7E9000`.
4. **The movie draw** — `es=0x1258E3AB00 ps=0x1258E49600` (SPIR-V 257,592 B),
   primitive `0x6`, 4 vertices, blend off, `write_mask=0xF`, no depth, viewport
   `3840x-2160` (Y-flipped) — renders into a **3840x2160 `fmt9/tile27` target**,
   one per ring slot: `0x01C00000`, `0x03BE0000`, `0x05BC0000`.
5. **A composite draw consumes those targets** — `es=0x805949000
   ps=0x805948E00` (SPIR-V 15,496 B), primitive `0x15`, scissor and viewport
   `1920x1080` — sampling the 4K target into `0x1238BA0000`
   (`1920x2160 fmt12/num7/tile27`).

So the whole path exists in the command stream. **Yet nothing of it reaches the
screen:** swapchain dumps 4-10 of `movie1` span the entire playback (after
`decoder_started`) and are one identical hash `0xD8A0F7357A633A3D`, pixel-equal
to dump 3 from *before* the movie — the sign-in dialog. The picture only changes
after playback ends (dump 11+), when the monitor graphic appears (black
rectangle in the dump; the user saw it red on screen, so it likely animates).

### Ruled out

- *"No draw binds the movie frames"* (the §6bh next step): wrong target. 165,000+
  binds were logged with the positive control live and **zero** touched
  AvPlayer's buffers — because GT7 samples its own copies, not those buffers.
- The pointer watch `SHARPEMU_WATCH_GUEST_OBJECT=<Y plane>` hits only
  `YpkGsMXP3ew` = **`sceRazorCpuPopMarker`** (unresolved, takes no arguments):
  the frame pointer is merely left in `rsi`. 44 hits = 132/3, the rate buffer 0
  comes round in the 3-buffer rotation. Not consumption evidence.
- AvPlayer event order: `NotifyEvent` traces *after* the callback returns, so the
  logged `3,2,3` is really READY -> (callback calls `Start` -> PLAY) -> a second
  PLAY from auto-start. Only the duplicate PLAY is questionable.

### Leads, not conclusions

- **Last-writer mismatch.** In the composite draw, the sampled 4K target's
  recorded writer is `writer=1693: es0x1258E0AF00 ps0x1258E0F500 v4 prim0x5` — a
  *different* shader pair from the movie draw. Either another pass overwrites the
  target after the movie draw, or the writer bookkeeping is stale. Worth checking
  whether SharpEmu's GPU hands the composite the image the movie draw wrote.
- **Two of four bindings in every movie group are junk**: `0x3E809BCD00
  65534x65536 fmt3` and a null `1x1 fmt10`, each producing a fallback texture
  (exactly the 32+32 `texture_fallback` lines). Unknown whether the pixel shader
  samples those slots.
- **The composite target is `1920x2160`** — double height for a 1080p output.
  Which half is presented is unknown.
- The Y plane probes as `0x10` and UV as `0x80` (black/neutral) at the top-left
  corner — consistent with a logo fade-in, not proof of a blank frame.

### Next

Establish which of these holds: the movie draw never executes on the host GPU;
it executes but the composite samples a different image; or the composite
executes and its output never reaches the flip. A per-flip trace of the
video-out buffer address and content hash during playback separates the last one
from the first two.

## 6bj. Movie draw fails creating an unsupported scaled Vulkan texture

2026-09-14, independent debugging for the Opus handoff. No emulator source
changes. Rebuilt Release successfully (0 warnings/errors), then ran three
controlled captures with existing diagnostics. Stopped only these captures'
launcher/child processes. Evidence: `artifacts/gt7-astra-20260914/`.

### Confirmed execution failure

`movie-pixels/err.log:26947`:

```text
Vulkan offscreen draw failed mrt=1 vs=0x0000001258E49600
size=3840x2160 format=9/0 textures=4 vertices=4:
System.DivideByZeroException: Attempted to divide by zero.
  at Silk.NET.Vulkan.Vk.CreateImage(...)
  at VulkanVideoPresenter.Presenter.CreateTextureResource(...)
  at VulkanVideoPresenter.Presenter.GetOrCreateCachedTextureResource(...)
  at VulkanVideoPresenter.Presenter.ResolveTextureResource(...)
  at VulkanVideoPresenter.Presenter.CreateTranslatedDrawResources(...)
  at VulkanVideoPresenter.Presenter.ExecuteOffscreenDrawCore(...)
```

115 such failures in `movie-pixels`, 124 in `movie-validation`. The same
exception and movie shader appear in **all six original movie captures**
(`movie1` first occurrence line 29119; `movie6` line 16177). This is not a
new readback failure. The catch discards the draw; an `agc.shader_draw` line
is not proof that the draw reached GPU execution.

`SHARPEMU_LOG_VK_RESOURCES=1` in `movie-validation` identifies the first
movie texture creation before the exception (`err.log:46676`):

```text
vk.texture addr=0x000000F41D228000 fmt=1 num=2 vk=R8Uscaled
size=3840x2160x1 type=9 row=3840 tile=0 layers=1 dst=0x204
bytes=8294400 expected=8294400
```

The other ring slots attempt the same format and fail too. The Y upload
bytes are dumped, but no movie target GPU readback is produced. Upload
dumps occur before `vkCreateImage` and are not GPU execution evidence.

### Host capability check

`vulkaninfo --show-formats` saved as `vulkan-formats.txt`. The RTX 5080
section identifies the device at line 467. Common Format Group[1], lines
2497–2552, includes `FORMAT_R8_USCALED` and `FORMAT_R8G8_USCALED`:

```text
linearTilingFeatures: None
optimalTilingFeatures: None
bufferFeatures: FORMAT_FEATURE_2_VERTEX_BUFFER_BIT
```

`VulkanVideoPresenter.GetTextureFormat` maps `(1,2)` to `R8Uscaled` and
`(3,2)` to `R8G8Uscaled`. `CreateTextureResource` passes those directly to
`vkCreateImage` with optimal tiling and sampled usage. It checks storage
support for optional storage usage, but does not validate sampled-image
support or the complete image creation combination. The first failure is
already the real Y plane; the two questionable extra descriptors are not
needed to explain it. UV has the same unsupported-format problem waiting
behind Y.

Khronos requires sampled-image format support for sampled views:
[format capabilities](https://docs.vulkan.org/guide/latest/formats.html).
The descriptor mapping should not be changed merely to evade this failure:
[AMD RDNA2 ISA table 47](https://www.amd.com/content/dam/amd/en/documents/radeon-tech-docs/instruction-set-architectures/rdna2-shader-instruction-set-architecture.pdf)
identifies unified format 3 as `8_USCALED`. Scaled and normalized values
have different numeric ranges; preserving the guest shader's inputs matters.

### Corrections to the previous direction

- The cited `writer=1693` is `movie6.err.log:3791`, `t=16.222`, before
  `decoder_started` at line 15993. It does not show a movie overwrite.
- The diagnostic field named `vs` / `ShaderAddress` is supplied with
  `translatedDraw.PixelShaderAddress` by AGC. The readback shader filter
  must use `0x1258E49600`, not vertex shader `0x1258E3AB00`. The first new
  capture (`movie-target`) used the wrong filter; `movie-pixels` corrected
  it. Both reproduce the same image creation exception.
- `SHARPEMU_VK_VALIDATION=1` reports the Khronos validation layer missing
  on this host. The capability evidence above comes from `vulkaninfo`,
  not a validation-layer report.

### Concrete handoff for Opus

1. Implement sampled scaled-format support with a supported host image
   representation and the required conversion. Preserve USCALED/SSCALED
   numeric semantics, component selection and sampler behavior. A blind
   USCALED-to-UNORM substitution or catch-and-ignore is not a fix.
2. Check the host format/image capabilities before creating images. Keep
   vertex attribute format handling separate: the host supports these
   scaled formats as vertex data, even though images are unsupported.
3. Add a small synthetic sampled-texture check with known values (e.g.
   unsigned 8-bit 0, 16, 128, 255), then verify in a controlled GT7 boot
   that the movie draws stop throwing and their GPU targets contain the
   decoded picture. Only then evaluate remaining composite/flip problems.

No claim that this will resolve every visual defect: text remains a separate
upstream traversal problem, and later movie rendering stages are still
unverified. No full test-suite rerun was needed for this read-only emulator
investigation; the Release build and live reproductions above were run.

## 6bk. Scaled texture formats are stored widened to float; the movie draw now reaches the screen

2026-09-14 (Opus), acting on the §6bj handoff. Emulator changed, tests added,
full suite green. Captures: `artifacts/gt7-opus-20260914/`.

### The fix

USCALED/SSCALED image formats deliver the stored integer to the shader as a
float **without normalising it**: a stored 200 samples as `200.0`, not
`200/255`. Vulkan makes no scaled format mandatory for images and this host
reports no image features for any of them, so `vkCreateImage` on `R8_USCALED`
faults inside the NVIDIA driver instead of failing — the `DivideByZeroException`
of §6bj.

SharpEmu now stores those textures widened to a float format that represents
every source integer exactly and filters the way the hardware does, so the value
the guest shader reads is unchanged and the shader needs no knowledge of the
descriptor's number format:

| guest format (`fmt`/`num`) | host image format |
| --- | --- |
| `1`/`2`,`3` (8) | `R16_SFLOAT` |
| `3`/`2`,`3` (8_8) | `R16G16_SFLOAT` |
| `10`/`2`,`3` (8_8_8_8) | `R16G16B16A16_SFLOAT` |
| `2`/`2`,`3` (16) | `R32_SFLOAT` |
| `5`/`2`,`3` (16_16) | `R32G32_SFLOAT` |
| `12`/`2`,`3` (16_16_16_16) | `R32G32B32A32_SFLOAT` |

Halves are exact for integers to 2048, so every 8-bit source value survives;
16-bit sources go to `float`. SSCALED sign-extends first. This is the same
shape as the existing `32_32_32` expansion (no three-component Vulkan format),
so the four upload sites now share one `PrepareUploadPixels`. The GPU detile
compute pass writes the guest's own bytes into the image, which is wrong once
the texels are widened, so widened formats take the CPU detile path.

Vertex attribute formats are untouched: the host does support scaled formats as
vertex data (§6bj), and `ToVkVertexFormat` is a separate table.

Not covered: the packed `2_10_10_10` scaled formats (`fmt` 8/9, `num` 2/3) are
still mapped to the scaled Vulkan format. They are vertex formats in practice.
Scaled **storage** images are also not handled — a shader `image_store` to one
would write floats where the guest expects the packed integer. Both now fail
with a named error rather than a driver fault, because `CreateTextureResource`
asks `vkGetPhysicalDeviceFormatProperties` for `SAMPLED_IMAGE` support before
creating any texture and throws with the guest format, number type and host
format in the message if the device reports none.

`src/SharpEmu.Libs/VideoOut/VulkanVideoPresenter.cs`;
tests `VulkanScaledTextureFormatTests` (widening table, unnormalised 8-bit
values, SSCALED sign extension, exact 16-bit values, untouched non-scaled
pixels). Suite: 1,109 passed, 0 failed.

### What the captures show

`scaled1`, `scaled2`, `scaled3`, `white1`, each a 170–180 s boot with
`SHARPEMU_AUTO_PAD='30:cross,36:cross,42:cross,48:cross'`.

- **No `DivideByZeroException`, no failed draw, in any run** (115 in the §6bj
  capture). The movie's planes are created as
  `addr=0xF41D228000 fmt=1 num=2 vk=R16Sfloat 3840x2160` and
  `addr=0xF41DA11000 fmt=3 num=2 vk=R16G16Sfloat 1920x1080`, three ring slots
  each, `bytes == expected`.
- **The movie output now reaches the screen.** In §6bj every swapchain dump
  across playback was pixel-identical to the sign-in dialog (centre
  `(24,25,29)` in all 15 dumps). Now the dialog appears in dumps 1–3 and from
  the dump immediately after the first plane upload every frame is
  `(0,95,0)` at both the corner and the centre, with the small UI elements
  still composited on top, and the dumps are no longer identical to each other.
- **The uploaded plane data is correct NV12.** `vk.texture_upload_contents`
  reports `center=10` for luma (Y=16, video black) and `center=8080` for chroma
  (U=V=128), `nonzero_bytes` full, later ring slots carrying real detail
  (`sample_unique` 43/78 vs 1 for the first).

### The next failure: the composite samples zero

Flat `(0,95,0)` is what a limited-range YUV→RGB matrix produces from
`Y=U=V=0`, not from the `Y=16, U=V=128` that was uploaded: with
`Y-16`/`U-128`/`V-128` and BT.709 coefficients, zero input gives
`R,B < 0 → 0` and `G ≈ 0.30..0.37`. The draw executes and its output reaches
the flip; the sample returns zeros.

Two leads, both from these captures:

1. **Addressing, not content.** Clamp-to-edge on this data would give black
   (edge texel 16), not green; a transparent-black **border** gives exactly
   zero. Check the sampler's address mode and border colour, and the
   composite's texture coordinates, against the `movie_bind_group` descriptors.
2. **The planes are uploaded once per ring slot and never again.**
   `vk.texture_refresh` appears **zero** times across a whole playback and only
   three luma creations occur, so the per-frame `memcpy` into the guest ring
   (§6bi) never reaches the host image. Even a correct sample would show one
   stale frame.

Ruled out by these runs: unsupported image formats (fixed), draw execution
(no failures logged), and "the plane data never arrives in guest memory" (the
upload contents trace reads real NV12 from it).

Also note for future captures: `SHARPEMU_VK_VALIDATION=1` is inert on this host
— the Khronos validation layer is not installed (§6bj) — so a clean log is not
evidence of a clean API use. `SHARPEMU_FORCE_WHITE_TEXTURE_TARGETS` only fires
in `CreateTextureResource`, so with three creations per playback it cannot
colour a frame a periodic swapchain dump will catch; that experiment was
inconclusive, not negative.

## 6bl. Corrections to 6bk, and what the movie shader actually does

2026-09-14 (Opus), after review. Two claims in §6bk were not supported by the
evidence behind them; both are retracted here. No emulator source change beyond
§6bk (a temporary input-scale probe was added, used, and reverted).
Captures: `artifacts/gt7-opus-20260914/{readback1,probe4b,probe4-readback,
probe4-readback2,shader1,videoout1}`.

### Retraction 1: "vk.texture_refresh fires zero times"

`vk.texture_refresh` goes through `TraceVulkanShader`, which is gated on
`SHARPEMU_LOG_AGC` or `SHARPEMU_LOG_AGC_SHADER`. The §6bk capture script set
neither, so the trace was disabled, not silent. With
`SHARPEMU_LOG_AGC_SHADER=1` (positive control: 105,709 `vk.texture_variant_hit`
and 78,265 `vk.offscreen_draw` lines in the same log), the playback shows
**177 refreshes**, all six movie planes, at the widened sizes
(`bytes=16588800` luma, `8294400` chroma):

| plane | refreshes |
| --- | --- |
| `0xF41D228000`, `0xF41DE06800`, `0xF41E9E5000` (Y) | 28, 29, 29 |
| `0xF41DA11000`, `0xF41E5EF800`, `0xF41F1CE000` (UV) | 31, 30, 30 |

Three creations in §6bk established resource reuse, nothing more. What is true
is that ~188 plane binds over ~130 s of playback is far below the movie's
59.94 fps, so the sampled content is often several frames old — a question
worth its own measurement, not a conclusion. `ComputeSparseGuestContentProbe`
sampling 64 bytes at the start, midpoint and end of a tightly packed plane is a
plausible cause and is untested.

### Retraction 2: "the composite samples zero; suspect the sampler border"

That was inferred from CPU-side bytes, which say nothing about the image the
GPU sampled. Reading the images back **from the GPU** shows the upload is
correct end to end:

| GPU readback | content |
| --- | --- |
| `0xF41DA11000` UV, `R16G16Sfloat` | every texel exactly `128.0` (NV12 neutral chroma) |
| `0xF41E5EF800` UV | `118.0 … 153.0`, mean `129.4` |
| `0xF41F1CE000` UV | `110.0 … 171.0`, mean `129.6` |

So the widening, staging, copy and view are right, and the sampled image holds
the unnormalised values the hardware would deliver. No border-colour or
addressing conclusion is warranted.

### Where the green is produced

The movie's 3840x2160 render targets `0x1C00000`, `0x3BE0000`, `0x5BC0000` are
the guest's **display buffers** (`videoout.register_buffers handle=1 group=0
count=3 … fmt=0x8100000022000000 3840x2160 pitch=3840`, a 10-bit
2R10G10B10A2 format that is *not* the Bt2100 PQ variant), and
`Vulkan VideoOut presented guest frame: image=0x0000000005BC0000` confirms one
of them is what reaches the screen. Read back mid-playback they are already
green — `R` mean 1.7–2.6, `G` mean ~380/1023, `B` mean 1.7–2.7, with real
spatial variation (`G` 140…575). The composite and flip are therefore not the
problem: the movie's own draw writes green.

### What the movie pixel shader does

`SHARPEMU_DUMP_SPIRV=1` + `SHARPEMU_DUMP_SPIRV_ADDRESS=0x1258E49600` dumps the
guest ISA (`artifacts/gt7-opus-20260914/shader1/shader-dumps`). The YUV block:

```
0x0064 ImageSample v0 <- v2,s0,s16   Dmask=1     ; luma
0x0070 VMulF32   v4 <- 0x3A800000,v0             ; v4 = Y * 1/1024
0x0078 ImageSample v0 <- v2,s8,s20   Dmask=3     ; chroma (U,V)
0x00B0 VMadF32   v3 <- 0x3A800000,v0,src[241]    ; U/1024 - 0.5
0x00BC VMadF32   v1 <- 0x3A800000,v1,src[241]    ; V/1024 - 0.5
0x00C8 VMadMkF32 v5 <- v3,0xBE28809D,v4          ; -0.164556
0x00D0 VMadMkF32 v0 <- v1,0x3FBCBFB1,v4          ; R = Y + 1.4746 V
0x00D8 VMadMkF32 v3 <- v3,0x3FF0D1B7,v4          ; B = Y + 1.8814 U
0x00E0 VMadMkF32 v1 <- v1,0xBF124433,v5          ; G = Y - 0.1646 U - 0.5713 V
```

Those are the **BT.2020** non-constant-luminance coefficients, and the
Log/Mul/Exp/Rcp sequence that follows carries the PQ constants
(`0x4196D000` = 18.8515625 = c2, `s8 = 0xC1958000` = -18.6875 = -c3,
`0x3C4FCDAC` = 1/78.84375 = 1/m2, `0x40C8E06B` = 6.2777 = 1/m1) — the **PQ
EOTF**. The `1/1024` scaling means this path expects **10-bit** YCbCr samples.

SharpEmu delivers 8-bit: `FfmpegMediaStream.ConvertVideoFrame` converts every
decoded frame to `AV_PIX_FMT_NV12` unconditionally, and the frame info reports
`luma_bit_depth = chroma_bit_depth = 8` (offsets 64/65 of `AvPlayerFrameInfoEx`,
which match shadPS4's `AvPlayerVideoEx`). The asset itself is 8-bit: `ffprobe`
on the extracted `/movie/oped/psstudio.mp4` reports `h264 High, 3840x2160,
yuvj420p, bits_per_raw_sample=8, color_range=pc, bt709`.

**Unresolved, and deliberately not concluded:** an 8-bit sample through a
`/1024` + BT.2020 + PQ path would land near the observed green, but a temporary
probe that multiplied every widened texel by 4 (10-bit scaling) changed the
presented frame and the render target by nothing at all (`G` mean 380.1/380.2
against 380.1/380.7 unprobed). Either the probe never reached the shader — its
activation was **not** confirmed, and one earlier attempt was silently wiped by
the capture script's `SHARPEMU_*` reset — or the sampled planes do not drive the
visible output. The second is live: at `0x0060` the shader takes `SCbranchVccz`
on a **uniform constant-buffer value** (`SBufferLoadDwordx2 s32,s33 <- s24`,
then `s32 > 0.5`) that skips the whole YUV block, so this 75 KB shader may be
rendering an entirely different path.

### Next

1. Confirm probe delivery before trusting a probe result: log once when the
   scale is active, and pass it by a name the capture script's
   `Get-ChildItem Env:SHARPEMU_* | Remove-Item` does not clear (that reset ate
   the first attempt; `SHARPEMU_FORCE_WHITE_TEXTURE_TARGETS` survived only
   because it was passed under a different name).
2. Decide the branch: read the constant buffer behind `s24` at offset 0 during
   playback, or decompile the branch target at `0x0060 + 0x467*4`. Until that
   is known, nothing about the YUV block explains the screen.
3. Only then weigh 10-bit delivery. Note the asset is 8-bit SDR BT.709
   full-range, so "AvPlayer should hand over P010" is a hypothesis about what
   the title's HDR path expects, not an established requirement.
4. Unrelated but real, found while reading the layout: SharpEmu never writes
   `video_full_range_flag` (offset 66) and this asset is full-range (`pc`).

### Caveats on the §6bk fix itself

`RequireSampledImageSupport` covers `CreateTextureResource` and one feature bit
(`SAMPLED_IMAGE`); other `vkCreateImage` call sites and the rest of the creation
parameters are unchecked. `VulkanScaledTextureFormatTests` covers the conversion
bytes, not GPU sampling. Host capabilities, read first-hand from `vulkaninfo` on
the RTX 5080 (616.86):

| format | optimalTiling | bufferFeatures |
| --- | --- | --- |
| `R8_USCALED`, `R8G8_USCALED`, `R8G8B8A8_USCALED`, `R16_USCALED`, `A2B10G10R10_USCALED_PACK32` | **none** | `VERTEX_BUFFER` |
| `R16_SFLOAT`, `R16G16_SFLOAT`, `R16G16B16A16_SFLOAT`, `R32_SFLOAT`, `R32G32_SFLOAT`, `R32G32B32A32_SFLOAT` | `SAMPLED_IMAGE` + `SAMPLED_IMAGE_FILTER_LINEAR` (+8-9 more) | texel buffer + vertex |

Every widening target is sampled *and* linearly filterable here, including the
32-bit float ones, so the choice in §6bk holds on this host. Vulkan guarantees
image features only for the formats in its mandatory format-feature table;
scaled formats are not among them, which is what makes a driver reporting
nothing for them legal.

## 6bm. The movie shader has both an 8-bit and a 10-bit path; 6bl corrected

2026-09-14 (Opus), after review. No emulator source change. Evidence is the same
dump as §6bl: `artifacts/gt7-opus-20260914/shader1/shader-dumps/
0000001258E49600-B782E9FEF15C1F04.ps.{ir.txt,spv}`.

### Retraction: "the branch at 0x0060 skips the YUV block"

It does not. `SCbranchVccz` at `0x0060` has literal `0x467`, so its target is
`0x0060 + 4 + 0x467*4` = **`0x1200`**, and `0x1200` samples the *same* two
resources again:

```
0x1200 ImageSample v4 <- v2,s0,s16   Dmask=1        ; luma, same s0/s16
0x1208 ImageSample v0 <- v2,s8,s20   Dmask=3        ; chroma, same s8/s20
0x1218 SBufferLoadDword s106 <- s24, offset 8       ; range flag
0x1224 VCmpGtF32 s106 <- s106, 0.5
0x1250 VCndmaskB32 v6 <- 0x80000000(-0.0), v5(-0.0625)   ; luma offset
0x1258 VCndmaskB32 v5 <- 1.0, 0x3F95A025(1.1689)         ; luma scale
0x1280 VMadMkF32 v4 <- v4,0x3B808081,v6             ; Y*(1/255) + offset
0x12C0 VMadF32   v0 <- 0x3B808081,v0,src[241]       ; U*(1/255) - 0.5
0x12CC VMadF32   v4 <- 0x3B808081,v1,src[241]       ; V*(1/255) - 0.5
0x12E0 VMadMkF32 v2 <- v0,0xBE5A6B51,v3             ; -0.2132
0x12E8 VMadMkF32 v1 <- v4,0x3FE57732,v3             ;  1.7927
0x12F0 VMadMkF32 v3 <- v0,0x40073190,v3             ;  2.1124
0x1300 VMadMkF32 v2 <- v4,0xBF086C22,v2             ; -0.5329
```

`0x3B808081` is `1/255`: this is the **8-bit** conversion path, with BT.709
coefficients and an explicit full/limited range selection (offset `-0.0` or
`-0.0625`, scale `1.0` or `1.1689`) taken from the constant buffer at `s24+8`.
The fall-through at `0x0064` is the 10-bit/PQ path of §6bl. The shader handles
both; `cb[0] > 0.5` at `0x0058`/`0x0060` selects between them.

So §6bl's "this path expects 10-bit YCbCr" is retracted as a statement about the
shader, and with it the suggestion that AvPlayer should deliver P010. **Leave
AvPlayer's bit depth alone.** The open question is which path runs and why its
output is green.

### Both paths survive translation, but the control flow is flattened

Scanning the translated SPIR-V for each path's signature constants finds each
exactly once — `1/255` and `1/1024`, `1.7927` and `1.4746`, `2.1124` and
`1.8814`, the range offset/scale pair, and the PQ constants. So the translator
did not drop a branch.

What it did do is flatten: the module has **942 `OpSelect`** against **2
`OpBranchConditional`**, 1 `OpLoopMerge`, 1 `OpSwitch` and 43 `OpLabel`. Both
YUV paths are therefore likely evaluated with the result selected afterwards,
which makes the *selector* the thing that decides the picture. A mis-derived
scalar condition — these comparisons write a scalar destination through SDWA
(`VCmpGtF32 … ScalarDestination = 106`) rather than VCC in the usual encoding —
would pick the wrong path's value while both computed correctly. Unverified;
this is the next thing to measure, not a conclusion.

### Retraction: "188 binds over 130 s proves the content is stale"

Wrong denominator. The movie's own duration is **8.558 s**
(`[AVPLAYER][INFO] source … 3840x2160 fps=59.940 duration_ms=8558`), not the
capture's 130 s, so the refresh counts cannot be divided by the run length.
Over one playback, 8.558 s at 59.94 fps is ~513 frames against 86 luma and 91
chroma refreshes, but nothing here correlates an upload with a frame timestamp,
and repeat/loop behaviour is unknown. Frame age is unestablished; measuring it
needs upload events correlated with `video_frame` timestamps.

### What the GPU readback does and does not prove

It proves the image at that address holds the right texels. It does not prove
the draw sampled *that* image, through the right view, with the right
coordinates, or that the descriptor the shader used points at it. Those stay
open and should be checked on whichever path the selector picks.

### Delivered range is limited, not full

§6bl noted the asset is `color_range=pc` and that SharpEmu never writes
`video_full_range_flag`. The flag must describe the pixels we hand over, not the
asset, and those are **limited range**: measured on the delivered planes, luma
spans `16 … 235` (`min 16` on the first slot, `max 127` then `235` as the logo
fades in) and chroma sits at `128` and spreads to `110 … 171`. swscale converted
full-range `yuvj420p` to limited-range NV12 (that is what its "deprecated pixel
format used, make sure you did set range correctly" warning is about). So the
zero we leave at offset 66 currently matches what we produce. The real
requirement is that the flag track the conversion, not the source.

### Next

1. Read the constant buffer behind `s24` during playback — `cb[0]` (path
   selector) and `cb[8]` (range) — and record which path the guest intends.
2. Verify the translated SPIR-V evaluates and selects that path, given the
   flattening above; check how the SDWA scalar-destination comparisons at
   `0x0058`, `0x0060`, `0x1224` and `0x1230` become SPIR-V conditions.
3. Trace the Y/UV sample results on the selected path before judging the
   descriptors, coordinates or the delivered bit depth.

## 6bn. Measured: the 8-bit block executes and its samples return zero

2026-09-14 (Opus). No emulator source change — this used the existing
`SHARPEMU_MARK_PIXEL_PCS` / `SHARPEMU_CAPTURE_PIXEL_VGPR_POINTS` hooks.
Captures: `artifacts/gt7-opus-20260914/{pathmark2,samples1}`.

### Retraction: "the SPIR-V is flattened, so both paths execute"

§6bm read 942 `OpSelect` against 2 `OpBranchConditional` as predication. It is
not. `Gen5SpirvTranslator` emits a **program-counter dispatcher**: one loop with
an `OpSwitch` on `_programCounter`, and a guest conditional branch becomes an
`OpSelect` choosing the *next block number*, not a select between two finished
colour values. Opcode counts say nothing about which path runs.

The companion lead is also weak: scalar destination 106 **is** VCC in this
translator — the SDWA comparison calls `StoreWaveMask(106, …)` and scalar-
register writes update `_vcc`. There is no missing SDWA-to-VCC connection to
find.

### How the two blocks were told apart

The pixel export at `0x1680` is compressed FP16: `v0` packs (R,G) and `v1` packs
(B,A), with the sources written just before at `0x1660` (`v4` → B), `0x1664`
(`v5` → G) and `0x1668` (`v0` → R). The hooks run **after** each instruction and
take **decimal** PCs, so:

```
SHARPEMU_CAPTURE_PIXEL_VGPR_ADDRESS=0x1258E49600
SHARPEMU_MARK_PIXEL_PCS=100:40,4608:41            # 0x0064 -> v40, 0x1200 -> v41
SHARPEMU_CAPTURE_PIXEL_VGPR_POINTS=
  100:0:42,4608:4:42,   # luma sample of whichever block ran -> v42
  5732:42:5,            # G = v42
  5736:40:0,            # R = 10-bit marker
  5728:41:4             # B = 8-bit marker
```

Reading the movie's render target back (`pathmark2`, seven dumps, all agreeing):

| channel | meaning | result |
| --- | --- | --- |
| R | marker set at `0x0064` (10-bit / PQ block) | mean **2.0/1023**, nothing above half |
| B | marker set at `0x1200` (8-bit / `1/255` block) | mean **1016/1023**, above half on **99.2%** of the surface |
| G | luma sample result of the block that ran | mean **2.1/1023** |

R and B in the same run are each other's control: an unset marker VGPR reads 0,
a set one reads 1.0. So **the 8-bit path executes and the 10-bit/PQ path does
not** — the selector picks correctly for 8-bit NV12 content, and nothing about
the delivered bit depth is implicated.

### What the samples return

`samples1` captured the chroma pair and the texture coordinate the same way
(`4616:0:43,4616:1:44,4608:2:45` then `5736:43:0,5728:44:4,5732:45:5`):

| channel | value | reading |
| --- | --- | --- |
| G — texcoord `v2` at `0x1200` | mean **508/1023**, max 1023, non-zero on 99.9% | a clean 0…1 gradient; coordinates are fine |
| R — U sample from `0x1208` | mean **1.2…1.6/1023** | ~0, not 128 |
| B — V sample from `0x1208` | mean **1.3…1.7/1023** | ~0, not 128 |

Luma reads ~0 as well (the `pathmark2` G column). Feeding zeros into the 8-bit
block reproduces the screen: with the limited-range selection
(`offset -0.0625`, `scale 1.1689`) and its BT.709 coefficients, `Y'=-0.073` and
`U=V=-0.5` give `R=-0.97 → 0`, `B=-1.13 → 0`, `G=+0.300`, against the ~0.37
observed once the remaining terms and the dither add are included. The green is
arithmetic on zero samples, measured on the block that actually runs.

### What this leaves

The plane images hold the right texels on the GPU (§6bl), the coordinates are a
valid 0…1 gradient, the correct block runs, and the samples still return zero.
That isolates the failure to **the resource the draw's descriptors actually bind
for `s0` and `s8`** — the image/view the sampler reads, not its contents. Prime
suspect from §6bi's `agc.movie_bind_group`: the four-entry group also carries a
`65534x65536 fmt3/num4` entry and a null `0x0 1x1 fmt10` entry, so a slot
mismatch would sample an empty resource exactly like this.

### Next

1. Dump the descriptor set the movie draw binds — image, view and slot for each
   of `s0` (luma) and `s8` (chroma) — and compare with the plane addresses
   `0xF41D228000` / `0xF41DA11000` (and their ring siblings).
2. If the slots are right, check the sampler object and the view's component
   mapping (`dst=0x204` luma, `0x22C` chroma) on the bound descriptor.
3. Ruled out by measurement, do not revisit: image content, texture
   coordinates, path selection, guest bit depth, the composite and the flip.

## 6bo. Driving the calibration wizard: lost key taps, a missing sign-in library, and two harness traps

2026-09-14 (Opus). Goal changed to reaching the main menu. `SHARPEMU_GUEST_ARGS=skip_op`
reaches the display-calibration wizard, so work moved there. Captures:
`artifacts/gt7-opus-20260914/{menu1,drive1..drive7}`.

### Tooling added

- `SHARPEMU_LOG_PAD=1` (`Pad/PadExports.cs`): one `[PAD][TRACE]` line whenever the
  button mask or window focus changes (with sticks and triggers), and guest read
  counts at 1/100/1000/... There was previously no way to tell "the key never
  reached the guest" from "the guest ignored it".
- `artifacts/gt7-opus-20260914/sendkeys.ps1`: real key presses via `keybd_event`
  to the focused SDL window (the path a user's keyboard takes). Supports combos
  (`stickright+square`) so a held stick shows in the trace line a button change
  emits.

### Bug 1 (fixed): short key taps never reached the guest

`SdlHostWindow.Run` calls `PumpEvents` once per frame, and `PumpEvents` drains
**every** queued event (`while (SDL_PollEvent(...))`) before `render`. A key-down
and key-up arriving between two drains are applied to `PressedKeys` back to back,
so the set is empty again before the guest samples the pad. Measured with
`SHARPEMU_LOG_PAD` on the wizard, window focused, guest reading the pad
(`guest_reads=1000`):

| press | trace |
| --- | --- |
| Cross held 150 ms | stays `buttons=0x0000` |
| Cross held 2 s | `buttons=0x4000`, then `0x0000` |

Real hardware cannot lose a tap this way, and any title that samples once a frame
loses them. Fix: `HostWindowInput` keeps a released key visible for 100 ms from
release (`ReleaseHoldUntilMs`), cleared on focus loss and disconnect. After the
fix a 120 ms tap produces `buttons=0x4000`. Tests: `HostWindowInputLatchTests`
(tap between samples, held key, focus loss drops the latch, untouched key).
Known ceiling, marked in the code: a title polling slower than 10 Hz could still
miss a tap; the upgrade is latching until a sample consumes the key.

Also verified: the left stick reaches the guest (`stickright+square` gives
`buttons=0x8000 lx=255`).

### Bug 2 (fixed): libSceSigninDialog did not exist

The wizard runs log `unresolved: nid=mlYGfmqE3fQ` (`sceSigninDialogInitialize`)
immediately followed by `LXlmS6PvJdU` (`sceSigninDialogTerminate`), twice per
boot, plus `Oad3rvY-NJQ` (`sceNpHasSignedUp`) three times. An unresolved import
returns `ORBIS_GEN2_ERROR_NOT_FOUND`, which no console returns, so GT7 took a
failure path the hardware never produces. There was no `SigninDialog` library at
all in `src/`.

Added `CommonDialog/SigninDialogExports.cs` (Initialize, Open, GetStatus,
UpdateStatus, GetResult, Close, Terminate) following the existing
`ErrorDialogExports` shape: with no PSN account to sign in to, an opened dialog
finishes at once and `GetResult` reports `USER_CANCELED`, the result a console
gives when the user backs out. Status values (0 none, 1 initialized, 3 finished),
result values (`OK=0`, `USER_CANCELED=1`) and error codes (`0x80B80003`
not-initialized, `0x80B80004` already-initialized, `0x80B80005` not-finished,
`0x80B8000D` arg-null) are the shared common-dialog ones, checked against
shadPS4 `system/commondialog.h`. `GetResult` writes only the leading `int32`
result; the rest of that structure's layout is not established and is left
untouched. `sceNpHasSignedUp` (`Np/NpManagerExports.cs`) writes `false` and
returns OK. Tests in `MissingGt7ExportsTests`. In the next boot all three NIDs
resolve (0 unresolved hits).

Still unresolved and not on this path: `sceNetResolverStartNtoa` (6 per boot),
VR tracker / HMD2 reprojection, TextToSpeech2, Share overlay,
`sceNpSessionSignalingCreateContext2`, `sceRudpInit`.

### Retraction: "the title stalls entering the brightness page"

It did not. `drive4`/`drive5` ran with `SHARPEMU_GUEST_IMAGE_DUMP_CONTINUOUS=1`.
In `RecordGuestImageBlit` a dump fires when
`!_tracedPresentedSwapchain || presentedCount % interval == 0`, and the continuous
flag clears `_tracedPresentedSwapchain` after each dump, so the first term is
true on **every** present and the interval is ignored: an 8 MB synchronous
readback per frame. `presented_fps` fell to ~8 (`drive4`) and ~1.2 (`drive5`).
At one guest frame per second the pad is sampled about once a second, so 200 ms
presses fell between samples and the wizard looked unresponsive. The same boot
without the flag runs the wizard at 22–28 fps. Do not use
`SHARPEMU_GUEST_IMAGE_DUMP_CONTINUOUS` while driving input; `SWAPCHAIN_DUMP_EVERY`
alone keeps periodic dumps.

### Harness trap: focus

The emulator reads the keyboard only while its window is focused (correct). In
`drive6` the pad trace shows `focus=False` three times and only one of six Cross
presses registered: focus was taken by other processes between keys.
`sendkeys.ps1` now re-takes and verifies foreground focus before every key and
prints `NOFOCUS <key>` instead of sending blind.

### Harness trap: arrow keys are extended keys

In `drive7` all 11 Cross presses but none of the three d-pad `right` presses
appeared in the pad trace. `keybd_event` without `KEYEVENTF_EXTENDEDKEY` sends
the numpad scancode for an arrow, which SDL does not report as the arrow key.
With the flag set, `right` produces `buttons=0x0020`. A physical keyboard sets the
flag, so this was never an emulator bug.

### Harness trap: swapchain dumps stop when the present branch changes

Periodic `SHARPEMU_SWAPCHAIN_DUMP_EVERY` dumps exist only in
`RecordGuestImageBlit`, the one branch that increments
`_presentedSwapchainCount`. In `drive7` dump 5 is log line 54848 and the first
Cross press is line 54856; no dump follows, while `presented_fps` stays at 45–50
and all 421 `vk.present_dropped` lines precede the press. After it, GT7's flips
no longer name a guest image, so frames take the CPU-upload or translated-draw
branch, which never dump. `artifacts/gt7-opus-20260914/capwin.ps1` grabs the
window's client area from the composited desktop instead, which sees every
branch — contrary to the README's note that window screenshots of the Vulkan
surface come out black or white, a `CopyFromScreen` of the client rectangle
shows the real frame.

### Wizard progress so far

Cross advances the wizard's pages (monitor graphic, then monitor with stand and a
second bar, then a larger monitor with an inner panel and a slider). Whether the
slider page accepts left/right is not yet established: every earlier attempt on
that page was made under one of the two harness problems above.

## 6bp. The wizard's render stall: a refused SNORM compute dispatch

2026-09-14 (Opus). Emulator changed, tests added, suite green (1,119 passed).
Captures: `artifacts/gt7-opus-20260914/{drive7,drive8}`.

### What drive7 actually showed

With the harness fixed (focus verified per key, extended arrows), 10 of 11 Cross
presses and the d-pad presses reached the guest. The first Cross on the wizard's
first page is log line 54856. After it:

- the last `[LOADER][PERF]` line is 60220 of 390,491. `ReportFrameRate` runs only
  on a guest flip or a completed present, so its silence means **neither happens**;
- the last `vk.render_work_enter` is line 61917 — GPU work stops too;
- the remaining ~328k lines are pad reads and imports: the guest is still
  executing. A live per-thread sample showed one thread at a full core
  (5,016 ms in 5 s) and every other thread waiting, and failing
  `sceFiberGetSelf` calls (`0x80590005`, not on a fiber) rose about tenfold —
  consistent with a thread spin-waiting on work that never completes.

This retracts two readings from §6bo drafts: the title did not "switch present
branch" (the stopped dumps and frozen desktop captures were simply no new frames),
and `capwin.ps1`'s `CopyFromScreen` capture is **not** reliable — two captures
4 s apart were identical down to the perf overlay's clock.

### The trigger

Between the last PERF and the end of render work:

```
61502 agc.wait_suspended queue=acb.compute[80] submission=4111 value=0 mask=all ref=1 cmp=3 form=agc-nop producer=none-observed
61503 agc.wait_suspended queue=acb.compute[81] submission=4112 ... ref=1 ... producer=none-observed
61572 agc.wait_suspended queue=dcb.graphics submission=4110 ... ref=1 ... producer=none-observed
61711 vk.compute_reject cs=0x1258C38D00 groups=53x38x1 reason=storage-binding[1]-invalid(
        addr=0x1238BA0000, reason=typed-format-mismatch(
        spirv=Rgba16Snorm/R16G16B16A16SNorm, guest=12/num=1, vk=R8G8B8A8Unorm))
```

Guest format 12 is 16_16_16_16 and number type 1 is SNORM; the shader declares
`Rgba16Snorm`. They agree. SharpEmu's `GetTextureFormat` had entries for `(12,0)`,
`(12,4)`, `(12,5)` and `(12,7)` but none for `(12,1)`, so the pair fell to the
table's `R8G8B8A8Unorm` default and `TryValidateStorageImageContract` refused a
correct binding. A refused dispatch is skipped entirely. Three waits for a value
to become 1, with no producer ever observed, suspend the graphics and compute
queues in the same window; that the skipped dispatch is the missing producer is
the working hypothesis, not yet shown. The same rejection appears in `drive3` and
`menu1`, so it is not a one-off.

### Fix

Compared with the vertex table, the texture table lacked SNORM for data formats
5 (16_16), 10 (8_8_8_8) and 12 (16_16_16_16). Added `(5,1) R16G16SNorm`,
`(10,1) R8G8B8A8SNorm`, `(12,1) R16G16B16A16SNorm`. The RTX 5080 reports all
three as sampled, linearly filterable, storage and colour-attachment capable
(`vulkaninfo`). `MetalGuestFormats`, documented as mirroring the Vulkan table
case for case, gained the same entries and the `Rg16Snorm=62`, `Rgba8Snorm=72`,
`Rgba16Snorm=112` members (Apple's numbering, each SNORM two above its UNORM,
matching the existing `R8Snorm`/`R16Snorm`/`Rg8Snorm`). Tests in
`VulkanScaledTextureFormatTests`: the exact GT7 contract (`Rgba16Snorm` against
`12/1`) and the three SNORM data formats.

Not changed: `GetRenderTargetFormat` and `TryDecodeRenderTargetFormat` also lack
SNORM entries, so a guest SNORM *render target* would still be refused. The
evidence here is a storage binding, which goes through the texture table; the
render-target gap is recorded, not widened without a failing case.

## 6bq. After the SNORM fix: a later stall on producerless cross-queue waits

2026-09-14 (Opus). No emulator change in this section. Capture:
`artifacts/gt7-opus-20260914/drive9` (inputs driven from a background task, with
the foreground-lock workaround in `sendkeys.ps1`).

### The SNORM fix removed the first stall

`verdict.sh drive9`: `compute_reject=0`; 12 Cross, 5 right and 2 left presses
registered. PERF continues to line 232,575 against a first Cross at 52,123 (in
`drive7` flips ended ~6,000 lines after the first Cross). The wizard advances to
the large monitor with the inner panel and slider (dumps 8 and 11).

### The second stall

PERF drops from ~19 fps to 3–4 fps at line 232,575; the last
`vk.render_work_enter` is 233,138 of ~297k. Every later press (Cross at
234–239k, d-pad right at 260–263k, Cross at 264–266k) arrives after flips have
ended. In the same window three fresh waits suspend and never clear:

```
232437 agc.wait_suspended label=0x1000CC2DE0 queue=acb.compute[81] submission=9821 value=0 ref=1 cmp=3 form=agc-nop producer=none-observed
232487 agc.wait_suspended label=0x1000CC2520 queue=dcb.graphics    submission=9819 ...
232728 agc.wait_suspended label=0x10015A6AD0 queue=acb.compute[80] submission=9830 ...
```

The periodic snapshot shows the render dispatch thread `RDisp` **Blocked** in
`scePthreadCondWait` after 1,053 imports; a live sample shows one host thread at
a full core and the rest waiting (the same shape as `drive7`).

### What does and does not release a wait

- 969 suspensions of exactly this form (`value=0 ref=1 cmp=3 agc-nop`, no
  producer) occurred steadily from line ~40k to ~230k and all cleared, so
  "producerless" is normal: those labels are written by something the producer
  tracker does not record.
- A producer is recorded only when a label-writing GPU packet is scheduled
  through `SubmitOrderedGpuSideEffect` (`release_mem`, `write_data`, rewind
  patch). A guest CPU write, or a shader writing the label, never registers.
- `MonitorGpuWaits` re-evaluates suspended waits on a backoff loop without
  needing a new submission, so these persist because the label genuinely never
  becomes 1.
- The deadlock breaker (`GpuWaitRegistry.CollectDeadlockBroken`) releases a waiter
  only by replaying a value a recorded producer wrote to that label; it never
  fabricates one. With no producer it cannot act.
- `agc.label_write_failed` — the path where a producer's write fails and the
  breaker is left without a value — occurs **0** times in `drive3`, `drive7`,
  `drive9` and `menu1`. The producers for the final labels were never scheduled,
  not scheduled and failed.
- `SHARPEMU_GPU_WAIT_FALLBACK_MS` only enables a one-shot `agc.wait_stale`
  diagnostic. `SHARPEMU_GPU_WAIT_MODE=force` fakes satisfying values at parse
  time and lets work run ahead of what it samples; at most a diagnostic, not a
  fix.

### Open

### The blocked render thread follows from the wait, not the other way round

`NotifySubmittedDcbCompleted` raises the end-of-pipe event for every completed
submission on every queue, delivered only to equeues that registered that ident
through `sceAgcDriverAddEqEvent` — hardware behaviour, on by default. It runs only
when parsing reaches the end of a submission. A DCB suspended on an unmet
WAIT_REG_MEM never gets there, so it raises no EOP, and a render thread waiting on
that completion (`RDisp` in `scePthreadCondWait`) never wakes. The opt-in
`SHARPEMU_AGC_SUBMIT_COMPLETION_EVENT` adds a broad fan-out to unmatched
registrations that the code itself calls "a compatibility guess rather than
hardware behavior"; it would mask this, not fix it.

### Ruled out as a fix for GT7: orphan force-submit (`drive10`)

`SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES=1` submits the unsubmitted tail of a closed
command-buffer arena — exactly a producer the guest built but never submitted — and
relaxes `==` waits to "reached or passed". It is off by default and documented as
validated only against Ghost of Yotei. With it on, during boot:

```
715 agc.orphan_preamble_force_submit header=0x80650E948 command=0x1000200174 dwords=9 targetLabel=0x0 owner=900001
716 agc.orphan_preamble_force_submit header=0x80650E948 command=0x10002002B0 dwords=9 targetLabel=0x0 owner=900002
717 VEH_AV first-chance at 0x801F94F2B ... NATIVE EXCEPTION 0xC0000005
      guest thread 'PGL Event', code at RIP 49 8B 46 48 (mov rax,[r14+0x48]) with r14=0
```

and the boot never presented (`presented_fps=0.2`, zero draws, 828 log lines at two
minutes). One run shows the crash following the forced submits, not that they
cause it, but a mechanism validated on another title that crashes GT7 at boot is
not a candidate fix.

### Tracing without changing the ordering

The destinations of scheduled label writes are logged only through `TraceAgc`,
i.e. full `SHARPEMU_LOG_AGC=1`. In `stall1` that trace grew the log ~1.17 MB/s and
dropped GT7 to 1.7–1.9 fps before any input (clean boots run 20–28 fps): too slow
to sample the pad reliably, and a cross-queue ordering problem may not reproduce at
that speed. Added `SHARPEMU_LOG_AGC_LABELS=1`, which logs only
`RegisterLabelProducer` — one `agc.label_producer` line per scheduled
`release_mem`/`write_data`/rewind-patch write, with queue, submission, destination
and action. In `stall2` it keeps 22–23 fps.

### How routine cross-queue waits resolve (`stall2`, before the stall)

With the label trace, an ordinary triple of suspensions at ~15 fps reads:

```
228487 agc.wait_suspended label=0x1000AC2B50 queue=acb.compute[80] submission=6881 ref=1
228497 agc.wait_suspended label=0x1000BEF760 queue=acb.compute[81] submission=6882 ref=1
228516 agc.label_producer queue=dcb.graphics submission=6880 dst=0x1000BEF760 action='release_mem ... data=1'
228542 agc.label_producer queue=dcb.graphics submission=6880 dst=0x1000AC2B50 action='release_mem ... data=1'
228572 agc.wait_suspended label=0x1000BEEEA0 queue=dcb.graphics submission=6880 ref=1
228591 agc.label_producer queue=acb.compute[80] submission=6881 dst=0x1000BEEEA0 action='release_mem ... data=1'
```

Graphics and compute[80] each wait on a label the other writes. It resolves only
because graphics reaches its writes (`…BEF760`, `…AC2B50`) before suspending on
`…BEEEA0`, and compute[80], resumed by that write, then reaches its write of
`…BEEEA0`. Every one of these was logged as `producer=none-observed` in `drive9`
simply because the producer is scheduled *after* the wait registers — so that
field does not mean "no producer exists". If both queues suspend before reaching
their writes, neither write is ever scheduled and both wait forever, which fits
the three waits that never clear at the stall.

### `stall2`: the stall is a job-system livelock, and one event thread freezes

`stall2` (short drive, `SHARPEMU_LOG_AGC_LABELS=1`) reproduced the stall — PERF
and render work end at line ~401,450 — but **no wait is wedged**: nothing suspends
after line 399,614, and submissions 10500–10509 (graphics, compute[80], compute[81],
compute[42]) all complete with normal render work and label writes. The guest
simply stops submitting.

The snapshot first suggested a lost semaphore wake: `Job#0` Running on
`sceKernelSignalSema(id=3, count=5)` with `Job#1`–`Job#5` Blocked in
`sceKernelWaitSema(id=3)` — identical in `drive9`. It is not. Import counts across
the periodic snapshots after the stall:

| snapshot line | RDisp | PGL Event | PCL Event | Job#0 | Job#1 | Job#2 | Job#5 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 403,400 | 961 | 220,242 | 29,966 | 12,253,394 | 13,191,839 | 12,582,911 | 8,845,596 |
| 426,662 | 999 | 258,141 | 29,966 | 14,929,435 | 14,715,979 | 13,835,963 | 9,864,241 |
| 449,772 | 1,037 | 296,052 | 29,966 | 17,600,029 | 16,047,267 | 14,946,793 | 10,829,895 |
| 473,071 | 1,075 | 333,993 | 29,966 | 20,273,001 | 17,180,238 | 15,978,794 | 11,766,824 |

The workers gain 1.0–1.5 M imports per 30 s: they wake, find nothing, and re-block
while `Job#0` keeps kicking them. `ShouldLogImportResult` logs every unexpected
negative result without sampling, and `sceKernelSignalSema` has no result lines, so
it never failed. `RDisp` (+38 per 30 s) and `PGL Event` (~1,260 waits/s) are cycling
too. **The only frozen thread is `PCL Event`**: 1 import at the first snapshot,
29,966 at line 403,400 just after the stall, and exactly 29,966 ever after — blocked
in `sceKernelWaitEqueue` on equeue handle 4 (`PGL Event` uses 3, `HMD Event` 5),
waiting for an event that stopped arriving. The job system's spin and `RDisp`'s
repeated wake-ups are consistent with a frame waiting on that event thread.

### `stall3`: equeue 4 is compute[42]'s completion queue — and tracing it hides the stall

`stall3` repeats the same short drive with `SHARPEMU_LOG_EQUEUE=1` as the only
change. Equeue handles 2–5 are all created by one thread at boot, each with one
user event. Handle 4 shows `registrations=4` on nearly every line, and every event
it receives is

```
equeue.wake: handle=0x…04 … thread='SharpEmu Vulkan VideoOut' source=trigger ident=0x2A filter=-14
```

— an AGC end-of-pipe event (filter −14) with ident 42. `DriverSubmitAcb` names a
compute queue `acb.compute[{ownerHandle}]` and sets `CompletionEventId = ownerHandle`,
so equeue 4 is where `PCL Event` waits for **`acb.compute[42]`** completions. In
`stall2` the last submission before everything stopped was compute[42] #10509.

Completions are not raised inline: `NotifySubmittedDcbCompleted` queues them with
`SubmitOrderedGuestAction` behind the submission's GPU work, and the render thread
runs the action once `TryMakeActiveGuestQueueSubmissionsCpuVisible` finds the target
fence complete (it defers after a 2 ms probe while the GPU is busy). That deferral
is bounded by GPU completion, so on its own it cannot strand an event.

**`stall3` did not stall.** PERF runs to line 504,957 of 507,107, render work to
507,037, and equeue 4 is still receiving compute[42] completions at the very end
(wake → resume → re-block at 507,029–507,032), at 16.8–20 fps, through submission
14,452 — past where `stall2` (~10,509) and `drive9` (~9,830) froze on the same
drive. The trace writes a line on every equeue wait, wake and resume, enough to
change thread interleaving, so the stall is timing-sensitive.

**Ruled out: a lost wakeup in the equeue wait.** The obvious window —
`sceKernelWaitEqueue` requests a *pending* block, and the thread is only registered
as blocked after the import returns, so `WakeBlockedThreads` (which needs `Blocked`
plus a registered continuation) cannot see it in between — is closed by the runner:
on a `Blocked` exit it sets `State = Blocked` and, under the scheduler gate, calls
`BlockWaiter.TryWake()`, readying the thread at once when an event is already queued
(`EqueueWaiter.TryWake` reserves it). The exception-delivery path re-checks the same
way. The equeue wait/wake path is sound as written.

Also ruled out as the reason a compute[42] completion never reaches equeue 4:

- **A dependency gate on the queued completion.** The render worker takes a
  queue's head only when its `RequiredSequence` has completed, but that value comes
  only from `GetGuestWorkDependencyLocked`, which is non-zero just for draws and
  computes binding a storage texture with an outstanding upload. Ordered actions
  (completion events) always carry 0.
- **Producer backpressure.** `EnqueueGuestWorkLocked` parks the enqueuing thread
  when the payload or sync item cap is reached, and logs `vk.guest_queue_backpressure`
  / `vk.guest_queue_starvation` unconditionally when it does. Neither line occurs in
  `stall2`, `drive9` or `stall3`.
- **Unbounded deferral.** An ordered action is deferred only while its target fence
  is still busy after a 2 ms probe and is retried on the next render tick.

Which queue's packet should write each of the three labels, and why it never
runs. The breaker's own documentation names "cross-queue GPU deadlocks the serial
parser cannot avoid": a suspended DCB blocks every later packet in it, including
label writes another queue is waiting for. Showing that needs the destinations
of the scheduled `release_mem`/`write_data` packets around submission 9819–9830.

## 6br. GPU completion interrupts were queued on the wrong logical queue

2026-09-14 (Opus). Emulator changed; suite green (1,119 passed). Captures:
`artifacts/gt7-opus-20260914/{stall4,fix1}`.

### Defect

`NotifySubmittedDcbCompleted` raises a submission's end-of-pipe event through
`GuestGpu.Current.SubmitOrderedGuestAction`, and its comment says why: the
notification must go "on that same logical queue", so it fires only after that
submission's Vulkan work and ordered guest-memory writes have finished. The queue
identity is ambient — `EnqueueGuestWorkLocked` files work under the thread-static
`_submittingGuestQueue`, falling back to `host.default` — and the only place that
sets it is the `EnterGuestQueue` scope inside `ParseSubmittedDcb`. All four callers
of `NotifySubmittedDcbCompleted` (AgcExports 4703, 4726, 7599, 7629) run after that
scope has been disposed.

So every completion interrupt was queued on `host.default`. There it is not ordered
behind its own queue's work, and `TryMakeActiveGuestQueueSubmissionsCpuVisible`
checks `host.default`'s fence timeline — which has nothing pending — so the
interrupt can fire before the work it reports.

### Evidence (`stall4`, `SHARPEMU_TRACE_ORDERED_ACTION_LATENCY=1`)

- All 3,737 `agc submit completion N` actions executed with `queue=host.default`.
- compute[42]'s last completions (submissions 3733 and 3737) ran 0.76 ms and
  0.35 ms after being queued — effectively immediately.
- `PCL Event` (equeue 4, compute[42]'s completion queue, §6bq) froze at 17 imports
  from line 105,383 on, with its events delivered. Together with `stall3` (the
  equeue trace hides the freeze), this points at an interrupt that arrives too
  early rather than one that never arrives.

That run froze sooner than `stall2` — the per-action trace slows the render worker
— which is itself consistent with an ordering race.

### Fix

`NotifySubmittedDcbCompleted` re-enters `EnterGuestQueue(queueName, submissionId)`
around `SubmitOrderedGuestAction`, so the interrupt is filed behind that queue's
work and gated on that queue's fence. One change covers all four call sites.
If a completion is raised while the render thread is executing a work item it is
still an immediate follow-up, now at the front of its own queue instead of
`host.default`.

Tests: the existing `AgcSubmitCompletionEventTests` (delivery under the owner-handle
ident, registration gating, graphics ident) still pass. No unit test pins the
queue: `GuestGpu.Current` has no test seam, no fake `IGuestGpuBackend` exists
(about 50 members), and without a presenter thread the action runs inline
regardless of queue. The runtime check is that the latency trace now names
`acb.compute[N]` / `dcb.graphics` instead of `host.default`.

### Result (`fix1`)

The clean wizard drive that froze in `drive9` (no heavy traces: pad log, periodic
snapshots) ran to completion with the fix: all 14 Cross and 6 d-pad presses
registered, PERF prints to the last lines of the log (399,439), render work
continues to the end, no compute rejects, and swapchain dumps advanced to #17
(`drive9` stopped at #11). No stall snapshot was emitted. This is the first run to
get through the whole scripted wizard drive without freezing.

The frames show the wizard completing: dumps 8–17 step through the calibration
pages, and the latest dump (#19) is a new screen — a horizontal carousel of
album-art tiles (a vinyl record, "VROOM", "Music", a sunset track image) on a dark
gradient, with page dots underneath. Textures render; text is still not drawn.

## 6bs. Race load hangs; compute queues stop submitting first

2026-09-14 (Opus). Diagnostics changed; suite green (1,119 passed). Captures:
`artifacts/gt7-opus-20260914/{fix1,race1,…,race12}`. Status: the deadlock's cause
is found and fixed (*Root cause and fix* below); the race still does not start.

### Symptom

With the §6br fix the menus are reachable. In `fix1` the user drove to the race
setup screen (Race of Turbo Sportscars, Suzuka) and pressed Start: the album-art
loading carousel appeared and never finished.

### Evidence (`fix1`)

- Race start is visible in the log: `set_prt_aperture id=1 base=0x3000000000`
  (line 449,509), then new threads `Strea`, five `WorkT` and a `SceLibc_Thr`.
- From the first snapshot after that, and in every one after, these never move:
  one `FWRKR` blocked in `scePthreadMutexLock` on `0x806DFAB08` (909 imports),
  `RDisp` in `scePthreadCondWait` on `0xE400BBADD0` (1,141), `Job#0` in
  `scePthreadCondWait` (18,920,762), `Job#1`–`Job#5` in `sceKernelWaitSema` id 3.
- The lock is in the eboot's data; the call returns into `libc.prx+0x5F59`
  (`libc.prx` base `0x808174000`), libc's shared mutex-lock wrapper, which the
  title calls for many different locks. That return address does not identify
  the subsystem.
- No faults, no unresolved imports, no `pthread_mutex_abandon`. The repeated
  negative-result warnings (`304ooNZxWDY`/`9wO9XrMsNhc` in NetExports,
  `p+zLIOg27zU` in FiberExports) run from boot and are not new.
- `PCL Event` (equeue 4) stopped at 48,291 imports around line 351,865 —
  about 100k lines **before** the race start, while the menus still worked.

### Evidence (`race2`, same build plus diagnostics below)

Driven by script along the `fix1` path (the pad trace is the route; see Driving).
Only the first race tile on the carousel is available on a fresh save: Cross on
other tiles does nothing. Moving to the first tile (left ×15) and pressing Cross
started the race load (line 2,485,465, same markers as `fix1`), and it hung.

- `PCL Event` stopped at 140,270 imports around line 1,016,112.
- Compute work stopped at the same point. Render work per logical queue:
  `acb.compute[42]` last at line 987,501 (submission 30943), `acb.compute[80]`
  last at line 987,677 (submission 30946), **zero** items on either afterwards;
  `dcb.graphics` kept running (451,341 items afterwards).
- `agc.wait_suspended`: 1,586 before line 1,016,112, none after.
- `Job#0` kept running (78.5M → 80.9M imports per 30 s); no thread was blocked on
  a mutex (85 live threads, 463 exited).

After the race load starts (snapshots at lines 2,492,620 and 2,503,524, identical):

- Three `FWRKR` threads wait in `scePthreadMutexLock` on `0x806DFAB08` with
  `owner=0x00000288A88A09C0('FWRKR') waiters=3/2/1`.
- The owner is not blocked on a lock: it is in `scePthreadCondWait` on a
  stack condvar `0x7FFFDBDFFBE8` (mutex `0x7FFFDBDFFBE0`), returning to
  `0x80077C823` — the same site idle workers wait at, but 0x1B0 deeper in its
  stack. It holds the shared lock while waiting for something to signal it.
- Frozen with it: `Job#0` in `scePthreadCondWait` (126,027,082 imports),
  `Job#1`–`5` in `sceKernelWaitSema` id 3, `RDisp` (2,670), `Strea` (4 imports) and
  all eight `WorkT` in `scePthreadCondWait`; `PCL Event` still at 140,270.

What the owner waits on (Ghidra, `gt7_text.bin` at base `0x800000000`; every one
of these return addresses is preceded by `call rel32` to the same thunk
`0x804192350` = `scePthreadCondWait`):

- `0x80077C7E0` is a generic auto-reset **event wait**: object layout is mutex at
  `+0`, condvar at `+8`, saved word at `+0x10`, `waiting` flag at `+0x18`,
  `signalled` flag at `+0x19`. It locks, returns immediately if `signalled`,
  else sets `waiting`, waits on the condvar, clears `waiting`, unlocks.
  The owner's `rdi`/`rsi` (`0x7FFFDBDFFBE8`/`0x7FFFDBDFFBE0`) put the event in its
  own stack frame, so it handed the event to other work and is waiting for it
  while holding `0x806DFAB08`.
- `0x8009BBCC0` (24 callers) is the matching **event signal**: locks, signals the
  condvar (thunk `0x804192370`) when `waiting` is set, otherwise sets `signalled`.
- Both sides hold the event's own mutex, so the guest pattern has no lost-wakeup
  window of its own.
- `Job#0`'s wait is inside `0x800776C00`, which builds a request on its stack and
  passes it to a manager global at `0x806DFBF18` — adjacent to the stuck lock,
  so `0x806DFAB08` is likely that manager's lock.

Two readings remain and the snapshots cannot separate them: (a) the owner waits on
work that never completes — plausibly work gated on the compute completions that
stopped earlier; (b) the HLE owner is stale because libc released the lock on a
userspace fast path the HLE never saw. `SHARPEMU_LOG_PTHREAD_MUTEX_FILTER=0x806DFAB08`
prints every HLE lock/unlock with the guest's lock words and settles (b).

So the §6br freeze of `PCL Event` still happens, only later (48k imports in
`fix1`, 140k in `race2`, 17 in `stall4`), and it coincides with the title no longer
submitting compute work. Whether the race-load deadlock follows from it is not
established.

### Evidence (`race3`: mutex trace on `0x806DFAB08`, AGC label trace)

`drive-race.ps1` reproduces the hang unattended (boot → wizard → carousel → first
tile → race load; `RACE LOAD STARTED: True`). The same shape: owner `FWRKR`
`0x28C27AD6F10` waiting at `0x80077C823`, three `FWRKR` queued on the lock, all
eight `WorkT` idle in their own event waits at `0x8008F3E98`, and `Strea` parked at
`0x80077C227` after only 4 imports — the work was never handed out.

- The lock trace shows normal traffic (19 `lock`, 41 `unlock`, plus block/reserve/
  resume) and then the owner acquiring by hand-off (`lock-resume`) and never
  unlocking. Reading (b) is dead: the HLE owner is real, not stale, and unlocks
  are not bypassing the HLE (95 locks vs 272 unlocks in `race2`'s log).
- `pthread_cond_wake_ungranted` (new, below) fired **zero** times, so no signal
  was delivered that the HLE then failed to hand the mutex back for. The guest
  was never signaled.
- The GPU is idle, not deadlocked: last `agc.wait_suspended` at line 458,420 with
  **none** after (race load begins at 667,848), and `agc.deadlock_break` never
  fired. Both compute queues stop at ~458k — before the carousel, while the menus
  still work — and never resume.

Correction to the `fix1`/`race2` reading above: `PCL Event` stopping is **not** a
lasting symptom. In `race3` it sat at 40,393 for three snapshots and then resumed,
reaching 121,545. Those pauses are idle periods; do not treat them as the defect.

### The call before the wait: `sceKernelVirtualQuery` answers NOT_FOUND

In `race4` the trace puts an unmistakable line immediately before each worker
parks on the event it never leaves:

```
Import#600093109 result: ORBIS_GEN2_ERROR_NOT_FOUND (rVjRvHJ0X6c)
  rdi=0x0000000009AA0000 rsi=0 rdx=0x00007FFFDB9FFB08 rcx=0x48 ret=0x8040A48C0
```

`rVjRvHJ0X6c` is `sceKernelVirtualQuery`, which SharpEmu does implement — this is
our own NOT_FOUND, not a missing export. The caller is `sub_8040A4820`
(entry found by prologue scan; the call-target heuristic mis-attributed it to
`0x8040A4500`):

```c
r = sceKernelVirtualQuery(addr, 0, &info, 0x48);
if (r == 0 && (info[32] & 2) && (info[24] & 0xC0))   /* direct memory, GPU bits */
      → queue the work (the path that eventually signals the event)
else if (size <= 0x1000) → small staging-buffer fallback
else → return 0, nothing queued
```

`info[32]` is the state byte this HLE writes (`0x02` = direct) and `info[24]` the
protection. So the title asks "is this buffer direct memory the GPU can see?", and
our NOT_FOUND sends every request larger than 4 KB down the path that queues
nothing — which is why the event is never signaled and the manager lock is held
forever. Both failing addresses (`0x09AA0000`, `0x00F25000`) appear nowhere else in
the log, and `MapDirectMemoryCore` maps at VA = direct offset when the title passes
no address, which is how addresses of that shape arise.

New diagnostic for the next run: `virtual_query_miss` (under
`SHARPEMU_LOG_DIRECT_MEMORY=1`) prints the queried address, the region count and
the next known region, alongside the existing `map_direct` / `map_flexible` traces.

### Ruled out (code)

- A deferred completion is not dropped. When `TryMakeActiveGuestQueueSubmissionsCpuVisible`
  cannot see the fence within 2 ms, `TryExecuteOrderedGuestAction` returns false,
  the render loop puts the item back with `RequeueGuestWorkFront`, and
  `TryTakeGuestWork` round-robins `_pendingGuestQueueSchedule`, so the next tick
  retries it. Starvation of a compute queue behind graphics does not follow from
  the scheduler.

### Diagnostics added

- Stall snapshot: exited guest threads are no longer listed (a count line
  replaces them) and the cap rose from 48 to 1024. With 48, the new loader threads
  in `fix1` were never printed (`... 458 more`); `race1` still showed `... 498 more`
  with only 2 exited, because ~550 threads are live during boot.
- `pthread_cond_wake_ungranted`: a signaled cond waiter that cannot re-acquire its
  mutex at wake time is logged with cond, mutex, thread and owner, under
  `SHARPEMU_LOG_PTHREAD_COND_WAKE=1` — about 124,000 benign `count=1` lines per
  boot, so it is opt-in.
  A thread snapshot cannot otherwise tell that state from "never signaled" — both
  read `block=pthread_cond_wait`.
- `SHARPEMU_LOG_PTHREAD_COND_SITE=<hex,…>` traces cond waits/signals by the guest
  **call site** (caller return address), because condvar addresses move between
  boots while the call site is fixed. For this hang: `0x80077C823` (event wait) and
  `0x8009BBCEA` (event signal, inside `0x8009BBCC0`).
- A cooperative `pthread_mutex_lock` block reason names the owner and waiter count:
  `block=pthread_mutex_lock owner=0x…('name') waiters=N`. It named the owner on
  the first hang it saw (`race2`, above).

### Separate crash (`race1`)

Before any race load: access violation writing `0x130845D8AC` (a free region) at
`0x8001E65A2` on `Job#0`, index `rax=0x2821396B` into base `0x1267C0F300`; last
import `sceKernelSignalSema`. Not seen in `fix1` or `race2`.

### Driving notes

- The calibration wizard's slider moves only with a held stick (`stickright`,
  1.5 s), not the d-pad; Cross then advances the page.
- Swapchain dumps are the only reliable way to see the screen (§6bn); `fix1`
  stopped dumping at #19, so its user-driven screens are not recorded.

### Root cause and fix (`race6`–`race12`)

Findings, in the order they settled the question. Handover version with the full
chain: [Appendix D](#appendix-d-gt7-race-load-hang-handover-2026-09-14-opus). Codex's live-stack follow-up, which
located the low values in the read requests: [Appendix E](#appendix-e-codex-follow-up-race-load-hang-2026-09-14).

- **The call before the wait.** `sub_8040A4820` asks `sceKernelVirtualQuery`
  whether the request's buffer is direct memory with GPU protection bits and, on
  NOT_FOUND, drops any submission over 4 KB; its caller `0x8040A00A0` waits on
  the event regardless. The buffers queried (`0x00F25000`, `0x09AA0000`,
  `0x0A867000`) are mapped by nothing, so NOT_FOUND is the correct answer.
- **Not the candidate writer.** An exec watch on `0x8040970E5` (the store in
  `0x8040970C0`) fired 22 times in `race6` with a high `rax` (`0xEC050DC000`) and
  never in `race7`, which still hung. The requests are built by `0x80057B8D0`,
  which zeroes `+0x258…+0x270`.
- **Offsets, not pointers.** Live reads (`ReadProcessMemory` only) of `race7` and
  `race9`: the low value sits at `+0x10` of a 0x30-byte descriptor (vtable
  `0x8058F30F0`, built by `sub_8005D16C0` from `sub_8005D19F0`). `sub_8005D19F0`
  is a free-list sub-allocator over an arena and returns offsets. The arena
  object (`race9`, `0xE41B5E2060`) has `+0x00 = 0` (base) and
  `+0x38 = 0x143C00000` (size). The read path is `crowd/amsx/0014.amsx`, and the
  three values are consecutive slices of it.
- **Why the base is 0.** The arena's `sceKernelAllocateDirectMemory(len=0x143C00000,
  align=0x200000)` gets TRY_AGAIN. `direct_memory_exhausted` in `race9`:
  `used=0x2CC044002 free=0x133FBBFFE largest_gap=0x132600000` of a 16 GiB pool.
- **Why the pool is short.** Reconstructing live allocations from the traces
  (`race11`): 369 blocks, 11.18 GiB, the largest `0x13000000+0x180800002`
  (6152 MiB) from return `0x800FEE9CB` with `alignment=0x6007FFE32`. Length and
  alignment are not multiples of 16 KiB; KytyPS5's `KernelAllocateDirectMemory`
  rejects that with EINVAL, and SharpEmu granted it.

**Fix** (`Libs/Kernel/KernelMemoryCompatExports.cs`):

- `sceKernelAllocateDirectMemory` and `sceKernelAllocateMainDirectMemory` return
  `ORBIS_GEN2_ERROR_INVALID_ARGUMENT` when the length, or a non-zero alignment, is
  not a multiple of 16 KiB.
- `DirectMemorySizeBytes` = 13,824 MiB − 448 MiB flexible = 13,376 MiB, KytyPS5's
  model, instead of 16,384 MiB. Alone (`race11`) this did not help: GT7 requests
  the same `0x143C00000` regardless and had only 1.9 GiB free. Untested: the
  validation fix on the old 16 GiB pool.

Suite green after each change. Regression test:
`AllocateDirectMemory_RejectsLengthOrAlignmentOffPageBoundary`.

**Upstream cause of the malformed call:** `sub_800FEE780` uses the `rax:rdx`
return of `sceHmd2ReprojectionQueryDisplayBufferSizeAlign` (`-C2nkoEYOnU`,
unresolved) unchecked as size and alignment. The unresolved import leaves
`rax = 0xFFFFFFFF80020002` and `rdx = 0x6007FFE32`, giving exactly
`len=0x180800002`. On hardware this allocation is valid. Detail in
[Appendix D](#root-cause).

**Result (`race12`):** the invalid call returns INVALID_ARGUMENT; the arena
allocates (`selected=0x14D600000`); `virtual_query_miss` 0; no exceptions; no
waiters on `0x806DFAB08`; `Strea` 30,611 imports (4 before). The race still does
not start: the carousel shows a spinner at 3.3 fps. The last snapshot has
`PCL Event`, `Job#1` and `Job#3` waiting on `0xE418577F88` (owner `Job#2`) and
`GPUex` on `0x80650D210` (owner `PCL Event`). `Job#2` keeps running (~0.5M imports
per snapshot) and `PCL Event` climbs, so this is contention rather than the old
deadlock. `Strea` was flat for the last seven snapshots. The session was closed
~7.5 minutes after the load began, so slow versus stalled is not established.

**Ruled out along the way:**

- Direct-memory leak: the title frees with `sceKernelCheckedReleaseDirectMemory`
  (82 calls in `race9`, none rejected). The earlier "zero releases" traced only
  the unchecked API.
- Allocator double-allocation: the first-fit search skips occupied ranges;
  repeated start offsets are re-allocations after those frees.
- Mapping failures: none in any run; arenas are mapped in 28 MiB sub-ranges.
- 32-bit truncation of a real pointer: no traced address has low 32 bits equal to
  a queried value. `0x09AA0000 == (0x29AA00000 & 0xFFFFFFFF) >> 4` held for one
  allocation only — coincidence.
- The caller-supplied request path (`0x800BDF656`, fixed size `0x218`) writes
  proper high pointers; it is not the source.

**Corrections to this section's own earlier statements:** the 5.06 GiB refusal
is the trigger (an interim note said it was harmless because 4.79 GiB succeeds
next), and `PCL Event` pausing is idle time, not a defect.

### After the fix: a crash, and aliased direct mappings (`race13`)

`race13` crashed ~1 minute into the race load: read of `0x1E` at `0x8000CB5D0`
(`Job#3`) in GT7's allocator `sub_8000CB0C0` — a lock-free tagged free list whose
head had become `0x1E0037`. `race13` had no direct-memory trace, so where that
list lives in `race13` is unknown (an earlier note borrowed a mapping from
`race12`, a different boot).

`race12`'s trace shows physical ranges mapped at two VAs concurrently (e.g.
physical `0x291000000` at `0x142C200000` and `0x30009A0000`, the latter in the
PRT aperture): 3,536 pairs, 3,421 after the arena allocation, 115 already at
boot. SharpEmu gives every mapping its own host memory
(`KernelVirtualRangeAllocator.TryReserve`; no shared sections in `src`; the PRT
range is lazily committed as zero pages), so the two views diverge. KytyPS5
shares backing (`GuestAddressSpace::MapBacking`, alias self-test). A correctness
bug; **not shown** to cause the spinner, the corruption or the stall.

**Withdrawn: "proven in `race14`".** `race14` passed the allocation
prerequisites (INVALID_ARGUMENT, arena `selected=0x14D400000`, 0 query misses)
but never reached the aliasing phase — 0 aperture mappings, 0 alias pairs after
the arena — and stalled in the **original** `0x806DFAB08` deadlock. The four
pairs read were boot-time pairs whose first view's VA had since been reused, and
the reads were not taken with the guest paused. Detail in
[Appendix D](#after-the-fix-aliased-direct-mappings-do-not-share-memory-race13-race12).

### Next for the race load

1. Implement `sceHmd2ReprojectionQueryDisplayBufferSizeAlign` from researched
   hardware values.
2. Keep host memory out of PS5 user space. `race14`'s deadlock is a failed
   `MAP_FIXED` of the streaming arena at `0x12E9800000`: the emulator main
   thread's host stack (`RSP=0x1340D7AB48`) was inside it. Detail and ruled-out
   options (upstream `0a461f8`/`f84246c`, high-entropy ASLR off) in
   [Appendix D](#race14s-deadlock-a-host-thread-stack-in-the-guests-fixed-window).
3. Shared backing for direct memory, host-level alias/unmap/partial-unmap tests
   first:
   [Fix design](#fix-design-shared-backing-for-direct-memory-not-started).

## 6bt. The reprojection size queries, and returning an aggregate in RAX:RDX

§6bs left the malformed 6 GiB direct-memory request treated but not cured: the
kernel now rejects it, but GT7 still builds it out of an unresolved import's
error code. Both queries behind it are implemented here.

### The caller, resolved

`sub_800FEE780` (Ghidra, `gt7_text.bin` @ `0x800000000`) is the display/VR init.
Each thunk below is pinned by the **return address** the loader logged, not by
its slot order, from `artifacts/gt7-codex-20260912/baseline.err.log`:

| ret | NID | export |
|---|---|---|
| — | — | `sceHmd2Initialize`, then `sceHmd2GetDeviceInformation(0x80650D4D0)` (both resolved) |
| `0x800FEE7DF` | `gF8+lvc7GuQ` | `sceHmd2GetFieldOfViewWithoutHandle(0x80650D4EC)` |
| `0x800FEE936` | `U-CnbmeyYaA` | `sceHmd2ReprojectionQueryBufferSizeAlign` |
| `0x800FEE97D` | `-C2nkoEYOnU` | `sceHmd2ReprojectionQueryDisplayBufferSizeAlign` |
| `0x800FEEA47` | `C0rPwER-yxg` | `sceHmd2ReprojectionInitialize(param, 0)` |
| `0x800FEEA8E` | `TwqZnaIjWv4` | VrTracker2 query-memory |
| `0x800FEEB1B` | `6Jy73SRfG-o` | VrTracker2 init |
| `0x800FEEB2A` | `UVCMLmS-Eas` | `sceVrTracker2SetCoordinateSystem(1)` |

Incidentally this confirms the `sceHmd2GetDeviceInformation` record bound: its
argument is `0x80650D4D0` and the next object is the FOV block at `0x80650D4EC`,
`0x1C` later, exactly the bound the export's comment already assumed.

The decompile shows both queries' results used without a status check:

- work query -> `sub_8000D12E0(size, align)`, the title's aligned heap allocator,
  guarded by a flag at `0x80650C858`;
- display query -> `sceKernelAllocateDirectMemory(0, sceKernelGetDirectMemorySize(),
  roundup(size, max(0x4000, align)), max(0x200000, align), 0xC, &phys)`, then
  `sceKernelMapDirectMemory(&va, len, prot=0x32, flags=0x400000, phys, align)`.
  `prot=0x32` is CPU read/write plus GPU read/write, so the display buffer is a
  GPU-visible surface.

Both pointers then go into a 72-byte `SceHmd2ReprojectionInitializeParam`
(`+0` work, `+8` display, `+0x20` type = 1, `+0x28` see-through = null) — the
layout KytyPS5 asserts — and `sceHmd2ReprojectionInitialize`'s own result is
discarded, so leaving *that* one unresolved changes nothing.

### The values

`sceHmd2ReprojectionQueryDisplayBufferSizeAlign` returns `0x2000000` / `0x10000`:
PSVR2's panel is a publicly documented 2000x2040 per eye, the buffer covers both
(4000x2040), and a 64 KiB-tiled render target at 4 bytes per pixel pads to whole
128x128-element blocks — 4096 x 2048 x 4 = 32 MiB, aligned to one block. The
panel geometry is public; **the format and tiling are inference** from what the
buffer is for. KytyPS5 computes the same surface through its own tiler and
arrives at the same pair, which is corroboration, not a measurement.

`sceHmd2ReprojectionQueryBufferSizeAlign` returns one 16 KiB page. This is
scratch the title allocates from its own heap for a reprojection engine SharpEmu
does not run; nothing is ever stored there, so the only requirement is that the
title can allocate it. The hardware figure is unknown. (KytyPS5 returns
`sizeof` its own state object here, which is the same kind of answer.)

`sceHmd2GetFieldOfViewWithoutHandle` is still unresolved; its failure only skips
GT7's FOV maths.

### Returning a 16-byte aggregate

Both queries return `{ size, alignment }`, which SysV splits across RAX:RDX, and
an HLE handler's `int` result cannot express the second half. Writing
`ctx[CpuRegister.Rdx]` did not work either: the import trampoline pushes the
guest GPRs as its argument pack and its epilogue **pops RDX back** from
`argPack+16` (the same slot the gateway reads argument 3 from), so any RDX a
handler set was discarded on the way out.

`CpuContext.SetReturnPair(first, second)` now flags that case, and
`StoreImportPairReturn` writes the second eightbyte into `argPack+16` next to
the existing vector-return writeback, on both dispatch paths.
`ClearRaxWriteFlag` resets the new flag with the old one, so it cannot leak into
the next import on the same thread.

### Verified

`dotnet build` clean; suite green (1,136 tests) with
`tests/SharpEmu.Libs.Tests/Hmd2/Hmd2ExportsTests.cs` added — the returned pair,
the pair flag, and the property that actually matters: the length and alignment
GT7 derives from the display pair are both multiples of the 16 KiB direct-memory
page, so the §6bs validation accepts the call.

**Not verified:** no GT7 run. The RDX writeback is argued from the trampoline's
own push/pop sequence, not observed reaching guest code, and there is no
end-to-end import-dispatch harness in the suite to assert it. The next race-load
run should show the allocation at return `0x800FEE9CB` succeeding at 32 MiB
instead of being rejected.

## 6bu. The race load streams: the missing AMPR gather/scatter command

With §6bt's allocation fixed, `race20` reached the race load cleanly — 5.06 GiB
arena allocated *and mapped*, `direct_memory_exhausted` 0, `TRY_AGAIN` 0,
`virtual_query_miss` 0, no waiters on `0x806DFAB08` — and still stalled, with
`Strea` frozen at 5,584 imports in `pthread_cond_wait` (return `0x80077C823`,
the documented event wait) across three snapshots.

### The cause: one unresolved export

The unresolved-import census of that run had one entry that mattered:
**799 calls to `sceAmprMeasureCommandSizeReadFileGatherScatter`**
(`DXmgc5op8Yw`, `libSceAmpr`), all returning `ORBIS_GEN2_ERROR_NOT_FOUND`.

`sub_8040A4ED0` (entry found by padding+prologue; the call-target list
mis-attributes this one) is GT7's streaming emit loop:

```
size   = measureReadFileGatherScatter(dst, size, offset)   // ret 0x8040A4F47
offset = commandBuffer.getCurrentOffset()
if (bufferSize < offset + size + reserve) { submit(); getFreshBuffer(); continue; }
else { readFileGatherScatter(buffer, dst, size, offset); ... }
```

The free-space test is **unsigned**. With the sign-extended `NOT_FOUND` in
`size` the sum is enormous, the test always says "full", and the loop submits
the buffer and asks for a new one forever without ever emitting the read. The
streaming thread then waits on a completion event that nothing can signal. This
is the §"unresolved import returns NOT_FOUND, not 0" trap in its purest form:
one absent export, an entire subsystem silently dead.

### The ABI, from the title's own wrappers

The public symbol catalogue gives the signature —
`sce::Ampr::MeasureAprCommandSize::readFileGatherScatter(void*, size_t, size_t)`
— and the observed arguments match it exactly (`rdi` a PRT-aperture
destination, `rsi` `0x10000`, `rdx` the file offset, with
`rdi == 0x3000000000 + rdx` on every sample).

The command's own ABI was **not** guessed: GT7 reaches it through a wrapper at
`0x801923630` that shuffles registers and tail-jumps into the import, and the
disassembly states the layout outright:

```
49 89 c9        mov r9, rcx        ; file offset
49 89 d0        mov r8, rdx        ; size
48 89 f1        mov rcx, rsi       ; destination
48 8d 77 18     lea rsi, [rdi+0x18]  ; visible command-buffer pointers,
48 8d 57 20     lea rdx, [rdi+0x20]  ; not arguments
e9 ..           jmp  <import>
```

So `sceAmprAprCommandBufferReadFileGatherScatter(buffer, -, -, dst, size,
offset)` — the same shape as the plain `ReadFile` minus the file id, which
shifts the remaining arguments down one register each. The identical wrapper for
plain `ReadFile` sits at `0x8019235E0` and confirms the convention.

The tail-jump is why both call sites show the *caller's* return address in the
ampr trace (`ret=0x8040A50D0` for gather/scatter, `ret=0x801923604` for
`ReadFile`); the wrapper leaves no frame of its own.

**No file id** is the point of the call: it continues the file the buffer's last
`ReadFile` selected, which is what `resetGatherScatterState` exists to clear.
SharpEmu models that as `GatherScatterFileId` on the command-buffer state.

### Change (`Libs/Ampr/AmprExports.cs`)

- `sceAmprMeasureCommandSizeReadFileGatherScatter` returns `ReadFileRecordSize`.
- `sceAmprAprCommandBufferReadFileGatherScatter` reads the selected file at
  `(dst, size, offset)` and appends the same record a `ReadFile` does.
- `sceAmprAprCommandBufferResetGatherScatterState` clears the selection and its
  measure reports 0 (it writes no command).
- `sceAmprAprCommandBufferReadFile` now records the file it selected.

Tests: `tests/SharpEmu.Libs.Tests/Ampr/AmprGatherScatterTests.cs` — the measure's
size, a gather/scatter read continuing the previous `ReadFile`'s file with the
right bytes and record, the no-file-selected error, and reset dropping the
selection. Suite green, 1,140 tests.

### Result (`race22`, `SHARPEMU_LOG_AMPR=1`)

- The measure resolves: 444 calls, no unresolved AMPR imports left.
- **2,572 gather/scatter reads, every one full-size** (`read == size`), correct
  file, sensible offsets. First run in which this path transfers any bytes.
- An early attempt mapped the arguments as `(rsi, rdx, rcx)` and produced 405
  zero-byte reads with the visible-pointer addresses as destination and size —
  worth recording because the trace looked plausible until the destination and
  size turned out to be `buffer+0x18` and `buffer+0x20`.

## 6bv. Next blocker: a null GPU descriptor at `0x806973F20`

With streaming working the load runs further and then **crashes**, identically in
`race19` (HMD fix only) and `race22`: access violation reading `0x4` at guest
`0x800A06D15` on `Job#0`.

```
49 8b 4c 24 20   mov rcx,[r12+0x20]     ; r12 = 0x806973F00, loads NULL
48 8b 05 ..      mov rax,[rip+..]
8b 71 04         mov esi,[rcx+4]        ; faults
```

The C is
`func_0x8002E7E20(cmdbuf, *(uint*)(*(long*)(obj+0x20) + 4) * 0x40 + *(long*)(lRam805FF3420 + 0xA8))`
— an index into a descriptor table — so `[0x806973F20]` is a descriptor pointer
that should have been set.

What is established:

- `sub_800A06029` is **not** a function: it is a label inside `sub_800A05650`
  (Ghidra's decompile of it reads `unaff_RBP`/`unaff_RBX`, and the crash's
  frame#1 return `0x8028325CC` is exactly the caller the exec watch reports for
  `sub_800A05650`).
- `sub_800A05650` does run, often: `SHARPEMU_WATCH_GUEST_EXEC=800A05650` in
  `race24` logged 768+ hits, always from caller `0x8028325CC`, on the Job
  threads.
- A rip-relative scan of the text finds **exactly one** writer of
  `0x806973F20`: `mov [rip+disp], rax` at `0x800A06333` — *later in the same
  function* than the `sub_800A06BB0(0x806973F00)` flush call that faults. So on
  the faulting pass the field has not been written yet.
- `SHARPEMU_WATCH_GUEST_WRITE=806973F20:8` (`race23`) armed and **never fired**,
  in a run that did not reach the streaming stage. That is consistent with the
  gate below rejecting every pass, but it is not proof: the watch covers
  registered guest threads only.
- `sub_800A05650` early-exits unless all of: the `param_2` range is non-empty
  and at most 15 entries, `*(char*)(ctx+0xFC) != 0`, `*(char*)(ctx+0xFD) == 0`,
  and `*(char*)(ctx+0xB0) == 1`, where `ctx = *(param_1+8)`. The write at
  `0x800A06333` is inside that gate.

> **Correction (2026-09-15).** The execute watch used here never set the x86
> resume flag, so every thread that reached `0x800A05650` re-trapped on its first
> arrival for the rest of the run. `race24` and `race26` logged exactly **6
> distinct `rdi` values across 6 job threads**, each constant per thread (see
> [Appendix F](#race61-livelocked-execute-watches-never-set-the-resume-flag)). "Runs often", "768+ hits",
> "0 on all 1,536 entries" and "a different address on nearly every hit"
> therefore describe **6 first arrivals** repeated by the sampler, not 1,536
> entries. The gate reading below rests on those 6 samples, and those runs froze
> six Job threads after the first hit. The fix (`EFlags |= RF` on execute-watch
> traps) is in `DirectExecutionBackend.WriteWatch.cs`.

### The gate condition, measured

`SHARPEMU_WATCH_GUEST_EXEC_PROBE` (added for this, below) read the gate's own
inputs at every entry to `sub_800A05650` in `race26`:

```
guest-exec-watch hit#1536 rip=0x800A05650 caller=0x8028325CC rdi=0xEC05636438
  [rdi+8]=0xE416EE1C70  [[rdi+8]]+F8=0x0000000000000000  [[rdi+8]]+B0=0x...
```

`ctx = *(rdi+8)` is a **per-item** object (a different address on nearly every
hit), and the byte the gate tests, `ctx+0xFC`, read **0 on all 1,536 entries**.
So `sub_800A05650` takes its early exit every time, the guarded block never
runs, and the whole `0x806973F00` static is never constructed. Confirmed
independently: `SHARPEMU_LOG_GUARDS=1` in `race25` logged 9,579 guard
operations and **not one** for this object's guard `0x806973F78`.

That closes the chain:

1. `ctx+0xFC` is 0 on every item, so the gate rejects.
2. The C++ function-local static at `0x806973F00..F78` — `__cxa_guard_acquire`
   at `0x804191930`, `__cxa_atexit` at `0x804191920`, release at `0x804191950`,
   with the initialiser writing `+0x20` at `0x800A06333` — is therefore never
   constructed, so `0x806973F20` stays NULL **and so do `+0x50`/`+0x58`**.
3. `sub_800A06BB0(0x806973F00)` is called precisely when
   `lRam806973F50 == lRam806973F58`, which an unconstructed static satisfies
   trivially (both zero).
4. It dereferences `+0x20` and faults.

So the crash is a *consequence*, not a cause: on hardware some item has
`ctx+0xFC` set, the static is built on that pass, and the flush is safe.

**Do not "fix" this by null-checking anything in the emulator** — the faulting
code is the title's. The open question is what sets `ctx+0xFC`, i.e. which piece
of per-item state SharpEmu never produces. `ctx` is reached as `*(rdi+8)` from
the job context the caller `0x8028325CC` passes; the object is allocated in the
guest heap around `0xE416Exxxxx` and is not a singleton.

Also still a lead rather than a fact: the rip-relative scan found one writer of
`0x806973F20`, but it cannot see `mov [rax+0x20], rcx`, so a pointer-based
writer elsewhere is not excluded.

### `SHARPEMU_WATCH_GUEST_EXEC_PROBE`

`SHARPEMU_WATCH_GUEST_EXEC_PROBE=<spec>[,<spec>...]` reads guest memory at each
execute-watch hit and appends the values to the hit line. Specs use the existing
deref grammar and may start from a register in the trapped frame, so
`[rdi+8],[[rdi+8]]+F8` follows two hops off an argument. Registers alone answer
"was this called"; a gate that rejects on a flag two hops out needs the flag,
and resolving each hop between runs costs a boot because the objects move.

## 6bw. Past the descriptor crash: the carousel walk, and a garbage pointer in physics

Two corrections to §6bv, both from runs that actually reached the load.

**The gate does pass.** §6bv concluded `ctx+0xFC` is never set. That was measured
in a run that never started the race load. Watching the first instruction past
the gate (`0x800A056FB`, found by disassembling the gate rather than reading the
decompiler's flattened conditions) shows **105 hits** once the load runs. So the
guarded static *is* constructible, and the `0x800A06D15` crash is the narrower
case of the inner `plVar5[0x1c] == plVar5[0x1d]` skip, not a permanently dead
gate. The gate, exactly:

```
800a056cf  MOV AL,[R13+0xfc]     ; R13 = ctx = [RDI+8]
800a056d6  TEST AL,AL
800a056d8  JZ  0x800a06426
800a056de  MOV AL,[R13+0xfd]
800a056e7  JNZ 0x800a06426
800a056ed  CMP byte [R13+0xb0],0x1
800a056f5  JNZ 0x800a0641f
800a056fb  ...                   ; first post-gate instruction
```

Beware watching it: that instruction is hot enough that the debug-register trap
fired 29,308 times and stopped the guest presenting. A run with a watch on a hot
path is not a run you can read a stall out of.

**The carousel walk was wrong, not the carousel.** The album carousel *is*
interactive; `left`/`right` scroll it and only the first tile starts the load. The
driver's 90 ms taps were being dropped at ~19 fps, so several runs pressed Cross
on the wrong tile and reported "race load started" from an unrelated `Strea`.
With 260 ms holds the walk reaches the first tile reliably;
`drive-race2.ps1` now does that.

**With the right tile and no probes (`race28`), the `0x800A06D15` crash does not
occur.** A different one does, on `Job#4`, in float-heavy code that looks like
car/physics setup:

```
Code at RIP: 48 8B 89 D8 11 00 00   mov rcx,[rcx+0x11D8]   ; rcx = 0xEE
AV target 0x12C6 = 0xEE + 0x11D8
```

`rcx` came from `mov rcx,[rax+0xDE8]` a few instructions earlier, with
`rax = 0x1003076D00` — inside a **direct-memory mapping**. So a field that should
hold a pointer reads `0xEE`: the structure's bytes are wrong, not the code.

That is the signature of the open aliasing bug (§6bs "aliased direct mappings do
not share memory"): GT7 maps the same physical range at two virtual addresses,
SharpEmu backs each with private pages, and a writer's bytes never reach the
reader's view. **Not proven for this address** — that needs
`SHARPEMU_LOG_DIRECT_MEMORY=1` on a crashing run and a check of whether this
physical range is mapped twice. Other explanations remain open: data the
streaming path never wrote, or an initialiser skipped upstream.

The host-level primitives for the fix already exist and pass
(`tests/SharpEmu.Libs.Tests/Memory/SharedBackingSectionTests.cs` — sections,
placeholders, aliases), but `PhysicalVirtualMemory` still contains no section or
alias handling, so the guest map path does not use them. That remains the
designed-but-unstarted work in
[Appendix D](#fix-design-shared-backing-for-direct-memory-not-started).

### Where the race load now stands

Boot -> wizard -> carousel -> first tile -> streaming (working) -> crash in
physics setup on a garbage pointer read out of direct memory. Each fix this
session moved the failure later; this is the first one that is a *data*
corruption rather than a missing export.

## 6bx. Aliased direct mappings, finally evidenced for the race load

§6bs recorded aliased direct mappings as "a real correctness bug, but **not
shown** to cause either stall". That qualifier can now be dropped for the race
load.

### The diagnostic gap that hid it

`map_direct` traced `inout` — the address *of* the in/out pointer, a caller stack
slot — and never the address the range actually landed at. Aliasing is by
definition two virtual addresses over one physical range, so it was invisible in
the one trace that should have shown it. (An earlier count of "3,536 alias pairs"
came from live process reads, not this log, and §6bs withdrew the conclusion
drawn from it.) `map_direct_result` now logs `mapped`, `len`, `direct` and `prot`
on the success path.

### The measurement (`race30`, race load reached)

Of 8,871 direct mappings over 8,821 distinct physical starts, **30 physical
ranges are mapped at more than one distinct guest virtual address**:

```
phys=0x8A600000 -> 0xE416E00000(0x200000), 0xEC05000000(0x200000),
                   0xF40C000000(0x800000), 0xF40C400000(0x1A00000)
phys=0xA9800000 -> 0xEC06600000(0x200000), 0xF41AC00000(0x600000)
phys=0xAAA00000 -> 0x1267C00000(0x200000), 0xF41BE00000(0x2C00000)
```

`PhysicalVirtualMemory` contains no section or alias handling, so each of those
views is private host memory: a write through one is invisible through the
others.

### Why this is the race load's blocker

`race28` crashed on `Job#4` at `mov rcx,[rcx+0x11D8]` with `rcx = 0xEE`, where
`rcx` came from `[rax+0xDE8]` and `rax = 0x1003076D00` — inside a direct mapping.
A pointer field reading as a small integer is what a diverged alias produces: the
producer wrote through one view, the consumer read another.

Two further observations from the same run support a sparse-residency model, in
which aliasing is not incidental but structural:

- **`munmap_partial` is a hot path, not a safety net.** 669 calls, all on the
  streaming arena (`region=0x12E9800000+0x143C00000`), carving 2 MiB at a time
  with the head advancing `0x200000` per call. §"Reproducing it" records
  `munmap` count 0 for a full boot-to-load run; that was measured before the
  load actually streamed, and is now wrong.
- **Both PRT apertures are configured**: `set_prt_aperture id=1
  base=0x3000000000 size=0x200000000` and `id=0 base=0x2900000000
  size=0x100000000`.

Timing dependence fits too: `race28` crashed in physics, `race29` stalled with
`Strea` blocked at `0x80077C227` and never crashed, from the same build and the
same route.

### What this does not yet prove

The faulting address in `race28` was not itself shown to be in one of the 30
aliased ranges — `race28` ran without direct-memory logging, and `race30` (which
has the mapping census) did not reproduce the crash. Closing that last gap means
one run with both the logging and a crash, then checking the faulting `rax`
against the alias list.

### The fix, and why it is not in this change

Shared backing for direct memory, per
[Appendix D](#fix-design-shared-backing-for-direct-memory-not-started).
The host primitives already exist and pass —
`tests/SharpEmu.Libs.Tests/Memory/SharedBackingSectionTests.cs` covers sections,
placeholders and aliased views — but nothing in the guest map path uses them, and
wiring them in means a section per physical range, view splitting for the partial
unmaps above, the fault-retry protocol, the PRT aperture, and a POSIX equivalent.
That is the load-bearing centre of the emulator with 1,140 tests standing on the
current model; it wants its own session rather than the tail of this one.

## 6by. Two pixel/vertex encodings taken from KytyPS5, 2026-09-16

Not found by running GT7 — found by reading KytyPS5's 2026-09-15 commits after
asking whether that emulator was the better base. It is not (no GT7 support),
but three of its recent fixes are hardware facts, and two applied to us. Both
are general: neither depends on a title.

### FRONT\_FACE was never wired up, and the encoding is not a flag

- **Symptom.** Nothing observed directly. `Gen5SpirvTranslator.EmitPixelInputState`
  reserved the compact VGPR slot for `SPI_PS_INPUT` bit 12 and never wrote it,
  so any guest pixel shader branching on facing read whatever the register held.
- **Evidence.** KytyPS5 `7b5a33f87` ("Fix PS5 front-face VGPR encoding to
  restore scene lighting"): hardware initialises the front-face VGPR with the
  **float bits** of +1.0 (`0x3F800000`) front-facing and -1.0 (`0xBF800000`)
  back-facing, not `1`/`0`. Guest shaders feed it to float math and compare it
  against zero, so a 0/1 flag inverts or kills every facing-dependent branch.
- **Fix.** Declare the `FrontFacing` built-in when `SPI_PS_INPUT_ENA` bit 12 is
  set and store the selected float bits into the compacted VGPR
  (`EmitPixelFrontFaceInput`). Covered by `Gen5PixelFrontFaceSpirvTests`.
- **Not verified against GT7.** Whether any GT7 shader reads bit 12 is unknown;
  this fixes the encoding, it is not a claim about the title's symptoms.

### A vertex exported at the origin must be culled, not divided by zero

- **Symptom.** Draws that execute and reach the screen with wrong geometry or
  colour — the class the flat-green movie (§6bk–§6bn) belongs to, though this
  change is not known to be its cause.
- **Evidence.** KytyPS5 `86414760e` ("Cull zero homogeneous vertex positions
  before rasterization"): the PS5 discards a vertex whose exported position is
  all zeroes **before** the perspective divide. Vulkan has no such rule and
  rasterises the NaN that `0/0` produces.
- **Fix.** On a position export (`Exp` target 12) emit
  `OpAll(pos == vec4(0))` and write `-1.0` (else `0.0`) to `ClipDistance[0]`,
  collapsing the primitive to its remaining edge. Requires the
  `shaderClipDistance` device feature, now enabled in `VulkanVideoPresenter`
  with a warning when the GPU lacks it. Guest clip/cull exports (targets 13–15)
  are dropped today, so slot 0 is free. Covered by
  `Gen5VertexZeroPositionSpirvTests`.

### Ruled out: SDWA byte destinations

KytyPS5 `acd7435d3` restricts `V_MOV_B32` SDWA to word destinations; hardware
allows bytes. **SharpEmu is already correct here** — `CreateSdwaControl` decodes
`dst_sel`/`dst_unused` generically with no per-opcode rule table, and
`ApplySdwaDestination` implements PAD, SEXT and PRESERVE. Checked by hand
against that commit's expected vectors (RDNA2 table 88) for all four byte
selects in all three unused modes; every one matches. No change made, and no
test added for behaviour that was already right.

### Not taken

`441367b1a` (pixel shader input aliases) and `0d9e95d96` (centroid interpolation
weights) carry a real idea — two pixel inputs mapping to one vertex output with
different interpolation must share a single SPIR-V interface variable — but the
code is entangled with KytyPS5's own pixel-parameter abstraction. Revisit when
the text-never-drawn blocker is next worked.

KytyPS5 is GPL-2.0-only against this project's GPL-2.0-or-later, so nothing was
copied: both fixes are reimplementations of the stated hardware behaviour.

## 6bz. Blocker F: the unlock correlation resolves to a job-context teardown, 2026-09-16

### The correlation, and what it is not

Three of four recorded blocker-F crashes report `last_import=tn3VlD0hG60`
(`scePthreadMutexUnlock`) — `race52`, `race53`, `race64`; `race54` reports
`KT-hTp-Ch14` instead, so it is **3 of 4, not 4 of 4**.

Base rate checked before building on it: in `race63`, which stalled without
crashing, Job#0's last import across eight stall snapshots is `4czppHBiriw`,
`PFT2S-tJ7Uk`, `p+zLIOg27zU` and `9UK1vLZQft4` — never the unlock. So the unlock
is not Job#0's resting last-import, and the correlation is not pure frequency.

The sharper fact is the **call site**, not the import: `race52` (Job#4) and
`race64` (Job#0) both unlock at `0x8002CAECA`, returning to `0x8002CAECF` — the
same site on two different job threads. `race53`'s unlock returns to
`0x808179F49`, outside the eboot text, so it comes from an LLE module.

### What that site is

Enclosing entry `0x8002CAA20` (+0x4AF), found from the call-target list
(77,949 raw targets, matching §6bt's count; 30,783 with two or more callers).
Mapping confirmed first: the five bytes before `0x8002CAECF` decode as
`call rel32` to `0x804192320`.

```
0x8002CAECA  e8 51 74 ec 03              call   0x804192320   (unlock thunk)
0x8002CAECF  48 8b 45 a0                 mov    rax, [rbp-0x60]
0x8002CAEDA  48 c7 40 28 00 00 00 00     mov    qword [rax+0x28], 0
0x8002CAEE2  48 c7 83 70 04 00 00 00 ..  mov    qword [rbx+0x470], 0
```

Decompiled (`blockerF-decomp.log`), the function resolves the object it clears
out of FS-relative TLS:

```c
lVar6  = *in_FS_OFFSET;
lVar18 = 0x806999f10;  if (*(long *)(lVar6 + -0xe0) != 0) lVar18 = *(long *)(lVar6 + -0xe0);
lVar11 = *(long *)(lVar18 + 0x470);
...
func_0x000804192320(puVar22 + 10);   /* unlock */
*(undefined8 *)(lVar11 + 0x28) = 0;
*(undefined8 *)(lVar18 + 0x470) = 0;
```

So it is a **job-context teardown that nulls `+0x28` and `+0x470` immediately
after releasing the mutex**, on a context selected through FS-relative TLS.

### Why this fits blocker F

`race44` crashed calling an allocator whose pointer at **`[ctx+0x28]`** held
garbage — the same offset this function nulls. If the FS base is wrong or stale
for the thread running this teardown, it clears **another job's** context: a
different victim each run, a null dereference somewhere downstream, and always
shortly after a mutex unlock. That is blocker F's signature, including the
moving crash site.

This is a hypothesis with an address, not a conclusion. Blocker E's fiber-fs fix
and the `sceFiberGetSelf` audit (§6bu, 0 mismatches over 170,000+ checks)
validated `sceFiberGetSelf`, not the FS base observed at an arbitrary guest
instruction, so they do not rule it out.

### The experiment that settles it

`SHARPEMU_WATCH_GUEST_EXEC=0x8002CAEDA` with
`SHARPEMU_WATCH_GUEST_EXEC_RING` (the site is hot, so printing per hit would
hide the one that matters), recording `rbx` — the context base — per hit. Two
outcomes distinguish the hypotheses:

- two different guest threads clearing the **same** `rbx`, or one thread
  clearing an `rbx` that is not its own context → the teardown is running
  against the wrong context, and the FS base is the thing to fix;
- every hit clearing a context private to its own thread → the teardown is
  innocent and the unlock correlation is a proximity artefact (the crash sites
  are thousands of bytes from the unlock return, so an import-free stretch after
  the unlock would produce the same correlation).

### Result (`race65`, 2026-09-16): hypothesis refuted, chain found

The watch fired 136 times and the ring dumped at the native exception.

**Refuted, cleanly.** 43 distinct contexts, and **every one was torn down by
exactly one thread** — no context is cleared twice, and the 136 victim objects
read from `[rbx+0x470]` are 136 distinct values, never shared. The teardown does
not run against another thread's context, so a stale FS base at this site is not
what corrupts job state. Do not re-run this test.

**What the same ring showed instead.** The allocator slot `[rax+0x28]` holds only
**58 distinct values across 136 teardowns**, and 32 of them are shared by more
than one (thread, context) pair — one arena appears in 10 pairs across 5 threads.
The values are page-aligned 16 KB blocks, matching the
`func_0x0008000d12e0(0x4000,0x4000)` scratch allocation in the same function. So
job contexts **share recycled scratch arenas**, arriving independently at
`race56`'s recycled-scratch conclusion.

**The crash, and what it names.** `race65` lost three job threads at once —
Job#3, Job#4, Job#5 — all reading tiny addresses (`0x2C`, `0x50`, `0x2C`), two of
them at the same instruction `0x8002CC0BD`. Enclosing entries `0x8002CBFF0`
(+0xCD) and `0x8002C2A00` (+0x228), both close enough to trust, both in the same
subsystem as the teardown. Decompiled, `sub_8002CBFF0` reads the job context out
of the same TLS slot and uses it as a lookup key:

```c
lVar11 = (*(long *)(lVar4 + -0xe0) != 0) ? *(long *)(lVar4 + -0xe0) : 0x806999f10;
lVar9  = func_0x00080019b760(*(undefined8 *)(*(long *)(lVar4 + -0xe8) + 0xde8),
                             *(ushort *)(lVar11 + 0x18a) - 0x1700);
fVar5  = ... * *(float *)(lVar9 + 0x2c) + *(float *)(lVar9 + 0x3c);
```

The `0x2C` fault target is `*(float *)(lVar9 + 0x2c)` with `lVar9 == 0`: the
lookup returned NULL and the guest used it without checking. The key comes from
the job context at `+0x18a`.

**Proven vs inferred.** Proven: no double teardown; unique victims; shared
`+0x28` arenas; three simultaneous null-lookup crashes; the `0x2C` target is the
unchecked lookup result. Inferred, not yet shown: that the null lookup follows
from a cleared or recycled context supplying a stale key at `+0x18a`.

**Next.** Establish whether `+0x18a` is stale at the failing lookup — exec-watch
`0x8002CC0BD` probing `rbx+18a` and the table base, or a crash dump at that site.
Three threads failing together argues for one shared object going away rather
than three independent races, so the table at `[fs-0xe8]+0xde8` is worth reading
at the same time.

### Result (`race66`, 2026-09-16): the TLS fallback is refuted too

`sub_8002CBFF0` disassembles to a lookup keyed off the job context, with a
**global default** if the TLS slot is empty:

```
8002cc013  LEA    RDX,[0x806999f10]              ; global default context
8002cc021  MOV    RCX,qword ptr [RAX + -0xe0]    ; job context from FS TLS
8002cc03c  CMOVNZ RDX,RCX                        ; fall back to the global when TLS is 0
8002cc04a  MOVZX  ESI,word ptr [RDX + 0x18a]     ; lookup key
8002cc072  CALL   0x80019b760
8002cc0bd  VMULSS XMM0,XMM0,dword ptr [RAX+0x2c] ; faults with RAX = 0
```

Watching `0x8002CC021` and probing `rax-E0` reads the TLS slot itself.
**`[fs-0xe0]` is never zero: 0 of 478 hits**, across six job threads. The guest
always had a real job context, the global-default path is not taken, and a
missing TLS slot is not the cause. Do not re-run this test either.

### Observer effect, stated plainly

Arming the watch changed the failure. `race66` did not reproduce the moving-site
crash: instead **five job threads (Job#0, #2, #3, #4, #5) faulted at one
instruction**, `0x803262FD4`, reading `[null+0x87]` and `[null+0x12A]`, four of
them with identical `last_import=Zxa0VhQVTsk` (`sceKernelWaitSema`) and
`last_ret=0x8001354C2`. The watch's trap overhead perturbs the timing of the
very function under investigation, so `race66`'s signature may be a different
failure from blocker F rather than a clearer view of it. Treat the
`sceKernelWaitSema` correlation as unconfirmed until it reproduces without a
watch armed.

### Ruled out: the semaphore wait/signal paths

Reviewed against that correlation. All three acquisition paths check the count
and decrement it inside the **same** `lock (semaphore.Gate)` critical section —
the fast path, the host-thread fallback (`while (Count < need) Monitor.Wait`,
then `Count -= need`), and the cooperative `WakePredicate`. `SignalSema` wakes
every cooperative waiter, but each re-tests the predicate and commits its own
acquisition atomically, so a thundering herd cannot hand one token to two
threads. No double-acquire is possible here; the mass wake is not coming from a
broken semaphore.

### Latent, unrelated: `sceKernelCancelSema`

Found while reviewing the above, **not** a cause of any observed crash — GT7 does
not call it in any recorded run (`4DM06U2BNEY`: 0 hits). Recorded so it is not
rediscovered as a suspect:

- it sets `Count` and calls `Monitor.PulseAll`, so a host-path waiter sees a
  sufficient count and returns `ORBIS_GEN2_OK`, where hardware returns a
  cancellation error to every waiter;
- it never calls `WakeBlockedThreads`, so a cooperatively-blocked guest thread is
  not woken by a cancel at all.

Fixing it needs a title that exercises it, or hardware evidence for the exact
error code; neither is in hand.

### What the NULL actually is (static, `sub_80019b760`)

```c
undefined8 sub_80019b760(undefined8 param_1, uint param_2)
{
  if (param_2 < 0x10) {
    return (*(code *)(/* 16-entry jump table at 0x804e9bd2c */))();
  }
  return 0;                     /* NULL only when index >= 0x10 */
}
```

The index is `*(u16)(jobctx + 0x18a) - 0x1700`, so a zeroed type tag would
underflow past the bound and return NULL — which would have tied blocker F to
blocker E's zero type word.

### Result (`race67`, 2026-09-16): the type tag is not corrupt

Watching `0x8002CC04A` and probing `rdx+18a`: **1,233 hits, tag `0x1701` every
time**, index 1, never out of range. The bound is never the thing that returns
NULL at this site. It follows that when this site does fail, the NULL comes from
**the dispatch handler behind jump-table entry 1**, not from the bounds check —
a different search than the one this test was designed for.

`race67` also crashed at a **fourth** distinct site, `0x8002BE398` (Job#4), so
the watched site was not the failing one that run. Site-specific watches are the
wrong instrument for a moving target; three runs (`race65`–`race67`) were spent
establishing that, and the next attempt should not be a fourth.

### The invariant across every recorded crash

| run | site(s) | threads |
| --- | --- | --- |
| `race64` | `0x802F8C74D` | Job#0 |
| `race65` | `0x8002CC0BD` x2, `0x8002C2C28` | Job#3, #4, #5 |
| `race66` | `0x803262FD4` x5 | Job#0, #2, #3, #4, #5 |
| `race67` | `0x8002BE398` | Job#4 |

Every fault is a **read at exactly null plus a field offset** — `0xCC`, `0x2C`,
`0x50`, `0x87`, `0x12A`. Different objects and different consumers, one disease:
pointer fields reading zero. That is the shape of memory that was valid and
became zero underneath its holder, which points at the shared recycled scratch
arenas (`race65`) or at the emulator's own shared-backing remap window, rather
than at any one guest function.

### Result (`race68`, 2026-09-16): no crash, and the backing layer is clean

Run with `SHARPEMU_LOG_SHARED_BACKING=1` and **no execute watch**, so it doubles
as the control for the observer effect.

- **It did not crash.** Carousel, race select and race load all succeeded; the
  run went to a stall instead. So blocker F is **intermittent**: `race63` and
  `race68` stalled without crashing, `race64`-`race67` crashed. Any future claim
  that a change "fixed" it needs several clean runs, not one.
- **The `sceKernelWaitSema` signature did not recur** without a watch armed,
  which supports it having been induced by `race66`'s instrumentation. Do not
  chase it.
- **The shared-backing layer reported no failures**: 8,869 successful maps, and
  zero split, unmap or map errors. Note the trace is failure-only — successful
  splits are not logged — so this shows the layer erroring nowhere, not how many
  splits occurred. The three `reclaim:` lines are early-boot thread-stack setup
  in the guest stack region (`0x6000_00000`), not race-load activity.

Because the run did not crash, it is not a test of correlation between a remap
and a fault; it only removes "the backing layer is visibly failing" from the
board.

### Where blocker F stands after 2026-09-16

Eliminated, each with evidence, none to be re-run:

| ruled out | evidence |
| --- | --- |
| teardown clearing another thread's context | `race65`: 43 contexts, 0 torn down twice; 136 unique victims |
| job context missing from FS TLS | `race66`: `[fs-0xe0]` non-zero in 478/478 hits |
| corrupt job type tag / bounds rejection | `race67`: tag `0x1701` in 1,233/1,233 hits |
| semaphore double-acquire | all three acquisition paths check and decrement under one lock |
| `sceKernelWaitSema` correlation | `race66` only, with a watch armed; absent in `race68` |
| shared-backing layer failing | `race68`: 0 split/unmap/map errors in 8,869 maps |

Still standing, unproven: job contexts share recycled 16 KB scratch arenas
(`race65`: 58 distinct arenas across 136 teardowns, one shared by 5 threads),
and every fault is a read at exactly null plus a field offset. The open question
is what zeroes a pointer field that a live holder still reads.

The instrument this needs is one that catches the **writer**, not the reader:
`SHARPEMU_WATCH_GUEST_WRITE` on a victim field, which requires knowing a victim
address in advance, or a crash dump (`SHARPEMU_CRASH_DUMP_DIR`) read offline to
establish the null pointer's provenance. Site-watching is exhausted.

### The full chain to the fault (`race69`, 2026-09-16)

Two earlier readings of `sub_80019b760` were wrong and are corrected here.

The 16 jump-table entries at `0x804e9bd2c` all land **inside `sub_80019b760`
itself** (`0x80019B77B`-`0x80019B94E`): it is an internal switch, not a dispatch
to separate handlers. Ghidra's `-noanalysis` decompile modelled the `jmp` table
as a call returning a value, and §6bz repeated that. Entries 13 and 14 both
point at `0x80019B94E` (`XOR EAX,EAX; RET`), the same NULL the out-of-range
default returns.

The table is not in `gt7_text.bin` and `eboot.bin` is a signed PS5 container
(magic `54 14 F5 EE`), not a bare ELF, so it was read from a live process with
`read-vm.py` — no debugger attached.

Case 1, the path a `0x1701` tag always takes, is two instructions:

```
80019b823  MOV RAX, qword ptr [RDI + 0x11d8]
80019b82a  RET
```

So the NULL is a **plain field read**, not a failed lookup. The chain is:

```
ctx2   = [fs-0xe8]            per-job descriptor (102 distinct values in race66)
table  = [ctx2 + 0xde8]
result = [table + 0x11d8]     <- reads zero at the crash
         [result + 0x2c]      <- the AV
```

**Measured in 95 healthy hits (`race69`):**

- `[table + 0x11d8]` normally holds **`table + 0x800`** — an interior
  self-pointer. All six distinct tables show exactly that relationship.
- **The tables are shared between job threads**: one seen by 3 guest threads,
  two more by 2 threads each, over only 95 samples.
- The field is **never zero** in healthy operation (0 of 95).

A structure whose own interior self-pointer reads zero is a **zeroed or
re-initialised** allocation, not a dangling one — and because the table is shared
by up to three job threads, a single zeroing explains several threads faulting at
once on different objects, which is what every recorded crash shows.

`race69` itself did not crash (it stalled), consistent with the intermittency
recorded above; the 95 entries are healthy-path samples.

### Next, with the target this narrow

Find what zeroes `table + 0x11d8`. The table base moves between boots and is
created during the race load, after watches arm, so a `SHARPEMU_WATCH_GUEST_WRITE`
spec has nothing stable to anchor to yet — the open problem is finding a stable
anchor, or capturing the table's full contents at the fault (whole-structure
zeroing versus one field) via `SHARPEMU_CRASH_DUMP_DIR` read offline.

Worth checking first, because it is free: whether the emulator ever zeroes guest
memory that is still mapped — `GuestSharedBacking` has a `zero:` trace path, and
`race68` recorded none of it.

### Not yet done

The `race64` crash site `0x802F8C74D` has no resolved enclosing entry: the
nearest call target is `0x802F879F0` at +0x4D5D, which is too far to trust, so
that function is reached indirectly (vtable) and needs the padding+prologue
fallback. Not required for the experiment above.

## Appendix A. Live Adhoc VM findings, 2026-09-12 (Codex)

Condensed from the retired session ledger. These corrected §6ao/§6ap.

- The callback `MGOM.start` schedules is **`boot`** (`gt7/boot/boot_entry.ad`,
  bootstrap `script25` line 312), not `main_loop`. `getRenderContext` is also
  called six times by `initMenuSystem` (lines 259–264), so a one-shot hit on it
  proves nothing about `main_loop`.
- Outer VM stack `boot -> clearSequence -> finalize -> terminate`, joining the
  Main sequence thread. That thread's 15-frame stack ended
  `… CheckLoginStatus -> makeSureLogin -> checkMakeSureLoginError ->
  openLoginRequiredDialog -> … -> ConfirmDialog.open -> EventLoop.enter`, with
  the IP just after the loop's YIELD: an ordinary dialog wait, not a lock.
- `waitLaunchSequence(nil)` substitutes `req_id` and still yields; bytecode LEAVE
  is not RETURN.
- The selected message, read from the frame's local: "You cannot use network
  functions unless you sign in to PlayStation Network" (the not-signed-in branch).
- Presentation then: flips for display buffers `0x1C00000`, `0x3BE0000`,
  `0x5BC0000` were taken and dropped (`found=False`) because
  `RegisterKnownDisplayBuffer` marks a buffer available before any image exists
  and `RenderCore` drops a missing one. The upstream cause was the PM4 COUNT
  decode (§6az).
- Exec-break register logging read RDI from CONTEXT offset 136 (RDX); corrected
  to 176.

Evidence: `artifacts/gt7-codex-20260912/` (`main-sequence-stack.txt`,
`login-dialog-message.txt`, `boot-vm.txt`, `present-trace.err.log`,
`draw-trace.err.log`, `boot-entry.ad.diss`, `login-util.ad.diss`,
`confirm-dialog.ad.diss`, `*-ghidra.log`).

## Appendix B. Reading Adhoc VM state from a live guest

Offsets hexadecimal; heap addresses must be rediscovered every boot.

| Object | Layout |
| --- | --- |
| Update context | wait event `+28`, state `+48`, callback `+78`, args vector `+88..+90` |
| Callback command | vtable `0x8054AC2E8`, callback `+40`, VM `+68` |
| VM | current frame `+30`, state `+50/+54`, storage base `+F8`, frame vector `+120..+128` |
| Script frame | caller line (low uint) `+10`, parent `+18`, VM `+20`, metadata `+28`, IP `+38`, storage offset (low uint) `+40` |
| Compiled metadata | source string `+68`, function name `+90`, instruction vector `+C0..+C8` (8 bytes per instruction) |
| String layout | inline data or pointer `+0`, length `+10`, capacity `+18` (capacity < 16 = inline) |
| Reference wrapper / hString | referenced object / string layout at `+28` |

- `caller_line` is the line in the **caller**; locate execution by
  `(IP - instruction vector) / 8`.
- Locals: storage header = VM `+F8` + frame `+40`; four static slots precede the
  locals, starting at header `+20`.
- Find a live command by scanning for its vtable with the matching callback, then
  validate the frame vector; scans also find freed copies.
- Live objects carry Itanium RTTI: `[vtable-8] -> typeinfo -> +8 ->` name
  (`N4MENU8mProjectE`).
- `read-vm.py` layouts were corroborated against Ghidra and script disassembly;
  do not apply them to other object types.

## Appendix C. Resolved script-native handlers

A native's name string lives in the live module image (not in `gt7_text.bin`);
its registration site does `lea rsi,[name]; lea rdx,[handler]; call`
(`48/4C 8D` with `(modrm & 0xC7) == 0x05`, target `base + off + 7 + disp32`).
Verify the mapping first: the `lea` at `0x800F23455` must resolve to
`0x804B32371` (`getRenderContextCount`). See also §6ay.

| Name | String | Registration | Handler |
| --- | --- | --- | --- |
| `startPage` | — | — | `0x801DB9BC0` |
| `getProjectByName` | `0x804AFC8B1` | `0x800F47538` | `0x801DA03D0` |
| `pushEvent` | `0x804AC8654` | `0x800CABC42` | `0x801DBCB70` |
| `IsGameRegionScec` | `0x804D0F576` | `0x800E7337C` | `0x801D51050` (shared with `WidgetBind.adi`) |
| `createRenderContext` | `0x804AFCA47` | `0x800F2343F` | `ret` stub `0x800000160` |
| `getRenderContext` | — | `sub_800F231A0` | `0x801DFA610` (do not probe) |
| `getRenderContextCount` | `0x804B32371` | `sub_800F231A0` | `0x800EE06B0` (`mov eax,[rdi+0x6d8]; ret`) |
| `finishProject` | — | — | `0x801DA10E0` |
| `closeOSK` | — | — | `0x801DC28D0` |

## Appendices

Documents folded in from separate files; their cross-references now resolve
inside this log.

- [Appendix D. Race load hang — handover (2026-09-14)](#appendix-d-gt7-race-load-hang-handover-2026-09-14-opus) — was `race-load-hang.md`
- [Appendix E. Codex follow-up on the race-load hang (2026-09-14)](#appendix-e-codex-follow-up-race-load-hang-2026-09-14) — was `race-load-hang-codex.md`
- [Appendix F. Session record, 2026-09-15](#appendix-f-gt7-race-load-session-record-2026-09-15) — was `session-2026-09-15.md`
- [Appendix G. Direct memory: allocation and mapping contract](#appendix-g-direct-memory-allocation-and-mapping-contract) — was `direct-memory-contract.md`
- [Appendix H. Handover, 2026-09-16](#appendix-h-handover-gt7-2026-09-16) — was `handover-2026-09-16.md`

## Appendix D. GT7 race load hang — handover (2026-09-14, Opus)

Follow-up: [Codex's live-stack and caller analysis](#appendix-e-codex-follow-up-race-load-hang-2026-09-14)
confirms the unconditional wait after rejected submission, locates the low
values in guest read requests, and records their contiguous buffer lengths.
Their origin is now traced; see "Fix and result" below.

Status: **partly fixed, still open**. The upstream defect is the unresolved
`sceHmd2ReprojectionQueryDisplayBufferSizeAlign`: GT7 builds a direct-memory
allocation from its return value, and SharpEmu's unresolved-import error code
turns that into a 6 GiB request. Rejecting the malformed request (the fix below)
removed the deadlock on `0x806DFAB08` in `race12` but **not** in `race14`, which
reproduced it. In `race12` the race load got further — the streaming arena
allocated, `sceKernelVirtualQuery` stopped missing, `Strea` ran — but the race
still did not start. Direct mappings that alias the same physical memory do not
share bytes in SharpEmu; that is a real correctness bug, but it is **not shown**
to cause either stall. Full detail is in `boot-investigation.md` §6bs.

### Fix and result (2026-09-14, Opus)

#### Root cause

0. **Upstream: a missing Hmd2 export.** `sub_800FEE780` (Ghidra; entry
   confirmed as a `call rel32` target and by padding+prologue) calls
   `sceHmd2ReprojectionQueryDisplayBufferSizeAlign` (NID `-C2nkoEYOnU`, PLT
   `0x8041924F0`, return `0x800FEE97D`) and uses its 128-bit return unchecked:
   `size = rax`, `a = rdx`, then
   `sceKernelAllocateDirectMemory(0, <size query>, roundup(size, max(0x4000, a)), max(0x200000, a), 0xC, &out)`,
   and later `sceHmd2ReprojectionInitialize` (`C0rPwER-yxg`, also unresolved).
   The export is unresolved in every traced run, so `rax` is the sign-extended
   `ORBIS_GEN2_ERROR_NOT_FOUND` (`0xFFFFFFFF80020002`) and `rdx` is leftover
   register state (`0x6007FFE32`, the query's own `rdx` argument). That
   reproduces the logged `len=0x180800002` exactly. On hardware the query
   returns a real size and alignment and this allocation succeeds, so the
   title's arguments are not malformed: ours are. No reference emulator
   implements the query (shadPS4 lists it as a stub, KytyPS5 not at all); the
   hardware values are unknown and must be researched, not guessed.
1. The call site (return `0x800FEE9CB`) therefore calls
   `sceKernelAllocateDirectMemory` with `len=0x180800002` and
   `alignment=0x6007FFE32`. Neither is a multiple of the 16 KiB page, so the
   kernel rejects the call with EINVAL — KytyPS5's `KernelAllocateDirectMemory`
   checks exactly this. SharpEmu granted it: 6152 MiB at physical `0x13000000`,
   the largest live block in the pool.
2. When the title later asks for its 5.06 GiB streaming arena
   (`len=0x143C00000`, return `0x8002D56BF`), the pool is short. `race9` (16 GiB
   pool): `used=0x2CC044002 free=0x133FBBFFE largest_gap=0x132600000`. `race11`:
   369 live allocations, 11.18 GiB, with the 6152 MiB block the largest. The
   request gets TRY_AGAIN.
3. The arena object keeps its size and a zero base. Live read of `race9`
   (`0xE41B5E2060`): `+0x00 = 0`, `+0x38 = 0x143C00000`.
4. The arena's sub-allocator `sub_8005D19F0` (a free list of 0x38-byte nodes:
   `[5]` size, `[6]` offset) returns **offsets**. `sub_8005D16C0` stores one at
   `+0x10` of a 0x30-byte descriptor (vtable `0x8058F30F0`). With the base at 0
   the offset reaches the read request as its buffer pointer (`0x00F25000`,
   `0x09AA0000`, `0x0A867000` — contiguous because they are consecutive slices
   of one archive, e.g. `crowd/amsx/0014.amsx`).
5. `sub_8040A4820` asks `sceKernelVirtualQuery` whether that "pointer" is GPU
   direct memory, gets NOT_FOUND (correctly), and drops any submission larger
   than 4 KB. `0x8040A00A0` waits on its event anyway, holding `0x806DFAB08`,
   and the other workers queue behind it.

#### Changes (both in `Libs/Kernel/KernelMemoryCompatExports.cs`)

- `sceKernelAllocateDirectMemory` and `sceKernelAllocateMainDirectMemory` return
  `ORBIS_GEN2_ERROR_INVALID_ARGUMENT` for a length, or a non-zero alignment, that
  is not a multiple of 16 KiB, matching KytyPS5.
- `DirectMemorySizeBytes` is 13,824 MiB minus the 448 MiB flexible pool
  (13,376 MiB) instead of 16,384 MiB, matching KytyPS5's
  `PhysicalMemory::TotalSize()` / `Size()`. The smaller pool *without* the
  validation fix did **not** help (`race11`): GT7 asks for the same
  `0x143C00000` whatever the pool size, and less was free. The untested
  combination is the validation fix on the **old** 16 GiB pool, so whether GT7
  needs the smaller pool is unknown; it stays because it matches the reference.

Suite green after each change. Regression test:
`KernelMemoryCompatExportsTests.AllocateDirectMemory_RejectsLengthOrAlignmentOffPageBoundary`
(GT7's exact arguments plus a misaligned-alignment and a misaligned-length case,
both allocate variants, out pointer left untouched).

The validation is correct hardware behaviour but treats the symptom: with the
Hmd2 query implemented, GT7 would make a valid allocation here and the pool
accounting would change again.

#### Result (`race12`, `SHARPEMU_LOG_DIRECT_MEMORY=1`)

- The invalid call: `result=ORBIS_GEN2_ERROR_INVALID_ARGUMENT`.
- The streaming arena: `selected=0x14D600000 result=ORBIS_GEN2_OK`.
- `virtual_query_miss`: 0. No native exceptions. No `FWRKR` waiters on
  `0x806DFAB08`.
- `Strea` made 30,611 imports (4 before the fix), rising until log line ~1,035,819
  and flat for the last seven snapshots.
- The race did not start. The last frames show the album carousel with a small
  spinner at the lower right, at 3.3 fps.
- Final snapshot: `PCL Event`, `Job#1` and `Job#3` wait on `0xE418577F88`, owned
  by `Job#2`; `GPUex` waits on `0x80650D210`, owned by `PCL Event`. `Job#2` is not
  stuck: its import count climbs ~0.5M per snapshot, its last call alternating
  between `sceKernelWaitSema`, `scePthreadMutexLock` and `sceAgcDriverSubmitAcb`.
  `PCL Event` climbs ~27k per snapshot. This is not the earlier deadlock, but a
  climbing import count shows activity, not progress: a retry loop or livelock
  looks the same. What the load is waiting on is not established.
- The session was closed at 15:22 (the log ends without a crash dump), 15
  snapshots (~7.5 minutes) after the race load began, so slow versus stalled is
  not settled either.

#### After the fix: aliased direct mappings do not share memory (`race13`, `race12`)

`race13` reached the race load at 16:25:44 and crashed a minute later: access
violation reading `0x1E` at `0x8000CB5D0` on `Job#3`, inside GT7's allocator
(`sub_8000CB0C0`, a per-thread cache over a lock-free tagged free list,
`head = node << 16 | counter`). The head it read was `0x1E0037`, so a corrupt
node had been pushed. No earlier run faulted there. **The link to aliasing is
not established:** `race13` ran without `SHARPEMU_LOG_DIRECT_MEMORY`, so its
mappings are unknown; the 2 MiB direct mapping said to hold the list (VA
`0x1005A00000`, physical `0x2C6600000`) comes from `race12`, a different boot
with different heap addresses.

In `race12`'s trace GT7 maps the **same physical direct memory at two virtual
addresses at once**, e.g. physical `0x291000000` already at VA `0x142C200000`
and then also at `0x30009A0000` (`len=0x10000 prot=0xD2 flags=0x10`), the second
address inside the PRT aperture that appears when the load starts. On hardware
both are the same pages. Recount (scratch script pairing each `map_direct` with
its `reserve` line, dropping a view once a later mapping reuses its VA): 3,536
overlapping pairs, 3,421 after the streaming-arena allocation, 3,048 touching
the aperture — and 115 already during boot, while menus work. So aliasing is
not by itself fatal, and munmap is untraced, so a pair can be stale even when
no VA reuse shows it.

SharpEmu cannot represent that: there is no shared section anywhere in `src`
(no `CreateFileMapping`/`MapViewOfFile`/placeholders). Each mapping reserves its
own host range through `KernelVirtualRangeAllocator.TryReserve`, and the PRT
aperture is lazily committed as fresh zero pages. So data written through one
view is invisible through the other. KytyPS5 backs guest memory with a shared
store (`GuestAddressSpace::MapBacking`) and self-tests that two addresses see
the same bytes (`SelfTestSub64SharedPlaceholderAlias`).

That the views do not share bytes follows from the code (below). That this
causes the spinner or the corrupted free list is **not shown**.

**Withdrawn: the `race14` "proof".** An earlier version of this section called
aliasing proven from `ReadProcessMemory` on `race14` (4 of 4 alias pairs held
different bytes). Re-checking `race14`'s own log:

- Its prerequisites held: the malformed call got `INVALID_ARGUMENT` (line 668),
  the arena allocated (`selected=0x14D400000 result=ORBIS_GEN2_OK`, line
  345,160), 0 `virtual_query_miss`, and the only `direct_memory_exhausted`
  lines are the four boot-time probes `race12` also has.
- But it **never reached the aliasing phase**: 22 `map_direct` traces after the
  arena (`race12`: 17,854), 0 in the PRT aperture (`race12`: 3,049), 0 alias
  pairs after the arena. Its 116 pairs are all from boot.
- The cited pairs are stale. Physical `0x69800000` was mapped at VA
  `0xF406400000` (`len=0x600000`, line 1,100) and `0xEC02A00000` (line 1,138);
  `0x78400000` at `0xF40B600000` (line 3,100) and `0xEC04E00000` (line 5,410).
  Both first views had their VA reused by later mappings, so they were unmapped
  and the differing bytes say nothing about sharing. Only 3 pairs survive the
  VA-reuse check (physical `0xA9000000`–`0xA95FFFFF`); none was read.
- It stalled with the **original deadlock**, not `race12`'s state: an `FWRKR`
  blocked on `0x806DFAB08` owned by an `FWRKR` in `pthread_cond_wait` at
  `0x80077C823`, `Job#0`–`5` parked, `Strea` at 4 imports, with no
  `virtual_query_miss`. The trigger is a **host allocation inside a guest
  fixed mapping** (next section), not a failure of the validation fix.
- The reads were sequential on a running process, so even live pairs could
  differ because another thread wrote between them.

A valid check needs: a run that reaches the aperture mappings, pairs whose views
are both live (trace munmap first), and the guest paused while both are read.

#### `race14`'s deadlock: a host thread stack in the guest's fixed window

GT7 maps the 5.06 GiB streaming arena with `MAP_FIXED` at an address it chose.
In `race14` that was `0x12E9800000`, and the map failed:
`reserve fixed range: no host mapping at 0x00000012E9800000 len=0x143C00000`,
`reserved=False`, `sceKernelMapDirectMemory` → NOT_FOUND (line 345,166). The
emulator's own main thread (the SDL pump) had its host stack at
`RSP=0x1340D7AB48` — inside `[0x12E9800000, 0x142D400000)`. Windows placed it
there at process creation; `TryBackFixedRange` cannot back a range a host
allocation already owns.

Across every capture this is the only run with a failed fixed map, and the
deadlock appears exactly in the pre-fix runs (`fix1`, `race2`–`5`, `7`, `9`,
`11`: arena refused) and in `race14` (arena map failed). `race12` (arena
mapped at `0x12E8800000`, main stack at `0x48547AAAF8`) reached the aperture
phase with no deadlock. One success and one collision after the fix: the
outcome depends on where ASLR puts host memory.

It is not a one-off. A `VirtualQueryEx` scan of the mitigated child 8 s into
boot found 128 host thread stacks (guard pages) around `0x87_C000_0000`, and
the main-thread RSP was between `0x05_…` and `0xEB_…` in every capture — all
inside PS5 user space `[0x10_0000_0000, 0x100_0000_0000)`, which on hardware
belongs to the title alone. shadPS4 (`address_space.cpp`, `USER_MIN`) and
KytyPS5 (`sysWindowsVirtual.cpp`, `USER_MIN = 0x1000000000`) reserve that
range at startup; SharpEmu reserves guest ranges only on demand.

Ruled out:

- **Upstream `0a461f8` / `f84246c`.** `TryCommitRange` commits pages of an
  existing *guest* region; the host stack is not one, so the map still fails.
  The removed inflate-epilogue VEH hack never fired in any GT7 run, and the
  app-root PRX indexing is never exercised (GT7 references none of its six
  root PRXs). The AGC/shader fixes are unrelated to this hang.
- **Turning high-entropy ASLR off for the child**
  (`PROCESS_CREATION_MITIGATION_POLICY_HIGH_ENTROPY_ASLR_ALWAYS_OFF`). The child
  then dies at load: a 32 GiB host reservation at `0x7FFF0000` covers the
  eboot base `0x800000000`. Low placement just moves the collision.

Fix direction: keep host allocations out of PS5 user space by reserving it
early and handing pieces to guest mappings on demand, using the same
placeholder machinery the shared-backing design needs.

**GT7 chooses its own fixed layout, so holes cannot be steered around.** Both
`race12` and `race14` issue the identical `MAP_FIXED` sequence right after boot
(`0x1200000000`, `0x1200200000`, `0x1202200000`, …, log line 612 onward); those
addresses are not derived from anything SharpEmu handed out. A hole left by a
host allocation inside PS5 user space is therefore not something a holes-aware
allocator can avoid — GT7 will map over it and the fixed map must fail.

##### Feasibility probe: reserving before the CLR starts

Scratch harness (not in tree): launch the mitigated child exactly as
`Program.TryRunMitigatedChild` does (CFG/CET policies, trusted child flag and
environment) but `CREATE_SUSPENDED`; scan the child with `VirtualQueryEx`,
placeholder-reserve free space with `VirtualAlloc2(hProcess, …,
MEM_RESERVE | MEM_RESERVE_PLACEHOLDER)`, resume, and poll-scan until exit.

| Run | Before resume, occupied in `[0x1_0000, 0x100_0000_0000)` | Reservation | Outcome |
| --- | --- | --- | --- |
| control (default ASLR, none) | 6 regions at `0x2BF GiB` (initial stack + neighbours) | — | boots; after 25 s 762 regions in the window, 130 thread stacks next to the initial one |
| `reserve1`/`reserve2` (default ASLR) | 6–8 regions, cluster at `0x2B6`/`0x29D GiB` | 960 GiB, 5 ms, 0 failures | CLR, HLE JIT warm-up, eboot + 3 PRX load, native workers prewarm; other thread stacks leave the window; only hole is the initial-stack cluster, which grew 6 → 10 regions |
| `b0` (HEASLR off, none) | 13 regions, all below 2 GiB | — | fails at image load: a host reservation at `0x7FFF0000` (size `0x8186A2000`) covers `0x800000000` |
| `b1`/`b1p` (HEASLR off) | 13 regions, all below 2 GiB | 960 GiB, 0 failures | same host startup success; PS5 user space one contiguous reservation, **zero holes**; host memory below 1 GiB (46 thread stacks, ~1,600 regions) |
| `b2` (HEASLR off, from `0x9_0000_0000`) | same | 988 GiB | same as `b1` |

Every reserving run then dies at the same place: GT7's first direct map writes
to `0xE3FFFFC000` (from `8zTFvBIAIN8+0x66`), an address SharpEmu's allocator
handed out inside the placeholder reservation it does not know about. That is
the expected failure of the probe, not of host startup.

What this establishes:

- Reserving from the parent while the child is suspended works and is cheap,
  and host startup survives it.
- With default ASLR the initial thread's stack is always inside PS5 user space
  (main-thread RSP was between `0x05_…` and `0xEB_…` in all 15 captures), so a
  hole always exists and host allocations can grow inside its alignment slack.
  Relaunching cannot converge — the ASLR range for that stack is the same
  terabyte.
- High-entropy ASLR off moves the initial cluster below 2 GiB, and together with
  the reservation leaves PS5 user space entirely to the guest. Either change
  alone is not enough (`b0`).

Not yet established: a guest running on a placeholder-aware allocator; the
GUI launch path (`SharpEmu.GUI/EmulatorProcess.cs` creates the child too); and
SharpEmu's own low non-fixed direct maps (`desired = directMemoryStart`, e.g.
`0x01C00000`), which would share the sub-2 GiB range with host memory once
high-entropy ASLR is off and should instead be placed from reserved space.
Turning high-entropy ASLR off is a security trade on top of the CFG/CET
mitigations the child already disables.

#### Fix design: shared backing for direct memory (not started)

Independently of the stall, aliasing is wrong and should be fixed. Start with
host-level tests before touching the kernel exports:

1. Two views of one section at a 16 KiB-aligned offset see each other's writes
   in both directions.
2. Unmap one view and map it again; the data is still there.
3. Unmap the **middle** of a view: both surviving pieces keep their bytes, and a
   third alias of the same physical range still matches them.

Then integrate direct mappings, then the aperture.

What has to change, from reading the current code:

- **No shared-memory primitive exists.** `IHostMemory` has only
  Allocate/Reserve/Commit/Free/Protect/Query. `WindowsHostMemory` wraps
  `VirtualAlloc`; `PosixHostMemory` uses anonymous `mmap` with
  `MAP_FIXED_NOREPLACE`. Every guest range is private memory.
- **`sceKernelMunmap` frees nothing.** It removes `_mappedRegions` entries and
  calls `RegisterReleasedVirtualRange`; the host pages stay committed.
- **Physical model.** Back the direct pool with one pagefile section of
  `DirectMemorySizeBytes` (`CreateFileMappingW(INVALID_HANDLE_VALUE, …,
  PAGE_READWRITE | SEC_RESERVE, …)`) so a physical offset *is* a section
  offset and aliases share bytes automatically. POSIX: `memfd_create` +
  `mmap(MAP_FIXED | MAP_SHARED)`.
- **Views need placeholders.** `MapViewOfFile3(…, MEM_REPLACE_PLACEHOLDER, …)`
  into a `VirtualAlloc2(MEM_RESERVE | MEM_RESERVE_PLACEHOLDER)` range is the
  only way to map at sub-64 KiB offsets, which GT7 uses (`len=0x10000` pieces at
  16 KiB-aligned physical offsets). KytyPS5 self-tests exactly this
  (`SelfTestSub64SharedPlaceholderAlias`).
- **The hard part is reservation.** `PhysicalVirtualMemory` reserves guest ranges
  as ordinary allocations, often inside larger ones (fixed granules; the 8 GiB
  PRT aperture pre-committed/lazily committed as one block in
  `DirectExecutionBackend`). `VirtualFree` cannot punch a 64 KiB hole in those,
  so direct-map and aperture ranges must be reserved as placeholders from the
  start, and region tracking, the lazy-commit fault path, `sceKernelMunmap` and
  `sceKernelReleaseDirectMemory` must all understand views
  (`UnmapViewOfFile2(…, MEM_PRESERVE_PLACEHOLDER)`, then re-coalesce).
- **Commit charge.** With `SEC_RESERVE`, commit only the pages a view maps
  (`VirtualAlloc(MEM_COMMIT)` on the view), not the whole 13 GiB pool.
- **Commit lifetime needs a policy.** Committed pages of a `SEC_RESERVE` section
  cannot be decommitted again with `VirtualFree`, so
  `sceKernelReleaseDirectMemory` cannot hand commit back. Decide explicitly:
  keep released physical pages committed (bounded by the pool size) or reset
  them (e.g. `DiscardVirtualMemory`/zeroing on release, since a new allocation
  must not see old data).
- **Placeholders must match exactly.** `MEM_REPLACE_PLACEHOLDER` needs a
  placeholder of exactly the view's address and size, so every map first splits
  the placeholder (`VirtualFree(MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)`) and
  every partial unmap splits the view's placeholder and re-maps the surviving
  pieces.

##### Backing validation (done)

`tests/SharpEmu.Libs.Tests/Memory/SharedBackingSectionTests.cs` exercises the
Windows primitives directly, before any allocator code depends on them. All
pass on Windows 11 (26200):

| Behaviour | Result |
| --- | --- |
| Two views of one `SEC_RESERVE` section at a 16 KiB offset, in split placeholders | share bytes both ways; committing through one view makes the pages usable through the other |
| Unmap with `MEM_PRESERVE_PLACEHOLDER` | address stays `MEM_RESERVE`; a plain `VirtualAlloc` there fails; remapping sees the old bytes |
| Unmap the middle of a view (unmap whole → split → remap survivors) | surviving pieces still match a third alias; middle stays a placeholder |
| Split and map at 16 KiB page granularity | works |
| `VirtualProtect` on one page of one view | only that page of that view changes; alias unaffected |
| Executable views | need a `PAGE_EXECUTE_READWRITE` section; an RW section refuses them |
| Placeholder created in a suspended process via `VirtualAlloc2(hProcess)` | splittable (`VirtualFreeEx`) and replaceable (`MapViewOfFile3(hProcess)`); bytes shared with a local view |
| `VirtualFree(MEM_DECOMMIT)` on committed section pages | fails; pages keep their bytes after every view is gone |

Commit-charge reclamation, measured with `GetPerformanceInfo` on a 1 GiB
section (scratch probe, system-wide so a few MiB is noise):

| Step | Commit delta |
| --- | --- |
| section created / view mapped | +1 / +5 MiB |
| view committed | +1,029 MiB |
| `VirtualFree(MEM_DECOMMIT)` | fails, +1,029 MiB |
| `DiscardVirtualMemory`, `OfferVirtualMemory` | succeed, +1,016 MiB |
| every view unmapped, section open | +1,014 MiB |
| section handle closed | −12 MiB |

Consequences for the allocator:

- **Commit is reclaimed only when the section object dies** — that needs the
  handle closed *and* every view unmapped, in either order (the probe unmapped
  first, then closed). A single pool section's commit therefore only grows.
  **Decided:** one bounded pool section, accepting retained commit up to the
  13,376 MiB pool; this bounds the backing pool, not the emulator's total
  memory. Chunked sections would reclaim only once every alias into a chunk is
  gone, which is why they are not worth the complexity yet.
- **Released physical memory keeps its bytes.** Freshly created pagefile-backed
  section pages are zero, but a recycled physical offset is not, and
  `DiscardVirtualMemory` is no substitute (contents undefined, commit retained).
  **Decided:** clear explicitly when recycled backing is handed to a new
  allocation, and never while creating an alias or remapping. What hardware
  does here is unverified.
- **Partial unmap is not atomic.** Between unmapping a view and remapping its
  survivors, a guest thread touching a surviving piece faults. The allocator
  needs a synchronisation story (e.g. retry such faults under the mapping lock).
- **The pool section must be executable** if guest code may live in direct
  memory; individual views can still be mapped with narrower protection.

For the launch step:

- **Reservation success is a startup gate**, checked on every launch. The
  observed below-2 GiB host placement with high-entropy ASLR off is this host's
  behaviour, not a documented guarantee, so the child must verify every interval
  it was promised before it runs anything.
- **Reserve the image range too** (`0x8_0000_0000`), not just PS5 user space —
  `b0` shows a host reservation taking the eboot base otherwise. Consequence:
  `MEM_REPLACE_PLACEHOLDER` accepts private allocations and pagefile-backed
  views but **not** `SEC_IMAGE` views, so the loader must place the guest image
  as private or section-backed memory inside the reservation. That restriction
  is about the Windows mapping type, not about the bytes being guest code.
- **SharpEmu's low non-fixed maps** (`desired = directMemoryStart`, e.g.
  `0x01C00000`) should move into reserved space — but only after confirming
  those addresses are emulator-selected rather than guest-required. GT7 does
  request some low addresses itself (`0x600000000`, fixed), so the two cases
  must be separated before anything moves.

##### Concurrency protocol for partial unmap (settled)

A vectored handler must not acquire synchronisation objects or allocate, and
`SuspendThread` on a thread holding a lock can deadlock — so neither "take the
mapping lock in the handler" nor "suspend the guest" is acceptable. SharpEmu's
existing lazy-commit handler already obeys that: it uses raw `VirtualQuery` /
`VirtualAlloc` only (`DirectExecutionBackend.Exceptions.cs`,
`TryHandleLazyCommittedPage`).

Per-view granularity removes most of the problem, because unmapping one view
leaves its neighbours mapped and needs no remap. Measured on this host, mapping
1 GiB of a `SEC_RESERVE` section view by view (scratch probe):

| View size | Views | Map time | Per view | Extra commit | `VirtualQuery` walk |
| --- | --- | --- | --- | --- | --- |
| 16 KiB | 65,536 | 172 ms | 2.6 µs | +16 MiB | 36 ms |
| 64 KiB | 16,384 | 46 ms | 2.8 µs | +2 MiB | 17 ms |

Unmapping a middle view left both neighbours intact in each case. Full 16 KiB
granularity is too expensive though: GT7's 5.06 GiB arena alone would be
327,680 views (~0.9 s, ~80 MiB of kernel overhead), and a fully mapped 13 GiB
pool ~218 MiB.

Decision:

- **Map guest direct memory in 2 MiB views.** GT7 maps direct memory 2 MiB
  aligned (`align=0x200000`, sizes `0xA00000`, `0x1C00000`, the arena itself),
  so the arena costs ~2,560 views (~7 ms) and unmaps that land on 2 MiB
  boundaries need no remap at all.
- **Only a sub-view partial unmap splits and remaps**, and it affects at most
  one 2 MiB view. For that case the unmapping thread publishes the pending
  range in an atomic field before unmapping and clears it after remapping; the
  vectored handler, on a fault inside that range, spins on the atomic with
  `YieldProcessor` and then retries the faulting instruction. No locks, no
  allocation, no thread suspension, and the unmapping thread never waits on
  guest threads, so the two cannot deadlock.
- **Host workers** reach guest memory through `PhysicalVirtualMemory`, which
  takes its reader lock, so they are serialised against remapping by the
  ordinary mapping lock and never depend on fault retry.

To verify during integration: how often the guest unmaps below 2 MiB —
`sceKernelMunmap` is currently untraced, so the sub-view path's real frequency
is unknown. Trace it first; if it never happens in practice, the retry path
stays a safety net rather than a hot path.

Order of work: backing validation (done) → concurrency protocol (settled) →
shared suspended launch with reservation for CLI and GUI (done) →
placeholder-aware allocator (private allocations done; shared section next) →
guest regression tests → PRT aperture → POSIX.

##### Integration so far (behind `SHARPEMU_RESERVE_GUEST_VA=1`, default off)

Enabling the flag makes the launcher create the emulator child suspended,
reserve the guest window in it, and verify that reservation in the child before
anything else runs. With it off nothing changes, so the guest stays bootable at
every step.

| Piece | Where |
| --- | --- |
| Guest windows (`0x6_0000_0000`–`0x100_0000_0000`) and placeholder claim/restore/fault-claim | `SharpEmu.HLE/Host/GuestAddressWindows.cs`, `GuestPlaceholder.cs` |
| Parent-side reservation into the suspended child, child-side verification with hole reporting | `SharpEmu.Core/Memory/GuestAddressSpaceReservation.cs` |
| Suspended create → reserve → resume, and the startup gate | `SharpEmu.CLI/Program.cs`, `SharpEmu.GUI/EmulatorProcess.cs` |
| Host allocations claim placeholders instead of failing; frees restore them | `PhysicalVirtualMemory.CrossPlatformHostMemory`, `WindowsHostMemory` |
| Fixed-range allocation claims placeholder segments | `PhysicalVirtualMemory.TryAllocateFixedThroughGranules` |
| Lazy-commit faults claim a 2 MiB placeholder window (placeholders cannot be committed in place) | `DirectExecutionBackend.Exceptions.cs` |
| Stubs near guest code claim placeholders | `DirectExecutionBackend.TryAllocAt` |
| `munmap` / `munmap_partial` trace under `SHARPEMU_LOG_DIRECT_MEMORY=1` | `KernelMemoryCompatExports.KernelMunmap` |
| **Shared direct backing (2026-09-15):** one `SEC_RESERVE` pool section, ≤2 MiB views, partial-unmap split with bounded fault retry, zero-on-reuse | `SharpEmu.HLE/Host/GuestSharedBacking.cs`; contract in [Appendix G](#appendix-g-direct-memory-allocation-and-mapping-contract) |
| Map/unmap wiring — every `sceKernelMapDirectMemory` (and `BatchMap`, which re-enters it) attaches to its physical offset; `munmap` detaches; `map_direct_shared` trace says which mappings really are shared | `MapDirectMemoryCore`, `KernelMunmap`, `PhysicalVirtualMemory.TryMapSharedDirect` / `TryUnmapShared` |
| Works without `SHARPEMU_RESERVE_GUEST_VA`: an ordinary private allocation fully inside the range is released and retaken as a placeholder under the mapping lock | `GuestSharedBacking.TryRetakeAsPlaceholder` |
| `GuestPlaceholder.IsPlaceholder` reported the queried page as the placeholder base, so a claim ending at the placeholder's end skipped its split and failed | fixed to `AllocationBase` |

PRT needs no separate route: in `race30`, 8,376 of ~8,870 direct maps land inside
the two apertures (`0x29_0000_0000` +4 GiB, `0x30_0000_0000` +8 GiB), all
`len=0x10000`, all through `MapDirectMemoryCore`. `TryPreReservePrtAperture` has no
call site.

Measured with the flag on: 1,000 GiB reserved before the CLR starts, 6 host
holes totalling ~3 MiB (the initial thread's stack cluster), and GT7 boots to
Vulkan video output with no rel32 warnings and no native exception.

Two traps found while integrating, both worth remembering:

- **The backend in use is `CrossPlatformHostMemory`** (nested in
  `PhysicalVirtualMemory`), not `WindowsHostMemory`; a placeholder path added
  only to the latter never runs.
- **Stubs must stay within rel32 of guest code.** `TryAllocateNearEntry` probes
  addresses next to the image, which the reservation owns; without a claim path
  it fell back to an address 2 TiB away, every TLS patch logged
  `out of rel32 range`, and the guest executed garbage (an execute fault at
  `0x848942FFB`). A reservation is not complete until everything that *needs* to
  live inside the guest window can still get there.

Still to do: the shared section for direct memory (aliases), the 2 MiB view
model with the fault-retry protocol, `sceKernelMunmap`/release on views, the low
mappings below `0x6_0000_0000`, the PRT aperture, and POSIX.

`race15` (reservation on, driven): reached the calibration wizard and parked
there, with **no** `no host mapping`, **no** `reserved=False`, and **no**
`0x806DFAB08` waiters — the collision class this work removes did not occur. It
never reached the streaming stage (no `0x143C00000` arena request, no PRT range,
no `Strea` thread), because the driver script's presses went to a minimised
window. A clean run with the reservation **off** (`race16`) parks on the same
wizard screen, so that is the script's problem, not the reservation's.

**Driving the wizard by hand works** (window focused, verified by the guest's own
`present-*.bgra` dumps rather than window captures, which are placeholder
images):

1. `cross` a few times through the opening dialogs.
2. `right`×3 then `cross`×2, then `cross` — display pages.
3. `stickright` **held ~1.5 s** twice, then `cross` — the slider page; taps do
   nothing and the pad trace shows `lx` stays 128 unless the key is held.
4. `right` then `cross` — the confirm pop-up takes the right-hand button; a bare
   `cross` re-opens it (its highlight returns to the left button, which is how
   you can tell).
5. Repeat 4 for the second pop-up; GT7 then shows its loading wheel.

`sceKernelMunmap` is never called by GT7 in a full boot-to-load run
(`munmap` count 0 in `race15`), so the partial-unmap retry path is a safety net
rather than a hot path — as hoped when the protocol was settled.

#### Next (2026-09-14)

1. **Implement `sceHmd2ReprojectionQueryDisplayBufferSizeAlign`** (and look at
   `sceHmd2ReprojectionInitialize`) from researched hardware values: size and
   alignment returned in `rax:rdx`. This removes the malformed allocation at its
   source; then re-check pool accounting.
2. Keep host memory out of PS5 user space (see "`race14`'s deadlock"): reserve
   `[0x10_0000_0000, 0x100_0000_0000)` at startup and release pieces to guest
   fixed mappings, plus a startup check on the main thread's stack. Until then
   a run can deadlock or not depending on ASLR; check a capture's
   `no host mapping` count before reading anything into its stall.
3. Build shared backing (design above), independent of the stall.
4. For `race12`'s state if it recurs: what `Strea` waits on (return
   `0x80077C227`) and what `Job#2` does while holding `0xE418577F88` (caller
   chain from `0x8002CD250`), checking for a retry loop before calling it slow.
5. Run the validation fix on the old 16 GiB pool to learn whether the pool
   change matters for GT7.

### Reproducing it

`artifacts/gt7-opus-20260914/drive-race.ps1 -Name <capture> [-ExtraEnv @{...}]`
drives boot → dialogs → calibration wizard → carousel → first tile → race load
unattended and prints `RACE LOAD STARTED: True/False`.

Two things any driver must get right:

- The wizard's slider moves only with a **held stick** (`stickright`, ~1 s). The
  d-pad nudges it a few pixels and the page never advances.
- On a fresh save only the **first** tile on the album carousel starts a race.
  Cross on any other tile does nothing, so walk left to the end first.
- The confirm pop-up after the wizard takes the **right-hand** button; Cross
  alone reopens it.

Neither script in the tree is reliable end to end yet; expect to finish the last
steps by hand:

- `drive-race.ps1` uses fixed sleeps (currently 22 s boot, 18 s load). Too short
  and presses land on the wrong screen (`race5`); too long wastes minutes.
- `drive-fast.ps1` waits for `guest_reads=1000` in the log before pressing, then
  sends 100 ms taps. Below ~5 fps (`race11` ran at 2.2) the guest samples the pad
  too rarely and taps are missed; hold keys ≥400 ms when frame rate is low.
- The slider page advanced on Cross alone in `race7` and `race10` but needed one
  held `stickright` in `race2` and `race11`.
- The loading wheel animates, so "the frame changed" does not mean the load
  finished. The load screen is blue-dominant and the carousel neutral grey; wait
  for the mean blue−red of the newest dump to drop below ~12 before pressing.

### The deadlock

From the first stall snapshot after the race load starts, and identically in
every snapshot after (`fix1`, `race2`, `race3`, `race4`, `race5`):

- Three `FWRKR` workers block in `scePthreadMutexLock` on `0x806DFAB08`.
- The owner is a fourth `FWRKR`, itself in `scePthreadCondWait` on an event object
  in **its own stack frame**, holding the lock while it waits.
- `Job#0` (cond wait), `Job#1`–`5` (`sceKernelWaitSema` id 3), `RDisp`, `Strea`
  (4 imports — created and immediately parked) and every `WorkT` are stopped.
- No faults, no `pthread_mutex_abandon`, no unresolved imports, GPU idle.

The event helpers (Ghidra, `gt7_text.bin` @ `0x800000000`):

- `0x80077C7E0` wait: mutex `+0`, condvar `+8`, `waiting` flag `+0x18`,
  `signalled` flag `+0x19`. Locks; returns at once if `signalled`; else sets
  `waiting`, waits, clears, unlocks.
- `0x8009BBCC0` signal: locks; signals the condvar if `waiting`, else sets
  `signalled`. (Never actually called in any traced session — the live signal
  helper is elsewhere; candidates `0x80409D800`, `0x80409A1D0`, `0x80415D340`.)

Both sides hold the event's own mutex, so the guest pattern has no lost-wakeup
window; the waiter is simply never signaled.

### The call immediately before the wait

```
Import#600093109 result: ORBIS_GEN2_ERROR_NOT_FOUND (rVjRvHJ0X6c)
  rdi=0x0000000009AA0000 rsi=0 rdx=0x00007FFFDB9FFB08 rcx=0x48 ret=0x8040A48C0
```

`rVjRvHJ0X6c` is `sceKernelVirtualQuery`, which SharpEmu implements — this is our
own NOT_FOUND, **not** a missing export. Caller `sub_8040A4820` (entry found by
prologue scan; the call-target heuristic mis-attributes it to `0x8040A4500`):

```c
r = sceKernelVirtualQuery(addr, 0, &info, 0x48);
if (r == 0 && (info[32] & 2) && (info[24] & 0xC0))   /* direct memory + GPU bits */
      → queue the work (the path that ends in signaling the event)
else if (size <= 0x1000) → small staging-buffer fallback
else → return 0, nothing queued
```

`info[32]` is the state byte this HLE writes (`0x02` = direct), `info[24]` the
protection. So one NOT_FOUND for a buffer larger than 4 KB drops the request, and
the event behind it is never signaled. This is the strongest lead: it is the last
thing the stuck worker does before parking, and it happens twice (once per worker).

### Root cause chain (2026-09-14, Opus, after Codex's follow-up)

The low values are **archive offsets, not addresses**, and the failure starts
upstream with a refused direct-memory allocation.

Live reads of the hung `race7` process (`ReadProcessMemory` only, PID from
`tasklist`) on the stuck worker's stack (`0x7FFFDB1FF900`):

- the failing pair is `{0x00F25000, 0x007B0750}`, and the frames above it are
  `0x800BDF13C` → `0x800BDECD9` → `0x800BDE784` → `0x800BDDD8F`, i.e. the
  `0x800BDD…`/`0x800BDE…`/`0x800BDF…` read subsystem;
- the request's path string is `crowd/amsx/0014.amsx`;
- a healthy sibling request (`0xE41B6ADD10`, work fn `0x800BDDB60`) carries
  `+0x50 = 0xE41B6AD880` — a real high pointer with size `0x218`, written by the
  caller-supplied path at `0x800BDF656`;
- the descriptor holding the low value is `0xE41708FD60` (`+0x10 = 0x00F25000`),
  pointing at `0xE41B717E20` whose `+0x38` is `0x143C00000`.

`0x143C00000` is **not** a base: the log shows it as a length.

```
allocate_direct: len=0x0000000143C00000 align=0x200000 type=0x0C
  selected=0x0 result=ORBIS_GEN2_ERROR_TRY_AGAIN
Import#674119061 (rTXw65xmLIA sceKernelAllocateDirectMemory)
  searchStart=0 searchEnd=0x400000000 len=0x143C00000 align=0x200000
```

GT7 asks for 5.06 GiB of direct memory for its streaming arena and SharpEmu
refuses it. That refusal **is** the trigger. An earlier correction here said the
title recovers by retrying at 4.79 GiB; in `race7` the next allocation is indeed
4.79 GiB and succeeds, but in `race9` the live arena object still had size
`0x143C00000` and base 0, so the offsets it hands out become pointers. Why the
pool was short is in "Fix and result": an invalid 6152 MiB allocation SharpEmu
should have rejected.

Two other readings in this section were wrong and stay only as history. The
pool was **not** leaking: the title frees through
`sceKernelCheckedReleaseDirectMemory` (82 calls in `race9`, none rejected), which
was untraced — hence "zero releases". And the first-fit search is correct:
repeated start offsets are re-allocations after those frees.

`0x8040970C0` is **not** the writer for this request: an exec watch on its store
(`0x8040970E5`) fired 22 times in one session with `rax=0xEC050DC000` (a proper
high address, size `0x577`) and **zero** times in `race7`, which still hung.
The requests are built by `0x80057B8D0`, which zeroes `+0x258…+0x270`.

Other refusals in the same run are correct: the title also probes `0x782000000`
(30 GiB) and `0x3ED400000` (15.7 GiB), both larger than the whole pool.

#### Open question for the fix — answered in "Fix and result"

Whether the 5.06 GiB refusal is genuine exhaustion or fragmentation of our
16 GiB pool (`DirectMemorySizeBytes`, first-fit in
`TryFindAllocatableDirectMemoryRangeLocked`). In `race7` the successful
allocations sum to 52.8 GiB with a highest end offset of exactly `0x400000000`,
and 55 allocations reuse a start offset, so the title does release and reallocate
(releases are simply not traced). New diagnostic `direct_memory_exhausted`
(under `SHARPEMU_LOG_DIRECT_MEMORY=1`) prints used/free, the largest contiguous
gap and its position on every refusal; run it before changing allocator policy.

### What the queried addresses are — superseded by the section above

`SHARPEMU_LOG_DIRECT_MEMORY=1` adds `map_direct` / `map_flexible` / `allocate_direct`
traces and (new, this session) `virtual_query_miss`. In `race5`:

```
virtual_query_miss: addr=0x0000000000F25000 regions=644 next=0x0000000001C00000+0x05FA0000 direct=True
virtual_query_miss: addr=0x0000000009AA0000 regions=644 next=0x0000000600000000+0x00004000 direct=True
virtual_query_miss: addr=0x000000000A867000 regions=644 next=0x0000000600000000+0x00004000 direct=True
```

- The title *does* map direct memory at low VAs (`0x01C00000`, len `0x05FA0000`,
  flagged direct), so low addresses are not inherently wrong.
- `0x09AA0000` and `0x0A867000` sit just past that region's end (`0x07BA0000`);
  `0x00F25000` sits below its start. Nothing covers them, and the next region is
  far away at `0x600000000`.
- Of 560 mappings in that run, only 3 were placed at an address other than the one
  requested, and all three were `requested=0` (title let the kernel choose), so
  "we moved a fixed mapping" is **not** supported.
- I noticed `0x09AA0000 == (0x29AA00000 & 0xFFFFFFFF) >> 4` for one allocation and
  briefly took it for a pattern; the other two addresses do **not** match any
  allocation under that rule, so treat it as coincidence.

So: where the title gets these addresses is still unknown. That is the next
question, and the one that decides the fix.

Suggested next steps:

1. Trace the source of the argument. `sub_8040A4820`'s 6th parameter carries it
   (`uStack_c0 = param_6`); decompile its callers and find who computes it. A
   runtime probe (`SHARPEMU_PROBE_IMPORT_RET_ADDRESS=0x8040A48C0`) logs the
   caller frame at each failing call.
2. Check whether the addresses belong to a mapping the title makes through a path
   that never reaches `KernelMemoryCompatExports` (AGC/Gnm-side allocation, PRT
   aperture at `0x3000000000`, or a `sceKernelMapDirectMemory2` variant).
3. If they are legitimately mapped on hardware, the fix is to make the region
   table cover them (and report `direct=true` + GPU protection bits), not to
   special-case the answer.

### Ruled out (don't redo)

- **Stale HLE lock owner / unlocks bypassing the HLE.** `SHARPEMU_LOG_PTHREAD_MUTEX_FILTER=0x806DFAB08`
  shows balanced traffic and the owner acquiring by hand-off (`lock-resume`) and
  never unlocking; 95 locks vs 272 unlocks overall in `race2`.
- **Signal delivered but mutex hand-back lost.** New `pthread_cond_wake_ungranted`
  diagnostic fired 0 times at the hang. (During boot it fires ~124k times with
  `count=1` each — ordinary churn across engine threads, hence env-gated.)
- **GPU deadlock.** At the hang there are no outstanding `agc.wait_suspended`
  waits and `agc.deadlock_break` never fires. Compute queues do stop earlier
  (line ~458k, before the carousel) while graphics keeps running; menus work
  fine afterwards, so that is a consequence, not the cause.
- **`PCL Event` freezing is not a defect** — in `race3` it resumed and climbed to
  121,545. §6bq/§6br treat it as a symptom; that reading is corrected in §6bs.
- **Deferred completion actions are not dropped** — `TryExecuteOrderedGuestAction`
  requeues at the queue head and `TryTakeGuestWork` round-robins queues.

### Uncommitted changes on this branch from this session

Diagnostics plus the two memory fixes; suite green (1,119 passed) after each.

| Change | File |
| --- | --- |
| Stall snapshot skips exited guest threads (count line) and lists up to 1024 — with the old cap of 48 the race-load threads were never printed | `Core/Cpu/Native/DirectExecutionBackend.cs` |
| `block=pthread_mutex_lock owner=0x…('name') waiters=N` on a blocked cooperative lock | `Libs/Kernel/KernelPthreadCompatExports.cs` |
| `pthread_cond_wake_ungranted` (env-gated, `SHARPEMU_LOG_PTHREAD_COND_WAKE=1`) | same |
| `SHARPEMU_LOG_PTHREAD_COND_SITE=<hex,…>` — trace cond waits/signals by guest call site, since condvar addresses move between boots | same |
| `virtual_query_miss` under `SHARPEMU_LOG_DIRECT_MEMORY=1` | `Libs/Kernel/KernelMemoryCompatExports.cs` |
| Direct-memory allocate rejects a length or non-zero alignment that is not a multiple of 16 KiB (EINVAL), per KytyPS5 | `Libs/Kernel/KernelMemoryCompatExports.cs` |
| Direct-memory pool 13,376 MiB (13,824 MiB total − 448 MiB flexible), per KytyPS5 | same |
| `direct_memory_exhausted`, `release_direct`, `checked_release_direct`, `virtual_query_miss_stack` under `SHARPEMU_LOG_DIRECT_MEMORY=1` | same |
| Exec-watch hits also log `rax`, `rbx`, `rcx`, `rdx` | `Core/Cpu/Native/DirectExecutionBackend.WriteWatch.cs` |
| `drive-race.ps1`, `drive-fast.ps1` (new, ignored tree) | `artifacts/gt7-opus-20260914/` |
| Regression test for the allocate validation | `tests/SharpEmu.Libs.Tests/Kernel/KernelMemoryCompatExportsTests.cs` |

Captures: `artifacts/gt7-opus-20260914/{fix1,race1,…,race14}` (ignored).
`race5`, `race9`, `race11`, `race12` and `race14` ran with
`SHARPEMU_LOG_DIRECT_MEMORY=1`; `race13` did not. `race12` is the first run with
the validation fix. The artifacts tree is gitignored, so ripgrep-based search
skips these logs; use plain `grep`.


## Appendix E. Codex follow-up: race-load hang (2026-09-14)

Status: **not fixed**. Read-only investigation of the existing `race5` process
(PID 66092), its saved stacks, the log, HLE source, and targeted Ghidra
decompilation. No emulator source changes, no restart, no debugger attachment,
and no new test-suite run. The earlier 1,119-pass result remains Opus's result.

### What is now established

The missing-query addresses are already present in guest read requests. They
are not introduced by `KernelVirtualQuery` or its import argument handling.
The immediate caller also really does ignore submission failure and then wait.

Live `ReadProcessMemory` captures give these buffer/length pairs:

| Query buffer | Read length | Pair address on worker stack |
| --- | --- | --- |
| `0x00F25000` | `0x08B7B000` | `0x7FFFDB1FFE70` |
| `0x09AA0000` | `0x00DC7000` | `0x7FFFDB5FFE70` |
| `0x0A867000` | `0x007B0750` | `0x7FFFDB9FFCC0` |

The first two lengths exactly connect all three addresses:

```text
0x00F25000 + 0x08B7B000 = 0x09AA0000
0x09AA0000 + 0x00DC7000 = 0x0A867000
```

This suggests consecutive slices of one buffer/arena. It does **not** establish
the arena base, mapping legitimacy, or a physical-address encoding.

For the first two workers, the actual request objects are:

| Request | Owner (`request+0x18`) | `request+0x50` | `request+0x58` |
| --- | --- | --- | --- |
| `0xE41B52FD50` | `0xE41B52FB40` | `0x00F25000` | `0x08B7B000` |
| `0xE41B630A90` | `0xE41B630880` | `0x09AA0000` | `0x00DC7000` |

Both have vtable `0x8059023C8` and work function `0x800777BC0` at `+0x38`.
They are embedded at `owner+0x210`, so `request+0x50 == owner+0x260`.
The third worker uses a different work function (`0x800BDDB60`) and reaches
the same read backend through `0x800BDF0A0`; do not assume its request layout
puts the low buffer at `+0x50` too.

### Confirmed caller chain

For the first two workers:

```text
0x800777970 dispatches request->work_function
  0x800777BC0 loads request+0x50 into R15
    constructs {R15, read_length} at [RBP-0x40]
    calls 0x80077DAD0 (return 0x800777C82)
      virtual +0xC0 -> 0x80077DBA7
        virtual +0xB8 -> 0x8040A00A0
          virtual +0x30 -> 0x8040A4820
            sceKernelVirtualQuery (return 0x8040A48C0)
          waits at 0x8040A0235, return from event wait = 0x8040A0240
```

`0x8040A00A0` passes `*param_3` as argument 6 to the virtual submission method.
`0x8040A4820` queries that argument and returns zero without queuing if the
query fails and the size exceeds 4 KB. `0x8040A00A0` never checks that return:
after callback-object cleanup it unconditionally calls `0x80077C7E0` on its
stack event. This connects the rejected submission to the indefinite wait.

The raw listing matters: the untyped decompile of `0x800777BC0` omits the
buffer pair from its displayed call to `0x80077DAD0`. The instructions at
`0x800777C6F` through `0x800777C7D` explicitly build and pass it. Do not infer
argument absence from this decompiler artifact.

### Allocator lead investigated, but not established as the producer

A static writer of `owner+0x260` exists at `0x8040970C0`:

```c
size = *(uint64_t *)(owner + 0x190);
buffer = sub_800209060(0x80, size);
*(uint8_t *)(owner + 0x270) = 0;
*(uint64_t *)(owner + 0x260) = buffer;
*(uint64_t *)(owner + 0x268) = size;
```

Its allocator calls thunk `0x804194870`. Reading the live thunk chain showed:

```text
0x804194870 -> GOT [0x805911A10]
            -> 0x700000003580 (movabs/jmp bridge)
            -> guest libc 0x80817A140
            -> function pointer [0x808314F18]
            -> game hook 0x80061B8C0
```

Thus this path runs the guest's allocator, not HLE `Memalign`. For requests
at least 2 MB, the game hook uses `0x80061D000` with arena `0x806E1B800`,
rounding size/alignment to at least `0x4000`. Its record array is at
`0x600000000`; records are `0x20` bytes, starting with address and length.
The allocator returns the record's address directly.

**Counterevidence to treating this as solved provenance:** the queried
`0x00F25000` and `0x0A867000` are not 16 KB aligned. The captured first 64 KB
of this arena's record array contain high-address allocations (examples
`0xF4018D4000`, `0xF410584000`) and no nonempty range covering any queried low
address. We have not observed `0x8040970C0` executing for these two requests.
Another writer, a later overwrite, or another allocation path remains possible.

### What to do next

1. Trace the assignment of **`request+0x50` / `owner+0x260`** for the first
   two read requests. Establish whether it comes from `0x8040970C0` or another
   writer. Capture the full 64-bit producer value and any base-plus-offset
   computation; the contiguous slices make a missing base worth checking.
2. If it is the allocator path above, compare its returned record address
   with the value eventually written to the request. Trace `0x80061D000`
   and `0x80061C980` only once this path is confirmed for the bad buffers.
3. Keep `VirtualQuery` behavior unchanged until the address is proven valid.
   The current evidence does not justify inventing low direct/GPU mappings
   or changing the HLE `memalign` path. Acceptance still requires the race
   to finish loading, not merely the worker to wake.

### Captures and reproduction of this analysis

All new captures are under `artifacts/gt7-opus-20260914/` (ignored):

- `codex-stack-{7fffdb1ff900,7fffdb5ff900,7fffdb9ff900}.bin`
  are 0x700-byte stack reads at the address encoded in each filename.
- `codex-request-{e41b52fd50,e41b630a90,e41b52f690}.bin`
  are 0x70-byte request reads.
- `codex-query-decomp.log`, `codex-callers-decomp.log`,
  `codex-buffer-listing.log` contain the confirmed query-to-wait chain.
- `codex-producers-decomp.log`, `codex-setter-decomp.log`,
  `codex-allocator-decomp.log`, `codex-hook-decomp.log`,
  `codex-arena-decomp.log` contain the candidate producer investigation.
- `codex-arena-{806e1b7b0,806e1b800}.bin` contain the first 64 KB of
  each arena's **record array**, not the arena object itself.
- `codex-libc-live.bin` is 0x1000 bytes at `0x80817A000`;
  `codex-libc-decomp.log` decompiles its indirect wrapper.

The process was opened with `OpenProcess(0x410, false, 66092)` and only
`ReadProcessMemory` was used. No `DebugActiveProcess`, thread suspension,
or process-memory writes. Re-identify the PID/addresses after any restart.

Ghidra: `C:/Tools/ghidra_12.1.3_PUBLIC/support/analyzeHeadless.bat`, existing
project `artifacts/gt7-ghidra/project gt7`, `-process gt7_text.bin -readOnly
-noanalysis -scriptPath scripts/ghidra -postScript DecompAt.java <addresses>`.
For writable tool settings/cache in this workspace, set `JAVA_TOOL_OPTIONS`
to `-Duser.home=<repo>/artifacts/ghidra-home
-Dapplication.cachedir=<repo>/artifacts/ghidra-home/cache` and set this shell's
`APPDATA` and `LOCALAPPDATA` to `<repo>/artifacts/ghidra-home/AppData`.
The separate `codexlibc` project contains only the captured wrapper page.


## Appendix F. GT7 race load — session record, 2026-09-15

Goal for the session: get GT7 into a race. **Not reached.** The load now runs
much further and fails on a different, diagnosed cause. Full detail is in
[boot-investigation.md](boot-investigation.md) §6bt–§6bx; this is the short
version and the honest ledger.

### Where the race load got to

| before | after |
|---|---|
| malformed 6 GiB direct-memory request; arena starved | arena allocates and maps, pool healthy |
| streaming emitted **zero** reads, `Strea` parked on an unsignalled event | **2,572 full-size** gather/scatter reads |
| — | crash in car/physics setup on a corrupted pointer, or a timing-dependent stall |

Three distinct failures, each later than the last.

### Fixed (verified in live runs, tests added)

**1. `sceHmd2Reprojection*QueryBufferSizeAlign` / `…QueryDisplayBufferSizeAlign`**
(§6bt). GT7 used the unresolved import's error code as a size/alignment pair.
Required a backend fix first: the import trampoline pops the guest's RDX back
from `argPack+16`, so no handler could return a 16-byte aggregate at all —
`CpuContext.SetReturnPair` + `StoreImportPairReturn`. Verified: `ret=0x800FEE9CB`
now allocates `0x2000000`/`0x200000` and maps it, 0 unresolved HMD imports.

**2. `sceAmprMeasureCommandSizeReadFileGatherScatter` and the command it
measures** (§6bu). 799 unresolved calls; the caller folds the returned size into
an **unsigned** free-space test, so `NOT_FOUND` made the emit loop submit and
re-request buffers forever without ever writing the read — `Strea` then waited on
a completion nothing could signal. ABI taken from GT7's own wrapper at
`0x801923630` (register shuffle + tail-jump), not guessed. Verified: 2,572 reads,
every one full-size, correct file and offsets.

### Diagnosed, not fixed — the current blocker

Aliased direct mappings (§6bx). **Now evidenced for the race load**, where the
docs previously said "not shown". It was invisible because `map_direct` logged
the address *of* the out-pointer rather than the mapped address; the new
`map_direct_result` trace shows **30 physical ranges mapped at more than one
guest VA** in `race30`, each backed by private host pages, so a write through one
view is invisible through the others.

That is the mechanism behind `race28`'s crash: `mov rcx,[rcx+0x11D8]` with
`rcx = 0xEE`, where `rcx` came from a field inside a direct mapping.

Supporting, and both contradicting previously recorded assumptions:

- `munmap_partial` is a **hot path** — 669 calls carving the streaming arena
  2 MiB at a time. The docs record `munmap` count 0 for a boot-to-load run;
  that was measured before the load ever streamed.
- Both PRT apertures are configured. Sparse residency is structural, so the
  aliasing is not incidental.

**Fix:** shared backing for direct memory. Guest code runs natively, so guest VA
*is* host VA — two views of one physical range can only share bytes through OS
sections. There is no cheap variant. The host primitives already exist and pass
(`tests/SharpEmu.Libs.Tests/Memory/SharedBackingSectionTests.cs`), but nothing in
the guest map path uses them; wiring them in means a section per physical range,
view splitting for those 669 partial unmaps, the fault-retry protocol, the PRT
aperture, and a POSIX equivalent. Design in
[Appendix D](#fix-design-shared-backing-for-direct-memory-not-started).

### Open gap in the evidence

`race28` (the crash) ran without direct-memory logging; `race30`/`race31` (with
the census) did not reproduce it — the failure is timing-dependent, crashing in
some runs and stalling in others from the same build and route. Tying the
faulting `rax` to the alias list still needs one run with both. Three attempts
did not land it.

### Corrections made to earlier conclusions

- **"The gate at `sub_800A05650` never passes"** — wrong, measured in a run that
  never started the load. It passes 105 times once the load runs (§6bw).
- **"Only the first tile starts a race, and the driver walks to it"** — the walk
  was dropping presses. The carousel is interactive; 90 ms taps are missed at
  ~19 fps, so several runs pressed Cross on the wrong tile and still reported
  "race load started" from an unrelated `Strea` thread. 260 ms holds fixed it.
- **A watch on a hot instruction is not free** — `0x800A056FB` trapped 29,308
  times and froze presentation. That run's "stall" was the probe, not the guest.

### Tooling added (2026-09-15)

- `SHARPEMU_WATCH_GUEST_EXEC_PROBE=<spec>[,…]` — reads guest memory at each
  execute-watch hit, deref specs starting from a register in the trapped frame
  (`[rdi+8],[[rdi+8]]+F8`). Resolves a gate flag several hops off an argument in
  one boot instead of one boot per hop.
- `map_direct_result` trace — the mapped address, which is what makes aliasing
  visible at all.
- `drive-race2.ps1` + `sig.py` — frame-driven boot → wizard → carousel → first
  tile, keyed on what the guest presents rather than fixed sleeps. The old
  `drive-race.ps1` pressed during boot and desynced the dialog stack, which is
  why several earlier runs never left the wizard.

### Not attempted

The menus. Text still renders no glyphs (Blocker A), which is why the wizard has
to be driven by frame signature rather than by reading it. That was not the easy
case.

### State

Suite green, 1,140 tests, including new `AmprGatherScatterTests` and
`Hmd2ExportsTests`. Nothing committed. Captures `race18`–`race31` under the
ignored `artifacts/gt7-opus-20260914/`.

### Shared direct backing wired in (afternoon)

Direct mappings now share pages through one `SEC_RESERVE` pool section; the
contract (allocate / map / unmap / release) is in
[Appendix G](#appendix-g-direct-memory-allocation-and-mapping-contract). Suite 1,154, including
14 `SharedDirectMappingTests` through the real memory layer and the kernel
exports. Each GT7 run below found one real emulator bug; each fix is general and
has a regression test reproducing the run's exact sequence.

| Run | Symptom | Cause | Fix |
| --- | --- | --- | --- |
| `race32` | native exception at boot (baseline `race30`/`race31`: none) | memcpy spanning two adjacent 2 MiB mappings: shared views are separate regions, and `TryCopy`/`TryRead`/`TryWrite` resolved a span to one region only | managed read/write split a span across contiguous regions |
| `race33` | Job#0 null read after first dialog | release left mappings attached; zero-on-reuse then cleared pages still visible through them. All 37 releases hit mapped ranges, 0 munmaps | release detaches the overlapping part of every mapping (matches ShadPS4 `MemoryManager::Free` and KytyPS5 `ReleaseDirectMemoryInternal`) |
| `race34` | memcpy `MEMORY_FAULT`, then crash | fixed map over holes left by two releases went through the private claim path, which threw; map returned `NOT_FOUND` | fixed direct maps over an unbacked range take the shared path first |
| `race35` | none — 393 shared maps, 3 private fallbacks, 0 exceptions, 0 memory faults; reached the carousel | — | — |
| `race36` | race load started (`Strea`), then Job#1 null read at `0x801B9F0AD` — registers identical to `race33` | lazily committed views: the first touch of a page inside SysV leaf loop `0x801B9F010` faulted, and Windows dispatched the fault on the guest stack over the red zone holding the array base (`[rsp-0x28]`). Zeroing ruled out (no recycled allocation overlaps the arena); aliasing ruled out (152 overlapping pairs, none in the arena) | views are committed at map time, as the private backing was |
| `race37` | **race load streamed to completion** — 8,869 shared maps, 669 partial unmaps, 72 release detaches, 0 memory faults — then Job#5 faults at `0x8002CDC4B` on a zero switch word `[rbx+0x18A]` | same context as old-build `race28` (last import `scePthreadMutexUnlock` → `0x80034F561`); not a backing bug: the recycled range's old mapping was detached before reuse | open — README blocker E |

Also fixed: `GuestPlaceholder.IsPlaceholder` reported the queried page as the
placeholder base (`VirtualQuery.BaseAddress`, not `AllocationBase`), so a claim
ending at a placeholder's end skipped its split and failed.

Ruled out / corrected:

- **"PRT needs its own route."** 8,376 of ~8,870 direct maps in `race30` land in
  the two PRT apertures, all through `MapDirectMemoryCore`; they are shared by
  the same path. `TryPreReservePrtAperture` has no call site.
- **"Release leaves views mapped."** Written into the first draft of the contract
  without evidence; `race33` and both reference emulators contradict it.
- **`race35`'s driver reported `NOFRAME` throughout.** Not a stall: `sig.py`
  resolves `{name}/images` against the working directory, and the driver was
  launched from the repo root. Run `drive-race2.ps1` from its own directory.

The 3 remaining private fallbacks (16/64 KiB maps at `0xE3FFFFC000` and
`0x6_0000_0000`) overlap no shared map, so none is a broken alias.

#### Blocker E follow-up (race37–race39)

- **Red-zone mechanism validated.** Lazy-commit fault traces (first 16, then every
  256th): `race32`–`race36` (lazily committed views) log 16–18 each, i.e. hundreds
  of native first-touch faults during guest execution; `race37`/`race39`
  (commit-at-map) and old-build `race30` log **0**. No sampled fault is inside the
  `0x801B9F0xx` leaf itself, so the link to that crash rests on the mechanism plus
  the signature disappearing after the fix, not on a captured fault.
- **Physics-job crash (`race37`, `0x8002CDC4B`) mapped:** `sub_8002C00F0` calls
  `sub_8002CDAA0([fs-0xE0], …)`, which switches on `word[obj+0x18A] - 0x1700`.
  `[fs-0xE0]` is the job's current state object, set by the job-context allocator
  `sub_8000C9A00` (`[fs-0xE0] = desc[2]`, `[fs-0xD8] = desc[3]`) after popping a
  context from a lock-free tagged free list and `memset(ctx, 0, 0x24D0)` (the only
  write to the watched field in `race38`). `0x1700`/`0x1703` writers save, set and
  restore the word on `[fs-0xE0]` (static default object when null); none wrote the
  watched object.
- **Ruled out for E:** lazy commit (0 faults in `race37`); AGC `wait_suspended`
  (present in crash-free `race30`/`race31` at higher counts, deduplicated per
  label); zero-on-reuse and aliasing (no recycled range or alias overlaps the
  arena; the struct's recycled range was detached before reuse).
- **Not usable:** a fixed-address write watch — the context free list hands out
  different contexts per boot (`race39`: zero hits). `race39` also crashed at a
  different site (`0x801F90460`) with an inconsistent report (write to 0 at
  `push rbp`, `rsp` valid, frame #0 returning into a different call), so it is not
  treated as evidence.
- **`race41` (clean repeat, current build):** load streamed completely again
  (8,888 shared maps, 669 partial unmaps, 0 lazy faults), then **Job#5** faulted in
  the same function `sub_8002CDAA0` (`0x8002CE519`, a switch case reading
  `[rdx+0x14]` with `rdx=0x38D`), same jump table, same last import, same fs base
  `0x7FFDFD300000` as `race37`. The state object differs (`0x1005B66900` vs
  `0x10017C0D80`) and its type word is garbage rather than zero — so the object in
  `[fs-0xE0]` is wrong or stale, not one field left unset. Always the same worker:
  Job#5 (`race37`, `race41`), Job#4 in old-build `race28`.
- **Ruled out:** static TLS overlap (GT7 `total_static=0x570` vs the `0x20000`
  reservation); a missing CPU-id/affinity import (`race41`'s 21 unresolved NIDs are
  VR tracker, Share, NetResolver, TTS, HMD2 init, RUDP, NP session and 7 uncatalogued
  — `sceKernelGetCurrentCpu` `g0VTBxfJyu0` is not among them).
- **`race40`** did not start the race: the driver's carousel check accepted a
  bright transitional wizard frame. `drive-race2.ps1` now also requires the
  carousel's `4444` top row.
- **`race42` (exec probe on the allocator tail `0x8000CA086`, `[rbx+188]`):** sampled
  state objects arrive with a **valid type word `0x1701`** (qword at `+0x188` =
  `0xFC00FFFF17010000`), so the crashing workers' objects are the anomaly, not the
  normal case. The probe site is too hot — presentation dropped to 0 fps and the
  run was stopped; do not exec-watch this site again. Only three real writers of
  the per-thread `[fs-0xE0]` slot exist: the allocator tail and two stores at
  `0x803FB15DF` / `0x803FB1687` (the other 13 static hits are `rbp` locals).

#### Blocker E root cause: fibers carried the suspending thread's fs

GT7 runs job work as fibers (`sceFiberRun` `a0LLrZWac0M`, `sceFiberSwitch`
`PFT2S-tJ7Uk`, `sceFiberReturnToThread`, `sceFiberGetSelf` are all imported), and
its job system migrates suspended fibers between the Job workers. Around a switch
it detaches and re-attaches its own TLS job state: `sub_803FB15B0` moves
`[fs-0xD8]`, `[fs-0xE0]`, the top of the `[fs-0xE8]` context list and `[fs-0xC8]`
into a save block and clears them; `sub_803FB1630` pushes them back onto the
current thread. Both are reached only through a table (one eboot-relative pointer
each, no direct calls).

SharpEmu's `FiberExports.CaptureContinuation` (and the initial continuation)
recorded `ctx.FsBase`, and `ApplyGuestContinuation` restores a non-zero `FsBase`,
which `BindTlsBase` then binds as the native guest thread pointer. A fiber resumed
on a different worker therefore ran on the suspending worker's TLS block, so the
re-attach wrote into the wrong thread and one worker was left with a stale
`[fs-0xE0]` state object — the invalid type word in `race37` and `race41`.

Hardware behaviour, per ShadPS4: its fiber switch (`fiber_context.cpp`) saves
`rsp`, `rbp`, `rbx`, `r12`–`r15`, MXCSR and the FPU control word only, and keeps the
current fiber in the per-thread TCB; fs is never switched. Fix: fiber continuations
carry no fs/gs, so the resuming thread keeps its own. Regression test
`FiberExportsTests.Run_ResumedOnAnotherThread_KeepsTheResumingThreadsPointer` — red
against the old capture (thread A's `0x7FFDFD300000` travelled with the fiber),
green with the fix. Suite 1,155.

Verification run `race43`: the race load streamed completely again (8,876 shared
maps, 669 partial unmaps, 0 lazy faults) and **the Job-worker state-object crash did
not recur**. A different post-load crash followed — the first of its kind in any run
(blocker F below).

#### Blocker F: corrupt node in GPUex's deferred-callback list (race43)

- GPUex (`priority=256`, entry `0x8008F3D80`) jumped to `0x58`. The call at
  `0x8005FE9C4` walks a 16-bit-tagged lock-free stack at `[rbx+0x20]`, detached
  whole with `lock cmpxchg`: `next=[node]`, `fn=[node+8]`, `arg=[node+0x10]`,
  `call fn` — one node held `fn=0x58`, so the list contained a corrupt or stale
  node. Frame #0 (`0x800341D98`) is a dispatcher doing `call [[rcx]]`.
- Same phase as `race37`/`race41` (after the last partial unmap and the last map).
- **Not causal on this evidence:** AGC "buffer read unavailable; using zero buffer"
  warnings with garbage addresses (float data read as SRT buffer pointers). They
  also appear in crash-free old-build `race30` (50); higher counts now (164/373/303)
  track how far the load gets.
- `sceFiberGetSelf` still returns PERMISSION thousands of times (8,689 in `race43`);
  unverified whether those callers are inside a fiber.
- Next: `race44` repeats `race43` unchanged to see whether F is deterministic.
- **`race44` (repeat of `race43`, unchanged build):** load streamed completely
  (9,032 shared maps, 669 partial unmaps), then **Job#5** faulted at a new site
  `0x8031B7C74` reading `-1` through `RDI=0xBE3DCE7D3E1B1386` (float bits used as a
  pointer), last import **`sceFiberSwitch`** returning to `0x8001357DF`. So F is not
  deterministic: post-load crashes move between sites (`race43` GPUex deferred
  callback node, `race44` Job#5) but stay on the job-worker/fiber path — Job#5 in
  `race37`, `race41` and `race44`. The fiber fs fix removed the stale-state-object
  signature; something else around fiber migration still corrupts job state.
- `sceFiberGetSelf` PERMISSION results all come from one PLT stub (`0x8041928F0`)
  called by the job system's wait primitive `sub_800254470`, which picks a fiber
  wait when the returned address lies in its fiber pool and a mutex/condvar thread
  wait otherwise (14,582 calls at `0x8002544C5` alone in `race43`). Each guest
  thread runs on its own named runner, so `[ThreadStatic]` fiber tracking is
  normally consistent; whether any of those calls are made inside a fiber is still
  unverified.
- **Ruled out — false aliasing from shared backing:** replaying `race44`'s
  allocate/release/map trace, all 9,035 direct mappings lie inside a single live
  allocation at map time, and the struct holding `race44`'s float-bit allocator
  pointer (`0x1004A00000`, phys `0x2C7E00000`) has no other alias. `race44`'s crash
  is an allocator-interface call (`[ctx+0x28]->alloc(0x60, 8)` via thunk
  `0x8031B7C70`) whose allocator pointer held float bits — job context contents
  overwritten, not a wrong mapping.

#### Blocker F: is "not in a fiber" answered inside a fiber? (race45)

Fiber tracing now names the guest and host thread on every line, and
`SHARPEMU_LOG_FIBER=getself` checks each `sceFiberGetSelf` result against an
independent per-guest-thread record of the fiber last entered through a
transition (not derived from `_threadStates` or the per-host-thread current fiber).

`race45` (`SHARPEMU_LOG_FIBER=getself`): **0 mismatches** ("not in a fiber" while
the record names a fiber) and **0 wrong-fiber** results across 131,072+ "outside"
answers. Every sampled "outside" caller is a non-Job thread — **SNDZ, Updat,
QWRKR, Rendr, PCL Event** — including all four wait-primitive sites
(`0x8002544C5`, `0x8002597A3`, `0x80024D735`, `0x80024CD5F`). The PERMISSION flood
is those threads asking whether they are on a fiber when they never run one.
Not yet shown: that the Job workers enter fibers in this mode (entries were not
counted), so `race46` adds per-thread entry counts.

`race45` also did not crash: the load streamed completely (8,883 shared maps, 669
partial unmaps), then presentation stopped on the carousel with a loading ring at
~287k log lines — past where `race37`/`41`/`43`/`44` crashed. No per-thread
snapshot was enabled, so the stall's thread state is not recorded; the tracing may
have shifted timing.

#### race46: fiber entries per thread — GetSelf is answered correctly

`race46` (`SHARPEMU_LOG_FIBER=getself` with per-thread entry counts,
`SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1`):

- **Twelve guest threads enter fibers:** Job#0–5, **Updat**, and five **ADTsk**
  threads (first-entry lines). Job#0 had 44,186 entries at the last summary,
  Updat 5,474.
- **0 mismatches and 0 wrong-fiber results** again, across 40,960+ "outside"
  answers. The wait-primitive "outside" answers now include **Updat**, a thread
  that runs fibers — at each of those calls its transition record said it was on
  its root context between fibers, so "not in a fiber" was correct. The other
  caller (`0x803311E15`, `0x80024CD5F`) is a thread that never enters a fiber.
- **Conclusion:** across `race45` and `race46`, `sceFiberGetSelf` never reports
  "not in a fiber" while the calling guest thread is executing a fiber, and never
  returns another fiber. The job system's fiber-vs-thread wait choice is being
  made on correct information; SharpEmu's current-fiber tracking is ruled out as
  the cause of blocker F.
- `race46` crashed early in the load (406 shared maps, no partial unmaps), but on
  the host thread `.NET TP Gate` executing address 0 with no guest thread
  attached. The only difference from the earlier runs is the per-thread snapshot
  tracing, so this is treated as tooling-induced, not as F.
- Nothing connects blocker F to shader translation: the AGC zero-buffer warnings
  are baseline (present in crash-free `race30`), and no crash site so far is in
  GPU command or shader paths. Shader work stays out of scope for F.

#### Blocker F: the race44 allocator pointer is a stack slot overwritten asynchronously

`race44`'s float-bit allocator pointer is not a heap field. `sub_800782490` builds an
allocator descriptor `p3` on its own stack (`rbp-0x60`): four RIP-relative code
pointers, `p3[4] = 0`, and **`p3[5] = r12 = [param_1+8]`**, stored at `0x8007824FA`
into `rbp-0x38`. It then calls `sub_800782640`, which calls `p3[0](p3[5], 0x60, 8)`
at `0x8007826A1` without validating `p3[5]`.

- **Expected contents:** `[param_1+8]`, a valid object pointer. `r12` was
  dereferenced successfully after the store (`[r12+8]`, `[r12+0x10]`, `[r12+0x38]`),
  so the stored value was valid.
- **Legitimate writers in the window:** none. Between the store and the read are
  about 30 instructions, no calls and no import traps: an argument copy
  `vmovups [rsp], ymm0` (32 bytes from `[r12+0x38]`, below the slot) and the callee
  prologue. The slot is *above* `rsp` at the read, so a Windows exception frame
  (written below `rsp`) cannot account for it.
- **The bad value is this thread's own data, shifted:** `0xBE3DCE7D3E1B1386` is
  floats 3 and 4 of the 32-byte float block the thread had just copied onto its stack
  (`[rsp+0x68…]` in the crash dump), i.e. that block offset by 12 bytes. Something
  copied data from the same source object over this slot between two instructions.
- **Where:** Job#5's root thread stack (the RBP chain ends at the thread entry
  `0x8008F4B79`; stack top `0x600B80000`, slot `0x600B7FF28`), the same stack top as
  `race37`'s Job#5.
- **Candidates, untested:** another thread executing on the same stack memory
  (a continuation or fiber restored on two threads at once, or overlapping stack
  allocations), or emulator code writing guest memory through a wrong address.

Next: a full crash dump (all threads' `rsp` and memory around the slot), then a
write watch on the slot once its address reproduces.

#### Crash dump capture (race47)

- `race47` crashed after a complete load (8,877 shared maps, 669 partial unmaps):
  **Job#0** at `0x8002C072F` reading `null+0x28`, `RSP=0x600B7FDF0`, RBP chain to the
  thread entry at `0x600B7FFF0`. The root stack top is **`0x600B80000`**, the same
  region as Job#5's in `race37` and `race44`, now owned by a different job thread.
- **No dump was produced.** `DOTNET_DbgEnableMiniDump` (createdump), WER and
  SharpEmu's own `UnhandledExceptionFilter` all stayed silent: in every F crash
  (`race37`/`41`/`43`/`44`/`46`/`47`) the log ends after `NATIVE EXCEPTION CAUGHT` and
  the filter line never appears. The WER per-app `LocalDumps\SharpEmu.exe` key
  (full dumps) needs administrator rights (`reg add` → access denied). The two WER
  minidumps under `AppCertKit` are from 2026-09-12/13, unrelated.
- Added `SHARPEMU_CRASH_DUMP_DIR=<dir>`: when the vectored handler gives up on a
  native exception it writes one full-memory dump (handles, thread info, memory
  info) from a helper thread, with the faulting thread's exception pointers, before
  returning. `artifacts/gt7-opus-20260914/dump-stall.ps1` takes two external dumps
  for a stall; both suspend the process while writing, which perturbs timing.

#### race48: first full crash dump

`SHARPEMU_CRASH_DUMP_DIR` wrote a 15.8 GB full dump (`race48/dumps/crash-68224-13256.dmp`,
137 threads); offline `cdb -z` reads it (`artifacts/gt7-opus-20260914/cdb-threads.txt`,
`cdb-tls.txt`). Writing it suspends the process; the crash had already happened.

- **Crash:** Updat at `0x8005F9113`, right after `scePthreadSetprio(…, 0x100)`:
  `rax = [r14]` with `r14 = 0xE400000D00`, `[r14] = 0xD000000000`, fault reading
  `0xD0000000B8`.
- **The "pointer" is an index table.** Memory at `0xE400000CC0…` is a regular table
  of 16-byte `{uint32 0, uint32 index}` entries whose index equals the entry's
  position (`…D00` holds `0xD0`). The region is a 2 MB shared direct view,
  `0xE400000000` → physical `0x200000` (type `0xC`), mapped at log line 460 in
  early boot, with no alias, release or unmap before the crash — the table
  legitimately lives there. The bug is `r14` pointing into it as if it were an
  object array.
- **Where `r14` came from:** `mov r14, [rbp-0x20E8]` at `0x8005F8F6A`, a local in
  Updat's update function (`rbp = 0x7FFFD3DFFFA0`, slot `0x7FFFD3DFDEB8` on Updat's
  SharpEmu-allocated guest stack; frame directly under the thread entry).
- **TLS sharing at the dump instant:** 109 live guest TLS blocks (canary
  `0xC0DEC0DECAFEBABE`, not `…BA00`); no two share a job context (`fs-0xE8`), state
  object (`fs-0xE0`) or `fs-0xD8` value. 108 had all three slots zero (workers idle);
  one held GT7's static defaults. This covers only the moment of the dump, not a
  transient overwrite earlier.
- **Updat's `this` was valid; its memory was not.** The thread entry
  `0x8008F3D80` does `rdi = [arg+0x28]; call [arg+0x20]`; Updat's argument struct
  (`0xE400BF7DE0`) holds `fn = 0x8005DD330`, `this = 0xE400000D00`. In the dump the
  update function's locals are all consistent fields of that base (`[rbp-0x2118] =
  this+0xB0`), so the stack local was not overwritten — the function was entered
  with `this = 0xE400000D00` and read a corrupted object.
- **The object was overrun by an index table.** The 2 MB mapping at `0xE400000000`
  (physical `0x200000`) is a guest heap arena (allocator boundary tags `0x64708000…`
  at `+0x10000`, `+0x100000`, `+0x1FFFE0`). It starts with 16-byte
  `{0, index}` entries **0 through 0xD3** (`+0x000…+0xD3F`); object data resumes at
  `+0xD40`. A 0xD0-entry table would end exactly at `0xE400000D00`, where Updat's
  object begins — the table was initialised with **four entries too many**, and the
  store of entry `0xD0` (`0xD000000000`) into `0xE400000D00` is the first invalid
  write for this crash. Whether the extra four come from a count SharpEmu reports
  is not yet known.
- `race49` (hardware watch on Updat's stack slot, which did not reproduce: Updat's
  guest stack moved) ended with no exception log and no dump; the driver then found
  no window. Unexplained; not used as evidence.
- `race50` watches `0xE400000D00:8` (hardware and managed): the arena is mapped at
  the same point in early boot, so the address is expected to reproduce.

#### race51: watching Updat's object head, with the main thread armed

- **Main-thread gap fixed in the tooling.** `race50` (watch `0xE400000D00:8`)
  reproduced the arena mapping (`0xE400000000` → physical `0x200000`) but logged no
  hits at all: the hardware write/exec watch armed only registered guest threads,
  and the title's main thread — where early-boot construction runs — is not one.
  The arm loop now also arms the first non-guest thread that dispatches an import
  (logged as `armed title main thread host_tid=…`).
- **Expected contents.** `race51` hit #1: main thread wrote `0x806461930` to
  `0xE400000D00` at `after_rip = 0x800DC5138`, in the constructor `sub_800DC5120`
  (`[obj] = rsi`, `[obj+8] = 0`, `[obj+0x10] = 0xFF`, `[obj+0x18…] = 0`). So the head
  of Updat's object should hold `0x806461930`; `race48`'s crash read the index entry
  `0xD000000000` there instead, so the overrun store came after construction.
- **Tooling defect found and fixed:** the main thread's debug-register trap
  (`0x80000004`) reached `VectoredHandler` before the watch handler claimed it, was
  logged as `NATIVE EXCEPTION CAUGHT`, and triggered the one-shot crash dump
  mid-run (1.4 GB). `race51`'s timing after that point is perturbed and it cannot
  produce a crash dump. The crash dump now ignores single-step and breakpoint
  exceptions.

#### race51 stalled instead of crashing — wait graph from two external dumps

`race51` (watch armed, object head intact) never hit the overrun and did not crash.
Presentation stopped at log line ~219,900 of 756,961 (last `PERF videoout`,
`present_taken`, `vk.render_work_enter` all there); everything after is noise.
Two external dumps 5 s apart (`dump-stall.ps1`, 14.8 GB and 10.9 GB; the writes
suspend the process, so timing after each is perturbed) plus the periodic snapshots
give the state:

| thread | state |
| --- | --- |
| `Rendr`, `Updat` | `pthread_cond_wait` on the **same** job-system condvar `0x806E1A308` (mutex `0x806E1A300`) |
| `Job#0` | **Running but frozen** (imports stuck at 9,141,590): spinning in the `pause` loop at `0x800135350`, polling its queue ring head `+0x40` vs tail `+0x80` (`[rbx+0x138C0]`, stride `0xCC0`) |
| `Job#1`–`#5` | `sceKernelWaitSema` on semaphore 3 (idle workers) |
| `Flipx` | `pthread_cond_wait` on `0xE4000010B8` |
| `GPUex` | `pthread_cond_wait` on `0xE400001010` |
| `60Hz`, `Vsync`, `GPOTL`, `DlyLd`, `Strea`, many `WorkT`, `ADTsk`, `defTS` | `pthread_cond_wait` |

Submitters wait for job completion, workers are idle, and `Job#0` finds nothing in
its queue: a lost wake-up, not slow progress. Between the two dumps only the .NET
finalizer moved — but identical rip/rsp alone cannot prove a thread is parked (a
tight loop can sample identically), and the `.ttime` comparison did not parse, so
per-thread CPU time is still unmeasured.

- **Object head intact this run:** `0xE400000D00` still holds the constructor's
  `0x806461930`, and the arena start holds ordinary allocator chunks rather than the
  index table. The overrun is timing-dependent, not inevitable.
- **Ruled out (this run):** the `sceNetRecv`/`sceNetRecvfrom` spin (`0x80410123`) is
  baseline — present in `race45` and `race48` too. `SNDZ`'s 2.4 billion `strchr`
  calls are a legitimate linear scan of an intact audio bus-name table
  (`0x803542682`), i.e. a hot HLE path, not corruption.
- **Not yet examined:** SharpEmu's condvar and semaphore wake paths look
  structurally sound on reading (`sceKernelSignalSema` keeps a persistent count and
  wakes by key; `PthreadCondSignalCore` walks a waiter queue with a signal epoch),
  but no trace of the final signal/wait sequence has been captured. That needs a run
  with `SHARPEMU_LOG_PTHREAD_CONDS` and semaphore tracing.

#### Per-thread CPU time, measured (`race51`)

`.ttime` does not parse on these dumps; `!runaway 7` does. Over the 5 s between the
two `race51` dumps:

| thread | CPU in the gap |
| --- | --- |
| `Job#0` (`1b78`) | **5.0 s of 5 s wall** — genuinely spinning, not parked |
| `SNDZ` (`f9d4`) | 2.1 s |
| `SceSndzAudioOutMain` (`a3f0`) | 0.6 s |
| `SceLibc_Thr` (`a28c`) | 0.24 s |
| `SharpEmu Vulkan VideoOut` (`10b04`) | 0.047 s — idle |

So the earlier "`Job#0` spins, the rest are parked" reading holds, but only now on
measured CPU time rather than on identical instruction pointers.

#### A bounded synchronisation trace

`src/SharpEmu.HLE/SyncTraceRing.cs` records a fixed 8,192-entry ring for **one**
condvar and **one** semaphore, selected by `SHARPEMU_TRACE_SYNC_COND=<hex addr>` and
`SHARPEMU_TRACE_SYNC_SEMA=<handle>`; every other lock keeps its untouched path.
Nothing is printed until a failure dumps it — the native-exception handler, the
stall watchdog and the periodic snapshot each dump only the entries added since the
last dump. Events: cond waiter enqueue, signal/broadcast (woken, still-queued,
epoch), waiter completion, cooperative wake (**with the count actually woken**),
wait exit; semaphore fast acquire, block registration, each predicate evaluation,
signal (count before/after, waiters, woken) and resume.

#### `race52`: a second overlapping-write crash, in a different arena

Run: write watch on `0xE400000D00:8`, sync trace on cond `0x806E1A308` and
semaphore 3, `SHARPEMU_CRASH_DUMP_DIR` armed. The race load started, ran ~6 s of
gameplay, and crashed with a 17 GB full-memory dump written from the handler.

- **The fault.** `Job#4`, `0xC0000005` reading `0x271` at `0x8002C014D`, which is
  `mov r9,[r12+0xD0]` — i.e. `if (param_2 == 0) param_2 = *(long *)(param_1 + 0xd0)`
  in `sub_8002C00F0`, entered with **`param_1 = 0x1A1`**.
- **The caller.** `sub_802F8C250` (entry found by padding+prologue: the nearest
  call target, `0x802F88720`, is a 15-line function and was a mis-attribution).
  It is the job body's end-of-batch flush: after the atomic
  `*(ctx+0x180)++ == *(job+0x288)` barrier it walks 16 descriptors at
  `ctx = *(job+0x30)`, each `{head, tail, count}` of 0x18 bytes, and for each walks
  a chain of blocks — 6 records of 0x20 bytes after an 8-byte next-link — calling
  `sub_8002C00F0(rec.obj, rec.p2, 0, rec.p4)` once per record until `count`.
- **The descriptor was intact.** `ctx = 0x1000270800`; descriptor 3 at
  `0x1000270848` reads `{head = 0x100511ED08, tail = 0x100511ED08, count = 2}` —
  a well-formed single-block list.
- **The block it points at had been overwritten by an index table.** A valid block
  (e.g. `0x10050DF778`) is `{next = 0, p4, obj = 0x12F22A7D00, p2, u32}`. At
  `0x100511ED08` the memory is instead 16-byte entries `{index, 0}` with the index
  incrementing by one per entry. The run spans **`0x100511D308` (index 0) through
  `0x1005121AF8` (index 0x47F)** — 1,152 entries, 0x4800 bytes — with ordinary float
  data immediately before and after it. `0x100511ED08` is index 0x1A0 inside that
  table, so the record `sub_8002C00F0` was handed was the table entry `0x1A1`.
- **Nothing else references the table.** Searching `0x1000000000…0x1006000000` for
  the table's base and end found no pointer to either; the only reference to the
  block is the descriptor itself.

This is the same *shape* as `race48` — a 16-byte-strided index table written across
a live structure — but in a different arena (`0x1005……`, which the AGC log shows is
the command-buffer and label region, not the `0xE4……` direct arena) and with the
opposite field order (`{index, 0}` here, `{0, index}` there). Two independent
instances make "two allocations overlap / memory recycled while still referenced"
the stronger reading and "one table initialised with four entries too many" the
weaker one — **but neither is proven, and the writer still has not been caught.**

- **Write watch:** hit #1 only, the same constructor store as `race51`
  (`0x806461930` at `after_rip = 0x800DC5138`, title main thread). New from the
  frame walk: the constructor's callers are `0x800DC4CB4` → `0x801BD7BBD` →
  `0x8010EE132`. The `0xE400000D00` overrun did not reproduce this run.
- **Ruled out for `race52`:** `agc.wait_suspended` (a guest GPU wait whose label no
  observed producer writes) is **baseline** — 241/243/216/249/292/204 in
  `race44`/`45`/`48`/`50`/`51`/`52` — so it is an emulator gap worth its own entry,
  not evidence for this crash.
- **The condvar is not losing wake-ups (this run).** In the dumped ring windows:
  528 `CondWaitEnqueue`, 528 `CondComplete`, 529 `CondWake`, **528 `CondWaitExit`** —
  every waiter that enqueued also resumed. `WakeBlockedThreads` returned 0 on all
  529 wakes, which is benign: the signal lands between the waiter joining the queue
  and registering with the scheduler, and the waiter's own re-check resumes it.
  Semaphore 3: 7,057 blocks against 7,023 resumes, i.e. 34 still parked at the
  crash. This says nothing yet about `race51`'s stall, which this run did not
  reproduce.

#### `race53`: the same signature a third time, and a predicted base confirmed

Run: write watch moved to the **start** of the `0xE4……` arena (`0xE400000000:8`) on
the theory that a table that overruns into `+0xD00` must first write entry 0, plus
the same sync trace and crash dump.

- **The watch caught only allocator initialisation** — two hits, both on the title
  main thread in early boot (`0xE400008000` at `after_rip = 0x8002FD650`,
  `0xE400000030` at `0x8002FD49D`), then nothing. No table was laid down over that
  arena this run.
- **It crashed anyway**, in `Job#5` at `0x80012E596` = `mov ecx,[rdx]` where
  `rdx = *(rbx + 0x128)` and `rbx = 0x1004C533F0`. `rdx` held **`0x341`**.
- **Predicted and confirmed.** If the victim again sat inside a 16-byte `{index, 0}`
  run, the entry holding `0x341` implies a base of `0x1004C53518 - 0x3410 =
  0x1004C50108`. That address holds index 0, `0x1004C50118` holds 1, and so on; the
  object at `0x1004C533F0` is inside the run (its "fields" are entries `0x32F`,
  `0x330`, …).

Three crashes, three arenas, one signature: a live object sitting inside a
16-byte-strided run of `{index, 0}` entries that starts at index 0. The runs have
**no owner pointer** — searching `0x1000000000…0x1006000000`, `0xE4……`, `0x12……`,
`0xEC……` and the `0x804……` globals found nothing pointing at a run's base or end —
which fits a pool re-initialised in place (identity/free-list fill) at least as well
as a freshly allocated table that overruns. Still unproven either way, and the
writer is still uncaught: the address is different every time, so a fixed-address
hardware watch is down to luck.

#### Ruled out: a broken mutex fast path

Both crashes' last import is `scePthreadMutexUnlock`, which suggested our HLE might
be granting a mutex another thread holds through a guest-side fast path (the comment
in `PthreadCondWaitCore` assumes exactly that, and `TryAcquireUncontended` consults
only our shadow `OwnerThreadId`). The dump says otherwise: GT7's
`pthread_mutex_t` at `0x806E1A300` holds `0x00006000000146C0` — a **SharpEmu-issued
opaque handle** — so every acquisition has to come through the HLE and there is no
word for the guest to lock itself.

One real defect surfaced while checking: `IsGuestTrackedSelfLock` reads
`mutexAddress + 8` as a guest owner field, but for this layout `+8` is simply the
next variable — at `0x806E1A308` it is the **condvar handle**. It only gates a
`DEADLOCK` return for adaptive mutexes, so it has not been seen to matter, but the
premise is wrong.

#### GPU labels nobody produces — and one recycled under a waiting queue

`agc.wait_suspended … producer=none-observed; remaining-suspended` is **baseline**
(204–292 per run) and the queue really does stay suspended — we do not release it.
In `race53` those 204 split as `acb.compute[80]` 102, `acb.compute[81]` 78,
`dcb.graphics` 72, on queues that are otherwise busy (10,436 and 2,217 dispatches).
Label producers are registered from exactly three packet kinds — `DMA_DATA`,
`WRITE_DATA` and `RELEASE_MEM` (standard and AGC forms) — so a label written any
other way (including by a compute shader storing to memory) is invisible to the
wait tracker.

Reading ten of `race53`'s suspended labels out of the crash dump:

| labels | value at the crash |
| --- | --- |
| 6 | `1` — the wait did resolve later; the warning only means no producer was known *at evaluation time* |
| 3 | still `0` — never produced |
| 1 (`0x1005D2AD80`) | **`0x3E92FBC43CA0BC98`** — float data |

That last row is the important one: the memory a suspended compute queue is waiting
on has been **recycled into vertex/float data while the wait is still outstanding**,
in the same `0x1005……` arena where the index runs appear. Work was outstanding, it
never completed, and its memory was reused underneath it.

Which way the causality runs is not yet established — GT7 may recycle because it
believes the work finished, or the work may never finish because we never produce
the label and GT7 recycles on a timeout. But it is concrete, it is in the arena that
the corruption keeps appearing in, and it is an emulator-side gap rather than a
guest-side mystery.

#### Correction: the index runs are ordinary heap content

Searching `race53`'s dump for a run containing consecutive entries `0x10, 0x11`
(16-byte stride, `{index, 0}`) across `0x1004000000…0x1006000000` — one 32 MB
window of one arena — returns **73 matches**. These runs are everywhere; they are
normal pool/free-list/handle-array state, not a rare corrupting write.

That inverts the reading of all three crashes. The victims are not objects that a
table overran — they are **stale pointers into memory that has since been recycled
and refilled** with an ordinary identity run. `race48`'s "table initialised with
four entries too many" does not survive this: that table was one of dozens like it,
and the object pointer into it was stale. Consistent with the runs having **no owner
pointer anywhere** — nothing reads them by base because they are the resting state
of freed memory, not a live table.

So the question is not "who wrote past the end" but **"why does a job still hold a
pointer into memory the title has already recycled"** — which is the ownership /
completion question, not a stray-write question. Hunting the writer with a
fixed-address hardware watch was chasing the wrong thing.

#### Measured: GPU waits resolve; the label gap is not a deadlock

`race54` ran with `SHARPEMU_PROFILE_GPU_WAIT=1` (19 report windows). Every window:
`suspend/s == resume/s`, `outstanding=1`, `producerless/s=0` (220 producerless
warnings over a ~5 minute run is <1/s and rounds to zero, so this is consistent with
the warnings, not in conflict with them). Nothing accumulates — GPU waits do resolve.

What the profile *does* show is cost: `blocked_ms/s` climbs from 54–127 early to
817–1357 by the end, with single waits up to **1.66 s** (`max_ms=1661.6`). Queues
overlap their stalls, so that is not wall-clock-bounded, but it means GPU waits
dominate late-load time.

This retires the "labels nobody produces → permanently stuck queues" reading from
the previous section. The one concrete oddity there stands on its own and is
explained by the correction above: label `0x1005D2AD80` holding float data is the
same recycling, seen from the GPU side.

#### `race54`: a fourth crash, a different shape

`Job#5` at `0x800A06D15`, `mov esi,[rcx+4]` with `rcx = 0`, in `sub_800A06BB0`:

```c
func_0x0008002e7e20(puVar28,
    (ulong)*(uint *)(*(long *)(param_1 + 0x20) + 4) * 0x40
    + *(long *)(lRam0000000805ff3420 + 0xa8));
```

`param_1 = 0x806973F00` (a static global). Its `+0x20` read **zero** — but in the
crash dump, taken from the handler moments later, `0x806973F20` holds
`0x805BDE9C4`. So the field was null at the load and populated immediately after:
a concurrent initialisation this thread raced, not a stale pointer. Last import was
`sceAgcAcbAcquireMem` (`KT-hTp-Ch14`), i.e. this is GT7's ACB building path.

Different shape from the other three; recorded but not pursued.

#### Flip completion is reported at submit time

`SubmitFlip` increments `port.FlipCount` under `_stateGate` **before** the frame is
presented, `sceVideoOutIsFlipPending` returns 0 unconditionally, and
`sceVideoOutGetFlipStatus` reports that count with `gcQueueNum`, `flipArg`,
`submitTsc` and `flipTsc` all zero — the code comments state outright that "flips
complete synchronously in this emulator". On hardware `count` is flips *completed*
and `gcQueueNum` is flips *queued and not yet completed*, which is how a title knows
a display buffer is free to reuse.

Two things temper this for GT7 specifically:

- GT7 flips through the **AGC SetFlip packet** (`SubmitFlipFromAgc`,
  `submitGpuImage: false`), and that path already orders the flip-complete event
  against the render queue via `SubmitOrderedGuestAction`. The immediate-ack path is
  the direct `sceVideoOutSubmitFlip` entry point, which GT7 is not using for this.
- Whether GT7 polls `sceVideoOutGetFlipStatus` or `sceVideoOutIsFlipPending` at all
  is **not yet established** — `race55` is running with
  `SHARPEMU_LOG_IMPORT_CENSUS=1` to answer exactly that.

If it does poll them, this is a real and general accuracy bug (any title using the
documented flip-status contract to recycle buffers would hit it) and a plausible
mechanism for "the title recycled memory a job still points at". If it does not,
the recycling trigger is elsewhere and this stays a separate correctness item.

#### Flip status made hardware-accurate (`VideoOutExports`)

Checked against KytyPS5's PS5 `VideoOutFlipStatus` and its `FlipQueue`: `count`
increments when a flip **retires**, `flipArg`/`currentBuffer` describe the retired
flip, `flipPendingNum` counts submitted-not-retired flips and `gcQueueNum` the
GPU-queued subset. SharpEmu incremented `count` at submit, wrote `flipArg = 0`,
put `currentBuffer` at `+0x20` (reserved on PS5; the field is at `+0x38`, which
was left holding caller stack), and `sceVideoOutIsFlipPending` returned 0
unconditionally. `race55`'s import census confirms GT7 calls
`sceVideoOutGetFlipStatus` and `sceVideoOutAddFlipEvent` (not `IsFlipPending`).

Fixed: counters move at completion (the existing ordered completion point for AGC
flips, inline for `sceVideoOutSubmitFlip`), the struct is written in the PS5 layout
through `+0x40`, and `IsFlipPending` returns the pending count.
`VideoOutFlipStatusTests` (3 tests) cover the offsets and the completion values;
suite 1,158 green.

#### `race56`: the flip fix did not change blocker F

Same drive, flip fix in. The race load started and crashed in `Job#0` at
`0x8004343B2`: `r14 = [fs-0xC8]` (`0x1002A01668`), `rcx = [r14+0x10]` = `0xF7`,
then `mov r8,[rcx+8]`. Predicted from `0xF7` alone, the index run's base is
`0x1002A00708` — the dump shows index 0 there and `0xF6`/`0xF7` at the victim. A
**fifth** stale pointer into recycled pool memory, and this time the stale
pointer is held in the job thread's **TLS slot `fs-0xC8`** (the per-thread
allocation context `sub_802F8C250` installs from `job+0x430` on entry and restores
on exit).

Presentation and crash timing match `race52`–`55` (78 videoout PERF windows, crash
at ~line 154k, ~1.3 presented fps during the load). The one outlier — render work
`queued_ms=34365` — appears only after the exception, while the handler was writing
a 17 GB dump; before it the maximum was 2,851 ms, in line with `race52`'s 3,064 ms.
Not a regression.

So flip-status accuracy was a real bug, but it is not what recycles this memory.

#### Where `race56`'s stale pointer comes from: the job's scratch context

Decompiled (`artifacts/gt7-opus-20260914/race56-decomp.log`):

- **`sub_802DEB520`** (job body; the call at `0x802DEB8B1` targets the crash
  function, which confirms the entries) begins by retiring the thread's previous
  scratch context — the one in TLS `fs-0xC8`. It pushes that context's current
  chunk back onto its pool's lock-free free list: a CAS on `pool+0x28`, holding a
  48-bit pointer `<< 16` with a 16-bit tag. It then drops the pool refcount at
  `pool+0x10` and installs **`fs-0xC8 = job+0x18`**. The new context is not
  dereferenced at install.
- **`sub_800432340`** (crash function) later bump-allocates from
  `ctx = [fs-0xC8]`: `chunk = ctx[2]`, cursor `chunk[1]`, end `chunk[2]`, fresh
  64 KiB chunks from `func_0x8003F0000(pool->parent, 0x10000, 8)`. At the crash
  `ctx = 0x1002A01668` sits inside an index run, so `chunk = 0xF7`.
- **`sub_800135110`** (dispatcher) pops jobs from lock-free tagged queues (stride
  `0xCC0` per worker). Its `_Thrd_yield` spin, while a tag does not match, is the
  loop `race51`'s `Job#0` sat in.

At the dump instant only `Job#0`'s TLS holds a non-zero `fs-0xC8`, so the context
was not shared with another thread. **Nothing** in `0x1000000000…0x1006000000` or
`0xE400000000…0xE420000000` still references `0x1002A01668`, which means the job
record that carried it is gone as well. The dump cannot say whether the context
was already dead when this job installed it (stale job record) or was freed while
the job ran — that needs a record of each install with the context's state at the
time.

The tagged-pointer encoding (`ptr << 16 | tag`, recovered with an arithmetic
`>> 16`) round-trips only for addresses below 2^47. Every guest address seen here
is well below that, so it is noted and not suspected.

#### Instrument: an execute-watch ring for the context install

`SHARPEMU_WATCH_GUEST_EXEC_RING=<entries>` (in
`DirectExecutionBackend.WriteWatch.cs`) makes the execute watch record **every**
hit into a bounded ring instead of printing a sample. Each entry holds the
sequence number, timestamp, host tid, guest thread, rip, `rdi`/`rbx`/`r14`, and
two `SHARPEMU_WATCH_GUEST_EXEC_PROBE` values read at the hit. The ring prints only
from the native-exception handler and the stall snapshot, incrementally, like
`SyncTraceRing`. Probes resolve against a per-thread context: the existing
sampled path shares one static context, which job threads trapping concurrently
would overwrite.

`race57` watches `0x802DEB566` (`mov [fs-0xC8], r14` in `sub_802DEB520`); every
job installs its scratch context there, with `r14` = context and `rbx` = job.
Probes are `[r14]` (the context's pool) and `[r14+10]` (its current chunk). The
crashing job's context should appear in the ring. If its probes were already
index-run values at install, the job record carried a dead context. If they were
a valid pool pointer and chunk, the context was freed while the job ran.

Timing caveat: each install is now a debug trap (a vectored exception and a
context switch), so this run's job timing is slower than normal. A timing-dependent
failure may shift or not reproduce, and that outcome must be recorded as such.

**`race57` is not usable evidence.** Two instrument defects showed in its first
snapshots:

- The vectored handler logs every watch trap (`0x80000004`) through the same
  native-exception block that dumps the rings, so each hit dumped one ring entry:
  2,011 ring lines by log line 123k, and extra cost on every trap. Fixed — both ring
  dumps now skip single-step and breakpoint codes, as the crash dump already did.
- Probe semantics were one dereference off. A probe spec names an **address** that
  is then read, so `[r14]` printed `[[r14]]` (`0x8058F1380`, the pool's first word)
  and `[r14+10]` printed `[[r14+0x10]]` (unreadable when the chunk is null). Reading
  the context's own words takes `r14` and `r14+10`.

What the snapshots did show: installs ran about 7 ms apart on one job thread
(`#1008`–`#1011`, host tid 47620), all with the same job `rbx = 0x1000801648` and
context `r14 = 0x1000801668`. The install reads `mov rbx, rdi` then
`mov r14, [rdi+0x18]`, so `r14` is the pointer *stored* at `job+0x18` — and it
points at `job+0x20`. The scratch context is **embedded in the job record itself**
(`job+0x18` is a self-pointer to it). That makes `race56`'s reading sharper: a
context sitting in recycled memory means the **job record** it lives in had been
recycled, not just a separately allocated context. This is from one job in the
snapshots; the next run's ring should confirm that the self-pointer layout holds
for every install.

Two checks on the embedded-context reading:

- **Layout holds everywhere observed.** Every one of `race57`'s 18,673 recorded
  installs has `r14 − rbx = 0x20`. But all of them come from a **single** job
  record, `0x1000801648`, re-dispatched ~7 ms apart. That is one long-lived job,
  not the population the crashes come from, so coverage of the crashing job kind
  is still unproven.
- **`race56`'s whole job record was recycled.** The stale context
  `0x1002A01668` implies the record at `0x1002A01648`. In the dump that address
  holds index-run entries `0xF4`, `0xF5`, `0xF6` — the record itself sits in the
  refilled pool memory, not just its context.

So the object whose lifetime is wrong is the **job record**. A job still being
dispatched, or still running, points at a record whose memory has been freed and
reused by an ordinary pool. Every earlier crash fits the same statement: the
`race52` descriptor, the `race53` object, the `race56` context.

`race57` outcome: every ring hit came from one host thread (47620); the race load
never started (no `Strea` thread, 16 videoout windows, no access violation). A
ring dump on every debug trap slowed boot enough that the drive never reached the
race. The run is still alive and holds the output DLLs, so the rebuild with the
trap-path fix, and a rerun with probes `r14,r14+10`, wait until it is stopped.

`race57` ended on its own. Rebuilt with the trap-path fix (suite 1,158 green);
`race58` repeats the watch on `0x802DEB566` with probes `r14,r14+10` (the
context's pool pointer and current chunk, read at the install) and a 65,536-entry
ring.

**`race58` is perturbed as well, by a pre-existing logging path.** The ring and
probes work: ring dumps now appear only at stall snapshots, and probes resolve
(context `0x1000A01668`, pool `0x1000A00100`, chunk 0). But the log holds 11,982
`NATIVE EXCEPTION CAUGHT` blocks, every one code `0x80000004` and 11,978 of them
at `0x802DEB566`, averaging 113 lines each. That is ~1.35 M lines of exception
reports, one per watch trap. The trap is claimed by `TryHandleGuestWriteWatch`
(reached from `DirectExecutionBackend.Imports.cs`), but the general
`VectoredHandler` reports it first. With a hot execute site that report dominates
the run: 751 k log lines, 16 videoout windows, and no race load. As in `race57`,
all hits came from one host thread and one job record (`0x1000A01648`) —
unsurprising, since the drive never got past the menus.

Why the general handler saw watch traps first: both vectored handlers register at
the head of the chain (`AddVectoredExceptionHandler(1u, …)`). The raw handler that
claims watch traps (`RawVectoredHandlerManaged`, `Exceptions.cs:41`) is added
before the general `VectoredHandler` (`Exceptions.cs:65`), so the general one runs
first on every exception. This also explains `race51`/`race53`'s early
`NATIVE EXCEPTION` lines for main-thread write-watch traps. Fix: `VectoredHandler`
now calls `TryHandleGuestWriteWatch` right after its existing exec-break probe,
ahead of the nested-exception guard, and returns `EXCEPTION_CONTINUE_EXECUTION`
when the watch claims the trap. Not built yet: `race58` still holds the output
DLLs.

#### `race59`: current build, no watch — renders, then stalls (no crash)

Plain drive on the build with the flip-status fix and the handler-order fix, no
watch armed. The run rendered 55 frames, reached the carousel and started the race
load (`Strea` scheduled), with **no access violation** and zero native-exception
blocks. This confirms `race57`/`race58`'s black screens (one frame each) came from
the watch-trap logging flood, not from a rendering regression.

It stalled instead: the last `vk.present_taken` is at line 156,367 of 970,112,
followed by 39 stall snapshots. From the first snapshot after presentation stopped
(line 167,313) to the last (963,249):

| thread | imports first → last | waiting on |
| --- | --- | --- |
| `Job#0` | 9,107,217 → 135,758,822 | running |
| `Job#1`,`#2`,`#3`,`#5` | ~9 M → 67–171 M | `sceKernelWaitSema` (in and out) |
| `Updat` | 8,543,469 → 28,950,955 | cond `0x806E1A308` (last snapshot) |
| `Rendr` | 151,122 → 275,936 | cond `0x806E1A308` |
| **`Flipx`** | **13,058 → 13,058** | cond `0xE4000010B8` |
| **`GPUex`** | **84,643 → 84,643** | cond `0xE400001010` |

So this is **not** a whole-process deadlock: the job system, `Updat` and `Rendr`
keep cycling through imports, while the flip thread and the GPU-execution thread
never move again. The same two threads, on the same two condvars, were parked in
`race51`, which predates the flip-status change. The stall and the crashes
(`race52`–`56`) alternate from run to run on the same drive; one clean-of-crash
run says nothing about whether either fix changed the crash rate.

Next evidence needed: the signal/wait sequence on `0xE4000010B8` and `0xE400001010`
(`SHARPEMU_TRACE_SYNC_COND=E4000010B8,E400001010`; `SyncTraceRing` already
splits the variable on commas) — who is expected to signal the flip and
GPU-execution threads, and whether that signal was sent and lost, or never sent.

`race60`: same plain drive, with `SHARPEMU_TRACE_SYNC_COND=E4000010B8,E400001010`
(the `Flipx` and `GPUex` condvars) and `SHARPEMU_TRACE_SYNC_SEMA=3`. No watch and
no object logging, to stay as close to `race59`'s timing as possible. The ring
prints at each 30 s stall snapshot and on any native exception. If the run stalls
the same way, the last entries for each condvar show whether a signal completed a
waiter that never exited, whether a signal found no waiter queued (sent early), or
whether no signal came at all.

#### What `Flipx` and `GPUex` are waiting for

Decompiled (`artifacts/gt7-opus-20260914/race59-flipx-gpuex-decomp.log`); both call
`func_0x804192350` (the condvar wait) from a two-slot hand-off queue:

- **`Flipx` — `sub_8005FF360`**: under mutex `obj+0x3B0`, waits on cond
  `obj+0x3B8` while `count (obj+0x3C1) == 0 && quit (obj+0x3C3) == 0`, first
  setting `waiting (obj+0x3C2) = 1`. When woken it pops the entry indexed by
  `obj+0x3C0`, decrements the count, and — if a producer left the waiting flag
  set — signals the same cond (a full queue blocks the producer on it too). It then
  submits the flip (`func_0x8041926A0` under global mutex `0x806511688`).
- **`GPUex` — `sub_8005FF840`**: the same pattern at `obj+0x308` (mutex),
  `+0x310` (cond), `+0x319` (count), `+0x31A` (waiting) and `+0x31B` (quit). After
  popping it submits GPU work, sets busy byte `0x806511668 = 1`, and waits on
  global cond `0x806511660` until that byte is cleared.

In `race59` both threads were parked on their **input** conds (`0xE4000010B8`,
`0xE400001010`), not on the GPU-busy cond `0x806511660`. Their queues were empty:
the stall is not a flip or GPU completion that never arrived, but **nothing
upstream pushing a frame**. Meanwhile `Rendr` and `Updat` kept cycling on job
condvar `0x806E1A308`. The producer side — whichever thread pushes into these
queues, presumably `Rendr` — is where progress stops, which points back at the job
system's bookkeeping. `race60`'s trace on the two input conds should show the
producer's last push and signal before presentation stopped, and nothing after.

#### `race60`: crashed on `Rendr` — a sixth stale pointer, off the job threads

`race60` (sync trace on `Flipx`/`GPUex` conds) crashed rather than stalled, so its
cond trace can speak only to the healthy period. That period is clean: on
`0xE4000010B8` and `0xE400001010` every `CondSignal` completed a queued waiter that
then exited (22 and 29 of each), with no signals that found nothing queued.

- **The fault.** Thread `Rendr` at `0x80050780F`: `mov rcx,[rdi+0x10]` with
  `rdi = 0`, inside `0x8005077F0`, which looks like an allocator:
  `(pool = rdi, size = rsi = 0x68, align = rdx = 8)`, `cmp rsi, 0x200`.
  The caller at `0x800600F12` loads `rdi = [r13]`, `r13 = 0x1000C01770`.
- **Same signature.** `0x1000C01770` lies inside a `{0, index}` run — `race48`'s
  field order — with `+0x08` holding `0x107`, `0x108`, `0x109`, and
  `0x1000C01708` holding `0x100`. The "null pool" is the zero half of an ordinary
  refilled pool entry, not an uninitialised field.
- **The `0xE400000D00` object head was intact** in this run (`0x806461930`), even
  though `Rendr`'s `rbx` pointed at it.

This is the first instance on a non-job thread. The object whose lifetime is wrong
is therefore not specific to job records. `Rendr` also held a pointer to a record
that had already gone back to a pool.

#### How `Rendr` and `Updat` wait on the job system

Decompiled (`artifacts/gt7-opus-20260914/rendr-updat-wait-decomp.log`):
`sub_80024F6A0` (`Rendr`) and `sub_800254470` (`Updat`, and the helper
`race56`'s crash function calls) push a **wait node allocated on the caller's own
stack** into the job system's lock-free list — `head = tag | (&node << 16)`, a
16-bit ABA tag. They then lock `0x806E1A300`, increment a counter held in that
stack frame, and wait on `0x806E1A308` until a job worker has run the node, driven
the counter back to zero and broadcast. The stall snapshots' `rdx` values
(`0x7FFFD41FFA400002`, `0x7FFFD3DFD9400001`) are exactly such tagged stack pointers.

So a stalled `Rendr`/`Updat` means its node was never run, and a stale node pointer
in the job queue would point at a stack frame that has already returned. Both
readings are consistent with the stall graph; neither is shown yet.

#### Where `Rendr` got the stale record: a two-slot frame queue on `0xE400000D00`

Disassembling from `0x800600D30` (the decompiler dropped the path as unreachable —
an unresolved indirect jump) shows `Rendr` popping from the **same two-slot
hand-off pattern as `Flipx` and `GPUex`**, on the object at `0xE400000D00`:

```
lock  [rbp-0x68]                          ; mutex
while (quit [+0x2FB] == 0 && count [+0x2F9] == 0) { waiting [+0x2FA] = 1; wait }
idx   = [+0x2F8]                          ; read index
r13   = [rbx + idx*8 + 0x2D8]             ; slot 0 at +0x2D8, slot 1 at +0x2E0
count = count - 1 ; idx = ~idx & 1
```

`race60`'s dump of that object: slot 0 = `0x1000C01770`, slot 1 =
`0x1004C01770`, read index 1, count 1, waiting 0, quit 0. `Rendr` had just
popped slot 0. Slot 0's record lies in a refilled `{0, index}` run. Slot 1's
record is live-looking (self-relative pointer `0x1004C017D8`, arena pointer
`0x1004C00100`, small counters `0x13F3`, `0x1773`).

**Correction (same session): these are not two fixed arenas.** The slot values
differ in every dump, and all of them sit at `+0x1770` into a **2 MiB-aligned
block**:

| run | slot 0 | slot 1 | read idx / count | crash address |
| --- | --- | --- | --- | --- |
| `race52` | `0x1005401770` | `0x1003401770` | 1 / 1 | `0x100511D308` run |
| `race53` | `0x1004401770` | `0x1004601770` | 1 / 1 | `0x1004C50108` run |
| `race56` | `0x1002A01770` | `0x1004801770` | 1 / 1 | `0x1002A01668` (slot 0's block) |
| `race60` | `0x1000C01770` | `0x1004C01770` | 1 / 1 | `0x1000C01770` (slot 0 itself) |

So each queued frame is a 2 MiB block drawn from a pool, and the queue hands a
block's `+0x1770` record from producer to `Rendr`. The "64 MB apart" of `race60`
was a coincidence. Two of the four crashes land in the **block of the slot-0
record** — `race60` on the record itself, `race56` on a job context
`0x108` bytes before it. `race52` and `race53` crash in blocks that are in
neither slot at dump time, so this does not yet explain them.

The reading that survives: a frame block was returned to its pool and refilled
while its record was still queued for, or being consumed by, `Rendr`. What returns
a block, and which event tells the producer the consumer is done with it, is the
next question. If that event is a flip/GPU completion reported too early, the
fault is emulator-side; if the queue's own count/index drifts, it is guest
bookkeeping exposed by timing.

Two follow-ups against the older dumps:

- **`race56`'s queued slot-0 record was already refilled.** `0x1002A01770`
  (slot 0) sits inside the same index run as the crash's job context:
  `0x1002A01748` holds `0x104`, `0x1002A01778` holds `0x107`. The block was
  back in its pool **while still queued** for `Rendr`, and a job had installed a
  context inside it.
- **`race52`'s slot records are both live** — self-relative pointers and
  consecutive frame counters (`0x14AC`/`0x14AD`, `0x1B29`/`0x1B2A`) — and its
  crash was in a block in neither slot. So in `race52` (and likely `race53`) the
  recycled block had already been popped and was in use, not still queued. The
  statement that covers all four: **a frame block is returned to its pool while
  something still holds a pointer into it** — queued (`race56`, `race60`) or
  popped and in use (`race52`, `race53`).

**Correction on field order.** The `{index, 0}` vs `{0, index}` distinction
drawn between `race48`/`race60` and `race52`/`race53`/`race56` is an alignment
artifact, not two structures. The runs are one 16-byte identity layout: reading
from an address 8 bytes off the entry boundary shows the zero half first
(`race56`'s run reads `{0xF6, 0}` at `0x1002A01668` and `{0, 0x104}` at
`0x1002A01740` — the same run, phase-shifted).

#### The frame queue's producer is correct: it waits when full

Disassembled from `Updat`'s update function (sweep from its padding entry
`0x8005E5800`; the push is at `0x8005F8FF9`–`0x8005F9012`):

```
lock   [rbp-0x2108]
loop:  if (quit [+0x2FB] == 0 && count [+0x2F9] == 2) {    ; 0x8005FE265
           waiting [+0x2FA] = 1; cond_wait([rbp-0x2140], mutex); goto loop
       }
       if (count == 2) goto unlock                          ; only when quitting
       count = count + 1
       write = ~(count + readIdx [+0x2F8]) & 1
       slot[write] = rbx                                    ; 0x8005F9012
       if (waiting == 1) { waiting = 0; cond_signal(...) }  ; 0x8005F9484
unlock
```

The ring arithmetic is right (the first queued record goes to the read slot, the
second to the other), a full queue blocks the producer rather than overwriting or
evicting, and each side clears the shared waiting flag before signalling. So the
push path does not reuse a queued block. The block's return to its pool happens
elsewhere — after `Rendr` consumes it, on GPU completion, or on a fallback path —
and that is where the lifetime goes wrong.

**Next run (`race61`).** Execute-watch ring on the push instruction `0x8005F9012`,
with `rbx` = record being pushed, `r14` = queue object, and probes `r14+2d8` and
`r14+2e0` (both slots, read before the push executes). It fires once per frame, so
the perturbation is negligible. At the crash the ring shows, for the stale record,
when it was pushed and what the queue held at that moment. A block pushed while
its own record still occupies the other slot is reuse-while-queued, seen directly.

#### `race61` livelocked: execute watches never set the resume flag

`race61` armed the execute watch on the frame-queue push `0x8005F9012`. By log
line 29k it had recorded **6.25 million** hits, all from one thread (host tid
54736), ~60 µs apart, with the same record (`rbx = 0x1000201648`) and the same
slot contents every time — and no frame was ever presented. A push fires about
once per frame. This was the breakpoint re-trapping on one instruction.

**Cause: RF, confirmed from the older logs.** A hardware **execute** breakpoint
traps *before* the instruction runs. Returning `EXCEPTION_CONTINUE_EXECUTION`
without `EFlags.RF` (bit 16) re-executes it with DR7 still armed, so it traps
again forever, and nothing in the backend set RF. `TryHandleGuestWriteWatch` now
ORs `0x10000` into `CONTEXT.EFlags` (offset 68) when claiming an execute-watch
trap. Not built yet: `race61` holds the output DLLs.

The confirmation comes from every earlier execute-watch log still on disk. In each
one, the number of distinct `rdi` values equals the number of trapping threads,
and each thread's `rdi` never changes:

| run | watched | hits (last logged) | distinct `rdi` | threads |
| --- | --- | --- | --- | --- |
| `race24` | `0x800A05650` | #26112 | 6 | 6 |
| `race26` | `0x800A05650` | #12032 | 6 | 6 |
| `race27` | `0x800A056FB` | #33024 | 6 | 6 |
| `race42` | `0x8000CA086` | #7680 | 1 | 1 |

Each job thread that reached the watched address looped on its first arrival for
the rest of the run.

A version of this section written a few minutes earlier "withdrew" the RF
explanation on the strength of `race26`'s reported per-hit variation. That was
wrong: the variation was six looping threads interleaved by the 1-in-256
sampler. Consequences, now standing:

- `race57`/`race58`'s "18,673 installs from one job record" were one install
  looping. The embedded-context layout (`job+0x18` → `job+0x20`) rests on that one
  observation plus `race56`'s dump arithmetic.
- An execute watch has **never** yielded more than one real hit per thread in this
  project, and it froze every thread it caught. First-hit facts ("this address is
  reached", its caller and register values) hold. Hit counts, "runs often", and
  anything a run did after its watched threads stopped do not.
- `boot-investigation.md`'s `race26` gate reading is affected: it now carries a
  dated correction.


## Appendix G. Direct memory: allocation and mapping contract

The four operations a PS5 title performs on direct memory are distinct, and
SharpEmu conflated them until shared backing was wired in. This is the contract
the implementation now holds to, and the reasoning behind each choice. Host
behaviour it relies on is pinned down by
`tests/SharpEmu.Libs.Tests/Memory/SharedBackingSectionTests.cs`; the measurements
behind the design decisions are in
[Appendix D](#fix-design-shared-backing-for-direct-memory-not-started).

### The four operations

| Guest call | Operation | Owns | Does not touch |
| --- | --- | --- | --- |
| `sceKernelAllocateDirectMemory` | **Allocate** | a physical range `[start, start+len)` of the direct pool | any virtual address |
| `sceKernelMapDirectMemory` | **Map** | a virtual range attached to a physical offset | physical ownership |
| `sceKernelMunmap` | **Unmap** | removes a virtual attachment | physical ownership, and every other mapping of the same physical range |
| `sceKernelReleaseDirectMemory` | **Release** | ends physical ownership | virtual attachments (see below) |

Physical offsets are section offsets: the whole direct pool is one pagefile
section (`SEC_RESERVE`, `PAGE_EXECUTE_READWRITE`, `DirectMemorySizeBytes`), so a
physical offset *is* a section offset and two mappings of one offset are the same
pages with no bookkeeping of ours in between.

### What each operation must do

**Allocate** hands out a physical range and nothing else. It never touches the
address space.

**Map** attaches virtual addresses to a physical offset. Every map of an
overlapping physical range must see the same bytes, in both directions,
regardless of the starting offset each mapping used. Protection belongs to the
mapping, not to the physical pages — `mprotect` through one alias leaves the
other alone (verified: `ProtectionChangesStayPerViewAtPageGranularity`). A map
that cannot be satisfied with shared backing falls back to private memory and
says so in the `map_direct_shared` trace; it is never silently reported as
shared.

**Unmap** removes the virtual attachment and returns the address to the
launcher's placeholder reservation, so no host allocation can take an address the
guest may map again. It must not disturb any other mapping of the same physical
range, and a partial unmap must leave the surviving head and tail attached *at
their original physical offsets* — the tail's offset is not the view's base
offset.

**Release** ends physical ownership *and detaches every mapping of the released
range* — only the overlapping part of each mapping, wherever it starts. Both
reference implementations do this: ShadPS4's `MemoryManager::Free` unmaps the
overlap of every Direct VMA, and KytyPS5's `ReleaseDirectMemoryInternal` unmaps
every alias `FindMappings` returns. GT7 depends on it: in `race33` all 37
releases hit ranges that were still mapped, with zero `munmap` calls, and the
same physical ranges were then allocated and mapped again at new addresses.
An earlier draft of this contract said release leaves views mapped; that was an
unverified assumption, and it is what let zero-on-reuse clear memory that stale
views still exposed.

Release must also guarantee that the *next* allocation of that physical range
does not see the previous owner's bytes.

### Zero-on-reuse, not zero-on-release

Committed pages of a `SEC_RESERVE` section cannot be decommitted
(`VirtualFree(MEM_DECOMMIT)` fails) and keep their contents after every view is
gone (`CommittedSectionPagesCannotBeDecommittedAndOutliveTheirViews`). So
recycled physical memory is *not* zero, and something has to clear it.

Zeroing at **release** would be wrong: a still-mapped alias would see its data
vanish under it, which is the "modify an old, still-accessible alias" hazard.
Zeroing happens at **allocate** instead, and only for physical ranges the pool
has handed out before — a physical offset allocated for the first time is zero by
construction, so first allocation costs nothing. Only pages the pool previously
owned and has since reclaimed are cleared, so no live alias of a range the guest
still owns is ever touched.

What hardware does on release is unverified. Zero-on-reuse is the conservative
reading: it cannot leak one allocation's contents into the next, and it cannot
corrupt a mapping the guest still holds.

### Commit

Commit happens at map time, one view at a time — **not** on first touch. Lazy
commit was the first design and it is wrong for native guest code: SysV code keeps
locals in the 128-byte red zone below `rsp`, and Windows dispatches a page fault on
the faulting thread's own stack, below `rsp`, overwriting them. GT7's leaf loop at
`0x801B9F010` keeps its array base at `[rsp-0x28]` and reloads it every
iteration; the first touch of a lazily committed page mid-loop replaced it with
zero, and the next iteration read `null+0xF50` (`race33`, `race36`, identical
registers both times). The private backing shared views replaced was committed at
map time as well (`TryAllocateFixedThroughGranules` commits the whole request), so
committing views costs no more — less, since aliases share pages.

The lazy-commit fault handler is therefore not a correctness mechanism for guest
memory at all; any first-touch fault inside a red-zone function has the same
hazard.

Commit charge is never reclaimed while the pool section lives — it is reclaimed
only when the section object dies, which needs the handle closed *and* every view
unmapped. The pool is therefore bounded by `DirectMemorySizeBytes` (13,376 MiB)
and only grows within that bound. That is a deliberate trade against chunked
sections, which would reclaim only once every alias into a chunk is gone.

### View granularity and the partial-unmap race

Mappings are cut into views of at most 2 MiB, on the placeholder boundaries that
already exist. GT7 maps direct memory 2 MiB aligned, so most unmaps land on view
boundaries and need no remap at all; 16 KiB views everywhere would cost ~0.9 s
and ~80 MiB of kernel overhead for GT7's arena alone.

Only a sub-view partial unmap has to split, and it affects at most the one or two
boundary views the unmap cuts. For those:

1. The unmapping thread publishes the pending range in an atomic pair.
2. It unmaps the whole view, splits the placeholder, remaps the survivors.
3. It clears the pending range — on failure as well as success.

A guest thread that faults inside the pending range spins on the atomic with
`YieldProcessor` and retries the faulting instruction. The spin is **bounded**: if
the remap never completes the handler gives up and falls through to the ordinary
lazy-commit path rather than spinning forever. The vectored handler takes no
lock, allocates nothing, and suspends no thread, and the unmapping thread never
waits on a guest thread, so the two cannot deadlock.

Host workers reach guest memory through `PhysicalVirtualMemory`, which takes its
reader lock, so they are serialised against remapping by the ordinary mapping
lock and never depend on fault retry.

### Adjacent mappings are one span

Two mappings that touch are contiguous guest memory, and a host copy may run
across the seam. Private direct regions used to hide this: `InsertRegionSorted`
merged them. Shared views stay separate regions (they are reserved-only, and
merging would lose the per-view unmap boundaries), so the managed read/write
paths now split a span across contiguous regions and still fail on any gap.

Found the hard way: the first GT7 boot on shared backing (`race32`) died at boot.
`memcpy` of `0x5000` bytes to `0xEC027FD000` ran from the 2 MiB mapping at
`0xEC02600000` into the next one at `0xEC02800000`, `TryCopy` and the
read/write fallback both resolved to no single region, the export returned
`MEMORY_FAULT`, and the guest dereferenced the null result. Baseline runs
`race30`/`race31` had no native exception. Regression test:
`MemcpyAcrossTwoAdjacentMappingsSucceeds`.

### A fixed map into a hole maps shared first

Release and unmap leave placeholders behind. A later fixed-address map over such a
hole is served by the shared path directly; only if that fails does it fall back
to the private reservation path. The order matters: the private claim path could
not take a hole spanning the remnants of two earlier mappings. In `race34` GT7
released the tail of one mapping and the head of the next, then mapped
`0xF40C000000` +0x800000 over both. `TryReserveExactGuestVirtualRange` threw ("no
host mapping"), the map returned `NOT_FOUND`, and the title's next memcpy into it
faulted. Regression test: `FixedMapOverHolesFromTwoReleasesSucceeds`.

### Where the contract is enforced

| Piece | Where |
| --- | --- |
| Pool section, view map/unmap/remap, zero-on-reuse, pending-remap atomics | `SharpEmu.HLE/Host/GuestSharedBacking.cs` |
| Region bookkeeping for shared views | `PhysicalVirtualMemory.TryMapSharedDirect` / `TryUnmapShared` |
| Guest entry points | `KernelMemoryCompatExports` — `MapDirectMemoryCore`, `KernelMunmap`, `TryAllocateDirectMemoryLocked` |
| Fault retry during a pending remap | `DirectExecutionBackend.Exceptions.cs` |

### Not covered

- **POSIX.** `memfd_create` + `mmap(MAP_FIXED | MAP_SHARED)` is the equivalent and
  is not implemented; on non-Windows hosts every mapping stays private, exactly as
  before.
- **GPU cache coherence.** Sharing CPU bytes does not make Vulkan resources
  alias-aware. A write through one virtual address does not invalidate a cached
  resource uploaded from another.
- **Ranges that are not 16 KiB aligned**, or whose virtual range is not inside the
  launcher's guest-window reservation, fall back to private backing.


## Appendix H. Handover — GT7, 2026-09-16

For the next agent picking up this branch. Read [CLAUDE.md](../../CLAUDE.md)
first (**fix the emulator, not the game**), then [README.md](./README.md) for
current status. This file covers only what changed on 2026-09-16 and what to do
next; the evidence lives in [boot-investigation.md](./boot-investigation.md)
sections **6by** and **6bz**.

### Branch state

Branch `gt7-boot`. **Nothing is committed after `58272cd`** — the working tree
carries a large uncommitted change set that predates today. Do not commit or
push unless asked.

`dotnet test SharpEmu.slnx -c Release` → **1,161 passed, 0 failed**.

#### Changed today (code)

| file | change |
| --- | --- |
| `src/SharpEmu.ShaderCompiler.Vulkan/Gen5SpirvTranslator.cs` | FRONT_FACE pixel input wired up; zero-position clip guard; clip-distance init |
| `src/SharpEmu.ShaderCompiler.Vulkan/SpirvModuleBuilder.cs` | `SpirvCapability.ClipDistance = 32`, `SpirvBuiltIn.ClipDistance = 3` |
| `src/SharpEmu.Libs/VideoOut/VulkanVideoPresenter.cs` | enable `ShaderClipDistance`, warn when unsupported |
| `tests/SharpEmu.Libs.Tests/Agc/Gen5PixelFrontFaceSpirvTests.cs` | new (2 tests) |
| `tests/SharpEmu.Libs.Tests/Agc/Gen5VertexZeroPositionSpirvTests.cs` | new (1 test) |

Two hardware facts, both general, neither title-specific:

1. **FRONT_FACE was never wired up.** `SPI_PS_INPUT` bit 12 had its VGPR slot
   reserved and never written, so any pixel shader branching on facing read an
   undefined register. Hardware initialises that VGPR with the **float bits** of
   +1.0 (`0x3F800000`) front-facing and -1.0 (`0xBF800000`) back-facing — not
   `1`/`0`. Guest shaders feed it to float math and compare it against zero.
2. **A vertex exported at the origin must be culled.** The PS5 discards a vertex
   whose position is all zeroes *before* the perspective divide; Vulkan has no
   such rule and rasterises the NaN that `0/0` produces. Fixed with a
   clip-distance guard, which required enabling the `shaderClipDistance` device
   feature.

Both are reimplementations of behaviour described by KytyPS5 commits
(`7b5a33f87`, `86414760e`). **Nothing was copied** — KytyPS5 is GPL-2.0-only
against this project's GPL-2.0-or-later, so lifting source would pin the combined
work to v2-only. Keep it that way.

Verified by tests and by a full boot-to-race-load run with no regression
(`race64`). **Neither is verified to change a visible GT7 symptom.**

#### KytyPS5 as a reference

`README.md` pins KytyPS5 at `2026-09-12-d3d7bd3` in `artifacts/kyty`. Upstream
has moved; the 2026-09-15 commits were mined for this work.

**Already checked, do not redo:** `acd7435d3` (V_MOV_B32 SDWA byte destinations)
— SharpEmu is already correct. `CreateSdwaControl` decodes `dst_sel`/`dst_unused`
generically with no per-opcode rule table, and `ApplySdwaDestination` implements
PAD, SEXT and PRESERVE. Verified by hand against that commit's RDNA2 table-88
vectors for all four byte selects in all three unused modes.

**Not taken:** `441367b1a` and `0d9e95d96` (pixel input aliases, centroid
weights). The idea is real — two pixel inputs mapping to one vertex output with
different interpolation must share one SPIR-V interface variable — but the code
is welded to KytyPS5's own pixel-parameter abstraction. Revisit when the
text-never-drawn blocker is next worked.

### Blocker F — where it actually stands

**It is intermittent.** `race64`–`race67` crashed; `race63`, `race68`, `race69`
stalled instead. Any claim that a change fixes it needs several clean runs, not
one.

#### The chain to the fault (established 2026-09-16)

```
ctx2   = [fs-0xe8]            per-job descriptor (102 distinct values observed)
table  = [ctx2 + 0xde8]       SHARED: one table seen by 3 guest threads
result = [table + 0x11d8]     reads ZERO at the crash
         [result + 0x2c]      the access violation
```

Measured over 95 healthy samples (`race69`): `[table + 0x11d8]` normally holds
**`table + 0x800`**, an interior self-pointer, on all six tables observed, and is
**never zero**. A structure whose own self-pointer reads zero has been zeroed or
re-initialised, not freed. Because the table is shared by up to three job
threads, one zeroing explains several threads faulting at once on different
objects — which is what every recorded crash shows.

Every fault in every run is a read at **exactly null plus a field offset**
(`0xCC`, `0x2C`, `0x50`, `0x87`, `0x12A`), at a different site each run.

#### Ruled out — do not re-run these

| ruled out | evidence |
| --- | --- |
| teardown clearing another thread's context | `race65`: 43 contexts, 0 torn down twice, 136 unique victims |
| job context missing from FS TLS | `race66`: `[fs-0xe0]` non-zero in 478/478 hits |
| corrupt job type tag / bounds rejection | `race67`: tag `0x1701` in 1,233/1,233 hits |
| semaphore double-acquire | all three acquisition paths check and decrement under one lock |
| `sceKernelWaitSema` correlation | appeared only in `race66` with a watch armed; absent in `race68` |
| shared-backing layer failing | `race68`: 0 split/unmap/map errors across 8,869 maps |
| `sceFiberGetSelf` PERMISSION flood | already explained in section 6bu — root-context callers, expected |

**Site-specific execute watches are exhausted.** Three runs established that the
crash site moves and watching one site does not catch it. Do not spend a fourth.

#### Next steps, in order

1. **Free, do first:** confirm the emulator never zeroes guest memory that is
   still mapped. `GuestSharedBacking` has a `zero:` trace path and `race68`
   recorded none of it — but `SHARPEMU_LOG_SHARED_BACKING=1` traces only
   failures, so read the code paths, don't just trust an empty log.
2. **Catch the writer of `table + 0x11d8`.** The obstacle: the table is created
   during the race load, *after* watches arm, and its address moves between
   boots, so `SHARPEMU_WATCH_GUEST_WRITE` has nothing stable to anchor its deref
   spec to. Either find a stable anchor, or:
3. **Crash dump.** `SHARPEMU_CRASH_DUMP_DIR`, read offline with `cdb -z`, to see
   whether the whole structure is zeroed or only that one field. Dumps are
   10–15 GB each — check free disk first.

The open question is narrow now: **what zeroes `table + 0x11d8`.**

### Tooling and traps

#### Running a race

```powershell
cd artifacts/gt7-opus-20260914          # REQUIRED — see trap below
./drive-race2.ps1 -Name race70 -ExtraEnv @{ SHARPEMU_LOG_SHARED_BACKING = '1' }
```

- **`sig.py` globs `<name>/images` relative to the current directory.** Run the
  drive script from anywhere else and every signature reads `NOFRAME`, the
  carousel is never detected, and the tile walk is skipped. This cost a run
  (`race63`). Always `cd` first.
- The script drives boot → wizard → carousel → first tile → race select by frame
  signature, not fixed sleeps. `RACE LOAD STARTED: True` means the `Strea` thread
  was scheduled. A carousel signature starts `4444`.
- **A running emulator locks the output DLLs.** Stop it before rebuilding. Never
  kill a session you did not start without asking.

#### Execute-watch ring

```
SHARPEMU_WATCH_GUEST_EXEC=0xADDR
SHARPEMU_WATCH_GUEST_EXEC_PROBE=<spec>,<spec>      # e.g. [[rax-E8]+de8]+11d8
SHARPEMU_WATCH_GUEST_EXEC_RING=8192
```

- Probe specs take `reg±hex`, `[spec]±hex` (nested), and absolute addresses;
  negative offsets work. The probe reads a **qword at the resolved address**, so
  to capture a register's own value you must probe something it points at.
- The ring records `rdi`, `rbx`, `r14` and two probe values — **not** `rax`,
  `rsi` or `rdx`.
- **The ring prints only at a native exception or a stall snapshot**, and entries
  can flush to the log after the drive script exits. If an analysis finds zero
  entries, re-read the log before concluding the watch never fired.
- **Observer effect is real.** `race66` produced a five-thread crash at one
  instruction, right after `sceKernelWaitSema`, that did not recur without the
  watch armed. Treat any signature seen only under instrumentation as
  unconfirmed.

#### Ghidra, headless

```powershell
$env:JAVA_TOOL_OPTIONS='-Duser.home=G:/Games/GT7/SharpEmu-Source/sharpemu-gt7/artifacts/ghidra-home'
& C:\Tools\ghidra_12.1.3_PUBLIC\support\analyzeHeadless.bat artifacts/gt7-ghidra/project gt7 `
  -process gt7_text.bin -noanalysis -readOnly -scriptPath scripts/ghidra `
  -postScript DecompAt.java 0xADDR          # or ListingRange.java 0xSTART 0xCOUNT
```

- **`-noanalysis` mis-models `jmp` switch tables as calls returning a value.**
  This produced a wrong reading twice on 2026-09-16. When a decompile shows
  `uVar1 = (*(code *)(...))(); return uVar1;`, disassemble before believing it —
  the jump targets may all be inside the same function.
- Capture the whole stdout. Filtering on `DecompAt.java>` drops the function
  body, because only the first line of multi-line script output carries the
  prefix.

#### Finding an enclosing function

`artifacts/gt7-call-entries.json` holds 30,783 call targets with two or more
callers, built by scanning `E8` call sites in `gt7_text.bin` (77,949 raw targets,
matching the count recorded in section 6bt). `bisect_right - 1` gives the
enclosing entry for an address.

**A large offset means the entry is wrong** — that function is reached indirectly
through a vtable and the call-target list cannot see its real entry. `+0x5D` and
`+0xCD` are trustworthy; `+0x4D5D` is not.

#### Reading data outside the text dump

`gt7_text.bin` maps as `0x800000000` + file offset. Verify before trusting it:
the five bytes before a printed return address must decode as `call rel32`.

Anything beyond roughly `0x804195B5C` — jump tables, vtables, rodata — is **not**
in that dump, and `eboot.bin` is a signed PS5 container (magic `54 14 F5 EE`),
not a bare ELF, so its segments cannot be read from disk without unpacking.
Read from a live process instead:

```
python artifacts/gt7-codex-20260912/read-vm.py <pid> dump 0xADDR 0x40
```

That uses `ReadProcessMemory` and is read-only. **Never attach a Windows debugger
to a running SharpEmu** — dbgeng attach destroys the guest session.

### Latent, unrelated defect found today

`sceKernelCancelSema` in
[KernelSemaphoreCompatExports.cs](../../src/SharpEmu.Libs/Kernel/KernelSemaphoreCompatExports.cs):

- it sets `Count` and calls `Monitor.PulseAll`, so a host-path waiter sees a
  sufficient count and returns `ORBIS_GEN2_OK`, where hardware returns a
  cancellation error to every waiter;
- it never calls `WakeBlockedThreads`, so a cooperatively-blocked guest thread is
  not woken by a cancel at all.

**GT7 never calls it** (`4DM06U2BNEY`: 0 hits), so it caused none of the observed
crashes. Fixing it properly needs a title that exercises it, or hardware evidence
for the exact error code. Recorded so it is not rediscovered as a suspect.

### Pre-existing title special-casing

Noted, not touched, flagged because CLAUDE.md forbids adding more:

- `SHARPEMU_FORCE_TITLE_SINGLE_MRT` — `Gen5SpirvTranslator.cs`, keyed on program
  address `0x500781200`
- `SHARPEMU_FORCE_PACKED_STORE_EXEC_VALUES` — same file, same address
- `SHARPEMU_FORCE_TITLE_VERTEX_OUTPUTS_ONE` — same file, address `0x500780000`

All are env-gated and inert unless set, but they are title special-casing and
should eventually go.
