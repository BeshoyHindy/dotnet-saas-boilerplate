import assert from "node:assert/strict";
import test from "node:test";

import { claudeCode } from "@ai-hero/sandcastle";

import { FALLBACK_ACCOUNT, MAIN_ACCOUNT, onAccount } from "./accounts.mts";

const SECRET = "fallback-token-value";
const print = { prompt: "the whole prompt", dangerouslySkipPermissions: true };

test("the main account's command is left untouched", () => {
  const provider = claudeCode("claude-model-x", { effort: "high" });

  const wrapped = onAccount(provider, MAIN_ACCOUNT);

  assert.equal(wrapped, provider);
  assert.deepEqual(wrapped.buildPrintCommand(print), provider.buildPrintCommand(print));
});

test("the fallback's prefix carries the variable name, never the secret, and the prompt stays on stdin", () => {
  const provider = claudeCode("claude-model-x", { effort: "high" });
  const plain = provider.buildPrintCommand(print);

  const wrapped = onAccount(provider, FALLBACK_ACCOUNT).buildPrintCommand(print);

  assert.equal(
    wrapped.command,
    `CLAUDE_CODE_OAUTH_TOKEN="$CLAUDE_CODE_OAUTH_TOKEN_FALLBACK" ${plain.command}`,
  );
  assert.equal(wrapped.stdin, "the whole prompt");
  assert.doesNotMatch(wrapped.command, new RegExp(SECRET));
  assert.doesNotMatch(wrapped.command, /the whole prompt/);
});

test("the rest of the provider is kept", () => {
  const provider = claudeCode("claude-model-x", { permissionMode: "bypassPermissions" });

  const wrapped = onAccount(provider, FALLBACK_ACCOUNT);

  assert.equal(wrapped.name, provider.name);
  assert.equal(wrapped.captureSessions, provider.captureSessions);
  assert.match(wrapped.buildPrintCommand(print).command, /--permission-mode bypassPermissions/);
});

// The prefix is expanded by the shell each sandbox runs the command through,
// so it must set the token for that one command from the forwarded variable.
test("the prefixed command runs on the fallback token in a shell", async () => {
  const { execFileSync } = await import("node:child_process");
  // `printenv` reads its own environment, as `claude` does.
  const echo = { buildPrintCommand: () => ({ command: "printenv CLAUDE_CODE_OAUTH_TOKEN" }) };

  const command = onAccount(echo, FALLBACK_ACCOUNT).buildPrintCommand().command;
  const out = execFileSync("sh", ["-c", command], {
    encoding: "utf8",
    env: {
      PATH: process.env.PATH,
      CLAUDE_CODE_OAUTH_TOKEN: "main-token",
      CLAUDE_CODE_OAUTH_TOKEN_FALLBACK: SECRET,
    },
  });

  assert.equal(out.trim(), SECRET);
});
