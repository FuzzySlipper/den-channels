# UI Branch Hygiene (pre-extraction)

Until the Den Web UI (ClientApp) is extracted into its own repo (`den-web`), the ClientApp lives inside
`den-channels/src/DenChannels.Service/ClientApp`. This doc describes the branch conventions that prevent
accidental disruption during the cohabitation period.

## Starting point

Always branch from current `origin/main`:

```bash
git fetch origin main
git checkout -b task/NNNN-my-change origin/main
```

## Before requesting review or promotion

1. **Rebase or merge `main`** — ensure your branch is current with `origin/main` so you never
   accidentally drop newer UI files, features, or fixes that arrived while you were working.

2. **Run the frontend verification suite**:
   ```bash
   cd src/DenChannels.Service/ClientApp
   npm test          # runs all Vitest tests
   npm run build     # TypeScript compilation + Vite bundle
   npm run lint      # ESLint
   git diff --check  # no whitespace errors
   ```

3. **Live asset smoke evidence** — for any visible UI change, include an assertion that the
   Vite/TypeScript build was clean and the relevant component renders (screenshots, `curl` output
   from a dev server, or a clear description of the manual smoke test performed).

## What not to do

- **Do not promote branches that drop newer UI files**. If your branch was based on an older
  `main`, rebasing or merging `main` before promotion is required. A promotion from a stale base
  would effectively delete any ClientApp files added between your base and `HEAD` of `main`.

- **Do not merge `main` into a UI branch without first running `npm test` and `npm run build`**
  on the merged state.

- **Do not skip frontend tests** for UI changes. Even small CSS-only changes should at minimum
  pass `npm run build` and `npm test`.

## Keeping tests honest

- Source-string tests (reading `.tsx`/`.ts` files and asserting string content) are acceptable
  regression markers for high-risk invariants (document discussion separation, dirty-switch guard,
  viewport-relative presets, channel selection fallback). Supplement them with pure-function tests
  or behavioral tests where practical.

- New pure functions that manage UI invariants should be exported and tested directly rather than
  tested via source-string assertions.

## Review checklist

Before marking a UI branch as ready:

- [ ] `npm test` passes (all Vitest tests)
- [ ] `npm run build` passes (tsc + vite)
- [ ] `npm run lint` passes (ESLint)
- [ ] `git diff --check` is clean
- [ ] Branch is based on current `origin/main` or `main` has been merged/rebased in
- [ ] Visible UI changes include live asset smoke evidence
