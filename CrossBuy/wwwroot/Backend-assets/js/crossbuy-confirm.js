/*
 * crossbuy-confirm.js
 * ------------------------------------------------------------------
 * Unified, project-wide confirmation + notification standard for CrossBuy.
 *
 * Rule (applies to EVERY edit / save / approve / reject / delete action):
 *   1) Show a branded SweetAlert2 confirmation FIRST.
 *   2) After execution, show a toastr toast for success or failure.
 *
 * Styling is "pure Metronic": SweetAlert buttons reuse Metronic/brand
 * button classes (btn btn-primary / btn-danger / btn-light), so they
 * automatically follow the CrossBuy visual identity (crossbuy-brand.css).
 *
 * Bilingual: text is auto-selected (Arabic when <html dir="rtl"> or lang="ar").
 *
 * Usage patterns -- see the bottom of this file and the project docs.
 * ------------------------------------------------------------------
 */
(function (window, document) {
    'use strict';

    var CB = window.CB || (window.CB = {});

    // ---- locale -----------------------------------------------------
    function isRTL() {
        var d = (document.documentElement.getAttribute('dir') || document.dir || '').toLowerCase();
        return d === 'rtl';
    }
    function isAr() {
        var lang = (document.documentElement.lang || '').toLowerCase();
        return isRTL() || lang.indexOf('ar') === 0;
    }
    function L(ar, en) { return isAr() ? ar : en; }
    CB.L = L;

    // ---- toastr defaults (configured once) --------------------------
    function ensureToastr() {
        if (!window.toastr) return false;
        if (CB._toastrReady) return true;
        window.toastr.options = {
            closeButton: true,
            progressBar: true,
            newestOnTop: true,
            preventDuplicates: true,
            positionClass: isRTL() ? 'toast-top-left' : 'toast-top-right',
            rtl: isRTL(),
            timeOut: 4000,
            extendedTimeOut: 2000
        };
        CB._toastrReady = true;
        return true;
    }

    // ---- toast wrappers (graceful fallback to alert) ----------------
    CB.toast = {
        success: function (msg, title) { if (ensureToastr()) window.toastr.success(msg || L('تمت العملية بنجاح', 'Operation completed successfully'), title || ''); else _fallback(msg, false); },
        error:   function (msg, title) { if (ensureToastr()) window.toastr.error(msg   || L('فشلت العملية', 'Operation failed'), title || ''); else _fallback(msg, true); },
        warning: function (msg, title) { if (ensureToastr()) window.toastr.warning(msg || '', title || ''); else _fallback(msg, false); },
        info:    function (msg, title) { if (ensureToastr()) window.toastr.info(msg    || '', title || ''); else _fallback(msg, false); }
    };
    function _fallback(msg, isErr) { try { (isErr ? console.error : console.log)('[CB.toast]', msg); } catch (e) {} alert(msg || ''); }

    // ---- action presets ---------------------------------------------
    // Each preset maps to an icon, a confirm-button style and default copy.
    function presets() {
        return {
            generic: { icon: 'question', btn: 'btn btn-primary',
                       title: L('تأكيد الإجراء', 'Confirm action'),
                       text:  L('هل تريد متابعة هذا الإجراء؟', 'Do you want to proceed with this action?'),
                       ok:    L('متابعة', 'Continue') },
            save:    { icon: 'question', btn: 'btn btn-primary',
                       title: L('تأكيد الحفظ', 'Confirm save'),
                       text:  L('هل تريد حفظ التغييرات؟', 'Do you want to save the changes?'),
                       ok:    L('حفظ', 'Save') },
            edit:    { icon: 'question', btn: 'btn btn-primary',
                       title: L('تأكيد التعديل', 'Confirm edit'),
                       text:  L('هل تريد حفظ التعديلات؟', 'Do you want to save these changes?'),
                       ok:    L('حفظ التعديل', 'Save') },
            approve: { icon: 'question', btn: 'btn btn-success',
                       title: L('تأكيد الاعتماد', 'Confirm approval'),
                       text:  L('هل تريد اعتماد هذا المستند؟', 'Do you want to approve this record?'),
                       ok:    L('اعتماد', 'Approve') },
            reject:  { icon: 'warning', btn: 'btn btn-danger',
                       title: L('تأكيد الرفض', 'Confirm rejection'),
                       text:  L('هل تريد رفض هذا المستند؟', 'Do you want to reject this record?'),
                       ok:    L('رفض', 'Reject') },
            'delete':{ icon: 'warning', btn: 'btn btn-danger',
                       title: L('تأكيد الحذف', 'Confirm delete'),
                       text:  L('لا يمكن التراجع عن هذا الإجراء.', 'This action cannot be undone.'),
                       ok:    L('حذف', 'Delete') }
        };
    }

    // ---- core confirm ------------------------------------------------
    // Returns a Promise<boolean>. Resolves true when confirmed.
    CB.confirm = function (opts) {
        opts = opts || {};
        var p = presets();
        var preset = p[opts.type] || p.generic;

        if (!window.Swal) {
            // Fallback to native confirm if SweetAlert is unavailable.
            return Promise.resolve(window.confirm(opts.text || preset.text || preset.title));
        }

        return window.Swal.fire({
            icon: opts.icon || preset.icon,
            title: opts.title != null ? opts.title : preset.title,
            html: opts.html || null,
            text: (opts.html ? null : (opts.text != null ? opts.text : preset.text)),
            showCancelButton: true,
            buttonsStyling: false,           // use Metronic/brand button classes
            reverseButtons: isRTL(),
            confirmButtonText: opts.confirmText || preset.ok,
            cancelButtonText: opts.cancelText || L('إلغاء', 'Cancel'),
            customClass: {
                confirmButton: opts.confirmClass || preset.btn,
                cancelButton: 'btn btn-light',
                popup: 'cb-swal'
            },
            input: opts.input || undefined,
            inputLabel: opts.inputLabel || undefined,
            inputPlaceholder: opts.inputPlaceholder || undefined,
            inputAttributes: opts.inputAttributes || undefined,
            inputValidator: opts.inputValidator || undefined
        }).then(function (res) {
            if (opts.input) { return res.isConfirmed ? { confirmed: true, value: res.value } : { confirmed: false }; }
            return !!res.isConfirmed;
        });
    };

    // Convenience shortcuts -------------------------------------------
    ['save', 'edit', 'approve', 'reject', 'delete', 'generic'].forEach(function (t) {
        var name = 'confirm' + t.charAt(0).toUpperCase() + t.slice(1);
        CB[name] = function (opts) { return CB.confirm(Object.assign({ type: t }, opts || {})); };
    });

    // ---- run: confirm -> action -> toastr ---------------------------
    // action(): may return a jqXHR/Promise. Result {success,message} is
    // inspected to pick the success/error toast. Returns a Promise.
    CB.run = function (opts, action) {
        opts = opts || {};
        return CB.confirm(opts).then(function (ok) {
            var confirmed = (typeof ok === 'object') ? ok.confirmed : ok;
            if (!confirmed) return false;
            var ret;
            try { ret = action ? action((typeof ok === 'object') ? ok.value : undefined) : undefined; }
            catch (e) { CB.toast.error(opts.errorText || (e && e.message)); throw e; }
            return Promise.resolve(ret).then(function (r) {
                _toastResult(r, opts);
                return r == null ? true : r;
            }, function (xhr) {
                CB.toast.error(_xhrMsg(xhr) || opts.errorText);
                throw xhr;
            });
        });
    };

    function _toastResult(r, opts) {
        // Accept { success, message } envelopes; treat anything else as success.
        if (r && typeof r === 'object' && ('success' in r)) {
            if (r.success) CB.toast.success(r.message || opts.successText);
            else CB.toast.error(r.message || opts.errorText);
        } else {
            CB.toast.success(opts.successText);
        }
    }
    function _xhrMsg(xhr) {
        try {
            if (xhr && xhr.responseJSON && xhr.responseJSON.message) return xhr.responseJSON.message;
            if (xhr && xhr.statusText && xhr.status) return xhr.status + ' ' + xhr.statusText;
        } catch (e) {}
        return null;
    }

    // ---- submit a normal (non-AJAX) form after confirmation ---------
    CB.submitForm = function (form, opts) {
        return CB.confirm(opts).then(function (ok) {
            var confirmed = (typeof ok === 'object') ? ok.confirmed : ok;
            if (confirmed) { form._cbConfirmed = true; if (form.requestSubmit) form.requestSubmit(); else form.submit(); }
            return confirmed;
        });
    };

    // ---- AJAX action from a declarative element ---------------------
    function ajaxSend(url, method, data) {
        if (window.Ajax && window.Ajax.sendJson) return window.Ajax.sendJson(url, data || {}, { method: method || 'POST' });
        if (window.jQuery) return window.jQuery.ajax({ url: url, type: method || 'POST', data: data || {} });
        return Promise.reject(new Error('No AJAX transport available'));
    }

    // -----------------------------------------------------------------
    // DECLARATIVE AUTO-BINDING
    // Add data-cb-confirm to any form/link/button to get the standard flow
    // automatically -- no JS needed per screen.
    //
    //   <form ... data-cb-confirm data-cb-action="approve">  (normal post)
    //   <button data-cb-confirm data-cb-action="delete"
    //           data-cb-url="/Ctrl/Delete" data-cb-id="5">   (AJAX, reloads)
    //
    // Supported data-* attributes:
    //   data-cb-confirm            marker (presence enables the flow)
    //   data-cb-action             save|edit|approve|reject|delete|generic
    //   data-cb-title              override title
    //   data-cb-text / data-cb-html  override body
    //   data-cb-confirm-text       override confirm button label
    //   data-cb-url                AJAX endpoint (button/link); if absent the
    //                              element's form is submitted (or link followed)
    //   data-cb-id / data-cb-name=value  extra POST fields (data-cb-* become fields)
    //   data-cb-method             AJAX method (default POST)
    //   data-cb-reload             "true" (default for AJAX) reloads on success
    //   data-cb-success / data-cb-error  override toast text
    // -----------------------------------------------------------------
    function optsFromEl(el) {
        var o = { type: el.getAttribute('data-cb-action') || 'generic' };
        if (el.hasAttribute('data-cb-title')) o.title = el.getAttribute('data-cb-title');
        if (el.hasAttribute('data-cb-text')) o.text = el.getAttribute('data-cb-text');
        if (el.hasAttribute('data-cb-html')) o.html = el.getAttribute('data-cb-html');
        if (el.hasAttribute('data-cb-confirm-text')) o.confirmText = el.getAttribute('data-cb-confirm-text');
        if (el.hasAttribute('data-cb-success')) o.successText = el.getAttribute('data-cb-success');
        if (el.hasAttribute('data-cb-error')) o.errorText = el.getAttribute('data-cb-error');
        return o;
    }
    function extraFields(el) {
        var data = {};
        for (var i = 0; i < el.attributes.length; i++) {
            var a = el.attributes[i];
            if (a.name.indexOf('data-cb-') === 0) {
                var key = a.name.slice('data-cb-'.length);
                if (['confirm', 'action', 'title', 'text', 'html', 'confirm-text',
                     'url', 'method', 'reload', 'success', 'error', 'confirm-if', 'confirm-unless'].indexOf(key) === -1) {
                    data[key] = a.value;
                }
            }
        }
        return data;
    }

    // Conditional confirm gate.
    //   data-cb-confirm-if="<selector>"      -> confirm ONLY if that field has a
    //                                            truthy, non-zero value (e.g. an
    //                                            existing record id => "edit", not "add").
    //   data-cb-confirm-unless="<selector>"  -> confirm UNLESS that field is truthy.
    // When the attribute/target is absent, defaults to confirming.
    function fieldTruthy(el, sel) {
        var scope = (el.closest && el.closest('form')) || document;
        var t = scope.querySelector(sel) || document.querySelector(sel);
        if (!t) return null; // unknown
        var v = (t.value != null ? String(t.value) : '').trim().toLowerCase();
        return !(v === '' || v === '0' || v === 'false');
    }
    function shouldConfirm(el) {
        var ifSel = el.getAttribute('data-cb-confirm-if');
        if (ifSel) { var r = fieldTruthy(el, ifSel); if (r === false) return false; }
        var unlessSel = el.getAttribute('data-cb-confirm-unless');
        if (unlessSel) { var u = fieldTruthy(el, unlessSel); if (u === true) return false; }
        return true;
    }

    function handleSubmit(e) {
        var form = e.target;
        if (!form.matches || !form.matches('form[data-cb-confirm]')) return;
        if (form._cbConfirmed) { form._cbConfirmed = false; return; } // allow the real submit through
        if (!shouldConfirm(form)) return; // conditional gate (e.g. add vs edit) -> submit without confirm
        e.preventDefault();
        e.stopImmediatePropagation();
        CB.submitForm(form, optsFromEl(form));
    }

    function handleClick(e) {
        // closest-or-self element carrying the confirm marker
        var el = e.target.closest ? e.target.closest('[data-cb-confirm]') : null;
        if (!el) return;
        // A FORM marker is handled by the submit listener (whole-form confirm).
        if (el.tagName === 'FORM') return;
        // conditional gate: if it shouldn't confirm, let the native behavior proceed
        if (!shouldConfirm(el)) return;

        e.preventDefault();
        e.stopImmediatePropagation();
        var opts = optsFromEl(el);
        var ajaxUrl = el.getAttribute('data-cb-url');
        var form = el.form || (el.closest ? el.closest('form') : null);
        var isSubmit = (el.tagName === 'BUTTON' && el.type === 'submit') || (el.tagName === 'INPUT' && el.type === 'submit');

        if (ajaxUrl) {
            // AJAX flow (button/link with data-cb-url)
            var reload = el.getAttribute('data-cb-reload') !== 'false';
            CB.run(opts, function () {
                return ajaxSend(ajaxUrl, el.getAttribute('data-cb-method') || 'POST', extraFields(el));
            }).then(function (r) {
                var ok = (r === true) || (r && r.success);
                if (ok && reload) setTimeout(function () { window.location.reload(); }, 600);
            }).catch(function () {});
            return;
        }

        if (isSubmit && form) {
            // Per-button confirm: confirm, then submit the owning form *as this button*
            // (preserving the button's name/value, e.g. approve=true/false).
            CB.confirm(opts).then(function (ok) {
                var confirmed = (typeof ok === 'object') ? ok.confirmed : ok;
                if (!confirmed) return;
                form._cbConfirmed = true;
                if (form.requestSubmit) { form.requestSubmit(el); }
                else {
                    if (el.name) { var h = document.createElement('input'); h.type = 'hidden'; h.name = el.name; h.value = el.value; form.appendChild(h); }
                    form.submit();
                }
            });
            return;
        }

        var href = el.getAttribute('href');
        if (href && href !== '#') {
            // plain navigation (link)
            CB.confirm(opts).then(function (ok) { if (ok === true || (ok && ok.confirmed)) window.location.href = href; });
        }
    }

    // ---- select change binding (select2-safe) -----------------------
    //
    // WHY THIS EXISTS. select2 announces a pick with jQuery's .trigger('change'), and jQuery's
    // trigger runs jQuery handlers WITHOUT dispatching a DOM event - so a handler attached with
    // addEventListener('change') never hears it. _LayoutInventory.cshtml carries a relay for
    // exactly this ("relay select2's jQuery change to native addEventListener('change')
    // listeners"), but it is bound inside the branch that is skipped for a select which is
    // ALREADY select2-ified:
    //
    //     if ($(el).hasClass('select2-hidden-accessible')) return;   // <- skips the relay too
    //
    // and Metronic's own KTApp claims every select carrying data-control="select2" first, because
    // scripts.bundle.js registers its DOMContentLoaded handler before that enhancer does. So any
    // select with data-control="select2" ends up with select2 and NO relay.
    //
    // Measured on ReorderSettings, Planning, ExpiryAlerts and Serials: the only change handler on
    // those selects was select2's own. Their filters were dead - picking a warehouse changed the
    // control's value and its label and did nothing else at all.
    //
    // The views were also deciding which channel to use at parse time:
    //
    //     if (window.jQuery && jQuery(el).data('select2')) jQuery(el).on('change', go);
    //     else el.addEventListener('change', go);
    //
    // At that point NOTHING has been select2-ified yet - both KTApp and the enhancer run on
    // DOMContentLoaded, and an inline body script runs before it - so the jQuery branch was dead
    // code and every one of those screens took the native branch.
    //
    // A view should not have to know which library claimed the control first. This binds BOTH
    // channels and drops the duplicate, so it is right whether select2 is present, absent, or
    // arrives afterwards.
    CB.onSelectChange = function (el, handler) {
        if (typeof el === 'string') el = document.getElementById(el);
        if (!el || typeof handler !== 'function') return el || null;
        var busy = false;
        var fire = function (e) {
            // One pick can arrive twice - once through jQuery and once as a real DOM event (a
            // plain select fires natively and jQuery hears it too; where the layout's relay does
            // exist, it re-dispatches). Collapse them to a single call per pick.
            if (busy) return;
            busy = true;
            setTimeout(function () { busy = false; }, 0);
            handler.call(el, e);
        };
        el.addEventListener('change', fire);
        if (window.jQuery) window.jQuery(el).on('change', fire);
        return el;
    };

    document.addEventListener('submit', handleSubmit, true);
    document.addEventListener('click', handleClick, true);

    // initialize toastr config as soon as possible
    if (document.readyState !== 'loading') ensureToastr();
    else document.addEventListener('DOMContentLoaded', ensureToastr);

})(window, document);
