namespace CrossBuy.BL
{
    // Canonical catalog for notification "Type" strings + their presentation (category/icon/priority) and a
    // CENTRAL click-through URL resolver. Producers pass only (type, refId); NotificationService fills
    // Category/Icon/Priority/Url from here, so no producer needs editing. Recognizes BOTH the canonical
    // constants and the real ad-hoc strings already emitted across the 12 producer services.
    public static class NotificationTypes
    {
        // ----- HR / Leave / Requests -----
        public const string LeaveSubmitted = "leave_submitted";
        public const string LeaveApproved = "leave_approved";
        public const string LeaveRejected = "leave_rejected";
        public const string DocExpiry = "hr_expiry";
        // ----- Sales / Receivables -----
        public const string SalesOrder = "sales_order";
        public const string SalesInvoice = "sales_invoice";
        public const string CreditBlock = "credit_block";
        // ----- Purchasing / Payables -----
        public const string PurchaseOrder = "purchase_order";
        public const string GoodsReceipt = "goods_receipt";
        public const string PurchaseInvoice = "purchase_invoice";
        // ----- Inventory / Governance -----
        public const string InventoryApproval = "inventory_approval";
        public const string Integrity = "integrity";
        // ----- CRM -----
        public const string CrmReminder = "crm_reminder";
        public const string CrmAssigned = "crm_assigned";
        public const string CrmTicket = "crm_ticket";
        // ----- Chat (P3) -----
        public const string ChatMessage = "chat_message";
        public const string ChatMention = "chat_mention";

        public enum Prio { Normal, High, Critical }

        // Default presentation per type (recognizes the real producer strings too). Unknown → General.
        public static (string category, string icon, string priority) Meta(string? type) => (type ?? "") switch
        {
            "leave_submitted" or "leave_approved" or "leave_rejected" or "leave_advanced" => ("HR", "ki-calendar-tick", nameof(Prio.Normal)),
            "emp_request_submitted" or "emp_request_approved" or "emp_request_rejected" => ("HR", "ki-file-added", nameof(Prio.Normal)),
            "appraisal_submitted" or "appraisal_acknowledged" or "appraisal_due" => ("HR", "ki-medal-star", nameof(Prio.Normal)),
            "hr_expiry" => ("HR", "ki-shield-cross", nameof(Prio.High)),
            "sales_order" or "sales_invoice" or "delivery_posted" => ("Sales", "ki-handcart", nameof(Prio.Normal)),
            "credit_block" => ("Sales", "ki-lock", nameof(Prio.High)),
            "purchase_order" => ("Purchasing", "ki-purchase", nameof(Prio.Normal)),
            "goods_receipt" => ("Purchasing", "ki-parcel-tracking", nameof(Prio.Normal)),
            "purchase_invoice" => ("Purchasing", "ki-bill", nameof(Prio.Normal)),
            "inventory_approval" or "InventoryApproval" => ("Inventory", "ki-check-square", nameof(Prio.High)),
            "integrity" or "Integrity" => ("Governance", "ki-shield-tick", nameof(Prio.Critical)),
            "opportunity_won" or "crm_reminder" or "crm-reminder" or "crm_automation" or "crm_assigned" or "crm_ticket" => ("CRM", "ki-notification-status", nameof(Prio.Normal)),
            "chat_message" or "chat_mention" => ("Chat", "ki-message-text-2", nameof(Prio.Normal)),
            _ => ("General", "ki-notification-status", nameof(Prio.Normal)),
        };

        // Central click-through target. refId meaning is per-type (doc id / customer id / conversation id …).
        // Returns null when there is no sensible destination (caller-supplied url wins over this anyway).
        public static string? UrlFor(string? type, int? refId) => (type ?? "") switch
        {
            "leave_submitted" or "leave_approved" or "leave_rejected" or "leave_advanced" => "/People/Leaves",
            "emp_request_submitted" or "emp_request_approved" or "emp_request_rejected" => "/People/Requests",
            "appraisal_submitted" or "appraisal_acknowledged" or "appraisal_due" => "/Admin/Appraisals",
            "hr_expiry" => "/Admin/DocExpiryAlerts",
            "sales_order" => "/Inventory/SalesOrders",
            "delivery_posted" => "/Inventory/Deliveries",
            "goods_receipt" => "/Inventory/GoodsReceipts",
            "purchase_order" => "/Inventory/PurchaseOrders",
            "purchase_invoice" => "/Accounting/PurchaseInvoices",
            "sales_invoice" => "/Accounting/SalesInvoices",
            "credit_block" => "/Accounting/Customers",
            "opportunity_won" => "/Crm/Opportunities",
            "crm_reminder" or "crm-reminder" or "crm_automation" => "/Crm/Index",
            "crm_ticket" => "/Crm/Tickets",
            "integrity" or "Integrity" => "/Inventory/IntegrityReconciliation",
            "inventory_approval" or "InventoryApproval" => "/Inventory/Approvals",
            "chat_message" or "chat_mention" => refId.HasValue ? $"/Chat?c={refId.Value}" : "/Chat",
            _ => null,
        };
    }
}
