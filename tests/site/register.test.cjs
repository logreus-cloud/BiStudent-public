const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const site = path.join(__dirname, '../../BiStudent.Server/wwwroot/register');
const html = fs.readFileSync(path.join(site, 'index.html'), 'utf8');
const script = fs.readFileSync(path.join(site, 'register.js'), 'utf8');
const validCode = 'K7PM-Q2XA';

function group() {
  return {
    groupName: 'Группа 1',
    expiresAt: '2026-12-01T12:00:00Z',
    students: [
      { id: 'a', fullName: 'Анна Иванова', state: 'Registered' },
      { id: 'b', fullName: 'Борис Петров', state: 'Pending' },
      { id: 'c', fullName: 'Вера Сидорова', state: 'None' }
    ]
  };
}

function setup({ search = '', secure = true, mediaDevices = true, replies = [], bitmap = null } = {}) {
  const elements = new Map();
  const stepLabels = new Map();
  const requests = [];
  const cameraCalls = [];
  let stoppedTracks = 0;
  const queue = [...replies];

  class Element {
    constructor(tag, id = '', attributes = '') {
      this.id = id;
      this.tagName = tag.toUpperCase();
      this.value = '';
      this.checked = false;
      this.disabled = /\bdisabled\b/.test(attributes);
      this.hidden = /\bhidden\b/.test(attributes);
      this.textContent = '';
      this.innerHTML = '';
      this.dataset = {};
      this.style = {};
      this.children = [];
      this.files = [];
      this.listeners = new Map();
      this.attributes = new Map();
      this.classes = new Set();
      this.classList = {
        add: (...names) => names.forEach(name => this.classes.add(name)),
        remove: (...names) => names.forEach(name => this.classes.delete(name)),
        contains: name => this.classes.has(name),
        toggle: (name, force) => {
          const active = force === undefined ? !this.classes.has(name) : force;
          if (active) this.classes.add(name);
          else this.classes.delete(name);
          return active;
        }
      };
      const type = attributes.match(/\btype="([^"]+)"/);
      this.type = type?.[1] || '';
      if (this.type) this.attributes.set('type', this.type);
      if (this.disabled) this.attributes.set('disabled', '');
      if (this.hidden) this.attributes.set('hidden', '');
      if (this.tagName === 'VIDEO') {
        this.srcObject = null;
        this.videoWidth = 640;
        this.videoHeight = 480;
        this.play = async () => {};
      }
      if (this.tagName === 'CANVAS') {
        this.getContext = () => ({ drawImage() {} });
        this.toDataURL = () => 'data:image/jpeg;base64,QUJD';
      }
    }
    get className() { return [...this.classes].join(' '); }
    set className(value) { this.classes = new Set(value.split(/\s+/).filter(Boolean)); }
    get options() { return this.children.filter(child => child.tagName === 'OPTION'); }
    append(...children) {
      for (const child of children) {
        child.parent = this;
        this.children.push(child);
      }
    }
    appendChild(child) { this.append(child); return child; }
    replaceChildren(...children) { this.children = []; this.append(...children); }
    addEventListener(type, listener) {
      if (!this.listeners.has(type)) this.listeners.set(type, []);
      this.listeners.get(type).push(listener);
    }
    dispatchEvent(event) {
      const e = typeof event === 'string' ? { type: event } : event;
      e.target ||= this;
      e.preventDefault ||= () => { e.defaultPrevented = true; };
      for (const listener of this.listeners.get(e.type) || []) listener(e);
      return !e.defaultPrevented;
    }
    click() {
      if (this.disabled) return;
      if (this.type === 'radio') {
        this.checked = true;
        this.dispatchEvent('change');
      }
      this.dispatchEvent('click');
    }
    focus() { this.focused = true; }
    querySelectorAll(selector) {
      const found = [];
      for (const child of this.children) {
        if (child.tagName.toLowerCase() === selector) found.push(child);
        found.push(...child.querySelectorAll(selector));
      }
      return found;
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    setAttribute(name, value) {
      this.attributes.set(name, String(value));
      if (name === 'hidden' || name === 'disabled') this[name] = true;
      if (name === 'type') this.type = String(value);
    }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    removeAttribute(name) {
      this.attributes.delete(name);
      if (name === 'hidden' || name === 'disabled') this[name] = false;
    }
  }

  for (const match of html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/gi))
    elements.set(match[3], new Element(match[1], match[3], match[2]));
  for (const match of html.matchAll(/<span\b[^>]*data-step-label="(\d+)"[^>]*>/gi))
    stepLabels.set(match[1], new Element('span'));
  const el = id => elements.get(id);
  const document = {
    documentElement: { lang: 'ru' },
    getElementById: el,
    createElement: tag => new Element(tag),
    addEventListener() {},
    querySelector: selector => stepLabels.get(selector.match(/data-step-label="(\d+)"/)?.[1]) || null,
    querySelectorAll: selector => selector === '[data-step-label]' ? [...stepLabels.values()] : []
  };
  const navigator = {
    mediaDevices: mediaDevices ? {
      async getUserMedia(constraints) {
        cameraCalls.push(constraints);
        const tracks = Array.from({ length: 2 }, () => ({
          stop() { stoppedTracks++; },
          getSettings() { return { deviceId: 'front' }; }
        }));
        return { getTracks: () => tracks, getVideoTracks: () => [tracks[0]] };
      },
      async enumerateDevices() { return [{ kind: 'videoinput', deviceId: 'front', label: 'Front' }]; }
    } : undefined
  };
  const context = {
    document, navigator, location: { search }, history: { replaceState() {} },
    isSecureContext: secure, URLSearchParams, setTimeout, AbortController,
    scrollTo() {},
    async createImageBitmap(file) {
      return bitmap ? bitmap(file) : { width: 640, height: 480, close() {} };
    },
    async fetch(url, options) {
      requests.push({ url, method: options.method, body: JSON.parse(options.body) });
      const next = queue.shift();
      if (!next) throw new Error(`No response queued for ${url}`);
      const answer = await (typeof next === 'function' ? next() : next);
      const status = answer.status ?? 200;
      return { status, ok: status >= 200 && status < 300, json: async () => answer.body };
    }
  };
  context.window = context;
  context.globalThis = context;
  vm.runInNewContext(script, context, { filename: 'register.js' });
  return {
    el, requests, cameraCalls,
    get stoppedTracks() { return stoppedTracks; },
    reply(body, status = 200) { queue.push({ body, status }); },
    defer(promise) { queue.push(() => promise); }
  };
}

