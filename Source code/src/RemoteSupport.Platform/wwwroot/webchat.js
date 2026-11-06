// Website chat (3.8.3, 2026-09-30): the visitor's side, inside the bubble's frame (/webchat/{slug}/frame).
// Every rule is the server's (WebChatService); this draws what it is told. The chat it is in is kept
// in localStorage — {id, token} — so moving to another page of the company's site, which reloads this
// frame, carries on the same conversation rather than starting a new one.
(function () {
  var body = document.body;
  var slug = body.getAttribute('data-slug'), mode = body.getAttribute('data-mode'), visited = body.getAttribute('data-page') || '';
  var KEY = 'rsp.webchat.' + slug;
  var $ = function (id) { return document.getElementById(id); };
  var views = ['start', 'leave', 'sent', 'chat', 'off'];
  var ws = null, chat = null, lastId = 0, ended = false, isOpen = false, unread = 0, retry = 0;

  function show(v) {
    views.forEach(function (x) { $(x).hidden = x !== v; });
    $('compose').hidden = v !== 'chat' || ended;
    $('foot').hidden = v !== 'chat';
    var first = { start: 's-name', leave: 'l-name' }[v];
    if (first && isOpen) setTimeout(function () { $(first).focus(); }, 0);
  }
  function saved() { try { return JSON.parse(localStorage.getItem(KEY) || 'null'); } catch (e) { return null; } }
  function save(v) { try { if (v) localStorage.setItem(KEY, JSON.stringify(v)); else localStorage.removeItem(KEY); } catch (e) { } }
  function toParent(msg) { msg.rsp = 'webchat'; try { parent.postMessage(msg, '*'); } catch (e) { } }   // nothing private is ever in it

  // The bubble tells us whether we are showing.
  window.addEventListener('message', function (e) {
    if (e.source !== parent || !e.data || e.data.rsp !== 'webchat') return;
    if (typeof e.data.open === 'boolean') {
      isOpen = e.data.open;
      if (isOpen) { unread = 0; if (!$('chat').hidden && !ended) $('c-text').focus(); }
    }
  });
  $('close').addEventListener('click', function () { toParent({ close: true }); });

  // A link a technician sends — the app's download page, browser support — must be clickable, and
  // nothing else in a line may become markup: text nodes, and an <a> only for http(s) addresses.
  function fill(el, text) {
    var re = /https?:\/\/[^\s<>"']+/g, at = 0, m;
    while ((m = re.exec(text))) {
      if (m.index > at) el.appendChild(document.createTextNode(text.slice(at, m.index)));
      var a = document.createElement('a'); a.href = m[0]; a.textContent = m[0]; a.target = '_blank'; a.rel = 'noopener noreferrer';
      el.appendChild(a); at = m.index + m[0].length;
    }
    if (at < text.length) el.appendChild(document.createTextNode(text.slice(at)));
  }
  function when(utc) { return new Date(utc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
  function addLine(l) {
    if (l.id <= lastId) return;
    lastId = l.id;
    var row = document.createElement('div');
    if (l.note) {
      // "Sue Tech has taken over this chat." (a hand-over, 2026-10-15): nobody said it, so no name.
      row.className = 'line note'; row.textContent = l.text;
      $('lines').appendChild(row); $('main').scrollTop = $('main').scrollHeight;
      return;
    }
    row.className = 'line' + (l.fromVisitor ? ' mine' : '');
    var meta = document.createElement('span'); meta.className = 'meta';
    meta.textContent = (l.fromVisitor ? 'You' : l.name) + ' · ' + when(l.sentUtc);
    row.appendChild(meta); fill(row, l.text);
    $('lines').appendChild(row);
    var main = $('main'); main.scrollTop = main.scrollHeight;
    if (!l.fromVisitor && (!isOpen || document.hidden)) { unread++; toParent({ unread: unread }); }
  }
  function status(c) {
    ended = !!c.ended;
    var s = $('status');
    if (ended) {
      s.textContent = c.endedBy === 'you' ? 'You ended the chat. Thank you!' : 'The chat has ended. Thank you for chatting with us!';
      $('compose').hidden = true; $('end').hidden = true; $('again').hidden = false;
      save(null); toParent({ active: false });
    } else {
      s.textContent = c.answeredBy ? c.answeredBy + ' is chatting with you.' : 'Waiting for somebody to answer — it is usually a minute or two.';
      $('end').hidden = false; $('again').hidden = true;
    }
  }

  function connect(v) {
    var url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws/webchat?id=' + encodeURIComponent(v.id) + '&token=' + encodeURIComponent(v.token);
    ws = new WebSocket(url);
    ws.onmessage = function (e) {
      var d; try { d = JSON.parse(e.data); } catch (x) { return; }
      if (d.type === 'gone') { save(null); ws.onclose = null; begin(); return; }
      if (d.type === 'hello') { retry = 0; chat = d.chat; show('chat'); d.lines.forEach(addLine); status(d.chat); if (!ended) toParent({ active: true }); }
      else if (d.type === 'line') { addLine(d.line); if (!d.line.fromVisitor && window.rspSounds) rspSounds.ding(); }   // a reply dings (2026-10-07)
      else if (d.type === 'state') status(d.chat);
      else if (d.type === 'error') $('status').textContent = d.error;
    };
    ws.onclose = function () {
      ws = null;
      if (ended || !saved()) return;
      // A dropped connection: back in a moment, with whatever was said meanwhile.
      $('status').textContent = 'Reconnecting…';
      setTimeout(function () { var s = saved(); if (s) connect(s); }, Math.min(15000, 1000 * Math.pow(2, retry++)));
    };
  }

  function details(p) {
    return { name: $(p + '-name').value.trim(), email: $(p + '-email').value.trim(), phone: $(p + '-phone').value.trim(),
      text: $(p + '-text').value.trim(), page: visited, website: $(p + '-website').value };
  }
  function check(d) {
    if (!d.name) return 'Please tell us your name.';
    if (!d.email && !d.phone) return 'Please give us an email address or a phone number, so we can get back to you.';
    if (!d.text) return 'Please type your question.';
    return '';
  }
  function post(path, d) {
    return fetch('/api/webchat/' + encodeURIComponent(slug) + path, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(d), credentials: 'omit'
    }).then(function (r) { return r.json().catch(function () { return {}; }).then(function (b) { return { ok: r.ok, b: b }; }); });
  }

  $('start').addEventListener('submit', function (e) {
    e.preventDefault();
    var d = details('s'), problem = check(d);
    $('s-err').textContent = problem; if (problem) return;
    $('s-go').disabled = true;
    post('/start', d).then(function (res) {
      if (!res.ok) {
        // It closed while they were typing (the last person signed out, or the hours ended): keep
        // what they wrote and offer the message form instead.
        if (res.b.mode === 'message') { copyTo('l', d); $('l-why').textContent = res.b.error; $('sub').textContent = "Leave us a message and we'll get back to you."; show('leave'); return; }
        $('s-err').textContent = res.b.error || 'That did not go through. Please try again.'; return;
      }
      if (!res.b.id) { show('sent'); return; }   // the robot's answer
      var v = { id: res.b.id, token: res.b.token };
      save(v); lastId = 0; $('lines').innerHTML = ''; connect(v);
    }).catch(function () { $('s-err').textContent = 'That did not go through. Check your connection and try again.'; })
      .finally(function () { $('s-go').disabled = false; });
  });
  function copyTo(p, d) { $(p + '-name').value = d.name; $(p + '-email').value = d.email; $(p + '-phone').value = d.phone; $(p + '-text').value = d.text; }

  $('leave').addEventListener('submit', function (e) {
    e.preventDefault();
    var d = details('l'), problem = check(d);
    $('l-err').textContent = problem; if (problem) return;
    $('l-go').disabled = true;
    post('/leave', d).then(function (res) {
      if (!res.ok) { $('l-err').textContent = res.b.error || 'That did not go through. Please try again.'; return; }
      show('sent');
    }).catch(function () { $('l-err').textContent = 'That did not go through. Check your connection and try again.'; })
      .finally(function () { $('l-go').disabled = false; });
  });

  $('compose').addEventListener('submit', function (e) {
    e.preventDefault();
    var box = $('c-text'), text = box.value.trim();
    if (!text || !ws || ws.readyState !== 1) return;
    ws.send(JSON.stringify({ type: 'say', text: text }));
    box.value = ''; box.focus();
  });
  $('c-text').addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); $('compose').requestSubmit ? $('compose').requestSubmit() : $('c-go').click(); }
  });
  $('end').addEventListener('click', function () { if (ws && ws.readyState === 1) ws.send(JSON.stringify({ type: 'end' })); });
  $('again').addEventListener('click', function () { ended = false; save(null); lastId = 0; $('lines').innerHTML = ''; begin(true); });
  document.addEventListener('visibilitychange', function () { if (!document.hidden && isOpen) unread = 0; });

  // A fresh start asks the server what the bubble does NOW — somebody may have signed in or out.
  function begin(fresh) {
    if (!fresh) {
      // The header promises only what is true now: a reply in minutes while somebody is there.
      $('sub').textContent = mode === 'open' ? 'We usually reply within a minute or two.' : "Leave us a message and we'll get back to you.";
      show(mode === 'open' ? 'start' : mode === 'message' ? 'leave' : 'off');
      return;
    }
    fetch('/api/webchat/' + encodeURIComponent(slug), { credentials: 'omit' }).then(function (r) { return r.json(); })
      .then(function (s) { mode = s.mode; begin(false); }).catch(function () { begin(false); });
  }

  var s = saved();
  if (s) connect(s); else begin(false);
  toParent({ ready: true });
})();
