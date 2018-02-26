#!/bin/bash
set -e
if [ ! -d "packages" ]; then mono .paket/paket.exe restore; fi
mono packages/FAKE/tools/FAKE.exe scripts/build.fsx $@
