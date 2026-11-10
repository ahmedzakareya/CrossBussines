import 'package:flutter/widgets.dart';

/// App-wide navigation hints so notifications (toast or list) can route the user to the right tab.
///
/// The redesigned shell (owner-approved prototype) has FOUR tabs and a centre quick-action button. The tab indexes are
/// named constants: the old code assumed "4 = Notifications" while Notifications sat at index 6, so a tap on a
/// notification opened Finance.
class AppNav {
  static final navigatorKey = GlobalKey<NavigatorState>();

  static const int home = 0, inbox = 1, apps = 2, me = 3;

  /// Bottom-nav tab the shell should show.
  static final ValueNotifier<int> tabIndex = ValueNotifier<int>(home);

  /// Inbox filter: 0 all · 1 approvals · 2 updates · 3 sent.
  static final ValueNotifier<int> inboxFilter = ValueNotifier<int>(0);

  /// Kept for the leave screen's own sub-tabs: 0 = My Requests, 1 = For Approval.
  static final ValueNotifier<int> leaveSubTab = ValueNotifier<int>(0);

  /// Bumped whenever data changes (create / approve / reject) so any visible screen can reload itself immediately —
  /// even the requester's own device, which receives no notification for its own action.
  static final ValueNotifier<int> dataChanged = ValueNotifier<int>(0);
  static void notifyDataChanged() => dataChanged.value++;

  static void goInbox({int filter = 0}) {
    inboxFilter.value = filter;
    tabIndex.value = inbox;
  }

  static void goLeaves({int sub = 0}) {
    leaveSubTab.value = sub;
    goInbox(filter: sub == 1 ? 1 : 2);
  }

  static void goNotifications() => goInbox(filter: 2);

  static void resetTabs() {
    leaveSubTab.value = 0;
    inboxFilter.value = 0;
    tabIndex.value = home;
  }

  /// Route based on a notification type: anything waiting for me goes to the approvals filter, the rest to updates.
  static void routeForNotification(String? type) {
    if (type == 'leave_submitted' || type == 'emp_request_submitted') {
      goInbox(filter: 1);
    } else {
      goInbox(filter: 2);
    }
  }
}
