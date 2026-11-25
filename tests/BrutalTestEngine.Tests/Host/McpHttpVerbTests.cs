using BrutalTestEngine.Host;

namespace BrutalTestEngine.Tests.Host;

/// <summary>
/// Covers the HTTP verb policy of the MCP endpoint.
/// </summary>
/// <remarks>
/// This exists because of a real defect: GET used to be forwarded to the transport, which throws
/// <c>InvalidOperationException</c> in stateless mode, and the catch-all turned that into a 500. A 500 tells a
/// client the server is broken, so it retries — when the truthful answer is that the endpoint does not offer a
/// server-to-client stream at all. The status codes are pinned here so that cannot drift back.
/// </remarks>
[TestFixture]
public sealed class McpHttpVerbTests
{
    [Test]
    public void Post_is_handed_to_the_body_handler()
    {
        // Zero is the sentinel for "this verb needs the request body read", not a status code.
        Assert.That(McpServerThread.ResponseStatusFor("POST"), Is.Zero);
    }

    [Test]
    public void Get_is_405_rather_than_a_server_error()
    {
        // A stateless transport has no session for a stream to belong to, so the capability is absent — which
        // is a 405. A 500 here is what made a client treat a documented limitation as a fault.
        Assert.That(McpServerThread.ResponseStatusFor("GET"), Is.EqualTo(405));
    }

    [Test]
    public void Delete_is_204_because_there_is_no_session_to_end()
    {
        // The request's intent is satisfied by doing nothing, so it is a success rather than an unsupported verb.
        Assert.That(McpServerThread.ResponseStatusFor("DELETE"), Is.EqualTo(204));
    }

    [TestCase("PUT")]
    [TestCase("PATCH")]
    [TestCase("HEAD")]
    [TestCase("")]
    public void An_unsupported_verb_is_405(string method)
    {
        Assert.That(McpServerThread.ResponseStatusFor(method), Is.EqualTo(405));
    }

    [Test]
    public void The_verb_test_is_case_sensitive_because_http_methods_are()
    {
        // HttpListener reports the method as sent. A lowercase verb is a malformed request, not a POST.
        Assert.That(McpServerThread.ResponseStatusFor("post"), Is.EqualTo(405));
    }
}
