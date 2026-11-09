FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/OrderSystem.Domain/OrderSystem.Domain.csproj src/OrderSystem.Domain/
COPY src/OrderSystem.Application/OrderSystem.Application.csproj src/OrderSystem.Application/
COPY src/OrderSystem.Infrastructure/OrderSystem.Infrastructure.csproj src/OrderSystem.Infrastructure/
COPY src/OrderSystem.Migrator/OrderSystem.Migrator.csproj src/OrderSystem.Migrator/
RUN dotnet restore src/OrderSystem.Migrator/OrderSystem.Migrator.csproj

COPY src/ ./src/
RUN dotnet publish src/OrderSystem.Migrator/OrderSystem.Migrator.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "OrderSystem.Migrator.dll"]
