# Releasing Dnstap.NET to GitHub Packages

Dnstap.NET is published under the NuGet package ID `Dnstap.NET` to the DeltaZulu-OU GitHub Packages feed. Publishing is manual and runs **only from main**. No push, pull request or CI test publishes an artifact.

## Prepare a version

Update the single `<Version>` property in repository-root `Directory.Build.props`. The initial prerelease version is `0.1.0-preview.1`. Each new release must have a higher SemVer precedence than every existing `v*` release tag, excluding identical same-commit tags used for recovery. Pre-release numeric identifiers have standard SemVer ordering. No build metadata suffixes are supported by this publishing policy.

Merge the version change into `main` and verify the normal **Protocol specifications** workflow passes. The normal workflow checks package creation and metadata, then restores and executes a standalone consumer using the newly built local NuGet archive, without a project reference to the library. It does not publish.

## Publish

In GitHub Actions, open **Publish NuGet package**, select **Run workflow**, and choose the `main` branch.

The workflow first checks the version and release tags, then invokes the existing protocol specification workflow as a reusable gate (MSTest, pinned Go reader, and pinned DNS-collector tests). Only after all three jobs pass does it build and pack the .NET 10 library.

Before publication, it verifies the NuGet archive contains the `net10.0` library, readme, GPL-3.0-only license metadata, and the required Google.Protobuf dependency; Grpc.Tools must not appear as a runtime dependency.

The final steps push the package to `https://nuget.pkg.github.com/DeltaZulu-OU/index.json` using the Actions `GITHUB_TOKEN`, create an annotated `v<Version>` tag on the tested commit, and create a GitHub Release containing the `.nupkg`. The workflow does **not** publish to nuget.org.

The GitHub Actions token must have permission to write repository contents and organization packages. Organization policy may impose additional package publication restrictions.

## Failure handling

Publication involves separate remote operations, not an atomic transaction. The package is pushed **before** the tag, minimizing the risk of an apparently released tag with no package. If the package push succeeds but later operations fail, re-running the same version on the **same commit** is supported: duplicate package upload is skipped, the existing same-commit tag is accepted, and an existing GitHub Release is left unchanged.

Do not modify the code or package contents under an already published version. If an intermediate operation fails, inspect GitHub Packages, the tag and the Release before retrying. The workflow does not override conflicting tags or change previously published packages.

## NuGet consumers

GitHub Packages requires authentication where applicable. Configure the organization feed as a NuGet package source and install `Dnstap.NET` using an authorized token. See [GitHub's NuGet registry documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

The repository commits `packages.lock.json` for `src/Dnstap`, `tests/Dnstap.Tests`, `Tapper`, and `benchmarks/Dnstap.Benchmarks`, using `RestorePackagesWithLockFile=true` in `Directory.Build.props`. Both regular CI and the publishing workflow enforce `dotnet restore --locked-mode`, matching the dependency-graph reproducibility approach used by DeltaZulu.Kql. When dependencies change, regenerate and review the affected lock files deliberately before merging. Package locks pin the restored dependency graph and hashes; they do not make compilation or remote publication atomic.

`tests/PackageConsumer` deliberately disables lock files: its input is the freshly packed `Dnstap.NET` archive, whose content hash can change with each source commit while the prerelease version remains the same. This consumer still tests actual NuGet restoration rather than a source project reference, but is not part of the locked source/build graph.
