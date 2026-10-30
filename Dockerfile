# The Ente → Homebase proxy. Build from proxy/:
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
ENV ASPNETCORE_URLS=http://0.0.0.0:8095 \
    ALLOWED_ORIGINS=http://localhost:3020
EXPOSE 8095
USER $APP_UID
ENTRYPOINT ["dotnet", "EnteHomebaseProxy.dll"]
