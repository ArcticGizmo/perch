# Quick links vanish when one link's icon lookup stalls: manual testing

## The bug

The quick-links strip showed only the note button, even though `settings.json` still held every link. The strip waited for every link's icon before it showed any of them. A link whose `ExePath` is a non-executable file (here a `.png`, whose icon comes from the packaged Photos app) made the shell icon call hang on a thread-pool (MTA) thread. That one stall hid every link, and because it held the provider's lock, every later icon request (a Settings edit, removing the link) blocked behind it until Perch restarted.

## The fix

- `App.ReloadQuickLinks` puts the links on the strip at once (initials), then swaps the icons in when they resolve. Each link's lookup is isolated, and a generation counter stops an older, slower pass from overwriting a newer edit.
- `WindowsAppIconProvider` renders each icon on its own STA thread with a 10s deadline. A render that misses the deadline is abandoned and the link keeps its initials.

## Checks

1. **Before (v0.6.20):** add a quick link whose path is an image file (Settings → Quick Links → Add → browse to a `.png`). Restart Perch. Expect: the strip shows only the note button.
2. **After:** same setup with the fixed build. Expect: every link appears straight away. The presets get their real icons within a second or two. The image link shows either an icon or its initials, and never hides the others.
3. **After:** with Perch running, disable and then re-enable the image link in Settings. Expect: the strip updates immediately each time and never goes blank.
4. **After:** click the image link. Expect: it opens in its default viewer, as before.
5. **Regression:** the session-row IDE/Claude Desktop origin icons (same provider) still render.
