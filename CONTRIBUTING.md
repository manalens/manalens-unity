# Contributing

This repo is where the Manalens Unity SDK is developed. Open issues and pull requests here.

## Test

You need the .NET 11 SDK and Node 20 or later. Unity is not needed.

```bash
dotnet test 'Tests~/Manalens.Core.Tests'
dotnet build 'Tests~/UnityStubs' --nologo -v q
node 'Tests~/tools/generate-meta.mjs' --check
```

1. The core tests (`Runtime/Core` on .NET). They also write the shared fixture scenario to `Tests~/fixtures/request-*.json`. Review the diff when a request body changes.
2. A compile check of the Unity layer and the samples against UnityEngine stand-ins.
3. Every file that Unity imports has its `.meta` file. Run the tool without `--check` to write the missing ones, and commit them.

CI (`.github/workflows/ci.yml`) runs the same three steps on each pull request.

## Release

1. In a pull request, set the new version in `package.json` and put a `## X.Y.Z (YYYY-MM-DD)` section at the top of `CHANGELOG.md` (rename `## Unreleased` if it is there). CI checks that the two are the same. Between releases, CI allows a `## Unreleased` section above the newest version; the release workflow does not.
2. Merge it to `main`.
3. Tag that commit and push the tag:

   ```bash
   git tag vX.Y.Z && git push origin vX.Y.Z
   ```

The release workflow (`.github/workflows/release.yml`) checks the tag, runs the tests, builds the package tarball, `SHA256SUMS`, and the provenance attestation, and makes the GitHub release. Users install a version with `https://github.com/manalens/manalens-unity.git#vX.Y.Z`.

## The delivery contract

The SDK sends app events to the Manalens collector (`POST /v0/events`). The contract (the request shape, the retry and batch defaults, and the shared fixture scenario) is `sdks/SPEC.md` in the private Manalens monorepo, next to the collector. The monorepo pins a commit of this repo in `sdks.lock.json`. Its tests check the fixtures and the source of the pinned commit against the collector schema, and run the SDK end to end against a local collector and Tinybird.

So a change that also needs a collector change takes two pull requests:

1. This repo first. Merge it to `main`.
2. Then the monorepo: bump the pin (`pnpm sdks:bump <engine>`) together with the collector change.

A change to the request body changes the fixtures. Write them again (see Test), review the diff, and commit them with the change.

## Commits

This repo is public, and its history shows each commit's author and committer.

- Use your GitHub noreply email (`<id>+<user>@users.noreply.github.com`) for both. Set it with `git config user.email` in this clone.
- Do not add AI attribution lines (`Co-Authored-By: Claude ...`, `Claude-Session: ...`, or "Generated with Claude Code") to commit messages or pull request descriptions.
- Merge after CI passes. Use a fast-forward push of the reviewed branch (`git push origin <branch>:main`), or a squash merge on GitHub. Both keep noreply emails in the history.
