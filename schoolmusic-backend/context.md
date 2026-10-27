# School Music Backend: kontekst projektu i zasady

## 1. Opis projektu

Backend API systemu radiowego „School Music” dla szkoły. Uczniowie mogą wyszukiwać utwory, dodawać je do ulubionych, proponować utwory na nadchodzące przerwy i głosować na nie. Moderatorzy sprawdzają nowe propozycje, zanim trafią do kolejki odtwarzania.

- zobacz "C:\Users\Szymon\Desktop\schoolmusic\schoolmusic-backend\docs\KONKURS PROGRAMISTYCZNY.pdf" jeśli potrzebujesz dowiedzieć sie czegoś dodatkowo o projekcie

## 2. Stack technologiczny

| Obszar | Technologia |
|---|---|
| Framework | ASP.NET Core Web API (C# 13) |
| Baza danych | MySQL 8+ / MariaDB 10.4 przez Entity Framework Core (Pomelo) |
| Cache i stan ulotny | Redis (StackExchange.Redis) |
| Czas rzeczywisty | SignalR (`QueueHub`, `/hubs/queue`) |
| Odtwarzanie audio | LibVLCSharp |
| Uwierzytelnianie | JWT Bearer, claims: `sub` (ID użytkownika), `unique_name`, `role` |
| Hasła | BCrypt (`BCrypt.Net-Next`) |
| Dokumentacja API | Scalar + `Microsoft.AspNetCore.OpenApi` (tylko w `Development`) |
| Konfiguracja i sekrety | `dotnet user-secrets` lokalnie, zmienne środowiskowe w Dockerze. Nigdy nie commituj haseł do bazy ani kluczy JWT. |

> Uwaga: w dokumentacji wcześniej była wersja .NET 9, a `Dockerfile` używa .NET 10 (`sdk:10.0`). Ujednolić.

## 3. Baza danych

Schemat jest w `database.sql` (plik `schoolmusic_fixed.sql`). Modele EF są generowane do `Models/` przez scaffold (`--force` nadpisuje pliki, więc własne zmiany umieszczaj w klasach `partial`).

### Role (`ranks`)
| id | nazwa |
|---|---|
| 1 | user |
| 2 | mod |
| 3 | admin |
| 4 | head_admin |

### Tabele
- **Użytkownicy i role:** `users`, `moderators`, `banned_users`
- **Utwory:** `artists`, `songs` (w tym `source`, `song_url`, `genre`, `is_active`), `user_favourites`, `default_songs`
- **Listy:** `song_greenlist` (zatwierdzone utwory), `song_blacklist`, `artist_blacklist`
- **Harmonogram:**
  - `break_templates`: szablon przerw (numer, godzina startu i końca)
  - `weekly_schedule`: muzyka włączona/wyłączona dla dnia tygodnia (1 = poniedziałek, 7 = niedziela)
  - `weekly_break_settings`: ustawienie konkretnej przerwy w dniu tygodnia
  - `exception_day`: dzień wolny lub bez muzyki w zakresie dat
  - `exception_break`: wyjątek dla jednej przerwy w jednym dniu
  - `break`: konkretne przerwy z datą i flagą `music_enabled`
- **Kolejka i głosy:** `queue_items` (ze `status`: queued, playing, played, skipped), `votes` (unikalne `(user_id, queue_item_id)`), `history`
- **Stan odtwarzania:** `playback_state` (jeden wiersz, `id = 1`)
- **Inne:** `special_event`
- **Widok:** `song_vote_counts` (łączna liczba głosów na utwór, do rankingu)

### Priorytet decyzji o muzyce
1. `exception_day` (dzień wolny lub bez muzyki)
2. `exception_break` (wyjątek dla przerwy)
3. `weekly_break_settings` (przerwa w dniu tygodnia)
4. `weekly_schedule` (cały dzień tygodnia)

## 4. Kluczowe przepływy

1. **Propozycja i moderacja**
   - Propozycja utworu, który NIE jest na greenliście, trafia tymczasowo do Redis (`proposals:pending`), a nie do `queue_items`.
   - Moderator zatwierdza lub odrzuca. Dopiero zatwierdzenie dodaje wpis do `queue_items` i do `greenlist:tracks` w Redis.
   - Utwór już zatwierdzony wcześniej omija moderację i trafia bezpośrednio do `queue_items`.
   - Utwór z czarnej listy, z aktywnym cooldownem (24 h) albo na zamkniętym głosowaniu jest odrzucany.

2. **Głosowanie**
   - Głosy są przypisane do `queue_items` (czyli do konkretnej przerwy).
   - Ochrona przed spamem: unikalność `(user_id, queue_item_id)`.

3. **Ulubione**
   - Zapisywane w MySQL (`user_favourites`).
   - Logika „get-or-create”: jeśli utwór z Spotify nie istnieje w `songs`, najpierw dodaj utwór i wykonawcę.

4. **Bezpieczeństwo i kontrolery**
   - ID użytkownika zawsze pobieraj z claims: `User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")`.
   - Aktualne kontrolery w repozytorium: `UsersController` (`/api/auth`), `TracksController` (`/api/tracks`), `RecessController` (`/api/breaks`), `QueueController` (`/api/queue`), `ModerationController` (`/api/moderation`), `PlayerController` (`/api/player`), `RankingsController` (`/api/rankings`).

## 5. Zasady kodowania

- Używaj `async`/`await` wszędzie (`ToListAsync`, `SaveChangesAsync` itd.).
- Dla body żądań używaj jawnych DTO. Nie zwracaj surowych encji EF, zwłaszcza z hasłami.
- Zawsze waliduj dane wejściowe (długość, `null`, białe znaki).
- Statusy HTTP: `200 OK`, `201 Created`, `204 NoContent`, `400 BadRequest`, `401 Unauthorized`, `403 Forbidden`, `404 NotFound`, `409 Conflict`.
- Komentarze XML w języku polskim, zgodnie z istniejącym kodem.

## 6. Stan prac

### Zrobione
- [x] Baza MySQL/MariaDB i scaffold EF do `Models/`
- [x] Uwierzytelnianie JWT i autoryzacja z rolami
- [x] Rejestracja (BCrypt), logowanie (JWT), usuwanie konta
- [x] Ulubione utwory (CRUD)
- [x] Harmonogram przerw, wyjątki dni i przerw
- [x] Propozycje, głosowanie, cofanie głosu, archiwizacja przerwy
- [x] Moderacja (Redis), banowanie
- [x] Ranking utworów, artystów i historii
- [x] SignalR (`QueueHub`)
- [x] Odtwarzanie przez LibVLC (`PlayerController`)

### Do zrobienia

#### 1. Zarządzanie utworami (admin)
Cel: admin dodaje, edytuje, usuwa (miękko) i wyszukuje utwory; w bazie minimum 100 aktywnych pozycji.
- [ ] `TracksController`: `GET /api/tracks` z parametrami `search`, `genre`, `artistId`, `page`, `pageSize`; tylko `is_active = 1`
- [ ] `POST /api/tracks` (role `mod`, `admin`, `head_admin`): tytuł, wykonawca, `spotifyId` lub URL, czas trwania, okładka, gatunek, źródło; tworzy wykonawcę w `artists`, jeśli go brak
- [ ] `PUT /api/tracks/{id}`: edycja
- [ ] `DELETE /api/tracks/{id}`: ustawia `is_active = 0`, nie kasuje wiersza
- [ ] Skrypt seedujący: minimum 100 utworów (SQL lub metoda w `Program.cs` tylko w `Development`), metadane z oficjalnego źródła
- [ ] Sprawdzenie: dodawanie, edycja, wyszukiwanie i usuwanie działają w Swaggerze; `SELECT COUNT(*) FROM songs WHERE is_active = 1` zwraca co najmniej 100

#### 2. Automatyczne przerwy (`BackgroundService`)
Cel: system sam startuje muzykę na przerwie, zatrzymuje ją na końcu i archiwizuje kolejkę.
- [ ] Klasa `BreakSchedulerService : BackgroundService` w `Services/`
- [ ] Rejestracja w `Program.cs`: `builder.Services.AddHostedService<BreakSchedulerService>()`
- [ ] Pętla co ok. 5 s: sprawdza, czy zaczyna się przerwa z `music_enabled = 1`
- [ ] Na starcie przerwy: `BreakService.CalculateBreak`, potem `AudioPlayerService.PlayFileOrUrlAsync` dla pierwszego utworu; stan w `playback_state` lub singletonie
- [ ] Po utworze: następny z planu
- [ ] Na końcu przerwy: `StopAsync` i `QueueService.ArchiveBreakAsync`
- [ ] Każdy obieg pętli w `IServiceScope` (serwisy scoped)
- [ ] Sprawdzenie: w testowej przerwie muzyka startuje i kończy się sama, a kolejka jest zarchiwizowana bez ręcznego wywołania

#### 3. Harmonogram tygodniowy
Cel: admin ustawia muzykę na cały tydzień i na pojedyncze przerwy w dniu tygodnia.
- [ ] `WeeklyScheduleController`: `GET /api/schedule/weekly`
- [ ] `PUT /api/schedule/weekly/{weekday}` (1–7, 1 = poniedziałek), zmiana `music_enabled`
- [ ] `PUT /api/schedule/weekly/{weekday}/breaks/{breakNumber}` dla `WeeklyBreakSetting`
- [ ] `RadioStatusService` i `RecessController` respektują priorytet: dzień wolny → wyjątek przerwy → ustawienie przerwy w tygodniu → ustawienie dnia
- [ ] Sprawdzenie: „środa bez muzyki” wyłącza muzykę we wszystkie środy, a wyjątek dla przerwy ją nadpisuje

#### 4. Sterowanie kolejką (pomijanie i kolejność)
Cel: moderator pomija grany utwór i zmienia kolejność w kolejce.
- [ ] `POST /api/player/skip` (w `PlayerController` lub `QueueControlController`): `StopAsync`, `queue_items.status = 'skipped'`, odtworzenie następnego
- [ ] `PUT /api/queue/break/{breakId}/order`: lista `queueItemId` w nowej kolejności, aktualizacja `order_index` w jednej transakcji (najpierw wartości tymczasowe, bo `(break_id, order_index)` jest unikalne)
- [ ] Po każdej zmianie `BroadcastQueueAsync`
- [ ] Sprawdzenie: po pominięciu gra następny utwór; nowa kolejność widać w `GET /api/queue/break/{id}` i w SignalR

#### 5. Role i konta
Cel: `head_admin` nadaje role użytkownikom.
- [ ] `PUT /api/users/{id}/role` w `UsersController`, `[Authorize(Roles = "head_admin")]`; tylko `rank_id` 1–4; zakaz zmiany własnej roli
- [ ] Nadanie `mod` tworzy wiersz w `moderators`; odebranie go usuwa, jeśli FK na to pozwalają (`queue_items`, `song_greenlist` itd.)
- [ ] `GET /api/users` z paginacją dla `admin` i `head_admin`, bez hashy haseł
- [ ] Sprawdzenie: zmiana roli działa przy kolejnym żądaniu

#### 6. Integracja z zewnętrznym źródłem muzyki (Spotify)
Cel: utwory pochodzą z oficjalnego API zgodnego z regulaminem.

Stan wyjściowy: pakiet `SpotifyAPI.Web` 7.4.2 jest w `.csproj`, ale nie jest używany. Jest TODO w `TracksController` (ok. linii 91) i komunikat „dołącz Spotify API później”. Endpoint `spotify/{spotifyId}/favourites` działa tylko dla utworów już zapisanych w bazie.

Uwaga do prezentacji: Spotify Web API zwraca metadane (tytuł, wykonawca, okładka, czas, link), ale nie służy do odtwarzania pełnych utworów w tej aplikacji. Przed startem sprawdź aktualny regulamin i dostępne endpointy na stronie Spotify, bo zmieniały się w ostatnim czasie.

- [ ] Aplikacja w Spotify Developer Dashboard: Client ID i Client Secret
- [ ] Client ID i Secret w user-secrets (`Spotify:ClientId`, `Spotify:ClientSecret`), nie w `appsettings.json`
- [ ] Rejestracja klienta `SpotifyAPI.Web` jako singleton w `Program.cs`, z flow Client Credentials (do wyszukiwania nie trzeba logowania użytkownika)
- [ ] Interfejs `ISongSourceService` (`SearchAsync`, `GetByIdAsync`) i implementacja `SpotifySongSource`, która mapuje wyniki na DTO aplikacji
- [ ] `GET /api/tracks/external/search?q=` zwracający wyniki z Spotify
- [ ] Endpoint importu: zapis utworu i wykonawcy do `songs` i `artists` z `source = 'spotify'`, `spotify_id`, `song_url`; bez duplikatów (sprawdzenie po `spotify_id`)
- [ ] Usunięcie TODO i komunikatu „dołącz Spotify API później” z `TracksController`
- [ ] Sprawdzenie: wyszukanie i import działają w Swaggerze; umiem wyjaśnić, czego wymaga regulamin i dlaczego odtwarzanie idzie inną drogą

#### 7. Porządki bezpieczeństwa
- [ ] Usunąć `testing-websockets` z `TracksController` i `testing` z `UsersController`
- [ ] `Program.cs`: zamiast `SetIsOriginAllowed(_ => true)` lista z `Cors:AllowedOrigins`
- [ ] `Jwt:Key`, hasło do bazy i klucze API przenieść z `appsettings.json` do user-secrets
- [ ] Limit prób logowania w `UsersController` (np. `AspNetCoreRateLimit`)
- [ ] Sprawdzenie: w repozytorium nie ma sekretów ani endpointów testowych; nieznany origin jest odrzucany

#### 8. Docker i dokumentacja
- [ ] `docker-compose.yml`: serwis `db` (MariaDB 10.4 lub MySQL 8), `database.sql` w `docker-entrypoint-initdb.d`, connection string wskazuje na nazwę serwisu
- [ ] `Dockerfile`: libVLC w obrazie `final` (np. `libvlc5`, `vlc`, `libvlc-dev`)
- [ ] `README.md`: wymagania, `docker compose up`, uruchomienie lokalnie bez Dockera, sekrety, lista endpointów
- [ ] Swagger/Scalar dostępne poza `Development`, za flagą konfiguracji
- [ ] Sprawdzenie: na czystym komputerze `docker compose up` uruchamia bazę i API

#### 9. Testy
- [ ] Projekt `schoolmusic-backend.Tests` (xUnit) w solution
- [ ] Testy `BreakService.CalculateBreak`: pusta kolejka, utwory dokładnie wypełniające przerwę, przerwa krótsza niż najkrótszy utwór
- [ ] Testy `QueueService.VoteAsync`: drugi głos na tę samą pozycję → `Conflict`; głos po terminie → `BadRequest`
- [ ] Testy harmonogramu: dzień wolny → wyjątek przerwy → tydzień → dzień
- [ ] GitHub Actions: build i testy przy każdym pushu
- [ ] Sprawdzenie: `dotnet test` przechodzi lokalnie i w CI

## 7. Poza tym repozytorium
- Panel WWW (React + Next.js + TypeScript)
- Aplikacja mobilna (React Native + Expo)