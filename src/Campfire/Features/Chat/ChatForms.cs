using Campfire.Contracts;

namespace Campfire.Features.Chat;

public sealed partial class ChatRenderer
{
    public string RoomForm(RoomRecord room, UserRecord user)
    {
        var canAdminister = room.Id == 0 || user.IsAdmin || room.CreatorId == user.Id;
        var nav = $"<div class=\"flex-item-justify-start\"><a class=\"btn\" href=\"{(room.Id == 0 ? "/" : "/rooms/" + room.Id)}\">{Icon("arrow-left.svg")}<span class=\"for-screen-reader\">Go Back</span></a></div>";
        if (room.Direct)
        {
            var participants = store.Members(room.Id);
            var members = string.Concat(participants.Where(x => participants.Count <= 1 || x.Id != user.Id).Select(x => $"<div class=\"member flex flex-column gap fill-shade pad border-radius\"><figure class=\"avatar center\" style=\"--avatar-border-radius: 10ch; --avatar-size: 10ch\">{Avatar(x)}</figure><strong>{E(x.Name)}</strong></div>"));
            var direct = $"<div class=\"panel txt-align-center\"><section class=\"directs--edit margin-block-end\">{members}</section><form class=\"button_to\" method=\"post\" action=\"/rooms/directs/{room.Id}\"><input type=\"hidden\" name=\"_method\" value=\"delete\"><button class=\"btn btn--negative center\" aria-label=\"Delete Ping\" data-turbo-confirm=\"Are you sure you want to delete this ping and all messages in it? This can’t be undone.\" type=\"submit\">{Icon("trash.svg")}Ping</button></form></div>";
            return Layout(user, Forms(direct), "Edit settings for " + RoomName(room, user.Id), nav);
        }
        var open = room.Type == "Rooms::Open";
        var selected = room.Id == 0 ? new HashSet<long> { user.Id } : store.Members(room.Id).Select(x => x.Id).ToHashSet();
        var users = store.Users().OrderByDescending(x => !open && selected.Contains(x.Id)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var userList = string.Concat(users.Select(member =>
        {
            var control = "";
            if (canAdminister)
            {
                if (open || room.Id == 0 && member.Id == user.Id) control = (open ? "" : $"<input type=\"hidden\" name=\"user_ids[]\" value=\"{member.Id}\">") + Icon("check.svg");
                else control = $"<label class=\"switch flex-item-no-shrink\"><input type=\"checkbox\" name=\"user_ids[]\" value=\"{member.Id}\" class=\"switch__input\" {(selected.Contains(member.Id) ? "checked" : "")}><span class=\"switch__btn round\"></span><span class=\"for-screen-reader\">Give {E(member.Name)} access to this room</span></label>";
            }
            return $"<li class=\"flex align-center gap margin-none\" data-value=\"{E(member.Name.ToLowerInvariant())}\"><figure class=\"avatar flex-item-no-shrink\" style=\"--avatar-size: 4ch\">{Avatar(member)}</figure><div class=\"min-width\"><div class=\"overflow-ellipsis fill-shade\"><strong>{E(member.Name)}</strong></div></div><hr class=\"separator\" aria-hidden=\"true\">{control}</li>";
        }));
        var typeSwitch = canAdminister ? $"<a class=\"btn--faux flex-inline\" tabindex=\"-1\" data-turbo-action=\"replace\" href=\"/rooms/{(open ? "closeds" : "opens")}/{(room.Id == 0 ? "new" : room.Id + "/edit")}\"><label for=\"room_type\" class=\"switch\"><input type=\"checkbox\" id=\"room_type\" class=\"switch__input\" {(open ? "checked" : "")}><span class=\"switch__btn round\"></span><span class=\"for-screen-reader\">{(open ? "Give only some access to this room" : "Give everyone access to this room")}</span></label></a>" : "";
        var everyone = canAdminister || open ? $"<li class=\"flex align-center gap margin-none\"><figure class=\"avatar flex-item-no-shrink\" style=\"--avatar-border-radius: 0; --avatar-size: 4ch\">{Icon("everyone.svg")}<span class=\"for-screen-reader\">Everyone</span></figure><div class=\"min-width\"><div class=\"overflow-ellipsis fill-shade\"><strong>Everyone</strong></div></div><hr class=\"separator\" aria-hidden=\"true\">{typeSwitch}</li><hr class=\"separator full-width\" style=\"--border-style: solid\">" : "";
        var filter = users.Count > 20 ? "<input type=\"search\" id=\"search\" autocorrect=\"off\" autocomplete=\"off\" data-1p-ignore=\"true\" class=\"input input--transparent full-width\" placeholder=\"Filter…\" data-action=\"input->filter#filter\">" : "";
        var heading = canAdminister ? $"<label class=\"flex-item-grow txt-large\"><input name=\"room[name]\" id=\"room_name\" class=\"input full-width\" required autofocus placeholder=\"Name the room\" data-turbo-permanent=\"true\" data-action=\"keydown.enter->form#submit:prevent\" type=\"text\" value=\"{E(room.Name)}\"><span class=\"for-screen-reader\">Name this room</span></label>" : $"<h1 class=\"flex-item-grow txt-x-large\">{E(room.Name)}</h1>";
        var submit = canAdminister ? $"<button type=\"submit\" class=\"btn btn--reversed txt-large center\">{Icon("check.svg")}<span class=\"for-screen-reader\">Save</span></button>" : "";
        var form = $$"""
            <section class="panel txt-align-center" style="view-transition-name: {{(room.Id == 0 ? "new-room" : "edit-room-" + room.Id)}}"><form method="post" action="/rooms/{{(open ? "opens" : "closeds")}}{{(room.Id == 0 ? "" : "/" + room.Id)}}" accept-charset="UTF-8" data-controller="form">{{(room.Id == 0 ? "" : "<input type=\"hidden\" name=\"_method\" value=\"patch\">")}}<div class="flex align-center gap">{{heading}}</div><hr class="margin-block borderless"><section class="room-access margin-block pad-inline fill-shade border-radius"><menu class="flex flex-column gap margin-none pad overflow-y constrain-height" data-controller="filter" data-filter-active-class="filter--active" data-filter-selected-class="selected">{{everyone}}{{filter}}<div data-filter-target="list" contents>{{userList}}</div></menu></section>{{submit}}</form></section>
            """;
        if (room.Id != 0 && canAdminister) form += $"<section class=\"panel txt-align-center\"><form class=\"button_to\" method=\"post\" action=\"/rooms/{room.Id}\"><input type=\"hidden\" name=\"_method\" value=\"delete\"><button class=\"btn btn--negative max-width\" aria-label=\"Delete {E(room.Name)}\" data-turbo-confirm=\"Are you sure you want to delete this room and all messages in it? This can’t be undone.\" type=\"submit\">{Icon("trash.svg")}<span class=\"overflow-ellipsis\">{E(room.Name)}</span></button></form></section>";
        if (canAdminister) form = form.Replace("<div class=\"flex align-center gap\">" + heading, "<div class=\"flex align-center gap\">" + Translation("room_name") + heading, StringComparison.Ordinal);
        return Layout(user, Forms(form), room.Id == 0 ? "New chat room" : "Edit settings for " + room.Name, nav);
    }
    public string SearchHtml(UserRecord user, string query, List<ChatMessage> messages, long? returnRoom)
        => SearchShell(user, query, messages.Count, returnRoom, MessagesHtml(messages, user.Id));
    private string SearchShell(UserRecord user, string query, int messageCount, long? returnRoom, string messagesHtml)
    {
        // The pinned search footer calls room_path(nil) when the user has no
        // reachable return room, raising rather than linking to the welcome page.
        if (returnRoom is null) throw new ChatHttpException(500, "No room is available for the search return link");
        var recents = store.RecentSearches(user.Id);
        var recentHtml = string.Concat(recents.Select(x => $"<a class=\"align-center gap room btn txt-nowrap\" href=\"/searches?q={E(Uri.EscapeDataString(x))}\"><span class=\"overflow-ellipsis\">“{E(x)}”</span></a>"));
        if (recents.Count > 0) recentHtml += Forms($"<form class=\"button_to\" method=\"post\" action=\"/searches/clear\"><input type=\"hidden\" name=\"_method\" value=\"delete\"><button class=\"btn searches__btn\" data-turbo-confirm=\"Are you sure you want to clear your recent searches?\" type=\"submit\">{Icon("broom.svg")}<span class=\"for-screen-reader\">Clear recent searches</span></button></form>");
        var body = $$"""
            <div id="message-area" class="message-area"><div class="message-area--empty min-width center"><figure class="center pad">{{Icon("search.svg")}}</figure></div><div id="search-results" class="messages searches__results" data-controller="search-results" data-search-results-target="messages" data-search-results-me-class="message--me" data-search-results-threaded-class="message--threaded" data-search-results-mentioned-class="message--mentioned" data-search-results-formatted-class="message--formatted">{{messagesHtml}}</div></div>
            """;
        var nav = (string.IsNullOrWhiteSpace(query) ? "" : $"<div class=\"searches__query flex align-center gap pad-block-start-half\"><div class=\"btn btn--reversed btn--faux align-center gap txt-nowrap\"><span class=\"overflow-ellipsis\">“{E(query)}”</span><span class=\"flex-item-no-shrink\">{messageCount}</span></div></div>") + "<div class=\"searches__recents align-center gap pad-block-half overflow-y overflow-hide-scrollbar\">" + recentHtml + "</div>";
        var footer = Forms($$"""
            <div class="composer flex align-end gap"><a class="btn flex-item-no-shrink margin-block-end" style="view-transition-name: input-switcher; --btn-border-radius: 0.5em" href="{{(returnRoom.HasValue ? "/rooms/" + returnRoom : "/")}}">{{Icon("arrow-left.svg")}}<span class="for-screen-reader">Exit search</span></a><form class="margin-block flex-item-grow contain flex align-center gap" data-controller="form" data-action="keydown.esc-&gt;form#cancel" action="/searches" accept-charset="UTF-8" method="post"><div class="composer__input flex align-center flex-item-grow gap full-width input input--actor min-width">{{Icon("search.svg")}}<input value="{{E(query)}}" class="searches__input input flex-item-grow" role="searchbox" aria-label="search" autofocus required type="text" name="q" id="q"><a data-form-target="cancel" role="button" class="searches__reset" href="/searches">{{Icon("remove.svg", 14)}}<span class="for-screen-reader">Clear search field</span></a><button type="submit" class="btn btn--reversed flex-item-no-shrink txt-small" style="--btn-border-radius: 0.5em">{{Icon("arrow-up.svg")}}<span class="for-screen-reader">Search</span></button></div></form></div>
            """);
        return Layout(user, body, "Search", nav, footer, "<div class=\"rooms position-relative flex flex-column gap overflow-y overflow-hide-scrollbar\">" + recentHtml + "</div>", "sidebar searches");
    }
}
