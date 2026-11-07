import { dataUri } from './render.mjs';

export const PLAYER_PREFIX = 'local.tux.yamusic.music-';
export const PLAYER_COMMANDS = {
  previous: 'Предыдущий', play: 'Пауза / пуск', next: 'Следующий',
  repeat: 'Повтор', shuffle: 'Вперемешку', like: 'Лайк'
};
export const PLAYER_INFO = { cover: 'Обложка трека', artist: 'Исполнитель' };
const playerActions = { ...PLAYER_COMMANDS, ...PLAYER_INFO };
const escape = value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[c]);
export function artistLines(value, limit = 11) {
  const lines = [];
  for (const word of String(value).trim().split(/\s+/).filter(Boolean)) {
    const chars = Array.from(word);
    if (lines.length && Array.from(lines.at(-1)).length + chars.length + 1 <= limit) lines[lines.length - 1] += ` ${word}`;
    else {
      while (chars.length > limit) lines.push(chars.splice(0, limit).join(''));
      if (chars.length) lines.push(chars.join(''));
    }
  }
  return lines;
}
export function playerInfoSvg(kind, state = {}) {
  if (kind === 'cover' && /^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(state.cover ?? ''))
    return `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="144" height="144"><image width="144" height="144" xlink:href="${state.cover}"/></svg>`;
  const text = kind === 'artist' ? (state.artist || 'Нет данных') : 'Нет обложки';
  let lines = artistLines(text), size = 21, maxLines = 4;
  if (lines.length > maxLines) { lines = artistLines(text, 14); size = 16; maxLines = 5; }
  if (lines.length > maxLines) { lines = lines.slice(0, maxLines); lines[maxLines - 1] = Array.from(lines[maxLines - 1]).slice(0, 13).join('') + '…'; }
  const lineHeight = size + 3, top = 77 - (lines.length - 1) * lineHeight / 2;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144"><rect width="144" height="144" rx="20" fill="#191b23"/><text x="72" y="18" text-anchor="middle" fill="#ffdd66" font-family="sans-serif" font-size="11">${kind === 'artist' ? 'ИСПОЛНИТЕЛЬ' : 'ОБЛОЖКА'}</text><g fill="white" font-family="sans-serif" font-size="${size}" font-weight="bold" text-anchor="middle">${lines.map((line, i) => `<text x="72" y="${top + i * lineHeight}" textLength="${Math.min(128, Array.from(line).length * size * 0.62).toFixed(1)}" lengthAdjust="spacingAndGlyphs">${escape(line)}</text>`).join('')}</g></svg>`;
}
export function yandexAudioApp(apps) {
  const matches = apps.filter(app => app.online !== false && /[\\/](?:y\.music|yandexmusic)\.exe$/i.test(app.key));
  if (matches.length !== 1) throw new Error(matches.length ? 'Найдено несколько аудиосессий Яндекс Музыки.' : 'Аудиосессия Яндекс Музыки не найдена. Начните воспроизведение.');
  return matches[0];
}
const icons = {
  previous: '<path d="M43 50h8v44h-8zM91 50v44L54 72z"/>',
  next: '<path d="M93 50h8v44h-8zM53 50v44l37-22z"/>',
  play: '<path d="M57 47v50l39-25z"/>',
  pause: '<path d="M52 49h14v46H52zM79 49h14v46H79z"/>',
  repeat: '<path d="M99 62V49H57c-16 0-25 13-25 24h8c0-8 6-16 17-16h34v5l17-13-17-13v13h8zM45 82v13h42c16 0 25-13 25-24h-8c0 8-6 16-17 16H53v-5L36 95l17 13V95h-8z"/>',
  shuffle: '<g fill="none" stroke="currentColor" stroke-width="7"><path d="M34 51h15c17 0 28 42 45 42h14M34 93h15c7 0 13-7 19-17m9-17c6-6 10-8 17-8h14"/><path d="m98 40 12 11-12 11m0 20 12 11-12 11"/></g>',
  like: '<path d="M72 99 39 68c-22-24 13-47 33-21 20-26 55-3 33 21z"/>'
};
export function playerKeySvg(command, { playing = null, toggle = null, enabled = true, error = false } = {}) {
  const active = toggle === 'On' || toggle === 'Indeterminate';
  const color = error ? '#ff6666' : !enabled ? '#666977' : active ? '#ffcc33' : '#f4f6ff';
  const label = command === 'play' && playing !== null ? (playing ? 'Пауза' : 'Пуск') : PLAYER_COMMANDS[command];
  const icon = icons[command === 'play' && playing === true ? 'pause' : command] ?? '';
  return `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144"><rect width="144" height="144" rx="22" fill="#191b23"/><rect x="5" y="5" width="134" height="134" rx="18" fill="none" stroke="${active ? '#ffcc33' : '#34394a'}" stroke-width="3"/><g fill="${color}" color="${color}">${icon}</g>${toggle === 'Indeterminate' ? '<text x="72" y="78" text-anchor="middle" fill="#191b23" font-size="16">1</text>' : ''}<text x="72" y="124" text-anchor="middle" fill="${color}" font-family="sans-serif" font-size="13">${label ?? 'Музыка'}</text></svg>`;
}

