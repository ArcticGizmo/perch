using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaloniaSpike;

/// <summary>
/// Spike of an owner-drawn Perch surface ported to Avalonia. Mirrors a StatsForm stat card: a
/// rounded dark panel with a big centered number, a muted caption, and a usage pill. The whole point
/// is to prove two things the real port depends on:
/// <list type="bullet">
/// <item>GDI+ <c>Graphics</c> drawing translates to <see cref="DrawingContext"/> with near-identical calls.</item>
/// <item>The CLAUDE.md rule — <b>size text from the font's line height, never a magic pixel value</b> —
/// is expressible in Avalonia via <see cref="FormattedText.Height"/>, so glyphs never clip on a DPI change.</item>
/// </list>
/// </summary>
internal sealed class StatCard : Control
{
    // Perch overlay/stats palette (see OverlayForm + Theme).
    private static readonly IBrush Bg      = new SolidColorBrush(Color.FromRgb(15, 15, 20));
    private static readonly IBrush Border  = new SolidColorBrush(Color.FromRgb(45, 45, 60));
    private static readonly IBrush Fg      = new SolidColorBrush(Color.FromRgb(245, 245, 250));
    private static readonly IBrush Muted   = new SolidColorBrush(Color.FromRgb(140, 140, 160));
    private static readonly IBrush Track   = new SolidColorBrush(Color.FromRgb(38, 38, 52));
    private static readonly IBrush Running = new SolidColorBrush(Color.FromRgb(34, 197, 94));

    public string Number { get; init; } = "7";
    public string Caption { get; init; } = "SESSIONS ACTIVE";
    public double UsageFraction { get; init; } = 0.62;

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);

        // Card background + 1px border. Inset by 0.5 so the stroke is crisp.
        var card = bounds.Deflate(0.5);
        ctx.DrawRectangle(Bg, new Pen(Border, 1), new RoundedRect(card, 10));

        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        // Big number. Height/position derived from FormattedText — never a hard-coded pixel box.
        var number = new FormattedText(Number, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 42, Fg);
        double numX = (bounds.Width - number.Width) / 2;
        // Vertically place the number block in the upper portion, leaving room for caption + bar.
        double numY = bounds.Height * 0.18;
        ctx.DrawText(number, new Point(numX, numY));

        // Caption, centered under the number. Its baseline follows the number's measured height,
        // so the gap is constant across font/DPI changes rather than a magic offset.
        var caption = new FormattedText(Caption, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 12, Muted);
        double capX = (bounds.Width - caption.Width) / 2;
        double capY = numY + number.Height + 6;
        ctx.DrawText(caption, new Point(capX, capY));

        // Usage pill: track + fill, proving FillRoundedBar. Sits a line-height below the caption.
        double barH = 8;
        double barY = capY + caption.Height + 12;
        double barMargin = 20;
        var track = new Rect(barMargin, barY, bounds.Width - barMargin * 2, barH);
        PaintKit.FillRoundedBar(ctx, Track, track);
        PaintKit.FillRoundedBar(ctx, Running,
            track.WithWidth(Math.Max(barH, track.Width * Math.Clamp(UsageFraction, 0, 1))));
    }
}
