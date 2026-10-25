# Scrivere una Provider Definition

Una *Provider Definition* è un file JSON dichiarativo che descrive una fonte esterna. Il plugin la valida
con lo schema versionato [`schemas/provider.schema.json`](../schemas/provider.schema.json) (versione 1) e la
interpreta con il Provider Engine. Il risultato è il modello interno unico: categorie, eventi, canali 24/7,
stream, programmi EPG.

Non serve scrivere codice né ricompilare. Di norma la definizione la crea il **wizard "Aggiungi API"**
(Dashboard → Plugin → Live Hub → Fonti). Questa guida serve per capirla, ritoccarla a mano o
condividerla (Importa/Esporta).

> Le definizioni sono **dati, non codice**:
> - non esistono script, `eval` o espressioni arbitrarie;
> - le regex hanno un timeout;
> - le risposte hanno un limite di dimensione;
> - sono ammessi solo URL `http`/`https`;
> - gli indirizzi privati sono bloccati, salvo `allowPrivateNetwork: true` impostato dall'admin.

## Struttura

```jsonc
{
  "schemaVersion": 1,
  "id": "my-api",              // stabile: [a-z0-9][a-z0-9_-]{1,63}
  "name": "La mia API",
  "kind": "rest",              // rest | m3u | xtream | direct
  "enabled": true,
  "priority": 100,             // più alto = preferito nella deduplica e nell'ordine delle fonti
  "egressProfile": "italia",   // profilo proxy/VPN (null = default per regione o globale)
  "defaults": { "language": "it", "country": "IT", "timezone": "Europe/Rome",
                "category": "Sport", "eventDurationMinutes": 120, "contentRating": null, "tags": [] },
  "connection": { ... },       // base URL, header, auth, timeout, rate limit
  "secrets": { "apiKey": "..." },   // cifrati su disco, mascherati in UI e log
  "variables": { "region": "eu" },  // costanti {var.region}
  "categories": { ... },       // endpoint opzionale delle categorie
  "events": [ { ... } ],       // uno o più elenchi di eventi (live / today / upcoming / all)
  "streams": { ... },          // catena opzionale di endpoint per gli stream (evento → sorgenti → stream)
  "channels": [ { ... } ],     // canali 24/7 da REST
  "resolver": { "enabled": true, "strategy": "auto" },
  "refresh": { "liveSeconds": 45, "todaySeconds": 300, "allSeconds": 1800, "adaptive": true },
  "categoryRules": [ { "match": "(?i)serie a", "country": "IT", "durationMinutes": 115 } ]
}
```

### Connessione e autenticazione

```jsonc
"connection": {
  "baseUrl": "https://api.example.com/v1",
  "headers": { "Accept": "application/json" },
  "userAgent": "Mozilla/5.0 ...",
  "timeoutSeconds": 15,
  "maxResponseBytes": 16777216,
  "rateLimit": { "requestsPerMinute": 60, "burst": 10 },
  "auth": { "type": "apiKey", "in": "header", "name": "X-Api-Key", "value": "{secret.apiKey}" }
}
```

| `auth.type` | Campi |
|---|---|
| `none` | — |
| `apiKey` | `in` (`header`/`query`), `name`, `value` |
| `bearer` | `value` (token) |
| `basic` | `username`, `password` |
| `oauth2ClientCredentials` | `tokenUrl`, `clientId`, `clientSecret`, `scope` (il token è rinnovato da solo) |
| `cookie` | `name`, `value` |

I valori sensibili si mettono in `secrets` e si richiamano con `{secret.nome}`. All'esportazione i segreti
vengono svuotati.

### Endpoint

Ogni endpoint (`categories`, `events[]`, `channels[]`, `eventDetail`) ha:

```jsonc
{
  "id": "live",
  "scope": "live",                       // live | today | upcoming | all → intervallo di polling
  "request": {
    "url": "{baseUrl}/events?date={date}",
    "method": "GET",                     // o POST con "body" (template)
    "headers": {}, "query": { "lang": "{var.lang}" }
  },
  "forEach": "none",                     // category: una richiesta per categoria ({category.id}); date: una per giorno ({date})
  "dateRange": { "fromDays": 0, "toDays": 2, "format": "yyyy-MM-dd" },
  "pagination": { "type": "cursor", "param": "cursor", "cursorPath": "$.meta.next", "maxPages": 10 },
  "refreshSeconds": 45,
  "root": "$.data.items[*]",             // JSONPath degli elementi
  "fields": { ... },                     // mappatura dei campi
  "streams": { "path": "$.feeds[*]", "fields": { ... } },   // stream dentro l'elemento
  "skipIf": [ { "field": "status", "equals": "cancelled" } ]
}
```

