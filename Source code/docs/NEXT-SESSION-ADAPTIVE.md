# Stage 2.3 — adaptive quality

Written 2026-09-17, in the session that parked 2.2 for want of hardware. **This is the next piece
of work.** `docs/NEXT-SESSION-CODEC.md` is still live and still correct; it is waiting on a machine,
not on a decision. `docs/HANDOVER.md` wins on any matter of fact.

---

## The one paragraph that matters

The host sends a picture at a quality a human chose and a frame rate nobody chose — `Fps = 12`,
written as a literal at all three places the console sends `QualityMsg`. Neither number responds to
the link. When the link cannot carry what is being sent, the frames do not slow down and they do not
get uglier: they are **silently discarded, oldest first, by a bounded channel**, and because the
host records what the far end has at *capture* time rather than at *delivered* time, the discarded
rectangles are never sent again. The technician is left looking at a screen with stale patches in
it, on exactly the bad links where they can least afford to doubt what they are seeing. 2.3 is the
controller that makes those two numbers respond, and the repair of the thing underneath it that
absorbed the overload by losing picture.

**That repair is done and proven as of 2026-09-17 — see "What is DONE" below. The controller is not
started.** The paragraph above is left in the tense of the day it was written, because it states the
problem, and the problem is what the rest of this file answers.

---

## Why this is happening before 2.2 finishes

`NEXT-SESSION-CODEC.md` says, at the bottom, that 2.3 "is only worth doing once 2.2 has a measured
cost to adapt". **That was right when it was written and is wrong now**, for two reasons that only
became visible afterwards. Reversing it is deliberate; this section is the argument, so that nobody
reverses it back by reading the older line.

**The tile stream is not interim work.** 2.2 negotiates: a host that cannot produce H.264 falls back
to JPEG tiles, by design, for ever. On 2026-09-16 this laptop turned out to be exactly such a
machine — it registers an encoder and never produces a frame — and the whole reason 2.2 is parked is
that a machine with a *working* hardware encoder was not to hand. That is evidence about the estate
we will actually be installed on. Every machine without hardware encode runs the tile path for the
life of the product, and an adaptive tile path is permanently valuable rather than a stopgap.

**It is the only remaining lever on the number.** 2817 KB/s scrolling is what stage 2 exists to fix.
2.1 could not move it and said so. 2.2 was to be the answer and cannot be measured here. Quality and
frame rate are the two inputs to that number that are already on the wire, already obeyed by the
host, and already measurable by a harness that exists. Nothing else in the stage can be worked on
without hardware.

And when 2.2 does come back, none of this is wasted: the controller measures the link and sets a
target, and what consumes the target — JPEG quality and frame rate, or encoder bitrate and frame
rate — is one branch at the bottom. `H264Encoder.BitrateFor` maps the same slider onto 0.4–6 Mbit/s
today and is described in that brief as "a guess informed by nothing". A controller producing a
measured target is what replaces the guess.

---

## What is already there

Do not rebuild any of this.

**The wire.** `QualityMsg { int Jpeg, int Fps }` (`Protocol.cs:407`) already carries both numbers.
The host already obeys both: `HostEngine.cs:133` takes the quality, clamps the rate to 1–30, and the
capture loop paces itself to `1000 / _fps` milliseconds a pass (`HostEngine.cs:519`). **No protocol
change and no server change.** What is missing is anything that sends a value other than the
constant.

**The pacing loop is already the right shape.** `HostEngine.cs:482-521` times each pass with a
`Stopwatch` and sleeps the remainder of the budget. A controller changes `_fps`; the loop needs no
other edit.

**The measurement.** `tools/codecbench` drives the real capture path against scenes it paints
itself, so runs weeks apart compare. It has already caught a ten-times bandwidth regression and
caught itself measuring the wrong screen. The "done when" for this stage is a number, and the thing
that produces the number exists.

**The manual slider stays.** A technician on a bad link often wants it ugly on purpose. The slider
becomes an override and a ceiling, not a thing the controller fights.

---

## What is DONE (2026-09-17): the floor

**The repair below is built and proven. The controller is not started.** `codecbench --stale` is
14 of 14 on this laptop, and the two halves of it are worth reading separately.

**The queue's contract, proven against a real socket that has stopped draining:** it refuses the
newest frame rather than evicting an older one; it refuses after 11 frames — 2.1 MB, about three
quarters of a second of backlog — rather than after a minute of one; the caller is told; text
messages and file chunks are still accepted in the exact state that refused a frame; and every
frame the queue *accepted* arrived, in order, with no gaps. That last one is the property the host's
convergence depends on.

**The consequence, in pixels, with the old policy run beside the new one on the same screen:**

