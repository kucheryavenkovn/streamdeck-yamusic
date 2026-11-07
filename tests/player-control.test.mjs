import test from 'node:test';
import assert from 'node:assert/strict';
import { PlayerControl, PLAYER_PREFIX, playerKeySvg, yandexAudioApp, playerInfoSvg, artistLines } from '../src/player-control.mjs';
import { WindowCapture, WINDOW, dialRole } from '../src/window-capture.mjs';

const event = (name, command = 'play', context = 'key') => ({ event: name, action: `${PLAYER_PREFIX}${command}`, context, device: 'deck' });
const decodeImage = image => Buffer.from(image.split(',')[1], 'base64').toString('utf8');
test('music keys execute exactly one targeted command per completed press', async () => {
  const requests = [], sent = [];
  const player = new PlayerControl({ send: m => sent.push(m), bridge: { request: async (op, payload) => { requests.push({ op, payload }); return { result: {} }; } } });
  await player.handle(event('willAppear'));
  await player.handle(event('keyUp'));
  assert.equal(requests.length, 0);
  await player.handle(event('keyDown')); await player.handle(event('keyDown'));
  await player.handle(event('keyUp')); await player.handle(event('keyUp'));
  assert.deepEqual(requests, [{ op: 'player-control', payload: { command: 'play' } }]);
  await player.handle(event('keyDown')); await player.handle(event('willDisappear')); await player.handle(event('keyUp'));
  assert.equal(requests.length, 1);
  assert.equal(await player.handle({ event: 'keyDown', action: 'local.tux.appmixer.app', context: 'mixer' }), false);
});
test('state polling never applies controls; icons reflect playback and toggles', async () => {
  const requests = [];
  let now = 100;
  const player = new PlayerControl({ send: () => {}, now: () => now, bridge: { request: async op => {
    requests.push(op); return { result: { playing: true, buttons: { repeat: { toggle: 'On', enabled: true } } } };
  } } });
  await player.handle(event('willAppear', 'repeat'));
  await player.refresh(); await player.refresh();
  assert.deepEqual(requests, ['player-state']);
  now += 1100; await player.refresh();
  assert.deepEqual(requests, ['player-state', 'player-state']);
  assert.match(playerKeySvg('play', { playing: true }), />Пауза</);
  assert.match(playerKeySvg('repeat', { toggle: 'On' }), /stroke="#ffcc33"/);
  await player.handle({ event: 'deviceDidDisconnect', device: 'deck' });
  now += 1100; await player.refresh(); assert.equal(requests.length, 2);
});
test('native failure alerts without replaying command; queue recovers', async () => {
  const sent = [], requests = [];
  const player = new PlayerControl({ send: m => sent.push(m), bridge: { request: async (op, payload) => {
    requests.push({ op, payload }); throw new Error('Player unavailable');
  } } });
  await player.handle(event('willAppear', 'like'));
  await player.handle(event('keyDown', 'like')); await player.handle(event('keyUp', 'like'));
  assert.equal(requests.length, 1); assert(sent.some(m => m.event === 'showAlert'));
  player.bridge.request = async (op, payload) => { requests.push({ op, payload }); return { result: {} }; };
  await player.control('seek', -5);
  assert.deepEqual(requests[1], { op: 'player-seek', payload: { seconds: -5 } });
});
test('volume target never falls back to browser, system or offline sessions', () => {
  const yandex = { key: 'c:\\program files\\windowsapps\\a025c540.yandex.music_version\\y.music.exe', volume: 80 };
  const browser = { key: 'c:\\chrome.exe', name: 'Yandex Music' };
  assert.equal(yandexAudioApp([browser, yandex]), yandex);
  assert.throws(() => yandexAudioApp([browser, { ...yandex, online: false }]), /не найдена/);
  assert.throws(() => yandexAudioApp([yandex, { key: 'c:\\app\\YandexMusic.exe' }]), /несколько/);
});
test('default dials seek and adjust volume; press rotates never cause play or mute', async () => {
  const commands = [], capture = new WindowCapture({ send: () => {}, bridge: {}, control: async (...args) => commands.push(args) });
  const dial = (event, column, payload = {}) => ({ event, action: WINDOW, context: `dial${column}`, device: 'deck', payload: { coordinates: { column }, ...payload } });
  for (let c = 0; c < 4; c++) await capture.handle(dial('willAppear', c));
  await capture.handle(dial('dialRotate', 0, { ticks: -2 }));
  await capture.handle(dial('dialRotate', 3, { ticks: 3 }));
  await capture.handle(dial('dialRotate', 1, { ticks: 3 }));
  assert.deepEqual(commands, [['seek', -10], ['volume', 6]]);
  await capture.handle(dial('dialDown', 0)); await capture.handle(dial('dialUp', 0));
  await capture.handle(dial('dialDown', 3)); await capture.handle(dial('dialUp', 3));
  assert.deepEqual(commands.slice(2), [['play', undefined], ['mute', undefined]]);
  await capture.handle(dial('dialDown', 3));
  await capture.handle(dial('dialRotate', 3, { ticks: 1, pressed: true }));
  await capture.handle(dial('dialUp', 3));
  assert.equal(commands.length, 4);
  assert.equal(dialRole({ dialRole: 'seek' }, 3), 'seek');
  assert.equal(dialRole({ dialRole: 'none' }, 0), 'none');
});
test('rapid controls execute serially and preserve relative seek order', async () => {
  let finish;
  const requests = [], player = new PlayerControl({ send: () => {}, bridge: { request: async (op, payload) => {
    requests.push(payload.seconds); if (requests.length === 1) await new Promise(resolve => { finish = resolve; });
    return { result: {} };
  } } });
  const first = player.control('seek', 5), second = player.control('seek', -5);
  await Promise.resolve(); assert.deepEqual(requests, [5]);
  finish(); await Promise.all([first, second]); assert.deepEqual(requests, [5, -5]);
});
test('cover key embeds only local PNG data; missing or unsafe artwork has a placeholder', () => {
  assert.match(playerInfoSvg('cover', { cover: 'data:image/png;base64,AAAA' }), /xlink:href="data:image\/png;base64,AAAA"/);
  assert.doesNotMatch(playerInfoSvg('cover', { cover: 'https://example.com/private.png' }), /<image|https:/);
  assert.match(playerInfoSvg('cover'), /Нет обложки/);
});
test('artist is wrapped, escaped and constrained to a key-sized text area', () => {
  assert.deepEqual(artistLines('Chris Rea'), ['Chris Rea']);
  assert.deepEqual(artistLines('One two three four'), ['One two', 'three four']);
  assert(artistLines('ОченьДлинноеИмяИсполнителя').every(line => Array.from(line).length <= 11));
  const image = playerInfoSvg('artist', { artist: '<script>& X' });
  assert.doesNotMatch(image, /<script>/); assert.match(image, /&lt;script&gt;&amp;/);
  assert.match(image, /font-size="21"/);
  const long = playerInfoSvg('artist', { artist: 'Очень длинное имя исполнителя '.repeat(10) });
  assert.match(long, /font-size="16"/); assert.match(long, /…/);
  for (const match of long.matchAll(/textLength="([\d.]+)"/g)) assert(Number(match[1]) <= 128);
});
test('cover and artist key presses never dispatch player commands', async () => {
  const requests = [], player = new PlayerControl({ send: () => {}, bridge: { request: async op => {
    requests.push(op); return { result: { title: 'Song', artist: 'Artist', cover: 'data:image/png;base64,AAAA' } };
  } } });
  for (const command of ['cover', 'artist']) {
    await player.handle(event('willAppear', command, command));
    await player.handle(event('keyDown', command, command));
    await player.handle(event('keyUp', command, command));
  }
  assert.equal(requests.length, 0);
  await player.refresh(); assert.deepEqual(requests, ['player-state']);
});
test('new track replaces cover and artist together, absent artwork never reuses old cover', async () => {
  let now = 0, index = 0;
  const sent = [], states = [
    { title: 'First', artist: 'First artist', cover: 'data:image/png;base64,AAAA' },
    { title: 'Second', artist: 'Second artist', cover: null }
  ];
  const player = new PlayerControl({ send: m => sent.push(m), now: () => now, bridge: { request: async () => ({ result: states[index++] }) } });
  await player.handle(event('willAppear', 'cover', 'cover'));
  await player.handle(event('willAppear', 'artist', 'artist'));
  await player.refresh();
  assert.match(decodeImage(sent.filter(m => m.event === 'setImage' && m.context === 'cover').at(-1).payload.image), /AAAA/);
  now = 1100; await player.refresh();
  const latest = decodeImage(sent.filter(m => m.event === 'setImage' && m.context === 'cover').at(-1).payload.image);
  assert.doesNotMatch(latest, /AAAA/); assert.match(latest, /Нет обложки/);
  assert.match(decodeImage(sent.filter(m => m.event === 'setImage' && m.context === 'artist').at(-1).payload.image), /Second/);
});
