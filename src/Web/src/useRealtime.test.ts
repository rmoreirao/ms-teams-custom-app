import { describe, expect, it } from "vitest";
import { parseEvent, parseHint } from "./useRealtime";

describe("channel realtime contracts", () => {
  const operation = { type: "channel.operation.changed", schemaVersion: 1, sequence: 3, revision: 2,
    operationId: "11111111-2222-3333-4444-555555555555" };
  it("preserves existing conversation hints", () => {
    expect(parseEvent(JSON.stringify({ conversationId: "chat-one", revision: 9 }))).toEqual(parseHint({ conversationId: "chat-one", revision: 9 }));
  });
  it("accepts sequenced actor operations and revision-zero known-empty readiness", () => {
    expect(parseEvent(operation)).toMatchObject(operation);
    expect(parseEvent({ type: "channel.view.ready", schemaVersion: 1, sequence: 0, revision: 0,
      conversationId: "channel-one", parentId: "root", generation: 0 })).toMatchObject({
      conversationId: "channel-one", parentId: "root", revision: 0, generation: 0,
    });
  });
  it.each([
    { ...operation, schemaVersion: 2 },
    { ...operation, type: "unknown" },
    { ...operation, sequence: -1 },
    { ...operation, sequence: Number.MAX_SAFE_INTEGER + 1 },
    { ...operation, revision: 0.1 },
    { ...operation, operationId: "------------------------------------" },
    { ...operation, generation: "0" },
    { ...operation, type: "channel.catalog.changed", conversationId: "../other-tenant" },
    { ...operation, type: "channel.view.ready", conversationId: "channel-one", parentId: {} },
  ])("rejects malformed or unsupported evidence", value => {
    expect(() => parseEvent(value)).toThrow(/channel/);
  });
});
