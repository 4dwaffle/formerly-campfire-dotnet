using Campfire.Features.Persistence;
using Dapper;
using Campfire.Contracts;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Campfire.Features.Identity;

// HTML follows the pinned Rails views. Dynamic values are escaped at their output boundary.
public sealed class IdentityHtml(HttpContext context, IDataStore db, IAuthService auth, IRailsCrypto crypto, IPageRenderer renderer)
{
    private static string E(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");
    private string Icon(string name, int size = 20) => renderer.Icon(name, size);
    private string Image(string name, string classes = "", string attributes = "") => $"<img src=\"{E(renderer.Asset(name))}\" class=\"{E(classes)}\" aria-hidden=\"true\" {attributes}>";
    private string Back(string destination = "/") => $"<div class=\"flex-item-justify-start\"><a href=\"{E(destination)}\" class=\"btn\">{Icon("arrow-left.svg")}<span class=\"for-screen-reader\">Go Back</span></a></div>";
    private string Submit(string label = "Save changes", string icon = "check.svg") => $"<button class=\"btn btn--reversed center txt-large\" type=\"submit\">{Icon(icon)}<span class=\"for-screen-reader\">{E(label)}</span></button>";
    private string Form(string action, string body, string method = "post", string classes = "flex flex-column gap", string attributes = "", bool multipart = false) => $"<form action=\"{E(action)}\" accept-charset=\"UTF-8\" method=\"post\" class=\"{E(classes)}\" {attributes}{(multipart ? " enctype=\"multipart/form-data\"" : "")}><input type=\"hidden\" name=\"authenticity_token\" value=\"{E(auth.CsrfToken(context))}\">" + (method == "post" ? "" : $"<input type=\"hidden\" name=\"_method\" value=\"{E(method)}\">") + body + "</form>";
    public IResult Page(string title, string body, string nav = "", string bodyClass = "", int status = 200, string footer = "")
    {
        var notice = (auth as AuthService)?.ConsumeNotice(context);
        if (notice != null) body = "<div class=\"flash\" data-controller=\"element-removal\" data-action=\"animationend->element-removal#remove\"><div class=\"flash__inner shadow\">" + Icon("check.svg",24) + "</div><span class=\"for-screen-reader\" role=\"alert\" aria-atomic=\"true\">" + E(notice) + "</span></div>" + body;
        return Results.Content(renderer.Layout(auth.Current(context) ?? new UserRecord(), body, title, nav, footer, bodyClass: bodyClass), "text/html", statusCode: status);
    }
    private string AccountName => ReadAccountName();
    private string ReadAccountName() => db.Read(queryConnection1 => queryConnection1.QuerySingleOrDefault<string>("SELECT name FROM accounts LIMIT 1")) ?? "Campfire";
    private static string VersionBadge()
    {
        var version = Environment.GetEnvironmentVariable("APP_VERSION");
        if (string.IsNullOrWhiteSpace(version)) version = Environment.GetEnvironmentVariable("GIT_REVISION");
        if (string.IsNullOrWhiteSpace(version)) version = "0";
        return "<span class=\"version-badge\">" + E(version) + "</span>";
    }
    private string HelpContact()
    {
        var owner = db.Read(c => c.QueryFirstOrDefault<UserRecord>("SELECT * FROM users WHERE role=1 ORDER BY id LIMIT 1"));
        return owner == null ? "" : $"<div class=\"txt-align-center margin-block-double full-width\"><a href=\"{E("mailto:\"" + owner.Name + "\" <" + owner.EmailAddress + ">")}\" class=\"btn center\" title=\"{E("Email " + owner.Name)}\">{Icon("lifebuoy.svg")}<span>{E(owner.EmailAddress)}</span></a><div class=\"txt-align-center center margin-block txt-subtle\">Campfire&trade; version {VersionBadge()}</div></div>";
    }
    private static string CurrentOrigin(HttpContext c) => c.Request.Scheme + "://" + c.Request.Host + c.Request.PathBase;
    private string AvatarUrl(UserRecord user) => "/users/" + Uri.EscapeDataString(crypto.SignedId("User", user.Id, "avatar")) + "/avatar?v=" + (DateTime.TryParse(user.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var updated) ? updated.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) : Uri.EscapeDataString(user.UpdatedAt));
    private string Avatar(UserRecord user, int size = 48) => $"<img src=\"{E(AvatarUrl(user))}\" width=\"{size}\" height=\"{size}\" aria-hidden=\"true\" loading=\"lazy\">";
    private string Input(string name, string placeholder, string icon, string translation, string? value = null, string type = "text", string autocomplete = "off", bool required = false, bool autofocus = false, string classes = "txt-large", string attributes = "")
    {
        return $"<div class=\"flex align-center gap\">{Translation(translation)}<label class=\"flex align-center gap flex-item-grow input input--actor {E(classes)}\"><input type=\"{E(type)}\" name=\"{E(name)}\" id=\"{E(name.Replace('[', '_').Replace("]", ""))}\" class=\"input\" autocomplete=\"{E(autocomplete)}\" placeholder=\"{E(placeholder)}\" value=\"{E(value)}\" {(required ? "required" : "")} {(autofocus ? "autofocus" : "")} {attributes}>{Icon(icon, 24)}</label></div>";
    }
    private string Upload(string name, string preview, string label = "Upload avatar", bool autoSubmit = false)
    {
        return $"<label class=\"align-center center avatar__form gap\" data-controller=\"upload-preview\"><div class=\"btn input--file\">{Icon("camera.svg")}<input type=\"file\" name=\"{E(name)}\" class=\"input\" accept=\"image/*\" data-upload-preview-target=\"input\" data-action=\"upload-preview#previewImage{(autoSubmit ? " change->form#submit" : "")}\"><span class=\"for-screen-reader\">{E(label)}</span></div><div class=\"btn avatar input--file txt-xx-large\"><img src=\"{E(preview)}\" aria-hidden=\"true\" data-upload-preview-target=\"image\"><span class=\"for-screen-reader\">Avatar</span></div></label>";
    }
    private string AutoUpload(string name, string preview, string action, string uploadLabel, string avatarLabel, int size, string classes = "")
    {
        var input = $"<input type=\"file\" name=\"{E(name)}\" id=\"{(name == "user[avatar]" ? "file" : "account_logo")}\" class=\"input\" accept=\"image/*\" data-upload-preview-target=\"input\" data-action=\"upload-preview#previewImage change->form#submit\">";
        var camera = $"<label class=\"btn input--file\">{Icon("camera.svg")}{input}<span class=\"for-screen-reader\">{E(uploadLabel)}</span></label>";
        var image = $"<label class=\"btn avatar input--file txt-xx-large{classes}\"><img src=\"{E(preview)}\" width=\"{size}\" height=\"{size}\" aria-hidden=\"true\" data-upload-preview-target=\"image\">{input}<span class=\"for-screen-reader\">{E(avatarLabel)}</span></label>";
        return Form(action, camera, "patch", "txt-medium", "data-controller=\"form\"", true) + Form(action, image, "patch", attributes: "data-controller=\"form\"", multipart: true);
    }
    public IResult Signup(bool first)
    {
        var legend = first ? "<legend class=\"txt-large txt-align-center\"><strong>Set up Campfire</strong></legend>" : $"<legend class=\"txt-align-center flex gap\"><figure class=\"account-logo avatar\"><img src=\"/account/logo\" alt=\"Account logo\" width=\"300\" height=\"300\"></figure><strong class=\"txt-large\">{E(AccountName)}</strong></legend>";
        var fields = $"<section class=\"nametag u-relative\"><div class=\"flex justify-center align-center pad-block\">{Image("lanyard.svg", "nametag__lanyard")}</div><div class=\"nametag__inner flex flex-column gap\"><fieldset class=\"flex flex-column center-block\">{legend}{Upload("user[avatar]", renderer.Asset("default-avatar.svg"), "Upload avatar")}</fieldset>";
        fields += Input("user[name]", "Name", "person.svg", "user_name", autocomplete: "name", required: true, autofocus: true, attributes: "data-1p-ignore=\"true\"") + Input("user[email_address]", "Email address", "email.svg", "email_address", type: "email", autocomplete: "username", required: true) + Input("user[password]", "Password", "password.svg", "password", type: "password", autocomplete: "new-password", required: true, attributes: "maxlength=\"72\"") + Submit("Save", first ? "arrow-right.svg" : "check.svg") + "</div></section>";
        var nav = first ? "" : $"<div class=\"flex-item-justify-end\"><a href=\"/session/new\" class=\"btn flex-item-justify-end\">{Icon("login-keys.svg")}<span class=\"for-screen-reader\">Sign in</span></a></div>";
        return Page(first ? "Set up Campfire" : "Sign up", Form(context.Request.Path, fields, classes: first ? "center max-width" : "center", multipart: true) + HelpContact(), nav, "signup");
    }
    public IResult Login(int status = 200)
    {
        var form = "<fieldset class=\"flex flex-column gap center-block upad\"><legend class=\"txt-large txt-align-center\"><strong>" + E(AccountName) + "</strong></legend>" + Input("email_address", "Enter your email address", "email.svg", "email_address", context.Request.Query["email_address"].ToString(), "email", "username", true, true) + Input("password", "Enter your password", "password.svg", "password", type: "password", autocomplete: "current-password", required: true, attributes: "maxlength=\"72\"") + Submit("Go", "arrow-right.svg") + "</fieldset>";
        var logo = "<figure class=\"account-logo avatar center margin-block-end txt-xx-large\"><img src=\"/account/logo\" alt=\"Account logo\" width=\"300\" height=\"300\"></figure>";
        var flash = status == 200 ? "" : "<div class=\"flash\" data-controller=\"element-removal\" data-action=\"animationend->element-removal#remove\"><div class=\"flash__inner shadow\" style=\"--flash-background: var(--color-negative)\">" + Icon("alert.svg",24) + "</div><span class=\"for-screen-reader\" role=\"alert\" aria-atomic=\"true\">Too many requests or unauthorized.</span></div>";
        return Page("Sign in", $"<meta name=\"turbo-visit-control\" content=\"reload\">{flash}<section class=\"txt-align-center\"><div class=\"panel{(status == 200 ? "" : " shake")}\">{logo}{Form("/session", form)}</div>{HelpContact()}</section>", status: status);
    }
    public IResult Profile(UserRecord user)
    {
        var avatarForm = AutoUpload("user[avatar]", AvatarUrl(user), "/users/me/profile", "Upload avatar", "Avatar", 300);
        if (db.Read(queryConnection3 => queryConnection3.ExecuteScalar<long>("SELECT count(*) FROM active_storage_attachments WHERE record_type='User' AND record_id=@id AND name='avatar'",new { id = user.Id })) > 0) avatarForm += Form("/users/" + user.Id + "/avatar", $"<button class=\"btn btn--negative txt-small avatar__delete-btn\">{Icon("minus.svg")}<span class=\"for-screen-reader\">Delete avatar</span></button>", "delete", "");
        avatarForm = "<div class=\"align-center center avatar__form gap\" data-controller=\"upload-preview\">" + avatarForm + "</div>";
        var fields = Input("user[name]", "Enter your name", "person.svg", "user_name", user.Name, "text", "name", true, true, attributes: "data-1p-ignore=\"true\"") + Input("user[email_address]", "Enter your email address", "email.svg", "email_address", user.EmailAddress, "email", "username") + Input("user[password]", "Change password", "password.svg", "update_password", type: "password", autocomplete: "new-password", attributes: "maxlength=\"72\"") + $"<div class=\"flex align-start gap\">{Translation("bio")}<label class=\"flex align-center gap flex-item-grow input input--actor\"><textarea name=\"user[bio]\" class=\"input txt-large\" placeholder=\"A few words about yourself…\" maxlength=\"200\" rows=\"3\">{E(user.Bio)}</textarea>{Icon("bio.svg",24)}</label></div>" + Submit();
        var rooms = db.Read(queryConnection4 => queryConnection4.Query<MembershipRow>("SELECT r.id,r.name,r.type,m.involvement FROM memberships m JOIN rooms r ON r.id=m.room_id WHERE m.user_id=@id ORDER BY (r.type='Rooms::Direct'),lower(r.name)",new { id = user.Id }).Materialize());
        var memberships = new StringBuilder();
        var wasDirect = false;
        foreach (var room in rooms)
        {
            if (!wasDirect && room.Type == "Rooms::Direct" && memberships.Length > 0) memberships.Append("<hr class=\"separator full-width\" style=\"--border-style:solid\">");
            wasDirect = room.Type == "Rooms::Direct";
            var members = room.Type == "Rooms::Direct" ? db.Read(queryConnection5 => queryConnection5.Query<string>("SELECT u.name FROM users u JOIN memberships m ON m.user_id=u.id WHERE m.room_id=@roomId AND u.id<>@userId",new { roomId = room.Id, userId = user.Id }).Materialize()) : [];
            var name = room.Type != "Rooms::Direct" ? room.Name : members.Count switch { 0 => user.Name, 1 => members[0], 2 => members[0]+" and "+members[1], _ => string.Join(", ", members.Take(members.Count-1))+", and "+members[^1] };
            memberships.Append($"<li class=\"flex align-center gap margin-none min-width membership-item\"><a href=\"/rooms/{room.Id}\" class=\"overflow-ellipsis fill-shade txt-primary txt-undecorated\"><strong>{E(name)}</strong></a><hr class=\"separator\" aria-hidden=\"true\"><span class=\"txt-small\"><turbo-frame id=\"involvement_rooms_{room.Type.Replace("Rooms::", "").ToLowerInvariant()}_{room.Id}\">{InvolvementControl(room.Id, room.Type, room.Involvement)}</turbo-frame></span></li>");
        }
        var logout = Form("/session", "<input type=\"hidden\" name=\"push_subscription_endpoint\" data-sessions-target=\"pushSubscriptionEndpoint\"><button class=\"btn\" data-action=\"sessions#logout:prevent\">" + Icon("logout.svg") + "<span class=\"for-screen-reader\">Log out</span></button>", "delete", "", "data-controller=\"sessions\"");
        var nav = Back() + "<div class=\"flex-item-justify-end\">" + logout + "</div>";
        return Page(user.Name, $"<section class=\"panel flex flex-column gap\" style=\"view-transition-name: avatar-{user.Id}\">{renderer.InstallInstructions(context)}{avatarForm}{Form("/users/me/profile", fields, "patch", attributes: "data-controller=\"form\"")}<div class=\"margin-block pad-inline pad-block fill-shade border-radius\"><menu class=\"flex flex-column gap margin-none pad\">{memberships}</menu></div>{Transfer(user)}</section>", nav);
    }
    public string InvolvementControl(long roomId, string type, string involvement)
    {
        var order = type == "Rooms::Direct" ? new[] { "everything", "nothing" } : ["mentions", "everything", "nothing", "invisible"];
        var next = order[(Array.IndexOf(order, involvement) + 1) % order.Length];
        var id = "involvement_label_rooms_" + type.Replace("Rooms::", "").ToLowerInvariant() + "_" + roomId;
        var label = involvement switch { "mentions" => "Notifying about @ mentions", "everything" => "Notifying about all messages", "invisible" => "Notifications are off and room invisible in sidebar", _ => "Notifications are off" };
        return Form($"/rooms/{roomId}/involvement?involvement={next}", $"<button type=\"submit\" role=\"checkbox\" aria-checked=\"true\" aria-labelledby=\"{id}\" tabindex=\"0\" class=\"btn {E(involvement)}\">{Icon("notification-bell-"+involvement+".svg")}<span class=\"for-screen-reader\" id=\"{id}\">{label}</span></button>", "put", "button_to");
    }
    public string Transfer(UserRecord user)
    {
        var url = CurrentOrigin(context) + "/session/transfers/" + crypto.SignedId("User", user.Id, "transfer");
        var label = auth.Current(context)?.Id == user.Id ? "<label for=\"session_transfer_url\" class=\"for-screen-reader\">Use this link to login automatically on another device</label>" : "<div class=\"flex align-center gap justify-center\">"+Icon("crown.svg",16)+"<label for=\"session_transfer_url\">Share to get them back into their account</label></div>";
        return $"<fieldset><legend class=\"gap\">{Icon("laptop.svg",36)}{Icon("transfer.svg",36)}{Icon("mobile-phone.svg",36)}</legend><div class=\"flex flex-column gap\">{label}<input type=\"text\" class=\"input\" value=\"{E(url)}\" id=\"session_transfer_url\" readonly><div class=\"flex align-center center gap\">{ShareTools(url, "auto-login", "Your sign-in link", "This is your own private sign-in URL, DO NOT SHARE IT. Use it to sign-in on another device or if you get locked out.")}</div></div></fieldset>";
    }
    private string ShareTools(string url, string label, string title, string text)
    {
        var qr = "/qr_code/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).Replace('+','-').Replace('/','_');
        return $"<a href=\"{E(qr)}\" class=\"btn\" data-lightbox-target=\"image\" data-action=\"lightbox#open\" data-lightbox-url-value=\"{E(qr)}\"><span class=\"for-screen-reader\">Show {E(label)} QR code</span>{Icon("qr-code.svg")}</a>" + Copy(url, "Copy " + label + " link") + $"<button class=\"btn\" hidden data-controller=\"web-share\" data-action=\"web-share#share\" data-web-share-url-value=\"{E(url)}\" data-web-share-text-value=\"{E(text)}\" data-web-share-title-value=\"{E(title)}\"><span class=\"for-screen-reader\">Share {E(label)} link</span>{Icon("share.svg")}</button>";
    }
    private string Copy(string text, string label) => $"<button class=\"btn\" data-controller=\"copy-to-clipboard\" data-action=\"copy-to-clipboard#copy\" data-copy-to-clipboard-content-value=\"{E(text)}\" data-copy-to-clipboard-success-class=\"btn--success\"><span class=\"for-screen-reader\">{E(label)}</span>{Icon("copy-paste.svg")}</button>";
    public IResult TransferPage() => Page("Sign in", Form(context.Request.Path, "", "put", attributes: "data-controller=\"auto-submit\""));
    public IResult User(UserRecord target, UserRecord viewer)
    {
        var content = $"<div class=\"avatar txt-xx-large center\" style=\"background:white\">{Avatar(target,300)}</div><div class=\"flex flex-column gap\" style=\"--row-gap:calc(var(--block-space)/3)\"><h1 class=\"txt-x-large txt-tight-lines margin-none\">{E(target.Name)}</h1>" + (viewer.IsAdmin ? $"<div><a href=\"mailto:{E(target.EmailAddress)}\">{E(target.EmailAddress)}</a></div>" : "") + $"<div>{E(target.Bio)}</div></div>";
        if (target.Status == 1) content = $"<div class=\"avatar txt-xx-large center\" style=\"background:white\">{Avatar(target,300)}</div><div><h1 class=\"txt-x-large margin-none\">{E(target.Name)}</h1><div>{E(target.Name)} is no longer on this account</div></div>";
        else if (target.Role == 2) content = $"<div class=\"avatar txt-xx-large center\" style=\"background:white\">{Avatar(target,300)}</div><div class=\"pad-double--inline push--inline push--block-start\">" + (target.Status == 0 ? Form("/rooms/directs", $"<input type=\"hidden\" name=\"user_ids[]\" value=\"{target.Id}\"><button class=\"btn btn--primary full-width txt--large\">{Icon("messages.svg")}</button>", classes: "button_to") : $"<div>{E(target.Name)} is no longer on this account</div>") + "</div>";
        else if (target.Status == 0)
        {
            content += "<div class=\"pad-inline-double margin-inline margin-block-start\">" + Form("/rooms/directs", $"<input type=\"hidden\" name=\"user_ids[]\" value=\"{target.Id}\"><button class=\"btn btn--reversed full-width txt-large\">{Icon("messages.svg")}<span class=\"for-screen-reader\">Ping {E(target.Name)}</span></button>", classes: "") + "</div>";
            if (viewer.IsAdmin) content += "<hr class=\"margin-block-start borderless\">" + Transfer(target);
        }
        if (viewer.IsAdmin && viewer.Id != target.Id && target.Status != 1 && target.Role != 2)
        {
            var unban = target.Status != 0; var confirm = unban ? "Are you sure you want to remove the ban on this user?" : "Are you sure you want to ban this user? This will log them out, delete their messages, and block their IP addresses.";
            content += "<div class=\"margin-block-start\">" + Form($"/users/{target.Id}/ban", $"<button class=\"btn {(unban ? "btn--negative " : "")}full-width\" data-turbo-confirm=\"{E(confirm)}\">{Icon("cancel.svg")}<span>{(unban ? "Remove ban" : "Ban " + E(target.Name))}</span></button>", unban ? "delete" : "post", "") + "</div>";
        }
        var nav = Back() + (viewer.Id == target.Id ? $"<div class=\"flex-item-justify-end\"><a href=\"/users/me/profile\" class=\"btn\">{Icon("pencil.svg")}<span class=\"for-screen-reader\">Edit my profile</span></a></div>" : "");
        return Page(target.Name, $"<section class=\"panel txt-align-center\"><div class=\"flex flex-column gap {(target.Status == 2 ? "banned" : "")}\">{content}</div></section>", nav);
    }
    public IResult Account(UserRecord viewer)
    {
        var account = db.Read(queryConnection6 => queryConnection6.QuerySingleOrDefault<AccountRow>("SELECT * FROM accounts LIMIT 1")); if (account == null) return Results.NotFound();
        var nav = Back();
        var body = new StringBuilder("<section class=\"panel txt-align-center flex flex-column gap\" style=\"view-transition-name:account-settings\">");
        if (viewer.IsAdmin)
        {
            nav += $"<div class=\"flex align-center gap flex-item-justify-end\"><a href=\"/account/bots\" class=\"btn\" style=\"view-transition-name:chat-bots\">{Icon("bot.svg")}<span class=\"for-screen-reader\">Set up chat bots</span></a><a href=\"/account/custom_styles/edit\" class=\"btn\" style=\"view-transition-name:custom-styles\">{Icon("art.svg")}<span class=\"for-screen-reader\">Custom styles</span></a></div>";
            body.Append("<div class=\"align-center center avatar__form gap\" data-controller=\"upload-preview\">" + AutoUpload("account[logo]", "/account/logo", "/account", "Upload logo", "Upload logo", 48, " account-logo"));
            if (db.Read(queryConnection7 => queryConnection7.ExecuteScalar<long>("SELECT count(*) FROM active_storage_attachments WHERE record_type='Account' AND record_id=@id AND name='logo'",new { id = account.Id })) > 0) body.Append(Form("/account/logo", $"<button class=\"btn btn--negative txt-small avatar__delete-btn\">{Icon("minus.svg")}<span class=\"for-screen-reader\">Delete logo</span></button>", "delete", ""));
            body.Append("</div>");
            body.Append(Form("/account", Input("account[name]", "Name this account", "person.svg", "account_name", account.Name, autofocus: true, classes: "", attributes: "data-action=\"keydown.enter->form#submit\"") + Submit(), "patch", attributes: "data-controller=\"form\""));
            var restricted = JsonNode.Parse(account.Settings ?? "{}")?["restrict_room_creation_to_administrators"]?.ToString() is "true" or "1";
            body.Append("<div class=\"margin-block-start pad-block pad-inline-double fill-shade border-radius\">" + Form("/account", $"<div class=\"flex-item-grow flex align-center gap txt-align-start\">{Icon("crown.svg",18)} Must be admin to create new rooms</div><input type=\"hidden\" name=\"account[settings][restrict_room_creation_to_administrators]\" value=\"{(!restricted).ToString().ToLowerInvariant()}\"><label class=\"switch\"><input type=\"checkbox\" class=\"switch__input\" {(restricted ? "checked" : "")} data-action=\"change->form#submit\"><span class=\"switch__btn round\"></span><span class=\"for-screen-reader\">Must be admin to create new rooms</span></label>", "put", "flex align-center gap center", "data-controller=\"form\"") + "</div>");
        }
        else body.Append($"<img src=\"/account/logo\" class=\"account-logo txt-xx-large center\" aria-hidden=\"true\"><h1 class=\"flex-item-grow txt-x-large\">{E(account.Name)}</h1>");
        var url = CurrentOrigin(context) + "/join/" + account.JoinCode;
        var invite = $"<div class=\"flex flex-column align-center gap\"><label class=\"flex flex-column gap full-width\" style=\"--row-gap:0.5em\"><strong id=\"invite_label\" class=\"invite-label\">Share to invite more people</strong><span class=\"flex align-center gap input input--actor fill-white\">{Icon("person-add.svg")}<input type=\"text\" class=\"input\" id=\"invite_url\" value=\"{E(url)}\" aria-labelledby=\"invite_label\" readonly></span></label><div class=\"flex align-center gap\">{ShareTools(url, "join", "Link to join Campfire", "Hit this link to join me in Campfire and start chatting.")}" + (viewer.IsAdmin ? Form("/account/join_code", $"<button class=\"btn btn--regenerate\">{Icon("refresh.svg")}<span class=\"for-screen-reader\">Regenerate join link</span></button>", classes: "") : "") + "</div></div>";
        body.Append("<div class=\"margin-block pad-inline pad-block-start fill-shade border-radius\">" + invite + "<hr class=\"margin-block separator full-width\" style=\"--border-style:solid\"><menu class=\"flex flex-column gap margin-none pad\"><turbo-frame id=\"account_users\">" + AccountUsers(viewer) + "</turbo-frame></menu></div></section>");
        return Page("Account settings", body.ToString(), nav, footer: "<div class=\"txt-align-center center margin-block-double txt-subtle\">Campfire&trade; version "+VersionBadge()+"</div>");
    }
    public string AccountUsers(UserRecord viewer, bool activeOnly = false)
    {
        var page = int.TryParse(context.Request.Query["page"], out var requested) && requested > 0 ? Math.Min(requested, 1000000) : 1;
        var offset = (page - 1) * 500;
        var users = db.Read(queryConnection8 => queryConnection8.Query<UserRecord>(activeOnly ? "SELECT * FROM users WHERE role<>2 AND status=0 ORDER BY lower(name) LIMIT 501 OFFSET @offset" : viewer.IsAdmin ? "SELECT * FROM users WHERE role<>2 AND status IN (0,2) ORDER BY (role<>1),lower(name)" : "SELECT * FROM users WHERE role<>2 AND status=0 ORDER BY (role<>1),lower(name)",new { offset }).Materialize());
        var result = new StringBuilder(); var previous = -1;
        foreach (var user in activeOnly ? users.Take(500) : users)
        {
            if (!activeOnly && previous == 1 && user.Role != 1) result.Append("<hr class=\"separator full-width\" style=\"--border-style:solid\">"); previous = user.Role;
            result.Append($"<li class=\"flex align-center gap margin-none {(user.Status == 2 ? "banned" : "")}\"><figure class=\"avatar flex-item-no-shrink\" style=\"--avatar-size:3.75ch\">{Avatar(user)}</figure><div class=\"min-width\"><div class=\"overflow-ellipsis fill-shade\"><strong>{E(user.Name)}</strong></div></div><hr class=\"separator\" aria-hidden=\"true\">");
            if (viewer.IsAdmin && user.Status == 0)
            {
                result.Append(Form($"/account/users/{user.Id}", $"<label class=\"btn txt-small flex-item-no-shrink\" for=\"role_user_{user.Id}\"><span class=\"for-screen-reader\">Role: {(user.Role == 1 ? "Administrator" : "Member")}</span>{Icon("crown.svg")}<input name=\"user[role]\" type=\"hidden\" value=\"member\"><input name=\"user[role]\" type=\"checkbox\" id=\"role_user_{user.Id}\" value=\"administrator\" data-action=\"form#submit\" hidden {(user.Role == 1 ? "checked" : "")} {(viewer.Id == user.Id ? "disabled" : "")}></label>", "patch", "", "data-controller=\"form\""));
                if (viewer.Id != user.Id) result.Append(Form($"/account/users/{user.Id}", $"<button class=\"btn txt-small flex-item-no-shrink btn--negative\" data-turbo-confirm=\"Are you sure you want to permanently remove this person from the account? This can’t be undone.\">{Icon("minus.svg")}<span class=\"for-screen-reader\">Delete {E(user.Name)}</span></button>", "delete", ""));
            }
            if (viewer.Id == user.Id) result.Append($"<a href=\"/users/me/profile\" class=\"btn txt-small flex-item-no-shrink\" target=\"_top\">{Icon("pencil.svg")}<span class=\"for-screen-reader\">My settings</span></a>");
            result.Append("</li>");
        }
        if (users.Count > (activeOnly ? 500 : offset+500)) result.Append($"<turbo-frame id=\"next_page_container\" src=\"/account/users?page={page+1}&amp;format=turbo_stream\" loading=\"lazy\" class=\"flex center\"><div class=\"spinner center\"></div></turbo-frame>");
        return result.ToString();
    }
    public IResult AccountUsersResponse(UserRecord viewer)
    {
        var listing = AccountUsers(viewer, true);
        var next = listing.IndexOf("<turbo-frame id=\"next_page_container\"", StringComparison.Ordinal);
        var users = next < 0 ? listing : listing[..next];
        var append = next < 0 ? "" : "<turbo-stream action=\"append\" target=\"account_users\"><template>" + listing[next..] + "</template></turbo-stream>";
        return Results.Content("<turbo-stream action=\"replace\" target=\"next_page_container\"><template>" + users + "</template></turbo-stream>" + append, "text/vnd.turbo-stream.html");
    }
    public IResult CustomStyles()
    {
        var content = $"<div class=\"panel__button\">{Translation("custom_styles")}</div><div class=\"pad-inline-double margin-inline\"><h1 class=\"margin-none\">Custom CSS</h1><p class=\"flex flex-wrap align-center justify-center gap margin-none-block-start\"><span>Add custom CSS styles.</span>{Icon("alert.svg",16)}<span>Use Caution: you could break things.</span></p></div><label class=\"flex align-start gap flex-item-grow\"><textarea name=\"account[custom_styles]\" class=\"input input--code txt--small\" placeholder=\"Add CSS styles…\" autocomplete=\"off\" spellcheck=\"false\" autocorrect=\"off\" autocapitalize=\"off\" rows=\"16\">{E(db.Read(queryConnection9 => queryConnection9.QuerySingleOrDefault<string>("SELECT custom_styles FROM accounts LIMIT 1")))}</textarea></label>{Submit()}";
        return Page("Custom styles", "<section class=\"panel panel--wide txt-align-center flex flex-column position-relative\" style=\"view-transition-name:custom-styles\">" + Form("/account/custom_styles", content, "patch", attributes: "data-controller=\"form\" data-action=\"keydown.ctrl+enter->form#submit keydown.meta+enter->form#submit\"") + "</section>", Back("/account/edit"));
    }
    public IResult BotForm(UserRecord? bot)
    {
        var webhook = bot == null ? "" : db.Read(queryConnection10 => queryConnection10.QuerySingleOrDefault<string>("SELECT url FROM webhooks WHERE user_id=@id LIMIT 1",new { id = bot.Id }));
        var body = "<h1 class=\"for-screen-reader\">Chat Bot Setup</h1>" + Upload("user[avatar]", bot == null ? renderer.Asset("default-bot-avatar.svg") : AvatarUrl(bot), "Upload bot avatar") + Input("user[name]", "Name the bot", "bot.svg", "bot_name", bot?.Name, "text", "name", true, true, attributes: "data-1p-ignore=\"true\"") + Input("user[webhook_url]", "Webhook URL", "web.svg", "webhook_url", webhook, "url") + Submit();
        body = Form(bot == null ? "/account/bots" : $"/account/bots/{bot.Id}", body, bot == null ? "post" : "patch", multipart: true);
        if (bot != null) body += "<hr class=\"separator full-width margin-block-double\"><div class=\"flex align-center gap justify-space-between\">" + Form($"/account/bots/{bot.Id}", $"<button class=\"btn txt--small btn--negative\" aria-label=\"Delete this chat bot\" data-turbo-confirm=\"Are you sure you want to permanently remove this bot from the account? This can’t be undone.\">{Icon("trash.svg")}{Icon("bot.svg")}</button>", "delete", "") + Form($"/account/bots/{bot.Id}/key", $"<button class=\"btn full-width txt--small btn--negative\" aria-label=\"Generate a new key\" data-turbo-confirm=\"Are you sure you want to change the bot key? All usage of this bot must be updated.\">{Icon("refresh.svg")}{Icon("key.svg")}</button>", "put", "") + "</div>";
        return Page(bot == null ? "New chat bot" : "Edit bot", $"<section class=\"panel\"{(bot == null ? "" : " style=\"view-transition-name:chat-bot-" + bot.Id + "\"")}>{body}</section>", Back("/account/bots"));
    }
    public IResult Bots()
    {
        var bots = db.Read(queryConnection11 => queryConnection11.Query<UserRecord>("SELECT * FROM users WHERE role=2 AND status=0 ORDER BY lower(name)").Materialize());
        var content = new StringBuilder($"<section class=\"panel panel--wide txt-align-center flex flex-column position-relative\" style=\"view-transition-name:chat-bots\"><div class=\"flex align-center gap\"><div class=\"panel__button\">{Translation("chat_bots")}</div><div class=\"pad-inline-double center\"><h1 class=\"margin-none\">Chat bots</h1><p class=\"margin-none-block-start\">With Chat bots, other sites and services can post updates directly to Campfire.</p><a href=\"/account/bots/new\" class=\"btn btn--reversed txt-large\" aria-label=\"Add a chat bot\">{Icon("bot.svg")}{Icon("add.svg")}</a></div></div><div class=\"pad-inline pad-block-start\"><menu class=\"flex flex-column gap margin-none pad\">");
        foreach (var bot in bots)
        {
            content.Append($"<li class=\"flex flex-column gap flush fill-shade border-radius pad-block pad-inline-double\"><div class=\"flex align-center gap\"><figure class=\"avatar flex-item-no-shrink\" style=\"--avatar-size:2.65em\">{Avatar(bot)}</figure><div class=\"min-width\"><div class=\"overflow-ellipsis txt-large\"><strong>{E(bot.Name)}</strong></div></div><a href=\"/account/bots/{bot.Id}/edit\" class=\"btn flex-item-justify-end\" style=\"view-transition-name:chat-bot-{bot.Id}\">{Icon("pencil.svg")}<span class=\"for-screen-reader\">Edit {E(bot.Name)}</span></a></div>");
            foreach (var room in db.Read(queryConnection12 => queryConnection12.Query<MembershipRow>("SELECT r.id,r.name FROM rooms r JOIN memberships m ON m.room_id=r.id WHERE m.user_id=@id AND r.type<>'Rooms::Direct' ORDER BY lower(r.name)",new { id = bot.Id }).Materialize()))
            {
                var url = CurrentOrigin(context) + $"/rooms/{room.Id}/{bot.Id}-{bot.BotToken}/messages";
                content.Append($"<fieldset class=\"gap max-width pad border border-radius\"><legend class=\"min-width txt-align-start pad-inline\"><strong class=\"overflow-ellipsis\">{E(room.Name)}</strong></legend>");
                foreach (var (command, icon, label) in new[] { ($"curl -d 'Hello!' {url}", "messages-outlined.svg", "messages"), ($"curl -F \"attachment=@/path/to/file\" {url}", "attachment.svg", "attachments") }) content.Append($"<div class=\"flex align-center gap\">{Icon(icon,24)}<div class=\"flex-item-grow\"><input type=\"text\" class=\"input full-width fill-white\" value=\"{E(command)}\" aria-label=\"curl command for posting {label}\" readonly></div><div class=\"txt-small\">{Copy(command, "Copy " + label + " command")}</div></div>");
                content.Append("</fieldset>");
            }
            content.Append("</li>");
        }
        content.Append("</menu></div></section>"); return Page("Chat bots", content.ToString(), Back("/account/edit"));
    }
    private string Translation(string key)
    {
        var languages = Translations.All.GetValueOrDefault(key, []);
        return $"<details class=\"position-relative\" data-controller=\"popup\" data-action=\"keydown.esc->popup#close toggle->popup#toggle click@document->popup#closeOnClickOutside\" data-popup-orientation-top-class=\"popup-orientation-top\"><summary class=\"btn\" tabindex=\"-1\">{Icon("globe.svg")}<span class=\"for-screen-reader\">Translate</span></summary><div class=\"language-list-menu shadow\" data-popup-target=\"menu\"><dl class=\"language-list\">{string.Concat(languages.Select(pair => "<dt>" + E(pair.Flag) + "</dt><dd class=\"margin-none\">" + E(pair.Text) + "</dd>"))}</dl></div></details>";
    }
    internal sealed class MembershipRow { public long Id { get; set; } public string? Name { get; set; } public string Type { get; set; } = ""; public string Involvement { get; set; } = ""; }
    internal sealed class AccountRow { public long Id { get; set; } public string Name { get; set; } = ""; public string JoinCode { get; set; } = ""; public string? Settings { get; set; } }
}

