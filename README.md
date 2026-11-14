# Edge Chat Relay

**A single Windows executable: two Edge browser panels side by side, with the power to
let the chatbots in those panels talk to *each other* — through you as the conduit.**

No API keys. No extensions. No automation services. Every message goes through the
real websites' chat boxes in a real Edge browser (WebView2), under your own signed-in
accounts — exactly as if you typed it yourself.

---

## Quick start

1. Download **`EdgeChatRelay.exe`** from the [Releases page](../../releases) anywhere on
   Windows 10/11 and double-click it.
   *(The WebView2 runtime it uses is Microsoft Edge's engine — already installed on
   virtually every Windows 10/11 machine. If missing, the app will point you to the free installer.)*
2. You get **one window, three columns: Panel 1 | CONDUIT | Panel 2** — no tabs.
3. Each outer panel is a full browser with its **own separate profile** (own cookies,
   own logins) — so you can even sign into *two different accounts on the same site*.
4. Type a chatbot URL in a panel's address bar (e.g. `chatgpt.com`, `claude.ai`,
   `gemini.google.com`, `perplexity.ai`, …), press Enter, and **sign in like a human**.
5. Chat normally. Everything works exactly like a regular browser.

## The CONDUIT — the third prompt box in the middle

The middle column is a literal prompt box **connected to nothing**. It is only a
text clipboard — it never talks to any service, API, or website. It just copies
and pastes, and every relayed message physically passes through it:

```
left chatbot's reply ──► CONDUIT BOX ──► right panel's chat box
left panel's chat box ◄── CONDUIT BOX ◄── right chatbot's reply
```

Because text *lands in the box* before moving on, you can:

- **watch every message as it transits** (the transit log underneath records
  everything that passed through),
- **edit a relayed message mid-flight** — during the delay window the text sits in
  the box; whatever it says when the timer ends is what actually gets sent
  (erase it entirely to cancel that relay),
- **type your own interjections** into the middle box and fire them with
  **→ Panel 1**, **→ Panel 2**, or **→ Both** — then the next chatbot reply to
  your interjection flows right back through the conduit.

## Automatic response logging (per panel)

Every new chatbot response captured in a panel is **appended to that panel's own text
file**, automatically, the moment it finishes arriving:

- Panel 1 → `Documents\EdgeChatRelay\panel1_chat_log.txt`
- Panel 2 → `Documents\EdgeChatRelay\panel2_chat_log.txt`
- Click **“Log file…”** in a panel to choose a different file. The **Log** button
  toggles logging per panel.

Your own messages are logged too (marked `[user]`), so the file is a clean transcript.

## RELAY mode — two chatbots, one conduit

Press **ARM RELAY** in the top bar. From then on:

- Whenever a **new chatbot response** appears in Panel 1 (with that panel's **Relay**
  button checked), it is staged in the **conduit box**, then automatically **typed
  into Panel 2's chat box and sent**.
- Whenever Panel 2's chatbot responds, that reply passes through the conduit box
  back into Panel 1 and sent.
- …and so on. The two chatbots are now conversing with each other through a plain
  text clipboard that connects to nothing. **Go take that shower.** Come back
  whenever you want.

### Take the reins at any time

- **STOP** (or **Esc**) instantly halts forwarding.
- Type into the **conduit box** and press **→ Panel 1 / → Panel 2 / → Both** to
  interject on your own terms; the next chatbot reply relays as usual when you re-ARM.
- **Edit mid-flight:** during the delay window a relayed message is sitting in the
  conduit box — change it before the timer ends and your version is what gets sent.
- **Your drafts are safe everywhere:** if you're mid-typing in a panel's chat box
  *or* in the conduit box when a relay forward arrives, the relay *waits* for you
  instead of overwriting your text.
- Press **ARM RELAY** again to hand the conversation back to the bots.

## Safety & pacing controls

| Control | What it does |
|---|---|
| **max turns** | Circuit breaker — relay auto-stops after N forwarded messages (default 50, 0 = unlimited) |
| **delay (s)** | Human-ish pause before each forward (default 2.5 s) |
| **Send: Auto / Button / Enter / Fill only** | Per-panel send style. *Fill only* stages the message in the chat box **without sending** so you can review it first |
| **Relay** (per panel) | Include/exclude that panel from relaying (its responses still get logged) |
| **Rescan** | If auto-capture ever misses a response on an unusual site, click this to grab the most recent one manually |

## How “website-agnostic” capture works

The app injects a small observer script into every page. It watches the DOM for
*newly arriving message-sized text* (including token-by-token streaming replies),
while ignoring things like:

- your own sent messages and anything the relay itself typed (never re-relayed),
- site chrome (headers, sidebars, buttons, inputs),
- re-rendered history and duplicate bubbles.

Most major chat UIs (ChatGPT, Claude, Gemini, Copilot, Perplexity, open-source chat
web UIs, etc.) expose enough structure for reliable detection. Sites that render
conversations in unusual ways can occasionally fool it — that's what **Rescan** and
the per-panel capture status line are for.

## Popups & sign-in

OAuth/sign-in popups open as real little windows **sharing that panel's profile**,
so “Sign in with Google” etc. works like it would in Edge.

## Notes

- Profiles live in `%LOCALAPPDATA%\EdgeChatRelay\ProfileA` and `ProfileB`;
  settings (URLs, toggles, window layout) persist between runs.
- The first launch extracts its runtime to a temp cache (the exe is self-contained;
  nothing else to install).
- Chatbot sites' terms of service apply to your use — this tool just does what your
  own fingers could do, at your own direction.

## Building from source

```
cd EdgeChatRelay
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableWindowsTargeting=true -o publish
```

Requires the .NET 8 SDK (the `EnableWindowsTargeting` flag lets it build on
Linux/macOS too — that's how the shipped exe was made).

The capture engine's JavaScript has a test suite (14 checks, covers streaming
capture, duplicate suppression, and the input-busy / Illegal-invocation fixes):

```
cd js-test
npm ci
npm test
```

CI runs it on every push (`.github/workflows/ci.yml`). Pushing a `v*` tag — or
running the **Release** workflow manually with a tag — builds the single-file exe
on a Windows runner and attaches it to a GitHub Release
(`.github/workflows/release.yml`). The binary is ~163 MB, well past GitHub's
100 MB file limit, so it lives on the Releases page and never in the Git tree.

## Documentation

| File | Purpose |
|---|---|
| `SETUP-GUIDE.txt` | End-user setup and assembly guide |
| `COLLAB-SETUP.txt` | Step-by-step for the two-AI collaboration run |
| `CHARTER-FORGE.txt` / `CHARTER-BLUEPRINT.txt` | The two per-panel AI charters |
| `uploads/` | Sample per-panel chat logs and reference screenshots |
