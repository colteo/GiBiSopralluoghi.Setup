using System.ServiceModel;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

// ── Configurazione ──────────────────────────────────────────────
const string Url = "https://org8ec7b174.crm4.dynamics.com/";   // il tuo URL istanza

const string AutoreNome = "matteocolazzo";
const string AutoreEtichetta = "Matteo Colazzo";
const string AutorePrefisso = "mc";
const int AutorePrefissoScelte = 47700;   // i valori delle scelte partiranno da 477000000

const string SoluzioneNome = "GiBiSopralluoghi";
const string SoluzioneEtichetta = "GiBi Sopralluoghi";

// ── Connessione ─────────────────────────────────────────────────
var schema = JsonSerializer.Deserialize<Schema>(File.ReadAllText("schema.json"),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

using var client = new ServiceClient(
    $"AuthType=OAuth;Url={Url};" +
    "AppId=51f81489-12ee-4a9e-aaae-a2591f45987d;" +
    "RedirectUri=http://localhost;LoginPrompt=Auto");
if (!client.IsReady)
{
    Console.WriteLine($"Connessione fallita: {client.LastError}");
    return;
}
Console.WriteLine($"Connesso a {client.ConnectedOrgFriendlyName}");

int lcid = client.RetrieveMultiple(new QueryExpression("organization") { ColumnSet = new ColumnSet("languagecode") })
    .Entities[0].GetAttributeValue<int>("languagecode");

// ── 1. Autore ───────────────────────────────────────────────────
var autore = TrovaPerNome("publisher", AutoreNome, "customizationprefix");
Guid autoreId;
string prefisso;
if (autore is null)
{
    autoreId = client.Create(new Entity("publisher")
    {
        ["uniquename"] = AutoreNome,
        ["friendlyname"] = AutoreEtichetta,
        ["customizationprefix"] = AutorePrefisso,
        ["customizationoptionvalueprefix"] = AutorePrefissoScelte
    });
    prefisso = AutorePrefisso;
    Console.WriteLine($"\nAutore creato: {AutoreEtichetta} (prefisso '{prefisso}')");
}
else
{
    autoreId = autore.Id;
    prefisso = autore.GetAttributeValue<string>("customizationprefix");
    Console.WriteLine($"\nAutore già presente: {AutoreEtichetta} (prefisso '{prefisso}')");
    if (prefisso != AutorePrefisso)
        Console.WriteLine($"  Attenzione: il prefisso esistente è '{prefisso}', non '{AutorePrefisso}'. Uso '{prefisso}'.");
}

// ── 2. Soluzione ────────────────────────────────────────────────
if (TrovaPerNome("solution", SoluzioneNome) is null)
{
    client.Create(new Entity("solution")
    {
        ["uniquename"] = SoluzioneNome,
        ["friendlyname"] = SoluzioneEtichetta,
        ["publisherid"] = new EntityReference("publisher", autoreId),
        ["version"] = "1.0.0.0"
    });
    Console.WriteLine($"Soluzione creata: {SoluzioneEtichetta}");
}
else
{
    Console.WriteLine($"Soluzione già presente: {SoluzioneEtichetta}");
}

// ── 3. Tabelle e colonne ────────────────────────────────────────
foreach (var tabella in schema.Tables)
{
    var nomeTabella = NomeLogico(tabella.Name);
    Console.WriteLine($"\nTabella {nomeTabella}:");

    var metadati = LeggiTabella(nomeTabella);
    if (metadati is null)
    {
        client.Execute(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                SchemaName = $"{prefisso}_{tabella.Name}",
                DisplayName = L(tabella.Display),
                DisplayCollectionName = L(tabella.Plural),
                OwnershipType = OwnershipTypes.UserOwned,   // serve ai ruoli di sicurezza
                IsActivity = false
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                SchemaName = $"{prefisso}_Name",
                DisplayName = L(tabella.Primary.Display),
                RequiredLevel = Livello(tabella.Primary.Required),
                MaxLength = 200,
                FormatName = StringFormatName.Text,
                AutoNumberFormat = tabella.Primary.AutoNumber
            },
            HasActivities = false,
            HasNotes = false,
            SolutionUniqueName = SoluzioneNome
        });
        Console.WriteLine("  tabella creata");
        metadati = LeggiTabella(nomeTabella)!;
    }
    else
    {
        Console.WriteLine("  tabella già presente");
    }

    var esistenti = metadati.Attributes.Select(a => a.LogicalName).ToHashSet();
    foreach (var colonna in tabella.Columns)
    {
        var attributo = CreaMetadati(colonna);
        var nome = attributo.SchemaName.ToLowerInvariant();
        if (esistenti.Contains(nome))
        {
            Console.WriteLine($"  già presente  {nome}");
            continue;
        }
        client.Execute(new CreateAttributeRequest
        {
            EntityName = nomeTabella,
            Attribute = attributo,
            SolutionUniqueName = SoluzioneNome
        });
        Console.WriteLine($"  creata        {nome}");
    }
}