const settle = () => new Promise(resolve => setImmediate(resolve));

async function checked(options = {}) {
  const h = setup(options);
  h.reply(group());
  h.el('code').value = validCode;
  h.el('code-form').dispatchEvent('submit');
  await settle();
  assert.equal(h.el('step-2').hidden, false);
  return h;
}

function select(h, index = 0) {
  assert.equal(h.el('step-2').hidden, false);
  const radio = h.el('students').children[index].children[0];
  radio.checked = true;
  radio.dispatchEvent('change');
  h.el('consent').checked = true;
  h.el('consent').dispatchEvent('change');
}

async function atPhotos(options = {}) {
  const h = await checked(options);
  select(h);
  h.el('student-next').click();
  await settle();
  assert.equal(h.el('step-3').hidden, false);
  return h;
}

function takeThree(h) {
  for (let i = 0; i < 3; i++) h.el('capture').click();
}

async function uploadThree(h) {
  for (let i = 0; i < 3; i++) {
    h.el(`file-${i}`).files = [{ name: `${i}.jpg` }];
    h.el(`file-${i}`).dispatchEvent('change');
  }
  await settle();
}

function pictured(h) {
  return h.el('thumbnails').children.filter(item => item.children[0]?.tagName === 'IMG');
}

test('formats code input and rejects invalid characters', () => {
  const h = setup();
  h.el('code').value = 'k7pm q2xa!@#zz';
  h.el('code').dispatchEvent('input');
  assert.equal(h.el('code').value, validCode);
  h.el('code').value = 'a1b-';
  h.el('code').dispatchEvent('input');
  assert.equal(h.el('code').value, 'AB');
});

