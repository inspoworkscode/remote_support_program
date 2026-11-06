# Talking to the customer — roadmap 3.3

> **Status, 2026-09-30: BUILT, both ways, in the app and the browser. NOT on hardware.** HANDOVER §67
> is what was built and what building it found (a technician could open the customer's microphone —
> fixed). `voicewire` 37, `websupportcheck --browser-voice` 26, `voicelocal` 4, `audiolocal
> --exclude-self`. Next: `TEST-3.6.md` section AH, starting with 223 — micprobe on a Windows 11 machine.

Written 2026-09-30. Read `docs/HANDOVER.md` first — §18 (Listen) and §66 (support in the browser)
are the two things this is built on. `docs/NEXT-SESSION-AUDIO.md` holds the reasoning that still
governs sound in general; this brief is only the microphone.

---

## Clint's words

> lets work on the mic feature. this will be great if it can work on the app and the online view
> only. Ideally mic chat should work both ways if a client has a mic installed. is this possible
> and does it make sense?

**His answers, the same day:**

1. **The customer must allow their microphone, every session.** The technician's voice reaching
   their speakers needs no permission; their microphone opening does, each time. **Never in an
   unattended session** — nobody is there to allow it, and a microphone opened in an empty room is
   eavesdropping.
2. **The technician's microphone is an open mic with a Mute button**, like a phone call — not push
   to talk. That makes echo cancellation a requirement, not a nicety.
3. **Session recordings stay picture only** in the first version. Recording a conversation is a
   POPIA consent question; decide it later, deliberately.

---

## What already exists, and what it buys

- **The wire needs no server change.** The hub relays binary frames host → every viewer and viewer
  → host without reading them (§18). `BinKind.Audio = 3` already carries
  `[3][seq:u32][rate:u32][channels:u8][format:u8][pcm]` one way; the other way is the same frame.
- **SYSTEM in session 1 can render sound** — the test tone (§18). So the technician's voice can
  come out of the customer's speakers from the host.
- **The browser page already speaks the host protocol** (§66, `wwwroot/help.js`). It can send and
  receive the same audio frame; `getUserMedia({ audio: { echoCancellation: true } })` gives it echo
  cancellation for free. **The browser half is the easier half.** It also works on a PHONE, where
  screen sharing does not — a caller on a phone can still talk.

## What is NOT known, and is measured before anything is designed

1. **Can the host, as SYSTEM, open the customer's microphone?** Windows' microphone privacy
   (`CapabilityAccessManager\ConsentStore\microphone`, device-wide and per user, "Let desktop apps
   access your microphone") may refuse a SYSTEM process, or hand it silence without an error. If
   it refuses, the §39 answer applies: a short-lived helper running as the person. That would also
   make Windows show its own microphone-in-use icon, which is a feature.
2. **Does Windows' own echo canceller (the Voice Capture DSP, `CLSID_CWMAudioAEC`) run as
   SYSTEM?** It is part of Windows, so it keeps the "nothing is bought in" rule.
3. **Can Listen leave our own voice out?** Loopback records everything the speakers play,
   including the technician's voice — which would come straight back to them. Windows 10 2004+ has
   process loopback with an EXCLUDE mode (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`).
4. **What does the customer's microphone hear of their own speakers?** Play the 1 kHz tone and
   capture the microphone: the Goertzel reading IS the echo path the canceller has to remove, and
   proves the capture is real without anybody speaking.

`tools/hostinfo/micprobe.ps1`, run through `tools/runscript`, answers all four.

### Measured, 2026-09-30

| | this laptop, as Clint (Win 11 26200) | DESKTOP-OPHQIDH, as SYSTEM (Win 10 19045) | DESKTOP-2N6FVGT, as SYSTEM (Win 10 19045) |
|---|---|---|---|
| 1. microphone opens, real audio (not all zeroes) | **yes** (Realtek) | **yes** (Lenovo USB, peak 0.167) | **yes** (Realtek, peak 0.0096) |
| 2. Voice Capture DSP delivers | **yes**, 16 kHz mono | **yes** | **yes** |
| 3. process loopback | **activates; INCLUDE heard our tone at 0.36, EXCLUDE heard nothing (0)** | activates | activates |
| 4. echo path | the raw mic did not hear its own speakers: **the Realtek driver cancels them itself** | not run (plays a tone) | not run |

**So the host does the voice itself — no helper as the person.** The expensive risk is retired on
Windows 10. **Still unmeasured: Windows 11 as SYSTEM** (no Windows 11 machine has a host yet), and
a person who has set "Let desktop apps access your microphone" to **Deny** — both privacy values
were Allow everywhere. Whatever SYSTEM can do there, **the host reads that setting and honours it**
(`micprobe` shows where it lives): a person who switched their microphone off in Windows has
answered already, and is told so rather than asked.

What building the probe found — each one would have cost the feature a day later:

- **The echo canceller delivers NOTHING until something is playing.** In system mode
  `ProcessOutput` is `S_FALSE` for ever with no render stream; with one — even of silence — it
  delivers at once. **The host must keep its voice playback stream open, writing silence, for the
  whole call**, or the customer's microphone goes dead whenever the technician stops talking.
- **`ProcessOutput`'s array must be `MarshalAs(UnmanagedType.LPArray)`.** On a COM interface an
  array marshals as a SAFEARRAY by default; Windows PowerShell died of an access violation with no
  other word, and only a .NET 8 copy said "SafeArrayMarshaler".
- **Process loopback must be used on an MTA thread.** The activation completes on one and
  `IAudioClient` has no proxy between apartments: `E_NOINTERFACE` from an STA thread.
- **A steady tone is removed by noise suppression**, the driver's and the DSP's own, so it flatters
  a canceller. The probe uses 250 ms pulses.

## Design, provisionally

- **Voice is not Listen.** Listen's buffer holds up to 600 ms, fine for hearing a machine and far
  too slow for a conversation. Voice gets its own frame kind and a ~150 ms buffer.
- **16 kHz mono 16-bit**, about 32 KB/s each way. Raw PCM first; the Opus question stands (§18) and
  is now sharper, because voice runs both ways on the customer's uplink.
- **One Talk button** on the session toolbar, becoming Mute once on. The customer's side (tray app,
  or the page) shows who is talking and asks to allow their microphone, with Stop always there.
- **Talking one way works on any machine with speakers.** A customer with no microphone hears the
  technician and types back in chat.

## Order of work

1. **Measure** — `micprobe` on this laptop, then on DESKTOP-OPHQIDH and DESKTOP-2N6FVGT through
   `runscript`.
2. **Technician → customer**, app and browser.
3. **Customer → technician**, with echo cancellation, after they allow it.
4. **The UI** on both sides, and a harness that proves a tone crosses both ways by Goertzel, not by
   ear.
