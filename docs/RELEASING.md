# Releasing Dnstap.NET to GitHub Packages

Dnstap.NET is published under the NuGet package ID `Dnstap.NET` to the DeltaZulu-OU GitHub Packages feed. Publishing is manual and runs **only from main**. No push, pull request or CI test publishes an artifact.

## Prepare a version

Update the single `<Version>` property in repository-root `Directory.Build.props`. The initial prerelease version is `0.1.0-preview.1`. Each new release must have a higher SemVer precedence than every existing `v*` release tag, excluding identical same-commit tags used for recovery. Pre-release numeric identifiers have standard SemVer ordering. No build metadata suffixes are supported by this publishing policy.

Merge the version change into `main` and verify the normal **Protocol specifications** workflow passes. The normal workflow checks package creation and metadata, then restores and executes a standalone consumer using the newly built local NuGet archive, without a project reference to the library. It does not publish.

## Publish

In GitHub Actions, open **Publish NuGet package**, select **Run workflow**, and choose the `main` branch.

The workflow first checks the version and release tags, then invokes the existing protocol specification workflow as a reusable gate (MSTest, pinned Go reader, and pinned DNS-collector tests). The test job packs the .NET 10 library, inspects the NuGet metadata, restores it using a standalone consumer, and uploads the **tested** `.nupkg` together with a SHA-256 manifest as the `validated-nuget-package` workflow artifact. The publishing job downloads this exact artifact and verifies its digest. It does not build another NuGet archive.

Before publication, it verifies the NuGet archive contains the `net10.0` library, readme, GPL-3.0-only license metadata, and the required Google.Protobuf dependency; Grpc.Tools must not appear as a runtime dependency.

The final steps push the tested package to `https://nuget.pkg.github.com/DeltaZulu-OU/index.json` using the Actions `GITHUB_TOKEN`. An isolated NuGet consumer then restores directly from the authenticated registry, with an **exact-ID `Dnstap.NET` source mapping to GitHub Packages** and a wildcard mapping of other dependencies to nuget.org. It compares the downloaded `.nupkg` SHA-256 against the tested archive and exercises the public API. The candidate's NuGet repository metadata also records the full tested Git commit, which is checked against `GITHUB_SHA` in CI. Only after this verification does the workflow create an annotated `v<Version>` tag and reconcile the GitHub Release asset. The workflow does **not** publish to nuget.org.

The GitHub Actions token must have permission to write repository contents and organization packages. Organization policy may impose additional package publication restrictions.

## Failure handling

Publication involves separate remote operations, not an atomic transaction. The package is pushed **before** the tag, minimizing the risk of an apparently released tag with no package. If the push reports a duplicate, the workflow **does not** accept it as proof of success: a clean, authenticated registry restore must retrieve a package whose SHA-256 is identical to the validated release candidate. Any mismatch fails before tag creation. Re-running the same version on the **same commit** is supported when the registry package is identical. The GitHub Release retains both the tested `.nupkg` and its `SHA256SUMS` digest manifest; existing assets are downloaded and byte-compared, missing assets are uploaded, and conflicting assets fail the workflow. Existing drafts and unexpected GitHub API responses also fail closed.

Do not modify the code or package contents under an already published version. If an intermediate operation fails, inspect GitHub Packages, the tag and the Release before retrying. A retry executes the protocol suite again on the selected commit and regenerates its validated package artifact. If regeneration is not byte-identical to the already published package, the workflow deliberately fails rather than replacing published bytes; this may require explicit manual investigation and recovery. GitHub Actions artifacts are subject to retention, so workflow artifacts alone are not a permanent archive. The workflow does not override conflicting tags or change previously published packages. The integrity check prevents conflicting **package bytes** from being accepted across retries, even when the initial package push happened before any release tag existed. The candidate embeds its tested Git commit as NuGet repository metadata. Consequently, a package genuinely rebuilt for a different commit should differ and be rejected rather than matched to the earlier publication.


## Package source mapping

The CI package-consumer test pins the exact package ID `Dnstap.NET` to the freshly packed **local source**. Its runtime and transitive dependencies resolve from nuget.org. The publish workflow instead pins `Dnstap.NET` to the authenticated **DeltaZulu-OU GitHub Packages feed**. These mappings are generated by `.github/scripts/configure-nuget-sources.py`; the release verification never accepts a same-ID package from nuget.org. NuGet gives exact package-ID patterns precedence over the `*` wildcard.

Consumers who configure both feeds should use the equivalent source mapping:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="github" value="https://nuget.pkg.github.com/DeltaZulu-OU/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="github">
      <package pattern="Dnstap.NET" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Use a credential provider or an appropriate protected NuGet credential configuration for the GitHub feed. Do not commit tokens to source control. NuGet's package source mapping controls restores; it cannot ensure that package metadata discovery commands never query other configured feeds.

The availability or ownership of the `Dnstap.NET` ID on nuget.org has not been established; reserving that name may reduce confusion but is not a substitute for explicit mapping. The current publishing workflow targets GitHub Packages only.

## NuGet consumers

GitHub Packages requires authentication where applicable. Configure the organization feed as a NuGet package source and install `Dnstap.NET` using an authorized token. See [GitHub's NuGet registry documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

The repository commits `packages.lock.json` for `src/Dnstap`, `tests/Dnstap.Tests`, `Tapper`, and `benchmarks/Dnstap.Benchmarks`, using `RestorePackagesWithLockFile=true` in `Directory.Build.props`. Both regular CI and the publishing workflow enforce `dotnet restore --locked-mode`, matching the dependency-graph reproducibility approach used by DeltaZulu.Kql. When dependencies change, regenerate and review the affected lock files deliberately before merging. Package locks pin the restored dependency graph and hashes; they do not make compilation or remote publication atomic.

`tests/PackageConsumer` deliberately disables lock files: its input is the freshly packed `Dnstap.NET` archive, whose content hash can change with each source commit while the prerelease version remains the same. This consumer still tests actual NuGet restoration rather than a source project reference, but is not part of the locked source/build graph.
