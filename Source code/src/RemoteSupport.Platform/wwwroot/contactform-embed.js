// The contact form (3.8.4, 2026-10-05): what a company pastes WHERE the form should appear on its own
// website,
//   <script src="https://<platform>/contactform/<slug>.js" async></script>
// served as /contactform/{slug}.js with the slug written in below. It puts our form
// (/contact/{slug}?embed=1) in a frame right where the line is, and makes the frame as tall as the
// form says it is, so there is never a scroll bar inside it. Everything typed happens inside the
// frame, on OUR origin — this script never sees it.
(function () {
  var SLUG = "__SLUG__";
  var script = document.currentScript;
  if (!script || !window.postMessage) return;
  var origin = new URL(script.src, location.href).origin;

  var frame = document.createElement('iframe');
  frame.title = 'Contact form';
  frame.src = origin + '/contact/' + encodeURIComponent(SLUG) + '?embed=1&from=' + encodeURIComponent(location.href.slice(0, 300));
  frame.setAttribute('loading', 'lazy');
  frame.style.cssText = 'display:block;width:100%;max-width:640px;height:640px;border:0;background:transparent;';
  script.parentNode.insertBefore(frame, script);

  window.addEventListener('message', function (e) {
    if (e.origin !== origin || e.source !== frame.contentWindow || !e.data || typeof e.data.rspContactHeight !== 'number') return;
    frame.style.height = Math.max(200, Math.min(3000, e.data.rspContactHeight + 4)) + 'px';
  });
})();
