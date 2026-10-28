# Etap 1: Budowanie i publikacja
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Kopiowanie pliku projektu i przywracanie zależności (optymalizacja cache)
COPY schoolmusic-backend/*.csproj ./schoolmusic-backend/
RUN dotnet restore schoolmusic-backend/schoolmusic-backend.csproj

# Kopiowanie reszty kodu i publikacja w trybie Release
COPY schoolmusic-backend/ ./schoolmusic-backend/
WORKDIR /src/schoolmusic-backend
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    libvlc-dev \
    vlc-plugin-base \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Aplikacja działa jako użytkownik bez uprawnień roota. Członkostwo w grupie audio
# jest dodawane tylko wtedy, gdy obraz udostępnia tę grupę.
RUN groupadd --system --gid 10001 schoolmusic \
    && useradd --system --uid 10001 --gid schoolmusic --create-home schoolmusic \
    && mkdir -p /app/recordings \
    && chown -R schoolmusic:schoolmusic /app \
    && if getent group audio >/dev/null; then usermod -aG audio schoolmusic; fi

USER schoolmusic

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "schoolmusic-backend.dll"]
