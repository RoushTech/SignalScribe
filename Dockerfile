# One image, three services (api / workers / capture) — docker-compose picks the binary via `command`.

# ---- Vue build ----
FROM node:24-alpine AS vue-build
WORKDIR /src
COPY SignalScribe.Vue/package*.json ./
RUN npm install
COPY SignalScribe.Vue/ ./
RUN npm run build

# ---- .NET build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY SignalScribe/SignalScribe.csproj SignalScribe/
COPY SignalScribe.Api/SignalScribe.Api.csproj SignalScribe.Api/
COPY SignalScribe.Capture/SignalScribe.Capture.csproj SignalScribe.Capture/
COPY SignalScribe.Workers/SignalScribe.Workers.csproj SignalScribe.Workers/
RUN dotnet restore SignalScribe.Api/SignalScribe.Api.csproj \
    && dotnet restore SignalScribe.Capture/SignalScribe.Capture.csproj \
    && dotnet restore SignalScribe.Workers/SignalScribe.Workers.csproj
COPY SignalScribe/ SignalScribe/
COPY SignalScribe.Api/ SignalScribe.Api/
COPY SignalScribe.Capture/ SignalScribe.Capture/
COPY SignalScribe.Workers/ SignalScribe.Workers/
RUN dotnet publish SignalScribe.Api/SignalScribe.Api.csproj -c Release -o /app/api \
    && dotnet publish SignalScribe.Capture/SignalScribe.Capture.csproj -c Release -o /app/capture \
    && dotnet publish SignalScribe.Workers/SignalScribe.Workers.csproj -c Release -o /app/workers

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# libusb: sdrplay service; libgomp1: whisper.cpp/llama.cpp OpenMP runtime
RUN apt-get update && apt-get install -y --no-install-recommends libusb-1.0-0 libgomp1 procps \
    && rm -rf /var/lib/apt/lists/*

# Proprietary SDRplay API (populate vendor/sdrplay with scripts/fetch-sdrplay-api.sh before building).
# The capture service starts sdrplay_apiService in its entrypoint; api/workers ignore it.
ARG TARGETARCH
COPY vendor/sdrplay/ /tmp/sdrplay/
RUN ARCH_DIR=$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo amd64) \
    && install -m 644 /tmp/sdrplay/$ARCH_DIR/libsdrplay_api.so.3.15 /usr/local/lib/ \
    && ln -s /usr/local/lib/libsdrplay_api.so.3.15 /usr/local/lib/libsdrplay_api.so.3 \
    && cp /tmp/sdrplay/$ARCH_DIR/sdrplay_apiService /usr/local/bin/ \
    && chmod +x /usr/local/bin/sdrplay_apiService \
    && ldconfig \
    && rm -rf /tmp/sdrplay

WORKDIR /app
COPY --from=dotnet-build /app ./
COPY --from=vue-build /src/dist ./api/wwwroot
COPY docker/start-capture.sh /app/start-capture.sh
RUN chmod +x /app/start-capture.sh
# Models are NOT baked in — mount the models volume (scripts/download-models.sh).
EXPOSE 5020
CMD ["dotnet", "/app/api/SignalScribe.Api.dll"]
