# Feeds (Atom, then RSS)

**Status:** **F0 built** on branch `feeds` (uncommitted). It adds `src/Perch.Core/Feeds/` (`FeedModels`,
`FeedParser`, `FeedText`, `FeedUrl`, `HtmlTokenizer`, `HtmlToMarkdown`) and the tests `FeedInjectionTests` (the
hostile corpus), `AtomParserTests` and `HtmlToMarkdownTests`. The full suite passes and the solution builds clean.

The corpus was mutation-checked: disabling text escaping fails 19 tests, and disabling the URL gate fails 16.

**F1 built** (uncommitted on `feeds`). It adds:

- **`FeedFetcher`**: the fenced and open pipelines, manual redirects, caps and our own error wording.
- **`FeedIcon`**: raster-only icon validation, including ICO and its embedded PNGs.
- **`FeedStore`**: cache, read state and icons, re-cleaned on load, with an id allowlist.
- **`FeedReadState`**, **`FeedSchedule`** and **`FeedStoryQueue`** (`StoryPlan`).
- **`FeedsService`**: the UI-free engine F3's monitor host will drive.

Tests are `FeedFetcherTests`, `FeedStateTests`, `FeedStoryQueueTests` and `FeedsServiceTests`, plus the shared
`FeedTestSupport`. All 209 feed tests pass, the full suite passes and the solution builds clean.

Pulled forward from F3: `FeedSchedule` and `FeedsService` live in Core, so scheduling, priming, ordering and
failure handling are tested end to end without the UI. F3's `FeedsMonitorHost` shrinks to a `DispatcherTimer`
that calls `TickAsync` and pushes `Snapshot()` to the canvas.

**F2 built** (on `feeds`). It adds:

- **`AppSettings`**: `Feeds`, `ShowFeeds` (off), `FeedsIntervalMinutes` (30) and `NotifyOnFeedEntry` (off).
- **Registry descriptors**:
  - `feed-subscriptions`, a List entry whose "Manage feeds →" card links to the page. It's used instead of a
    `NotSettings` exemption, so search finds it.
  - `feeds` and `feeds-interval` under Integrations.
  - `feeds-notify` under Notifications.
  - A new `PreviewTarget.Feeds`.
- **The Settings Feeds page** (`SettingsWindow.Feeds.cs`; `SettingsWindow` is now `partial`). Rows carry
  enable, ↑/↓, Edit and Remove. The URL is trimmed from the left, and a live status line shows "N new · Latest: …
  · 2h ago", the error, "Not checked yet" or "Paused", with a not-secure marker for http. Below the list are a
  5–240 min interval stepper in 5-minute steps and a notify toggle.
- **`FeedDialog`**: checks through the fenced fetcher off the UI thread, and previews the title, icon, entry
  count and latest entry. It warns on http, refuses duplicates and gives hints for web pages, RSS, 404 and 401/403.
  A failed check makes the button "Save anyway".
- **`FeedAddress`** (Core, tested): input normalization (no scheme means https; `feed:` is mapped), duplicate
  detection and failure hints.

`FeedsMonitorHost` (App) was pulled forward from F3. It's a 5-second `DispatcherTimer` over
`FeedsService.TickAsync`, skipped while locked, with debounced off-thread read saves. It's applied idempotently
from `ApplyDisplaySettings`, so with Feeds on the engine already polls and the Settings page shows live status.
There's no overlay row yet; that's F3.

The headless render now has `feed_dialog_{ok,webpage,http,duplicate}_1x.png`, all eyeballed. **The Settings
page itself isn't render-verified** (the renderer has no Settings-window harness); check it in the running app.

**Deviations from the draft**

- **Read-marker pruning** is retention-based (30 days after an entry leaves the feed, hard cap 2,000). It no
  longer prunes to the ids currently in the feed. A feed that briefly serves an empty document would otherwise
  wipe its markers and re-light everything.
- **A private subscription's icon** may be fetched only from that subscription's own host; redirects off it are
  refused. Any other icon is fenced.
