import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

export class AudioBridge {
  constructor(executable, log = () => {}, args = []) {
    this.executable = executable;
    this.log = log;
    this.args = args;
    this.pending = new Map();
    this.sequence = 0;
  }
  start() {
    if (this.closed) throw new Error('AudioBridge is closed');
    if (this.child) return;
    const child = spawn(this.executable, this.args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    this.child = child;
    const fail = error => {
      if (this.child !== child) return;
      this.child = null;
      for (const request of this.pending.values()) { clearTimeout(request.timer); request.reject(error); }
      this.pending.clear();
    };
    child.on('error', fail);
    child.on('exit', code => fail(new Error(`AudioBridge exited (${code})`)));
    child.stdin.on('error', fail);
    child.stderr.on('data', data => this.log(String(data)));
    createInterface({ input: child.stdout }).on('line', line => {
      try {
        const message = JSON.parse(line), request = this.pending.get(message.id);
        if (!request) return;
        clearTimeout(request.timer);
        this.pending.delete(message.id);
        if (message.error) request.reject(new Error(message.error));
        else request.resolve(message);
      } catch (error) { this.log(`Invalid bridge output: ${error.message}`); }
    });
  }
  request(op, payload = {}) {
    this.start();
    const id = ++this.sequence;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error('AudioBridge request timed out'));
        // Restart a stuck helper rather than building an unbounded stdin queue.
        this.child?.kill();
      }, 8000);
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(JSON.stringify({ ...payload, op, id }) + '\n');
    });
  }
  close() {
    this.closed = true;
    // Stream Deck may replace the plugin directory immediately after its host exits.
    // EOF alone can leave a native scan holding the EXE open for another second.
    // There is no persistent state in the helper; terminate it promptly on host shutdown.
    this.child?.kill();
  }
}
