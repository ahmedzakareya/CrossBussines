(() => {
    'use strict';
    document.querySelectorAll('[data-construction-editor]').forEach(form => {
        const rows = form.querySelector('[data-editor-rows]');
        const template = form.querySelector('[data-editor-template]');
        const json = form.querySelector('[data-editor-json]');
        const error = form.querySelector('[data-editor-error]');
        const allocation = form.dataset.mode === 'allocation';
        const maximum = Number(form.dataset.maximum);
        const fields = allocation ? ['SourceLineId', 'NetDocumentAmount'] : ['ItemId', 'Description', 'Quantity', 'UnitId', 'UnitPrice', 'Discount', 'TaxRate'];
        const showError = text => { error.textContent = text; error.hidden = false; };
        function add(value = {}) {
            if (rows.children.length >= maximum) { showError(form.dataset.limit); return; }
            const row = template.content.firstElementChild.cloneNode(true);
            fields.forEach(field => {
                if (Object.prototype.hasOwnProperty.call(value, field)) row.querySelector(`[data-field="${field}"]`).value = value[field] == null ? '' : String(value[field]);
            });
            if (allocation && value.WbsNodeId != null && value.CostCodeId != null) row.querySelector('[data-dimension]').value = `${value.WbsNodeId}:${value.CostCodeId}`;
            row.querySelector('[data-editor-remove]').addEventListener('click', () => row.remove());
            rows.append(row);
        }
        form.querySelector('[data-editor-add]').addEventListener('click', () => add());
        try {
            const saved = JSON.parse(json.value);
            if (!Array.isArray(saved) || saved.length > maximum) throw new Error('Invalid rows');
            saved.forEach(value => {
                if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid row');
                const money = allocation ? ['NetDocumentAmount'] : ['Quantity', 'UnitPrice', 'Discount', 'TaxRate'];
                if (money.some(field => typeof value[field] !== 'string')) throw new Error('Decimal strings required');
                add(value);
            });
            if (!saved.length) add();
        } catch (_) { rows.replaceChildren(); showError(form.dataset.invalid); }
        form.addEventListener('submit', event => {
            const data = [];
            let valid = rows.children.length > 0 && rows.children.length <= maximum;
            rows.querySelectorAll('tr').forEach(row => {
                const value = {};
                fields.forEach(field => {
                    const input = row.querySelector(`[data-field="${field}"]`);
                    const text = input.value;
                    if (input.hasAttribute('data-decimal') && !/^[0-9]+(?:\.[0-9]+)?$/.test(text)) valid = false;
                    value[field] = field === 'ItemId' || field === 'UnitId' ? (text || null) : text;
                });
                if (allocation) {
                    const dimension = row.querySelector('[data-dimension]').value.split(':');
                    if (dimension.length !== 2 || !dimension.every(part => /^[0-9]+$/.test(part))) valid = false;
                    value.WbsNodeId = dimension[0]; value.CostCodeId = dimension[1];
                }
                data.push(value);
            });
            if (!valid) { event.preventDefault(); showError(form.dataset.invalid); return; }
            // Decimal values stay strings; never round or sum financial input through JavaScript floating point.
            json.value = JSON.stringify(data);
        });
    });
})();
