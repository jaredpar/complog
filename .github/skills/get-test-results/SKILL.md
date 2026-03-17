---
name: get-test-results
description: Use when user wants to get the test results or test failures for the current branch
---

# Skill Instructions

Use the following command to get the branch name

```bash
gh pr view --json headRefName -q .headRefName
```

Use the following command to get the workflow run id

```bash
gh run list -b <branch-name> -L 1 --json databaseId -q .[0].databaseId
```

To download the test results for the workflow run, use the following command

```bash
gh run download <workflow-run-id>--output <output-file-path> -p test-results-*
```

The test results file in trx format will be inside the downloaded zip file.
