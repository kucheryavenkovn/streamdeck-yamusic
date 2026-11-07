import test from 'node:test';
import assert from 'node:assert/strict';
import { YandexAudio } from '../src/audio.mjs';

test('YandexAudio caches apps from list and forwards operations', async () => {
  const calls = [];
  const bridge = {
    request: async (op, payload) => {
      calls.push([op, payload]);
      return op === 'list' ? { apps: [{ key: 'C:\\Y.Music.exe', name: 'Яндекс Музыка', online: true, volume: 50 }] } : {};
    }
  };
  const yandex = new YandexAudio(bridge);
  await yandex.refresh();
  assert.equal(yandex.apps.length, 1);
  await yandex.operation('adjust', { key: 'C:\\Y.Music.exe', delta: 5 });
  assert.deepEqual(calls.at(-1), ['adjust', { key: 'C:\\Y.Music.exe', delta: 5 }]);
});

test('YandexAudio clears cached apps when a scan fails', async () => {
  let fail = false;
  const bridge = {
    request: async op => {
      if (op === 'list' && fail) throw new Error('bridge down');
      return { apps: [{ key: 'a' }] };
    }
  };
  const yandex = new YandexAudio(bridge);
  await yandex.refresh();
  assert.equal(yandex.apps.length, 1);
  // Like the original mixer controller, a failed operation resolves to undefined.
  fail = true;
  assert.equal(await yandex.refresh(), undefined);
  assert.deepEqual(yandex.apps, []);
});

test('YandexAudio serializes operations', async () => {
  const order = [];
  const bridge = {
    request: async (op, payload) => {
      await new Promise(resolve => setTimeout(resolve, payload.delay ?? 0));
      order.push(op);
      return {};
    }
  };
  const yandex = new YandexAudio(bridge);
  await Promise.all([
    yandex.operation('adjust', { delay: 20 }),
    yandex.operation('mute'),
  ]);
  assert.deepEqual(order, ['adjust', 'mute']);
});
