using System.Net;

namespace Starfall.Grove.Moba.Api.Services;

/// <summary>
/// The game's emails, dressed like the game: a night-sky page, a parchment card with a gold edge and a big gold
/// button. Built from tables with inline styles, which is what email programs (Gmail, Outlook, Apple Mail) still
/// understand. Every email also has a plain-text version.
/// </summary>
public static class Emails
{
    private const string Night = "#1D1520", Plum = "#2E1F33", Parchment = "#FFF4DE", Gold = "#F2C96A", GoldDark = "#B8862F", Ink = "#3B2A2F", Muted = "#7A6A6E";
    private const string Font = "'Nunito','Trebuchet MS',Arial,sans-serif", Serif = "Georgia,'Times New Roman',serif";
    public const string Logo = "https://starfallgrove.eu/minirift/icon-180.png";

    public static (string Subject, string Text, string Html) Reset(string name, string link)
    {
        var text = $"Hi {name},\n\nSomeone (hopefully you) asked to reset the password of your Mini Rift account. Open this link within an hour to choose a new one:\n\n{link}\n\nIf you didn't ask for this, ignore this email; your password stays the same.\n\nMini Rift · Starfall Grove";
        var n = WebUtility.HtmlEncode(name);
        var l = WebUtility.HtmlEncode(link);
        var body = $"""
            <p style="margin:0 0 6px;font:800 13px/1.4 {Font};letter-spacing:2px;text-transform:uppercase;color:{GoldDark}">✦ Password reset ✦</p>
            <h1 style="margin:0 0 18px;font:700 28px/1.25 {Serif};color:{Ink}">Hi {n}, ready for a new password?</h1>
            <p style="margin:0 0 22px;font:400 16px/1.6 {Font};color:{Ink}">Someone (hopefully you) asked to reset the password of your Mini Rift account. Tap the button within <b>one hour</b> to choose a new one. It works once.</p>
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 auto 24px"><tr><td align="center" bgcolor="{Gold}" style="border-radius:16px;border:2px solid {Ink};box-shadow:0 4px 0 {Ink}">
              <a href="{l}" style="display:inline-block;padding:15px 34px;font:900 17px/1 {Font};color:{Ink};text-decoration:none;border-radius:16px">Choose a new password &rarr;</a>
            </td></tr></table>
            <p style="margin:0 0 6px;font:400 13px/1.5 {Font};color:{Muted}">Button not working? Copy this address into your browser:</p>
            <p style="margin:0 0 22px;font:400 12px/1.5 monospace;color:{Muted};word-break:break-all"><a href="{l}" style="color:{GoldDark}">{l}</a></p>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr><td style="border-top:2px dashed #E3CFA6;padding-top:16px;font:400 13px/1.5 {Font};color:{Muted}">
              Didn't ask for this? Just ignore this email: your password stays the same, and nobody can change it without this link.
            </td></tr></table>
            """;
        return ("Reset your Mini Rift password", text, Page("Choose a new password for your Mini Rift account.", body));
    }

