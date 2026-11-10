FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ForgeLink.Contracts/ src/ForgeLink.Contracts/
COPY tools/ForgeLink.ErpSimulator/ tools/ForgeLink.ErpSimulator/
RUN dotnet publish tools/ForgeLink.ErpSimulator/ForgeLink.ErpSimulator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ForgeLink.ErpSimulator.dll"]
