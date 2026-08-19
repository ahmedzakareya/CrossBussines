namespace CrossBuy.BL.Workspace
{
    // ============================================================================================
    // CrossBusiness Workspace — NAVIGATION (Phase 2)
    //
    // Eight sections, built FROM the resolved capabilities rather than from a fixed list. Three rules:
    //
    //   1. HIDDEN WHEN UNAVAILABLE. An entry whose backing platform is not registered is not rendered at all.
    //      A nav link that always leads to "not available" trains people to ignore the rail.
    //   2. VISUALLY DISTINCT FROM AN EMPTY DATA STATE. Hiding is for "this deployment cannot do that";
    //      an entry that IS shown and leads to an empty panel is a different thing, and the panel says so.
    //      The two are never expressed the same way.
    //   3. FUTURE MODULES ARE NEVER ACTIVE LINKS. They render dimmed and non-clickable (`Soon`), or not at all.
    //
    // Capability, not permission-guessing. The Workspace does not evaluate module permissions itself — that
    // would be a second, divergent copy of each module's rule. It asks whether the CONTRACT resolves and
    // whether the caller can be identified; the module's own screen still authorizes on arrival.
    // ============================================================================================
    public static class WorkspaceNavigation
    {
        public static IReadOnlyList<WorkspaceNavSection> Build(
            WorkspaceCapabilities capabilities,
            int unreadNotifications = 0,
            int unreadMentions = 0,
            int openWork = 0)
        {
            var sections = new List<WorkspaceNavSection>();

            // ---- WORKSPACE -------------------------------------------------------------------------
            var workspace = new List<WorkspaceNavItem>
            {
                new()
                {
                    LabelAr = "الرئيسية", LabelEn = "Home",
                    Icon = "ki-outline ki-element-11", Action = "Index",
                },
            };

            if (capabilities.Tasks)
                workspace.Add(new WorkspaceNavItem
                {
                    LabelAr = "أعمالي", LabelEn = "My Work",
                    Icon = "ki-outline ki-check-square",
                    Action = "Index", Fragment = "my-work",
                    Badge = openWork > 0 ? openWork : null,
                });

            if (capabilities.Agenda)
                workspace.Add(new WorkspaceNavItem
                {
                    LabelAr = "الأجندة", LabelEn = "Agenda",
                    Icon = "ki-outline ki-calendar", Action = "Agenda",
                });

            sections.Add(new WorkspaceNavSection
            {
                LabelAr = "مساحة العمل", LabelEn = "Workspace", Items = workspace,
            });

            // ---- ATTENTION -------------------------------------------------------------------------
            var attention = new List<WorkspaceNavItem>();

            if (capabilities.Notifications)
                attention.Add(new WorkspaceNavItem
                {
                    LabelAr = "الإشعارات", LabelEn = "Notifications",
                    Icon = "ki-outline ki-notification-bing", Action = "Notifications",
                    Badge = unreadNotifications > 0 ? unreadNotifications : null,
                    BadgeIsAttention = true,
                });

            // Mentions is hidden when Communication is not activated — the panel exists and states its reason,
            // but a rail entry that always says "not activated" is noise. The Mentions ROUTE stays reachable so
            // a reviewer can open it directly and see the state; it is only the nav entry that is conditional.
            if (capabilities.Communication)
                attention.Add(new WorkspaceNavItem
                {
                    LabelAr = "الإشارات إليّ", LabelEn = "Mentions",
                    Icon = "ki-outline ki-messages", Action = "Mentions",
                    Badge = unreadMentions > 0 ? unreadMentions : null,
                    BadgeIsAttention = true,
                });

            if (attention.Count > 0)
                sections.Add(new WorkspaceNavSection
                {
                    LabelAr = "ما يحتاج انتباهك", LabelEn = "Needs attention", Items = attention,
                });

            // ---- QUICK ACCESS ----------------------------------------------------------------------
            var quick = new List<WorkspaceNavItem>
            {
                new()
                {
                    LabelAr = "المفضلة", LabelEn = "Favorites",
                    Icon = "ki-outline ki-star", Action = "Index", Fragment = "favorites",
                },
                new()
                {
                    LabelAr = "النشاط الأخير", LabelEn = "Recent Activity",
                    Icon = "ki-outline ki-time", Action = "Index", Fragment = "activity",
                },
            };

            if (capabilities.Reporting)
                quick.Insert(0, new WorkspaceNavItem
                {
                    LabelAr = "التقارير", LabelEn = "Reports",
                    Icon = "ki-outline ki-chart-simple", Action = "Reports",
                });

            sections.Add(new WorkspaceNavSection
            {
                LabelAr = "الوصول السريع", LabelEn = "Quick access", Items = quick,
            });

            // ---- MODULES ---------------------------------------------------------------------------
            //
            // Links OUT to module screens that already exist. Each target performs its own authorization on
            // arrival, so the Workspace neither duplicates nor pre-empts that decision.
            //
            // The six out-of-scope products (Security Console, Report Studio, CRM, Construction, AI, Mobile)
            // appear nowhere — not even dimmed. They are out of this product's scope rather than merely
            // unbuilt, and showing them would imply a commitment this tab has not been asked to make.
            sections.Add(new WorkspaceNavSection
            {
                LabelAr = "الوحدات", LabelEn = "Modules",
                Items = new[]
                {
                    new WorkspaceNavItem
                    {
                        LabelAr = "المحاسبة", LabelEn = "Accounting",
                        Icon = "ki-outline ki-bill", Controller = "Accounting", Action = "Index",
                    },
                    new WorkspaceNavItem
                    {
                        LabelAr = "المخزون", LabelEn = "Inventory",
                        Icon = "ki-outline ki-package", Controller = "Inventory", Action = "Index",
                    },
                    new WorkspaceNavItem
                    {
                        LabelAr = "المهام", LabelEn = "Tasks",
                        Icon = "ki-outline ki-abstract-26", Controller = "Tasks", Action = "Index",
                    },
                },
            });

            return sections;
        }

        // Quick Actions, filtered by capability for the same reason the rail is: an action that cannot work in
        // this deployment is not offered. Every one LINKS to a screen that already exists — the Workspace
        // creates nothing, which is what stops "quick actions" becoming a second, competing write path.
        public static IReadOnlyList<WorkspaceQuickAction> QuickActions(WorkspaceCapabilities capabilities)
        {
            var actions = new List<WorkspaceQuickAction>();

            if (capabilities.Tasks)
                actions.Add(new WorkspaceQuickAction
                {
                    LabelAr = "مهمة جديدة", LabelEn = "New task",
                    Icon = "ki-outline ki-plus-square", Url = "/Tasks/Index",
                });

            actions.Add(new WorkspaceQuickAction
            {
                LabelAr = "فاتورة مبيعات", LabelEn = "Sales invoice",
                Icon = "ki-outline ki-bill", Url = "/Accounting/SalesInvoices",
            });
            actions.Add(new WorkspaceQuickAction
            {
                LabelAr = "فاتورة مشتريات", LabelEn = "Purchase invoice",
                Icon = "ki-outline ki-basket", Url = "/Accounting/PurchaseInvoices",
            });
            actions.Add(new WorkspaceQuickAction
            {
                LabelAr = "الأصناف", LabelEn = "Items",
                Icon = "ki-outline ki-package", Url = "/Inventory/Items",
            });

            if (capabilities.Calendar)
                actions.Add(new WorkspaceQuickAction
                {
                    LabelAr = "التقويم", LabelEn = "Calendar",
                    Icon = "ki-outline ki-calendar", Url = "/Calendar/Index",
                });

            if (capabilities.Reporting)
                actions.Add(new WorkspaceQuickAction
                {
                    LabelAr = "التقارير", LabelEn = "Reports",
                    Icon = "ki-outline ki-chart-simple", Url = "/Workspace/Reports",
                });

            return actions;
        }
    }
}
