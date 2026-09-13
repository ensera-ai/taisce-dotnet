<!-- Copyright 2026 The Taisce Authors -->
<!-- SPDX-License-Identifier: Apache-2.0 -->

# Security

## Reporting a vulnerability

Report it privately through GitHub's
[private vulnerability reporting](https://github.com/ensera-ai/taisce-dotnet/security/advisories/new)
for this repository, not in a public issue.

## How a release is published

`Taisce.Client` and `Taisce.AgentFramework` reach nuget.org only through
`.github/workflows/release.yml`, run on a `v*.*.*` tag.

- **The job waits for approval.** It runs in the `nuget` environment, which admits only release tags
  and waits for a maintainer.
- **No publishing key is stored.** The job exchanges GitHub's short-lived identity token for a
  nuget.org key that is valid for about an hour, through nuget.org trusted publishing.
- **Each package is attested before it is pushed.** The job records a build provenance attestation
  for every `.nupkg` it builds.
- **Tags and actions are fixed.** Release tags cannot be moved or deleted, and every action is
  referenced by commit SHA.

## What a downloaded package can prove

A package from nuget.org carries two independent proofs, and each covers something the other does
not.

| Check | What it proves | What it does not prove |
|---|---|---|
| **nuget.org repository signature** | The file is unchanged since nuget.org accepted it. | Who uploaded it, or what it was built from. |
| **Build provenance attestation** | The package's contents were built by this repository's `release.yml`, from the tag `vX.Y.Z`. The check is anchored in GitHub's identity token and a public transparency log, not in anything this project holds. | That the code is safe. A compromised build step produces an attested package like any other. |

The attestation covers the package as built, but nuget.org adds its repository signature to every
package it accepts as a `.signature.p7s` entry, so the file you download has a different digest. To
check the attestation, remove that entry first; what remains is the file the workflow built.

## Verifying a release yourself

Run both checks for every package of a version:

```bash
scripts/verify-release.sh 0.1.1
```

The script also appends a byte to a copy of each package and requires both checks to refuse it, so
a check that passes everything is caught. The same steps by hand:

```bash
curl -fsSLO https://api.nuget.org/v3-flatcontainer/taisce.client/0.1.1/taisce.client.0.1.1.nupkg

# 1. nuget.org's repository signature
dotnet nuget verify taisce.client.0.1.1.nupkg --all

# 2. provenance: remove nuget.org's signature entry, then verify the attestation
cp taisce.client.0.1.1.nupkg built.nupkg
zip -d built.nupkg .signature.p7s
gh attestation verify built.nupkg \
  --repo ensera-ai/taisce-dotnet \
  --signer-workflow ensera-ai/taisce-dotnet/.github/workflows/release.yml \
  --source-ref refs/tags/v0.1.1
```

## Limits

- **Removing `.signature.p7s` reproducing the built bytes is observed, not specified.** It holds for
  every version published so far. If nuget.org ever changes more than that one entry, step 2 fails.
  It cannot pass by mistake: a digest either matches an attestation or it does not.
- **Approval is by the only maintainer.** It stops accidents and automation, not someone holding that
  maintainer's GitHub credentials.
- **An unlisted version is still downloadable.** A package pushed to nuget.org can be unlisted but not
  deleted, so a bad release is withdrawn by unlisting it and publishing a fixed version.