**Paginazione** (`pagination.type`):

| Tipo | Parametri |
|---|---|
| `page` | `param`, `start`, `size`/`sizeParam`, `totalPagesPath` |
| `offset` | `param`, `size`/`sizeParam` |
| `cursor` | `cursorPath`, `param` |
| `nextUrl` | `nextUrlPath` (URL completo) |
| `linkHeader` | header HTTP `Link: <...>; rel="next"` |

La paginazione si ferma a una pagina vuota, senza cursore, oppure a `maxPages`.

### Template

Le stringhe con `{...}` sono template:

| Variabile | Significato |
|---|---|
| `{baseUrl}` | `connection.baseUrl` |
| `{page}` `{offset}` `{limit}` `{cursor}` | paginazione |
| `{date}` `{date:yyyyMMdd}` | data (nel fuso `defaults.timezone`) |
| `{now}` `{nowMs}` | unix secondi/millisecondi |
| `{category.id}` `{category.name}` | con `forEach: "category"` |
| `{event.id}` `{event.title}` … | campi mappati dell'evento (catena stream, dettaglio) |
| `{source.id}` `{source.<campo>}` | livello precedente della catena stream (`as`) |
| `{secret.x}` `{var.x}` | segreti e variabili |
| `{$.path}` | JSONPath relativo all'elemento corrente |
| `{value}` | valore corrente (dentro una trasformazione) |

Filtri: `{x|urlencode}`, `{x|lower}`, `{x|upper}`. Le graffe letterali si scrivono `{{` e `}}`.

### Mappatura dei campi

Un campo è:
- una stringa JSONPath (`"$.title"`): `$` è l'elemento corrente;
- un template (`"{$.home} vs {$.away}"`);
- una costante, cioè qualsiasi stringa che non inizia con `$`/`@` e non contiene `{` (`"label": "Embed"`);
- oppure un oggetto:

```jsonc
"start": { "path": "$.kickoff", "format": "dd/MM/yyyy HH:mm", "timezone": "Europe/Rome" },
"title": { "path": "$.name", "transforms": [ { "op": "trim" } ] },
"type":  { "value": "embed" }                 // costante
```

Campi evento: `id` · `title` · `category` · `categoryId` · `competition` · `start` · `end` ·
`durationMinutes` · `isLive` · `status` (`live`/`upcoming`/`ended`) · `popularity` · `poster` · `thumb` ·
`logo` · `participants` · `homeTeam` · `awayTeam` · `homeLogo` · `awayLogo` · `country` · `language` ·
`description` · `pageUrl` · `tags` · `contentRating`.

`participants` è un oggetto: `{ "path": "$.teams[*]", "name": "$.name", "logo": "$.badge" }`. Se manca, il
motore prova a ricavare i partecipanti dal titolo (`A vs B`, `A - B`, `A v B`) per la deduplica e per le
immagini generate.

Campi stream: `id` · `url` · `type` (`hls`/`dash`/`mp4`/`ts`/`embed`/`auto`) · `quality` (es. `1080p`,
`HD`) · `height` · `bitrate` · `hd` · `language` · `country` · `headers` (oggetto o `"Nome: valore"`) ·
`referer` · `userAgent` · `expiresAt` · `label`.

Formati data (`format`):
- `auto`: riconosce unix secondi/millisecondi e ISO 8601;
- `unix`, `unixMs`, `iso8601`, `rfc1123`;
- un pattern .NET (`dd/MM/yyyy HH:mm`), che si combina con `timezone` IANA per le date senza offset.

### JSONPath supportato

`$` radice · `@` corrente · `.nome` · `['nome con spazi']` · `[0]` · `[-1]` · `[*]` · `[0,2]` ·
`[1:5:2]` · `..nome` (discesa ricorsiva) · filtri
`[?(@.type == 'live' && @.viewers > 100)]`, `[?(@.name =~ /serie a/i)]`, `[?(@.url)]`.

### Trasformazioni (dichiarative e sicure)

