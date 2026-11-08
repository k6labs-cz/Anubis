using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using k6labs.Anubis.Options;
using k6labs.Anubis.Services.Email;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace Anubis.Tests;

/// <summary>Checks the exact requests sent to each email provider, without network access.</summary>
public class EmailTransportTests
{
    private static readonly EmailMessage Message = new("user@example.com", "Hello", "<p>Hi <b>there</b></p>", "Hi there");

    [Fact]
    public async Task SendGrid_PostsMailSendRequest_WithTrackingOff()
    {
        var options = Options(o => { o.Provider = EmailProvider.SendGrid; o.SendGrid.ApiKey = "SG.key"; });
        var handler = new RecordingHandler(HttpStatusCode.Accepted);

        await new SendGridEmailTransport(handler.Factory(), options).SendAsync(Message);

        Assert.Equal("https://api.sendgrid.com/v3/mail/send", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer SG.key", handler.Request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal("user@example.com", root.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("noreply@example.com", root.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("text/plain", root.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("<p>Hi <b>there</b></p>", root.GetProperty("content")[1].GetProperty("value").GetString());
        Assert.False(root.GetProperty("tracking_settings").GetProperty("click_tracking").GetProperty("enable").GetBoolean());
    }

    [Fact]
    public async Task Mailgun_PostsForm_WithBasicAuth()
    {
        var options = Options(o =>
        {
            o.Provider = EmailProvider.Mailgun;
            o.Mailgun.ApiKey = "mg-key";
            o.Mailgun.Domain = "mg.example.com";
            o.Mailgun.BaseUrl = "https://api.eu.mailgun.net/";
        });
        var handler = new RecordingHandler(HttpStatusCode.OK);

        await new MailgunEmailTransport(handler.Factory(), options).SendAsync(Message);

        Assert.Equal("https://api.eu.mailgun.net/v3/mg.example.com/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("api:mg-key")), handler.Request.Headers.Authorization!.ToString());
        var form = System.Web.HttpUtility.ParseQueryString(handler.Body!);
        Assert.Equal("Anubis Test <noreply@example.com>", form["from"]);
        Assert.Equal("user@example.com", form["to"]);
        Assert.Equal("Hi there", form["text"]);
        Assert.Equal("no", form["o:tracking"]);
    }

    [Fact]
    public async Task Postmark_PostsEmail_WithServerToken()
    {
        var options = Options(o => { o.Provider = EmailProvider.Postmark; o.Postmark.ServerToken = "pm-token"; });
        var handler = new RecordingHandler(HttpStatusCode.OK);

        await new PostmarkEmailTransport(handler.Factory(), options).SendAsync(Message);

        Assert.Equal("https://api.postmarkapp.com/email", handler.Request!.RequestUri!.ToString());
        Assert.Equal("pm-token", handler.Request.Headers.GetValues("X-Postmark-Server-Token").Single());
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("outbound", json.RootElement.GetProperty("MessageStream").GetString());
        Assert.Equal("None", json.RootElement.GetProperty("TrackLinks").GetString());
        Assert.Equal("Hi there", json.RootElement.GetProperty("TextBody").GetString());
    }

    [Fact]
    public async Task Resend_PostsEmail_WithBearerToken()
    {
        var options = Options(o => { o.Provider = EmailProvider.Resend; o.Resend.ApiKey = "re_key"; });
        var handler = new RecordingHandler(HttpStatusCode.OK);

        await new ResendEmailTransport(handler.Factory(), options).SendAsync(Message);

        Assert.Equal("https://api.resend.com/emails", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer re_key", handler.Request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("user@example.com", json.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("Anubis Test <noreply@example.com>", json.RootElement.GetProperty("from").GetString());
    }

    [Fact]
    public async Task HttpProvider_Error_ThrowsWithStatusAndBody()
    {
        var options = Options(o => { o.Provider = EmailProvider.SendGrid; o.SendGrid.ApiKey = "bad"; });
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"invalid key\"}]}");

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(
            () => new SendGridEmailTransport(handler.Factory(), options).SendAsync(Message));

        Assert.Contains("401", exception.Message);
        Assert.Contains("invalid key", exception.Message);
        Assert.DoesNotContain("bad", exception.Message);
    }

    [Fact]
    public async Task Smtp_DeliversMimeMessage()
    {
        await using var server = new FakeSmtpServer();
        var options = Options(o =>
        {
            o.Smtp.Host = "127.0.0.1";
            o.Smtp.Port = server.Port;
            o.Smtp.Security = SmtpSecurity.None;
        });

        await new SmtpEmailTransport(options).SendAsync(Message);

        var data = await server.ReceivedData;
        Assert.Contains("MAIL FROM:<noreply@example.com>", server.Commands);
        Assert.Contains("RCPT TO:<user@example.com>", server.Commands);
        Assert.Contains("Subject: Hello", data);
        Assert.Contains("multipart/alternative", data);
        Assert.Contains("Hi there", data);
    }

    [Fact]
    public async Task Smtp_UnreachableServer_ThrowsDeliveryException()
    {
        var options = Options(o =>
        {
            o.Smtp.Host = "127.0.0.1";
            o.Smtp.Port = FreePort();
            o.Smtp.Security = SmtpSecurity.None;
            o.Smtp.TimeoutSeconds = 5;
        });

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() => new SmtpEmailTransport(options).SendAsync(Message));
        Assert.Contains("SMTP delivery", exception.Message);
    }

    [Fact]
    public async Task LogTransport_DoesNotThrow()
    {
        var transport = new LogEmailTransport(NullLogger<LogEmailTransport>.Instance, new HostingEnvironment { EnvironmentName = Environments.Production });

        await transport.SendAsync(Message);
    }

    [Theory]
    [InlineData(EmailProvider.SendGrid, "Email:SendGrid:ApiKey")]
    [InlineData(EmailProvider.Mailgun, "Email:Mailgun:ApiKey")]
    [InlineData(EmailProvider.Postmark, "Email:Postmark:ServerToken")]
    [InlineData(EmailProvider.Resend, "Email:Resend:ApiKey")]
    public void Validator_RequiresProviderCredentials(EmailProvider provider, string missingKey)
    {
        var options = new EmailOptions { Provider = provider, FromAddress = "noreply@example.com" };

        var result = new EmailOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(missingKey));
    }

