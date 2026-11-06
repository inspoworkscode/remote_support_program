// phone.js — the portal on a phone (2026-10-02).
//
// Clint: "If a technician logs into the dashboard via their cellphone, tickets and workflows must
// work flawlessly almost as if they are working from an app."
//
// A table nine columns wide was swiped sideways on a phone, a column at a time. This labels every
// cell with its column's heading (data-label) and marks the table "cards"; site.css then draws each
// row as a card of label/value lines below 700px. Above that nothing changes. Only tables whose
// FIRST row is all headings — a list — are touched; a label/value table (th then td on each row)
// already reads well narrow, and "no-cards" opts a table out.
(function () {
  'use strict';
  function label(table) {
    if (table.classList.contains('cards') || table.classList.contains('no-cards')) return;
    var rows = table.rows;
    if (rows.length < 2) return;
    var head = rows[0].cells;
    if (!head.length || [].some.call(head, function (c) { return c.tagName !== 'TH'; })) return;
    var names = [];
    [].forEach.call(head, function (c) {
      for (var i = 0; i < (c.colSpan || 1); i++) names.push(c.textContent.trim());
    });
    for (var r = 1; r < rows.length; r++) {
      var col = 0;
      [].forEach.call(rows[r].cells, function (c) {
        if (c.tagName === 'TD' && names[col] && !c.hasAttribute('data-label')) {
          c.setAttribute('data-label', names[col]);
          // The value in one box beside its label, however many pieces the cell holds. Moving the
          // nodes keeps them as they are — their forms, their listeners, their values.
          var v = document.createElement('div');
          v.className = 'cell-v';
          while (c.firstChild) v.appendChild(c.firstChild);
          c.appendChild(v);
        }
        col += c.colSpan || 1;
      });
    }
    rows[0].classList.add('cards-head');
    table.classList.add('cards');
  }
  function all() { document.querySelectorAll('main table').forEach(label); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', all); else all();
  window.phoneTables = { label: all };
})();
