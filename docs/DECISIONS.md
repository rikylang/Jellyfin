# Decisioni di progetto (ADR sintetici)

Ogni voce: contesto → decisione → conseguenze. Le firme citate sono state verificate sul codice
sorgente del tag `v12.3` di `jellyfin/jellyfin` (commit `06de749d`) e, per reflection, sugli assembly
dei pacchetti NuGet `Jellyfin.Controller`/`Jellyfin.Model` 12.3.0.

## D-001 · Versione target: Jellyfin 12.3, .NET 10, targetAbi 12.3.0.0

- Ricerca (24/10/2026): l'ultima stable è **v12.3** (23/10/2026). Dalla 12.0 Jellyfin è passato dalla
  numerazione 10.x alla 12.x. Il `nuspec` di `Jellyfin.Controller 12.3.0` dichiara `net10.0`.
- Il template ufficiale (`jellyfin-plugin-template`, master) è ancora su 10.11/net9.0. Il branch Renovate
  `renovate/major-jellyfin-12.3.0` aggiorna solo i pacchetti. I plugin ufficiali 12.x nel repository stabile
  dichiarano `targetAbi` da 12.0.0.0 a 12.2.0.0.
- **Decisione**: compiliamo contro `Jellyfin.Controller`/`Jellyfin.Model` **12.3.0** con
  `ExcludeAssets=runtime`, `TargetFramework=net10.0`, `targetAbi: 12.3.0.0`. Il targetAbi è la versione
  minima del server: dichiarare 12.0 compilando contro 12.3 rischierebbe un `TypeLoadException` se le
  interfacce implementate fossero cambiate tra 12.0 e 12.3.
- Il pacchetto contiene solo `Jellyfin.Plugin.LiveHub.dll` e `YamlDotNet.dll`. SkiaSharp (3.119.4, la stessa
  versione del server 12.3) è referenziato solo in compilazione (`ExcludeAssets=runtime;native`).

## D-002 · Come Jellyfin 12.3 scopre i componenti del plugin

Verificato in `ApplicationHost`, `CoreAppHost`, `PluginManager.RegisterServices` e
`LiveTvServiceCollectionExtensions`:

| Componente | Meccanismo | Cosa fa il plugin |
|---|---|---|
| `IChannel` | `IEnumerable<IChannel>` iniettato in `ChannelManager` | `AddSingleton<IChannel, LiveHubChannel>()` nel `IPluginServiceRegistrator` |
| `ITunerHost` | `IEnumerable<ITunerHost>` in `TunerHostManager` | registrato in DI |
| `IListingsProvider` | `IEnumerable<IListingsProvider>` in `ListingsManager` | registrato in DI |
| `IScheduledTask` | scansione dei tipi (`GetExports<IScheduledTask>`) | **non** va registrato in DI (si duplicherebbe) |
| `IMediaSourceProvider` | scansione dei tipi (`GetExports<IMediaSourceProvider>`) | nessuna registrazione |
| `IHostedService` | host generico ASP.NET Core | `AddHostedService<...>()` |
| Controller API | application part degli assembly dei plugin | classi `ControllerBase` |

`IServerEntryPoint` non esiste più: i servizi in background sono `IHostedService`.

## D-003 · Live TV senza configurazione manuale del tuner

- `DefaultLiveTvService.GetChannelsAsync` chiama `GetChannels(...)` su **tutti** gli `ITunerHost`
  registrati, a prescindere da `LiveTvOptions.TunerHosts`. I canali compaiono quindi senza che l'admin
  aggiunga un tuner.
- La guida invece passa da `ListingsManager.GetProgramsAsync`, che interroga solo i provider presenti in
  `LiveTvOptions.ListingProviders`.
- **Decisione**: all'avvio il plugin crea (se manca) una voce `ListingsProviderInfo { Type = "livehub" }`
  con `EnableAllTuners = false` e `EnabledTuners = ["livehub"]`, così la nostra EPG non viene applicata ai
  canali di altri tuner. I canali del plugin hanno `TunerHostId = "livehub"`.
- Gli ID canale (`ChannelInfo.Id`) sono stabili. Il `ListingsManager` collega l'EPG al canale prima per Id,
  poi per numero, poi per nome. Usiamo numeri alti (configurabili: 24/7 da 8000, slot da 9000) per evitare
  collisioni con i match per numero/nome di altri provider XMLTV.

## D-004 · Eventi su canali "slot" stabili

