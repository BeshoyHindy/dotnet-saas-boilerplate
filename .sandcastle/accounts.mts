// Claude accounts — which subscription a phase's `claude` command runs on.
//
// A run always has the MAIN account: the one whose token is
// `CLAUDE_CODE_OAUTH_TOKEN`. It may also have a FALLBACK account, off by
// default, that the usage-limit wait (usage-limit.mts) moves phases onto while
// the main one is limited, and moves them back from once it answers again.
//
// The switch is made by COMMAND PREFIX, never by agent env. In
// `@ai-hero/sandcastle` 0.12.0 a sandbox is created with an empty agent env,
// and the implementer and reviewer share one sandbox per issue that exists
// before any account is picked, so a token passed as agent env never reaches
// them: a "fallback" phase would silently run on the limited main token. Every
// phase command runs through `sh -c` (inside the container for the docker
// sandbox, on the host for the healer), so a `VAR="$OTHER" claude …` prefix
// applies to that one command, and `$OTHER` expands from the environment the
// command already has. Only the variable NAME is on the command line, never
// the secret.
//
// That environment is why the fallback token must be LISTED in
// `.sandcastle/.env`: the library forwards into every sandbox exactly the keys
// named in that file, with their value from the file or the host env. A token
// exported from the shell alone never reaches a sandbox.

/** One Claude subscription a phase can run on. */
export interface ClaudeAccount {
  /** For logs: `main` or `fallback`. */
  readonly name: string;
  /**
   * The variable holding this account's OAuth token, both on the host and
   * inside every sandbox. The main account's is the variable `claude` itself
   * reads, so its commands need no prefix.
   */
  readonly tokenVar: string;
}

/** The variable `claude` reads its OAuth token from. */
export const CLAUDE_TOKEN_VAR = "CLAUDE_CODE_OAUTH_TOKEN";

export const MAIN_ACCOUNT: ClaudeAccount = { name: "main", tokenVar: CLAUDE_TOKEN_VAR };

export const FALLBACK_ACCOUNT: ClaudeAccount = {
  name: "fallback",
  tokenVar: "CLAUDE_CODE_OAUTH_TOKEN_FALLBACK",
};

/** The part of an agent provider this module rewrites. */
export interface PrintCommandBuilder {
  // A method, so a provider with a narrower options type still fits.
  buildPrintCommand(options: never): { readonly command: string; readonly stdin?: string };
}

/**
 * The agent `provider` with every print command run on `account`.
 *
 * The main account is returned untouched. Any other account's command is
 * prefixed with `CLAUDE_CODE_OAUTH_TOKEN="$<its token var>"`, which the shell
 * running the command expands; the prompt stays on stdin.
 */
export function onAccount<P extends PrintCommandBuilder>(
  provider: P,
  account: ClaudeAccount,
): P {
  if (account.tokenVar === CLAUDE_TOKEN_VAR) {
    return provider;
  }
  if (!/^[A-Z_][A-Z0-9_]*$/.test(account.tokenVar)) {
    throw new Error(
      `The ${account.name} account's token variable must be an upper-case ` +
        `shell variable name, got ${JSON.stringify(account.tokenVar)}`,
    );
  }
  const build = provider.buildPrintCommand.bind(provider) as (
    options: unknown,
  ) => ReturnType<P["buildPrintCommand"]>;
  return {
    ...provider,
    buildPrintCommand(options: unknown) {
      const print = build(options);
      return {
        ...print,
        command: `${CLAUDE_TOKEN_VAR}="$${account.tokenVar}" ${print.command}`,
      };
    },
  } as P;
}
