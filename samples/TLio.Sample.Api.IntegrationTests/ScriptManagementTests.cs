using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace TLio.Sample.Api.IntegrationTests;

[TestFixture]
public class ScriptManagementTests
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

    [Test]
    public async Task GetScripts_EmptyRegistry_ReturnsEmptyArray()
    {
        var response = await _client.GetAsync("/scripts");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
    }

    [Test]
    public async Task GetScripts_AfterRegistration_ListsSlug()
    {
        var body = """{"slug":"list-me","script":"[]"}""";
        await _client.PostAsync("/scripts", new StringContent(body, Encoding.UTF8, "application/json"));

        var response = await _client.GetAsync("/scripts");
        var content = await response.Content.ReadAsStringAsync();

        Assert.That(content, Does.Contain("list-me"));
    }

    [Test]
    public async Task DeleteScript_ExistingSlug_Returns204()
    {
        var body = """{"slug":"del-me","script":"[]"}""";
        await _client.PostAsync("/scripts", new StringContent(body, Encoding.UTF8, "application/json"));

        var response = await _client.DeleteAsync("/scripts/del-me");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task DeleteScript_UnknownSlug_Returns404()
    {
        var response = await _client.DeleteAsync("/scripts/never-registered");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DeleteScript_AfterDelete_SlugIsGone()
    {
        var body = """{"slug":"gone-after-delete","script":"[]"}""";
        await _client.PostAsync("/scripts", new StringContent(body, Encoding.UTF8, "application/json"));
        await _client.DeleteAsync("/scripts/gone-after-delete");

        var runResponse = await _client.PostAsync("/run/gone-after-delete",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.That(runResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DeleteScript_InvalidSlugFormat_Returns400()
    {
        var response = await _client.DeleteAsync("/scripts/INVALID FORMAT");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
