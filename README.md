# Usporedba graf baze (Neo4j) i dokumentne baze (MongoDB) na primjeru društvene mreže

Benchmark koji uspoređuje Neo4j i MongoDB na istom, sintetički generiranom
skupu podataka društvene mreže (korisnici, praćenja, objave) — kroz pet
upita koji pokrivaju najvažniji spektar razlika između graf i dokumentne
baze: jednostavan lookup, 1-hop, 2-hop, filtrirano+sortirano dohvaćanje,
traversal najkraćeg puta.

Kôd i mjerenja su u C#/.NET 10; sam seminarski rad (tekst, analiza,
zaključci) piše autor odvojeno — ovaj repozitorij daje reproducibilne
brojke i verificiranu ispravnost, ne gotov tekst.

Uz to postoji i **web sučelje** za interaktivno pokretanje upita i usporedbu
rezultata triju implementacija rame uz rame.

## Preduvjeti

- Docker Desktop (ili drugi Docker engine s `docker compose`)
- .NET 10 SDK — **potreban samo za punjenje baza i mjerenje**, ne i za
  pokretanje sučelja (v. sljedeći odjeljak)

---

## Pokretanje

### Ako su baze već napunjene (uobičajen slučaj)

```bash
docker compose up -d
```

Jedna naredba podiže **sva tri servisa**: Neo4j, MongoDB i web sučelje.
Nakon toga:

| Što | Adresa |
|---|---|
| **Web sučelje ovog projekta** | http://localhost:8080 |
| Neo4j Browser | http://localhost:7474 (`neo4j` / `benchmarkpassword`) |
| MongoDB | `mongodb://localhost:27017`, baza `benchmark` |

Zaustavljanje: `docker compose down` (podaci u bazama ostaju — nema
imenovanih volumena, podaci žive u samim spremnicima, pa ih **nemoj**
brisati s `docker compose down -v` ni `docker rm`).

### Ako su baze prazne — puni se samo

Ista naredba. Servis `app` pri pokretanju provjeri broj korisnika u obje
baze i, ako je neka prazna, **sam je napuni** iz `Data/raw/*.csv`
prije nego počne posluživati sučelje:

```
Seed: cekam da baze prihvate konekcije...
Seed: zatecen broj korisnika - Neo4j=0, Mongo=0.
Seed: punim Neo4j...
Seed: punim Mongo...
Seed: gotovo.
Serve: sucelje na http://localhost:8080
```

Prati tijek s `docker compose logs -f app`. Punjenje traje ~20 s.

Tri stvari koje o tome treba znati:

- **Puni se samo baza koja je stvarno prazna.** Loaderi prije punjenja
  **brišu** sve postojeće podatke (moraju, da budu idempotentni), pa se
  nikad ne pokreću nad bazom koja već ima sadržaj — restart spremnika ne
  može tiho obrisati dataset.
- **Čeka se da baze budu spremne** (do 120 s). `depends_on` u Dockeru čeka
  samo da se spremnik *pokrene*, a Neo4ju treba još ~20-30 s dok bolt ne
  počne primati konekcije.
- **CSV-ovi se ne generiraju u spremniku.** Ako `Data/raw/*.csv` ne postoje,
  sučelje se svejedno podigne, ali ispiše uputu da pokreneš `dotnet run --
  generate` na hostu. CSV-ovi su izvor istine za obje baze i namjerno
  nastaju na hostu — spremnik nikad ne piše u izvorno stablo projekta
  (mount je `:ro`).

### Puni pipeline s hosta

Za mjerenje i za promjenu parametara generiranja i dalje treba .NET SDK:

```bash
docker compose up -d           # 1. podigni servise

dotnet run -- generate         # 2. generiraj CSV-ove (deterministicno, seed iz appsettings.json)
dotnet run -- load-neo4j       # 3. napuni Neo4j
dotnet run -- load-mongo       # 4. napuni MongoDB
dotnet run -- verify           # 5. provjeri da sve tri varijante vracaju isto
dotnet run -- bench            # 6. izmjeri
```

