# Przewodnik Integracji ze Szkolnym Katalogiem LDAP / Active Directory (SchoolMusic)

Niniejszy dokument stanowi kompendium wdrożeniowe dla administratora sieci szkolnej i zespołu technicznego wdrażającego system **SchoolMusic**.

---

## 1. Architektura Uwierzytelniania Szkolnego

System **SchoolMusic** wspiera bezpośrednie logowanie uczniów za pomocą ich istniejących szkolnych kont domenowych (Active Directory / OpenLDAP / Samba AD).

### Kluczowe zasady działania:
1. **Brak przechowywania haseł szkolnych:** Hasła wprowadzane przez użytkowników są przekazywane wyłącznie bezpośrednio do serwera LDAP przez operację `Bind`. Hasła nigdy nie są zapisywane w bazie danych ani w logach aplikacji.
2. **JIT (Just-In-Time) User Provisioning:** Przy pierwszym udanym zalogowaniu przez LDAP konto ucznia jest automatycznie inicjalizowane w lokalnej bazie z podstawową rolą `user`. Kolejne logowania weryfikują tożsamość w LDAP i aktualizują sesję bez tworzenia duplikatów.
3. **Kontrola bezpieczeństwa i moderacji:** Moderator radiowęzła ma możliwość zablokowania lub zbanowania konta w panelu radiowęzła — wówczas użytkownik otrzymuje natychmiast status `403 Forbidden` niezależnie od poprawności hasła w LDAP.
4. **Ochrona przed LDAP Injection:** Wszystkie filtry wyszukiwania są automatycznie sanitizowane zgodnie ze standardem **RFC 4515**.

---

## 2. Kwestionariusz dla Administratora Sieci Szkolnej

Przed podłączeniem systemu do produkcyjnego serwera szkolnego należy uzgodnić poniższe parametry:

| Parametr | Pytanie do administratora | Przykładowa wartość |
| :--- | :--- | :--- |
| **Host LDAP** | Jaki jest adres FQDN lub IP serwera katalogowego? | `ad.szkola.edu.pl` |
| **Port i Szyfrowanie** | Czy serwer wymaga LDAPS (port 636) czy LDAP z StartTLS (port 389)? | Port 636 (LDAPS) |
| **Certyfikat CA** | Czy serwer używa publicznego certyfikatu czy wewnętrznego szkolnego Root CA? | Wewnętrzny CA (wymaga instalacji w kontenerze) |
| **Format loginu** | Jakiego formatu używają uczniowie do logowania na komputerach? | `sAMAccountName` (np. `jan.kowalski`) lub UPN (`jan.kowalski@szkola.edu.pl`) |
| **Base DN** | Jaka jest ścieżka bazowa wyszukiwania kont uczniowskich? | `OU=Uczniowie,DC=szkola,DC=edu,DC=pl` |
| **Filtr wyszukiwania** | Jaki atrybut identyfikuje login? | `(&(objectClass=user)(sAMAccountName={0}))` |
| **Wymagana grupa** | Czy dostęp do radiowęzła ma mieć każdy uczeń, czy tylko określona grupa? | Np. `CN=Uczniowie,OU=Groups,DC=szkola,DC=edu,DC=pl` |
| **Konto techniczne** | Czy do wyszukiwania atrybutów wymagane jest konto serwisowe (Service Account)? | `CN=svc-schoolmusic,OU=ServiceAccounts,DC=szkola,DC=edu,DC=pl` |
| **Ograniczenia sieciowe** | Czy wymagana jest reguła na firewallu lub podsieć szkolna? | Dopuszczenie ruchu TCP 636 z IP serwera aplikacji |

---

## 3. Konfiguracja w Aplikacji (`appsettings.json` / Zmienne Środowiskowe)

### Struktura pliku konfiguracyjnego:
```json
{
  "Ldap": {
    "Enabled": true,
    "Server": "ad.szkola.edu.pl",
    "Port": 636,
    "UseSsl": true,
    "Domain": "szkola.edu.pl",
    "SearchBase": "OU=Uczniowie,DC=szkola,DC=edu,DC=pl",
    "UserSearchFilter": "(|(sAMAccountName={0})(userPrincipalName={0})(mail={0}))",
    "RequiredGroup": "Uczniowie",
    "SimulatedMode": false
  }
}
```

### Mapowanie na zmienne środowiskowe (np. w Dockerze):
```bash
Ldap__Enabled=true
Ldap__Server=ad.szkola.edu.pl
Ldap__Port=636
Ldap__UseSsl=true
Ldap__Domain=szkola.edu.pl
Ldap__SearchBase=OU=Uczniowie,DC=szkola,DC=edu,DC=pl
Ldap__UserSearchFilter=(|(sAMAccountName={0})(userPrincipalName={0})(mail={0}))
Ldap__RequiredGroup=Uczniowie
Ldap__SimulatedMode=false
```

---

## 4. Testy Lokalne bez Serwera Szkolnego

Do weryfikacji funkcjonalności bez dostępu do infrastruktury szkolnej przygotowano dwa tryby:

### A. Tryb symulacyjny (Simulated Mode)
- Aktywny domyślnie w profilu `Development` (`appsettings.Development.json`).
- Weryfikuje logowanie z predefiniowaną listą testową (`jan.kowalski`, `anna.nowak`).
- Zabezpieczenie produkcyjne: próba uruchomienia `SimulatedMode=true` przy `ASPNETCORE_ENVIRONMENT=Production` spowoduje natychmiastowe zablokowanie startu aplikacji.

### B. Lokalny serwer OpenLDAP w Dockerze
Uruchomienie lokalnego kontenera OpenLDAP:
```bash
docker compose -f docker-compose.yml -f docker-compose.ldap.yml up -d
```
Kontener udostępnia bazę `dc=szkola,dc=edu,dc=pl` na porcie `389` z kontami:
- Login: `jan.kowalski` | Hasło: `SzkolaPassword123!`
- Login: `anna.nowak` | Hasło: `SzkolaPassword123!`

---

## 5. Dedykowane Endpointy API

- `POST /api/auth/school/login` – dedykowany endpoint dla aplikacji mobilnej/webowej do logowania szkolnego.
- `POST /api/auth/ldap/login` – alternatywny alias.
- `POST /api/auth/login` – uniwersalny punkt wejścia: najpierw sprawdza lokalne hasło użytkownika, a w razie braku konta transparentnie odpytuje LDAP i tworzy profil (JIT provisioning).
