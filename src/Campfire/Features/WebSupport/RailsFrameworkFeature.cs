namespace Campfire.Features.WebSupport;

// These engine controllers inherit ActionController::Base, not Campfire's
// ApplicationController. Campfire never configures an Action Mailbox ingress.
public sealed record RailsEngineController(bool RequireCsrf = false);

public static class RailsFrameworkFeature
{
    public static void MapRailsFrameworkFeature(this WebApplication app)
    {
        app.MapGet("/recede_historical_location", () => Results.Content("Going back…", "text/html; charset=utf-8")).WithMetadata(new RailsEngineController());
        app.MapGet("/refresh_historical_location", () => Results.Content("Refreshing…", "text/html; charset=utf-8")).WithMetadata(new RailsEngineController());
        app.MapGet("/resume_historical_location", () => Results.Content("Staying put…", "text/html; charset=utf-8")).WithMetadata(new RailsEngineController());
        foreach (var ingress in new[] { "postmark", "relay", "sendgrid", "mandrill" })
            app.MapPost($"/rails/action_mailbox/{ingress}/inbound_emails", () => Results.StatusCode(404)).WithMetadata(new RailsEngineController());
        app.MapGet("/rails/action_mailbox/mandrill/inbound_emails", () => Results.StatusCode(404)).WithMetadata(new RailsEngineController());
        app.MapPost("/rails/action_mailbox/mailgun/inbound_emails/mime", () => Results.StatusCode(404)).WithMetadata(new RailsEngineController());
        // The pinned Rails engine disables its development conductor outside
        // development. Its tables/mailboxes are absent from Campfire's schema.
        foreach (var (path, methods) in new[]
        {
            ("/rails/conductor/action_mailbox/inbound_emails", new[] { "GET", "POST" }),
            ("/rails/conductor/action_mailbox/inbound_emails/new", new[] { "GET" }),
            ("/rails/conductor/action_mailbox/inbound_emails/{id}", new[] { "GET" }),
            ("/rails/conductor/action_mailbox/inbound_emails/sources/new", new[] { "GET" }),
            ("/rails/conductor/action_mailbox/inbound_emails/sources", new[] { "POST" }),
            ("/rails/conductor/action_mailbox/{id}/reroute", new[] { "POST" }),
            ("/rails/conductor/action_mailbox/{id}/incinerate", new[] { "POST" })
        })
            app.MapMethods(path, methods, () => Results.StatusCode(403)).WithMetadata(new RailsEngineController());
    }
}