Koraci 3 i 4 nisu nužni ako si pustio `app` da napuni baze sam — potrebni su
kad mijenjaš `appsettings.json` (npr. `NUsers`, `Seed`) i želiš novi dataset.

### Sučelje bez Dockera

```bash
dotnet run -- serve
```

Isti port (8080), spaja se na `localhost` adrese iz `appsettings.json`.
Korisno za razvoj — nema ponovnog builda slike pri svakoj izmjeni.

---

## Što se gdje pokreće

| Naredba | Gdje | Što radi |
|---|---|---|
| `docker compose up -d` | host | Podiže Neo4j + MongoDB + web sučelje |
| `docker compose down` | host | Zaustavlja sve (podaci ostaju) |
| `docker compose logs -f app` | host | Prati logove sučelja |
| `dotnet run -- generate` | korijen repozitorija | Generira `Data/raw/*.csv` |
| `dotnet run -- load-neo4j` | korijen repozitorija | Puni Neo4j, gradi constraint/indekse |
| `dotnet run -- load-mongo` | korijen repozitorija | Puni MongoDB, gradi indekse |
| `dotnet run -- verify` | korijen repozitorija | Provjerava da sve tri varijante vraćaju isto |
| `dotnet run -- bench` | korijen repozitorija | Mjeri (interno prvo pokrene `verify`) |
| `dotnet run -- serve` | korijen repozitorija | Sučelje lokalno, bez Dockera |

**Mjerodavna mjerenja rade se `dotnet run -- bench` s hosta**, ne kroz
sučelje. Spremnik `app` natječe se za CPU sa spremnicima baza, pa brojke iz
mini-benchmarka u sučelju nisu usporedive s onima u `results/summary.md`.
Sučelje to i piše na ekranu.

---

## Tri varijante upita

Svaki od Q1–Q5 postoji u **tri** implementacije, i sve tri moraju vraćati
semantički isti rezultat:

| Varijanta | Datoteka | Kako radi |
|---|---|---|
| `neo4j` | `Queries/Neo4jQueries.cs` | Jedan Cypher upit |
| `mongo` | `Queries/MongoQueries.cs` | Više tipiziranih `Builders<T>` odlazaka u bazu, spajanje u C#-u |
| `mongo-raw` | `Queries/MongoRawQueries.cs` | Jedan aggregation pipeline (`$lookup` / `$graphLookup`) |

Treća varijanta postoji da odgovori na pitanje **je li izmjerena razlika
razlika između baza ili razlika između dva stila pisanja koda.** Kod Q3 i Q4
`mongo-raw` se izjednačava s Neo4jem, što znači da ondje razlika dolazi iz
aplikacijske orkestracije, ne iz modela podataka. Kod Q5 se ne izjednačava —
`$graphLookup` nema rani prekid. Detalji su u bilješkama uz seminarski rad (izvan ovog repozitorija).

**Poznato ograničenje `mongo-raw` varijante kod Q5:** `$graphLookup` vraća
samo *dubinu* svakog dosegnutog čvora, ne i lanac kojim se do njega došlo.
Ta varijanta zato vraća ispravnu duljinu puta, ali **prazan put**. `verify`
kod Q5 ionako uspoređuje samo duljinu (v. komentar u `Bench/Verifier.cs`) i
izričito to ispiše, da zeleni rezultat nitko ne pročita kao dokaz da su
putevi identični.

---

## Web sučelje

Četiri dijela:

1. **Usporedba upita** — odaberi Q1–Q5 i subjekta (ili par za Q5), rezultat
   sve tri varijante rame uz rame, s vremenom, brojem redaka i oznakom
   poklapaju li se.
2. **Tekst upita** — doslovni Cypher / MQL koji je izvršen. Za varijantu
   `mongo` prikazuju se C# pozivi drivera, jer ta varijanta **nema** jedan
   tekst upita — radi više tipiziranih odlazaka u bazu. Prikaz izmišljenog
   MQL-a bio bi netočan.
