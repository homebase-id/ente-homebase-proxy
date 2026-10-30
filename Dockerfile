# The Ente → Homebase proxy, serving the Ente Photos web app on the same site.
# Build the web app first, then the image, from the repo root:
#   scripts/build-web.sh
#   docker build -t ente-homebase-proxy .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/EnteHomebaseProxy/EnteHomebaseProxy.csproj src/EnteHomebaseProxy/
RUN dotnet restore src/EnteHomebaseProxy/EnteHomebaseProxy.csproj
COPY src/ src/
RUN dotnet publish src/EnteHomebaseProxy/EnteHomebaseProxy.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The web app is static files, the same on every platform: built once, outside Docker.
COPY client/photos client/photos
ENV ASPNETCORE_URLS=http://0.0.0.0:8095 \
    ALLOWED_ORIGINS=http://localhost:3020
EXPOSE 8095
USER $APP_UID
ENTRYPOINT ["dotnet", "EnteHomebaseProxy.dll"]
