import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist', 'node_modules', '*.config.js'] },
  {
    extends: [...tseslint.configs.recommended],
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
      'jsx-a11y': jsxA11y,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      ...jsxA11y.configs.recommended.rules,
      // eslint-plugin-react-hooks 7 folds the React Compiler rules into
      // `recommended`: 14 rules on top of rules-of-hooks + exhaustive-deps,
      // all at `error`. Eleven of them already pass here and stay at `error`,
      // so they gate new code from now on. These three do not, and each one
      // is a real design change rather than a mechanical fix:
      //
      //   set-state-in-effect (15) — effects that seed or reset state from
      //     props/route/query. Each has to be re-expressed as derived state
      //     or a key reset; doing that blind is how you introduce render
      //     loops.
      //   use-memo (2)             — layout/sidebar.tsx and layout/mobile-nav.tsx
      //     pass `[granted.join(",")]` as a dependency list, an existing
      //     deliberate hack that already carries an exhaustive-deps
      //     suppression.
      //   refs (3)                 — use-inactivity-timeout.ts reads refs
      //     during render to build its timer state.
      //
      // Left at `warn` so they stay visible in output and in editors while
      // the work is scheduled, not silenced: no rule is turned off and no
      // new `eslint-disable` comment is added anywhere in src/. Matches the
      // dashboard's config, which hit the same three plus static-components.
      'react-hooks/set-state-in-effect': 'warn',
      'react-hooks/use-memo': 'warn',
      'react-hooks/refs': 'warn',
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      // autofocus is intentional on dialog search inputs (impersonate / add-members)
      // and the login email field — the first field IS the dialog's purpose.
      'jsx-a11y/no-autofocus': 'off',
      // The permission-editor checkbox labels nest their text one level deeper
      // than the rule's default search depth; the control + text are both
      // present (e.g. roles/detail.tsx), so allow the extra nesting level.
      'jsx-a11y/label-has-associated-control': ['error', { depth: 3 }],
    },
  },
);
