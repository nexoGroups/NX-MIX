---
name: release
description: Publish a tested NX-MIX version to GitHub with the Velopack installer and update packages when the user invokes /release or requests an NX-MIX release.
---

# NX-MIX release

Work in the `nexoGroups/NX-MIX` repository. A request to run `/release` authorizes publishing the current intended changes for that run. Do not include private audio configuration, credentials, or unrelated working-tree changes.

1. Inspect `git status`, the latest GitHub release and tag, `.github/workflows/release.yml`, `tools/pack.ps1`, the Velopack package version, and the current update source. Choose the next semantic version from the actual release history and changes. Never reuse or move a published tag.
2. Review the intended diff and write `docs/release-notes/<version>.md` describing user-visible changes and material limits. Check that no personal REW/APO files were copied into the public repo.
3. Run the Release build, the repository's required tests, and the app self-test. Resolve failures before publishing. When relevant, render the changed UI and inspect it. Check `git diff --check` and the release workflow's version/asset naming.
4. Commit the reviewed changes. With Git CLI authentication, create an annotated `v<version>` tag on that exact commit and push the commit to `main` and the tag to `origin`. If the CLI is unavailable but the GitHub connector has write access, create one equivalent remote commit through its Git tree and commit tools, fast-forward `main`, then create the new `release/v<version>` branch at that exact commit. The workflow creates the tag and GitHub release through Velopack in this route. Never use both triggers for the same version. If `main` has advanced, reconcile it before updating; never force-push.
5. Watch the tagged workflow until it finishes. If it fails, inspect the job logs, fix the cause, and publish a new version/tag rather than replacing a published tag. Verify the GitHub release is public and contains `NxMix-win-Setup.exe`, `NxMix-<version>-full.nupkg`, `RELEASES`, and any other artifacts emitted by Velopack. Confirm `latest` points to the new stable release. Report the release and installer links, tests, and any remaining update verification that needs an installed Windows copy.

Use the GitHub connector for repository and workflow reads when available. Prefer the configured Git remote for an authenticated push; use the connector branch route when CLI authentication is absent. Do not declare success from a local build or a queued workflow alone. Stop publication if authentication or a required test cannot be completed, and state the exact blocker.
