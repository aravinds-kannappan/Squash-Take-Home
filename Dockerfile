FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src ./src
RUN dotnet publish src/ControlPlane/ControlPlane.csproj -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=https://+:8443 Rmm__DataDir=/data
EXPOSE 8443
ENTRYPOINT ["dotnet", "ControlPlane.dll"]
