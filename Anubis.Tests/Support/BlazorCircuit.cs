using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anubis.Tests.Support;

/// <summary>
/// What a browser does after loading an interactive page: opens the Blazor circuit over a WebSocket
/// (the SignalR "blazorpack" protocol), starts it and adds the page's interactive root components.
/// Whatever the circuit then renders arrives as render batches, whose text is returned.
/// </summary>
public static partial class BlazorCircuit
{
    private const byte RecordSeparator = 0x1e;

    /// <summary>The text the circuit of <paramref name="path"/> renders, read until it contains <paramref name="until"/> (or the time is up).</summary>
    public static async Task<string> RenderAsync(AnubisFactory factory, HttpClient browser, string cookies, string path, string until)
    {
        var html = await browser.GetStringAsync(path);
        var markers = ServerMarkerRegex().Matches(html).Select(match => match.Groups[1].Value).ToList();
        Assert.NotEmpty(markers);

        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Cookie = cookies;
        var origin = browser.BaseAddress!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = await client.ConnectAsync(new Uri($"ws://{origin.Authority}/_blazor"), timeout.Token);

        await SendAsync(socket, [.. Encoding.UTF8.GetBytes("""{"protocol":"blazorpack","version":1}"""), RecordSeparator], timeout.Token);
        await ReceiveAsync(socket, timeout.Token);   // the handshake response

        // StartCircuit(baseUri, uri, initial components, application state) -> the circuit's id.
        await SendAsync(socket, Invocation("0", "StartCircuit", origin.AbsoluteUri, new Uri(origin, path).AbsoluteUri, "[]", string.Empty), timeout.Token);
        while (!(await ReceiveAsync(socket, timeout.Token)).Any(message => message.Length > 1 && message[1] == 3))
        {
        }

        // UpdateRootComponents(operations, application state): the page's interactive components, as the HTML names them.
        var operations = markers.Select((marker, index) => $$"""{"type":"add","ssrComponentId":{{index + 1}},"marker":{{marker}}}""");
        await SendAsync(socket, Invocation(null, "UpdateRootComponents", $$"""{"batchId":1,"operations":[{{string.Join(',', operations)}}]}""", string.Empty), timeout.Token);

        var rendered = new StringBuilder();
        try
        {
            while (!rendered.ToString().Contains(until, StringComparison.Ordinal))
            {
                foreach (var message in await ReceiveAsync(socket, timeout.Token))
                {
                    rendered.Append(Encoding.UTF8.GetString(message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Never rendered: the caller's assertion says what is missing.
        }

        return rendered.ToString();
    }

    /// <summary>A SignalR invocation in MessagePack: [1, headers, invocation id, target, arguments, stream ids], length-prefixed.</summary>
    private static byte[] Invocation(string? invocationId, string target, params string[] arguments)
    {
        var body = new List<byte> { 0x96, 0x01, 0x80 };
        if (invocationId is null)
        {
            body.Add(0xc0);
        }
        else
        {
            WriteString(body, invocationId);
        }

        WriteString(body, target);
        body.Add((byte)(0x90 | arguments.Length));
        foreach (var argument in arguments)
        {
            WriteString(body, argument);
        }

        body.Add(0x90);

        var message = new List<byte>();
        for (var length = body.Count; ; length >>= 7)
        {
            if (length < 0x80)
            {
                message.Add((byte)length);
                break;
            }

            message.Add((byte)(length & 0x7f | 0x80));
        }

        message.AddRange(body);
        return [.. message];
    }

    private static void WriteString(List<byte> output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        switch (bytes.Length)
        {
            case < 32: output.Add((byte)(0xa0 | bytes.Length)); break;
            case < 256: output.AddRange([0xd9, (byte)bytes.Length]); break;
            case < 65536: output.AddRange([0xda, (byte)(bytes.Length >> 8), (byte)bytes.Length]); break;
            default: output.AddRange([0xdb, (byte)(bytes.Length >> 24), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length]); break;
        }

        output.AddRange(bytes);
    }

    private static Task SendAsync(WebSocket socket, byte[] data, CancellationToken cancellationToken)
        => socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

    /// <summary>One WebSocket message, split into the SignalR messages it carries (the handshake response is returned whole).</summary>
    private static async Task<List<byte[]>> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var frame = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new OperationCanceledException("The server closed the circuit's connection.");
            }

            frame.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var data = frame.ToArray();
        if (data.Length > 0 && data[^1] == RecordSeparator)
        {
            using var handshake = JsonDocument.Parse(data.AsMemory(0, data.Length - 1));
            Assert.False(handshake.RootElement.TryGetProperty("error", out var error), error.ToString());
            return [data];
        }

        var messages = new List<byte[]>();
        for (var position = 0; position < data.Length;)
        {
            var (length, shift) = (0, 0);
            byte part;
            do
            {
                part = data[position++];
                length |= (part & 0x7f) << shift;
                shift += 7;
            }
            while ((part & 0x80) != 0);

            messages.Add(data[position..(position + length)]);
            position += length;
        }

        return messages;
    }

    [GeneratedRegex("""<!--Blazor:(\{[^>]*?"type":"server"[^>]*?"descriptor"[^>]*?\})-->""")]
    private static partial Regex ServerMarkerRegex();
}