```
old policy (refusal ignored):  9 tiles wrong at the far end, worst 74/255
new policy (complete frame):   0 tiles wrong at the far end, worst  1/255
```

Nine is the right number and that is the check on the check: the patch painted during the overload
is 384x384, which is exactly nine tiles of 128 px. Identical across three consecutive runs.

**One thing changed from what this brief originally said**, and the harness is what changed it. The
threshold was written as an item count and had to become a byte count: at 12 fps a stalled socket
needs a minute to queue a few hundred frames, so an item-count threshold could never fire for the
screen stream at all. A frame is a few hundred KB and a ping is thirty bytes; an item count cannot
tell those apart, and neither the drop rule nor the controller above it can work on a number like
that. `PendingBytes` is the signal. It also means the original eviction was most often triggered by
**file chunks and audio** — the high item-rate producers — evicting screen tiles, rather than by the
screen stream filling its own queue, which is how a fault in the screen path could go years without
being traced to a file transfer happening at the same time.

**It also turned up a separate fault, filed and then FIXED the same day as BUGS #8:** nothing
acknowledged, sequenced or re-sent a file chunk, so one lost on a slow link hung a file-set
transfer for ever and made a single-file transfer report success it could not know about. Senders
now wait for room rather than losing a chunk, and both receivers detect an offset gap and fail
loudly. `filesetcheck` is 26 of 26 including a lossy link tested in both directions.

**Two lessons the harness itself taught, both of the kind §25 of the handover collects.** The first
version froze an animating scene after the overload — which repaints every pixel, so the next
capture resent everything and both policies converged. It printed both columns, they agreed, and it
reported that it could not tell them apart rather than reporting a pass. The second version topped
the queue up to the threshold and then slept while painting; a stalled socket still absorbs
megabytes into its own buffers, so the queue drained back under the line and the frame it was
supposed to lose was accepted. Both were caught by the check that runs the absent case beside the
present one, and neither would have been caught by a check that only ran the fixed path.

---

## What is DONE (2026-09-17): the controller

**Built, proven against scripted adversity, and proven quiet on real hardware.** The sections below
are the brief it was built from and are still the reasoning; this is what came of them.

**`src/RemoteSupport.Host/LinkController.cs`** — deliberately a class of its own rather than logic
inside the capture loop, so it can be driven at a thousand passes a second instead of twelve. It
takes a queue depth, a refusal count and the share of the screen that moved, and answers with a
quality and a frame rate. That is its whole contract, and it is what gets tested.

**The five decisions below were settled like this:**

1. **Two ladders, chosen by content.** The share of tiles that changed decides: above half the
   screen is treated as a moving picture and loses **frame rate** first; below that is treated as
   still and loses **quality** first. Proven to diverge — under identical pressure, moving settles
   at 3 fps and still at 4 fps, while still drops to quality 28 against moving's 36.
2. **Fast down, slow up**, and measured: it falls in 2.1 s and climbs back in 15.4 s. A refusal
   bypasses the step-down rate limit entirely, because by then picture has already been lost.
3. **The slider is a ceiling.** The controller may go below it and never above it.
4. **The technician is told**, through a `Link` line in the Info tab — *"holding back 4 of 6 —
   quality 33 of 80, 6 of 15 fps (mostly still)"*.
5. **On the host**, as argued below.

**The trap was real and is handled.** `QualityMsg` no longer sets `_forceFull` and no longer
rebuilds the encoder. Bitrate moves live through `ICodecAPI` (`H264Encoder.TrySetBitrate`); an
encoder that will not take it live simply holds its bitrate rather than triggering a rebuild,
because a rebuild per nudge costs more than the adaptation saves.

### Proven, and how

**`codecbench --adapt`, 13 of 13.** No screen, no socket, no machine. Every check asserts a
*property*, never a number — the ladders are guesses meant to be replaced by measurement, and a
check asserting "level 3 gives quality 33" would fail the first time someone improved them. It
prints the non-adaptive case beside the adaptive one (*quality 80, 15 fps on a link 2 MB behind*
against *quality 28, 4 fps*), so it can show it is measuring something rather than agreeing with
itself.

**On DESKTOP-2N6FVGT, the property that mattered most: it does nothing.**

```
14:45:16.091 link: carrying it: quality 55, 12 fps
total 'link:' lines ever written: 1
```

One line for the whole session — announced once when the console set the ceiling, then silent
through the H.264 pass, the tiles pass and a provoked change on screen. On a healthy link it stays
at level 0 and does not hunt. `codeccheck` was 8 of 8 alongside it, so it does not interfere with
the codec either.

### NOT proven, and this is the gap

