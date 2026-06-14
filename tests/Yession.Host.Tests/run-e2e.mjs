// Watchdog runner: executes the Fable-compiled E2E with a hard timeout so a failed
// WebRTC connection can never hang. Exits non-zero if the E2E fails or times out.
import { spawn } from 'node:child_process';

const target = process.argv[2];
const timeoutMs = Number(process.argv[3] ?? 20000);

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
