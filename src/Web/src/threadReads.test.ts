import { afterEach, describe, expect, it, vi } from "vitest";
import { threadRead } from "./threadReads";
afterEach(() => vi.restoreAllMocks());

describe("bounded thread reads", () => {
  it("passes a twenty-second deadline to the request and releases the lane after timeout", async () => {
    const deadline = new AbortController();
    const timeout = vi.spyOn(AbortSignal, "timeout").mockReturnValueOnce(deadline.signal);
    const reading = threadRead(signal => new Promise<void>((_, reject) => {
      signal.addEventListener("abort", () => reject(signal.reason), { once: true });
    }));
    const rejected = expect(reading).rejects.toMatchObject({ name: "TimeoutError" });
    await Promise.resolve();
    deadline.abort(new DOMException("The reply read timed out.", "TimeoutError"));
    await rejected;
    expect(timeout).toHaveBeenCalledWith(20_000);
    expect(await threadRead(async () => "recovered")).toBe("recovered");
  });
  it("runs at most three reads and removes aborted queued work without issuing it", async () => {
    let active = 0;
    let maximum = 0;
    const release: (() => void)[] = [];
    const read = () => threadRead(async () => {
      maximum = Math.max(maximum, ++active);
      await new Promise<void>(resolve => release.push(resolve));
      active--;
    });
    const first = [read(), read(), read()];
    await vi.waitFor(() => expect(active).toBe(3));
    const controller = new AbortController();
    const unused = vi.fn(async () => {});
    const cancelled = threadRead(unused, controller.signal);
    const rejection = expect(cancelled).rejects.toMatchObject({ name: "AbortError" });
    const next = read();
    controller.abort();
    await rejection;
    expect(unused).not.toHaveBeenCalled();
    release.shift()!();
    await vi.waitFor(() => expect(release).toHaveLength(3));
    for (const complete of release) complete();
    await Promise.all([...first, next]);
    expect(maximum).toBe(3);
    expect(active).toBe(0);
    expect(await threadRead(async () => "ready")).toBe("ready");
  });
});
