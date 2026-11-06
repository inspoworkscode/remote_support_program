// Support in the browser (stage 3.8.2, 2026-09-30) — the customer's side of the call.
//
// Two sockets. /ws/web is the caller's own: it rings, says who answered, carries the chat, and hands
// over the session. /ws/session?role=host is the same socket the support app's host uses, and this
// page speaks the same protocol on it: it announces one "monitor" (whatever was shared), and when a
// technician subscribes it sends H.264 frames in the host's own binary layout —
// [4][monitor i32][flags u8][width i32][height i32][Annex-B] — which the console's existing decoder
// shows. A browser with no H.264 encoder sends JPEG pictures in the tile layout instead.
//
// What it does NOT do, because no browser allows it: take any input. Mouse and key messages from
// the console are ignored; the session is marked view only on the server.
(function () {
  var root = document.getElementById('call');
  if (!root) return;
  var tenant = root.dataset.tenant, callerId = root.dataset.id, token = root.dataset.token;
  var company = root.dataset.company, me = root.dataset.me;
  var wsBase = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host;
  var $ = function (id) { return document.getElementById(id); };
  var stateEl = $('state'), subEl = $('sub'), shareBox = $('share'), shareBtn = $('shareBtn'), shareNote = $('shareNote');
  var log = $('log'), chatForm = $('chatForm'), chatBox = $('chatBox'), hangUp = $('hangUp');

  var ctl = null;          // the caller's socket
  var host = null;         // the session's host socket, once they share
  var session = null;      // { id, token, tech }
  var ended = false;

  function setState(text, sub, busy) {
    stateEl.innerHTML = '';
    if (busy) { var p = document.createElement('span'); p.className = 'pulse'; stateEl.appendChild(p); }
    stateEl.appendChild(document.createTextNode(text));
    subEl.textContent = sub || '';
  }
  function line(who, text, mine) {
    var m = document.createElement('div'); m.className = 'm' + (mine ? ' me' : '');
    var w = document.createElement('div'); w.className = 'who'; w.textContent = who;
    var t = document.createElement('div'); t.textContent = text;
    m.appendChild(w); m.appendChild(t); log.appendChild(m); log.scrollTop = log.scrollHeight;
  }
  function send(ws, msg) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(msg)); }

  // ---------------------------------------------------------------- the call
  function connect() {
    ctl = new WebSocket(wsBase + '/ws/web?tenant=' + encodeURIComponent(tenant) + '&id=' + encodeURIComponent(callerId) + '&token=' + encodeURIComponent(token));
    ctl.onmessage = function (e) {
      if (typeof e.data !== 'string') return;
      var m; try { m = JSON.parse(e.data); } catch (x) { return; }
      switch (m.t) {
        case 'call_update': onCall(m.call || {}); break;
        case 'start_session':
          session = { id: m.sessionId, token: m.hostToken, tech: m.technicianName || 'The technician' };
          setState(session.tech + ' is with you', 'Chat below, or share your screen so they can see what you see.', false);
          shareBox.style.display = 'block';
          // The host socket opens NOW, not when they share (3.3): it is the only way the
          // technician's voice can reach this page, and a phone — which cannot share at all — can
          // still talk. announce() says nothing until there is a picture to describe.
          openHost();
          break;
        case 'session_ended': finish(m.reason || 'The session has ended.'); break;
        case 'chat': line(m.fromTech ? m.from : 'You', m.text, !m.fromTech); if (m.fromTech && window.rspSounds) rspSounds.ding(); break;   // a reply dings (2026-10-07)
        case 'error': setState('Something went wrong', m.message || '', false); break;
      }
    };
    ctl.onclose = function () {
      if (ended) return;
      ended = true;
      endVoice(false);
      stopShare(true);
      setState('Disconnected', 'The connection to ' + company + ' was lost. If you still need help, fill in the form again or phone us.', false);
      chatForm.style.display = 'none'; hangUp.style.display = 'none';
    };
  }
  function onCall(c) {
    if (c.status === 'ringing') setState('Calling ' + company + '…', 'A technician will answer shortly. Keep this page open.', true);
    // Only before the session is handed over: the handover arrives FIRST, and its words — share your
    // screen — must not be replaced by these.
    else if (c.status === 'accepted' && !session) setState((c.acceptedBy || 'A technician') + ' is with you', 'Chat below.', false);
    else if (c.status === 'cancelled') finish('Your request was cancelled.');
  }
  function finish(why) {
    if (ended) return;
    ended = true;
    endVoice(false);
    stopShare(true);
    shareBox.style.display = 'none';
    setState('Thank you', why.replace(/[.!?]?\s*$/, '.') + ' You can close this page.', false);
    chatForm.style.display = 'none'; hangUp.style.display = 'none';
    try { host && host.close(); } catch (e) { }
    try { ctl && ctl.close(); } catch (e) { }
  }

  chatForm.addEventListener('submit', function (e) {
    e.preventDefault();
    var text = chatBox.value.trim(); if (!text) return;
    send(ctl, { t: 'chat', text: text });      // it comes back from the server, and is drawn then
    chatBox.value = '';
  });
  hangUp.addEventListener('click', function () {
    if (!confirm('End this support request?')) return;
    send(ctl, { t: 'cancel_call' });
    finish('You ended the request.');
  });
  window.addEventListener('beforeunload', function (e) { if (!ended && session) { e.preventDefault(); e.returnValue = ''; } });

  // ---------------------------------------------------------------- sharing the screen
  var stream = null, track = null, video = null, canvas = null, g = null;
  var W = 0, H = 0, srcW = 0, srcH = 0;
  var subscribed = false, wantsH264 = false, fps = 10;
  var encoder = null, codec = null, needKey = true, lastKey = 0, lastSent = 0, ts = 0;
  var timer = null, reader = null, sharing = false;
  var MAX_W = 1920, MAX_H = 1080, BUFFER_LIMIT = 1500000;

  shareBtn.addEventListener('click', startShare);

  async function startShare() {
    if (!session) return;
    shareNote.textContent = '';
    if (!navigator.mediaDevices || !navigator.mediaDevices.getDisplayMedia) {
      shareNote.textContent = 'This browser cannot share its screen. On a computer, use Chrome, Edge or Firefox — or get the support app.';
      return;
    }
    try {
      stream = await navigator.mediaDevices.getDisplayMedia({
        video: { frameRate: { ideal: 10, max: 15 }, width: { max: MAX_W }, height: { max: MAX_H }, cursor: 'always' }, audio: false
      });
    } catch (err) {
      shareNote.textContent = 'Nothing was shared. Press the button again whenever you are ready.';
      return;
    }
    beginWith(stream);
  }

  // Separate from the button so a test can hand in any stream (a canvas's captureStream) — the
  // same path everything after the browser's own picker takes.
  function beginWith(s) {
    stream = s; track = stream.getVideoTracks()[0];
    track.addEventListener('ended', function () { stopShare(false); });
    video = document.createElement('video'); video.muted = true; video.playsInline = true; video.srcObject = stream;
    video.play().catch(function () { });
    canvas = document.createElement('canvas'); g = canvas.getContext('2d', { alpha: false });
    sharing = true;
    shareBtn.textContent = 'Stop sharing';
    shareBtn.onclick = function () { stopShare(false); };
    shareBtn.removeEventListener('click', startShare);
    setState('You are sharing your screen', (session ? session.tech : 'The technician') + ' can see it, but cannot move your mouse or type.', false);
    openHost();
    pump();
  }

  function stopShare(quiet) {
    if (!sharing) return;
    sharing = false;
    if (timer) { clearInterval(timer); timer = null; }
    if (reader) { try { reader.cancel(); } catch (e) { } reader = null; }
    try { stream && stream.getTracks().forEach(function (t) { t.stop(); }); } catch (e) { }
    closeEncoder();
    if (quiet || ended) return;
    shareBtn.textContent = 'Share my screen again';
    shareBtn.onclick = null; shareBtn.addEventListener('click', startShare);
    setState((session ? session.tech : 'The technician') + ' is with you', 'You stopped sharing your screen.', false);
    send(ctl, { t: 'chat', text: '(I stopped sharing my screen.)' });
  }

  // ---- end-to-end encryption (2026-10-07, wwwroot/e2e.js): this page is the HOST, so it makes the session key ----
  var e2e = null;
  /** A picture, a voice frame: sealed while the hub says every viewer can open it, in order with everything else. */
  function hostBinary(buf) { if (e2e) e2e.seal.sendBinary(host, buf); else if (host && host.readyState === 1) host.send(buf); }

  // ---- the host socket: the support app's own protocol, spoken by the page ----
  function openHost() {
    if (host && host.readyState <= 1) { announce(); return; }
    host = new WebSocket(wsBase + '/ws/session?role=host&id=' + encodeURIComponent(session.id) + '&token=' + encodeURIComponent(session.token));
    host.binaryType = 'arraybuffer';
    e2e = window.rspE2e && rspE2e.available ? rspE2e.host(session.id) : null;
    host.onopen = function () {
      announce();
      // This host seals: every console that can says hello and is given the key (unsigned — nothing is installed here).
      if (e2e) e2e.ready.then(function () { send(host, { t: 'e2e_ready' }); });
    };
    host.onmessage = function (e) {
      if (typeof e.data !== 'string') {
        var u8 = new Uint8Array(e.data);
        if (window.rspE2e && rspE2e.isSealed(u8)) { if (e2e) e2e.seal.receive(u8, function (r) { if (r.bin) voiceFrame(r.bin.buffer); else hostMessage(r.msg); }); return; }
        // Sealing: content that came clear could only have come from the server.
        if (e2e && e2e.seal.refuseClear && rspE2e.isContentBinary(u8[0])) return;
        voiceFrame(e.data);
        return;
      }
      var m; try { m = JSON.parse(e.data); } catch (x) { return; }
      if (m.t === 'e2e_mode') {
        if (e2e) { e2e.seal.sealing = !!m.sealed; e2e.seal.refuseClear = !!m.sealed; needKey = true; }
        return;
      }
      if (m.t === 'e2e_hello') {
        if (e2e) e2e.ready.then(function () { return e2e.answer(m); }).then(function (k) { if (k) send(host, k); });
        return;
      }
      if (e2e && e2e.seal.refuseClear && !e2e.seal.isClearType(m.t)) return;
      hostMessage(m);
    };
  }

  function hostMessage(m) {
      switch (m.t) {
        case 'voice_talk': voiceTalk(m); break;
        case 'participants': voiceParticipants(m.technicians || []); break;
        case 'subscribe':
          subscribed = !!m.on;
          wantsH264 = (m.codecs || []).indexOf('h264') >= 0;
          needKey = true;                      // a new or returning viewer has seen nothing yet
          break;
        case 'quality':
          if (m.fps > 0) fps = Math.max(2, Math.min(15, m.fps));
          break;
        // The server dropped a frame on its way to a viewer that could not keep up (HANDOVER §142).
        case 'key_frame_request': needKey = true; break;
        case 'session_ended': finish(m.reason || 'The session has ended.'); break;
        // mouse, key and everything else the support app's host would act on: a browser cannot.
      }
  }

  function measure(sw, sh) {
    if (!sw || !sh) return false;
    if (sw === srcW && sh === srcH && W) return true;
    srcW = sw; srcH = sh;
    var scale = Math.min(1, MAX_W / sw, MAX_H / sh);
    // Even, because H.264 works in pairs of pixels and an odd edge is a decoder's argument.
    W = Math.max(2, Math.floor(sw * scale / 2) * 2); H = Math.max(2, Math.floor(sh * scale / 2) * 2);
    canvas.width = W; canvas.height = H;
    closeEncoder(); needKey = true;
    announce();
    return true;
  }

  function announce() {
    if (!W || !host || host.readyState !== 1) return;
    send(host, { t: 'monitors', monitors: [{ index: 0, x: 0, y: 0, width: W, height: H, name: 'Shared from the browser', primary: true }] });
    send(host, { t: 'sysinfo', info: {
      'Shared from': 'A web browser — view only',
      'Browser': navigator.userAgent,
      'Shared picture': W + ' × ' + H + (srcW !== W ? ' (from ' + srcW + ' × ' + srcH + ')' : '')
    } });
  }

  // Where the pictures come from. The customer is looking at whatever they are showing, so this tab
  // is usually NOT in front — and a browser slows a background tab's timers to one a second or
  // less. Chrome and Edge hand every captured frame straight to the page (MediaStreamTrackProcessor),
  // with no timer at all; the timer is only for the browsers that cannot.
  function pump() {
    if (window.MediaStreamTrackProcessor) {
      try {
        reader = new MediaStreamTrackProcessor({ track: track }).readable.getReader();
        (async function loop() {
          var r = reader;
          while (sharing && r === reader) {
            var got; try { got = await r.read(); } catch (e) { break; }
            if (got.done) break;
            var f = got.value;
            try { produce(f, f.displayWidth, f.displayHeight); } finally { f.close(); }
          }
        })();
        return;
      } catch (e) { reader = null; }
    }
    if (timer) clearInterval(timer);
    timer = setInterval(function () { produce(video, video.videoWidth, video.videoHeight); }, 1000 / 15);
  }

  // One picture at a time, at the viewer's frame rate, and never more than the link can take: a
  // frame skipped BEFORE it is encoded costs nothing, where one dropped after would break the
  // pictures that follow it.
  function produce(source, sw, sh) {
    if (!sharing || !host || host.readyState !== 1) return;
    if (!measure(sw, sh) || !subscribed) return;
    var now = performance.now();
    if (now - lastSent < 1000 / (wantsH264 && window.VideoEncoder ? fps : Math.min(fps, 3))) return;
    if (host.bufferedAmount > BUFFER_LIMIT) return;
    lastSent = now;
    g.drawImage(source, 0, 0, W, H);
    if (wantsH264 && window.VideoEncoder && codec !== false) encodeH264(now);
    else sendJpeg();
  }

  // ---- H.264 through the browser's own encoder (WebCodecs) ----
  var CODECS = ['avc1.42E028', 'avc1.42E01F', 'avc1.4D0028', 'avc1.640028'];
  var paramSets = null;       // SPS and PPS as Annex-B, when the encoder gives them only in its description

  async function pickCodec() {
    for (var i = 0; i < CODECS.length; i++) {
      var cfg = config(CODECS[i]);
      try { var r = await VideoEncoder.isConfigSupported(cfg); if (r.supported) return cfg; } catch (e) { }
    }
    return null;
  }
  function config(c) {
    return { codec: c, width: W, height: H, bitrate: 1500000, framerate: 15, latencyMode: 'realtime', avc: { format: 'annexb' } };
  }

  var building = false;
  function encodeH264(now) {
    if (!encoder) {
      if (building) return;
      building = true;
      pickCodec().then(function (cfg) {
        building = false;
        if (!cfg) { codec = false; return; }       // no H.264 here: pictures from now on
        codec = cfg.codec;
        encoder = new VideoEncoder({ output: onChunk, error: function () { closeEncoder(); codec = false; } });
        encoder.configure(cfg);
        needKey = true;
      });
      return;
    }
    if (encoder.encodeQueueSize > 2) return;
    var key = needKey || now - lastKey > 4000;   // a key frame every 4 s, so a viewer can always recover
    if (key) { needKey = false; lastKey = now; }
    var frame = new VideoFrame(canvas, { timestamp: (ts += Math.round(1e6 / fps)) });
    try { encoder.encode(frame, { keyFrame: key }); } finally { frame.close(); }
  }

  function onChunk(chunk, meta) {
    if (meta && meta.decoderConfig && meta.decoderConfig.description) paramSets = avcToAnnexB(meta.decoderConfig.description);
    var data = new Uint8Array(chunk.byteLength); chunk.copyTo(data);
    var key = chunk.type === 'key';
    // A key frame must carry its SPS and PPS: the console's decoder starts from it with nothing else.
    if (key && paramSets && !hasSps(data)) { var both = new Uint8Array(paramSets.length + data.length); both.set(paramSets); both.set(data, paramSets.length); data = both; }
    var buf = new ArrayBuffer(14 + data.length), dv = new DataView(buf);
    dv.setUint8(0, 4); dv.setInt32(1, 0, true); dv.setUint8(5, key ? 1 : 0); dv.setInt32(6, W, true); dv.setInt32(10, H, true);
    new Uint8Array(buf, 14).set(data);
    if (host && host.readyState === 1) hostBinary(buf);
  }
  function closeEncoder() { if (encoder) { try { encoder.close(); } catch (e) { } } encoder = null; building = false; if (codec !== false) codec = null; }

  function hasSps(d) {
    for (var i = 0; i + 4 < d.length && i < 64; i++)
      if (d[i] === 0 && d[i + 1] === 0 && (d[i + 2] === 1 || (d[i + 2] === 0 && d[i + 3] === 1))) {
        var n = d[i + 2] === 1 ? i + 3 : i + 4;
        if ((d[n] & 0x1f) === 7) return true;
      }
    return false;
  }
  // avcC (ISO 14496-15) → Annex-B: every SPS and PPS behind a start code.
  function avcToAnnexB(desc) {
    var b = new Uint8Array(desc.buffer ? desc.buffer.slice(desc.byteOffset || 0, (desc.byteOffset || 0) + desc.byteLength) : desc);
    if (b.length < 7) return null;
    var out = [], o = 5, n = b[o++] & 0x1f, i, len;
    for (i = 0; i < n && o + 2 <= b.length; i++) { len = (b[o] << 8) | b[o + 1]; o += 2; out.push(0, 0, 0, 1); for (var k = 0; k < len; k++) out.push(b[o + k]); o += len; }
    n = b[o++];
    for (i = 0; i < n && o + 2 <= b.length; i++) { len = (b[o] << 8) | b[o + 1]; o += 2; out.push(0, 0, 0, 1); for (var k2 = 0; k2 < len; k2++) out.push(b[o + k2]); o += len; }
    return new Uint8Array(out);
  }

  // ---- JPEG pictures, for a browser with no H.264 encoder: one whole-screen "tile" ----
  var jpegBusy = false;
  function sendJpeg() {
    if (jpegBusy) return;
    jpegBusy = true;
    canvas.toBlob(function (blob) {
      if (!blob) { jpegBusy = false; return; }
      blob.arrayBuffer().then(function (jpg) {
        jpegBusy = false;
        var buf = new ArrayBuffer(9 + 20 + jpg.byteLength), dv = new DataView(buf);
        dv.setUint8(0, 1); dv.setInt32(1, 0, true); dv.setInt32(5, 1, true);
        dv.setInt32(9, 0, true); dv.setInt32(13, 0, true); dv.setInt32(17, W, true); dv.setInt32(21, H, true); dv.setInt32(25, jpg.byteLength, true);
        new Uint8Array(buf, 29).set(new Uint8Array(jpg));
        if (host && host.readyState === 1) hostBinary(buf);
      });
    }, 'image/jpeg', 0.6);
  }

  // For tools/websupportcheck and a person checking a browser by hand: share any stream.
  // ---------------------------------------------------------------- talking (3.3)
  // The technician presses Talk; their voice arrives here as [6][seq u32][rate u32][ch u8][fmt u8]
  // [16 kHz mono PCM16] — the host's own voice frame — and is played. The caller is asked whether
  // to use their microphone; if they say yes, the BROWSER asks as well, and its own echo canceller
  // (echoCancellation) keeps the technician's voice out of what goes back. No browser plays sound
  // before the person has clicked something on the page, so the buttons are that click too.
  var voiceBox = $('voice'), voiceText = $('voiceText'), voiceYes = $('voiceYes'), voiceNo = $('voiceNo');
  var talkers = [], micState = 'off', micDetail = '', actx = null, playAt = 0;
  var micStream = null, micNode = null, micSrc = null, micSink = null, micSeq = 0, micPos = 0, micFrame = null, micFill = 0, framesIn = 0, framesOut = 0;
  var VOICE_RATE = 16000, FRAME = 320;

  function who() { return talkers.length ? talkers.join(' and ') : (session ? session.tech : 'Your technician'); }

  function voiceTalk(m) {
    var name = m.technician || (session ? session.tech : 'Your technician');
    var i = talkers.indexOf(name);
    if (m.on && i < 0) talkers.push(name);
    if (!m.on && i >= 0) talkers.splice(i, 1);
    if (!talkers.length) { endVoice(true); return; }
    if (micState === 'off') micState = 'asking';
    ensureAudio();
    drawVoice(); reportVoice();
  }

  // A technician who vanished without saying stop is taken out of the call, as on the app's host.
  function voiceParticipants(list) {
    var before = talkers.length;
    talkers = talkers.filter(function (t) { return list.indexOf(t) >= 0; });
    if (before && !talkers.length) endVoice(true);
    else if (talkers.length !== before) { drawVoice(); reportVoice(); }
  }

  function ensureAudio() {
    try {
      if (!actx) actx = new (window.AudioContext || window.webkitAudioContext)();
      if (actx.state === 'suspended') actx.resume().then(drawVoice, function () { });
    } catch (e) { actx = null; }
  }

  function drawVoice() {
    if (!talkers.length) { voiceBox.style.display = 'none'; return; }
    var quiet = actx && actx.state === 'suspended';
    var text = '', yes = null, no = null;
    if (micState === 'asking') { text = who() + ' is talking to you. Would you like them to hear you too?'; yes = 'Use my microphone'; no = quiet ? 'Just listen' : 'Not now'; }
    else if (micState === 'on') { text = 'Your microphone is on — ' + who() + ' can hear you.'; no = 'Turn my microphone off'; }
    else if (micState === 'unavailable') { text = 'Your microphone could not be used: ' + micDetail + '.'; yes = 'Try again'; no = quiet ? 'Turn on sound' : null; }
    else { text = who() + ' is talking to you. Your microphone is off.'; yes = 'Use my microphone'; no = quiet ? 'Turn on sound' : null; }
    if (quiet) text += ' Press a button to hear them.';
    voiceText.textContent = text;
    voiceYes.textContent = yes || ''; voiceYes.style.display = yes ? '' : 'none';
    voiceNo.textContent = no || ''; voiceNo.style.display = no ? '' : 'none';
    voiceBox.style.display = 'block';
  }

  function reportVoice() {
    send(host, { t: 'voice_state', call: talkers.length > 0, technicians: talkers.join(' and '), mic: talkers.length ? micState : 'off', detail: micDetail });
  }

  voiceYes.addEventListener('click', function () { ensureAudio(); startMic(); });
  voiceNo.addEventListener('click', function () {
    ensureAudio();
    if (micState === 'on' || micState === 'asking') { stopMic(); micState = 'declined'; micDetail = ''; reportVoice(); }
    drawVoice();
  });

  async function startMic() {
    if (micStream || !actx) return;
    micDetail = '';
    try {
      if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) throw { name: 'NoApi' };
      micStream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 } });
      micSrc = actx.createMediaStreamSource(micStream);
      micFrame = new Int16Array(FRAME); micFill = 0; micPos = 0;
      if (actx.audioWorklet && window.AudioWorkletNode) {
        var code = "class P extends AudioWorkletProcessor{process(i){var c=i[0]&&i[0][0];if(c)this.port.postMessage(c.slice(0));return true}}registerProcessor('rs-mic',P);";
        var url = URL.createObjectURL(new Blob([code], { type: 'application/javascript' }));
        await actx.audioWorklet.addModule(url);
        micNode = new AudioWorkletNode(actx, 'rs-mic');
        micNode.port.onmessage = function (e) { micSamples(e.data); };
      } else {
        micNode = actx.createScriptProcessor(2048, 1, 1);
        micNode.onaudioprocess = function (e) { micSamples(e.inputBuffer.getChannelData(0)); };
      }
      // Pulled through a silent gain to the output: a node nobody listens to is not always run.
      micSink = actx.createGain(); micSink.gain.value = 0;
      micSrc.connect(micNode); micNode.connect(micSink); micSink.connect(actx.destination);
      micState = 'on';
    } catch (err) {
      stopMic();
      micState = 'unavailable';
      var n = err && err.name;
      micDetail = n === 'NotAllowedError' ? 'the browser was not allowed to use it (check the microphone icon in the address bar)'
        : n === 'NotFoundError' ? 'this device has no microphone'
        : n === 'NoApi' ? 'this browser cannot use a microphone on this page'
        : (err && err.message) || 'it would not open';
    }
    drawVoice(); reportVoice();
  }

  // Down to 16 kHz by averaging what each output sample spans (the host's resampler, in miniature),
  // 20 ms at a time, straight onto the host socket.
  function micSamples(buf) {
    if (!host || host.readyState !== 1 || micState !== 'on') return;
    var step = actx.sampleRate / VOICE_RATE;
    while (micPos < buf.length) {
      var from = Math.floor(micPos), to = Math.min(buf.length, Math.floor(micPos + step) + 1), sum = 0, n = 0;
      for (var i = from; i < to; i++) { sum += buf[i]; n++; }
      var v = n ? sum / n : 0;
      micFrame[micFill++] = Math.max(-32768, Math.min(32767, Math.round(v * 32767)));
      if (micFill === FRAME) {
        var out = new ArrayBuffer(11 + FRAME * 2), dv = new DataView(out);
        dv.setUint8(0, 6); dv.setUint32(1, micSeq++, true); dv.setUint32(5, VOICE_RATE, true); dv.setUint8(9, 1); dv.setUint8(10, 1);
        // Sample by sample, not an Int16Array over the buffer: the header is 11 bytes, and a typed
        // array cannot start at an odd offset — the first try threw on every frame and sent nothing.
        for (var k = 0; k < FRAME; k++) dv.setInt16(11 + k * 2, micFrame[k], true);
        if (host.bufferedAmount < BUFFER_LIMIT) { hostBinary(out); framesOut++; }
        micFill = 0;
      }
      micPos += step;
    }
    micPos -= buf.length;
  }

  function stopMic() {
    try { micSrc && micSrc.disconnect(); } catch (e) { }
    try { micNode && micNode.disconnect(); } catch (e) { }
    try { micSink && micSink.disconnect(); } catch (e) { }
    try { micStream && micStream.getTracks().forEach(function (t) { t.stop(); }); } catch (e) { }
    micStream = micNode = micSrc = micSink = null;
  }

  // The technician's voice. Each 20 ms frame is scheduled just after the last; a lead of 120 ms
  // absorbs the network, and a queue that has drifted past half a second is started again.
  function voiceFrame(data) {
    if (!(data instanceof ArrayBuffer) || data.byteLength <= 11) return;
    var dv = new DataView(data);
    if (dv.getUint8(0) !== 6 || !talkers.length) return;
    if (dv.getUint32(5, true) !== VOICE_RATE || dv.getUint8(9) !== 1 || dv.getUint8(10) !== 1) return;
    framesIn++;
    if (!actx || actx.state !== 'running') return;
    var pcm = new Int16Array(data.slice(11));
    var b = actx.createBuffer(1, pcm.length, VOICE_RATE), ch = b.getChannelData(0);
    for (var i = 0; i < pcm.length; i++) ch[i] = pcm[i] / 32768;
    var src = actx.createBufferSource(); src.buffer = b; src.connect(actx.destination);
    var now = actx.currentTime;
    if (playAt < now + 0.02 || playAt > now + 0.5) playAt = now + 0.12;
    src.start(playAt);
    playAt += pcm.length / VOICE_RATE;
  }

  function endVoice(report) {
    stopMic();
    // Asked by micState, not talkers: the caller has usually just emptied talkers, and a call
    // that ended must still be REPORTED as ended (websupportcheck --browser-voice caught it not).
    var was = talkers.length || micState !== 'off';
    talkers = []; micState = 'off'; micDetail = ''; playAt = 0;
    voiceBox.style.display = 'none';
    if (report && was) reportVoice();
  }

  window.rspHelp = {
    shareStream: function (s) { if (session) beginWith(s); return !!session; },
    state: function () { return { W: W, H: H, codec: codec, subscribed: subscribed, sharing: sharing, talkers: talkers.slice(), mic: micState, micDetail: micDetail, audio: actx ? actx.state : 'none', voiceIn: framesIn, voiceOut: framesOut }; }
  };

  connect();
})();
