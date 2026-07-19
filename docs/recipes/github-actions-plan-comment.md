# Recipe: post the plan as a PR comment (GitHub Actions)

Every pull request that touches the schema gets its Schemorph plan — what would
change, at what risk, with lint warnings — as a comment, before anyone merges.
The workflow is self-contained: it rebuilds the *base branch's* schema in a
throwaway SQL Server service container and diffs the PR's files against it, so
the plan shows exactly what the PR changes. No secrets, no live database.

```yaml
name: schemorph-plan

on:
  pull_request:
    paths:
      - 'schema/**'
      - 'migrations/**'

permissions:
  pull-requests: write   # to post the comment

jobs:
  plan:
    runs-on: ubuntu-latest
    services:
      mssql:
        image: mcr.microsoft.com/mssql/server:2022-latest
        env:
          ACCEPT_EULA: 'Y'
          MSSQL_SA_PASSWORD: 'Throwaway!Passw0rd'   # ephemeral container, never a real credential
        ports:
          - 1433:1433
        options: >-
          --health-cmd "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Throwaway!Passw0rd' -C -Q 'SELECT 1'"
          --health-interval 5s --health-timeout 3s --health-retries 20
    env:
      SCHEMORPH_URL: 'Server=localhost,1433;Database=plan_baseline;User Id=sa;Password=Throwaway!Passw0rd;TrustServerCertificate=True'
    steps:
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet tool install -g Schemorph   # pin with --version x.y.z for reproducible plans

      - name: Create the baseline database
        run: |
          /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Throwaway!Passw0rd' -C \
            -Q "CREATE DATABASE plan_baseline" || true

      # Baseline = the schema as the base branch has it.
      - uses: actions/checkout@v4
        with:
          ref: ${{ github.event.pull_request.base.sha }}
      - name: Apply the base branch schema
        run: schemorph apply --schema ./schema

      # The PR's desired state, diffed against that baseline.
      - uses: actions/checkout@v4
      - name: Compute the plan
        id: plan
        run: |
          set +e
          schemorph diff --schema ./schema --format text > plan.txt
          code=$?
          schemorph diff --schema ./schema --format json > plan.json
          set -e
          # diff exits 0 = no changes, 2 = changes pending, 1 = error
          if [ "$code" -eq 1 ]; then cat plan.txt; exit 1; fi
          echo "haschanges=$([ "$code" -eq 2 ] && echo true || echo false)" >> "$GITHUB_OUTPUT"

      # Optional policy gate: fail the job on specific lint codes
      # (SCHEMORPH1xx band, docs/errors.md) instead of just reporting them.
      - name: Enforce lint policy
        run: |
          blocked=$(jq -r '[.messages[] | select(.code == "SCHEMORPH101" or .code == "SCHEMORPH103")] | length' plan.json)
          if [ "$blocked" -gt 0 ]; then
            echo "Plan contains blocked lint findings:" && jq -r '.messages[] | select(.code | startswith("SCHEMORPH1")) | "\(.code): \(.text)"' plan.json
            exit 1
          fi

      - name: Post the plan as a PR comment
        uses: actions/github-script@v7
        with:
          script: |
            const fs = require('fs');
            const plan = fs.readFileSync('plan.txt', 'utf8');
            const marker = '<!-- schemorph-plan -->';
            const body = `${marker}\n### Schemorph plan\n\n\`\`\`\n${plan}\`\`\``;
            const { data: comments } = await github.rest.issues.listComments({
              ...context.repo, issue_number: context.issue.number, per_page: 100 });
            const existing = comments.find(c => c.body.startsWith(marker));
            if (existing) {
              await github.rest.issues.updateComment({ ...context.repo, comment_id: existing.id, body });
            } else {
              await github.rest.issues.createComment({ ...context.repo, issue_number: context.issue.number, body });
            }
```

## How it reads

- The comment carries the text plan — risk markers (`+` safe, `~` warning,
  `!` destructive), lint warnings, and the `--expect-plan <planHash>` line.
  Whoever applies later can use that exact fingerprint: if the schema moved
  since review, apply refuses (`plan_mismatch`) instead of executing a plan
  nobody saw.
- The comment is updated in place on each push (marker-based), so a PR has one
  living plan, not a trail of stale ones.
- A plan that never goes empty — the same change reappearing on every PR,
  including PRs that did not touch it — is usually not drift but an expression
  the engine does not round-trip; see [Limitations](../limitations.md) for the
  symptom and the fix.

## Variants

- **Diff against a live environment** instead of a rebuilt baseline: drop the
  service container and the two baseline steps, and point `SCHEMORPH_URL` at a
  repository secret for your dev/staging database. The plan then shows
  PR-vs-live drift (including changes that landed outside this PR).
- **Migrations in the plan**: pass `--migrations ./migrations` to `status`
  (or check them at apply time) — pending migration files are reported with
  their own lint warnings (`SCHEMORPH104`–`106`).
- **Stricter or looser policy**: the lint gate is one `jq` filter over
  `messages[].code` — pick which codes block ([errors.md](../errors.md)
  documents the band). Deleting the step keeps lint informational.

The workflow uploads no artifacts and needs no cache; the plan lives in the PR
comment. (Runner note: the `sqlcmd` path and health-check flags match the
`mssql/server:2022` image; adjust if you pin a different image.)
