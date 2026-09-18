import assert from "node:assert/strict";
import test from "node:test";

import {
  dotnetBuildParser,
  dotnetTestParser,
  genericParser,
  LOG_PARSERS,
  resolveLogParser,
  type LogParserName,
} from "./log-parsers.mts";

// --- generic ---------------------------------------------------------------

test("the generic parser returns the last non-empty lines", () => {
  const content = Array.from({ length: 55 }, (_, i) => `line ${i + 1}`).join("\n");

  const excerpt = genericParser.excerpt(content);

  assert.equal(excerpt.length, 40);
  assert.equal(excerpt[0], "line 16");
  assert.equal(excerpt.at(-1), "line 55");
});

test("the generic parser drops blank lines and tolerates an empty log", () => {
  assert.deepEqual(genericParser.excerpt("a\n\n\nb\n"), ["a", "b"]);
  assert.deepEqual(genericParser.excerpt(""), []);
});

test("the generic parser briefs with the same tail it prints", () => {
  const content = "only line";
  assert.deepEqual(genericParser.failures(content), genericParser.excerpt(content));
});

// --- dotnetBuild -----------------------------------------------------------

test("the build parser prefers compiler and MSBuild diagnostics", () => {
  const content = [
    "Determining projects to restore...",
    "/repo/src/Acme.Api/Program.cs(12,5): error CS0103: The name 'foo' does not exist",
    "  chatter nobody needs",
    "/repo/src/Acme.slnf : error MSB4025: project file could not be loaded",
    "Build FAILED.",
    "",
  ].join("\n");

  assert.deepEqual(dotnetBuildParser.excerpt(content), [
    "/repo/src/Acme.Api/Program.cs(12,5): error CS0103: The name 'foo' does not exist",
    "/repo/src/Acme.slnf : error MSB4025: project file could not be loaded",
    "Build FAILED.",
  ]);
});

test("the build parser caps a mass failure instead of re-flooding the console", () => {
  const content = Array.from(
    { length: 300 },
    (_, i) => `    error CS0103: name ${i + 1}`,
  ).join("\n");

  const excerpt = dotnetBuildParser.excerpt(content);

  assert.equal(excerpt.length, 41);
  assert.match(excerpt[0]!, /name 1$/);
  assert.match(excerpt[39]!, /name 40$/);
  assert.match(excerpt[40]!, /260 more matching lines in the log/);
});

test("the build parser falls back to the tail when no marker matches", () => {
  const content = ["something", "went", "sideways"].join("\n");
  assert.deepEqual(dotnetBuildParser.excerpt(content), ["something", "went", "sideways"]);
});

// --- dotnetTest ------------------------------------------------------------

// A real `dotnet test` log, shape for shape: two failed tests, each with a
// message, an inner exception line, and a stack whose first frames name the
// test file. The healer is briefed with these blocks, not the whole log.
const SAMPLE_TEST_LOG = [
  "  Passed Integration.Tests.Tests.Foo.Bar [12 ms]",
  "[xUnit.net 00:01:34.02]     Integration.Tests.Tests.Orders.OrderTests.Reorder [FAIL]",
  "  Failed Integration.Tests.Tests.Orders.OrderTests.Reorder [141 ms]",
  "  Error Message:",
  "   System.Text.Json.JsonException : The JSON value could not be converted. Path: $.contains",
  "---- System.InvalidOperationException : Cannot get the value of a token type 'String' as a number.",
  "  Stack Trace:",
  "     at System.Text.Json.Utf8JsonReader.GetInt32()",
  "     at Integration.Tests.Tests.Orders.OrderTests.CreateAsync(HttpClient client) in /repo/src/Tests/Integration.Tests/Tests/Orders/OrderTests.cs:line 356",
  "     at Integration.Tests.Tests.Orders.OrderTests.Reorder() in /repo/src/Tests/Integration.Tests/Tests/Orders/OrderTests.cs:line 164",
  "   at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()",
  "--- End of stack trace from previous location ---",
  "",
  '  Failed Integration.Tests.Tests.Users.LifecycleTests.Decline_Should_409(action: "decline") [78 ms]',
  "  Error Message:",
  "   Shouldly.ShouldAssertException : response.StatusCode",
  "    should be",
  "HttpStatusCode.Conflict",
  "    but was",
  "HttpStatusCode.OK",
  "  Stack Trace:",
  "     at Integration.Tests.Tests.Users.LifecycleTests.Decline_Should_409(String action) in /repo/src/Tests/Integration.Tests/Tests/Users/LifecycleTests.cs:line 172",
  "",
  "Failed!  - Failed:     2, Passed:   886, Skipped:     0, Total:   888, Duration: 3 m 26 s",
].join("\n");