- `GuideManager.RefreshGuide` cancella dal DB i canali non più restituiti. Un canale per evento
  creerebbe e distruggerebbe in continuazione `LiveTvChannel` e i relativi programmi.
- **Decisione**: per ogni categoria si creano N canali slot ("Calcio 1…N"), con N pari al massimo di eventi
  contemporanei nella finestra della guida e con un limite configurabile. L'assegnazione è un interval
  scheduling greedy *stabile*: un evento già assegnato resta sul suo slot (assegnazioni persistite su
  disco), mentre gli eventi nuovi prendono lo slot libero con numero più basso.
- Il refresh della guida (`RefreshGuide`, globale e costoso) viene lanciato tramite `ITaskManager` (task con
  Key `RefreshGuide`), con debounce e solo quando cambia l'hash EPG-rilevante.

## D-005 · Channel: metadati immutabili → ID versionati

- `ChannelManager.GetChannelItemEntityAsync` imposta `Name`, `Overview`, `Genres`, ecc. **solo** quando
  l'elemento è nuovo. Gli elementi esistenti non vengono aggiornati, salvo il nome delle cartelle.
- **Decisione**: l'ID esterno di ogni elemento media include un hash di versione dei campi mostrati
  (titolo, bucket di stato LIVE/tra X min/orario, immagine). Quando lo stato cambia nasce un nuovo
  elemento e il vecchio viene eliminato dal `ChannelManager` come "dead id". I bucket sono grossolani
  (orario → "tra 15 min" → LIVE → finito) per limitare il churn.

## D-006 · Channel: personalizzazione per utente e cache condivisa

- Il `ChannelManager` crea elementi nel DB condivisi tra utenti. Dopo ogni `GetChannelItems` elimina gli
  elementi della cartella che non sono stati restituiti, quindi la vista filtrata di un utente cancella
  quella di un altro.
- La cache su file è indicizzata da `IHasCacheKey.GetCacheKey(userId)`.
- **Decisione**:
  1. la cache key include la revisione di visualizzazione del catalogo, l'utente e la versione delle sue
     preferenze;
  2. include anche un *epoch di cambio utente*: se l'ultimo utente servito è diverso da chi sta navigando,
     l'epoch si incrementa e la cache viene invalidata, così le cartelle vengono ricalcolate per l'utente
     corrente.
  - Effetto: chi usa il server da solo ha cache piena; con più utenti si ricalcola solo al cambio.
- Gli ID degli elementi includono la cartella, perché un `BaseItem` ha un solo `ParentId` e lo stesso evento
  compare in più cartelle (In diretta, Oggi, Per categoria…).
- `ISupportsLatestMedia.GetLatestMedia` non viene mai chiamato dal server 12.3: l'interfaccia serve solo da
  marker per la sezione "Ultimi". La implementiamo comunque.

## D-007 · Fonti multiple per utente: IMediaSourceProvider proprio

- `IRequiresMediaInfoCallback.GetChannelItemMediaInfo(id)` non riceve l'utente e il `ChannelManager`
  mette in cache il risultato per 5 minuti per id. Non si può quindi ordinare per preferenze utente.
- `MediaSourceManager.GetPlaybackMediaSources` unisce le sorgenti statiche (elemento senza path → tipo
  `Placeholder`, poi scartato) con quelle dinamiche di **tutti** gli `IMediaSourceProvider`. Le ordina poi:
  prima quella con `Id == item.Id`, poi per larghezza video decrescente.
- **Decisione**: il plugin espone `LiveHubMediaSourceProvider : IMediaSourceProvider` (scoperto per
  scansione). Per gli elementi del canale Live Hub restituisce una `MediaSourceInfo` per stream, con nome
  leggibile ("1080p · Italiano · Fonte X #2"). La sorgente preferita dall'utente (letto dai claim
  `Jellyfin-UserId` della richiesta tramite `IHttpContextAccessor`) riceve `Id = item.Id` e quindi diventa
  la predefinita del selettore nativo.
- Le sorgenti hanno `RequiresOpening = true`: l'apertura passa da `OpenMediaSource`, che crea/condivide un
  `ILiveStream` (stesso meccanismo della Live TV).

## D-008 · Avvio rapido e probing

- `MediaSourceManager.OpenLiveStreamInternal` salta il probe di ffmpeg (che aggiunge almeno 3 s,
  `LiveStreamHelper` con `addProbeDelay`) se almeno un `MediaStream` ha `Index != -1` oppure
  `SupportsProbing = false`.
