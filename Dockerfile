FROM node:22-bookworm-slim AS web
WORKDIR /repo/src/Web
COPY src/Web/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /repo
COPY global.json Directory.Build.props NuGet.config TeamsReference.slnx ./
COPY src/Teams.Core/ src/Teams.Core/
COPY src/Teams.Infrastructure/ src/Teams.Infrastructure/
COPY src/Teams.Host/ src/Teams.Host/
COPY --from=web /repo/src/Teams.Host/wwwroot/ src/Teams.Host/wwwroot/
RUN dotnet restore src/Teams.Host --locked-mode --nologo && dotnet publish src/Teams.Host -c Release -o /out --no-restore --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=build /out/ .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Teams.Host.dll"]
