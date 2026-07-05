# Observations

- [x] loading flight plan crashes — *actually the **History** window; the whole app
  vanished (0xC0000005). The session-picker ComboBox's item template was invoked with a
  null item during its measure pass and dereferenced it inside a layout pass. Fixed with a
  null-guard. (`2d15b4b`)
- [x] cannot move window around — the canvas's `VisualRoot` is an internal `TopLevelHost`, not
  the `Window`, so the drag guards never fired. Now moves via OS-native `BeginMoveDrag` through a
  direct `OwnerWindow` reference. (`b47acd6`, corrected in `5a46818`)
- [x] perch logo in floating UI is low fidelity — 256px icon drawn at ~18px with default
  sampling; set `BitmapInterpolationMode.HighQuality` so Skia mipmaps it. (`efabdc4`)
- [x] dense mode — full-parity port (edge strip, hover-popup, drag-to-redock across monitors,
  status dots); show/hide removed (Alt+Shift+W / tray now toggle dense). (`5a46818`)
- [ ] Settings page has not been implemented — still a Phase-3 scaffold; the last item.
