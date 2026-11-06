// Files and photos on a comment (Clint, 2026-10-13: "an option to upload files or photos on all comments"), and the
// damage report on its step. Each file goes up on its own the moment it is picked — a photo made smaller first
// (photos.js), as workshop photos are — and comes back as an id the form then carries in a hidden "attach" box. The
// server holds it loose on the ticket until the comment is posted; the × takes it off again. Every rule is the
// server's (TicketAttachmentService); this only sends and shows.
//
// Markup (the _Attach partial): <div class="att" data-ticket="(the ticket's number)"> <input type="file" multiple> <div class="att-list">
// with any already picked, as <span class="att-chip" data-id="…">…<button>×</button><input type=hidden name=attach></span>.
(function () {
  function chip(box, f) {
    var list = box.querySelector('.att-list');
    var c = document.createElement('span');
    c.className = 'att-chip';
    c.dataset.id = f.id;
    c.appendChild(document.createTextNode((f.image ? '🖼 ' : '📎 ') + f.name + ' (' + f.size + ') '));
    var x = document.createElement('button');
    x.type = 'button'; x.className = 'att-x'; x.title = 'Take it off'; x.textContent = '×';
    c.appendChild(x);
    var h = document.createElement('input');
    h.type = 'hidden'; h.name = 'attach'; h.value = f.id;
    c.appendChild(h);
    list.appendChild(c);
  }

  async function pick(input) {
    var box = input.closest('.att');
    if (!box || !input.files.length) return;
    var note = box.querySelector('.att-note');
    var files = Array.from(input.files), problems = [];
    for (var i = 0; i < files.length; i++) {
      note.textContent = 'Uploading ' + (i + 1) + ' of ' + files.length + '…';
      var form = new FormData();
      form.append('ticketNumber', box.dataset.ticket);
      var f = files[i];
      // A photo is made smaller where it is picked; anything else goes as it is.
      if (window.shrinkPhoto && f.type && f.type.indexOf('image/') === 0) f = await shrinkPhoto(f);
      form.append('files', f, f.name);
      try {
        var res = await fetch('/api/portal/attachments', { method: 'POST', body: form, headers: { 'X-Attach': '1' } });
        var body = await res.json().catch(function () { return {}; });
        if (!res.ok) problems.push(files[i].name + ': ' + (body.error || 'did not upload'));
        (body.files || []).forEach(function (r) { if (r.error) problems.push(r.error); else chip(box, r); });
      } catch (e) { problems.push(files[i].name + ': did not upload'); }
    }
    input.value = '';
    note.textContent = problems.join(' ');
    note.classList.toggle('att-bad', problems.length > 0);
  }

  document.addEventListener('change', function (e) {
    if (e.target && e.target.matches('.att input[type=file]')) pick(e.target);
  });
  document.addEventListener('click', async function (e) {
    var x = e.target && e.target.closest ? e.target.closest('.att-x') : null;
    if (!x) return;
    var c = x.closest('.att-chip');
    try { await fetch('/api/portal/attachments/' + c.dataset.id + '/remove', { method: 'POST', headers: { 'X-Attach': '1' } }); } catch (err) { }
    c.remove();
  });
  // Nothing is sent while a file is still going up: Post waits, rather than posting without it.
  document.addEventListener('submit', function (e) {
    var busy = Array.from(e.target.querySelectorAll('.att-note')).some(function (n) { return /^Uploading/.test(n.textContent); });
    if (busy) { e.preventDefault(); alert('A file is still uploading — wait a moment, then press it again.'); }
  }, true);
})();
