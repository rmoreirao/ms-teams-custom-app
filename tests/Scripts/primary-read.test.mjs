import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = readFileSync(new URL("../../infra/cosmos/read-current-v1.js", import.meta.url), "utf8");
function execute({ document, error, accepted = true, continuation, id = "member:actor", partition = "tenant|chat" } = {}) {
  let body;
  const calls = [];
  const collection = {
    getSelfLink: () => "dbs/db/colls/authority/",
    queryDocuments: (link, query, options, callback) => {
      calls.push({ link, query, options });
      if (accepted) callback(error, document ? [document] : [], { continuation });
      return accepted;
    },
  };
  const invoke = runInNewContext(`${source}\nreadCurrentV1;`, {
    getContext: () => ({
      getCollection: () => collection,
      getResponse: () => ({ setBody: value => { body = JSON.parse(JSON.stringify(value)); } }),
    }),
  });
  invoke(id, partition);
  return { body, calls };
}
test("the primary read returns exact document identity and ETag without any mutation API", () => {
  const document = { id: "member:actor", pk: "tenant|chat", _etag: "current-version", active: false };
  const result = execute({ document });
  assert.deepEqual(result.body, { version: 1, found: true, document, etag: "current-version" });
  assert.deepEqual(JSON.parse(JSON.stringify(result.calls)), [{
    link: "dbs/db/colls/authority/",
    query: { query: "SELECT TOP 1 * FROM c WHERE c.id = @id", parameters: [{ name: "@id", value: "member:actor" }] },
    options: { pageSize: 1 },
  }]);
});
test("a primary-confirmed absence is explicit and distinct from a failed stored procedure", () => {
  assert.deepEqual(execute().body, {
    version: 1, found: false, document: null, etag: null,
  });
});
test("source errors and exhausted execution budgets never become not-found success", () => {
  assert.throws(() => execute({ error: new Error("source unavailable") }), /source unavailable/);
  assert.throws(() => execute({ accepted: false }), /execution budget/);
  assert.throws(() => execute({ continuation: "remaining" }), /absence is not proven/);
  assert.throws(() => execute({ error: new Error("container not found") }), /container not found/);
});
test("wrong partition, wrong item and missing ETag fail closed", () => {
  for (const document of [
    { id: "member:actor", pk: "other", _etag: "version" },
    { id: "other", pk: "tenant|chat", _etag: "version" },
    { id: "member:actor", pk: "tenant|chat" },
  ]) assert.throws(() => execute({ document }), /identity or version mismatch/);
});
test("invalid and oversized primary point-read arguments are rejected", () => {
  for (const input of [{ id: "" }, { id: "x".repeat(1025) }, { partition: "" }, { partition: "x".repeat(2049) }])
    assert.throws(() => execute(input), /Invalid primary point-read arguments/);
});
test("Session provisioning retains critical current reads without item-write actions for authority readers", () => {
  const template = readFileSync(new URL("../../infra/live.bicep", import.meta.url), "utf8");
  const role = template.match(/resource primaryReadRole[\s\S]*?\n\}/)?.[0];
  assert.ok(role);
  assert.match(role, /containers\/executeStoredProcedure/);
  assert.match(role, /containers\/items\/read/);
  assert.doesNotMatch(role, /containers\/items\/(?:create|replace|upsert|delete|\*)/);
  assert.match(template, /loadTextContent\('cosmos\/read-current-v1\.js'\)/);
  assert.match(template, /parent: containers\[index\]/);
  const indexing = JSON.parse(readFileSync(new URL("../../infra/cosmos/live-indexing.json", import.meta.url), "utf8"));
  assert.ok(!indexing.includedPaths.some(entry => entry.path === "/id/?"), "Cosmos automatically indexes system id; an explicit override is rejected.");
});