| op | Parametri | Esempio |
|---|---|---|
| `lowercase` / `uppercase` / `trim` | — | |
| `default` | `value` | `{ "op": "default", "value": "it" }` |
| `map` | `table`, `default`, `ignoreCase`, `strict` | `{ "op": "map", "table": { "ENG": "en" } }` |
| `replace` | `pattern`, `replacement` | `{ "op": "replace", "pattern": "\\s+-\\s+", "replacement": " vs " }` |
| `extract` | `pattern`, `group` | `{ "op": "extract", "pattern": "(\\d{3,4})p", "group": 1 }` |
| `concat` | `values` (template) | `{ "op": "concat", "values": ["{value}", " · ", "{$.league}"] }` |
| `split` | `separator`, `index` | `{ "op": "split", "separator": ",", "index": 0 }` |
| `join` | `separator` | |
| `prefix` / `suffix` | `value` | |
| `number` / `boolean` | `trueValues` | `"live"`, `"1"`, `"true"`, `"yes"` → vero |
| `absoluteUrl` | `base` | URL relativo → assoluto |
| `stripHtml` | — | |
| `template` | `value` | `{ "op": "template", "value": "https://img.example.com/{value}.png" }` |
| `first` / `last` | — | su un array |

### Stream su endpoint separati (anche a più livelli)

```jsonc
"streams": {
  "lazy": true,              // caricati all'apertura dell'evento o dal pre-resolve, poi in cache
  "cacheSeconds": 120,
  "levels": [
    { "request": { "url": "{baseUrl}/matches/{event.id}/sources" }, "root": "$.sources[*]", "as": "source",
      "fields": { "id": "$.sourceId", "label": "$.provider", "language": "$.language" } },
    { "request": { "url": "{baseUrl}/sources/{source.id}/streams" }, "root": "$.streams[*]",
      "fields": { "url": "$.file", "quality": "$.label", "headers": "$.headers" } }
  ]
}
```

Gli attributi dei livelli intermedi (`label`, `language`, `country`, `headers`) vengono ereditati dagli
stream che non li dichiarano.

### Lingua e regione

Ogni evento e stream riceve lingua (ISO 639-1) e paese (ISO 3166-1), presi nell'ordine:
1. dai campi mappati;
2. da `defaults` della definizione;
3. da euristiche su titolo, categoria e attributi M3U (`tvg-language`, `tvg-country`, suffissi come
   `IT`, `[ENG]`, `(ES)`).

Valori come `ITA`, `Italian`, `italiano`, `it-IT` vengono normalizzati in `it`. Le `categoryRules`
(qui e nelle impostazioni globali) assegnano paese, lingua o durata in base a categoria, competizione o
titolo.

## Formati non REST

```jsonc
// Playlist M3U/M3U8 (#EXTINF con tvg-id, tvg-name, tvg-logo, group-title, tvg-language, tvg-country)
{ "kind": "m3u", "m3u": { "url": "https://.../playlist.m3u", "xmltvUrl": "https://.../guide.xml.gz",
  "mode": "channels", "excludeGroups": "(?i)adult" } }

// Xtream Codes (player_api.php)
{ "kind": "xtream", "secrets": { "username": "...", "password": "..." },
  "xtream": { "server": "http://host:8080", "username": "{secret.username}", "password": "{secret.password}",
              "output": "m3u8", "includeEpg": true } }

// URL diretto HLS/DASH/MP4/TS → canale 24/7
{ "kind": "direct", "direct": { "url": "https://.../master.m3u8", "name": "Il mio canale" } }
```

Con `"mode": "events"`, una playlist M3U di eventi (es. `Inter vs Milan 20:45`) viene trasformata in
eventi tramite `eventTitlePattern`, una regex con gruppi nominati `title`, `time`, `date`.

## Esempi completi

Le sei definizioni usate nei test, una per ciascuna API del mock server, sono in
[`tests/fixtures/definitions`](../tests/fixtures/definitions):

| File | Struttura |
|---|---|
| `mock1-flat.json` | array piatto, timestamp unix, stream inclusi, mappa lingue `ITA→it` |
| `mock2-nested.json` | JSON annidato, paginazione a cursore, date ISO con offset, API key, partecipanti con loghi |
| `mock3-multilevel.json` | OAuth2 client credentials, data `dd/MM/yyyy HH:mm` in `Europe/Rome`, stream evento → sorgenti → stream con header `Referer` |
| `mock4-embed.json` | stream che sono pagine embed (risolte dal resolver) |
| `mock5-m3u.json` | M3U + XMLTV gzip |
| `mock6-xtream.json` | Xtream Codes |

La libreria di template del wizard è in [`templates/`](../templates): REST JSON generico, M3U+XMLTV,
Xtream Codes, URL diretto.

## Validazione

- In UI: il pulsante **Valida** mostra gli errori con il percorso JSON.
- Nel repository: `dotnet test` valida tutte le definizioni di esempio contro lo schema.
- Errori comuni:
  - campo sconosciuto (lo schema è chiuso: `additionalProperties: false`);
  - regex non valida;
  - URL non http(s);
  - `kind: rest` senza `events` né `channels`.
