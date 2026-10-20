import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

test("the CloudEvents URL exclusions affect only expected protocol metadata in one enabled RFI rule", () => {
  const rules = JSON.parse(readFileSync(new URL("../../infra/waf-managed-rules.json", import.meta.url), "utf8"));
  assert.deepEqual(rules, {
    managedRuleSets: [{
      ruleSetType: "Microsoft_DefaultRuleSet", ruleSetVersion: "2.1", ruleSetAction: "Block",
      ruleGroupOverrides: [{
        ruleGroupName: "RFI",
        rules: [{
          ruleId: "931130", enabledState: "Enabled",
          exclusions: ["claims.iss", "claims.aud", "headers.Origin"].map(selector => ({
            matchVariable: "RequestBodyJsonArgNames", selectorMatchOperator: "Equals", selector,
          })),
        }],
      }],
    }],
  });
});

test("both Azure deployment paths retain prevention and the same narrow protocol exclusion", () => {
  for (const [path, callbacksPath] of [
    ["../../infra/main.bicep", "../../infra/modules/workloads.bicep"],
    ["../../infra/live.bicep", "../../infra/live.bicep"],
  ]) {
    const source = readFileSync(new URL(path, import.meta.url), "utf8");
    assert.ok(source.includes("managedRules: loadJsonContent('waf-managed-rules.json')"));
    assert.match(source, /mode: 'Prevention', requestBodyCheck: 'Enabled'/);
    const callbacks = readFileSync(new URL(callbacksPath, import.meta.url), "utf8");
    assert.match(callbacks, /callbacks\/pubsub/);
  }
});

test("Development resolves the protected public origin before deploying Live workloads", () => {
  const platform = readFileSync(new URL("../../infra/main.bicep", import.meta.url), "utf8");
  const workloads = readFileSync(new URL("../../infra/modules/workloads.bicep", import.meta.url), "utf8");
  assert.match(platform, /output publicOrigin string = 'https:\/\/\$\{endpoint\.properties\.hostName\}'/);
  assert.match(workloads, /resource edge .* existing =/);
  assert.match(workloads, /resource endpoint .* existing =/);
});
