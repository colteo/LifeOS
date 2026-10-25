using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LifeOS.Application.Assistant;
using LifeOS.Domain.Assistant;
using LifeOS.Infrastructure.Interpretation;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeOS.UnitTests.Assistant;

// AI-007 F3: the .NET side of the canonical interpretation contract, docs/contracts/assistant-interpretation/v1
// — the same files the Python suite checks (src/python/lifeos-ai/tests/test_assistant_contract.py). The golden
// cases go through the real client (strict deserialisation) and the validator, exactly as a service answer
// does, and must get the same verdict as in Python: valid, "shape" (refused while reading) or the same rule
// code. The schema's names, enum values and nullability, the limits, the example request and the HTTP error
// mapping are compared with the .NET types, so neither side can drift without a failing test.
public class AssistantContractTests
{
    private const string ServiceKey = "test-service-key-0123456789abcdefghijklmnop";

    private static readonly string Contract = Path.Combine(RepositoryRoot(), "docs", "contracts", "assistant-interpretation", "v1");

    private static readonly JsonNamingPolicy Snake = JsonNamingPolicy.SnakeCaseLower;

    public static TheoryData<string> CaseNames => new(Cases().Select(item => (string)item!["name"]!));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task EveryGoldenCase_GetsTheContractsVerdict(string name)
    {
        var item = Cases().Single(entry => (string)entry!["name"]! == name)!;

        var verdict = await VerdictAsync(item["interpretation"]!, (string)item["message"]!);

        Assert.Equal((string?)item["violation"], verdict);
    }

    [Fact]
    public void TheSchemasEnumValues_AreTheDotNetEnums()
    {
        var schema = Load("interpretation.schema.json");
        var properties = schema["properties"]!;
        var date = properties["finance"]!["properties"]!["date"]!["properties"]!;

        Assert.Equal(Names<AssistantIntent>(), Enum(properties["intent"]!));
        Assert.Equal(Names<AssistantUnsupportedReason>(), Enum(properties["unsupported_reason"]!));
        Assert.Equal(Names<AssistantFieldState>(), Enum(date["state"]!));
        Assert.Equal(Names<AssistantDateKind>(), Enum(date["kind"]!));
        Assert.Equal(Names<DayOfWeek>(), Enum(date["weekday"]!));
        Assert.Equal(AssistantFinanceFieldNames.All.ToHashSet(), Enum(properties["ambiguous_fields"]!["items"]!));
        Assert.Equal([AssistantInterpretationProtocol.Version], properties["protocol_version"]!["enum"]!.AsArray().Select(value => (int)value!));
    }

    // Every object of the schema lists exactly the members of the .NET record (snake_case), all required, with
    // "null" allowed exactly where the .NET member is nullable.
    [Fact]
    public void TheSchemasObjects_AreTheDotNetRecords()
    {
        var schema = Load("interpretation.schema.json");
        var finance = schema["properties"]!["finance"]!;

        AssertObject(schema, typeof(AssistantInterpretation));
        AssertObject(finance, typeof(AssistantFinanceFields));

        foreach (var (name, field) in finance["properties"]!.AsObject())
        {
            AssertObject(field!, name switch
            {
                "date" => typeof(AssistantDateField),
                "time" => typeof(AssistantTimeField),
                _ => typeof(AssistantTextField)
            });
        }
    }

    [Fact]
    public void VersionsLocaleAndLimits_AreTheContracts()
    {
        var protocol = Load("protocol.json");
        var limits = protocol["limits"]!;

        Assert.Equal((AssistantInterpretationProtocol.Version, AssistantInterpretationProtocol.Version, AssistantInterpretationProtocol.Locale),
            ((int)protocol["protocol_version"]!, (int)protocol["output_version"]!, (string)protocol["locale"]!));
        int[] dotNet =
        [
            AssistantMessage.MaxUserContentLength, AssistantInterpretationProtocol.MaxValueLength, AssistantInterpretationProtocol.MaxNoteLength,
            AssistantInterpretationProtocol.MaxSpanLength, AssistantInterpretationProtocol.MaxCategoryNames,
            AssistantInterpretationProtocol.MaxCategoryNameLength, AssistantInterpretationValidator.MaxDaysAgo
        ];
        string[] keys = ["message_length", "value_length", "note_length", "span_length", "category_names_per_type", "category_name_length", "days_ago"];

        Assert.Equal(dotNet, keys.Select(key => (int)limits[key]!));
        Assert.Equal(keys.Order(), limits.AsObject().Select(limit => limit.Key).Order());
    }

