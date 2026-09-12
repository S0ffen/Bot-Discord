FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NuGet.Config DiscordActivityBot.sln ./
COPY src/DiscordActivityBot/DiscordActivityBot.csproj src/DiscordActivityBot/
COPY tests/DiscordActivityBot.Tests/DiscordActivityBot.Tests.csproj tests/DiscordActivityBot.Tests/
RUN dotnet restore DiscordActivityBot.sln

COPY . .
RUN dotnet test DiscordActivityBot.sln --configuration Release --no-restore
RUN dotnet publish src/DiscordActivityBot/DiscordActivityBot.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data

ENV DOTNET_ENVIRONMENT=Production \
    Bot__DatabasePath=/app/data/activity-bot.db

ENTRYPOINT ["dotnet", "DiscordActivityBot.dll"]
