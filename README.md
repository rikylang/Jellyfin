# Live Hub per Jellyfin

Plugin Jellyfin che aggiunge una sezione **live in tempo reale** alimentata da API esterne qualsiasi (REST JSON,
M3U/XMLTV, Xtream Codes, stream diretti HLS/DASH/MP4/TS). Le fonti si collegano **senza scrivere codice**: un
wizard rileva il formato, propone la mappatura dei campi, mostra un'anteprima e prova la riproduzione.

I contenuti compaiono in modo nativo su tutti i client (web, Android, iOS, Android TV, altre TV) tramite:

- la sezione **Canali → Live Hub** (In diretta ora, Oggi, Prossimi, Popolari, Per categoria, Per fonte, Preferiti,
  Canali 24/7);
- la **Live TV**: canali 24/7 reali e canali per categoria ("Calcio 1…N") con guida EPG.

> ## Avvertenza importante
> Questo plugin **non ospita né distribuisce alcun contenuto**: collega soltanto sorgenti già esistenti.
> **Sei tu il responsabile** di collegare esclusivamente fonti per le quali hai diritto di accesso nel tuo paese
> (abbonamenti, contenuti gratuiti legali, tuoi server). Rispetta i termini d'uso dei servizi e le leggi locali.

*English: Live Hub adds live events and 24/7 channels from any external API to Jellyfin (Channels section + Live TV
with EPG). It hosts no content; you are responsible for connecting only sources you are entitled to use. The
documentation below is in Italian; UI strings are Italian and English.*

## Requisiti

- Jellyfin **12.3** (testato su 12.3.0, .NET 10). Il plugin dichiara `targetAbi 12.3.0.0`.
- Facoltativo: un proxy/VPN (consigliato [gluetun](https://github.com/qdm12/gluetun)) e Docker.

## Installazione

1. Dashboard → **Plugin** → **Repository** → **+** e aggiungi l'URL del manifest:
   `https://github.com/<tuo-utente>/<repo>/releases/latest/download/manifest.json`
   (il repository GitHub deve essere **pubblico**; la release si crea con un tag `v0.1.0.0`, vedi "Pubblicare su GitHub")
2. Dashboard → **Plugin** → **Catalogo** → *Live Hub* → **Installa** (categoria *Live TV*).
3. Riavvia Jellyfin. In Dashboard → Plugin → **Live Hub** trovi le impostazioni; nel menu principale compare
   **Live Hub** (le tue preferenze personali).

Installazione manuale: copia i file dello zip di una release in `<data>/plugins/Live Hub_<versione>/` e riavvia.

## Pubblicare su GitHub e installare dal link

1. Crea un repository **pubblico**, carica tutti i file (compresa la cartella `.github`) e attendi la CI verde
   (tab *Actions*).
2. Crea la release: *Releases* → *Draft a new release* → tag **`v0.1.0.0`** (4 numeri, nuovo tag) → *Publish*.
   La CI allega `livehub_0.1.0.0.zip` e `manifest.json`.
3. In Jellyfin: Dashboard → Plugin → Repository → **+** → nome *Live Hub*, URL
   `https://github.com/<utente>/<repo>/releases/latest/download/manifest.json` → Salva.
4. Dashboard → Plugin → Catalogo → *Live Hub* → Installa → riavvia Jellyfin.

Per una nuova versione: nuovo tag (`v0.1.1.0`…); l'URL `latest` punta sempre all'ultima release.

## Aggiungere una API, passo per passo

1. Dashboard → Plugin → Live Hub → **Aggiungi API**.
2. Incolla l'URL (anche una pagina di documentazione, un file OpenAPI/Swagger, una playlist M3U, un endpoint Xtream
   o un URL `.m3u8`). Se serve, scegli l'autenticazione (header/query API key, Bearer, Basic, OAuth2, cookie) e
   inserisci le credenziali: vengono cifrate e non compaiono mai in chiaro nell'interfaccia o nei log.
3. **Rileva**: il plugin riconosce il formato e propone la mappatura (titolo, data, categoria, lingua, immagini,
   stream…). Per le OpenAPI sceglie gli endpoint di elenco.
4. **Anteprima**: tabella con gli eventi trovati; i campi mancanti o non validi sono evidenziati (data fuori
   intervallo, nessuno stream, nessuna lingua, nessuna immagine). Correggi dal form o dal JSON.
5. **Prova riproduzione**: risolve uno stream, scarica la playlist e il primo segmento e mostra formato, codec,
   varianti, header necessari e una **diagnosi** (403/header mancanti, URL scaduto, geoblocco, rete, formato…).
6. **Salva**. La fonte resta modificabile, ri-testabile, esportabile/importabile (i segreti non vengono esportati).

Template inclusi: *REST generico*, *M3U + XMLTV*, *Xtream*, *stream diretto*. Guida alla scrittura a mano:
[docs/PROVIDERS.md](docs/PROVIDERS.md) (schema: [schemas/provider.schema.json](schemas/provider.schema.json)).

## Come si usa

- **Canali → Live Hub**: scegli un evento; nel selettore delle versioni compaiono le sorgenti
  ("1080p · Italiano · Fonte X #2"). La qualità, la lingua e l'ordine seguono le tue preferenze.
- **Live TV → Guida**: i canali 24/7 e i canali per categoria ("Calcio 1", "Tennis 1"…) con orari nel tuo fuso.
  Un canale per categoria mostra l'evento in onda in quel momento.
- **Preferenze personali** (menu principale → Live Hub): lingua e lingue di riserva, "solo la mia lingua", qualità
  (Auto / Solo HD / Preferisci SD / bitrate massimo), fuso orario, paesi, fonti, categorie/squadre/canali preferiti,
  categorie nascoste, ordinamento, notifiche. Valgono su **tutti** i dispositivi perché sono salvate sul server per
  il tuo utente.
- **Amministratore** (Dashboard → Live Hub → Utenti): decide quali fonti vede ogni utente. Il parental control di
  Jellyfin funziona con i tag `livehub-src-<fonte>` e con il campo `OfficialRating` se l'API lo fornisce.
- **Failover**: se uno stream si blocca o l'URL scade, il relay passa da solo alla fonte successiva (stessa lingua,
  qualità più vicina, anche di un altro provider) senza fermare la riproduzione.

