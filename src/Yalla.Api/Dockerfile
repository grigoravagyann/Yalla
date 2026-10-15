#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore against the project files alone first, so a source-only change does not
# re-download the package graph. Every referenced project has to be copied here,
# or the restore of Yalla.Api cannot resolve them.
COPY ["Directory.Build.props", "./"]
COPY ["global.json", "./"]
COPY ["src/Yalla.Api/Yalla.Api.csproj", "src/Yalla.Api/"]
COPY ["src/Yalla.Infrastructure/Yalla.Infrastructure.csproj", "src/Yalla.Infrastructure/"]
COPY ["src/Yalla.Application/Yalla.Application.csproj", "src/Yalla.Application/"]
COPY ["src/Yalla.Domain/Yalla.Domain.csproj", "src/Yalla.Domain/"]
RUN dotnet restore "src/Yalla.Api/Yalla.Api.csproj"

# The EF tool is pinned by .config/dotnet-tools.json (dotnet-ef 9.0.9, rollForward: false), the
# same file CI and verify.sh restore. Restored here, in the cached layer, so a source change does
# not re-download it.
COPY [".config/dotnet-tools.json", ".config/"]
RUN dotnet tool restore

COPY . .
WORKDIR "/src/src/Yalla.Api"
RUN dotnet build "./Yalla.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Yalla.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# The migration bundle: one self-contained executable that applies every migration still pending
# against the database it is pointed at, and nothing else. Outside Development the API never
# migrates on start (Program.cs, "The way in to a fresh database"), so a deployment has to - and
# this is what deploy/compose.yaml's `migrate` service runs, once, before the API starts.
#
# No --startup-project, as in verify.sh: YallaDbContextFactory exists so the tools never boot the
# API. The bundle therefore also honours that factory's YALLA_MIGRATIONS_CONNECTION, which is how
# the migrate service names the real database; `--connection` on the command line overrides it.
# Self-contained, so the final image's runtime version is irrelevant to it.
FROM build AS bundle
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
RUN dotnet tool run dotnet-ef migrations bundle \
        --project src/Yalla.Infrastructure \
        --configuration $BUILD_CONFIGURATION \
        --self-contained -r linux-x64 \
        --output /out/efbundle \
    && /out/efbundle --help >/dev/null

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=bundle /out/efbundle /app/efbundle

# The photo root and the Serilog file sink are both relative to the content root, and
# LocalDiskPhotoStorage refuses to start when its root is not writable - deliberately, because a
# photo pipeline that silently drops uploads is worse than one that will not boot. So both
# directories have to exist and belong to the app user before the switch below, or the container
# fails on start rather than at the first upload.
RUN mkdir -p /app/.photos /app/Logs && chown -R $APP_UID /app/.photos /app/Logs

# Not root. The aspnet image ships this user (UID 1654) but does not select it, and the default
# Visual Studio template's USER line was missing here - so any code-execution primitive in the API
# ran with nothing left to escalate to.
USER $APP_UID

ENTRYPOINT ["dotnet", "Yalla.Api.dll"]
