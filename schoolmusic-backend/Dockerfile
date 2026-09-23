# Etap 1: Budowanie i publikacja
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Kopiowanie pliku projektu i przywracanie zależności (optymalizacja cache)
COPY schoolmusic/schoolmusic.csproj schoolmusic/
RUN dotnet restore schoolmusic/schoolmusic.csproj

# Kopiowanie reszty kodu i publikacja w trybie Release
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Etap 2: Środowisko uruchomieniowe (mały obraz końcowy)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Domyślny port w .NET 8 to 8080
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "schoolmusic.dll"]