- **ICO icons** are validated by `FeedIcon` (`ImageHeader` doesn't read ICO). Every entry is size-checked,
  including PNGs embedded in the ICO.

**Not tested here**

- The **gzip-bomb** case has no separate test. The cap counts bytes read from the handler's already-decompressed
  stream, so it applies by construction; stub handlers can't decompress.
- The **SSRF fence** is exercised for real: the actual `SocketsHttpHandler` connect hook runs with a stub
  resolver that returns private addresses, and refuses before any socket opens. It was **not** mutation-checked,
  because disabling it would make the test open a real connection.
- **Behind a system proxy** the connect hook sees the proxy, not the target. The fence then only checks the
  target's local DNS answer, which is best effort and documented in `FeedFetcher`.

Decisions settled 2026-10-07 (see §7):

- Adding a feed marks its existing entries read.
- The viewer is a **story player**, not a three-pane reader.
- The parser is **hand-rolled**, with injection defences as a first-class concern (§3.4).

Subscribe to Atom feeds (RSS later) and see them on the overlay as a single row of **story-style heads**. Each
head is a round feed icon. Its ring lights up when the feed has entries you haven't seen. Clicking a head **plays
that feed's story**: its unread entries, one card at a time. A dedicated Settings page manages the subscriptions.

Atom first, because the format is far less ambiguous than RSS. Every entry has a required `id` and `updated`,
content says what type it is (`text`/`html`/`xhtml`), and links carry a `rel`. RSS 2.0 and RSS 1.0/RDF come in a
later phase and parse into the **same** normalized model, so nothing above the parser changes.

Existing patterns this copies (deliberately; nothing new to invent):

| Need | Copy from |
|---|---|
| Opt-in polled integration, one overlay section, a window | GitHub alerts (`OverlayCanvas.GitHub.cs`, `GitHubAlertsMonitorHost`, `GitHubAlertsWindow`, `docs/github-alerts.md`) |
| Row of icons with hit-testing | Quick-links row (`OverlayCanvas.DrawQuickLinksRow` / `HitTestQuickLink`) |
| List-editor Settings page + add/edit dialog | Quick Links page (`SettingsWindow.BuildQuickLinksPage`, `QuickLinkDialog`) |
| Seen/read markers on disk | `GitHubAlertsSeenStore` (`AtomicFile`, `%AppData%/<profile>/…`) |
| Self-stopping "something changed" attention cue | Social status-change glow (`OverlayCanvas.Feed.cs`) |
| Rendering untrusted rich text | `Rendering/MarkdownView` (fed sanitized Markdown, see below) |
| Images from untrusted sources | `Views/BoundedBitmap` |

---

## 1. What the user sees

### Overlay row: `OverlaySection.Feeds`

```
 ◉ ◉ ◎ ◎ ◎ ◎ ◎  +3   ＋
 ↑ ↑ ↑                ↑
 │ │ └ seen: thin muted ring
 │ └── unread: accent ring + count badge ("4")
 └──── just arrived: brief pop/glow (self-stopping)
```

- One line. Each head is a ~22 DIP circle holding the feed icon, clipped round, with a 2 DIP ring. If there's no
  icon, it shows the feed's initials over a colour derived from its name (reuse `Initials`/`FallbackColor`).
- **Ring states**:
  - **Unread**: accent ring. This is the "story" cue. Use the theme's `Accent` → `AccentHover` as a two-stop
    sweep so it reads like a stories ring. Both stops are theme roles, so no new palette role is needed.
  - **Seen**: a 1 DIP muted ring.
  - **Error**: seen ring plus a small yellow `DrawGlyphBadge` dot. The tooltip gives the reason.
  - **Unread count**: a small count pill at the bottom-right (`OverlayDraw.Text` height-derived, "9+" cap).
- **Order**: feeds with unread entries first, newest unread first. Then seen feeds, in the user's configured
  order. A stable sort, so heads don't jump around on every poll.
- **Overflow**: as many heads as fit the panel width. The rest collapse into a muted `+N` chip that plays the
  next feed with unread entries; the story window's tray reaches every feed. Width is computed, never a fixed
  head count.
- **Trailing "＋"**: opens the add-feed dialog (the stories "your story" slot). It's an ~18 px hover box, the same
  idiom as the collapsible headers' "+".
- **Attention**: when a poll brings new entries for a feed, its head plays a short pop/glow (~1.2 s, then
  stops), like the social status glow. The ring stays lit until the entries are read. **No perpetual animation**:
  the overlay repaints on a 60 ms pulse and a lit ring is enough. Honour reduce-motion (no pop, ring only) and
  `QuietMode` (the pop is playful; the ring isn't).
- **Hover tooltip**: the feed title, "4 new · latest: ‹title› · 2h ago" (or the error), then "Click to read".
- **Click**: plays that feed's story in the story window (below).
- **Right-click** menu on a head: *Mark all read*, *Refresh now*, *Open website*, *Edit feed…*, *Feeds settings…*.
- **Visible when** `ShowFeeds` is on **and** at least one subscription is enabled. In Rearrange/preview mode it
  is forced on and seeded from `SampleData.Feeds()`, like every other section.
- Not a collapsible section. It's already one line, the same reasoning as the GitHub strip. Height comes from the
  head diameter plus padding. The count pill's height comes from its measured text, per the line-height rule.

### Story player: `FeedStoryWindow`

A single reused window (`WindowHost.ShowOrFocus`, closed in `CloseAuxWindows`). It's a portrait card of about
440×640 DIP, opened on the overlay's monitor, that plays **one entry per card**:

```
┌──────────────────────────────────────────┐
│  ◉  ◉  ◎  ◎  ◎            (story tray)   │  ← every feed; current one enlarged, unread rings lit
│  ▬▬▬▬ ▬▬▬▬ ▭▭▭▭ ▭▭▭▭ ▭▭▭▭  (progress)     │  ← one segment per card in this feed's story
│  ◉ .NET Blog · 2h                     ⋯  │  ← head, feed title, entry age, menu
│ ┌──────────────────────────────────────┐ │
│ │ Performance improvements in .NET 10  │ │  ← entry title (large)
│ │ Jane Doe                             │ │
‹ │                                      │ ›  ← prev / next: buttons in the side gutters
│ │ (sanitized content via MarkdownView, │ │
│ │  scrolls inside the card)            │ │
│ └──────────────────────────────────────┘ │
│            [ Open in browser ↗ ]         │  ← pinned, always visible
└──────────────────────────────────────────┘
```

**What plays**

- **A head with unread entries**: its unread entries, **oldest to newest** (stories order). When they run out,
  the player moves on to the next feed with unread entries, in row order. After the last one comes an "All caught
  up" end card that closes on the next advance.
- **A head with nothing unread**: a replay of that feed's newest 10 entries. It opens on the newest, and `‹`
  walks back through older ones. When the replay ends, the player stops; it doesn't chain into other feeds.
- **The `+N` chip**: plays the next feed with unread entries, or opens the tray on the first feed if none have
  any.
- **The tray** at the top switches feeds at any point, so overflowed feeds are always reachable.

**Navigation**

| Input | Action |
|---|---|
| `›` gutter button, `→`, `Space` | Next card. At the end of a feed's story, the next feed with unread entries |
| `‹` gutter button, `←` | Previous card. At the start of a story, the previous feed |
| Click a progress segment | Jump to that card |
| `↑` / `↓`, wheel, `PgUp` / `PgDn` | Scroll the card body (only the body scrolls; the scroll region must actually scroll) |
| `Enter` | Open in browser |
| `Esc` | Close |

- **No timed auto-advance.** Instagram's timed advance suits pictures. These are posts you read at your own pace,
  so a segment fills when its card is **shown**.
- Advancing is deliberately **not** bound to clicks on the body. The body holds selectable text and links, so a
  stray click mustn't skip a post. Only the side gutters, the keys and the segments advance.

**Read state and look**

- Showing a card marks it read. The overlay ring updates as you go, and a feed's ring goes muted once its story
  has been watched. *Mark all read* lives in the `⋯` menu, along with *Open website*, *Edit feed…* and
  *Refresh*.
- **Card**:
  - The body is the content rendered by `MarkdownView` from sanitized HTML (§3.4) inside its own
    `ScrollViewer`. It falls back to `summary`, then to a "No preview, open in browser" stub.
  - The background is the theme surface, with a faint wash of the feed's derived colour behind the header.
  - The card and its colours come from `Palette` roles only.
- **Transitions**:
  - Moving between cards is a short horizontal slide (~150 ms).
  - Moving to another feed is a slightly bigger cube-ish slide.
  - With reduce-motion on, both are instant.
- **Error card**: a feed that can't be fetched plays a single card ("Couldn't load .NET Blog: 404 Not Found",
  with *Retry* and *Edit feed…*), then moves on.

**Dropped from the earlier draft**

The three-pane reader, list search and Unread/All tabs are gone. They fight the story model. If browsing an
archive is ever wanted, it's a later "All posts" sheet off the `⋯` menu, not part of v1.

### Settings: a dedicated **Feeds** page

Feeds are a list of records with a unique editor, so they get their own page (the Quick Links precedent), not
registry toggles. The toggles and steppers still go through the registry (§4.4).

- A list of subscriptions. Each row has an enable toggle, the head and title, the URL underneath (trimmed with
  `PrefixCharacterEllipsis` so the end of the URL stays readable), last-fetch status ("Updated 12 min ago" /
  "⚠ 404 Not Found"), and **Edit**/**Remove** buttons. Drag or up/down buttons set the base order.
- **Add feed…** opens `FeedDialog`. Paste a URL, then **Check**, which fetches and parses off the UI thread and
  shows the detected title, icon and entry count. The title is editable (an override). The dialog shows a clear
  error for HTML pages, 404s and non-feeds. Phase F6 adds autodiscovery: if the URL is an HTML page, the dialog
  finds its `<link rel="alternate" type="application/atom+xml">` and offers it.
- Below the list are the registry-backed controls: *Show feeds row*, *Check every N minutes*, *Notify on new
  entries*.
- Later (F7): **Import/Export OPML**.

---

## 2. Architecture

```
Perch.Core/Feeds/                       (UI-free, tested)
  FeedSubscription      — persisted config record (in AppSettings.Feeds)
  FeedModel             — FeedDoc / FeedEntry: the normalized model every format parses into
  AtomParser            — Atom 1.0 → FeedDoc (F0); RssParser (F6)
  FeedParser            — sniffs root element, dispatches; throw-free, returns FeedParseResult
  FeedFetcher           — HttpClient GET w/ conditional headers, caps, scheme checks
  FeedStore             — on-disk cache (last good doc per feed + HTTP validators) and read markers
  FeedReadState         — pure: unread computation, priming, pruning
  FeedStoryQueue        — pure: which cards play, in what order, and where next/prev go (§3.6)
  FeedText              — plain-text sanitizer for every feed-supplied string shown outside the card body
  FeedUrl               — the one gate every feed-supplied URL passes through
  HtmlToMarkdown        — allowlist sanitizer: entry HTML → safe Markdown for MarkdownView
  FeedsSnapshot         — what the UI consumes: per-feed heads + unread counts + errors + entries

Perch.App/
  Services/FeedsMonitorHost      — scheduler (DispatcherTimer), off-thread fetch, publish to canvas + window
  Views/OverlayCanvas.Feeds.cs   — the heads row (measure/paint/hit-test/tooltip/menu)
  Windows/FeedStoryWindow.cs     — the story player (tray, progress segments, card, gutters)
  Windows/FeedDialog.cs          — add/edit
  Windows/SettingsWindow  BuildFeedsPage
  Rendering/SampleData.Feeds(), HeadlessRenderer probes
```

Nothing is OS-specific: `HttpClient`, XML and Avalonia all run on both heads, so no `Perch.Platform.*` work is
needed. (The `docs/live-sources-plan.md` "Signals" pipeline could later consume `FeedEntry` as one more source.
This plan stays standalone, but it keeps entries keyed by a stable id + url so that adapter would be trivial.)

### 2.1 Model

```csharp
// Persisted in AppSettings.Feeds (List<FeedSubscription>?; null = never configured, like QuickLinks).
internal sealed class FeedSubscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");   // stable key for cache + read state
    public string Url { get; set; } = "";
    public string? TitleOverride { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime AddedUtc { get; set; }                            // priming baseline (see §3.3)
}

internal sealed record FeedDoc(string? Title, string? SiteUrl, string? IconUrl, DateTime? Updated,
    IReadOnlyList<FeedEntry> Entries);

internal sealed record FeedEntry(
    string Id,                // atom:id; RSS: guid ?? link ?? hash(title+date). Opaque key, never displayed
    string Title,             // FeedText-cleaned plain text (type="html" titles decoded + stripped)
    string? Url,              // FeedUrl-vetted absolute http(s) link, or null
    string? Author,           // FeedText-cleaned; entry author ?? feed author
    DateTime? Published, DateTime Updated,
    string? ContentHtml,      // content (html/xhtml/text→escaped) ?? summary; RAW, untrusted
    string? SummaryText);     // FeedText-cleaned plain-text summary (tooltip, toast)
```

Invariant: every **string** field except `ContentHtml` is already clean when the parser hands it over.
`ContentHtml` stays raw in the model and the cache, and is sanitized at render time by `HtmlToMarkdown`. That
way, a sanitizer fix applies to cached entries without a refetch.

`FeedDoc` is cached as JSON per subscription (`%AppData%/<profile>/feeds/<id>.json`), so the row and the player
are populated immediately at startup, before any network call. The cache is re-validated on load (`FeedUrl` and
`FeedText` run again), so a hand-edited or stale cache file is held to the same rules as a fresh fetch.

---

## 3. Core details

### 3.1 Parsing (Atom 1.0, RFC 4287)

- **Hand-rolled** (decided), with no new dependency. It reads through a hardened `XmlReader` into `XDocument`;
  the XML-level defences are in §3.4.
  - We chose this over `System.ServiceModel.Syndication`, whose RSS date parsing throws on the date formats
    real-world feeds use.
  - A tolerant ~200-line parser is also easier to fixture-test.
- Handle these:
  - `feed/title`, `feed/icon` (preferred, square) then `feed/logo`, and `feed/link[@rel=alternate]` as the
    site URL.
  - `entry/id`, `updated`, `published`, `title`, `link`, `author/name` (falling back to the feed author),
    `content`, `summary`.
  - Content `type`: `text` (escape it, then wrap it in a paragraph), `html` (the element text is HTML), `xhtml`
    (the inner XML of the wrapper `div` is the HTML). Out-of-line `content[@src]` gets a link stub only.
  - **`xml:base`** on feed, entry and content, plus the feed URL, for resolving relative `href`/`src`.
- Tolerance: an entry missing its `id` falls back to its link, then to a hash. A missing `updated` falls back to
  `published`, then to fetch time. Bad dates are tried as RFC 3339, then RFC 822 (for RSS), then dropped. A
  malformed entry is skipped, never the whole feed. A document that isn't XML or isn't a feed returns
  `FeedParseResult.Error("not a feed (looks like HTML)")`. It never throws, matching the "never throw out of a
  scan" rule.
- Cap entries kept per feed (newest 100) so the cache stays bounded.

### 3.2 Fetching (`FeedFetcher`)

- One shared `HttpClient` (`SocketsHttpHandler`). Settings:
  - Timeout 20 s, `AllowAutoRedirect = false` (redirects are followed manually so each hop is vetted; see
    §3.4.3), automatic gzip/deflate/brotli decompression.
  - Response cap: **4 MB** of decompressed body. Read through a counting stream and abort past the cap, which
    also caps compression bombs.
  - `User-Agent: Perch/<version> (+repo url)`.
  - `Accept: application/atom+xml, application/rss+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.1`.
- **Conditional GET**: store `ETag`/`Last-Modified` per feed and send `If-None-Match`/`If-Modified-Since`. A
  `304` means "no change", with no parse.
- Accept only `http`/`https`, for the subscription URL and for every redirect hop. Warn, but don't block, on
  plain `http`.
- Honour `429`/`503` `Retry-After`. Per-feed exponential backoff on errors (5 min → 2 h cap), so one dead feed
  doesn't hammer anything.
- Errors are mapped to short user-facing strings ("404 Not Found", "Timed out", "Not a feed", "Too large"). The
  last good cache stays on screen while a feed is failing.

### 3.3 Read state and "new" (`FeedReadState`, pure)

- Stored in `%AppData%/<profile>/feeds/read.json`: `{ subId: { readIds: [...], primedUtc } }`, written with
  `AtomicFile`.
- **Priming**: on the first successful fetch after a feed is added, every entry already there is marked read.
  Adding a feed with 50 posts does **not** light 50 unread (decided). Only entries that appear after that are
  unread. The head can still be clicked straight away and plays the replay story.
- Unread means the entry's `id` isn't in `readIds`. **Edits don't re-light** a read entry (`updated` bumps are
  noisy in real feeds); a per-feed "treat updates as new" option can come later if anyone asks.
- **Pruning is conservative.** A read marker is dropped only once its entry has left the feed **and** it's more
  than 30 days old, with a hard cap of 2,000 per feed. A feed that briefly serves an empty or truncated document
  can't wipe the markers and re-light everything when it recovers. (Markers are stored as id → read time.)

### 3.4 Injection defences

**The threat model.** Every byte of a feed is attacker-controlled. That includes the XML, every string, every
URL and the content HTML. A malicious or compromised feed (or a feed host hijacked mid-flight over plain
`http`) must not be able to:

- read local files;
- make Perch request internal addresses;
- run code or launch anything other than a browser on an `http(s)` page;
- spoof Perch's own UI;
- hang or exhaust the app;
- reach a Claude session.

No browser engine is embedded (no WebView), so there's no script runtime to attack. The remaining surfaces are
the XML parser, the HTML → Markdown conversion, URLs, plain-text fields and resource limits. Each gets **one
owning type**, so there's exactly one place to audit and test.

#### 3.4.1 XML layer (`FeedParser`)

- Read with `XmlReader.Create(capped stream, settings)`. The settings are always set **explicitly**, never left
  to defaults, so a refactor can't lose them:

  ```csharp
  new XmlReaderSettings
  {
      DtdProcessing = DtdProcessing.Prohibit,   // a DOCTYPE is an error: no XXE, no billion laughs
      XmlResolver = null,                       // never resolve an external anything
      MaxCharactersFromEntities = 1024,
      MaxCharactersInDocument = 8_000_000,      // a backstop behind the 4 MB byte cap
      IgnoreProcessingInstructions = true, IgnoreComments = true,
  }
  ```

- **Bounded depth**: track element depth while reading, and refuse documents nested deeper than 64. This stops
  a stack-exhaustion feed before it reaches any recursive code.
- `type="xhtml"` content is serialized back to a string with the same reader. It never passes through a second,
  differently configured parser.

#### 3.4.2 Content HTML (`HtmlToMarkdown`)

Entry HTML is converted to Markdown through an **allowlist**, then rendered by `MarkdownView`.

- **Tokenizer**: our own small tolerant tokenizer, **iterative and not recursive**. It uses an explicit tag stack
  capped at 32 deep; deeper nesting is flattened to text. The input is capped at 512 KB, and the rest becomes
  "… (continued in browser)".
- **Elements kept**: `p br h1–h6 strong b em i code pre blockquote ul ol li a hr table tr th td`.
- **Unwrapped to their text**: every other element (`span`, `div`, `font`, `section`…).
- **Dropped with all their content**: `script style iframe frame object embed applet form input button
  textarea select svg math noscript template link meta base`.
- **Attributes**: all of them are dropped at tokenize time, except `a@href`, `img@src`/`alt` and `ol@start`
  (parsed as a clamped integer). Since `style`, `class` and the
  `on*` handlers never survive, there's nothing to filter in them.
- **Text escaping**:
  - Entities are decoded **once**, then every text node is escaped for Markdown (`\ ` `` ` `` `* _ { } [ ] ( )
    # + - . ! | < > ~` and line-leading `>`/`#`/digits).
  - Content can therefore only gain the structure its **allowlisted tags** give it; text can never become a link,
    heading, table or raw HTML.
  - `<` is always escaped, so **no raw HTML reaches Markdig**, and `MarkdownView`'s `HtmlBlock` path stays
    unreachable for feed content.
  - Code is fenced with a backtick run one longer than the longest run inside it, so a code span can't break out
    of its fence.
- **Links**:
  - Every `href` goes through `FeedUrl.Safe` (§3.4.3).
  - A link that fails becomes its plain text.
  - URL characters that are Markdown-significant (`(`, `)`, spaces, `<`, `>`) are percent-encoded before the
    link is emitted, so a URL can't close the link early.
  - Link text that *looks like* a URL but points at a different host is shown with the real host after it
    ("paypal.com ↗ evil.example"), so a link can't disguise where it goes.
- **Images (v1)**: never fetched. Each one becomes a link stub ("🖼 alt-text ↗"), and the link goes through
  `FeedUrl.Safe` like any other. This rules out tracking pixels, decompression bombs and image-codec bugs. F7's
  opt-in images go through `BoundedBitmap` (header check, pixel cap, off-thread decode) with SVG refused.
- **`MarkdownView` wiring**: the story card is built with **no** `FileRefContext`. That keeps the renderer's
  file-reference arming (`OpenTargets.LinkFilePath`) inert, so a feed link can never open a local path. A test
  asserts that a feed-rendered `MarkdownView` arms only `http`/`https`/`mailto` links. This is a guard against a
  future change wiring file refs in by default.

#### 3.4.3 URLs (`FeedUrl`)

There's one gate, `FeedUrl.Safe(string raw, Uri? @base) → Uri?`, and **every** feed-supplied URL passes through
it: entry links, the site link, icon/logo, image stubs, content links and redirect hops.

- Resolve it against `xml:base`, then the document URL.
- Accept only an absolute `http`, `https` or (for links only) `mailto` URL. Everything else is refused:
  `javascript:`, `vbscript:`, `data:`, `file:`, UNC paths, `ms-*:`, `search-ms:`, custom app schemes and
  protocol-relative junk.
- Refuse URLs with userinfo (`https://bank.com@evil.example/`), a classic spoofing trick.
- Normalise IDN hosts to punycode for display, so a homograph host shows as `xn--…`.
- `IUrlOpener` already refuses non-web schemes (`OpenTargets.WebUrl`, from CP8). That's a second, independent
  layer, not the only one.

**Requests caused by the feed** (icon and logo now, images in F7) are **SSRF-fenced**. They're fetched with a
handler whose `ConnectCallback` refuses loopback, private (RFC 1918 / ULA), link-local and unspecified
addresses **after DNS resolution**. The check runs at connect time, so DNS rebinding can't get past it. A hostile
feed therefore can't make Perch `GET http://192.168.1.1/…`.

The user's **own subscription URL** may be private (a self-hosted feed is legitimate). Even so, redirects are
followed manually (max 5), and each hop is checked against `FeedUrl`. A public feed may not redirect into a
private address unless the original URL was itself private.

#### 3.4.4 Plain-text fields (`FeedText`)

Titles, authors, feed titles and summaries appear in places that aren't Markdown: the overlay tooltip, the story
header and tray, toasts and the Settings list. They're all cleaned **once, in the parser**, by `FeedText.Clean`:

- Strip tags, then decode entities **exactly once**, so `&amp;lt;script&gt;` stays literal text.
- Remove these:
  - C0/C1 control characters.
  - Bidi overrides, isolates and marks (U+202A–202E, U+2066–2069, LRM/RLM/ALM), which allow
    Trojan-Source-style reordering.
  - Zero-width space and non-joiner, word joiner and BOM (U+200B, U+200C, U+2060, U+FEFF).
  - Unicode tag characters (U+E0000–E007F), used for "ASCII smuggling".
  - Lone surrogates.

  The zero-width **joiner** (U+200D) is kept. It glues emoji sequences together and hides nothing on its own.
- Collapse whitespace and newlines, so a title can't render as multiple fake lines of Perch UI.
- Cap the length: title 300, author 100, feed title 100, summary 500 characters. Truncation happens at a grapheme
  boundary.
- Avalonia `TextBlock`s and `OverlayDraw.Text` don't interpret markup, so clean text is safe to draw.
- Toasts go through `ToastContentBuilder.AddText`, which XML-escapes (that's confirmed in
  `WindowsToastNotifier`). The mac notifier must do the same when it's filled in; that gets a note in the port
  plan.

#### 3.4.5 Resource limits

| Thing | Limit |
|---|---|
| Fetched body (decompressed) | 4 MB, abort past it |
| Icon | 256 KB, ≤ 256×256 px via `BoundedBitmap` |
| Entries kept per feed | 100 newest |
| Content HTML rendered | 512 KB per entry |
| HTML nesting / XML depth | 32 / 64 |
| Concurrent fetches | 4 |
| Request timeout | 20 s |

All parsing and conversion runs **off the UI thread**. The UI receives finished `FeedDoc`s and finished Markdown
strings only.

#### 3.4.6 Never into a session

Feed text is **never** passed to a Claude session, prompt, hook or command line. There are no "summarize this
post" or "send to session" actions in v1. If one is ever added, it needs its own review: it's the
prompt-injection point from `docs/live-sources-plan.md`.

### 3.5 Icons

Icons are resolved in this order and cached to `%AppData%/<profile>/feeds/icons/<id>.<ext>`:

1. Atom `<icon>`, then `<logo>`.
2. `https://<site host>/favicon.ico`.
3. Initials fallback.

Rules:

- Downloaded through the **SSRF-fenced** handler (§3.4.3), with a 256 KB cap. Decoded off the UI thread via
  `BoundedBitmap` with a small target width. SVG and anything `BoundedBitmap`'s header check doesn't recognise
  are refused, and the head falls back to initials.
- Re-fetched at most weekly.
- The canvas receives pre-decoded `Bitmap`s, exactly as quick links do. Nothing in `Render` touches the disk or
  the network.

### 3.6 Story order (`FeedStoryQueue`, pure)

The play order lives in Core, so it can be tested without a window:

- `ForHead(snapshot, subId)` returns a `StoryPlan`.
  - If the feed has unread entries, the plan is those entries oldest → newest, chained to every other feed with
    unread entries in row order, ending with an "All caught up" card.
  - If it has none, the plan is a replay of its newest 10 entries, positioned on the newest, unchained.
- `ForOverflow(snapshot)` starts at the next feed with unread entries.
- `Next`/`Prev`/`JumpTo(segment)`/`SwitchFeed(subId)` return the new position. Feed boundaries are explicit, so
  the window knows when to play the larger transition.
- The plan is a **snapshot**. A poll that lands mid-story appends any new unread entries for feeds *later* in the
  chain, but never reshuffles cards behind the cursor. Playback stays stable while the data updates.

---

## 4. App wiring

### 4.1 `FeedsMonitorHost`

This is `GitHubAlertsMonitorHost`'s shape, generalized to N sources:

- A single `DispatcherTimer` ticking every minute. Each feed has its own `NextDueUtc`, from the interval
  (`FeedsIntervalMinutes`, default **30**, clamped 5–240) plus a per-feed jitter so they don't all fire at once,
  stretched by backoff.
- Due feeds are fetched on the thread pool, **max 4 concurrently**. The result is parsed off-thread, then
  marshalled back with `Dispatcher.UIThread.Post`, guarded by a generation counter (Stop/restart drops stale
  answers).
- Ticks are skipped while the session is locked (`ISessionLock`). The first tick after unlock catches up.
- On each change it rebuilds a `FeedsSnapshot` and pushes it to the canvas (`SetFeedsRow(...)`), to the story
  player (`Changed` event) and to the notifier.
- Exposes `RefreshNow(subId?)`, `MarkRead(subId, entryId)`, `MarkAllRead(subId?)` and `SeedForRender(...)`.
- `ShowFeeds` off means stopped: no network at all, and the row is cleared (load-bearing, like GitHub alerts).
- It starts from the disk cache at launch, so the row is correct before the first fetch.
- **Startup fetch**: on start, every enabled feed is made due at `launch + 20 s + i × 3 s` (i = its index in the
  row). This is a staggered, delayed refresh, never a burst at launch. Switching the feature on at runtime uses
  the same stagger with no initial delay. A feed added in Settings is fetched immediately; that's its priming
  fetch.

### 4.2 Notifications (optional, off by default)

`NotifyOnFeedEntry` sends one toast per poll per feed, batched ("3 new posts in .NET Blog", with the latest title
underneath), through `NotificationService`. Priming never notifies. It respects `QuietMode`, DND and the global
notification switch. A click plays that feed's story. Toast text is `FeedText`-clean (§3.4.4).

### 4.3 Overlay canvas: `OverlayCanvas.Feeds.cs`

- Gate: `SetShowFeeds(bool)`. Data: `SetFeedsRow(FeedsRow row, IReadOnlyList<Bitmap?> icons)`, where
  `FeedsRow` is a toolkit-neutral record built from the snapshot.
- Section plumbing, as in the CLAUDE.md "Adding a section" recipe:
  - Add `OverlaySection.Feeds` and put it in `OverlaySectionOrder.Default`. Default slot: directly before
    `GitHub`. Check how `Normalize` splices a new member into a persisted order: it should land before GitHub
    there too.
  - Add arms in `SectionVisible`, `SectionHeight` and `PaintSectionCore`.
  - Add a `_sectionTop`-backed `FeedsTop` getter.
  - Rearrange forcing and dimming work as for other sections.
- Events: `FeedStoryRequested(string? subId)` (null = the overflow chip), `FeedAddRequested`,
  `FeedMenuRequested(subId)`.
- Perf rules:
  - Head geometry is cached per layout.
  - Rings use `OverlayDraw.Pen`/`Brush`.
  - The sweep brush is a field rebuilt only on theme change.
  - The pop animation runs on the existing pulse only while one is active, then stops.
  - `PERCH_BENCH=1` stays flat.

### 4.4 Settings registry and AppSettings

| AppSettings | Registry | Notes |
|---|---|---|
| `List<FeedSubscription>? Feeds` | — | Edited on the Feeds page; add to `SettingsRegistryTests.NotSettings` (like `QuickLinks`) |
| `bool ShowFeeds` (false) | `Toggle("feeds", "Feeds", …, SettingSurface.Integrations, ["atom","rss","feed","blog","news","stories","subscribe"], PreviewTarget.Feeds)` | Gate; drives `OverlaySettingsGates.Apply` → `SetShowFeeds` |
| `int FeedsIntervalMinutes` (30) | `Stepper("feeds-interval", …)` | Clamped 5–240 when applied |
| `bool NotifyOnFeedEntry` (false) | `Toggle("feeds-notify", …, SettingSurface.Notifications)` | |

- Add `PreviewTarget.Feeds` and seed the `PreviewPane` from `SampleData.Feeds()`.
- `SettingsHooks.FeedsChanged` goes to the App, which restarts or reschedules the host.
- `SettingsLiveApply` extends to start or stop the poller on toggle (it's a poll, so beyond the idempotent
  `DisplayChanged`).
- The pop animation is a *playful* effect. Check `QuietMode` there, rather than adding a separate setting.

---

## 5. Testing

The .NET suite (fixtures under `tests/Perch.Tests/fixtures/feeds/`), all runnable on both CI hosts:

- **`AtomParserTests`**:
  - The minimal valid feed.
  - Content types `text`/`html`/`xhtml`.
  - `content[@src]`.
  - Relative links with `xml:base` at feed and entry level.
  - Missing `id`/`updated` fallbacks.
  - Feed-level author inheritance.
  - `icon` vs `logo`.
  - A malformed entry among good ones.
  - A truncated document.
  - An HTML page given as the "feed" (expects an error result, not a throw).
  - The over-cap entry count.
- **`FeedInjectionTests`**: the hostile corpus, under `fixtures/feeds/hostile/`. **Every defence in §3.4 has at
  least one fixture**, and the suite is the gate for any change to the parser or sanitizers. Each fixture asserts
  a safe outcome: an error result, a refusal or neutralized output, and never a throw or hang.
  - **XML**: an external entity (`file:///…`, `http://…`); a billion-laughs DTD; parameter entities; a DOCTYPE
    of any kind; nesting deeper than 64; a 5 MB body; processing instructions.
  - **HTML**:
    - `<script>`, `<style>`, `<iframe>`, `<svg onload>` and `<math>` are removed *with* their contents.
    - `on*`, `style` and `class` attributes are gone.
    - `<a href="javascript:…">` in mixed case, with entity-encoded schemes (`jav&#x61;script:`), embedded
      tabs/newlines or a leading space becomes plain text.
    - `data:` and `vbscript:` links are neutralized, and `file:`/UNC/`ms-settings:` links are neutralized.
    - Unclosed tags and mis-nesting don't confuse the output.
    - HTML nested 10,000 deep is flattened.
    - Double-encoded entities (`&amp;lt;script&amp;gt;`) stay literal.
  - **Markdown injection**:
    - Text such as `[click](javascript:x)`, `![](http://track)`, `<b>`, `| a | b |`, `# Heading`, ``` `code` ```,
      `***` and `\` stays literal text in the rendered output.
    - A URL containing `)` or spaces can't close its link.
    - A code span containing backtick runs can't escape its fence.
  - **URLs**: userinfo spoofing is refused; protocol-relative URLs resolve correctly; IDN hosts show as
    punycode; a mismatched link text shows the real host.
  - **SSRF**:
    - Icon URLs pointing at `127.0.0.1`, `localhost`, `10.x`, `192.168.x`, `169.254.169.254`, `[::1]` and
      `fc00::/7` are refused at connect time, tested with a stub resolver plus `ConnectCallback`.
    - A public subscription redirecting to a private address is refused.
    - A user-entered private subscription is allowed.
  - **Text**: bidi overrides, zero-width characters, control characters, newlines in titles and over-length
    fields are all cleaned and capped. A cache file with a dirty title is re-cleaned on load.
  - **Output inertness** (built in F0): every converter output in the corpus is parsed with the exact pipeline
    `MarkdownView` uses (`MarkdownSourceMap.Pipeline`). The test then asserts it contains:
    - no `HtmlBlock`/`HtmlInline`;
    - no image;
    - no frontmatter block;
    - only links `OpenTargets.WebUrl` accepts.
  - **Render guard** (F4): the test suite only references Core, so the `MarkdownView`-level check lands with
    `FeedStoryWindow`. Building the card without a `FileRefContext` keeps path-like links (`C:\Windows\…`,
    `\\host\share`) inert. Assert that via a render probe, or move the arming decision into a Core helper the test
    can reach.
- **`HtmlToMarkdownTests`** (the happy path): relative hrefs resolved; images become link stubs; nested lists,
  `pre`, tables and blockquotes preserved; entities decoded once.
- **`FeedReadStateTests`**: priming on first fetch, new ids go unread, edits don't re-light, prune, mark-all.
- **`FeedStoryQueueTests`**: the unread plan's order and chaining; the replay plan's position; the end card;
  prev across feed boundaries; jump to a segment; a mid-story snapshot update doesn't move the cursor and appends
  only to later feeds.
- **`FeedFetcherTests`** (stub `HttpMessageHandler`, the `SupabaseSocialClient` precedent; no network):
  - 304 handling.
  - The ETag round-trip.
  - The size cap aborts, including a gzip bomb.
  - A manual redirect chain over 5 hops is refused, and each hop's scheme is checked.
  - `Retry-After` and backoff.
  - Error → message mapping.
- **Existing suites**: `OverlaySectionOrderTests` (new member normalizes in), `SettingsRegistryTests` coverage,
  `UiConventionTests` unaffected.
- **UI**: `HeadlessRenderer` probes:
  - `overlay_feeds` in mixed states: unread, seen, error, overflow `+N`, initials fallback; dark and light.
  - `feed_story`: a mid-story card, the first card, the "All caught up" end card, the error card, and a long post
    with the body scrolled.
  - `feed_story_hostile`: a card rendered from the hostile corpus, to eyeball that it reads as inert text.
  - `feed_dialog`.

  Eyeball them via `render`.
- **Manual live check** (owed after F4): a few real Atom feeds (GitHub releases `…/releases.atom`, a Blogger or
  WordPress `?feed=atom`, YouTube channel feeds).

---

## 6. Phases

Each phase is independently mergeable. The feature stays behind `ShowFeeds` (off) until F4.

| Phase | Scope | Done when |
|---|---|---|
| **F0** | Core model + hardened `FeedParser`/`AtomParser` + `FeedText` + `FeedUrl` + `HtmlToMarkdown`, **with the hostile corpus written first** | `FeedInjectionTests` (XML, HTML, Markdown, URL, text) + parser tests green on both hosts |
| **F1** | `FeedFetcher` (manual redirects, caps, SSRF-fenced handler for feed-caused requests) + `FeedStore` (cache, validators, read state, re-clean on load) + `FeedReadState` + `FeedStoryQueue` | Fetcher, SSRF, read-state and story-queue tests green |
| **F2** | `AppSettings` + registry descriptors + Feeds Settings page + `FeedDialog` (check/validate) | Can add/edit/remove/reorder feeds; registry coverage passes |
| **F3** | `FeedsMonitorHost` + `OverlaySection.Feeds` + `OverlayCanvas.Feeds.cs` row (rings, badges, overflow, tooltip, menu, pop) + SampleData/preview/render probe | Row renders correctly in render probes (dark/light); live polling updates it |
| **F4** | `FeedStoryWindow`: tray, progress segments, card with a scrolling `MarkdownView` body, gutters + keys, transitions (reduce-motion aware), end and error cards, read-on-show | Clicking a head plays its unread story and chains to the next feed; watching clears the ring; render guard test green; live check against real feeds |
| **F5** | Notifications + polish: keyboard shortcuts, *Mark all read* everywhere, error UX | Toasts batched + quiet-mode aware |
| **F6** | RSS 2.0 + RSS 1.0/RDF parsing into the same model; URL autodiscovery from HTML pages | RSS fixture tests (incl. bad RFC-822 dates, `guid isPermaLink`, `content:encoded`) |
| **F7** (later) | Opt-in inline images via `BoundedBitmap`; OPML import/export; authenticated feeds (token/basic via `ISecretStore`, never in `AppSettings`) | — |

---

## 7. Decisions

**Settled (2026-10-07, user)**

1. **Adding a feed** marks everything already in it read. The quiet start is in §3.3.
2. **The viewer is story-style.** It plays one card per entry, with a tray and progress segments, and chains
   between feeds (§1, §3.6). The three-pane reader is dropped.
3. **The parser is hand-rolled**, with injection defence as a first-class concern. §3.4 sets out the layered
   defences, each with an owning type, and `FeedInjectionTests` is written first in F0.

4. **Row placement**: the default slot is **directly before GitHub** (…Todo, Feeds, GitHub, Sessions…).
5. **Plain `http` feeds** are **allowed, with a warning** in the dialog and a small "not secure" marker on the
   Settings row.
6. **Polling** happens every **30 min**, plus a fetch **on startup**. The startup fetch is delayed (~20 s after
   launch) and staggered per feed (a few seconds apart), so launching Perch doesn't cause a request storm. The
   cache paints the row instantly in the meantime. Per-feed `<ttl>`/`sy:updatePeriod` hints are ignored in v1.
7. **Story auto-advance** is **off**. A segment fills when its card is shown.
