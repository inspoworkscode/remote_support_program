// Every comment box grows as it is typed into (Clint, 2026-10-13: "all the comments boxes in the ticketing
// system need to automatically expand when putting in a new line. Currently wording becomes unreadable when too
// much text is inputted" — the portal showed two lines). Every textarea in the portal, from the height its page
// gave it up to 15 lines, then it scrolls — so a long note never pushes Submit off the screen. A box whose page
// sizes it on purpose says data-autogrow="off" (the knowledge base editor).
(function () {
  var MAX_LINES = 15;
  function lineHeight(el) {
    var cs = getComputedStyle(el);
    var lh = parseFloat(cs.lineHeight);
    return isNaN(lh) ? parseFloat(cs.fontSize) * 1.35 : lh;
  }
  function fit(el) {
    if (!el || el.dataset.autogrow === 'off' || el.offsetParent === null) return;
    var cs = getComputedStyle(el);
    var pad = parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom) + parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);
    // The page's own height is the least it ever is: measured once, before this first changes it.
    if (!el.dataset.agMin) el.dataset.agMin = String(el.offsetHeight);
    var min = parseFloat(el.dataset.agMin) || 0;
    var max = Math.max(min, Math.ceil(lineHeight(el) * MAX_LINES + pad));
    el.style.height = 'auto';
    var want = el.scrollHeight + parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);
    var h = Math.min(max, Math.max(min, want));
    el.style.height = h + 'px';
    el.style.overflowY = want > max ? 'auto' : 'hidden';
  }
  window.autogrow = fit;
  document.addEventListener('input', function (e) { if (e.target && e.target.tagName === 'TEXTAREA') fit(e.target); });
  // A box filled in by the page (words kept after a refusal, a mention added) and a step's form opened later.
  function all() { document.querySelectorAll('textarea').forEach(fit); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', all); else all();
  document.addEventListener('focusin', function (e) { if (e.target && e.target.tagName === 'TEXTAREA') fit(e.target); });
  window.addEventListener('resize', function () { document.querySelectorAll('textarea').forEach(fit); });
})();
