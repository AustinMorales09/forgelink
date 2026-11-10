FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY tools/ForgeLink.OtSimulator/ tools/ForgeLink.OtSimulator/
RUN dotnet publish tools/ForgeLink.OtSimulator/ForgeLink.OtSimulator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "ForgeLink.OtSimulator.dll"]
