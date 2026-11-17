FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so this layer is cached until a csproj changes
COPY global.json Directory.Build.props ./
COPY src/GameService.Api/GameService.Api.csproj src/GameService.Api/
COPY src/GameService.Service/GameService.Service.csproj src/GameService.Service/
COPY src/GameService.Repository/GameService.Repository.csproj src/GameService.Repository/
COPY src/GameService.Domain/GameService.Domain.csproj src/GameService.Domain/
RUN dotnet restore src/GameService.Api/GameService.Api.csproj

COPY src/ src/
RUN dotnet publish src/GameService.Api/GameService.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "GameService.Api.dll"]