- **Decisione**: quando il master HLS dichiara `CODECS` e `RESOLUTION`, il plugin compila i `MediaStreams`
  (h264/hevc/aac…, larghezza, altezza, bitrate) con indici reali ed evita il probe. Se i codec sono ignoti
  lascia il probe a Jellyfin (più lento ma sicuro). `AnalyzeDurationMs` è ridotto (1500 ms).

## D-009 · Relay HLS, mai open proxy

- ffmpeg e Jellyfin leggono `http://<server-locale>/LiveHub/stream/{token}/master.m3u8`. Il token è firmato
  HMAC-SHA256, scade, e identifica un *target logico* (evento/canale + selezione stream), mai un URL.
- Gli URL d'origine vivono solo nello stato di sessione lato server. Le sotto-risorse
  (`/r/{id}.m3u8`, `/s/{id}`) sono indici in quella mappa. Non esiste nessun parametro che accetti un URL.
- Protezione SSRF:
  - blocco di loopback, reti private, link-local, multicast e nomi locali;
  - con egress diretto, controllo al momento della connessione (`ConnectCallback`), contro il DNS
    rebinding;
  - eccezione solo con `allowPrivateNetwork` esplicito nella definizione.
- Rate limit per utente: numero massimo di stream contemporanei all'apertura, più un token bucket per le
  richieste al relay.

## D-010 · Egress/VPN

- Un plugin non può creare un tunnel VPN di sistema in modo affidabile. Tutto il traffico in uscita del
  plugin (API, resolver, relay, immagini) passa dal named HttpClient `LiveHub.Egress` di
  `IHttpClientFactory`.
  - Il suo handler primario sceglie, per ogni richiesta, l'handler del profilo (diretto, proxy HTTP(S) o
    SOCKS5 con credenziali).
  - Con SOCKS5 .NET invia il nome host al proxy, quindi la risoluzione DNS avviene lato proxy.
- Kill switch: se il profilo di una fonte risulta giù, le sue richieste falliscono subito con un errore
  esplicito. Il ripiego sulla connessione diretta non esiste per costruzione.
- ffmpeg non contatta mai l'origine: legge sempre dal relay.

## D-011 · Validazione delle definizioni

- A runtime la validazione è tipizzata:
  - deserializzazione `System.Text.Json` con `UnmappedMemberHandling.Disallow`;
  - poi un `DefinitionValidator` (regex con timeout, solo http/https, limiti di dimensione).
- Nei test, gli stessi file vengono validati anche contro `schemas/provider.schema.json` con un validatore
  JSON Schema (`JsonSchema.Net`, solo nel progetto di test). Così schema e modello C# non divergono.
- JSONPath è implementato internamente (subset Goessner/RFC 9535 sufficiente al mapping). Evita dipendenze
  aggiuntive nel processo del server.

## D-012 · Limiti tecnici dichiarati

- **UI personalizzata nelle app TV/mobile: impossibile.** Tutto ciò che vede l'utente passa da Channel, Live
  TV, selettore versioni e messaggi di sessione nativi. Le preferenze si modificano dalla pagina web del
  plugin (web/desktop) e valgono su tutti i dispositivi perché sono salvate per UserId sul server.
- L'ordinamento all'interno delle cartelle del Channel dipende dal client (ordinamento per nome di default).
  I titoli nelle cartelle temporali iniziano con l'orario locale `HH:mm`, così l'ordine per nome è anche
  cronologico.
- Per la Live TV i canali sono globali: il filtro per utente usa i tag nativi (`livehub-src-<provider>`)
  bloccabili dal parental control di Jellyfin.

## D-013 · Relay: sessioni condivise, id deterministici, token senza stato

- Ogni riproduzione passa da `/LiveHub/stream/{token}/…`. Il token è `base64url(JSON) + "." + HMAC-SHA256/128` con
  chiave derivata (HKDF) dalla master key del plugin: sopravvive ai riavvii e contiene evento/canale, stream
  preferito, variante, utente e scadenza. Se la sessione non esiste più (riavvio, chiusura per inattività) viene
  ricreata dal token stesso.
- La sessione è condivisa da tutti gli utenti che guardano lo stesso stream con la stessa variante
  (`tipo|chiave|stream|variante`): una sola connessione all'origine, segmenti in cache breve con single-flight.