test('short code shows an error without a request', async () => {
  const h = setup();
  h.el('code').value = 'K7PM-Q2X';
  h.el('code-form').dispatchEvent('submit');
  await settle();
  assert.equal(h.requests.length, 0);
  assert.match(h.el('code-error').textContent, /восьмизначный/i);
  assert.equal(h.el('step-1').hidden, false);
});

test('valid code checks group and renders students with status badges', async () => {
  const h = await checked();
  assert.deepEqual(h.requests[0], {
    url: '/api/public/enrollment/check', method: 'POST', body: { code: validCode }
  });
  assert.equal(h.el('group-name').textContent, 'Группа 1');
  const cards = h.el('students').children;
  assert.equal(cards.length, 3);
  assert.deepEqual(cards.map(card => card.children[1].textContent),
    ['Анна Иванова', 'Борис Петров', 'Вера Сидорова']);
  assert.equal(cards[0].children[2].textContent, 'Лицо зарегистрировано');
  assert.equal(cards[1].children[2].textContent, 'На проверке');
});

test('404 response keeps code step and displays server message', async () => {
  const h = setup();
  h.reply({ message: 'Код не найден' }, 404);
  h.el('code').value = validCode;
  h.el('code-form').dispatchEvent('submit');
  await settle();
  assert.equal(h.el('code-error').textContent, 'Код не найден');
  assert.equal(h.el('step-1').hidden, false);
});

test('429 response asks the user to wait', async () => {
  const h = setup();
  h.reply({}, 429);
  h.el('code').value = validCode;
  h.el('code-form').dispatchEvent('submit');
  await settle();
  assert.match(h.el('code-error').textContent, /подождите/i);
});

test('query code starts checking automatically', async () => {
  const h = setup({ search: `?code=${validCode}`, replies: [{ body: group() }] });
  await settle();
  assert.equal(h.requests.length, 1);
  assert.equal(h.requests[0].body.code, validCode);
  assert.equal(h.el('step-2').hidden, false);
});

test('name search filters student cards', async () => {
  const h = await checked();
  h.el('student-search').value = 'пЕтР';
  h.el('student-search').dispatchEvent('input');
  assert.equal(h.el('students').children.length, 1);
  assert.equal(h.el('students').children[0].children[1].textContent, 'Борис Петров');
});

test('next needs both a student and consent', async () => {
  const h = await checked();
  assert.equal(h.el('student-next').disabled, true);
  h.el('consent').checked = true;
  h.el('consent').dispatchEvent('change');
  assert.equal(h.el('student-next').disabled, true);
  h.el('consent').checked = false;
  h.el('consent').dispatchEvent('change');
  const radio = h.el('students').children[0].children[0];
  radio.checked = true;
  radio.dispatchEvent('change');
  assert.equal(h.el('student-next').disabled, true);
  h.el('consent').checked = true;
  h.el('consent').dispatchEvent('change');
  assert.equal(h.el('student-next').disabled, false);
});

test('camera captures, retakes and stops tracks on back', async () => {
  const h = await atPhotos();
  assert.equal(h.cameraCalls.length, 1);
  assert.equal(h.cameraCalls[0].video.facingMode, 'user');
  takeThree(h);
  assert.equal(pictured(h).length, 3);
  assert.equal(h.el('submit').disabled, false);
  h.el('thumbnails').children[1].children[1].click();
  assert.equal(h.el('capture').disabled, false);
  h.el('capture').click();
  assert.equal(pictured(h).length, 3);
  h.el('back').click();
  assert.equal(h.stoppedTracks, 2);
  assert.equal(h.el('video').srcObject, null);
});

