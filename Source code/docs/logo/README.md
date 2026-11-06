# The Remote Support Program: logo

Concept A, "signal to the pointer", chosen by Clint on 2026-10-26. `concepts.png` shows the four drawn.

| File | Use it for |
|---|---|
| `rsp-logo.svg`, `png/rsp-logo-2400.png`, `png/rsp-logo-800.png` | the full logo on WHITE or light backgrounds |
| `rsp-logo-white.svg`, `png/rsp-logo-white-2400.png`, `png/rsp-logo-white-800.png` | the full logo on DARK backgrounds |
| `rsp-mark.svg`, `png/rsp-mark-1024/512/256/48.png` | the square mark alone: app icons, social media pictures |
| `rsp-mark-small.svg`, `png/rsp-mark-32/16.png` | 32 pixels and under (one thicker arc; two thin ones run together) |
| `rsp.ico` | Windows icon, 16 to 256 pixels |

Every PNG has a transparent background. The wording is outlines, so no file needs a font installed.

**Colours** (the website's): tile gradient `#8b5cf6` → `#5b21b6`; wording `#1b2430` with `#6d28d9`
on light, white with `#a78bfa` on dark. **Font**: Plus Jakarta Sans ExtraBold and Bold (SIL Open
Font License, `source/fonts/OFL.txt`), free to use in a logo.

**To change it**, edit `source/make-logo.js` and run it again — it writes every file above:

    npm install opentype.js@1.3.4 @resvg/resvg-js@2.6.2   (once, in any folder)
    set NODE_PATH=<that folder>\node_modules
    node docs\logo\source\make-logo.js docs\logo
