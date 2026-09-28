FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY backend/Goulash.sln ./backend/
COPY backend/Directory.Build.props ./backend/
COPY backend/NuGet.Config ./backend/
COPY backend/Api/Goulash.Api.csproj ./backend/Api/
COPY backend/Application/Goulash.Application.csproj ./backend/Application/
COPY backend/Domain/Goulash.Domain.csproj ./backend/Domain/
COPY backend/Infrastructure/Goulash.Infrastructure.csproj ./backend/Infrastructure/
RUN dotnet restore ./backend/Api/Goulash.Api.csproj --configfile ./backend/NuGet.Config
COPY backend/ ./backend/
RUN dotnet publish ./backend/Api/Goulash.Api.csproj --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
RUN apk add --no-cache krb5-libs
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "Goulash.Api.dll"]
