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
      // `recommended`, all at `error`, and every one of them gates here. The
      // three that used to be held at `warn` are named so nobody lowers them
      // again without meaning to:
      //
      //   set-state-in-effect — state that follows a prop or query is derived
      //     during render ("adjust state when a prop changes": remember the
      //     previous value in state and compare), or set in the event handler
      //     that caused the change. Never a synchronous setState in an effect.
      //   static-components   — a component is never created during render;
      //     pick an icon from a module-level map, not a function call.
      //   refs                — a ref is never read or written during render;
      //     mirror a prop into a ref from an effect.
      //
      // No rule is turned off and no `eslint-disable` for them exists in src/.
      'react-hooks/set-state-in-effect': 'error',
      'react-hooks/static-components': 'error',
      'react-hooks/refs': 'error',
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
