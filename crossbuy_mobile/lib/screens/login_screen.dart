import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../l10n/app_localizations.dart';
import '../providers/auth_provider.dart';
import '../providers/locale_provider.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'shell.dart';

/// THE SIGN-IN — the owner-approved design (2026-11-22; the same design the web sign-in carries,
/// design-output/executive-prototype ?page=login): the brand's login scene under a flat navy veil with the white
/// logo, the motto and one thin gold rule; three moments of a working day (an approval with the person's photo,
/// the day's sales, a decision landing) arrive one after another over the picture and then breathe; the form rises
/// over it as a sheet with rounded shoulders, a faint CrossBusiness mark in its corner. No gradient anywhere.
///
/// Only what is real is a control: the cards are pictures of the product (not buttons), the token is always kept
/// (no "remember me" box that changes nothing), and there is no fingerprint or two-step button until the server
/// offers them.
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> with TickerProviderStateMixin {
  final _formKey = GlobalKey<FormState>();
  final _user = TextEditingController();
  final _pass = TextEditingController();
  bool _obscure = true;
  String? _error;

  // the cards' arrival (once) and their breathing (always)
  late final AnimationController _intro = AnimationController(vsync: this, duration: const Duration(milliseconds: 1900))..forward();
  late final AnimationController _float = AnimationController(vsync: this, duration: const Duration(seconds: 6))..repeat();

  @override
  void dispose() {
    _intro.dispose();
    _float.dispose();
    _user.dispose();
    _pass.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    setState(() => _error = null);
    if (!_formKey.currentState!.validate()) return;
    final t = AppLocalizations.of(context);
    final ok = await context.read<AuthProvider>().login(_user.text.trim(), _pass.text);
    if (!mounted) return;
    if (ok) {
      Navigator.of(context).pushReplacement(MaterialPageRoute(builder: (_) => const Shell()));
    } else {
      setState(() => _error = t.t('loginFailed'));
    }
  }

  InputDecoration _field(String label, IconData icon, {Widget? suffix}) => InputDecoration(
        hintText: label,
        hintStyle: const TextStyle(color: Color(0xFF8A96AD), fontSize: 14),
        prefixIcon: Icon(icon, size: 20, color: Brand.support),
        suffixIcon: suffix,
        filled: true,
        fillColor: Colors.white,
        contentPadding: const EdgeInsets.symmetric(vertical: 17, horizontal: 14),
        enabledBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: Brand.lineC)),
        focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: Brand.blue, width: 1.4)),
        errorBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: Brand.badC)),
        focusedErrorBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: Brand.badC, width: 1.4)),
      );

  /// A system the platform holds, as a frosted chip on the veil: it arrives in its slot of the intro, then breathes
  /// on its own phase. (Owner 2026-11-22: invented records — a named employee, "sales today" — say nothing; the
  /// systems themselves say what the motto promises.)
  Widget _module(int i, IconData icon, String label) {
    final reduce = MediaQuery.of(context).disableAnimations;
    final arrive = CurvedAnimation(parent: _intro, curve: Interval((.10 + i * .07).clamp(0, .7), (.45 + i * .07).clamp(0, 1), curve: Curves.easeOutCubic));
    return AnimatedBuilder(
      animation: Listenable.merge([_intro, _float]),
      builder: (_, c) {
        final a = reduce ? 1.0 : arrive.value;
        final bob = reduce ? 0.0 : math.sin((_float.value + i * .17) * 2 * math.pi) * 2.5;
        return Opacity(opacity: a, child: Transform.translate(offset: Offset(0, 14 * (1 - a) + bob), child: c));
      },
      child: Container(
        padding: const EdgeInsetsDirectional.fromSTEB(6, 6, 12, 6),
        decoration: BoxDecoration(
          color: Colors.white.withValues(alpha: .13),
          borderRadius: BorderRadius.circular(14),
          border: Border.all(color: Colors.white.withValues(alpha: .24)),
        ),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          Container(
            width: 26,
            height: 26,
            decoration: BoxDecoration(color: Colors.white.withValues(alpha: .16), borderRadius: BorderRadius.circular(9)),
            child: Icon(icon, size: 15, color: Colors.white),
          ),
          const SizedBox(width: 7),
          Text(label, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: Colors.white)),
        ]),
      ),
    );
  }

  Widget _modules() {
    final mods = [
      (Lu.landmark, L(context, 'المحاسبة', 'Accounting')),
      (Lu.package, L(context, 'المخزون', 'Inventory')),
      (Lu.usersRound, L(context, 'العملاء', 'CRM')),
      (Lu.store, L(context, 'نقاط البيع', 'Point of sale')),
      (Lu.factory, L(context, 'التصنيع', 'Manufacturing')),
      (Lu.briefcase, L(context, 'المشاريع', 'Projects')),
      (Lu.idCard, L(context, 'الموارد البشرية', 'Human resources')),
      (Lu.chartColumn, L(context, 'التقارير', 'Reports')),
    ];
    return Wrap(
      alignment: WrapAlignment.center,
      spacing: 8,
      runSpacing: 9,
      children: [for (final (i, m) in mods.indexed) _module(i, m.$1, m.$2)],
    );
  }

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    final auth = context.watch<AuthProvider>();
    final ar = isAr(context);
    final hour = DateTime.now().hour;
    final day = hour >= 4 && hour < 12;
    final top = MediaQuery.of(context).padding.top;
    const heroH = 330.0;

    return Scaffold(
      backgroundColor: Brand.page,
      body: LayoutBuilder(
        builder: (context, box) => SingleChildScrollView(
          child: ConstrainedBox(
            constraints: BoxConstraints(minHeight: box.maxHeight),
            child: IntrinsicHeight(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // ------------------------------------------------------------ the picture
                  SizedBox(
                    height: heroH + top,
                    child: Stack(clipBehavior: Clip.hardEdge, children: [
                      const Positioned.fill(
                        child: DecoratedBox(
                          decoration: BoxDecoration(
                            color: Brand.navy,
                            image: DecorationImage(
                              image: AssetImage('assets/brand/login-scene.jpg'),
                              fit: BoxFit.cover,
                              alignment: Alignment(0.48, -0.2),
                            ),
                          ),
                        ),
                      ),
                      const Positioned.fill(child: ColoredBox(color: Color(0x991F2D4D))),   // the flat veil — never a gradient
                      Positioned(
                        top: top + 26,
                        left: 0,
                        right: 0,
                        child: Column(children: [
                          Image.asset('assets/brand/crossbusiness-logo-white.png', height: 28),
                          const SizedBox(height: 10),
                          Text(L(context, 'أعمالك كلها، في مكان واحد', 'All your business, in one place'),
                              style: const TextStyle(fontSize: 12, color: Color(0xFFC9D4EA))),
                          const SizedBox(height: 10),
                          Container(width: 26, height: 2, decoration: BoxDecoration(color: Brand.gold, borderRadius: BorderRadius.circular(2))),
                        ]),
                      ),
                      Positioned(left: 20, right: 20, top: top + 128, child: _modules()),
                    ]),
                  ),
                  // ------------------------------------------------------------ the sheet, pulled up by its shoulders
                  Expanded(
                    child: Transform.translate(
                      offset: const Offset(0, -32),
                      child: Container(
                        clipBehavior: Clip.antiAlias,
                        decoration: const BoxDecoration(
                          color: Brand.page,
                          borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
                        ),
                        child: Stack(children: [
                          // a faint CrossBusiness mark in the corner
                          PositionedDirectional(
                            end: -70,
                            bottom: -70,
                            child: IgnorePointer(child: Opacity(opacity: .035, child: Image.asset('assets/brand/crossbusiness-mark.png', width: 250))),
                          ),
                          Padding(
                            padding: const EdgeInsets.fromLTRB(22, 22, 22, 0),
                            child: TweenAnimationBuilder<double>(
                              tween: Tween(begin: 0, end: 1),
                              duration: const Duration(milliseconds: 600),
                              curve: Curves.easeOutCubic,
                              builder: (_, v, child) => Opacity(opacity: v, child: Transform.translate(offset: Offset(0, 18 * (1 - v)), child: child)),
                              child: Form(
                                key: _formKey,
                                child: Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    // the greeting, by the phone's own clock
                                    Container(
                                      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 5),
                                      decoration: BoxDecoration(color: Brand.soft, borderRadius: BorderRadius.circular(999)),
                                      child: Row(mainAxisSize: MainAxisSize.min, children: [
                                        Icon(day ? Lu.sun : Lu.moon, size: 15, color: Brand.blue),
                                        const SizedBox(width: 6),
                                        Text(day ? L(context, 'صباح الخير', 'Good morning') : L(context, 'مساء الخير', 'Good evening'),
                                            style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: Brand.blue)),
                                      ]),
                                    ),
                                    const SizedBox(height: 10),
                                    Text(L(context, 'أهلًا بعودتك', 'Welcome back'),
                                        style: const TextStyle(fontSize: 24, fontWeight: FontWeight.w700, color: Brand.inkC, height: 1.4)),
                                    Text(L(context, 'سجّل الدخول بالحساب الذي أنشأته لك الإدارة', 'Sign in with the account your administrator created'),
                                        style: const TextStyle(fontSize: 12, color: Brand.mutedC)),
                                    const SizedBox(height: 18),
                                    if (_error != null) ...[
                                      Container(
                                        width: double.infinity,
                                        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
                                        decoration: BoxDecoration(
                                          color: Brand.badSoft,
                                          borderRadius: BorderRadius.circular(12),
                                          border: Border.all(color: const Color(0xFFE3A7A5)),
                                        ),
                                        child: Text(_error!, style: const TextStyle(color: Brand.badC, fontSize: 13)),
                                      ),
                                      const SizedBox(height: 12),
                                    ],
                                    TextFormField(
                                      controller: _user,
                                      textInputAction: TextInputAction.next,
                                      autocorrect: false,
                                      textDirection: TextDirection.ltr,
                                      decoration: _field(t.t('username'), Lu.userRound),
                                      validator: (v) => (v == null || v.isEmpty) ? t.t('required') : null,
                                    ),
                                    const SizedBox(height: 12),
                                    TextFormField(
                                      controller: _pass,
                                      obscureText: _obscure,
                                      textDirection: TextDirection.ltr,
                                      decoration: _field(
                                        t.t('password'),
                                        Lu.lockKeyhole,
                                        suffix: IconButton(
                                          icon: Icon(_obscure ? Lu.eyeOff : Lu.eye, size: 19, color: Brand.mutedC),
                                          tooltip: _obscure ? L(context, 'إظهار كلمة المرور', 'Show password') : L(context, 'إخفاء كلمة المرور', 'Hide password'),
                                          onPressed: () => setState(() => _obscure = !_obscure),
                                        ),
                                      ),
                                      validator: (v) => (v == null || v.isEmpty) ? t.t('required') : null,
                                      onFieldSubmitted: (_) => _submit(),
                                    ),
                                    Align(
                                      alignment: AlignmentDirectional.centerEnd,
                                      child: TextButton(
                                        onPressed: () => ScaffoldMessenger.of(context).showSnackBar(SnackBar(
                                            content: Text(L(context, 'تُعاد كلمة المرور عن طريق مدير النظام في شركتك.',
                                                'Your password is reset by your company\'s system administrator.')))),
                                        style: TextButton.styleFrom(foregroundColor: Brand.blue, padding: const EdgeInsets.symmetric(horizontal: 4)),
                                        child: Text(L(context, 'نسيت كلمة المرور؟', 'Forgot password?'),
                                            style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600)),
                                      ),
                                    ),
                                    const SizedBox(height: 4),
                                    SizedBox(
                                      width: double.infinity,
                                      height: 52,
                                      child: FilledButton(
                                        onPressed: auth.loading ? null : _submit,
                                        style: FilledButton.styleFrom(
                                          backgroundColor: Brand.navy,
                                          disabledBackgroundColor: Brand.navy.withValues(alpha: .7),
                                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14)),
                                        ),
                                        child: auth.loading
                                            ? const SizedBox(height: 22, width: 22, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                                            : Row(mainAxisSize: MainAxisSize.min, children: [
                                                Text(t.t('signIn'), style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w600, color: Colors.white)),
                                                const SizedBox(width: 8),
                                                Icon(ar ? Lu.arrowLeft : Lu.arrowRight, size: 18, color: Colors.white),
                                              ]),
                                      ),
                                    ),
                                    const Spacer(),
                                    // pinned to the bottom of the screen, never floating mid-page
                                    Padding(
                                      padding: EdgeInsets.only(top: 20, bottom: 32 + 14 + MediaQuery.of(context).padding.bottom),
                                      child: Row(children: [
                                        const Icon(Lu.shieldCheck, size: 14, color: Brand.mutedC),
                                        const SizedBox(width: 5),
                                        Expanded(
                                          child: Text(L(context, 'اتصال آمن', 'Secure connection'),
                                              style: const TextStyle(fontSize: 11, color: Brand.mutedC)),
                                        ),
                                        _LangSwitch(ar: ar),
                                      ]),
                                    ),
                                  ],
                                ),
                              ),
                            ),
                          ),
                        ]),
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// عربي | English — the same two-segment switch the prototype draws.
class _LangSwitch extends StatelessWidget {
  final bool ar;
  const _LangSwitch({required this.ar});

  @override
  Widget build(BuildContext context) {
    Widget seg(String label, bool on, Locale locale) => GestureDetector(
          onTap: on ? null : () => context.read<LocaleProvider>().setLocale(locale),
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 5),
            color: on ? Brand.soft : Colors.white,
            child: Text(label,
                style: TextStyle(fontSize: 11, color: on ? Brand.navy : Brand.mutedC, fontWeight: on ? FontWeight.w600 : FontWeight.w400)),
          ),
        );
    return Container(
      clipBehavior: Clip.antiAlias,
      decoration: BoxDecoration(border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(9), color: Colors.white),
      child: Row(mainAxisSize: MainAxisSize.min, children: [
        seg('عربي', ar, const Locale('ar')),
        seg('English', !ar, const Locale('en')),
      ]),
    );
  }
}
