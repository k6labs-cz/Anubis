# Anubis

**Anubis** je free open-source OpenID Connect identity provider — self-hostovaná alternativa k PocketID.
Kromě OIDC přihlášení podporuje i klasické **přihlášení e-mailem a heslem** (hesla se ukládají jako Argon2id hashe).

- **OIDC/OAuth2 server**: OpenIddict 7 (MIT) — authorization code flow s povinným PKCE, refresh tokeny, client credentials, introspekce, revokace, end-session
- **UI**: Blazor Web App (.NET 10) + [Hathor](https://github.com/k6labs-cz/Hathor) (MIT)
- **Úložiště**: SQLite přes EF Core (soubor `anubis.db`, migrace v `Data/Migrations`)
- **Hesla**: Argon2id (OWASP parametry: 64 MiB, t=3, p=4), PHC formát, unikátní salt
- **Identity**: ASP.NET Core Identity (role `Admin`/`User`, lockout po 5 neúspěšných pokusech, rate limiting token endpointu)
- **100 % free OSS**: MIT licence projektu, všechny závislosti MIT/Apache-2.0/public domain

## NuGet balíčky Hathor

Řešení používá `k6labs.Hathor` a `k6labs.Hathor.Lucide` **0.3.0**.
Feed organizace je v `NuGet.Config`; mapování posílá balíčky Hathor pouze do
GitHub Packages a ostatní závislosti na nuget.org.

GitHub NuGet vyžaduje přihlášení i pro veřejné balíčky. Před restore nebo prvním
otevřením devcontaineru nastav přístup pomocí PAT classic s `read:packages`
(případně autorizovaného pro SSO organizace). Přihlašovací údaje patří do
prostředí nebo soukromého uživatelského NuGet configu, ne do repozitáře:

```bash
export NuGetPackageSourceCredentials_k6labs='Username=TVUJ_GITHUB_LOGIN;Password=TVUJ_READ_PACKAGES_TOKEN'
dotnet restore Anubis.slnx
```

Celé UI je z komponent Hathor; Anubis nemá žádné vlastní CSS (`wwwroot` obsahuje
jen JavaScript). Jediný stylesheet je CSS isolation bundle `k6labs.Anubis.styles.css`
se styly Hathoru; Lucide set registruje `AddHathorLucide()`. Kořenový `Hathor`
se vykresluje jako `<body>` (`AsBody`), nese barvy a písmo z `Anubis:Appearance`
a uložený světlý/tmavý/systémový režim. Rozložení, typografie, upozornění, tabulky
a menu jsou komponenty Hathoru (`Stack`, `Grid`, `Typography`, `Alert`, `Table`,
`Menu`, `AppShell`, …) – nepoužívejte vlastní třídy ani `style`. Formuláře
používají komponenty Hathor s aplikačními obaly pro popisky a validaci; statické
POST formuláře zachovávají názvy polí, antiforgery a WebAuthn.

## Spuštění (devcontainer)

Nejrychlejší start přes VS Code Dev Containers (obsahuje .NET 10 SDK + sqlite3 CLI):

1. Otevři složku projektu ve VS Code → **Reopen in Container**
2. V terminálu:

```bash
cp Anubis/appsettings.Development.example.json Anubis/appsettings.Development.json
dotnet run --project Anubis
```

Aplikace běží na `http://localhost:5080` (HTTPS profil: `https://localhost:5443`).
Výchozí admin: `admin@anubis.local` / heslo z `appsettings.Development.json`. Bez hesla se admin nevytvoří a administrátorem
se stane první zaregistrovaný uživatel.

Bez devcontaineru jen: `dotnet tool restore && dotnet restore && dotnet run --project Anubis`.

## Testy

```bash
dotnet test
```

## OIDC endpointy

| Endpoint | Popis |
| --- | --- |
| `GET /.well-known/openid-configuration` | discovery dokument |
| `GET /.well-known/jwks` | podpisové klíče |
| `GET/POST /authorize` | autorizace (code flow + PKCE) |
| `POST /token` | vydání tokenu (authorization_code, refresh_token, client_credentials) |
| `GET /userinfo` | informace o uživateli (Bearer token) |
| `GET/POST /endsession` | odhlášení (RP-initiated logout) |
| `POST /introspect` | introspekce tokenu (klient s `ept:introspection`) |
| `POST /revocation` | revokace tokenu |
| `GET /healthz` | health check (včetně databáze) |

Ukázkový klient pro client credentials (vývojová konfigurace):

```bash
curl -X POST http://localhost:5080/token \
  -d grant_type=client_credentials \
  -d client_id=sample-service \
  -d client_secret=dev-service-secret-change-me \
  -d scope=anubis-api
```

## SCIM API (správa uživatelů, rolí a claimů)

Pod `/api/` běží podmnožina **SCIM 2.0** (RFC 7643 / 7644) pro provisioning — standardizované API,
kterým externí systém (HR, IdP nadřazený Anubisu, skripty) spravuje uživatele, role a jejich claimy.
Autentizace je stejným bearer tokenem jako výše: `client_credentials` grant na `/token` se scope
`Anubis:ApiScope` (výchozí `anubis-api`). Požadavky i odpovědi jsou `application/scim+json`,
chyby mají tvar RFC 7644 (`schemas`, `status`, `scimType`, `detail`).

| Endpoint | Popis |
| --- | --- |
| `GET /api/ServiceProviderConfig` | co API podporuje (patch, filtry, stránkování, auth schéma) |
| `GET /api/ResourceTypes`, `GET /api/Schemas` (+ `/{id}`) | discovery schémat |
| `GET/POST /api/Users`, `GET/PUT/PATCH/DELETE /api/Users/{id}` | uživatelé |
| `GET/POST /api/Groups`, `GET/PUT/PATCH/DELETE /api/Groups/{id}` | role (groups) |

Mapování na Anubis:

- **User**: `userName` = e-mail (zároveň přihlašovací jméno), `displayName`/`name.formatted`, `active` = zámek účtu,
  `emails[primary]`, `phoneNumbers[primary]`, `password` (jen při zápisu), `groups` = role uživatele (`value` = id role, `display` = název)
- **Anubis extension** `urn:ietf:params:scim:schemas:extension:anubis:2.0:User`: `claims` (vlastní claimy uživatele, rezervované typy jako `sub`/`email` jsou odmítnuty) a `emailConfirmed`
- **Group** = role: `displayName` = název role, `members` = uživatelé v roli. Vestavěné role `Admin`/`User` nelze přejmenovat ani smazat

List endpointy podporují `filter` (`userName eq "..."`, `displayName co "..."`, kombinace přes `and`), `sortBy`, `sortOrder`,
`startIndex` (1-based), `count` (výchozí 50, max 200), `attributes` a `excludedAttributes`; odpověď je `ListResponse`
(`totalResults`, `startIndex`, `itemsPerPage`, `Resources`). `PATCH` přijímá operace `add`/`remove`/`replace` nad cestami
`displayName`, `active`, `password`, `emails[...]`, `phoneNumbers[...]`, `groups[...]`, `members[...]` a claimy v extension.

Příklad:

```bash
TOKEN=$(curl -s -X POST http://localhost:5080/token \
  -d grant_type=client_credentials -d client_id=sample-service \
  -d client_secret=dev-service-secret-change-me -d scope=anubis-api \
  | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')

curl -X POST http://localhost:5080/api/Users \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/scim+json" \
  -d '{"userName":"jane@example.com","displayName":"Jane","password":"...",
       "urn:ietf:params:scim:schemas:extension:anubis:2.0:User":{"claims":[{"type":"department","value":"sales"}]}}'
```

## Webové UI

- `/` — bez úvodní stránky: přesměruje na `/account` (přihlášený uživatel) nebo `/login`
- `/login`, `/register` — přihlášení (heslo nebo passkey) a registrace
- `/login/2fa`, `/login/recovery-code` — druhý krok přihlášení (kód z aplikace / záchranný kód)
- `/forgot-password`, `/reset-password` — obnova hesla odkazem v e-mailu (platnost 1 h, jednorázový)
- `/confirm-email`, `/confirm-email/resend` — potvrzení e-mailové adresy (odkaz platí 24 h)
- `/account/security` — passkeys (otisk prstu, Face ID, PIN), authenticator aplikace, záchranné kódy
- `/consent` — souhlas uživatele s oprávněními klienta (při code flow)
- `/account` — profil: avatar, jméno, e-mail (+ odeslání ověřovacího e-mailu), telefonní číslo (uloží se jako neověřené, ověří se kódem z SMS; po změně je potřeba ověřit znovu),
  jazyk a barevný režim (světlý / tmavý / podle systému)
- `/account/change-password` — změna hesla
- `/account/history` — historie účtu: přihlášení (i neúspěšná a do aplikací), odhlášení, změny hesla, MFA, passkeys,
  telefonu, e-mailu a zásahy administrátora; s časem (v místním čase prohlížeče), zařízením a IP adresou
- `/admin` — administrace (role `Admin`). Účet i administrace sdílejí jedno rozložení (Hathor `AppShell`) a jednu navigaci;
  sekce **Správa** a **Nastavení** v ní vidí jen administrátor. Sekce **Správa** jsou jen seznamy: akce nad záznamem jsou v menu ⋮ na řádku, nové záznamy (a úprava klienta) se zadávají v modálním okně:
  - `/admin/users` — uživatelé: úprava účtu (e-mail = přihlašovací jméno, jméno, telefon, ověření e-mailu, role) s validací
    před uložením (formát a jedinečnost e-mailu, poslední administrátor, vlastní role Admin, souběžné změny),
    zablokování, reset MFA, smazání
  - `/admin/roles` — role a jejich členové
  - `/admin/scopes`, `/admin/clients` — scopes, OIDC klienti (včetně generování secretu)
  - **Nastavení** (vše se ukládá do souboru s nastavením a platí hned, kromě veřejné adresy, která se projeví po restartu):
    - `/admin/general` – Obecné: název serveru (titulek záložky „Stránka — Název“, logo + název vlevo nahoře),
      veřejná adresa (issuer), samoobslužná registrace, povinné ověření e-mailu
    - `/admin/security` – Zabezpečení: pravidla hesel, zablokování po neúspěšném přihlášení, délka přihlášení,
      maximální počet passkeys na účet
    - `/admin/tokens` – Tokeny: platnost access tokenu, ID tokenu, autorizačního kódu, refresh tokenu a souhlasu
    - `/admin/appearance` – Vzhled (logo, favicon, písmo, barvy), `/admin/email` – E-mail (provider, odesílatel,
      přístupové údaje + testovací e-mail)
  - Všechny seznamy (i passkeys na účtu a členové role) mají akce nad záznamem v menu ⋮

### Vzhled a jazyk

- **UI je čistě z komponent [Hathor](https://github.com/k6labs-cz/Hathor)** – Anubis nemá žádné vlastní CSS,
  písma ani externí styly. Výchozí motiv je Hathor (systémové neproporcionální písmo, cyan a magenta jako
  v `anubis.svg`), světlý i tmavý režim. Přepíšete ho přes konfiguraci `Anubis:Appearance` (viz níže).
- **Světlý/tmavý režim** se nastavuje na stránce účtu (světlý / tmavý / podle systému) a ukládá se k účtu,
  takže platí na všech zařízeních. Nepřihlášení návštěvníci mají vždy režim podle systému (i za běhu).
- **SMS:** ověření telefonního čísla posílá kód přes `ISmsSender`. Výchozí `LogSmsSender` zprávu jen zapíše do logu;
  pro skutečné SMS zaregistrujte vlastní implementaci (`Services/Sms`).
- **Jazyk** se volí na stránce účtu; nepřihlášení dostanou jazyk prohlížeče (nebo `ui_locales` od klienta).
  Patička obsahuje jen vycentrované `© 2006 k6labs`. `/` vede na profil, resp. přihlašovací stránku.
- **Rozložení přihlášené části** (účet i administrace) je přes celou šířku: poloprůhledný horní panel s logem a názvem
  serveru zůstává při posouvání nahoře, navigace vlevo (účet → Správa → Nastavení → Odhlásit) se neposouvá s obsahem.
  Na užších displejích (pod 60 rem) ji otevře tlačítko menu v horním panelu jako boční panel.
- **Avatar uživatele** je na stránce účtu; výchozí je [Gravatar](https://gravatar.com) podle
  e-mailu (MD5 hash), vlastní URL jde nastavit přes `AvatarUrl` u uživatele. S `profile` scope se avatar
  posílá i jako standardní OIDC claim `picture` (userinfo + ID token).
- **Jazyky:** všech 24 úředních jazyků EU (bg, cs, da, de, el, en, es, et, fi, fr, ga, hr, hu, it, lt, lv, mt,
  nl, pl, pt, ro, sk, sl, sv); výchozí je angličtina. Pořadí: jazyk uložený u účtu (volí se na stránce účtu)
  → OIDC parametr `ui_locales` od klienta → jazyk prohlížeče → angličtina.
- **E-maily** se posílají v jazyce příjemce (uloženém u účtu; při registraci se uloží aktuální jazyk).

Texty jsou v `Anubis/Resources/Translations.resx` (EN) a `Translations.<kód>.resx` (ostatní jazyky); v kódu přes
typovou třídu `Translations.Klic` (chybějící klíč = chyba kompilace). Testy hlídají, že každý jazyk má všechny klíče
a stejné `{0}` zástupce, že se v něm vykreslí přihlašovací stránka, a že v `.razor` souborech nezůstal žádný napevno
zapsaný text. Překlady kromě češtiny jsou strojové; před nasazením je vhodné je nechat zkontrolovat rodilými mluvčími.
Nový jazyk: přidejte `Translations.<kód>.resx` a kód a nativní název do `Localization/Cultures.cs`.

## Konfigurace (`appsettings.json`, sekce `Anubis`)

| Klíč | Význam |
| --- | --- |
| `AdminEmail` / `AdminPassword` | admin účet vytvořený při prvním startu; prázdné heslo = admin se nevytvoří a administrátorem se stane první zaregistrovaný uživatel |
| `AllowRegistration` | povolení self-service registrace |
| `RequireConfirmedEmail` | přihlášení až po potvrzení e-mailu (default `false`); vyžaduje funkční sekci `Email` |
| `Issuer` | explicitní issuer (např. za reverse proxy); prázdné = odvozeno z requestu |
| `ApiScope` / `ApiResource` | scope/resource pro client credentials |
| `AccessTokenLifetimeMinutes` | životnost access tokenu (default 60) |
| `IdentityTokenLifetimeMinutes` | životnost ID tokenu (default 20) |
| `AuthorizationCodeLifetimeMinutes` | životnost authorization code (default 10) |
| `RefreshTokenLifetimeDays` | životnost refresh tokenu (default 14) |
| `ConsentCookieLifetimeMinutes` | životnost cookie souhlasu (default 10) |
| `Clients` | pole klientů, které se při startu idempotentně nasadí do DB (ClientId, ClientSecret, RedirectUris, GrantTypes, Scopes, RequireConsent, ...) |
| `DataDirectory` | adresář pro databázi, nastavení a klíče (default content root; v image `/data`) – viz Nasazení |
| `SettingsFile` | soubor s nastavením uloženým z administrace (default `anubis.settings.json` v datovém adresáři) |
| `ConfigFile` / `SecretsDirectory` | volitelný JSON s konfigurací (ConfigMap) a adresář se soubory na klíč (Secret) |
| `Keys:SigningCertificatePath` / `...Password`, `Keys:EncryptionCertificatePath` / `...Password` | vlastní certifikáty OpenIddict místo vygenerovaných |

Příklad seedovaného klienta viz `Anubis/appsettings.Development.example.json`.

### Obecné a zabezpečení (`Anubis:General`, `Anubis:Security`)

| Klíč | Význam (default) |
| --- | --- |
| `General:ServerName` | název serveru (`Anubis`) |
| `Security:PasswordMinLength` | minimální délka hesla (12, rozsah 8–128) |
| `Security:PasswordRequireDigit` / `RequireLowercase` / `RequireUppercase` / `RequireNonAlphanumeric` | povinné znaky v hesle (`true` / `true` / `true` / `false`) |
| `Security:LockoutMaxFailedAttempts` / `LockoutMinutes` | zablokování účtu po N neúspěšných pokusech na M minut (5 / 15) |
| `Security:SessionLifetimeHours` / `SlidingSession` | délka přihlášení a její prodlužování při aktivitě (8 / `true`) |
| `Security:MaxPasskeysPerUser` | max. počet passkeys na účet (20) |

### Appearance (`Anubis:Appearance`)

Výchozí motiv je téma Hathor (systémové neproporcionální písmo, cyan / magenta, logo `anubis.svg`);
všechny hodnoty níže jsou nepovinné a přepíšou výchozí. V konfiguraci přijímají barvy libovolnou CSS hodnotu
(`#7c3aed`, `rgb(...)`, `oklch(...)`); administrace je vybírá color pickerem ve formátu `#RRGGBB`.
Konfigurace se dá zadat i přes proměnné prostředí — v kontejneru/k8s stačí `Anubis__Appearance__...`.

| Klíč | Význam |
| --- | --- |
| `LogoPath` | logo na přihlašovací stránce: `data:` URL, cesta ve `wwwroot` nebo `https://` adresa (default: pyramida Anubis jako `data:` URL přímo v `appsettings.json`) |
| `FaviconPath` | favicon, stejné možnosti (default: pyramida Anubis jako `data:` URL v `appsettings.json`) |
| `FontFamily` | CSS font-family pro celé UI (Hathor `FontFamily`) z písem nainstalovaných v zařízení; prázdné = výchozí písmo Hathoru |
| `Light:Primary` / `Light:Secondary` / `Light:Background` / `Light:Foreground` | barvy světlého motivu (Hathor `--primary`, `--secondary`, `--bg`, `--text`) |
| `Dark:Primary` / `Dark:Secondary` / `Dark:Background` / `Dark:Foreground` | totéž pro tmavý motiv |

Příklad (proměnné prostředí v kontejneru):

```bash
Anubis__Appearance__Light__Primary=#7c3aed
Anubis__Appearance__Light__Background=#ffffff
Anubis__Appearance__Dark__Primary=#a78bfa
Anubis__Appearance__Dark__Background=#0f0a1a
Anubis__Appearance__FontFamily='"Segoe UI", sans-serif'
Anubis__Appearance__LogoPath=data:image/svg+xml;base64,PHN2Zy...   # nebo https://cdn.example.com/logo.svg
Anubis__Appearance__FaviconPath=https://cdn.example.com/favicon.png
```

Hodnoty se předávají kořenové komponentě `Hathor` (palety `Light` / `Dark`), která je vykreslí jako
CSS proměnné v atributu `style` elementu `<body>`.

### E-mail (sekce `Email`)

Výchozí je SMTP. Bez vyplněného `Email:Smtp:Host` se e-maily jen zapisují do logu
(v Development včetně odkazů), takže aplikace běží i bez mail serveru.
Chybná konfigurace zvoleného providera zastaví start s jasnou chybou.

| Klíč | Význam |
| --- | --- |
| `Provider` | `Smtp` (default), `SendGrid`, `Mailgun`, `Postmark`, `Resend` |
| `FromAddress` / `FromName` | odesílatel (u HTTP providerů musí být ověřená doména/adresa) |
| `Smtp:Host`, `Smtp:Port` | SMTP server; port 587 (STARTTLS), 465 (TLS), 25 |
| `Smtp:Security` | `Auto` (default), `StartTls`, `SslOnConnect`, `None` |
| `Smtp:Username` / `Smtp:Password` | přihlášení k SMTP (prázdné = bez autentizace) |
| `SendGrid:ApiKey` | API klíč; `SendGrid:BaseUrl` = `https://api.eu.sendgrid.com` pro EU |
| `Mailgun:ApiKey`, `Mailgun:Domain` | API klíč a odesílací doména; `Mailgun:BaseUrl` = `https://api.eu.mailgun.net` pro EU |
| `Postmark:ServerToken` | server token; `Postmark:MessageStream` (default `outbound`) |
| `Resend:ApiKey` | API klíč |

Příklad (SendGrid přes proměnné prostředí):

```bash
Email__Provider=SendGrid
Email__FromAddress=noreply@example.com
Email__SendGrid__ApiKey=SG.xxxxx
```

Sledování kliknutí je u všech HTTP providerů vypnuté (jinak by přepisovali odkazy pro obnovu hesla).
Odkazy v e-mailech používají `Anubis:Issuer`; v produkci ho nastavte, jinak se bere host z requestu.
Pro lokální vývoj se hodí SMTP catcher, např. [Mailpit](https://mailpit.axllent.org/) (`localhost:1025`, `Security=None`).
Všechny hodnoty lze přetížit přes proměnné prostředí (`Anubis__AdminPassword`, `ConnectionStrings__Default`, ...).

### Nastavení z administrace

Stránky *Obecné*, *Vzhled* a *Email* ukládají sekce `Anubis:General`, `Anubis:Appearance` a `Email` do samostatného souboru
`anubis.settings.json` (cesta přes `Anubis:SettingsFile`, např. na persistentní volume vedle databáze).
Soubor se načítá jako poslední zdroj konfigurace, takže **má přednost před `appsettings.json` i proměnnými
prostředí**, a změny platí hned bez restartu. Tajné hodnoty (heslo SMTP, API klíče) se ve formuláři
nezobrazují; prázdné pole ponechá současnou hodnotu. V souboru jsou uložené v čitelné podobě, proto ho
nedávejte do gitu (je v `.gitignore`).

**Logo a favicon** lze na stránce *Vzhled* nahrát (SVG, WebP, PNG, JPEG, GIF). Uloží se do souboru s nastavením
jako `data:` URL, takže je jde zadat i ručně v konfiguraci (`Anubis__Appearance__LogoPath=data:image/svg+xml;base64,...`),
stejně jako cestu ve `wwwroot` nebo adresu `https://`. Do stránek se data URL nevkládá: obrázky se servírují z
`/appearance/logo` a `/appearance/favicon` s hashem v URL (dlouhodobá cache). Z favicon se automaticky generují PNG
32×32, 180×180 (`apple-touch-icon`) a 192×192 pro prohlížeče bez podpory SVG. Bitmapy se zmenší (logo max. 1024 px,
favicon 512 px) a převedou na bezztrátový WebP; SVG zůstává beze změny, ale nesmí obsahovat DTD ani externí odkazy
a servíruje se se sandboxovou CSP.

**Avatary** přijímají JPEG, PNG, WebP, GIF i SVG; vše (včetně SVG) se převede na WebP max. 1024×1024.

## Nasazení (kontejner / Kubernetes)

Image se sestaví z `Dockerfile` v kořeni repozitáře:

```bash
# GITHUB_PACKAGES_TOKEN nastav v prostředí, GITHUB_LOGIN je tvůj GitHub login.
docker build --build-arg GITHUB_PACKAGES_USER="$GITHUB_LOGIN" \
  --secret id=github_packages_token,env=GITHUB_PACKAGES_TOKEN -t anubis .
docker run -p 8080:8080 -v anubis-data:/data anubis
```

Token pro restore se předává přes BuildKit secret a neukládá se do image.

Hotový image publikuje GitHub Actions (`.github/workflows/image.yml`) po úspěšných testech při každém pushi do `main`:
`ghcr.io/k6labs-cz/anubis` s tagy `latest`, verze z `Anubis.csproj` a `sha-<commit>`.

**Kubernetes** (mikroslužba v clusteru): manifesty jsou v [`deploy/kubernetes/`](deploy/kubernetes) jako kustomize
set — `namespace.yaml`, `pvc.yaml`, `configmap.yaml`, `secret.yaml` (placeholder hodnoty!), `deployment.yaml`,
`service.yaml` (ClusterIP, jen pro cluster), `ingress.yaml` (TLS + veřejná adresa). Nasazení:

```bash
# uprav nejdřív: image, host, storage class, secret.yaml
kubectl apply -k deploy/kubernetes          # nebo: kubectl kustomize deploy/kubernetes | kubectl apply -f -
kubectl -n anubis rollout status deployment/anubis
kubectl -n anubis logs deployment/anubis     # první start: migrace + vygenerování klíčů (startup probe /healthz)
```

**Helm:** chart je v [`deploy/helm/anubis`](deploy/helm/anubis) (popis hodnot v jeho README). Vytvoří stejné objekty
jako kustomize manifesty, `Anubis:Issuer` odvodí z hostu ingressu a heslo admina vygeneruje do Secretu:

```bash
helm install anubis deploy/helm/anubis -n anubis --create-namespace \
  --set ingress.enabled=true --set ingress.hosts[0]=id.example.com
```

**Datový adresář** (`Anubis:DataDirectory`, v image `/data`) obsahuje veškerý stav aplikace – připojte ho na PVC:

| Soubor | Obsah |
| --- | --- |
| `anubis.db` (+ `-wal`, `-shm`) | SQLite databáze (relativní `Data Source` v `ConnectionStrings:Default` je vůči datovému adresáři) |
| `anubis.settings.json` | nastavení uložené z administrace |
| `keys/key-*.xml` | klíče ASP.NET Core Data Protection (přihlašovací cookies, antiforgery, odkazy v e-mailech) |
| `keys/signing-*.pfx`, `keys/encryption-*.pfx` | certifikáty OpenIddict (podpis a šifrování tokenů); vygenerují se při prvním startu, platí 2 roky a 60 dní před vypršením se automaticky vytvoří nový (starý se dál používá k ověření) |

Místo vygenerovaných certifikátů lze zadat vlastní: `Anubis:Keys:SigningCertificatePath` / `SigningCertificatePassword`
a `Anubis:Keys:EncryptionCertificatePath` / `EncryptionCertificatePassword` (např. PFX z k8s Secret). Lokálně je datovým
adresářem složka projektu (`Anubis`), takže se nic nemění.

**Zdroje konfigurace** (od nejnižší priority): `appsettings.json` → `appsettings.{Environment}.json` →
`Anubis:ConfigFile` (JSON ve formátu appsettings, např. ConfigMap) → `Anubis:SecretsDirectory` (soubor na klíč, např.
Secret: soubor `Email__Smtp__Password` nastaví `Email:Smtp:Password`) → proměnné prostředí → příkazová řádka →
nastavení z administrace. Připojené soubory se načtou znovu při změně (image zapíná `DOTNET_USE_POLLING_FILE_WATCHER`,
protože k8s mění ConfigMapy/Secrety přepnutím symlinku).

**Na co dát pozor:**
- SQLite: právě **jedna replika** se strategií `Recreate`, úložiště typu block storage (`ReadWriteOnce`), ne NFS.
- TLS končí na ingressu: image zapíná `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (schéma a IP klienta z
  `X-Forwarded-*`), pod proto vystavujte jen přes proxy. Nastavte `Anubis:Issuer` na veřejnou adresu.
- Image běží jako uživatel `app` (uid 1654); `fsGroup: 1654` zpřístupní PVC pro zápis. Kořenový souborový systém
  může být jen pro čtení (zapisuje se jen do `/data` a `/tmp`).
- Health check: `/healthz` (startup / readiness / liveness probe).

## Observabilita (OpenTelemetry)

Logy, trasy (traces) i metriky jdou přes OpenTelemetry a exportují se protokolem **OTLP**, jakmile je nastavený
`OTEL_EXPORTER_OTLP_ENDPOINT` (bez něj se nic neodesílá). Ostatní standardní proměnné fungují také:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317     # gRPC; pro HTTP např. :4318 + OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=api-key=xxxxx                     # autentizace u SaaS backendů
OTEL_SERVICE_NAME=anubis                                     # výchozí "anubis"
OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=prod,service.instance.id=anubis-1
```

Pro lokální vývoj stačí .NET Aspire dashboard:
`docker run --rm -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard` a `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`.

- **Trasy:** HTTP požadavky (bez `/healthz` a statických souborů), odchozí HttpClient (e-mailové API), EF Core dotazy,
  SMTP (MailKit) a vlastní spany `password.hash` / `password.verify` (Argon2id), `email.send`, `sms.send`, `avatar.process`.
- **Logy:** všechny `ILogger` záznamy včetně scope a trace id; události účtu se logují strukturovaně (`EventType`, `UserId`, ...).
- **Metriky Anubis** (meter `Anubis`):

| Metrika | Typ | Atributy |
| --- | --- | --- |
| `anubis.sign_ins` | counter | `anubis.sign_in.method` (password, passkey, authenticator, recovery-code), `anubis.sign_in.result` (success, failure, locked_out, requires_two_factor, not_allowed) |
| `anubis.account.events` | counter | `anubis.account_event.type` (stejné typy jako v historii účtu) |
| `anubis.tokens.issued` | counter | `oauth.grant_type`, `oauth.client_id` |
| `anubis.emails.sent` / `anubis.email.send.duration` | counter / histogram (s) | `anubis.email.provider`, `anubis.result` |
| `anubis.sms.sent` | counter | `anubis.result` |
| `anubis.registrations` | counter | `anubis.registration.source` (self-service, admin, seed) |
| `anubis.password.hash.duration` | histogram (s) | `anubis.password.operation` (hash, verify) |
| `anubis.users` | gauge | počet účtů |

- **Vestavěné metriky:** ASP.NET Core (`http.server.*`, Kestrel, routing, rate limiting, autentizace, autorizace,
  **Identity**, Blazor circuits), HttpClient, .NET runtime (GC, paměť, vlákna) a MailKit.

Historie účtu se ukládá do tabulky `UserEvents` a záznamy starší než 12 měsíců se automaticky mažou. Za reverse
proxy je pro skutečné IP adresy klientů potřeba nastavit forwarded headers (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`).

## Architektura

```
Anubis/
├── Program.cs                        # DI + pipeline (OpenIddict, Identity, rate limiting, security headers)
├── Controllers/
│   ├── AuthorizationController.cs    # OIDC endpointy (OpenIddict passthrough)
│   └── AuthController.cs             # POST /auth/consent, POST /auth/logout
├── Components/                       # Blazor stránky (Hathor + konfigurovatelné appearance)
│   ├── Pages/Auth/                   # login (+ 2FA, recovery), register, consent, obnova hesla, potvrzení e-mailu
│   ├── Pages/Account/                # profil, změna hesla, passkeys a 2FA
│   ├── Pages/Admin/                  # uživatelé, role, klienti, scopes, nastavení
│   └── Layout/                       # AppLayout (účet + administrace), MainLayout (přihlášení)
├── Data/                             # ApplicationDbContext (Identity + OpenIddict), seed, pruning, migrace
├── Services/                         # consent cookie, správa klientů/scopes
│   └── Email/                        # SMTP (MailKit) + SendGrid/Mailgun/Postmark/Resend, šablony, odkazy
├── Identity/                         # Argon2id hasher, passkey endpointy, TOTP setup, token providery
├── Localization/                     # jazyky, výběr jazyka, přeložené validace a chyby Identity
├── Resources/                        # Translations.resx (EN) + Translations.<kód>.resx (23 dalších jazyků EU)
└── Security/                         # security headers middleware
Anubis.Tests/                         # integrační testy (WebApplicationFactory) + unit testy
deploy/kubernetes/                    # kustomize manifesty
deploy/helm/anubis/                   # Helm chart
.devcontainer/                        # .NET 10 devcontainer + sqlite3
```

Certifikáty pro podpis/šifrování tokenů se při prvním startu vygenerují do `keys/` v datovém adresáři (viz Nasazení). Expirované tokeny maže na pozadí `TokenPruningService` (1× za hodinu, retence 14 dní).

## Bezpečnost

- PKCE (S256) povinné pro authorization code flow
- Argon2id hashe hesel, lockout, rate limiting na `/token` (30 req/min)
- MFA: passkeys (WebAuthn/FIDO2, vždy s ověřením uživatele), TOTP aplikace, jednorázové záchranné kódy
- obnova hesla a potvrzení e-mailu neprozradí, zda účet existuje; max. 1 e-mail na účet za 2 minuty
- HttpOnly + SameSite=Lax cookie, HSTS a security hlavičky v produkci
- consent cookie je chráněný přes Data Protection (nelze podvrhnout)
- redirect URI validace přes OpenIddict, `returnUrl` validace proti open redirect

## Licence

MIT — viz [LICENSE](LICENSE).
