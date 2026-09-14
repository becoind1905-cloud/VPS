FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/EkkoBatchVideo.AuthServer/EkkoBatchVideo.AuthServer.csproj src/EkkoBatchVideo.AuthServer/
RUN dotnet restore src/EkkoBatchVideo.AuthServer/EkkoBatchVideo.AuthServer.csproj
COPY src/EkkoBatchVideo.AuthServer/ src/EkkoBatchVideo.AuthServer/
RUN dotnet publish src/EkkoBatchVideo.AuthServer/EkkoBatchVideo.AuthServer.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "EkkoBatchVideo.AuthServer.dll"]