**It has never been watched backing off on a genuinely bad link.** Everything above is either
scripted adversity on this laptop or a healthy LAN. The remaining test is the one no harness can
do: a session over a phone hotspot on one bar, reading an error message and scrolling a log, with
the `Link` line watched in the Info tab. Until someone has seen that, the back-off is a controller
with a good argument behind it rather than one known to help.

**The thresholds are guesses.** 512 KB to back off, 64 KB to recover, 2.5 s of quiet. They were
chosen from the old tile stream's cost, not measured against the depth at which latency becomes
visible to a technician. That measurement is worth making and has not been made.

---

## The brief this was built from

---

## The finding this work repaired first

**`WsConnection` absorbed overload by throwing picture away, and the host never learned.** Fixed
2026-09-17; the account below is what it was and why, because the reasoning outlives the fix.

```
Channel.CreateBounded<(byte[], bool)>(new BoundedChannelOptions(1024) { FullMode = DropOldest })
```

`WsConnection.cs:11`. `SendBinary` is `TryWrite` and its result is discarded (`:28`). So when the
socket cannot drain as fast as the capture loop fills, the queue reaches 1024 and every further
frame silently evicts the oldest one. Nothing returns false, nothing is traced, nothing counts it.

That would be survivable if the frames were whole pictures. **They are deltas.** Both capture paths
record what the far end is believed to have at the moment of capture:

- GDI: `st.Hashes![cell] = hash;` — written unconditionally, one line after the comparison that used
  it (`ScreenCapture.cs:105-106`).
- DXGI: `CopyTileIfDifferent` copies the new pixels into the persistent frame buffer as it decides
  they differ (`DxgiCapture.cs:483-485`), and that buffer *is* the record of what was sent.

So a tile that is captured, queued, and then evicted is marked as delivered. The next pass compares
against the new state, finds no difference, and sends nothing. **The stale rectangle persists until
that region of the screen changes again on its own, or until something forces a full frame.** On a
mostly-static screen — a technician reading an error dialog — that can be indefinitely.

This is very likely the mechanism behind some share of the "black screen" and "stale picture"
reports that have come in over the life of this thing, and it is worth saying plainly that it has
never been proven to be. It is a mechanism that certainly exists and certainly produces that
symptom. Whether it produced any particular report is unknown, and a guess about that is exactly
what §7 of the handover exists to prevent.

**Two separate repairs, and they should not be conflated:**

1. **Stop losing deltas.** Either the eviction must invalidate the host's record of what was
   delivered — so the next pass resends it — or binary frames must not be evicted at all and
   backpressure must reach the capture loop instead. The second is better and is the same signal the
   controller wants, which is why they are one job.
2. **Count it.** Whatever is decided, an evicted frame must increment something that
   `host-trace.log` can print. A fault that leaves no trace is the one that costs a day.

**`DropOldest` is right for text and wrong for binary.** A queued `SessionStateMsg` or
`ClipboardMsg` that gets old is still worth delivering and there are few of them; a queued screen
frame that gets old is worth less than the one behind it. Whatever replaces this should keep that
distinction rather than picking one rule for both — the channel already carries a `bool text` beside
every payload.

---

## The trap that will eat the first attempt

**Every `QualityMsg` forces a full frame.**

```
case QualityMsg q: _jpeg = q.Jpeg; _fps = Math.Clamp(q.Fps, 1, 30); _codecDirty = true; _forceFull = true; break;
```

`HostEngine.cs:133`, and the comment two lines above explains why: a rebuilt encoder has nothing to
decode against, so the demand is correct **for a codec change**. It is ruinous for a controller. A
full 1920×1200 frame is the most expensive thing the host can send, and a controller that nudges
quality once a second would send one every second — **on the congested link that caused the nudge**.
The first naive implementation of 2.3 will make bad links worse and will look, from the outside,
exactly like adaptive quality not working.

Separate the two demands. A change of *codec* needs a full frame; a change of *quality or rate* does
not, on the tile path, because every tile is independently coded and the next one carries the new
quality on its own. On the H.264 path a quality change should ask the encoder for a key frame
through `CODECAPI_AVEncVideoForceKeyFrame`, which 2.2 already sets, rather than re-capturing
everything.

Rate-limit the controller regardless. It should be allowed to move perhaps twice a second, and to
move down faster than it moves up — see below.

---

## What to measure, and what does not exist yet

The controller needs to know the link is struggling **before** the queue overflows, because by then
picture is already being lost.

**Queue depth is the cheap, direct signal and it is one property away.** `ChannelReader<T>.Count` is
supported on a bounded channel; `WsConnection` needs to expose it — `PendingSends`, alongside
`IsOpen` — and the capture loop needs to read it. A depth that is climbing means the socket is not
draining as fast as we are filling, which is the definition of the condition being adapted to. It
costs nothing and needs no new message.

