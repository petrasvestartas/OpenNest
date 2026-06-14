@echo off
REM OpenNest Python example — Windows. Installs compas_nest (the OpenNest engines on PyPI) and runs.
python -m pip install --quiet --upgrade compas_nest compas_viewer || exit /b 1
python main.py
