using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace TLio.Sample.Api.IntegrationTests;

/// <summary>
/// The API must have the extension packs registered.
///
/// It previously used a bare ParseOptions.CreateDefault(), unlike the CLI sample. A
/// script calling a pack function then failed as a whole, so the endpoint returned no
/// output at all rather than a partial result — and no test noticed, because every
/// bundled script was a single command with a literal value.
/// </summary>
[TestFixture]
public class ExtensionPackAvailabilityTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>();
        _client  = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(HttpStatusCode Status, string Body)> RegisterAndRun(
        string slug, string script, string document)
    {
        var registration = $$"""{"slug":"{{slug}}","script":{{System.Text.Json.JsonSerializer.Serialize(script)}}}""";
        var registered = await _client.PostAsync("/scripts",
            new StringContent(registration, Encoding.UTF8, "application/json"));
        Assert.That(registered.StatusCode,
            Is.EqualTo(HttpStatusCode.Created).Or.EqualTo(HttpStatusCode.OK),
            "registration failed — the script did not compile");

        var response = await _client.PostAsync($"/run/{slug}",
            new StringContent(document, Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task MathPack_IsRegistered()
    {
        var (status, body) = await RegisterAndRun("pack-math",
            """[{"command":"put","path":"$.total","value":"=sum($.nums[*])"}]""",
            """{"nums":[10,20,30]}""");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body);
        Assert.That(body, Does.Contain("60"));
    }

    [Test]
    public async Task TextPack_IsRegistered()
    {
        var (status, body) = await RegisterAndRun("pack-text",
            """[{"command":"put","path":"$.out","value":"=toupper($.name)"}]""",
            """{"name":"ada"}""");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body);
        Assert.That(body, Does.Contain("ADA"));
    }

    [Test]
    public async Task EtlPack_IsRegistered()
    {
        var (status, body) = await RegisterAndRun("pack-etl",
            """[{"command":"flatten","path":"$.data","flattenSettings":{"delimiter":"_","preserveTypes":false,"metadataPath":""}}]""",
            """{"data":{"user":{"name":"Alice"}}}""");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body);
        Assert.That(body, Does.Contain("user_name"));
    }

    [Test]
    public async Task TimeDatePack_IsRegistered()
    {
        var (status, body) = await RegisterAndRun("pack-date",
            """[{"command":"put","path":"$.first","value":"=mindate($.a, $.b)"}]""",
            """{"a":"2024-12-31","b":"2024-01-01"}""");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body);
        Assert.That(body, Does.Contain("2024-01-01"));
    }
}
