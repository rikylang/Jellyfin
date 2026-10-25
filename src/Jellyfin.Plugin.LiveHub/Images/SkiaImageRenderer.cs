using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.LiveHub.Images;

/// <summary>
/// Renders composite images with SkiaSharp (the graphics library shipped with Jellyfin): gradient background coloured
/// by category, "Home vs Away" or the title, competition and time. Returns null when Skia is unavailable.
/// </summary>
public sealed class SkiaImageRenderer
{
    private readonly ILogger<SkiaImageRenderer> _logger;
    private bool _unavailable;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkiaImageRenderer"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public SkiaImageRenderer(ILogger<SkiaImageRenderer> logger) => _logger = logger;

    /// <summary>Gets the pixel size of a shape.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>Width and height.</returns>
    public static (int Width, int Height) Size(ImageShape shape) => shape switch
    {
        ImageShape.Poster => (680, 1000),
        ImageShape.Logo => (512, 512),
        _ => (1280, 720),
    };

    /// <summary>Renders a PNG.</summary>
    /// <param name="text">The text.</param>
    /// <param name="shape">The shape.</param>
    /// <returns>The PNG bytes, or null when rendering is not possible.</returns>
    public byte[]? Render(ImageText text, ImageShape shape)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_unavailable)
        {
            return null;
        }

        try
        {
            return RenderCore(text, shape);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or TypeLoadException or System.IO.FileNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _unavailable = true;
            _logger.LogWarning("SkiaSharp is not available, generated images are disabled: {Error}", ex.Message);
            return null;
        }
    }

    private static byte[] RenderCore(ImageText text, ImageShape shape)
    {
        var (w, h) = Size(shape);
        var hue = Hue(text.ColorKey ?? text.Top ?? text.Title);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        using (var bg = new SKPaint())
        {
            bg.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(w, h),
                new[] { SKColor.FromHsl(hue, 70, 28), SKColor.FromHsl((hue + 40) % 360, 75, 12) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, w, h, bg);
        }

        using (var stripe = new SKPaint { Color = SKColor.FromHsl(hue, 85, 55).WithAlpha(200), IsAntialias = true })
        {
            canvas.DrawRect(0, h - (h / 60f), w, h / 60f, stripe);
        }

        var typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold) ?? SKTypeface.Default;
        var regular = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Normal) ?? SKTypeface.Default;
        var margin = w * 0.07f;
        var maxWidth = w - (2 * margin);

        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var soft = new SKPaint { Color = SKColors.White.WithAlpha(190), IsAntialias = true };

        if (!string.IsNullOrWhiteSpace(text.Top))
        {
            using var font = new SKFont(regular, h * (shape == ImageShape.Thumb ? 0.06f : 0.045f));
            DrawCentered(canvas, Fit(text.Top!.ToUpperInvariant(), font, maxWidth), h * 0.16f, font, soft, w);
        }

        var lines = text.Participants.Count >= 2
            ? new List<string> { text.Participants[0], "vs", text.Participants[1] }
            : Wrap(text.Title, 18);
        var size = h * (shape == ImageShape.Thumb ? 0.12f : 0.085f);
        using (var big = new SKFont(typeface, size))
        {
            var total = lines.Count * size * 1.25f;
            var y = (h / 2f) - (total / 2f) + size;
            foreach (var line in lines.Take(4))
            {
                var isVs = line == "vs";
                using var font = isVs ? new SKFont(regular, size * 0.6f) : null;
                DrawCentered(canvas, Fit(line, font ?? big, maxWidth), y, font ?? big, isVs ? soft : white, w);
                y += size * 1.25f;
            }
        }

        if (!string.IsNullOrWhiteSpace(text.Bottom))
        {
            using var font = new SKFont(regular, h * (shape == ImageShape.Thumb ? 0.065f : 0.05f));
            DrawCentered(canvas, Fit(text.Bottom!, font, maxWidth), h * 0.88f, font, white, w);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static void DrawCentered(SKCanvas canvas, string text, float y, SKFont font, SKPaint paint, int width)
    {
        var textWidth = font.MeasureText(text);
        canvas.DrawText(text, (width - textWidth) / 2f, y, SKTextAlign.Left, font, paint);
    }

    private static string Fit(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        var s = text;
        while (s.Length > 3 && font.MeasureText(s + "…") > maxWidth)
        {
            s = s[..^1];
        }

        return s.TrimEnd() + "…";
    }

    private static List<string> Wrap(string text, int perLine)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + word.Length + 1 > perLine)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            current.Append(current.Length > 0 ? " " : string.Empty).Append(word);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines.Count == 0 ? new List<string> { "Live" } : lines;
    }

    private static float Hue(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))[0] * 360f / 256f;
}
