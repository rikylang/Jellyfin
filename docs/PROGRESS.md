# Avanzamento

Legenda: [x] fatto e verificato · [~] fatto, verifica parziale · [ ] da fare

## Fase 0 — Ricerca e fondamenta
- [x] Versione stabile: Jellyfin 12.3 (.NET 10, targetAbi 12.3.0.0), pacchetti NuGet 12.3.0 esclusi dal runtime
- [x] Firme verificate sul sorgente v12.3 (IChannel, ISupportsLatestMedia, IHasCacheKey, ITunerHost, IListingsProvider, ILiveStream, IHasWebPages, IPluginServiceRegistrator, IMediaSourceProvider, ISessionManager)
- [x] `schemas/provider.schema.json` (v1) progettato prima del motore
- [x] `docs/PROVIDERS.md` con esempi
- [x] Mock server con 6 API strutturalmente diverse + HLS sintetico
- [x] `docs/DECISIONS.md`

## Fase 1 — Scheletro installabile
- [x] Plugin (GUID, IHasWebPages admin + pagina utente nel menu), IPluginServiceRegistrator, risorse i18n it/en
- [x] build.yaml (targetAbi 12.3.0.0, net10.0), packaging JPRM-compatibile (`build/package.py`), manifest.json
- [x] CI GitHub Actions: build -warnaserror, test, pacchetto + validazione JPRM ufficiale, integrazione Docker, release

## Fase 2 — Schema e Provider Engine
- [x] Modello C# delle definizioni speculare allo schema (campi sconosciuti rifiutati), validatore con percorsi JSON
- [x] JSONPath interno (filtri, slice, discesa ricorsiva), template `{...}`, 19 trasformazioni sicure (regex con timeout)
- [x] Date: unix s/ms, ISO 8601, RFC 1123, pattern .NET + fuso IANA (DST)
- [x] Paginazione page/offset/cursor/nextUrl/Link, forEach categoria/data, auth apiKey/bearer/basic/cookie/OAuth2
- [x] Catene di stream multi-livello (lazy/eager), event detail, deduplica intra-provider
- [x] Gateway per provider: rate limit, circuit breaker, single-flight, ETag/If-Modified-Since con riuso del body su 304
- [x] HTTP di uscita: IHttpClientFactory "LiveHub.Egress", SSRF su ogni redirect, limiti dimensione, segreti AES-GCM, redazione
- [x] Test: JSONPath, trasformazioni, template, date, schema (JsonSchema.Net) e validatore su tutti gli esempi

