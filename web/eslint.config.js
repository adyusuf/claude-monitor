import js from "@eslint/js";
import globals from "globals";
import reactHooks from "eslint-plugin-react-hooks";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["dist", "coverage", "node_modules"] },
  {
    files: ["**/*.{ts,tsx}"],
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    languageOptions: { ecmaVersion: 2023, globals: globals.browser },
    plugins: { "react-hooks": reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // Triage (global #19): the pages load from the API in an effect and set state only after the response
      // arrives (an async callback), the pattern React documents for fetching. The rule cannot see the await.
      "react-hooks/set-state-in-effect": "off",
      "no-restricted-syntax": [
        "error",
        { selector: "CallExpression[callee.property.name='toLocaleDateString']", message: "Dates are dd/mm/yyyy through lib/format (global #12)." },
        { selector: "NewExpression[callee.object.name='Intl']", message: "No Intl date formatting: lib/format (global #12)." },
      ],
    },
  },
);
