# Contributing to ObdGarage

Thanks for taking the time. ObdGarage is a single-maintainer project, so the process is
deliberately small - but it is the same for every change, including the maintainer's own.

## How changes get in

1. Open an issue first for anything bigger than a typo or an obvious bug fix, so the direction can
   be agreed before you spend time on it. Use the templates under `.github/ISSUE_TEMPLATE/`.
2. Fork the repository (or branch, if you have write access) and make your change on a branch.
3. Open a pull request against `main`. The pull-request template asks for what changed and why.
4. `main` is protected: a PR merges only after the test stage of
   [`.github/workflows/ci-cd.yml`](.github/workflows/ci-cd.yml) is green and the branch is up to
   date with `main` (enable auto-merge and it lands on its own once that is the case). Nobody
   pushes to `main` directly, not even the maintainer.

## What a pull request needs

- **Conventional Commits.** The version and the changelog are generated from the commit messages
  (`feat:` = minor release, `fix:` = patch release, `build:`/`ci:`/`docs:`/`test:` = no release).
  Squash-merge keeps the PR title as the commit message, so give the PR a Conventional Commit
  title.
- **Green required checks.** `test-core`, `test-e2e`, `coverage`, `test-lint`, `build` and
  `review / dependency-review` are required; a red one blocks the merge.
- **Tests for new functionality.** There are two suites and both are required:
  `tests/ObdGarage.Tests` (xUnit) and `tools/ObdGarage.TestRunner`, a dependency-free console
  runner that drives the end-to-end scenarios against the vehicle simulator including sync
  roundtrips. New features and bug fixes come with tests in whichever suite fits; a PR that adds
  behaviour without one is asked to add it. The coverage badge is regenerated from the merged
  report of both suites and is expected not to drop.
- **Formatting and warnings.** `dotnet format ObdGarage.slnx --verify-no-changes` runs as the
  required `test-lint` check; run `dotnet format ObdGarage.slnx` before pushing. Warnings are
  errors repo-wide (`TreatWarningsAsErrors` in `Directory.Build.props`) - do not silence one
  without saying why in the PR.
- **Lock files.** Projects carry a `packages.lock.json` and `build` restores with `--locked-mode`,
  so a csproj that disagrees with its lock file fails the restore instead of silently updating it.
  A plain `dotnet restore ObdGarage.slnx` refreshes them locally after a package change - commit
  the result.
- **Read-only OBD access.** The ELM327 connection is deliberately read-only. A change that would
  write to the vehicle bus is out of scope for this project.

## Running things locally

Requires the .NET 10 SDK. Restore the local tools first (`coverlet.console` and
`reportgenerator`, pinned in `.config/dotnet-tools.json`) if you want coverage:

```bash
dotnet tool restore
```

Then:

```bash
# 1. Full end-to-end suite (including the vehicle simulator + sync roundtrips)
dotnet run --project tools/ObdGarage.TestRunner

# 2. Start the backend (invite code defaults to OBDGARAGE-2026)
ASPNETCORE_URLS=http://0.0.0.0:5299 dotnet run --project src/ObdGarage.Server --no-launch-profile

# 3. Start the web app and open it in a browser
ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet run --project src/ObdGarage.Web --no-launch-profile
```

`src/ObdGarage.Web/launchSettings.json` overrides `ASPNETCORE_URLS`, so `--no-launch-profile` is
required for those two commands to bind where you asked.

The remaining commands CI runs:

```bash
dotnet test tests/ObdGarage.Tests/ObdGarage.Tests.csproj --configuration Release
dotnet format ObdGarage.slnx --verify-no-changes
dotnet restore ObdGarage.slnx --locked-mode
dotnet build ObdGarage.slnx --configuration Release --no-restore
```

The MAUI app (`src/ObdGarage.App`) is not part of `ObdGarage.slnx` and is not built by CI.

## Security issues

Please do not open a public issue for a vulnerability - use the private reporting path described
in [SECURITY.md](SECURITY.md). The [Code of Conduct](CODE_OF_CONDUCT.md) applies to every
interaction in this repository.
