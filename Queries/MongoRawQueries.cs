using BenchmarkApp.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BenchmarkApp.Queries;

// Treca varijanta uz "neo4j" i "mongo" (programatski, MongoQueries.cs):
// ISTI upiti Q1-Q5 kao MongoQueries, ali kroz JEDAN aggregation pipeline po
// upitu umjesto vise tipiziranih Builders<T> odlazaka u bazu. Nalaz koji ova
// klasa postoji da dokaze: kad se Mongo strana Q3/Q4 prepise u pipeline,
// izmjerena razlika prema Neo4ju kod tih upita nestaje - dakle ranija razlika
// je odrazavala ODLUKU O IMPLEMENTACIJI (vise round-tripova), ne svojstvo
// dokumentnog modela podataka. MongoQueries.cs ostaje NETAKNUT (Odluka #1
// u planu) - ovo je dodatna, ne zamjenska, varijanta.
//
// Potpisi metoda su NAMJERNO identicni MongoQueries - Runner/VerifyRunner
// (i sad i QueryCatalog) tretiraju sve tri varijante uniformno.
//
// Kolekcije se dohvacaju kao IMongoCollection<BsonDocument>, ne kao
// IMongoCollection<UserDocument>/<PostDocument> - izlazni oblik dokumenata
// iz pipelinea (nakon $project/$unwind/...) vise ne odgovara tim POCO-ima
// (npr. "id" umjesto "_id", pridruzena "f"/"u" polja usred obrade).
//
// Parametri (id, fromId, toId, maxHops) ULAZE U PIPELINE ISKLJUCIVO kao BSON
// VRIJEDNOSTI kroz "new BsonDocument(...)" - NIKAD spojeni u tekst upita
// (isto nacelo kao "$id" parametar na Cypher strani). Metode "BuildQnPipeline"
// ispod grade TOCNO ono sto se izvrsava - iste te metode Queries/QueryCatalog.cs
// poziva za prikaz (serijalizirano u JSON), pa tekst prikazan u UI-u i tekst
// koji stvarno ide bazi NIKAD ne mogu razici (Faza 2 plana - jedinstven izvor
// istine).
public static class MongoRawQueries
{
    // Q1 - profil po _id. $project preslika "_id" u "id" polje da izlazni
    // oblik odgovara UserProfile recordu.
    public static BsonDocument[] BuildQ1Pipeline(string id) => new[]
    {
        new BsonDocument("$match", new BsonDocument("_id", id)),
        BsonDocument.Parse("""{ "$project": { "_id": 0, "id": "$_id", "username": 1, "name": 1, "created_at": 1 } }"""),
    };

    public static async Task<UserProfile?> Q1(IMongoDatabase database, string id)
    {
        var users = database.GetCollection<BsonDocument>("users");
        BsonDocument[] pipeline = BuildQ1Pipeline(id);

        List<BsonDocument> results = await users.Aggregate<BsonDocument>(pipeline).ToListAsync();
        if (results.Count == 0)
        {
            return null;
        }

        BsonDocument doc = results[0];
        return new UserProfile(
            doc["id"].AsString,
            doc["username"].AsString,
            doc["name"].AsString,
            doc["created_at"].ToUniversalTime());
    }

    // Q2 - pratitelji subjekta. $lookup dovlaci PUNE profile iz "followers"
    // popisa, $sort {f._id:1} + $limit 500 rade u bazi tocno ono sto Mongov
    // C# strana radi u memoriji (v. komentar u MongoQueries.Q2 o Ordinal
    // sortiranju - Mongov binarni poredak stringova se poklapa za ove
    // ID-eve, cisti ASCII).
    public static BsonDocument[] BuildQ2Pipeline(string id) => new[]
    {
        new BsonDocument("$match", new BsonDocument("_id", id)),
        BsonDocument.Parse("""{ "$lookup": { "from": "users", "localField": "followers", "foreignField": "_id", "as": "f" } }"""),
        BsonDocument.Parse("""{ "$unwind": "$f" }"""),
        BsonDocument.Parse("""{ "$sort": { "f._id": 1 } }"""),
        BsonDocument.Parse("""{ "$limit": 500 }"""),
        BsonDocument.Parse("""{ "$project": { "_id": 0, "id": "$f._id", "username": "$f.username", "name": "$f.name" } }"""),
    };

