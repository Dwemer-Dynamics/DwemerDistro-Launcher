# Bundled brand title fonts

These faces are packaged into the launcher assembly as WPF `Resource` items and are
used **only** for mod title text (hero title, bottom game cards, and the mod-name
portion of the details headings). All other launcher text stays in the UI font.

Each file is the same font this monorepo already redistributes in that product's own
web UI, copied so the launcher has no dependency on a system-installed font and makes
no runtime download.

## Magic Cards — CHIM

- File: `MagicCardsNormal.ttf`
- WPF family name: `Magic Cards`
- Copied from: `HerikaServer/ui/css/font/MagicCardsNormal.ttf`
- Declared as the CHIM title face by `HerikaServer/ui/css/chim-theme.css`
  (`@font-face { font-family: "MagicCards"; }`) and `hub-navigation.css`.
- Name-table copyright: `(c)1998 Neale Davidson`. The file carries no embedded
  license string. It is the Morrowind-style display face CHIM's UI already ships.
- Alternative if a formally licensed replacement is required: **Pelagiad**
  (SIL Open Font License 1.1, https://github.com/Isaskar/Pelagiad), a Magic
  Cards-inspired face. Dropping `Pelagiad.ttf` plus its `OFL.txt` in this folder and
  repointing `GameCenter.Font.Chim` in `Themes/GameCenter.xaml` is the only change
  needed; no other code refers to the family name.

## Mailart Rubberstamp — STOBE

- File: `MailartRubberstamp-Regular.otf`
- WPF family name: `Mailart Rubberstamp`
- Copied from: `StobeServer/ui/css/font/MailartRubberstamp-Regular.otf`
- Declared as the STOBE title face by `StobeServer/ui/css/main.css`
  (`--stobe-title-font: 'MailartRubberstamp', ...`).
- Name-table copyright: `© 2004, 2013 K-Type (www.k-type.com)`, designed by
  Keith Bates. No embedded license string.

## Monofonto — DIALECTIC

- File: `MonofontoRg.otf`
- WPF family name: `Monofonto`
- Copied from: `DialecticServer/ui/css/font/MonofontoRg.otf`
- Declared as the DIALECTIC title face by `DialecticServer/ui/css/main.css`
  (`@font-face { font-family: 'Monofonto'; }`).
- Name-table copyright: `Copyright (c) 1999-2022 Typodermic Fonts Inc.`, designed by
  Ray Larabie (https://typodermicfonts.com). No embedded license string.

None of the three files embed an OFL/licence block, so redistribution here rests on
the same terms under which each server project already ships the identical file.
