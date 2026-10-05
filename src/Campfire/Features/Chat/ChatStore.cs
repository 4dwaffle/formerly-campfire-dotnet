using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.Contracts;
using Campfire.Features.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Campfire.Features.Chat;

public sealed class ChatStore(IDataStore db, RichText richText)
{
    public const int PageSize = 40;
    // This is an application constant, never request input. A bound LIMIT makes
    // SQLite recompile this prepared query on every execution, even at the same
    // value. Keep the ordinary planner and bind all request-dependent values.
    private static readonly string PageLimit = " limit " + PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private const string RoomProjection = "select r.*, m.involvement, m.unread_at from rooms r join memberships m on m.room_id=r.id";
    private const string MessageJoins = """
        from messages m left join users u on u.id=m.creator_id join rooms r on r.id=m.room_id
        left join action_text_rich_texts rt on rt.record_type='Message' and rt.record_id=m.id and rt.name='body'
        left join active_storage_attachments a on a.record_type='Message' and a.record_id=m.id and a.name='attachment'
        left join active_storage_blobs b on b.id=a.blob_id
        """;
    private const string MessageProjection = """
        select m.*, coalesce(rt.body,'') body, coalesce(u.name,'') creator_name, u.bio creator_bio,
          coalesce(u.updated_at,m.updated_at) creator_updated_at, coalesce(u.role,0) creator_role, u.id is null creator_missing, r.name room_name, r.type room_type,
          b.filename attachment_filename, b.id attachment_blob_id, b.metadata attachment_metadata
        """ + " " + MessageJoins;
    private const string MessageMetadataProjection = "select m.*,0 presentation_hydrated from messages m";
    // HTML pages need only cache versions and pagination anchors. Cold cache
    // entries are hydrated through the authorized presentation query below.
    private const string PageMetadataProjection = "select m.id,m.created_at,m.updated_at,0 presentation_hydrated from messages m";
    private const string RoomKeyProjection = "select m.id,m.updated_at from messages m";
    private long presentationHydrations;
    public long PresentationHydrations => Interlocked.Read(ref presentationHydrations);

