FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY tests/LiveHub.MockServer tests/LiveHub.MockServer
COPY Directory.Build.props global.json* ./
RUN dotnet publish tests/LiveHub.MockServer -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
EXPOSE 5055
ENTRYPOINT ["dotnet", "LiveHub.MockServer.dll"]
