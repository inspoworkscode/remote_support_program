// End-to-end encryption in the browser (2026-10-07; docs/NEXT-SESSION-E2E.md, HANDOVER §112) — Clint's answer 3:
// the browser routes are included. The SAME frames as Shared/SessionSeal.cs and the same handshake as
// E2eHandshake, with WebCrypto: P-256 ECDH, HKDF-SHA256, AES-256-GCM, ECDSA P-256 (r‖s). The server relays what
// this makes and can open none of it.
//
//   a message: [7][tagLen][tag][sender u32][counter u64][ciphertext ‖ tag16]   (tag = the JSON "t")
//   binary:    [kind | 0x80][sender u32][counter u64][ciphertext ‖ tag16]
//   associated data: the visible header ‖ the session id; nonce = sender ‖ counter; a counter not higher than the
//   last from that sender is refused (no replays).
//
// WebCrypto is asynchronous, so frames are opened — and sent — through a chain each, in arrival order: a counter
// that overtook another would be refused as a replay, and a picture decoded out of order is a smear.
(function () {
  'use strict';
  // THE clear list — what the server acts on. MUST match SessionSeal.ClearTypes (tools/e2ecore compares them).
  var CLEAR = ['ping', 'error', 'session_ended', 'participants', 'end_session', 'decline_session', 'consent_granted',
    'monitors', 'sysinfo', 'privacy_state', 'audio_status', 'voice_state', 'file_activity', 'recording_state', 'voice_talk',
    'sys_action', 'sys_action_result', 'voice_mic_answer', 'chat', 'script_audit', 'key_frame_request', 'e2e_ready', 'e2e_hello', 'e2e_key', 'e2e_mode', 'bridge_ask', 'bridge_end'];
  var clear = {}; CLEAR.forEach(function (t) { clear[t] = true; });

  var subtle = window.crypto && window.crypto.subtle;
  var enc = new TextEncoder(), dec = new TextDecoder();

  function b64(u8) { var s = ''; for (var i = 0; i < u8.length; i++) s += String.fromCharCode(u8[i]); return btoa(s); }
  function unb64(s) { var b = atob(s || ''), u = new Uint8Array(b.length); for (var i = 0; i < b.length; i++) u[i] = b.charCodeAt(i); return u; }
  function cat() {
    var n = 0, i; for (i = 0; i < arguments.length; i++) n += arguments[i].length;
    var out = new Uint8Array(n), o = 0;
    for (i = 0; i < arguments.length; i++) { out.set(arguments[i], o); o += arguments[i].length; }
    return out;
  }
  function isContentBinary(k) { return k >= 1 && k <= 6; }
  function isSealed(u8) { return u8.length > 0 && (u8[0] === 7 || (u8[0] & 0x80) !== 0); }

  function Seal(sessionId) {
    this.sid = enc.encode(sessionId); this.key = null; this.sender = 0; this.counter = 0; this.last = {};
    this.sealing = false; this.refuseClear = false;
    this.opened = 0; this.clearSeen = 0; this.refused = 0;
    this.onClear = null; this.onFirstOpened = null;
    this.recv = Promise.resolve(); this.out = Promise.resolve();
  }
  Seal.prototype.use = function (raw, sender) {
    var self = this;
    return subtle.importKey('raw', raw, 'AES-GCM', false, ['encrypt', 'decrypt']).then(function (k) { self.key = k; self.sender = sender >>> 0; self.counter = 0; });
  };
  Seal.prototype.noteClear = function () { if (++this.clearSeen === 1 && this.onClear) this.onClear(); };

  Seal.prototype._seal = function (header, plain) {
    // The counter is taken NOW, in calling order; the chain keeps the frames in that order on the socket.
    var n = ++this.counter, iv = new Uint8Array(12), dv = new DataView(iv.buffer);
    dv.setUint32(0, this.sender); dv.setUint32(4, Math.floor(n / 4294967296)); dv.setUint32(8, n >>> 0);
    return subtle.encrypt({ name: 'AES-GCM', iv: iv, additionalData: cat(header, this.sid), tagLength: 128 }, this.key, plain)
      .then(function (ct) { return cat(header, iv, new Uint8Array(ct)).buffer; });
  };
  Seal.prototype.sealMessage = function (msg) {
    var tag = enc.encode(msg.t);
    return this._seal(cat(new Uint8Array([7, tag.length]), tag), enc.encode(JSON.stringify(msg)));
  };
  Seal.prototype.sealBinary = function (u8) { return this._seal(new Uint8Array([u8[0] | 0x80]), u8.subarray(1)); };

  /** Sends a message: sealed while sealing (unless the server must read it), clear otherwise — always in order. */
  Seal.prototype.send = function (ws, msg) {
    var self = this, sealIt = this.sealing && this.key && !clear[msg.t];
    var make = sealIt ? function () { return self.sealMessage(msg); } : function () { return Promise.resolve(JSON.stringify(msg)); };
    this.out = this.out.then(make).then(function (data) { if (ws && ws.readyState === 1) ws.send(data); }).catch(function () { });
  };
  /** Sends a binary frame (ArrayBuffer or Uint8Array): sealed while sealing — in order with the messages. */
  Seal.prototype.sendBinary = function (ws, buf) {
    var self = this, u8 = buf instanceof Uint8Array ? buf : new Uint8Array(buf);
    var sealIt = this.sealing && this.key && isContentBinary(u8[0]);
    var make = sealIt ? function () { return self.sealBinary(u8); } : function () { return Promise.resolve(u8); };
    this.out = this.out.then(make).then(function (data) { if (ws && ws.readyState === 1) ws.send(data); }).catch(function () { });
  };

  Seal.prototype._open = function (u8) {
    var self = this, h;
    if (u8[0] === 7) { if (u8.length < 2) return Promise.resolve(null); h = 2 + u8[1]; }
    else h = 1;
    if (!this.key || u8.length < h + 12 + 16) { this.refused++; return Promise.resolve(null); }
    var tag = u8[0] === 7 ? dec.decode(u8.subarray(2, h)) : '';
    var iv = u8.slice(h, h + 12), dv = new DataView(iv.buffer);
    var sender = dv.getUint32(0), counter = dv.getUint32(4) * 4294967296 + dv.getUint32(8);
    return subtle.decrypt({ name: 'AES-GCM', iv: iv, additionalData: cat(u8.subarray(0, h), this.sid), tagLength: 128 }, this.key, u8.subarray(h + 12))
      .then(function (pt) {
        // Only after it proved genuine: a forged frame must not move the counter.
        if (self.last[sender] !== undefined && counter <= self.last[sender]) { self.refused++; return null; }
        self.last[sender] = counter;
        var plain = new Uint8Array(pt);
        if (u8[0] === 7) {
          var m; try { m = JSON.parse(dec.decode(plain)); } catch (x) { self.refused++; return null; }
          // The visible tag must be the truth, and nothing the server must see may arrive sealed.
          if (!m || m.t !== tag || clear[tag]) { self.refused++; return null; }
          if (++self.opened === 1 && self.onFirstOpened) self.onFirstOpened();
          return { msg: m };
        }
        var kind = u8[0] & 0x7f;
        if (!isContentBinary(kind)) { self.refused++; return null; }
        if (++self.opened === 1 && self.onFirstOpened) self.onFirstOpened();
        return { bin: cat(new Uint8Array([kind]), plain) };
      }, function () { self.refused++; return null; });
  };
  /** Opens a sealed frame in arrival order and hands it on: { msg } or { bin } (Uint8Array, [kind][payload]). */
  Seal.prototype.receive = function (u8, handle) {
    var self = this;
    this.recv = this.recv.then(function () { return self._open(u8); }).then(function (r) { if (r) handle(r); }).catch(function () { });
  };
  /** Clear content arrived (a host or console that cannot seal, or a server forcing it): counted for the badge. */
  Seal.prototype.isClearType = function (t) { return !!clear[t]; };

  // ---- the handshake ----------------------------------------------------------------------------------------------
  var DH = { name: 'ECDH', namedCurve: 'P-256' };
  function newEphemeral() { return subtle.generateKey(DH, true, ['deriveBits']); }
  function rawPub(k) { return subtle.exportKey('raw', k.publicKey).then(function (r) { return b64(new Uint8Array(r)); }); }
  function wrapKey(myPriv, theirPubB64, sessionId, viewerPub, hostPub) {
    return subtle.importKey('raw', unb64(theirPubB64), DH, false, []).then(function (their) {
      return subtle.deriveBits({ name: 'ECDH', public: their }, myPriv, 256);
    }).then(function (shared) {
      return subtle.importKey('raw', shared, 'HKDF', false, ['deriveBits']);
    }).then(function (ikm) {
      return subtle.deriveBits({ name: 'HKDF', hash: 'SHA-256', salt: enc.encode(sessionId), info: enc.encode('rsp-e2e wrap v1|' + viewerPub + '|' + hostPub) }, ikm, 256);
    }).then(function (bits) { return subtle.importKey('raw', bits, 'AES-GCM', false, ['encrypt', 'decrypt']); });
  }
  function signed(sessionId, viewerPub, hostPub, senderId) { return enc.encode('rsp-e2e key v1|' + sessionId + '|' + viewerPub + '|' + hostPub + '|' + senderId); }

  /** A viewer (the guest in a browser): a hello, and the host's answer checked and opened. */
  function viewer(sessionId) {
    var v = { seal: new Seal(sessionId), keyed: false, signed: false, hello: null, pub: '' }, eph = null;
    v.ready = newEphemeral().then(function (k) { eph = k; return rawPub(k); }).then(function (p) { v.pub = p; v.hello = { t: 'e2e_hello', pub: p }; return v; });
    /** True when this answer was ours and opened; the seal is then in use. */
    v.accept = function (m) {
      if (v.keyed || !eph || m['for'] !== v.pub) return Promise.resolve(false);
      var signedNow = !!(m.signature || m.identityPub);
      var check = !signedNow ? Promise.resolve(true)
        : subtle.importKey('raw', unb64(m.identityPub), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify'])
          .then(function (id) { return subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, id, unb64(m.signature), signed(sessionId, v.pub, m.hostPub, m.senderId)); })
          .catch(function () { return false; });
      return check.then(function (ok) {
        if (!ok) return false;
        return wrapKey(eph.privateKey, m.hostPub, sessionId, v.pub, m.hostPub).then(function (wk) {
          return subtle.decrypt({ name: 'AES-GCM', iv: unb64(m.nonce), additionalData: enc.encode(sessionId), tagLength: 128 }, wk, unb64(m.wrapped));
        }).then(function (key) {
          return v.seal.use(new Uint8Array(key), m.senderId).then(function () { v.seal.sealing = true; v.keyed = true; v.signed = signedNow; return true; });
        });
      }).catch(function () { return false; });
    };
    return v;
  }

  /** A host (browser support): the session key, and an answer for each viewer that says hello. No identity to sign
   *  with — the customer installed nothing — so a console shows "identity not checked". */
  function host(sessionId) {
    var h = { seal: new Seal(sessionId), answers: {}, next: 0 }, eph = null, raw = crypto.getRandomValues(new Uint8Array(32)), hostPub = '';
    h.ready = newEphemeral().then(function (k) { eph = k; return rawPub(k); }).then(function (p) { hostPub = p; return h.seal.use(raw, 0); }).then(function () { return h; });
    h.answer = function (hello) {
      if (h.answers[hello.pub]) return Promise.resolve(h.answers[hello.pub]);   // the same sender number, always
      var sender = ++h.next, iv = crypto.getRandomValues(new Uint8Array(12));
      return wrapKey(eph.privateKey, hello.pub, sessionId, hello.pub, hostPub).then(function (wk) {
        return subtle.encrypt({ name: 'AES-GCM', iv: iv, additionalData: enc.encode(sessionId), tagLength: 128 }, wk, raw);
      }).then(function (ct) {
        var m = { t: 'e2e_key', 'for': hello.pub, hostPub: hostPub, senderId: sender, nonce: b64(iv), wrapped: b64(new Uint8Array(ct)), identityPub: '', signature: '' };
        h.answers[hello.pub] = m;
        return m;
      }).catch(function () { return null; });
    };
    return h;
  }

  window.rspE2e = { available: !!subtle, viewer: viewer, host: host, isSealed: isSealed, isContentBinary: isContentBinary, clear: CLEAR.slice() };
})();