3. **Mini-benchmark** — N ponavljanja na zahtjev, medijan po varijanti. Nije
   mjerodavno (v. gore).
4. **Slobodan unos upita** — proizvoljni Cypher ili MQL.

### Slobodan unos je read-only, i to provodi baza

- **Cypher** se izvršava kroz `session.ExecuteReadAsync` — Neo4j sam odbija
  upise u čitajućoj transakciji (`Writing in read access mode not allowed`).
  To je jamstvo koje provodi baza, ne provjera teksta.
- **MQL** prihvaća samo `find` i `aggregate`; pipeline koji sadrži `$out`
  ili `$merge` se odbija jer te faze pišu.

Uz to je servis u `docker-compose.yml` vezan na `127.0.0.1:8080:8080`, dakle
nije dostupan s drugih strojeva na mreži.

### API

| Metoda | Putanja | Tijelo |
|---|---|---|
| GET | `/api/subjects` | — |
| POST | `/api/run` | `{"query":"Q3","subject":"u0005199"}` ili za Q5 `{"query":"Q5","fromId":"...","toId":"..."}` |
| POST | `/api/bench` | `{"query":"Q3","subject":"u0005199","iterations":20}` |
| POST | `/api/raw` | `{"engine":"cypher"\|"mql","text":"..."}` |

Primjer:

```bash
curl -X POST http://localhost:8080/api/run \
  -H "Content-Type: application/json" \
  -d '{"query":"Q3","subject":"u0005199"}'
```

---

## Rezultati

| Datoteka | Sadržaj |
|---|---|
| `results/timings.csv` | Svako pojedinačno mjerenje (stupac `db`: `neo4j` / `mongo` / `mongo-raw`) |
| `results/db_stats.csv` | Vrijeme punjenja + zauzeće na disku (samo `neo4j` i `mongo` — `mongo-raw` dijeli iste podatke) |
| `results/summary.md` | Tri tablice u markdownu, spremne za seminar |
| `results/baseline-2var/` | **Kopija rezultata iz mjerenja s dvije varijante**, prije uvođenja `mongo-raw` |

`baseline-2var/` postoji jer seminarski rad citira konkretne
brojke iz tog mjerenja, a `bench` prepisuje `timings.csv` i `summary.md`.

---

## Konfiguracija

Sve je konfigurabilno preko `appsettings.json`:

| Sekcija | Polje | Značenje |
|---|---|---|
| `Generator` | `NUsers` | Broj korisnika u sintetickom datasetu |
| `Generator` | `AvgDegree` | Prosjecan broj pratitelja po korisniku (preferential attachment) |
| `Generator` | `Seed` | Seed za deterministicko generiranje (isti seed = identican dataset) |
| `Generator` | `SubjectsCount` | Broj subjekata za Q1-Q4 verifikaciju/mjerenje |
| `Generator` | `PairsCount` | Broj parova za Q5 verifikaciju/mjerenje |
| `Generator` | `MaxHops` | Gornja granica duljine puta za Q5 |
| `Bench` | `Warmup` | Broj odbacenih poziva prije mjerenja, po upitu/subjektu |
| `Bench` | `Measurements` | Broj mjerenja koja se biljeze, po upitu/subjektu |
| `Neo4j` / `Mongo` | `Uri`/`ConnectionString`, ... | Konekcijski podaci |

U Dockeru se konekcijski podaci nadjačavaju varijablama okoline
(`Neo4j__Uri`, `Mongo__ConnectionString` — dvostruka podvlaka je ASP.NET
Core konvencija za ugniježđene ključeve), jer unutar Docker mreže baze nisu
na `localhost` nego na imenima servisa. Vidi `docker-compose.yml`.

Svaka od `generate`/`load-neo4j`/`load-mongo` je idempotentna — može se
ponovno pokrenuti bez ručnog čišćenja.

---

## Struktura projekta

