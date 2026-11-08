using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using k6labs.Anubis.Options;
using k6labs.Anubis.Services;
using k6labs.Anubis.Services.Avatar;

using Microsoft.Extensions.DependencyInjection;

using SkiaSharp;

namespace Anubis.Tests;

/// <summary>
/// SVG/WebP support: SVG safety checks and rasterization, SVG avatars (stored as WebP),
/// uploaded logo/favicon kept as data URLs and served from /appearance/* with PNG favicons.
/// </summary>
[Collection(AnubisCollection.Name)]
public class ImageTests
{
    private const string CircleSvg = """
        <?xml version="1.0" encoding="UTF-8"?>
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50" width="100" height="50">
          <defs><linearGradient id="g"><stop offset="0" stop-color="#00f2fe"/><stop offset="1" stop-color="#ff2a85"/></linearGradient></defs>
          <rect x="0" y="0" width="100" height="50" fill="url(#g)"/>
        </svg>
        """;

    private readonly AnubisFactory _factory;

    public ImageTests(AnubisFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void IsSvg_DetectsSvgOnly()
    {
        Assert.True(ImageProcessing.IsSvg(Encoding.UTF8.GetBytes(CircleSvg)));
        Assert.True(ImageProcessing.IsSvg(Encoding.UTF8.GetBytes("﻿  <svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
        Assert.False(ImageProcessing.IsSvg(Png(10, 10)));
        Assert.False(ImageProcessing.IsSvg(Encoding.UTF8.GetBytes("<html><body/></html>")));
    }

    [Theory]
    [InlineData("""<!DOCTYPE svg [<!ENTITY x SYSTEM "file:///etc/passwd">]><svg xmlns="http://www.w3.org/2000/svg"><text>&x;</text></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="http://169.254.169.254/latest" width="10" height="10"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image href="file:///etc/passwd" width="10" height="10"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect width="10" height="10" style="fill: url(https://evil.example/x.svg#a)"/></svg>""")]
    [InlineData("""<html xmlns="http://www.w3.org/1999/xhtml"><svg/></html>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect""")]
    public void EnsureSafeSvg_RejectsDangerousOrBrokenSvg(string svg)
    {
        Assert.Throws<InvalidDataException>(() => ImageProcessing.EnsureSafeSvg(Encoding.UTF8.GetBytes(svg)));
    }

    [Fact]
    public void EnsureSafeSvg_AllowsInternalReferences_AndRejectsUseBombs()
    {
        ImageProcessing.EnsureSafeSvg(Encoding.UTF8.GetBytes(CircleSvg));
        ImageProcessing.EnsureSafeSvg(Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg"><defs><circle id="c" r="5"/></defs><use href="#c"/></svg>"""));

        var bomb = "<svg xmlns=\"http://www.w3.org/2000/svg\"><circle id=\"c\" r=\"1\"/>" + string.Concat(Enumerable.Repeat("<use href=\"#c\"/>", 600)) + "</svg>";
        Assert.Throws<InvalidDataException>(() => ImageProcessing.EnsureSafeSvg(Encoding.UTF8.GetBytes(bomb)));
    }

    [Fact]
    public void RasterizeSvg_RendersAtTheRequestedSize()
    {
        using var bitmap = ImageProcessing.RasterizeSvg(Encoding.UTF8.GetBytes(CircleSvg), 400);

        Assert.Equal(400, bitmap.Width);   // 100x50 scaled up to the requested longer side
        Assert.Equal(200, bitmap.Height);
        Assert.Equal(255, bitmap.GetPixel(200, 100).Alpha);
    }

    [Fact]
    public void AvatarFromSvg_IsStoredAsWebp()
    {
        var (data, width, height) = AvatarService.ProcessImage(Encoding.UTF8.GetBytes(CircleSvg));

        Assert.Equal("WEBP", Encoding.ASCII.GetString(data, 8, 4));
        Assert.Equal(1024, width);
        Assert.Equal(512, height);
    }

    [Fact]
    public void AvatarFromWebp_IsAccepted()
    {
        using var source = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        source.Erase(SKColors.Red);
        var webp = ImageProcessing.EncodeWebp(source);

        var (data, width, height) = AvatarService.ProcessImage(webp);

        Assert.Equal("WEBP", Encoding.ASCII.GetString(data, 8, 4));
        Assert.Equal((300, 200), (width, height));
    }

    [Fact]
    public void LogoUpload_KeepsSvg_AndConvertsRasterToWebp()
    {
        var svg = AppearanceAssets.ProcessUpload(Encoding.UTF8.GetBytes(CircleSvg), AppearanceAssets.LogoMaxDimension);
        Assert.StartsWith("data:image/svg+xml;base64,", svg);
        Assert.True(AppearanceAssets.TryParseDataUrl(svg, out _, out var svgBytes));
        Assert.Equal(CircleSvg, Encoding.UTF8.GetString(svgBytes));

        var raster = AppearanceAssets.ProcessUpload(Png(2000, 1000), AppearanceAssets.LogoMaxDimension);
        Assert.StartsWith("data:image/webp;base64,", raster);
        Assert.True(AppearanceAssets.TryParseDataUrl(raster, out _, out var webp));
        using var decoded = SKBitmap.Decode(webp);
        Assert.Equal((1024, 512), (decoded.Width, decoded.Height));

        Assert.Throws<InvalidDataException>(() => AppearanceAssets.ProcessUpload(Encoding.UTF8.GetBytes("not an image"), 512));
        Assert.False(AppearanceAssets.TryParseDataUrl("data:text/html;base64,PHNjcmlwdD4=", out _, out _));
    }

    [Fact]
    public async Task DefaultFavicon_GetsPngVersions()
    {
        using var client = _factory.CreateAnonymousClient();

        var html = await client.GetStringAsync("/login");
        Assert.Contains("href=\"/appearance/favicon?v=", html);
        Assert.Matches("type=\"image/svg(\\+|&#x2B;)xml\"", html);   // Razor encodes '+'

        Assert.Contains("rel=\"apple-touch-icon\" href=\"/appearance/favicon-180.png?v=", html);

        using var response = await client.GetAsync("/appearance/favicon-32.png");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        using var png = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal((32, 32), (png.Width, png.Height));

        using var odd = await client.GetAsync("/appearance/favicon-31.png");
        Assert.Equal(HttpStatusCode.NotFound, odd.StatusCode);
    }

    [Fact]
    public async Task UploadedLogoAndFavicon_AreServedFromAppearanceUrls()
    {
        var settings = _factory.Services.GetRequiredService<SettingsFile>();
        var logo = AppearanceAssets.ProcessUpload(Png(400, 100), AppearanceAssets.LogoMaxDimension);
        var favicon = AppearanceAssets.ProcessUpload(Encoding.UTF8.GetBytes(CircleSvg), AppearanceAssets.FaviconMaxDimension);

        try
        {
            await settings.SaveSectionAsync(AppearanceOptions.SectionName, new JsonObject
            {
                ["LogoPath"] = logo,
                ["FaviconPath"] = favicon,
            });

            using var client = _factory.CreateAnonymousClient();
            var html = await client.GetStringAsync("/login");
            Assert.DoesNotContain("data:image", html);   // never inlined into the page
            var logoVersion = AppearanceAssets.Version(logo);
            Assert.Contains($"src=\"/appearance/logo?v={logoVersion}\"", html);
            Assert.Contains($"href=\"/appearance/favicon?v={AppearanceAssets.Version(favicon)}\"", html);
            Assert.Contains("href=\"/appearance/favicon-32.png?v=", html);

            using (var response = await client.GetAsync($"/appearance/logo?v={logoVersion}"))
            {
                Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
                Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
            }

            using (var response = await client.GetAsync("/appearance/favicon"))
            {
                Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
                Assert.Contains("sandbox", string.Join(" ", response.Headers.GetValues("Content-Security-Policy")));
                Assert.Contains("no-cache", response.Headers.CacheControl?.ToString());   // no/old version
            }

            using (var response = await client.GetAsync("/appearance/favicon-180.png"))
            {
                using var png = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
                Assert.Equal((180, 180), (png.Width, png.Height));
            }
        }
        finally
        {
            await settings.SaveSectionAsync(AppearanceOptions.SectionName, new JsonObject());
        }
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(0, 180, 216));
        return ImageProcessing.EncodePng(bitmap);
    }
}