    public AccountPresentation Account() => db.Read(queryConnection1 => queryConnection1.QuerySingleOrDefault<AccountPresentation>("select * from accounts order by id limit 1")) ?? new();
    public AccountPresentation LayoutAccount() => db.Read(connection => connection.QuerySingleOrDefault<AccountPresentation>("select a.*,exists(select 1 from active_storage_attachments where record_type='Account' and record_id=a.id and name='logo') has_logo from accounts a order by a.id limit 1")) ?? new();
    public AccountPresentation RoomLayoutAccount(long roomId) => db.Read(connection => connection.QuerySingleOrDefault<AccountPresentation>("select a.*,exists(select 1 from active_storage_attachments where record_type='Account' and record_id=a.id and name='logo') has_logo, @roomId=(select id from rooms order by created_at,id limit 1) and not exists(select 1 from messages where room_id=@roomId limit 1 offset @size) show_invitation from accounts a order by a.id limit 1", new { roomId, size = PageSize })) ?? new() { ShowInvitation = ShowInvitation(roomId) };
    public bool AccountHasLogo(long id) => db.Read(connection => connection.ExecuteScalar<long>("select count(*) from active_storage_attachments where record_type='Account' and record_id=@id and name='logo'", new { id })) > 0;
    public bool CanCreate(UserRecord user)
    {
        return user.IsAdmin || CanCreate(user, Account());
    }
    public static bool CanCreate(UserRecord user, AccountPresentation account)
    {
        var settings = account.Settings;
        if (user.IsAdmin || string.IsNullOrEmpty(settings)) return true;
        using var json = JsonDocument.Parse(settings);
        return !json.RootElement.TryGetProperty("restrict_room_creation_to_administrators", out var flag) || flag.ValueKind != JsonValueKind.True;
    }
    public List<RoomRecord> Rooms(long userId, bool visible = false) => db.Read(queryConnection2 => queryConnection2.Query<RoomRecord>(RoomProjection + " where m.user_id=@userId" + (visible ? " and m.involvement!='invisible'" : "") + " order by lower(r.name), r.id",new { userId }).Materialize());
    public RoomRecord Room(long roomId, long userId) => db.Read(queryConnection3 => queryConnection3.QuerySingleOrDefault<RoomRecord>(RoomProjection + " where r.id=@roomId and m.user_id=@userId",new { roomId, userId })) ?? throw new ChatHttpException(404, "Room not found or inaccessible");
    public List<UserRecord> Members(long roomId) => db.Read(queryConnection4 => queryConnection4.Query<UserRecord>("select u.* from users u join memberships m on m.user_id=u.id where m.room_id=@roomId",new { roomId }).Materialize());
    public Dictionary<long, List<UserRecord>> SidebarMembers(IEnumerable<long> roomIds)
    {
        var ids = roomIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = db.Read(connection => connection.Query<SidebarMember>("select m.room_id,u.id,u.name,u.bio,u.updated_at from users u join memberships m on m.user_id=u.id where m.room_id in (select value from json_each(@ids))", new { ids = SqlParameters.Ids(ids) }).Materialize());
        return rows.GroupBy(row => row.RoomId).ToDictionary(group => group.Key, group => group.Select(row => new UserRecord { Id = row.Id, Name = row.Name, Bio = row.Bio, UpdatedAt = row.UpdatedAt }).ToList());
    }
    public List<UserRecord> Users() => db.Read(queryConnection5 => queryConnection5.Query<UserRecord>("select * from users where status=0 order by lower(name),id").Materialize());
    public bool ShowInvitation(long roomId) => db.Read(connection => connection.ExecuteScalar<bool>("select @roomId=(select id from rooms order by created_at,id limit 1) and not exists(select 1 from messages where room_id=@roomId limit 1 offset @size)", new { roomId, size = PageSize }));
    public List<long> RoomBlobs(long roomId, long userId)
    {
        _ = Room(roomId, userId);
        return Blobs(db.Read(queryConnection8 => queryConnection8.Query<long>("select id from messages where room_id=@roomId",new { roomId }).Materialize()));
    }
    public List<long> MessageBlobs(long messageId, long userId)
    {
        _ = Message(messageId, userId);
        return Blobs([messageId]);
    }
    private List<long> Blobs(List<long> ids) => ids.Count == 0 ? [] : db.Read(queryConnection9 => queryConnection9.Query<long>("select distinct blob_id from active_storage_attachments where (record_type='Message' and record_id in (select value from json_each(@ids))) or (record_type='ActionText::RichText' and record_id in(select id from action_text_rich_texts where record_type='Message' and record_id in (select value from json_each(@ids))))",new { ids = SqlParameters.Ids(ids) }).Materialize());
    public ChatMessage Message(long messageId, long userId, long? roomId = null)
    {
        var message = db.Read(queryConnection10 => queryConnection10.QuerySingleOrDefault<ChatMessage>(MessageProjection + " where m.id=@messageId and (@roomId is null or m.room_id=@roomId) and exists(select 1 from memberships mem where mem.room_id=m.room_id and mem.user_id=@userId)",new { messageId, roomId, userId })) ?? throw new ChatHttpException(404, "Message not found");
        LoadBoosts([message]);
        return message;
    }
    public ChatMessage MessageMetadata(long messageId, long userId, long? roomId = null) => db.Read(connection => connection.QuerySingleOrDefault<ChatMessage>(MessageMetadataProjection + " where m.id=@messageId and (@roomId is null or m.room_id=@roomId) and exists(select 1 from memberships mem join rooms r on r.id=mem.room_id where mem.room_id=m.room_id and mem.user_id=@userId)", new { messageId, roomId, userId })) ?? throw new ChatHttpException(404, "Message not found");
    public List<ChatMessage> Page(long roomId, long userId, long? before = null, long? after = null, long? around = null, bool htmlOnly = false)
        => RoomPage(roomId, userId, before, after, around, htmlOnly).Messages;

    public (RoomRecord Room, List<ChatMessage> Messages) RoomPage(long roomId, long userId, long? before = null, long? after = null, long? around = null, bool htmlOnly = false)
        => ReadRoomPage(roomId, userId, before, after, around, htmlOnly, roomKeysOnly: false);

    public (RoomRecord Room, List<ChatMessage> Messages) RoomPageForRendering(long roomId, long userId, long? around = null)
        => ReadRoomPage(roomId, userId, null, null, around, htmlOnly: true, roomKeysOnly: true);