    [Fact]
    public void Validator_UnconfiguredSmtp_IsAllowed()
    {
        // No host = log-only mode, so a fresh install starts without email settings.
        var result = new EmailOptionsValidator().Validate(null, new EmailOptions());

        Assert.True(result.Succeeded);
        Assert.False(new EmailOptions().IsConfigured);
    }

    [Fact]
    public void Validator_ConfiguredSmtp_RequiresFromAddress_AndPasswordWithUsername()
    {
        var options = new EmailOptions { Smtp = { Host = "smtp.example.com", Username = "user" } };

        var result = new EmailOptionsValidator().Validate(null, options);

        Assert.Contains(result.Failures!, f => f.Contains("Email:FromAddress"));
        Assert.Contains(result.Failures!, f => f.Contains("Email:Smtp:Password"));
    }

    [Fact]
    public void Validator_RejectsNonHttpsApiUrl()
    {
        var options = new EmailOptions
        {
            Provider = EmailProvider.Resend,
            FromAddress = "noreply@example.com",
            Resend = { ApiKey = "key", BaseUrl = "http://api.resend.com" },
        };

        var result = new EmailOptionsValidator().Validate(null, options);

        Assert.Contains(result.Failures!, f => f.Contains("https://"));
    }

    [Fact]
    public void Template_EncodesUserInput()
    {
        var message = EmailTemplates.Action("a@example.com", "S", "<script>", ["Hi <img src=x>"], "Go", "https://example.com/?a=1&b=2", "f");

        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.DoesNotContain("<img src=x>", message.HtmlBody);
        Assert.Contains("https://example.com/?a=1&amp;b=2", message.HtmlBody);
        Assert.Contains("Go: https://example.com/?a=1&b=2", message.TextBody);
    }

    private static Microsoft.Extensions.Options.IOptions<EmailOptions> Options(Action<EmailOptions> configure)
    {
        var options = new EmailOptions { FromAddress = "noreply@example.com", FromName = "Anubis Test" };
        configure(options);
        return MsOptions.Create(options);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;

        public RecordingHandler(HttpStatusCode status, string responseBody = "{}")
        {
            _status = status;
            _responseBody = responseBody;
        }

        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public IHttpClientFactory Factory() => new StubFactory(this);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_responseBody) };
        }

        private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }
    }

    /// <summary>Just enough of an SMTP server (RFC 5321) to accept one message.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _data = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;

        public FakeSmtpServer()
        {
            _listener.Start();
            _loop = Task.Run(RunAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Commands { get; } = [];

        public Task<string> ReceivedData => _data.Task.WaitAsync(TimeSpan.FromSeconds(10));

        private async Task RunAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            await writer.WriteLineAsync("220 fake ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                Commands.Add(line);
                var verb = line.Split(' ', ':')[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 go ahead");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        {
                            data.AppendLine(dataLine);
                        }

                        _data.TrySetResult(data.ToString());
                        await writer.WriteLineAsync("250 queued");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 ok");
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // The loop ends with an exception when the listener stops; that's expected.
            }
        }
    }
}
