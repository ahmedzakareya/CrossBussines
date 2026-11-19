// Communication v2 — the chat for the app (api/chat). The same conversations, messages and rules as the browser.

String _s(Object? v) => v == null ? '' : '$v';
String? _n(Object? v) => v == null || '$v'.isEmpty ? null : '$v';
int _i(Object? v) => v is num ? v.toInt() : int.tryParse('$v') ?? 0;
DateTime? _d(Object? v) {
  if (v == null) return null;
  var s = '$v';
  if (!s.endsWith('Z') && !RegExp(r'[+-]\d\d:\d\d$').hasMatch(s)) s += 'Z';   // the server's times are UTC
  return DateTime.tryParse(s)?.toLocal();
}

class ChatConv {
  final int id;
  final String kind, title;
  final String? avatar, preview;
  final bool online, pinned, muted, lastMine, lastSeen;
  final DateTime? lastAt;
  final int unread;
  final int? otherId;
  ChatConv({required this.id, required this.kind, required this.title, this.avatar, this.preview, this.online = false, this.pinned = false, this.muted = false, this.lastMine = false, this.lastSeen = false, this.lastAt, this.unread = 0, this.otherId});
  bool get isGroup => kind != 'Direct';
  factory ChatConv.fromJson(Map<String, dynamic> j) => ChatConv(
        id: _i(j['id']), kind: _s(j['kind']), title: _s(j['title']), avatar: _n(j['avatar']), preview: _n(j['preview']),
        online: j['online'] == true, pinned: j['pinned'] == true, muted: j['muted'] == true, lastMine: j['lastMine'] == true, lastSeen: j['lastSeen'] == true,
        lastAt: _d(j['lastAt']), unread: _i(j['unread']), otherId: j['otherEmployeeId'] == null ? null : _i(j['otherEmployeeId']),
      );
}

class ChatReaction {
  final String emoji;
  final int count;
  final bool mine;
  ChatReaction(this.emoji, this.count, this.mine);
}

class ChatMsg {
  final int id, senderId;
  final String senderName;
  final String? senderAvatar, body, attachmentPath, attachmentName, attachmentType, replyToSender, replyToBody;
  final bool mine, deleted;
  final DateTime createdAt;
  final DateTime? editedAt;
  final int? replyToId;
  final List<ChatReaction> reactions;
  ChatMeta? meta;
  ChatMsg({required this.id, required this.senderId, required this.senderName, this.senderAvatar, this.body, this.attachmentPath, this.attachmentName, this.attachmentType,
      this.replyToSender, this.replyToBody, this.mine = false, this.deleted = false, required this.createdAt, this.editedAt, this.replyToId, this.reactions = const []});
  factory ChatMsg.fromJson(Map<String, dynamic> j) => ChatMsg(
        id: _i(j['id']), senderId: _i(j['senderId']), senderName: _s(j['senderName']), senderAvatar: _n(j['senderAvatar']), body: _n(j['body']),
        attachmentPath: _n(j['attachmentPath']), attachmentName: _n(j['attachmentName']), attachmentType: _n(j['attachmentType']),
        replyToSender: _n(j['replyToSender']), replyToBody: _n(j['replyToBody']), mine: j['mine'] == true, deleted: j['deleted'] == true,
        createdAt: _d(j['createdAt']) ?? DateTime.now(), editedAt: _d(j['editedAt']), replyToId: j['replyToId'] == null ? null : _i(j['replyToId']),
        reactions: [for (final r in (j['reactions'] as List? ?? const [])) ChatReaction(_s((r as Map)['emoji']), _i(r['count']), r['mine'] == true)],
      );
  /// The text as it is read: a record token (#rec:Code:Id) is the card's, not the reader's.
  String get text => (body ?? '').replaceAll(RegExp(r'#rec:[A-Za-z.]+:\d+'), '').trim();
}

