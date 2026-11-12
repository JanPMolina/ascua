# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Ascua.slnx ./
COPY src/ src/
RUN dotnet publish src/Ascua.Api/Ascua.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# Imagen chiseled: sin shell ni gestor de paquetes, corre como usuario no root (app, UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Ascua.Api.dll"]
