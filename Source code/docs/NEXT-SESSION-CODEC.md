# Stage 2 — the codec

Rewritten 2026-09-16, at the end of the session that built 2.2. **2.1 is done and proven on
hardware. 2.2 is built, and its encoder is proven on nothing**, for a reason that turned out to be
interesting. §24 and §25 of the handover are the record; this file is what the next session needs.

---

## FIXED 2026-09-17 — **two constants were swapped, and the encoder now works**

**The first frame this project has ever encoded**, on the laptop that was written off as having no
encode engine: a whole 1920x1200 screen in **10.9 KB**, decoded and compared to the screen at
**1.0 of 255** using BT.709. `codecbench --h264check` PASSES.

### The bug: two enum values the wrong way round

```c
// mftransform.h
MFT_MESSAGE_NOTIFY_END_OF_STREAM   = 0x10000002
MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003
```

`Mf.cs` had those two swapped. They are consecutive, and the order reads backwards from the way
anyone would write them down. So `BeginStreaming()` sent `NOTIFY_BEGIN_STREAMING` and then told the
encoder **the stream had ENDED**. Every encoder answered `S_OK` to both and then, entirely
correctly, never asked for another frame.

**That is the whole of `events 0`, on both machines, for two days.** It is the same shape as the
`MFTEnumEx` category GUID that preceded it: *an API returning success for a well-formed request that
means something other than what was intended.* Twice now in one stage. The lesson is not "check
GUIDs" — it is that **a constant copied into interop is not checkable by reading the code around
it**, and the only defence is to check it against the header, which takes seconds.

### The second bug, which the first one was hiding

With the pump alive the encoder got one frame further and stopped again:

```
events 2 (needInput 1, haveOutput 1), inputs accepted 1, outputs taken 0,
last ProcessOutput: the transform's output format changed and must be set again
```

`MF_E_TRANSFORM_STREAM_CHANGE`. A hardware encoder settles its output type on the **first** frame
and says so by failing that first `ProcessOutput`. `MfTransform.TryOutput` already reported it
through an `out bool formatChanged` — and `H264Encoder.Encode` declared that flag in its `while`
condition and **never read it**. So the first frame was dropped, the encoder held an output nobody
would collect, and every later frame was refused.

`Encode` now renegotiates (`GetOutputAvailableType(0)` then `SetOutputType`), re-reads the sequence
header because the SPS and PPS belong to the type that just changed, and retries — bounded at three,
because "renegotiate and try again" spins for ever the day an encoder reports a change it will not
accept. `H264Encoder.StreamChanges` counts them; one on the first frame is normal, a climbing number
is a fault.

### The number the stage exists for

1920x1200, same screen, same conditions, 12 fps:

| condition | JPEG tiles (dxgi) | H.264 | |
|---|---|---|---|
| scrolling a document | 2785 KB/s | **198 KB/s** | **14x less** |
| video | 2619 KB/s | **186 KB/s** | **14x less** |
| full frame on subscribe | 321 KB | **122 KB** | |
| CPU, scrolling | 12.7% | **1.0%** | |

**2817 KB/s was the number to beat. It is 198 KB/s.** Idle is still 0 on both, as it should be.

**One honest caveat.** The encoder was asked for 1799 kbit/s and delivered about that, so the
comparison is "what we told it to spend" against "what JPEG happened to cost" — the 14x is real but
it is a bitrate we chose. The open question is whether the picture holds up at that bitrate *during
motion*; `--h264check` measures a still frame, where it is excellent. `--live` against real footage
is what settles it, and 2.3's controller is what should be choosing the number rather than a
constant.

### PROVEN OVER A REAL SESSION, same day

`tools/codeccheck` against DESKTOP-2N6FVGT running the rebuilt host: **8 of 8**.

```
host says the video codec is now: H.264 - Intel(r) Quick Sync Video H.264 Encoder MFT
                                  at 819 kbit/s, 1366x768
PASS  a first picture arrived after subscribing - 585 ms
PASS  the host used H.264 for at least one pass
PASS  the video stream decoded here - 2 of 2 frames drawn, 2 key frames
PASS  frames followed a change on screen - 8 frames in 3 s at 12.1 KB/s
      - the stream is LIVE, not stuck on its first key frame
PASS  the picture actually changed - mean difference 7.5 of 255
PASS  the composed picture is a screen, not black - mean brightness 193
```

