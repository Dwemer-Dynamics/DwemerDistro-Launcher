# Bundled brand title fonts — third-party attribution

The launcher packages three typefaces into the assembly as WPF `Resource` items. They
are used **only** for mod title text (hero title, bottom game cards, and the mod-name
portion of the details headings). All other launcher text stays in the UI font.

Every face here is licensed under the **SIL Open Font License, Version 1.1** (OFL), which
permits bundling and redistribution with software. The complete upstream
license text for each face ships next to this file and is copied beside the published
binary (`Assets/Fonts/` in the release archive):

| Face | Font file | License file |
| --- | --- | --- |
| Pelagiad | `Pelagiad.ttf` | `OFL-Pelagiad.txt` |
| Rye | `Rye-Regular.ttf` | `OFL-Rye.txt` |
| Share Tech Mono | `ShareTechMono-Regular.ttf` | `OFL-ShareTechMono.txt` |

No font file has been renamed in a way that touches a Reserved Font Name, subsetted,
re-hinted, or otherwise modified — each is a byte-for-byte copy of the upstream release.
None of them is sold on its own, and each ships bundled with this software, so the OFL
conditions are satisfied.

## Pelagiad — CHIM titles

- File: `Pelagiad.ttf` (SHA-256 `b282ea043af32e15bab8cf9e01d9625c0fa5032761ffbc57d7a5f939098a7d92`)
- WPF family name: `Pelagiad`
- Source: <https://github.com/Isaskar/Pelagiad> — `Pelagiad.ttf` at commit
  `f981301916d0e5a7ea9ce39d2af4e56df09677ca`
- Copyright (c) 2015, Isak Larborn (isaskar.github.io/Pelagiad), with Reserved Font
  Name "Pelagiad.ttf".
- License: SIL Open Font License 1.1 — full text in `OFL-Pelagiad.txt`
  (upstream `LICENSE.txt`).
- Why this face: Pelagiad is the Morrowind-style display face used by OpenMW, so it
  carries the same Elder Scrolls title feel CHIM's own web UI aims for, with a license
  that permits redistribution.

## Rye — STOBE titles

- File: `Rye-Regular.ttf` (SHA-256 `b7edee5e615ae1b6b07e9d030c1309152bf3672a0e8a2a46293e273730f5adba`)
- WPF family name: `Rye`
- Source: <https://github.com/google/fonts> — `ofl/rye/Rye-Regular.ttf` at commit
  `ec626514f79f831f1ab848a82114a0ce7e2d6372`
- Copyright (c) 2011 by Sorkin Type Co (www.sorkintype.com), with Reserved Font Name
  "Rye".
- License: SIL Open Font License 1.1 — full text in `OFL-Rye.txt` (upstream
  `ofl/rye/OFL.txt`).
- Why this face: a heavy slab/woodtype display face, the closest redistributable match
  for the stamped, weathered STOBE title look.

## Share Tech Mono — DIALECTIC titles

- File: `ShareTechMono-Regular.ttf` (SHA-256 `9ceab1f87414829af259c0f537573ae03ef7dd3147c0b27a36a1a0beb6732677`)
- WPF family name: `Share Tech Mono`
- Source: <https://github.com/google/fonts> — `ofl/sharetechmono/ShareTechMono-Regular.ttf`
  at commit `ec626514f79f831f1ab848a82114a0ce7e2d6372`
- Copyright (c) 2012, Carrois Type Design, Ralph du Carrois (post@carrois.com,
  www.carrois.com), with Reserved Font Name 'Share'.
- License: SIL Open Font License 1.1 — full text in `OFL-ShareTechMono.txt` (upstream
  `ofl/sharetechmono/OFL.txt`).
- Why this face: a narrow technical monospace, matching the terminal/monofonto
  character DIALECTIC's own web UI uses, under a redistributable license.

## Previously bundled faces (removed)

`MagicCardsNormal.ttf`, `MailartRubberstamp-Regular.otf`, and `MonofontoRg.otf` were
removed. Each was copied from a server project's web UI and carried only a name-table
copyright with no embedded license grant, so none of them was clearly redistributable
inside a published launcher binary. The three OFL faces above replace them and keep the
per-mod title branding.

## Changing a face

Each face is referenced from exactly one place: the `GameCenter.Font.Chim`,
`GameCenter.Font.Stobe`, and `GameCenter.Font.Dialectic` `FontFamily` resources in
`Themes/GameCenter.xaml`. To swap one, drop the new `.ttf`/`.otf` and its license file
in this folder, delete the old pair, and repoint that one resource — the fragment after
`#` must be the font's family name as reported by
`System.Windows.Media.GlyphTypeface.Win32FamilyNames`, not the file name.
