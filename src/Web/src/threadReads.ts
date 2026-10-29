let active = 0;
const waiting: (() => void)[] = [];

export async function threadRead<T>(read: (signal: AbortSignal) => Promise<T>, cancellation?: AbortSignal): Promise<T> {
  const deadline = AbortSignal.timeout(20_000);
  const signal = cancellation ? AbortSignal.any([cancellation, deadline]) : deadline;
  await new Promise<void>((resolve, reject) => {
    const abort = () => {
      const index = waiting.indexOf(start);
      if (index >= 0) waiting.splice(index, 1);
      reject(signal.reason ?? new DOMException("Read cancelled.", "AbortError"));
    };
    const start = () => {
      signal?.removeEventListener("abort", abort);
      if (signal?.aborted) { abort(); return; }
      active++; resolve();
    };
    if (signal?.aborted) { abort(); return; }
    if (active < 3) start();
    else { waiting.push(start); signal?.addEventListener("abort", abort, { once: true }); }
  });
  try { return await read(signal); }
  finally { active--; waiting.shift()?.(); }
}