The host trace agrees: `codec: H.264 via Intel(r) Quick Sync Video H.264 Encoder MFT, 1366x768 at
819 kbit/s, taking the frame as BGRA` — the direct-BGRA path, so the colour converter is not even
needed on that machine.

**And the picture was looked at**, because a byte count cannot say it is the right screen: the
decoded frame is that machine's desktop, legible down to window text and the taskbar clock, with the
tray's own "Remote support in progress" notice visible in it.

**The 20-second A/B in that run is worth nothing and says so itself** — the screen was idle, so both
codecs read ~0 KB/s. The 14x figure above stands on the bench, not on this run. A session-length
bandwidth comparison on that machine still wants a screen that is doing something.

### Still open on this

- **NVIDIA still would not activate** on that Optimus machine (`0x8000FFFF`) — see below. Untouched
  by this fix and still real.
- **Bandwidth over a session has not been compared** on anything but an idle screen.
- The old binary is still beside the new one on that machine as
  `RemoteSupport.Host.old-20260917-141620.exe`. Delete it once this is trusted.

---

## How it was diagnosed, 2026-09-17 on DESKTOP-2N6FVGT

**Parked in the morning for want of hardware, unparked the same afternoon.** DESKTOP-2N6FVGT is a
Kaby Lake i5-7300HQ with an Intel HD 630 *and* an NVIDIA GTX 1050 — two independent hardware
encoders, and the HD 630 unquestionably has a working H.264 encode engine. It was reached through
the product's own unattended access: `runscript` for the probes, `pushfile` for a 70 MB
self-contained bench, and the bench run from a scheduled local command.

The result, twice, once as the default pick and once forced onto Quick Sync:

```
encoder: Intel(r) Quick Sync Video H.264 Encoder MFT
1366x768, 819 kbit/s, taking BGRA directly
FAIL  the encoder never produced a key frame
      asynchronous, unlocked True (ok), events 0 (needInput 0, haveOutput 0, other 0,
      last type 0, last GetEvent no event queued), inputs accepted 0 refused 20,
      outputs taken 0, providesSamples True, outputSize 0, beginStreaming ok,
      startOfStream ok, last ProcessInput ok, last ProcessOutput ok
      frames the encoder was too busy for: 20
```

**`events 0`, on silicon that certainly works, with a signature identical to the Alder Lake-N laptop
this was written on.** That was the answer: the pump was wrong, and it turned out to be two swapped
constants — the section above is the fix. What follows is how it was cornered, kept because the
elimination is the expensive part and because the order it happened in is the lesson.

**The missing-silicon theory is dead and must not be repeated.** §25 of the handover, the roadmap
and the paragraphs further down this file all reason carefully from "an Alder Lake-N GPU with no
H.264 encode engine whose driver registers the transform regardless". That reasoning was sound and
the conclusion was wrong: the same code fails the same way on hardware that has the engine. Two
machines agreeing is not a coincidence, it is our bug reproducing.

### What the diagnostics have already eliminated

Read this before re-opening the suspect list below, because the evidence narrows it.

- **`last GetEvent: no event queued`** is `MF_E_NO_EVENTS_AVAILABLE` — a *sensible* answer to a
  correctly-formed call. A failed `IMFMediaEventGenerator` QI or a wrong vtable slot would give a
  garbage HRESULT or a crash, not a well-formed "nothing yet". **Suspects 1 and 2 in the list below
  are therefore unlikely**, which is the opposite of the order that list gives them.
- `unlocked True`, `beginStreaming ok`, `startOfStream ok` — the unlock and both messages did what
  they claim, and that is read back rather than assumed.
- `inputs accepted 0 refused 20` — every `ProcessInput` was refused with `MF_E_NOTACCEPTING`, which
  is *correct behaviour* for an async MFT that has not asked for input. The encoder is not broken;
  it is waiting for something we are not doing.

So the live candidates are, in the order they should now be tried:

1. **The Direct3D device is not marked multithread-protected.** `ID3D10Multithread::SetMultithreadProtected(TRUE)`
   on the device shared with the encoder. Hardware MFTs routinely refuse to start their event loop
   without it, and this is not currently done anywhere. **Try this first.**
