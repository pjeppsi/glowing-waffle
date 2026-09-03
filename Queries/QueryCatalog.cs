using MongoDB.Bson;

namespace BenchmarkApp.Queries;

// Jedina klasa koju UI sloj (Web/*) smije pitati "kakav je tekst upita bas
// izvrsen?". Namjerno NE drzi vlastite kopije teksta - svaka grana ispod
// poziva TOCNO ona polja/metode koje Neo4jQueries/MongoQueries/MongoRawQueries
// stvarno koriste za izvrsavanje (Faza 2 plana: "tekst upita kao jedinstveni
// izvor istine" - ako se prikaz i izvrsavanje drze odvojeno, razidu se pri
// prvoj izmjeni jedne strane bez druge).
public static class QueryCatalog
{
    // maxHops dolazi iz konfiguracije (appsettings.json), ne od korisnika -
    // isto opravdanje kao u Neo4jQueries.Q5Cypher.
    public static string Neo4jText(string queryId, string subjectOrFromId, string? toId, int maxHops) => queryId switch
    {
        "Q1" => Neo4jQueries.Q1Cypher,
        "Q2" => Neo4jQueries.Q2Cypher,
        "Q3" => Neo4jQueries.Q3Cypher,
        "Q4" => Neo4jQueries.Q4Cypher,
        "Q5" => Neo4jQueries.Q5Cypher(maxHops),
        _ => throw new ArgumentException($"Nepoznat upit: {queryId}"),
    };

    // "mongo" (programatska) varijanta nema jedan tekst upita - v. napomena
    // uz *Snippet polja u MongoQueries.cs i Odluku #4. Vraceni tekst UI mora
    // prikazati uz jasnu oznaku da je ovo VISE odvojenih poziva, ne jedan upit.
    public static string MongoProgrammaticText(string queryId) => queryId switch
    {
        "Q1" => MongoQueries.Q1Snippet,
        "Q2" => MongoQueries.Q2Snippet,
        "Q3" => MongoQueries.Q3Snippet,
        "Q4" => MongoQueries.Q4Snippet,
        "Q5" => MongoQueries.Q5Snippet,
        _ => throw new ArgumentException($"Nepoznat upit: {queryId}"),
    };

    // Raw MQL pipeline se za prikaz RENDERIRA s konkretnim subjektom/parom
    // (parametri kao stvarne vrijednosti u istom BsonDocument nizu koji ide
    // bazi - v. napomena u planu, Faza 2) i serijalizira u citljiv JSON.
    // Ovo NIJE odvojena "pipeline predlozak" kopija - BuildQnPipeline metode
    // u MongoRawQueries.cs su iste one koje se izvrsavaju.
    public static string MongoRawText(string queryId, string subjectOrFromId, string? toId, int maxHops)
    {
        BsonDocument[] pipeline = queryId switch
        {
            "Q1" => MongoRawQueries.BuildQ1Pipeline(subjectOrFromId),
            "Q2" => MongoRawQueries.BuildQ2Pipeline(subjectOrFromId),
            "Q3" => MongoRawQueries.BuildQ3Pipeline(subjectOrFromId),
            "Q4" => MongoRawQueries.BuildQ4Pipeline(subjectOrFromId),
            "Q5" => MongoRawQueries.BuildQ5Pipeline(subjectOrFromId, toId ?? "", maxHops),
            _ => throw new ArgumentException($"Nepoznat upit: {queryId}"),
        };

        return string.Join(",\n", pipeline.Select(stage => stage.ToJson()));
    }
}
