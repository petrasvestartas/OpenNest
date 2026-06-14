#!/usr/bin/env bash
# OpenNest Python example — macOS / Linux. Installs compas_nest and runs.
set -e
cd "$(dirname "$0")"
python3 -m pip install --quiet --upgrade compas_nest compas_viewer
python3 main.py