2. **`METransformNeedInput` may require `BeginGetEvent` with a real `IMFAsyncCallback`** rather than
   a polled `GetEvent(MF_EVENT_FLAG_NO_WAIT)`. Some hardware MFTs only begin generating events once
   an async callback is registered.
3. **The message order around `SetD3DManager`** — the device manager must be set before the media
   types on most hardware encoders, and `MFT_MESSAGE_SET_D3D_MANAGER` must precede
   `NOTIFY_BEGIN_STREAMING`.
4. The bitrate and media-type details, which only matter once an event has ever arrived.

### The other finding: NVIDIA would not activate at all

```
NVIDIA H.264 Encoder MFT: would not activate (HRESULT 0x8000FFFF)
```

`E_UNEXPECTED`, on an Optimus machine. This was predicted before the run: Desktop Duplication runs
on the adapter driving the display — the Intel one — and our encoder deliberately shares that
duplication's Direct3D device so the frame never leaves the card. The NVIDIA transform will not
take a device belonging to the Intel GPU. **This is a separate, real problem**, and it matters
commercially: a gaming laptop is exactly the machine a customer calls about. It is not the reason
2.2 is failing, and it must not be conflated with the pump.

### The bandwidth table, for the record

The h264 rows are all `0.0 KB/s` with "82 frames the encoder was too busy for", which is the same
fault seen from the other end. The tile figures on that machine's 1366x768 screen: dxgi scroll
capped 12 = **1471 KB/s**, video = 1218 KB/s. Not comparable with the 2817 KB/s figure, which was
measured at 1920x1200 — that comparison must be made at one resolution.

### How to verify a fix

The bench is still on that machine at `C:\Temp\codecbench\bench\`, unpacked and hash-verified.

**This used to need a hand on that machine and no longer does.** The old host held the Desktop
Duplication for as long as a session was open, so the session used to start the bench was exactly
what made it fail with "access denied, another process already has this output duplicated". The
rebuilt host gives the screen back after three seconds with no subscriber — proven in its own
trace, 3.016 s after acquiring it:

```
14:22:48.378 capture: using DXGI Desktop Duplication
14:22:51.394 capture: nobody subscribed for 3s; released the duplication and the encoder
```

So the loop is now: rebuild, push the changed files, and run — a tool that does not subscribe to a
monitor leaves the screen free while it works.

---

## The one paragraph that matters

The codec is written, it compiles, it negotiates, it falls back, and everything underneath it is
measured correct. **The encoder itself has never produced a frame**, and this laptop cannot make it.
It does have Intel Quick Sync Video H.264 Encoder MFT -- hardware, asynchronous, willing to take
BGRA directly, the best case this was written for -- and it activates, unlocks, accepts the Direct3D
device and both media types, reports streaming started, and then never asks for a frame. The
Microsoft AVC DX12 Encoder refuses the output type outright. Both fit an Alder Lake-N GPU with no
H.264 encode engine whose driver registers the transform regardless.

Which leaves **the asynchronous pump unproven, and unprovable here**: an encoder that never asks for
input looks identical whether the silicon is absent or `MfTransform`'s event loop is wrong.

So the next session's first job is not to write anything. It is to run two commands on a machine
with a WORKING encoder and read one diagnostic line.

---

## Step zero: find a machine

Neither machine available on 2026-09-16 can do this, and both doors were tried and shut — §25 of
the handover has the detail, including why the K430 cannot be given its iGPU back. **The next
session starts at the office, on machines not yet seen.**

Run this on each candidate first. It needs nothing installed and touches nothing — plain PowerShell
reading the Media Foundation registry — so for a machine that is not on the portal, copy the one
file and run it there:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\hostinfo\codecprobe.ps1
```

or, for a machine that IS on the portal:

```
tools\runscript\bin\Release\net8.0\runscript.exe <MACHINE> codecprobe.ps1
```

You are looking for a **hardware** H.264 encoder: `Intel® Quick Sync Video H.264 Encoder MFT`,
`NVIDIA H.264 Encoder MFT`, or an AMD equivalent. `H264 Encoder MFT` on its own is Microsoft's
software encoder, which 2.2 refuses by design, and a machine with only that one is no better than
the two already tried.

