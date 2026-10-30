# syntax=docker/dockerfile:1
# Generador de carga (NBomber). No se despliega: corre local o en CI contra la URL que se le pase.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY loadtests/Ascua.LoadTests/ loadtests/Ascua.LoadTests/
RUN dotnet publish loadtests/Ascua.LoadTests/Ascua.LoadTests.csproj -c Release -o /app /p:UseAppHost=false

# Sin USER: escribe los resultados en un volumen montado del host (/reports), cuyo dueño varía.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Ascua.LoadTests.dll"]
