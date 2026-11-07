import { dataUri } from './render.mjs';

export const WINDOW = 'local.tux.yamusic.window';
export function dialRole(settings = {}, column = 0) {
  return ['seek', 'volume', 'none'].includes(settings.dialRole) ? settings.dialRole : column === 0 ? 'seek' : column === 3 ? 'volume' : 'none';
}
const escape = value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[c]);
export function captureOptions(settings = {}) {
  return { handle: typeof settings.handle === 'string' ? settings.handle : '',
    pid: Number.isInteger(settings.pid) ? settings.pid : 0,
    mode: ['fit', 'top', 'center', 'bottom'].includes(settings.mode) ? settings.mode : 'bottom',
    width: settings.span === false ? 200 : 800 };
}
export function formatTime(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) return '--:--';
  const s = Math.floor(seconds), m = Math.floor(s / 60);
  return `${m}:${String(s % 60).padStart(2, '0')}`;
}
export function frameSvg(image, width, column, media = null, age = 0, phase = 0) {
  const title = media?.title ? [media.artist, media.title].filter(Boolean).join(' — ') : 'Нет данных трека';
  const time = media ? `${formatTime(Number.isFinite(media.elapsed) ? Math.min(media.duration > 0 ? media.duration : Infinity, media.elapsed + (media.playing ? age : 0)) : NaN)} / ${media.duration > 0 ? formatTime(media.duration) : '--:--'}` : '--:-- / --:--';
  const textWidth = Math.max(100, Array.from(title).length * 9), gap = 60;
  const offset = (Math.max(0, phase) * 28) % (textWidth + gap);
  const single = width === 200;
  const timeWidth = Math.min(width - 12, Math.max(single ? 160 : 200, time.length * 12 + 20));
  const clipWidth = single ? width - 5 : width - timeWidth - 12;
  const barHeight = single ? 58 : 29;
  // Opaque timer plate also masks overflowing text on renderers with limited clipPath support.
  const timePlate = single ? '' : `<rect x="${clipWidth}" y="0" width="${width - clipWidth}" height="${barHeight}" fill="#000"/>`;
  let marquee = '';
  for (let x = 7 - offset; x < clipWidth + textWidth; x += textWidth + gap)
    marquee += `<text x="${x.toFixed(1)}" y="20" textLength="${textWidth}" lengthAdjust="spacingAndGlyphs">${escape(title)}</text>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="200" height="100" viewBox="${width === 800 ? column * 200 : 0} 0 200 100"><defs><clipPath id="track"><rect x="5" y="0" width="${clipWidth - 5}" height="29"/></clipPath></defs><image width="${width}" height="100" xlink:href="${escape(image)}"/><rect width="${width}" height="${barHeight}" fill="#000" fill-opacity="0.94"/><g clip-path="url(#track)" fill="white" font-family="sans-serif" font-size="17">${marquee}</g>${timePlate}<text x="${width - 8}" y="${single ? 50 : 22}" text-anchor="end" fill="#ffdd66" font-family="monospace" font-size="20" font-weight="bold"${time.length * 12 > timeWidth - 12 ? ` textLength="${timeWidth - 12}" lengthAdjust="spacingAndGlyphs"` : ''}>${escape(time)}</text></svg>`;
}
export function messageSvg(message) {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="200" height="100"><rect width="200" height="100" fill="#15151b"/><text x="100" y="30" text-anchor="middle" fill="#ffcc00" font-family="sans-serif" font-size="15">Окно приложения</text><text x="100" y="55" text-anchor="middle" fill="white" font-family="sans-serif" font-size="11">${escape(message)}</text><text x="100" y="77" text-anchor="middle" fill="#aaaaaa" font-family="sans-serif" font-size="10">Подробности в настройках</text></svg>`;
}

export class WindowCapture {
  constructor({ send, bridge, log = () => {}, now = Date.now, control = null }) {
    this.send = send; this.bridge = bridge; this.log = log;
    this.actions = new Map(); this.status = new Map(); this.images = new Map();
    this.frames = new Map(); this.now = now; this.started = now();
    this.control = control; this.down = new Map();
  }
  command(event, context, payload) { this.send({ event, context, payload }); }
  draw(context, svg) {
    if (this.images.get(context) === svg) return;
    this.images.set(context, svg);
    this.command('setFeedback', context, { canvas: dataUri(svg) });
  }
  async inspector(context) {
    const action = this.actions.get(context);
    if (!action) return;
    try {
      const { result } = await this.bridge.request('list');
      if (this.actions.get(context) !== action) return;
      this.command('sendToPropertyInspector', context, { windows: result.windows, status: this.status.get(context) ?? '', settings: action.settings, dialRole: dialRole(action.settings, action.column) });
    } catch (error) { this.command('sendToPropertyInspector', context, { status: error.message }); }
  }
  async handle(ev) {
    const { event, context, payload = {} } = ev;
    if (event === 'deviceDidDisconnect') {
      for (const [id, action] of this.actions) if (action.device === ev.device) this.remove(id);
      return false;
    }
    if (ev.action !== WINDOW && !this.actions.has(context)) return false;
    if (event === 'willAppear') {
      this.actions.set(context, { device: ev.device, column: Math.max(0, Math.min(3, payload.coordinates?.column ?? 0)), settings: payload.settings ?? {} });
      this.command('setFeedbackLayout', context, { layout: 'layouts/mixer.json' });
      this.draw(context, messageSvg('Ожидание снимка…'));
    } else if (event === 'willDisappear') this.remove(context);
    else if (event === 'didReceiveSettings') {
      const action = this.actions.get(context);
      if (action) { action.settings = payload.settings ?? {}; this.frames.delete(context); }
    } else if (event === 'propertyInspectorDidAppear' || event === 'sendToPlugin') await this.inspector(context);
    else if (event === 'dialDown') this.down.set(context, { rotated: false });
    else if (event === 'dialRotate') {
      const action = this.actions.get(context), press = this.down.get(context);
      if (press) press.rotated = true;
      if (!payload.pressed && Number.isInteger(payload.ticks) && payload.ticks && action) {
        const role = dialRole(action.settings, action.column);
        if (role !== 'none') await this.operate(context, role, Math.max(-60, Math.min(60, payload.ticks * (role === 'seek' ? 5 : 2))));
      }
    } else if (event === 'dialUp') {
      const press = this.down.get(context), action = this.actions.get(context);
      this.down.delete(context);
      if (press && !press.rotated && action) {
        const role = dialRole(action.settings, action.column);
        if (role !== 'none') await this.operate(context, role === 'seek' ? 'play' : 'mute');
      }
    }
    return true; // Touches on the screenshot are not desktop mouse clicks.
  }
  async operate(context, command, value) {
    if (!this.control) return;
    try { await this.control(command, value); }
    catch (error) {
      this.log(error.message); this.status.set(context, error.message);
      if (this.actions.has(context)) { this.command('showAlert', context, {}); this.command('sendToPropertyInspector', context, { status: error.message }); }
    }
  }
  remove(context) { this.actions.delete(context); this.status.delete(context); this.images.delete(context); this.frames.delete(context); this.down.delete(context); }
  animate() {
    const now = this.now();
    for (const [context, frame] of this.frames) {
      const action = this.actions.get(context);
      if (!action) continue;
      this.draw(context, frameSvg(frame.image, frame.width, action.column, frame.media, Math.max(0, now - frame.received) / 1000, (now - this.started) / 1000));
    }
  }
  async refresh() {
    if (this.busy || !this.actions.size) return;
    this.busy = true;
    try {
      const groups = new Map();
      for (const [context, action] of this.actions) {
        const options = captureOptions(action.settings), key = JSON.stringify(options);
        if (!groups.has(key)) groups.set(key, { options, targets: [] });
        groups.get(key).targets.push({ context, action });
      }
      for (const { options, targets } of groups.values()) {
        let result, error;
        try { ({ result } = await this.bridge.request('capture', options)); }
        catch (ex) { error = ex.message; }
        for (const { context, action } of targets) {
          if (this.actions.get(context) !== action || JSON.stringify(captureOptions(action.settings)) !== JSON.stringify(options)) continue;
          const status = error ?? [result.title, result.mediaError].filter(Boolean).join(' — ');
          if (this.status.get(context) !== status) {
            this.status.set(context, status);
            this.command('sendToPropertyInspector', context, { status });
          }
          if (error) { this.frames.delete(context); this.draw(context, messageSvg('Нет изображения')); }
          else this.frames.set(context, { ...result, width: options.width, received: this.now() });
        }
      }
      this.animate();
    } finally { this.busy = false; }
  }
}