export class PlayerControl {
  constructor({ send, bridge, log = () => {}, now = Date.now }) {
    this.send = send; this.bridge = bridge; this.log = log; this.now = now;
    this.actions = new Map(); this.images = new Map(); this.pressed = new Set(); this.pending = new Set();
    this.state = {}; this.error = null; this.nextRefresh = 0;
    this.queue = Promise.resolve();
  }
  control(command, value) {
    const task = this.queue.then(async () => {
      const result = command === 'seek'
        ? await this.bridge.request('player-seek', { seconds: value })
        : await this.bridge.request('player-control', { command });
      this.nextRefresh = 0;
      return result;
    });
    this.queue = task.catch(() => {});
    return task;
  }
  command(event, context, payload) { this.send({ event, context, ...(payload === undefined ? {} : { payload }) }); }
  draw() {
    for (const [context, action] of this.actions) {
      const svg = Object.hasOwn(PLAYER_INFO, action.command) ? playerInfoSvg(action.command, this.state)
        : playerKeySvg(action.command, { ...this.state.buttons?.[action.command], playing: this.state.playing, error: !!this.error });
      if (this.images.get(context) === svg) continue;
      this.images.set(context, svg);
      this.command('setImage', context, { image: dataUri(svg), target: 0 });
    }
  }
  inspector(context) {
    const action = this.actions.get(context);
    if (action) this.command('sendToPropertyInspector', context, { label: playerActions[action.command],
      info: Object.hasOwn(PLAYER_INFO, action.command),
      status: this.error ?? (Object.hasOwn(PLAYER_INFO, action.command)
        ? (action.command === 'cover' && !this.state.cover ? 'Обложка пока недоступна. ' + (this.state.coverError ?? '') : [this.state.artist, this.state.title].filter(Boolean).join(' — ') || 'Ожидание данных трека.')
        : this.state.buttons?.[action.command]?.enabled === false ? 'Кнопка сейчас недоступна в приложении.' : 'Управление окном Яндекс Музыки через UI Automation.') });
  }
  remove(context) { this.actions.delete(context); this.images.delete(context); this.pressed.delete(context); }
  async handle(ev) {
    const { event, context } = ev;
    if (event === 'deviceDidDisconnect') {
      for (const [id, action] of this.actions) if (action.device === ev.device) this.remove(id);
      return false;
    }
    const command = ev.action?.startsWith(PLAYER_PREFIX) ? ev.action.slice(PLAYER_PREFIX.length) : null;
    if (!Object.hasOwn(playerActions, command) && !this.actions.has(context)) return false;
    if (event === 'willAppear') {
      if (ev.payload?.isInMultiAction) return true;
      this.actions.set(context, { command, device: ev.device });
      this.command('setTitle', context, { title: '', target: 0 }); this.draw(); this.nextRefresh = 0;
    } else if (event === 'willDisappear') this.remove(context);
    else if (event === 'propertyInspectorDidAppear' || event === 'sendToPlugin') this.inspector(context);
    else if (event === 'keyDown' && this.actions.has(context) && !Object.hasOwn(PLAYER_INFO, this.actions.get(context).command)) this.pressed.add(context);
    else if (event === 'keyUp') {
      const pressed = this.pressed.delete(context), action = this.actions.get(context);
      if (!pressed || !action || this.pending.has(context)) return true;
      this.pending.add(context);
      try {
        await this.control(action.command);
        this.error = null; this.nextRefresh = 0;
      } catch (error) {
        this.error = error.message; this.log(error.message);
        if (this.actions.get(context) === action) { this.command('showAlert', context); this.inspector(context); }
      } finally { this.pending.delete(context); this.draw(); }
    }
    return true;
  }
  async refresh() {
    if (this.busy || !this.actions.size || this.now() < this.nextRefresh) return;
    this.busy = true;
    try {
      const { result } = await this.bridge.request('player-state');
      this.state = result; this.error = null;
    } catch (error) { this.state = {}; this.error = error.message; }
    finally { this.nextRefresh = this.now() + 1000; this.busy = false; this.draw(); }
  }
}
