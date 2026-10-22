# SchoolMusic Backend

[![Schoolmusic CI](https://github.com/school-music/schoolmusic-backend/actions/workflows/ci.yml/badge.svg)](https://github.com/school-music/schoolmusic-backend/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Docker](https://img.shields.io/badge/Docker-Enabled-2496ED?logo=docker&logoColor=white)](https://www.docker.com/)
[![MariaDB](https://img.shields.io/badge/MariaDB-10.4-003545?logo=mariadb&logoColor=white)](https://mariadb.org/)
[![Redis](https://img.shields.io/badge/Redis-7-DC382D?logo=redis&logoColor=white)](https://redis.io/)
[![Scalar OpenAPI](https://img.shields.io/badge/API_Docs-Scalar-FF5A00)](http://localhost:8080/scalar/v1)

> **SchoolMusic** to kompleksowy, inteligentny system zarządzania radiowęzłem szkolnym. Projekt eliminuje chaos i subiektywizm tradycyjnych radiowęzłów, wprowadzając demokratyczne głosowanie na muzykę, automatyczną synchronizację z dzwonkami lekcyjnymi oraz dwuetapową moderację treści.

---

## Spis treści
- [O projekcie](#-o-projekcie)
- [Główne funkcjonalności](#-główne-funkcjonalności)
- [Technologie](#-stos-technologiczny)
- [Szybki start (Środowisko demonstracyjne)](#-szybki-start-środowisko-demonstracyjne)
- [Domyślne konta testowe](#-domyślne-konta-testowe)
- [Interaktywna dokumentacja API (Scalar)](#-interaktywna-dokumentacja-api-scalar)
- [Zmienne środowiskowe](#-zmienne-środowiskowe)
- [Uruchomienie w środowisku deweloperskim](#-uruchomienie-w-środowisku-deweloperskim)
- [Testy jednostkowe i integracyjne](#-testy-jednostkowe-i-integracyjne)
- [Materiały konkursowe](#-materiały-konkursowe)

---

## O projekcie

W wielu szkołach radiowęzeł jest obsługiwany ręcznie przez pojedyncze osoby, co prowadzi do monotonii muzycznej, spóźnień z wyłączaniem muzyki na lekcjach oraz ryzyka odtwarzania nieodpowiednich utworów.

**SchoolMusic** rozwiązuje te problemy poprzez:
1. **Demokratyczny wybór muzyki:** Uczniowie zgłaszają i wybierają utwory w aplikacji mobilnej.
2. **Precyzyjną automatyzację:** Serwer w tle zarządza odtwarzaniem, idealnie dopasowując utwory do długości przerw.
3. **Kulturalne środowisko szkolne:** Dwuetapowa moderacja (Zielona Lista zweryfikowanych utworów + panel weryfikacji w Redis) chroni szkołę przed wulgaryzmami.

---

## Główne funkcjonalności

* **Live Ranking i demokratyczne głosowanie:** Dynamiczne przeliczanie punktów z natychmiastowym rozgłaszaniem wyników przez WebSockets (**SignalR**).
* **Inteligentny harmonogram przerw (`BreakService`):** Algorytm zachłanny z mechanizmem *gap-fillera*, który optymalnie wypełnia czas przerwy i automatycznie wycisza dźwięk przed wejściem uczniów do klas.
* **Ochrona anty-monopolowa:** Utwór po odtworzeniu otrzymuje **24-godzinny cooldown** – zapobiega to zapętleniu tych samych hitów każdego dnia.
* **Hybrydowy silnik audio:**
  * **Spotify Connect API:** Odtwarzanie w najwyższej jakości bezpośrednio z licencjonowanego katalogu Spotify na urządzeniu szkolnym.
  * **LibVLCSharp:** Lokalny odtwarzacz zapasowy do dżingli, komunikatów dyrekcji oraz pracy w trybie offline.
* **Obsługa dni i przerw specjalnych:** Możliwość wyciszenia muzyki na czas egzaminów (`ExceptionDay`), apeli szkolnych (`ExceptionBreak`) lub zaplanowania audycji dyrekcji (`SpecialEvents`).
* **Analityka i statystyki w Redis:** Monitorowanie najpopularniejszych gatunków, artystów i aktywności uczniów.

---

## Wykorzystane technologie

| Warstwa | Technologia | Opis |
| :--- | :--- | :--- |
| **Platforma** | `.NET 10.0` (C# 14) | Najnowsza, ultra-szybka platforma backendowa ASP.NET Core Web API |
| **Baza danych** | `MariaDB 10.4` | Relacyjna baza danych z mapowaniem przez Entity Framework Core |
| **Cache & Kolejki** | `Redis 7 Alpine` | Szybki cache rankingów, kolejka moderacji i blokady anty-spam |
| **Komunikacja Real-Time** | `SignalR (WebSockets)` | Natychmiastowa synchronizacja stanu odtwarzacza i kolejki |
| **Silnik Audio** | `SpotifyAPI.Web` / `LibVLCSharp` | Hybrydowe sterowanie Spotify Connect oraz lokalny silnik VLC |
| **Dokumentacja API** | `OpenAPI` + `Scalar.AspNetCore` | Nowoczesny, interaktywny interfejs testowania endpointów REST |
| **Konteneryzacja** | `Docker & Docker Compose` | Zapewnia powtarzalność i start całego stosu jedną komendą |
| **Testy i Jakość** | `xUnit`, `Moq`, `FluentAssertions` | Zestaw 149 testów jednostkowych i integracyjnych |
| **CI/CD** | `GitHub Actions` | Automatyczna budowa, testy pokrycia kodu i weryfikacja Dockera |

---

## Szybki start (Środowisko demonstracyjne)

Wszystkie usługi (API, baza danych MariaDB z wgranymi danymi demo oraz Redis) uruchamiają się **jedną komendą** przy użyciu Dockera.

### Wymagania wstępne
* Zainstalowany [Docker Desktop](https://www.docker.com/products/docker-desktop/) lub Docker Engine z wtyczką Compose.

### Uruchomienie:
1. Sklonuj repozytorium:
   ```bash
   git clone https://github.com/school-music/schoolmusic-backend.git
   cd schoolmusic-backend
   ```
2. Uruchom środowisko demonstracyjne:
   ```bash
   docker compose up -d
   ```
3. Poczekaj kilkanaście sekund na inicjalizację bazy MariaDB i start API.
4. **Gotowe!** System jest dostępny pod adresami:
   * **API Backend:** `http://localhost:8080`
   * **Interaktywna dokumentacja Scalar:** `http://localhost:8080/scalar/v1`
   * **Baza danych MariaDB:** `localhost:3306` (baza: `schoolmusic`, user: `root`, hasło: `secret`)
   * **Redis Cache:** `localhost:6379`

Aby zatrzymać środowisko:
```bash
docker compose down
```

---

## Domyślne konta testowe

Baza demonstracyjna w kontenerze MariaDB (`docs/schoolmusic.sql`) jest wstępnie zainicjalizowana kontami o różnych poziomach uprawnień:

| Rola w systemie | Login | Hasło | Uprawnienia |
| :--- | :--- | :--- | :--- |
| **Head Admin** | `admin` | `Admin123!` | Pełne zarządzanie radiowęzłem, użytkownikami, harmonogramami i Spotify |
| **Moderator** | `moderator` | `Mod123!` | Dostęp do panelu moderacji utworów, blokowanie spamerów i Greenlisty |
| **Uczeń** | `uczen` | `Uczen123!` | Standardowe konto ucznia: zgłaszanie utworów, głosowanie, ulubione |

*Hasła w bazie danych są bezpiecznie zahashowane algorytmem **BCrypt**.*

---

## Interaktywna dokumentacja API (Scalar)

Aplikacja wykorzystuje nowoczesny interfejs **Scalar** zintegrowany ze specyfikacją **OpenAPI v1**:
* **Adres interfejsu Scalar:** [http://localhost:8080/scalar/v1](http://localhost:8080/scalar/v1)
* **Surowy schemat OpenAPI:** [http://localhost:8080/openapi/v1.json](http://localhost:8080/openapi/v1.json)

### Jak testować zabezpieczone endpointy w Scalar:
1. W interfejsie Scalar przejdź do sekcji `Users` -> `POST /api/auth/login`.
2. Zaloguj się danymi konta `admin` (hasło: `Admin123!`).
3. Skopiuj zwrócony token JWT.
4. W prawym górnym rogu Scalar kliknij **Authorize / Authentication**, wybierz `Bearer` i wklej swój token.
5. Wszystkie chronione zapytania będą teraz automatycznie autoryzowane Twoim tokenem!

---

## Zmienne środowiskowe

Konfiguracja aplikacji zarządzana jest przez zmienne środowiskowe (zgodnie ze standardem *12-Factor App*):

| Zmienna środowiskowa | Domyślna wartość w Dockerze | Opis |
| :--- | :--- | :--- |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Środowisko uruchomieniowe ASP.NET Core |
| `Features__SwaggerEnabled` | `true` | Aktywuje dokumentację Scalar / OpenAPI na produkcji |
| `ConnectionString__database` | `server=db;port=3306;database=schoolmusic;user=root;password=secret;` | Połączenie do MariaDB |
| `ConnectionString__redis` | `redis:6379` | Połączenie do instancji Redis |
| `Jwt__Key` | `schoolmusic_super_secret_jwt_key_2026_dev!` | Klucz symetryczny do podpisywania tokenów JWT |
| `Jwt__Issuer` | `SchoolMusicAPI` | Wystawca tokenów JWT |
| `Jwt__Audience` | `SchoolMusicApp` | Odbiorca tokenów JWT |
| `Spotify__ClientId` | *(opcjonalne)* | Identyfikator aplikacji Spotify Developer Dashboard |
| `Spotify__ClientSecret` | *(opcjonalne)* | Sekret aplikacji Spotify Developer Dashboard |
| `Spotify__RedirectUri` | `http://localhost:8080/api/spotify-playback/callback` | Callback autoryzacji Spotify OAuth |
| `Spotify__DeviceName` | `PA Spotify` | Nazwa urządzenia Spotify Connect radiowęzła |

---

## Uruchomienie w środowisku deweloperskim

Jeśli chcesz rozwijać aplikację lokalnie na maszynie bez kontenera API:

1. Uruchom same usługi bazodanowe (MariaDB i Redis):
   ```bash
   docker compose up -d db redis
   ```
2. Skompiluj i uruchom backend:
   ```bash
   dotnet run --project schoolmusic-backend
   ```
3. Aplikacja uruchomi się domyślnie na porcie `https://localhost:7198` lub `http://localhost:5242`.

---

## Testy jednostkowe i integracyjne

Projekt posiada rozbudowany pakiet **149 zautomatyzowanych testów** sprawdzających logikę algorytmów kolejkowania, harmonogramu, moderacji oraz autoryzacji:

```bash
# Uruchomienie wszystkich testów
dotnet test schoolmusic-backend.sln

# Uruchomienie testów z raportem pokrycia kodu (Coverage)
dotnet test --collect:"XPlat Code Coverage"
```

Każdy Pull Request oraz push do gałęzi `main` jest automatycznie testowany przez potok **GitHub Actions CI**.

---

## Materiały konkursowe

* 🎤 **Konspekt prezentacji przed komisją:** Szczegółowe omówienie 10 punktów regulaminowych (Sekcje 14 i 15) znajduje się w pliku [`docs/PREZENTACJA.md`](docs/PREZENTACJA.md).
* 🗄️ **Schemat bazy danych demo:** Gotowy plik inicjalizacyjny SQL znajduje się w [`docs/schoolmusic.sql`](docs/schoolmusic.sql).
