using Boilerplate.BuildingBlocks.Core.Common;
using Boilerplate.BuildingBlocks.Mailing;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Domain;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using System.Collections.ObjectModel;
using System.Text;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// Builds the "confirm your e-mail" message — the link and the body — for the two places that owe a
/// user one: the handler that reacts to a committed registration, and the resend endpoint.
///
/// It only builds. Who sends it, and whether that send is awaited or queued, is the caller's
/// decision: registration's copy is sent by an event handler running on a dispatch cycle, and the
/// resend endpoint queues its copy on the mail queue so the request returns immediately. Keeping the
/// construction in one place is what stops the two links drifting apart — they are the same link.
/// </summary>
internal sealed class ConfirmationMailBuilder(
    UserManager<AppUser> userManager,
    IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor)
{
    /// <summary>
    /// Returns the message to send, or <c>null</c> when there is no address to send it to.
    /// </summary>
    /// <param name="user">The user to confirm; their token is minted here.</param>
    /// <param name="origin">Configured client origin the link is built on (see <see cref="MailLinkOrigin"/>).</param>
    public async Task<MailRequest?> BuildAsync(AppUser user, string origin)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrEmpty(user.Email))
        {
            return null;
        }

        var verificationUri = await GetEmailVerificationUriAsync(user, origin).ConfigureAwait(false);
        var body = BuildConfirmationEmailHtml(user.FirstName ?? user.UserName ?? "User", verificationUri);

        return new MailRequest(
            new Collection<string> { user.Email },
            "Confirm Your Email Address",
            body);
    }

    private async Task<string> GetEmailVerificationUriAsync(AppUser user, string origin)
    {
        string code = await userManager.GenerateEmailConfirmationTokenAsync(user).ConfigureAwait(false);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

        // Mail the *client* route, not the API route: a recipient clicking an API link lands on a raw
        // JSON body. The page reads userId/code/tenant from the query and calls the tenant-routed
        // endpoint itself. Built exactly like the reset-password link in UserPasswordService — the
        // configured origin with any trailing slash trimmed (Uri.ToString() adds one for a host-only
        // URL, which would produce "//confirm-email" and miss the client route) and QueryHelpers doing
        // the URL-encoding. The tenant rides in the query because the client route has no path segment
        // for it; it is a page parameter, never an input to tenant resolution (ADR-0002).
        return QueryHelpers.AddQueryString(
            $"{origin.TrimEnd('/')}/confirm-email",
            new Dictionary<string, string?>
            {
                [QueryStringKeys.UserId] = user.Id,
                [QueryStringKeys.Code] = code,
                ["tenant"] = multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id,
            });
    }

    private static string BuildConfirmationEmailHtml(string userName, string confirmationUrl)
    {
        return $"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>Confirm Your Email</title>
            </head>
            <body style="margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; background-color: #f8fafc;">
                <table role="presentation" style="width: 100%; border-collapse: collapse;">
                    <tr>
                        <td align="center" style="padding: 40px 0;">
                            <table role="presentation" style="width: 100%; max-width: 600px; border-collapse: collapse; background-color: #ffffff; border-radius: 8px; box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1);">
                                <tr>
                                    <td style="padding: 40px 40px 30px 40px; text-align: center; background-color: #2563eb; border-radius: 8px 8px 0 0;">
                                        <h1 style="margin: 0; color: #ffffff; font-size: 24px; font-weight: 600;">Confirm Your Email Address</h1>
                                    </td>
                                </tr>
                                <tr>
                                    <td style="padding: 40px;">
                                        <p style="margin: 0 0 20px 0; color: #334155; font-size: 16px; line-height: 1.6;">
                                            Hi {System.Net.WebUtility.HtmlEncode(userName)},
                                        </p>
                                        <p style="margin: 0 0 20px 0; color: #334155; font-size: 16px; line-height: 1.6;">
                                            Thank you for registering! Please confirm your email address by clicking the button below:
                                        </p>
                                        <table role="presentation" style="width: 100%; border-collapse: collapse;">
                                            <tr>
                                                <td align="center" style="padding: 30px 0;">
                                                    <a href="{System.Net.WebUtility.HtmlEncode(confirmationUrl)}" style="display: inline-block; padding: 14px 32px; background-color: #2563eb; color: #ffffff; text-decoration: none; font-size: 16px; font-weight: 600; border-radius: 6px;">
                                                        Confirm Email Address
                                                    </a>
                                                </td>
                                            </tr>
                                        </table>
                                        <p style="margin: 0 0 20px 0; color: #64748b; font-size: 14px; line-height: 1.6;">
                                            If the button doesn't work, copy and paste this link into your browser:
                                        </p>
                                        <p style="margin: 0 0 20px 0; color: #2563eb; font-size: 14px; line-height: 1.6; word-break: break-all;">
                                            {System.Net.WebUtility.HtmlEncode(confirmationUrl)}
                                        </p>
                                        <p style="margin: 30px 0 0 0; color: #64748b; font-size: 14px; line-height: 1.6;">
                                            If you didn't create an account, you can safely ignore this email.
                                        </p>
                                    </td>
                                </tr>
                                <tr>
                                    <td style="padding: 20px 40px; background-color: #f1f5f9; border-radius: 0 0 8px 8px; text-align: center;">
                                        <p style="margin: 0; color: #94a3b8; font-size: 12px;">
                                            This is an automated message. Please do not reply to this email.
                                        </p>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                </table>
            </body>
            </html>
            """;
    }
}
