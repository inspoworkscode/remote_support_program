// Office chat in the portal (stage 3.8.1, 2026-09-30). Clint: "when you chat and do either the @ or
// select the person it pops up a chat window in the browser". One to one, and Everyone; a message to
// you pops its window open, and so does an Everyone message that names you with @; the rest of
// Everyone only counts as unread. The browser shows a notification when the tab is not in front.
//
// Every rule is the server's (StaffChatService). This draws what it is sent: the list and unread
// counts from /api/portal/chat, a conversation from /history, and live messages from /stream.
(function () {
  if (!window.fetch || !window.EventSource) return;
  var me = '', people = [], threads = {}, total = 0;
  var open = [];                          // conversations with a window: a name, or '' for Everyone
  var windows = {};                       // with -> { el, list, box, lastId }
  var MAX_OPEN = 3;
  var KEY = 'rsp.chat.open';

  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function label(w) { return w === '' ? 'Everyone' : w; }
  function threadOf(m) { return m.to === '' ? '' : (m.from === me ? m.to : m.from); }
  function when(utc) {
    var d = new Date(utc), now = new Date();
    var hm = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    return d.toDateString() === now.toDateString() ? hm : d.toLocaleDateString([], { day: 'numeric', month: 'short' }) + ' ' + hm;
  }
  function saveOpen() { try { sessionStorage.setItem(KEY, JSON.stringify(open)); } catch (e) { } }
  function loadOpen() { try { var v = JSON.parse(sessionStorage.getItem(KEY) || '[]'); return Array.isArray(v) ? v : []; } catch (e) { return []; } }
  // The ding and the ring (2026-10-07), from sounds.js: a message from somebody else dings; a website
  // visitor nobody has answered yet rings until somebody does.
  function ding() { if (window.rspSounds) rspSounds.ding(); }
  // An owner-only login sees website chats but is never rung for them: it may not answer (2026-10-15).
  function ring() { if (window.rspSounds) rspSounds.ringing(web.mayAnswer && waiting() > 0); }
  function post(path, body) {
    return fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Chat': '1' }, body: JSON.stringify(body), credentials: 'same-origin' });
  }

  // ---- the launcher and the list ----
  var launcher = el('button', 'chat-launcher'); launcher.type = 'button';
  launcher.setAttribute('aria-label', 'Office chat');
  var launcherText = el('span', null, 'Chat'); var badge = el('span', 'chat-badge');
  launcher.appendChild(el('span', 'chat-icon', '💬')); launcher.appendChild(launcherText); launcher.appendChild(badge);
  var panel = el('div', 'chat-panel'); panel.hidden = true;
  var search = el('input', 'chat-search'); search.placeholder = 'Type @ or a name, then Enter'; search.setAttribute('aria-label', 'Find a person');
  var listEl = el('div', 'chat-list');
  panel.appendChild(el('div', 'chat-panel-h', 'Office chat'));
  panel.appendChild(search); panel.appendChild(listEl);
  var dock = el('div', 'chat-dock');
  document.body.appendChild(dock); document.body.appendChild(panel); document.body.appendChild(launcher);

  launcher.addEventListener('click', function () {
    // Asked here, because a browser only asks when somebody has just clicked something.
    if (window.Notification && Notification.permission === 'default') { try { Notification.requestPermission(); } catch (e) { } }
    panel.hidden = !panel.hidden;
    if (!panel.hidden) { search.value = ''; drawList(); search.focus(); }
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !panel.hidden) panel.hidden = true; });
  search.addEventListener('input', drawList);
  search.addEventListener('keydown', function (e) {
    if (e.key !== 'Enter') return;
    e.preventDefault();
    var first = listEl.querySelector('.chat-row');
    if (first) first.click();
  });

  function drawBadge() {
    // A website visitor waiting for an answer outranks unread office messages: somebody outside is
    // sitting there. The count is theirs while any are waiting.
    var w = typeof waiting === 'function' ? waiting() : 0;
    var n = w > 0 ? w : total;
    badge.textContent = n > 0 ? String(n) : '';
    badge.style.display = n > 0 ? 'inline-block' : 'none';
    launcherText.textContent = w > 0 ? (w === 1 ? 'Website visitor' : 'Website visitors') : 'Chat';
    launcher.classList.toggle('has-visitor', w > 0);
    launcher.classList.toggle('has-unread', w === 0 && total > 0);
  }

  function drawList() {
    var q = search.value.trim().replace(/^@/, '').toLowerCase();
    listEl.innerHTML = '';
    // Website visitors first — somebody outside is waiting — then the office.
    var vids = Object.keys(web.chats).filter(function (k) { return !web.chats[k].ended && (q === '' || web.chats[k].name.toLowerCase().indexOf(q) >= 0); });
    if (vids.length) {
      listEl.appendChild(el('div', 'chat-section', 'Website visitors'));
      vids.forEach(function (k) {
        var c = web.chats[k];
        var row = el('button', 'chat-row visitor'); row.type = 'button';
        var top = el('div', 'chat-row-top');
        top.appendChild(el('strong', null, c.name));
        top.appendChild(el('span', 'chat-state' + (!c.answeredBy ? ' waiting' : ''), vstate(c)));
        row.appendChild(top);
        if (c.lastText) row.appendChild(el('div', 'chat-row-last', c.lastText));
        row.addEventListener('click', function () { panel.hidden = true; openVisitor(c.id, true); });
        listEl.appendChild(row);
      });
      listEl.appendChild(el('div', 'chat-section', 'Office'));
    }
    // Everyone first, then whoever was spoken to most recently, then everybody else by name.
    var names = [''].concat(people.slice().sort(function (a, b) {
      var ta = threads[a] && threads[a].last ? threads[a].last.id : 0, tb = threads[b] && threads[b].last ? threads[b].last.id : 0;
      return tb - ta || a.localeCompare(b);
    }));
    names.filter(function (n) { return q === '' || label(n).toLowerCase().indexOf(q) >= 0; }).forEach(function (n) {
      var t = threads[n] || { unread: 0, last: null };
      var row = el('button', 'chat-row'); row.type = 'button';
      var top = el('div', 'chat-row-top');
      top.appendChild(el('strong', null, label(n)));
      if (t.unread > 0) top.appendChild(el('span', 'chat-badge', String(t.unread)));
      row.appendChild(top);
      if (t.last) row.appendChild(el('div', 'chat-row-last', (t.last.from === me ? 'You: ' : (n === '' ? t.last.from + ': ' : '')) + t.last.text));
      row.addEventListener('click', function () { panel.hidden = true; openWindow(n, true); });
      listEl.appendChild(row);
    });
    if (!listEl.children.length) listEl.appendChild(el('p', 'chat-empty', 'Nobody by that name.'));
  }

  function refresh() {
    return fetch('/api/portal/chat', { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (o) {
      if (!o) return;
      me = o.me; people = o.people; total = o.totalUnread; threads = {};
      o.threads.forEach(function (t) { threads[t.with] = t; });
      drawBadge();
      if (!panel.hidden) drawList();
      Object.keys(windows).forEach(function (w) { var t = threads[w]; windows[w].el.classList.toggle('has-unread', !!(t && t.unread)); });
    });
  }

  // ---- a conversation's window ----
  function openWindow(w, focus) {
    if (windows[w]) { if (focus) windows[w].box.focus(); return windows[w]; }
    if (open.length >= MAX_OPEN) closeWindow(open[0]);
    var win = el('section', 'chat-win'); win.setAttribute('aria-label', 'Chat with ' + label(w));
    var head = el('div', 'chat-win-h');
    head.appendChild(el('strong', null, label(w)));
    var x = el('button', 'chat-x', '×'); x.type = 'button'; x.setAttribute('aria-label', 'Close');
    x.addEventListener('click', function () { closeWindow(w); });
    head.appendChild(x);
    var list = el('div', 'chat-msgs');
    var form = el('form', 'chat-form');
    var box = el('textarea', 'chat-input'); box.rows = 2;
    box.placeholder = w === '' ? 'Message everybody — @ and a name to ask someone' : 'Message ' + w;
    var sug = el('div', 'chat-sug'); sug.hidden = true;
    var send = el('button', 'btn btn-accent btn-sm', 'Send'); send.type = 'submit';
    var err = el('div', 'chat-err');
    form.appendChild(sug); form.appendChild(box); form.appendChild(send);
    win.appendChild(head); win.appendChild(list); win.appendChild(err); win.appendChild(form);
    dock.appendChild(win);
    // While its history is on its way, live messages wait in `pending` and are drawn after it. Drawn
    // at once, the message that popped the window up set lastId, and the older history arriving
    // next was all skipped as already shown — found walking it: only the newest line appeared.
    var rec = { el: win, list: list, box: box, lastId: 0, loading: true, pending: [] };
    windows[w] = rec; open.push(w); saveOpen();

    box.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' && !e.shiftKey && sug.hidden) { e.preventDefault(); form.requestSubmit ? form.requestSubmit() : form.submit(); }
    });
    // "@" and a name, in the Everyone window: the names are offered as it is typed.
    box.addEventListener('input', function () {
      var m = /(^|\s)@([^\s@]*)$/.exec(box.value.slice(0, box.selectionStart));
      if (w !== '' || !m) { sug.hidden = true; return; }
      var q = m[2].toLowerCase();
      var hits = people.filter(function (p) { return p.toLowerCase().indexOf(q) === 0; }).slice(0, 6);
      sug.innerHTML = ''; sug.hidden = hits.length === 0;
      hits.forEach(function (p) {
        var b = el('button', 'chat-sug-row', p); b.type = 'button';
        b.addEventListener('click', function () {
          var before = box.value.slice(0, box.selectionStart).replace(/@[^\s@]*$/, '@' + p + ' ');
          box.value = before + box.value.slice(box.selectionStart);
          sug.hidden = true; box.focus();
        });
        sug.appendChild(b);
      });
    });
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var text = box.value.trim(); if (!text) return;
      send.disabled = true; err.textContent = '';
      post('/api/portal/chat/send', { to: w, text: text }).then(function (r) {
        return r.json().then(function (b) { return { ok: r.ok, b: b }; });
      }).then(function (res) {
        if (!res.ok) { err.textContent = res.b.error || 'It did not send.'; return; }
        box.value = '';
        append(w, res.b);            // the stream will bring it too; append() ignores a repeat
      }).catch(function () { err.textContent = 'It did not send. Check the connection and try again.'; })
        .finally(function () { send.disabled = false; box.focus(); });
    });

    fetch('/api/portal/chat/history?with=' + encodeURIComponent(w), { credentials: 'same-origin' })
      .then(function (r) { return r.ok ? r.json() : []; })
      .catch(function () { return []; })
      .then(function (rows) {
        rec.loading = false;
        rows.concat(rec.pending).sort(function (a, b) { return a.id - b.id; }).forEach(function (m) { append(w, m); });
        rec.pending = [];
        markRead(w);
      });
    if (focus) box.focus();
    return rec;
  }

  function closeWindow(w) {
    var r = windows[w]; if (!r) return;
    r.el.remove(); delete windows[w];
    open = open.filter(function (o) { return o !== w; }); saveOpen();
  }

  function append(w, m) {
    var r = windows[w]; if (!r) return;
    if (r.loading) { r.pending.push(m); return; }
    if (m.id <= r.lastId) return;
    r.lastId = m.id;
    var mine = m.from === me;
    var row = el('div', 'chat-msg' + (mine ? ' mine' : ''));
    var meta = el('div', 'chat-meta', (mine ? 'You' : m.from) + ' · ' + when(m.sentUtc));
    var text = el('div', 'chat-text', m.text);
    row.appendChild(meta); row.appendChild(text);
    var atBottom = r.list.scrollHeight - r.list.scrollTop - r.list.clientHeight < 40;
    r.list.appendChild(row);
    if (atBottom || mine) r.list.scrollTop = r.list.scrollHeight;
  }

  function markRead(w) {
    var r = windows[w]; if (!r || !r.lastId || document.hidden) return;
    var t = threads[w]; if (t && !t.unread) return;
    post('/api/portal/chat/read', { with: w, upTo: r.lastId });
  }

  function notify(m, w) {
    if (!document.hidden && document.hasFocus()) return;
    if (!window.Notification || Notification.permission !== 'granted') return;
    try {
      var n = new Notification(w === '' ? m.from + ' (Everyone)' : m.from, { body: m.text.slice(0, 200), tag: 'rsp-chat-' + w });
      n.onclick = function () { window.focus(); openWindow(w, true); n.close(); };
    } catch (e) { }
  }

  // ---- website chat (3.8.3): visitors to the company's own website ----
  // Everybody signed in hears every one. The first to reply takes it (WebChatService); the others see
  // who is answering. A new chat pops its window open for everybody; after that, a visitor's line pops
  // it only for whoever is answering, or for everybody while nobody is.
  var web = { enabled: false, chats: {}, appLink: '', helpLink: '', mayHandOverAny: false, mayAnswer: true };
  // Hand over (2026-10-15): anybody may give a chat nobody has answered; whoever is answering may give theirs;
  // a supervisor or the owner may give anybody's. The server decides (WebChatService.HandOverAsync) — this only
  // hides a button that would be refused.
  function mayHandOver(c) { return !c.ended && (!c.answeredBy || c.answeredBy === me || web.mayHandOverAny); }
  function vkey(id) { return 'v:' + id; }
  function waiting() { return Object.keys(web.chats).filter(function (k) { var c = web.chats[k]; return !c.ended && !c.answeredBy; }).length; }
  function vstate(c) {
    if (c.ended) return 'ended';
    if (!c.answeredBy) return 'waiting';
    return (c.answeredBy === me ? 'you are answering' : c.answeredBy + ' is answering') + (c.visitorHere ? '' : ' · left the page');
  }
  function vsub(c) {
    return [c.email, c.phone].filter(Boolean).join(' · ') + (c.page ? ' · on ' + c.page.replace(/^https?:\/\//, '') : '');
  }
  function refreshWeb() {
    return fetch('/api/portal/webchats', { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (o) {
      if (!o) return;
      web.enabled = o.enabled; web.appLink = o.appLink; web.helpLink = o.helpLink; web.mayHandOverAny = !!o.mayHandOverAny; web.mayAnswer = o.mayAnswer !== false; web.chats = {};
      o.chats.forEach(function (c) { web.chats[c.id] = c; });
      drawBadge(); ring(); if (!panel.hidden) drawList();
    });
  }
  function linkify(el, text) {
    var re = /https?:\/\/[^\s<>"']+/g, at = 0, m;
    while ((m = re.exec(text))) {
      if (m.index > at) el.appendChild(document.createTextNode(text.slice(at, m.index)));
      var a = el.appendChild(document.createElement('a')); a.href = m[0]; a.textContent = m[0]; a.target = '_blank'; a.rel = 'noopener';
      at = m.index + m[0].length;
    }
    if (at < text.length) el.appendChild(document.createTextNode(text.slice(at)));
  }

  function openVisitor(id, focus) {
    var key = vkey(id), c = web.chats[id];
    if (windows[key]) { if (focus && !windows[key].box.disabled) windows[key].box.focus(); return windows[key]; }
    if (!c) return null;
    if (open.length >= MAX_OPEN) closeWindow(open[0]);
    var win = el('section', 'chat-win visitor'); win.setAttribute('aria-label', 'Website chat with ' + c.name);
    var head = el('div', 'chat-win-h');
    var title = el('strong', null, c.name);
    var st = el('span', 'chat-state');
    var x = el('button', 'chat-x', '×'); x.type = 'button'; x.setAttribute('aria-label', 'Close');
    x.addEventListener('click', function () { closeWindow(key); });
    var sub = el('div', 'chat-sub', vsub(c));
    head.appendChild(title); head.appendChild(st); head.appendChild(x); head.appendChild(sub);
    var list = el('div', 'chat-msgs');
    var tools = el('div', 'chat-tools');
    function tool(label, title, fn) { var b = el('button', null, label); b.type = 'button'; b.title = title; b.addEventListener('click', fn); tools.appendChild(b); return b; }
    // One click hands them where to go next — Clint: "guide them to the support page".
    tool('App link', 'Send them the link to download our support app', function () { say('You can download our support app here: ' + web.appLink); });
    if (web.helpLink) tool('Browser support link', 'Send them the link to let us see their screen from their browser', function () { say('We can look at your screen from your browser here: ' + web.helpLink); });
    var handBtn = tool('Hand over…', 'Give this chat to somebody else who is signed in — they are brought it, and the visitor is told', function () {
      if (!hand.hidden) { hand.hidden = true; return; }
      err.textContent = '';
      fetch('/api/portal/webchats/people', { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (o) {
        var cur = web.chats[id] || c;
        var names = o ? o.names.filter(function (n) { return n !== cur.answeredBy; }) : [];
        if (!names.length) { err.textContent = 'Nobody else is signed in right now.'; return; }
        pick.innerHTML = '';
        names.forEach(function (n) { var op = el('option', null, n === me ? n + ' (you)' : n); op.value = n; pick.appendChild(op); });
        hand.hidden = false; pick.focus();
      }).catch(function () { err.textContent = 'Could not ask who is signed in. Check the connection and try again.'; });
    });
    var endBtn = tool('End chat', 'End the chat — they are told it has ended', function () {
      if (!confirm('End the chat with ' + c.name + '?')) return;
      post('/api/portal/webchats/' + id + '/end', {}).then(function (r) { if (!r.ok) r.json().then(function (b) { err.textContent = b.error || 'It did not end.'; }); });
    });
    // Who to give it to: the people signed in now, asked when Hand over is pressed.
    var hand = el('div', 'chat-handover'); hand.hidden = true;
    var pick = el('select'); pick.setAttribute('aria-label', 'Hand this chat to');
    var give = el('button', 'btn btn-accent btn-sm', 'Hand over'); give.type = 'button';
    var keep = el('button', 'btn btn-sm', 'Cancel'); keep.type = 'button';
    hand.appendChild(el('span', null, 'To')); hand.appendChild(pick); hand.appendChild(give); hand.appendChild(keep);
    keep.addEventListener('click', function () { hand.hidden = true; });
    give.addEventListener('click', function () {
      var to = pick.value; if (!to) return;
      give.disabled = true; err.textContent = '';
      post('/api/portal/webchats/' + id + '/handover', { to: to }).then(function (r) {
        if (r.ok) { hand.hidden = true; return; }
        return r.json().then(function (b) { err.textContent = b.error || 'It was not handed over.'; });
      }).catch(function () { err.textContent = 'It was not handed over. Check the connection and try again.'; })
        .finally(function () { give.disabled = false; });
    });
    tools.appendChild(hand);
    var form = el('form', 'chat-form');
    var box = el('textarea', 'chat-input'); box.rows = 2; box.placeholder = 'Reply to ' + c.name;
    var send = el('button', 'btn btn-accent btn-sm', 'Send'); send.type = 'submit';
    var err = el('div', 'chat-err');
    form.appendChild(box); form.appendChild(send);
    win.appendChild(head); win.appendChild(list); win.appendChild(err); win.appendChild(tools); win.appendChild(form);
    // An owner-only login reads the chat; it has nothing to press (2026-10-15).
    if (!web.mayAnswer) { tools.hidden = true; form.hidden = true; }
    dock.appendChild(win);
    var rec = { el: win, list: list, box: box, lastId: 0, loading: true, pending: [], visitor: id, st: st, sub: sub, send: send, endBtn: endBtn, tools: tools, handBtn: handBtn, hand: hand };
    windows[key] = rec; open.push(key); saveOpen();
    drawVisitor(id);

    function say(text) {
      send.disabled = true; err.textContent = '';
      post('/api/portal/webchats/' + id + '/say', { text: text }).then(function (r) {
        return r.json().then(function (b) { return { ok: r.ok, b: b }; });
      }).then(function (res) {
        if (!res.ok) { err.textContent = res.b.error || 'It did not send.'; return; }
        if (text === box.value.trim()) box.value = '';
        appendVisitor(res.b);
      }).catch(function () { err.textContent = 'It did not send. Check the connection and try again.'; })
        .finally(function () { send.disabled = false; if (!box.disabled) box.focus(); });
    }
    box.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit ? form.requestSubmit() : form.submit(); }
    });
    form.addEventListener('submit', function (e) { e.preventDefault(); var t = box.value.trim(); if (t) say(t); });

    fetch('/api/portal/webchats/' + id, { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : []; }).catch(function () { return []; })
      .then(function (rows) {
        rec.loading = false;
        rows.concat(rec.pending).sort(function (a, b) { return a.id - b.id; }).forEach(appendVisitor);
        rec.pending = [];
      });
    if (focus) box.focus();
    return rec;
  }

  function drawVisitor(id) {
    var r = windows[vkey(id)], c = web.chats[id]; if (!r || !c) return;
    r.st.textContent = vstate(c);
    r.st.className = 'chat-state' + (!c.ended && !c.answeredBy ? ' waiting' : '');
    r.sub.textContent = vsub(c);
    r.box.disabled = r.send.disabled = !!c.ended;
    r.tools.hidden = !!c.ended;
    r.handBtn.hidden = !mayHandOver(c);
    if (r.handBtn.hidden) r.hand.hidden = true;
    r.box.placeholder = c.ended ? 'This chat has ended.' : 'Reply to ' + c.name;
  }

  function appendVisitor(l) {
    var r = windows[vkey(l.chatId)]; if (!r) return;
    if (r.loading) { r.pending.push(l); return; }
    if (l.id <= r.lastId) return;
    r.lastId = l.id;
    if (l.note) {
      // A hand-over's line (2026-10-15): nobody said it, so it is a note, not a message.
      r.list.appendChild(el('div', 'chat-note', when(l.sentUtc) + ' · ' + l.text));
      r.list.scrollTop = r.list.scrollHeight;
      return;
    }
    var mine = !l.fromVisitor && l.name === me;
    var row = el('div', 'chat-msg' + (mine ? ' mine' : ''));
    row.appendChild(el('div', 'chat-meta', (mine ? 'You' : l.name) + ' · ' + when(l.sentUtc)));
    var text = el('div', 'chat-text'); linkify(text, l.text); row.appendChild(text);
    var atBottom = r.list.scrollHeight - r.list.scrollTop - r.list.clientHeight < 40;
    r.list.appendChild(row);
    if (atBottom || mine) r.list.scrollTop = r.list.scrollHeight;
  }

  function notifyVisitor(title, body, id) {
    if (!document.hidden && document.hasFocus()) return;
    if (!window.Notification || Notification.permission !== 'granted') return;
    try {
      var n = new Notification(title, { body: body.slice(0, 200), tag: 'rsp-webchat-' + id });
      n.onclick = function () { window.focus(); openVisitor(id, true); n.close(); };
    } catch (e) { }
  }

  // ---- the billing queue (2026-10-05) ----
  // Clint: "when something is assigned to billing, all office administrators should be notified" —
  // a badge and a pop-up. The server decides who is told (Permissions.HearsOfBilling) and sends only
  // to them; this draws it. The pop-up stays until it is closed or opened: it is there so that it
  // is not missed. The badge beside "Billing queue" is how many are waiting for the office.
  var billPops = el('div', 'bill-pops'); document.body.appendChild(billPops);
  function drawBillingCount(n) {
    var b = document.getElementById('billingBadge'); if (!b) return;
    b.textContent = n + ' to do';
    b.style.display = n > 0 ? '' : 'none';
  }
  function billingPop(a) {
    var p = el('div', 'bill-pop'); p.setAttribute('role', 'alert');
    var h = el('div', 'bill-pop-h'); h.appendChild(el('span', null, 'In the billing queue'));
    var x = el('button', 'chat-x', '×'); x.type = 'button'; x.setAttribute('aria-label', 'Close');
    x.addEventListener('click', function () { p.remove(); });
    h.appendChild(x); p.appendChild(h);
    p.appendChild(el('div', 'bill-pop-t', '#' + a.number + ' — ' + a.subject));
    p.appendChild(el('div', 'bill-pop-s', a.stage + ' · from ' + a.by));
    var acts = el('div', 'bill-pop-a');
    var open = el('a', 'btn btn-sm btn-accent', 'Open the ticket'); open.href = '/portal/t/' + a.number;
    acts.appendChild(open); p.appendChild(acts);
    // Below the header, never over it: the card stays until it is dealt with, and the menu must stay
    // usable meanwhile. Measured now, because the header is a different height on a phone.
    var head = document.querySelector('header');
    var below = head ? head.getBoundingClientRect().bottom : 0;
    billPops.style.top = Math.max(16, Math.round(below) + 12) + 'px';
    billPops.insertBefore(p, billPops.firstChild);
    // Four at most: an afternoon's backlog arriving at once is what the badge and the queue are for.
    while (billPops.children.length > 4) billPops.removeChild(billPops.lastChild);
    if (!document.hidden && document.hasFocus()) return;
    if (!window.Notification || Notification.permission !== 'granted') return;
    try {
      var n = new Notification('In the billing queue: #' + a.number, { body: a.subject + '\n' + a.stage + ' · from ' + a.by, tag: 'rsp-billing-' + a.number });
      n.onclick = function () { window.focus(); location.href = '/portal/t/' + a.number; n.close(); };
    } catch (e) { }
  }

  // ---- Latest activity (2026-10-26, HANDOVER §164) ----
  // The server sends this person's count of what is new since they last looked, whenever something is posted on a
  // ticket they may see or they look somewhere else. The tab's badge and the dashboard's card follow it; the Latest
  // activity page hears it as 'rsp-activity' and fetches its list again — one connection for the whole portal.
  function drawActivity(d) {
    var b = document.getElementById('activityBadge');
    if (b) { b.textContent = d.label + ' new'; b.style.display = d.count > 0 ? '' : 'none'; }
    var bell = document.getElementById('headBell');
    if (bell) { bell.textContent = d.count > 0 ? String(d.count) : ''; bell.hidden = !(d.count > 0); }
    document.querySelectorAll('[data-activity-count]').forEach(function (c) {
      c.textContent = d.count > 0 ? d.label + ' new' : 'nothing new';
      c.classList.toggle('pill-warn', d.count > 0); c.classList.toggle('pill-muted', !(d.count > 0));
    });
    document.dispatchEvent(new CustomEvent('rsp-activity', { detail: d }));
  }

  // ---- live ----
  function listen() {
    var es = new EventSource('/api/portal/chat/stream');
    es.addEventListener('activity', function (e) {
      var d; try { d = JSON.parse(e.data); } catch (x) { return; }
      drawActivity(d);
    });
    es.addEventListener('billing', function (e) {
      var d; try { d = JSON.parse(e.data); } catch (x) { return; }
      drawBillingCount(d.count || 0);
      if (d.arrived) billingPop(d.arrived);
    });
    es.addEventListener('message', function (e) {
      var d; try { d = JSON.parse(e.data); } catch (x) { return; }
      var m = d.message, w = threadOf(m);
      var toMe = m.from !== me && (m.to === me || d.mentionsMe);
      if (toMe && !windows[w]) openWindow(w, false);   // it pops up; the keyboard stays where it was
      append(w, m);
      if (m.from !== me && (toMe || windows[w])) notify(m, w);
      if (m.from !== me) ding();
      refresh().then(function () { if (windows[w]) markRead(w); });
    });
    es.addEventListener('read', function () { refresh(); });
    es.addEventListener('webchat', function (e) {
      var c; try { c = JSON.parse(e.data); } catch (x) { return; }
      var isNew = !web.chats[c.id];
      if (c.ended && isNew) return;
      web.chats[c.id] = c;
      if (isNew && !c.answeredBy && web.mayAnswer) {
        // Somebody on the website is waiting: it pops up for everybody, without taking the keyboard.
        openVisitor(c.id, false);
        notifyVisitor(c.name + ' on your website', c.lastText || 'wants to chat', c.id);
      }
      // Handed to me by somebody else (2026-10-15): it is mine now — it pops up, dings, and from here on the
      // visitor's lines ding for me (they key on answeredBy) and for the person who had it no longer.
      if (!c.ended && c.handedOverBy && c.handedOverBy !== me && c.answeredBy === me) {
        openVisitor(c.id, false);
        ding();
        notifyVisitor(c.name + ' (website)', c.handedOverBy + ' handed this chat to you.', c.id);
      }
      drawVisitor(c.id);
      if (c.ended) delete web.chats[c.id];   // its window keeps the transcript until it is closed
      drawBadge(); ring(); if (!panel.hidden) drawList();
    });
    es.addEventListener('webline', function (e) {
      var l; try { l = JSON.parse(e.data); } catch (x) { return; }
      var c = web.chats[l.chatId];
      if (c) { c.lastText = l.text; }
      // A visitor's line pops the window for whoever is answering it, or for everybody while nobody is.
      if (l.fromVisitor && c && !windows[vkey(l.chatId)] && (!c.answeredBy || c.answeredBy === me) && web.mayAnswer) openVisitor(l.chatId, false);
      appendVisitor(l);
      if (l.fromVisitor && c && (!c.answeredBy || c.answeredBy === me) && web.mayAnswer) notifyVisitor(c.name + ' (website)', l.text, l.chatId);
      // The visitor I am answering dings. One nobody has answered is ringing already.
      if (l.fromVisitor && c && c.answeredBy === me) ding();
      if (!panel.hidden) drawList();
    });
  }

  document.addEventListener('visibilitychange', function () { if (!document.hidden) Object.keys(windows).forEach(markRead); });

  Promise.all([refresh(), refreshWeb().catch(function () { })]).then(function () {
    loadOpen().forEach(function (w) {
      if (w.indexOf('v:') === 0) { var id = +w.slice(2); if (web.chats[id]) openVisitor(id, false); }
      else if (w === '' || people.indexOf(w) >= 0) openWindow(w, false);
    });
    listen();
  });
})();
