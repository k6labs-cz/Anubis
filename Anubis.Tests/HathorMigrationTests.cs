using System.Reflection;

using k6labs.Anubis.Components.Shared;
using k6labs.Hathor.Feedback;
using k6labs.Hathor.Lucide;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Anubis.Tests;

public class HathorMigrationTests
{
    [Fact]
    public void EveryApplicationIcon_ExistsInLucide()
    {
        foreach (var field in typeof(AppIcons).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var name = Assert.IsType<string>(field.GetRawConstantValue());
            Assert.True(LucideIconSet.Instance.TryGetIcon(name, out _), $"Missing Lucide icon: {name}");
        }
    }

    [Fact]
    public async Task TextFields_PreservePostNames_Labels_AndBrowserHints()
    {
        var html = await RenderAsync<FormText>(new()
        {
            [nameof(FormText.Id)] = "email",
            [nameof(FormText.Label)] = "Email",
            [nameof(FormText.Type)] = "email",
            [nameof(FormText.Value)] = "user@example.com",
            [nameof(FormText.Attributes)] = new Dictionary<string, object>
            {
                ["name"] = "model.Email",
                ["autocomplete"] = "username webauthn",
                ["maxlength"] = 254
            }
        });

        Assert.Contains("for=\"email\"", html);
        Assert.Contains("id=\"email\"", html);
        Assert.Contains("name=\"model.Email\"", html);
        Assert.Contains("type=\"email\"", html);
        Assert.Contains("autocomplete=\"username webauthn\"", html);
        Assert.Contains("maxlength=\"254\"", html);
        Assert.Contains("value=\"user@example.com\"", html);
    }

    [Fact]
    public async Task MultilineField_RendersTextArea_AndEscapesUntrustedValues()
    {
        var html = await RenderAsync<FormText>(new()
        {
            [nameof(FormText.Rows)] = 3,
            [nameof(FormText.Value)] = "<script>alert(1)</script>",
            [nameof(FormText.Error)] = true,
            [nameof(FormText.ErrorText)] = "Invalid URI"
        });

        Assert.Contains("<textarea", html);
        Assert.Contains("rows=\"3\"", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("aria-invalid=\"true\"", html);
        Assert.Contains("Invalid URI", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modal_OnlyRendersItsFormWhenOpen(bool open)
    {
        RenderFragment form = builder =>
        {
            builder.OpenElement(0, "form");
            builder.AddAttribute(1, "id", "editor");
            builder.CloseElement();
        };
        var html = await RenderAsync<AppModal>(new()
        {
            [nameof(AppModal.Open)] = open,
            [nameof(AppModal.Title)] = "Edit user",
            [nameof(AppModal.ChildContent)] = form
        });

        Assert.Contains("<dialog", html);
        Assert.Equal(open, html.Contains("<form", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Notifications_QueuedBeforeFirstRender_AreShownAndDismissed()
    {
        var state = new NotificationState();
        state.Add("Saved profile", AlertSeverity.Success);
        var html = await RenderAsync<NotificationList>(new() { [nameof(NotificationList.State)] = state });
        Assert.Contains("Saved profile", html);
        Assert.Contains("aria-live=\"polite\"", html);

        state.Dismiss(state.Items.Single());
        Assert.Empty(state.Items);
    }

    [Theory]
    [InlineData("7", true)]
    [InlineData("1", true)]
    [InlineData("10", true)]
    [InlineData("7.5", false)]
    [InlineData("0", false)]
    [InlineData("11", false)]
    [InlineData("", false)]
    public void NumericSettings_AcceptOnlyWholeNumbersWithinTheirLimits(string text, bool valid)
        => Assert.Equal(valid, new NumberProbe().Accepts(text));

    private sealed class NumberProbe : FormNumber
    {
        public NumberProbe() { Min = 1; Max = 10; }
        public bool Accepts(string text) => TryParseValueFromString(text, out _, out _);
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHathorLucide();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new InvalidOperationException("Static rendering must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }
}
