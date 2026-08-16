#!/bin/sh
# Capture-container entrypoint: the proprietary sdrplay_apiService must be running before the
# daemon opens the API (it owns the USB device; our daemon is just its client).
/usr/local/bin/sdrplay_apiService &
sleep 2
cd /app/capture
exec dotnet /app/capture/SignalScribe.Capture.dll
