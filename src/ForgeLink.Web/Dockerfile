# Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/ForgeLink.Web/ForgeLink.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Non-root user shipped with the .NET images.
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# No Docker HEALTHCHECK: the slim aspnet image ships without curl/wget. Orchestrators probe /health/live and
# /health/ready over HTTP instead (Kubernetes probes, Azure Container Apps, or tools/smoke/stack-smoke.sh in CI).
ENTRYPOINT ["dotnet", "ForgeLink.Web.dll"]
