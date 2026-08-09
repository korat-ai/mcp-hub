using Korat.Cli.Mcp.Aggregation;
using Xunit;
using Korat.Mcp;

public class JsonRpcTests
{
    [Fact]
    public void Parse_request_exposes_id_method_params()
    {
        var m = JsonRpcMessage.Parse("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"x"}}""");
        Assert.True(m.IsRequest);
        Assert.Equal("tools/call", m.Method);
        Assert.Equal("x", m.Params!["name"]!.GetValue<string>());
        Assert.Equal("7", m.IdAsString);
    }

    [Fact]
    public void Parse_notification_has_no_id()
    {
        var m = JsonRpcMessage.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.True(m.IsNotification);
        Assert.Null(m.IdAsString);
    }

    [Fact]
    public void Result_and_Error_build_valid_response_envelopes_preserving_id()
    {
        var ok = JsonRpcMessage.Result(idNode: System.Text.Json.Nodes.JsonValue.Create(7), resultJson: """{"ok":true}""");
        Assert.Contains("\"id\":7", ok);
        Assert.Contains("\"result\"", ok);

        var err = JsonRpcMessage.Error(System.Text.Json.Nodes.JsonValue.Create("a"), code: -32601, message: "nope");
        Assert.Contains("\"id\":\"a\"", err);
        Assert.Contains("-32601", err);
    }

    [Fact]
    public void Notification_builds_envelope_without_id()
    {
        var n = JsonRpcMessage.Notification("notifications/tools/list_changed");
        Assert.DoesNotContain("\"id\"", n);
        Assert.Contains("list_changed", n);
    }
}
