import test from 'node:test';
import assert from 'node:assert/strict';
import { WindowCapture, WINDOW, captureOptions, frameSvg, formatTime } from '../src/window-capture.mjs';

test('capture defaults to bottom Yandex strip; validates crop and span', () => {
  assert.deepEqual(captureOptions(), { handle: '', pid: 0, mode: 'bottom', width: 800 });
  assert.deepEqual(captureOptions({ mode: 'bad', span: false, pid: 'oops' }), { handle: '', pid: 0, mode: 'bottom', width: 200 });
});
test('full-width frame slices by physical column; single-section frame does not', () => {
  assert.match(frameSvg('data:image/png;base64,AAAA', 800, 3), /viewBox="600 0 200 100"/);
  assert.match(frameSvg('data:image/png;base64,AAAA', 200, 3), /viewBox="0 0 200 100"/);
});
test('track time is large and bold, with reserved space or its own line', () => {
  const media = { title: 'Song', elapsed: 65, duration: 243 };
  const full = frameSvg('image', 800, 3, media);
  assert.match(full, /font-family="monospace" font-size="20" font-weight="bold"/);
  assert.match(full, /x="792" y="22"/);
  assert.match(full, /width="583" height="29"/);
  assert.match(full, /<\/g><rect x="588" y="0" width="212" height="29" fill="#000"\/><text/);
  const single = frameSvg('image', 200, 0, media);
  assert.match(single, /x="192" y="50"/);
  assert.match(single, /height="58" fill="#000"/);
});
test('marquee escapes title and advances, time respects pause, duration and missing timeline', () => {
  const media = { title: '<script>&', artist: 'Artist', elapsed: 65, duration: 243, playing: true };
  const first = frameSvg('image', 800, 0, media, 2, 0);
  assert.match(first, /1:07 \/ 4:03/);
  assert.match(first, /&lt;script&gt;&amp;/);
  assert.doesNotMatch(first, /<script>/);
  assert.notEqual(first, frameSvg('image', 800, 0, media, 2, 1));
  assert.match(frameSvg('image', 800, 0, { ...media, playing: false }, 5), /1:05 \/ 4:03/);
  assert.match(frameSvg('image', 800, 0, media, 1000), /4:03 \/ 4:03/);
  assert.match(frameSvg('image', 800, 0, { ...media, elapsed: null, duration: 0 }), /--:-- \/ --:--/);
  assert.equal(formatTime(3601), '60:01');
});
const appear = (context, column = 0, settings = {}) => ({ event: 'willAppear', action: WINDOW, context, device: 'deck', payload: { coordinates: { column }, settings } });
test('one capture serves four sections; unrelated and disconnected actions are isolated', async () => {
  const sent = [], requests = [];
  const controller = new WindowCapture({ send: m => sent.push(m), bridge: { request: async (op, payload) => {
    requests.push({ op, payload });
    return { result: { image: 'image', title: 'Yandex', media: { title: 'Song', elapsed: 10, duration: 100 } } };
  } } });
  assert.equal(await controller.handle({ event: 'dialRotate', action: 'local.tux.appmixer.dial', context: 'other' }), false);
  for (let i = 0; i < 4; i++) await controller.handle(appear(`c${i}`, i));
  await controller.refresh();
  assert.equal(requests.length, 1);
  assert.equal(controller.frames.size, 4);
  assert.equal(await controller.handle({ event: 'dialRotate', action: WINDOW, context: 'c0' }), true);
  assert.equal(requests.length, 1);
  await controller.handle({ event: 'deviceDidDisconnect', device: 'deck' });
  assert.equal(controller.frames.size, 0);
  await controller.refresh();
  assert.equal(requests.length, 1);
});
test('capture failure replaces old frame; settings changed during capture discard stale result', async () => {
  let finish;
  const sent = [], controller = new WindowCapture({ send: m => sent.push(m), bridge: { request: () => new Promise(resolve => { finish = resolve; }) } });
  await controller.handle(appear('c'));
  const pending = controller.refresh();
  await controller.handle({ event: 'didReceiveSettings', context: 'c', payload: { settings: { span: false } } });
  finish({ result: { image: 'old', title: 'old' } });
  await pending;
  assert.equal(controller.frames.size, 0);
  controller.bridge.request = async () => { throw new Error('Minimized'); };
  await controller.refresh();
  assert.equal(controller.status.get('c'), 'Minimized');
  assert.equal(controller.frames.size, 0);
  assert.equal(controller.busy, false);
});
