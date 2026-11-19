import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:signalr_netcore/signalr_client.dart';

import '../app_config.dart';
import 'api_service.dart';

/// One thing that happened in the chat, as the server announced it.
class ChatEvent {
  final String name;              // message | conversation | edited | deleted | reaction | read | typing | pinned | poll | thread | status
  final int conversationId;       // 0 when it is not about one conversation (status)
  final Map<String, dynamic> data;
  const ChatEvent(this.name, this.conversationId, this.data);
}

/// THE CHAT, LIVE (owner 2026-11-19: "finish everything"). The browser's chat hub (/hubs/chat), reached with the app's
/// token: a new message, a read mark, a reaction, someone typing — the same instant the browser sees it, instead of the
/// next poll. The screens listen to [events]; the polling they keep is only the fallback for a dropped connection.
///
/// The hub itself decides who may join a conversation's group (JoinConversation checks membership); this only asks.
class ChatHub {
  ChatHub._();
  static final ChatHub instance = ChatHub._();

  HubConnection? _hub;
  bool connected = false;
  final ValueNotifier<ChatEvent?> events = ValueNotifier(null);
  final Set<int> _joined = {};

  static const _names = ['message', 'conversation', 'edited', 'deleted', 'reaction', 'read', 'typing', 'pinned', 'poll', 'thread', 'status', 'members', 'removed'];

  static Map<String, dynamic> _m(List<Object?>? a) => (a != null && a.isNotEmpty && a[0] is Map) ? (a[0] as Map).cast<String, dynamic>() : <String, dynamic>{};

  Future<void> connect() async {
    if (_hub != null) { if (!connected) _keepTrying(); return; }
    final hub = HubConnectionBuilder()
        .withUrl('${AppConfig.apiBaseUrl}/hubs/chat', options: HttpConnectionOptions(accessTokenFactory: () async => ApiService.instance.token ?? ''))
        .withAutomaticReconnect(retryDelays: [0, 2000, 5000, 10000, ...List.filled(120, 15000)])
        .build();
    for (final n in _names) {
      hub.on(n, (a) {
        final m = _m(a);
        events.value = ChatEvent(n, (m['conversationId'] as num?)?.toInt() ?? 0, m);
      });
    }
    hub.onreconnected(({connectionId}) { connected = true; for (final c in _joined) { _join(c); } });
    hub.onreconnecting(({error}) { connected = false; });
    hub.onclose(({error}) { connected = false; _keepTrying(); });
    _hub = hub;
    try {
      await hub.start();
      connected = true;
    } catch (_) {
      connected = false;   // the screens still poll; the automatic reconnect does not cover a failed first start
      _keepTrying();
    }
  }

  /// A closed connection (a server restart outlasts the automatic reconnect) is started again until it is back.
  bool _retrying = false;
  void _keepTrying() {
    if (_retrying) return;
    _retrying = true;
    () async {
      var wait = 5;
      while (_hub != null && !connected) {
        await Future.delayed(Duration(seconds: wait));
        try { await _hub?.start(); connected = true; for (final c in _joined) { await _join(c); } } catch (_) { wait = wait < 30 ? wait * 2 : 30; }
      }
      _retrying = false;
    }();
  }

  Future<void> _join(int c) async { try { await _hub?.invoke('JoinConversation', args: [c]); } catch (_) {} }

  /// Hear this conversation's events (messages, reads, typing) while it is open.
  Future<void> join(int c) async { _joined.add(c); await connect(); if (connected) await _join(c); }

  Future<void> leave(int c) async { _joined.remove(c); try { await _hub?.invoke('LeaveConversation', args: [c]); } catch (_) {} }

  /// "I am typing" to the others in the conversation (the hub sends it to everyone but me).
  Future<void> typing(int c) async { if (!connected) return; try { await _hub?.invoke('Typing', args: [c]); } catch (_) {} }

  Future<void> disconnect() async {
    try { await _hub?.stop(); } catch (_) {}
    _hub = null; connected = false; _joined.clear();
  }
}
