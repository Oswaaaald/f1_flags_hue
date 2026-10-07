FROM node:22-bookworm-slim AS ui
WORKDIR /web
COPY apps/web/package*.json ./
RUN npm ci --ignore-scripts
COPY apps/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props F1Hue.slnx ./
COPY src/ src/
COPY apps/host/ apps/host/
COPY --from=ui /web/dist/ apps/web/dist/
RUN dotnet restore apps/host/F1Hue.Host.csproj --locked-mode
RUN dotnet publish apps/host/F1Hue.Host.csproj -c Release --no-restore -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV F1_HUE_DATA_DIR=/data F1_HUE_LISTEN=0.0.0.0 F1_HUE_PORT=8080 DOTNET_EnableDiagnostics=0
RUN mkdir /data && chown app:app /data
COPY --from=build /out/ ./
USER app
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s CMD ["dotnet", "f1-hue.dll", "--health-check"]
ENTRYPOINT ["dotnet", "f1-hue.dll"]
