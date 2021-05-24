FROM mcr.microsoft.com/dotnet/runtime:5.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["reference/csharp/DistributedHashMap/IntegrationTest/IntegrationTest.csproj", "IntegrationTest/"]
COPY ["reference/csharp/DistributedHashMap/DistributedHashMap/DistributedHashMap.csproj", "DistributedHashMap/"]
RUN dotnet restore "IntegrationTest/IntegrationTest.csproj"
COPY reference/csharp/DistributedHashMap .
WORKDIR "/src/IntegrationTest"
RUN dotnet build "IntegrationTest.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "IntegrationTest.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "IntegrationTest.dll"]
