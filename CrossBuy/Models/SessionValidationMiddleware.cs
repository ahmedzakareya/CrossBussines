namespace CrossBuy.Models
{
	public class SessionValidationMiddleware
	{
		private readonly RequestDelegate _next;

		public SessionValidationMiddleware(RequestDelegate next)
		{
			_next = next;
		}

		public async Task InvokeAsync(HttpContext context)
		{
			var path = context.Request.Path.Value?.ToLower() ?? string.Empty;

			// استثناء صفحات تسجيل الدخول والخروج وتغيير اللغة
			// وكذلك واجهات الـ API (الموبايل) لأنها تعتمد على JWT وليس على الجلسة
			// The independent cashier app (/pos/login, /pos/start, /pos/terminal, /pos/order/*, /pos/orders/*,
			// /pos/tables/*, /pos/logout) is gated by its OWN session key ("PosCtx") inside PosAppController —
			// NOT the admin "Employee" gate. (Admin POS setup screens live under /Pos/Setup, ... and stay gated below.)
			bool isCashierApp = path.StartsWith("/pos/login") || path.StartsWith("/pos/start")
				|| path.StartsWith("/pos/terminal") || path.StartsWith("/pos/order") || path.StartsWith("/pos/table") || path.StartsWith("/pos/item") || path.StartsWith("/pos/shift") || path.StartsWith("/pos/offline") || path.StartsWith("/pos/sync")
				|| path.StartsWith("/pos/customer") || path.StartsWith("/pos/delivery") || path.StartsWith("/pos/drivers") || path.StartsWith("/pos/reservation") || path.StartsWith("/pos/kds") || path.StartsWith("/pos/kitchen") || path.StartsWith("/pos/logout");

			// The PUBLIC e-commerce storefront is end-user facing: browse without any login, on its own visitor session
			// (NOT the staff "Employee" gate). Covers the catalog (/Home/Store) + product/category pages (/Store/*).
			bool isStorefront = path == "/home/store" || path.StartsWith("/store");

			// HM-0: the INDEPENDENT hypermarket lane at /hyper/pos has its OWN gate (session "HyperCtx") inside
			// HyperPosController — same pattern as the restaurant /pos cashier app. The bypass is granted ONLY to
			// /hyper/pos (StartsWith), NOT to /hyper — the hyper BACK-OFFICE (/hyper) stays behind the Employee gate.
			bool isHyperLane = path.StartsWith("/hyper/pos");

			if (!isCashierApp &&
				!isHyperLane &&
				!isStorefront &&
				!path.StartsWith("/api") &&
				!path.StartsWith("/swagger") &&
				!path.StartsWith("/hubs") &&
				!path.Contains("/account/login") &&
				!path.Contains("/account/logout") &&
				!path.Contains("/account/setlanguage"))
			{
				// التحقق من وجود جلسة الموظف
				if (string.IsNullOrEmpty(context.Session.GetString("Employee")))
				{
					// نحتفظ بالصفحة التي كان عليها المستخدم لنعيده إليها بعد إعادة تسجيل الدخول.
					// نلتقط returnUrl فقط لطلبات الصفحات (GET وغير Ajax) حتى لا نعيد المستخدم
					// إلى نقطة نهاية Ajax/JSON بدلًا من صفحة فعلية.
					var isAjax = context.Request.Headers["X-Requested-With"] == "XMLHttpRequest";
					if (HttpMethods.IsGet(context.Request.Method) && !isAjax)
					{
						var returnUrl = context.Request.Path + context.Request.QueryString;
						context.Response.Redirect("/Account/Login?returnUrl=" + Uri.EscapeDataString(returnUrl));
					}
					else
					{
						context.Response.Redirect("/Account/Login");
					}
					return;
				}
			}

			await _next(context);
		}

	}

}
