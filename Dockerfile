# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached between code changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/DocIntel.Core/DocIntel.Core.csproj src/DocIntel.Core/
COPY src/DocIntel.Api/DocIntel.Api.csproj src/DocIntel.Api/
RUN dotnet restore src/DocIntel.Api/DocIntel.Api.csproj

COPY src/ src/
RUN dotnet publish src/DocIntel.Api/DocIntel.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# SQLite file lives on a volume; the image runs as the non-root 'app' user.
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME /data

ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Documents="Data Source=/data/docintel.db"
EXPOSE 8080

COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "DocIntel.Api.dll"]