And remember the warning the probe itself carries: **registered is not working.** This laptop
registers Quick Sync and it never encodes a frame. The probe narrows the search; `--h264check`
is what settles it.

## Then run these, in this order

On a machine whose encoder actually works -- a normal desktop or a laptop with a proper Intel,
NVIDIA or AMD GPU. Not this one.

```
tools\codecbench\bin\Release\net8.0-windows\codecbench.exe --mfts
```

Does it have an encoder at all, and what is it called? If this prints `(none)` under **video
encoder**, stop: that machine cannot answer anything either, and the answer is to find one that
can. It also prints whether the encoder will take BGRA directly, which decides whether the GPU
colour conversion runs.

```
tools\codecbench\bin\Release\net8.0-windows\codecbench.exe --h264check
```

Encodes one frame of a saturated still scene, decodes it, and compares it to the screen. **Nothing
else in this stage is worth looking at until this passes.** It writes the PNGs out: a tint is the
colour matrix, a shift or a swap is a vtable slot, a blank is the frame never reaching the encoder.

```
tools\codecbench\bin\Release\net8.0-windows\codecbench.exe --source all
```

The table the whole stage is judged on — GDI, DXGI and H.264 on the same three scenes. **2817 KB/s
scrolling is the number to beat.**

```
tools\codeccheck\bin\Release\net8.0-windows\codeccheck.exe DESKTOP-OPHQIDH --seconds 20
```

The same machine, the same screen, seconds apart, once offering H.264 and once offering only tiles.
Both ends must be the new build — the host ships inside the client payload, so `build.ps1` and then
reinstall on the far machine, or it will keep answering as a pre-2.2 host. Which, usefully, is
already tested: it did exactly that today and the console coped.

**The idle figure is not the result.** An untouched machine reports 0 KB/s on both codecs, because
2.1 already made a still screen free, and `codeccheck` says so rather than claiming a win. The
comparison means something only while the screen is doing something.

---

## What is most likely to be wrong, in order

Nothing below is a known bug. They are the places where this code made a decision that a machine
with an encoder will be the first to judge.

1. **The asynchronous encoder pump. This is the one.** Hardware encoders are async MFTs: they
   announce when they want a frame and when they have one, and `ProcessInput` without being asked
   returns `MF_E_NOTACCEPTING`. `MfTransform` implements that, and it has never once seen an event.

   `MfTransform.Diagnostics` prints on failure and settles it in a line. On the first working
   encoder:
   - `events 0` — the pump is wrong. Everything downstream is untested guesswork and this is where
     the session goes. Suspects, in order: the `IMFMediaEventGenerator` QI, `GetEvent`'s slot (3),
     the message order in `BeginStreaming`, and whether an async MFT needs `BeginGetEvent` rather
     than polled `GetEvent`.
   - `needInput` climbing but `haveOutput` 0 — the encoder is taking frames and producing nothing;
     look at the media types and the bitrate, not the pump.
   - both climbing but `outputs taken 0` — `ProcessOutput`, `MFT_OUTPUT_DATA_BUFFER`'s padding, or
     the provides-samples branch.

   Ruled out here already, so do not spend time on them again: the async unlock (set and read back,
   `unlocked True`), `MFSTARTUP_FULL` versus `LITE` (no difference), and the two start-streaming
   messages (both `S_OK`).

   Beyond that, the shape of `H264Encoder.Encode` — feed one, wait a bounded time, collect whatever
   is there — assumes low-latency mode keeps the pipeline one frame deep. If `Dropped` climbs with
   the frame rate, that assumption is wrong too and the fix is to stop treating one call as one
   frame.
2. **Whether the encoder takes BGRA.** It is asked, not assumed, and both answers are implemented —
   but only the NV12 answer's converter has ever run. `DirectBgra` in the trace line says which.
3. **The forced key frame.** `CODECAPI_AVEncVideoForceKeyFrame` is set before the input sample, and
   the GOP is two seconds as a floor under it. If a technician subscribing sees black for up to two
   seconds, the force is being ignored and the floor is doing the work.
4. **The sequence header.** SPS and PPS are prepended to a key frame that does not already carry
   them. If the console decodes nothing at all, look here first — and at
   `H264Decoder.DroppedBeforeKey`, which counts exactly this.