// ── 4. Relazioni ────────────────────────────────────────────────
Console.WriteLine("\nRelazioni:");
foreach (var rel in schema.Relationships)
{
    var uno = NomeLogico(rel.One);
    var molti = NomeLogico(rel.Many);
    var lookup = $"{prefisso}_{rel.Lookup}";

    if (LeggiTabella(molti)!.Attributes.Any(a => a.LogicalName == lookup.ToLowerInvariant()))
    {
        Console.WriteLine($"  già presente  {uno} → {molti}");
        continue;
    }

    client.Execute(new CreateOneToManyRequest
    {
        OneToManyRelationship = new OneToManyRelationshipMetadata
        {
            SchemaName = $"{prefisso}_{rel.Name}",
            ReferencedEntity = uno,      // lato "uno"
            ReferencingEntity = molti,   // lato "molti": qui nasce la colonna Ricerca
            CascadeConfiguration = Comportamento(rel.Behavior),
            AssociatedMenuConfiguration = new AssociatedMenuConfiguration
            {
                Behavior = AssociatedMenuBehavior.UseLabel,
                Group = AssociatedMenuGroup.Details,
                Label = L(rel.Label),
                Order = 10000
            }
        },
        Lookup = new LookupAttributeMetadata
        {
            SchemaName = lookup,
            DisplayName = L(rel.LookupDisplay),
            RequiredLevel = Livello(rel.Required)
        },
        SolutionUniqueName = SoluzioneNome
    });
    Console.WriteLine($"  creata        {uno} → {molti} ({rel.Behavior})");
}

// ── 5. Pubblicazione ────────────────────────────────────────────
Console.WriteLine("\nPubblicazione delle personalizzazioni...");
client.Execute(new PublishAllXmlRequest());
Console.WriteLine("Fatto.");


// ── Funzioni di supporto ────────────────────────────────────────
Label L(string testo) => new(testo, lcid);

AttributeRequiredLevelManagedProperty Livello(bool obbligatorio) =>
    new(obbligatorio ? AttributeRequiredLevel.ApplicationRequired : AttributeRequiredLevel.None);

// Le tabelle definite nello schema prendono il prefisso; le altre (es. "contact") restano così
string NomeLogico(string nome) =>
    schema.Tables.Any(t => t.Name == nome) ? $"{prefisso}_{nome}".ToLowerInvariant() : nome;

Entity? TrovaPerNome(string tabella, string nomeUnivoco, params string[] colonne) =>
    client.RetrieveMultiple(new QueryExpression(tabella)
    {
        ColumnSet = new ColumnSet(colonne),
        Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, nomeUnivoco) } }
    }).Entities.FirstOrDefault();

EntityMetadata? LeggiTabella(string nomeLogico)
{
    try
    {
        return ((RetrieveEntityResponse)client.Execute(new RetrieveEntityRequest
        {
            LogicalName = nomeLogico,
            EntityFilters = EntityFilters.Attributes,
            RetrieveAsIfPublished = true
        })).EntityMetadata;
    }
    catch (FaultException<OrganizationServiceFault>)
    {
        return null;   // la tabella non esiste ancora
    }
}

