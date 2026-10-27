# Issue #26 --- szkolne logowanie LDAP

## Cel

Dokończyć gałąź `feature/issue-26-ldap-school-auth` tak, żeby logowanie
kontem szkolnym było bezpieczne, przetestowane lokalnie i gotowe do
sprawdzenia z prawdziwym serwerem szkoły.

**Ważne:** brak dostępu do szkolnego LDAP nie blokuje prac lokalnych.
Integrację z serwerem szkoły można uznać za zweryfikowaną dopiero po
otrzymaniu parametrów i wykonaniu testów z tym serwerem.

## 1. Najpierw sprawdź obecną implementację

-   [x] Przejrzyj `LdapService` oraz metody `Login` i `SchoolLogin` w
    `UsersController`.
-   [x] Sprawdź, jak działa `SimulatedMode` i czy logowanie symulowane
    akceptuje wyłącznie użytkowników z jawnej listy testowej.
-   [x] Sprawdź, kiedy konto lokalne jest tworzone automatycznie po
    udanym logowaniu LDAP (JIT provisioning).
-   [x] Sprawdź, czy konto zablokowane lub oznaczone jako zbanowane nie
    może zalogować się przez żadną ścieżkę.
-   [x] Sprawdź, czy odpowiedzi API nie ujawniają szczegółów wyjątków
    LDAP ani konfiguracji serwera.

## 2. Zabezpiecz konfigurację

-   [x] Wyłącz tryb symulowany domyślnie w konfiguracji używanej poza
    środowiskiem deweloperskim.
-   [x] Upewnij się, że tryb symulowany nie może zostać przypadkowo
    włączony na produkcji (`ValidateConfiguration()` rzuca wyjątek).
-   [x] Nie przechowuj prawdziwych haseł ani danych kont szkolnych w
    repozytorium.
-   [x] Dodaj walidację konfiguracji LDAP przy starcie aplikacji, aby
    błędne ustawienia były wykrywane od razu.
-   [ ] Zdecyduj z administratorem szkoły, czy połączenie ma używać
    LDAPS lub LDAP z StartTLS. Nie wysyłaj haseł przez niezabezpieczone
    połączenie. *(Wymaga kontaktu ze szkołą)*
-   [x] Sprawdź, czy aplikacja dopuszcza logowanie tylko użytkowników,
    którzy rzeczywiście mają prawo korzystać z systemu (np. właściwa
    domena lub grupa szkolna `Ldap:RequiredGroup`).

## 3. Popraw obsługę loginu i zapytań LDAP

-   [x] Sprawdź normalizację loginu. Nie usuwaj automatycznie części
    `@domena`, jeśli aplikacja ma obsługiwać UPN lub adres e-mail jako
    login.
-   [x] Escapuj wartości użytkownika w filtrach LDAP zamiast wstawiać
    surowy login do filtra (`EscapeLdapFilterValue` wg RFC 4515). Dodaj testy pod kątem LDAP injection.
-   [ ] Ustal, jakie identyfikatory szkoła rzeczywiście udostępnia: np.
    `sAMAccountName`, `userPrincipalName`, e-mail lub inny atrybut. *(Wymaga kontaktu ze szkołą)*
-   [x] Używaj stabilnego identyfikatora użytkownika z LDAP do wiązania
    konta szkolnego z lokalnym kontem, zamiast polegać wyłącznie na
    nazwie wyświetlanej.
-   [x] Sprawdź, czy błędy LDAP są logowane po stronie serwera w sposób
    przydatny diagnostycznie, ale bez zapisywania haseł i bez zwracania
    szczegółów klientowi.
-   [x] Sprawdź, czy ścieżki `/login`, `/school/login` i `/ldap/login`
    mają jednoznaczne, spójne zasady działania.

## 4. Uruchom lokalny LDAP do testów

Wybierz jedną opcję:

-   [ ] **LLDAP** --- prostszy serwer do lokalnego testowania logowania
    i wyszukiwania użytkowników.
-   [x] **OpenLDAP** --- skonfigurowano plik `docker-compose.ldap.yml` oraz `docs/ldap/bootstrap.ldif` dla środowiska deweloperskiego.

Pamiętaj, że lokalny LLDAP/OpenLDAP nie odwzoruje w pełni Active
Directory. Atrybuty takie jak `sAMAccountName`, `userPrincipalName` czy
`memberOf` mogą wymagać osobnego skonfigurowania albo nie być dostępne w
ten sam sposób.

Po uruchomieniu lokalnego serwera:

-   [x] Utwórz wyłącznie testowe konta i hasła (`jan.kowalski`, `anna.nowak`).
-   [x] Skonfiguruj aplikację do połączenia z lokalnym LDAP przez
    ustawienia deweloperskie.
-   [x] Przetestuj poprawny login i hasło.
-   [x] Przetestuj błędne hasło oraz nieistniejącego użytkownika.
-   [x] Przetestuj brak połączenia, błędny adres, błędne dane bind i
    timeout.
-   [x] Przetestuj wyszukiwanie użytkownika oraz obsługę loginu w formie
    nazwy, UPN i e-maila.
-   [x] Potwierdź, że po udanym logowaniu API wydaje poprawny JWT.
-   [x] Potwierdź, że przy pierwszym logowaniu konto lokalne tworzy się
    poprawnie, a przy kolejnym nie powstaje duplikat.
-   [x] Przetestuj ponowne logowanie istniejącego użytkownika i konto
    zbanowane/zablokowane.
-   [x] Upewnij się, że testy nie wymagają dostępu do szkolnego serwera.

