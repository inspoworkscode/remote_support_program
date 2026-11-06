// Outside help, step 4 (roadmap 3.9, 2026-10-06): a GUEST connects from a web page, with nothing to
// download. Clint's answer 3: "both options should be available" — the guest console, and this.
//
// It speaks exactly what the guest console speaks. /ws/guest with the code and who they are; the
// server answers with a token for that one session and the session's progress (waiting for the person,
// allowed, ended). Then the session socket with that token, as a VISITOR: the server lets through the
// picture, the pointer and the monitors, and takes from us only the subscribe, the mouse and the keys
// (TenantHub.VisitorMaySend / VisitorMaySee). Nothing here is trusted to enforce anything.
//
// The picture: H.264 decoded by the browser (WebCodecs) when it can, JPEG tiles when it cannot — the
// host chooses from the list we send, as it does for the console.
(function () {
  var $ = function (id) { return document.getElementById(id); };
  var form = $('gvForm'), status = $('gvStatus'), stage = $('gvStage'), screenCv = $('gvScreen'), cursorCv = $('gvCursor');
  var monSel = $('gvMonitor'), leave = $('gvLeave'), wrap = $('gvWrap');
  if (!form || !screenCv) return;
  var ctx = screenCv.getContext('2d'), cctx = cursorCv.getContext('2d');
  var wsBase = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host;
  var guest = null, view = null, token = '', session = null, opened = false;
  var monitors = [], monitor = -1, monW = 0, monH = 0;
  var canH264 = typeof window.VideoDecoder === 'function';
  var decoder = null, decoderCodec = '', needKey = true, ts = 0;
  var shapes = {}, cursor = { x: 0, y: 0, visible: false, shape: '' };
  var down = {};   // keys held, so leaving the picture lets them all go

  function say(text, bad) { status.textContent = text; status.className = 'gv-status' + (bad ? ' bad' : ''); }
  function send(ws, msg) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(msg)); }

  // ---- end-to-end encryption (2026-10-07, wwwroot/e2e.js): what is sealed is opened here and nowhere else ----
  var e2e = null, lock = null;
  /** To the session: sealed once the key is in hand (unless the server must read it), always in order. */
  function sendView(msg) { if (e2e) e2e.seal.send(view, msg); else send(view, msg); }
  function sendHello() { if (e2e && e2e.hello && view && view.readyState === 1) view.send(JSON.stringify(e2e.hello)); }
  /** What this session actually is, from what arrived — never from what anybody claimed. */
  function badge() {
    if (!lock) return;
    if (e2e && e2e.seal.clearSeen > 0) { lock.textContent = 'Not end-to-end encrypted — the support app on that computer needs updating.'; lock.className = 'gv-status bad'; }
    else if (e2e && e2e.keyed) { lock.textContent = '🔒 End-to-end encrypted — our server cannot see this session.'; lock.className = 'gv-status'; }
    else lock.textContent = '';
  }
  /** A frame opened, or one that came clear: handed on as if it had always been plain. */
  function deliver(r) {
    if (r.msg) message(r.msg);
    else binary(new DataView(r.bin.buffer), r.bin);
  }
  function digits(s) { return (s || '').replace(/\D/g, ''); }
  /** A reason from the server, ending in a full stop — some do, "Ended by …" does not. */
  function stop(s) { s = (s || '').trim(); return /[.!?]$/.test(s) ? s : s + '.'; }

  // ---- connecting: the code, and who they are ----
  // A technician's message (§155) links here with the code after a # — a browser never sends that part to a server,
  // so it is in no log, and opening the link spends nothing: it only fills the box. Then it leaves the address bar.
  (function () {
    var fromLink = digits(location.hash);
    if (fromLink.length !== 9) return;
    $('gvCode').value = fromLink.slice(0, 3) + ' ' + fromLink.slice(3, 6) + ' ' + fromLink.slice(6);
    try { history.replaceState(null, '', location.pathname + location.search); } catch (x) { }
    say('The code from your link is filled in. Type your name, then Connect.');
  })();

  form.addEventListener('submit', function (e) {
    e.preventDefault();
    var name = $('gvName').value.trim(), company = $('gvCompany').value.trim(), code = digits($('gvCode').value);
    if (name.length < 2) { say('Type your name — the person at the computer is shown it before they decide.', true); return; }
    if (code.length !== 9) { say('A code has nine digits.' + (code.length ? ' That has ' + code.length + '.' : ''), true); return; }
    $('gvGo').disabled = true;
    say('Connecting…');
    var msg = { t: 'guest_connect', code: code, name: name, company: company };
    if (guest && guest.readyState === 1) { send(guest, msg); return; }
    guest = new WebSocket(wsBase + '/ws/guest');
    guest.onopen = function () { send(guest, msg); };
    guest.onmessage = function (ev) {
      var m; try { m = JSON.parse(ev.data); } catch (x) { return; }
      if (m.t === 'error') { say(m.message, true); $('gvGo').disabled = false; }
      else if (m.t === 'guest_ticket') token = m.token;
      else if (m.t === 'session_ready') follow(m.session);
    };
    guest.onclose = function () { if (!opened) { say('The connection to the server was lost. Try again.', true); $('gvGo').disabled = false; } };
  });

  function follow(s) {
    session = s;
    if (s.ended) { finish('It has ended: ' + stop(s.endReason || 'the session is over') + ' A new code is needed to connect again.'); return; }
    // A technician's code (§155): THEY let you in, not the person at the machine.
    if (s.awaitingConsent && s.awaitingTechnician) { say('Waiting for ' + s.awaitingTechnician + ' to let you in…'); return; }
    if (s.awaitingConsent || !s.hostConnected) { say('Waiting for the person at ' + s.machineName + ' to allow you…'); return; }
    if (!opened) openView();
  }

  // ---- the session ----
  function openView() {
    opened = true;
    say('Connected to ' + session.machineName + '. Click the picture to type into it.'
      + (session.visitorFiles ? ' They also allowed copying files — to copy files, use the guest console (download it from the guest page).' : ''));
    form.hidden = true; stage.hidden = false;
    view = new WebSocket(wsBase + '/ws/session?role=viewer&id=' + encodeURIComponent(session.sessionId) + '&token=' + encodeURIComponent(token));
    view.binaryType = 'arraybuffer';
    if (!lock) { lock = document.createElement('div'); lock.className = 'gv-status'; status.parentNode.insertBefore(lock, status.nextSibling); }
    e2e = window.rspE2e && rspE2e.available ? rspE2e.viewer(session.sessionId) : null;
    if (e2e) { e2e.seal.onClear = badge; e2e.ready.then(sendHello); }
    badge();
    view.onopen = sendHello;
    view.onmessage = function (ev) {
      if (typeof ev.data !== 'string') {
        var u8 = new Uint8Array(ev.data);
        if (window.rspE2e && rspE2e.isSealed(u8)) { if (e2e) e2e.seal.receive(u8, deliver); return; }
        if (e2e && rspE2e.isContentBinary(u8[0])) e2e.seal.noteClear();
        binary(new DataView(ev.data), u8);
        return;
      }
      var m; try { m = JSON.parse(ev.data); } catch (x) { return; }
      if (m.t === 'e2e_ready') { sendHello(); return; }
      // Ours? Opened, then a fresh picture asked for: what was asked before went clear, which a sealing host refuses.
      if (m.t === 'e2e_key') { if (e2e) e2e.accept(m).then(function (ok) { if (ok) { badge(); if (monitor >= 0) subscribe(); } }); return; }
      if (e2e && !e2e.seal.isClearType(m.t)) e2e.seal.noteClear();
      message(m);
    };
    view.onclose = function () { if (opened) finish('The session has ended.'); };
  }

  function message(m) {
    switch (m.t) {
      case 'monitors': gotMonitors(m.monitors || []); break;
      case 'cursor': cursor.x = m.x; cursor.y = m.y; cursor.visible = !!m.visible && m.monitor === monitor; cursor.shape = String(m.shapeId); drawCursor(); break;
      case 'session_ended': finish(m.reason || 'The session has ended.'); break;
    }
  }

  function finish(text) {
    opened = false;
    try { if (view) view.close(); } catch (x) { }
    view = null; e2e = null; closeDecoder(); if (lock) lock.textContent = '';
    stage.hidden = true; form.hidden = false; $('gvGo').disabled = false; $('gvCode').value = '';
    say(text);
  }

  leave.addEventListener('click', function () {
    if (session) sendView({ t: 'end_session', sessionId: session.sessionId });
    finish('You left. A new code is needed to connect again.');
  });

  function gotMonitors(list) {
    monitors = list;
    monSel.innerHTML = '';
    list.forEach(function (mn, i) {
      var o = document.createElement('option'); o.value = String(mn.index);
      o.textContent = 'Screen ' + (i + 1) + (mn.primary ? ' (main)' : '') + ' — ' + mn.width + '×' + mn.height;
      monSel.appendChild(o);
    });
    monSel.parentNode.hidden = list.length < 2;
    var main = list.filter(function (mn) { return mn.primary; })[0] || list[0];
    if (main) watch(main.index);
  }
  monSel.addEventListener('change', function () { watch(+monSel.value); });

  function watch(index) {
    if (monitor >= 0 && monitor !== index) sendView({ t: 'subscribe', monitor: monitor, on: false });
    monitor = index;
    var mn = monitors.filter(function (m) { return m.index === index; })[0];
    if (mn) { monW = mn.width; monH = mn.height; size(monW, monH); monSel.value = String(index); }
    subscribe();
  }

  // A subscribe is also how a viewer with nothing to show asks for a fresh key frame.
  function subscribe() {
    needKey = true;
    sendView({ t: 'subscribe', monitor: monitor, on: true, codecs: canH264 ? ['h264', 'jpeg-tiles'] : ['jpeg-tiles'] });
  }

  function size(w, h) {
    if (screenCv.width !== w || screenCv.height !== h) { screenCv.width = cursorCv.width = w; screenCv.height = cursorCv.height = h; }
  }

  // ---- what the host sends: byte 0 says which (BinKind) ----
  function binary(dv, bytes) {
    switch (bytes[0]) {
      case 1: tiles(dv, bytes); break;
      case 4: video(dv, bytes); break;
      case 5: cursorShape(dv, bytes); break;
    }
  }

  // [1][monitor:i32][count:i32] then count × [x][y][w][h][len] + JPEG
  function tiles(dv, bytes) {
    if (dv.getInt32(1, true) !== monitor) return;
    var n = dv.getInt32(5, true), o = 9;
    for (var i = 0; i < n && o + 20 <= bytes.length; i++) {
      var x = dv.getInt32(o, true), y = dv.getInt32(o + 4, true), len = dv.getInt32(o + 16, true);
      var jpeg = bytes.slice(o + 20, o + 20 + len);
      o += 20 + len;
      (function (x, y, jpeg) {
        createImageBitmap(new Blob([jpeg], { type: 'image/jpeg' })).then(function (img) { ctx.drawImage(img, x, y); img.close && img.close(); }).catch(function () { });
      })(x, y, jpeg);
    }
  }

  // [4][monitor:i32][flags:u8][width:i32][height:i32] then Annex B H.264
  function video(dv, bytes) {
    if (dv.getInt32(1, true) !== monitor || !canH264) return;
    var key = (bytes[5] & 1) !== 0, w = dv.getInt32(6, true), h = dv.getInt32(10, true);
    var data = bytes.subarray(14);
    if (needKey && !key) return;                     // a decoder fed a predicted frame first draws a smear
    if (key) {
      var codec = codecFromSps(data);
      if (!decoder || decoder.state === 'closed' || codec !== decoderCodec) {
        closeDecoder();
        try {
          decoder = new VideoDecoder({
            output: function (frame) {
              size(frame.displayWidth, frame.displayHeight);
              ctx.drawImage(frame, 0, 0);
              frame.close();
            },
            error: function () { closeDecoder(); subscribe(); }    // ask for a key frame and start again
          });
          // No description: the stream is Annex B, which is what the host sends.
          decoder.configure({ codec: codec, optimizeForLatency: true });
          decoderCodec = codec;
        } catch (x) {
          // This browser cannot decode it after all: ask for tiles instead.
          canH264 = false; closeDecoder(); subscribe(); return;
        }
      }
      needKey = false;
    }
    if (!decoder || decoder.state !== 'configured') return;
    size(w, h);
    try { decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: (ts += 1000), data: data })); }
    catch (x) { closeDecoder(); subscribe(); }
  }

  function closeDecoder() { try { if (decoder && decoder.state !== 'closed') decoder.close(); } catch (x) { } decoder = null; decoderCodec = ''; needKey = true; }

  // The codec string from the stream's own SPS: "avc1." + profile, constraints, level.
  function codecFromSps(d) {
    for (var i = 0; i + 4 < d.length; i++) {
      if (d[i] === 0 && d[i + 1] === 0 && (d[i + 2] === 1 || (d[i + 2] === 0 && d[i + 3] === 1))) {
        var s = d[i + 2] === 1 ? i + 3 : i + 4;
        if ((d[s] & 0x1f) === 7 && s + 3 < d.length) return 'avc1.' + hex(d[s + 1]) + hex(d[s + 2]) + hex(d[s + 3]);
      }
    }
    return 'avc1.42E01F';
  }
  function hex(b) { return (b < 16 ? '0' : '') + b.toString(16).toUpperCase(); }

  // [5][shapeId:i64][w:i32][h:i32][hotX:i32][hotY:i32] then PNG — the customer's real pointer, once per shape
  function cursorShape(dv, bytes) {
    var id = dv.getBigInt64(1, true).toString(), hx = dv.getInt32(17, true), hy = dv.getInt32(21, true);
    createImageBitmap(new Blob([bytes.slice(25)], { type: 'image/png' })).then(function (img) {
      shapes[id] = { img: img, hx: hx, hy: hy }; drawCursor();
    }).catch(function () { });
  }

  function drawCursor() {
    cctx.clearRect(0, 0, cursorCv.width, cursorCv.height);
    if (!cursor.visible) return;
    var s = shapes[cursor.shape];
    if (s) cctx.drawImage(s.img, cursor.x - s.hx, cursor.y - s.hy);
    else { cctx.beginPath(); cctx.arc(cursor.x, cursor.y, 6, 0, Math.PI * 2); cctx.strokeStyle = '#e11d48'; cctx.lineWidth = 2; cctx.stroke(); }
  }

  // ---- the mouse ----
  function at(e) {
    var r = cursorCv.getBoundingClientRect();
    var x = Math.round((e.clientX - r.left) / r.width * monW), y = Math.round((e.clientY - r.top) / r.height * monH);
    return { x: Math.max(0, Math.min(monW - 1, x)), y: Math.max(0, Math.min(monH - 1, y)) };
  }
  var lastMove = 0;
  cursorCv.addEventListener('mousemove', function (e) {
    var now = Date.now(); if (now - lastMove < 30) return; lastMove = now;
    var p = at(e); sendView({ t: 'mouse', monitor: monitor, x: p.x, y: p.y, action: 'move' });
  });
  function button(b) { return b === 2 ? 1 : b === 1 ? 2 : 0; }   // browser: 0 left, 1 middle, 2 right
  cursorCv.addEventListener('mousedown', function (e) {
    e.preventDefault();
    // Where the click was FIRST, and focus without scrolling: a focus that scrolled the picture into view
    // moved it under the pointer, and the click landed 180 px lower than it was made (seen in the browser).
    var p = at(e);
    cursorCv.focus({ preventScroll: true });
    sendView({ t: 'mouse', monitor: monitor, x: p.x, y: p.y, action: 'down', button: button(e.button) });
  });
  cursorCv.addEventListener('mouseup', function (e) {
    e.preventDefault();
    var p = at(e); sendView({ t: 'mouse', monitor: monitor, x: p.x, y: p.y, action: 'up', button: button(e.button) });
  });
  cursorCv.addEventListener('contextmenu', function (e) { e.preventDefault(); });
  cursorCv.addEventListener('wheel', function (e) {
    e.preventDefault();
    var p = at(e); sendView({ t: 'mouse', monitor: monitor, x: p.x, y: p.y, action: 'wheel', delta: e.deltaY < 0 ? 120 : -120 });
  }, { passive: false });

  // ---- the keyboard: the key's place on the keyboard (e.code) as a Windows key code ----
  var VK = { Backspace: 8, Tab: 9, Enter: 13, ShiftLeft: 160, ShiftRight: 161, ControlLeft: 162, ControlRight: 163, AltLeft: 164, AltRight: 165,
    Pause: 19, CapsLock: 20, Escape: 27, Space: 32, PageUp: 33, PageDown: 34, End: 35, Home: 36, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40,
    PrintScreen: 44, Insert: 45, Delete: 46, MetaLeft: 91, MetaRight: 92, ContextMenu: 93,
    NumpadMultiply: 106, NumpadAdd: 107, NumpadSubtract: 109, NumpadDecimal: 110, NumpadDivide: 111, NumpadEnter: 13, NumLock: 144, ScrollLock: 145,
    Semicolon: 186, Equal: 187, Comma: 188, Minus: 189, Period: 190, Slash: 191, Backquote: 192, BracketLeft: 219, Backslash: 220, BracketRight: 221, Quote: 222, IntlBackslash: 226 };
  // The keys Windows calls "extended": the second copies, and the arrows and their block.
  var EXT = { ControlRight: 1, AltRight: 1, Insert: 1, Delete: 1, Home: 1, End: 1, PageUp: 1, PageDown: 1, ArrowLeft: 1, ArrowUp: 1, ArrowRight: 1, ArrowDown: 1,
    NumpadDivide: 1, NumpadEnter: 1, MetaLeft: 1, MetaRight: 1, ContextMenu: 1, PrintScreen: 1 };
  function vk(code) {
    if (/^Key[A-Z]$/.test(code)) return code.charCodeAt(3);
    if (/^Digit[0-9]$/.test(code)) return 48 + +code[5];
    if (/^Numpad[0-9]$/.test(code)) return 96 + +code[6];
    var f = /^F([0-9]{1,2})$/.exec(code); if (f && +f[1] >= 1 && +f[1] <= 24) return 111 + +f[1];
    return VK[code] || 0;
  }
  function key(e, isDown) {
    var v = vk(e.code); if (!v) return;
    e.preventDefault();
    if (isDown) down[e.code] = v; else delete down[e.code];
    sendView({ t: 'key', vk: v, down: isDown, extended: !!EXT[e.code] });
  }
  cursorCv.addEventListener('keydown', function (e) { key(e, true); });
  cursorCv.addEventListener('keyup', function (e) { key(e, false); });
  // Leaving the picture must not leave a key held down on somebody else's computer.
  cursorCv.addEventListener('blur', function () {
    Object.keys(down).forEach(function (c) { sendView({ t: 'key', vk: down[c], down: false, extended: !!EXT[c] }); });
    down = {};
  });
  window.addEventListener('beforeunload', function () { if (opened && session) sendView({ t: 'end_session', sessionId: session.sessionId }); });
})();
