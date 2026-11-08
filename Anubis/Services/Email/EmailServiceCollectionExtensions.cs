using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Options;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace k6labs.Anubis.Services.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Registers the transport chosen by <c>Email:Provider</c> (SMTP by default) and the
    /// Identity email sender used for password reset and email confirmation.
    /// </summary>
    public static IServiceCollection AddAnubisEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();

        services.AddHttpClient(HttpEmailTransport.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddTransient<LogEmailTransport>();

        // Resolved per use from the current options, so settings saved in the admin UI
        // apply without a restart. Each transport gets a snapshot of the current values.
        services.AddTransient<IEmailTransport>(provider =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<EmailOptions>>().CurrentValue;
            if (!options.IsConfigured)
            {
                return new InstrumentedEmailTransport(provider.GetRequiredService<LogEmailTransport>());
            }

            var snapshot = Microsoft.Extensions.Options.Options.Create(options);
            return new InstrumentedEmailTransport(options.Provider switch
            {
                EmailProvider.SendGrid => ActivatorUtilities.CreateInstance<SendGridEmailTransport>(provider, snapshot),
                EmailProvider.Mailgun => ActivatorUtilities.CreateInstance<MailgunEmailTransport>(provider, snapshot),
                EmailProvider.Postmark => ActivatorUtilities.CreateInstance<PostmarkEmailTransport>(provider, snapshot),
                EmailProvider.Resend => ActivatorUtilities.CreateInstance<ResendEmailTransport>(provider, snapshot),
                _ => ActivatorUtilities.CreateInstance<SmtpEmailTransport>(provider, snapshot),
            });
        });

        services.AddMemoryCache();
        services.AddSingleton<EmailThrottle>();
        services.AddTransient<IEmailSender<User>, AccountEmailSender>();
        services.AddScoped<AccountEmailService>();

        return services;
    }
}
