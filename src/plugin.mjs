import WebSocket from 'ws';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { AudioBridge } from './bridge.mjs';
import { WindowCapture } from './window-capture.mjs';
import { PlayerControl, yandexAudioApp } from './player-control.mjs';
import { YandexAudio } from './audio.mjs';

const args = Object.fromEntries(Array.from({ length: Math.floor((process.argv.length - 2) / 2) }, (_, i) => [process.argv[2 + i * 2], process.argv[3 + i * 2]]));
if (!args['-port'] || !args['-pluginUUID']) throw new Error('Launch this plugin from Stream Deck.');
const ws = new WebSocket(`ws://127.0.0.1:${Number(args['-port'])}`, { maxPayload: 8 * 1024 * 1024 });
const send = message => { if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message)); };
const log = message => send({ event: 'logMessage', payload: { message: String(message) } });
const audio = new AudioBridge(path.join(path.dirname(fileURLToPath(import.meta.url)), 'native', 'AudioBridge.exe'), log);
const captureBridge = new AudioBridge(path.join(path.dirname(fileURLToPath(import.meta.url)), 'native', 'WindowCapture.exe'), log);
const yandex = new YandexAudio(audio, log);
const player = new PlayerControl({ send, bridge: captureBridge, log });
const capture = new WindowCapture({ send, bridge: captureBridge, log, control: async (command, value) => {
  if (command === 'play' || command === 'seek') return player.control(command, value);
  const app = yandexAudioApp(yandex.apps);
  const result = await yandex.operation(command === 'volume' ? 'adjust' : 'mute', { key: app.key, ...(command === 'volume' ? { delta: value } : {}) });
  if (!result) throw new Error('Не удалось изменить громкость Яндекс Музыки. Проверьте аудиосессию.');
  return result;
} });
let stopped = false;
let audioTimer;
let captureTimer;
let animationTimer;
async function pollAudio() {
  if (stopped) return;
  try { await yandex.refresh(); } catch { /* logged inside YandexAudio */ }
  if (!stopped) audioTimer = setTimeout(pollAudio, 1000);
}
async function pollCapture() {
  if (stopped) return;
  const started = Date.now();
  try { await capture.refresh(); await player.refresh(); } catch (error) { log(error.message); }
  if (!stopped) captureTimer = setTimeout(pollCapture, Math.max(50, 500 - (Date.now() - started)));
}
ws.on('open', () => {
  send({ event: args['-registerEvent'], uuid: args['-pluginUUID'] });
  pollAudio();
  pollCapture();
  animationTimer = setInterval(() => capture.animate(), 100);
});
ws.on('message', data => {
  try {
    const event = JSON.parse(String(data));
    player.handle(event).then(handled => { if (!handled) return capture.handle(event); }).catch(error => log(error.stack));
  }
  catch (error) { log(error.message); }
});
function close() { stopped = true; clearTimeout(audioTimer); clearTimeout(captureTimer); clearInterval(animationTimer); captureBridge.close(); audio.close(); ws.close(); }
ws.on('close', close);
ws.on('error', error => { console.error(error.message); close(); });
process.on('SIGTERM', close);
process.on('SIGINT', close);
