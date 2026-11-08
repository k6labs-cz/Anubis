using System.Net.Http.Headers;
using System.Text;

using k6labs.Anubis.Options;

using Microsoft.Extensions.Options;

using MimeKit;

namespace k6labs.Anubis.Services.Email;

/// <summary>
/// Base for providers with an HTTP API. Link tracking is turned off everywhere:
/// providers would otherwise rewrite password-reset links through their own domain.
/// </summary>
public abstract class HttpEmailTransport : IEmailTransport
{
    public const string HttpClientName = "email";

    private readonly IHttpClientFactory _httpClientFactory;

    protected HttpEmailTransport(IHttpClientFactory httpClientFactory, IOptions<EmailOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        Options = options.Value;
    }

    public abstract string Name { get; }

    protected EmailOptions Options { get; }

    /// <summary>The sender as <c>"Name" &lt;address&gt;</c>, RFC 2047-encoded when needed.</summary>
    protected string From => new MailboxAddress(Options.FromName, Options.FromAddress).ToString(FormatOptions.Default, encode: true);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(message);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new EmailDeliveryException($"{Name} could not be reached: {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmailDeliveryException($"{Name} did not respond in time.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (body.Length > 500)
                {
                    body = body[..500] + "…";
                }

                throw new EmailDeliveryException($"{Name} rejected the message ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");
            }
        }
    }

    protected abstract HttpRequestMessage CreateRequest(EmailMessage message);

    protected static Uri Endpoint(string baseUrl, string path) => new(baseUrl.TrimEnd('/') + path);
}

/// <summary>SendGrid v3 Mail Send API.</summary>
public sealed class SendGridEmailTransport : HttpEmailTransport
{
    public SendGridEmailTransport(IHttpClientFactory httpClientFactory, IOptions<EmailOptions> options)
        : base(httpClientFactory, options)
    {
    }

    public override string Name => "SendGrid";

    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(Options.SendGrid.BaseUrl, "/v3/mail/send"))
        {
            Content = JsonContent.Create(new
            {
                personalizations = new[] { new { to = new[] { new { email = message.To } } } },
                from = new { email = Options.FromAddress, name = Options.FromName },
                subject = message.Subject,
                // SendGrid requires text/plain before text/html.
                content = new[]
                {
                    new { type = "text/plain", value = message.TextBody },
                    new { type = "text/html", value = message.HtmlBody },
                },
                tracking_settings = new
                {
                    click_tracking = new { enable = false, enable_text = false },
                    open_tracking = new { enable = false },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.SendGrid.ApiKey);
        return request;
    }
}

/// <summary>Mailgun Messages API.</summary>
public sealed class MailgunEmailTransport : HttpEmailTransport
{
    public MailgunEmailTransport(IHttpClientFactory httpClientFactory, IOptions<EmailOptions> options)
        : base(httpClientFactory, options)
    {
    }

    public override string Name => "Mailgun";

    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        var mailgun = Options.Mailgun;
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(mailgun.BaseUrl, $"/v3/{Uri.EscapeDataString(mailgun.Domain!)}/messages"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["from"] = From,
                ["to"] = message.To,
                ["subject"] = message.Subject,
                ["text"] = message.TextBody,
                ["html"] = message.HtmlBody,
                ["o:tracking"] = "no",
            }),
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("api:" + mailgun.ApiKey));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return request;
    }
}

/// <summary>Postmark Email API.</summary>
public sealed class PostmarkEmailTransport : HttpEmailTransport
{
    public PostmarkEmailTransport(IHttpClientFactory httpClientFactory, IOptions<EmailOptions> options)
        : base(httpClientFactory, options)
    {
    }

    public override string Name => "Postmark";

    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        var postmark = Options.Postmark;
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(postmark.BaseUrl, "/email"))
        {
            Content = JsonContent.Create(new Dictionary<string, object>
            {
                ["From"] = From,
                ["To"] = message.To,
                ["Subject"] = message.Subject,
                ["HtmlBody"] = message.HtmlBody,
                ["TextBody"] = message.TextBody,
                ["MessageStream"] = postmark.MessageStream,
                ["TrackLinks"] = "None",
                ["TrackOpens"] = false,
            }),
        };
        request.Headers.Add("X-Postmark-Server-Token", postmark.ServerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}

/// <summary>Resend Emails API.</summary>
public sealed class ResendEmailTransport : HttpEmailTransport
{
    public ResendEmailTransport(IHttpClientFactory httpClientFactory, IOptions<EmailOptions> options)
        : base(httpClientFactory, options)
    {
    }

    public override string Name => "Resend";

    protected override HttpRequestMessage CreateRequest(EmailMessage message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(Options.Resend.BaseUrl, "/emails"))
        {
            Content = JsonContent.Create(new
            {
                from = From,
                to = new[] { message.To },
                subject = message.Subject,
                html = message.HtmlBody,
                text = message.TextBody,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.Resend.ApiKey);
        return request;
    }
}
