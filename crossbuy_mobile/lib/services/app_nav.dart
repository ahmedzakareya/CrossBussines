import 'package:flutter/widgets.dart';

/// App-wide navigation hints so notifications (toast or list) can route
/// the user to the right screen/tab.
class AppNav {
  static final navigatorKey = GlobalKey<NavigatorState>();

  /// Bottom-nav tab index the Home screen should show.
  static final ValueNotifier<int> tabIndex = ValueNotifier<int>(0);

  /// Leaves sub-tab: 0 = My Requests, 1 = For Approval.
  static final ValueNotifier<int> leaveSubTab = ValueNotifier<int>(0);

  /// Bumped whenever leave data changes (create / approve / reject) so any
  /// visible screen can reload itself immediately — even the requester's own
  /// device, which receives no notification for its own action.
  static final ValueNotifier<int> dataChanged = ValueNotifier<int>(0);
  static void notifyDataChanged() => dataChanged.value++;

  // tab order: 0 Dashboard · 1 Profile · 2 Structure · 3 Leaves · 4 Notifications
  static void goLeaves({int sub = 0}) {
    leaveSubTab.value = sub;
    tabIndex.value = 3;
  }

  static void goNotifications() => tabIndex.value = 4;

  static void resetTabs() {
    leaveSubTab.value = 0;
    tabIndex.value = 0;
  }

  /// Route based on a notification type.
  static void routeForNotification(String? type) {
    if (type == 'leave_submitted') {
      goLeaves(sub: 1); // manager → For Approval
    } else if (type == 'leave_approved' || type == 'leave_rejected' || type == 'leave_progress') {
      goLeaves(sub: 0); // employee → My Requests
    } else {
      goNotifications();
    }
  }
}
