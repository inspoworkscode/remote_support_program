// Website chat (3.8.3, 2026-09-30): the bubble a company puts on ITS OWN website with one line,
//   <script src="https://<platform>/webchat/<slug>.js" async></script>
// served as /webchat/{slug}.js with the slug written in below. It draws a round button in the corner
// and, when it is clicked, our chat frame (/webchat/{slug}/frame) beside it. Everything the visitor
// types happens inside that frame, on OUR origin — this script never sees it, and never touches the
// host page beyond the button and the frame. If the company has chat switched off it draws nothing.
(function () {
  var SLUG = "__SLUG__";
  if (window.__rspWebChat) return;              // pasted twice: one bubble
  window.__rspWebChat = true;
  var script = document.currentScript;
  if (!script || !window.fetch || !window.postMessage) return;
  var origin = new URL(script.src, location.href).origin;
  // ?site=1 is written only by OUR website's layout: there the bubble wears our name and colour (Services/SiteChat.cs).
  var SITE = new URL(script.src, location.href).searchParams.get('site') === '1' ? 'site=1' : '';
  var KEY_OPEN = 'rsp-webchat-open', KEY_ACTIVE = 'rsp-webchat-active';
  function get(k) { try { return sessionStorage.getItem(k); } catch (e) { return null; } }
  function set(k, v) { try { if (v) sessionStorage.setItem(k, '1'); else sessionStorage.removeItem(k); } catch (e) { } }

  fetch(origin + '/api/webchat/' + encodeURIComponent(SLUG) + (SITE ? '?' + SITE : ''), { credentials: 'omit' })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (s) { if (s && s.mode !== 'off') build(s); })
    .catch(function () { });

  function build(s) {
    var accent = /^#[0-9a-fA-F]{3,8}$/.test(s.accent || '') ? s.accent : '#2E9BFF';
    var root = document.createElement('div');
    root.setAttribute('data-rsp-webchat', '');
    // Fixed, above everything, and owning its own styles, so the host site's CSS cannot reach in.
    root.style.cssText = 'position:fixed;right:20px;bottom:20px;z-index:2147483000;font:14px/1.3 system-ui,-apple-system,"Segoe UI",sans-serif;';

    var frameBox = document.createElement('div');
    var BESIDE = 'position:absolute;left:auto;top:auto;right:0;bottom:76px;width:370px;height:560px;max-height:calc(100vh - 110px);' +
      'border-radius:16px;overflow:hidden;box-shadow:0 18px 50px rgba(0,0,0,.28);background:#fff;';
    // A phone: the whole screen, as every chat on a phone does.
    var WHOLE = 'position:fixed;left:0;top:0;right:0;bottom:0;width:auto;height:auto;max-height:none;border-radius:0;overflow:hidden;background:#fff;';
    frameBox.style.cssText = 'display:none;' + BESIDE;
    var frame = null;

    var button = document.createElement('button');
    button.type = 'button';
    button.setAttribute('aria-label', 'Chat with ' + (s.company || 'us'));
    button.title = s.mode === 'open' ? 'Chat with ' + (s.company || 'us') : 'Leave ' + (s.company || 'us') + ' a message';
    button.style.cssText = 'width:60px;height:60px;border-radius:50%;border:0;cursor:pointer;background:' + accent + ';color:#fff;' +
      'box-shadow:0 8px 24px rgba(0,0,0,.25);display:flex;align-items:center;justify-content:center;padding:0;position:relative;';
    button.innerHTML = '<svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>';
    var badge = document.createElement('span');
    badge.style.cssText = 'display:none;position:absolute;top:-2px;right:-2px;min-width:20px;height:20px;border-radius:10px;background:#dc2626;color:#fff;font-size:12px;font-weight:700;line-height:20px;text-align:center;padding:0 5px;';
    button.appendChild(badge);

    root.appendChild(frameBox); root.appendChild(button);
    document.body.appendChild(root);

    function small() { return window.innerWidth < 480; }
    // Set whole each time, both ways: a window that was narrow once must not stay full-screen after it widens.
    function place() { var shown = frameBox.style.display; frameBox.style.cssText = (small() ? WHOLE : BESIDE) + 'display:' + (shown || 'none') + ';'; }
    window.addEventListener('resize', function () { if (frameBox.style.display !== 'none') { place(); button.style.display = small() ? 'none' : 'flex'; } });
    function ensureFrame() {
      if (frame) return;
      frame = document.createElement('iframe');
      frame.title = 'Chat with ' + (s.company || 'us');
      frame.src = origin + '/webchat/' + encodeURIComponent(SLUG) + '/frame?from=' + encodeURIComponent(location.href.slice(0, 400)) + (SITE ? '&' + SITE : '');
      frame.style.cssText = 'border:0;width:100%;height:100%;display:block;';
      frame.setAttribute('allow', 'clipboard-write');
      frameBox.appendChild(frame);
    }
    function tell(msg) { if (frame && frame.contentWindow) frame.contentWindow.postMessage(msg, origin); }
    function open(on) {
      if (on) { ensureFrame(); place(); }
      frameBox.style.display = on ? 'block' : 'none';
      button.style.display = on && small() ? 'none' : 'flex';
      set(KEY_OPEN, on);
      if (on) { badge.style.display = 'none'; badge.textContent = ''; }
      tell({ rsp: 'webchat', open: on });
    }
    button.addEventListener('click', function () { open(frameBox.style.display === 'none'); });

    // The frame tells us: close me; a reply arrived while I was closed; a chat is (or is no longer) going on.
    window.addEventListener('message', function (e) {
      if (e.origin !== origin || !e.data || e.data.rsp !== 'webchat') return;
      if (e.data.close) open(false);
      if (e.data.ready) tell({ rsp: 'webchat', open: frameBox.style.display !== 'none' });
      if (typeof e.data.active === 'boolean') set(KEY_ACTIVE, e.data.active);
      if (e.data.unread && frameBox.style.display === 'none') { badge.textContent = String(e.data.unread); badge.style.display = 'block'; }
    });

    // Moving to another page of the site: open again if it was open, and connect (hidden) if a chat
    // is going on, so a reply still lights the badge.
    if (get(KEY_OPEN)) open(true);
    else if (get(KEY_ACTIVE)) ensureFrame();
  }
})();