class ChatPollOption {
  final int id, votes;
  final String text;
  final bool mine;
  ChatPollOption(this.id, this.text, this.votes, this.mine);
}

class ChatPoll {
  final String question;
  final bool multi, closed;
  final int voters;
  final List<ChatPollOption> options;
  ChatPoll(this.question, this.multi, this.closed, this.voters, this.options);
  factory ChatPoll.fromJson(Map<String, dynamic> j) => ChatPoll(_s(j['question']), j['multi'] == true, j['closed'] == true, _i(j['voters']),
      [for (final o in (j['options'] as List? ?? const [])) ChatPollOption(_i((o as Map)['id']), _s(o['text']), _i(o['votes']), o['mine'] == true)]);
}

class ChatMeta {
  final int threadCount;
  final bool forwarded, pinned;
  final String? forwardedFrom;
  final ChatPoll? poll;
  ChatMeta({this.threadCount = 0, this.forwarded = false, this.pinned = false, this.forwardedFrom, this.poll});
  factory ChatMeta.fromJson(Map<String, dynamic> j) => ChatMeta(
        threadCount: j['thread'] is Map ? _i((j['thread'] as Map)['count']) : 0,
        forwarded: j['forwarded'] == true, pinned: j['pinned'] == true, forwardedFrom: _n(j['forwardedFrom']),
        poll: j['poll'] is Map ? ChatPoll.fromJson((j['poll'] as Map).cast<String, dynamic>()) : null,
      );
}

class ChatMember {
  final int id;
  final String name;
  final String? avatar, altName;
  final bool online;
  ChatMember(this.id, this.name, this.avatar, this.altName, this.online);
}

class ChatHeader {
  final int id, memberCount, otherReadMessageId;
  final String kind, title;
  final String? avatar;
  final bool online;
  final List<ChatMember> members;
  final Map<int, int> reads;   // member → the last message they read
  ChatHeader({required this.id, required this.kind, required this.title, this.avatar, this.online = false, this.memberCount = 0, this.otherReadMessageId = 0, this.members = const [], this.reads = const {}});
  bool get isGroup => kind != 'Direct';
  factory ChatHeader.fromJson(Map<String, dynamic> j) => ChatHeader(
        id: _i(j['id']), kind: _s(j['kind']), title: _s(j['title']), avatar: _n(j['avatar']), online: j['online'] == true,
        memberCount: _i(j['memberCount']), otherReadMessageId: _i(j['otherReadMessageId']),
        members: [for (final m in (j['members'] as List? ?? const [])) ChatMember(_i((m as Map)['id']), _s(m['name']), _n(m['avatar']), _n(m['altName']), m['online'] == true)],
        reads: {for (final r in (j['memberReads'] as List? ?? const [])) _i((r as Map)['employeeId']): _i(r['lastReadMessageId'])},
      );
}

class ChatPinned {
  final int id;
  final String body;
  final String? sender;
  ChatPinned(this.id, this.body, this.sender);
}

class ChatHit {
  final int messageId, conversationId;
  final String conversationTitle, senderName;
  final String? body;
  final DateTime? createdAt;
  ChatHit(this.messageId, this.conversationId, this.conversationTitle, this.senderName, this.body, this.createdAt);
}

class ChatPerson {
  final int id;
  final String name;
  final String? avatar;
  final bool online;
  ChatPerson(this.id, this.name, this.avatar, this.online);
}

ChatHit chatHitFromJson(Map<String, dynamic> j) =>
    ChatHit(_i(j['id']), _i(j['conversationId']), _s(j['conversation']), _s(j['sender']), _n(j['body']), _d(j['at']));
ChatPerson chatPersonFromJson(Map<String, dynamic> j) => ChatPerson(_i(j['id']), _s(j['name']), _n(j['avatar']), j['online'] == true);
ChatPinned chatPinnedFromJson(Map<String, dynamic> j) => ChatPinned(_i(j['id']), _s(j['body']), _n(j['sender']));
