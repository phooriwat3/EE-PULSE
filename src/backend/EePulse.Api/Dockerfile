FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /source
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["global.json", "."]
COPY ["src/backend/EePulse.Contracts/EePulse.Contracts.csproj", "src/backend/EePulse.Contracts/"]
COPY ["src/backend/EePulse.Domain/EePulse.Domain.csproj", "src/backend/EePulse.Domain/"]
COPY ["src/backend/EePulse.Application/EePulse.Application.csproj", "src/backend/EePulse.Application/"]
COPY ["src/backend/EePulse.Infrastructure/EePulse.Infrastructure.csproj", "src/backend/EePulse.Infrastructure/"]
COPY ["src/backend/EePulse.Api/EePulse.Api.csproj", "src/backend/EePulse.Api/"]
RUN dotnet restore src/backend/EePulse.Api/EePulse.Api.csproj
COPY . .
RUN dotnet publish src/backend/EePulse.Api/EePulse.Api.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-alpine3.24 AS final
WORKDIR /app
RUN addgroup -S eepulse && adduser -S -G eepulse eepulse
COPY --from=build /app/publish .
USER eepulse
EXPOSE 8080
ENTRYPOINT ["dotnet", "EePulse.Api.dll"]
