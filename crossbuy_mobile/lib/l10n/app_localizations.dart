import 'package:flutter/material.dart';

/// Lightweight manual localization (AR/EN) — no codegen required.
class AppLocalizations {
  final Locale locale;
  AppLocalizations(this.locale);

  static AppLocalizations of(BuildContext context) =>
      Localizations.of<AppLocalizations>(context, AppLocalizations)!;

  static const LocalizationsDelegate<AppLocalizations> delegate =
      _AppLocalizationsDelegate();

  static const List<Locale> supportedLocales = [Locale('en'), Locale('ar')];

  bool get isAr => locale.languageCode == 'ar';

  static const Map<String, Map<String, String>> _values = {
    'en': {
      'appName': 'CrossBuy',
      'login': 'Login',
      'username': 'Username',
      'password': 'Password',
      'signIn': 'Sign in',
      'loginFailed': 'Login failed. Check your credentials.',
      'required': 'Required',
      'profile': 'My Profile',
      'structure': 'Org Structure',
      'logout': 'Logout',
      'language': 'العربية',
      'firstName': 'First name',
      'lastName': 'Last name',
      'email': 'Email',
      'phone': 'Phone',
      'address': 'Address',
      'gender': 'Gender',
      'maritalStatus': 'Marital status',
      'jobTitle': 'Job title',
      'company': 'Company',
      'branch': 'Branch',
      'dateOfBirth': 'Date of birth',
      'dateOfJoining': 'Joining date',
      'verified': 'Verified',
      'tasks': 'Tasks',
      'notifications': 'Notifications',
      'more': 'More',
      'comingSoon': 'Coming soon',
      'dashboard': 'Dashboard',
      'leaveBalances': 'Leave Balances',
      'myPending': 'My pending',
      'approvedThisYear': 'Approved this year',
      'awaitingMyApproval': 'Awaiting my approval',
      'myTeamSize': 'My team',
      'leaves': 'Leaves',
      'myRequests': 'My Requests',
      'forApproval': 'For Approval',
      'newLeave': 'New Request',
      'leaveType': 'Leave type',
      'fromDate': 'From',
      'toDate': 'To',
      'reason': 'Reason (optional)',
      'submit': 'Submit',
      'approve': 'Approve',
      'reject': 'Reject',
      'pending': 'Pending',
      'approved': 'Approved',
      'rejected': 'Rejected',
      'days': 'days',
      'noRequests': 'No leave requests yet',
      'noApprovals': 'No requests awaiting your approval',
      'requestSubmitted': 'Leave request submitted',
      'decisionNote': 'Note (optional)',
      'selectLeaveType': 'Select leave type',
      'cancel': 'Cancel',
      'requestedBy': 'Requested by',
      'myPosition': 'My position',
      'noData': 'No data available',
      'retry': 'Retry',
      'finance': 'Finance',
      'inventory': 'Inventory',
      'cash': 'Cash & Banks',
      'receivables': 'Receivables',
      'payables': 'Payables',
      'revenue': 'Revenue',
      'expenses': 'Expenses',
      'netProfit': 'Net Profit',
      'netLoss': 'Net Loss',
      'resultThisYear': 'Result This Year',
      'partners': 'Customers & Vendors',
      'customers': 'Customers',
      'vendors': 'Vendors',
      'recentEntries': 'Recent Entries',
      'loading': 'Loading…',
      'reportingLine': 'Reporting line',
      'myTeam': 'Reporting to me',
      'totalTeamSize': 'Total Team Size',
      'noSubordinates': 'No one reports to you',
      'notPlaced': 'You are not placed in the org structure yet',
      'you': 'You',
      'person': 'person',
      'people': 'people',
    },
    'ar': {
      'appName': 'كروس باي',
      'login': 'تسجيل الدخول',
      'username': 'اسم المستخدم',
      'password': 'كلمة المرور',
      'signIn': 'دخول',
      'loginFailed': 'فشل تسجيل الدخول. تحقق من بياناتك.',
      'required': 'مطلوب',
      'profile': 'ملفي',
      'structure': 'الهيكل الإداري',
      'logout': 'تسجيل الخروج',
      'language': 'English',
      'firstName': 'الاسم الأول',
      'lastName': 'الاسم الأخير',
      'email': 'البريد الإلكتروني',
      'phone': 'الهاتف',
      'address': 'العنوان',
      'gender': 'الجنس',
      'maritalStatus': 'الحالة الاجتماعية',
      'jobTitle': 'المسمى الوظيفي',
      'company': 'الشركة',
      'branch': 'الفرع',
      'dateOfBirth': 'تاريخ الميلاد',
      'dateOfJoining': 'تاريخ الالتحاق',
      'verified': 'موثّق',
      'tasks': 'المهام',
      'notifications': 'الإشعارات',
      'more': 'المزيد',
      'comingSoon': 'قريبًا',
      'dashboard': 'الرئيسية',
      'leaveBalances': 'رصيد الإجازات',
      'myPending': 'طلباتي المعلّقة',
      'approvedThisYear': 'معتمَدة هذا العام',
      'awaitingMyApproval': 'بانتظار اعتمادي',
      'myTeamSize': 'حجم فريقي',
      'leaves': 'الإجازات',
      'myRequests': 'طلباتي',
      'forApproval': 'للاعتماد',
      'newLeave': 'طلب جديد',
      'leaveType': 'نوع الإجازة',
      'fromDate': 'من',
      'toDate': 'إلى',
      'reason': 'السبب (اختياري)',
      'submit': 'إرسال',
      'approve': 'موافقة',
      'reject': 'رفض',
      'pending': 'معلّق',
      'approved': 'موافَق عليه',
      'rejected': 'مرفوض',
      'days': 'أيام',
      'noRequests': 'لا توجد طلبات إجازة بعد',
      'noApprovals': 'لا توجد طلبات بانتظار اعتمادك',
      'requestSubmitted': 'تم إرسال طلب الإجازة',
      'decisionNote': 'ملاحظة (اختياري)',
      'selectLeaveType': 'اختر نوع الإجازة',
      'cancel': 'إلغاء',
      'requestedBy': 'مقدّم الطلب',
      'myPosition': 'موقعي',
      'noData': 'لا توجد بيانات',
      'retry': 'إعادة المحاولة',
      'finance': 'المالية',
      'inventory': 'المخزون',
      'cash': 'النقدية والبنوك',
      'receivables': 'العملاء (مدينون)',
      'payables': 'الموردون (دائنون)',
      'revenue': 'الإيرادات',
      'expenses': 'المصروفات',
      'netProfit': 'صافي الربح',
      'netLoss': 'صافي الخسارة',
      'resultThisYear': 'نتيجة العام',
      'partners': 'العملاء والموردون',
      'customers': 'العملاء',
      'vendors': 'الموردون',
      'recentEntries': 'أحدث القيود',
      'loading': 'جارٍ التحميل…',
      'reportingLine': 'التسلسل الإداري',
      'myTeam': 'يتبعونني',
      'totalTeamSize': 'إجمالي الفريق',
      'noSubordinates': 'لا يوجد موظفون تابعون لك',
      'notPlaced': 'لم يتم تحديد موقعك في الهيكل بعد',
      'you': 'أنت',
      'person': 'موظف',
      'people': 'موظفين',
    },
  };

  String t(String key) =>
      _values[locale.languageCode]?[key] ?? _values['en']![key] ?? key;
}

class _AppLocalizationsDelegate
    extends LocalizationsDelegate<AppLocalizations> {
  const _AppLocalizationsDelegate();

  @override
  bool isSupported(Locale locale) =>
      ['en', 'ar'].contains(locale.languageCode);

  @override
  Future<AppLocalizations> load(Locale locale) async =>
      AppLocalizations(locale);

  @override
  bool shouldReload(_AppLocalizationsDelegate old) => false;
}