    public static async Task<List<UserSummary>> Q2(IMongoDatabase database, string id)
    {
        var users = database.GetCollection<BsonDocument>("users");
        BsonDocument[] pipeline = BuildQ2Pipeline(id);

        List<BsonDocument> results = await users.Aggregate<BsonDocument>(pipeline).ToListAsync();
        return results.Select(MapUserSummary).ToList();
    }

    // Q3 - prijatelji prijatelja. $reduce+$setUnion spaja "following" popise
    // svih hop1-korisnika u JEDAN skup UNUTAR baze (zamjena za C#-ov
    // HashSet<string> union iz MongoQueries.Q3), $setDifference makne
    // subjekta samog sebe.
    public static BsonDocument[] BuildQ3Pipeline(string id) => new[]
    {
        new BsonDocument("$match", new BsonDocument("_id", id)),
        BsonDocument.Parse("""{ "$lookup": { "from": "users", "localField": "following", "foreignField": "_id", "as": "hop1" } }"""),
        BsonDocument.Parse("""{ "$project": { "fof": { "$reduce": { "input": "$hop1.following", "initialValue": [], "in": { "$setUnion": ["$$value", "$$this"] } } } } }"""),
        new BsonDocument("$project", new BsonDocument("fof",
            new BsonDocument("$setDifference", new BsonArray { "$fof", new BsonArray { id } }))),
        BsonDocument.Parse("""{ "$unwind": "$fof" }"""),
        BsonDocument.Parse("""{ "$sort": { "fof": 1 } }"""),
        BsonDocument.Parse("""{ "$limit": 500 }"""),
        BsonDocument.Parse("""{ "$lookup": { "from": "users", "localField": "fof", "foreignField": "_id", "as": "u" } }"""),
        BsonDocument.Parse("""{ "$unwind": "$u" }"""),
        BsonDocument.Parse("""{ "$project": { "_id": 0, "id": "$u._id", "username": "$u.username", "name": "$u.name" } }"""),
    };

    public static async Task<List<UserSummary>> Q3(IMongoDatabase database, string id)
    {
        var users = database.GetCollection<BsonDocument>("users");
        BsonDocument[] pipeline = BuildQ3Pipeline(id);

        List<BsonDocument> results = await users.Aggregate<BsonDocument>(pipeline).ToListAsync();
        return results.Select(MapUserSummary).ToList();
    }

    // Q4 - feed. Prvi $lookup spaja subjektove "following" na tude "posts"
    // preko author_id (JEDAN pipeline umjesto MongoQueries.Q4-ina dva
    // odvojena Find poziva), drugi $lookup dovlaci username autora,
    // $arrayElemAt uzima prvi (jedini) element rezultata drugog $lookupa.
    public static BsonDocument[] BuildQ4Pipeline(string id) => new[]
    {
        new BsonDocument("$match", new BsonDocument("_id", id)),
        BsonDocument.Parse("""{ "$lookup": { "from": "posts", "localField": "following", "foreignField": "author_id", "as": "feed" } }"""),
        BsonDocument.Parse("""{ "$unwind": "$feed" }"""),
        BsonDocument.Parse("""{ "$sort": { "feed.created_at": -1, "feed._id": 1 } }"""),
        BsonDocument.Parse("""{ "$limit": 50 }"""),
        BsonDocument.Parse("""{ "$lookup": { "from": "users", "localField": "feed.author_id", "foreignField": "_id", "as": "autor" } }"""),
        BsonDocument.Parse("""{ "$project": { "_id": 0, "post_id": "$feed._id", "author_id": "$feed.author_id", "author_username": { "$arrayElemAt": ["$autor.username", 0] }, "text": "$feed.text", "created_at": "$feed.created_at" } }"""),
    };

    public static async Task<List<FeedItem>> Q4(IMongoDatabase database, string id)
    {
        var users = database.GetCollection<BsonDocument>("users");
        BsonDocument[] pipeline = BuildQ4Pipeline(id);

        List<BsonDocument> results = await users.Aggregate<BsonDocument>(pipeline).ToListAsync();
        return results.Select(MapFeedItem).ToList();
    }

