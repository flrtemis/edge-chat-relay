// Test harness for the capture/injection JS embedded in CaptureScript.cs
const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

const src = fs.readFileSync(path.join(__dirname, '..', 'EdgeChatRelay', 'CaptureScript.cs'), 'utf8');
const m = src.match(/public const string Source = """\r?\n([\s\S]*?)\r?\n""";/);
if (!m) { console.error('FAIL: could not extract JS from CaptureScript.cs'); process.exit(1); }
const js = m[1];

// syntax check first
try { new Function(js); } catch (e) { console.error('FAIL: JS syntax error:', e.message); process.exit(1); }
console.log('OK: JS syntax valid');

const dom = new JSDOM(`<!DOCTYPE html><html><body>
  <header id="hdr"><div id="hdrnav">Home</div></header>
  <div id="chat" style="overflow-y:auto;height:500px"><div id="log"></div></div>
  <div id="composer"><textarea id="ta"></textarea><button id="send" aria-label="Send message">Send</button></div>
</body></html>`, { runScripts: 'outside-only', pretendToBeVisual: true });

const { window } = dom;
const { document } = window;

// --- layout stubs (jsdom has no layout) ---
window.Element.prototype.getClientRects = function () { return [{ width: 200, height: 40, top: 400, left: 0 }]; };
window.Element.prototype.getBoundingClientRect = function () {
  return { width: 300, height: 40, top: 400, left: 0, right: 300, bottom: 440, x: 0, y: 400 };
};
// force fallback text-setting path (execCommand not in jsdom)
document.execCommand = () => false;

// --- capture postMessage traffic ---
const posted = [];
window.chrome = { webview: { postMessage: (msg) => posted.push(msg) } };

window.eval(js);
console.log('OK: script installed');

const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const log = document.getElementById('log');
const ta = document.getElementById('ta');

let failures = 0;
function check(name, cond, extra) {
  if (cond) { console.log('OK:', name); }
  else { failures++; console.log('FAIL:', name, extra !== undefined ? JSON.stringify(extra) : ''); }
}

function streamResponse(text, cls, done) {
  const msg = document.createElement('div');
  msg.className = cls;
  const span = document.createElement('span');
  msg.appendChild(span);
  log.appendChild(msg);
  const chunks = text.split(/(?<= )/);
  let acc = '', i = 0;
  const t = setInterval(() => {
    acc += chunks[i++];
    span.textContent = acc;
    if (i >= chunks.length) { clearInterval(t); done(msg); }
  }, 40);
}

