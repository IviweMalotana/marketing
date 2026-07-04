FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Marketing.Api/Marketing.Api.csproj Marketing.Api/
RUN dotnet restore Marketing.Api/Marketing.Api.csproj
COPY Marketing.Api/ Marketing.Api/
RUN dotnet publish Marketing.Api/Marketing.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Railway injects PORT; the app binds 0.0.0.0:$PORT (defaults to 8080 locally).
EXPOSE 8080
ENTRYPOINT ["dotnet", "Marketing.Api.dll"]
