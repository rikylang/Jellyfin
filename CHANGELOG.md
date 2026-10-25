# Changelog

Formato: [Keep a Changelog](https://keepachangelog.com/it/1.1.0/). Versioni: `MAJOR.MINOR.PATCH.0`.

## [0.1.0.0] - 2026-10-25

Prima versione, per Jellyfin 12.3.

### Aggiunto
- Provider Definition dichiarative (schema JSON versionato v1) e Provider Engine: REST JSON con JSONPath,
  M3U/M3U8, XMLTV (anche gzip), Xtream Codes, stream diretti HLS/DASH/MP4/TS, OpenAPI/Swagger.
- Wizard "Aggiungi API" con rilevamento del formato, mappatura automatica, anteprima con campi mancanti evidenziati,
  "Prova riproduzione" con diagnosi, editor, import/export, libreria di template.
- Catalogo unico con deduplica tra fonti, stato LIVE / tra X min, sincronizzazione adattiva (ETag, backoff con
  jitter, single-flight, circuit breaker, snapshot su disco).
- Channel nativo "Live Hub" (8 cartelle) e Live TV (canali 24/7 + canali per categoria stabili, EPG, guida
  configurata e aggiornata automaticamente).
- Resolver (diretto → estrazione da pagine → sidecar headless Playwright), relay HLS/TS/MP4/DASH con token firmati,
  failover con discontinuità, segmenti condivisi, kill switch.
- Una sorgente per stream e per qualità nel selettore nativo; lingua/regione/qualità per utente.
- Egress: profili direct/HTTP(S)/SOCKS5, verifica connessione con rilevamento VPN non attiva, integrazione gluetun,
  monitor di salute, `docker-compose.example.yml`.
- Preferenze personali, accesso alle fonti per utente, notifiche, proxy immagini firmato e compositi SkiaSharp.
- 216 test automatici, CI GitHub Actions, packaging JPRM, scenario d'integrazione Docker.

### Limiti noti
- Le app Android, iOS e TV non sono state provate direttamente (solo web su Jellyfin 12.3.0 per Windows).
- Lo scenario Docker (`tests/integration/run.sh`) non è stato eseguito in locale (Docker non disponibile): il flusso
  equivalente è stato verificato a mano su Jellyfin 12.3.0 per Windows.
- Il resolver headless richiede il sidecar opzionale e non è stato provato contro siti reali.