(async () => {
  // ---------- 1. user send gesture ----------
  ta.value = 'What is the capital of France?';
  ta.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
  ta.value = ''; // real chat sites clear the input after send
  await sleep(150);
  check('captures user send (Enter)', posted.some(p => p.type === 'user' && p.text === 'What is the capital of France?'), posted);

  // user bubble appears (as the site renders it)
  const ub = document.createElement('div');
  ub.className = 'message user-row';
  ub.textContent = 'What is the capital of France?';
  log.appendChild(ub);
  await sleep(300);

  // ---------- 2. streaming assistant response A ----------
  const textA = 'The capital of France is Paris. It has been the capital since the late 10th century and is the largest city in the country.';
  await new Promise(res => streamResponse(textA, 'message assistant-row', res));
  await sleep(2200);
  const respA = posted.filter(p => p.type === 'response' && p.text.includes('capital of France is Paris'));
  check('captures streaming response A', respA.length === 1, posted.filter(p => p.type === 'response'));

  // ---------- 3. second response (for dedupe/composite tests) ----------
  const textB = 'Here is a second, differently worded reply about rivers. The longest river in France is the Loire, stretching roughly one thousand kilometers.';
  await new Promise(res => streamResponse(textB, 'message assistant-row', res));
  await sleep(2200);
  check('captures streaming response B', posted.some(p => p.type === 'response' && p.text.includes('longest river in France')), posted.filter(p => p.type === 'response'));

  // ---------- 4. injected text must be excluded ----------
  const r = JSON.parse(window.__relayInjectAndSend('Please tell me a joke about computers and keyboards', true, 'auto'));
  check('injectAndSend fills the input', r.ok === true && ta.value.includes('joke about computers'), r);
  ta.value = ''; // site clears input when the send goes through
  // site renders the user bubble for our injected message
  const ib = document.createElement('div');
  ib.className = 'message user-row';
  ib.textContent = 'Please tell me a joke about computers and keyboards';
  log.appendChild(ib);
  await sleep(2200);
  check('injected message NOT captured as response', !posted.some(p => p.type === 'response' && p.text.includes('joke about computers')), posted.filter(p => p.type === 'response'));

  // ---------- 5. role-marked user bubble excluded ----------
  const rb = document.createElement('div');
  rb.setAttribute('data-message-author-role', 'user');
  rb.textContent = 'This is a role marked user message that should never be captured at all.';
  log.appendChild(rb);
  await sleep(2200);
  check('role=user bubble NOT captured', !posted.some(p => p.type === 'response' && p.text.includes('role marked user message')), posted.filter(p => p.type === 'response'));

  // ---------- 6. header/chrome text ignored ----------
  document.getElementById('hdrnav').textContent = 'BRAND NEW HEADER PROMOTIONAL BANNER TEXT';
  await sleep(1800);
  check('header chrome NOT captured', !posted.some(p => p.type === 'response' && p.text.includes('PROMOTIONAL')), posted.filter(p => p.type === 'response'));

  // ---------- 7. full re-render (history blob) ignored ----------
  log.textContent = '';
  const blob = document.createElement('div');
  blob.className = 'conversation';
  blob.textContent = textA + ' ' + textB + ' What is the capital of France? more history here';
  log.appendChild(blob);
  await sleep(2200);
  const after = posted.filter(p => p.type === 'response');
  check('re-render history blob NOT re-emitted', after.length === 2, after);

  // ---------- 8. re-adding exact bubble A ignored (hash dedupe) ----------
  const dup = document.createElement('div');
  dup.className = 'message assistant-row';
  dup.textContent = textA;
  log.appendChild(dup);
  await sleep(2200);
  check('duplicate bubble NOT re-emitted', posted.filter(p => p.type === 'response').length === 2, posted.filter(p => p.type === 'response'));

  // ---------- 9. rescan finds nothing new ----------
  const rs = JSON.parse(window.__relayRescan());
  check('rescan: nothing new after all captured', rs.ok === false, rs);

  // ---------- 10. custom editor widget must NOT win over the chat box ----------
  // (regression: Perchance-style code editor with setText() + huge rect)
  const editor = document.createElement('div');
  editor.className = 'code-editor cm-content';
  editor.setAttribute('role', 'textbox');
  editor.setText = function (t) { editor.textContent = t; editor.__lastSet = t; };
  document.body.appendChild(editor);
  ta.value = '';
  const r2 = JSON.parse(window.__relayInjectAndSend('Ping from the relay with a new bus entry', true, 'auto'));
  check('inject targets chat textarea, not custom editor', r2.ok === true && ta.value.includes('Ping from the relay') && !editor.__lastSet, r2);
  ta.value = '';

  // ---------- 11. our own leftover text is overwriteable (not a user draft) ----------
  const r3 = JSON.parse(window.__relayInjectAndSend('First attempt text that got stuck', true, 'none'));
  check('fill-only leaves text in box', r3.ok === true && r3.method === 'fill-only', r3);
  const r4 = JSON.parse(window.__relayInjectAndSend('Second attempt after stuck text', true, 'none'));
  check('own leftover text does not trigger input busy', r4.ok === true && ta.value.includes('Second attempt'), r4);

  console.log(failures === 0 ? '\nALL TESTS PASSED' : `\n${failures} TEST(S) FAILED`);
  process.exit(failures === 0 ? 0 : 1);
})();
