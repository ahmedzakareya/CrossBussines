// wwwroot/js/app.ajax.js
// ===== Unified AJAX Helper for ASP.NET MVC (jQuery-based) =====
window.Ajax = (function ($) {
    if (!$) { throw new Error("jQuery is required for Ajax helper."); }

    const DEFAULTS = { method: 'POST', timeout: 60000, expectJson: true, showSpinner: false };

    function readAntiForgeryToken() {
        var $input = $('input[name="__RequestVerificationToken"]').first();
        if ($input.length) return $input.val();
        var cookie = document.cookie.split('; ').find(c => c.startsWith('__RequestVerificationToken='));
        if (cookie) return decodeURIComponent(cookie.split('=')[1]);
        return null;
    }

    function hasFileDeep(obj) {
        if (!obj) return false;
        if (obj instanceof File || obj instanceof Blob) return true;
        if (Array.isArray(obj)) return obj.some(hasFileDeep);
        if (typeof obj === 'object') { for (const k in obj) { if (hasFileDeep(obj[k])) return true; } }
        return false;
    }

    function appendFormData(fd, value, keyPath) {
        if (value === null || value === undefined) { fd.append(keyPath, ''); return; }
        if (value instanceof File || value instanceof Blob) { fd.append(keyPath, value); return; }
        if (value instanceof Date) { fd.append(keyPath, value.toISOString()); return; }
        if (Array.isArray(value)) { for (let i = 0; i < value.length; i++) appendFormData(fd, value[i], keyPath + '[' + i + ']'); return; }
        if (typeof value === 'object') { for (const p in value) if (Object.prototype.hasOwnProperty.call(value, p)) appendFormData(fd, value[p], keyPath ? (keyPath + '.' + p) : p); return; }
        fd.append(keyPath, String(value));
    }

    function buildFormDataFromModels(modelsMap) {
        const fd = new FormData();
        const keys = Object.keys(modelsMap || {});
        for (const root of keys) appendFormData(fd, modelsMap[root], root);
        return fd;
    }

    function coreSendAjax(opts) {
        const ajaxOpts = {
            url: opts.url, type: opts.method || DEFAULTS.method, data: opts.data,
            timeout: opts.timeout || DEFAULTS.timeout, headers: Object.assign({}, opts.headers || {})
        };
        if (opts.isFormData) { ajaxOpts.processData = false; ajaxOpts.contentType = false; }
        else { ajaxOpts.contentType = 'application/json; charset=utf-8'; if (opts.expectJson) ajaxOpts.dataType = 'json'; }

        const token = readAntiForgeryToken();
        if (token) {
            ajaxOpts.headers['RequestVerificationToken'] = token;
            ajaxOpts.headers['X-Request-Verification-Token'] = token;
        }

        if (opts.showSpinner || DEFAULTS.showSpinner) $(document).trigger('ajax:spinner:start');
        const jq = $.ajax(ajaxOpts);
        return jq.always(function () { if (opts.showSpinner || DEFAULTS.showSpinner) $(document).trigger('ajax:spinner:stop'); });
    }

    function sendJson(url, data, options) {
        const opts = Object.assign({}, DEFAULTS, options || {});
        return coreSendAjax({
            url: url, method: opts.method || 'POST', data: JSON.stringify(data || {}),
            expectJson: opts.expectJson !== false, showSpinner: !!opts.showSpinner, headers: opts.headers || {}
        });
    }

    function sendForm(url, modelsMap, options) {
        const opts = Object.assign({}, DEFAULTS, options || {});
        const fd = buildFormDataFromModels(modelsMap || {});
        const _hasFile = hasFileDeep(modelsMap);
        return coreSendAjax({
            url: url, method: opts.method || 'POST', data: fd, isFormData: true,
            expectJson: opts.expectJson !== false, showSpinner: !!opts.showSpinner, headers: opts.headers || {}
        });
    }

    function smart(url, payload, options) {
        const useForm = hasFileDeep(payload);
        if (useForm) {
            const map = options && options.paramName ? { [options.paramName]: payload } : payload;
            return sendForm(url, map, options);
        }
        return sendJson(url, payload, options);
    }

    return { sendJson, sendForm, smart };
})(window.jQuery);
