// clientpick.js — one client picker for every "which client" in the portal (2026-10-02).
//
// Clint: "when linking a client, it brings a dropdown of all clients and one has to scroll through
// the list to find the client. there should be a search bar that works the same as client search so
// that when typing the client it automatically narrows the search as you type."
//
// It asks /api/portal/clients — ClientService.SearchAsync, the Clients page's own matching (name,
// contact, phone or email, any case) — so a client is found here exactly as it is found there.
//
//   <div class="cpick" data-mode="id|owner|name">
//     <input type="hidden" name="…" value="…">                 what the form sends
//     <input class="cpick-in" value="…current label…">          what the person types in
//     <div class="cpick-list" hidden></div>
//   </div>
//
//   id     the client's id ("0" for none)              — Dashboard: link a machine
//   owner  "c:{id}" the company, "p:{id}" a person, "0" — Devices: whose machine is this
//   name   the client's name ("" unlinks)               — a ticket's Details
//
// Nothing typed is the whole list, alphabetically, so a stray machine can still be read down.
(function () {
  'use strict';
  var TAKE = 50;

  function esc(s) {
    return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; });
  }

  function init(box) {
    if (box.dataset.ready) return;
    box.dataset.ready = '1';
    var mode = box.dataset.mode || 'id';
    var hidden = box.querySelector('input[type=hidden]');
    var input = box.querySelector('.cpick-in');
    var list = box.querySelector('.cpick-list');
    var form = box.closest('form');
    var chosen = input.value;          // the label of what the hidden value says
    var rows = [], active = -1, timer = 0, seq = 0;

    function close() { list.hidden = true; active = -1; input.setAttribute('aria-expanded', 'false'); }

    function render(data) {
      rows = [];
      data.forEach(function (c) {
        if (mode === 'owner') {
          rows.push({ value: 'c:' + c.id, label: c.name, text: c.name + ' (the company)', sub: false });
          (c.people || []).forEach(function (p) {
            rows.push({ value: 'p:' + p.id, label: p.name + ' — ' + c.name, text: p.name + (p.role ? ' — ' + p.role : ''), sub: true });
          });
        } else {
          rows.push({ value: mode === 'name' ? c.name : String(c.id), label: c.name, text: c.display || c.name, sub: false });
        }
      });
      var html = rows.map(function (r, i) {
        return '<button type="button" role="option" class="cpick-row' + (r.sub ? ' sub' : '') + '" data-i="' + i + '">' + esc(r.text) + '</button>';
      }).join('');
      if (!rows.length) html = '<div class="cpick-note">No client matches. Add them on the Clients page first.</div>';
      else if (data.length >= TAKE) html += '<div class="cpick-note">Keep typing to narrow it down.</div>';
      list.innerHTML = html;
      list.hidden = false;
      active = -1;
      input.setAttribute('aria-expanded', 'true');
    }

    function term() { return input.value === chosen ? '' : input.value.trim(); }

    async function ask() {
      var my = ++seq;
      var url = '/api/portal/clients?take=' + TAKE + (mode === 'owner' ? '&people=true' : '') + '&term=' + encodeURIComponent(term());
      try {
        var res = await fetch(url, { headers: { Accept: 'application/json' } });
        if (my !== seq || !res.ok) return;          // an older answer never overwrites a newer one
        var data = await res.json();
        if (my === seq && document.activeElement === input) render(data);
      } catch (e) { /* the box still submits what was typed */ }
    }

    // The hidden value says 'change' when it does (2026-10-06), so a page can follow the choice —
    // the knowledge base editor offers the chosen client's folders.
    function set(value) {
      if (hidden.value === value) return;
      hidden.value = value;
      hidden.dispatchEvent(new Event('change', { bubbles: true }));
    }

    function pick(i) {
      var r = rows[i];
      if (!r) return;
      input.value = chosen = r.label;
      set(r.value);
      input.setCustomValidity('');
      close();
    }

    function highlight(i) {
      var buttons = list.querySelectorAll('.cpick-row');
      if (!buttons.length) return;
      active = (i + buttons.length) % buttons.length;
      buttons.forEach(function (b, n) { b.classList.toggle('on', n === active); });
      buttons[active].scrollIntoView({ block: 'nearest' });
    }

    input.setAttribute('autocomplete', 'off');
    input.setAttribute('role', 'combobox');
    input.setAttribute('aria-expanded', 'false');
    input.addEventListener('focus', function () { input.select(); ask(); });
    input.addEventListener('input', function () {
      input.setCustomValidity('');
      // Emptied: nobody, as Save would say. What was chosen is forgotten with it, so typing the
      // same name back is a new choice rather than a silent return to a value no longer held.
      if (input.value.trim() === '') { chosen = ''; set(mode === 'name' ? '' : '0'); }
      clearTimeout(timer);
      timer = setTimeout(ask, 150);
    });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown') { e.preventDefault(); if (list.hidden) ask(); else highlight(active + 1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); highlight(active - 1); }
      else if (e.key === 'Escape') { close(); }
      else if (e.key === 'Enter' && !list.hidden) {
        // The highlighted row; or, when the typing has narrowed it to one, that one.
        var only = list.querySelectorAll('.cpick-row').length === 1 ? 0 : -1;
        if (active >= 0 || only === 0) { e.preventDefault(); pick(active >= 0 ? active : 0); }
      }
    });
    // Pressed rather than clicked: a click arrives after the box has lost focus and closed.
    list.addEventListener('mousedown', function (e) { e.preventDefault(); });
    list.addEventListener('click', function (e) {
      var b = e.target.closest('.cpick-row');
      if (b) pick(+b.dataset.i);
    });
    input.addEventListener('blur', function () { setTimeout(close, 120); });

    if (form) form.addEventListener('submit', function (e) {
      var typed = input.value.trim();
      if (typed === '') { hidden.value = mode === 'name' ? '' : '0'; return; }   // empty: nobody / unlink
      if (input.value === chosen) return;
      if (mode === 'name') { hidden.value = typed; return; }                      // the server finds it by name
      e.preventDefault();
      input.setCustomValidity('Pick one from the list.');
      input.reportValidity();
      input.focus();
    });
  }

  function all() { document.querySelectorAll('.cpick').forEach(init); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', all); else all();
  window.clientPick = { init: all };
})();