**Round-trip time does not exist today and is the one thing that has to be added.** `PingMsg` is an
empty class sent on a 15-second keepalive loop from both `DeviceLink.cs:114` and `TechLink.cs:101`,
with no identifier and no timestamp, so nothing can be correlated to it and nothing measures
anything. Give it a token the far end echoes. Note that the keepalive interval is far too slow for a
controller, so either the interval changes for the session link or the measurement rides on
something else.

**Prefer queue depth over round-trip time where they disagree.** RTT tells you about the path; queue
depth tells you about *this* stream on it, which is the thing being controlled, and it is the one
that is true on a link that is fine for small packets and hopeless for 2.8 MB/s. Build the
controller on depth and use RTT as corroboration.

**Decide what "struggling" is by measuring, not by choosing a constant.** The number wanted is the
depth at which latency becomes visible to a technician, and `codecbench` with a throttled link can
find it.

---

## The five decisions most likely to be wrong

Nothing here is a known bug. These are the places where this work will make a choice that only a
measurement can judge, listed so the next session knows where to look when the picture is worse
rather than better.

1. **Moving quality and rate together rather than in an order.** They are not equivalent. For
   scrolling text, frame rate is what makes it readable and quality is what makes it legible;
   dropping to 3 fps at quality 90 and dropping to 12 fps at quality 30 cost about the same and look
   completely different. The likely right answer is **drop frame rate first for video, drop quality
   first for text**, and the host cannot tell which it is looking at — but the *tile count per pass*
   is a decent proxy, and it is already computed.
2. **How fast it is allowed to move.** Fast down and slow up is the standard shape and is probably
   right: a technician tolerates a picture that gets ugly instantly when the link sags far better
   than one that oscillates. An oscillating controller is the classic failure here and it will look
   like flickering quality.
3. **Whether the slider is a ceiling or a target.** If a technician sets quality 90 on a link that
   cannot carry it, does the controller override them? Recommendation: **the slider is a ceiling**,
   the controller may only go below it, and the session window must show when it has — an adaptive
   system that silently disagrees with a control the user just moved is a bug report waiting to be
   filed.
4. **What the technician is told.** A quality number that moves on its own with no explanation reads
   as a fault. Something small and honest on the status bar — the current effective rate, and that
   it is the link rather than a setting — costs little. The precedent is drag-in, which was reported
   as broken purely because it was invisible (roadmap 1.3).
5. **Whether the controller belongs on the host or the console.** The host has the queue and the
   capture cost; the console has the arrival times and the decode cost. **Host**, because it is the
   end that can act without a round trip, and a controller whose corrections travel over the
   congested link it is correcting is a controller with a delay in the worst possible place.

---

## How this gets proven

The stage rule applies: a harness before it is called done, and it must answer "what would this
print if the thing it tests were absent?"

**`codecbench` extended with a constrained link.** The existing three scenes, run with the send side
throttled to a fixed bandwidth, comparing fixed settings against the controller. The measurement is
not "did quality change" — it is **bytes per second, frames actually delivered, and how long a
changed rectangle takes to appear at the far end**. A controller that hits the bandwidth target by
delivering a slideshow has not succeeded, which is why delivered frames and staleness are in the
table beside the bytes.

**A test for the staleness repair specifically, and it must fail before the fix.** Fill the queue
past 1024, change a region of the screen while it is full, drain it, and assert the far end has the
new pixels. Run it against today's code first — it must show the stale rectangle. A repair whose
test passes before the repair is not a test.

**Then the real thing.** `tools/codeccheck` against DESKTOP-OPHQIDH over the LAN, and — the part
that actually matters and that no harness on this desk can perform — **a session over a genuinely
poor link**, a phone hotspot with one bar, doing the thing the product is for: reading an error
message, scrolling a log, watching an installer. Stage 2's own "done when" is that a side-by-side is
not embarrassing, and this is the stage most prone to feeling better without being better.

---

## What this does not touch

**2.4, the cursor**, is the other stage-2 item that needs no encoder, and it stays after this one.
It is cheap — `GetFramePointerShape`, and `DXGI_OUTDUPL_FRAME_INFO` already carries
`PointerPosition` and `PointerVisible`, read and ignored in `Dxgi.cs` today. It is worth noting that
2.3 makes 2.4 matter *more*: the lower the frame rate goes, the more the cursor is what makes the
picture feel alive, and a real cursor at 6 fps reads far better than a drawn ring at 6 fps.

**2.2 stays open and stays first in line the moment hardware exists.** Nothing here changes the two
commands at the top of `NEXT-SESSION-CODEC.md` or makes them less urgent.