    private (RoomRecord Room, List<ChatMessage> Messages) ReadRoomPage(long roomId, long userId, long? before, long? after, long? around, bool htmlOnly, bool roomKeysOnly)
        => db.Read(connection =>
        {
            var room = connection.QuerySingleOrDefault<RoomRecord>(RoomProjection + " where r.id=@roomId and m.user_id=@userId", new { roomId, userId }) ?? throw new ChatHttpException(404, "Room not found or inaccessible");
            var messages = PageRows(connection, roomId, userId, before, after, around, htmlOnly, roomKeysOnly);
            if (!htmlOnly) LoadBoosts(connection, messages);
            return (room, messages);
        });

    private static List<ChatMessage> PageRows(SqliteConnection connection, long roomId, long userId, long? before, long? after, long? around, bool htmlOnly, bool roomKeysOnly)
    {
        var projection = roomKeysOnly ? RoomKeyProjection : htmlOnly ? PageMetadataProjection : MessageProjection;
        if (before.HasValue) after = null;
        if (around is not null)
        {
            var message = connection.QuerySingleOrDefault<ChatMessage>(projection + " where m.id=@around and m.room_id=@roomId", new { around, roomId });
            if (message != null)
            {
                if (roomKeysOnly) message.PresentationHydrated = false;
                var result = PageRows(connection, roomId, userId, around, null, null, htmlOnly, roomKeysOnly);
                result.Add(message);
                result.AddRange(PageRows(connection, roomId, userId, null, around, null, htmlOnly, roomKeysOnly));
                return result;
            }
        }
        var anchor = before ?? after;
        var timestamp = anchor is null ? null : connection.QuerySingleOrDefault<string>("select m.created_at from messages m where m.id=@anchor and m.room_id=@roomId", new { anchor, roomId }) ?? throw new ChatHttpException(404, "Message not found");
        // Authorization was checked on this connection before selecting any rows.
        // Rails pages by timestamp, deliberately excluding equal-time peers.
        var comparison = before is not null ? " and m.created_at<@timestamp" : after is not null ? " and m.created_at>@timestamp" : "";
        var ordering = after is not null ? " order by m.created_at,m.id" : " order by m.created_at desc,m.id desc";
        var rows = connection.Query<ChatMessage>(projection + " where m.room_id=@roomId" + comparison + ordering + PageLimit, new { roomId, timestamp }).Materialize();
        if (roomKeysOnly) foreach (var row in rows) row.PresentationHydrated = false;
        if (after is null) rows.Reverse();
        return rows;
    }
    public List<ChatMessage> HydrateMessages(IEnumerable<long> messageIds, long userId)
    {
        var ids = SqlParameters.Ids(messageIds.Distinct());
        var messages = db.Read(connection =>
        {
            // Keep the model version and loaded boost graph in one SQLite read
            // snapshot, so a concurrent touch cannot poison an older version.
            using var transaction = connection.BeginTransaction(deferred: true);
            var rows = connection.Query<ChatMessage>(MessageProjection + " where m.id in (select value from json_each(@ids)) and exists(select 1 from memberships mem where mem.room_id=m.room_id and mem.user_id=@userId)", new { ids, userId }, transaction).Materialize();
            LoadBoosts(connection, rows, transaction);
            transaction.Commit();
            return rows;
        });
        Interlocked.Add(ref presentationHydrations, messages.Count);
        return messages;
    }
    public (List<ChatMessage> Created, List<ChatMessage> Updated) Refresh(long roomId, long userId, long since, bool htmlOnly = false)
    {
        _ = Room(roomId, userId);
        var projection = htmlOnly ? MessageMetadataProjection : MessageProjection;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(since, 0, 253402300799999)).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);
        var created = db.Read(queryConnection13 => queryConnection13.Query<ChatMessage>(projection + " where m.room_id=@roomId and m.created_at>@timestamp order by m.created_at,m.id limit @size",new { roomId, timestamp, size = PageSize }).Materialize());
        var updated = db.Read(queryConnection14 => queryConnection14.Query<ChatMessage>(projection + " where m.room_id=@roomId and m.updated_at>@timestamp and m.id not in (select value from json_each(@ids)) order by m.created_at desc,m.id desc limit @size",new { roomId, timestamp, ids = SqlParameters.Ids(created.Select(x => x.Id)), size = PageSize }).Materialize());
        updated.Reverse();
        if (!htmlOnly) { LoadBoosts(created); LoadBoosts(updated); }
        return (created, updated);
    }
    public static string CleanQuery(string query) => Regex.Replace(query, @"[^\p{L}\p{M}\p{N}_]", " ");
    public List<ChatMessage> Search(long userId, string query, bool htmlOnly = false)
    {
        var match = CleanQuery(query);
        if (string.IsNullOrWhiteSpace(match)) return [];
        var projection = htmlOnly ? MessageMetadataProjection : MessageProjection;
        var result = db.Read(queryConnection15 => queryConnection15.Query<ChatMessage>(projection + " join message_search_index idx on m.id=idx.rowid where idx.body match @match and exists(select 1 from memberships mem where mem.room_id=m.room_id and mem.user_id=@userId) order by m.created_at desc,m.id desc limit 100",new { userId, match }).Materialize());
        result.Reverse(); if (!htmlOnly) LoadBoosts(result);
        return result;
    }
    public List<string> RecentSearches(long userId) => db.Read(queryConnection16 => queryConnection16.Query<string>("select query from searches where user_id=@userId order by updated_at desc,id desc",new { userId }).Materialize());
    public void RecordSearch(long userId, string query) => db.Write((connection, tx) =>
    {
        var now = RequestUser.Timestamp();
        var id = connection.QuerySingleOrDefault<long?>("select id from searches where user_id=@userId and query=@query limit 1", new { userId, query }, tx);
        if (id is null) connection.Execute("insert into searches(user_id,query,created_at,updated_at) values(@userId,@query,@now,@now)", new { userId, query, now }, tx);
        else connection.Execute("update searches set updated_at=@now where id=@id", new { now, id }, tx);
        return connection.Execute("delete from searches where user_id=@userId and id not in(select id from searches where user_id=@userId order by updated_at desc,id desc limit 10)", new { userId }, tx);
    });
    public void ClearSearches(long userId) => db.Write((queryConnection17,queryTransaction17) => queryConnection17.Execute("delete from searches where user_id=@userId",new { userId },transaction: queryTransaction17));

    public long CreateRoom(UserRecord user, string type, string? name, IEnumerable<long> requestedUsers)
    {
        var id = db.Write((connection, tx) =>
        {
        if (type != "Rooms::Direct" && !CanCreate(user)) throw new ChatHttpException(403, "Room creation is restricted to administrators");
        var users = requestedUsers.Distinct().ToArray();
        var selected = connection.Query<long>("select id from users where id in (select value from json_each(@users))", new { users = SqlParameters.Ids(users) }, tx).Materialize();
        if (type == "Rooms::Open") selected = [user.Id];
        if (type == "Rooms::Direct")
        {
            if (!selected.Contains(user.Id)) selected.Add(user.Id);
            var candidates = connection.Query<long>("select r.id from rooms r join memberships m on m.room_id=r.id where r.type='Rooms::Direct' group by r.id having count(*)=@count and sum(case when m.user_id in (select value from json_each(@selected)) then 1 else 0 end)=@count", new { count = selected.Count, selected = SqlParameters.Ids(selected) }, tx).Materialize();
            if (candidates.Count != 0) return candidates[0];
        }
        var now = RequestUser.Timestamp();
        var id = connection.ExecuteScalar<long>("insert into rooms(name,type,creator_id,created_at,updated_at) values(@name,@type,@creator,@now,@now); select last_insert_rowid()", new { name, type, creator = user.Id, now }, tx);
        foreach (var userId in selected) Grant(connection, tx, id, userId, type == "Rooms::Direct" ? "everything" : "mentions", now);
        return id;
        });
        // Rooms::Open grants active users in after_save_commit. The creator's
        // initial membership and room survive failure of this bulk callback.
        if (type == "Rooms::Open") db.Read(connection => connection.Execute("insert into memberships(room_id,user_id,involvement,created_at,updated_at) select @id,id,'mentions',@now,@now from users where status=0 on conflict(room_id,user_id) do nothing", new { id, now = RequestUser.Timestamp() }));
        return id;
    }
    public List<long> UpdateRoom(UserRecord user, long roomId, string type, string? name, IEnumerable<long> requestedUsers)
    {
        // Rails commits update! before memberships.revise's separate transaction.
        var previousType = db.Write((connection, tx) =>
        {
            var room = AuthorizeRoom(connection, tx, user, roomId);
            if (room.Direct) throw new ChatHttpException(404, "Direct rooms cannot be converted");
            RequireAdministrator(user, room.CreatorId);
            connection.Execute("update rooms set name=@name,type=@type,updated_at=@now where id=@roomId", new { name, type, now = RequestUser.Timestamp(), roomId }, tx);
            return room.Type;
        });
        return db.Write((connection, tx) =>
        {
        var now = RequestUser.Timestamp();
        var revoked = new List<long>();
        if (type == "Rooms::Closed")
        {
            var selected = connection.Query<long>("select id from users where id in (select value from json_each(@ids))", new { ids = SqlParameters.Ids(requestedUsers.Distinct()) }, tx).ToArray();
            revoked = connection.Query<long>("select user_id from memberships where room_id=@roomId and user_id not in (select value from json_each(@selected))", new { roomId, selected = SqlParameters.Ids(selected) }, tx).Materialize();
            connection.Execute("delete from memberships where room_id=@roomId and user_id not in (select value from json_each(@selected))", new { roomId, selected = SqlParameters.Ids(selected) }, tx);
            foreach (var id in selected) Grant(connection, tx, roomId, id, "mentions", now);
        }
        else if (previousType != type)
        {
            foreach (var id in connection.Query<long>("select id from users where status=0", transaction: tx)) Grant(connection, tx, roomId, id, "mentions", now);
        }
        return revoked;
        });
    }
    public List<long> DeleteRoom(UserRecord user, long roomId, bool directNamespace = false)
    {
        var deleted = db.Write((connection, tx) =>
        {
        var room = AuthorizeRoom(connection, tx, user, roomId);
        if (directNamespace && !room.Direct) throw new ChatHttpException(404, "Room not found");
        if (!directNamespace) RequireAdministrator(user, room.CreatorId);
        var members = connection.Query<long>("select user_id from memberships where room_id=@roomId", new { roomId }, tx).Materialize();
        var messages = connection.Query<long>("select id from messages where room_id=@roomId", new { roomId }, tx).ToArray();
        foreach (var id in messages) DeleteMessageRecords(connection, tx, id);
        connection.Execute("delete from memberships where room_id=@roomId; delete from rooms where id=@roomId", new { roomId }, tx);
        return (members, messages);
        });
        foreach (var id in deleted.messages) RemoveFromIndex(id);
        return deleted.members;
    }
    public void Involvement(UserRecord user, long roomId, string involvement)
    {
        if (!new[] { "invisible", "nothing", "mentions", "everything" }.Contains(involvement)) throw new ChatHttpException(500, "Invalid involvement");
        db.Write((connection, tx) =>
        {
            _ = AuthorizeRoom(connection, tx, user, roomId);
            return connection.Execute("update memberships set involvement=@involvement,updated_at=@now where room_id=@roomId and user_id=@userId", new { involvement, roomId, userId = user.Id, now = RequestUser.Timestamp() }, tx);
        });
    }
    public long CreateMessage(UserRecord user, long roomId, string? body, string? clientId, long? blobId = null, bool bodySpecified = false)
    {
        var saved = db.Write((connection, tx) => InsertMessage(connection, tx, user, roomId, body, clientId, blobId, bodySpecified),
            (connection, committed) => AfterMessageCreated(connection, committed, roomId, user.Id));
        return saved.id;
    }
    public async Task<long> CreateMessageAsync(UserRecord user, long roomId, string? body, string? clientId, long? blobId = null, bool bodySpecified = false, CancellationToken cancellationToken = default)
    {
        var saved = await db.WriteAsync((connection, tx) => Task.FromResult(InsertMessage(connection, tx, user, roomId, body, clientId, blobId, bodySpecified)),
            (connection, committed) => AfterMessageCreated(connection, committed, roomId, user.Id), cancellationToken);
        return saved.id;
    }
    private static (long id, string now) InsertMessage(SqliteConnection connection, SqliteTransaction tx, UserRecord user, long roomId, string? body, string? clientId, long? blobId, bool bodySpecified)
    {
        _ = AuthorizeRoom(connection, tx, user, roomId);
        var now = RequestUser.Timestamp();
        var safeBody = body;
        var id = connection.ExecuteScalar<long>("insert into messages(room_id,creator_id,client_message_id,created_at,updated_at) values(@roomId,@userId,@clientId,@now,@now);select last_insert_rowid()", new { roomId, userId = user.Id, clientId = clientId ?? Guid.NewGuid().ToString(), now }, tx);
        if (safeBody is not null || bodySpecified) SaveBody(connection, tx, id, safeBody, now);
        if (blobId.HasValue) AttachBlob(connection, tx, id, blobId.Value, now);
        connection.Execute("update rooms set updated_at=@now where id=@roomId", new { now, roomId }, tx);
        return (id, now);
    }
    private void AfterMessageCreated(SqliteConnection connection, (long id, string now) committed, long roomId, long userId)
    {
        // Retain the writer lease/connection through separate after-commit
        // autocommits. Failure still preserves the message transaction and
        // stops unread handling, exactly as the Rails callback chain does.
        var plain = IndexedBody(connection, committed.id);
        connection.Execute("insert into message_search_index(rowid,body) values(@id,@plain)", new { id = committed.id, plain });
        connection.Execute("update memberships set unread_at=@now,updated_at=@updated where room_id=@roomId and user_id!=@userId and involvement!='invisible' and (connected_at is null or connected_at<@cutoff)", new { now = committed.now, updated = RequestUser.Timestamp(), roomId, userId, cutoff = DateTime.UtcNow.AddSeconds(-60).ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture) });
    }
    public void UpdateMessage(UserRecord user, long roomId, long messageId, string? body, string? clientId = null, long? blobId = null, bool clearAttachment = false, bool bodySpecified = false)
    {
        db.Write((connection, tx) =>
        {
        _ = AuthorizeRoom(connection, tx, user, roomId);
        var previous = connection.QuerySingleOrDefault<MessageUpdateState>("""
            select m.creator_id,m.client_message_id,rt.id body_id,rt.body,a.blob_id
            from messages m
            left join action_text_rich_texts rt on rt.record_type='Message' and rt.record_id=m.id and rt.name='body'
            left join active_storage_attachments a on a.record_type='Message' and a.record_id=m.id and a.name='attachment'
            where m.id=@messageId and m.room_id=@roomId
            """, new { messageId, roomId }, tx) ?? throw new ChatHttpException(404, "Message not found");
        RequireAdministrator(user, previous.CreatorId);
        var bodyChanged = (body is not null || bodySpecified) &&
            (!previous.BodyId.HasValue || !string.Equals(previous.Body, StoredBody(body), StringComparison.Ordinal));
        var attachmentChanged = blobId.HasValue ? blobId != previous.BlobId : clearAttachment && previous.BlobId.HasValue;
        var clientChanged = clientId is not null && !string.Equals(clientId, previous.ClientMessageId, StringComparison.Ordinal);
        // Action Text autosaves only dirty rich text; an unchanged save does
        // not touch its record. Message's commit callback still indexes it.
        if (!bodyChanged && !attachmentChanged && !clientChanged) return false;
        var now = RequestUser.Timestamp();
        if (bodyChanged) SaveBody(connection, tx, messageId, body, now);
        if (attachmentChanged && clearAttachment) connection.Execute("delete from active_storage_attachments where record_type='Message' and record_id=@messageId and name='attachment'", new { messageId }, tx);
        if (attachmentChanged && blobId.HasValue) AttachBlob(connection, tx, messageId, blobId.Value, now);
        connection.Execute("update messages set updated_at=@now,client_message_id=coalesce(@clientId,client_message_id) where id=@messageId; update rooms set updated_at=@now where id=@roomId", new { now, messageId, roomId, clientId }, tx);
        return true;
        });
        UpdateIndex(messageId);
    }
    public void DeleteMessage(UserRecord user, long roomId, long messageId)
    {
        db.Write((connection, tx) =>
        {
        _ = AuthorizeRoom(connection, tx, user, roomId);
        var creator = connection.QuerySingleOrDefault<long?>("select creator_id from messages where id=@messageId and room_id=@roomId", new { messageId, roomId }, tx) ?? throw new ChatHttpException(404, "Message not found");
        RequireAdministrator(user, creator);
        DeleteMessageRecords(connection, tx, messageId);
        return connection.Execute("update rooms set updated_at=@now where id=@roomId", new { now = RequestUser.Timestamp(), roomId }, tx);
        });
        RemoveFromIndex(messageId);
    }
    private string IndexedBody(long messageId) => db.Read(connection => IndexedBody(connection, messageId));
    private string IndexedBody(SqliteConnection connection, long messageId)
    {
        var body = connection.QuerySingleOrDefault<string>("select body from action_text_rich_texts where record_type='Message' and record_id=@messageId and name='body'", new { messageId }) ?? "";
        var plain = richText.PlainText(body);
        return string.IsNullOrWhiteSpace(plain) ? connection.QuerySingleOrDefault<string>("select b.filename from active_storage_blobs b join active_storage_attachments a on a.blob_id=b.id where a.record_type='Message' and a.record_id=@messageId and a.name='attachment'", new { messageId }) ?? "" : plain;
    }
    private void RemoveFromIndex(long id) => db.Read(connection => connection.Execute("delete from message_search_index where rowid=@id", new { id }));
    private void UpdateIndex(long messageId)
    {
        var plain = IndexedBody(messageId);
        db.Read(connection => connection.Execute("update message_search_index set body=@plain where rowid=@messageId", new { messageId, plain }));
    }
    public long CreateBoost(UserRecord user, long messageId, string content)
    {
        var id = db.Write((connection, tx) =>
        {
        var roomId = AccessibleMessageRoom(connection, tx, user.Id, messageId);
        var now = RequestUser.Timestamp();
        var id = connection.ExecuteScalar<long>("insert into boosts(message_id,booster_id,content,created_at,updated_at) values(@messageId,@userId,@content,@now,@now);select last_insert_rowid()", new { messageId, userId = user.Id, content, now }, tx);
        TouchMessage(connection, tx, messageId, roomId, now);
        return id;
        });
        UpdateIndex(messageId);
        return id;
    }
    public void DeleteBoost(UserRecord user, long messageId, long boostId)
    {
        db.Write((connection, tx) =>
        {
        var roomId = AccessibleMessageRoom(connection, tx, user.Id, messageId);
        if (connection.Execute("delete from boosts where id=@boostId and message_id=@messageId and booster_id=@userId", new { boostId, messageId, userId = user.Id }, tx) == 0) throw new ChatHttpException(404, "Boost not found");
        TouchMessage(connection, tx, messageId, roomId, RequestUser.Timestamp());
        return 0;
        });
        UpdateIndex(messageId);
    }
    public List<UserRecord> Autocomplete(long userId, long? roomId, string? query, int page)
    {
        if (roomId.HasValue) _ = Room(roomId.Value, userId);
        return db.Read(queryConnection18 => queryConnection18.Query<UserRecord>("select u.* from users u where u.status=0 and (@roomId is null or exists(select 1 from memberships m where m.user_id=u.id and m.room_id=@roomId)) and (@query is null or u.name like @pattern) order by lower(u.name),u.id limit 20 offset @offset",new { roomId, query, pattern = "%" + query + "%", offset = (Math.Clamp(page, 1, 100000) - 1) * 20 }).Materialize());
    }
    public List<UserRecord> DirectPlaceholders(long userId)
    {
        var ids = db.Read(queryConnection19 => queryConnection19.Query<long>("select distinct user_id from memberships where room_id in(select r.id from rooms r join memberships m on m.room_id=r.id where r.type='Rooms::Direct' and m.user_id=@userId)",new { userId }).Materialize()).Append(userId).Distinct().ToArray();
        return db.Read(queryConnection20 => queryConnection20.Query<UserRecord>("select * from users where status=0 and id not in (select value from json_each(@ids)) order by created_at,id limit @limit",new { ids = SqlParameters.Ids(ids), limit = Math.Max(20 - ids.Length, 0) }).Materialize());
    }
    private void LoadBoosts(List<ChatMessage> messages)
    {
        if (messages.Count == 0) return;
        db.Read(connection => { LoadBoosts(connection, messages); return 0; });
    }
    private static void LoadBoosts(SqliteConnection connection, List<ChatMessage> messages, SqliteTransaction? transaction = null)
    {
        if (messages.Count == 0) return;
        var boosts = connection.Query<ChatBoost>("select b.*,u.name booster_name,u.bio booster_bio,u.updated_at booster_updated_at from boosts b join users u on u.id=b.booster_id where message_id in (select value from json_each(@ids)) order by b.created_at,b.id",new { ids = SqlParameters.Ids(messages.Select(x => x.Id)) }, transaction).Materialize().ToLookup(x => x.MessageId);
        foreach (var message in messages) message.Boosts = boosts[message.Id].ToList();
    }
    private static RoomRecord AuthorizeRoom(SqliteConnection connection, SqliteTransaction tx, UserRecord user, long roomId) => connection.QuerySingleOrDefault<RoomRecord>(RoomProjection + " join users u on u.id=m.user_id where m.user_id=@userId and r.id=@roomId and u.status=0", new { userId = user.Id, roomId }, tx) ?? throw new ChatHttpException(404, "Room not found or inaccessible");
    private static long AccessibleMessageRoom(SqliteConnection connection, SqliteTransaction tx, long userId, long messageId) => connection.QuerySingleOrDefault<long?>("select m.room_id from messages m join memberships mem on mem.room_id=m.room_id join users u on u.id=mem.user_id where m.id=@messageId and mem.user_id=@userId and u.status=0", new { userId, messageId }, tx) ?? throw new ChatHttpException(404, "Message not found");
    private static void RequireAdministrator(UserRecord user, long creatorId)
    {
        if (!user.IsAdmin && user.Id != creatorId) throw new ChatHttpException(403, "Forbidden");
    }
    private static void Grant(SqliteConnection connection, SqliteTransaction tx, long roomId, long userId, string involvement, string now) => connection.Execute("insert into memberships(room_id,user_id,involvement,created_at,updated_at) values(@roomId,@userId,@involvement,@now,@now) on conflict(room_id,user_id) do nothing", new { roomId, userId, involvement, now }, tx);
    private static string? StoredBody(string? body) => body?.Trim(' ', '\t', '\r', '\n', '\f', '\v', '\0');
    private static void SaveBody(SqliteConnection connection, SqliteTransaction tx, long id, string? body, string now) => connection.Execute("insert into action_text_rich_texts(record_type,record_id,name,body,created_at,updated_at) values('Message',@id,'body',@body,@now,@now) on conflict(record_type,record_id,name) do update set body=@body,updated_at=@now", new { id, body = StoredBody(body), now }, tx);
    private static void AttachBlob(SqliteConnection connection, SqliteTransaction tx, long id, long blobId, string now)
    {
        if (connection.ExecuteScalar<long>("select count(*) from active_storage_blobs where id=@blobId", new { blobId }, tx) == 0) throw new ChatHttpException(422, "Attachment not found");
        connection.Execute("delete from active_storage_attachments where record_type='Message' and record_id=@id and name='attachment'; insert into active_storage_attachments(record_type,record_id,name,blob_id,created_at) values('Message',@id,'attachment',@blobId,@now)", new { id, blobId, now }, tx);
        connection.Execute("delete from active_storage_attachments where record_type='ChatUpload' and blob_id=@blobId", new { blobId }, tx);
    }
    private static void DeleteMessageRecords(SqliteConnection connection, SqliteTransaction tx, long id) => connection.Execute("delete from boosts where message_id=@id; delete from active_storage_attachments where (record_type='Message' and record_id=@id) or (record_type='ActionText::RichText' and record_id in(select id from action_text_rich_texts where record_type='Message' and record_id=@id)); delete from action_text_rich_texts where record_type='Message' and record_id=@id; delete from messages where id=@id", new { id }, tx);
    private static void TouchMessage(SqliteConnection connection, SqliteTransaction tx, long messageId, long roomId, string now) => connection.Execute("update messages set updated_at=@now where id=@messageId; update rooms set updated_at=@now where id=@roomId", new { messageId, roomId, now }, tx);
}