```
docker-compose.yml        # Neo4j + Mongo (identican mem_limit/cpus) + app
Dockerfile                  # multi-stage build web sucelja
.dockerignore                 # bez njega Windowsov obj/ ruši publish u Linux slici
appsettings.json                # sva konfiguracija
Program.cs                        # dispatch podnaredbi
Config/AppConfig.cs                 # kalup za appsettings.json
Models/                               # oblici podataka (Raw/kanonski/Mongo dokumenti)
Data/
  Generator.cs                          # preferential attachment generator
  CsvReader.cs                            # zajednicki CSV citac
  SubjectsAndPairsReader.cs                 # cita subjects.csv/pairs.csv
  raw/                                        # generirani CSV-ovi (izvor istine za obje baze)
Load/
  Neo4jLoader.cs                                # puni Neo4j
  MongoLoader.cs                                  # puni MongoDB
  DockerDiskSize.cs                                 # zauzece na disku preko docker exec
  DbStatsWriter.cs                                    # pise results/db_stats.csv
Queries/
  Neo4jQueries.cs                                       # Q1-Q5 u Cypheru
  MongoQueries.cs                                         # Q1-Q5 app-side (Builders<T>)
  MongoRawQueries.cs                                        # Q1-Q5 kao aggregation pipeline
  QueryCatalog.cs                                             # tekst upita za prikaz u sucelju
Bench/
  Verifier.cs                                                   # usporedba triju varijanti
  VerifyRunner.cs                                                 # ozicuje Queries + Verifier
  Runner.cs                                                         # mjerenje + tablice + summary.md
  Stats.cs                                                            # medijan/min/max/p95
  TimingsWriter.cs                                                      # pise results/timings.csv
Web/
  Server.cs                                                               # ASP.NET Core host, port 8080
  Endpoints.cs                                                              # /api/*
  Seeder.cs                                                                   # auto-punjenje praznih baza
  RawMongoGuard.cs                                                              # read-only ograda za MQL
  Dtos.cs                                                                         # oblici JSON zahtjeva/odgovora
wwwroot/index.html            # sucelje (vanilla JS, bez build koraka)
results/                        # timings.csv, db_stats.csv, summary.md, baseline-2var/
```

---

## Napomena o seminarskom radu

Ovaj repozitorij sadrži **samo kod i mjerenja**. Sam seminarski rad (tekst,
analiza, zaključci) te prateći dokumenti — bilješke o nalazima, popis
svjesno izostavljenog iz opsega i dnevnik izrade — vode se odvojeno i nisu
dio ovog repozitorija.

---

## Metodologija (sažetak)

- Svaki upit postoji u **sve tri** varijante i vraća semantički isti
  rezultat (provjereno preko `verify` — usporedba ID-skupova, ne uređenih
  listi). Iznimka je Q5 kod `mongo-raw`, gdje se uspoređuje samo duljina
  puta — v. gore.
- Obje baze imaju indekse (nikad se ne optimizira jedna baza a druga ostavi
  naivna).
- Mjeri se: warmup(5) + measurements(20) po upitu/subjektu, **interleaved**
  (neo4j, mongo, mongo-raw, pa opet neo4j...) da sve varijante dijele iste
  trenutne uvjete stroja.
- Izvještava se medijan, min, **p95** i max.
- Mongo `mongo` varijanta NIKAD ne simulira traversal čitajući graf
  unaprijed u C# — svaki hop (Q3 fan-out, Q5 BFS) je stvaran upit prema
  bazi.
- Svi upiti vraćaju ograničen broj redaka (mjeri se baza, ne
  serijalizacija/network transfer): Q2/Q3/Q4 preko eksplicitnog
  `LIMIT`/`.Limit()` (500/500/50), Q1 i Q5 po samoj semantici upita.
- Oba spremnika baza imaju **identična** ograničenja resursa
  (`mem_limit: 2g`, `cpus: 2`). Spremnik `app` nema takvo ograničenje —
  ono postoji da usporedba *neo4j prema mongu* bude poštena, a sučelje nije
  dio te usporedbe.
