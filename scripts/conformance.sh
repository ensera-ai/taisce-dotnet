#!/usr/bin/env bash
# Copyright 2026 The Taisce Authors
# SPDX-License-Identifier: Apache-2.0
#
# Runs the adapter conformance suite against a live deployment, with this adapter as the driver.
# Needs the `taisce` binary of the service (it carries the runner), the suite file from the service
# repository, and a write-enabled credential in TAISCE_TOKEN. Nothing here is mocked.
#
#   TAISCE_TOKEN=… TAISCE_API=https://memory.example TAISCE_CASES=../taisce/conformance/cases.json \
#     scripts/conformance.sh
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
: "${TAISCE_API:?the deployment address}"
: "${TAISCE_TOKEN:?a write-enabled credential}"
: "${TAISCE_CASES:?the path to conformance/cases.json from the service repository}"
taisce="${TAISCE_BIN:-taisce}"
dotnet build "$here/Taisce.Conformance" -c Release --nologo -v quiet
dll="$here/Taisce.Conformance/bin/Release/net10.0/Taisce.Conformance.dll"
exec "$taisce" conformance --api "$TAISCE_API" --cases "$TAISCE_CASES" --driver "dotnet $dll"
