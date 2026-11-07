window.connectElgatoStreamDeckSocket = (port, uuid, registerEvent, info, actionInfo) => {
  const action = JSON.parse(actionInfo), ws = new WebSocket(`ws://127.0.0.1:${port}`);
  ws.onopen = () => {
    ws.send(JSON.stringify({ event: registerEvent, uuid }));
    ws.send(JSON.stringify({ event: 'sendToPlugin', action: action.action, context: action.context, payload: {} }));
  };
  ws.onmessage = ({ data }) => {
    const message = JSON.parse(data);
    if (message.event !== 'sendToPropertyInspector') return;
    document.getElementById('label').textContent = message.payload.label ?? 'Яндекс Музыка';
    if (message.payload.info) document.getElementById('description').textContent = 'Информационная кнопка: обновляется при смене трека. Нажатие не управляет плеером. Обложка берётся из медиасессии Windows, исполнитель — из интерфейса Музыки. Изображения не скачиваются отдельно и не сохраняются на диск.';
    document.getElementById('status').textContent = message.payload.status ?? '';
  };
};
