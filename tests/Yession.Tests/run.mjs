// Watchdog runner for the Pyxpecto test suite. Runs the Fable-compiled tests on Node with
// a hard timeout so a failed WebRTC connection (or any hang) can never block CI. Forwards
// Pyxpecto's exit code (0 = all passed); exits 124 on timeout.
import { spawn } from 'node:child_process';

const target = process.argv[2];
const timeoutMs = Number(process.argv[3] ?? 30000);

const child = spawn(process.execPath, [target], { stdio: 'inherit' });

const timer = setTimeout(() => {
  console.error(`runner: timed out after ${timeoutMs}ms — killing`);
  child.kill('SIGKILL');
  process.exit(124);
}, timeoutMs);

child.on('exit', (code, signal) => {
  clearTimeout(timer);
  if (signal) {
    console.error(`runner: child terminated by ${signal}`);
    process.exit(1);
  }
  process.exit(code ?? 1);
});