    // What .NET sends is, byte for byte as JSON, the example the Python suite validates.
    [Fact]
    public async Task TheRequestDotNetSends_IsTheContractsExample()
    {
        var request = new AssistantInterpretationRequest(1, "Revolut", new AssistantInterpretationContext("it-IT", new AssistantFinanceContext(
            new AssistantOpenDraftContext("finance.transaction.create", "expense", ["type", "amount", "note"], ["account", "category"], "account"),
            ["Ristoranti", "Spesa"], [])));
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, """{"error": {"code": "provider_unavailable"}}""");

        await Client(handler).InterpretAsync(request, CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(Load("request.example.json"), JsonNode.Parse(handler.Bodies.Single())));
    }

    public static TheoryData<int, string, string> Errors
    {
        get
        {
            var data = new TheoryData<int, string, string>();

            foreach (var error in Load("protocol.json")["errors"]!.AsArray())
            {
                data.Add((int)error!["status"]!, (string)error["code"]!, (string)error["failure"]!);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task EveryServiceError_MapsToTheContractsFailure(int status, string code, string failure)
    {
        var handler = new ScriptedHandler((HttpStatusCode)status, new JsonObject { ["error"] = new JsonObject { ["code"] = code } }.ToJsonString());

        var result = await Client(handler).InterpretAsync(Request, CancellationToken.None);

        Assert.Equal(failure, Snake.ConvertName(result.Failure!.Value.ToString()));
    }

    // ---- Helpers ----

    private static readonly AssistantInterpretationRequest Request = new(1, "Speso 5",
        new AssistantInterpretationContext("it-IT", new AssistantFinanceContext(null, ["Ristoranti"], [])));

    // As a service answer: through the client (shape), then the processor's validator (rules).
    private static async Task<string?> VerdictAsync(JsonNode interpretation, string message)
    {
        var body = new JsonObject
        {
            ["output_version"] = 1,
            ["provider"] = "fake",
            ["model"] = "fake-model",
            ["prompt_version"] = "assistant-finance-interpret-v1",
            ["interpretation"] = interpretation.DeepClone()
        };

        var result = await Client(new ScriptedHandler(HttpStatusCode.OK, body.ToJsonString())).InterpretAsync(Request, CancellationToken.None);

        return result.Failure switch
        {
            null => AssistantInterpretationValidator.Validate(result.Interpretation, message),
            AssistantInterpretationFailure.InvalidOutput => "shape",
            var other => throw new InvalidOperationException($"Unexpected failure {other}.")
        };
    }

    private static void AssertObject(JsonNode schema, Type record)
    {
        var properties = schema["properties"]!.AsObject();
        var nullability = new NullabilityInfoContext();
        var members = record.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => Snake.ConvertName(property.Name),
                property => nullability.Create(property).ReadState == NullabilityState.Nullable);

        Assert.Equal(members.Keys.Order(), properties.Select(property => property.Key).Order());
        Assert.Equal(properties.Select(property => property.Key), schema["required"]!.AsArray().Select(name => (string)name!));
        Assert.False((bool)schema["additionalProperties"]!);

        foreach (var (name, property) in properties)
        {
            var nullable = property!["type"] is JsonArray types && types.Any(type => (string?)type == "null");
            Assert.True(members[name] == nullable, $"{record.Name}.{name}: nullable in .NET {members[name]}, in the schema {nullable}.");
        }
    }

    private static HashSet<string> Names<T>() where T : struct, Enum => System.Enum.GetNames<T>().Select(Snake.ConvertName).ToHashSet();

    private static HashSet<string> Enum(JsonNode property) =>
        property["enum"]!.AsArray().Where(value => value is not null).Select(value => (string)value!).ToHashSet();

    private static JsonArray Cases() => Load("cases.json")["cases"]!.AsArray();

    private static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Contract, name)))!;

    private static AssistantInterpreterClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000/") }, ServiceKey, NullLogger<AssistantInterpreterClient>.Instance);

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", ".."));

    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