test('insecure page uses file inputs for photos', async () => {
  const h = await atPhotos({ secure: false });
  assert.equal(h.el('file-mode').hidden, false);
  assert.equal(h.el('camera-mode').hidden, true);
  for (let i = 0; i < 3; i++) assert.equal(h.el(`file-${i}`).type, 'file');
  h.el('file-0').files = [{ name: 'face.jpg' }];
  h.el('file-0').dispatchEvent('change');
  await settle();
  assert.equal(pictured(h).length, 1);
});

test('stale file decoding closes bitmap without adding a photo', async () => {
  let resolveBitmap;
  let closed = 0;
  const pending = new Promise(resolve => { resolveBitmap = resolve; });
  const h = await atPhotos({ secure: false, bitmap: () => pending });
  h.el('file-0').files = [{ name: 'old.jpg' }];
  h.el('file-0').dispatchEvent('change');
  h.el('back').click();
  select(h, 1);
  h.el('student-next').click();
  await settle();
  resolveBitmap({ width: 640, height: 480, close() { closed++; } });
  await settle();
  assert.equal(pictured(h).length, 0);
  assert.equal(closed, 1);
});

test('submit locks navigation and reports the submitted student', async () => {
  const h = await atPhotos();
  takeThree(h);
  let resolveSubmit;
  const pending = new Promise(resolve => { resolveSubmit = resolve; });
  h.defer(pending);
  h.el('submit').click();
  assert.equal(h.el('back').disabled, true);
  assert.equal(h.el('camera-select').disabled, true);
  h.el('back').click();
  h.el('camera-select').value = 'other';
  h.el('camera-select').dispatchEvent('change');
  if (!h.el('step-2').hidden) select(h, 1);
  assert.equal(h.el('step-3').hidden, false);
  assert.equal(h.cameraCalls.length, 1);
  assert.deepEqual(h.requests[1], {
    url: '/api/public/enrollment/submit', method: 'POST',
    body: { code: validCode, studentId: 'a', images: ['QUJD', 'QUJD', 'QUJD'], consent: true }
  });
  resolveSubmit({ body: { ok: true, message: 'Готово' } });
  await settle();
  assert.equal(h.el('step-4').hidden, false);
  assert.equal(h.el('completed-student').textContent, 'Анна Иванова');
});

test('submit problems keep photos and the capture step', async () => {
  const h = await atPhotos();
  takeThree(h);
  h.reply({ ok: false, message: 'Проверьте снимки', problems: ['Лицо не найдено', 'Плохое освещение'] });
  h.el('submit').click();
  await settle();
  assert.equal(h.el('step-3').hidden, false);
  assert.equal(pictured(h).length, 3);
  assert.equal(h.el('submit-error').textContent, 'Проверьте снимки');
  assert.deepEqual(h.el('problems').children.map(item => item.textContent),
    ['Лицо не найдено', 'Плохое освещение']);
});

test('another student rechecks the code and clears photos, files and consent', async () => {
  const h = await atPhotos({ secure: false });
  await uploadThree(h);
  assert.equal(pictured(h).length, 3);
  h.reply({ ok: true, message: 'Готово' });
  h.el('submit').click();
  await settle();
  for (let i = 0; i < 3; i++) h.el(`file-${i}`).value = `old-${i}`;
  h.reply(group());
  h.el('another').click();
  await settle();
  assert.equal(h.el('step-2').hidden, false);
  assert.equal(h.requests[2].body.code, validCode);
  assert.equal(h.el('consent').checked, false);
  assert.equal(h.el('student-next').disabled, true);
  for (let i = 0; i < 3; i++) assert.equal(h.el(`file-${i}`).value, '');
  select(h, 1);
  h.el('student-next').click();
  await settle();
  assert.equal(pictured(h).length, 0);
  assert.equal(h.el('submit').disabled, true);
});
