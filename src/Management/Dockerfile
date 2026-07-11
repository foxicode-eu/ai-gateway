# Build context is `src/` (the directory containing Gateway.slnx) — see deploy/README.md for the exact
# `docker build` invocation. Only copies the two project files this image actually needs (Management + its Core
# dependency), not the whole solution — see Api/Dockerfile's comment for why.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Management/Management.csproj Management/
COPY Core/Core.csproj Core/
RUN dotnet restore Management/Management.csproj

COPY Management/ Management/
COPY Core/ Core/
RUN dotnet publish Management/Management.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Management.dll"]
