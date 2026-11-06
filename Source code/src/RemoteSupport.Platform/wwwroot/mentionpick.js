// Typing @ in a ticket's comment box pops up a list (Clint, 2026-10-21: "putting in the @ should list everyone
// assigned to the task"): the people on the ticket first, then everybody else, narrowing as the name is typed.
// Up/Down choose, Enter or Tab or a click puts the whole name in, Esc closes it. It only saves typing — the server
// still reads who is mentioned out of the posted words, so a name typed out by hand works as it always did.
//
// The SAME rule as Shared/MentionPick.cs (the console's). Change one, change the other.
// A box opts in with data-mention='{"first":[...],"all":[...],"me":"..."}'.
(function () {
  var MAX_QUERY = 40;
  var AT = String.fromCharCode(64);

  function isWordChar(c) { return /[\p{L}\p{N}]/u.test(c); }

  // The @ being typed at the caret, and what follows it so far; null when the caret is not in one.
  function typing(text, caret) {
    if (!text || caret <= 0) return null;
    for (var i = caret - 1; i >= 0 && caret - i - 1 <= MAX_QUERY; i--) {
      var c = text[i];
      if (c === '\n' || c === '\r') return null;
      if (c !== AT) continue;
      if (i > 0 && isWordChar(text[i - 1])) return null;   // an email address, as the server reads it
      var q = text.substring(i + 1, caret);
      return q.charAt(0) === ' ' ? null : { at: i, query: q };
    }
    return null;
  }

  function fits(name, q) {
    if (!q) return true;
    var n = name.toLowerCase(), lq = q.toLowerCase();
    if (n.indexOf(lq) === 0) return true;
    return lq.indexOf(' ') < 0 && n.split(' ').some(function (w) { return w.indexOf(lq) === 0; });
  }

  function matches(q, d) {
    var first = [], seen = {};
    (d.first || []).forEach(function (n) { if (n && n !== d.me && !seen[n]) { seen[n] = 1; first.push(n); } });
    var rest = [];
    (d.all || []).forEach(function (n) { if (n && n !== d.me && !seen[n]) { seen[n] = 1; rest.push(n); } });
    return first.filter(function (n) { return fits(n, q); }).map(function (n) { return { name: n, on: true }; })
      .concat(rest.filter(function (n) { return fits(n, q); }).map(function (n) { return { name: n, on: false }; }));
  }

  // Where the caret is on screen: a copy of the box with the same text up to the caret, measured.
  function caretXY(box, pos) {
    var cs = getComputedStyle(box), m = document.createElement('div');
    ['boxSizing', 'width', 'fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'letterSpacing', 'paddingTop', 'paddingRight',
     'paddingBottom', 'paddingLeft', 'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth', 'tabSize']
      .forEach(function (p) { m.style[p] = cs[p]; });
    m.style.position = 'absolute'; m.style.visibility = 'hidden'; m.style.whiteSpace = 'pre-wrap'; m.style.wordWrap = 'break-word';
    m.style.top = '0'; m.style.left = '-9999px';
    m.textContent = box.value.substring(0, pos);
    var s = document.createElement('span'); s.textContent = '​'; m.appendChild(s);
    document.body.appendChild(m);
    var x = s.offsetLeft, y = s.offsetTop + s.offsetHeight;
    document.body.removeChild(m);
    var r = box.getBoundingClientRect();
    return { x: r.left + window.scrollX + x - box.scrollLeft, y: r.top + window.scrollY + y - box.scrollTop };
  }

  var list = null, box = null, items = [], sel = 0, tok = null, shutAt = -1;

  function close() { if (list) list.style.display = 'none'; items = []; tok = null; }

  function draw() {
    if (!list) { list = document.createElement('div'); list.className = 'mention-pick'; list.setAttribute('role', 'listbox'); document.body.appendChild(list); }
    list.innerHTML = '';
    var lastOn = null, anyOn = items.some(function (it) { return it.on; });
    items.forEach(function (it, i) {
      if (it.on !== lastOn) {
        var h = document.createElement('div'); h.className = 'mp-h';
        h.textContent = it.on ? 'On this ticket' : anyOn ? 'Everybody else' : 'Everybody';
        list.appendChild(h); lastOn = it.on;
      }
      var el = document.createElement('div');
      el.className = 'mp-i' + (i === sel ? ' on' : ''); el.setAttribute('role', 'option');
      el.textContent = it.name;
      el.addEventListener('mousedown', function (e) { e.preventDefault(); pick(i); });   // the box keeps the focus
      el.addEventListener('mousemove', function () { if (sel !== i) { sel = i; mark(); } });
      list.appendChild(el);
    });
    var p = caretXY(box, tok.at);
    list.style.display = 'block';
    var w = list.offsetWidth, maxX = window.scrollX + document.documentElement.clientWidth - w - 8;
    list.style.left = Math.max(window.scrollX + 8, Math.min(p.x, maxX)) + 'px';
    list.style.top = (p.y + 4) + 'px';
  }

  function mark() {
    var opts = list.querySelectorAll('.mp-i');
    opts.forEach(function (o, i) { o.classList.toggle('on', i === sel); });
    if (opts[sel]) opts[sel].scrollIntoView({ block: 'nearest' });
  }

  function update(el) {
    var d; try { d = JSON.parse(el.dataset.mention || '{}'); } catch (e) { return close(); }
    var t = el.selectionStart === el.selectionEnd ? typing(el.value, el.selectionStart) : null;
    if (!t || t.at === shutAt) return close();
    shutAt = -1;
    var found = matches(t.query, d);
    if (found.length === 0) return close();
    var same = box === el && tok && tok.at === t.at;
    box = el; tok = t; items = found;
    if (!same || sel >= items.length) sel = 0;
    draw();
  }

  function pick(i) {
    if (!tok || !items[i]) return;
    var v = box.value, caret = box.selectionStart, after = v.substring(caret);
    var put = AT + items[i].name + (after.charAt(0) === ' ' ? '' : ' ');
    box.value = v.substring(0, tok.at) + put + after;
    var at = tok.at + put.length + (after.charAt(0) === ' ' ? 1 : 0);
    box.setSelectionRange(at, at);
    close();
    box.focus();
    if (window.autogrow) window.autogrow(box);
    box.dispatchEvent(new Event('input', { bubbles: true }));   // anything else listening (drafts) hears it
  }

  function mine(e) { return e.target && e.target.tagName === 'TEXTAREA' && e.target.dataset.mention; }

  document.addEventListener('input', function (e) { if (mine(e)) update(e.target); });
  document.addEventListener('click', function (e) { if (mine(e)) update(e.target); else if (list && !list.contains(e.target)) close(); });
  document.addEventListener('keyup', function (e) {
    if (mine(e) && (e.key === 'ArrowLeft' || e.key === 'ArrowRight' || e.key === 'Home' || e.key === 'End')) update(e.target);
  });
  document.addEventListener('keydown', function (e) {
    if (!mine(e) || !items.length || !list || list.style.display === 'none') return;
    if (e.key === 'ArrowDown') { sel = (sel + 1) % items.length; mark(); e.preventDefault(); }
    else if (e.key === 'ArrowUp') { sel = (sel - 1 + items.length) % items.length; mark(); e.preventDefault(); }
    else if (e.key === 'Enter' || e.key === 'Tab') { pick(sel); e.preventDefault(); }
    else if (e.key === 'Escape') { shutAt = tok ? tok.at : -1; close(); e.preventDefault(); e.stopPropagation(); }
  }, true);
  document.addEventListener('focusout', function (e) { if (mine(e)) setTimeout(function () { if (document.activeElement !== box) close(); }, 150); });
  window.addEventListener('resize', close);

  // For tools/mentioncheck: the same answers the console's MentionPick gives.
  window.mentionPick = { typing: typing, matches: matches };
})();
