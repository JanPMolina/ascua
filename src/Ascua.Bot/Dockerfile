# syntax=docker/dockerfile:1
# Bot de prueba (docker compose --profile bot up --build). No se despliega en Azure.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ascua.Bot/ src/Ascua.Bot/
RUN dotnet publish src/Ascua.Bot/Ascua.Bot.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Ascua.Bot.dll"]