## Fase 3 — Parser M3U/XMLTV/Xtream
- [x] Parser M3U (#EXTINF, #EXTGRP, #EXTVLCOPT, #KODIPROP, `url|header`), modalità canali ed eventi
- [x] Parser XMLTV streaming (gzip, DTD/entità mai espanse, finestra temporale, filtro canali)
- [x] Client Xtream Codes (account, categorie, live stream, EPG xmltv.php), URL diretti
- [x] Le 6 API del mock server funzionano end-to-end nel motore (test in-process, nessuna rete)
- Nota: Fasi 2 e 3 sono in un unico commit perché il runner REST instrada i body M3U/XMLTV negli stessi convertitori.

## Fase 4 — Wizard con anteprima
- [x] Rilevamento formato: REST JSON, M3U, XMLTV, Xtream, HLS/DASH/MP4/TS (anche da magic bytes), pagina web, OpenAPI 3/Swagger 2 (JSON/YAML)
- [x] Mappatura automatica per nome campo + tipo di valore (timestamp, URL immagine, URL .m3u8/.mpd), partecipanti, casa/ospiti, stream annidati o embed, paginazione (cursor/nextUrl/page/offset/Link)
- [x] Anteprima: tabella eventi/canali con campi mancanti/non validi evidenziati, conteggi, richieste, campioni grezzi, catene lazy provate sui primi eventi
- [x] UI admin: elenco fonti, wizard a 4 passi, editor form + JSON, import/export (segreti rimossi), libreria template, impostazioni
- [x] Test: il wizard mappa senza modifiche le API 1, 2, 4, 5, 6, OpenAPI, HLS e pagine; per la 3 rileva casa/ospiti e data custom (la catena stream si aggiunge dal form)
- [x] "Prova riproduzione": risoluzione, playlist e primo segmento con diagnosi (endpoint aggiunto nella Fase 7)

## Fase 5 — Sync
- [x] BackgroundService con polling per endpoint: live 45 s, oggi 5 min, calendario 30 min (configurabili, anche per endpoint), adattivo in base a eventi live/imminenti
- [x] ETag/If-Modified-Since con riuso del body su 304, single-flight, rate limit e circuit breaker per provider
- [x] Backoff esponenziale con jitter, Retry-After su 429/503, max 4 provider in parallelo, un provider rotto non tocca gli altri
- [x] Catalogo unico con deduplica tra fonti (partecipanti/titolo normalizzati, ±15 min), stream uniti, regole di regione
- [x] Stato effettivo (LIVE / tra X min / finito) con durate per categoria; rimozione automatica degli eventi finiti
- [x] Snapshot su disco per avvio a freddo e API irraggiungibili; ultimi dati buoni mantenuti se un endpoint fallisce
- [x] Revisioni dati/visualizzazione (invalidazione cache Channel ed EPG), task pianificato "Live Hub: synchronise sources", API Sync/Diagnostics

## Fase 6 — Channel
- [x] IChannel + ISupportsLatestMedia + IHasCacheKey; cartelle In diretta ora, Oggi, Prossimi (per giorno), Popolari, Per categoria, Per fonte, Preferiti, Canali 24/7
- [x] Titoli brevi per TV ("LIVE · …", "21:00 · …", "domani 04:57 · …"), descrizione con ora locale + offset, competizione · categoria, fonti, lingue, qualità, numero stream
- [x] Filtri per UserId (accesso fonti deciso dall'admin, fonti/categorie nascoste, solo la mia lingua, paesi, preferiti squadre/categorie, ordinamento), vista anonima = intersezione delle fonti consentite
- [x] Id versionati (il ChannelManager aggiorna i metadati solo su elementi nuovi), cache key con epoca per cambio utente (la cache dei file del canale non è per utente), loghi canale incorporati
- [x] Test: cartelle, contenuti, filtri per utente/restrizioni admin, stabilità id, cache key, immagini

## Fase 7 — Resolver e relay
- [x] Catena di resolver: diretto (sniff formato, varianti HLS, scadenza da URL firmati) → estrazione generica da pagine (script, escape, base64, iframe) → sidecar headless opzionale; strategia forzabile/disattivabile per definizione
- [x] Cache delle risoluzioni con TTL < scadenza, single-flight, invalidazione su 403/410; stream "lazy" multi-livello caricati on demand
- [x] Relay `/LiveHub/stream/{token}/…`: token HMAC con scadenza, master con una variante (preferenza utente), playlist riscritte, segmenti/chiavi/init via relay con cache condivisa, TS condiviso (fan-out), MP4 con Range, DASH riscritto
- [x] Failover automatico (stallo, errori segmenti, 403 dopo nuova risoluzione) con discontinuità e numerazione continua; kill switch → 503 chiaro
- [x] Mai open proxy (solo URL registrati per sessione, prefissi DASH confinati), SSRF, rate limit per utente, limite stream per utente, chiusura sessioni inattive
- [x] IMediaSourceProvider (utente dal contesto HTTP) con una MediaSource per stream, ILiveStream condivisi, MediaStreams dalle varianti per saltare il probe; pre-risoluzione di preferiti/popolari in partenza
- [x] "Prova riproduzione" (Admin/TestPlayback): risoluzione + playlist + primo segmento, con diagnosi localizzata (completa la Fase 4)
- [x] Test: parser HLS, token, sniff/scadenze, estrazione, resolver (embed, header, SSRF, cache), relay (riscrittura, varianti, failover stallo/errori/403, kill switch, anti open-proxy, limiti), controller HTTP end-to-end, media source e condivisione

## Fase 8 — Live TV/EPG
- [x] ITunerHost "Live Hub": canali 24/7 reali con numeri stabili e slot evento per categoria ("Calcio 1…N") persistiti
- [x] IListingsProvider: eventi sugli slot (live/sport, immagini, descrizione con fonti e lingue), XMLTV o blocchi di riempimento sui 24/7, orari UTC (fuso dell'utente lato client)
- [x] Sorgente guida creata automaticamente e legata al solo tuner Live Hub; invalidazione cache canali EPG; "Refresh Guide" con debounce
- [x] Riproduzione dei canali tramite relay (evento in onda sullo slot, sorgenti filtrate per utente), immagini dei canali
- [x] Test: allocatore slot (stabilità, crescita, overflow), lineup persistito, tuner/EPG/stream, integrazione guida

## Fase 9 — Qualità, lingua, regione
- [x] Una MediaSource per stream e, se il master è noto, per ogni qualità ("1080p · Italiano · Fonte X #2"), ordinate per lingua preferita/fallback e preferenza qualità (Auto, Solo HD, Preferisci SD, Bitrate massimo)
- [x] Varianti HLS → MediaStreams (codec h264/hevc/av1, profilo, risoluzione, frame rate, bitrate, audio aac/ac3/eac3 con lingua ISO 639-2): probe saltato, avvio rapido
- [x] Lingue ISO 639-1 / paesi ISO 3166-1 normalizzati da campi, default della definizione ed euristiche sul titolo; regole regione (competizione → paese/lingua), filtri paese e "solo la mia lingua"
- [x] Test: normalizzazione, euristiche, scelte di variante per preferenza, mappatura codec, sorgenti per qualità, regole regione

## Fase 10 — Egress/VPN
- [x] Profili direct / HTTP(S) / SOCKS5 (DNS lato proxy) via IHttpClientFactory, profilo per definizione e per paese, default
- [x] API Admin/Egress (lista con segreti mascherati, salvataggio validato), "Verifica connessione" (IP, paese, città, operatore, latenza, rilevamento VPN non attiva)
- [x] Monitor di salute (probe TCP, stato tunnel gluetun, verifica IP periodica) e kill switch senza fallback diretto
- [x] Gluetun: stato tunnel/IP/paese dal control server (API key), cambio paese a runtime se supportato altrimenti istruzioni
- [x] `docker-compose.example.yml` (Jellyfin, gluetun con proxy e control server, sidecar resolver opzionale), modalità A/B documentate
- [x] Test: parser servizi IP, verifica e salute, VPN non attiva, kill switch prima dell'invio, profili mancanti, monitor, gluetun, salvataggio con segreti mascherati

## Fase 11 — Preferenze utente
- [x] API LiveHub/User/Preferences e pagina utente nel menu principale (lingue, qualità, fuso, paesi, fonti, preferiti, nascosti, ordinamento, notifiche), valide su tutti i dispositivi
- [x] API Admin/Users: accesso alle fonti per utente (solo fonti consentite), applicato a Channel, MediaSource e Live TV
- [x] Notifiche DisplayMessage per eventi preferiti, solo su sessioni inattive, una volta per evento
- [x] Immagini: proxy firmato via egress con cache su disco, compositi SkiaSharp (poster, 16:9, logo) per eventi e canali, immagini EPG e canali Live TV
- [x] Test: pagina preferenze/accesso, salvataggio normalizzato, notifiche (sessioni, anticipo, una volta), rendering PNG, proxy firmato e cache

## Fase 12 — Hardening, test, documentazione
- [x] Sidecar headless (`resolver-sidecar/`: Playwright, Dockerfile) e `docker-compose.example.yml`
- [x] Scenario d'integrazione Docker (`tests/integration/`: compose, mock, `run.sh`) richiamato dalla CI — **non eseguito in locale** (Docker non disponibile)
- [x] README (installazione, wizard, VPN/gluetun, compatibilità client, troubleshooting, avvertenza sui contenuti), ARCHITECTURE con diagramma, CHANGELOG
- [x] Verifica manuale su **Jellyfin 12.3.0 per Windows** (client web): installazione dal repository, caricamento, wizard + 6 API mock, Channel (8 cartelle), Live TV (14 canali, guida automatica), apertura dello stream, ffmpeg che legge dal relay e produce segmenti TS, aggiornamento del Channel entro pochi secondi, kill switch con proxy morto, pagine admin/utente
- [x] Bug trovati solo sul server reale e corretti: `LiveStreamId` mancante, estensione dei segmenti rifiutata da ffmpeg, risposte 204 lette come JSON dalla UI