## 5. Dodaj testy automatyczne

-   [x] Test poprawnego logowania przez LDAP.
-   [x] Test odrzucenia nieprawidłowego hasła.
-   [x] Test nieistniejącego użytkownika.
-   [x] Test wyłączonego LDAP.
-   [x] Test błędu lub niedostępności serwera LDAP.
-   [x] Test utworzenia konta lokalnego po pierwszym udanym logowaniu.
-   [x] Test, że kolejne logowanie nie tworzy drugiego konta.
-   [x] Test blokady/bana użytkownika.
-   [x] Test znaków specjalnych w loginie i ochrony przed LDAP
    injection (`EscapeLdapFilterValue` + próba ataku).
-   [x] Test wszystkich obsługiwanych formatów loginu (domena, e-mail).
-   [x] Test konfiguracji symulowanej oraz upewnienie się, że nie omija
    ona zasad bezpieczeństwa (blokada na produkcji).

## 6. Zweryfikuj zachowanie API i JWT

-   [x] Sprawdź, czy token zawiera właściwe identyfikatory i
    role/claims.
-   [x] Sprawdź, czy konto utworzone automatycznie otrzymuje właściwe
    domyślne uprawnienia (`RankId = 1`).
-   [x] Sprawdź, czy błędne logowanie nie tworzy konta lokalnego.
-   [x] Sprawdź, czy użytkownik LDAP nie może przypadkowo ominąć
    kontroli uprawnień dostępnej dla kont lokalnych.
-   [x] Sprawdź, czy odpowiedzi dla błędnego loginu nie ujawniają, czy
    dany użytkownik istnieje, jeśli nie jest to zamierzone.
-   [x] Sprawdź, czy logowanie i tworzenie konta zachowują się poprawnie
    przy dwóch równoczesnych pierwszych logowaniach (obsłużono `DbUpdateException` z przeładowaniem encji).

## 7. Przygotuj się do integracji ze szkolnym serwerem

Gdy administrator szkoły będzie mógł pomóc, poproś o następujące
informacje --- nie proś o hasła użytkowników (szczegóły w `docs/LDAP_INTEGRATION_GUIDE.md`):

-   [ ] Adres hosta LDAP/Active Directory.
-   [ ] Port oraz wymagany sposób szyfrowania: LDAPS lub StartTLS.
-   [ ] Sposób wyszukiwania użytkowników: konto techniczne do
    bind/search czy bezpośredni bind jako użytkownik.
-   [ ] Bazę wyszukiwania (Base DN), jeśli jest potrzebna.
-   [ ] Atrybut, który ma służyć do wyszukiwania loginu.
-   [ ] Informację, czy logować mogą się wszyscy uczniowie, czy tylko
    członkowie konkretnej grupy/jednostki.
-   [ ] Stabilny atrybut identyfikujący użytkownika.
-   [ ] Ograniczenia sieciowe, certyfikaty CA i ewentualny VPN, jeśli są
    wymagane.
-   [ ] Testowe konto ucznia oraz --- jeśli potrzebne --- ograniczone
    konto techniczne, przekazane bezpiecznym kanałem.

Po uzyskaniu dostępu:

-   [ ] Przetestuj połączenie ze środowiska, w którym faktycznie działa
    backend.
-   [ ] Sprawdź logowanie poprawnym i błędnym hasłem.
-   [ ] Sprawdź użytkownika spoza dozwolonej grupy/domeny.
-   [ ] Sprawdź TLS i poprawną walidację certyfikatu.
-   [ ] Potwierdź działanie JIT provisioning, JWT i blokowania kont.
-   [ ] Zapisz wyniki testów bez umieszczania sekretów w repozytorium.

## 8. Zbuduj projekt i przygotuj PR

-   [x] Uruchom testy automatyczne (183/183 zaliczone).
-   [x] Zbuduj backend w trybie Release.
-   [x] Sprawdź, czy sekrety, hasła testowe i lokalne ustawienia nie
    zostały dodane do commita.
-   [x] Zaktualizuj README: konfiguracja lokalnego LDAP, ustawienia
    deweloperskie, endpointy i znane ograniczenia.
-   [x] W opisie PR rozdziel to, co działa i zostało przetestowane
    lokalnie, od tego, co nadal wymaga potwierdzenia na szkolnym
    serwerze.

## Kryteria ukończenia

### Można zrobić bez szkolnego LDAP

-   [x] Bezpieczna konfiguracja i tryb symulowany.
-   [x] Ochrona filtrów LDAP i poprawna obsługa loginów.
-   [x] Testy jednostkowe oraz integracyjne z lokalnym LDAP.
-   [x] Sprawdzenie JWT, JIT provisioning, blokad kont i obsługi błędów.
-   [x] Dokumentacja lokalnego uruchomienia (`docs/LDAP_INTEGRATION_GUIDE.md`).

### Wymaga dostępu lub informacji od szkoły

-   [ ] Potwierdzenie właściwych atrybutów i filtrów Active Directory.
-   [ ] Potwierdzenie wymagań TLS, uprawnień i ograniczeń sieciowych.
-   [ ] Testy na rzeczywistym szkolnym serwerze.
-   [ ] Potwierdzenie, które konta szkolne mają być dopuszczone.

**Rekomendowana kolejność:** najpierw sprawdź tryb symulowany i
konfigurację, potem zabezpiecz filtry/login, następnie uruchom lokalny
LDAP i testy integracyjne. Nie czekaj z tymi zadaniami na dostęp do
szkolnego serwera.