    /// <summary>
    /// Q5 (raw MQL, $graphLookup) - dokumentirano odstupanje od Neo4j i
    /// programatske Mongo varijante: $graphLookup vraca SAMO DUBINU svakog
    /// dosegnutog cvora ("depthField"), NE i lanac cvorova kojim se do njega
    /// doslo (za razliku od Cypherovog shortestPath(), koji vraca cijeli put,
    /// i BFS-a u MongoQueries.Q5, koji put rekonstruira preko parent[] mape).
    /// Zato ova metoda vraca PathResult s ISPRAVNOM Length, ali PRAZNIM Path -
    /// nema nacina rekonstruirati put iz onoga sto $graphLookup vraca bez
    /// dodatnog upita. Verifier.CompareLength namjerno usporeduje samo
    /// duljinu (v. komentar u Bench/Verifier.cs), pa "verify" ovime i dalje
    /// prolazi - ali "prazan Path" ovdje znaci "nedostupno", ne "nema puta",
    /// i UI to MORA prikazati eksplicitno (v. Bench/VerifyRunner.cs napomena
    /// i NOTES.md stavka o ovome), da zeleni rezultat verifikacije nitko ne
    /// procita kao dokaz da se putevi podudaraju.
    ///
    /// Uz to: $graphLookup obilazi CIJELI dosezni podgraf do maxDepth PRIJE
    /// nego provjeri je li cilj unutra - nema rani prekid kakav ima BFS
    /// (MongoQueries.Q5) ili shortestPath(). Q5 je zato na ovoj varijanti
    /// osjetno sporiji - to je legitiman nalaz, ne bug.
    /// </summary>
    public static BsonDocument[] BuildQ5Pipeline(string fromId, string toId, int maxHops) => new[]
    {
        new BsonDocument("$match", new BsonDocument("_id", fromId)),
        new BsonDocument("$graphLookup", new BsonDocument
        {
            { "from", "users" },
            { "startWith", "$following" },
            { "connectFromField", "following" },
            { "connectToField", "_id" },
            { "as", "reach" },
            { "maxDepth", maxHops - 1 },
            { "depthField", "d" },
        }),
        new BsonDocument("$project", new BsonDocument("hit", new BsonDocument("$filter", new BsonDocument
        {
            { "input", "$reach" },
            { "as", "r" },
            { "cond", new BsonDocument("$eq", new BsonArray { "$$r._id", toId }) },
        }))),
        BsonDocument.Parse("""{ "$project": { "len": { "$add": [ { "$min": "$hit.d" }, 1 ] } } }"""),
    };

    public static async Task<PathResult> Q5(IMongoDatabase database, string fromId, string toId, int maxHops)
    {
        var users = database.GetCollection<BsonDocument>("users");

        if (fromId == toId)
        {
            return new PathResult(new List<string> { fromId }, 0);
        }

        BsonDocument[] pipeline = BuildQ5Pipeline(fromId, toId, maxHops);
        List<BsonDocument> results = await users.Aggregate<BsonDocument>(pipeline).ToListAsync();

        // "len" nedostaje/je null kad $min nad praznim "$hit.d" nizom (cilj
        // nije unutar dosega) - isto konvencija kao ostale dvije varijante:
        // Length=-1, prazan Path.
        if (results.Count == 0 || !results[0].TryGetValue("len", out BsonValue? lenValue) || lenValue.IsBsonNull)
        {
            return new PathResult(new List<string>(), -1);
        }

        return new PathResult(new List<string>(), lenValue.ToInt32());
    }

    private static UserSummary MapUserSummary(BsonDocument doc) =>
        new(doc["id"].AsString, doc["username"].AsString, doc["name"].AsString);

    private static FeedItem MapFeedItem(BsonDocument doc) => new(
        doc["post_id"].AsString,
        doc["author_id"].AsString,
        doc["author_username"].AsString,
        doc["text"].AsString,
        doc["created_at"].ToUniversalTime());
}
