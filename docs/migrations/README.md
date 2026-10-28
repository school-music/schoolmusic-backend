# Migracje bazy danych

`docs/schoolmusic.sql` jest pełnym, aktualnym zrzutem schematu i danych startowych. Służy do utworzenia
nowej bazy (np. przez `docker-entrypoint-initdb.d`). Nie jest skryptem migracyjnym.

Pliki w tym katalogu są numerowanymi, idempotentnymi zmianami przeznaczonymi dla baz, które już istnieją.
Stosuj je po kolei i zapisuj wykonaną wersję w tabeli `schema_migrations` (lub w notatce wdrożeniowej),
aby uniknąć ponownego uruchamiania.

| Wersja | Plik | Opis |
| --- | --- | --- |
| 001 | `001_song_vote_counts_invoker.sql` | Widok `song_vote_counts` działa jako `SQL SECURITY INVOKER` i nie wymaga konta `root` jako definera. |

## Zasady

- Nowa zmiana schematu = nowy plik `NNN_opis.sql`; nie modyfikuj plików już wdrożonych.
- Skrypt musi być bezpieczny do ponownego uruchomienia (`IF EXISTS` / `IF NOT EXISTS`).
- Po zmianie zaktualizuj także `docs/schoolmusic.sql`, aby nowa instalacja od razu miała aktualny schemat.
- Aplikacja nie wykonuje migracji automatycznie; wdrożenie wymaga jawnego uruchomienia skryptu.
