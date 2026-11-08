using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace k6labs.Anubis.Telemetry;

/// <summary>
/// Anubis' own OpenTelemetry signals: the <see cref="ActivitySource"/> for spans and the
/// <see cref="Meter"/> for metrics (both named "Anubis"). Registered in Program.cs and
/// exported over OTLP together with the ASP.NET Core / HttpClient / EF Core / runtime ones.
/// Metric names follow the OpenTelemetry conventions (dot-separated, unit in the metadata).
/// </summary>
public static class AnubisTelemetry
{
    public const string Name = "Anubis";

    public static readonly string Version =
        typeof(AnubisTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public static readonly ActivitySource ActivitySource = new(Name, Version);

    public static readonly Meter Meter = new(Name, Version);

    /// <summary>Sign-in attempts. Tags: <c>anubis.sign_in.method</c>, <c>anubis.sign_in.result</c>.</summary>
    public static readonly Counter<long> SignIns = Meter.CreateCounter<long>(
        "anubis.sign_ins", "{attempt}", "Sign-in attempts by method (password, passkey, authenticator, recovery-code) and result.");

    /// <summary>Recorded account history events. Tag: <c>anubis.account_event.type</c>.</summary>
    public static readonly Counter<long> AccountEvents = Meter.CreateCounter<long>(
        "anubis.account.events", "{event}", "Account history events (password changed, passkey added, ...).");

    /// <summary>Tokens issued by the OIDC endpoints. Tags: <c>oauth.grant_type</c>, <c>oauth.client_id</c>.</summary>
    public static readonly Counter<long> TokensIssued = Meter.CreateCounter<long>(
        "anubis.tokens.issued", "{response}", "Authorization and token endpoint responses that issued a code or tokens.");

    /// <summary>Emails sent. Tags: <c>anubis.email.provider</c>, <c>anubis.result</c>.</summary>
    public static readonly Counter<long> EmailsSent = Meter.CreateCounter<long>(
        "anubis.emails.sent", "{email}", "Outgoing emails by provider and result (success / failure).");

    public static readonly Histogram<double> EmailSendDuration = Meter.CreateHistogram<double>(
        "anubis.email.send.duration", "s", "Time to hand an email over to the provider.");

    /// <summary>Text messages sent. Tag: <c>anubis.result</c>.</summary>
    public static readonly Counter<long> SmsSent = Meter.CreateCounter<long>(
        "anubis.sms.sent", "{message}", "Outgoing text messages (phone verification codes).");

    /// <summary>New accounts. Tag: <c>anubis.registration.source</c> (self-service / admin / seed).</summary>
    public static readonly Counter<long> Registrations = Meter.CreateCounter<long>(
        "anubis.registrations", "{account}", "Accounts created.");

    public static readonly Histogram<double> PasswordHashDuration = Meter.CreateHistogram<double>(
        "anubis.password.hash.duration", "s", "Argon2id hashing / verification time (the deliberately slow part of a sign-in).");

    /// <summary>
    /// Registers the observable "number of accounts" gauge, read from the database when the
    /// metrics are collected (once per export interval). A failing read reports nothing.
    /// </summary>
    public static void ObserveUsers(Func<long> countUsers)
        => Meter.CreateObservableGauge("anubis.users", () =>
        {
            try
            {
                return [new Measurement<long>(countUsers())];
            }
            catch (Exception)
            {
                return Array.Empty<Measurement<long>>();
            }
        }, "{account}", "Accounts in the database.");
}
