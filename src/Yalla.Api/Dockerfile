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

COPY . .
WORKDIR "/src/src/Yalla.Api"
RUN dotnet build "./Yalla.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Yalla.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Yalla.Api.dll"]
