FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Hawaslak.Api/Hawaslak.Api.csproj src/Hawaslak.Api/
RUN dotnet restore src/Hawaslak.Api/Hawaslak.Api.csproj
COPY src/Hawaslak.Api/ src/Hawaslak.Api/
RUN dotnet publish src/Hawaslak.Api/Hawaslak.Api.csproj -c Release -o /app --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","Hawaslak.Api.dll"]