    /// <summary>The welcome email after signing up: thanks, and a link that confirms the email address.</summary>
    public static (string Subject, string Text, string Html) Welcome(string name, string link)
    {
        var text = $"Hi {name},\n\nThank you for creating your Mini Rift account! Please confirm your registration by opening this link (it works for 7 days):\n\n{link}\n\nYour starting heroes and coins are waiting. See you on the Rift!\n\nDidn't sign up? Ignore this email and nothing happens.\n\nMini Rift · Starfall Grove";
        var n = WebUtility.HtmlEncode(name);
        var l = WebUtility.HtmlEncode(link);
        var body = $"""
            <p style="margin:0 0 6px;font:800 13px/1.4 {Font};letter-spacing:2px;text-transform:uppercase;color:{GoldDark}">✦ Welcome to the Rift ✦</p>
            <h1 style="margin:0 0 18px;font:700 28px/1.25 {Serif};color:{Ink}">Thank you for joining, {n}!</h1>
            <p style="margin:0 0 22px;font:400 16px/1.6 {Font};color:{Ink}">Your Mini Rift account is ready. One last step: tap the button to <b>confirm your registration</b>. That way you can always get back in if you forget your password.</p>
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 auto 24px"><tr><td align="center" bgcolor="{Gold}" style="border-radius:16px;border:2px solid {Ink};box-shadow:0 4px 0 {Ink}">
              <a href="{l}" style="display:inline-block;padding:15px 34px;font:900 17px/1 {Font};color:{Ink};text-decoration:none;border-radius:16px">Confirm my registration &rarr;</a>
            </td></tr></table>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 22px"><tr>
              <td width="33%" align="center" style="padding:10px 4px;font:800 13px/1.4 {Font};color:{Ink}"><div style="font-size:24px">⚔</div>3v3 battles</td>
              <td width="33%" align="center" style="padding:10px 4px;font:800 13px/1.4 {Font};color:{Ink}"><div style="font-size:24px">✦</div>Duels</td>
              <td width="33%" align="center" style="padding:10px 4px;font:800 13px/1.4 {Font};color:{Ink}"><div style="font-size:24px">🏆</div>Ranks &amp; skins</td>
            </tr></table>
            <p style="margin:0 0 6px;font:400 13px/1.5 {Font};color:{Muted}">Button not working? Copy this address into your browser:</p>
            <p style="margin:0 0 22px;font:400 12px/1.5 monospace;color:{Muted};word-break:break-all"><a href="{l}" style="color:{GoldDark}">{l}</a></p>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr><td style="border-top:2px dashed #E3CFA6;padding-top:16px;font:400 13px/1.5 {Font};color:{Muted}">
              Didn't sign up for Mini Rift? Just ignore this email and nothing happens. The link works for 7 days.
            </td></tr></table>
            """;
        return ("Welcome to Mini Rift! Please confirm your registration", text, Page("Thank you for creating your account. Confirm your registration in one tap.", body));
    }

    /// <summary>The frame every email shares: the logo over the night sky, the parchment card, and the footer.</summary>
    private static string Page(string preheader, string body) => $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light only"><title>Mini Rift</title></head>
        <body style="margin:0;padding:0;background:{Night}">
        <div style="display:none;max-height:0;overflow:hidden;opacity:0">{WebUtility.HtmlEncode(preheader)}</div>
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{Night}" style="background:{Night};background-image:radial-gradient(circle at 20% 10%,{Plum} 0,{Night} 60%)">
          <tr><td align="center" style="padding:32px 14px 40px">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:520px">
              <tr><td align="center" style="padding:0 0 6px;font:400 14px/1 {Font};color:{Gold};letter-spacing:8px">✦ · ✧ · ✦</td></tr>
              <tr><td align="center" style="padding:8px 0 18px">
                <img src="{Logo}" width="76" height="76" alt="" style="display:block;border:3px solid {Gold};border-radius:20px">
                <div style="margin-top:12px;font:700 30px/1 {Serif};color:{Gold};letter-spacing:1px">Mini Rift</div>
                <div style="margin-top:6px;font:700 12px/1 {Font};color:#CDB8D6;letter-spacing:3px;text-transform:uppercase">Storybook battles</div>
              </td></tr>
              <tr><td bgcolor="{Parchment}" style="background:{Parchment};border:3px solid {Gold};border-radius:24px;padding:30px 28px;box-shadow:0 10px 0 rgba(0,0,0,.35)">
                {body}
              </td></tr>
              <tr><td align="center" style="padding:22px 10px 0;font:400 12px/1.6 {Font};color:#A894AE">
                Mini Rift · a Starfall Grove game<br>
                <a href="https://starfallgrove.eu/support/" style="color:{Gold};text-decoration:none">Help</a> &nbsp;·&nbsp; <a href="https://starfallgrove.eu/minirift/privacy/" style="color:{Gold};text-decoration:none">Privacy</a>
              </td></tr>
            </table>
          </td></tr>
        </table>
        </body></html>
        """;
}
