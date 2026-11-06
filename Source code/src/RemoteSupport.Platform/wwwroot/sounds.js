// The ding and the ring in a browser (2026-10-07). Clint: a "ding ding" for a chat message, and for
// somebody waiting "a constant ringing which is not overly intrusive". The sounds themselves are the
// console's, served from Chimes at /sounds/ding.wav and /sounds/ring.wav, so the two never differ.
//
// window.rspSounds: ding(), ringing(on), muted(), setMuted(b). Muted is per browser (localStorage).
// A browser plays nothing on a page nobody has clicked yet; that is the browser's rule, not ours, and
// a play it refuses is simply skipped. A button with id="soundToggle" anywhere on the page is wired up.
(function () {
  var KEY = 'rsp.sounds';
  var EVERY = 4000;   // Chimes.RingEverySeconds
  var players = {}, lastDing = 0, ringTimer = null;

  function muted() { try { return localStorage.getItem(KEY) === 'off'; } catch (e) { return false; } }
  function play(name) {
    if (muted()) return;
    try {
      var a = players[name] || (players[name] = new Audio('/sounds/' + name + '.wav'));
      a.currentTime = 0;
      var p = a.play();
      if (p && p.catch) p.catch(function () { });
    } catch (e) { }
  }
  function stop(name) { var a = players[name]; if (a) { try { a.pause(); a.currentTime = 0; } catch (e) { } } }

  function ding() {
    var now = Date.now();
    if (now - lastDing < 1200) return;   // several at once ding once
    lastDing = now;
    play('ding');
  }
  // Call with the truth whenever the waiting changes; "on" again does not restart it.
  function ringing(on) {
    if (on === !!ringTimer) return;
    if (!on) { clearInterval(ringTimer); ringTimer = null; stop('ring'); return; }
    ringTimer = setInterval(function () { play('ring'); }, EVERY);
    play('ring');
  }
  function setMuted(b) {
    try { if (b) localStorage.setItem(KEY, 'off'); else localStorage.removeItem(KEY); } catch (e) { }
    if (b) stop('ring');
    draw();
  }

  function draw() {
    var b = document.getElementById('soundToggle'); if (!b) return;
    var m = muted();
    b.textContent = m ? '🔕 Unmute sounds' : '🔔 Mute sounds';
    b.title = m ? 'Sounds are off in this browser. Messages still show, without the ding or the ring.'
                : 'Turns off the ding for a chat message and the ring for a waiting visitor, in this browser.';
    b.hidden = false;
  }
  function wire() {
    var b = document.getElementById('soundToggle'); if (!b) return;
    b.addEventListener('click', function () { setMuted(!muted()); });
    draw();
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wire); else wire();

  window.rspSounds = { ding: ding, ringing: ringing, muted: muted, setMuted: setMuted };
})();
