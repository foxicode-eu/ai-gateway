# Build context is `src/` (the directory containing Gateway.slnx) — see deploy/README.md for the exact
# `docker build` invocation. Only copies the two project files this image actually needs (Api + its Core
# dependency), not the whole solution, so unrelated projects (Management, Dashboard, test projects) don't bust
# Docker's layer cache on every unrelated change.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Api/Api.csproj Api/
COPY Core/Core.csproj Core/
RUN dotnet restore Api/Api.csproj

COPY Api/ Api/
COPY Core/ Core/
RUN dotnet publish Api/Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Container Apps (and most container platforms) expect a single listen port; ASPNETCORE_URLS overrides
# Properties/launchSettings.json's local-dev ports (5116/7251), which aren't meaningful inside a container.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Api.dll"]
