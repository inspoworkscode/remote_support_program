# Teams and Zoom background

`Clint - Teams and Zoom background.png`, 1920 x 1080, white: Anywhere I.T top left and HotKup
top right, the same size, each with its words and its web address centred beneath.
`... - logos only.png` is the same without the words. (ESET and The Remote Support Program were on it too, until Clint found
four busy; their cards are still in the page for `Render-Card.ps1`.) The centre is left clear for
your head, and the bottom third for your shoulders and the meeting's controls.

**A card on its own, for ads**: `source/Render-Card.ps1 -Card hotkup` (or `ait`, `eset`, `rsp`)
makes `<name> card.png` (transparent, with its shadow) and `<name> card - on white.png`.

- **Teams**: in a meeting, More (…) → Video effects → Backgrounds → Add new → pick the PNG.
- **Zoom**: Settings → Backgrounds & effects → Virtual backgrounds → + → Add image → pick the PNG.

Zoom mirrors your OWN preview, so the words look backwards to you; everybody else sees them the right way.

**To change it**, edit `source/backgrounds.html`, then:

    powershell -ExecutionPolicy Bypass -File docs\backgrounds\source\Render-Backgrounds.ps1

`source/anywhere-it-logo-for-white.png` is the Anywhere I.T logo with its black box taken out and
its grey wording darkened, so it reads on white. `source/eset-badge-for-white.png` is ESET's
partner badge with only its off-white paper made pure white — the badge itself is untouched, as
ESET's rules require. `source/hotkup-logo-for-white.png` is HotKup's logo trimmed to its edges,
its light grey paper made white. The originals Clint supplied are in `from-clint/`.