5. **The bitrate curve.** `H264Encoder.BitrateFor` maps the JPEG slider onto 0.4–6 Mbit/s at 1080p12
   and is a guess informed by nothing. The first real measurement replaces it.

---

## What IS proven, and how

Do not re-derive these.

**Everything the encoder stands on.** `codecbench --mfpipe` on this laptop: a real duplicated frame
through `MediaDevice`, the device manager, `SetD3DManager`, media type negotiation, ProcessInput and
ProcessOutput, the texture wrapping, `IMF2DBuffer` and `Nv12.ToBgra`, compared to the screen at
**0.5 of 255**, with the picture looked at. Correct primaries, no channel swap, correct geometry.

**The colour matrix**, by measurement: BT.709 0.5 against BT.601 3.9 at 1920x1200, agreeing with
`Nv12.Is709`.

**Backward compatibility**, over a real session: a pre-2.2 host ignored `SubscribeMsg.Codecs`, sent
tiles, and the new console composed the picture.

**The server needs no change**, as the roadmap predicted. It was not touched.

---

## The other thing this session nearly shipped

The video encoder category GUID in `Mf.cs` was wrong. **`MFTEnumEx` given a category that does not
exist returns `S_OK` and zero transforms** — identical to a machine with no encoder. So the
enumeration came back empty under every flag combination, and this file, the handover, the roadmap
and a commit message all said, at length and with evidence, that the machine had no H.264 encoder.
It has Quick Sync.

Two things make it worth remembering. The decoder and processor GUIDs were right, so two of three
categories enumerated perfectly and nothing looked broken — **one empty category among several full
ones should have been the question.** And the GUIDs were never checkable-looking but always
checkable: they are subkeys of `HKLM\SOFTWARE\Classes\MediaFoundation\Transforms\Categories`, named
without braces. Ten seconds of `Get-ChildItem` against a day of confident wrongness.

## The lesson this session added to the pile

The colour check passed before it worked. It decoded the same frame under both YUV matrices,
compared each to the screen, got **the same number to one decimal place for both**, and reported a
pass — because the scene it was looking at was black text on a white page, and on grey the two
matrices produce identical pixels. It was not measuring the thing it was written to measure and
nothing about its output said so.

`SceneKind.Colour` exists for that: still, saturated, primaries and complements and skin tones, with
a grey ramp as a control. With it, the two matrices score 0.5 and 3.9 and the harness has an
opinion.

That is now five times across 2.1 and 2.2 — six counting the category GUID, which is the same fault
one level down: an API answering a question it was never asked, in the same words it uses for the
answer you expected. It is apparently the default state of a harness, and the only defence is to
ask, of every check: **what would this print if the thing it is testing were absent?** If the answer
is "the same thing", it is not a check.

`MfTransform.Diagnostics` is that question answered in advance for the codec: it counts events seen
before it counts events understood, records the HRESULT of every message, and reads the unlock flag
back rather than trusting the call that set it. All three distinctions cost nothing and each one
would have taken a round to find.

---

## Then 2.3 and 2.4

> **Superseded 2026-09-17. 2.3 went first.** The paragraph below says it is only worth doing once
> 2.2 has a measured cost to adapt. That reasoning assumed the tile path was on its way out; it is
> not — 2.2 negotiates it as the permanent fallback for every machine with no hardware encoder,
> which is the class of machine that parked this stage in the first place. With 2.2 blocked on
> hardware, quality and frame rate are also the only remaining lever on the 2817 KB/s. The full
> argument is in `docs/NEXT-SESSION-ADAPTIVE.md`; read it before restoring the original order.
>
> The note on 2.4 below is unaffected and still correct.

Unchanged and still in that order. 2.3 adaptive quality is only worth doing once 2.2 has a measured
cost to adapt, and 2.4 the cursor is cheap — `GetFramePointerShape`, and `DXGI_OUTDUPL_FRAME_INFO`
already carries `PointerPosition` and `PointerVisible`, read and ignored in `Dxgi.cs` today. An
acquire with `AccumulatedFrames == 0` means only the mouse moved, which `TryTakeFrame` and
`TryCopyToVideo` both treat as nothing to send; that is right today and becomes the cursor path's
trigger in 2.4.
