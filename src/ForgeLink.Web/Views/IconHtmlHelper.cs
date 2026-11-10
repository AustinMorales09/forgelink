using System.Net;
using ForgeLink.UI;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ForgeLink.Web.Views;

/// <summary>MVC counterpart of the Blazor <c>Icon</c> component: same sprite, so both UIs share one icon set.</summary>
public static class IconHtmlHelper
{
    public static IHtmlContent Icon(this IHtmlHelper html, string name, string? cssClass = null) =>
        new HtmlString(
            $"<svg class=\"fl-icon {WebUtility.HtmlEncode(cssClass)}\" aria-hidden=\"true\" focusable=\"false\">" +
            $"<use href=\"/{IconSprite.Path}#{WebUtility.HtmlEncode(name)}\"></use></svg>");
}