test("the test parser extracts one block per failed test with message and test-side frames", () => {
  const blocks = dotnetTestParser.failures(SAMPLE_TEST_LOG);

  assert.equal(blocks.length, 2);
  assert.match(
    blocks[0]!,
    /^Failed Integration\.Tests\.Tests\.Orders\.OrderTests\.Reorder \[141 ms\]/,
  );
  assert.match(blocks[0]!, /JsonException/);
  assert.match(blocks[0]!, /Cannot get the value of a token type/);
  assert.match(blocks[0]!, /OrderTests\.cs:line 356/);
  assert.match(blocks[0]!, /OrderTests\.cs:line 164/);
  // Async-machinery and framework frames without a source file are dropped.
  assert.doesNotMatch(blocks[0]!, /ExceptionDispatchInfo/);
  assert.doesNotMatch(blocks[0]!, /Utf8JsonReader/);
  assert.match(blocks[1]!, /Decline_Should_409\(action: "decline"\) \[78 ms\]/);
  assert.match(
    blocks[1]!,
    /should be\nHttpStatusCode\.Conflict\nbut was\nHttpStatusCode\.OK/,
  );
  assert.match(blocks[1]!, /LifecycleTests\.cs:line 172/);
});

test("the test parser falls back to the marker excerpt when no test failed (build error)", () => {
  const content = ["Build started", "error TESTERROR: something", "Failed!  - Failed: 0", ""].join(
    "\n",
  );

  assert.deepEqual(dotnetTestParser.failures(content), [
    "error TESTERROR: something",
    "Failed!  - Failed: 0",
  ]);
});

test("the test parser caps a mass failure and says how many more there are", () => {
  const content = Array.from({ length: 30 }, (_, i) =>
    [`  Failed T.Test${i} [1 ms]`, "  Error Message:", "   boom", ""].join("\n"),
  ).join("\n");

  const blocks = dotnetTestParser.failures(content);

  assert.equal(blocks.length, 26);
  assert.equal(blocks[25], "… 5 more failed tests in the log.");
});

test("the test parser's console excerpt stays summary-sized, not block-sized", () => {
  const excerpt = dotnetTestParser.excerpt(SAMPLE_TEST_LOG);

  assert.equal(excerpt.length, 1);
  assert.match(excerpt[0]!, /Failed!\s+- Failed:\s+2/);
});

test("the test parser returns nothing for an empty log", () => {
  assert.deepEqual(dotnetTestParser.failures(""), []);
});

// --- resolveLogParser ------------------------------------------------------

test("every declared parser name resolves to its parser", () => {
  for (const name of Object.keys(LOG_PARSERS) as LogParserName[]) {
    assert.equal(resolveLogParser(name), LOG_PARSERS[name]);
  }
});

// Fails SOFT, unlike the rest of the pipeline: a typo'd parser name must cost
// a less-targeted excerpt, never a stopped run — the full log path is printed
// beside the excerpt either way.
test("an unset or unknown parser name falls back to the generic parser", () => {
  assert.equal(resolveLogParser(undefined), genericParser);
  assert.equal(resolveLogParser("no-such-parser" as LogParserName), genericParser);
});
