# Build context is the repository root (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Restore first, using only project files, so the layer is cached across code changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/NovaWallet.Domain/NovaWallet.Domain.csproj                 src/NovaWallet.Domain/
COPY src/NovaWallet.Application/NovaWallet.Application.csproj       src/NovaWallet.Application/
COPY src/NovaWallet.Infrastructure/NovaWallet.Infrastructure.csproj src/NovaWallet.Infrastructure/
COPY src/NovaWallet.Api/NovaWallet.Api.csproj                       src/NovaWallet.Api/
RUN dotnet restore src/NovaWallet.Api/NovaWallet.Api.csproj

COPY src/ src/
RUN dotnet publish src/NovaWallet.Api/NovaWallet.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
# Run as the image's built-in non-root user.
USER $APP_UID
ENTRYPOINT ["dotnet", "NovaWallet.Api.dll"]