CascadeConfiguration Comportamento(string tipo) => tipo switch
{
    // Padre-figlio: cancellare o riassegnare il padre agisce anche sui figli
    "parental" => new CascadeConfiguration
    {
        Assign = CascadeType.Cascade, Delete = CascadeType.Cascade, Merge = CascadeType.Cascade,
        Reparent = CascadeType.Cascade, Share = CascadeType.Cascade, Unshare = CascadeType.Cascade
    },
    // Impedisce di cancellare il padre finché ha figli collegati
    "restrict" => new CascadeConfiguration
    {
        Assign = CascadeType.NoCascade, Delete = CascadeType.Restrict, Merge = CascadeType.Cascade,
        Reparent = CascadeType.NoCascade, Share = CascadeType.NoCascade, Unshare = CascadeType.NoCascade
    },
    // Cancellando il padre, i figli restano ma perdono il collegamento
    "removeLink" => new CascadeConfiguration
    {
        Assign = CascadeType.NoCascade, Delete = CascadeType.RemoveLink, Merge = CascadeType.Cascade,
        Reparent = CascadeType.NoCascade, Share = CascadeType.NoCascade, Unshare = CascadeType.NoCascade
    },
    _ => throw new InvalidOperationException($"Comportamento sconosciuto: {tipo}")
};

AttributeMetadata CreaMetadati(ColumnDef c)
{
    AttributeMetadata a = c.Type switch
    {
        "text" => new StringAttributeMetadata { MaxLength = c.MaxLength ?? 100, FormatName = StringFormatName.Text },
        "memo" => new MemoAttributeMetadata { MaxLength = c.MaxLength ?? 4000, Format = StringFormat.TextArea },
        "int" => new IntegerAttributeMetadata { Format = IntegerFormat.None, MinValue = (int?)c.Min, MaxValue = (int?)c.Max },
        "decimal" => new DecimalAttributeMetadata { Precision = c.Precision ?? 2, MinValue = c.Min, MaxValue = c.Max },
        "date" => new DateTimeAttributeMetadata { Format = DateTimeFormat.DateOnly, DateTimeBehavior = DateTimeBehavior.DateOnly },
        "choice" => new PicklistAttributeMetadata { OptionSet = Opzioni(c) },
        "multichoice" => new MultiSelectPicklistAttributeMetadata { OptionSet = Opzioni(c) },
        "image" => new ImageAttributeMetadata { CanStoreFullImage = true, MaxSizeInKB = 10_240, IsPrimaryImage = c.PrimaryImage },
        _ => throw new InvalidOperationException($"Tipo di colonna sconosciuto: {c.Type}")
    };
    a.SchemaName = $"{prefisso}_{c.Name}";
    a.DisplayName = L(c.Display);
    a.Description = c.Description is null ? null : L(c.Description);
    a.RequiredLevel = Livello(false);
    return a;
}

OptionSetMetadata Opzioni(ColumnDef c)
{
    var set = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
    // Valore null: Dataverse numera le opzioni con il prefisso scelte dell'autore
    foreach (var v in c.Options ?? new List<string>()) set.Options.Add(new OptionMetadata(L(v), null));
    return set;
}

// ── Struttura di schema.json ────────────────────────────────────
record Schema(List<TableDef> Tables, List<RelationDef> Relationships);
record TableDef(string Name, string Display, string Plural, PrimaryDef Primary, List<ColumnDef> Columns);
record PrimaryDef(string Display, bool Required = true, string? AutoNumber = null);
record ColumnDef(string Type, string Name, string Display, string? Description = null,
    int? MaxLength = null, decimal? Min = null, decimal? Max = null, int? Precision = null,
    List<string>? Options = null, bool PrimaryImage = false);
record RelationDef(string Name, string One, string Many, string Lookup, string LookupDisplay,
    string Label, string Behavior, bool Required = false);