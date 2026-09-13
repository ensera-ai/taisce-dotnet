#!/usr/bin/env bash
# Copyright 2026 The Taisce Authors
# SPDX-License-Identifier: Apache-2.0
#
# Verifies a published release the way a consumer would: it downloads each package from nuget.org
# and proves two separate things about it. SECURITY.md explains what each proves and what neither
# does. Exits non-zero if any check fails for any package.
#
#   scripts/verify-release.sh 0.1.1                  # both packages
#   scripts/verify-release.sh 0.1.1 Taisce.Client    # one package
#
# Needs curl, zip, the .NET SDK (`dotnet nuget verify`) and an authenticated `gh`, because the
# attestation lookup goes through the GitHub API.
#
# ── WHY THE SIGNATURE FILE IS REMOVED BEFORE THE ATTESTATION CHECK ──────────────────────────────
#
# The attestation covers the package as release.yml built it. nuget.org adds its repository
# signature to every package it accepts, as a `.signature.p7s` entry, so the file it serves has a
# different digest. Removing that entry gives back the bytes the workflow attested. That was observed
# on every package published so far, with Info-ZIP's `zip -d`, and no specification promises it.
# If nuget.org ever rewrites more than the one entry, the attestation check fails here. A false pass
# is not possible, because a digest either matches an attestation or it does not.
#
# ── WHY IT ALSO TAMPERS WITH EACH PACKAGE ───────────────────────────────────────────────────────
#
# A verification that cannot fail proves nothing. Misused flags, or a tool that exits zero on a
# lookup error, would pass everything. So after both checks pass, each check is run once more on a
# copy with one byte appended, and that copy has to be refused.
set -euo pipefail

version="${1:?usage: scripts/verify-release.sh <version> [package ...]}"
shift
packages=("$@")
[ ${#packages[@]} -gt 0 ] || packages=(Taisce.Client Taisce.AgentFramework)

repo=ensera-ai/taisce-dotnet
workflow="$repo/.github/workflows/release.yml"
# The certificate nuget.org signs with rotates, so the subject is checked rather than a fingerprint.
# `dotnet nuget verify` has already validated its chain.
repository_subject='CN=NuGet.org Repository by Microsoft'

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failed=0

attest() { gh attestation verify "$1" --repo "$repo" --signer-workflow "$workflow" --source-ref "refs/tags/v$version" >/dev/null 2>&1; }
nuget_repository_signed() { dotnet nuget verify "$1" --all >"$work/verify.out" 2>&1 && grep -q "$repository_subject" "$work/verify.out"; }
fail() { echo "  FAIL: $1"; failed=1; }

for package in "${packages[@]}"; do
  id="$(printf '%s' "$package" | tr '[:upper:]' '[:lower:]')"
  served="$work/$id.$version.nupkg"
  echo "$package $version"
  if ! curl -fsSL -o "$served" "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg"; then
    fail "not downloadable from nuget.org"; continue
  fi

  # 1. nuget.org's repository signature: the file has not changed since nuget.org accepted it.
  if nuget_repository_signed "$served"; then
    echo "  ok: repository signature by NuGet.org is valid"
  else
    fail "repository signature did not verify"; sed 's/^/    /' "$work/verify.out"
  fi

  # 2. Provenance: the bytes were built by release.yml in this repository, from the tag v<version>.
  built="$work/$id.$version.built.nupkg"
  cp "$served" "$built"
  if ! zip -q -d "$built" .signature.p7s >/dev/null 2>&1; then
    fail "no .signature.p7s entry to remove; this is not the file nuget.org serves"; continue
  fi
  if attest "$built"; then
    echo "  ok: attestation verifies (signer $workflow, ref refs/tags/v$version)"
  else
    fail "no attestation from $workflow at refs/tags/v$version matches the package as built"
  fi

  # Refusal: both checks must reject a modified package.
  cp "$served" "$work/tampered-served.nupkg";  printf 'x' >> "$work/tampered-served.nupkg"
  cp "$built"  "$work/tampered-built.nupkg";   printf 'x' >> "$work/tampered-built.nupkg"
  if nuget_repository_signed "$work/tampered-served.nupkg"; then fail "repository signature check accepted a modified package"; else echo "  ok: repository signature check refuses a modified package"; fi
  if attest "$work/tampered-built.nupkg"; then fail "attestation check accepted a modified package"; else echo "  ok: attestation check refuses a modified package"; fi
done

[ "$failed" -eq 0 ] && echo "verified" || { echo "NOT verified"; exit 1; }