- **Mai un open proxy:** si possono scaricare solo URL comparsi nelle playlist d'origine di quella sessione
  (id = hash dell'URL, registro per sessione); per DASH solo percorsi sotto le directory registrate (niente `../`).
  SSRF verificato su ogni richiesta e su ogni redirect.

## D-014 · Una sola variante nel master del relay

ffmpeg, aprendo un master HLS, apre tutte le varianti (più connessioni e più banda verso l'origine). Il relay
espone quindi un master con **una** variante, scelta secondo la preferenza dell'utente (Auto / HD / SD / bitrate
massimo), più le rendition audio/sottotitoli collegate. La scelta della qualità avviene nel selettore nativo
delle versioni di Jellyfin (una MediaSource per stream/qualità). Dopo un failover si sceglie la variante più vicina
per altezza/bitrate.

## D-015 · Failover con discontinuità e numerazione continua

Il relay genera playlist proprie: numerazione `MEDIA-SEQUENCE` continua, `#EXT-X-DISCONTINUITY` (e
`DISCONTINUITY-SEQUENCE`) quando cambia l'origine. Si passa alla fonte successiva (stessa lingua, poi qualità più
vicina, poi priorità, anche di un altro provider) su: playlist ferma oltre `FailoverStallSeconds`, errori ripetuti
di playlist/segmenti (`FailoverSegmentErrors`), 401/403/404/410 dopo un tentativo di nuova risoluzione (URL firmati
scaduti). Il failover avviene solo tra fonti servibili con la stessa modalità (HLS↔HLS, TS↔TS), perché il client
non può cambiare formato a metà flusso.

## D-016 · Media source tramite IMediaSourceProvider con apertura

Gli elementi del Channel non usano `IRequiresMediaInfoCallback` (nessun utente, cache di 5 minuti): un
`IMediaSourceProvider` legge l'utente dal contesto HTTP (`Jellyfin-UserId`), costruisce una MediaSource per stream
ordinata per preferenze, con `RequiresOpening`. All'apertura restituisce un `ILiveStream` condiviso
(`EnableStreamSharing`) che risolve subito la sorgente: gli errori arrivano al client in modo chiaro e la prima
playlist è già pronta. Quando le varianti HLS sono note (pre-risoluzione), codec/risoluzione diventano
`MediaStreams` e Jellyfin salta il probe (avvio più rapido).

## D-017 · Kill switch nel relay

Se il profilo di egress di una fonte è giù, il relay risponde 503 con la diagnosi "egress non disponibile" e non
prova né altre fonti né la connessione diretta: nessuna richiesta parte in chiaro.

## D-018 · Resolver: niente esecuzione di codice

L'estrazione generica legge HTML e script come testo (URL assoluti/relativi, slash escapati, `\uXXXX`, base64 /
`atob`, iframe annidati fino a `maxDepth`). Per player che costruiscono l'URL con JavaScript complesso esiste il
sidecar headless opzionale (Playwright) raggiungibile via HTTP; il plugin poi valida l'URL trovato passando dal
proprio egress (il sidecar va messo nella stessa rete VPN).
## D-019 · Live TV senza configurazione manuale

- Il tuner "Live Hub" è registrato via DI: Jellyfin chiede i canali a tutti i tuner, non serve aggiungerlo a mano.
- La sorgente guida "Live Hub" viene creata automaticamente con `IListingsManager.SaveListingProvider`, legata solo
  al tuner `livehub` (`EnableAllTuners=false`), così non interferisce con altri tuner/XMLTV dell'utente.
- Jellyfin tiene in cache la lista dei canali EPG per sorgente guida e la svuota solo al salvataggio: quando
  l'insieme dei canali cambia il plugin risalva la sorgente (stessa configurazione) e poi accoda "Refresh Guide".
- I canali evento sono **slot stabili per categoria** ("Calcio 1…N", numeri 9000+): un evento resta sul suo slot,
  il numero di slot cresce soltanto (massimo configurabile), così Jellyfin non cancella e ricrea canali (perderebbe
  preferiti e immagini). I canali 24/7 hanno numeri persistiti (quello del provider se libero, altrimenti 8000+).
- Aggiornamento: il Channel si allinea entro ~60 s; la guida Live TV viene riaggiornata al massimo ogni
  `GuideRefreshMinMinutes` (default 10) perché "Refresh Guide" è un'operazione pesante per il server. La riproduzione
  di uno slot usa sempre l'evento in onda *in quel momento*, quindi resta corretta anche tra due aggiornamenti.
