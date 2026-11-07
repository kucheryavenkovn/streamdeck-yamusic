let ws, context, action, settings = {}, windows = [];
const $ = id => document.getElementById(id);
const send = (event, payload) => { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ event, action, context, payload })); };
function render() {
  const select = $('window');
  select.replaceChildren(new Option('Автоматически: Яндекс Музыка', ''));
  for (const w of windows) select.add(new Option(w.title, w.handle));
  if (settings.handle && !windows.some(w => w.handle === settings.handle)) select.add(new Option('Сохранённое окно (недоступно)', settings.handle));
  select.value = settings.handle || '';
  $('mode').value = settings.mode || 'bottom';
  $('span').value = String(settings.span !== false);
  $('dialRole').value = settings.dialRole || 'auto';
}
window.connectElgatoStreamDeckSocket = (port, uuid, registerEvent, info, actionInfo) => {
  const data = JSON.parse(actionInfo); context = data.context; action = data.action; settings = data.payload?.settings ?? {};
  render();
  ws = new WebSocket(`ws://127.0.0.1:${port}`);
  ws.onopen = () => { ws.send(JSON.stringify({ event: registerEvent, uuid })); send('sendToPlugin', { refresh: true }); };
  ws.onmessage = ({ data }) => {
    const message = JSON.parse(data), payload = message.payload ?? {};
    if (message.event === 'sendToPropertyInspector') {
      if (payload.settings) settings = payload.settings;
      if (payload.windows) { windows = payload.windows; render(); }
      if (payload.status !== undefined) $('status').textContent = payload.status;
    } else if (message.event === 'didReceiveSettings') { settings = payload.settings ?? {}; render(); }
  };
};
for (const id of ['window', 'mode', 'span', 'dialRole']) $(id).onchange = () => {
  const selected = windows.find(w => w.handle === $('window').value);
  settings = { ...settings, handle: $('window').value, pid: selected?.pid ?? settings.pid ?? 0, mode: $('mode').value, span: $('span').value === 'true', dialRole: $('dialRole').value };
  send('setSettings', settings);
};
$('refresh').onclick = () => send('sendToPlugin', { refresh: true });
