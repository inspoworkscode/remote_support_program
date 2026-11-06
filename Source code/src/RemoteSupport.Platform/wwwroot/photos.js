// Workshop photos are made smaller BEFORE they are sent (Clint, 2026-09-29: "can the uploaded
// photos be automatically resized so they don't use up so much space?").
//
// The longest side becomes 2048 pixels, saved as a JPEG at 85% — a 4 MB phone photo comes out
// around half a megabyte, and a scratch or a crack is still plain to see. Done here, in the
// browser, rather than on the server: it makes a phone's upload quicker too, and the server is
// headed for Linux, where .NET's own image code does not run. The console does the same
// (RemoteSupport.Technician/PhotoShrink.cs), so both ways in store the same size.
//
// A photo already small enough is sent as it is. Anything this browser cannot open (HEIC on a
// browser without it) is sent as it is too — the server's 12 MB limit still applies. Orientation
// is taken from the photo, so a portrait shot stays upright.
async function shrinkPhoto(file) {
  const MAX = 2048, QUALITY = 0.85, SMALL = 1.5 * 1024 * 1024;
  try {
    if (!file.type || !file.type.startsWith('image/')) return file;
    const bmp = await createImageBitmap(file, { imageOrientation: 'from-image' });
    const scale = Math.min(1, MAX / Math.max(bmp.width, bmp.height));
    if (scale === 1 && file.size <= SMALL) { bmp.close(); return file; }
    const c = document.createElement('canvas');
    c.width = Math.max(1, Math.round(bmp.width * scale));
    c.height = Math.max(1, Math.round(bmp.height * scale));
    c.getContext('2d').drawImage(bmp, 0, 0, c.width, c.height);
    bmp.close();
    const blob = await new Promise(function (r) { c.toBlob(r, 'image/jpeg', QUALITY); });
    if (!blob || blob.size >= file.size) return file;
    return new File([blob], file.name.replace(/\.[^.]+$/, '') + '.jpg', { type: 'image/jpeg' });
  } catch (e) {
    return file;
  }
}