- Live TV è globale: gli slot contengono gli eventi visibili a tutti (restrizioni admin applicate come
  intersezione); al momento della riproduzione le sorgenti sono filtrate per l'utente che guarda.
## D-020 · Qualità nel selettore nativo

Per ogni stream la cui playlist master è già nota (pre-risoluzione o riproduzione precedente) vengono esposte fino a
4 MediaSource, una per altezza distinta ("1080p · Spagnolo · Mock 2 #1", "480p · …"), ordinate secondo la preferenza
(Auto → dalla più alta, Preferisci SD → prima ≥360p crescente, Solo HD → solo ≥720p, Bitrate massimo → prima quelle
entro il limite). Ognuna apre una sessione relay con la variante forzata. Gli stream mai risolti espongono una sola
MediaSource con la variante scelta dal relay secondo la preferenza. Le lingue sono ISO 639-1 nel modello e ISO 639-2
(`ita`, `eng`) nelle MediaStream, come si aspetta Jellyfin.
## D-021 · Egress: due modalità, verifica attiva e kill switch

- **Modalità A (consigliata)**: solo il traffico di Live Hub passa dal proxy HTTP/SOCKS5 di gluetun (profilo di
  egress). Il resto di Jellyfin (metadati, accesso remoto, client) resta sulla rete normale. **Modalità B**: tutto il
  container Jellyfin in `network_mode: service:gluetun` (profilo "direct"): niente da configurare nel plugin, ma
  anche accesso remoto e metadati passano dalla VPN e se il tunnel cade Jellyfin non è raggiungibile.
- Ogni profilo proxy è monitorato: probe TCP sulla porta del proxy ogni `HealthCheckSeconds`, stato del tunnel dal
  control server di gluetun (se configurato) e verifica completa dell'IP pubblico ogni 5 minuti o quando il profilo
  cambia stato. La verifica confronta anche l'IP del proxy con l'IP diretto del server: se coincidono la VPN non è
  attiva e il profilo viene marcato giù.
- **Kill switch**: con profilo giù le richieste vengono rifiutate prima di inviare un byte (eccezione chiara, 503 nel
  relay); solo le sonde di salute possono ancora usare il profilo per accorgersi del ripristino. Un profilo
  inesistente o incompleto non ricade mai sulla connessione diretta. Con SOCKS5 il DNS è risolto dal proxy.
- Paese di uscita: mostrato in "Verifica connessione" e confrontato con quello atteso. Il cambio paese a runtime usa
  l'API `PUT /v1/vpn/settings` del control server di gluetun solo se la versione in uso la espone (verificato con una
  GET); altrimenti il plugin mostra le istruzioni (`SERVER_COUNTRIES` + ricreazione del container).
- Password dei proxy e API key di gluetun cifrate (AES-GCM) e restituite all'interfaccia mascherate; il valore
  mascherato inviato indietro conserva quello salvato.
## D-022 · Preferenze, notifiche e immagini

- La pagina "Live Hub" nel menu principale (web/desktop) modifica preferenze salvate **per UserId sul server**: lingua
  e lingue di riserva, "solo la mia lingua", qualità, fuso orario, paesi, fonti, categorie/squadre/canali preferiti,
  categorie nascoste, ordinamento, notifiche. Valgono su tutti i client perché Channel, MediaSource e Live TV le
  leggono lato server. Le fonti proposte sono solo quelle consentite dall'amministratore (pagina "Utenti").
- Notifiche: `DisplayMessage` nativo solo sulle sessioni attive dell'utente **che non stanno riproducendo nulla**
  e solo per eventi preferiti (squadre/categorie) in partenza (anticipo configurabile) o appena iniziati; una sola
  volta per evento. I client che non supportano DisplayMessage vengono ignorati.
- Immagini: le immagini remote (poster, loghi, EPG) sono scaricate dal plugin tramite l'egress del provider e
  servite da `/LiveHub/Images/Remote` con firma HMAC (niente open proxy, niente download diretti da parte di
  Jellyfin verso le origini). Quando l'API non ha immagini viene generato un composito SkiaSharp (la libreria grafica
  già presente in Jellyfin: il plugin non la distribuisce) per poster 2:3, miniatura 16:9 e logo, con cache su disco;
  se SkiaSharp non fosse disponibile si usa il logo incorporato.
- Parental control: gli elementi portano `OfficialRating` (se fornito dall'API) e i tag `livehub-src-<provider>`
  utilizzabili come tag bloccati nel profilo utente.