## VPN, proxy e privacy (gluetun)

Tutto il traffico in uscita del plugin (API, pagine, playlist, segmenti, immagini) passa dal **profilo di egress**
della fonte: diretto, proxy HTTP(S) o SOCKS5 (con SOCKS5 il DNS lo risolve il proxy). ffmpeg non contatta mai
l'origine: legge solo dal relay interno `/LiveHub/stream/{token}/…` (token firmato, a scadenza; non è un proxy aperto).

1. Copia [docker-compose.example.yml](docker-compose.example.yml) e compila le variabili VPN di gluetun
   (`HTTPPROXY=on`, `SERVER_COUNTRIES=Italy`, control server con API key).
2. Dashboard → Live Hub → **Egress / VPN** → **+**: tipo *Http*, host `gluetun`, porta `8888`, paese atteso `IT`,
   URL del control server `http://gluetun:8000`.
3. **Verifica connessione**: mostra IP pubblico, paese, città e operatore visti da fuori, e avvisa se il proxy esce
   con lo stesso IP del server (VPN non attiva). Il pulsante *Stato tunnel* legge gluetun; il cambio paese a
   runtime funziona solo se la versione di gluetun lo consente, altrimenti vedi le istruzioni (`SERVER_COUNTRIES`).
4. Assegna il profilo a una fonte (editor → Egress) o per paese (regole "profilo per regione").

**Kill switch**: se il profilo è giù non parte nessuna richiesta, né diretta né di riserva; il client riceve un
errore chiaro. Alternativa: tutto Jellyfin dietro la VPN (`network_mode: service:gluetun`, modalità B nel compose):
più semplice, ma anche accesso remoto e metadati passano dalla VPN (vedi [DECISIONS](docs/DECISIONS.md), D-021).

## Compatibilità dei client

Il plugin usa solo meccanismi nativi del server (Channel, Live TV, selettore versioni, messaggi di sessione): non
serve nulla sui client. Le app mostrano ciò che il server espone; alcune differenze dipendono dal client:

| Funzione | Web | Android / Android TV | iOS | Altre TV |
|---|---|---|---|---|
| Sezione Canali → Live Hub | sì | sì | sì | dipende dall'app |
| Live TV con guida | sì | sì | sì | dipende dall'app |
| Selezione sorgente/qualità | sì | sì | sì | dipende dall'app |
| Pagina preferenze | sì | solo da web | solo da web | solo da web |
| Notifiche (DisplayMessage) | sì | sì | dipende | dipende |

**Verificato** su Jellyfin 12.3.0 per Windows con il client web (vedi sotto). Non ho potuto provare le app
Android, iOS o TV: il comportamento lì è dedotto dalle API usate, non osservato.

## Risoluzione dei problemi

| Sintomo | Causa probabile | Cosa fare |
|---|---|---|
| "Accesso negato (401/403)" nella prova | header (Referer/Origin/UA) o paese bloccato | aggiungi gli header allo stream, usa un profilo VPN del paese giusto |
| "URL non trovato o scaduto" | URL firmato scaduto o evento non iniziato | la risoluzione ripetuta è automatica; controlla l'orario |
| "Destinazione bloccata" | indirizzo privato/locale | abilita *rete privata consentita* solo se la fonte è davvero nella tua LAN |
| "Egress non disponibile" | proxy/VPN giù, kill switch attivo | Egress / VPN → Verifica connessione, controlla il container gluetun |
| Nessun canale in Live TV | guida non ancora aggiornata | attendi ~1 min o esegui *Aggiorna guida*; il plugin la accoda da solo |
| Pagina del plugin vuota dopo l'aggiornamento | cache del browser | ricarica forzata (Ctrl+Shift+R) |
| Player JavaScript complesso, nessun URL trovato | l'estrazione non esegue JavaScript | abilita il resolver headless (sidecar Playwright, vedi compose) |

Diagnostica: Dashboard → Live Hub → **Diagnostica** (ultimo sync, errori, latenze, conteggi, stream attivi, stato
egress). I log del server contengono solo messaggi senza credenziali.

## Sviluppo

```bash
dotnet build LiveHub.slnx -warnaserror
dotnet test tests/Jellyfin.Plugin.LiveHub.Tests        # 216 test, nessun accesso a Internet
python build/package.py --version 0.1.0.0               # zip + manifest.json (compatibile JPRM)
bash tests/integration/run.sh artifacts                 # scenario completo su Jellyfin 12.3 in Docker
```

Documentazione: [ARCHITECTURE](docs/ARCHITECTURE.md) · [PROVIDERS](docs/PROVIDERS.md) · [DECISIONS](docs/DECISIONS.md)
· [PROGRESS](docs/PROGRESS.md) · [CHANGELOG](CHANGELOG.md).

## Licenza

GPL-3.0 (come Jellyfin). Vedi [LICENSE](LICENSE).
