# LifeOS API production image.
#
# Build (from the repository root):
#   docker build -t lifeos-api:<tag> .
#
# Contains only the published API on the ASP.NET Core runtime: no SDK, source, tests, MAUI app or
# configuration secrets. All Production settings (connection string, signing key, Google client,
# allowed emails) are supplied at run time as environment variables; the API refuses to start
# without them. The image never migrates the database.

# ---- restore: project files only, so the package restore layer is reused until a csproj changes ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY src/dotnet/LifeOS.Domain/LifeOS.Domain.csproj src/dotnet/LifeOS.Domain/
COPY src/dotnet/LifeOS.Application/LifeOS.Application.csproj src/dotnet/LifeOS.Application/
COPY src/dotnet/LifeOS.Infrastructure/LifeOS.Infrastructure.csproj src/dotnet/LifeOS.Infrastructure/
COPY src/dotnet/LifeOS.Contracts/LifeOS.Contracts.csproj src/dotnet/LifeOS.Contracts/
COPY src/dotnet/LifeOS.Api/LifeOS.Api.csproj src/dotnet/LifeOS.Api/

RUN dotnet restore src/dotnet/LifeOS.Api/LifeOS.Api.csproj

# ---- publish: the backend sources (the API and the projects it references), Release ----
FROM restore AS publish

COPY src/dotnet/LifeOS.Domain/ src/dotnet/LifeOS.Domain/
COPY src/dotnet/LifeOS.Application/ src/dotnet/LifeOS.Application/
COPY src/dotnet/LifeOS.Infrastructure/ src/dotnet/LifeOS.Infrastructure/
COPY src/dotnet/LifeOS.Contracts/ src/dotnet/LifeOS.Contracts/
COPY src/dotnet/LifeOS.Api/ src/dotnet/LifeOS.Api/

RUN dotnet publish src/dotnet/LifeOS.Api/LifeOS.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        -p:UseAppHost=false

# ---- final: ASP.NET Core runtime and the published output only ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Production unless explicitly overridden at run time. The runtime image already listens on
# 0.0.0.0:8080 (ASPNETCORE_HTTP_PORTS=8080), the port Cloud Run uses.
ENV ASPNETCORE_ENVIRONMENT=Production

COPY --from=publish /app/publish .

# The image's built-in non-root "app" user.
USER $APP_UID

EXPOSE 8080

ENTRYPOINT ["dotnet", "LifeOS.Api.dll"]
