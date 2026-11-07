// Standalone access to the Yandex Music audio session through AudioBridge.
// The mixer plugin keeps its own Controller; here we only need the session
// list plus volume/mute operations for a single EXE group.
export class YandexAudio {
  constructor(bridge, log = () => {}) {
    this.bridge = bridge;
    this.log = log;
    this.apps = [];
    this.queue = Promise.resolve();
  }
  operation(op, payload = {}) {
    const task = this.queue.then(async () => {
      try {
        const result = await this.bridge.request(op, payload);
        if (Array.isArray(result.apps)) this.apps = result.apps;
        return result;
      }
      catch (error) {
        // A failed scan must not leave a stale session targeting old volume/mute.
        if (op === 'list') this.apps = [];
        this.log(error.message);
        throw error;
      }
    });
    this.queue = task.catch(() => {});
    return this.queue;
  }
  refresh() { return this.operation('list'); }
}
