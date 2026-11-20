const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const test = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../Web/deck.js'), 'utf8');
const fixture = (0xdecaf).toString(16);

function dashboard(nativeBridge) {
  const elements = new Map(), pending = [], listeners = {}, intervals = new Map();
  function element(id) {
    if (!elements.has(id)) {
      const attributes = new Map(), events = {};
      const node = {
        disabled: true, hidden: true, value: '', textContent: '',
        setAttribute: (name, value) => attributes.set(name, value),
        getAttribute: name => attributes.get(name) || null,
        removeAttribute: name => attributes.delete(name),
        addEventListener: (type, cb) => { events[type] = cb; },
        querySelector: () => ({ textContent: id }),
        emit: type => events[type]({ currentTarget: node, preventDefault() {} }),
        focus() {}
      };
      elements.set(id, node);
    }
    return elements.get(id);
  }
  const mic = element('mic'), camera = element('camera');
  mic.setAttribute('data-action', 'mic');
  camera.setAttribute('data-action', 'camera');
  const window = {
    PortalPairing: nativeBridge,
    addEventListener: (type, cb) => { listeners[type] = cb; },
    fetch: (path, options) => new Promise(resolve => pending.push({ path, options, resolve })),
    setInterval: (cb, delay) => intervals.set(delay, cb),
    setTimeout() {},
    clearTimeout() {}
  };
  runInNewContext(source, {
    window, document: { getElementById: element, querySelectorAll: () => [mic, camera] }
  });
  async function respond(path, code, body, expectedToken = fixture, action) {
    assert.ok(pending.length, 'expected an API request');
    const request = pending.shift();
    assert.equal(request.path, path);
    assert.equal(request.options.headers.Authorization, 'Bearer ' + expectedToken);
    assert.equal(request.options.redirect, 'error');
    assert.equal(request.options.mode, 'same-origin');
    assert.equal(request.options.method, action ? 'POST' : 'GET');
    assert.equal(request.options.headers.Origin, undefined);
    if (action) assert.deepEqual(JSON.parse(request.options.body), { action });
    request.resolve({ status: code, json: () => Promise.resolve(body) });
    await new Promise(resolve => setImmediate(resolve));
  }
  return { element, pending, listeners, mic, camera, respond, poll: intervals.get(3000), window };
}

test('native startup pairs immediately, polls the real microphone state, and recovers after an outage', async () => {
  const page = dashboard({ token: () => fixture });
  await page.respond('/api/state', 200, { state: 'mixed', count: 2 });
  assert.equal(page.element('mic-state').textContent, 'Mixed');
  assert.equal(page.element('connection').textContent, 'Online');
  page.poll();
  await page.respond('/api/state', 503, null);
  assert.equal(page.element('mic-state').textContent, 'Unknown');
  assert.equal(page.element('connection').textContent, 'Offline');
  assert.equal(page.mic.disabled, true);
  page.poll();
  await page.respond('/api/state', 200, { state: 'live', count: 2 });
  assert.equal(page.element('mic-state').textContent, 'Unmuted');
  assert.equal(page.mic.disabled, false);
});

test('late native readiness pairs once and HTTP 200 action failure does not set camera state', async () => {
  const page = dashboard();
  assert.equal(page.pending.length, 0);
  page.window.PortalPairing = { token: () => fixture };
  page.listeners['portal-pairing-ready']();
  page.listeners['portal-pairing-ready']();
  assert.equal(page.pending.length, 1);
  await page.respond('/api/state', 200, { state: 'muted', count: 1 });
  page.camera.emit('click');
  page.mic.emit('click');
  assert.equal(page.pending.length, 1, 'suppresses overlapping actions');
  assert.equal(page.camera.getAttribute('data-result'), 'pending');
  await page.respond('/api/action', 200, { ok: false, message: 'Camera unavailable' }, fixture, 'camera');
  assert.equal(page.camera.getAttribute('data-result'), 'failure');
  assert.equal(page.camera.getAttribute('data-state'), null);
  assert.equal(page.element('feedback').textContent, 'Camera unavailable');
  assert.equal(page.element('connection').textContent, 'Online');
  await page.respond('/api/state', 200, { state: 'unavailable', count: 0 });
  assert.equal(page.element('mic-state').textContent, 'Unavailable');
});

test('manual pairing clears the field and allows retry after HTTP 401', async () => {
  const page = dashboard();
  const field = page.element('pair-token');
  const form = page.element('pair-form');
  page.element('manage-pairing').emit('click');
  field.value = '0000';
  form.emit('submit');
  assert.equal(field.value, '');
  await page.respond('/api/state', 401, null, '0000');
  assert.match(page.element('pair-status').textContent, /rejected/i);
  page.poll();
  assert.equal(page.pending.length, 0, 'does not poll with rejected token');
  field.value = fixture;
  form.emit('submit');
  await page.respond('/api/state', 200, { state: 'muted', count: 1 });
  assert.equal(page.element('pairing').hidden, true);
  assert.equal(field.value, '');
});
