# CrossBuy Mobile (Flutter)

تطبيق موبايل لنظام **CrossBuy** يعمل على **Android و iOS**، يدعم **العربية (RTL) والإنجليزية (LTR)**.

النسخة الأولى تعرض: **بياناتي كاملة** + **موقعي في الهيكل الإداري**.

---

## المتطلبات
- تثبيت **Flutter SDK** (3.3+): https://docs.flutter.dev/get-started/install
- تشغيل الـ backend (مشروع CrossBuy) على جهازك.

## خطوات التشغيل (أول مرة)

```bash
cd crossbuy_mobile

# توليد مجلدات المنصات (android/ ios/) مع الحفاظ على lib/ و pubspec.yaml الحاليين
flutter create .

# تنزيل الحزم
flutter pub get

# تشغيل
flutter run
```

## ضبط عنوان الـ API
عدّل [lib/app_config.dart](lib/app_config.dart) → `apiBaseUrl`:
- **محاكي Android**: `http://10.0.2.2:5000`
- **محاكي iOS**: `http://localhost:5000`
- **جهاز حقيقي**: `http://<IP-جهازك-على-الشبكة>:5000`

استخدم نفس البورت اللي بيطبعه `dotnet run` في الـ backend.

> شغّل الـ backend بـ HTTP صريح للتجربة:
> `dotnet run --urls http://0.0.0.0:5000` (من مجلد CrossBuy)

## مهم لأندرويد (HTTP بدون SSL)
أندرويد بيمنع HTTP العادي افتراضيًا. بعد `flutter create .`، أضف في
`android/app/src/main/AndroidManifest.xml` داخل وسم `<application ...>`:

```xml
android:usesCleartextTraffic="true"
```
(للتطوير فقط — في الإنتاج استخدم HTTPS.)

---

## البنية
```
lib/
├── app_config.dart            # عنوان الـ API
├── main.dart                  # نقطة البداية + الثيم + اللغات
├── l10n/app_localizations.dart# الترجمة AR/EN يدويًا
├── models/                    # Employee, OrgNode
├── services/api_service.dart  # Dio + حفظ التوكن
├── providers/                 # Auth + Locale (provider)
└── screens/                   # login, home, profile, structure
```

## الـ API المستخدَم (من الـ backend)
| Endpoint | الوصف |
|---|---|
| `POST /api/auth/login` | تسجيل الدخول ويرجّع JWT |
| `GET /api/me/profile` | بيانات الموظف الحالي |
| `GET /api/me/structure` | الهيكل الإداري لشركته + تحديد موقعه |

كل طلبات `/api/me/*` تحتاج هيدر: `Authorization: Bearer <token>`.
