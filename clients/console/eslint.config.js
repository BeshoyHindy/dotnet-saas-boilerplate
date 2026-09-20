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
      //   set-state-in-effect (29)  — effects that seed or reset state from
      //     props/queries. Each one has to be re-expressed as derived state
      //     or a key reset; doing that blind is how you introduce render
      //     loops.
      //   static-components (5)     — table/section components declared
      //     inside a parent's body on the big list pages; hoisting them means
      //     threading the closed-over props through by hand.
      //   refs (3)                  — use-inactivity-timeout.ts reads refs
      //     during render to build its timer state.
      //
      // Left at `warn` so they stay visible in output and in editors while
      // the work is scheduled, not silenced: no rule is turned off and no
      // `eslint-disable` comment is added anywhere in src/.
      'react-hooks/set-state-in-effect': 'warn',
      'react-hooks/static-components': 'warn',
      'react-hooks/refs': 'warn',
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      // Project-specific deviations from jsx-a11y/recommended:
      // - autofocus is intentionally used on confirmation dialogs (sign-out)
      //   where the destructive action should be the default focus, and on
      //   the command-palette search input which is its only purpose.
      'jsx-a11y/no-autofocus': 'off',
      // Promote from warn → error now that the four outstanding warnings
      // have been resolved (img onError is excluded explicitly below so
      // legitimate fallback handlers don't trip the rule).
      'jsx-a11y/no-noninteractive-element-interactions': [
        'error',
        {
          handlers: [
            'onClick',
            'onMouseDown',
            'onMouseUp',
            'onKeyPress',
            'onKeyDown',
            'onKeyUp',
          ],
        },
      ],
      'jsx-a11y/no-noninteractive-element-to-interactive-role': 'error',
      'jsx-a11y/no-aria-hidden-on-focusable': 'error',
      'jsx-a11y/anchor-has-content': 'error',
    },
  },
);
