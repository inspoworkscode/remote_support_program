# The investor pitch

`investor-deck.html` is the pitch deck for the **R500 000** raise: R250 000 to Anywhere IT as the
support team, R200 000 into the product, R50 000 to the developer, in return for 10% of monthly
turnover and 10% of any future sale.

It is written for a non-technical reader — a business associate, a friend, or an angel investor —
and it is deliberately honest about what is not built yet, because the two rows it loses to
TeamViewer and AnyDesk are both funded by the raise it is asking for.

It lives here rather than in `docs/` because it is not engineering documentation. It is a
deliverable, and it goes stale the moment the product moves on.

## Published

The live version is an Artifact:
<https://claude.ai/code/artifact/c62de496-f859-4acb-ae2d-b863cf7f6856>

Private until it is shared from the page's own share menu. **Republishing must target that URL**,
or a second, competing copy of the deck appears at a different link.

## Rebuilding the PDF

One command, from the repository root. It needs Edge, which every Windows machine has:

```bash
"/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --headless=new --disable-gpu --no-pdf-header-footer --virtual-time-budget=10000 --print-to-pdf="C:\Users\Clint\Desktop\Remote Support\pitch\The-Remote-Support-Program-Investment-Proposal.pdf" "file:///C:/Users/Clint/Desktop/Remote%20Support/pitch/investor-deck.html"
```

Two things worth knowing before that command is trusted:

- **It must run as a logged-in user, never as SYSTEM.** Edge refuses to start under SYSTEM and
  exits 1002 with no output at all — the same wall the monthly health check hit, which is why the
  server builds those PDFs instead of the customer's machine. See HANDOVER §17.
- **The typefaces are fetched from Google Fonts while it renders**, so the machine needs to be
  online or the PDF falls back to Georgia and Arial. It still reads correctly; it just looks less
  like itself.

The `@media print` block at the end of the stylesheet is what makes the printed copy hold together
— it forces the colour on (a PDF emailed to someone who will not click a link is the whole point of
having one) and keeps cards, table rows and timeline entries off page breaks.

## If the numbers change

Three places carry money and must move together: the three-part **ask** panel near the top, the
**Where the R500 000 goes** split, and the **market** table of what given customer numbers produce.
Every figure in that table is priced at the South African R500 rate on purpose, so the arithmetic is
conservative and checks out under a sceptical reader's pencil.
