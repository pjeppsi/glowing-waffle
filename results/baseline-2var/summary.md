# Rezultati benchmarka

## Tablica 1: brzina po upitu (medijan)

| Upit | Neo4j medijan (ms) | Mongo medijan (ms) | Omjer (brza baza) |
|---|---|---|---|
| Q1 | 4.92 | 2.11 | Mongo 2.3x |
| Q2 | 4.85 | 2.91 | Mongo 1.7x |
| Q3 | 8.21 | 48.10 | Neo4j 5.9x |
| Q4 | 7.23 | 18.08 | Neo4j 2.5x |
| Q5 | 5.98 | 4.94 | Mongo 1.2x |

## Tablica 2: raspon mjerenja po upitu i bazi

| Upit | Baza | Min (ms) | Max (ms) | Prosj. broj vracenih redaka |
|---|---|---|---|---|
| Q1 | neo4j | 3.66 | 11.46 | 1.0 |
| Q1 | mongo | 1.42 | 11.37 | 1.0 |
| Q2 | neo4j | 3.95 | 8.02 | 2.6 |
| Q2 | mongo | 1.68 | 10.11 | 2.6 |
| Q3 | neo4j | 6.82 | 12.44 | 44.8 |
| Q3 | mongo | 30.73 | 91.49 | 44.8 |
| Q4 | neo4j | 5.53 | 12.52 | 49.4 |
| Q4 | mongo | 11.98 | 45.65 | 49.4 |
| Q5 | neo4j | 4.42 | 162.71 | 2.3 |
| Q5 | mongo | 1.78 | 24.73 | 2.3 |

## Tablica 3: ucitavanje i zauzece na disku

| Baza | Vrijeme ucitavanja (s) | Zauzece na disku (MB) |
|---|---|---|
| neo4j | 15.305 | 522.0 |
| mongo | 1.899 | 305.9 |
