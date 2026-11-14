namespace EdgeChatRelay
{
    /// <summary>
    /// The JavaScript injected into every page in every panel.
    /// Responsibilities:
    ///   1. CAPTURE: a MutationObserver detects newly arrived chatbot responses
    ///      (website-agnostic heuristics) and posts them to the host.
    ///      User messages are also detected (send gestures) so they can be logged
    ///      and never mistaken for chatbot responses.
    ///   2. INJECT: window.__relayInjectAndSend() types text into the page's chat
    ///      input and optionally presses Send, exactly like a human would.
    /// </summary>
    public static class CaptureScript
    {
        public const string Source = """
(function () {
  'use strict';
  if (window.__edgeRelayInstalled) return;
  window.__edgeRelayInstalled = true;

  // ---------------- utilities ----------------
  var now = function () { return Date.now(); };

  // normalized single-line form used for comparisons / hashing
  function norm(s) {
    return String(s || '').replace(/\s+/g, ' ').trim();
  }

  // pretty form used for logging / forwarding (keeps paragraphs)
  function cleanText(s) {
    var t = String(s || '').replace(/\r\n?/g, '\n');
    var lines = t.split('\n').map(function (l) { return l.replace(/[ \t]+$/g, ''); });
    t = lines.join('\n').replace(/\n{3,}/g, '\n\n').trim();
    return t;
  }

  function hash(s) {
    var h = 5381, i;
    for (i = 0; i < s.length; i++) { h = (((h << 5) + h) + s.charCodeAt(i)) | 0; }
    return h + ':' + s.length;
  }

  function isVisible(el) {
    if (!el || !el.getClientRects) return false;
    if (el.getClientRects().length === 0) return false;
    var cs = window.getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden' || parseFloat(cs.opacity) === 0) return false;
    return true;
  }

  function isEditable(el) {
    if (!el) return false;
    var tag = (el.tagName || '').toLowerCase();
    if (tag === 'textarea') return !el.disabled && !el.readOnly;
    if (tag === 'input') {
      var t = (el.type || 'text').toLowerCase();
      return (t === 'text' || t === 'search' || t === '' || t === 'url') && !el.disabled && !el.readOnly;
    }
    return el.isContentEditable === true;
  }

  function getInputText(el) {
    if (!el) return '';
    try {
      if (el.isContentEditable) return cleanText(el.innerText || el.textContent || '');
      if ('value' in el) return cleanText(String(el.value || ''));
    } catch (e) { }
    return '';
  }

  // ---------------- state ----------------
  var state = {
    known: new Map(),       // hash -> true (LRU-ish)
    knownTexts: [],         // normalized texts (len>=16) for re-render detection
    injected: [],           // {text,t} messages the relay typed into this page
    sent: [],               // {text,t} messages the user/relay sent from this page
    pending: null,          // streaming capture buffer
    paused: false,
    lastResponse: '',
    counters: { responses: 0, users: 0, skipped: 0 }
  };

  var MAX_KNOWN = 500;
  var MIN_LEN = 12;          // fallback-mode minimum message length
  var STREAM_QUIET_MS = 1200; // finalize after this much text-quiet time
  var STREAM_MAX_MS = 25000;  // never wait longer than this

  function rememberHash(h) {
    state.known.set(h, true);
    if (state.known.size > MAX_KNOWN) {
      var first = state.known.keys().next().value;
      state.known.delete(first);
    }
  }

  function rememberText(n) {
    if (n.length < 16) return;
    state.knownTexts.push(n);
    if (state.knownTexts.length > 300) state.knownTexts.shift();
  }

  function isCompositeOfKnown(text) {
    // A full re-render re-adds old messages as one big blob. If the blob
    // contains 2+ previously-seen message texts, it is history, not a new reply.
    var n = norm(text), hits = 0, i, k;
    for (i = 0; i < state.knownTexts.length; i++) {
      k = state.knownTexts[i];
      if (k.length >= 16 && n.indexOf(k) !== -1) {
        hits++;
        if (hits >= 2) return true;
      }
    }
    return false;
  }

  function wasSentByUs(n) {
    var i, e, t0 = now();
    for (i = state.injected.length - 1; i >= 0; i--) {
      e = state.injected[i];
      if (t0 - e.t > 120000) break;
      if (e.text === n) return true;
    }
    return false;
  }

  function wasSentByUser(n) {
    var i, e, t0 = now();
    for (i = state.sent.length - 1; i >= 0; i--) {
      e = state.sent[i];
      if (t0 - e.t > 120000) break;
      if (e.text === n) return true;
      // bubble may truncate/ellipsize long messages
      if (n.length < 200 && (e.text.indexOf(n) === 0 || n.indexOf(e.text) === 0) && Math.abs(e.text.length - n.length) < 40) {
        return true;
      }
    }
    return false;
  }

  // ---------------- emission ----------------
  function post(msg) {
    try {
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage(msg);
      }
    } catch (e) { }
  }

  function emit(kind, text) {
    if (text.length > 120000) text = text.slice(0, 120000) + '\n…[truncated]';
    if (kind === 'response') {
      state.counters.responses++;
      state.lastResponse = text;
    } else {
      state.counters.users++;
    }
    post({ type: kind, text: text, t: now() });
  }

  // ---------------- DOM classification ----------------
  var BAD_SEL = 'nav,header,footer,aside,script,style,noscript,template,textarea,input,select,' +
    'button,[contenteditable="true"],[contenteditable=""],[role="navigation"],[role="banner"],' +
    '[role="contentinfo"],[role="toolbar"],[role="menu"],[role="menuitem"],[role="tab"],' +
    '[role="tablist"],[role="listbox"],[role="option"],[aria-hidden="true"]';

  var BAD_NAME = /(nav|navbar|navigation|menu|sidebar|side-bar|toolbar|header|footer|banner|ticker|toast|cookie|consent|modal|overlay|dropdown|tooltip|popover|breadcrumb|pagination|statusbar|tab-?list|tabbar|chip|badge|reaction|composer|editor-?wrap|input-?wrap|prompt-?wrap|chat-?input|search-?box|logo)/i;

  var GOOD_NAME = /(chat|message|conversation|thread|dialog|bubble|markdown|prose|response|answer|completion|assistant|bot|turn-?row|history|transcript|stream)/i;

  function nameBlob(el) {
    if (!el) return '';
    return ((el.id || '') + ' ' + (typeof el.className === 'string' ? el.className : '')).toLowerCase();
  }

  function inBadContainer(el) {
    try {
      if (el.closest && el.closest(BAD_SEL)) return true;
    } catch (e) { }
    var p = el, i, n;
    for (i = 0; i < 4 && p; i++) {
      n = nameBlob(p);
      if (n && BAD_NAME.test(n)) return true;
      p = p.parentElement;
    }
    return false;
  }

  function hasGoodName(el) {
    var p = el, i;
    for (i = 0; i < 6 && p; i++) {
      if (GOOD_NAME.test(nameBlob(p))) return true;
      p = p.parentElement;
    }
    return false;
  }

  function findScrollRoot(el) {
    var p = el.parentElement;
    while (p && p !== document.body && p !== document.documentElement) {
      var cs = window.getComputedStyle(p);
      var oy = cs.overflowY;
      if ((oy === 'auto' || oy === 'scroll') && p.scrollHeight > p.clientHeight + 30 && p.clientHeight > 120) {
        return p;
      }
      p = p.parentElement;
    }
    return null;
  }

  // Returns 'user' | 'assistant' | 'unknown' based on common author markers.
  function getRole(el) {
    var m = el && el.closest ? el.closest('[data-message-author-role],[data-author],[data-role],[data-sender],[data-message-sender]') : null;
    var v = '';
    if (m) {
      v = (m.getAttribute('data-message-author-role') || m.getAttribute('data-author') ||
           m.getAttribute('data-role') || m.getAttribute('data-sender') ||
           m.getAttribute('data-message-sender') || '').toLowerCase();
    } else {
      v = nameBlob(el).toLowerCase();
    }
    if (!v) return 'unknown';
    if (/(^|[^a-z])(user|human|customer|visitor|me)([^a-z]|$)/.test(v)) return 'user';
    if (/(^|[^a-z])(assistant|bot|ai|model|agent|gpt|claude|gemini)([^a-z]|$)/.test(v)) return 'assistant';
    return 'unknown';
  }

  // Split a freshly-added subtree into message-sized blocks. Big re-renders get
  // split; a single message stays whole.
  function pickBlocks(node, out) {
    var text = node.textContent || '';
    if (text.replace(/\s+/g, '').length < 4) return false;
    var kids = [], i, k;
    for (i = 0; i < node.children.length; i++) {
      k = node.children[i];
      if ((k.textContent || '').replace(/\s+/g, '').length >= 40) kids.push(k);
    }
    if (kids.length >= 2 && text.length > 800) {
      var any = false;
      for (i = 0; i < kids.length; i++) { any = pickBlocks(kids[i], out) || any; }
      return any;
    }
    out.push(node);
    return true;
  }

  function candidateFromEl(el) {
    if (!el || el.nodeType !== 1) return null;
    if (state.known.has(hash(norm(el.innerText || el.textContent || '')))) return null;
    if (inBadContainer(el)) return null;
    var role = getRole(el);
    if (role === 'user') return null;
    if (role !== 'assistant') {
      // unknown: require it to look like a conversation region
      var root = findScrollRoot(el);
      if (!root && !hasGoodName(el)) return null;
    }
    return el;
  }

  // ---------------- streaming buffer ----------------
  function beginOrUpdatePending(el) {
    var text = cleanText(el.innerText || el.textContent || '');
    if (!text) return;
    var p = state.pending;
    if (p && (p.el === el || p.el.contains(el) || el.contains(p.el))) {
      if (text.length >= p.text.length) {
        p.text = text;
        p.lastGrow = now();
      }
      return;
    }
    if (p) { flushPending(); }
    state.pending = { el: el, text: text, firstSeen: now(), lastGrow: now() };
  }

  function flushPending() {
    var p = state.pending;
    state.pending = null;
    if (!p) return;
    finalizeText(p.text);
  }

  function finalizeText(text) {
    var t = cleanText(text);
    var n = norm(t);
    if (n.length < 4) return;
    if (wasSentByUs(n) || wasSentByUser(n)) { state.counters.skipped++; return; }
    var h = hash(n);
    if (state.known.has(h)) { state.counters.skipped++; return; }
    // login/signup chrome that occasionally slips past the container filters
    if (n.length < 60 && /^(log in|log out|sign up|sign in|welcome back|accept cookies|learn more)(\s|$)/i.test(n)) { state.counters.skipped++; return; }
    if (isCompositeOfKnown(t)) {
      // old history re-rendered as one blob: record the parts, skip emission
      var i;
      for (i = 0; i < state.knownTexts.length; i++) { /* hashes already known */ }
      state.counters.skipped++;
      return;
    }
    if (t.length < MIN_LEN && !/[.!?]/.test(t) && n.indexOf(' ') === -1) { state.counters.skipped++; return; }
    rememberHash(h);
    rememberText(n);
    emit('response', t);
  }

  function tickPending() {
    var p = state.pending;
    if (!p) return;
    var t = now();
    if (t - p.lastGrow > STREAM_QUIET_MS || t - p.firstSeen > STREAM_MAX_MS) {
      flushPending();
    }
  }
  setInterval(tickPending, 400);

  // ---------------- mutation observer ----------------
  function handleRecords(records) {
    if (state.paused) return;
    var els = [], i, j, r, node;
    for (i = 0; i < records.length; i++) {
      r = records[i];
      if (r.type === 'characterData') {
        node = r.target && r.target.parentElement ? r.target.parentElement : null;
        if (node) els.push(node);
      } else if (r.type === 'childList') {
        for (j = 0; j < r.addedNodes.length; j++) {
          node = r.addedNodes[j];
          if (node.nodeType === 1) {
            pickBlocks(node, els);
          } else if (node.nodeType === 3 && node.parentElement) {
            els.push(node.parentElement);
          }
        }
      }
    }
    for (i = 0; i < els.length; i++) {
      var el = candidateFromEl(els[i]);
      if (el) beginOrUpdatePending(el);
    }
    // a user message appearing means the previous response is complete
    tickPending();
  }

  var observer = new MutationObserver(handleRecords);
  function startObserver() {
    try {
      observer.observe(document.documentElement, { childList: true, subtree: true, characterData: true });
    } catch (e) { }
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', startObserver);
  } else {
    startObserver();
  }

  // ---------------- user send-gesture tracking ----------------
  function markSent(t) {
    var n = norm(t);
    if (!n) return;
    var last = state.sent[state.sent.length - 1];
    if (last && last.text === n && now() - last.t < 1500) return; // double-fire guard
    state.sent.push({ text: n, t: now() });
    if (state.sent.length > 60) state.sent.shift();
    flushPending(); // response before this send is finished
    emit('user', t);
  }

  function findInputNear(el) {
    var scope = el.closest ? el.closest('form') : null;
    var root = scope || (el.closest ? el.closest('div,section,main') : null) || document.body;
    var nodes = root.querySelectorAll('textarea, input[type="text"], input:not([type]), [contenteditable="true"]');
    var best = null, bestScore = -1, i, e, rect, s;
    for (i = 0; i < nodes.length; i++) {
      e = nodes[i];
      if (!isVisible(e)) continue;
      rect = e.getBoundingClientRect();
      s = rect.width * rect.height / 1000;
      if (rect.top > window.innerHeight * 0.4) s += 10;
      if (s > bestScore) { bestScore = s; best = e; }
    }
    return best;
  }

  document.addEventListener('keydown', function (e) {
    if (state.paused) return;
    if (e.key !== 'Enter') return;
    if (e.shiftKey) return;
    if (!isEditable(e.target)) return;
    var t = getInputText(e.target);
    if (t) markSent(t);
  }, true);

  document.addEventListener('click', function (e) {
    if (state.paused) return;
    var b = e.target && e.target.closest ? e.target.closest('button,[role="button"],input[type="submit"]') : null;
    if (!b || !looksLikeSendButton(b)) return;
    var inp = findInputNear(b);
    var t = getInputText(inp);
    if (t) markSent(t);
  }, true);

  document.addEventListener('submit', function (e) {
    if (state.paused) return;
    var f = e.target;
    var inp = f && f.querySelector ? f.querySelector('textarea,input[type="text"],input:not([type])') : null;
    var t = getInputText(inp);
    if (t) markSent(t);
  }, true);

  // ---------------- send-button recognition (shared) ----------------
  var BAD_BTN = /\b(stop|generating|regenerate|retry|edit|copy|delete|remove|share|export|import|menu|new|clear|reset|cancel|close|attach|upload|file|image|camera|photo|video|mic|voice|audio|emoji|gif|tool|search|think|canvas|model|settings|profile|sign|login|home|more|like|dislike|thumb|pin|branch|plus|add)\b/i;

  function looksLikeSendButton(b) {
    var label = ((b.getAttribute('aria-label') || '') + ' ' + (b.getAttribute('title') || '') + ' ' +
                 (b.textContent || '') + ' ' + (b.getAttribute('data-testid') || '') + ' ' +
                 (b.getAttribute('name') || '') + ' ' + (b.getAttribute('id') || '')).trim();
    if (!label) return false;
    if (BAD_BTN.test(label)) return false;
    if (/\bsend\b|\bsubmit\b|send message|send prompt|send question|send chat|send reply/i.test(label)) return true;
    if (label.length <= 3 && /^[↑➤▶↵➜»>]$/.test(label)) return true;
    return false;
  }

  function findSendButton(input) {
    // 1) labelled send buttons near the input
    var scope = input && input.closest ? (input.closest('form') || input.closest('div')) : null;
    var roots = [];
    if (scope) roots.push(scope);
    if (input && input.parentElement) roots.push(input.parentElement.parentElement || input.parentElement);
    roots.push(document.body);
    var i, r, j, nodes, b;
    for (i = 0; i < roots.length; i++) {
      nodes = roots[i].querySelectorAll('button,[role="button"],input[type="submit"]');
      for (j = 0; j < nodes.length; j++) {
        b = nodes[j];
        if (!isVisible(b)) continue;
        if (b.disabled || b.getAttribute('aria-disabled') === 'true') continue;
        if (looksLikeSendButton(b)) return b;
      }
    }
    // 2) global labelled search
    nodes = document.querySelectorAll('button[aria-label*="send" i],button[title*="send" i],button[data-testid*="send" i]');
    for (j = 0; j < nodes.length; j++) {
      b = nodes[j];
      if (isVisible(b) && !b.disabled && b.getAttribute('aria-disabled') !== 'true' && looksLikeSendButton(b)) return b;
    }
    return null;
  }

  // ---------------- injection (host -> page) ----------------
  function isNonEmpty(el) {
    var t = getInputText(el);
    if (!t) return false;
    // leftover from our own previous attempt is NOT a user draft — allow overwrite
    var n = norm(t), i, e;
    for (i = state.injected.length - 1; i >= 0 && i > state.injected.length - 8; i--) {
      e = state.injected[i];
      if (now() - e.t > 300000) break;
      if (e.text === n) return false;
    }
    return true;
  }

  function fireInput(el, text) {
    var ev;
    try {
      ev = new InputEvent('input', { bubbles: true, cancelable: false, inputType: 'insertText', data: text, isComposing: false });
    } catch (e) {
      ev = new Event('input', { bubbles: true });
    }
    el.dispatchEvent(ev);
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // Safe text-setting that works for real form elements, contenteditable,
  // and custom editor widgets (e.g. Perchance's editor exposes setText()).
  // NEVER call a detached native setter on a foreign element (Illegal invocation).
  function setInputTextSafe(el, text) {
    var tag = (el.tagName || '').toLowerCase();
    if (typeof el.setText === 'function') {
      el.setText(text);
      fireInput(el, text);
      return 'setText';
    }
    if ((tag === 'textarea' || tag === 'input') && typeof el.value === 'string') {
      var proto = tag === 'textarea' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      var desc = Object.getOwnPropertyDescriptor(proto, 'value');
      try {
        if (desc && desc.set) { desc.set.call(el, text); } else { el.value = text; }
      } catch (e) {
        el.value = text;
      }
      fireInput(el, text);
      return 'value';
    }
    if (el.isContentEditable) {
      try {
        el.focus();
        var sel = window.getSelection();
        var range = document.createRange();
        range.selectNodeContents(el);
        range.collapse(false);
        sel.removeAllRanges();
        sel.addRange(range);
        if (document.execCommand('insertText', false, text)) return 'execCommand';
      } catch (e2) { }
      el.textContent = text;
      fireInput(el, text);
      return 'contenteditable';
    }
    el.textContent = text;
    fireInput(el, text);
    return 'textContent';
  }

  function findChatInput() {
    var sel = 'textarea, input[type="text"], input:not([type]), [contenteditable="true"], [contenteditable=""], [role="textbox"]';
    var nodes = document.querySelectorAll(sel);
    var best = null, bestScore = -1, i, el, rect, s, ph, name;
    for (i = 0; i < nodes.length; i++) {
      el = nodes[i];
      if (!isVisible(el)) continue;
      if (el.disabled || el.readOnly) continue;
      if ((el.getAttribute('type') || '').toLowerCase() === 'search') continue;
      rect = el.getBoundingClientRect();
      if (rect.width < 120 || rect.height < 18) continue;
      name = nameBlob(el).toLowerCase();
      ph = ((el.getAttribute('placeholder') || '') + ' ' + (el.getAttribute('aria-label') || '')).toLowerCase();
      // giant text surfaces are code/document editors, never chat inputs
      if (rect.height > 320) continue;
      s = 0;
      s += Math.min(25, (rect.width * rect.height) / 4000);
      s += (rect.top / Math.max(1, window.innerHeight)) * 40;
      if ((el.tagName || '').toLowerCase() === 'textarea') s += 12;
      else if (el.isContentEditable) s += 8;
      if (typeof el.setText === 'function') s += 6; // custom editor widgets are intentional
      if (/message|chat|reply|send|ask|type|prompt|input|say|write|question|help/.test(ph)) s += 18;
      if (/ask (the|a|your|me)|send a message|message the|chat with|talk to/.test(ph)) s += 10;
      if (hasGoodName(el)) s += 5;
      if (/editor|codemirror|\bcm[-_]|monaco|\bace\b|syntax|highlight|code-?area|code-?input/.test(name + ' ' + ph)) s -= 40;
      if (/search/i.test(name + ' ' + ph)) s -= 30;
      if (rect.height > 240) s -= 25;
      if (rect.width < 220 || rect.height < 26) s -= 15;
      if (s > bestScore) { bestScore = s; best = el; }
    }
    return bestScore >= 3 ? best : null;
  }

  window.__relayInjectAndSend = function (text, autoSend, mode) {
    // mode: 'auto' | 'button' | 'enter' | 'none'
    var result = { ok: false, method: '', detail: '' };
    try {
      var input = findChatInput();
      if (!input) { result.detail = 'no chat input found on page'; return JSON.stringify(result); }
      if (isNonEmpty(input)) { result.detail = 'input busy (draft present)'; return JSON.stringify(result); }

      input.focus();
      try {
        setInputTextSafe(input, text);
      } catch (e3) {
        result.detail = 'failed to set text: ' + e3;
        return JSON.stringify(result);
      }

      // remember so the resulting user bubble is never captured as a "response"
      state.injected.push({ text: norm(text), t: now() });
      if (state.injected.length > 60) state.injected.shift();

      if (!autoSend || mode === 'none') {
        result.ok = true;
        result.method = 'fill-only';
        return JSON.stringify(result);
      }

      var btn = (mode !== 'enter') ? findSendButton(input) : null;
      if (btn && mode !== 'enter') {
        btn.click();
        result.ok = true;
        result.method = 'button';
        return JSON.stringify(result);
      }

      // Enter fallback
      var keOpts = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true };
      input.dispatchEvent(new KeyboardEvent('keydown', keOpts));
      input.dispatchEvent(new KeyboardEvent('keypress', keOpts));
      input.dispatchEvent(new KeyboardEvent('keyup', keOpts));
      result.ok = true;
      result.method = 'enter';
      return JSON.stringify(result);
    } catch (e) {
      result.detail = String(e);
      return JSON.stringify(result);
    }
  };

  // ---------------- host controls ----------------
  window.__relayPauseCapture = function (p) { state.paused = !!p; };
  window.__relayStatus = function () { return JSON.stringify(state.counters); };
  window.__relayLastResponse = function () { return state.lastResponse; };

  // Manual "rescan": find the most recent unseen message-sized block and emit it.
  window.__relayRescan = function () {
    try {
      var all = document.querySelectorAll('div,p,li,article,section');
      var i, el, t, n;
      for (i = all.length - 1; i >= 0 && i > all.length - 400; i--) {
        el = all[i];
        if (!isVisible(el)) continue;
        if (inBadContainer(el)) continue;
        t = cleanText(el.innerText || '');
        n = norm(t);
        if (n.length < MIN_LEN) continue;
        if (state.known.has(hash(n)) || wasSentByUs(n) || wasSentByUser(n)) continue;
        if (getRole(el) === 'user') continue;
        // only leaf-ish blocks
        if (el.querySelector('div,p,li,article,section')) continue;
        finalizeText(t);
        return JSON.stringify({ ok: true, text: t.slice(0, 120) });
      }
      return JSON.stringify({ ok: false, text: '' });
    } catch (e) {
      return JSON.stringify({ ok: false, text: String(e) });
    }
  };

})();
""";
    }
}
